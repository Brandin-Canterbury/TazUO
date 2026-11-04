#nullable enable

using System.Collections.Generic;

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>Main material a training item consumes; drives the "out of materials" text.</summary>
public enum CraftMaterial
{
    Ingots,
    Cloth,
    Leather,
    Boards,
    Nightshade,
    Bloodmoss,
    MandrakeRoot,
    ScrollReagents
}

/// <summary>
/// One row of a training path: while the player's skill is in [<see cref="FromSkill"/>,
/// <see cref="ToSkill"/>), craft the item named by <see cref="ItemCliloc"/> in the category
/// <see cref="GroupCliloc"/>.
/// </summary>
/// <param name="FromSkill">Lowest skill (inclusive) this row applies to.</param>
/// <param name="ToSkill">Skill (exclusive) where the next row takes over.</param>
/// <param name="GroupCliloc">Craft menu category cliloc (ServUO <c>AddCraft</c> group).</param>
/// <param name="ItemCliloc">Craft menu item cliloc (ServUO <c>AddCraft</c> name).</param>
/// <param name="ItemMin">The item's minimum skill on the server.</param>
/// <param name="ItemMax">The item's maximum skill on the server (no gain chance at or above it below 100).</param>
/// <param name="Material">Main material consumed.</param>
/// <param name="Amount">Units of <paramref name="Material"/> per attempt.</param>
/// <param name="ManaCost">Mana the server requires per attempt (Inscription only).</param>
internal readonly record struct CraftPathStep(
    float FromSkill,
    float ToSkill,
    int GroupCliloc,
    int ItemCliloc,
    float ItemMin,
    float ItemMax,
    CraftMaterial Material,
    int Amount,
    int ManaCost = 0
);

/// <summary>
/// Static training paths for the SeriousFun shard, derived from ServUO's <c>Def*.cs</c> craft tables.
/// </summary>
/// <remarks>
/// ServUO's gain chance per attempt is about <c>B/2 + c(1-c)/4</c> (scaled 2.5x on this shard), where
/// <c>c</c> is <c>(skill - min) / (max - min)</c>, so it peaks with the skill mid-range. Rows pick, for
/// each skill band, the cheapest single-material item within ~15% of the best gain, preferring items
/// the server can recycle. From 100 up the shard's <c>OvercapGains</c> keeps a gain chance on items
/// that can no longer fail, as long as their max skill is within 50 of the player's, so a cheap item
/// whose max is at least <c>skill - 50</c> trains to 150 once nothing harder is worth its cost.
/// Items are found in the craft menu by cliloc, so the server's category/item order does not matter.
/// </remarks>
internal static class CraftTrainingPath
{
    #region Public accessors

    /// <summary>Skill at which every path ends (the shard's craft skill cap).</summary>
    public const float MAX_SKILL = 150f;

    #endregion

    #region Private members

    // Category clilocs.
    private const int BS_BLADED = 1011081;
    private const int BS_METAL_ARMOR = 1111704;
    private const int BS_THROWING = 1079508;
    private const int TL_HATS = 1011375;
    private const int TL_SHIRTS_PANTS = 1111747;
    private const int TL_MISC = 1015283;
    private const int TL_LEATHER_ARMOR = 1015293;
    private const int TL_CLOTH_ARMOR = 1111748;
    private const int TK_TOOLS = 1044046;
    private const int TK_UTENSILS = 1044048;
    private const int TK_JEWELRY = 1044049;
    private const int TK_MISC = 1044050;
    private const int CP_OTHER = 1044294;
    private const int CP_CONTAINERS = 1044292;
    private const int CP_DEEDS = 1044290;
    private const int CP_WEAPONS = 1044566;
    private const int CP_ARMOR = 1062760;
    private const int FL_MATERIALS = 1044457;
    private const int FL_WEAPONS = 1044566;
    private const int AL_ENHANCEMENT = 1116349;
    private const int AL_TOXIC = 1116350;
    private const int IN_CIRCLE_1_2 = 1111691;
    private const int IN_CIRCLE_3_4 = 1111692;
    private const int IN_CIRCLE_5_6 = 1111693;
    private const int IN_CIRCLE_7_8 = 1111694;

    private static readonly CraftPathStep[] _blacksmithy =
    [
        new(0, 45, BS_BLADED, 1023921, -0.4f, 49.6f, CraftMaterial.Ingots, 3),         // dagger
        new(45, 89, BS_BLADED, 1095362, 0f, 100f, CraftMaterial.Ingots, 3),            // gargish dagger
        new(89, 104, BS_METAL_ARMOR, 1098595, 60f, 110f, CraftMaterial.Ingots, 3),     // gargish amulet
        new(104, 118, BS_THROWING, 1095359, 75f, 125f, CraftMaterial.Ingots, 5),       // boomerang
        new(118, 150, BS_BLADED, 1095373, 75f, 150f, CraftMaterial.Ingots, 18)         // gargish talwar
    ];

