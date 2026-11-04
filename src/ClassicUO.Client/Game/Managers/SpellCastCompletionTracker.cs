#nullable enable
using System;

namespace ClassicUO.Game.Managers;

/// <summary>
///     What a <see cref="SpellCastCompletionTracker" /> concluded about the cast it watches.
/// </summary>
public enum SpellCastOutcome
{
    /// <summary>The spell may still be casting or waiting on its target cursor.</summary>
    Pending,

    /// <summary>The server reported the cast failed (disturbed, no mana, not recovered, ...).</summary>
    Failed,

    /// <summary>The spell's target cursor was used or cancelled.</summary>
    TargetClosed,

    /// <summary>The cast window elapsed with no target cursor in play.</summary>
    CastWindowElapsed,

    /// <summary>A target cursor stayed open past <see cref="SpellCastCompletionTracker.MaxWaitMs" />.</summary>
    TimedOut
}

/// <summary>
///     Decides when a spell the client just cast is over, so the caster's equipment can be touched again.
///     The server disturbs a spell when the caster equips during the cast or lifts an item while its target
///     cursor is up, so anything that re-arms after a cast has to wait for the end this reports.
/// </summary>
/// <remarks>
///     No packet marks a successful cast. The end is therefore the first of: a cast-failure cliloc after the
///     cast was sent, the spell's target cursor closing, or (when no cursor shows up) the estimated cast delay
///     plus a latency pad. Feed it with <see cref="Update" /> from one thread; it holds no locks.
/// </remarks>
public sealed class SpellCastCompletionTracker
{
    #region Public accessors

    /// <summary>Pad over the cast delay for a spell that needs no target cursor.</summary>
    public const int NoTargetPadMs = 300;

    /// <summary>Pad over the cast delay for the target cursor to reach the client.</summary>
    public const int TargetArrivalPadMs = 1000;

    /// <summary>Hard cap on the wait, so a cursor left open forever can't hold the caller indefinitely.</summary>
    public const int MaxWaitMs = 60_000;

    /// <summary>Estimate used when nothing is known about the spell's cast time.</summary>
    public const int DefaultCastDelayMs = 3000;

    /// <summary>When this cast stops being <see cref="SpellCastOutcome.Pending" /> without a target cursor.</summary>
    public long CastWindowEndsAt => _castIssuedAt + _castWindowMs;

    #endregion

    #region Private members

    private const int MaxPingMs = 1000;
    private const int PingPadMultiplier = 2;
    private const int CastDelayStepMs = 250;
    private const int MageryBaseSteps = 4;
    private const int MagerySpellsPerCircle = 8;
    private const int FirstMagerySpell = 1;
    private const int LastMagerySpell = 64;

    private readonly long _castIssuedAt;
    private readonly int _castWindowMs;

    // A cursor that was already open when the cast went out belongs to something else (usually a previous
    // spell the server is about to cancel); it must close once before a cursor counts as this spell's.
    private bool _staleCursorOpen;
    private bool _sawTargetCursor;

    #endregion

    /// <summary>
    ///     Starts tracking a cast that was just sent to the server.
    /// </summary>
    /// <param name="castIssuedAt">Monotonic milliseconds at which the cast was sent.</param>
    /// <param name="castDelayMs">Estimated server cast delay, see <see cref="EstimateCastDelayMs" />.</param>
    /// <param name="expectsTarget">Whether the spell is expected to open a target cursor.</param>
    /// <param name="pingMs">Current round-trip time; clamped to keep a bad reading from inflating the wait.</param>
    /// <param name="targetingAtCast">Whether a target cursor was already open when the cast was sent.</param>
    public SpellCastCompletionTracker(long castIssuedAt, int castDelayMs, bool expectsTarget, int pingMs, bool targetingAtCast)
    {
        _castIssuedAt = castIssuedAt;
        _staleCursorOpen = targetingAtCast;

        int pad = expectsTarget ? TargetArrivalPadMs : NoTargetPadMs;
        int latencyPad = Math.Clamp(pingMs, 0, MaxPingMs) * PingPadMultiplier;
        _castWindowMs = Math.Max(0, castDelayMs) + pad + latencyPad;
    }

    #region Public methods

    /// <summary>
    ///     Advances the tracker with the latest observed client state.
    /// </summary>
    /// <param name="now">Monotonic milliseconds, same clock as the constructor's.</param>
    /// <param name="isTargeting">Whether a target cursor is open right now.</param>
    /// <param name="lastCastFailedAt">Monotonic tick of the server's last cast-failure message.</param>
    /// <returns><see cref="SpellCastOutcome.Pending" /> until the cast is over, then why it ended.</returns>
    public SpellCastOutcome Update(long now, bool isTargeting, long lastCastFailedAt)
    {
        if (lastCastFailedAt > _castIssuedAt)
            return SpellCastOutcome.Failed;

        long elapsed = now - _castIssuedAt;

        if (isTargeting)
        {
            if (!_staleCursorOpen)
                _sawTargetCursor = true;

            return elapsed >= MaxWaitMs ? SpellCastOutcome.TimedOut : SpellCastOutcome.Pending;
        }

        _staleCursorOpen = false;

        if (_sawTargetCursor)
            return SpellCastOutcome.TargetClosed;

        return elapsed >= _castWindowMs ? SpellCastOutcome.CastWindowElapsed : SpellCastOutcome.Pending;
    }

    /// <summary>
    ///     Estimates the server-side cast delay of a spell, erring long. Faster Casting is deliberately ignored:
    ///     a too-short estimate re-arms mid-cast, while a too-long one only delays the re-arm.
    /// </summary>
    /// <param name="spellIndex">The spell's global cast index.</param>
    /// <param name="configuredCastTimeSec">The spell indicator config's cast time, or 0 when unknown.</param>
    /// <returns>The estimated delay in milliseconds.</returns>
    public static int EstimateCastDelayMs(int spellIndex, double configuredCastTimeSec)
    {
        int estimate = configuredCastTimeSec > 0 ? (int)Math.Round(configuredCastTimeSec * 1000.0) : 0;

        // AOS Magery: (4 + circle) ticks of 0.25s before Faster Casting.
        if (spellIndex is >= FirstMagerySpell and <= LastMagerySpell)
        {
            int circle = (spellIndex - FirstMagerySpell) / MagerySpellsPerCircle;
            estimate = Math.Max(estimate, (MageryBaseSteps + circle) * CastDelayStepMs);
        }

        return estimate > 0 ? estimate : DefaultCastDelayMs;
    }

    #endregion
}
