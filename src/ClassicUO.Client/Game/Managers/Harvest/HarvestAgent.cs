#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Crafting;
using ClassicUO.Game.Managers.Hotkeys;
using ClassicUO.Game.UI.MyraWindows;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>
/// Automates mining and lumberjacking from where the player stands: keeps a tool ready, swings at
/// each harvestable tile in reach until the server reports its bank empty, and makes room in the
/// backpack before the player gets too heavy.
/// </summary>
/// <remarks>
/// Main thread only; <see cref="Update"/> runs every frame and returns immediately while stopped.
/// Server replies arrive through <see cref="EventSink"/> and are queued, then consumed by the state
/// machine on the next update, so one swing can report several things (ore, then a broken tool).
/// <para>Mining runs this cycle (lumberjacking skips the smelting steps):</para>
/// <code>
/// 1 SmeltFirst (ore in pack) -> 2 PullOre (ore off pack animals) -> 3 SmeltSecond
///   (steps 1-3 need a forge or fire beetle in reach; without one they are skipped on start)
/// 4 mine (PrepareTool -> AwaitCursor -> AwaitResult)
///     area empty -> WaitingForSpot (prompt, resumes when a minable tile is in reach)
///     pack full  -> Offloading onto each pack animal; room made -> keep mining
///                   animals full -> back to 1, or WaitingForSmelter (prompt) when no forge/beetle is near
/// </code>
/// </remarks>
internal sealed class HarvestAgent
{
    #region Public accessors

    public static HarvestAgent Instance { get; } = new();

    /// <summary>True while the agent is running, including while paused.</summary>
    public bool IsRunning => _state != AgentState.Stopped;

    /// <summary>Human-readable state for the agent tab.</summary>
    public string Status { get; private set; } = TazLang.Get("harvestagent_status_stopped");

    /// <summary>Hotkey id of the start/stop toggle.</summary>
    public const string TOGGLE_HOTKEY_ID = "agent.harvest.toggle";

    #endregion

    #region Private members

    private enum AgentState
    {
        Stopped,
        SmeltFirst,
        PullOre,
        SmeltSecond,
        PrepareTool,
        AwaitEquip,
        AwaitCursor,
        AwaitResult,
        Offloading,
        WaitingForSpot,
        WaitingForSmelter,
        Paused
    }

    private const string CHAT_COMMAND = "harvest";
    private const long EQUIP_TIMEOUT_MS = 4_000;
    private const long CURSOR_TIMEOUT_MS = 3_000;
    private const long RESULT_TIMEOUT_MS = 7_000;
    private const long SWING_DELAY_MS = 250;
    private const long BUSY_RETRY_MS = 1_000;
    private const long WAIT_POLL_MS = 500;
    private const long PAUSE_POLL_MS = 1_500;
    private const int MAX_TIMEOUTS_PER_SPOT = 3;
    private const long LATE_CURSOR_WINDOW_MS = 6_000;

    /// <summary>Pull/smelt rounds per cycle; each round empties as much ore off the animals as the pack takes.</summary>
    private const int MAX_PULL_ROUNDS = 10;

    /// <summary>ServUO's backpack holds 125 items; a new ore color takes one more slot.</summary>
    private const int PACK_ITEM_LIMIT = 122;

    private readonly Queue<HarvestMessage> _messages = new();
    private readonly HashSet<int> _skippedTiles = new();
    private readonly HarvestLoadHandler _loadHandler = new();

    private AgentState _state = AgentState.Stopped;
    private HarvestMode _mode;
    private long _nextStepTicks;
    private long _deadline;
    private long _lastToolUseTicks;
    private HarvestSpot? _spot;

    /// <summary>The tile the current swing targeted; outlives <see cref="_spot"/> when the player steps.</summary>
    private HarvestSpot? _targetedSpot;
    private uint _toolSerial;
    private int _equipAttempts;
    private int _spotTimeouts;
    private int _pullRounds;
    private bool _lastPullMoved;
    private bool _toolBroke;
    private bool _mustEquipCurrentTool;

