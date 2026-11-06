#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Structs;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>What one <see cref="HarvestLoadHandler"/> pass does.</summary>
internal enum HarvestLoadTask
{
    /// <summary>Smelt every ore pile in the backpack at a forge or the player's fire beetle.</summary>
    Smelt,

    /// <summary>Move resources from the backpack onto the player's pack animals until each is full.</summary>
    Offload,

    /// <summary>Move ore from the player's pack animals into the backpack.</summary>
    Pull
}

/// <summary>
/// Moves harvest resources around for <see cref="HarvestAgent"/>: smelts ore at a forge or the player's fire
/// beetle, moves resources onto the player's pack animals, or pulls ore back off them. Driven step by step;
/// main thread only.
/// </summary>
/// <remarks>
/// Ownership of beetles and pack animals is read from the mobile's rename flag, which the server only
/// sends to the owner in a status reply, so unknown candidates get a status request and count from
/// the next step on. A pack animal that leaves an item in the player's pack is treated as full until
/// <see cref="ResetAnimals"/>; ServUO's pack-animal backpack rejects overweight drops silently.
/// </remarks>
internal sealed class HarvestLoadHandler
{
    #region Public accessors

    /// <summary>What the handler is doing, for the agent's status line.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>True once this pass has smelted or moved anything.</summary>
    public bool DidWork { get; private set; }

    #endregion

    #region Private members

    private enum Phase
    {
        Choose,
        SmeltAwaitCursor,
        SmeltAwaitResult,
        AwaitMove
    }

    /// <summary>Something ore can be smelted on: a forge item/static or a fire beetle.</summary>
    private readonly record struct Smelter(uint Serial, ushort Graphic, ushort X, ushort Y, sbyte Z);

    private const long CURSOR_TIMEOUT_MS = 3_000;
    private const long SMELT_RESULT_TIMEOUT_MS = 3_000;
    private const long MOVE_TIMEOUT_MS = 4_000;
    private const long OPEN_PACK_DELAY_MS = 800;
    private const long STEP_DELAY_MS = 300;
    private const long STATUS_REQUEST_INTERVAL_MS = 3_000;
    private const int MAX_SMELT_ATTEMPTS = 3;

    private readonly HashSet<uint> _fullAnimals = new();
    private readonly HashSet<uint> _openedAnimals = new();
    private readonly HashSet<uint> _stuckPulls = new();
    private readonly Dictionary<uint, int> _smeltAttempts = new();
    private readonly Dictionary<uint, long> _statusRequested = new();
    private readonly List<Item> _scratchItems = new();

    private Phase _phase = Phase.Choose;
    private HarvestLoadTask _task;
    private HarvestMode _mode;
    private HarvestAgentConfig _config = new();
    private Func<bool>? _canTakeMore;
    private long _deadline;
    private long _nextStepTicks;
    private uint _workingSerial;
    private uint _workingFromContainer;
    private uint _workingAnimal;
    private Smelter _smelter;
    private HarvestMessage _smeltReply;

    #endregion

    #region Public methods

    /// <summary>Starts a fresh pass.</summary>
    /// <param name="mode">Mining or lumberjacking; decides what is smelted and moved.</param>
    /// <param name="config">The agent settings.</param>
    /// <param name="task">What this pass does.</param>
    /// <param name="canTakeMore">For <see cref="HarvestLoadTask.Pull"/>: false once the backpack should take no more.</param>
    public void Begin(HarvestMode mode, HarvestAgentConfig config, HarvestLoadTask task, Func<bool>? canTakeMore = null)
    {
        _mode = mode;
        _config = config;
        _task = task;
        _canTakeMore = canTakeMore;
        _phase = Phase.Choose;
        _openedAnimals.Clear();
        _stuckPulls.Clear();
        _nextStepTicks = 0;
        DidWork = false;
        Status = string.Empty;
    }

    /// <summary>Forgets which animals were full and which ore piles failed; called when the agent starts.</summary>
    public void ResetAnimals()
    {
        _fullAnimals.Clear();
        _smeltAttempts.Clear();
    }

    /// <summary>Forgets which animals were full, so the next offload tries them all again.</summary>
    public void ForgetFullAnimals() => _fullAnimals.Clear();

    /// <summary>Records a smelting reply from the server.</summary>
    /// <param name="message">The classified message.</param>
    public void OnMessage(HarvestMessage message)
    {
        if (_phase == Phase.SmeltAwaitCursor || _phase == Phase.SmeltAwaitResult)
        {
            if (message == HarvestMessage.SmeltDone || message == HarvestMessage.SmeltSkip)
                _smeltReply = message;
        }
    }

