using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace lionfox_fixtl
{
    public class TranslocatorFixMod : ModSystem
    {
        ICoreServerAPI? sapi;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            this.sapi = sapi;

            sapi.ChatCommands.Create("fixtl")
                .WithDescription("Clear stuck translocator state. Default action clears the Teleporting flag; use /fixtl unstick for the full force-resync that handles the gamemode-shift case.")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(FixSelf)
                .BeginSubCommand("status")
                    .WithDescription("List all online players and whether their Teleporting flag is currently set.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(Status)
                .EndSubCommand()
                .BeginSubCommand("diag")
                    .WithDescription("Dump translocator-relevant state for the caller: Teleporting/IsTeleport flags, Pos and PreviousServerPos with the movement delta between them, MountedOn, ignoreTeleportCall (reflected), NoClip, and nearby teleporter block entities' tpingEntities entries.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(Diag)
                .EndSubCommand()
                .BeginSubCommand("unstick")
                    .WithDescription("Force-resync the caller: clears Teleporting + ignoreTeleportCall (via reflection) and re-runs TeleportToDouble to the entity's current Pos so clients snap. Use when warmup completed but you didn't actually move.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(UnstickSelf)
                .EndSubCommand()
                .BeginSubCommand("go")
                    .WithDescription("Bypass the teleporter's HandleTeleportingServer loop entirely: find the translocator pad you're standing on, read its tpLocation, and TeleportToDouble there directly. Workaround when warmup runs but the server-side loop never actually fires the teleport.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(GoSelf)
                .EndSubCommand()
                .BeginSubCommand("all")
                    .WithDescription("Clear the stuck Teleporting flag on every online player.")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(FixAll)
                .EndSubCommand()
                .BeginSubCommand("player")
                    .WithDescription("Clear the stuck flag on the named player. Usage: /fixtl player <name>")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(sapi.ChatCommands.Parsers.Word("playername"))
                    .HandleWith(FixNamed)
                .EndSubCommand();

            // Deferred to RunGame: the GameContent types this checks for live in
            // VSSurvivalMod, so checking during StartServerSide could report them
            // missing purely because that mod has not been started yet.
            sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => SelfCheck(sapi));
        }

        TextCommandResult FixSelf(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                return TextCommandResult.Error("No player entity to inspect.");

            return ClearOne(player, "self") ?? TextCommandResult.Success(
                "Teleporting flag was already clear. If the translocator still won't fire, try /fixtl unstick (gamemode-shift case) or /fixtl diag to inspect state.");
        }

        TextCommandResult FixAll(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");

            int cleared = 0;
            int checkedCount = 0;
            var detail = new StringBuilder();

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.Entity == null) continue;
                checkedCount++;
                if (player.Entity.Teleporting)
                {
                    player.Entity.Teleporting = false;
                    cleared++;
                    detail.AppendLine($"  cleared: {player.PlayerName} (eid {player.Entity.EntityId})");
                    sapi.Logger.Notification($"[lionfox_fixtl] Cleared stuck Teleporting flag on {player.PlayerName} (eid {player.Entity.EntityId}) via /fixtl all from {args.Caller.GetName()}.");
                }
            }

            string msg = $"Checked {checkedCount} online player(s), cleared {cleared} stuck Teleporting flag(s).";
            if (cleared > 0) msg += "\n" + detail.ToString().TrimEnd();
            return TextCommandResult.Success(msg);
        }

        TextCommandResult FixNamed(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");

            string? name = args[0] as string;
            if (string.IsNullOrWhiteSpace(name)) return TextCommandResult.Error("Usage: /fixtl player <name>");

            var target = sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(p => string.Equals(p.PlayerName, name, StringComparison.OrdinalIgnoreCase));

            if (target == null) return TextCommandResult.Error($"No online player named '{name}'.");
            if (target.Entity == null) return TextCommandResult.Error($"{target.PlayerName} has no entity.");

            return ClearOne(target, $"admin {args.Caller.GetName()}") ?? TextCommandResult.Success($"{target.PlayerName}: flag was already clear.");
        }

        TextCommandResult Status(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");

            var sb = new StringBuilder();
            int stuck = 0;
            int total = 0;
            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.Entity == null) continue;
                total++;
                bool flag = player.Entity.Teleporting;
                if (flag) stuck++;
                sb.AppendLine($"  {player.PlayerName} (eid {player.Entity.EntityId}): Teleporting={flag}");
            }
            sb.Insert(0, $"Status: {stuck}/{total} online player(s) have Teleporting=true.\n");
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        TextCommandResult? ClearOne(IServerPlayer player, string source)
        {
            if (sapi == null) return null;
            if (!player.Entity.Teleporting) return null;

            player.Entity.Teleporting = false;
            sapi.Logger.Notification($"[lionfox_fixtl] Cleared stuck Teleporting flag on {player.PlayerName} (eid {player.Entity.EntityId}) via /fixtl ({source}).");
            return TextCommandResult.Success($"Cleared stuck Teleporting flag on {player.PlayerName}. Try a translocator now.");
        }

        // --- diag ---------------------------------------------------------------

        TextCommandResult Diag(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");
            if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                return TextCommandResult.Error("No player entity to inspect.");

            var e = player.Entity;
            var sb = new StringBuilder();
            sb.AppendLine($"=== /fixtl diag for {player.PlayerName} (eid {e.EntityId}) ===");
            sb.AppendLine($"Teleporting       : {e.Teleporting}");
            sb.AppendLine($"IsTeleport        : {e.IsTeleport}");
            sb.AppendLine($"MountedOn         : {(e.MountedOn?.Entity?.GetType().Name ?? "<none>")}");
            sb.AppendLine($"Pos               : {e.Pos.X:F2}, {e.Pos.Y:F2}, {e.Pos.Z:F2}");
            // The old "ServerPos" line and the |Pos-ServerPos| delta are GONE, not
            // ported. On 1.22 Entity.ServerPos and Entity.SidedPos are both obsolete
            // shims marked "Use Pos instead" - the three have been collapsed onto one
            // property. Printing ServerPos would restate Pos, and the delta would be a
            // hard 0.00 every single time.
            //
            // That is worse than removing it. This is a diagnostic somebody reads while
            // a player is stuck in a translocator, and a field that always reads 0.00
            // looks like PROOF the position is synced rather than proof the measurement
            // is dead. PreviousServerPos is NOT obsolete and is still a distinct value,
            // so the movement-since-last-tick delta below carries real information.
            sb.AppendLine($"PreviousServerPos : {e.PreviousServerPos.X:F2}, {e.PreviousServerPos.Y:F2}, {e.PreviousServerPos.Z:F2}");
            double dx = e.Pos.X - e.PreviousServerPos.X;
            double dy = e.Pos.Y - e.PreviousServerPos.Y;
            double dz = e.Pos.Z - e.PreviousServerPos.Z;
            sb.AppendLine($"|Pos-PrevServer|  : {Math.Sqrt(dx * dx + dy * dy + dz * dz):F2} blocks (movement since last sync)");
            sb.AppendLine($"Motion            : {e.Pos.Motion.X:F3}, {e.Pos.Motion.Y:F3}, {e.Pos.Motion.Z:F3}");

            var ignoreVal = TryReadField<bool>(e, "ignoreTeleportCall", out bool ignoreOk);
            sb.AppendLine($"ignoreTeleportCall: {(ignoreOk ? ignoreVal.ToString() : "<field not found>")}");

            sb.AppendLine($"GameMode          : {player.WorldData?.CurrentGameMode}");
            sb.AppendLine($"NoClip (controls) : {SafeNoClip(e)}");
            var cb = e.CollisionBox;
            sb.AppendLine($"CollisionBox      : ({cb?.X1:F2},{cb?.Y1:F2},{cb?.Z1:F2}) -> ({cb?.X2:F2},{cb?.Y2:F2},{cb?.Z2:F2})  size {(cb?.X2 - cb?.X1):F2}x{(cb?.Y2 - cb?.Y1):F2}x{(cb?.Z2 - cb?.Z1):F2}");

            int nearbyBe = 0;
            int containsSelf = 0;
            var beDetail = new StringBuilder();
            foreach (var (be, dist) in NearbyTeleporterBEs(e, radius: 6))
            {
                nearbyBe++;
                var dict = TryReadField<IDictionary>(be, "tpingEntities", out bool dictOk);
                if (!dictOk || dict == null)
                {
                    beDetail.AppendLine($"  {be.GetType().Name} @ {be.Pos} (dist {dist:F1}): <tpingEntities unreadable>");
                    continue;
                }

                bool selfInDict = false;
                var entries = new List<string>();
                foreach (DictionaryEntry kv in dict)
                {
                    long eid = (long)kv.Key;
                    var val = kv.Value;
                    float secondsPassed = TryReadFieldDirect<float>(val, "SecondsPassed");
                    long lastCollideMs = TryReadFieldDirect<long>(val, "LastCollideMs");
                    long sinceMs = sapi.World.ElapsedMilliseconds - lastCollideMs;
                    entries.Add($"eid={eid} secondsPassed={secondsPassed:F2} sinceCollide={sinceMs}ms");
                    if (eid == e.EntityId) selfInDict = true;
                }
                if (selfInDict) containsSelf++;

                beDetail.AppendLine($"  {be.GetType().Name} @ {be.Pos} (dist {dist:F1}, {dict.Count} entries{(selfInDict ? ", SELF" : "")}):");
                foreach (var s in entries) beDetail.AppendLine($"    {s}");
            }

            sb.AppendLine($"Nearby teleporter BEs (r=6): {nearbyBe}, containing self: {containsSelf}");
            if (nearbyBe > 0) sb.Append(beDetail.ToString().TrimEnd());

            string report = sb.ToString().TrimEnd();
            // Mirror to server log so it can't be lost to chat scrollback/truncation.
            foreach (var line in report.Split('\n'))
                sapi.Logger.Notification("[lionfox_fixtl] " + line.TrimEnd('\r'));
            return TextCommandResult.Success(report + "\n(Full report also written to server log.)");
        }

        // --- unstick ------------------------------------------------------------

        TextCommandResult UnstickSelf(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");
            if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                return TextCommandResult.Error("No player entity to inspect.");

            var e = player.Entity;
            var actions = new List<string>();

            if (e.Teleporting)
            {
                e.Teleporting = false;
                actions.Add("cleared Teleporting=true");
            }

            var priorIgnore = TryReadField<bool>(e, "ignoreTeleportCall", out bool ignoreReadable);
            if (TryWriteField(e, "ignoreTeleportCall", false, out _))
            {
                actions.Add($"ignoreTeleportCall: prior={(ignoreReadable ? priorIgnore.ToString() : "?")} -> false");
            }
            else
            {
                actions.Add("ignoreTeleportCall: <field not writable>");
            }

            // Was e.ServerPos - obsolete on 1.22 ("Use Pos instead"), and Pos IS the
            // authoritative server-side position here, so the semantics are unchanged.
            double x = e.Pos.X, y = e.Pos.Y, z = e.Pos.Z;
            try
            {
                e.TeleportToDouble(x, y, z);
                actions.Add($"re-broadcast position via TeleportToDouble({x:F2},{y:F2},{z:F2})");
            }
            catch (Exception ex)
            {
                actions.Add($"TeleportToDouble threw: {ex.GetType().Name}: {ex.Message}");
            }

            string summary = string.Join("; ", actions);
            sapi.Logger.Notification($"[lionfox_fixtl] /fixtl unstick for {player.PlayerName} (eid {e.EntityId}): {summary}");
            return TextCommandResult.Success($"Force-resync done: {summary}\nIf you still can't translocate, step off the pad and back on.");
        }

        // --- go: bypass HandleTeleportingServer by teleporting straight to the pad's target

        TextCommandResult GoSelf(TextCommandCallingArgs args)
        {
            if (sapi == null) return TextCommandResult.Error("Server API unavailable.");
            if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                return TextCommandResult.Error("No player entity to inspect.");

            var e = player.Entity;
            BlockEntity? pad = null;
            double padDist = double.MaxValue;
            foreach (var (be, dist) in NearbyTeleporterBEs(e, radius: 2))
            {
                if (dist < padDist) { pad = be; padDist = dist; }
            }
            if (pad == null) return TextCommandResult.Error("No translocator pad within 2 blocks. Stand on one and try again.");

            // Resolve destination. Prefer calling GetTarget(entity) on the actual subclass
            // (e.g. BlockEntityStaticTranslocator returns tpLocation + (-0.3, 1, -0.3)).
            Vec3d? target = null;
            string targetSource;
            try
            {
                var getTarget = pad.GetType().GetMethod("GetTarget", new[] { typeof(Entity) });
                if (getTarget != null)
                {
                    target = getTarget.Invoke(pad, new object[] { e }) as Vec3d;
                    targetSource = "GetTarget(entity)";
                }
                else
                {
                    targetSource = "<GetTarget missing>";
                }
            }
            catch (Exception ex)
            {
                targetSource = $"<GetTarget threw {ex.GetType().Name}>";
            }

            if (target == null)
            {
                // Fallback: read tpLocation field directly.
                var tpLoc = TryReadField<BlockPos>(pad, "tpLocation", out bool tpOk);
                if (tpOk && tpLoc != null)
                {
                    target = new Vec3d(tpLoc.X - 0.3, tpLoc.Y + 1.0, tpLoc.Z - 0.3);
                    targetSource = "tpLocation field";
                }
            }

            if (target == null)
                return TextCommandResult.Error($"Pad {pad.GetType().Name} @ {pad.Pos} has no resolvable destination (source: {targetSource}). Pair may not have been discovered yet.");

            // Apply offset if the BE uses pad-relative target coords.
            var isOffset = TryReadField<bool>(pad, "tpLocationIsOffset", out bool _);
            if (isOffset) target = target.Clone().Add(pad.Pos.X, pad.Pos.Y, pad.Pos.Z);

            // Defensive: clear ignoreTeleportCall so TeleportToDouble actually runs.
            TryWriteField(e, "ignoreTeleportCall", false, out _);
            e.Teleporting = false;

            try
            {
                e.TeleportToDouble(target.X, target.Y, target.Z);
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"[lionfox_fixtl] /fixtl go TeleportToDouble threw for {player.PlayerName}: {ex}");
                return TextCommandResult.Error($"TeleportToDouble threw: {ex.GetType().Name}: {ex.Message}");
            }

            sapi.Logger.Notification($"[lionfox_fixtl] /fixtl go for {player.PlayerName} (eid {e.EntityId}): pad {pad.GetType().Name} @ {pad.Pos} -> ({target.X:F2},{target.Y:F2},{target.Z:F2}) via {targetSource}");
            return TextCommandResult.Success($"Bypassed BE loop: teleported to ({target.X:F2}, {target.Y:F2}, {target.Z:F2}) via {targetSource} on {pad.GetType().Name}.");
        }

        // --- startup self-check -------------------------------------------------

        // WHY THIS EXISTS. Every reflective lookup below is individually guarded and
        // degrades to a placeholder string rather than throwing: "<field not found>",
        // "<GetTarget missing>", "<tpingEntities unreadable>". That is the right
        // behaviour for a recovery command - but it means a renamed member produces a
        // mod that loads clean, reports healthy, and silently fixes nobody. The first
        // evidence would be a player stuck in a translocator who cannot be unstuck,
        // which is the worst possible moment to discover it.
        //
        // So resolve everything once, up front, and say so loudly. This does not fix a
        // break; it makes the break arrive as a log line at startup instead of as a
        // confused player mid-incident.
        //
        // It runs at RunGame rather than in StartServerSide because the GameContent
        // types live in VSSurvivalMod, another mod - checking too early would report
        // its types missing purely because of load order.
        void SelfCheck(ICoreServerAPI api)
        {
            var missing = new List<string>();

            // API-side. EntityAgent is a compile-time reference, so this one is cheap.
            if (typeof(EntityAgent).GetField("ignoreTeleportCall",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null)
                missing.Add("EntityAgent.ignoreTeleportCall (used by /fixtl unstick and /fixtl go)");

            // GameContent side, resolved BY NAME because this mod deliberately carries
            // no VSSurvivalMod reference.
            var teleBase = FindTypeByName("BlockEntityTeleporterBase");
            if (teleBase == null)
            {
                missing.Add("type BlockEntityTeleporterBase - /fixtl diag and /fixtl go see NO teleporters at all");
            }
            else
            {
                if (FindFieldOn(teleBase, "tpingEntities") == null)
                    missing.Add("BlockEntityTeleporterBase.tpingEntities (used by /fixtl diag)");
                if (FindFieldOn(teleBase, "tpLocationIsOffset") == null)
                    missing.Add("BlockEntityTeleporterBase.tpLocationIsOffset (used by /fixtl go)");
                if (teleBase.GetMethod("GetTarget", new[] { typeof(Entity) }) == null)
                    missing.Add("BlockEntityTeleporterBase.GetTarget(Entity) (primary destination lookup for /fixtl go)");
            }

            var statTl = FindTypeByName("BlockEntityStaticTranslocator");
            if (statTl == null)
                missing.Add("type BlockEntityStaticTranslocator (fallback destination lookup for /fixtl go)");
            else if (FindFieldOn(statTl, "tpLocation") == null)
                missing.Add("BlockEntityStaticTranslocator.tpLocation (fallback destination for /fixtl go)");

            var tping = FindTypeByName("TeleportingEntity");
            if (tping == null)
            {
                missing.Add("type TeleportingEntity (/fixtl diag cannot report warmup progress)");
            }
            else
            {
                if (FindFieldOn(tping, "SecondsPassed") == null)
                    missing.Add("TeleportingEntity.SecondsPassed (used by /fixtl diag)");
                if (FindFieldOn(tping, "LastCollideMs") == null)
                    missing.Add("TeleportingEntity.LastCollideMs (used by /fixtl diag)");
            }

            if (missing.Count == 0)
            {
                api.Logger.Notification(
                    "[lionfox_fixtl] self-check ok - all 7 reflected members resolved against this build. "
                    + "/fixtl, status, diag, unstick and go should all function.");
                return;
            }

            api.Logger.Warning(
                "[lionfox_fixtl] SELF-CHECK FAILED: {0} reflected member(s) could not be resolved on this game build. "
                + "The mod HAS LOADED and its commands will still run, but the affected ones will report a "
                + "placeholder instead of working. Treat this as broken:", missing.Count);
            foreach (var m in missing)
                api.Logger.Warning("[lionfox_fixtl]   MISSING: {0}", m);
        }

        static Type? FindTypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                // GetTypes() throws when ANY referenced type fails to load, and the
                // partial result is on the exception. Swallowing that turns "three
                // types unloadable" into "assembly empty" - the same silent
                // degradation this self-check exists to prevent.
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var t in types)
                    if (t != null && t.Name == name) return t;
            }
            return null;
        }

        static FieldInfo? FindFieldOn(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public
                                       | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        // --- reflection helpers -------------------------------------------------

        static FieldInfo? FindField(object target, string name)
        {
            var t = target.GetType();
            while (t != null)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        static T? TryReadField<T>(object target, string name, out bool ok)
        {
            var f = FindField(target, name);
            if (f == null) { ok = false; return default; }
            try { ok = true; return (T?)f.GetValue(target); }
            catch { ok = false; return default; }
        }

        static T TryReadFieldDirect<T>(object? target, string name)
        {
            if (target == null) return default!;
            var f = FindField(target, name);
            if (f == null) return default!;
            try { return (T)f.GetValue(target)!; } catch { return default!; }
        }

        static bool TryWriteField(object target, string name, object? value, out bool previousValueWasTruthy)
        {
            previousValueWasTruthy = false;
            var f = FindField(target, name);
            if (f == null) return false;
            try
            {
                var prev = f.GetValue(target);
                previousValueWasTruthy = prev is bool b && b;
                f.SetValue(target, value);
                return true;
            }
            catch { return false; }
        }

        // No reflection here any more. EntityAgent exposes PUBLIC Controls and
        // ServerControls properties over the protected `controls` / `servercontrols`
        // fields this used to reach for, and EntityControls.NoClip is a public field -
        // verified against 1.22.6. That was three reflective lookups and three silent
        // failure strings bought for nothing, on members with no stability contract.
        // A rename now becomes a compile error instead of "<no controls field>" in
        // someone's chat window.
        static string SafeNoClip(Entity e)
        {
            if (e is not EntityAgent agent) return "<not an EntityAgent>";
            var controls = agent.Controls ?? agent.ServerControls;
            if (controls == null) return "<null controls>";
            return controls.NoClip.ToString();
        }

        // Walk loaded chunks in a small bounding box around the entity and return
        // BlockEntities that derive from BlockEntityTeleporterBase. We match by
        // type name to avoid a hard reference on VSSurvivalMod.
        IEnumerable<(BlockEntity be, double dist)> NearbyTeleporterBEs(Entity e, int radius)
        {
            if (sapi == null) yield break;
            var origin = e.Pos.XYZ;
            var min = new BlockPos((int)origin.X - radius, Math.Max(0, (int)origin.Y - radius), (int)origin.Z - radius);
            var max = new BlockPos((int)origin.X + radius, (int)origin.Y + radius, (int)origin.Z + radius);

            var found = new List<(BlockEntity, double)>();
            sapi.World.BlockAccessor.WalkBlocks(min, max, (block, x, y, z) =>
            {
                var be = sapi.World.BlockAccessor.GetBlockEntity(new BlockPos(x, y, z));
                if (be == null) return;
                if (!IsTeleporterBE(be)) return;
                double d = origin.DistanceTo(new Vec3d(x + 0.5, y + 0.5, z + 0.5));
                found.Add((be, d));
            }, centerOrder: true);

            foreach (var x in found.OrderBy(t => t.Item2)) yield return x;
        }

        static bool IsTeleporterBE(BlockEntity be)
        {
            for (var t = be.GetType(); t != null; t = t.BaseType)
            {
                if (t.Name == "BlockEntityTeleporterBase") return true;
            }
            return false;
        }
    }
}
