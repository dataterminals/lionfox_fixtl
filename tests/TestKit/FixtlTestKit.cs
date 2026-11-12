using System;
using System.Collections.Generic;
using LaneFn = System.Func<Vintagestory.API.Server.IServerPlayer, System.Collections.Generic.IEnumerable<double>>;
using System.Linq;
using System.Reflection;
using RelocCore = TranslocatorRelocator.src.Core;
using TranslocatorRelocator.src.block;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace lionfox_fixtl.TestKit;

/// <summary>
/// Test-only server mod for tests/boot_test.py (never ship it). One fake client (tests/FakeClient) joins as FixFox.
/// Each lane builds translocators in the air near spawn, puts FixFox in one of the stuck states found on LionFox,
/// shows the translocator really doesn't take them, then runs /fixtl as FixFox and checks what it did.
///
/// "Stands in it" goes through the server's own path: the kit sets FixFox's position on the translocator's floor and
/// calls EntityBehaviorPlayerPhysics.OnReceivedClientPos, which is what a real game's position packet does. So the
/// server's own collision pass decides whether the translocator notices them, and the translocator's own tick
/// decides whether it sends them. Results are "[fixtltest] PASS|FAIL NAME: ...", then "[fixtltest] done";
/// each lane is bracketed by "[fixtltest] begin NAME" / "[fixtltest] end NAME".
/// </summary>
public class FixtlTestKit : ModSystem
{
    const string Tag = "[fixtltest]";
    const string Fox = "FixFox";
    ICoreServerAPI sapi = null!;
    BlockPos baseAt = null!;

    static readonly FieldInfo LinkKey = typeof(BlockEntityRelocatedTranslocator).GetField("linkKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly FieldInfo Tping = typeof(BlockEntityTeleporterBase).GetField("tpingEntities", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly FieldInfo CanTele = typeof(BlockEntityStaticTranslocator).GetField("canTeleport", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly FieldInfo Ignore = typeof(EntityAgent).GetField("ignoreTeleportCall", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.ChatCommands.Create("fixtltest")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("run").WithArgs(api.ChatCommands.Parsers.OptionalAll("lanes")).HandleWith(a => Run(a[0] as string)).EndSubCommand();
    }

    void Log(string line) => sapi.Logger.Notification($"{Tag} {line}");
    void Check(string name, bool ok, string detail) => Log($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");

    IServerPlayer? Player() => sapi.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerName == Fox && p.Entity != null);

