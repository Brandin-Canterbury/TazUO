#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Managers.Crafting;
using ClassicUO.Game.Managers.Harvest;
using ClassicUO.Game.Managers.Warmode;

namespace ClassicUO.Game.Managers;

/// <summary>One agent whose state can be shown in an <see cref="UI.Gumps.AgentStatusGump"/>.</summary>
/// <param name="Key">Stable id, saved with the gump.</param>
/// <param name="Name">Display name.</param>
/// <param name="Status">Current one-line status.</param>
internal sealed record AgentStatusSource(string Key, string Name, Func<string> Status);

/// <summary>Every agent with a status window. Agents without a running state of their own report On or Off.</summary>
internal static class AgentStatusSources
{
    #region Public accessors

    /// <summary>All sources, in the Agents tab's order.</summary>
    public static readonly IReadOnlyList<AgentStatusSource> All =
    [
        new("autoloot", "Auto Loot", () => OnOff(p => p.EnableAutoLoot)),
        new("scavenger", "Scavenger", () => Enabled(p => p.EnableScavenger) ? ScavengerManager.Instance.Status : OFF),
        new("autobuy", "Auto Buy", () => OnOff(p => p.BuyAgentEnabled)),
        new("autosell", "Auto Sell", () => OnOff(p => p.SellAgentEnabled)),
        new("bandage", "Bandage", () => OnOff(p => p.EnableBandageAgent)),
        new("selfheal", "Self Heal", () => OnOff(p => p.SelfHeal_Enabled)),
        new("statlock", "Stat Lock", () => AutoStatLockManager.Instance.State.Enabled ? ON : OFF),
        new("harvest", "Harvest", () => HarvestAgent.Instance.Status),
        new("crafting", "Crafting", () => CraftingAgent.Instance.Status),
        new("warmode", "Warmode", () => WarmodeAgent.Instance.Status)
    ];

    #endregion

    #region Private members

    private const string ON = "On";
    private const string OFF = "Off";

    #endregion

    #region Public methods

    /// <summary>The source with this key, or null.</summary>
    /// <param name="key">A source key.</param>
    /// <returns>The source, or null when unknown.</returns>
    public static AgentStatusSource? Find(string? key)
    {
        foreach (AgentStatusSource source in All)
        {
            if (source.Key == key)
                return source;
        }

        return null;
    }

    #endregion

    #region Private methods

    private static bool Enabled(Func<Profile, bool> flag) => ProfileManager.CurrentProfile is { } profile && flag(profile);

    private static string OnOff(Func<Profile, bool> flag) => Enabled(flag) ? ON : OFF;

    #endregion
}
