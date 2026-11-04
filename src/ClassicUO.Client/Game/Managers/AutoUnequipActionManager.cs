using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.SpellVisualRange;
using ClassicUO.Game.Managers.Structs;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;
using Lock = System.Threading.Lock;

namespace ClassicUO.Game.Managers;

public sealed partial class AutoUnequipActionManager : IDisposable
{
    private struct Armament(uint serial, Layer layer)
    {
        public readonly uint Serial = serial;
        public readonly Layer Layer = layer;
    }

    /// <summary>
    ///     A queued action: a spell cast when <see cref="UseItem" /> is null, an item use (potion) otherwise.
    /// </summary>
    private readonly record struct InterceptRequest(int SpellIndex, Action UseItem)
    {
        public bool IsSpellCast => UseItem == null;
    }

    /// <summary>
    ///     A cast the consumer sent and is waiting on, with the armaments to put back once it is over.
    /// </summary>
    private sealed record CastInProgress(List<Armament> Arms, SpellCastCompletionTracker Tracker);

    private const int InterceptSettleMs = 50;
    private const int PostBatchDelayMs = 200;
    private const int CastCompletionPollMs = 50;

    public static AutoUnequipActionManager Instance { get; private set; }

    private readonly World _world;
    private readonly CancellationTokenSource _cTokenSource = new();
    private readonly Task _interceptConsumerCompletion;