    TextCommandResult Run(string? only)
    {
        var p = Player();
        if (p == null) return TextCommandResult.Error($"{Tag} needs {Fox} online");
        baseAt = p.Entity.Pos.AsBlockPos.Up(20);
        Log($"info {Fox}: role {p.Role?.Code}, controlserver {p.HasPrivilege(Privilege.controlserver)}, game mode {p.WorldData.CurrentGameMode}");
        var lanes = new (string, LaneFn)[]
        {
            ("works", LaneWorks), ("noclip", LaneNoclip), ("check", LaneCheck), ("spectator", LaneSpectator),
            ("teleporting", LaneTeleporting), ("ignore", LaneIgnore), ("stale-entry", LaneStaleEntry),
            ("keyed-alone", LaneKeyedAlone), ("keyed-desync", LaneKeyedDesync), ("keyed-leftover", LaneKeyedLeftover), ("keyed-moved", LaneKeyedMoved),
            ("stone-nodest", LaneStoneNoDest), ("stone-gone", LaneStoneGone), ("admin", LaneAdmin), ("keys", LaneKeys),
            ("cycle", LaneCycle), ("gm-matrix", LaneGmMatrix), ("stone-stalled", LaneStoneStalled),
        };
        string[] pick = (only ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (pick.Length > 0) lanes = lanes.Where(l => pick.Contains(l.Item1)).ToArray();
        NextLane(new Queue<(string, LaneFn)>(lanes), p);
        return TextCommandResult.Success($"{Tag} running {lanes.Length} lanes");
    }

    void NextLane(Queue<(string name, LaneFn lane)> queue, IServerPlayer p)
    {
        if (queue.Count == 0) { Clear(); Log("done"); return; }
        var (name, lane) = queue.Dequeue();
        Clear();
        Reset(p);
        // A game-mode change in Reset can fire SpecReturn's return teleport; let it land before the lane starts.
        sapi.Event.RegisterCallback(_ => StartLane(queue, p, name, lane), 800);
    }

    void StartLane(Queue<(string name, LaneFn lane)> queue, IServerPlayer p, string name, LaneFn lane)
    {
        Reset(p);
        Log($"begin {name}");
        IEnumerator<double> it;
        try { it = lane(p).GetEnumerator(); }
        catch (Exception e) { Check(name, false, $"{e.GetType().Name}: {e.Message}"); Log($"end {name}"); sapi.Event.RegisterCallback(_ => NextLane(queue, p), 100); return; }
        void Step()
        {
            bool more;
            try { more = it.MoveNext(); }
            catch (Exception e) { Check(name, false, $"{e.GetType().Name}: {e.Message}"); sapi.Logger.Error(e); more = false; }
            if (more) sapi.Event.RegisterCallback(_ => Step(), Math.Max(50, (int)(it.Current * 1000)));
            else { Log($"end {name}"); sapi.Event.RegisterCallback(_ => NextLane(queue, p), 300); }
        }
        Step();
    }

    // ---- places -------------------------------------------------------------------------------

    BlockPos A => baseAt;
    BlockPos B => baseAt.AddCopy(12, 0, 0);
    BlockPos C => baseAt.AddCopy(0, 0, 12);
    BlockPos D => baseAt.AddCopy(12, 0, 12);
    BlockPos E => baseAt.AddCopy(-12, 0, 0);
    BlockPos F => baseAt.AddCopy(-12, 0, 12);
    IEnumerable<BlockPos> Sites => new[] { A, B, C, D, E, F };

    void Clear()
    {
        var ba = sapi.World.BlockAccessor;
        foreach (var s in Sites) { ba.SetBlock(0, s); RelocCore.linkManager.RemoveKey(s); }
    }

    void Reset(IServerPlayer p)
    {
        var e = p.Entity;
        SetSync(true);
        if (p.WorldData.CurrentGameMode != EnumGameMode.Survival) Console($"/gm {Fox} survival");
        e.Controls.NoClip = false;
        e.Teleporting = false;
        Ignore.SetValue(e, false);
        e.Pos.SetPos(baseAt.X + 0.5, baseAt.Y + 3, baseAt.Z + 4.5);
    }

    BlockEntityStaticTranslocator Stone(BlockPos at, BlockPos? target, bool can = true, bool find = false)
    {
        var ba = sapi.World.BlockAccessor;
        ba.SetBlock(sapi.World.GetBlock(new AssetLocation("game:statictranslocator-normal-north")).BlockId, at);
        var be = ba.GetBlockEntity(at) as BlockEntityStaticTranslocator ?? throw new Exception("no stone translocator block entity");
        var tree = new TreeAttribute();
        be.ToTreeAttributes(tree);
        tree.SetBool("canTele", can);
        tree.SetInt("repairState", 4);
        tree.SetBool("activated", true);
        tree.SetInt("teleX", target?.X ?? 0);
        tree.SetInt("teleY", target?.Y ?? 0);
        tree.SetInt("teleZ", target?.Z ?? 0);
        tree.SetBool("findNextChunk", find);
        be.FromTreeAttributes(tree, sapi.World);
        be.findNextChunk = find;   // FromTreeAttributes turns it back on for a repaired, unlinked one
        be.setupGameTickers();     // Initialize ran while it was unrepaired, so it has no tick yet
        return be;
    }

    void StonePair() { Stone(A, B); Stone(B, A); }

    BlockEntityRelocatedTranslocator Keyed(BlockPos at, string key, string? listAs = null)
    {
        var ba = sapi.World.BlockAccessor;
        ba.SetBlock(sapi.World.GetBlock(new AssetLocation("translocatorrelocatorfork:relocatedtranslocator-north")).BlockId, at);
        var be = ba.GetBlockEntity(at) as BlockEntityRelocatedTranslocator ?? throw new Exception("no keyed translocator block entity");
        LinkKey.SetValue(be, key);
        RelocCore.linkManager.AddOrSetKey(at.Copy(), listAs ?? key);
        be.CheckLinks();
        be.MarkDirty();
        return be;
    }

    // ---- fixtl's own switch, and game-mode round trips ------------------------------------------

    /// <summary>Turns fixtl's noclip re-sync on game-mode change on or off (its ModConfig switch), for the control run.</summary>
    void SetSync(bool on)
    {
        var ms = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "lionfox_fixtl.TranslocatorFixMod") ?? throw new Exception("fixtl's mod system isn't loaded");
        var cfg = ms.GetType().GetProperty("Config")!.GetValue(ms)!;
        cfg.GetType().GetField("SyncNoClipOnGameModeChange")!.SetValue(cfg, on);
    }

