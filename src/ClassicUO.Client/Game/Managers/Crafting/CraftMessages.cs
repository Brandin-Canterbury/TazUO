#nullable enable

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>What a craft gump notice or system message means to the Crafting agent.</summary>
internal enum CraftResult
{
    None,

    /// <summary>The item was made (any quality).</summary>
    Success,

    /// <summary>The attempt failed; materials may be lost.</summary>
    Failed,

    /// <summary>Not enough of a material for the selected item.</summary>
    NoMaterials,

    /// <summary>The craft needs an anvil and forge (or similar) nearby.</summary>
    NeedStation,

    /// <summary>The selected material (colored ingots, leather) needs more skill.</summary>
    MaterialSkill,

    /// <summary>The tool wore out.</summary>
    ToolBroken,

    /// <summary>The player lacks the skill for the item.</summary>
    NoSkill,

    /// <summary>The spell is not in the player's spellbook (Inscription).</summary>
    MissingSpell,

    /// <summary>The player lacks the mana for the scroll (Inscription).</summary>
    NeedMana,

    /// <summary>The backpack cannot hold the product.</summary>
    PackFull,

    /// <summary>Make Last was used before anything was made.</summary>
    NothingMadeYet,

    /// <summary>Acting too fast; retry shortly.</summary>
    Busy,

    /// <summary>A smelt or cut produced material.</summary>
    Recycled,

    /// <summary>A smelt or cut was refused for this item; skip it.</summary>
    RecycleRefused,

    /// <summary>The server is asking for a target (smelt or scissors).</summary>
    TargetPrompt
}

/// <summary>Maps ServUO craft, smelt and scissors clilocs to <see cref="CraftResult"/>.</summary>
internal static class CraftMessages
{
    #region Public methods

    /// <summary>Classifies a craft gump notice or localized system message.</summary>
    /// <param name="cliloc">The cliloc number.</param>
    /// <returns>The meaning, or <see cref="CraftResult.None"/> when unrelated.</returns>
    public static CraftResult Classify(int cliloc)
    {
        // "You do not have sufficient <reagent> to make that." for the eight magery reagents.
        if (cliloc >= 1044361 && cliloc <= 1044368)
            return CraftResult.NoMaterials;

        switch (cliloc)
        {
            case 1044154: // You create the item.
            case 1044155: // You create an exceptional quality item.
            case 1044156: // You create an exceptional quality item and affix your maker's mark.
            case 502785:  // You were barely able to make this item.
            case 501629:  // You inscribe the spell and put the scroll in your backpack.
            case 500279:  // You pour the potion into a bottle...
            case 1048136: // You create the potion and pour it into a keg.
                return CraftResult.Success;

            case 1044043: // You failed to create the item, and some of your materials are lost.
            case 1044157: // You failed to create the item, but no materials were lost.
            case 501630:  // You fail to inscribe the scroll, and the scroll is ruined.
            case 500287:  // You fail to create a useful potion.
                return CraftResult.Failed;

            case 1044037: // You do not have sufficient metal to make that.
            case 1044287: // You do not have sufficient cloth to make that.
            case 1044463: // You do not have sufficient leather to make that.
            case 1044351: // You do not have sufficient wood to make that.
            case 1044378: // You do not have enough blank scrolls to make that.
            case 500315:  // You need an empty bottle to make a potion.
            case 502925:  // You don't have the resources required to make that item.
            case 1044253: // You don't have the components needed to make that.
                return CraftResult.NoMaterials;

            case 1044267: // You must be near an anvil and a forge to smith items.
            case 1044265: // You must be near a forge.
            case 1044266: // You must be near an anvil.
                return CraftResult.NeedStation;

            case 1044268: // You cannot work this strange and unusual metal (selected material needs more skill).
                return CraftResult.MaterialSkill;

            case 1044038: // You have worn out your tool!
                return CraftResult.ToolBroken;

            case 1044153: // You don't have the required skills to attempt this item.
                return CraftResult.NoSkill;

            case 1042404: // You don't have that spell!
                return CraftResult.MissingSpell;

            case 1048147: // Your backpack can't hold anything else.
                return CraftResult.PackFull;

            case 1044165: // You haven't made anything yet.
                return CraftResult.NothingMadeYet;

            case 500119: // You must wait to perform another action.
                return CraftResult.Busy;

            case 1044270: // You melt the item down into ingots.
            case 500418:  // (store-bought) You melt the item down into ingots.
                return CraftResult.Recycled;

            case 1044272: // You can't melt that down into ingots.
            case 1044269: // You have no idea how to work this metal (smelting colored metal).
            case 502440:  // Scissors can not be used on that to produce anything.
            case 502437:  // Items you wish to cut must be in your backpack.
                return CraftResult.RecycleRefused;

            case 1044273: // Target an item to recycle.
            case 502434:  // What should I use these scissors on?
                return CraftResult.TargetPrompt;
        }

        return CraftResult.None;
    }

    #endregion
}