    /// <summary>
    /// Set when a smelt/pull cycle ran and nothing has been mined since; a full pack then pauses instead of
    /// running the same cycle again.
    /// </summary>
    private bool _cycleSinceLastHarvest;

    /// <summary>A shovel that could not be equipped; it is used from the pack instead.</summary>
    private uint _unequippableShovel;
    /// <summary>Item count at which the server last refused a harvest for a full pack; 0 when it has not.</summary>
    private int _serverFullCount;
    private int _lastPlayerX = -1;
    private int _lastPlayerY = -1;
    private bool _subscribed;

    #endregion

    #region Ctor

    private HarvestAgent() { }

    #endregion

    #region Public methods

    /// <summary>Hooks messages, registers the hotkey and chat command. Call on game scene load.</summary>
    /// <param name="world">The world.</param>
    public void Load(World world)
    {
        if (!_subscribed)
        {
            EventSink.ClilocMessageReceived += OnClilocMessage;
            EventSink.MessageReceived += OnTextMessage;
            _subscribed = true;
        }

        HarvestDepletionTracker.Instance.Load();
        HotKeys.Register(TOGGLE_HOTKEY_ID, TazLang.Get("harvestagent_hotkey_toggle"), new HotkeyBinding(), "Agents", Toggle);
        world.CommandManager.Register(CHAT_COMMAND, OnChatCommand);
    }

    /// <summary>Stops the agent and unhooks messages. Call on game scene unload.</summary>
    public void Unload()
    {
        StopInternal(TazLang.Get("harvestagent_status_stopped"), print: false);

        if (_subscribed)
        {
            EventSink.ClilocMessageReceived -= OnClilocMessage;
            EventSink.MessageReceived -= OnTextMessage;
            _subscribed = false;
        }

        HarvestDepletionTracker.Instance.Unload();
    }

    /// <summary>Starts (or restarts, clearing any pause) with the profile's mode.</summary>
    public void Start()
    {
        HarvestAgentConfig? config = ProfileManager.CurrentProfile?.HarvestAgent;
        World? world = World.Instance;

        if (config == null || world?.Player == null)
            return;

        _mode = config.Mode;
        _messages.Clear();
        _skippedTiles.Clear();
        _loadHandler.ResetAnimals();
        _spot = null;
        _toolSerial = 0;
        _toolBroke = false;
        _serverFullCount = 0;
        _equipAttempts = 0;
        _mustEquipCurrentTool = false;
        _unequippableShovel = 0;
        _cycleSinceLastHarvest = false;
        _nextStepTicks = 0;

        string modeName = TazLang.Get(_mode == HarvestMode.Mining ? "harvestagent_mode_mining" : "harvestagent_mode_lumberjacking");
        SetStatus(TazLang.GetEx("harvestagent_status_started", "Harvesting ({0})", [modeName]));
        GameActions.Print(Status, Constants.HUE_SUCCESS);

        if (_mode == HarvestMode.Mining)
            BeginCycle(world, packFull: false);
        else
            ReturnToPrepare(0);
    }

    /// <summary>Stops the agent.</summary>
    public void Stop() => StopInternal(TazLang.Get("harvestagent_status_stopped"), print: true);

    /// <summary>Starts when stopped, stops when running.</summary>
    public void Toggle()
    {
        if (IsRunning)
            Stop();
        else
            Start();
    }

