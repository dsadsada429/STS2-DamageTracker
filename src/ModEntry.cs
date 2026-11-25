using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace DamageTracker;

[ModInitializer("Initialize")]
public static class ModEntry
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (_harmony != null)
        {
            return;
        }

        _harmony = new Harmony("com.example.sts2.damage_tracker");
        PatchHook(nameof(Hook.BeforeCombatStart), prefix: nameof(HookPatches.BeforeCombatStartPrefix));
        PatchHook(nameof(Hook.AfterCombatEnd), postfix: nameof(HookPatches.AfterCombatEndPostfix));
        PatchHook(nameof(Hook.BeforeSideTurnStart), prefix: nameof(HookPatches.BeforeSideTurnStartPrefix));
        PatchHook(nameof(Hook.AfterPlayerTurnStart), postfix: nameof(HookPatches.AfterPlayerTurnStartPostfix));
        PatchHook(nameof(Hook.AfterDamageGiven), prefix: nameof(HookPatches.AfterDamageGivenPrefix));
        PatchHook(nameof(Hook.AfterDiedToDoom), postfix: nameof(HookPatches.AfterDiedToDoomPostfix));
        PatchRequiredMethod(typeof(PoisonPower), nameof(PoisonPower.Trigger), typeof(StatusPowerPatches),
            prefix: nameof(StatusPowerPatches.PoisonTriggerPrefix),
            postfix: nameof(StatusPowerPatches.CompleteDamageContextPostfix),
            finalizer: nameof(StatusPowerPatches.RestoreDamageContextOnException));
        PatchRequiredMethod(typeof(DoomPower), nameof(DoomPower.DoomKill), typeof(StatusPowerPatches),
            prefix: nameof(StatusPowerPatches.DoomKillPrefix),
            postfix: nameof(StatusPowerPatches.DoomKillPostfix),
            finalizer: nameof(StatusPowerPatches.DoomKillFinalizer));
        PatchOrbTurnEndMethods();

        // 手动加载PCK文件到res://
        string modDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        string pckPath = System.IO.Path.Combine(modDir, "DamageTracker.pck");
        GD.Print($"[DamageTracker] PCK path: {pckPath}, exists: {System.IO.File.Exists(pckPath)}");
        if (System.IO.File.Exists(pckPath))
        {
            bool success = ProjectSettings.LoadResourcePack(pckPath);
            GD.Print($"[DamageTracker] LoadResourcePack result: {success}");
        }
        else
        {
            GD.PrintErr($"[DamageTracker] PCK file not found: {pckPath}");
        }

        // Write to log file for debugging
        string logDir = OS.GetUserDataDir();
        string logPath = Path.Combine(logDir, "..", "DamageTracker_debug.log");
        using (var log = Godot.FileAccess.Open(logPath, Godot.FileAccess.ModeFlags.WriteRead))
        {
            if (log != null)
            {
                log.StoreLine($"[{DateTime.Now:HH:mm:ss}] DamageTracker Init - hooks: {_harmony?.GetPatchedMethods().Count() ?? 0}");
            }
        }
        GD.Print("[DamageTracker] Log path: " + logPath);
        Log.Info("DamageTracker initialized");
    }

    private static void PatchHook(string name, string? prefix = null, string? postfix = null)
    {
        PatchRequiredMethod(typeof(Hook), name, typeof(HookPatches), prefix, postfix);
    }

    private static void PatchRequiredMethod(Type ownerType, string methodName, Type patchType,
        string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        MethodInfo original = AccessTools.Method(ownerType, methodName)
            ?? throw new MissingMethodException(ownerType.FullName, methodName);
        _harmony!.Patch(original, prefix: ResolvePatch(patchType, prefix), postfix: ResolvePatch(patchType, postfix),
            finalizer: ResolvePatch(patchType, finalizer));
    }

    private static HarmonyMethod? ResolvePatch(Type patchType, string? name)
    {
        if (name == null)
            return null;

        return new HarmonyMethod(AccessTools.Method(patchType, name)
            ?? throw new MissingMethodException(patchType.FullName, name));
    }

    private static void PatchOrbTurnEndMethods()
    {
        try
        {
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(OrbModel).Assembly))
            {
                if (typeof(OrbModel).IsAssignableFrom(type))
                    TryPatchMethod(type, "BeforeTurnEndOrbTrigger");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DamageTracker] Could not discover optional orb hooks: {ex.Message}");
        }
    }

    private static void TryPatchMethod(Type ownerType, string name)
    {
        try
        {
            MethodInfo? original = AccessTools.DeclaredMethod(ownerType, name, new[] { typeof(PlayerChoiceContext) });
            if (original == null || original.IsStatic || original.IsAbstract || original.ReturnType != typeof(Task))
                return;

            Type patches = typeof(StatusPowerPatches);
            _harmony!.Patch(original,
                prefix: ResolvePatch(patches, nameof(StatusPowerPatches.OrbBeforeTurnEndPrefix)),
                postfix: ResolvePatch(patches, nameof(StatusPowerPatches.CompleteDamageContextPostfix)),
                finalizer: ResolvePatch(patches, nameof(StatusPowerPatches.RestoreDamageContextOnException)));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DamageTracker] Could not patch optional orb hook {ownerType.Name}.{name}: {ex.Message}");
        }
    }
}

