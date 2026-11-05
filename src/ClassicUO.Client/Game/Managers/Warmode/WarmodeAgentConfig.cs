#nullable enable

using System.Collections.Generic;

namespace ClassicUO.Game.Managers.Warmode;

/// <summary>Per-profile settings for the Warmode agent.</summary>
/// <remarks>
/// Held on <see cref="ClassicUO.Configuration.Profile"/> and serialized as part of <c>profile.json</c>.
/// Public get/set properties only: the profile's serializer is not configured for fields.
/// </remarks>
public sealed class WarmodeAgentConfig
{
    /// <summary>The agent fights whenever the player is in war mode; off only through the agent tab.</summary>
    public bool Enabled { get; set; }

    /// <summary>Tiles from the player within which creatures are picked.</summary>
    public int Range { get; set; } = 10;

    /// <summary>Only pick creatures that are in war mode themselves, i.e. already fighting someone.</summary>
    public bool OnlyFighting { get; set; } = true;

    /// <summary>Leave the current target for a higher-priority creature as soon as one is in range.</summary>
    public bool SwitchToHigherPriority { get; set; } = true;

    /// <summary>Append every creature the player fights to the bottom of <see cref="Priorities"/>.</summary>
    public bool AutoAddAttackers { get; set; } = true;

    /// <summary>A harmful spell's target cursor goes straight to the agent's target.</summary>
    public bool AutoTargetSpells { get; set; } = true;

    /// <summary>Spell names that are auto-targeted; empty means every harmful spell.</summary>
    public List<string> TargetSpells { get; set; } = new();

    /// <summary>Creature names, most dangerous first. Unlisted creatures rank below every entry.</summary>
    public List<WarmodePriority> Priorities { get; set; } = new();
}

/// <summary>One line of the Warmode agent's priority list.</summary>
public sealed class WarmodePriority
{
    /// <summary>Matched case-insensitively against any part of a creature's name ("orc" matches "an orc mage").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Never pick matching creatures.</summary>
    public bool Ignore { get; set; }
}
