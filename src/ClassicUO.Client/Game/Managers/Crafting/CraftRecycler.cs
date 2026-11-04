#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Structs;
using ClassicUO.Game.UI.Gumps;

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>Why a recycling pass could not finish.</summary>
internal enum RecycleProblem
{
    None,

    /// <summary>Smelting needs an anvil and forge within 2 tiles.</summary>
    NeedForge,

    /// <summary>Tailoring recycling needs scissors in the backpack.</summary>
    NoScissors,

    /// <summary>Disposal is set to the trash barrel but none is in reach.</summary>
    NoTrashBarrel,

    /// <summary>The craft menu could not be opened to smelt.</summary>
    NoTool
}

/// <summary>
/// Gets rid of the Crafting agent's own products: smelts them through the craft menu, cuts them with
/// scissors, drops them at the player's feet or puts them in a trash barrel. Driven step by step by
/// <see cref="CraftingAgent"/>; main thread only.
/// </summary>
/// <remarks>
/// Only serials handed to <see cref="Begin"/> are touched, and each is re-checked right before use
/// (still loose in the backpack, not blessed or insured) because the player may have moved it.
/// </remarks>
internal sealed class CraftRecycler
{
    #region Public accessors

    /// <summary>What the recycler is doing, for the agent's status line.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Why the last pass stopped early, or <see cref="RecycleProblem.None"/>.</summary>
    public RecycleProblem Problem { get; private set; }

    #endregion

    #region Private members

    private enum Phase
    {
        Choose,
        AwaitMenu,
        AwaitCursor,
        AwaitResult,
        AwaitMove
    }

    private enum Method
    {
        Keep,
        Smelt,
        Scissors,
        Drop,
        Trash
    }

    private const long MENU_TIMEOUT_MS = 4_000;
    private const long CURSOR_TIMEOUT_MS = 3_000;
    private const long RESULT_TIMEOUT_MS = 4_000;
    private const long MOVE_TIMEOUT_MS = 4_000;
    private const long STEP_DELAY_MS = 300;
    private const uint GROUND = 0xFFFFFFFF;

    private Phase _phase;
    private Method _method;
    private CraftSkillInfo _info = CraftSkillInfo.Get(CraftSkill.Blacksmithy);
    private List<uint> _pending = new();
    private CraftResult _reply;
    private long _deadline;
    private long _nextStepTicks;
    private uint _workingSerial;
    private bool _menuRequested;

    #endregion

    #region Public methods

    /// <summary>True when this configuration does anything with crafted items.</summary>
    /// <param name="info">The craft skill.</param>
    /// <param name="config">The agent settings.</param>
    /// <returns>False for "keep" on a craft without (enabled) recycling.</returns>
    public static bool Disposes(CraftSkillInfo info, CraftingAgentConfig config) => PickMethod(info, config) != Method.Keep;

    /// <summary>Starts a pass over <paramref name="pending"/>, which it shrinks as items are handled.</summary>
    /// <param name="info">The craft skill.</param>
    /// <param name="config">The agent settings.</param>
    /// <param name="pending">The agent's own products still in the pack.</param>
    public void Begin(CraftSkillInfo info, CraftingAgentConfig config, List<uint> pending)
    {
        _info = info;
        _method = PickMethod(info, config);
        _pending = pending;
        _phase = Phase.Choose;
        _menuRequested = false;
        _nextStepTicks = 0;
        Problem = RecycleProblem.None;
        Status = TazLang.Get("craftagent_status_recycling");
    }

    /// <summary>Records a server reply that may concern the item being handled.</summary>
    /// <param name="result">The classified message or notice.</param>
    public void OnResult(CraftResult result)
    {
        if (result is CraftResult.Recycled or CraftResult.RecycleRefused or CraftResult.NeedStation)
            _reply = result;
    }

