#nullable enable

using System;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>Kind of resource bank a harvestable tile draws from.</summary>
public enum HarvestResourceKind : byte
{
    Ore,
    Wood
}

/// <summary>
/// Shard-side harvest facts the client has to mirror: harvestable tile IDs, resource bank sizes,
/// respawn times and the graphics of tools, resources, forges and helper animals.
/// </summary>
/// <remarks>
/// Values follow ServUO's <c>Mining.cs</c>, <c>Lumberjacking.cs</c> and <c>Ore.cs</c>. The server pools
/// resources per bank (a grid cell of the map), not per tile, so "empty" always applies to the whole
/// bank. Static IDs are compared the way the server does, masked to 14 bits.
/// </remarks>
internal static class HarvestData
{
    #region Public accessors

    /// <summary>Ore banks are 8x8 tiles.</summary>
    public const int ORE_BANK_WIDTH = 8;
    public const int ORE_BANK_HEIGHT = 8;

    /// <summary>Wood banks are 4x3 tiles.</summary>
    public const int WOOD_BANK_WIDTH = 4;
    public const int WOOD_BANK_HEIGHT = 3;

    /// <summary>Tiles from the player a harvest target may be (server <c>MaxRange</c>).</summary>
    public const int HARVEST_RANGE = 2;

    /// <summary>Tiles from the player a forge, fire beetle or pack animal may be.</summary>
    public const int HELPER_RANGE = 2;

    /// <summary>Upper respawn bound for ore banks; the server picks 10-20 minutes.</summary>
    public static readonly TimeSpan OreRespawn = TimeSpan.FromMinutes(20);

    /// <summary>Upper respawn bound for wood banks; the server picks 20-30 minutes.</summary>
    public static readonly TimeSpan WoodRespawn = TimeSpan.FromMinutes(30);

    public const ushort FIRE_BEETLE_BODY = 0x00A9;
    public const ushort PACK_HORSE_BODY = 0x0123;
    public const ushort PACK_LLAMA_BODY = 0x0124;
    public const ushort GIANT_BEETLE_BODY = 0x0317;

    #endregion

    #region Private members

    private const int STATIC_ID_MASK = 0x3FFF;

    /// <summary>Inclusive ranges from ServUO's mountain/cave list; values at 0x4000+ are statics.</summary>
    private static readonly ushort[] MINING_TILE_RANGES =
    [
        0x00DC, 0x00E7, 0x00EC, 0x00F7, 0x00FC, 0x0107, 0x010C, 0x0117,
        0x011E, 0x0126, 0x0128, 0x0129, 0x0141, 0x0144, 0x01D3, 0x01DA,
        0x01DC, 0x01E7, 0x01EC, 0x01EF, 0x021F, 0x0243, 0x0245, 0x0259,
        0x0262, 0x0265, 0x03F2, 0x03F2, 0x06CD, 0x06DD, 0x06EB, 0x06FE,
        0x0709, 0x0711, 0x0713, 0x0720, 0x0727, 0x073E, 0x0745, 0x075C,
        0x07BD, 0x07D4, 0x07EC, 0x07F1, 0x0834, 0x0839, 0x453B, 0x454F,
    ];

    /// <summary>Inclusive ranges from ServUO's tree list; all are statics.</summary>
    private static readonly ushort[] TREE_TILE_RANGES =
    [
        0x4CCA, 0x4CE8, 0x4CF8, 0x4D03, 0x4D41, 0x4D53, 0x4D57, 0x4D69,
        0x4D6E, 0x4D7F, 0x4D84, 0x4D90, 0x4D95, 0x4D97, 0x4D99, 0x4D9B,
        0x4D9D, 0x4D9F, 0x4DA1, 0x4DA3, 0x4DA5, 0x4DA7, 0x4DA9, 0x4DAB,
        0x52B5, 0x52C7,
    ];

    private static readonly ushort[] PICKAXE_GRAPHICS = [0x0E85, 0x0E86];
    private static readonly ushort[] SHOVEL_GRAPHICS = [0x0F39, 0x0F3A];

    /// <summary>Every ServUO <c>BaseAxe</c> except pickaxes, both facings.</summary>
    private static readonly ushort[] AXE_GRAPHICS =
    [
        0x0F43, 0x0F44, // hatchet
        0x0F49, 0x0F4A, // axe
        0x0F47, 0x0F48, // battle axe
        0x0F4B, 0x0F4C, // double axe
        0x0F45, 0x0F46, // executioner's axe
        0x13FA, 0x13FB, // large battle axe
        0x1442, 0x1443, // two handed axe
        0x13AF, 0x13B0, // war axe
        0x2D28, 0x2D34, // ornate axe
        0x08FD, 0x4068, // dual short axes
        0x48B0, 0x48B1, // gargish battle axe
        0x48B2, 0x48B3, // gargish axe
    ];

