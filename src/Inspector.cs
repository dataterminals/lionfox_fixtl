using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace lionfox_fixtl
{
    /// <summary>What /fixtl found, in plain words, and whether it changed anything.</summary>
    sealed class Report
    {
        public readonly bool Apply;
        public readonly List<string> Lines = new();
        public int Fixes, Problems;
        public Report(bool apply) { Apply = apply; }

        public void Fix(string text) { Fixes++; Lines.Add((Apply ? "Fixed: " : "Would fix: ") + text); }
        public void Problem(string text) { Problems++; Lines.Add("Problem: " + text); }
        public void Note(string text) => Lines.Add(text);
    }

    /// <summary>
    /// Everything that can leave a player unable to pass a translocator on 1.22.6 with Translocator Relocator, the
    /// check for each, and the fix where one is safe. Found by decompiling, then matched to LionFox's two incidents:
    ///
    ///  * 2026-10-21: the keyed translocator "wip" was the only one in the world with that key, so it had no partner.
    ///  * 2026-11-07 04:17 UTC: /gm 3 then /gm 1 left the server's own noclip switch on (see NoClipSync), so the server
    ///    stopped noticing her stand in anything; her game still played the warm-up.
    ///
    /// Player side: noclip out of step, spectator, Entity.Teleporting stuck, ignoreTeleportCall stuck, a mount, a
    /// translocator still holding a warm-up entry for an old copy of the player.
    /// Stone translocator: broken, not fully repaired, searching, a search that never answered, "linked" with no
    /// destination, a partner that's gone. Keyed translocator: no key, the relocator's list out of step with the
    /// key on the block, no partner (with look-alike keys), leftover list entries, three or more on one key.
    /// </summary>
    sealed class Inspector
    {
        readonly ICoreServerAPI sapi;
        readonly TranslocatorFixMod mod;
        public Inspector(ICoreServerAPI sapi, TranslocatorFixMod mod) { this.sapi = sapi; this.mod = mod; }

        const double StalledAfterMs = 120_000;

        // ---- the player ----------------------------------------------------------------------------

        public void CheckPlayer(IServerPlayer p, Report r)
        {
            var e = p.Entity;
            bool wd = p.WorldData.NoClip;
            if (e.Controls.NoClip != wd)
            {
                r.Fix(wd
                    ? "the server didn't know you were in noclip."
                    : "the server still had you in noclip from an earlier game-mode change, so translocators (and pressure plates and the like) couldn't feel you standing in them. That's switched off now.");
                if (r.Apply)
                {
                    e.Controls.NoClip = wd;
                    Log($"{p.PlayerName}: server-side noclip was {!wd}, game mode {p.WorldData.CurrentGameMode} says {wd}; synced.");
                }
            }
            if (wd || p.WorldData.CurrentGameMode == EnumGameMode.Spectator)
                r.Problem($"you're in {(p.WorldData.CurrentGameMode == EnumGameMode.Spectator ? "spectator" : "noclip")}, and translocators can't feel a player passing through things. Switch back (/gm 1 for survival), then step in again.");

            if (e.Teleporting)
            {
                r.Fix("you were marked as mid-teleport, so every translocator skipped you.");
                if (r.Apply) { e.Teleporting = false; Log($"{p.PlayerName}: cleared Entity.Teleporting."); }
            }
            if (R.Get<bool>(R.IgnoreTeleportCall, e))
            {
                r.Fix("the game was ignoring every teleport for you (left over from a teleport that failed half-way).");
                if (r.Apply) { R.Set(R.IgnoreTeleportCall, e, false); Log($"{p.PlayerName}: cleared ignoreTeleportCall."); }
            }
            if (e.MountedOn != null)
                r.Note($"You're riding or seated ({e.MountedOn.Entity?.GetName() ?? "on a seat"}); a translocator moves the mount with you on it.");

            // A translocator keeps a warm-up entry per entity id, and never swaps in a new copy of the player
            // (a rejoin makes one). An entry for an old copy stuck mid-teleport would skip the real player forever.
            foreach (var be in TeleportersNear(e.Pos.AsBlockPos, 6))
            {
                var d = R.Tping(be);
                if (d == null || !d.Contains(e.EntityId)) continue;
                var old = R.Get<Entity>(R.TeEntity, d[e.EntityId]);
                if (old == null || ReferenceEquals(old, e)) continue;
                r.Fix($"the translocator at {Map(be.Pos)} was still warming up an old copy of you from before you last joined.");
                if (r.Apply) { d.Remove(e.EntityId); Log($"{p.PlayerName}: removed a stale warm-up entry at {be.Pos}."); }
            }
        }

        // ---- the translocator they're at --------------------------------------------------------

        /// <summary>The teleporter the player stands in, or the nearest within 2 blocks.</summary>
        public BlockEntity? FindPad(Entity e, out bool standingIn)
        {
            var ba = sapi.World.BlockAccessor;
            var feet = new BlockPos((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Y + 0.01), (int)Math.Floor(e.Pos.Z), e.Pos.Dimension);
            foreach (var at in new[] { feet, feet.DownCopy() })
            {
                var be = ba.GetBlockEntity(at);
                if (R.IsTeleporter(be)) { standingIn = true; return be; }
            }
            standingIn = false;
            BlockEntity? best = null;
            double bestD = double.MaxValue;
            foreach (var be in TeleportersNear(feet, 2))
            {
                double d = e.Pos.XYZ.DistanceTo(new Vec3d(be.Pos.X + 0.5, be.Pos.Y, be.Pos.Z + 0.5));
                if (d < bestD) { best = be; bestD = d; }
            }
            if (best != null && bestD <= 1.6) standingIn = true;
            return best;
        }

        /// <summary>Checks the teleporter and fixes what's safe; true when it has somewhere real to send people.</summary>
        public bool CheckPad(BlockEntity be, IServerPlayer? forPlayer, Report r)
        {
            if (R.IsReloc(be)) return CheckKeyed(be, r);
            if (R.IsStatic(be)) return CheckStatic(be, forPlayer, r);
            var t = R.TargetOf(be, forPlayer?.Entity!, out string? why);
            if (t == null) { r.Problem($"the teleporter at {Map(be.Pos)} ({be.Block?.GetPlacedBlockName(sapi.World, be.Pos)}) has no destination: {why}."); return false; }
            r.Note($"The teleporter at {Map(be.Pos)} leads to {Map(t.AsBlockPos)}.");
            return true;
        }

        bool CheckStatic(BlockEntity be, IServerPlayer? forPlayer, Report r)
        {
            string where = Map(be.Pos);
            if (be.Block?.Variant?["state"] == "broken")
            {
                r.Problem($"the stone translocator at {where} is still broken. It needs 2 metal parts, then temporal gears.");
                return false;
            }
            int done = R.Get<int>(R.StRepairState, be), need = R.Get<int>(R.StRequired, be);
            if (done < need)
            {
                r.Problem($"the stone translocator at {where} isn't fully repaired: {need - done} more temporal gear(s) to go.");
                return false;
            }
            bool can = R.Get<bool>(R.StCanTeleport, be), searching = R.Get<bool>(R.StFindNextChunk, be);
            var tpl = R.Get<BlockPos>(R.StTpLocation, be);

            if (!can)
            {
                if (searching)
                {
                    r.Problem($"the stone translocator at {where} is still looking for its partner, somewhere 400 to 8000 blocks away (it says \"Warping spacetime\"). That usually takes under a minute, and it can't take anyone until then. Retries in the log (\"Regen chunk but broken translocator is gone... Searching again\") are normal.");
                    mod.ForgetStalled(be.Pos);
                    return false;
                }
                // Repaired, not linked, not searching: either a search is out (its answer comes back as a callback) or
                // that answer was lost. The game only re-arms the search when the chunk loads again.
                long since = mod.StalledSince(be.Pos, forPlayer);
                if (sapi.World.ElapsedMilliseconds - since >= StalledAfterMs)
                {
                    r.Fix($"the stone translocator at {where} had sent off a search for its partner that never came back; it's searching again.");
                    if (r.Apply) Rearm(be, "its partner search never came back");
                }
                else
                {
                    r.Problem($"the stone translocator at {where} is waiting to hear back from a search for its partner. If it's still waiting two minutes after I first saw it, I'll restart the search{(r.Apply ? " and tell you" : "")}.");
                    if (r.Apply) mod.WatchStalled(be.Pos, forPlayer);
                }
                return false;
            }
            mod.ForgetStalled(be.Pos);

            if (tpl == null)
            {
                // Saved with canTeleport but no target (or a target at x=0,z=0): GetTarget throws every tick anyone
                // stands in it ("Exception when ticking Static Translocator"), forever. The game's own repair for the
                // unlinked case is to search again, so that's what this does.
                r.Fix($"the stone translocator at {where} thought it was linked but had no destination, so it failed every time someone stood in it. It's looking for a partner again.");
                if (r.Apply) Rearm(be, "it was linked with no destination");
                return false;
            }
            if (searching)
                r.Note($"(The stone translocator at {where} is linked and still searching; when the search lands it will change its destination. That's the game's own behaviour.)");

            var ba = sapi.World.BlockAccessor;
            if (ba.GetChunkAtBlockPos(tpl) == null)
            {
                r.Note($"The stone translocator at {where} leads to {Map(tpl)}; that area isn't loaded, so I couldn't look at its partner.");
                return true;
            }
            var partner = ba.GetBlockEntity(tpl);
            if (R.IsStatic(partner))
            {
                var back = R.Get<BlockPos>(R.StTpLocation, partner);
                if (back != null && back.X == be.Pos.X && back.Y == be.Pos.Y && back.Z == be.Pos.Z)
                    r.Note($"The stone translocator at {where} is linked both ways with its partner at {Map(tpl)}.");
                else
                    r.Note($"The stone translocator at {where} leads to {Map(tpl)}, but that one leads {(back == null ? "nowhere yet" : "to " + Map(back))}, so it's one-way.");
                return true;
            }
            string there = ba.GetBlock(tpl)?.GetPlacedBlockName(sapi.World, tpl) ?? "nothing";
            r.Problem($"the stone translocator at {where} still points at its partner's old spot, {Map(tpl)}, but the partner is gone ({there} there now; picked up or broken). It would drop you there with no way back, so I'm not sending you. An admin can give it a new partner with /fixtl reroll, or pick it up and pair it by key like the others.");
            Log($"stone translocator {be.Pos}: partner {tpl} is gone ({ba.GetBlock(tpl)?.Code}).");
            return false;
        }

        void Rearm(BlockEntity be, string why)
        {
            R.Set(R.StCanTeleport, be, false);
            R.Set(R.StTpLocation, be, null);
            R.Set(R.StFindNextChunk, be, true);
            be.MarkDirty(true);
            mod.ForgetStalled(be.Pos);
            Log($"stone translocator {be.Pos}: searching for a partner again ({why}).");
        }

        /// <summary>/fixtl reroll: forget the partner and search for a new one, like a freshly repaired translocator.</summary>
        public bool Reroll(BlockEntity be, out string msg)
        {
            int done = R.Get<int>(R.StRepairState, be), need = R.Get<int>(R.StRequired, be);
            if (!R.IsStatic(be) || done < need || be.Block?.Variant?["state"] == "broken")
            {
                msg = "That's not a repaired stone translocator.";
                return false;
            }
            var old = R.Get<BlockPos>(R.StTpLocation, be);
            Rearm(be, "/fixtl reroll" + (old != null ? $", old partner {old}" : ""));
            msg = $"The stone translocator at {Map(be.Pos)} is looking for a new partner{(old != null ? $" (it used to lead to {Map(old)})" : "")}. Give it a minute.";
            return true;
        }

        bool CheckKeyed(BlockEntity be, Report r)
        {
            string where = Map(be.Pos);
            var reg = R.Registry();
            if (reg == null) { r.Problem("Translocator Relocator's list couldn't be read on this build, so I can't check keyed translocators."); return false; }
            string key = R.LinkKey(be);

            // The relocator's list (saved with the world) must say what the block says. When it doesn't, the block
            // shows "Key: X" and never links, and typing the same key again does nothing: the mod only updates its
            // list when the key changes. Ways in: a translocator put back from saved block data (CarryOn, schematics,
            // WorldEdit), or a crash between a chunk save and the world save.
            bool listed = reg.TryGetValue(be.Pos, out string? listedKey);
            if (!listed || listedKey != key)
            {
                r.Fix($"the translocator at {where} shows key \"{key}\", but Translocator Relocator's list had it {(listed ? $"under \"{listedKey}\"" : "missing")}, so it never linked. It's listed under \"{key}\" now.");
                if (r.Apply)
                {
                    R.AddOrSetKey(be.Pos, key);
                    Log($"keyed translocator {be.Pos}: list said {(listed ? $"\"{listedKey}\"" : "nothing")}, block says \"{key}\"; re-listed.");
                }
            }
            if (key.Length == 0)
            {
                r.Problem($"the translocator at {where} has no key. Shift + right-click it, choose Edit Key, and give it the same key as the translocator you want to reach.");
                return false;
            }

            // Its partners: everything else listed under the same key. Check the ones we can see.
            var ba = sapi.World.BlockAccessor;
            int live = 0, unseen = 0;
            foreach (var pos in reg.Where(kv => kv.Value == key && !SamePos(kv.Key, be.Pos)).Select(kv => kv.Key).ToList())
            {
                if (ba.GetChunkAtBlockPos(pos) == null) { unseen++; continue; }
                var other = ba.GetBlockEntity(pos);
                if (R.IsReloc(other))
                {
                    string otherKey = R.LinkKey(other!);
                    if (otherKey == key) { live++; continue; }
                    r.Fix($"Translocator Relocator listed the translocator at {Map(pos)} under \"{key}\", but it shows \"{otherKey}\". It's listed under its own key now.");
                    if (r.Apply) { R.AddOrSetKey(pos, otherKey); Log($"keyed translocator {pos}: listed under \"{key}\", block says \"{otherKey}\"; re-listed."); }
                    continue;
                }
                string there = ba.GetBlock(pos)?.GetPlacedBlockName(sapi.World, pos) ?? "nothing";
                r.Fix($"Translocator Relocator still listed a translocator with key \"{key}\" at {Map(pos)}, but there's {there} there now. Removed it, so nobody gets sent to an empty spot.");
                if (r.Apply) { R.RemoveKey(pos); Log($"keyed translocator list: removed leftover {pos} (\"{key}\"; block there is {ba.GetBlock(pos)?.Code})."); }
            }
            // The other way round: a translocator in a loaded area that shows this key but isn't in the list under it
            // (moved with CarryOn, say). From this end it looks like there's no partner at all.
            foreach (var other in LoadedKeyed())
            {
                if (SamePos(other.Pos, be.Pos) || R.LinkKey(other) != key) continue;
                reg.TryGetValue(other.Pos, out string? k);
                if (k == key) continue;
                r.Fix($"the translocator at {Map(other.Pos)} shows key \"{key}\" too, but Translocator Relocator's list had it {(k == null ? "missing" : $"under \"{k}\"")}, so they never paired. It's listed under \"{key}\" now.");
                if (r.Apply) { R.AddOrSetKey(other.Pos, key); Log($"keyed translocator {other.Pos}: list said {(k == null ? "nothing" : $"\"{k}\"")}, block says \"{key}\"; re-listed."); }
                live++;
            }
            if (r.Apply) R.CheckLinks(be);

            if (live + unseen == 0)
            {
                var alike = reg.Where(kv => kv.Value != key && Norm(kv.Value) == Norm(key) && kv.Value.Length > 0)
                    .Select(kv => $"\"{kv.Value}\" at {Map(kv.Key)}").ToList();
                r.Problem($"no other translocator has the key \"{key}\", so the one at {where} has nowhere to send you. Give the one you want to reach exactly the same key (shift + right-click, Edit Key; capitals and spaces count)."
                          + (alike.Count > 0 ? $" Close but not the same: {string.Join(", ", alike)}." : ""));
                return false;
            }
            int n = live + unseen;
            r.Note(n == 1
                ? $"The translocator at {where} (key \"{key}\") is paired with one other."
                : $"The translocator at {where} (key \"{key}\") shares its key with {n} others, so it picks one of them at random every time.");
            if (unseen > 0) r.Note($"({unseen} of them {(unseen == 1 ? "is" : "are")} in an area that isn't loaded, so I couldn't look.)");
            return true;
        }

        /// <summary>Sends the player where the teleporter would. Returns the destination, or null.</summary>
        public Vec3d? SendThrough(IServerPlayer p, BlockEntity be, Report r)
        {
            var e = p.Entity;
            if (R.IsReloc(be)) R.CheckLinks(be);
            var t = R.TargetOf(be, e, out string? why);
            if (t == null) { r.Problem($"couldn't work out where it leads: {why}."); return null; }
            e.Teleporting = false;
            R.Set(R.IgnoreTeleportCall, e, false);
            R.Tping(be)?.Remove(e.EntityId);
            var from = e.Pos.XYZ;
            e.TeleportToDouble(t.X, t.Y, t.Z, () => Log($"{p.PlayerName} arrived at {t.AsBlockPos}."));
            Log($"{p.PlayerName}: sent through the translocator at {be.Pos} from {from.AsBlockPos} to {t.AsBlockPos}.");
            sapi.Logger.Audit("[lionfox_fixtl] Teleporting player {0} through {1} to {2} (/fixtl)", p.PlayerName, be.Pos, t);
            return t;
        }

        // ---- a whole-registry look for admins ----------------------------------------------------

        public List<string> KeyAudit()
        {
            var lines = new List<string>();
            var reg = R.Registry();
            if (reg == null) { lines.Add(R.RelocatorLoaded ? "Couldn't read Translocator Relocator's list." : "Translocator Relocator isn't installed."); return lines; }
            var ba = sapi.World.BlockAccessor;
            var groups = reg.GroupBy(kv => kv.Value).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            int paired = groups.Count(g => g.Key.Length > 0 && g.Count() == 2);
            lines.Add($"{reg.Count} keyed translocators listed, {groups.Count(g => g.Key.Length > 0)} keys, {paired} of them paired.");
            foreach (var g in groups)
            {
                string at = string.Join(", ", g.Select(kv => Map(kv.Key) + LoadedState(ba, kv.Key, kv.Value)));
                if (g.Key.Length == 0) lines.Add($"No key yet: {at}");
                else if (g.Count() == 1) lines.Add($"Alone (no partner): \"{g.Key}\" at {at}");
                else if (g.Count() > 2) lines.Add($"Shared by {g.Count()} (random pick): \"{g.Key}\" at {at}");
            }
            foreach (var g in reg.Values.Where(v => v.Length > 0).Distinct().GroupBy(Norm).Where(g => g.Count() > 1))
                lines.Add("Look-alike keys (they don't pair): " + string.Join(" / ", g.Select(k => $"\"{k}\"")));
            foreach (var be in LoadedKeyed())
            {
                string k = R.LinkKey(be);
                if (!reg.TryGetValue(be.Pos, out string? listed))
                    lines.Add($"Not in the list at all (never pairs; /fixtl there fixes it): \"{k}\" at {Map(be.Pos)}");
            }
            return lines;
        }

        /// <summary>Keyed translocators standing in loaded areas (AllLoadedChunks is a copy taken under the server's lock).</summary>
        List<BlockEntity> LoadedKeyed()
        {
            var found = new List<BlockEntity>();
            if (!R.RelocatorLoaded) return found;
            foreach (var chunk in sapi.WorldManager.AllLoadedChunks.Values)
            {
                try
                {
                    var bes = chunk.BlockEntities;
                    if (bes == null) continue;
                    foreach (var be in bes.Values) if (R.IsReloc(be)) found.Add(be);
                }
                catch (InvalidOperationException) { }
            }
            return found;
        }

        string LoadedState(IBlockAccessor ba, BlockPos pos, string key)
        {
            if (ba.GetChunkAtBlockPos(pos) == null) return "";
            var be = ba.GetBlockEntity(pos);
            if (!R.IsReloc(be)) return " [nothing there now]";
            string k = R.LinkKey(be!);
            return k == key ? "" : $" [block says \"{k}\"]";
        }

        // ---- helpers -------------------------------------------------------------------------------

        public IEnumerable<BlockEntity> TeleportersNear(BlockPos at, int radius)
        {
            var found = new List<BlockEntity>();
            var ba = sapi.World.BlockAccessor;
            var min = new BlockPos(at.X - radius, Math.Max(0, at.Y - radius), at.Z - radius, at.dimension);
            var max = new BlockPos(at.X + radius, at.Y + radius, at.Z + radius, at.dimension);
            ba.WalkBlocks(min, max, (block, x, y, z) =>
            {
                if (block.EntityClass == null) return;
                var be = ba.GetBlockEntity(new BlockPos(x, y, z, at.dimension));
                if (R.IsTeleporter(be)) found.Add(be!);
            });
            return found;
        }

        static bool SamePos(BlockPos a, BlockPos b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

        static string Norm(string k) => k.Trim().ToLowerInvariant();

        /// <summary>Coordinates as players see them on the map: X and Z from the world's spawn.</summary>
        public string Map(BlockPos pos)
        {
            var s = sapi.World.DefaultSpawnPosition.AsBlockPos;
            return $"{pos.X - s.X}, {pos.Y}, {pos.Z - s.Z}";
        }

        void Log(string msg) => sapi.Logger.Notification("[lionfox_fixtl] " + msg);
    }
}