    /// <summary>Advances the pass by at most one action.</summary>
    /// <param name="world">The world.</param>
    /// <param name="craftWorld">The agent's world lookups.</param>
    /// <returns>True while still working; false once done or stopped by <see cref="Problem"/>.</returns>
    public bool Step(World world, CraftWorld craftWorld)
    {
        long now = Environment.TickCount64;

        if (now < _nextStepTicks)
            return true;

        switch (_phase)
        {
            case Phase.AwaitMenu: return StepAwaitMenu(world, craftWorld, now);
            case Phase.AwaitCursor: return StepAwaitCursor(world, craftWorld, now);
            case Phase.AwaitResult: return StepAwaitResult(world, now, craftWorld);
            case Phase.AwaitMove: return StepAwaitMove(world, now);
        }

        if (_method == Method.Keep || !IsReadyToAct(world))
            return _method != Method.Keep;

        PruneHandled(world);

        if (_pending.Count == 0)
            return false;

        return BeginNext(world, craftWorld, _pending[0], now);
    }

    #endregion

    #region Private methods

    private static Method PickMethod(CraftSkillInfo info, CraftingAgentConfig config)
    {
        if (config.Recycle && info.Recycle == CraftRecycleMethod.Smelt)
            return Method.Smelt;

        if (config.Recycle && info.Recycle == CraftRecycleMethod.Scissors)
            return Method.Scissors;

        return config.Disposal switch
        {
            CraftDisposal.DropAtFeet => Method.Drop,
            CraftDisposal.TrashBarrel => Method.Trash,
            _ => Method.Keep
        };
    }

    private static bool IsReadyToAct(World world) =>
        !world.TargetManager.IsTargeting
        && !GlobalActionCooldown.IsOnCooldown
        && !Client.Game.UO.GameCursor.ItemHold.Enabled;

