#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Hotkeys;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Game.UI.MyraWindows;

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>
/// Trains a craft skill along <see cref="CraftTrainingPath"/>: opens the server's craft menu with the
/// right tool, picks the path item for the current skill, repeats it with Make Last and recycles or
/// discards its own products every few crafts.
/// </summary>
/// <remarks>
/// Main thread only; <see cref="Update"/> runs every frame and returns immediately while stopped.
/// Outcomes come from the notice of the craft gump the server re-sends after each attempt, or from a
/// system message when no gump follows (worn-out tool); both are classified by <see cref="CraftMessages"/>.
/// <code>
/// Prepare ─(no menu)─► OpenMenu ─► AwaitMenu ─┐
///    │  ▲                                     │
///    │  └──────────────(group shown)◄─────────┘
///    ├─(item / Make Last)─► AwaitResult ─► Prepare
///    ├─(every N crafts, heavy, full)─► Recycling ─► Prepare / Paused
///    └─(heavy, nothing to recycle)─► Paused ─(lighter)─► Prepare
/// </code>
/// </remarks>
internal sealed class CraftingAgent
{
    #region Public accessors

    public static CraftingAgent Instance { get; } = new();

    /// <summary>True while the agent is running, including while paused for weight.</summary>
    public bool IsRunning => _state != AgentState.Stopped;

    /// <summary>Human-readable state for the agent tab.</summary>
    public string Status { get; private set; } = TazLang.Get("craftagent_status_stopped");

    /// <summary>Attempts made since the agent was last started.</summary>
    public int Attempts { get; private set; }

    /// <summary>Hotkey id of the start/stop toggle.</summary>
    public const string TOGGLE_HOTKEY_ID = "agent.crafting.toggle";

    #endregion

    #region Private members

    private enum AgentState
    {
        Stopped,
        Prepare,
        AwaitMenu,
        AwaitResult,
        Recycling,
        Paused
    }

    private const string CHAT_COMMAND = "craft";
    private const long MENU_TIMEOUT_MS = 4_000;
    private const long RESULT_TIMEOUT_MS = 12_000;
    private const long STEP_DELAY_MS = 250;
    private const long BUSY_RETRY_MS = 1_000;
    private const long WAIT_POLL_MS = 500;
    private const long PAUSE_POLL_MS = 1_500;
    private const int MAX_SILENT_ATTEMPTS = 3;
    private const int MAX_GROUP_CLICKS = 2;

    /// <summary>ServUO's default backpack item limit, less room for one product and its by-products.</summary>
    private const int PACK_ITEM_LIMIT = 120;

    private readonly Queue<CraftResult> _messages = new();
    private readonly HashSet<uint> _packBefore = new();
    private readonly List<uint> _products = new();
    private readonly CraftWorld _craftWorld = new();
    private readonly CraftRecycler _recycler = new();

    private AgentState _state = AgentState.Stopped;
    private CraftSkillInfo _info = CraftSkillInfo.Get(CraftSkill.Blacksmithy);
    private int _stepIndex = -1;
    private long _nextStepTicks;
    private long _deadline;
    private int _silentAttempts;
    private int _craftsSinceRecycle;
    private int _groupClicks;
    private bool _subscribed;

    /// <summary>The path item was crafted at least once, so Make Last repeats it.</summary>
    private bool _itemSelected;
    private bool _toolBroke;

    /// <summary>A recycling pass already ran to recover material for this shortage.</summary>
    private bool _recycledForMaterials;

    #endregion

    #region Ctor

    private CraftingAgent() { }

    #endregion

    #region Public methods

    /// <summary>Hooks messages, registers the hotkey and chat command. Call on game scene load.</summary>
    /// <param name="world">The world.</param>
    public void Load(World world)
    {
        if (!_subscribed)
        {
            EventSink.ClilocMessageReceived += OnClilocMessage;
            _subscribed = true;
        }

        HotKeys.Register(TOGGLE_HOTKEY_ID, TazLang.Get("craftagent_hotkey_toggle"), new HotkeyBinding(), "Agents", Toggle);
        world.CommandManager.Register(CHAT_COMMAND, OnChatCommand);
    }