    int viewDistance = 300;
    int modePackets;

    /// <summary>FixFox's fake game sends the mode-change packet a real game sends when its view distance changes,
    /// carrying what the game's own copy of the player's mode says (which the server just broadcast to it).</summary>
    IEnumerable<double> ModePacket(IServerPlayer p)
    {
        var w = p.WorldData;
        viewDistance = viewDistance == 300 ? 332 : 300;   // always a change, so the server logs each one
        int before = modePackets;
        p.SendMessage(GlobalConstants.GeneralChatGroup,
            $"fixtltest:mode {p.PlayerUID} {(int)w.CurrentGameMode} {(w.FreeMove ? 1 : 0)} {(w.NoClip ? 1 : 0)} {(int)(w.MoveSpeedMultiplier * 64f)} {(int)(w.PickingRange * 64f)} {viewDistance}",
            EnumChatType.Notification);
        for (double t = 0; t < 3 && modePackets == before; t += 0.1) yield return 0.1;
    }

    void CountModePackets(EnumLogType type, string message, params object[] args)
    {
        if (message.Contains("requested new view distance") && args.Length > 0 && args[0] as string == Fox) modePackets++;
    }

    /// <summary>Spectator, creative, spectator, survival, then spectator and back to survival once more, with the
    /// game's mode packet after each switch out of survival: Sylvia's "in and out of spectator, creative and
    /// survival a bunch". SpecReturn, when loaded, saves and restores the position around the spectator trips.</summary>
    IEnumerable<double> Cycle(IServerPlayer p, List<string> trace)
    {
        foreach (var mode in new[] { "spectator", "creative", "spectator", "survival", "spectator", "survival" })
        {
            Console($"/gm {Fox} {mode}");
            yield return 0.5;
            if (mode != "survival") foreach (var w in ModePacket(p)) yield return w;
            trace.Add($"{mode}: noclip {p.WorldData.NoClip}/server {p.Entity.Controls.NoClip}");
        }
    }

    // ---- the player ---------------------------------------------------------------------------

    void Put(IServerPlayer p, BlockPos pad) => p.Entity.Pos.SetPos(pad.X + 0.5, pad.Y + 0.125, pad.Z + 0.5);

    static bool Near(IServerPlayer p, BlockPos pad, double r = 2.5) => p.Entity.Pos.XYZ.DistanceTo(new Vec3d(pad.X + 0.5, pad.Y + 0.5, pad.Z + 0.5)) <= r;

    /// <summary>FixFox stands in the translocator at <paramref name="pad"/> for up to <paramref name="seconds"/>,
    /// sending a position update every 50 ms the way a game does, until something moves them.</summary>
    IEnumerable<double> StandIn(IServerPlayer p, BlockPos pad, double seconds)
    {
        var phys = p.Entity.GetBehavior<EntityBehaviorPlayerPhysics>() ?? throw new Exception("no player physics behaviour");
        Put(p, pad);
        for (double t = 0; t < seconds; t += 0.05)
        {
            if (!Near(p, pad) || p.Entity.Teleporting && !Near(p, pad)) yield break;
            p.Entity.Pos.SetPos(pad.X + 0.5, pad.Y + 0.125, pad.Z + 0.5);
            phys.OnReceivedClientPos(0);
            yield return 0.05;
        }
    }

    bool Warming(IServerPlayer p, BlockPos pad) =>
        sapi.World.BlockAccessor.GetBlockEntity(pad) is BlockEntityTeleporterBase be && Tping.GetValue(be) is System.Collections.IDictionary d && d.Contains(p.Entity.EntityId);

    string As(IServerPlayer p, string cmd, bool admin = false)
    {
        string? said = null;
        var caller = new Caller { Player = p, FromChatGroupId = GlobalConstants.GeneralChatGroup };
        if (admin) caller.CallerPrivileges = new[] { "*" };
        sapi.ChatCommands.ExecuteUnparsed(cmd, new TextCommandCallingArgs { Caller = caller }, r => said = $"<{r.Status}> {r.StatusMessage}");
        Log($"info {cmd} -> {said?.Replace("\n", " | ")}");
        return said ?? "<no answer>";
    }

