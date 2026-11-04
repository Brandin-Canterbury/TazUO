#nullable enable

using System;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>What a server message means to the Harvest agent.</summary>
internal enum HarvestMessage
{
    None,

    /// <summary>The swing produced a resource.</summary>
    Success,

    /// <summary>The swing finished without a resource; the spot still has some.</summary>
    Fail,

    /// <summary>The ore bank at the last target is empty.</summary>
    OreDepleted,

    /// <summary>The wood bank at the last target is empty.</summary>
    WoodDepleted,

    /// <summary>The target was out of reach or sight; try another tile.</summary>
    Unreachable,

    /// <summary>The target was not harvestable with this tool.</summary>
    InvalidTarget,

    /// <summary>The tool broke or wore out.</summary>
    ToolBroken,

    /// <summary>The backpack could not hold the harvest.</summary>
    PackFull,

    /// <summary>The player cannot harvest at all right now (mounted, polymorphed).</summary>
    CannotHarvest,

    /// <summary>The tool has to be equipped first.</summary>
    MustEquip,

    /// <summary>The server tried to equip the tool but the hands are full.</summary>
    HandsFull,

    /// <summary>Acting too fast; retry shortly.</summary>
    Busy,

    /// <summary>Ore smelted (fully or partly); try the next pile.</summary>
    SmeltDone,

    /// <summary>This ore pile cannot be smelted by this player; skip it.</summary>
    SmeltSkip
}

/// <summary>
/// Maps ServUO harvest and smelting messages to <see cref="HarvestMessage"/>. Cliloc numbers are
/// preferred; plain strings cover the shard's custom gathering scripts.
/// </summary>
internal static class HarvestMessages
{
    #region Private members

    private const string HANDS_FULL_TEXT = "You need your pickaxe in hand";

    #endregion

    #region Public methods

    /// <summary>Classifies a localized (cliloc) system message.</summary>
    /// <param name="cliloc">The cliloc number.</param>
    /// <returns>The meaning, or <see cref="HarvestMessage.None"/> when unrelated.</returns>
    public static HarvestMessage Classify(uint cliloc)
    {
        // Ore success: "You dig some <metal> ore and put it in your backpack."
        if (cliloc >= 1007072 && cliloc <= 1007080)
            return HarvestMessage.Success;

        // Wood success: "You put some <wood> logs into your backpack."
        if (cliloc >= 1072540 && cliloc <= 1072546)
            return HarvestMessage.Success;

        switch (cliloc)
        {
            case 500498:  // You put some logs into your backpack.
            case 1044606: // You carefully extract some workable stone from the ore vein!
            case 1112233: // You carefully extract a glistening gem from the vein!
            case 1149918: // You have uncovered a ~1_SIZE~ deposit of niter!
                return HarvestMessage.Success;

            case 503043: // You loosen some rocks but fail to find any useable ore.
            case 500495: // You hack at the tree for a while, but fail to produce any useable wood.
                return HarvestMessage.Fail;

            case 503040: // There is no metal here to mine.
            case 503042: // Someone has gotten to the metal before you.
                return HarvestMessage.OreDepleted;

            case 500493: // There's not enough wood here to harvest.
                return HarvestMessage.WoodDepleted;

            case 500446: // That is too far away.
            case 503041: // You have moved too far away to continue mining.
            case 500237: // Target can not be seen.
                return HarvestMessage.Unreachable;

            case 501862:  // You can't mine there.
            case 501863:  // You can't mine that.
            case 500489:  // You can't use an axe on that.
            case 1005213: // You can't do that
                return HarvestMessage.InvalidTarget;

            case 1044038: // You have worn out your tool!
            case 500499:  // You broke your axe.
                return HarvestMessage.ToolBroken;

            case 1010481: // Your backpack is full, so the ore you mined is lost.
            case 500497:  // You can't place any wood into your backpack!
                return HarvestMessage.PackFull;

            case 501864: // You can't mine while riding.
            case 501865: // You can't mine while polymorphed.
                return HarvestMessage.CannotHarvest;

            case 500487: // The axe must be equipped for any serious wood chopping.
                return HarvestMessage.MustEquip;

            case 500119: // You must wait to perform another action.
                return HarvestMessage.Busy;

            case 501988:  // You smelt the ore removing the impurities and put the metal in your backpack.
            case 501990:  // You burn away the impurities but are left with less useable metal.
            case 1152620: // The magic of your talisman guides your hands as you purify the metal.
                return HarvestMessage.SmeltDone;

            case 501986: // You have no idea how to smelt this strange ore!
            case 501987: // There is not enough metal-bearing ore in this pile to make an ingot.
            case 500447: // That is not accessible
            case 501976: // The ore is too far away.
                return HarvestMessage.SmeltSkip;
        }

        return HarvestMessage.None;
    }

    /// <summary>Classifies a plain-text system message (custom shard scripts send these).</summary>
    /// <param name="text">The message text.</param>
    /// <returns>The meaning, or <see cref="HarvestMessage.None"/> when unrelated.</returns>
    public static HarvestMessage Classify(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return HarvestMessage.None;

        // SeriousFun GatherMods: a pickaxe must be held, and the server could not equip it.
        if (text.StartsWith(HANDS_FULL_TEXT, StringComparison.OrdinalIgnoreCase))
            return HarvestMessage.HandsFull;

        return HarvestMessage.None;
    }

    #endregion
}