    /// <summary>Stops the agent and unhooks messages. Call on game scene unload.</summary>
    public void Unload()
    {
        StopInternal(TazLang.Get("craftagent_status_stopped"), notify: false);

        if (_subscribed)
        {
            EventSink.ClilocMessageReceived -= OnClilocMessage;
            _subscribed = false;
        }
    }

    /// <summary>Starts (or restarts) training the profile's selected skill.</summary>
    public void Start()
    {
        CraftingAgentConfig? config = ProfileManager.CurrentProfile?.CraftingAgent;

        if (config == null || World.Instance?.Player == null)
            return;

        _info = CraftSkillInfo.Get(config.Skill);
        _messages.Clear();
        _products.Clear();
        _recycler.ForgetProducts();
        _stepIndex = -1;
        _itemSelected = false;
        _toolBroke = false;
        _recycledForMaterials = false;
        _silentAttempts = 0;
        _craftsSinceRecycle = 0;
        _groupClicks = 0;
        Attempts = 0;
        _nextStepTicks = 0;
        _state = AgentState.Prepare;

        SetStatus(TazLang.GetEx("craftagent_status_started", "Training {0}", [SkillName]));
        GameActions.Print(Status, Constants.HUE_SUCCESS);
    }

    /// <summary>Stops the agent.</summary>
    public void Stop() => StopInternal(TazLang.Get("craftagent_status_stopped"), notify: false, print: true);

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
        if (_state == AgentState.Stopped)
            return;

        World? world = World.Instance;

        if (world?.Player == null || world.Player.IsDead)
        {
            StopInternal(TazLang.Get("craftagent_status_stopped"), notify: false);
            return;
        }

        if (Environment.TickCount64 < _nextStepTicks)
            return;

