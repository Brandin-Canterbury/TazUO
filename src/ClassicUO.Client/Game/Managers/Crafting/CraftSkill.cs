#nullable enable

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>Craft skills the Crafting agent can train.</summary>
public enum CraftSkill
{
    Blacksmithy,
    Tailoring,
    Tinkering,
    Carpentry,
    Fletching,
    Inscription,
    Alchemy
}

/// <summary>How the server lets a player get material back from a crafted item.</summary>
public enum CraftRecycleMethod
{
    /// <summary>The server has no recycling for this craft.</summary>
    None,

    /// <summary>The craft menu's Smelt Item button (Blacksmithy; needs an anvil and forge).</summary>
    Smelt,

    /// <summary>Scissors on the item (Tailoring).</summary>
    Scissors
}

/// <summary>What the agent does with crafted items it does not recycle.</summary>
public enum CraftDisposal
{
    /// <summary>Leave them in the backpack.</summary>
    Keep,

    /// <summary>Drop them on the ground under the player.</summary>
    DropAtFeet,

    /// <summary>Put them in a trash barrel within reach.</summary>
    TrashBarrel
}