    /// <summary>Advances the agent. Call once per frame.</summary>
    public void Update()
    {
        HarvestDepletionTracker.Instance.Update();

        if (_state == AgentState.Stopped)
            return;

        World? world = World.Instance;

        if (world?.Player == null || world.Map == null || world.Player.IsDead)
        {
            StopInternal(TazLang.Get("harvestagent_status_stopped"), print: false);
            return;
        }

        TrackPlayerMovement(world.Player);

        if (Environment.TickCount64 < _nextStepTicks)
            return;

        switch (_state)
        {
            case AgentState.SmeltFirst: StepSmeltFirst(world); break;
            case AgentState.PullOre: StepPullOre(world); break;
            case AgentState.SmeltSecond: StepSmeltSecond(world); break;
            case AgentState.PrepareTool: StepPrepareTool(world); break;
            case AgentState.AwaitEquip: StepAwaitEquip(world); break;
            case AgentState.AwaitCursor: StepAwaitCursor(world); break;
            case AgentState.AwaitResult: StepAwaitResult(world); break;
            case AgentState.Offloading: StepOffloading(world); break;
            case AgentState.WaitingForSpot: StepWaitingForSpot(world); break;
            case AgentState.WaitingForSmelter: StepWaitingForSmelter(world); break;
            case AgentState.Paused: StepPaused(world); break;
        }
    }

    #endregion

    #region Private methods

    private HarvestAgentConfig Config => ProfileManager.CurrentProfile?.HarvestAgent ?? new HarvestAgentConfig();

    private void SetStatus(string status) => Status = status;

    private void Delay(long milliseconds) => _nextStepTicks = Environment.TickCount64 + milliseconds;

    private void StopInternal(string reason, bool print, bool isError = false)
    {
        bool wasRunning = _state != AgentState.Stopped;
        _state = AgentState.Stopped;
        _messages.Clear();
        SetStatus(reason);

        if (print && (wasRunning || isError))
            GameActions.Print(reason, isError ? Constants.HUE_ERROR : (ushort)946);

        // Error stops mean the player has to do something (move, fix a tool), so they also get a popup.
        if (print && isError)
            AgentPopup.Show(TazLang.Get("harvestagent_popup_title"), reason);
    }

    /// <summary>Pauses in <paramref name="state"/> and tells the player why, in chat and with a popup.</summary>
    private void PauseWithPrompt(AgentState state, string key)
    {
        _state = state;
        SetStatus(TazLang.Get(key));
        GameActions.Print(Status, Constants.HUE_ERROR);
        AgentPopup.Show(TazLang.Get("harvestagent_popup_title"), Status);
        Delay(PAUSE_POLL_MS);
    }

    private void Resume()
    {
        GameActions.Print(TazLang.Get("harvestagent_status_resumed"), Constants.HUE_SUCCESS);
        ReturnToPrepare(SWING_DELAY_MS);
    }

    /// <summary>Moving to a new stand re-opens tiles the server refused from the old one.</summary>
    private void TrackPlayerMovement(PlayerMobile player)
    {
        if (player.X == _lastPlayerX && player.Y == _lastPlayerY)
            return;

        _lastPlayerX = player.X;
        _lastPlayerY = player.Y;
        _skippedTiles.Clear();
        _spot = null;
    }

    /// <summary>True when the backpack is at (or the server said it is at) its item limit.</summary>
    private bool IsPackCrowded(PlayerMobile player)
    {
        int limit = _serverFullCount > 0 ? Math.Min(_serverFullCount, PACK_ITEM_LIMIT) : PACK_ITEM_LIMIT;

        return CraftWorld.CountBackpackItems(player) >= limit;
    }

    /// <summary>Too heavy or too many items: either way the pack must be lightened before the next swing.</summary>
    private bool NeedsUnload(PlayerMobile player)
    {
        if (IsPackCrowded(player))
            return true;

        if (player.WeightMax == 0)
            return false;

        HarvestAgentConfig config = Config;
        int margin = _mode == HarvestMode.Mining ? config.OreWeightMargin : config.LogWeightMargin;

        return player.Weight + margin > player.WeightMax;
    }

    /// <summary>Pulling stops once the pack is at its item limit or the player is at full weight.</summary>
    private bool CanTakeMoreOre()
    {
        PlayerMobile? player = World.Instance?.Player;

        return player != null && !IsPackCrowded(player) && (player.WeightMax == 0 || player.Weight < player.WeightMax);
    }

