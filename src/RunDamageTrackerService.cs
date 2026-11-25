using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace DamageTracker;

public enum DamageSource
{
    Direct = 0,
    Poison = 1,
    Doom = 2,
    PetOrOrb = 3,
    Other = 4
}

public readonly record struct DamageEvent(
    PlayerHandle Player,
    DamageSource Source,
    string? CardName,
    decimal Damage,
    bool IsKill,
    decimal OverkillDamage);

public sealed class CombatDamageSnapshot
{
    public int CombatIndex { get; init; }

    public string EncounterName { get; init; } = "";

    public IReadOnlyList<PlayerDamageSnapshot> Players { get; init; } = Array.Empty<PlayerDamageSnapshot>();

    public CombatDamageSnapshot Clone()
    {
        return new CombatDamageSnapshot
        {
            CombatIndex = CombatIndex,
            EncounterName = EncounterName,
            Players = Players.Select(player => player.Clone()).ToList()
        };
    }
}

public static class RunDamageTrackerService
{
    private static readonly object SyncRoot = new();
    private static readonly object SaveLock = new();
    private static readonly Dictionary<ulong, PlayerDamageSnapshot> Totals = new();
    private static readonly List<CombatDamageSnapshot> CombatHistory = new();
    private static readonly string SavePath = ProjectSettings.GlobalizePath("user://damage_tracker_state.json");

    private static Dictionary<ulong, PlayerDamageSnapshot>? _committedTotals;
    private static int _committedCombatIndex;
    private static string _committedEncounterName = "";

    private static string? _currentRunToken;
    private static string? _stableRunId;
    private static int _combatIndex;
    private static bool _combatActive;
    private static bool _combatFinalized;
    private static object? _activeCombatState;
    private static string _currentEncounterName = "";
    private static ulong? _activePlayerKey;
    private static long _saveVersion;
    private static long _lastWrittenVersion;

    public static event Action<OverlayState>? Changed;

    public static void BeginRun(object? runState)
    {
        string nextToken = ReflectionHelpers.ResolveRunToken(runState);
        string stableId = ReflectionHelpers.ResolveStableRunId(runState);
        List<PlayerHandle> knownPlayers = CollectKnownPlayers(runState);
        bool shouldPublish;

        lock (SyncRoot)
        {
            // Same token in memory — skip reset
            if (string.Equals(_currentRunToken, nextToken, StringComparison.Ordinal))
            {
                shouldPublish = RegisterKnownPlayers(knownPlayers);
            }
            // Same stable ID (e.g. after save & quit) — keep accumulated data
            else if (!string.IsNullOrEmpty(stableId) &&
                     string.Equals(_stableRunId, stableId, StringComparison.Ordinal))
            {
                _currentRunToken = nextToken;
                shouldPublish = RegisterKnownPlayers(knownPlayers);
            }
            // Try restoring from disk if stable ID matches
            else if (!string.IsNullOrEmpty(stableId) && TryLoadState(stableId))
            {
                _currentRunToken = nextToken;
                _stableRunId = stableId;
                RegisterKnownPlayers(knownPlayers);
                shouldPublish = true;
            }
            // Truly new run — reset everything
            else
            {
                Totals.Clear();
                CombatHistory.Clear();
                _committedTotals = null;
                _committedCombatIndex = 0;
                _committedEncounterName = "";
                _currentRunToken = nextToken;
                _stableRunId = stableId;
                _combatIndex = 0;
                _combatActive = false;
                _combatFinalized = false;
                _activeCombatState = null;
                _currentEncounterName = "";
                _activePlayerKey = null;

                RegisterKnownPlayers(knownPlayers);
                CaptureCommittedCheckpointLocked();
                shouldPublish = true;
            }
        }

        if (shouldPublish)
        {
            Publish();
        }
    }

