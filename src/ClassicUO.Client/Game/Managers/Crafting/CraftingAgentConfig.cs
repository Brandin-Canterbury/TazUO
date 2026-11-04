#nullable enable

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>Per-profile settings for the Crafting agent.</summary>
/// <remarks>
/// Held on <see cref="ClassicUO.Configuration.Profile"/> and serialized as part of <c>profile.json</c>.
/// Public get/set properties only: the profile's serializer is not configured for fields.
/// </remarks>
public sealed class CraftingAgentConfig
{
    /// <summary>The craft skill to train.</summary>
    public CraftSkill Skill { get; set; } = CraftSkill.Blacksmithy;

    /// <summary>Skill at which the agent stops; 0 means the skill's cap.</summary>
    public float TargetSkill { get; set; }

    /// <summary>Smelt (Blacksmithy) or cut (Tailoring) the agent's own products to recover material.</summary>
    public bool Recycle { get; set; } = true;

    /// <summary>Crafts between recycling passes; a full or heavy pack triggers a pass sooner.</summary>
    public int RecycleEvery { get; set; } = 10;

    /// <summary>What happens to products of crafts the server cannot recycle.</summary>
    public CraftDisposal Disposal { get; set; } = CraftDisposal.Keep;

    /// <summary>Stones kept free below the weight limit before each craft.</summary>
    public int WeightMargin { get; set; } = 10;
}