    /// <summary>
    /// Steps 1-3 of the mining cycle. Without a forge or fire beetle in reach the smelting steps are skipped on
    /// start, and with a full pack the agent waits for the player to reach one.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="packFull">True when the cycle runs because the pack and every pack animal are full.</param>
    private void BeginCycle(World world, bool packFull)
    {
        if (!Config.SmeltOre)
        {
            if (packFull)
                PauseWithPrompt(AgentState.Paused, "harvestagent_status_paused_full");
            else
                ReturnToPrepare(0);

            return;
        }

        if (!_loadHandler.HasSmelter(world))
        {
            if (packFull)
                PauseWithPrompt(AgentState.WaitingForSmelter, "harvestagent_status_needforge");
            else
                ReturnToPrepare(0);

            return;
        }

        _cycleSinceLastHarvest = true;
        _pullRounds = 0;
        _loadHandler.Begin(_mode, Config, HarvestLoadTask.Smelt);
        _state = AgentState.SmeltFirst;
        SetStatus(TazLang.Get("harvestagent_status_smelting"));
    }

    private bool StepLoad(World world)
    {
        if (!_loadHandler.Step(world))
            return false;

        if (!string.IsNullOrEmpty(_loadHandler.Status))
            SetStatus(_loadHandler.Status);

        return true;
    }

    private void StepSmeltFirst(World world)
    {
        if (StepLoad(world))
            return;

        BeginPull();
    }

    private void BeginPull()
    {
        if (!Config.UsePackAnimal || _pullRounds >= MAX_PULL_ROUNDS)
        {
            ReturnToPrepare(SWING_DELAY_MS);
            return;
        }

        _pullRounds++;
        _loadHandler.Begin(_mode, Config, HarvestLoadTask.Pull, CanTakeMoreOre);
        _state = AgentState.PullOre;
        SetStatus(TazLang.Get("harvestagent_status_pulling"));
    }

    private void StepPullOre(World world)
    {
        if (StepLoad(world))
            return;

        bool pulled = _loadHandler.DidWork;

        // Ore came off the animals, so they have room again.
        if (pulled)
            _loadHandler.ForgetFullAnimals();

        _loadHandler.Begin(_mode, Config, HarvestLoadTask.Smelt);
        _state = AgentState.SmeltSecond;
        _lastPullMoved = pulled;
        SetStatus(TazLang.Get("harvestagent_status_smelting"));
    }

    private void StepSmeltSecond(World world)
    {
        if (StepLoad(world))
            return;

        // A pack that filled up mid-pull leaves ore on the animals; smelting made room, so pull again.
        if (_lastPullMoved && _loadHandler.HasWork(world, HarvestLoadTask.Pull) && CanTakeMoreOre())
        {
            BeginPull();
            return;
        }

        ReturnToPrepare(SWING_DELAY_MS);
    }

    private void StepPrepareTool(World world)
    {
        PlayerMobile player = world.Player;

        if (NeedsUnload(player))
        {
            BeginOffload();
            return;
        }

        Item? tool = HarvestTools.FindEquippedTool(player, _mode) ?? HarvestTools.FindBackpackTool(player, _mode);

        if (tool == null)
        {
            string key = _toolBroke ? "harvestagent_stop_toolbroke" : "harvestagent_stop_notool";
            StopInternal(TazLang.Get(key), print: true, isError: true);
            return;
        }

        _toolBroke = false;
        bool equipped = HarvestTools.IsEquippedOn(player, tool);

        bool wantsEquip = (HarvestTools.ShouldEquip(tool, _mode) && tool.Serial != _unequippableShovel)
            || (_mustEquipCurrentTool && tool.Serial == _toolSerial);

        if (!equipped && wantsEquip)
        {
            BeginEquip(player, tool);
            return;
        }

        _toolSerial = tool.Serial;

        if (_spot == null)
        {
            if (!HarvestSpotFinder.TryFindNext(world, _mode, _skippedTiles, out HarvestSpot spot))
            {
                PauseWithPrompt(AgentState.WaitingForSpot, "harvestagent_status_areaempty");
                return;
            }

            _spot = spot;
            _spotTimeouts = 0;
        }

        Swing(world, tool);
    }