    public static void BeginCombat(object? runState, object? combatState, string? encounterName = null)
    {
        List<PlayerHandle> knownPlayers = CollectKnownPlayers(runState, combatState);
        bool shouldPublish;
        SavedState? preCombatSave = null;
        long preCombatSaveVersion = 0;

        lock (SyncRoot)
        {
            // Deduplicate repeated calls for the same combatState instance
            if (combatState != null && ReferenceEquals(_activeCombatState, combatState))
            {
                shouldPublish = RegisterKnownPlayers(knownPlayers);
                if (string.IsNullOrWhiteSpace(_currentEncounterName) && !string.IsNullOrWhiteSpace(encounterName))
                {
                    _currentEncounterName = encounterName.Trim();
                    shouldPublish = true;
                }
            }
            else
            {
                // If the previous combat was aborted mid-fight (e.g. Save & Quit in the same process),
                // restore the pre-combat committed checkpoint so the replayed fight is not double-counted.
                if (_combatActive && !_combatFinalized && _committedTotals != null)
                {
                    RestoreCommittedCheckpointLocked();
                }
                else
                {
                    CaptureCommittedCheckpointLocked();
                    preCombatSave = CaptureSavedStateLocked(out preCombatSaveVersion);
                }

                _combatIndex++;
                _activeCombatState = combatState;
                _combatActive = combatState != null;
                _combatFinalized = false;
                _currentEncounterName = string.IsNullOrWhiteSpace(encounterName) ? "" : encounterName.Trim();
                _activePlayerKey = null;

                RegisterKnownPlayers(knownPlayers);

                foreach (PlayerDamageSnapshot snapshot in Totals.Values)
                {
                    snapshot.CombatDamage = 0m;
                    snapshot.TurnDamage = 0m;
                    snapshot.CombatKills = 0;
                    snapshot.CombatOverkillDamage = 0m;
                    snapshot.CombatSourceDamage = new Dictionary<DamageSource, decimal>();
                    snapshot.CombatCardDamage = new Dictionary<string, decimal>(StringComparer.Ordinal);
                    snapshot.IsActive = false;
                }

                shouldPublish = true;
            }
        }

        if (preCombatSave != null)
        {
            WriteSavedState(preCombatSave, preCombatSaveVersion);
        }

        if (shouldPublish)
        {
            Publish();
        }
    }

    public static void EnsurePlayersRegistered(params object?[] sources)
    {
        List<PlayerHandle> knownPlayers = CollectKnownPlayers(sources);
        if (knownPlayers.Count == 0)
        {
            return;
        }

        bool shouldPublish;
        lock (SyncRoot)
        {
            shouldPublish = RegisterKnownPlayers(knownPlayers);
        }

        if (shouldPublish)
        {
            Publish();
        }
    }

    public static void NotePlayer(object? player)
    {
        if (!ReflectionHelpers.TryResolvePlayerHandle(player, out PlayerHandle handle))
        {
            return;
        }

        lock (SyncRoot)
        {
            PlayerDamageSnapshot snapshot = GetOrCreate(handle);
            ApplyHandle(snapshot, handle);
            _activePlayerKey = handle.PlayerKey;

            foreach (PlayerDamageSnapshot playerSnapshot in Totals.Values)
            {
                playerSnapshot.IsActive = playerSnapshot.PlayerKey == handle.PlayerKey;
            }
        }

        Publish();
    }

    public static void EnsurePlayersRegisteredForCombat(object? combatState, params object?[] sources)
    {
        List<PlayerHandle> knownPlayers = CollectKnownPlayers(sources);
        if (knownPlayers.Count == 0)
        {
            return;
        }

        bool shouldPublish;
        lock (SyncRoot)
        {
            if (IsInactiveOrStaleCombatLocked(combatState))
            {
                return;
            }

            shouldPublish = RegisterKnownPlayers(knownPlayers);
        }

        if (shouldPublish)
        {
            Publish();
        }
    }

    public static void NotePlayerForCombat(object? combatState, object? player)
    {
        if (!ReflectionHelpers.TryResolvePlayerHandle(player, out PlayerHandle handle))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (IsInactiveOrStaleCombatLocked(combatState))
            {
                return;
            }

            PlayerDamageSnapshot snapshot = GetOrCreate(handle);
            ApplyHandle(snapshot, handle);
            _activePlayerKey = handle.PlayerKey;

            foreach (PlayerDamageSnapshot playerSnapshot in Totals.Values)
            {
                playerSnapshot.IsActive = playerSnapshot.PlayerKey == handle.PlayerKey;
            }
        }

