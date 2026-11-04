#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>
/// Remembers resource banks the server reported as empty, from the Harvest agent and from manual
/// harvesting alike, until they would have respawned, and supplies the highlight hue to the land and
/// static renderers.
/// </summary>
/// <remarks>
/// Main thread only. Manual depletion is attributed to the client's last targeted tile, which is
/// where the server's "nothing here" reply refers to since it arrives right after the target. Entries
/// live in memory only; they outlast a relog but not a client restart.
/// </remarks>
internal sealed class HarvestDepletionTracker
{
    #region Public accessors

    public static HarvestDepletionTracker Instance { get; } = new();

    #endregion

    #region Private members

    private const long PRUNE_INTERVAL_MS = 5_000;

    /// <summary>Last-target tiles farther than this from the player are not trusted as the harvested spot.</summary>
    private const int MANUAL_TARGET_MAX_DISTANCE = HarvestData.HARVEST_RANGE + 1;

    /// <summary>Bank key to expiry (<see cref="Environment.TickCount64"/>).</summary>
    private readonly Dictionary<long, long> _depletedUntil = new();
    private readonly List<long> _expiredKeys = new();

    /// <summary>Mirrors the entry count so the renderers can skip all work with one static read.</summary>
    private static int _activeCount;

    private long _nextPruneTicks;
    private bool _subscribed;

    #endregion

    #region Ctor

    private HarvestDepletionTracker() { }

    #endregion

    #region Public methods

    /// <summary>Starts listening for depletion messages. Safe to call repeatedly.</summary>
    public void Load()
    {
        if (_subscribed)
            return;

        EventSink.ClilocMessageReceived += OnClilocMessage;
        _subscribed = true;
    }

    /// <summary>Stops listening. Remembered banks are kept until they expire.</summary>
    public void Unload()
    {
        if (!_subscribed)
            return;

        EventSink.ClilocMessageReceived -= OnClilocMessage;
        _subscribed = false;
    }

    /// <summary>Drops expired banks every few seconds. Call once per frame.</summary>
    public void Update()
    {
        if (_activeCount == 0)
            return;

        long now = Environment.TickCount64;

        if (now < _nextPruneTicks)
            return;

        _nextPruneTicks = now + PRUNE_INTERVAL_MS;

        foreach (KeyValuePair<long, long> pair in _depletedUntil)
        {
            if (pair.Value <= now)
                _expiredKeys.Add(pair.Key);
        }

        foreach (long key in _expiredKeys)
            _depletedUntil.Remove(key);

        _expiredKeys.Clear();
        _activeCount = _depletedUntil.Count;
    }

    /// <summary>Records the bank containing a tile as empty until its respawn time has passed.</summary>
    /// <param name="kind">Ore or wood bank.</param>
    /// <param name="map">Map index.</param>
    /// <param name="x">Tile X.</param>
    /// <param name="y">Tile Y.</param>
    public void MarkDepleted(HarvestResourceKind kind, int map, int x, int y)
    {
        TimeSpan respawn = kind == HarvestResourceKind.Ore ? HarvestData.OreRespawn : HarvestData.WoodRespawn;
        _depletedUntil[MakeKey(kind, map, x, y)] = Environment.TickCount64 + (long)respawn.TotalMilliseconds;
        _activeCount = _depletedUntil.Count;
    }

    /// <summary>True while the bank containing a tile is remembered as empty.</summary>
    /// <param name="kind">Ore or wood bank.</param>
    /// <param name="map">Map index.</param>
    /// <param name="x">Tile X.</param>
    /// <param name="y">Tile Y.</param>
    /// <returns>Whether the bank is empty and not yet respawned.</returns>
    public bool IsDepleted(HarvestResourceKind kind, int map, int x, int y) =>
        _activeCount != 0
        && _depletedUntil.TryGetValue(MakeKey(kind, map, x, y), out long until)
        && until > Environment.TickCount64;

    /// <summary>Forgets every remembered bank.</summary>
    public void Clear()
    {
        _depletedUntil.Clear();
        _activeCount = 0;
    }

    /// <summary>Highlight hue for a land tile, when it is mineable rock in an empty ore bank.</summary>
    /// <param name="land">The land tile being drawn.</param>
    /// <param name="hue">The highlight hue when the method returns true.</param>
    /// <returns>Whether to draw the tile with <paramref name="hue"/>.</returns>
    public static bool TryGetLandHue(Land land, out ushort hue)
    {
        hue = 0;

        if (_activeCount == 0 || !HarvestData.IsMineableLand(land.OriginalGraphic))
            return false;

        return TryGetHue(HarvestResourceKind.Ore, land, out hue);
    }

    /// <summary>Highlight hue for a static, when it is a tree or mineable rock in an empty bank.</summary>
    /// <param name="staticObject">The static being drawn.</param>
    /// <param name="hue">The highlight hue when the method returns true.</param>
    /// <returns>Whether to draw the static with <paramref name="hue"/>.</returns>
    public static bool TryGetStaticHue(Static staticObject, out ushort hue)
    {
        hue = 0;

        if (_activeCount == 0)
            return false;

        ushort graphic = staticObject.OriginalGraphic;

        if (HarvestData.IsTree(graphic))
            return TryGetHue(HarvestResourceKind.Wood, staticObject, out hue);

        if (HarvestData.IsMineableStatic(graphic))
            return TryGetHue(HarvestResourceKind.Ore, staticObject, out hue);

        return false;
    }

    #endregion

    #region Private methods

    private static bool TryGetHue(HarvestResourceKind kind, GameObject tile, out ushort hue)
    {
        hue = 0;
        HarvestAgentConfig? config = ProfileManager.CurrentProfile?.HarvestAgent;

        if (config == null || !config.HighlightDepleted || tile.World.Map == null)
            return false;

        if (!Instance.IsDepleted(kind, tile.World.Map.Index, tile.X, tile.Y))
            return false;

        hue = config.DepletedHue;
        return hue != 0;
    }

    /// <summary>Packs map, kind and bank cell into one key; bank cells stay well under 24 bits.</summary>
    private static long MakeKey(HarvestResourceKind kind, int map, int x, int y)
    {
        (int width, int height) = HarvestData.BankSize(kind);
        long bankX = x / width;
        long bankY = y / height;

        return ((long)(map & 0xFF) << 56) | ((long)kind << 48) | ((bankX & 0xFFFFFF) << 24) | (bankY & 0xFFFFFF);
    }

    private void OnClilocMessage(object? sender, MessageEventArgs e)
    {
        HarvestMessage message = HarvestMessages.Classify(e.Cliloc);

        if (message != HarvestMessage.OreDepleted && message != HarvestMessage.WoodDepleted)
            return;

        World? world = World.Instance;
        LastTargetInfo? target = world?.TargetManager?.LastTargetInfo;

        if (world?.Player == null || world.Map == null || target == null || !target.IsSet || target.IsEntity)
            return;

        int distance = Math.Max(Math.Abs(target.X - world.Player.X), Math.Abs(target.Y - world.Player.Y));

        if (distance > MANUAL_TARGET_MAX_DISTANCE)
            return;

        HarvestResourceKind kind = message == HarvestMessage.OreDepleted ? HarvestResourceKind.Ore : HarvestResourceKind.Wood;
        MarkDepleted(kind, world.Map.Index, target.X, target.Y);
    }

    #endregion
}