    private void Swing(World world, Item tool)
    {
        // A cursor that showed up after the last swing's cursor wait timed out is still ours to use.
        if (world.TargetManager.IsTargeting && Environment.TickCount64 - _lastToolUseTicks < LATE_CURSOR_WINDOW_MS)
        {
            _state = AgentState.AwaitCursor;
            _deadline = Environment.TickCount64 + CURSOR_TIMEOUT_MS;
            return;
        }

        if (world.TargetManager.IsTargeting || GlobalActionCooldown.IsOnCooldown || Client.Game.UO.GameCursor.ItemHold.Enabled)
        {
            SetStatus(TazLang.Get("harvestagent_status_waiting"));
            Delay(WAIT_POLL_MS);
            return;
        }

        _messages.Clear();
        GameActions.DoubleClick(world, tool.Serial, true, true);
        GlobalActionCooldown.BeginCooldown();
        _lastToolUseTicks = Environment.TickCount64;

        _state = AgentState.AwaitCursor;
        _deadline = Environment.TickCount64 + CURSOR_TIMEOUT_MS;
        SetStatus(TazLang.Get(_mode == HarvestMode.Mining ? "harvestagent_status_mining" : "harvestagent_status_chopping"));
    }

    private void BeginEquip(PlayerMobile player, Item tool)
    {
        _equipAttempts++;

        if (_equipAttempts > 2 || !HarvestTools.QueueEquip(player, tool, Config.ClearHandsForTool, _equipAttempts > 1))
        {
            // A shovel the server will not put in hand still digs from the pack on stock rules.
            if (HarvestData.IsShovel(tool.Graphic) && !_mustEquipCurrentTool && _unequippableShovel != tool.Serial)
            {
                _unequippableShovel = tool.Serial;
                _equipAttempts = 0;
                ReturnToPrepare(SWING_DELAY_MS);
                return;
            }

            StopInternal(TazLang.Get("harvestagent_stop_cantequip"), print: true, isError: true);
            return;
        }

        _toolSerial = tool.Serial;
        _state = AgentState.AwaitEquip;
        _deadline = Environment.TickCount64 + EQUIP_TIMEOUT_MS;
        SetStatus(TazLang.Get("harvestagent_status_equipping"));
    }

    private void StepAwaitEquip(World world)
    {
        Item? tool = world.Items.Get(_toolSerial);

        if (tool != null && HarvestTools.IsEquippedOn(world.Player, tool))
        {
            _equipAttempts = 0;
            _mustEquipCurrentTool = false;
            _state = AgentState.PrepareTool;
            return;
        }

        if (Environment.TickCount64 >= _deadline)
            _state = AgentState.PrepareTool; // tries again, clearing both hands, then gives up
    }

    private void StepAwaitCursor(World world)
    {
        if (HandleEarlyMessages())
            return;

        if (world.TargetManager.IsTargeting && _spot is HarvestSpot spot)
        {
            world.TargetManager.Target(spot.Graphic, spot.X, spot.Y, spot.Z);
            _targetedSpot = spot;
            _state = AgentState.AwaitResult;
            _deadline = Environment.TickCount64 + RESULT_TIMEOUT_MS;
            return;
        }

        if (Environment.TickCount64 >= _deadline)
            ReturnToPrepare(BUSY_RETRY_MS);
    }

    /// <summary>Replies that can arrive before any target cursor (busy, wrong hands, broken tool).</summary>
    private bool HandleEarlyMessages()
    {
        while (_messages.Count > 0)
        {
            HarvestMessage message = _messages.Dequeue();

            if (HandleCommonMessage(message))
                return true;
        }

        return false;
    }

