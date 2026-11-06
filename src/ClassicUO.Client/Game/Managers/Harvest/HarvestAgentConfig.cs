#nullable enable

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>What the Harvest agent gathers.</summary>
public enum HarvestMode
{
    Mining,
    Lumberjacking
}

/// <summary>
/// Per-profile settings for the Harvest agent and the depleted-spot highlight.
/// </summary>
/// <remarks>
/// Held on <see cref="ClassicUO.Configuration.Profile"/> and serialized as part of <c>profile.json</c>.
/// Public get/set properties only: the profile's serializer is not configured for fields.
/// </remarks>
public sealed class HarvestAgentConfig
{
    /// <summary>Default highlight hue: a mid brown that stays readable on both rock and foliage.</summary>
    public const ushort DEFAULT_DEPLETED_HUE = 0x01BB;

    /// <summary>Which resource the agent gathers when started.</summary>
    public HarvestMode Mode { get; set; } = HarvestMode.Mining;

    /// <summary>Tint rocks and trees the server reported as empty until they would have respawned.</summary>
    public bool HighlightDepleted { get; set; } = true;

    /// <summary>Hue used for the depleted-spot highlight.</summary>
    public ushort DepletedHue { get; set; } = DEFAULT_DEPLETED_HUE;

    /// <summary>Stones kept free before each mining swing (one large ore pile weighs 12).</summary>
    public int OreWeightMargin { get; set; } = 12;

    /// <summary>Stones kept free before each chop (one chop yields 10 logs at 2 stones each on ServUO).</summary>
    public int LogWeightMargin { get; set; } = 20;

    /// <summary>Mining: smelt ore at a nearby forge or the player's fire beetle.</summary>
    public bool SmeltOre { get; set; } = true;

    /// <summary>When nearly full, move resources onto the player's nearby pack animal.</summary>
    public bool UsePackAnimal { get; set; } = true;

    /// <summary>Move whatever occupies the hand slot to the backpack so the harvest tool can be equipped.</summary>
    public bool ClearHandsForTool { get; set; } = true;
}