    string Console(string cmd)
    {
        string? said = null;
        var caller = new Caller { Type = EnumCallerType.Console, CallerRole = "admin", CallerPrivileges = new[] { "*" }, FromChatGroupId = GlobalConstants.ConsoleGroup };
        sapi.ChatCommands.ExecuteUnparsed(cmd, new TextCommandCallingArgs { Caller = caller }, r => said = $"<{r.Status}> {r.StatusMessage}");
        Log($"info console {cmd} -> {said?.Replace("\n", " | ")}");
        return said ?? "<no answer>";
    }

    IEnumerable<double> Settle(IServerPlayer p, BlockPos to)
    {
        for (double t = 0; t < 3 && !Near(p, to, 3); t += 0.1) yield return 0.1;
    }

    // ---- lanes --------------------------------------------------------------------------------

    /// <summary>A working pair of each kind: the game sends FixFox on its own, and /fixtl finds nothing wrong and
    /// sends them too.</summary>
    IEnumerable<double> LaneWorks(IServerPlayer p)
    {
        StonePair();
        foreach (var w in StandIn(p, A, 8)) yield return w;
        foreach (var w in Settle(p, B)) yield return w;
        bool stoneByGame = Near(p, B, 3);
        Clear(); Keyed(A, "Mansion"); Keyed(B, "Mansion");
        yield return 1.2;
        foreach (var w in StandIn(p, A, 8)) yield return w;
        foreach (var w in Settle(p, B)) yield return w;
        bool keyedByGame = Near(p, B, 3);
        Put(p, A);
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        Check("works", stoneByGame && keyedByGame && Near(p, B, 3) && said.Contains("nothing wrong") && said.Contains("Sending you through"),
            $"stone pair sent FixFox by itself {stoneByGame}; keyed pair by itself {keyedByGame}; /fixtl sent them {Near(p, B, 3)}, said: {said}");
    }

    /// <summary>Sylvia's report, 2026-11-12: a properly keyed pair stops working after a bunch of game-mode switches.
    /// Control (fixtl's re-sync off): the switches with the game's real mode packets leave the server's noclip on, the
    /// pair never notices FixFox, and /fixtl gets them through. Then with the re-sync on: the same switches, and the
    /// pair takes FixFox by itself.</summary>
    IEnumerable<double> LaneCycle(IServerPlayer p)
    {
        Keyed(A, "Mansion"); Keyed(B, "Mansion");
        yield return 1.2;
        sapi.Logger.EntryAdded += CountModePackets;
        var off = new List<string>();
        var on = new List<string>();
        try
        {
            SetSync(false);
            p.Entity.Pos.SetPos(A.X + 2.0, A.Y + 0.125, A.Z + 0.5);       // beside the pad
            foreach (var w in Cycle(p, off)) yield return w;
            bool leftOn = p.Entity.Controls.NoClip;
            foreach (var w in StandIn(p, A, 7)) yield return w;
            bool stuck = Near(p, A) && !Warming(p, A);
            string said = As(p, "/fixtl");
            foreach (var w in Settle(p, B)) yield return w;
            bool rescued = Near(p, B, 3);
            int packetsOff = modePackets;

            SetSync(true);
            Put(p, A);                                                      // in the pad this time
            foreach (var w in Cycle(p, on)) yield return w;
            bool offAfter = !p.Entity.Controls.NoClip;
            foreach (var w in StandIn(p, A, 8)) yield return w;
            foreach (var w in Settle(p, B)) yield return w;
            bool byPad = Near(p, B, 3);
            Check("cycle", packetsOff >= 4 && modePackets >= 8 && leftOn && stuck && rescued && said.Contains("noclip") && offAfter && byPad,
                $"{modePackets} mode packets reached the server. Re-sync off: [{string.Join("; ", off)}]; noclip left on {leftOn}, the pair never noticed FixFox {stuck}, /fixtl got them through {rescued}. "
                + $"Re-sync on: [{string.Join("; ", on)}]; noclip off after {offAfter}, the pair took FixFox by itself {byPad}. /fixtl said: {said}");
        }
        finally
        {
            sapi.Logger.EntryAdded -= CountModePackets;
            SetSync(true);
        }
    }

