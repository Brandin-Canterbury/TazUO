#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Structs;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>
/// Makes room in the player's backpack while harvesting: smelts ore at a forge or the player's fire
/// beetle, then moves resources onto the player's pack animal. Driven step by step by
/// <see cref="HarvestAgent"/>; main thread only.
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
        OffloadAwaitMove
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
    private readonly Dictionary<uint, int> _smeltAttempts = new();
    private readonly Dictionary<uint, long> _statusRequested = new();
    private readonly List<Item> _scratchItems = new();

    private Phase _phase = Phase.Choose;
    private HarvestMode _mode;
    private HarvestAgentConfig _config = new();
    private long _deadline;
    private long _nextStepTicks;
    private uint _workingSerial;
    private uint _workingFromContainer;
    private uint _workingAnimal;
    private Smelter _smelter;
    private HarvestMessage _smeltReply;
    private bool _smeltOnly;

    #endregion

    #region Public methods

    /// <summary>Starts a fresh unload pass.</summary>
    /// <param name="mode">Mining or lumberjacking; decides what is smelted and moved.</param>
    /// <param name="config">The agent settings.</param>
    /// <param name="smeltOnly">Only smelt ore; leave the pack animal alone.</param>
    public void Begin(HarvestMode mode, HarvestAgentConfig config, bool smeltOnly = false)
    {
        _mode = mode;
        _config = config;
        _smeltOnly = smeltOnly;
        _phase = Phase.Choose;
        _openedAnimals.Clear();
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

    /// <summary>True when there is something to smelt or somewhere to move resources right now.</summary>
    /// <param name="world">The world.</param>
    /// <returns>Whether a <see cref="Step"/> pass would do anything.</returns>
    public bool CanMakeRoom(World world) => FindSmeltWork(world, out _, out _) || FindOffloadWork(world, out _, out _, out _);

    /// <summary>True when a forge (not a fire beetle) is within reach, as smelting items needs.</summary>
    /// <param name="world">The world.</param>
    /// <returns>Whether a forge is in reach.</returns>
    public static bool HasForgeInReach(World world) => FindForge(world, out _);

    /// <summary>Advances the unload pass by at most one action.</summary>
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
                StepSmeltResult(world, now);
                return true;

            case Phase.OffloadAwaitMove:
                StepOffloadMove(world, now);
                return true;
        }

        if (!IsReadyToAct(world))
            return true;

        if (FindSmeltWork(world, out Smelter smelter, out Item? ore) && ore != null)
        {
            BeginSmelt(world, smelter, ore, now);
            return true;
        }

        if (!_smeltOnly && FindOffloadWork(world, out Mobile? animal, out Item? pack, out Item? resource) && animal != null && pack != null && resource != null)
        {
            BeginOffload(world, animal, pack, resource, now);
            return true;
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

    private void StepSmeltResult(World world, long now)
    {
        if (_smeltReply == HarvestMessage.SmeltSkip)
            _smeltAttempts[_workingSerial] = MAX_SMELT_ATTEMPTS;

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

    private void BeginOffload(World world, Mobile animal, Item pack, Item resource, long now)
    {
        // Pack horses and llamas open their pack on double-click; a giant beetle would be mounted
        // instead, so its pack (sent with its equipment) is used without opening it.
        if (animal.Graphic != HarvestData.GIANT_BEETLE_BODY && _openedAnimals.Add(animal.Serial))
        {
            GameActions.DoubleClick(world, animal.Serial, true, true);
            GlobalActionCooldown.BeginCooldown();
            _nextStepTicks = now + OPEN_PACK_DELAY_MS;
            Status = TazLang.Get("harvestagent_status_openingpack");
            return;
        }

        _workingSerial = resource.Serial;
        _workingFromContainer = resource.Container;
        _workingAnimal = animal.Serial;

        ObjectActionQueue.Instance.Enqueue(
            new MoveRequest(resource.Serial, pack.Serial, resource.Amount).ToObjectActionQueueItem(),
            ActionPriority.MoveItem
        );

        _phase = Phase.OffloadAwaitMove;
        _deadline = now + MOVE_TIMEOUT_MS;
        Status = TazLang.Get("harvestagent_status_offloading");
    }

    private void StepOffloadMove(World world, long now)
    {
        Item? item = world.Items.Get(_workingSerial);
        bool moved = item == null || item.IsDestroyed || item.Container != _workingFromContainer;

        if (!moved && now < _deadline)
            return;

        // Still in our pack after the timeout: the animal refused it, which on ServUO means it is full.
        if (moved)
            DidWork = true;
        else
            _fullAnimals.Add(_workingAnimal);

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

        _scratchItems.Clear();
        Func<ushort, bool> match = _mode == HarvestMode.Mining ? IsMiningResource : HarvestData.IsWood;
        HarvestTools.CollectInContainer(world.Player.Backpack, match, _scratchItems);
        resource = _scratchItems.Count > 0 ? _scratchItems[0] : null;
        _scratchItems.Clear();

        if (resource == null)
            return false;

        foreach (Mobile mobile in world.Mobiles.Values)
        {
            if (!HarvestData.IsPackAnimal(mobile.Graphic) || mobile.Distance > HarvestData.HELPER_RANGE)
                continue;

            if (_fullAnimals.Contains(mobile.Serial) || !IsOwned(world, mobile))
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

    private static bool IsMiningResource(ushort graphic) =>
        HarvestData.IsOre(graphic) || HarvestData.IsIngot(graphic) || HarvestData.IsGranite(graphic);

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
