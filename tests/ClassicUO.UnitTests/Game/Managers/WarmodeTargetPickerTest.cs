using System.Collections.Generic;
using ClassicUO.Game.Managers.Warmode;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers
{
    public class WarmodeTargetPickerTest
    {
        private static WarmodeCandidate Mob(uint serial, string name, int distance = 3, bool human = false, bool war = true, int hits = 100) =>
            new(serial, name, distance, human, war, hits);

        private static WarmodeAgentConfig Config(params string[] names)
        {
            var config = new WarmodeAgentConfig();

            foreach (string name in names)
                config.Priorities.Add(new WarmodePriority { Name = name });

            return config;
        }

        [Fact]
        public void Rank_FirstContainingEntryWins_CaseInsensitive()
        {
            WarmodeAgentConfig config = Config("orc mage", "orc");

            WarmodeTargetPicker.Rank(config.Priorities, "an Orc Mage", out _).Should().Be(0);
            WarmodeTargetPicker.Rank(config.Priorities, "an orc captain", out _).Should().Be(1);
            WarmodeTargetPicker.Rank(config.Priorities, "a ratman", out _).Should().Be(WarmodeTargetPicker.UNLISTED);
        }

        [Fact]
        public void Rank_MatchesTypeWhenNameIsPersonal()
        {
            WarmodeAgentConfig config = Config("mage", "orc");
            var grukk = new WarmodeCandidate(1, "Grukk", 3, false, true, 100, "orc mage");
            var brog = new WarmodeCandidate(2, "Brog", 3, false, true, 100, "orc warrior");

            WarmodeTargetPicker.Rank(config.Priorities, grukk, out _).Should().Be(0);
            WarmodeTargetPicker.Rank(config.Priorities, brog, out _).Should().Be(1);
        }

        [Fact]
        public void Choose_PrefersMageTypeOverNearerWarrior()
        {
            WarmodeAgentConfig config = Config("orc mage", "orc");
            var candidates = new List<WarmodeCandidate>
            {
                new(1, "Brog", 2, false, true, 100, "orc warrior"),
                new(2, "Grukk", 6, false, true, 100, "orc mage")
            };

            WarmodeTargetPicker.Choose(candidates, config, null).Should().Be(2u);
        }

        [Fact]
        public void Choose_PrefersListedOverNearer()
        {
            WarmodeAgentConfig config = Config("orc bomber");
            var candidates = new List<WarmodeCandidate> { Mob(1, "an orc", 2), Mob(2, "an orc bomber", 8) };

            WarmodeTargetPicker.Choose(candidates, config, null).Should().Be(2u);
        }

        [Fact]
        public void Choose_AdjacentBeatsPriority_ThenListDecidesAmongAdjacent()
        {
            WarmodeAgentConfig config = Config("orc bomber", "orc lord");
            var candidates = new List<WarmodeCandidate> { Mob(1, "an orc", 1), Mob(2, "an orc bomber", 8), Mob(3, "an orc lord", 1) };

            WarmodeTargetPicker.Choose(candidates, config, null).Should().Be(3u);
        }

        [Fact]
        public void Choose_DropsFleeingCurrent_ForAnythingAdjacent()
        {
            WarmodeAgentConfig config = Config("orc lord");
            WarmodeCandidate current = Mob(1, "an orc lord", 4);
            var candidates = new List<WarmodeCandidate> { current, Mob(2, "a rat", 1) };

            WarmodeTargetPicker.Choose(candidates, config, current).Should().Be(2u);
        }

        [Fact]
        public void Choose_KeepsAdjacentCurrent_OverDistantHigherPriority()
        {
            WarmodeAgentConfig config = Config("orc shaman", "orc");
            WarmodeCandidate current = Mob(1, "an orc", 1);
            var candidates = new List<WarmodeCandidate> { current, Mob(2, "an orc shaman", 6) };

            WarmodeTargetPicker.Choose(candidates, config, current).Should().Be(1u);
        }

        [Fact]
        public void Choose_UnlistedTieBreaksOnDistanceThenHealth()
        {
            WarmodeAgentConfig config = Config();
            var candidates = new List<WarmodeCandidate> { Mob(1, "a", 4), Mob(2, "b", 2, hits: 90), Mob(3, "c", 2, hits: 40) };

            WarmodeTargetPicker.Choose(candidates, config, null).Should().Be(3u);
        }

        [Fact]
        public void Choose_SwitchesToHigherPriority_WhenEnabled()
        {
            WarmodeAgentConfig config = Config("orc shaman", "orc lord");
            WarmodeCandidate current = Mob(1, "an orc lord");
            var candidates = new List<WarmodeCandidate> { current, Mob(2, "an orc shaman", 6) };

            WarmodeTargetPicker.Choose(candidates, config, current).Should().Be(2u);

            config.SwitchToHigherPriority = false;
            WarmodeTargetPicker.Choose(candidates, config, current).Should().Be(1u);
        }

        [Fact]
        public void Choose_KeepsCurrent_OverEqualOrLowerPriority()
        {
            WarmodeAgentConfig config = Config("orc");
            WarmodeCandidate current = Mob(1, "an orc", 5);
            var candidates = new List<WarmodeCandidate> { current, Mob(2, "an orc", 3), Mob(3, "a rat", 3) };

            WarmodeTargetPicker.Choose(candidates, config, current).Should().Be(1u);
        }

        [Fact]
        public void Choose_SkipsIgnoredAndDropsIgnoredCurrent()
        {
            WarmodeAgentConfig config = Config("rat");
            config.Priorities[0].Ignore = true;
            WarmodeCandidate current = Mob(1, "a giant rat");
            var candidates = new List<WarmodeCandidate> { current, Mob(2, "an orc", 7) };

            WarmodeTargetPicker.Choose(candidates, config, current).Should().Be(2u);
        }

        [Fact]
        public void Choose_HumansOnlyWhenListed()
        {
            var candidates = new List<WarmodeCandidate> { Mob(1, "Bob", 1, human: true) };

            WarmodeTargetPicker.Choose(candidates, Config(), null).Should().Be(0u);
            WarmodeTargetPicker.Choose(candidates, Config("bob"), null).Should().Be(1u);
        }

        [Fact]
        public void Choose_OnlyFighting_SkipsPeacefulCreatures()
        {
            WarmodeAgentConfig config = Config();
            var candidates = new List<WarmodeCandidate> { Mob(1, "a deer", 1, war: false) };

            WarmodeTargetPicker.Choose(candidates, config, null).Should().Be(0u);

            config.OnlyFighting = false;
            WarmodeTargetPicker.Choose(candidates, config, null).Should().Be(1u);
        }

        [Fact]
        public void Choose_RespectsRange()
        {
            WarmodeAgentConfig config = Config();
            config.Range = 5;
            var candidates = new List<WarmodeCandidate> { Mob(1, "an orc", 6) };

            WarmodeTargetPicker.Choose(candidates, config, null).Should().Be(0u);
        }
    }
}