    /// <summary>Sylvia's pattern, every way round: 3>1, 3>2>1, 2>3>1, 3>2>3>1 and 2>1 from survival, each with and
    /// without the game's mode packet after every switch out of survival, first with fixtl's re-sync off, then on.
    /// After every switch: is the server's noclip what the game mode says? After every sequence: FixFox walks into a
    /// properly keyed pair. Off, it should break exactly when a packet arrived while the game mode had noclip on;
    /// on, the server's noclip should be right after every switch and the pair should always take them.</summary>
    IEnumerable<double> LaneGmMatrix(IServerPlayer p)
    {
        Keyed(A, "Mansion"); Keyed(B, "Mansion");
        yield return 1.2;
        string[][] seqs =
        {
            new[] { "spectator", "survival" },
            new[] { "spectator", "creative", "survival" },
            new[] { "creative", "spectator", "survival" },
            new[] { "spectator", "creative", "spectator", "survival" },
            new[] { "creative", "survival" },
        };
        static string N(string m) => m == "spectator" ? "3" : m == "creative" ? "2" : "1";
        var rows = new List<string>();
        bool onInStep = true, onTook = true;
        int offPredicted = 0, offBroke = 0, offWrong = 0;
        sapi.Logger.EntryAdded += CountModePackets;
        try
        {
            foreach (bool sync in new[] { false, true })
                foreach (bool packets in new[] { false, true })
                    foreach (var seq in seqs)
                    {
                        SetSync(sync);
                        if (p.WorldData.CurrentGameMode != EnumGameMode.Survival) Console($"/gm {Fox} survival");
                        p.Entity.Controls.NoClip = false;
                        p.Entity.Teleporting = false;
                        yield return 0.3;
                        p.Entity.Pos.SetPos(A.X + 2.0, A.Y + 0.125, A.Z + 0.5);
                        bool inStep = true, packetInNoclip = false;
                        var marks = new List<string> { "1" };
                        foreach (var mode in seq)
                        {
                            Console($"/gm {Fox} {mode}");
                            yield return 0.3;
                            if (packets && mode != "survival")
                            {
                                packetInNoclip |= p.WorldData.NoClip;
                                foreach (var w in ModePacket(p)) yield return w;
                            }
                            bool ok = p.Entity.Controls.NoClip == p.WorldData.NoClip;
                            inStep &= ok;
                            marks.Add(N(mode) + (ok ? "" : "*"));
                        }
                        foreach (var w in StandIn(p, A, 6)) yield return w;
                        foreach (var w in Settle(p, B)) yield return w;
                        bool took = Near(p, B, 3);
                        rows.Add($"{(sync ? "on" : "off")} {(packets ? "packet" : "plain")} {string.Join(">", marks)}: {(took ? "took" : "STUCK")}");
                        if (sync) { onInStep &= inStep; onTook &= took; }
                        else
                        {
                            if (packetInNoclip) offPredicted++;
                            if (!took) offBroke++;
                            if (took == packetInNoclip) offWrong++;
                        }
                    }
        }
        finally
        {
            sapi.Logger.EntryAdded -= CountModePackets;
            SetSync(true);
            p.Entity.Controls.NoClip = false;
        }
        Check("gm-matrix", onInStep && onTook && offPredicted > 0 && offBroke == offPredicted && offWrong == 0,
            $"re-sync on: server noclip right after every switch {onInStep}, the pair took FixFox every time {onTook}; "
            + $"re-sync off: broke {offBroke} times, exactly where a packet came in during noclip ({offPredicted} predicted, {offWrong} off). "
            + $"(* = server's noclip didn't match the game mode right after that switch) {string.Join(" | ", rows)}");
    }