        Publish();
    }

    public static void BeginPlayerTurn(object? combatState)
    {
        lock (SyncRoot)
        {
            if (IsInactiveOrStaleCombatLocked(combatState))
            {
                return;
            }

            foreach (PlayerDamageSnapshot snapshot in Totals.Values)
            {
                snapshot.TurnDamage = 0m;
            }
        }

        Publish();
    }

    public static void RecordDamage(object? combatState, DamageEvent hit)
    {
        if (hit.Damage <= 0m)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (IsInactiveOrStaleCombatLocked(combatState))
            {
                return;
            }

            PlayerDamageSnapshot snapshot = GetOrCreate(hit.Player);
            ApplyHandle(snapshot, hit.Player);

            snapshot.TotalDamage += hit.Damage;
            snapshot.CombatDamage += hit.Damage;
            snapshot.TurnDamage += hit.Damage;
            snapshot.LastDamage = hit.Damage;
            if (hit.Damage > snapshot.MaxHitDamage)
            {
                snapshot.MaxHitDamage = hit.Damage;
            }

            if (hit.IsKill)
            {
                snapshot.Kills++;
                snapshot.CombatKills++;
            }

            if (hit.OverkillDamage > 0m)
            {
                snapshot.OverkillDamage += hit.OverkillDamage;
                snapshot.CombatOverkillDamage += hit.OverkillDamage;
            }

            AddSourceDamage(snapshot, hit.Source, hit.Damage);

            if (!string.IsNullOrWhiteSpace(hit.CardName))
            {
                string cardName = hit.CardName.Trim();
                AddCardDamage(snapshot, cardName, hit.Damage);
            }

            snapshot.LastUpdatedUtc = DateTime.UtcNow;
        }

        Publish();
    }

    public static void EndCombat(object? combatState, string? encounterName = null)
    {
        SavedState? pendingSave;
        long pendingSaveVersion;

        lock (SyncRoot)
        {
            if (_combatFinalized || IsStaleCombatLocked(combatState))
            {
                return;
            }

            _combatActive = false;
            _combatFinalized = true;
            _activePlayerKey = null;

            if (!string.IsNullOrWhiteSpace(encounterName))
            {
                _currentEncounterName = encounterName.Trim();
            }

            foreach (PlayerDamageSnapshot snapshot in Totals.Values)
            {
                snapshot.IsActive = false;
            }

            List<PlayerDamageSnapshot> combatPlayers = Totals.Values
                .OrderByDescending(snapshot => snapshot.CombatDamage)
                .ThenBy(snapshot => snapshot.PlayerKey)
                .Select(snapshot =>
                {
                    PlayerDamageSnapshot clone = snapshot.Clone();
                    clone.IsActive = false;
                    return clone;
                })
                .ToList();

            CombatHistory.Add(new CombatDamageSnapshot
            {
                CombatIndex = _combatIndex,
                EncounterName = _currentEncounterName,
                Players = combatPlayers
            });

            CaptureCommittedCheckpointLocked();
            pendingSave = CaptureSavedStateLocked(out pendingSaveVersion);
        }

        if (pendingSave != null)
        {
            WriteSavedState(pendingSave, pendingSaveVersion);
        }

        Publish();
    }

    public static OverlayState BuildOverlayState()
    {
        List<PlayerDamageSnapshot> snapshots;
        List<CombatDamageSnapshot> history;
        string runToken;
        int combatIndex;
        bool combatActive;
        ulong? activePlayerKey;
        string currentEncounterName;

        lock (SyncRoot)
        {
            snapshots = Totals.Values
                .OrderByDescending(snapshot => snapshot.TotalDamage)
                .ThenBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(snapshot => snapshot.Clone())
                .ToList();

            history = CombatHistory
                .Select(combat => combat.Clone())
                .ToList();

            runToken = _currentRunToken ?? "unknown-run";
            combatIndex = _combatIndex;
            combatActive = _combatActive;
            activePlayerKey = _activePlayerKey;
            currentEncounterName = _currentEncounterName;
        }

        return new OverlayState
        {
            RunToken = runToken,
            CombatIndex = combatIndex,
            CombatActive = combatActive,
            ActivePlayerKey = activePlayerKey,
            CurrentEncounterName = currentEncounterName,
            Players = snapshots,
            CombatHistory = history
        };
    }

    public static string Format(decimal value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static bool IsStaleCombatLocked(object? combatState)
    {
        return combatState != null &&
               _activeCombatState != null &&
               !ReferenceEquals(combatState, _activeCombatState);
    }

    private static bool IsInactiveOrStaleCombatLocked(object? combatState)
    {
        if (IsStaleCombatLocked(combatState))
        {
            return true;
        }

        return _combatFinalized || (combatState != null && !_combatActive);
    }

    private static void CaptureCommittedCheckpointLocked()
    {
        Dictionary<ulong, PlayerDamageSnapshot> copy = new(Totals.Count);
        foreach (KeyValuePair<ulong, PlayerDamageSnapshot> kv in Totals)
        {
            PlayerDamageSnapshot clone = kv.Value.Clone();
            clone.IsActive = false;
            copy[kv.Key] = clone;
        }

        _committedTotals = copy;
        _committedCombatIndex = _combatIndex;
        _committedEncounterName = _currentEncounterName;
    }

    private static void RestoreCommittedCheckpointLocked()
    {
        if (_committedTotals == null)
        {
            return;
        }

        Totals.Clear();
        foreach (KeyValuePair<ulong, PlayerDamageSnapshot> kv in _committedTotals)
        {
            Totals[kv.Key] = kv.Value.Clone();
        }

        _combatIndex = _committedCombatIndex;
        _currentEncounterName = _committedEncounterName;
    }

    private static void AddSourceDamage(PlayerDamageSnapshot snapshot, DamageSource source, decimal damage)
    {
        Dictionary<DamageSource, decimal> runMap = EnsureMutableSourceMap(
            snapshot.RunSourceDamage,
            map => snapshot.RunSourceDamage = map);
        runMap[source] = runMap.TryGetValue(source, out decimal existingRun)
            ? existingRun + damage
            : damage;

        Dictionary<DamageSource, decimal> combatMap = EnsureMutableSourceMap(
            snapshot.CombatSourceDamage,
            map => snapshot.CombatSourceDamage = map);
        combatMap[source] = combatMap.TryGetValue(source, out decimal existingCombat)
            ? existingCombat + damage
            : damage;
    }

    private static void AddCardDamage(PlayerDamageSnapshot snapshot, string cardName, decimal damage)
    {
        Dictionary<string, decimal> runMap = EnsureMutableCardMap(
            snapshot.RunCardDamage,
            map => snapshot.RunCardDamage = map);
        runMap[cardName] = runMap.TryGetValue(cardName, out decimal existingRun)
            ? existingRun + damage
            : damage;

        Dictionary<string, decimal> combatMap = EnsureMutableCardMap(
            snapshot.CombatCardDamage,
            map => snapshot.CombatCardDamage = map);
        combatMap[cardName] = combatMap.TryGetValue(cardName, out decimal existingCombat)
            ? existingCombat + damage
            : damage;
    }

    private static Dictionary<DamageSource, decimal> EnsureMutableSourceMap(
        IReadOnlyDictionary<DamageSource, decimal>? current,
        Action<IReadOnlyDictionary<DamageSource, decimal>> assign)
    {
        if (current is Dictionary<DamageSource, decimal> dict)
        {
            return dict;
        }

        Dictionary<DamageSource, decimal> copy = current != null
            ? new Dictionary<DamageSource, decimal>(current)
            : new Dictionary<DamageSource, decimal>();
        assign(copy);
        return copy;
    }

    private static Dictionary<string, decimal> EnsureMutableCardMap(
        IReadOnlyDictionary<string, decimal>? current,
        Action<IReadOnlyDictionary<string, decimal>> assign)
    {
        if (current is Dictionary<string, decimal> dict &&
            ReferenceEquals(dict.Comparer, StringComparer.Ordinal))
        {
            return dict;
        }

        Dictionary<string, decimal> copy = current != null
            ? new Dictionary<string, decimal>(current, StringComparer.Ordinal)
            : new Dictionary<string, decimal>(StringComparer.Ordinal);
        assign(copy);
        return copy;
    }

    private static PlayerDamageSnapshot GetOrCreate(PlayerHandle handle)
    {
        if (Totals.TryGetValue(handle.PlayerKey, out PlayerDamageSnapshot? existing))
        {
            return existing;
        }

        PlayerDamageSnapshot created = new()
        {
            PlayerKey = handle.PlayerKey,
            DisplayName = string.IsNullOrWhiteSpace(handle.DisplayName) ? "Unknown Player" : handle.DisplayName,
            CharacterName = string.IsNullOrWhiteSpace(handle.CharacterName) ? "Unknown Character" : handle.CharacterName,
            PortraitTexture = handle.PortraitTexture
        };
        Totals.Add(handle.PlayerKey, created);
        return created;
    }

    private static bool ApplyHandle(PlayerDamageSnapshot snapshot, PlayerHandle handle)
    {
        bool changed = false;

        if (!string.IsNullOrWhiteSpace(handle.DisplayName))
        {
            changed |= !string.Equals(snapshot.DisplayName, handle.DisplayName, StringComparison.Ordinal);
            snapshot.DisplayName = handle.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(handle.CharacterName))
        {
            changed |= !string.Equals(snapshot.CharacterName, handle.CharacterName, StringComparison.Ordinal);
            snapshot.CharacterName = handle.CharacterName;
        }

        if (handle.PortraitTexture != null)
        {
            changed |= !ReferenceEquals(snapshot.PortraitTexture, handle.PortraitTexture);
            snapshot.PortraitTexture = handle.PortraitTexture;
            BackfillHistoryPortraitLocked(handle.PlayerKey, handle.PortraitTexture);
        }

        return changed;
    }

    private static void BackfillHistoryPortraitLocked(ulong playerKey, Texture2D portraitTexture)
    {
        if (CombatHistory.Count == 0)
        {
            return;
        }

        foreach (CombatDamageSnapshot combat in CombatHistory)
        {
            foreach (PlayerDamageSnapshot player in combat.Players)
            {
                if (player.PlayerKey == playerKey && player.PortraitTexture == null)
                {
                    player.PortraitTexture = portraitTexture;
                }
            }
        }
    }

    private static bool RegisterKnownPlayers(IEnumerable<PlayerHandle> handles)
    {
        bool changed = false;

        foreach (PlayerHandle handle in handles)
        {
            bool existed = Totals.ContainsKey(handle.PlayerKey);
            PlayerDamageSnapshot snapshot = GetOrCreate(handle);
            changed |= !existed;
            changed |= ApplyHandle(snapshot, handle);
        }

        return changed;
    }

    private static List<PlayerHandle> CollectKnownPlayers(params object?[] sources)
    {
        Dictionary<ulong, PlayerHandle> handlesByKey = new();

        foreach (object? source in sources)
        {
            if (source == null)
            {
                continue;
            }

            foreach (PlayerHandle handle in ReflectionHelpers.EnumeratePlayerHandles(source))
            {
                handlesByKey[handle.PlayerKey] = handle;
            }
        }

        return handlesByKey.Values.ToList();
    }

    private static void Publish()
    {
        Changed?.Invoke(BuildOverlayState());
    }

    // ── Persistence ────────────────────────────────────────────

    private static SavedState? CaptureSavedStateLocked(out long version)
    {
        if (string.IsNullOrEmpty(_stableRunId))
        {
            version = 0;
            return null;
        }

        version = ++_saveVersion;
        return new SavedState
        {
            StableRunId = _stableRunId,
            CombatIndex = _combatIndex,
            CurrentEncounterName = _currentEncounterName,
            Players = Totals.Values.Select(ToSavedPlayer).ToList(),
            CombatHistory = CombatHistory.Select(combat => new SavedCombatHistory
            {
                CombatIndex = combat.CombatIndex,
                EncounterName = combat.EncounterName,
                Players = combat.Players.Select(ToSavedPlayer).ToList()
            }).ToList()
        };
    }

    private static void WriteSavedState(SavedState state, long version)
    {
        lock (SaveLock)
        {
            if (version <= _lastWrittenVersion)
            {
                return;
            }

            string tmpPath = $"{SavePath}.{System.Environment.ProcessId}.{version}.tmp";
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(SavePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(state, SavedStateCtx.Default.SavedState);
                System.IO.File.WriteAllText(tmpPath, json);
                System.IO.File.Move(tmpPath, SavePath, overwrite: true);
                _lastWrittenVersion = version;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[DamageTracker] Failed to save state to '{SavePath}': {ex.Message}");
                try
                {
                    if (System.IO.File.Exists(tmpPath))
                    {
                        System.IO.File.Delete(tmpPath);
                    }
                }
                catch
                {
                    // Best-effort temp file cleanup.
                }
            }
        }
    }

    private static bool TryLoadState(string stableId)
    {
        try
        {
            string json;
            lock (SaveLock)
            {
                if (!System.IO.File.Exists(SavePath))
                {
                    return false;
                }

                json = System.IO.File.ReadAllText(SavePath);
            }

            SavedState? state = JsonSerializer.Deserialize(json, SavedStateCtx.Default.SavedState);
            if (state == null || !string.Equals(state.StableRunId, stableId, StringComparison.Ordinal))
            {
                return false;
            }

            Totals.Clear();
            CombatHistory.Clear();
            _combatIndex = state.CombatIndex;
            _combatActive = false;
            _combatFinalized = false;
            _activeCombatState = null;
            _currentEncounterName = state.CurrentEncounterName ?? "";
            _activePlayerKey = null;

            if (state.Players != null)
            {
                foreach (SavedPlayer sp in state.Players)
                {
                    Totals[sp.PlayerKey] = FromSavedPlayer(sp);
                }
            }

            if (state.CombatHistory != null)
            {
                foreach (SavedCombatHistory savedCombat in state.CombatHistory)
                {
                    List<PlayerDamageSnapshot> combatPlayers = (savedCombat.Players ?? new List<SavedPlayer>())
                        .Select(FromSavedPlayer)
                        .OrderByDescending(player => player.CombatDamage)
                        .ThenBy(player => player.PlayerKey)
                        .ToList();

                    CombatHistory.Add(new CombatDamageSnapshot
                    {
                        CombatIndex = savedCombat.CombatIndex,
                        EncounterName = savedCombat.EncounterName ?? "",
                        Players = combatPlayers
                    });
                }
            }

            CaptureCommittedCheckpointLocked();
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[DamageTracker] Failed to load state from '{SavePath}': {ex.Message}");
            return false;
        }
    }

    private static SavedPlayer ToSavedPlayer(PlayerDamageSnapshot snapshot)
    {
        return new SavedPlayer
        {
            PlayerKey = snapshot.PlayerKey,
            DisplayName = snapshot.DisplayName,
            CharacterName = snapshot.CharacterName,
            TotalDamage = snapshot.TotalDamage,
            CombatDamage = snapshot.CombatDamage,
            TurnDamage = snapshot.TurnDamage,
            LastDamage = snapshot.LastDamage,
            MaxHitDamage = snapshot.MaxHitDamage,
            Kills = snapshot.Kills,
            CombatKills = snapshot.CombatKills,
            OverkillDamage = snapshot.OverkillDamage,
            CombatOverkillDamage = snapshot.CombatOverkillDamage,
            RunSourceDamage = SerializeSourceDamage(snapshot.RunSourceDamage),
            CombatSourceDamage = SerializeSourceDamage(snapshot.CombatSourceDamage),
            RunCardDamage = SerializeCardDamage(snapshot.RunCardDamage),
            CombatCardDamage = SerializeCardDamage(snapshot.CombatCardDamage)
        };
    }

    private static PlayerDamageSnapshot FromSavedPlayer(SavedPlayer saved)
    {
        Dictionary<DamageSource, decimal> runSourceDamage = DeserializeSourceDamage(saved.RunSourceDamage);
        if (runSourceDamage.Count == 0 && saved.TotalDamage > 0m)
        {
            runSourceDamage[DamageSource.Other] = saved.TotalDamage;
        }

        Dictionary<DamageSource, decimal> combatSourceDamage = DeserializeSourceDamage(saved.CombatSourceDamage);
        if (combatSourceDamage.Count == 0 && saved.CombatDamage > 0m)
        {
            combatSourceDamage[DamageSource.Other] = saved.CombatDamage;
        }

        return new PlayerDamageSnapshot
        {
            PlayerKey = saved.PlayerKey,
            DisplayName = string.IsNullOrWhiteSpace(saved.DisplayName) ? "Unknown Player" : saved.DisplayName,
            CharacterName = string.IsNullOrWhiteSpace(saved.CharacterName) ? "Unknown Character" : saved.CharacterName,
            IsActive = false,
            TotalDamage = saved.TotalDamage,
            CombatDamage = saved.CombatDamage,
            TurnDamage = saved.TurnDamage,
            LastDamage = saved.LastDamage,
            MaxHitDamage = saved.MaxHitDamage,
            Kills = saved.Kills,
            CombatKills = saved.CombatKills,
            OverkillDamage = saved.OverkillDamage,
            CombatOverkillDamage = saved.CombatOverkillDamage,
            RunSourceDamage = runSourceDamage,
            CombatSourceDamage = combatSourceDamage,
            RunCardDamage = DeserializeCardDamage(saved.RunCardDamage),
            CombatCardDamage = DeserializeCardDamage(saved.CombatCardDamage)
        };
    }

    private static Dictionary<string, decimal> SerializeSourceDamage(IReadOnlyDictionary<DamageSource, decimal>? sourceMap)
    {
        Dictionary<string, decimal> result = new(StringComparer.Ordinal);
        if (sourceMap == null)
        {
            return result;
        }

        foreach (KeyValuePair<DamageSource, decimal> entry in sourceMap)
        {
            if (entry.Value > 0m)
            {
                result[entry.Key.ToString()] = entry.Value;
            }
        }

        return result;
    }

    private static Dictionary<string, decimal> SerializeCardDamage(IReadOnlyDictionary<string, decimal>? cardMap)
    {
        Dictionary<string, decimal> result = new(StringComparer.Ordinal);
        if (cardMap == null)
        {
            return result;
        }

        foreach (KeyValuePair<string, decimal> entry in cardMap)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value > 0m)
            {
                result[entry.Key] = entry.Value;
            }
        }

        return result;
    }

    private static Dictionary<DamageSource, decimal> DeserializeSourceDamage(Dictionary<string, decimal>? raw)
    {
        Dictionary<DamageSource, decimal> result = new();
        if (raw == null)
        {
            return result;
        }

        foreach (KeyValuePair<string, decimal> entry in raw)
        {
            if (entry.Value <= 0m || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            if (Enum.TryParse<DamageSource>(entry.Key, ignoreCase: true, out DamageSource source) &&
                Enum.IsDefined(source))
            {
                result[source] = result.TryGetValue(source, out decimal existing)
                    ? existing + entry.Value
                    : entry.Value;
            }
        }

        return result;
    }

    private static Dictionary<string, decimal> DeserializeCardDamage(Dictionary<string, decimal>? raw)
    {
        Dictionary<string, decimal> result = new(StringComparer.Ordinal);
        if (raw == null)
        {
            return result;
        }

        foreach (KeyValuePair<string, decimal> entry in raw)
        {
            if (entry.Value <= 0m || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            string key = entry.Key.Trim();
            result[key] = result.TryGetValue(key, out decimal existing)
                ? existing + entry.Value
                : entry.Value;
        }

        return result;
    }
}

public sealed class OverlayState
{
    public string RunToken { get; init; } = "unknown-run";

    public int CombatIndex { get; init; }

    public bool CombatActive { get; init; }

    public ulong? ActivePlayerKey { get; init; }

    public string CurrentEncounterName { get; init; } = "";

    public IReadOnlyList<PlayerDamageSnapshot> Players { get; init; } = Array.Empty<PlayerDamageSnapshot>();

    public IReadOnlyList<CombatDamageSnapshot> CombatHistory { get; init; } = Array.Empty<CombatDamageSnapshot>();
}

public sealed class PlayerDamageSnapshot
{
    public ulong PlayerKey { get; set; }

    public string DisplayName { get; set; } = "Unknown Player";

    public string CharacterName { get; set; } = "Unknown Character";

    public Texture2D? PortraitTexture { get; set; }

    public bool IsActive { get; set; }

    public decimal TotalDamage { get; set; }

    public decimal CombatDamage { get; set; }

    public decimal TurnDamage { get; set; }

    public decimal LastDamage { get; set; }

    public decimal MaxHitDamage { get; set; }

    public int Kills { get; set; }

    public int CombatKills { get; set; }

    public decimal OverkillDamage { get; set; }

    public decimal CombatOverkillDamage { get; set; }

    public IReadOnlyDictionary<DamageSource, decimal> RunSourceDamage { get; set; } =
        new Dictionary<DamageSource, decimal>();

    public IReadOnlyDictionary<DamageSource, decimal> CombatSourceDamage { get; set; } =
        new Dictionary<DamageSource, decimal>();

    public IReadOnlyDictionary<string, decimal> RunCardDamage { get; set; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, decimal> CombatCardDamage { get; set; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    public DateTime LastUpdatedUtc { get; set; }

    public PlayerDamageSnapshot Clone()
    {
        return new PlayerDamageSnapshot
        {
            PlayerKey = PlayerKey,
            DisplayName = DisplayName,
            CharacterName = CharacterName,
            PortraitTexture = PortraitTexture,
            IsActive = IsActive,
            TotalDamage = TotalDamage,
            CombatDamage = CombatDamage,
            TurnDamage = TurnDamage,
            LastDamage = LastDamage,
            MaxHitDamage = MaxHitDamage,
            Kills = Kills,
            CombatKills = CombatKills,
            OverkillDamage = OverkillDamage,
            CombatOverkillDamage = CombatOverkillDamage,
            RunSourceDamage = RunSourceDamage != null
                ? new Dictionary<DamageSource, decimal>(RunSourceDamage)
                : new Dictionary<DamageSource, decimal>(),
            CombatSourceDamage = CombatSourceDamage != null
                ? new Dictionary<DamageSource, decimal>(CombatSourceDamage)
                : new Dictionary<DamageSource, decimal>(),
            RunCardDamage = RunCardDamage != null
                ? new Dictionary<string, decimal>(RunCardDamage, StringComparer.Ordinal)
                : new Dictionary<string, decimal>(StringComparer.Ordinal),
            CombatCardDamage = CombatCardDamage != null
                ? new Dictionary<string, decimal>(CombatCardDamage, StringComparer.Ordinal)
                : new Dictionary<string, decimal>(StringComparer.Ordinal),
            LastUpdatedUtc = LastUpdatedUtc
        };
    }
}

public readonly record struct PlayerHandle(
    ulong PlayerKey,
    string DisplayName,
    string? CharacterName,
    Texture2D? PortraitTexture);

// ── Persistence models ─────────────────────────────────────

public sealed class SavedState
{
    public string StableRunId { get; set; } = "";
    public int CombatIndex { get; set; }
    public string CurrentEncounterName { get; set; } = "";
    public List<SavedPlayer> Players { get; set; } = new();
    public List<SavedCombatHistory> CombatHistory { get; set; } = new();
}

public sealed class SavedCombatHistory
{
    public int CombatIndex { get; set; }
    public string EncounterName { get; set; } = "";
    public List<SavedPlayer> Players { get; set; } = new();
}

public sealed class SavedPlayer
{
    public ulong PlayerKey { get; set; }
    public string DisplayName { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public decimal TotalDamage { get; set; }
    public decimal CombatDamage { get; set; }
    public decimal TurnDamage { get; set; }
    public decimal LastDamage { get; set; }
    public decimal MaxHitDamage { get; set; }
    public int Kills { get; set; }
    public int CombatKills { get; set; }
    public decimal OverkillDamage { get; set; }
    public decimal CombatOverkillDamage { get; set; }
    public Dictionary<string, decimal> RunSourceDamage { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> CombatSourceDamage { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> RunCardDamage { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> CombatCardDamage { get; set; } = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(SavedState))]
internal partial class SavedStateCtx : JsonSerializerContext { }