    /// <summary>True when a pass of <paramref name="task"/> would do anything right now.</summary>
    /// <param name="world">The world.</param>
    /// <param name="task">The task to check.</param>
    /// <returns>Whether there is work for that task.</returns>
    public bool HasWork(World world, HarvestLoadTask task) => task switch
    {
        HarvestLoadTask.Smelt => FindSmeltWork(world, out _, out _),
        HarvestLoadTask.Offload => FindOffloadWork(world, out _, out _, out _),
        _ => FindPullWork(world, out _, out _, out _)
    };

    /// <summary>True when a forge or the player's own fire beetle is within reach.</summary>
    /// <param name="world">The world.</param>
    /// <returns>Whether ore can be smelted here.</returns>
    public bool HasSmelter(World world) => FindSmelter(world, out _);

    /// <summary>True when a forge (not a fire beetle) is within reach.</summary>
    /// <param name="world">The world.</param>
    /// <returns>Whether a forge is in reach.</returns>
    public static bool HasForgeInReach(World world) => FindForge(world, out _);

    /// <summary>Advances the pass by at most one action.</summary>
    /// <param name="world">The world.</param>
    /// <returns>True while still working; false once nothing more can be done.</returns>
    public bool Step(World world)
    {
        long now = Environment.TickCount64;

        if (now < _nextStepTicks)
            return true;

        switch (_phase)
        {
            case Phase.SmeltAwaitCursor:
                StepSmeltCursor(world, now);
                return true;

            case Phase.SmeltAwaitResult:
                StepSmeltResult(now);
                return true;

            case Phase.AwaitMove:
                StepAwaitMove(world, now);
                return true;
        }

        if (!IsReadyToAct(world))
            return true;

        switch (_task)
        {
            case HarvestLoadTask.Smelt:
                if (FindSmeltWork(world, out Smelter smelter, out Item? ore) && ore != null)
                {
                    BeginSmelt(world, smelter, ore, now);
                    return true;
                }

                break;

            case HarvestLoadTask.Offload:
                if (FindOffloadWork(world, out Mobile? animal, out Item? pack, out Item? resource) && animal != null && pack != null && resource != null)
                {
                    BeginOffload(world, animal, pack, resource, now);
                    return true;
                }

                break;

            case HarvestLoadTask.Pull:
                if (_canTakeMore != null && !_canTakeMore())
                    return false;

                if (FindPullWork(world, out Mobile? source, out Item? sourcePack, out Item? pile) && source != null && sourcePack != null)
                {
                    BeginPull(world, source, pile, now);
                    return true;
                }

                break;
        }

        return false;
    }

    #endregion

    #region Private methods

    private static bool IsReadyToAct(World world) =>
        !world.TargetManager.IsTargeting
        && !GlobalActionCooldown.IsOnCooldown
        && !Client.Game.UO.GameCursor.ItemHold.Enabled;

    private void BeginSmelt(World world, Smelter smelter, Item ore, long now)
    {
        _smelter = smelter;
        _workingSerial = ore.Serial;
        _smeltReply = HarvestMessage.None;
        _smeltAttempts[ore.Serial] = _smeltAttempts.GetValueOrDefault(ore.Serial) + 1;
        DidWork = true;

        GameActions.DoubleClick(world, ore.Serial, true, true);
        GlobalActionCooldown.BeginCooldown();

        _phase = Phase.SmeltAwaitCursor;
        _deadline = now + CURSOR_TIMEOUT_MS;
        Status = TazLang.Get("harvestagent_status_smelting");
    }

    private void StepSmeltCursor(World world, long now)
    {
        if (_smeltReply == HarvestMessage.SmeltSkip)
        {
            FinishSmelt(now);
            return;
        }

        if (world.TargetManager.IsTargeting)
        {
            if (SerialHelper.IsValid(_smelter.Serial))
                world.TargetManager.Target(_smelter.Serial);
            else
                world.TargetManager.Target(_smelter.Graphic, _smelter.X, _smelter.Y, _smelter.Z);

            _phase = Phase.SmeltAwaitResult;
            _deadline = now + SMELT_RESULT_TIMEOUT_MS;
            return;
        }

        if (now >= _deadline)
            FinishSmelt(now);
    }

    private void StepSmeltResult(long now)
    {
        if (_smeltReply != HarvestMessage.None || now >= _deadline)
            FinishSmelt(now);
    }

    private void FinishSmelt(long now)
    {
        if (_smeltReply == HarvestMessage.SmeltSkip)
            _smeltAttempts[_workingSerial] = MAX_SMELT_ATTEMPTS;

        _phase = Phase.Choose;
        _nextStepTicks = now + STEP_DELAY_MS;
    }