    /// <summary>Drops serials the agent may no longer touch (gone, moved, blessed, insured).</summary>
    private void PruneHandled(World world)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (!CraftWorld.IsDisposable(world, _pending[i]))
                _pending.RemoveAt(i);
        }
    }

    private bool BeginNext(World world, CraftWorld craftWorld, uint serial, long now)
    {
        _workingSerial = serial;
        _reply = CraftResult.None;

        switch (_method)
        {
            case Method.Smelt: return BeginSmelt(world, craftWorld, now);
            case Method.Scissors: return BeginCut(world, now);
            case Method.Drop: return BeginMove(world, serial, GROUND, now);
            default: return BeginTrash(world, serial, now);
        }
    }

    private bool BeginSmelt(World world, CraftWorld craftWorld, long now)
    {
        Gump? gump = craftWorld.FindCraftGump(_info, out _);

        if (gump == null)
            return OpenMenu(world, now);

        _menuRequested = false;
        gump.OnButtonClick(CraftGumpLayout.SMELT_BUTTON);
        _phase = Phase.AwaitCursor;
        _deadline = now + CURSOR_TIMEOUT_MS;
        Status = TazLang.Get("craftagent_status_smelting");
        return true;
    }

    private bool OpenMenu(World world, long now)
    {
        Item? tool = _menuRequested ? null : CraftWorld.FindTool(world.Player, _info);

        if (tool == null)
            return Fail(RecycleProblem.NoTool);

        _menuRequested = true;
        GameActions.DoubleClick(world, tool.Serial, true, true);
        GlobalActionCooldown.BeginCooldown();
        _phase = Phase.AwaitMenu;
        _deadline = now + MENU_TIMEOUT_MS;
        return true;
    }

    private bool StepAwaitMenu(World world, CraftWorld craftWorld, long now)
    {
        if (craftWorld.FindCraftGump(_info, out _) != null)
        {
            _phase = Phase.Choose;
            _nextStepTicks = now + STEP_DELAY_MS;
            return true;
        }

        return now < _deadline || Fail(RecycleProblem.NoTool);
    }

    private bool BeginCut(World world, long now)
    {
        Item? scissors = CraftWorld.FindScissors(world.Player);

        if (scissors == null)
            return Fail(RecycleProblem.NoScissors);

        GameActions.DoubleClick(world, scissors.Serial, true, true);
        GlobalActionCooldown.BeginCooldown();
        _phase = Phase.AwaitCursor;
        _deadline = now + CURSOR_TIMEOUT_MS;
        Status = TazLang.Get("craftagent_status_cutting");
        return true;
    }

    private bool StepAwaitCursor(World world, CraftWorld craftWorld, long now)
    {
        // Smelting without an anvil and forge answers with a menu notice instead of a cursor.
        if (_reply == CraftResult.NeedStation || ReadMenuNotice(craftWorld) == CraftResult.NeedStation)
            return Fail(RecycleProblem.NeedForge);

        if (world.TargetManager.IsTargeting)
        {
            world.TargetManager.Target(_workingSerial);
            _phase = Phase.AwaitResult;
            _deadline = now + RESULT_TIMEOUT_MS;
            return true;
        }

        if (now >= _deadline)
            SkipCurrent(now);

        return true;
    }

    private bool StepAwaitResult(World world, long now, CraftWorld craftWorld)
    {
        if (_method == Method.Smelt)
        {
            // Smelting always answers with a fresh craft menu (the clicked one was closed) carrying the outcome.
            CraftResult notice = ReadMenuNotice(craftWorld);

            if (notice == CraftResult.NeedStation || _reply == CraftResult.NeedStation)
                return Fail(RecycleProblem.NeedForge);

            if (notice != CraftResult.None || craftWorld.FindCraftGump(_info, out _) != null || now >= _deadline)
                SkipCurrent(now);

            return true;
        }

        Item? item = world.Items.Get(_workingSerial);
        bool gone = item == null || item.IsDestroyed || item.Container != world.Player.Backpack?.Serial;

        if (gone || _reply == CraftResult.RecycleRefused || now >= _deadline)
            SkipCurrent(now);

        return true;
    }

    /// <summary>Classified notice of the open craft menu, or <see cref="CraftResult.None"/>.</summary>
    private CraftResult ReadMenuNotice(CraftWorld craftWorld)
    {
        craftWorld.FindCraftGump(_info, out CraftGumpLayout? layout);

        return layout == null ? CraftResult.None : CraftMessages.Classify(layout.NoticeCliloc);
    }

    private bool BeginTrash(World world, uint serial, long now)
    {
        Item? barrel = CraftWorld.FindTrashBarrel(world);

        return barrel == null ? Fail(RecycleProblem.NoTrashBarrel) : BeginMove(world, serial, barrel.Serial, now);
    }

    private bool BeginMove(World world, uint serial, uint destination, long now)
    {
        PlayerMobile player = world.Player;
        bool toGround = destination == GROUND;

        ObjectActionQueue.Instance.Enqueue(
            new MoveRequest(
                serial,
                destination,
                0,
                toGround ? player.X : 0xFFFF,
                toGround ? player.Y : 0xFFFF,
                toGround ? player.Z : 0
            ).ToObjectActionQueueItem(),
            ActionPriority.MoveItem
        );

        _phase = Phase.AwaitMove;
        _deadline = now + MOVE_TIMEOUT_MS;
        Status = TazLang.Get(toGround ? "craftagent_status_dropping" : "craftagent_status_trashing");
        return true;
    }

    private bool StepAwaitMove(World world, long now)
    {
        Item? item = world.Items.Get(_workingSerial);
        bool moved = item == null || item.IsDestroyed || item.Container != world.Player.Backpack?.Serial;

        if (moved || now >= _deadline)
            SkipCurrent(now);

        return true;
    }

    /// <summary>Done with the current item, whatever the outcome; a refused item is not retried.</summary>
    private void SkipCurrent(long now)
    {
        _pending.Remove(_workingSerial);
        _phase = Phase.Choose;
        _nextStepTicks = now + STEP_DELAY_MS;
    }

    private bool Fail(RecycleProblem problem)
    {
        Problem = problem;
        _phase = Phase.Choose;
        return false;
    }

    #endregion
}
