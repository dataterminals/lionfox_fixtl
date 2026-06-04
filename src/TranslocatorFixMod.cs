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
                    .WithDescription("Dump translocator-relevant state for the caller: Teleporting/IsTeleport flags, Pos vs ServerPos delta, MountedOn, ignoreTeleportCall (reflected), NoClip, and nearby teleporter block entities' tpingEntities entries.")
                    .RequiresPrivilege(Privilege.chat)
                    .HandleWith(Diag)
                .EndSubCommand()
                .BeginSubCommand("unstick")
                    .WithDescription("Force-resync the caller: clears Teleporting + ignoreTeleportCall (via reflection) and re-runs TeleportToDouble to the entity's current ServerPos so clients snap. Use when warmup completed but you didn't actually move.")
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
            sb.AppendLine($"ServerPos         : {e.ServerPos.X:F2}, {e.ServerPos.Y:F2}, {e.ServerPos.Z:F2}");
            double dx = e.Pos.X - e.ServerPos.X;
            double dy = e.Pos.Y - e.ServerPos.Y;
            double dz = e.Pos.Z - e.ServerPos.Z;
            sb.AppendLine($"|Pos-ServerPos|   : {Math.Sqrt(dx * dx + dy * dy + dz * dz):F2} blocks");
            sb.AppendLine($"PreviousServerPos : {e.PreviousServerPos.X:F2}, {e.PreviousServerPos.Y:F2}, {e.PreviousServerPos.Z:F2}");
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

            double x = e.ServerPos.X, y = e.ServerPos.Y, z = e.ServerPos.Z;
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

        static string SafeNoClip(Entity e)
        {
            var controls = FindField(e, "controls")?.GetValue(e)
                        ?? FindField(e, "servercontrols")?.GetValue(e);
            if (controls == null) return "<no controls field>";
            var nc = controls.GetType().GetField("NoClip");
            if (nc == null) return "<NoClip field missing>";
            return nc.GetValue(controls)?.ToString() ?? "<null>";
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
