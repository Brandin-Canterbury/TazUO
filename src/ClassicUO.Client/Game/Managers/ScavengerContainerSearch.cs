#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI;

namespace ClassicUO.Game.Managers;

/// <summary>Whether a container may be opened by the scavenger.</summary>
internal enum ContainerAccess
{
    /// <summary>Not locked down or secured: search it.</summary>
    Open,

    /// <summary>Its property list has not arrived yet; ask again next scan.</summary>
    Unknown,

    /// <summary>Locked down or secured (house storage), or a trash barrel: never touch.</summary>
    Skip
}

/// <summary>Receives what the container search does, for the scavenger's status and log.</summary>
internal interface IContainerSearchReport
{
    /// <summary>A container was just opened.</summary>
    /// <param name="container">The container.</param>
    void Searching(Item container);

    /// <summary>The server never sent a container's contents, usually because it is locked.</summary>
    /// <param name="container">The container.</param>
    void Refused(Item container);
}

/// <summary>
/// Lets the scavenger reach into containers lying within reach: opens each one quietly (no gump) so the client
/// learns its contents, then hands every item found, sub-containers included, to the scavenger's matcher.
/// </summary>
/// <remarks>
/// Main thread only, driven from <see cref="ScavengerManager.Update"/>. Opens at most one container per scan so
/// the server is not flooded. A container whose lock refuses the open simply never sends contents. Locked-down or
/// secured containers (house storage) are never opened, whatever the scavenger's own locked-down setting says.
/// </remarks>
internal sealed class ScavengerContainerSearch
{
    #region Private members

    private const int REACH = 2;
    private const long SCAN_MS = 750;
    private const long RESEARCH_MS = 5 * 60_000;   // dungeon chests refill, so look again after a while
    private const long CONCEAL_EXPIRY_MS = 3_000;  // a locked container sends no contents; stop hiding its gump

    private readonly Dictionary<uint, long> _openedAt = new();
    private readonly Dictionary<uint, long> _concealUntil = new();
    private readonly List<uint> _expired = new();
    private long _nextScan;

    #endregion

    #region Public methods

    /// <summary>Forgets every container, so all are opened again.</summary>
    public void Reset()
    {
        _openedAt.Clear();
        _concealUntil.Clear();
    }

    /// <summary>Opens the next due container in reach and checks the known contents of all of them.</summary>
    /// <param name="world">The world.</param>
    /// <param name="access">Lock-down verdict for a container.</param>
    /// <param name="check">Called with every item inside a searchable container.</param>
    /// <param name="report">Activity callbacks for the scavenger's status and log.</param>
    public void Update(World world, Func<Item, ContainerAccess> access, Action<Item> check, IContainerSearchReport report)
    {
        long now = Time.Ticks;
        ExpireConceals(world, now, report);

        if (now < _nextScan)
            return;

        _nextScan = now + SCAN_MS;
        bool opened = false;

        foreach (Item item in world.Items.Values)
        {
            if (item.OnGround && item.Distance <= REACH && IsSearchable(item))
                opened = Visit(item, now, opened, access, check, report);
        }
    }

    /// <summary>Distance to an item for reach checks: a contained item counts from the container on the ground.</summary>
    /// <param name="world">The world.</param>
    /// <param name="item">The item.</param>
    /// <returns>Tiles from the player, or <see cref="int.MaxValue"/> for an item carried by a mobile.</returns>
    public static int ReachDistance(World world, Item item)
    {
        if (item.OnGround)
            return item.Distance;

        Item? root = world.Items.Get(item.RootContainer);
        return root is { OnGround: true } ? root.Distance : int.MaxValue;
    }

    #endregion

    #region Private methods

    private static bool IsSearchable(Item item) =>
        !item.IsDestroyed
        && item.ItemData.IsContainer
        && !item.IsCorpse
        && Array.IndexOf(UI.Controls.GridItem.SpellbookGraphics, (int)item.Graphic) < 0;

    /// <summary>Opens the container if due (one per scan) and checks its known contents, recursing into sub-containers.</summary>
    private bool Visit(
        Item container,
        long now,
        bool opened,
        Func<Item, ContainerAccess> access,
        Action<Item> check,
        IContainerSearchReport report
    )
    {
        if (access(container) != ContainerAccess.Open)
            return opened;

        bool due = !_openedAt.TryGetValue(container.Serial, out long at) || now - at >= RESEARCH_MS;

        if (due && !opened)
        {
            Open(container.Serial, now);
            report.Searching(container);
            opened = true;
        }

        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var child = (Item)node;

            if (child.IsDestroyed)
                continue;

            check(child);

            if (IsSearchable(child))
                opened = Visit(child, now, opened, access, check, report);
        }

        return opened;
    }

    private void Open(uint serial, long now)
    {
        _openedAt[serial] = now;
        _concealUntil[serial] = now + CONCEAL_EXPIRY_MS;
        NearbyLootGump.ConcealNextOpen(serial);
        GameActions.DoubleClickQueued(serial);
    }

    private void ExpireConceals(World world, long now, IContainerSearchReport report)
    {
        if (_concealUntil.Count == 0)
            return;

        _expired.Clear();

        foreach (KeyValuePair<uint, long> pair in _concealUntil)
        {
            if (now >= pair.Value)
                _expired.Add(pair.Key);
        }

        foreach (uint serial in _expired)
        {
            _concealUntil.Remove(serial);

            // A conceal still pending means the server never sent the container: it refused the open (locked).
            if (NearbyLootGump.IsCorpseRequested(serial) && world.Items.Get(serial) is { } container)
                report.Refused(container);
        }
    }

    #endregion
}