    /// <summary>The 2026-11-07 case. /gm spectator, the game's mode packet (which syncs the server's noclip), /gm
    /// survival: fixtl's handler turns the server's noclip off again (vanilla leaves it on), and the translocator
    /// works. Then the stale state by hand: the translocator never notices FixFox, and /fixtl fixes it and sends them.</summary>
    IEnumerable<double> LaneNoclip(IServerPlayer p)
    {
        StonePair();
        Put(p, A);
        Console($"/gm {Fox} spectator");
        p.Entity.Controls.NoClip = p.WorldData.NoClip;   // what the game's mode-change packet does (ServerMain.HandleRequestModeChange)
        bool onInSpectator = p.Entity.Controls.NoClip;
        Console($"/gm {Fox} survival");
        bool offAfter = !p.Entity.Controls.NoClip;
        foreach (var w in StandIn(p, A, 8)) yield return w;
        foreach (var w in Settle(p, B)) yield return w;
        bool workedAfterGm = Near(p, B, 3);

        p.Entity.Controls.NoClip = true;                 // the state vanilla leaves after that /gm sequence
        foreach (var w in StandIn(p, A, 7)) yield return w;
        bool stuck = Near(p, A) && !Warming(p, A);
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        Check("noclip", onInSpectator && offAfter && workedAfterGm && stuck && said.Contains("noclip") && said.Contains("Fixed") && Near(p, B, 3) && !p.Entity.Controls.NoClip,
            $"noclip on in spectator {onInSpectator}, off after /gm survival {offAfter}, then the translocator took FixFox {workedAfterGm}; "
            + $"with it left on: never noticed in 7 s {stuck}; /fixtl sent them {Near(p, B, 3)}, noclip now {p.Entity.Controls.NoClip}, said: {said}");
    }

    IEnumerable<double> LaneCheck(IServerPlayer p)
    {
        StonePair();
        Put(p, A);
        p.Entity.Controls.NoClip = true;
        string said = As(p, "/fixtl check");
        yield return 1;
        Check("check", said.Contains("Would fix") && said.Contains("noclip") && p.Entity.Controls.NoClip && Near(p, A) && said.Contains("/fixtl would send you through"),
            $"noclip left on {p.Entity.Controls.NoClip}, FixFox still there {Near(p, A)}, said: {said}");
    }

    IEnumerable<double> LaneSpectator(IServerPlayer p)
    {
        StonePair();
        Put(p, A);
        Console($"/gm {Fox} spectator");
        string said = As(p, "/fixtl");
        yield return 1;
        Check("spectator", said.Contains("spectator") && said.Contains("Not sending you") && Near(p, A),
            $"FixFox still there {Near(p, A)}, said: {said}");
    }

    IEnumerable<double> LaneTeleporting(IServerPlayer p)
    {
        StonePair();
        p.Entity.Teleporting = true;
        foreach (var w in StandIn(p, A, 7)) yield return w;
        bool stuck = Near(p, A);
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        Check("teleporting", stuck && said.Contains("mid-teleport") && Near(p, B, 3) && !p.Entity.Teleporting,
            $"skipped for 7 s {stuck}; /fixtl sent them {Near(p, B, 3)}, said: {said}");
    }

    IEnumerable<double> LaneIgnore(IServerPlayer p)
    {
        StonePair();
        Ignore.SetValue(p.Entity, true);
        foreach (var w in StandIn(p, A, 7)) yield return w;
        bool stuck = Near(p, A);
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        Check("ignore", stuck && said.Contains("ignoring every teleport") && Near(p, B, 3) && !(bool)Ignore.GetValue(p.Entity)!,
            $"the translocator fired but FixFox stayed for 7 s {stuck}; /fixtl sent them {Near(p, B, 3)}, said: {said}");
    }

    /// <summary>The translocator holds a warm-up entry for FixFox's entity id whose entity is an old copy stuck
    /// mid-teleport (a rejoin makes a new copy; the translocator never swaps it in).</summary>
    IEnumerable<double> LaneStaleEntry(IServerPlayer p)
    {
        StonePair();
        var ghost = sapi.World.ClassRegistry.CreateEntity(p.Entity.Properties);
        ghost.Teleporting = true;
        var d = (System.Collections.IDictionary)Tping.GetValue(sapi.World.BlockAccessor.GetBlockEntity(A))!;
        d[p.Entity.EntityId] = new TeleportingEntity { Entity = ghost, LastCollideMs = sapi.World.ElapsedMilliseconds };
        foreach (var w in StandIn(p, A, 7)) yield return w;
        bool stuck = Near(p, A);
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        Check("stale-entry", stuck && said.Contains("old copy of you") && Near(p, B, 3),
            $"skipped for 7 s {stuck}; /fixtl sent them {Near(p, B, 3)}, said: {said}");
    }

    /// <summary>The 2026-10-21 case: the only translocator keyed "wip". A look-alike "Wip " elsewhere doesn't pair.</summary>
    IEnumerable<double> LaneKeyedAlone(IServerPlayer p)
    {
        Keyed(A, "wip"); Keyed(C, "Wip ");
        yield return 1.2;
        foreach (var w in StandIn(p, A, 5)) yield return w;
        bool stuck = Near(p, A);
        string said = As(p, "/fixtl");
        yield return 1;
        Check("keyed-alone", stuck && said.Contains("no other translocator has the key \"wip\"") && said.Contains("\"Wip \"") && Near(p, A),
            $"not taken in 5 s {stuck}, still there after /fixtl {Near(p, A)}, said: {said}");
    }

