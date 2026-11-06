#nullable enable

using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Managers.Warmode;

/// <summary>What the picker needs to know about one hostile creature near the player.</summary>
/// <param name="Serial">The creature.</param>
/// <param name="Name">Its name as shown overhead; may be empty before the server sends it.</param>
/// <param name="Distance">Tiles from the player.</param>
/// <param name="IsHuman">Human-shaped body, which may be another player.</param>
/// <param name="InWarMode">The creature is fighting someone.</param>
/// <param name="HitsPercent">Remaining health, 0-100.</param>
internal readonly record struct WarmodeCandidate(
    uint Serial,
    string Name,
    int Distance,
    bool IsHuman,
    bool InWarMode,
    int HitsPercent
);

/// <summary>Chooses the Warmode agent's target from the priority list. Pure; unit-tested.</summary>
internal static class WarmodeTargetPicker
{
    #region Public accessors

    /// <summary>Rank of a creature no entry matches: below every listed one.</summary>
    public const int UNLISTED = int.MaxValue;

    /// <summary>Creatures this close (adjacent, within a swing) always come before the priority list.</summary>
    public const int ADJACENT = 1;

    #endregion

    #region Public methods

    /// <summary>Position of the first entry whose name appears in <paramref name="name"/>.</summary>
    /// <param name="priorities">The priority list, most dangerous first.</param>
    /// <param name="name">The creature's name.</param>
    /// <param name="ignored">True when that entry says to never attack it.</param>
    /// <returns>The entry's index, or <see cref="UNLISTED"/>.</returns>
    public static int Rank(IReadOnlyList<WarmodePriority> priorities, string name, out bool ignored)
    {
        ignored = false;

        if (string.IsNullOrEmpty(name))
            return UNLISTED;

        for (int i = 0; i < priorities.Count; i++)
        {
            string entry = priorities[i].Name.Trim();

            if (entry.Length == 0 || !name.Contains(entry, StringComparison.OrdinalIgnoreCase))
                continue;

            ignored = priorities[i].Ignore;
            return i;
        }

        return UNLISTED;
    }

    /// <summary>
    /// The creature to fight. Creatures adjacent to the player always come first, so a fleeing target never
    /// leaves the player standing still while others hit them; among them (or, when none is adjacent, among all)
    /// the best ranked, then nearest, then most wounded wins. The current target is kept while it stays adjacent,
    /// unless an adjacent higher-priority one appears (and switching is on); a current target that is not
    /// adjacent is dropped for any adjacent creature.
    /// </summary>
    /// <param name="candidates">Hostile creatures near the player.</param>
    /// <param name="config">The agent settings.</param>
    /// <param name="current">The player's current target, if it is still alive and in range.</param>
    /// <returns>The serial to attack, or 0 when nothing qualifies.</returns>
    /// <remarks>
    /// A current target the player picked by hand is respected even when it would not be eligible on its
    /// own (an unlisted human, a creature not in war mode); only an "ignore" entry drops it.
    /// </remarks>
    public static uint Choose(IReadOnlyList<WarmodeCandidate> candidates, WarmodeAgentConfig config, WarmodeCandidate? current)
    {
        int currentRank = UNLISTED;
        bool keepCurrent = false;

        if (current is { } cur && cur.Distance <= config.Range)
        {
            currentRank = Rank(config.Priorities, cur.Name, out bool currentIgnored);
            keepCurrent = !currentIgnored;
        }

        WarmodeCandidate? best = null;
        int bestRank = UNLISTED;

        foreach (WarmodeCandidate candidate in candidates)
        {
            int rank = Rank(config.Priorities, candidate.Name, out bool ignored);

            if (!IsEligible(candidate, config, rank, ignored))
                continue;

            if (best == null || IsBetter(candidate, rank, best.Value, bestRank))
            {
                best = candidate;
                bestRank = rank;
            }
        }

        if (keepCurrent)
        {
            bool currentAdjacent = current!.Value.Distance <= ADJACENT;
            bool bestAdjacent = best != null && best.Value.Distance <= ADJACENT;

            // Someone is in swing range and the current target isn't: fight what is hitting us.
            if (!currentAdjacent && bestAdjacent)
                return best!.Value.Serial;

            bool outranked = config.SwitchToHigherPriority
                             && best != null
                             && bestRank < currentRank
                             && (bestAdjacent || !currentAdjacent);

            return outranked ? best!.Value.Serial : current.Value.Serial;
        }

        return best?.Serial ?? 0;
    }

    #endregion

    #region Private methods

    // Humans may be players, so only listed ones are picked automatically.
    private static bool IsEligible(WarmodeCandidate candidate, WarmodeAgentConfig config, int rank, bool ignored) =>
        !ignored
        && candidate.Distance <= config.Range
        && (!candidate.IsHuman || rank != UNLISTED)
        && (!config.OnlyFighting || candidate.InWarMode);

    private static bool IsBetter(WarmodeCandidate candidate, int rank, WarmodeCandidate best, int bestRank)
    {
        bool adjacent = candidate.Distance <= ADJACENT;

        if (adjacent != best.Distance <= ADJACENT)
            return adjacent;

        if (rank != bestRank)
            return rank < bestRank;

        if (candidate.Distance != best.Distance)
            return candidate.Distance < best.Distance;

        return candidate.HitsPercent < best.HitsPercent;
    }

    #endregion
}