    private static readonly CraftPathStep[] _tailoring =
    [
        new(0, 24, TL_HATS, 1025444, 0f, 25f, CraftMaterial.Cloth, 2),                 // skullcap
        new(24, 34, TL_HATS, 1028965, 10f, 35f, CraftMaterial.Cloth, 5),               // flower garland
        new(34, 49, TL_SHIRTS_PANTS, 1025422, 24.8f, 49.8f, CraftMaterial.Cloth, 6),   // short pants
        new(49, 54, TL_SHIRTS_PANTS, 1025398, 29f, 54f, CraftMaterial.Cloth, 10),      // skirt
        new(54, 75, TL_LEATHER_ARMOR, 1025062, 51.8f, 76.8f, CraftMaterial.Leather, 3), // leather gloves
        new(75, 97, TL_MISC, 1041498, 74.6f, 99.6f, CraftMaterial.Cloth, 1),           // oil cloth
        new(97, 126, TL_CLOTH_ARMOR, 1021031, 82.9f, 132.9f, CraftMaterial.Cloth, 6),  // gargish cloth kilt
        new(126, 150, TL_CLOTH_ARMOR, 1021029, 94f, 144f, CraftMaterial.Cloth, 8)      // gargish cloth chest
    ];

    private static readonly CraftPathStep[] _tinkering =
    [
        new(0, 30, TK_UTENSILS, 1044158, 0f, 50f, CraftMaterial.Ingots, 1),            // spoon
        new(30, 35, TK_TOOLS, 1044167, 25f, 75f, CraftMaterial.Ingots, 1),             // mapmaker's pen
        new(35, 41, TK_TOOLS, 1024138, 30f, 80f, CraftMaterial.Ingots, 1),             // hammer
        new(41, 51, TK_TOOLS, 1024028, 35f, 85f, CraftMaterial.Ingots, 1),             // tongs
        new(51, 89, TK_TOOLS, 1025371, 45f, 95f, CraftMaterial.Ingots, 1),             // lockpicks
        new(89, 108, TK_JEWELRY, 1024234, 65f, 115f, CraftMaterial.Ingots, 3),         // gold ring
        new(108, 129, TK_MISC, 1030290, 80f, 130f, CraftMaterial.Ingots, 15),          // wind chimes
        new(129, 145, TK_TOOLS, 1025371, 45f, 95f, CraftMaterial.Ingots, 1),           // lockpicks (overcap)
        new(145, 150, TK_JEWELRY, 1024234, 65f, 115f, CraftMaterial.Ingots, 3)         // gold ring (overcap)
    ];

    private static readonly CraftPathStep[] _carpentry =
    [
        new(0, 15, CP_OTHER, 1027857, 0f, 25f, CraftMaterial.Boards, 5),               // barrel staves
        new(15, 32, CP_OTHER, 1027608, 11f, 36f, CraftMaterial.Boards, 4),             // barrel lid
        new(32, 37, CP_DEEDS, 1044325, 15.7f, 40.7f, CraftMaterial.Boards, 5),         // dart board (south)
        new(37, 52, CP_CONTAINERS, 1044310, 31f, 56f, CraftMaterial.Boards, 15),       // medium crate
        new(52, 60, CP_DEEDS, 1044327, 47.3f, 72.3f, CraftMaterial.Boards, 5),         // ballot box
        new(60, 70, CP_ARMOR, 1027034, 52.6f, 77.6f, CraftMaterial.Boards, 9),         // wooden shield
        new(70, 80, CP_WEAPONS, 1030229, 60f, 85f, CraftMaterial.Boards, 6),           // fukiya
        new(80, 94, CP_WEAPONS, 1023721, 73.6f, 98.6f, CraftMaterial.Boards, 6),       // quarter staff
        new(94, 102, CP_WEAPONS, 1023568, 81.5f, 106.5f, CraftMaterial.Boards, 9),     // black staff
        new(102, 118, CP_DEEDS, 1150593, 94.7f, 119.8f, CraftMaterial.Boards, 35),     // rustic bench (south)
        new(118, 150, CP_WEAPONS, 1023713, 78.9f, 103.9f, CraftMaterial.Boards, 7)     // shepherd's crook (overcap)
    ];

