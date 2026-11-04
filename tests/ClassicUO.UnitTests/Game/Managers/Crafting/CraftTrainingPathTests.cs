using System;
using System.Collections.Generic;
using ClassicUO.Game.Managers.Crafting;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers.Crafting
{
    public class CraftTrainingPathTests
    {
        private const float OVERCAP_START = 100f;
        private const float OVERCAP_REACH = 50f;
        private const float SKILL_STEP = 0.1f;

        public static IEnumerable<object[]> AllSkills()
        {
            foreach (CraftSkill skill in Enum.GetValues<CraftSkill>())
                yield return [skill];
        }

        [Theory]
        [MemberData(nameof(AllSkills))]
        public void Path_CoversZeroToCapWithoutGaps(CraftSkill skill)
        {
            IReadOnlyList<CraftPathStep> steps = CraftTrainingPath.For(skill);

            steps[0].FromSkill.Should().Be(0f);
            steps[^1].ToSkill.Should().Be(CraftTrainingPath.MAX_SKILL);

            for (int i = 1; i < steps.Count; i++)
                steps[i].FromSkill.Should().Be(steps[i - 1].ToSkill, $"row {i} must start where row {i - 1} ends");
        }

        [Theory]
        [MemberData(nameof(AllSkills))]
        public void Path_EveryRowCanBeCraftedAndGivesGains(CraftSkill skill)
        {
            foreach (CraftPathStep step in CraftTrainingPath.For(skill))
            {
                step.ItemMin.Should().BeLessThanOrEqualTo(step.FromSkill, "the item must be craftable when the row starts");

                // Below the shard's overcap start an item stops training at its max skill; from 100 up it keeps
                // training while its max is within the overcap reach of the player's skill.
                for (float value = step.FromSkill; value < step.ToSkill; value += SKILL_STEP)
                {
                    bool normalGains = value < step.ItemMax;
                    bool overcapGains = value >= OVERCAP_START && step.ItemMax >= value - OVERCAP_REACH;

                    (normalGains || overcapGains).Should().BeTrue($"{skill} row {step.FromSkill}-{step.ToSkill} must give gains at {value}");
                }
            }
        }

        [Fact]
        public void FindIndex_PicksRowContainingSkill()
        {
            CraftTrainingPath.FindIndex(CraftSkill.Blacksmithy, 0f).Should().Be(0);
            CraftTrainingPath.FindIndex(CraftSkill.Blacksmithy, 44.9f).Should().Be(0);
            CraftTrainingPath.FindIndex(CraftSkill.Blacksmithy, 45f).Should().Be(1);
            CraftTrainingPath.FindIndex(CraftSkill.Blacksmithy, 149.9f).Should().Be(4);
        }

        [Fact]
        public void FindIndex_ClampsOutsideThePath()
        {
            CraftTrainingPath.FindIndex(CraftSkill.Tailoring, -5f).Should().Be(0);

            CraftTrainingPath.FindIndex(CraftSkill.Tailoring, 200f)
                .Should().Be(CraftTrainingPath.For(CraftSkill.Tailoring).Count - 1);
        }
    }
}
