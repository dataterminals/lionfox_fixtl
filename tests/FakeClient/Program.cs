using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Vintagestory.Common;

namespace lionfox_fixtl.FakeClient;

/// <summary>
/// A stand-in for a player's game, for tests/boot_test.py. It joins a test
/// server over TCP the way the game does (login token, identification,
/// request join, loaded, playing; the server needs VerifyPlayerAuth off) and
/// answers its pings. It never opens UDP, so the server falls back to TCP
/// for it after 10 s, as it does for a game behind a strict firewall. Like the game, it reads the socket on one thread and handles
/// packets on another, its "main thread". Told so in chat, it hangs that
/// thread the way a frozen game does:
///
///   frozentest:hang S C    packets keep arriving and pile up for S seconds;
///                          then it answers the ping it hung on at once and
///                          takes C seconds over the rest
///   frozentest:hang-quit S hangs, and after S seconds drops the connection
///   frozentest:lag MS      answers each ping MS milliseconds late, like a
///                          player far away (0 turns it off)
///
/// It isn't a Vintage Story client: no window, no mods, no game pipe, so it
/// can't hand a connect to (or take one from) a game that's running.
///
///   FixtlFakeClient --port 43071 [--name FixFox]
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        string? game = null;
        foreach (string? dir in new[] { Environment.GetEnvironmentVariable("VINTAGE_STORY"), @"D:\Vintagestory", @"H:\Vintagestory" })
        {
            if (dir != null && File.Exists(Path.Combine(dir, "VintagestoryLib.dll"))) { game = dir; break; }
        }
        if (game == null)
        {
            Console.Error.WriteLine("no VintagestoryLib.dll (set VINTAGE_STORY)");
            return 2;
        }
        // The game's DLLs aren't copied next to this one, and its zstd is native.
        string lib = Path.Combine(game, "Lib");
        Environment.SetEnvironmentVariable("PATH", lib + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string file = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { game, lib })
            {
                if (File.Exists(Path.Combine(dir, file))) return Assembly.LoadFrom(Path.Combine(dir, file));
            }
            return null;
        };
        return Run(args);
    }

    // Kept out of Main so the game's DLLs load only once the resolver is in.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args) => Client.Run(args);
}

class Client
{
    static readonly Regex Command = new(@"frozentest:(hang-quit|hang|lag) (\d+)(?: (\d+))?", RegexOptions.Compiled);
    // fixtl's kit: send the mode-change packet a real game sends on a view-distance change or a fly/noclip
    // hotkey (ClientWorldPlayerData.RequestMode): uid, game mode, free move, noclip, move speed, picking range and
    // view distance, as the kit read them off the server (the game sends what its own copy says).
    static readonly Regex ModeCommand = new(@"fixtltest:mode (\S+) (\d+) (\d+) (\d+) (-?\d+) (-?\d+) (\d+)", RegexOptions.Compiled);
    static readonly Stopwatch clock = Stopwatch.StartNew();

    readonly TcpClient tcp = new() { NoDelay = true };
    readonly BlockingCollection<Packet_Server> inbox = new();
    readonly string name;
    readonly string uid;
    bool playing;
    // Set while the "main thread" hangs, and for the catch-up after.
    double hangUntil = -1, catchUp;
    int lagMs;
    bool thawing, quitAfterHang;

    Client(string name)
    {
        this.name = name;
        uid = name + "-fixtltest-uid";
    }

