using System.Globalization;

namespace DamageTracker;

public static class BattleReportFormatter
{
    public static string Format(
        OverlayState state,
        int? historyCombatIndex,
        Func<string, string> localize)
    {
        if (state == null)
        {
            return "";
        }

        Func<string, string> loc = localize ?? (static key => key);
        CultureInfo inv = CultureInfo.InvariantCulture;

        bool isHistory = historyCombatIndex.HasValue;
        IReadOnlyList<PlayerDamageSnapshot> scopePlayers;
        string scopeLabel;

        if (historyCombatIndex is int targetCombatIndex)
        {
            CombatDamageSnapshot? combat = state.CombatHistory?
                .FirstOrDefault(entry => entry.CombatIndex == targetCombatIndex);
            if (combat == null || combat.Players == null || combat.Players.Count == 0)
            {
                return "";
            }

            scopePlayers = combat.Players;
            string combatLabel = SafeFormat(inv, loc("HISTORY_COMBAT"), combat.CombatIndex);
            string encounterName = SanitizeSingleLine(combat.EncounterName);
            scopeLabel = string.IsNullOrEmpty(encounterName)
                ? combatLabel
                : $"{combatLabel} · {encounterName}";
        }
        else
        {
            scopePlayers = state.Players;
            if (scopePlayers == null || scopePlayers.Count == 0)
            {
                return "";
            }

            scopeLabel = SanitizeSingleLine(loc("RUN"));
        }

        List<PlayerDamageSnapshot> ranked = scopePlayers
            .OrderByDescending(player => GetPrimaryDamage(player, isHistory))
            .ThenBy(player => player.PlayerKey)
            .ToList();

        decimal teamTotal = ranked.Sum(player => GetPrimaryDamage(player, isHistory));

        List<string> lines = new(ranked.Count + 4)
        {
            SafeFormat(inv, loc("REPORT_TITLE"), scopeLabel),
            SafeFormat(inv, loc("REPORT_TEAM"), RunDamageTrackerService.Format(teamTotal))
        };

        for (int i = 0; i < ranked.Count; i++)
        {
            PlayerDamageSnapshot player = ranked[i];
            int rank = i + 1;
            string cleanName = GetCleanPlayerName(player);
            decimal dmg = GetPrimaryDamage(player, isHistory);
            int pctInt = teamTotal > 0m
                ? (int)Math.Round((dmg / teamTotal) * 100m, 0, MidpointRounding.AwayFromZero)
                : 0;
            int kills = GetKills(player, isHistory);
            decimal overkill = GetOverkill(player, isHistory);

            lines.Add(SafeFormat(
                inv,
                loc("REPORT_PLAYER"),
                rank,
                cleanName,
                RunDamageTrackerService.Format(dmg),
                pctInt,
                kills,
                RunDamageTrackerService.Format(overkill)));
        }

        PlayerDamageSnapshot topDamager = ranked[0];
        if (GetPrimaryDamage(topDamager, isHistory) > 0m)
        {
            lines.Add(SafeFormat(inv, loc("REPORT_MVP"), GetCleanPlayerName(topDamager)));
        }

        PlayerDamageSnapshot topFinisher = ranked
            .OrderByDescending(player => GetKills(player, isHistory))
            .ThenByDescending(player => GetPrimaryDamage(player, isHistory))
            .ThenBy(player => player.PlayerKey)
            .First();

        if (GetKills(topFinisher, isHistory) > 0)
        {
            lines.Add(SafeFormat(inv, loc("REPORT_FINISHER"), GetCleanPlayerName(topFinisher)));
        }

        return string.Join("\n", lines);
    }

    private static decimal GetPrimaryDamage(PlayerDamageSnapshot player, bool isHistory)
    {
        return isHistory ? player.CombatDamage : player.TotalDamage;
    }

    private static int GetKills(PlayerDamageSnapshot player, bool isHistory)
    {
        return isHistory ? player.CombatKills : player.Kills;
    }

    private static decimal GetOverkill(PlayerDamageSnapshot player, bool isHistory)
    {
        return isHistory ? player.CombatOverkillDamage : player.OverkillDamage;
    }

    private static string GetCleanPlayerName(PlayerDamageSnapshot player)
    {
        string clean = SanitizeSingleLine(player.DisplayName);
        return string.IsNullOrEmpty(clean) ? "Unknown Player" : clean;
    }

    private static string SanitizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string SafeFormat(CultureInfo culture, string template, params object[] args)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        try
        {
            return string.Format(culture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