    /// <summary>The block shows "home" but Translocator Relocator's list has it under "" (what a CarryOn move or a
    /// pasted schematic leaves). It never links, and typing "home" again changes nothing.</summary>
    IEnumerable<double> LaneKeyedDesync(IServerPlayer p)
    {
        var a = Keyed(A, "home", listAs: "");
        Keyed(B, "home");
        yield return 1.2;
        foreach (var w in StandIn(p, A, 5)) yield return w;
        bool stuck = Near(p, A);
        a.OnReceivedClientPacket(p, 1002, SerializerUtil.Serialize(new EditLinkKeyPacket { LinkKey = "home" }));
        bool retypeDidNothing = RelocCore.linkManager.rtData.TryGetValue(A, out string? k) && k == "";
        yield return 1.2;
        foreach (var w in StandIn(p, A, 4)) yield return w;
        bool stillStuck = Near(p, A);
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        bool listed = RelocCore.linkManager.rtData.TryGetValue(A, out string? k2) && k2 == "home";
        Check("keyed-desync", stuck && retypeDidNothing && stillStuck && listed && said.Contains("listed under \"home\" now") && Near(p, B, 3),
            $"not taken in 5 s {stuck}; typing \"home\" again left the list at \"\" {retypeDidNothing} and still not taken {stillStuck}; "
            + $"/fixtl re-listed it {listed} and sent them {Near(p, B, 3)}, said: {said}");
    }

    /// <summary>"x" is listed at A, B, and C where nothing stands any more; and D, which shows "y", is listed under
    /// "x" too. /fixtl drops C, re-lists D under "y", and sends FixFox to B, the one real partner.</summary>
    IEnumerable<double> LaneKeyedLeftover(IServerPlayer p)
    {
        Keyed(A, "x"); Keyed(B, "x"); Keyed(D, "y", listAs: "x");
        RelocCore.linkManager.AddOrSetKey(C.Copy(), "x");
        yield return 1.2;
        Put(p, A);
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        var reg = RelocCore.linkManager.rtData;
        bool cGone = !reg.ContainsKey(C), dRelisted = reg.TryGetValue(D, out string? kd) && kd == "y";
        Check("keyed-leftover", cGone && dRelisted && said.Contains("paired with one other") && Near(p, B, 3),
            $"leftover at C removed {cGone}, D re-listed under \"y\" {dRelisted}, sent to B {Near(p, B, 3)}, said: {said}");
    }

    /// <summary>B shows "Farm" but isn't in the list at all (what moving it with CarryOn leaves). From A, which is
    /// listed, it looks like A has no partner; /fixtl at A finds B in the loaded world, lists it, and sends FixFox.</summary>
    IEnumerable<double> LaneKeyedMoved(IServerPlayer p)
    {
        Keyed(A, "Farm"); Keyed(B, "Farm");
        RelocCore.linkManager.RemoveKey(B);
        yield return 1.2;
        foreach (var w in StandIn(p, A, 5)) yield return w;
        bool stuck = Near(p, A);
        string keys = Console("/fixtl keys");
        string said = As(p, "/fixtl");
        foreach (var w in Settle(p, B)) yield return w;
        bool listed = RelocCore.linkManager.rtData.TryGetValue(B, out string? k) && k == "Farm";
        Check("keyed-moved", stuck && keys.Contains("Not in the list at all") && listed && said.Contains("shows key \"Farm\" too") && Near(p, B, 3),
            $"not taken in 5 s {stuck}; /fixtl keys flagged B {keys.Contains("Not in the list at all")}; /fixtl listed B {listed} and sent them {Near(p, B, 3)}, said: {said}");
    }

