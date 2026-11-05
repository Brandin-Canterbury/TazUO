#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Structs;
using ClassicUO.Game.UI;

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>
/// Refills the backpack with a craft material from containers up to <see cref="SEARCH_RANGE"/> tiles away: house
/// storage, chests, crates and the bags inside them. Walks to each container that is out of reach, opens it
/// quietly (no gump), moves matching stacks into the backpack, and finally walks back to where it started (the
/// forge or anvil spot). Driven step by step by <see cref="CraftingAgent"/>; main thread only.
/// </summary>
/// <remarks>
/// Access is left to the server: a container the player may not open simply sends no contents. Every variant of
/// the material is taken (colored ingots, all woods including logs, all leathers including hides); plain stacks
/// are taken first, and the server falls back to the next material when the chosen one runs out.
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
        NextContainer,
        Walking,
        Opening,
        Settling,
        Moving,
        AwaitMove,
        ReturnHome,
        Done
    }

    /// <summary>A graphic range of one material and the weight of one unit of it.</summary>
    private readonly record struct MaterialPart(ushort First, ushort Last, float UnitWeight);

    private const int SEARCH_RANGE = 6;
    private const int REACH = 2;
    private const int MAX_DEPTH = 3;
    private const long OPEN_SPACING_MS = 350;
    private const long SETTLE_MS = 1_200;
    private const long MOVE_TIMEOUT_MS = 2_000;
    private const long WALK_TIMEOUT_MS = 8_000;
    private const float LIGHTEST_UNIT = 0.1f;

    private static readonly Dictionary<CraftMaterial, MaterialPart[]> _materials = new()
    {
        [CraftMaterial.Ingots] = [new(0x1BE3, 0x1BF4, 0.1f)],
        [CraftMaterial.Boards] = [new(0x1BD7, 0x1BD7, 0.1f), new(0x1BDD, 0x1BE2, 2f)],
        [CraftMaterial.Leather] = [new(0x1081, 0x1082, 1f), new(0x1078, 0x1079, 5f)],
        [CraftMaterial.Cloth] = [new(0x1766, 0x1767, 0.1f)],
        [CraftMaterial.Nightshade] = [new(0x0F88, 0x0F88, 0.1f)],
        [CraftMaterial.Bloodmoss] = [new(0x0F7B, 0x0F7B, 0.1f)],
        [CraftMaterial.MandrakeRoot] = [new(0x0F86, 0x0F86, 0.1f)]
    };

    private readonly List<uint> _containers = new();
    private readonly HashSet<uint> _opened = new();
    private readonly Queue<uint> _toOpen = new();
    private MaterialPart[] _parts = [];
    private Phase _phase;
    private int _wanted;
    private int _weightMargin;
    private int _depth;
    private uint _current;
    private long _nextAction;
    private long _deadline;
    private uint _moving;
    private int _movingAmountBefore;
    private int _homeX;
    private int _homeY;
    private int _homeZ;
    private bool _homeWalkIssued;

    #endregion

    #region Public methods

    /// <summary>True when the material is one the restocker knows how to find.</summary>
    /// <param name="material">The path step's material.</param>
    /// <returns>Whether <see cref="Begin"/> can handle it.</returns>
    public static bool Supports(CraftMaterial material) => _materials.ContainsKey(material);

    /// <summary>Starts a pass that brings up to <paramref name="units"/> of the material into the backpack.</summary>
    /// <param name="world">The world.</param>
    /// <param name="material">The material.</param>
    /// <param name="units">Units wanted.</param>
    /// <param name="weightMargin">Stones to keep free below the weight limit.</param>
    /// <returns>False when the material is not supported or no container is within range.</returns>
    public bool Begin(World world, CraftMaterial material, int units, int weightMargin)
    {
        if (!_materials.TryGetValue(material, out MaterialPart[]? parts))
            return false;

        PlayerMobile player = world.Player;
        _parts = parts;
        _wanted = units;
        _weightMargin = weightMargin;
        _homeX = player.X;
        _homeY = player.Y;
        _homeZ = player.Z;
        _homeWalkIssued = false;
        _nextAction = 0;
        Moved = 0;
        _opened.Clear();
        _toOpen.Clear();
        _containers.Clear();

        foreach (Item item in world.Items.Values)
        {
            if (item.OnGround && item.Distance <= SEARCH_RANGE && IsContainer(item))
                _containers.Add(item.Serial);
        }

        if (_containers.Count == 0)
            return false;

        // Nearest first, so the walk stays short.
        _containers.Sort((a, b) => Distance(world, a).CompareTo(Distance(world, b)));
        _phase = Phase.NextContainer;
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
            case Phase.NextContainer: return StepNextContainer(world, now);
            case Phase.Walking: return StepWalking(world, now);
            case Phase.Opening: return StepOpening(now);
            case Phase.Settling: return StepSettling(world, now);
            case Phase.Moving: return StepMoving(world, now);
            case Phase.AwaitMove: return StepAwaitMove(world, now);
            case Phase.ReturnHome: return StepReturnHome(world, now);
            default: return false;
        }
    }

    #endregion

    #region Private methods

    private static bool IsContainer(Item item) => !item.IsDestroyed && item.ItemData.IsContainer && !item.IsCorpse;

    private static int Distance(World world, uint serial) => world.Items.Get(serial)?.Distance ?? int.MaxValue;

    private bool StillNeeded(PlayerMobile player) => Math.Min(_wanted - Moved, RoomForUnits(player, LIGHTEST_UNIT)) > 0;

    private bool StepNextContainer(World world, long now)
    {
        Item? container = null;

        while (_containers.Count > 0 && container == null && StillNeeded(world.Player))
        {
            container = world.Items.Get(_containers[0]);
            _containers.RemoveAt(0);

            if (container is { IsDestroyed: true })
                container = null;
        }

        if (container == null)
        {
            _phase = Phase.ReturnHome;
            return true;
        }

        _current = container.Serial;
        _depth = 0;
        _toOpen.Clear();
        _toOpen.Enqueue(container.Serial);

        if (container.Distance <= REACH)
        {
            _phase = Phase.Opening;
            return true;
        }

        Status = TazLang.Get("craftagent_status_restock_walking");
        world.Player.Pathfinder.WalkTo(container.X, container.Y, container.Z, 1);
        _phase = Phase.Walking;
        _deadline = now + WALK_TIMEOUT_MS;
        _nextAction = now + 300;
        return true;
    }

    private bool StepWalking(World world, long now)
    {
        Item? container = world.Items.Get(_current);

        if (container == null)
        {
            _phase = Phase.NextContainer;
            return true;
        }

        if (container.Distance <= REACH)
        {
            world.Player.Pathfinder.StopAutoWalk();
            _phase = Phase.Opening;
            return true;
        }

        // The path ended short of it, or took too long: skip this container.
        if (!world.Player.Pathfinder.AutoWalking || now >= _deadline)
            _phase = Phase.NextContainer;

        return true;
    }

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
            Status = TazLang.Get("craftagent_status_restock_search");
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

        if (++_depth < MAX_DEPTH && world.Items.Get(_current) is { } root)
        {
            QueueSubContainers(root);

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

            if (!IsContainer(child))
                continue;

            if (!_opened.Contains(child.Serial))
                _toOpen.Enqueue(child.Serial);
            else
                QueueSubContainers(child);
        }
    }

    private bool StepMoving(World world, long now)
    {
        PlayerMobile player = world.Player;
        Item? backpack = player.Backpack;
        Item? root = world.Items.Get(_current);

        if (backpack == null || root == null || root.Distance > REACH || _wanted - Moved <= 0)
        {
            _phase = Phase.NextContainer;
            return true;
        }

        Item? stack = FindStack(root, out float unitWeight);
        int need = stack == null ? 0 : Math.Min(_wanted - Moved, RoomForUnits(player, unitWeight));

        if (stack == null || need <= 0)
        {
            _phase = Phase.NextContainer;
            return true;
        }

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
        bool done = item == null
                    || item.IsDestroyed
                    || item.Amount != _movingAmountBefore
                    || item.Container == world.Player.Backpack?.Serial;

        if (done || now >= _deadline)
            _phase = Phase.Moving;

        return true;
    }

    private bool StepReturnHome(World world, long now)
    {
        PlayerMobile player = world.Player;

        if (player.X == _homeX && player.Y == _homeY)
        {
            _phase = Phase.Done;
            return false;
        }

        if (!_homeWalkIssued)
        {
            _homeWalkIssued = true;
            Status = TazLang.Get("craftagent_status_restock_returning");
            player.Pathfinder.WalkTo(_homeX, _homeY, _homeZ, 0);
            _deadline = now + WALK_TIMEOUT_MS;
            _nextAction = now + 300;
            return true;
        }

        if (!player.Pathfinder.AutoWalking || now >= _deadline)
        {
            _phase = Phase.Done;
            return false;
        }

        return true;
    }

    /// <summary>Units of a material weighing <paramref name="unitWeight"/> that fit under the limit less the margin.</summary>
    private int RoomForUnits(PlayerMobile player, float unitWeight)
    {
        if (player.WeightMax <= 0)
            return int.MaxValue;

        float freeStones = player.WeightMax - _weightMargin - player.Weight;
        return freeStones <= 0 ? 0 : (int)(freeStones / unitWeight);
    }

    /// <summary>Best matching stack in the container tree: plain (uncolored) stacks first, then the largest.</summary>
    private Item? FindStack(Item root, out float unitWeight)
    {
        Item? best = null;
        unitWeight = LIGHTEST_UNIT;
        FindStackIn(root, ref best, ref unitWeight);
        return best;
    }

    private void FindStackIn(Item container, ref Item? best, ref float unitWeight)
    {
        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (item.IsDestroyed)
                continue;

            if (TryMatch(item, out float weight))
            {
                if (IsBetter(item, best))
                {
                    best = item;
                    unitWeight = weight;
                }
            }
            else if (IsContainer(item) && _opened.Contains(item.Serial))
                FindStackIn(item, ref best, ref unitWeight);
        }
    }

    private static bool IsBetter(Item candidate, Item? best)
    {
        if (best == null)
            return true;

        bool candidatePlain = candidate.Hue == 0;
        bool bestPlain = best.Hue == 0;

        if (candidatePlain != bestPlain)
            return candidatePlain;

        return candidate.Amount > best.Amount;
    }

    private bool TryMatch(Item item, out float unitWeight)
    {
        foreach (MaterialPart part in _parts)
        {
            if (item.Graphic >= part.First && item.Graphic <= part.Last)
            {
                unitWeight = part.UnitWeight;
                return true;
            }
        }

        unitWeight = 0;
        return false;
    }

    #endregion
}