    private static readonly bool[] _mineableLand = new bool[STATIC_ID_MASK + 1];
    private static readonly bool[] _mineableStatic = new bool[STATIC_ID_MASK + 1];
    private static readonly bool[] _treeStatic = new bool[STATIC_ID_MASK + 1];

    #endregion

    #region Ctor

    static HarvestData()
    {
        for (int i = 0; i < MINING_TILE_RANGES.Length; i += 2)
        {
            for (int id = MINING_TILE_RANGES[i]; id <= MINING_TILE_RANGES[i + 1]; id++)
            {
                if (id >= 0x4000)
                    _mineableStatic[id & STATIC_ID_MASK] = true;
                else
                    _mineableLand[id] = true;
            }
        }

        for (int i = 0; i < TREE_TILE_RANGES.Length; i += 2)
        {
            for (int id = TREE_TILE_RANGES[i]; id <= TREE_TILE_RANGES[i + 1]; id++)
                _treeStatic[id & STATIC_ID_MASK] = true;
        }
    }

    #endregion

    #region Public methods

    /// <summary>True for a land tile ID the server accepts for ore mining.</summary>
    /// <param name="landGraphic">The map's land tile ID (not a seasonal replacement).</param>
    public static bool IsMineableLand(ushort landGraphic) => landGraphic <= STATIC_ID_MASK && _mineableLand[landGraphic];

    /// <summary>True for a static the server accepts for ore mining (cave rocks).</summary>
    /// <param name="staticGraphic">The map's static ID (not a seasonal replacement).</param>
    public static bool IsMineableStatic(ushort staticGraphic) => _mineableStatic[staticGraphic & STATIC_ID_MASK];

    /// <summary>True for a static the server accepts for lumberjacking.</summary>
    /// <param name="staticGraphic">The map's static ID (not a seasonal replacement).</param>
    public static bool IsTree(ushort staticGraphic) => _treeStatic[staticGraphic & STATIC_ID_MASK];

    /// <summary>True for an item or static graphic ServUO treats as a forge when smelting.</summary>
    /// <param name="graphic">Item or static graphic.</param>
    public static bool IsForge(ushort graphic) => graphic == 0x0FB1 || (graphic >= 0x197A && graphic <= 0x19A9);

    /// <summary>True for a pickaxe graphic.</summary>
    /// <param name="graphic">Item graphic.</param>
    public static bool IsPickaxe(ushort graphic) => Contains(PICKAXE_GRAPHICS, graphic);

    /// <summary>True for a shovel graphic.</summary>
    /// <param name="graphic">Item graphic.</param>
    public static bool IsShovel(ushort graphic) => Contains(SHOVEL_GRAPHICS, graphic);

    /// <summary>True for an axe that chops wood (pickaxes excluded).</summary>
    /// <param name="graphic">Item graphic.</param>
    public static bool IsAxe(ushort graphic) => Contains(AXE_GRAPHICS, graphic);

    /// <summary>True for any ore pile.</summary>
    /// <param name="graphic">Item graphic.</param>
    public static bool IsOre(ushort graphic) => graphic >= 0x19B7 && graphic <= 0x19BA;

    /// <summary>True for an ingot.</summary>
    /// <param name="graphic">Item graphic.</param>
    public static bool IsIngot(ushort graphic) => graphic >= 0x1BEF && graphic <= 0x1BF4;

    /// <summary>True for granite.</summary>
    /// <param name="graphic">Item graphic.</param>
    public static bool IsGranite(ushort graphic) => graphic == 0x1779;

    /// <summary>True for logs or boards.</summary>
    /// <param name="graphic">Item graphic.</param>
    public static bool IsWood(ushort graphic) => graphic >= 0x1BD7 && graphic <= 0x1BE2;

    /// <summary>True for a pack horse, pack llama or giant beetle body.</summary>
    /// <param name="body">Mobile body graphic.</param>
    public static bool IsPackAnimal(ushort body) =>
        body == PACK_HORSE_BODY || body == PACK_LLAMA_BODY || body == GIANT_BEETLE_BODY;

    /// <summary>Bank width and height for a resource kind.</summary>
    /// <param name="kind">The resource kind.</param>
    /// <returns>The bank size in tiles.</returns>
    public static (int Width, int Height) BankSize(HarvestResourceKind kind) =>
        kind == HarvestResourceKind.Ore ? (ORE_BANK_WIDTH, ORE_BANK_HEIGHT) : (WOOD_BANK_WIDTH, WOOD_BANK_HEIGHT);

    #endregion

    #region Private methods

    private static bool Contains(ushort[] graphics, ushort graphic)
    {
        for (int i = 0; i < graphics.Length; i++)
        {
            if (graphics[i] == graphic)
                return true;
        }

        return false;
    }

    #endregion
}