internal static class HookPatches
{
    private static bool _overlayScheduled;

    public static void BeforeCombatStartPrefix(IRunState? runState, ICombatState? combatState)
    {
        StatusPowerPatches.ClearStaleDoomDamage(combatState);
        RunDamageTrackerService.BeginRun(runState);
        RunDamageTrackerService.BeginCombat(runState, combatState,
            ReflectionHelpers.ResolveEncounterName(runState, combatState, null));

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DamageTrackerOverlay.EnsureCreated();
        }
    }

    public static void AfterCombatEndPostfix(IRunState? runState, ICombatState? combatState, CombatRoom? room,
        ref Task __result)
    {
        __result = EndCombatAsync(__result, runState, combatState, room);
    }

    private static async Task EndCombatAsync(Task original, IRunState? runState, ICombatState? combatState, CombatRoom? room)
    {
        try
        {
            await original;
        }
        finally
        {
            StatusPowerPatches.ClearPendingDoomDamageForCombat(combatState);
            RunDamageTrackerService.EndCombat(combatState, ReflectionHelpers.ResolveEncounterName(runState, combatState, room));
        }
    }

    public static void BeforeSideTurnStartPrefix(ICombatState? combatState, CombatSide side,
        IReadOnlyList<Creature>? participants)
    {
        if (side == CombatSide.Player)
            RunDamageTrackerService.BeginPlayerTurn(combatState);
    }

    public static void AfterPlayerTurnStartPostfix(ICombatState? combatState, PlayerChoiceContext? choiceContext,
        Player? player)
    {
        RunDamageTrackerService.EnsurePlayersRegisteredForCombat(combatState, combatState?.RunState, combatState);
        if (player != null)
            RunDamageTrackerService.NotePlayerForCombat(combatState, player);
    }

    public static void AfterDamageGivenPrefix(
        PlayerChoiceContext? choiceContext,
        ICombatState? combatState,
        Creature? dealer,
        DamageResult? results,
        ValueProp props,
        Creature? target,
        CardModel? cardSource)
    {
        RunDamageTrackerService.EnsurePlayersRegisteredForCombat(combatState, combatState?.RunState, combatState);

        if (ReflectionHelpers.TryResolveDamageEvent(choiceContext, dealer, results, props, target, cardSource, out DamageEvent hit))
            RunDamageTrackerService.RecordDamage(combatState, hit);
    }

    public static void AfterDiedToDoomPostfix(ICombatState? combatState, IReadOnlyList<Creature>? creatures)
    {
        if (creatures == null)
            return;

        foreach (Creature creature in creatures)
        {
            if (StatusPowerPatches.TryConsumeDoomDamage(combatState, creature, out DamageEvent hit))
                RunDamageTrackerService.RecordDamage(combatState, hit);
        }
    }
}

internal static class StatusPowerPatches
{
    private static readonly AsyncLocal<DamageContext?> CurrentContext = new();
    private static readonly object DoomSyncRoot = new();
    private static readonly Dictionary<Creature, PendingDoomDamage> PendingDoomByCreature = new(ReferenceEqualityComparer.Instance);

    internal sealed class DamageContext(DamageContext? previous, Creature? target, Creature? applier, OrbModel? orb)
    {
        public DamageContext? Previous { get; } = previous;
        public Creature? Target { get; } = target;
        public Creature? Applier { get; } = applier;
        public OrbModel? Orb { get; } = orb;
        public volatile bool Active = true;
    }

    private readonly record struct PendingDoomDamage(ICombatState CombatState, PlayerHandle Player, int PreDeathHp);

    public static void PoisonTriggerPrefix(PoisonPower __instance, out DamageContext? __state)
    {
        Creature target = __instance.Owner;
        __state = null;
        if (target.IsEnemy)
        {
            __state = new DamageContext(CurrentContext.Value, target, __instance.Applier, null);
            CurrentContext.Value = __state;
        }
    }

    public static void OrbBeforeTurnEndPrefix(OrbModel __instance, out DamageContext __state)
    {
        __state = new DamageContext(CurrentContext.Value, null, null, __instance);
        CurrentContext.Value = __state;
    }

    public static void CompleteDamageContextPostfix(ref Task __result, DamageContext? __state)
    {
        if (__state == null)
            return;

        // The original async method captured the context. Restore the caller's context now.
        CurrentContext.Value = __state.Previous;
        __result = CompleteDamageContextAsync(__result, __state);
    }