    /// <summary>A stone translocator saved as linked with no destination: it errors every tick someone stands in it.</summary>
    IEnumerable<double> LaneStoneNoDest(IServerPlayer p)
    {
        var a = Stone(A, null, can: true);
        bool setUp = a.tpLocation == null && (bool)CanTele.GetValue(a)!;
        foreach (var w in StandIn(p, A, 3)) yield return w;
        bool stuck = Near(p, A);
        string said = As(p, "/fixtl");
        bool rearmed = !(bool)CanTele.GetValue(a)! && a.tpLocation == null && a.findNextChunk;
        sapi.World.BlockAccessor.SetBlock(0, A);   // before its search reaches out across the test world
        yield return 0.5;
        Check("stone-nodest", setUp && stuck && rearmed && said.Contains("had no destination") && Near(p, A),
            $"set up linked-with-nowhere {setUp}, not taken in 3 s {stuck}, searching again {rearmed}, said: {said}");
    }

    /// <summary>Its partner was picked up: /fixtl won't drop FixFox at the empty spot; /fixtl reroll (admin) sets it
    /// searching for a new one; a non-admin can't reroll.</summary>
    IEnumerable<double> LaneStoneGone(IServerPlayer p)
    {
        var a = Stone(A, B);
        Put(p, A);
        string said = As(p, "/fixtl");
        yield return 1;
        bool stayed = Near(p, A);
        string denied = As(p, "/fixtl reroll");
        string rerolled = As(p, "/fixtl reroll", admin: true);
        bool rearmed = !(bool)CanTele.GetValue(a)! && a.tpLocation == null && a.findNextChunk;
        sapi.World.BlockAccessor.SetBlock(0, A);
        yield return 0.5;
        Check("stone-gone", stayed && said.Contains("partner is gone") && said.Contains("/fixtl reroll") && denied.StartsWith("<Error>") && rearmed && rerolled.Contains("looking for a new partner"),
            $"not sent to the empty spot {stayed}; non-admin reroll refused {denied.StartsWith("<Error>")}; admin reroll set it searching {rearmed}; said: {said} // {rerolled}");
    }

    IEnumerable<double> LaneAdmin(IServerPlayer p)
    {
        StonePair();
        Put(p, A);
        string denied = As(p, $"/fixtl player {Fox}");
        p.Entity.Teleporting = true;
        string said = Console($"/fixtl player {Fox}");
        foreach (var w in Settle(p, B)) yield return w;
        Check("admin", denied.StartsWith("<Error>") && said.Contains($"Ran /fixtl for {Fox}") && said.Contains("mid-teleport") && Near(p, B, 3),
            $"a player can't run it for someone {denied.StartsWith("<Error>")}; the console's run fixed and sent {Fox} {Near(p, B, 3)}; said: {said}");
    }

    IEnumerable<double> LaneKeys(IServerPlayer p)
    {
        Keyed(A, "wip"); Keyed(C, "Wip "); Keyed(D, ""); Keyed(B, "tri"); Keyed(E, "tri"); Keyed(F, "tri");
        yield return 0.5;
        string said = Console("/fixtl keys");
        Check("keys", said.Contains("Alone (no partner): \"wip\"") && said.Contains("Look-alike keys") && said.Contains("No key yet") && said.Contains("Shared by 3"),
            $"said: {said}");
    }

    /// <summary>Repaired, unlinked, not searching: its search's answer never came back. /fixtl waits two minutes from
    /// when it first saw it, then restarts the search and tells FixFox.</summary>
    IEnumerable<double> LaneStoneStalled(IServerPlayer p)
    {
        var a = Stone(A, null, can: false, find: false);
        Put(p, A);
        long t0 = sapi.World.ElapsedMilliseconds;
        double restartedAt = -1;
        LogEntryDelegate watch = (type, message, args) =>
        {
            if (restartedAt < 0 && message.Contains("[lionfox_fixtl]") && message.Contains("its partner search never came back"))
                restartedAt = (sapi.World.ElapsedMilliseconds - t0) / 1000.0;
        };
        sapi.Logger.EntryAdded += watch;
        string said = As(p, "/fixtl");
        bool earlyChange = false;
        for (double t = 0; t < 135 && restartedAt < 0; t += 0.25)
        {
            if (t < 110 && (a.findNextChunk || (bool)CanTele.GetValue(a)!)) earlyChange = true;
            yield return 0.25;
        }
        sapi.Logger.EntryAdded -= watch;
        sapi.World.BlockAccessor.SetBlock(0, A);   // before its search reaches out across the test world
        yield return 0.5;
        Check("stone-stalled", said.Contains("waiting to hear back") && !earlyChange && restartedAt >= 115 && restartedAt <= 130,
            $"left alone for the first 110 s {!earlyChange}, search restarted at {restartedAt:0.0} s (want about 121), said: {said}");
    }
}