    private void StepAwaitResult(World world)
    {
        // Drain everything: one swing can report a result and a broken tool together.
        bool finished = false;

        while (_messages.Count > 0 && _state != AgentState.Stopped)
        {
            HarvestMessage message = _messages.Dequeue();
            finished |= HandleCommonMessage(message) || HandleSwingResult(world, message);
        }

        if (finished || _state != AgentState.AwaitResult)
            return;

        if (Environment.TickCount64 < _deadline)
            return;

        // Stock ServUO sends nothing for some interrupted chops; treat a silent swing as a miss.
        if (++_spotTimeouts >= MAX_TIMEOUTS_PER_SPOT && _targetedSpot is HarvestSpot spot)
            SkipSpot(spot);

        ReturnToPrepare(SWING_DELAY_MS);
    }

    /// <summary>Handles replies that end a swing whatever stage it is in.</summary>
    private bool HandleCommonMessage(HarvestMessage message)
    {
        switch (message)
        {
            case HarvestMessage.Busy:
                ReturnToPrepare(BUSY_RETRY_MS);
                return true;

            case HarvestMessage.ToolBroken:
                _toolBroke = true;
                ReturnToPrepare(SWING_DELAY_MS);
                return true;

            case HarvestMessage.CannotHarvest:
                StopInternal(TazLang.Get("harvestagent_stop_cannotharvest"), print: true, isError: true);
                return true;

            case HarvestMessage.MustEquip:
                _mustEquipCurrentTool = true;
                ReturnToPrepare(SWING_DELAY_MS);
                return true;

            case HarvestMessage.HandsFull:
                if (!Config.ClearHandsForTool)
                {
                    StopInternal(TazLang.Get("harvestagent_stop_cantequip"), print: true, isError: true);
                    return true;
                }

                _mustEquipCurrentTool = true;
                ReturnToPrepare(SWING_DELAY_MS);
                return true;
        }

        return false;
    }

    private bool HandleSwingResult(World world, HarvestMessage message)
    {
        if (_targetedSpot is not HarvestSpot spot)
            return false;

        switch (message)
        {
            case HarvestMessage.Success:
                _cycleSinceLastHarvest = false;
                _spotTimeouts = 0;
                ReturnToPrepare(SWING_DELAY_MS);
                return true;

            case HarvestMessage.Fail:
                _spotTimeouts = 0;
                ReturnToPrepare(SWING_DELAY_MS);
                return true;

            case HarvestMessage.OreDepleted:
            case HarvestMessage.WoodDepleted:
                HarvestDepletionTracker.Instance.MarkDepleted(spot.Kind, world.Map.Index, spot.X, spot.Y);
                _spot = null;
                ReturnToPrepare(SWING_DELAY_MS);
                return true;

            case HarvestMessage.Unreachable:
            case HarvestMessage.InvalidTarget:
                SkipSpot(spot);
                ReturnToPrepare(SWING_DELAY_MS);
                return true;

            case HarvestMessage.PackFull:
                _serverFullCount = World.Instance?.Player is PlayerMobile player ? CraftWorld.CountBackpackItems(player) : 1;
                ReturnToPrepare(SWING_DELAY_MS);
                return true;
        }

        return false;
    }

    private void SkipSpot(HarvestSpot spot)
    {
        _skippedTiles.Add(HarvestSpotFinder.TileKey(spot.X, spot.Y));
        _spot = null;
    }

    private void ReturnToPrepare(long delayMs)
    {
        _state = AgentState.PrepareTool;
        Delay(delayMs);
    }

    private void BeginOffload()
    {
        _loadHandler.Begin(_mode, Config, HarvestLoadTask.Offload);
        _state = AgentState.Offloading;
        SetStatus(TazLang.Get("harvestagent_status_unloading"));
    }