    private static async Task CompleteDamageContextAsync(Task original, DamageContext context)
    {
        try
        {
            await original;
        }
        finally
        {
            context.Active = false;
        }
    }

    public static void RestoreDamageContextOnException(Exception? __exception, DamageContext? __state)
    {
        if (__exception != null && __state != null && ReferenceEquals(CurrentContext.Value, __state))
        {
            CurrentContext.Value = __state.Previous;
            __state.Active = false;
        }
    }

    public static bool TryGetPoisonApplier(Creature receiver, out Creature? applier)
    {
        for (DamageContext? context = CurrentContext.Value; context != null; context = context.Previous)
        {
            if (context.Active && ReferenceEquals(context.Target, receiver))
            {
                applier = context.Applier;
                return true;
            }
        }

        applier = null;
        return false;
    }

    public static OrbModel? GetActiveOrb()
    {
        for (DamageContext? context = CurrentContext.Value; context != null; context = context.Previous)
        {
            if (context.Active && context.Orb is OrbModel orb)
                return orb;
        }

        return null;
    }

    public static void DoomKillPrefix(IReadOnlyList<Creature>? creatures, out List<Creature>? __state)
    {
        __state = null;
        if (creatures == null || creatures.Count == 0)
            return;

        List<Creature> captured = new();
        lock (DoomSyncRoot)
        {
            foreach (Creature creature in creatures)
            {
                if (!creature.IsEnemy || creature.CurrentHp <= 0 || creature.CombatState == null)
                    continue;

                Creature? applier = creature.GetPower<DoomPower>()?.Applier;
                if (ReflectionHelpers.IsPlayerCreature(applier) && ReflectionHelpers.TryResolvePlayerHandle(applier, out PlayerHandle player))
                {
                    PendingDoomByCreature[creature] = new PendingDoomDamage(creature.CombatState, player, creature.CurrentHp);
                    captured.Add(creature);
                }
            }
        }

        if (captured.Count > 0)
            __state = captured;
    }

    public static void DoomKillPostfix(ref Task __result, List<Creature>? __state)
    {
        if (__state == null || __state.Count == 0)
            return;

        __result = CleanupDoomAfterTaskAsync(__result, __state);
    }

    public static void DoomKillFinalizer(Exception? __exception, List<Creature>? __state)
    {
        if (__exception != null && __state != null)
            RemovePendingDoomEntries(__state);
    }

    private static async Task CleanupDoomAfterTaskAsync(Task original, List<Creature> captured)
    {
        try
        {
            await original;
        }
        finally
        {
            RemovePendingDoomEntries(captured);
        }
    }

    private static void RemovePendingDoomEntries(List<Creature> captured)
    {
        lock (DoomSyncRoot)
        {
            foreach (Creature creature in captured)
                PendingDoomByCreature.Remove(creature);
        }
    }

    public static bool TryConsumeDoomDamage(ICombatState? combatState, Creature creature, out DamageEvent hit)
    {
        lock (DoomSyncRoot)
        {
            if (PendingDoomByCreature.TryGetValue(creature, out PendingDoomDamage pending)
                && (combatState == null || ReferenceEquals(pending.CombatState, combatState)))
            {
                PendingDoomByCreature.Remove(creature);
                hit = new DamageEvent(pending.Player, DamageSource.Doom, null, pending.PreDeathHp, IsKill: true, OverkillDamage: 0m);
                return true;
            }
        }

        hit = default;
        return false;
    }

    public static void ClearPendingDoomDamageForCombat(ICombatState? combatState)
    {
        if (combatState == null)
            return;

        lock (DoomSyncRoot)
        {
            List<Creature>? toRemove = null;
            foreach (KeyValuePair<Creature, PendingDoomDamage> kv in PendingDoomByCreature)
            {
                if (ReferenceEquals(kv.Value.CombatState, combatState))
                {
                    toRemove ??= new List<Creature>();
                    toRemove.Add(kv.Key);
                }
            }

            if (toRemove != null)
            {
                foreach (Creature c in toRemove)
                    PendingDoomByCreature.Remove(c);
            }
        }
    }

    public static void ClearStaleDoomDamage(ICombatState? activeCombatState)
    {
        lock (DoomSyncRoot)
        {
            if (activeCombatState == null)
            {
                PendingDoomByCreature.Clear();
                return;
            }

            List<Creature>? toRemove = null;
            foreach (KeyValuePair<Creature, PendingDoomDamage> kv in PendingDoomByCreature)
            {
                if (!ReferenceEquals(kv.Value.CombatState, activeCombatState))
                {
                    toRemove ??= new List<Creature>();
                    toRemove.Add(kv.Key);
                }
            }

            if (toRemove != null)
            {
                foreach (Creature c in toRemove)
                    PendingDoomByCreature.Remove(c);
            }
        }
    }
}
