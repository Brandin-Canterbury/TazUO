using System.Collections.Generic;
using ClassicUO.Game.Managers;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class SelfHealSpellsTest
    {
        private const int GH = SelfHealStateMachine.MageryGreaterHealSpellId;
        private const int HEAL = SelfHealStateMachine.MageryHealSpellId;
        private const int CW = SelfHealStateMachine.ChivalryHealSpellId;

        [Fact]
        public void Pick_FirstAffordableInOrder()
        {
            SelfHealSpells.Pick(new List<int> { GH, HEAL }, 50, HEAL).Should().Be(GH);
            SelfHealSpells.Pick(new List<int> { GH, HEAL }, 10, HEAL).Should().Be(HEAL);
            SelfHealSpells.Pick(new List<int> { HEAL, GH }, 50, HEAL).Should().Be(HEAL);
        }

        [Fact]
        public void Pick_NoneAffordable_ReturnsLastSoServerExplains()
        {
            SelfHealSpells.Pick(new List<int> { GH, CW }, 2, HEAL).Should().Be(CW);
        }

        [Fact]
        public void Pick_EmptyOrUnknown_UsesFallback()
        {
            SelfHealSpells.Pick(new List<int>(), 50, CW).Should().Be(CW);
            SelfHealSpells.Pick(new List<int> { 9999 }, 50, HEAL).Should().Be(HEAL);
            SelfHealSpells.Pick(null, 50, HEAL).Should().Be(HEAL);
        }

        [Fact]
        public void Timings_GraceFollowsSlowestListedHeal()
        {
            // Heal only: Cure (1.25s - 0.5s) is the longest cast.
            SelfHealTimings.Compute(false, 2, 6, new List<int> { HEAL }).castStartGraceMs.Should().Be(1150);
            // Greater Heal listed: 1.75s - 0.5s.
            SelfHealTimings.Compute(false, 2, 6, new List<int> { HEAL, GH }).castStartGraceMs.Should().Be(1650);
        }
    }
}