    /// <summary>
    /// Pack horses and llamas open their pack on double-click; a giant beetle would be mounted instead, so its
    /// pack (sent with its equipment) is used without opening it.
    /// </summary>
    /// <returns>True when the pack was just opened and the caller should wait a moment.</returns>
    private bool OpenPackOnce(World world, Mobile animal, long now)
    {
        if (animal.Graphic == HarvestData.GIANT_BEETLE_BODY || !_openedAnimals.Add(animal.Serial))
            return false;

        GameActions.DoubleClick(world, animal.Serial, true, true);
        GlobalActionCooldown.BeginCooldown();
        _nextStepTicks = now + OPEN_PACK_DELAY_MS;
        Status = TazLang.Get("harvestagent_status_openingpack");
        return true;
    }

    private void BeginOffload(World world, Mobile animal, Item pack, Item resource, long now)
    {
        if (OpenPackOnce(world, animal, now))
            return;

        BeginMove(resource, pack.Serial, animal.Serial, now);
        Status = TazLang.Get("harvestagent_status_offloading");
    }

    private void BeginPull(World world, Mobile animal, Item? pile, long now)
    {
        // The pack's contents are only known once it has been opened.
        if (OpenPackOnce(world, animal, now) || pile == null)
            return;

        BeginMove(pile, world.Player.Backpack.Serial, animal.Serial, now);
        Status = TazLang.Get("harvestagent_status_pulling");
    }

    private void BeginMove(Item item, uint destination, uint animal, long now)
    {
        _workingSerial = item.Serial;
        _workingFromContainer = item.Container;
        _workingAnimal = animal;

        ObjectActionQueue.Instance.Enqueue(
            new MoveRequest(item.Serial, destination, item.Amount).ToObjectActionQueueItem(),
            ActionPriority.MoveItem
        );

        _phase = Phase.AwaitMove;
        _deadline = now + MOVE_TIMEOUT_MS;
    }

    private void StepAwaitMove(World world, long now)
    {
        Item? item = world.Items.Get(_workingSerial);
        bool moved = item == null || item.IsDestroyed || item.Container != _workingFromContainer;

        if (!moved && now < _deadline)
            return;

        if (moved)
            DidWork = true;
        else if (_task == HarvestLoadTask.Pull)
            _stuckPulls.Add(_workingSerial); // the server kept it on the animal; don't retry it this pass
        else
            _fullAnimals.Add(_workingAnimal); // still in our pack: on ServUO that means the animal is full

        _phase = Phase.Choose;
        _nextStepTicks = now + STEP_DELAY_MS;
    }

    private bool FindSmeltWork(World world, out Smelter smelter, out Item? ore)
    {
        smelter = default;
        ore = null;

        if (_mode != HarvestMode.Mining || !_config.SmeltOre || world.Player?.Backpack == null)
            return false;

        _scratchItems.Clear();
        HarvestTools.CollectInContainer(world.Player.Backpack, HarvestData.IsOre, _scratchItems);

        foreach (Item candidate in _scratchItems)
        {
            if (_smeltAttempts.GetValueOrDefault(candidate.Serial) < MAX_SMELT_ATTEMPTS)
            {
                ore = candidate;
                break;
            }
        }

        _scratchItems.Clear();

        return ore != null && FindSmelter(world, out smelter);
    }

    private bool FindSmelter(World world, out Smelter smelter)
    {
        smelter = default;

        foreach (Mobile mobile in world.Mobiles.Values)
        {
            if (mobile.Graphic == HarvestData.FIRE_BEETLE_BODY && mobile.Distance <= HarvestData.HELPER_RANGE && IsOwned(world, mobile))
            {
                smelter = new Smelter(mobile.Serial, mobile.Graphic, mobile.X, mobile.Y, mobile.Z);
                return true;
            }
        }

        return FindForge(world, out smelter);
    }

    private static bool FindForge(World world, out Smelter smelter)
    {
        smelter = default;
        PlayerMobile player = world.Player;

        foreach (Item item in world.Items.Values)
        {
            if (item.OnGround && !item.IsDestroyed && HarvestData.IsForge(item.Graphic) && item.Distance <= HarvestData.HELPER_RANGE)
            {
                smelter = new Smelter(item.Serial, item.Graphic, item.X, item.Y, item.Z);
                return true;
            }
        }

        for (int dy = -HarvestData.HELPER_RANGE; dy <= HarvestData.HELPER_RANGE; dy++)
        {
            for (int dx = -HarvestData.HELPER_RANGE; dx <= HarvestData.HELPER_RANGE; dx++)
            {
                if (TryFindStaticForge(world, player.X + dx, player.Y + dy, out smelter))
                    return true;
            }
        }

        return false;
    }

