#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Structs;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>
/// Finds harvest tools and resource stacks on the player and queues equip/unequip moves. Main thread only.
/// </summary>
internal static class HarvestTools
{
    #region Public methods

    /// <summary>The harvest tool the player holds for <paramref name="mode"/>, if any.</summary>
    /// <param name="player">The player.</param>
    /// <param name="mode">Mining or lumberjacking.</param>
    /// <returns>The equipped tool, or null.</returns>
    public static Item? FindEquippedTool(PlayerMobile player, HarvestMode mode)
    {
        Item? oneHanded = player.FindItemByLayer(Layer.OneHanded);

        if (oneHanded != null && IsTool(oneHanded.Graphic, mode))
            return oneHanded;

        Item? twoHanded = player.FindItemByLayer(Layer.TwoHanded);

        return twoHanded != null && IsTool(twoHanded.Graphic, mode) ? twoHanded : null;
    }

    /// <summary>
    /// A tool for <paramref name="mode"/> anywhere in the backpack. Mining prefers pickaxes over shovels.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mode">Mining or lumberjacking.</param>
    /// <returns>The tool, or null when the pack holds none.</returns>
    public static Item? FindBackpackTool(PlayerMobile player, HarvestMode mode)
    {
        Item? backpack = player.Backpack;

        if (backpack == null)
            return null;

        if (mode == HarvestMode.Lumberjacking)
            return FindInContainer(backpack, HarvestData.IsAxe);

        return FindInContainer(backpack, HarvestData.IsPickaxe) ?? FindInContainer(backpack, HarvestData.IsShovel);
    }

    /// <summary>True when the tool should be equipped before use rather than used from the pack.</summary>
    /// <remarks>
    /// The shard requires pickaxes in hand. Shovels are equipped too when their tile data names a hand
    /// slot; a shovel that cannot be worn is used from the pack as stock ServUO allows.
    /// </remarks>
    /// <param name="tool">A mining or lumberjacking tool.</param>
    /// <param name="mode">Mining or lumberjacking.</param>
    /// <returns>Whether to equip the tool first.</returns>
    public static bool ShouldEquip(Item tool, HarvestMode mode)
    {
        if (mode != HarvestMode.Mining)
            return false;

        if (HarvestData.IsPickaxe(tool.Graphic))
            return true;

        return IsHandLayer((Layer)tool.ItemData.Layer);
    }

    /// <summary>
    /// Queues moving the tool into the player's hand, first moving whatever occupies the needed hand
    /// slots to the backpack when <paramref name="clearHands"/> is set.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="tool">The tool to equip.</param>
    /// <param name="clearHands">Allow unequipping items in the way.</param>
    /// <param name="clearBothHands">Clear both hand slots, for when a one-slot clear was rejected.</param>
    /// <returns>False when a hand is occupied and clearing is not allowed.</returns>
    public static bool QueueEquip(PlayerMobile player, Item tool, bool clearHands, bool clearBothHands)
    {
        Layer layer = (Layer)tool.ItemData.Layer;

        if (!IsHandLayer(layer))
            layer = Layer.OneHanded;

        Item? backpack = player.Backpack;

        if (backpack == null)
            return false;

        bool bothHands = clearBothHands || layer == Layer.TwoHanded;

        if (!TryQueueClear(player, backpack, layer, clearHands))
            return false;

        if (bothHands && !TryQueueClear(player, backpack, OtherHand(layer), clearHands))
            return false;

        ObjectActionQueue.Instance.Enqueue(
            new MoveRequest(tool.Serial, player.Serial, 1, layer: layer, moveType: MoveType.Equip).ToObjectActionQueueItem(),
            ActionPriority.EquipItem
        );

        return true;
    }

    /// <summary>True when the item is currently worn by the player.</summary>
    /// <param name="player">The player.</param>
    /// <param name="item">The item.</param>
    /// <returns>Whether the item is equipped on the player.</returns>
    public static bool IsEquippedOn(PlayerMobile player, Item item) =>
        !item.IsDestroyed && item.Container == player.Serial;

    /// <summary>Collects matching stacks from the backpack and its sub-containers.</summary>
    /// <param name="container">The container to search.</param>
    /// <param name="match">Graphic predicate.</param>
    /// <param name="results">Receives the matches.</param>
    public static void CollectInContainer(Item container, Func<ushort, bool> match, List<Item> results)
    {
        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (item.IsDestroyed)
                continue;

            if (match(item.Graphic))
                results.Add(item);
            else if (!item.IsEmpty)
                CollectInContainer(item, match, results);
        }
    }

    #endregion

    #region Private methods

    private static bool IsTool(ushort graphic, HarvestMode mode) =>
        mode == HarvestMode.Lumberjacking
            ? HarvestData.IsAxe(graphic)
            : HarvestData.IsPickaxe(graphic) || HarvestData.IsShovel(graphic);

    private static bool IsHandLayer(Layer layer) => layer == Layer.OneHanded || layer == Layer.TwoHanded;

    private static Layer OtherHand(Layer layer) => layer == Layer.OneHanded ? Layer.TwoHanded : Layer.OneHanded;

    private static Item? FindInContainer(Item container, Func<ushort, bool> match)
    {
        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (item.IsDestroyed)
                continue;

            if (match(item.Graphic))
                return item;

            if (!item.IsEmpty)
            {
                Item? found = FindInContainer(item, match);

                if (found != null)
                    return found;
            }
        }

        return null;
    }

    /// <summary>Queues the occupant of <paramref name="layer"/> into the backpack, if allowed.</summary>
    private static bool TryQueueClear(PlayerMobile player, Item backpack, Layer layer, bool clearHands)
    {
        Item? occupant = player.FindItemByLayer(layer);

        if (occupant == null)
            return true;

        if (!clearHands)
            return false;

        ObjectActionQueue.Instance.Enqueue(
            new MoveRequest(occupant.Serial, backpack.Serial).ToObjectActionQueueItem(),
            ActionPriority.UnequipItem
        );

        return true;
    }

    #endregion
}
