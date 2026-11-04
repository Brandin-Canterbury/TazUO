#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI;
using ClassicUO.Game.UI.Gumps;

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>
/// Lookups the Crafting agent makes against the client's world: craft tools, scissors, trash
/// barrels, the open craft gump and backpack contents. Main thread only.
/// </summary>
internal sealed class CraftWorld
{
    #region Private members

    private const ushort SCISSORS_GRAPHIC = 0x0F9F;
    private const ushort SCISSORS_GRAPHIC_FLIPPED = 0x0F9E;
    private const ushort TRASH_BARREL_GRAPHIC = 0x0E77;
    private const string TRASH_NAME = "trash";
    private const string BLESSED_TEXT = "blessed";
    private const string INSURED_TEXT = "insured";

    /// <summary>Server reach for the craft target cursors and the trash barrel.</summary>
    private const int REACH = 2;

    private sealed record ParsedLayout(string Text, CraftGumpLayout Layout);

    /// <summary>Parsed layouts per gump; weak keys so closed gumps drop out on their own.</summary>
    private readonly ConditionalWeakTable<Gump, ParsedLayout> _layouts = new();

    #endregion

    #region Public methods

    /// <summary>The open craft gump for a craft skill and its parsed layout.</summary>
    /// <param name="info">The craft skill.</param>
    /// <param name="layout">The parsed layout when found.</param>
    /// <returns>The gump, or null when that craft menu is not open.</returns>
    /// <remarks>Parses each gump version once; repeated calls on an unchanged gump are cheap.</remarks>
    public Gump? FindCraftGump(CraftSkillInfo info, out CraftGumpLayout? layout)
    {
        foreach (IGui gui in UIManager.Gumps)
        {
            if (gui is not Gump { IsDisposed: false, IsFromServer: true } gump)
                continue;

            CraftGumpLayout parsed = GetLayout(gump);

            if (parsed.IsCraftGump && parsed.TitleCliloc == info.TitleCliloc)
            {
                layout = parsed;
                return gump;
            }
        }

        layout = null;
        return null;
    }

    /// <summary>
    /// The craft tool to use: one in hand first (the server insists on it), else an unhued one in the
    /// backpack. Hued tools are skipped so runic tools are never spent on training.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="info">The craft skill.</param>
    /// <returns>The tool, or null when the player has none.</returns>
    public static Item? FindTool(PlayerMobile player, CraftSkillInfo info)
    {
        Item? inHand = player.FindItemByLayer(Layer.OneHanded) ?? player.FindItemByLayer(Layer.TwoHanded);

        if (inHand != null && info.IsTool(inHand.Graphic) && inHand.Hue == 0)
            return inHand;

        return player.Backpack == null ? null : FindInContainer(player.Backpack, item => info.IsTool(item.Graphic) && item.Hue == 0);
    }

    /// <summary>Scissors anywhere in the backpack.</summary>
    /// <param name="player">The player.</param>
    /// <returns>The scissors, or null.</returns>
    public static Item? FindScissors(PlayerMobile player) =>
        player.Backpack == null
            ? null
            : FindInContainer(player.Backpack, item => item.Graphic is SCISSORS_GRAPHIC or SCISSORS_GRAPHIC_FLIPPED);

    /// <summary>A trash barrel on the ground within reach. Only barrels whose name says "trash" count.</summary>
    /// <param name="world">The world.</param>
    /// <returns>The barrel, or null.</returns>
    public static Item? FindTrashBarrel(World world)
    {
        foreach (Item item in world.Items.Values)
        {
            if (item.IsDestroyed || !item.OnGround || item.Graphic != TRASH_BARREL_GRAPHIC || item.Distance > REACH)
                continue;

            if (world.OPL.TryGetNameAndData(item.Serial, out string name, out _)
                && name != null
                && name.Contains(TRASH_NAME, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
    }

    /// <summary>Serials of the items lying directly in the backpack.</summary>
    /// <param name="player">The player.</param>
    /// <param name="serials">Cleared, then filled.</param>
    public static void SnapshotBackpack(PlayerMobile player, HashSet<uint> serials)
    {
        serials.Clear();

        if (player.Backpack == null)
            return;

        for (LinkedObject? node = player.Backpack.Items; node != null; node = node.Next)
            serials.Add(((Item)node).Serial);
    }

    /// <summary>Adds items now directly in the backpack that were not in <paramref name="before"/>.</summary>
    /// <param name="player">The player.</param>
    /// <param name="before">Serials taken before the craft.</param>
    /// <param name="added">Receives the new serials.</param>
    public static void CollectNewItems(PlayerMobile player, HashSet<uint> before, ICollection<uint> added)
    {
        if (player.Backpack == null)
            return;

        for (LinkedObject? node = player.Backpack.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (!item.IsDestroyed && !before.Contains(item.Serial))
                added.Add(item.Serial);
        }
    }

    /// <summary>Counts every item in the backpack, sub-containers included (the server's item limit).</summary>
    /// <param name="player">The player.</param>
    /// <returns>The item count.</returns>
    public static int CountBackpackItems(PlayerMobile player) => player.Backpack == null ? 0 : CountItems(player.Backpack);

    /// <summary>
    /// True when the item is still loose in the player's backpack and its properties do not say blessed or
    /// insured, so the agent may smelt, cut or discard it.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="serial">The crafted item.</param>
    /// <returns>Whether the item may be recycled or discarded.</returns>
    public static bool IsDisposable(World world, uint serial)
    {
        Item? item = world.Items.Get(serial);
        Item? backpack = world.Player?.Backpack;

        if (item == null || item.IsDestroyed || backpack == null || item.Container != backpack.Serial)
            return false;

        if (!world.OPL.TryGetNameAndData(serial, out _, out string data) || data == null)
            return true;

        return !data.Contains(BLESSED_TEXT, StringComparison.OrdinalIgnoreCase)
               && !data.Contains(INSURED_TEXT, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Private methods

    private CraftGumpLayout GetLayout(Gump gump)
    {
        string text = gump.PacketGumpText;

        // A reused server gump gets a new text string on each rebuild, so the reference marks the version.
        if (_layouts.TryGetValue(gump, out ParsedLayout? cached) && ReferenceEquals(cached.Text, text))
            return cached.Layout;

        var parsed = new ParsedLayout(text, CraftGumpLayout.Parse(text));
        _layouts.AddOrUpdate(gump, parsed);
        return parsed.Layout;
    }

    private static Item? FindInContainer(Item container, Func<Item, bool> match)
    {
        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (item.IsDestroyed)
                continue;

            if (match(item))
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

    private static int CountItems(Item container)
    {
        int count = 0;

        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (item.IsDestroyed)
                continue;

            count++;

            if (!item.IsEmpty)
                count += CountItems(item);
        }

        return count;
    }

    #endregion
}
