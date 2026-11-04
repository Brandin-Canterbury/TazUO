using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class SpellCastCompletionTrackerTest
    {
        private const long CastAt = 10_000;

        private static SpellCastCompletionTracker Create(bool expectsTarget, bool targetingAtCast = false) =>
            new(CastAt, castDelayMs: 1000, expectsTarget, pingMs: 100, targetingAtCast);

        [Fact]
        public void TargetSpell_StaysPendingUntilCursorClosed_EvenPastTheCastWindow()
        {
            var tracker = Create(expectsTarget: true);

            tracker.Update(CastAt + 500, false, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 1200, true, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 20_000, true, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 20_050, false, 0).Should().Be(SpellCastOutcome.TargetClosed);
        }

        [Fact]
        public void TargetSpell_WaitsForTheCursorToArrive()
        {
            var tracker = Create(expectsTarget: true);

            // Cast delay 1000 + target pad 1000 + 2 x 100 ping
            tracker.CastWindowEndsAt.Should().Be(CastAt + 2200);
            tracker.Update(CastAt + 2199, false, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 2200, false, 0).Should().Be(SpellCastOutcome.CastWindowElapsed);
        }

        [Fact]
        public void NoTargetSpell_EndsAfterCastDelayPlusShortPad()
        {
            var tracker = Create(expectsTarget: false);

            tracker.CastWindowEndsAt.Should().Be(CastAt + 1000 + 300 + 200);
            tracker.Update(CastAt + 1499, false, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 1500, false, 0).Should().Be(SpellCastOutcome.CastWindowElapsed);
        }

        [Fact]
        public void FailureAfterTheCast_EndsImmediately_ButOlderFailuresAreIgnored()
        {
            var tracker = Create(expectsTarget: true);

            tracker.Update(CastAt + 100, false, CastAt - 50).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 300, false, CastAt + 250).Should().Be(SpellCastOutcome.Failed);
        }

        [Fact]
        public void CursorOpenBeforeTheCast_ClosingDoesNotEndTheCast()
        {
            var tracker = Create(expectsTarget: true, targetingAtCast: true);

            tracker.Update(CastAt + 100, true, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 200, false, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 1200, true, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + 1300, false, 0).Should().Be(SpellCastOutcome.TargetClosed);
        }

        [Fact]
        public void CursorHeldPastTheCap_TimesOut()
        {
            var tracker = Create(expectsTarget: true);

            tracker.Update(CastAt + 1200, true, 0).Should().Be(SpellCastOutcome.Pending);
            tracker.Update(CastAt + SpellCastCompletionTracker.MaxWaitMs, true, 0).Should().Be(SpellCastOutcome.TimedOut);
        }

        [Fact]
        public void BadPingReading_IsClamped()
        {
            var tracker = new SpellCastCompletionTracker(CastAt, 1000, false, 50_000, false);

            tracker.CastWindowEndsAt.Should().Be(CastAt + 1000 + 300 + 2000);
        }

        [Theory]
        [InlineData(4, 0.5, 1000)]    // Heal, 1st circle: (4 + 0) x 250, config's 0.5s is too short
        [InlineData(29, 0, 1750)]     // Greater Heal, 4th circle
        [InlineData(59, 0, 2750)]     // Resurrection, 8th circle
        [InlineData(64, 3.0, 3000)]   // A longer configured time wins
        [InlineData(680, 1.5, 1500)]  // Non-Magery: the configured time
        [InlineData(680, 0, SpellCastCompletionTracker.DefaultCastDelayMs)]
        public void EstimateCastDelay_ErrsLong(int spellIndex, double configuredSec, int expectedMs)
        {
            SpellCastCompletionTracker.EstimateCastDelayMs(spellIndex, configuredSec).Should().Be(expectedMs);
        }
    }
}
