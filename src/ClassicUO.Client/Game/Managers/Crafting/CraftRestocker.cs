#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Structs;
using ClassicUO.Game.UI;

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>
/// Refills the backpack with a craft material from containers within reach: house storage, chests, crates and
/// bags inside them. Containers are opened quietly (no gump) so the client learns their contents, then matching
/// stacks are moved into the backpack. Driven step by step by <see cref="CraftingAgent"/>; main thread only.
/// </summary>
/// <remarks>
/// Access is left to the server: a container the player may not open simply sends no contents. Only plain
/// materials are taken (uncolored ingots, boards and leather; any cloth; the reagents), so colored ingots and
/// special wood kept for real crafting stay put.
/// </remarks>
internal sealed class CraftRestocker
{
    #region Public accessors

    /// <summary>What the restocker is doing, for the agent's status line.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Units moved into the backpack this pass.</summary>
    public int Moved { get; private set; }

    #endregion

    #region Private members

    private enum Phase
    {
        Opening,
        Settling,
        Moving,
        AwaitMove
    }

    private readonly record struct MaterialSpec(ushort[] Graphics, bool PlainOnly, float UnitWeight);

    private const int REACH = 2;
    private const int MAX_DEPTH = 3;
    private const long OPEN_SPACING_MS = 350;
    private const long SETTLE_MS = 1_200;
    private const long MOVE_TIMEOUT_MS = 2_000;

    private static readonly Dictionary<CraftMaterial, MaterialSpec> _specs = new()
    {
        [CraftMaterial.Ingots] = new([0x1BF2, 0x1BEF], true, 0.1f),
        [CraftMaterial.Boards] = new([0x1BD7], true, 0.1f),
        [CraftMaterial.Leather] = new([0x1081], true, 1f),
        [CraftMaterial.Cloth] = new([0x1766, 0x1767], false, 0.1f),
        [CraftMaterial.Nightshade] = new([0x0F88], false, 0.1f),
        [CraftMaterial.Bloodmoss] = new([0x0F7B], false, 0.1f),
        [CraftMaterial.MandrakeRoot] = new([0x0F86], false, 0.1f)
    };

    private readonly HashSet<uint> _opened = new();
    private readonly Queue<uint> _toOpen = new();
    private Phase _phase;
    private MaterialSpec _spec;
    private int _wanted;
    private int _depth;
    private int _weightMargin;
    private long _nextAction;
    private uint _moving;
    private int _movingAmountBefore;
    private long _deadline;

    #endregion

    #region Public methods

    /// <summary>True when the material is one the restocker knows how to find.</summary>
    /// <param name="material">The path step's material.</param>
    /// <returns>Whether <see cref="Begin"/> can handle it.</returns>
    public static bool Supports(CraftMaterial material) => _specs.ContainsKey(material);

    /// <summary>Starts a pass that brings up to <paramref name="units"/> of the material into the backpack.</summary>
    /// <param name="world">The world.</param>
    /// <param name="material">The material.</param>
    /// <param name="units">Units wanted.</param>
    /// <param name="weightMargin">Stones to keep free below the weight limit.</param>
    /// <returns>False when the material is not supported or no container is in reach.</returns>
    public bool Begin(World world, CraftMaterial material, int units, int weightMargin)
    {
        if (!_specs.TryGetValue(material, out _spec))
            return false;

        _opened.Clear();
        _toOpen.Clear();
        _wanted = units;
        _weightMargin = weightMargin;
        _depth = 0;
        Moved = 0;
        _nextAction = 0;

        foreach (Item item in world.Items.Values)
        {
            if (item.OnGround && item.Distance <= REACH && IsContainer(item))
                _toOpen.Enqueue(item.Serial);
        }

        if (_toOpen.Count == 0)
            return false;

        _phase = Phase.Opening;
        Status = TazLang.Get("craftagent_status_restock_search");
        return true;
    }

    /// <summary>Advances the pass by at most one action.</summary>
    /// <param name="world">The world.</param>
    /// <returns>True while still working; false once done (see <see cref="Moved"/>).</returns>
    public bool Step(World world)
    {
        long now = Environment.TickCount64;

        if (now < _nextAction)
            return true;

        switch (_phase)
        {
            case Phase.Opening: return StepOpening(now);
            case Phase.Settling: return StepSettling(world, now);
            case Phase.Moving: return StepMoving(world, now);
            default: return StepAwaitMove(world, now);
        }
    }