    private void StepOffloading(World world)
    {
        if (StepLoad(world))
            return;

        if (!NeedsUnload(world.Player))
        {
            ReturnToPrepare(SWING_DELAY_MS);
            return;
        }

        bool crowded = IsPackCrowded(world.Player);

        if (_mode == HarvestMode.Lumberjacking)
        {
            StopInternal(TazLang.Get(crowded ? "harvestagent_stop_packfull" : "harvestagent_stop_overweight_lumber"), print: true, isError: true);
            return;
        }

        // Every animal is full. Run the smelt/pull cycle, unless one already ran without anything mined since:
        // repeating it would loop without making room.
        if (_cycleSinceLastHarvest)
        {
            PauseWithPrompt(AgentState.Paused, crowded ? "harvestagent_status_paused_full" : "harvestagent_status_paused_weight");
            return;
        }

        BeginCycle(world, packFull: true);
    }

    /// <summary>Area empty: waits, without the player restarting the agent, until a harvestable tile is in reach.</summary>
    private void StepWaitingForSpot(World world)
    {
        Delay(PAUSE_POLL_MS);

        if (HarvestSpotFinder.TryFindNext(world, _mode, _skippedTiles, out HarvestSpot spot))
        {
            _spot = spot;
            _spotTimeouts = 0;
            Resume();
        }
    }

    /// <summary>Pack and animals full with no fire beetle: waits until the player reaches a forge.</summary>
    private void StepWaitingForSmelter(World world)
    {
        Delay(PAUSE_POLL_MS);

        if (_loadHandler.HasSmelter(world))
        {
            GameActions.Print(TazLang.Get("harvestagent_status_resumed"), Constants.HUE_SUCCESS);
            BeginCycle(world, packFull: true);
        }
        else if (!NeedsUnload(world.Player))
        {
            Resume();
        }
    }

    /// <summary>Waits for a lighter or emptier pack, a pack animal with room, or ore and a smelter to run the cycle.</summary>
    private void StepPaused(World world)
    {
        Delay(PAUSE_POLL_MS);

        if (!NeedsUnload(world.Player))
        {
            Resume();
            return;
        }

        if (_loadHandler.HasWork(world, HarvestLoadTask.Offload))
        {
            GameActions.Print(TazLang.Get("harvestagent_status_resumed"), Constants.HUE_SUCCESS);
            BeginOffload();
            return;
        }

        if (_mode == HarvestMode.Mining && Config.SmeltOre && _loadHandler.HasWork(world, HarvestLoadTask.Smelt))
        {
            GameActions.Print(TazLang.Get("harvestagent_status_resumed"), Constants.HUE_SUCCESS);
            BeginCycle(world, packFull: true);
        }
    }

    private void OnClilocMessage(object? sender, MessageEventArgs e)
    {
        if (_state == AgentState.Stopped || !IsFromSystemOrPlayer(e))
            return;

        Enqueue(HarvestMessages.Classify(e.Cliloc));
    }

    private void OnTextMessage(object? sender, MessageEventArgs e)
    {
        if (_state == AgentState.Stopped || e.Cliloc != 0 || !IsFromSystemOrPlayer(e))
            return;

        Enqueue(HarvestMessages.Classify(e.Text));
    }

    private static bool IsFromSystemOrPlayer(MessageEventArgs e) =>
        e.Parent == null || e.Parent.Serial == World.Instance?.Player?.Serial;

    private void Enqueue(HarvestMessage message)
    {
        if (message == HarvestMessage.None)
            return;

        if (_state is AgentState.SmeltFirst or AgentState.PullOre or AgentState.SmeltSecond or AgentState.Offloading)
            _loadHandler.OnMessage(message);
        else
            _messages.Enqueue(message);
    }

    private void OnChatCommand(string[] args)
    {
        HarvestAgentConfig? config = ProfileManager.CurrentProfile?.HarvestAgent;
        string argument = args.Length > 1 ? args[1].ToLowerInvariant() : string.Empty;

        if (config == null)
            return;

        switch (argument)
        {
            case "mine":
            case "mining":
                config.Mode = HarvestMode.Mining;
                Start();
                break;

            case "lumber":
            case "chop":
            case "lumberjacking":
                config.Mode = HarvestMode.Lumberjacking;
                Start();
                break;

            case "stop":
                Stop();
                break;

            default:
                Toggle();
                break;
        }
    }

    #endregion
}