    private static readonly CraftPathStep[] _fletching =
    [
        new(0, 30, FL_MATERIALS, 1027124, 0f, 40f, CraftMaterial.Boards, 1),           // shafts (whole stack per attempt)
        new(30, 61, FL_WEAPONS, 1025042, 30f, 70f, CraftMaterial.Boards, 7),           // bow
        new(61, 74, FL_WEAPONS, 1023919, 60f, 100f, CraftMaterial.Boards, 7),          // crossbow
        new(74, 105, FL_WEAPONS, 1029922, 70f, 110f, CraftMaterial.Boards, 7),         // composite bow
        new(105, 124, FL_WEAPONS, 1029923, 90f, 130f, CraftMaterial.Boards, 10),       // repeating crossbow
        new(124, 145, FL_WEAPONS, 1031562, 95f, 145f, CraftMaterial.Boards, 20),       // elven composite longbow
        new(145, 150, FL_WEAPONS, 1029922, 70f, 110f, CraftMaterial.Boards, 7)         // composite bow (overcap)
    ];

    // Spell scrolls: name cliloc is 1044381 + spell index; mana per circle from DefInscription.
    private static readonly CraftPathStep[] _inscription =
    [
        new(0, 15, IN_CIRCLE_1_2, 1044386, -25f, 25f, CraftMaterial.ScrollReagents, 1, 4),       // magic arrow
        new(15, 25, IN_CIRCLE_1_2, 1044391, -10.8f, 39.2f, CraftMaterial.ScrollReagents, 2, 6),  // cure
        new(25, 36, IN_CIRCLE_3_4, 1044398, 3.5f, 53.5f, CraftMaterial.ScrollReagents, 1, 9),    // fireball
        new(36, 50, IN_CIRCLE_3_4, 1044410, 17.8f, 67.8f, CraftMaterial.ScrollReagents, 2, 11),  // lightning
        new(50, 64, IN_CIRCLE_5_6, 1044418, 32.1f, 82.1f, CraftMaterial.ScrollReagents, 3, 14),  // paralyze
        new(64, 79, IN_CIRCLE_5_6, 1044422, 46.4f, 96.4f, CraftMaterial.ScrollReagents, 2, 20),  // energy bolt
        new(79, 93, IN_CIRCLE_7_8, 1044431, 60.7f, 110.7f, CraftMaterial.ScrollReagents, 2, 40), // flamestrike
        new(93, 150, IN_CIRCLE_7_8, 1044439, 75f, 125f, CraftMaterial.ScrollReagents, 3, 50)     // resurrection
    ];

    private static readonly CraftPathStep[] _alchemy =
    [
        new(0, 42, AL_TOXIC, 1044548, -5f, 45f, CraftMaterial.Nightshade, 1),          // lesser poison
        new(42, 61, AL_ENHANCEMENT, 1044540, 15f, 65f, CraftMaterial.Bloodmoss, 1),    // agility
        new(61, 71, AL_ENHANCEMENT, 1044546, 25f, 75f, CraftMaterial.MandrakeRoot, 2), // strength
        new(71, 101, AL_TOXIC, 1044550, 55f, 105f, CraftMaterial.Nightshade, 4),       // greater poison
        new(101, 150, AL_TOXIC, 1044551, 90f, 140f, CraftMaterial.Nightshade, 8)       // deadly poison
    ];

    #endregion

    #region Public methods

    /// <summary>The training path rows for a skill, lowest skill first.</summary>
    /// <param name="skill">The craft skill.</param>
    /// <returns>The rows; they cover 0 to <see cref="MAX_SKILL"/> without gaps.</returns>
    public static IReadOnlyList<CraftPathStep> For(CraftSkill skill) => skill switch
    {
        CraftSkill.Blacksmithy => _blacksmithy,
        CraftSkill.Tailoring => _tailoring,
        CraftSkill.Tinkering => _tinkering,
        CraftSkill.Carpentry => _carpentry,
        CraftSkill.Fletching => _fletching,
        CraftSkill.Inscription => _inscription,
        _ => _alchemy
    };

    /// <summary>Index of the row for a skill value.</summary>
    /// <param name="skill">The craft skill.</param>
    /// <param name="value">The player's skill value (with item bonuses, as the server checks it).</param>
    /// <returns>The row index; values outside the path clamp to the first or last row.</returns>
    public static int FindIndex(CraftSkill skill, float value)
    {
        IReadOnlyList<CraftPathStep> steps = For(skill);

        for (int i = 0; i < steps.Count; i++)
        {
            if (value < steps[i].ToSkill)
                return i;
        }

        return steps.Count - 1;
    }

    #endregion
}