    #endregion

    #region Private methods

    private static bool IsContainer(Item item) => !item.IsDestroyed && item.ItemData.IsContainer && !item.IsCorpse;

    private bool StepOpening(long now)
    {
        if (!_toOpen.TryDequeue(out uint serial))
        {
            _phase = Phase.Settling;
            _nextAction = now + SETTLE_MS;
            return true;
        }

        if (_opened.Add(serial))
        {
            NearbyLootGump.ConcealNextOpen(serial);
            GameActions.DoubleClickQueued(serial);
            _nextAction = now + OPEN_SPACING_MS;
        }

        return true;
    }

    /// <summary>Contents have had time to arrive: drop unused conceals, then open sub-containers or start moving.</summary>
    private bool StepSettling(World world, long now)
    {
        foreach (uint serial in _opened)
            NearbyLootGump.IsCorpseRequested(serial);   // a conceal still pending means the open was refused

        if (++_depth < MAX_DEPTH)
        {
            foreach (uint serial in _opened)
            {
                if (world.Items.Get(serial) is { } container)
                    QueueSubContainers(container);
            }

            if (_toOpen.Count > 0)
            {
                _phase = Phase.Opening;
                return true;
            }
        }

        _phase = Phase.Moving;
        Status = TazLang.Get("craftagent_status_restock_moving");
        return true;
    }

    private void QueueSubContainers(Item container)
    {
        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var child = (Item)node;

            if (IsContainer(child) && !_opened.Contains(child.Serial))
                _toOpen.Enqueue(child.Serial);
        }
    }

    private bool StepMoving(World world, long now)
    {
        PlayerMobile player = world.Player;
        Item? backpack = player.Backpack;
        int room = RoomForUnits(player);
        int need = Math.Min(_wanted - Moved, room);

        if (backpack == null || need <= 0)
            return false;

        Item? stack = FindStack(world);

        if (stack == null)
            return false;

        int amount = Math.Min(need, Math.Max(1, (int)stack.Amount));
        _moving = stack.Serial;
        _movingAmountBefore = stack.Amount;

        ObjectActionQueue.Instance.Enqueue(
            new MoveRequest(stack.Serial, backpack.Serial, (ushort)amount).ToObjectActionQueueItem(),
            ActionPriority.MoveItem
        );

        Moved += amount;
        _phase = Phase.AwaitMove;
        _nextAction = now + 150;
        _deadline = now + MOVE_TIMEOUT_MS;
        return true;
    }

    private bool StepAwaitMove(World world, long now)
    {
        Item? item = world.Items.Get(_moving);
        bool done = item == null || item.IsDestroyed || item.Amount != _movingAmountBefore
                    || item.Container == world.Player.Backpack?.Serial;

        if (done || now >= _deadline)
            _phase = Phase.Moving;

        return true;
    }

    /// <summary>Units of this material that fit under the weight limit less the margin.</summary>
    private int RoomForUnits(PlayerMobile player)
    {
        if (player.WeightMax <= 0)
            return int.MaxValue;

        float freeStones = player.WeightMax - _weightMargin - player.Weight;
        return freeStones <= 0 ? 0 : (int)(freeStones / _spec.UnitWeight);
    }

    /// <summary>The largest matching stack in an opened container still in reach.</summary>
    private Item? FindStack(World world)
    {
        Item? best = null;

        foreach (uint serial in _opened)
        {
            Item? container = world.Items.Get(serial);

            if (container == null || ReachOf(world, container) > REACH)
                continue;

            for (LinkedObject? node = container.Items; node != null; node = node.Next)
            {
                var item = (Item)node;

                if (Matches(item) && (best == null || item.Amount > best.Amount))
                    best = item;
            }
        }

        return best;
    }

    private bool Matches(Item item) =>
        !item.IsDestroyed
        && Array.IndexOf(_spec.Graphics, item.Graphic) >= 0
        && (!_spec.PlainOnly || item.Hue == 0);

    private static int ReachOf(World world, Item container) =>
        ScavengerContainerSearch.ReachDistance(world, container);

    #endregion
}