    private readonly Channel<InterceptRequest> _flushChannel = Channel.CreateUnbounded<InterceptRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
    );

    // Set while a re-equip is owed for a cast that went out. Any cast in that window must go through the
    // consumer too, otherwise the owed re-equip lands in the middle of it and disturbs it.
    private volatile bool _reEquipOwed;

    #region Dispose

    private readonly Lock _disposalLock = new();
    private bool _disposed;

    #endregion

    #region Accessors

    /// <summary>
    ///     Checks whether the manager is in a valid state and can intercept calls.
    ///     Note that this does not consider the per-action profile toggles.
    /// </summary>
    /// <returns>True if the manager is ready to intercept, false otherwise</returns>
    private bool CanIntercept => !_disposed && IsPlayerBackpackAvailable;

    /// <summary>
    ///     Whether spell casts may be intercepted, per the profile's settings and the manager's state
    /// </summary>
    private bool IsSpellCastInterceptionEnabled => CanIntercept && ProfileManager.CurrentProfile?.AutoUnequipForSpellCasting == true;

    /// <summary>
    ///     Whether potion uses may be intercepted, per the profile's settings and the manager's state
    /// </summary>
    private bool IsPotionInterceptionEnabled => CanIntercept && ProfileManager.CurrentProfile?.AutoUnequipForPotions == true;

    /// <summary>
    ///     Determines whether the player's backpack is available
    /// </summary>
    private bool IsPlayerBackpackAvailable => _world?.Player?.Backpack != null;

    #endregion

    public AutoUnequipActionManager(World world)
    {
        _world = world;
        Instance = this;
        _interceptConsumerCompletion = Task.Run(ActionConsumer).ContinueWith(result =>
        {
            if (result.IsCanceled || result.Exception == null)
                return;

            Log.Warn("Auto-Unequip manager task processor faulted:");
            Log.Warn(result.Exception.ToString());
            // We could restart but honestly if it faulted, we're probably better off leaving it disabled.
            MainThreadQueue.InvokeOnMainThread(() =>
            {
                if (_world == null)
                    return;
                try
                {
                    GameActions.Print(_world, TazLang.Get("auto_unequip_failed_stopped"), Constants.HUE_ERROR);
                }
                catch (Exception e)
                {
                    Log.Error($"Failed to notify user of auto-equip agent failure: {e}");
                }
            });
        });
    }

    #region Public Methods

    /// <summary>
    ///     Attempts to intercept a spell cast
    /// </summary>
    /// <param name="spellIndex">The spell being intercepted</param>
    /// <returns>True if the spell was intercepted, false otherwise</returns>
    public bool TryInterceptSpellCast(int spellIndex) =>
        ShouldInterceptCast(spellIndex) && _flushChannel.Writer.TryWrite(new InterceptRequest(spellIndex, null));

    /// <summary>
    ///     Attempts to intercept a double click
    /// </summary>
    /// <param name="itemSerial">The serial of the item being double-clicked</param>
    /// <param name="sendDoubleClickDelegate">The original 'send double click' function</param>
    /// <returns>True if the click was intercepted, false otherwise</returns>
    public bool TryInterceptDoubleClick(uint itemSerial, Action<uint> sendDoubleClickDelegate) =>
        ShouldInterceptDblClick(itemSerial, sendDoubleClickDelegate)
        && _flushChannel.Writer.TryWrite(new InterceptRequest(-1, () => sendDoubleClickDelegate(itemSerial)));

    /// <summary>
    ///     Disposes the manager instance.
    ///     Note this method may take a few milliseconds to return.
    /// </summary>
    public void Dispose()
    {
        lock (_disposalLock)
        {
            if (_disposed)
                return;

            // First, issue a cancel. The token propagates to the wait for the main thread as well
            _cTokenSource.Cancel();

            // Then, close the producers
            _flushChannel.Writer.Complete();

            // Wait for the consumer to return
            Task.WaitAll(_interceptConsumerCompletion);
            // Finally, dispose of the rest
            _cTokenSource.Dispose();
            Instance = null;

            _disposed = true;
        }
    }

    #endregion

    #region PrivateMethods

    /// <summary>
    ///     Determines whether a spell cast should be intercepted
    /// </summary>
    /// <param name="spellIndex">The intercepted spell's index</param>
    /// <returns>True if the spell should be intercepted, false otherwise</returns>
    private bool ShouldInterceptCast(int spellIndex)
    {
        if (!IsSpellCastInterceptionEnabled)
            return false;

        if (_reEquipOwed)
            return true;

        if (spellIndex is >= 100 and <= 678 or >= 700)
            return false;

        List<Armament> arms = GetArmingState();
        if (arms.Count <= 0)
            return false;

        foreach (Armament arm in arms)
        {
            if (!_world.OPL.TryGetNameAndData(arm.Serial, out string _, out string data))
                return true; // If missing from OPL, err on the side of caution and intercept

            if (string.IsNullOrWhiteSpace(data) || !IsSpellChannelling().IsMatch(data))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Determines whether a Double-Click should be intercepted
    /// </summary>
    /// <param name="serial">The clicked target's serial</param>
    /// <param name="sendDoubleClickDelegate">The original 'send double click' function</param>
    /// <returns>True if the event should be intercepted, false otherwise</returns>
    private bool ShouldInterceptDblClick(uint serial, Action<uint> sendDoubleClickDelegate)
    {
        if (sendDoubleClickDelegate == null || !IsPotionInterceptionEnabled)
            return false;

        return IsDrinkablePotionItem(serial) && GetArmingState().Count > 0;
    }

    /// <summary>
    ///     Gets a snapshot of the player's current arming state, that is, what weapons/shields they have equipped
    /// </summary>
    /// <returns>A list of equipped weapons/shields</returns>
    private List<Armament> GetArmingState()
    {
        // Check if player has weapons equipped
        Item oneHanded = _world.Player.FindItemByLayer(Layer.OneHanded);
        Item twoHanded = _world.Player.FindItemByLayer(Layer.TwoHanded);

        var arms = new List<Armament>();
        if (oneHanded?.Serial != null)
            arms.Add(new Armament(oneHanded.Serial, Layer.OneHanded));

        if (twoHanded?.Serial != null)
            arms.Add(new Armament(twoHanded.Serial, Layer.TwoHanded));

        return arms;
    }

    /// <summary>
    ///     Heuristically determines whether an item, given by serial, is a player-drinkable potion
    /// </summary>
    /// <param name="serial">The item's serial</param>
    /// <returns></returns>
    private bool IsDrinkablePotionItem(uint serial)
    {
        Item item = _world.Items.Get(serial);
        if (item == null)
            return false;

        // Check if item is 0xF06-0xF09 OR 0xF0B-0xF0C - these are the 'drinkable' potion graphics
        if (item.Graphic is (< 0xF06 or > 0xF09) and (< 0xF0B or > 0xF0C))
            return false;

        // Get the un-localized item name. We perform an extra check here as graphics may be shared by unrelated items
        if (_world.OPL.TryGetNameAndData(item.Serial, out string name, out _))
            // Use a simple regular expression to heuristically determine if something is a potion.
            // To be expanded on with configuration.
            return name != null && IsPotionRegex().IsMatch(name);

        // Data doesn't exist in OPL cache - stay conservative and assume this is *not* a potion
        return false;
    }

    /// <summary>
    ///     The task channel's consumer, responsible for actually performing any disarming/rearming
    /// </summary>
    private async Task ActionConsumer()
    {
        try
        {
            // Armaments a superseded cast still owes a re-equip for; the next request puts them back.
            List<Armament> owedArms = null;

            // Wait for interception requests/cancellation
            while (await _flushChannel.Reader.WaitToReadAsync(_cTokenSource.Token))
            {
                // A micro-delay to let producers settle, in case of a series of actions
                await Task.Delay(InterceptSettleMs, _cTokenSource.Token);

                while (_flushChannel.Reader.TryRead(out InterceptRequest request))
                {
                    if (request.IsSpellCast)
                    {
                        owedArms = await CastAndAwaitCompletionAsync(request.SpellIndex, owedArms);
                        continue;
                    }

                    // Gather-up consecutive item uses, so they share a single disarm/re-arm
                    var uses = new List<Action> { request.UseItem };
                    while (_flushChannel.Reader.TryPeek(out InterceptRequest next) && !next.IsSpellCast
                           && _flushChannel.Reader.TryRead(out next))
                        uses.Add(next.UseItem);

                    ExecuteBatchedTasks(uses, owedArms);
                    owedArms = null;
                }

                // A short delay to avoid excessive spam
                await Task.Delay(PostBatchDelayMs, _cTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Auto-Unequip action consumer has been interrupted by a cancellation request");
        }
    }

    /// <summary>
    ///     Casts a spell, waits for it to be over, then re-equips what left the player's hands.
    /// </summary>
    /// <remarks>
    ///     The server itself moves the hand items it won't let the caster keep into the backpack at cast start,
    ///     and it alone knows the shard's keep rules (Spell Channeling; on SeriousFun staves for mage spells and
    ///     maces/shields for heals and buffs). Pre-unequipping here would only second-guess it, so this snapshots
    ///     the hands, casts, and re-equips whatever ended up in the backpack. The re-equip must wait for
    ///     <see cref="SpellCastCompletionTracker" />: equipping while the spell is casting, or lifting an item
    ///     while its target cursor is up, makes the server disturb the spell.
    /// </remarks>
    /// <param name="spellIndex">The spell to cast.</param>
    /// <param name="owedArms">Armaments an earlier, superseded cast still owes a re-equip for.</param>
    /// <returns>The armaments still owed when another request superseded this cast, null otherwise.</returns>
    private async Task<List<Armament>> CastAndAwaitCompletionAsync(int spellIndex, List<Armament> owedArms)
    {
        CancellationToken token = _cTokenSource.Token;
        CastInProgress cast = MainThreadQueue.BubblingInvokeOnMainThread(() => BeginCast(spellIndex, owedArms), token);
        if (cast == null)
        {
            _reEquipOwed = false;
            return null;
        }

        SpellCastOutcome outcome = SpellCastOutcome.Pending;
        while (outcome == SpellCastOutcome.Pending)
        {
            await Task.Delay(CastCompletionPollMs, token);

            // Another queued request (typically the next spell in a chain) replaces this cast on the server;
            // re-arming now would disturb it, so hand the owed armaments over instead.
            if (_flushChannel.Reader.Count > 0)
                return cast.Arms;

            outcome = MainThreadQueue.BubblingInvokeOnMainThread(() => PollCast(cast.Tracker), token);
        }

        if (cast.Arms.Count > 0)
            EnqueueReEquip(cast.Arms);
        _reEquipOwed = false;

        return null;
    }

    /// <summary>
    ///     Snapshots the hands, sends the cast and starts tracking it. Main thread only.
    /// </summary>
    /// <param name="spellIndex">The spell to cast.</param>
    /// <param name="owedArms">Armaments an earlier, superseded cast still owes a re-equip for.</param>
    /// <returns>The cast being tracked, or null when the player/backpack is gone.</returns>
    private CastInProgress BeginCast(int spellIndex, List<Armament> owedArms)
    {
        if (!IsPlayerBackpackAvailable)
            return null;

        List<Armament> arms = MergeArms(GetArmingState(), owedArms);

        SpellVisualRangeManager.Instance.SpellRangeCache.TryGetValue(spellIndex, out SpellRangeInfo rangeInfo);
        var tracker = new SpellCastCompletionTracker(
            Time.Ticks,
            SpellCastCompletionTracker.EstimateCastDelayMs(spellIndex, rangeInfo?.CastTime ?? 0),
            rangeInfo?.ExpectTargetCursor ?? true, // An unknown spell may open a cursor; waiting longer is the safe side
            (int)(AsyncNetClient.Socket?.Statistics?.Ping ?? 0),
            _world.TargetManager.IsTargeting
        );

        _reEquipOwed = true;
        GameActions.CastSpellDirect(spellIndex);

        return new CastInProgress(arms, tracker);
    }

    /// <summary>
    ///     Feeds the tracker the current client state. Main thread only.
    /// </summary>
    /// <param name="tracker">The cast's tracker.</param>
    /// <returns>The tracker's verdict; <see cref="SpellCastOutcome.TimedOut" /> once the player is gone.</returns>
    private SpellCastOutcome PollCast(SpellCastCompletionTracker tracker)
    {
        if (!IsPlayerBackpackAvailable)
            return SpellCastOutcome.TimedOut;

        return tracker.Update(
            Time.Ticks,
            _world.TargetManager.IsTargeting,
            SpellVisualRangeManager.Instance.LastCastFailedTick
        );
    }

    /// <summary>
    ///     Adds the owed armaments that aren't already in <paramref name="arms" />.
    /// </summary>
    /// <param name="arms">The current arming state; modified in place.</param>
    /// <param name="owedArms">Armaments a superseded cast still owes a re-equip for, or null.</param>
    /// <returns><paramref name="arms" />.</returns>
    private static List<Armament> MergeArms(List<Armament> arms, List<Armament> owedArms)
    {
        foreach (Armament owed in owedArms ?? [])
            if (!arms.Exists(arm => arm.Serial == owed.Serial))
                arms.Add(owed);

        return arms;
    }

    /// <summary>
    ///     Executes the given item uses after ensuring the player has been disarmed, then re-arms the player.
    /// </summary>
    /// <param name="tasks">The item uses to execute, such as "drink a potion"</param>
    /// <param name="owedArms">Armaments a superseded cast still owes a re-equip for</param>
    private void ExecuteBatchedTasks(List<Action> tasks, List<Armament> owedArms)
    {
        if (_disposed)
            return;

        // Dispatch to MT to avoid any potential sync issues.
        //
        // We need to fetch a 'fresh' state - no point issuing an unequip command if we're not currently armed.
        List<Armament> arms = MainThreadQueue.BubblingInvokeOnMainThread(() => IsPlayerBackpackAvailable ? GetArmingState() : null, _cTokenSource.Token);
        if (arms == null) // null means world/player/backpack are gone and we should stop.
        {
            _reEquipOwed = false;
            return;
        }

        _cTokenSource.Token.ThrowIfCancellationRequested();

        // Enqueue a disarm if necessary
        if (arms.Count > 0)
            EnqueueUnequip(arms);
        _cTokenSource.Token.ThrowIfCancellationRequested();

        // Enqueue all batched actions
        foreach (Action task in tasks)
            ObjectActionQueue.Instance.Enqueue(new ObjectActionQueueItem(task), ActionPriority.EquipItem);
        _cTokenSource.Token.ThrowIfCancellationRequested();

        // Enqueue a re-arm, including anything a superseded cast still owes
        MergeArms(arms, owedArms);
        if (arms.Count > 0)
            EnqueueReEquip(arms);
        _reEquipOwed = false;
    }

    /// <summary>
    ///     Enqueues un-equip actions, if the player is currently armed
    /// </summary>
    /// <returns></returns>
    private void EnqueueUnequip(IList<Armament> arms)
    {
        // Issue an unequip for each equipped armament.
        // Future compatability with Cephalopod based players.
        foreach (Armament arm in arms ?? [])
            ObjectActionQueue.Instance.Enqueue(
                CreateUnequipQueueItem(arm.Serial, arm.Layer),
                ActionPriority.EquipItem
            );
    }

    /// <summary>
    ///     Create an un-equip queue item, to be used with the object queue
    /// </summary>
    /// <param name="serial">The item to un-equip</param>
    /// <param name="layer">The item's layer, used for validation</param>
    /// <returns></returns>
    private ObjectActionQueueItem CreateUnequipQueueItem(uint serial, Layer layer) =>
        new(() =>
        {
            if (!IsPlayerBackpackAvailable)
                return;

            Item item = _world.Items?.Get(serial);
            if (item == null || item.Container != _world.Player.Serial || item.Layer != layer)
                return;

            new MoveRequest(serial, _world.Player.Backpack.Serial, item.Amount).Execute();
        });

    /// <summary>
    ///     Enqueues the given armaments for re-equipment
    /// </summary>
    /// <param name="arms">The armaments to re-equip</param>
    private void EnqueueReEquip(IList<Armament> arms)
    {
        foreach (Armament arm in arms)
            ObjectActionQueue.Instance.Enqueue(new ObjectActionQueueItem(() =>
            {
                Item item = _world?.Items?.Get(arm.Serial);
                Item backpackItem = _world?.Player?.Backpack;

                if (item != null && backpackItem != null && item.Container == backpackItem.Serial)
                    MoveRequest.EquipItem(arm.Serial, arm.Layer)?.Execute();
            }), ActionPriority.EquipItem);
    }

    #endregion

    #region Regular Expressions

    /// <summary>
    ///     A regex used to match an item's `Spell Channeling` property
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"^\s*Spell Channeling\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IsSpellChannelling();

    /// <summary>
    ///     A regex used to match standard 'drinkable' potion names
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"(Strength|Agility|Heal|Cure|Nightsight|Refresh(ment)?)\s+Potion", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsPotionRegex();

    #endregion
}
