#nullable enable
using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;

namespace ClassicUO.Game.Managers;

/// <summary>
/// Opens backpack sub-containers whose contents the client hasn't received yet, then runs a callback once they have.
/// The server only sends a container's contents when it's opened, so an unopened pouch reads as empty until then.
/// </summary>
/// <remarks>
/// Main thread only. Call <see cref="Subscribe"/> / <see cref="Unsubscribe"/> around the game scene's lifetime and
/// <see cref="Update"/> every tick. Containers it opens are closed again before the callback runs.
/// </remarks>
internal sealed class BackpackContentsLoader
{
    #region Private members

    // Open-container and contents packets arrive together; the settle time covers them landing in separate reads.
    private const long SETTLE_MS = 500;
    private const long TIMEOUT_MS = 5000;

    // Double-clicks go through the action queue's cooldown, so the wait grows with the number of containers.
    private const long TIMEOUT_PER_CONTAINER_MS = 1000;

    // Each round opens one nesting level, since a pouch's sub-containers are unknown until the pouch is loaded.
    private const int MAX_ROUNDS = 4;

    private readonly HashSet<uint> _loaded = new();
    private readonly HashSet<uint> _pending = new();
    private readonly HashSet<uint> _attempted = new();
    private readonly List<uint> _toClose = new();
    private readonly List<Item> _unloaded = new();
    private Action? _onLoaded;
    private int _round;
    private long _settleUntil;
    private long _deadline;

    #endregion

    #region Public methods

    public void Subscribe() => EventSink.OnOpenContainer += OnOpenContainer;

    public void Unsubscribe()
    {
        EventSink.OnOpenContainer -= OnOpenContainer;
        Reset();
        _loaded.Clear();
    }

    /// <summary>
    /// Opens every unloaded container in the player's backpack, then runs <paramref name="onLoaded"/>.
    /// Runs it immediately when nothing needs opening. A call while one is in progress replaces its callback.
    /// </summary>
    /// <param name="onLoaded">Runs once contents have arrived or the wait timed out.</param>
    public void LoadThen(Action onLoaded)
    {
        bool inProgress = _onLoaded != null;
        _onLoaded = onLoaded;

        if (!inProgress)
            StartRound();
    }

    /// <summary>Advances to the next nesting level once the current one has loaded, or gives up on timeout.</summary>
    public void Update()
    {
        if (_onLoaded == null)
            return;

        if (_pending.Count == 0 && Time.Ticks >= _settleUntil)
            StartRound();
        else if (Time.Ticks >= _deadline)
            Finish();
    }

    #endregion

    #region Private methods

    private void OnOpenContainer(object? sender, uint serial)
    {
        _loaded.Add(serial);

        if (_pending.Remove(serial))
            _settleUntil = Time.Ticks + SETTLE_MS;
    }

    private void StartRound()
    {
        Item? backpack = World.Instance?.Player?.Backpack;

        _unloaded.Clear();

        if (backpack != null && _round < MAX_ROUNDS)
        {
            if (IsLoaded(backpack))
                CollectUnloaded(backpack);
            else if (!_attempted.Contains(backpack.Serial))
                _unloaded.Add(backpack);
        }

        if (_unloaded.Count == 0)
        {
            Finish();
            return;
        }

        _round++;
        _deadline = Time.Ticks + TIMEOUT_MS + _unloaded.Count * TIMEOUT_PER_CONTAINER_MS;

        foreach (Item container in _unloaded)
        {
            _pending.Add(container.Serial);
            _attempted.Add(container.Serial);

            if (!container.Opened)
                _toClose.Add(container.Serial);

            GameActions.DoubleClickQueued(container.Serial);
        }
    }

    private void CollectUnloaded(Item container)
    {
        for (LinkedObject? node = container.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (item.IsDestroyed || !item.ItemData.IsContainer || IsSpellbook(item))
                continue;

            if (IsLoaded(item))
                CollectUnloaded(item);
            else if (!_attempted.Contains(item.Serial))
                _unloaded.Add(item);
        }
    }

    // Contents only arrive through an open, so any known child means the container was loaded at some point.
    private bool IsLoaded(Item container) => !container.IsEmpty || container.Opened || _loaded.Contains(container.Serial);

    private static bool IsSpellbook(Item item) => Array.IndexOf(GridItem.SpellbookGraphics, (int)item.Graphic) >= 0;

    private void Finish()
    {
        foreach (uint serial in _toClose)
            UIManager.GetGump(serial)?.Dispose();

        Action? onLoaded = _onLoaded;
        Reset();
        onLoaded?.Invoke();
    }

    private void Reset()
    {
        _onLoaded = null;
        _round = 0;
        _settleUntil = 0;
        _deadline = 0;
        _pending.Clear();
        _attempted.Clear();
        _toClose.Clear();
        _unloaded.Clear();
    }

    #endregion
}