    private static bool TryFindStaticForge(World world, int x, int y, out Smelter smelter)
    {
        smelter = default;

        if (x < 0 || y < 0)
            return false;

        for (GameObject? obj = world.Map.GetTile(x, y, false); obj != null; obj = obj.TNext)
        {
            ushort graphic = obj switch
            {
                Static staticObject => staticObject.OriginalGraphic,
                Multi multi => multi.Graphic,
                _ => 0
            };

            if (graphic != 0 && HarvestData.IsForge(graphic))
            {
                smelter = new Smelter(0, graphic, (ushort)x, (ushort)y, obj.Z);
                return true;
            }
        }

        return false;
    }

    private bool FindOffloadWork(World world, out Mobile? animal, out Item? pack, out Item? resource)
    {
        animal = null;
        pack = null;
        resource = null;

        if (!_config.UsePackAnimal || world.Player?.Backpack == null)
            return false;

        resource = FindOffloadResource(world.Player.Backpack);

        if (resource == null)
            return false;

        foreach (Mobile mobile in world.Mobiles.Values)
        {
            if (!IsUsableAnimal(world, mobile) || _fullAnimals.Contains(mobile.Serial))
                continue;

            Item? animalPack = mobile.FindItemByLayer(Layer.Backpack);

            if (animalPack == null)
                continue;

            animal = mobile;
            pack = animalPack;
            return true;
        }

        return false;
    }

    /// <summary>Mining moves ore first (the heaviest), then ingots and granite; lumberjacking moves logs and boards.</summary>
    private Item? FindOffloadResource(Item backpack)
    {
        _scratchItems.Clear();

        if (_mode == HarvestMode.Mining)
        {
            HarvestTools.CollectInContainer(backpack, HarvestData.IsOre, _scratchItems);

            if (_scratchItems.Count == 0)
                HarvestTools.CollectInContainer(backpack, IsOtherMiningResource, _scratchItems);
        }
        else
        {
            HarvestTools.CollectInContainer(backpack, HarvestData.IsWood, _scratchItems);
        }

        Item? resource = _scratchItems.Count > 0 ? _scratchItems[0] : null;
        _scratchItems.Clear();
        return resource;
    }

    /// <summary>
    /// Finds the next ore pile on one of the player's pack animals. Returns the animal with a null pile when its
    /// pack has not been opened yet this pass, so <see cref="BeginPull"/> opens it first.
    /// </summary>
    private bool FindPullWork(World world, out Mobile? animal, out Item? pack, out Item? pile)
    {
        animal = null;
        pack = null;
        pile = null;

        if (_mode != HarvestMode.Mining || world.Player?.Backpack == null)
            return false;

        foreach (Mobile mobile in world.Mobiles.Values)
        {
            if (!IsUsableAnimal(world, mobile))
                continue;

            Item? animalPack = mobile.FindItemByLayer(Layer.Backpack);

            if (animalPack == null)
                continue;

            if (mobile.Graphic != HarvestData.GIANT_BEETLE_BODY && !_openedAnimals.Contains(mobile.Serial))
            {
                animal = mobile;
                pack = animalPack;
                return true;
            }

            _scratchItems.Clear();
            HarvestTools.CollectInContainer(animalPack, HarvestData.IsOre, _scratchItems);

            foreach (Item candidate in _scratchItems)
            {
                if (!_stuckPulls.Contains(candidate.Serial))
                {
                    pile = candidate;
                    break;
                }
            }

            _scratchItems.Clear();

            if (pile != null)
            {
                animal = mobile;
                pack = animalPack;
                return true;
            }
        }

        return false;
    }

    private bool IsUsableAnimal(World world, Mobile mobile) =>
        HarvestData.IsPackAnimal(mobile.Graphic) && mobile.Distance <= HarvestData.HELPER_RANGE && IsOwned(world, mobile);

    private static bool IsOtherMiningResource(ushort graphic) => HarvestData.IsIngot(graphic) || HarvestData.IsGranite(graphic);

    /// <summary>True for the player's own pet; asks the server for the status that reveals it otherwise.</summary>
    private bool IsOwned(World world, Mobile mobile)
    {
        if (mobile.IsRenamable)
            return true;

        long now = Environment.TickCount64;

        if (_statusRequested.TryGetValue(mobile.Serial, out long last) && now - last < STATUS_REQUEST_INTERVAL_MS)
            return false;

        _statusRequested[mobile.Serial] = now;
        GameActions.RequestMobileStatus(world, mobile.Serial);
        return false;
    }

    #endregion
}