    public static int Run(string[] args)
    {
        int port = 42420;
        string name = "FixFox";
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--port") port = int.Parse(args[++i]);
            else if (args[i] == "--name") name = args[++i];
        }
        return new Client(name).Play(port);
    }

    static void Say(string line)
    {
        Console.WriteLine($"[fakeclient {clock.Elapsed.TotalSeconds:0.000}] {line}");
        Console.Out.Flush();
    }

    int Play(int port)
    {
        tcp.Connect("127.0.0.1", port);
        Say($"connected to 127.0.0.1:{port} as {name}");
        // The game asks for a login token first, and identifies once it has one.
        Send(new Packet_Client { Id = 33 });
        new Thread(Read) { IsBackground = true, Name = "fakeclient-read" }.Start();

        while (true)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now < hangUntil)
            {
                Thread.Sleep(50);
                continue;
            }
            if (hangUntil > 0)
            {
                hangUntil = -1;
                if (quitAfterHang)
                {
                    Say("dropping the connection while hung");
                    tcp.Client.Close();
                    return 0;
                }
                Say($"main thread back; {inbox.Count} packets piled up");
                thawing = true;
            }
            if (!inbox.TryTake(out Packet_Server? packet, 1000))
            {
                if (inbox.IsCompleted) return playing ? 0 : 1;
                continue;
            }
            if (Handle(packet) is int exit) return exit;
        }
    }

    int? Handle(Packet_Server packet)
    {
        switch (packet.Id)
        {
            case 77:
                Say("login token in; identifying");
                Send(new Packet_Client
                {
                    Id = 1,
                    Identification = new Packet_ClientIdentification
                    {
                        Playername = name,
                        PlayerUID = uid,
                        MdProtocolVersion = "1.22.6",
                        NetworkVersion = "1.22.6",
                        ShortGameVersion = "1.22.6",
                        ViewDistance = 32,
                    },
                });
                break;
            case 73:
                Say("server ready; asking to join");
                Send(new Packet_Client { Id = 11, RequestJoin = new Packet_ClientRequestJoin { Language = "en" } });
                break;
            case 41:
                if (!playing && packet.PlayerData?.PlayerUID == uid)
                {
                    Send(new Packet_Client { Id = 26 });
                    Send(new Packet_Client { Id = 29 });
                    playing = true;
                    Say("own player data in; loaded and playing");
                }
                break;
            case 2:
                if (lagMs > 0) Thread.Sleep(lagMs);
                Send(new Packet_Client { Id = 2, PingReply = new Packet_ClientPingReply() });
                if (thawing)
                {
                    // The real game gets to this ping first and then works through
                    // everything that came in after it, the next ping last.
                    thawing = false;
                    Say($"answered the ping it hung on; catching up for {catchUp:0} s");
                    Thread.Sleep(TimeSpan.FromSeconds(catchUp));
                    Say("caught up");
                }
                break;
            case 8:
                string message = packet.Chatline?.Message ?? "";
                Say($"chat ({packet.Chatline?.ChatType}): {message}");
                Match mm = ModeCommand.Match(message);
                if (mm.Success)
                {
                    Send(new Packet_Client
                    {
                        Id = 20,
                        RequestModeChange = new Packet_PlayerMode
                        {
                            PlayerUID = mm.Groups[1].Value,
                            GameMode = int.Parse(mm.Groups[2].Value),
                            FreeMove = int.Parse(mm.Groups[3].Value),
                            NoClip = int.Parse(mm.Groups[4].Value),
                            MoveSpeed = int.Parse(mm.Groups[5].Value),
                            PickingRange = int.Parse(mm.Groups[6].Value),
                            ViewDistance = int.Parse(mm.Groups[7].Value),
                        }
                    });
                    Say($"sent a mode-change packet: game mode {mm.Groups[2].Value}, free move {mm.Groups[3].Value}, noclip {mm.Groups[4].Value}, view distance {mm.Groups[7].Value}");
                    break;
                }
                Match m = Command.Match(message);
                if (m.Success && m.Groups[1].Value == "lag")
                {
                    lagMs = int.Parse(m.Groups[2].Value);
                    Say($"answering pings {lagMs} ms late");
                }
                else if (m.Success)
                {
                    hangUntil = clock.Elapsed.TotalSeconds + int.Parse(m.Groups[2].Value);
                    catchUp = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                    quitAfterHang = m.Groups[1].Value == "hang-quit";
                    Say($"hanging the main thread for {m.Groups[2].Value} s{(quitAfterHang ? ", then dropping the connection" : "")}");
                }
                break;
            case 9:
                Say($"disconnected by the server: {packet.DisconnectPlayer?.DisconnectReason}");
                return 0;
        }
        return null;
    }

    void Read()
    {
        NetworkStream stream = tcp.GetStream();
        byte[] head = new byte[4];
        try
        {
            while (true)
            {
                stream.ReadExactly(head);
                int length = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
                bool compressed = (length & int.MinValue) != 0;
                byte[] body = new byte[length & int.MaxValue];
                stream.ReadExactly(body);
                if (compressed) body = Compression.Decompress(body);
                Packet_Server packet = new();
                Packet_ServerSerializer.DeserializeBuffer(body, body.Length, packet);
                inbox.Add(packet);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException)
        {
            Say($"connection closed ({e.GetType().Name})");
        }
        finally
        {
            inbox.CompleteAdding();
        }
    }

    void Send(Packet_Client packet)
    {
        CitoMemoryStream ms = new();
        Packet_ClientSerializer.Serialize(ms, packet);
        byte[] body = ms.ToArray();
        byte[] frame = new byte[body.Length + 4];
        frame[0] = (byte)(body.Length >> 24);
        frame[1] = (byte)(body.Length >> 16);
        frame[2] = (byte)(body.Length >> 8);
        frame[3] = (byte)body.Length;
        body.CopyTo(frame, 4);
        tcp.GetStream().Write(frame);
    }
}
