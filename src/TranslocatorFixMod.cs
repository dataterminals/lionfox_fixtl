using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace lionfox_fixtl
{
    /// <summary>
    /// /fixtl: gets a player through a translocator, or says plainly why it can't and what to do.
    ///
    ///   /fixtl            check you and the translocator you're at, fix what's safe, then send you through
    ///   /fixtl check      the same checks, changing nothing
    ///   /fixtl go         just send me where this translocator leads (after clearing my own stuck state)
    ///   /fixtl unstick    only my own stuck state, no translocator needed
    ///   /fixtl diag       raw state dump, also written to the server log
    ///   /fixtl status     every online player's teleport-related state
    ///   admin: /fixtl player NAME, /fixtl all, /fixtl keys, /fixtl reroll
    ///
    /// Also fixes, on its own, the game bug behind the 2026-11-07 incident (see OnSwitchGameMode).
    /// </summary>
    public class FixTlConfig
    {
        /// <summary>Re-sync the server's noclip with the player's game mode on every game-mode change (the /gm bug).</summary>
        public bool SyncNoClipOnGameModeChange = true;
    }

    public class TranslocatorFixMod : ModSystem
    {
        const string ConfigFile = "lionfoxfixtl.json";
        ICoreServerAPI sapi = null!;
        Inspector insp = null!;

        /// <summary>ModConfig/lionfoxfixtl.json, written with everything on at first start. If it can't be read,
        /// everything runs and the file is left alone.</summary>
        public FixTlConfig Config { get; private set; } = new();

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            insp = new Inspector(api, this);
            try
            {
                var loaded = api.LoadModConfig<FixTlConfig>(ConfigFile);
                if (loaded == null) api.StoreModConfig(Config, ConfigFile);
                else Config = loaded;
            }
            catch (Exception e)
            {
                api.Logger.Warning("[lionfox_fixtl] couldn't read ModConfig/{0} ({1}); running with everything on and leaving the file alone.", ConfigFile, e.Message);
            }
            var parsers = api.ChatCommands.Parsers;

            api.ChatCommands.Create("fixtl")
                .WithDescription("Stuck at a translocator? Checks you and the translocator you're standing in, fixes what it safely can, tells you what's wrong, and sends you through when it works.")
                .RequiresPrivilege(Privilege.chat)
                // No RequiresPlayer() here: on the root it also binds every subcommand, and /fixtl player, all, keys
                // and status must work from the console and RCON (that's how Cinder runs them). 1.3.0's first test run
                // caught exactly that: "Caller must be player".
                .HandleWith(a => a.Caller.Player is IServerPlayer ? Run(Self(a), apply: true, send: true, admin: null)
                    : TextCommandResult.Error("From the console, use /fixtl player NAME."))
                .BeginSubCommand("check")
                    .WithDescription("The same checks as /fixtl, but changes nothing and sends you nowhere.")
                    .RequiresPrivilege(Privilege.chat).RequiresPlayer()
                    .HandleWith(a => Run(Self(a), apply: false, send: false, admin: null))
                .EndSubCommand()
                .BeginSubCommand("go")
                    .WithDescription("Clears your own stuck state and sends you straight to wherever the translocator you're standing in leads, skipping its warm-up.")
                    .RequiresPrivilege(Privilege.chat).RequiresPlayer()
                    .HandleWith(a => Go(Self(a)))
                .EndSubCommand()
                .BeginSubCommand("unstick")
                    .WithDescription("Clears only your own stuck teleport state (noclip left on, mid-teleport flags). No translocator needed.")
                    .RequiresPrivilege(Privilege.chat).RequiresPlayer()
                    .HandleWith(a => Unstick(Self(a)))
                .EndSubCommand()
                .BeginSubCommand("diag")
                    .WithDescription("Dumps your teleport-related state and the nearby translocators' raw state; also written to the server log.")
                    .RequiresPrivilege(Privilege.chat).RequiresPlayer()
                    .HandleWith(a => Diag(Self(a)))
                .EndSubCommand()
                .BeginSubCommand("status")
                    .WithDescription("Every online player's teleport-related state.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(_ => Status())
                .EndSubCommand()
                .BeginSubCommand("player")
                    .WithDescription("Admin: run /fixtl for the named player (they see the report too, and are sent through if the translocator works).")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("playername"))
                    .HandleWith(Named)
                .EndSubCommand()
                .BeginSubCommand("all")
                    .WithDescription("Admin: clear stuck teleport state (noclip left on, mid-teleport flags) on every online player.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(a => All(a))
                .EndSubCommand()
                .BeginSubCommand("keys")
                    .WithDescription("Admin: Translocator Relocator's keys: which translocators are alone, unkeyed, shared by 3+, or have look-alike keys.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(_ => TextCommandResult.Success(string.Join("\n", insp.KeyAudit())))
                .EndSubCommand()
                .BeginSubCommand("reroll")
                    .WithDescription("Admin: the stone translocator you're standing in forgets its partner and searches for a new one, like a freshly repaired one.")
                    .RequiresPrivilege(Privilege.controlserver).RequiresPlayer()
                    .HandleWith(a => Reroll(Self(a)))
                .EndSubCommand();

            // /gm changes the player's game mode and noclip in their world data, but not the server's copy of their
            // controls, Entity.Controls.NoClip. Only the client's own mode-change packet syncs that one. With it left
            // on, the server skips the player's block collisions (EntityBehaviorPlayerPhysics.HandleRemotePhysics),
            // so no translocator ever learns they're standing in it, while their own game still plays the warm-up.
            // 2026-11-07: /gm 3, a view-distance change (which sent that packet, so noclip went on), /gm 1: stuck.
            api.Event.PlayerSwitchGameMode += OnSwitchGameMode;

            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, SelfCheck);
        }

        void OnSwitchGameMode(IServerPlayer p)
        {
            var e = p?.Entity;
            if (e == null || !Config.SyncNoClipOnGameModeChange) return;
            bool wd = p!.WorldData.NoClip;
            if (e.Controls.NoClip == wd) return;
            e.Controls.NoClip = wd;
            sapi.Logger.Notification("[lionfox_fixtl] {0} switched to {1}: the server's noclip was still {2}; set it to {3} to match.",
                p.PlayerName, p.WorldData.CurrentGameMode, !wd, wd);
        }

        static IServerPlayer Self(TextCommandCallingArgs a) => (IServerPlayer)a.Caller.Player;

        // ---- /fixtl, /fixtl check, /fixtl player -------------------------------------------------

        TextCommandResult Run(IServerPlayer p, bool apply, bool send, string? admin)
        {
            if (p.Entity == null) return TextCommandResult.Error("No player entity.");
            var r = new Report(apply);
            insp.CheckPlayer(p, r);

            var pad = insp.FindPad(p.Entity, out bool standingIn);
            bool works = pad != null && insp.CheckPad(pad, p, r);
            bool noclip = p.WorldData.NoClip || p.WorldData.CurrentGameMode == EnumGameMode.Spectator;

            if (pad == null)
                r.Note("No translocator within 2 blocks, so I only checked you. Stand in it and run /fixtl again.");
            else if (works && !standingIn)
                r.Note("Stand in the translocator and run /fixtl again to be sent through.");
            else if (works && noclip)
                r.Note("Not sending you while you're in spectator or noclip.");
            else if (works && !apply)
                r.Note("It works; /fixtl would send you through.");
            else if (works && send)
            {
                var t = insp.SendThrough(p, pad!, r);
                if (t != null) r.Note($"Sending you through to {insp.Map(t.AsBlockPos)}.");
            }

            string head = (r.Fixes, r.Problems) switch
            {
                (0, 0) => apply ? "/fixtl: nothing wrong that I can see." : "/fixtl check: nothing wrong that I can see.",
                _ => apply ? "/fixtl:" : "/fixtl check (nothing changed):",
            };
            string text = head + "\n" + string.Join("\n", r.Lines.Select(l => "- " + l));
            string who = admin == null ? p.PlayerName : $"{p.PlayerName} (run by {admin})";
            foreach (var line in text.Split('\n'))
                sapi.Logger.Notification("[lionfox_fixtl] {0}: {1}", who, line);
            if (admin != null)
            {
                p.SendMessage(GlobalConstants.GeneralChatGroup, $"{admin} ran /fixtl for you.\n" + text, EnumChatType.Notification);
                return TextCommandResult.Success($"Ran /fixtl for {p.PlayerName}; they've been told.\n" + text);
            }
            return TextCommandResult.Success(text);
        }

        TextCommandResult Named(TextCommandCallingArgs args)
        {
            string? name = args[0] as string;
            var p = sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
                .FirstOrDefault(x => string.Equals(x.PlayerName, name, StringComparison.OrdinalIgnoreCase));
            if (p?.Entity == null) return TextCommandResult.Error($"No online player named '{name}'.");
            return Run(p, apply: true, send: true, admin: args.Caller.GetName());
        }

        // ---- /fixtl go, unstick, all, status, reroll ---------------------------------------------

        TextCommandResult Go(IServerPlayer p)
        {
            var r = new Report(true);
            insp.CheckPlayer(p, r);
            var pad = insp.FindPad(p.Entity, out _);
            if (pad == null) return TextCommandResult.Error("No translocator within 2 blocks. Stand in one and try again.");
            var t = insp.SendThrough(p, pad, r);
            r.Note(t != null ? $"Sending you to {insp.Map(t.AsBlockPos)}." : "Nowhere to send you; /fixtl says why.");
            return TextCommandResult.Success("/fixtl go:\n" + string.Join("\n", r.Lines.Select(l => "- " + l)));
        }

        TextCommandResult Unstick(IServerPlayer p)
        {
            var r = new Report(true);
            insp.CheckPlayer(p, r);
            if (r.Lines.Count == 0) r.Note("Nothing stuck on you.");
            return TextCommandResult.Success("/fixtl unstick:\n" + string.Join("\n", r.Lines.Select(l => "- " + l)));
        }

        TextCommandResult All(TextCommandCallingArgs args)
        {
            var sb = new StringBuilder();
            int n = 0, fixedPlayers = 0;
            foreach (var p in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (p.Entity == null) continue;
                n++;
                var r = new Report(true);
                insp.CheckPlayer(p, r);
                if (r.Fixes == 0) continue;
                fixedPlayers++;
                sb.AppendLine($"{p.PlayerName}:");
                foreach (var l in r.Lines) sb.AppendLine("  " + l);
            }
            sapi.Logger.Notification("[lionfox_fixtl] /fixtl all by {0}: {1} of {2} player(s) needed fixing.", args.Caller.GetName(), fixedPlayers, n);
            return TextCommandResult.Success($"Checked {n} online player(s); {fixedPlayers} needed fixing.\n" + sb.ToString().TrimEnd());
        }

        TextCommandResult Status()
        {
            var sb = new StringBuilder();
            foreach (var p in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                var e = p.Entity;
                if (e == null) continue;
                bool ignore = R.Get<bool>(R.IgnoreTeleportCall, e);
                sb.AppendLine($"{p.PlayerName}: {p.WorldData.CurrentGameMode}, noclip {p.WorldData.NoClip} (server's copy {e.Controls.NoClip}"
                    + $"{(e.Controls.NoClip != p.WorldData.NoClip ? ", OUT OF STEP" : "")}), mid-teleport {e.Teleporting}, ignoring teleports {ignore}");
            }
            return TextCommandResult.Success(sb.Length == 0 ? "Nobody online." : sb.ToString().TrimEnd());
        }

        TextCommandResult Reroll(IServerPlayer p)
        {
            var pad = insp.FindPad(p.Entity, out _);
            if (pad == null || !R.IsStatic(pad)) return TextCommandResult.Error("Stand in a repaired stone translocator first.");
            return insp.Reroll(pad, out string msg) ? TextCommandResult.Success(msg) : TextCommandResult.Error(msg);
        }

        // ---- a search that may never answer -------------------------------------------------------

        readonly Dictionary<BlockPos, long> stalledSince = new();
        readonly Dictionary<BlockPos, string?> stalledWatch = new();

        internal long StalledSince(BlockPos pos, IServerPlayer? by)
        {
            if (!stalledSince.TryGetValue(pos, out long t)) stalledSince[pos.Copy()] = t = sapi.World.ElapsedMilliseconds;
            return t;
        }

        internal void ForgetStalled(BlockPos pos)
        {
            stalledSince.Remove(pos);
            stalledWatch.Remove(pos);
        }

        /// <summary>Looks again once two minutes have passed since it was first seen waiting; restarts the search if
        /// it's still waiting, and tells whoever asked.</summary>
        internal void WatchStalled(BlockPos pos, IServerPlayer? by)
        {
            if (stalledWatch.ContainsKey(pos)) return;
            var at = pos.Copy();
            stalledWatch[at] = by?.PlayerUID;
            long wait = Math.Max(1000, stalledSince[at] + 121_000 - sapi.World.ElapsedMilliseconds);
            sapi.Event.RegisterCallback(_ =>
            {
                stalledWatch.TryGetValue(at, out string? uid);
                stalledWatch.Remove(at);
                var be = sapi.World.BlockAccessor.GetBlockEntity(at);
                if (!R.IsStatic(be)) { ForgetStalled(at); return; }
                var r = new Report(true);
                insp.CheckPad(be!, null, r);
                var who = uid == null ? null : sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (who?.ConnectionState == EnumClientState.Playing && r.Lines.Count > 0)
                    who.SendMessage(GlobalConstants.GeneralChatGroup, "/fixtl, two minutes later:\n" + string.Join("\n", r.Lines.Select(l => "- " + l)), EnumChatType.Notification);
            }, (int)wait);
        }

        // ---- /fixtl diag ---------------------------------------------------------------------------

        TextCommandResult Diag(IServerPlayer player)
        {
            var e = player.Entity;
            var sb = new StringBuilder();
            sb.AppendLine($"=== /fixtl diag for {player.PlayerName} (eid {e.EntityId}) ===");
            sb.AppendLine($"GameMode {player.WorldData.CurrentGameMode}; noclip: world data {player.WorldData.NoClip}, server controls {e.Controls.NoClip}");
            sb.AppendLine($"Teleporting {e.Teleporting}, IsTeleport {e.IsTeleport}, ignoreTeleportCall {(R.IgnoreTeleportCall == null ? "<missing>" : R.Get<bool>(R.IgnoreTeleportCall, e).ToString())}");
            sb.AppendLine($"MountedOn {(e.MountedOn?.Entity?.Code?.ToString() ?? (e.MountedOn != null ? "<seat>" : "<none>"))}");
            sb.AppendLine($"Pos {e.Pos.X:F2}, {e.Pos.Y:F2}, {e.Pos.Z:F2} (map {insp.Map(e.Pos.AsBlockPos)})");
            var cb = e.CollisionBox;
            sb.AppendLine($"CollisionBox ({cb?.X1:F2},{cb?.Y1:F2},{cb?.Z1:F2}) -> ({cb?.X2:F2},{cb?.Y2:F2},{cb?.Z2:F2})");

            foreach (var be in insp.TeleportersNear(e.Pos.AsBlockPos, 6).OrderBy(b => e.Pos.XYZ.DistanceTo(b.Pos.ToVec3d().Add(0.5, 0, 0.5))))
            {
                sb.Append($"{be.GetType().Name} @ {be.Pos} (map {insp.Map(be.Pos)})");
                if (R.IsStatic(be))
                    sb.Append($": state {be.Block?.Variant?["state"]}, repair {R.Get<int>(R.StRepairState, be)}/{R.Get<int>(R.StRequired, be)}, canTeleport {R.Get<bool>(R.StCanTeleport, be)}, findNextChunk {R.Get<bool>(R.StFindNextChunk, be)}, tpLocation {R.Get<BlockPos>(R.StTpLocation, be)?.ToString() ?? "null"}");
                if (R.IsReloc(be))
                {
                    var reg = R.Registry();
                    string listed = reg == null ? "<unreadable>" : reg.TryGetValue(be.Pos, out string? k) ? $"\"{k}\"" : "<not listed>";
                    int partners = reg?.Count(kv => kv.Value == R.LinkKey(be) && !(kv.Key.X == be.Pos.X && kv.Key.Y == be.Pos.Y && kv.Key.Z == be.Pos.Z)) ?? -1;
                    sb.Append($": key \"{R.LinkKey(be)}\", listed as {listed}, {partners} other(s) listed under its key");
                }
                sb.AppendLine();
                var d = R.Tping(be);
                if (d == null) continue;
                foreach (DictionaryEntry kv in d)
                {
                    var ent = R.Get<Entity>(R.TeEntity, kv.Value);
                    long since = sapi.World.ElapsedMilliseconds - R.Get<long>(R.TeLastCollideMs, kv.Value);
                    sb.AppendLine($"  warming up: eid {kv.Key}{(ReferenceEquals(ent, e) ? " (you)" : ent?.EntityId == e.EntityId ? " (an OLD copy of you)" : "")}, {R.Get<float>(R.TeSecondsPassed, kv.Value):F2} s, last touched {since} ms ago");
                }
            }
            string report = sb.ToString().TrimEnd();
            foreach (var line in report.Split('\n')) sapi.Logger.Notification("[lionfox_fixtl] " + line.TrimEnd('\r'));
            return TextCommandResult.Success(report + "\n(Also written to the server log.)");
        }

        // ---- startup self-check -----------------------------------------------------------------

        // Every reflected member degrades to "not checked" rather than throwing, which is right for a recovery
        // command but means a renamed member would make a mod that loads clean and quietly fixes nobody. So resolve
        // them all once at RunGame (GameContent and Translocator Relocator are other mods, started before then) and
        // say so loudly in the log.
        void SelfCheck()
        {
            var missing = R.Resolve();
            if (missing.Count == 0)
            {
                sapi.Logger.Notification("[lionfox_fixtl] self-check ok: every reflected member resolved on this build; Translocator Relocator {0}; noclip re-sync on game-mode change {1}.",
                    R.RelocatorLoaded ? "found, so keyed translocators are checked too" : "not installed, so only the game's own translocators are checked",
                    Config.SyncNoClipOnGameModeChange ? "on" : "OFF (ModConfig)");
                return;
            }
            sapi.Logger.Warning("[lionfox_fixtl] SELF-CHECK FAILED: {0} member(s) didn't resolve on this build. /fixtl still runs, but skips what these cover:", missing.Count);
            foreach (var m in missing) sapi.Logger.Warning("[lionfox_fixtl]   MISSING: {0}", m);
        }
    }
}