        switch (_state)
        {
            case AgentState.Prepare: StepPrepare(world); break;
            case AgentState.AwaitMenu: StepAwaitMenu(); break;
            case AgentState.AwaitResult: StepAwaitResult(world); break;
            case AgentState.Recycling: StepRecycling(world); break;
            case AgentState.Paused: StepPaused(world); break;
        }
    }

    #endregion

    #region Private methods

    private static CraftingAgentConfig Config => ProfileManager.CurrentProfile?.CraftingAgent ?? new CraftingAgentConfig();

    private string SkillName => TazLang.Get(_info.NameKey);

    private void SetStatus(string status) => Status = status;

    private void Delay(long milliseconds) => _nextStepTicks = Environment.TickCount64 + milliseconds;

    private void ReturnToPrepare(long delayMs)
    {
        _state = AgentState.Prepare;
        Delay(delayMs);
    }

    /// <summary>Stops; with <paramref name="notify"/> the reason also goes to a popup, as the player must act.</summary>
    private void StopInternal(string reason, bool notify, bool print = false)
    {
        bool wasRunning = _state != AgentState.Stopped;
        _state = AgentState.Stopped;
        _messages.Clear();
        SetStatus(reason);

        if (!wasRunning)
            return;

        if (notify)
        {
            GameActions.Print(reason, Constants.HUE_ERROR);
            AgentPopup.Show(TazLang.Get("craftagent_popup_title"), reason);
        }
        else if (print)
            GameActions.Print(reason, 946);
    }

    private void StopWithKey(string key) => StopInternal(TazLang.GetEx(key, key, [SkillName]), notify: true);

    private void StepPrepare(World world)
    {
        PlayerMobile player = world.Player;
        CraftingAgentConfig config = Config;
        Skill skill = player.Skills[_info.SkillIndex];
        float target = config.TargetSkill > 0 ? config.TargetSkill : skill.Cap > 0 ? skill.Cap : CraftTrainingPath.MAX_SKILL;

        if (skill.Base >= target)
        {
            StopInternal(TazLang.GetEx("craftagent_stop_target", "{0} reached {1}", [SkillName, target.ToString("0.0")]), notify: true);
            return;
        }

        if (NeedsRoom(player, config) || DueForRecycling(config))
        {
            BeginRecyclingOrPause(world, config);
            return;
        }

        CraftPathStep step = SelectStep(skill.Value);

        if (step.ManaCost > 0 && player.Mana < step.ManaCost)
        {
            SetStatus(TazLang.Get("craftagent_status_mana"));
            Delay(WAIT_POLL_MS);
            return;
        }

        if (world.TargetManager.IsTargeting || GlobalActionCooldown.IsOnCooldown || Client.Game.UO.GameCursor.ItemHold.Enabled)
        {
            SetStatus(TazLang.Get("craftagent_status_waiting"));
            Delay(WAIT_POLL_MS);
            return;
        }

        Gump? menu = _craftWorld.FindCraftGump(_info, out CraftGumpLayout? layout);

        if (menu == null || layout == null)
            OpenMenu(world);
        else
            ClickNext(player, menu, layout, step);
    }

    /// <summary>The path row for the skill; moving to another row means selecting its item again.</summary>
    private CraftPathStep SelectStep(float skillValue)
    {
        int index = CraftTrainingPath.FindIndex(_info.Skill, skillValue);

        if (index != _stepIndex)
        {
            _stepIndex = index;
            _itemSelected = false;
            _groupClicks = 0;
        }

        return CraftTrainingPath.For(_info.Skill)[index];
    }

    private bool NeedsRoom(PlayerMobile player, CraftingAgentConfig config)
    {
        bool heavy = player.WeightMax > 0 && player.Weight + config.WeightMargin > player.WeightMax;

        return heavy || CraftWorld.CountBackpackItems(player) >= PACK_ITEM_LIMIT;
    }

    private bool DueForRecycling(CraftingAgentConfig config) =>
        _products.Count > 0 && _craftsSinceRecycle >= Math.Max(1, config.RecycleEvery);

    private void OpenMenu(World world)
    {
        Item? tool = CraftWorld.FindTool(world.Player, _info);

        if (tool == null)
        {
            StopWithKey(_toolBroke ? "craftagent_stop_toolbroke" : "craftagent_stop_notool");
            return;
        }

        GameActions.DoubleClick(world, tool.Serial, true, true);
        GlobalActionCooldown.BeginCooldown();
        AwaitMenu();
        SetStatus(TazLang.Get("craftagent_status_opening"));
    }

    private void AwaitMenu()
    {
        _messages.Clear();
        _state = AgentState.AwaitMenu;
        _deadline = Environment.TickCount64 + MENU_TIMEOUT_MS;
    }

    /// <summary>Make Last once the item was made; else the item's button, or its category first.</summary>
    private void ClickNext(PlayerMobile player, Gump menu, CraftGumpLayout layout, CraftPathStep step)
    {
        int button;

        if (_itemSelected)
            button = CraftGumpLayout.MAKE_LAST_BUTTON;
        else if (!layout.TryFindButton(step.ItemCliloc, CraftButtonKind.Item, out button))
        {
            // The category was opened and the item still is not listed: the server's menu differs from the path.
            if (_groupClicks >= MAX_GROUP_CLICKS || !layout.TryFindButton(step.GroupCliloc, CraftButtonKind.Group, out int groupButton))
            {
                StopWithKey("craftagent_stop_notinmenu");
                return;
            }

            _groupClicks++;
            menu.OnButtonClick(groupButton);
            AwaitMenu();
            SetStatus(TazLang.Get("craftagent_status_selecting"));
            return;
        }

        _groupClicks = 0;
        CraftWorld.SnapshotBackpack(player, _packBefore);
        _messages.Clear();
        menu.OnButtonClick(button);
        Attempts++;

        _state = AgentState.AwaitResult;
        _deadline = Environment.TickCount64 + RESULT_TIMEOUT_MS;
        SetStatus(TazLang.GetEx("craftagent_status_crafting", "Crafting {0}...", [ItemName(step)]));
    }

    private static string ItemName(CraftPathStep step) => Client.Game.UO.FileManager.Clilocs.GetString(step.ItemCliloc) ?? string.Empty;

    private void StepAwaitMenu()
    {
        if (_messages.TryDequeue(out CraftResult message) && HandleMenuMessage(message))
            return;

        if (_craftWorld.FindCraftGump(_info, out _) != null)
        {
            _silentAttempts = 0;
            ReturnToPrepare(STEP_DELAY_MS);
            return;
        }

        if (Environment.TickCount64 < _deadline)
            return;

        if (++_silentAttempts >= MAX_SILENT_ATTEMPTS)
            StopWithKey("craftagent_stop_nomenu");
        else
            ReturnToPrepare(BUSY_RETRY_MS);
    }

    /// <summary>Messages that can come back instead of a menu: busy, or a tool that just wore out.</summary>
    private bool HandleMenuMessage(CraftResult message)
    {
        switch (message)
        {
            case CraftResult.Busy:
                ReturnToPrepare(BUSY_RETRY_MS);
                return true;

            case CraftResult.ToolBroken:
                _toolBroke = true;
                ReturnToPrepare(STEP_DELAY_MS);
                return true;
        }

        return false;
    }

    private void StepAwaitResult(World world)
    {
        CraftResult result = CraftResult.None;
        bool toolBroke = false;

        // A worn-out tool sends the outcome and the breakage as messages instead of a menu.
        while (_messages.TryDequeue(out CraftResult message))
        {
            if (message == CraftResult.ToolBroken)
                toolBroke = true;
            else if (result == CraftResult.None)
                result = message;
        }

        _craftWorld.FindCraftGump(_info, out CraftGumpLayout? layout);

        if (layout != null && result == CraftResult.None)
            result = layout.NoticeLacksMana ? CraftResult.NeedMana : CraftMessages.Classify(layout.NoticeCliloc);

        _toolBroke |= toolBroke;

        if (result != CraftResult.None || layout != null || toolBroke)
        {
            _silentAttempts = 0;
            HandleResult(world, result);
            return;
        }

        if (Environment.TickCount64 < _deadline)
            return;

        _itemSelected = false;

        if (++_silentAttempts >= MAX_SILENT_ATTEMPTS)
            StopWithKey("craftagent_stop_noresponse");
        else
            ReturnToPrepare(BUSY_RETRY_MS);
    }

    private void HandleResult(World world, CraftResult result)
    {
        switch (result)
        {
            case CraftResult.Success:
            case CraftResult.Failed:
                OnAttemptFinished(world, result == CraftResult.Success);
                ReturnToPrepare(STEP_DELAY_MS);
                break;

            case CraftResult.NoMaterials:
            case CraftResult.PackFull:
                OnShortage(world, result);
                break;

            case CraftResult.NeedStation:
                StopWithKey("craftagent_stop_station");
                break;

            case CraftResult.MaterialSkill:
                StopWithKey("craftagent_stop_materialskill");
                break;

            case CraftResult.NoSkill:
                StopWithKey("craftagent_stop_noskill");
                break;

            case CraftResult.MissingSpell:
                StopWithKey("craftagent_stop_nospell");
                break;

            case CraftResult.NothingMadeYet:
                _itemSelected = false;
                ReturnToPrepare(STEP_DELAY_MS);
                break;

            case CraftResult.Busy:
            case CraftResult.NeedMana:
                ReturnToPrepare(BUSY_RETRY_MS);
                break;

            default:
                // A worn-out tool or a menu without a notice: just carry on.
                ReturnToPrepare(STEP_DELAY_MS);
                break;
        }
    }

    private void OnAttemptFinished(World world, bool success)
    {
        _itemSelected = true;
        _craftsSinceRecycle++;

        if (!success)
            return;

        _recycledForMaterials = false;

        if (CraftRecycler.Disposes(_info, Config))
            CraftWorld.CollectNewItems(world.Player, _packBefore, _products);
    }

    /// <summary>Out of materials or pack space: recycle once if that could help, else stop.</summary>
    private void OnShortage(World world, CraftResult result)
    {
        CraftingAgentConfig config = Config;
        bool recyclingHelps = _products.Count > 0
                              && CraftRecycler.Disposes(_info, config)
                              && (result == CraftResult.PackFull || (config.Recycle && _info.Recycle != CraftRecycleMethod.None));

        if (recyclingHelps && !_recycledForMaterials)
        {
            _recycledForMaterials = true;
            BeginRecycling(config);
            return;
        }

        if (result == CraftResult.PackFull)
        {
            StopWithKey("craftagent_stop_packfull");
            return;
        }

        CraftPathStep step = CraftTrainingPath.For(_info.Skill)[Math.Max(0, _stepIndex)];
        string material = TazLang.Get("craftagent_material_" + step.Material.ToString().ToLowerInvariant());
        StopInternal(TazLang.GetEx("craftagent_stop_nomaterials", "Out of {0}", [material, ItemName(step)]), notify: true);
    }

    private void BeginRecyclingOrPause(World world, CraftingAgentConfig config)
    {
        if (_products.Count > 0 && CraftRecycler.Disposes(_info, config))
        {
            BeginRecycling(config);
            return;
        }

        _craftsSinceRecycle = 0;

        if (NeedsRoom(world.Player, config))
            Pause();
        else
            ReturnToPrepare(STEP_DELAY_MS);
    }

    private void BeginRecycling(CraftingAgentConfig config)
    {
        _messages.Clear();
        _recycler.Begin(_info, config, _products, sweepMatching: true);
        _state = AgentState.Recycling;
        SetStatus(_recycler.Status);
    }

    private void StepRecycling(World world)
    {
        if (_recycler.Step(world, _craftWorld))
        {
            SetStatus(_recycler.Status);
            return;
        }

        _craftsSinceRecycle = 0;

        switch (_recycler.Problem)
        {
            case RecycleProblem.NeedForge:
                StopWithKey("craftagent_stop_recycle_forge");
                return;

            case RecycleProblem.NoScissors:
                StopWithKey("craftagent_stop_recycle_scissors");
                return;

            case RecycleProblem.NoTrashBarrel:
                StopWithKey("craftagent_stop_recycle_trash");
                return;

            case RecycleProblem.NoTool:
                StopWithKey(_toolBroke ? "craftagent_stop_toolbroke" : "craftagent_stop_notool");
                return;
        }

        if (NeedsRoom(world.Player, Config))
            Pause();
        else
            ReturnToPrepare(STEP_DELAY_MS);
    }

    private void Pause()
    {
        _state = AgentState.Paused;
        SetStatus(TazLang.Get("craftagent_status_paused_weight"));
        GameActions.Print(Status, Constants.HUE_ERROR);
        AgentPopup.Show(TazLang.Get("craftagent_popup_title"), Status);
        Delay(PAUSE_POLL_MS);
    }

    /// <summary>Waits for the player to lighten the pack; resumes on its own.</summary>
    private void StepPaused(World world)
    {
        Delay(PAUSE_POLL_MS);

        if (NeedsRoom(world.Player, Config))
            return;

        GameActions.Print(TazLang.Get("craftagent_status_resumed"), Constants.HUE_SUCCESS);
        ReturnToPrepare(STEP_DELAY_MS);
    }

    private void OnClilocMessage(object? sender, MessageEventArgs e)
    {
        if (_state == AgentState.Stopped || (e.Parent != null && e.Parent.Serial != World.Instance?.Player?.Serial))
            return;

        CraftResult result = CraftMessages.Classify((int)e.Cliloc);

        if (result == CraftResult.None)
            return;

        if (_state == AgentState.Recycling)
            _recycler.OnResult(result);
        else
            _messages.Enqueue(result);
    }

    private void OnChatCommand(string[] args)
    {
        CraftingAgentConfig? config = ProfileManager.CurrentProfile?.CraftingAgent;

        if (config == null)
            return;

        string argument = args.Length > 1 ? args[1] : string.Empty;

        if (string.Equals(argument, "stop", StringComparison.OrdinalIgnoreCase))
        {
            Stop();
            return;
        }

        if (Enum.TryParse(argument, true, out CraftSkill skill))
        {
            config.Skill = skill;
            Start();
            return;
        }

        Toggle();
    }

    #endregion
}
