#nullable enable

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>
/// Shard facts about one craft skill: the client skill index, the craft menu's title cliloc, the tools
/// that open it and how its products are recycled.
/// </summary>
/// <param name="Skill">The craft skill.</param>
/// <param name="SkillIndex">Index into the player's skill list (standard UO skill order).</param>
/// <param name="TitleCliloc">Cliloc of the craft gump title (ServUO <c>GumpTitleNumber</c>).</param>
/// <param name="NameKey">Language key of the skill's display name.</param>
/// <param name="Recycle">How crafted items get material back.</param>
/// <param name="ToolGraphics">Item graphics of every tool that opens this craft menu, both facings.</param>
internal sealed record CraftSkillInfo(
    CraftSkill Skill,
    int SkillIndex,
    int TitleCliloc,
    string NameKey,
    CraftRecycleMethod Recycle,
    ushort[] ToolGraphics
)
{
    #region Private members

    private static readonly CraftSkillInfo[] _all =
    [
        new(CraftSkill.Blacksmithy, 7, 1044002, "craftagent_skill_blacksmithy", CraftRecycleMethod.Smelt,
            [0x13E3, 0x13E4, 0x0FB5, 0x0FB4, 0x0FBB, 0x0FBC]), // smith's hammer, sledge hammer, tongs
        new(CraftSkill.Tailoring, 34, 1044005, "craftagent_skill_tailoring", CraftRecycleMethod.Scissors,
            [0x0F9D]), // sewing kit
        new(CraftSkill.Tinkering, 37, 1044007, "craftagent_skill_tinkering", CraftRecycleMethod.None,
            [0x1EB8, 0x1EB9, 0x1EBC]), // tinker's tools, tool kit
        new(CraftSkill.Carpentry, 11, 1044004, "craftagent_skill_carpentry", CraftRecycleMethod.None,
        [
            0x1034, 0x1035, 0x1028, 0x1029, 0x102A, 0x10E4, 0x10E5, 0x10E6, 0x10E7, // saws, hammer, drawknife, froe, inshave, scorp
            0x1030, 0x1031, 0x102C, 0x102D, 0x1032, 0x1033                          // jointing, moulding, smoothing planes
        ]),
        new(CraftSkill.Fletching, 8, 1044006, "craftagent_skill_fletching", CraftRecycleMethod.None,
            [0x1022, 0x1023]), // fletcher's tools
        new(CraftSkill.Inscription, 23, 1044009, "craftagent_skill_inscription", CraftRecycleMethod.None,
            [0x0FBF, 0x0FC0]), // scribe's pen
        new(CraftSkill.Alchemy, 0, 1044001, "craftagent_skill_alchemy", CraftRecycleMethod.None,
            [0x0E9B]) // mortar and pestle
    ];

    #endregion

    #region Public methods

    /// <summary>Info for a craft skill.</summary>
    /// <param name="skill">The craft skill.</param>
    /// <returns>The skill's info.</returns>
    public static CraftSkillInfo Get(CraftSkill skill) => _all[(int)skill];

    /// <summary>True when <paramref name="graphic"/> is a tool that opens this craft menu.</summary>
    /// <param name="graphic">Item graphic.</param>
    /// <returns>Whether the item is one of this craft's tools.</returns>
    public bool IsTool(ushort graphic)
    {
        for (int i = 0; i < ToolGraphics.Length; i++)
        {
            if (ToolGraphics[i] == graphic)
                return true;
        }

        return false;
    }

    #endregion
}
