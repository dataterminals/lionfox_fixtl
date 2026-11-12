using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace lionfox_fixtl
{
    /// <summary>
    /// Every member this mod reaches by reflection, resolved once at RunGame.
    ///
    /// Two sources, both by NAME on purpose. The game's translocators live in VSSurvivalMod, which this mod does
    /// not reference (it keeps loading when GameContent moves; the self-check says loudly when a member is gone).
    /// Translocator Relocator (translocatorrelocatorfork, on LionFox) is another mod entirely and may not be
    /// installed at all, so its members are optional: without it the keyed-translocator checks just switch off.
    ///
    /// Decompiled from 1.22.6 (VSSurvivalMod) and TranslocatorRelocatorFork 1.0.5, 2026-11-12.
    /// </summary>
    static class R
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // BlockEntityTeleporterBase: every teleporter, the game's and Translocator Relocator's.
        public static Type? TeleBase;
        public static FieldInfo? TpingEntities;        // Dictionary<long, TeleportingEntity>, who's warming up
        public static FieldInfo? TpLocationIsOffset;
        public static MethodInfo? GetTarget;            // Vec3d GetTarget(Entity)

        // TeleportingEntity: one warming-up entry.
        public static FieldInfo? TeEntity, TeSecondsPassed, TeLastCollideMs;

        // BlockEntityStaticTranslocator: the stone ones found in ruins.
        public static Type? StaticTl;
        public static FieldInfo? StTpLocation, StCanTeleport, StFindNextChunk, StRepairState, StRequired;

        // EntityAgent.ignoreTeleportCall (protected), in the API assembly.
        public static FieldInfo? IgnoreTeleportCall;

        // Translocator Relocator: placed translocators that pair by a typed key.
        public static Type? RelocTl;                    // BlockEntityRelocatedTranslocator
        public static FieldInfo? RlLinkKey;             // string linkKey, what the block info shows as "Key: X"
        public static MethodInfo? RlCheckLinks;         // re-reads its partners from the registry (it does this every 1 s)
        public static FieldInfo? CoreLinkManager;       // static Core.linkManager
        public static FieldInfo? MgrRtData;             // Dictionary<BlockPos, string>: the registry, saved with the world
        public static FieldInfo? MgrLinks;              // Dictionary<string, List<BlockPos>>: the registry by key
        public static MethodInfo? MgrAddOrSetKey, MgrRemoveKey;

        public static bool RelocatorLoaded => RelocTl != null;

        /// <summary>Resolves everything; returns the members that should exist and don't.</summary>
        public static List<string> Resolve()
        {
            var missing = new List<string>();

            IgnoreTeleportCall = typeof(EntityAgent).GetField("ignoreTeleportCall", Inst);
            if (IgnoreTeleportCall == null) missing.Add("EntityAgent.ignoreTeleportCall");

            TeleBase = FindType("Vintagestory.GameContent.BlockEntityTeleporterBase");
            if (TeleBase == null) missing.Add("type BlockEntityTeleporterBase (no translocator checks at all)");
            else
            {
                TpingEntities = Field(TeleBase, "tpingEntities", missing);
                TpLocationIsOffset = Field(TeleBase, "tpLocationIsOffset", missing);
                GetTarget = TeleBase.GetMethod("GetTarget", new[] { typeof(Entity) });
                if (GetTarget == null) missing.Add("BlockEntityTeleporterBase.GetTarget(Entity)");
            }

            var te = FindType("Vintagestory.GameContent.TeleportingEntity");
            if (te == null) missing.Add("type TeleportingEntity");
            else
            {
                TeEntity = Field(te, "Entity", missing);
                TeSecondsPassed = Field(te, "SecondsPassed", missing);
                TeLastCollideMs = Field(te, "LastCollideMs", missing);
            }

            StaticTl = FindType("Vintagestory.GameContent.BlockEntityStaticTranslocator");
            if (StaticTl == null) missing.Add("type BlockEntityStaticTranslocator (no stone translocator checks)");
            else
            {
                StTpLocation = Field(StaticTl, "tpLocation", missing);
                StCanTeleport = Field(StaticTl, "canTeleport", missing);
                StFindNextChunk = Field(StaticTl, "findNextChunk", missing);
                StRepairState = Field(StaticTl, "repairState", missing);
                StRequired = Field(StaticTl, "RepairInteractionsRequired", missing);
            }

            // Optional: only when Translocator Relocator is installed. Missing members of an installed one ARE reported.
            RelocTl = FindType("TranslocatorRelocator.src.block.BlockEntityRelocatedTranslocator");
            if (RelocTl != null)
            {
                RlLinkKey = Field(RelocTl, "linkKey", missing);
                RlCheckLinks = RelocTl.GetMethod("CheckLinks", Inst, null, Type.EmptyTypes, null);
                if (RlCheckLinks == null) missing.Add("BlockEntityRelocatedTranslocator.CheckLinks()");
                var core = FindType("TranslocatorRelocator.src.Core");
                CoreLinkManager = core?.GetField("linkManager", Stat);
                if (CoreLinkManager == null) missing.Add("TranslocatorRelocator Core.linkManager");
                var mgr = FindType("TranslocatorRelocator.src.manager.RelocatedTranslocatorLinkManager");
                if (mgr == null) missing.Add("type RelocatedTranslocatorLinkManager");
                else
                {
                    MgrRtData = Field(mgr, "rtData", missing);
                    MgrLinks = Field(mgr, "links", missing);
                    MgrAddOrSetKey = mgr.GetMethod("AddOrSetKey", Inst, null, new[] { typeof(BlockPos), typeof(string) }, null);
                    if (MgrAddOrSetKey == null) missing.Add("RelocatedTranslocatorLinkManager.AddOrSetKey(BlockPos, string)");
                    MgrRemoveKey = mgr.GetMethod("RemoveKey", Inst, null, new[] { typeof(BlockPos) }, null);
                    if (MgrRemoveKey == null) missing.Add("RelocatedTranslocatorLinkManager.RemoveKey(BlockPos)");
                }
            }
            return missing;
        }

        static FieldInfo? Field(Type t, string name, List<string> missing)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var f = x.GetField(name, Inst | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            missing.Add($"{t.Name}.{name}");
            return null;
        }

        static Type? FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        // ---- small typed accessors; each tolerates a member that didn't resolve -------------------

        public static bool IsTeleporter(BlockEntity? be) => be != null && TeleBase != null && TeleBase.IsInstanceOfType(be);
        public static bool IsStatic(BlockEntity? be) => be != null && StaticTl != null && StaticTl.IsInstanceOfType(be);
        public static bool IsReloc(BlockEntity? be) => be != null && RelocTl != null && RelocTl.IsInstanceOfType(be);

        public static T? Get<T>(FieldInfo? f, object? o)
        {
            if (f == null || o == null) return default;
            try { return f.GetValue(o) is T v ? v : default; } catch { return default; }
        }

        public static bool Set(FieldInfo? f, object o, object? value)
        {
            if (f == null) return false;
            try { f.SetValue(o, value); return true; } catch { return false; }
        }

        public static IDictionary? Tping(BlockEntity be) => Get<IDictionary>(TpingEntities, be);

        public static object? LinkManager() => CoreLinkManager?.GetValue(null);

        public static Dictionary<BlockPos, string>? Registry() => Get<Dictionary<BlockPos, string>>(MgrRtData, LinkManager());

        public static string LinkKey(BlockEntity be) => Get<string>(RlLinkKey, be) ?? "";

        public static void AddOrSetKey(BlockPos pos, string key) => MgrAddOrSetKey?.Invoke(LinkManager(), new object[] { pos.Copy(), key });

        public static void RemoveKey(BlockPos pos) => MgrRemoveKey?.Invoke(LinkManager(), new object[] { pos });

        public static void CheckLinks(BlockEntity be)
        {
            try { RlCheckLinks?.Invoke(be, null); } catch { }
        }

        /// <summary>Where the teleporter would send this entity right now, or null (and why).</summary>
        public static Vec3d? TargetOf(BlockEntity be, Entity e, out string? why)
        {
            why = null;
            if (GetTarget == null) { why = "GetTarget is missing on this build"; return null; }
            Vec3d? t;
            try { t = GetTarget.Invoke(be, new object[] { e }) as Vec3d; }
            catch (TargetInvocationException ex) { why = $"it throws {ex.InnerException?.GetType().Name}"; return null; }
            if (t == null) { why = "it has no destination"; return null; }
            t = t.Clone();
            if (Get<bool>(TpLocationIsOffset, be)) t.Add(be.Pos.X, be.Pos.Y, be.Pos.Z);
            return t;
        }
    }
}
