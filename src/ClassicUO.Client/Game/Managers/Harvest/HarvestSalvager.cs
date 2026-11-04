#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Crafting;

namespace ClassicUO.Game.Managers.Harvest;

/// <summary>
/// Smelts loose metal weapons and armor in the backpack into ingots before mining, through the
/// blacksmith menu's Smelt Item button. Driven step by step by <see cref="HarvestAgent"/>; main thread only.
/// </summary>
/// <remarks>
/// Only items lying directly in the backpack are considered, so anything inside a bag is safe. Blessed,
/// insured, artifact and quest items, tools, and items whose properties are not loaded yet are skipped,
/// as are colored metals the player's Mining or Blacksmithy is too low to smelt.
/// </remarks>
internal sealed class HarvestSalvager
{
    #region Public accessors

    /// <summary>What the salvager is doing, for the agent's status line.</summary>
    public string Status => _recycler.Status;

    /// <summary>Why the last pass stopped early, or <see cref="RecycleProblem.None"/>.</summary>
    public RecycleProblem Problem => _recycler.Problem;

    #endregion

    #region Private members

    private const int MINING_SKILL_INDEX = 45;
    private const string ARTIFACT_TEXT = "artifact";
    private const string QUEST_TEXT = "quest item";

    private static readonly CraftSkillInfo _smithing = CraftSkillInfo.Get(CraftSkill.Blacksmithy);
    private static readonly CraftingAgentConfig _smeltConfig = new() { Recycle = true };

    /// <summary>Equipment layers metal weapons and armor occupy; jewelry and clothing are left alone.</summary>
    private static readonly Layer[] _metalLayers =
    [
        Layer.OneHanded, Layer.TwoHanded, Layer.Helmet, Layer.Gloves, Layer.Neck,
        Layer.Pants, Layer.Torso, Layer.Arms
    ];

    /// <summary>ServUO resource hues and the skill its smelting check needs (iron, hue 0, needs none).</summary>
    private static readonly Dictionary<ushort, float> _metalHueDifficulty = new()
    {
        [0x0973] = 65f, // dull copper
        [0x0966] = 70f, // shadow iron
        [0x096D] = 75f, // copper
        [0x0972] = 80f, // bronze
        [0x08A5] = 85f, // gold
        [0x0979] = 90f, // agapite
        [0x089F] = 95f, // verite
        [0x08AB] = 99f  // valorite
    };

    private readonly CraftRecycler _recycler = new();
    private readonly CraftWorld _craftWorld = new();
    private readonly List<uint> _pending = new();

    /// <summary>Items already tried this run; ones the server refused (leather, say) are not retried.</summary>
    private readonly HashSet<uint> _tried = new();

    #endregion

    #region Public methods

    /// <summary>Forgets which items were tried; called when the agent starts.</summary>
    public void Reset() => _tried.Clear();

    /// <summary>Starts a pass when there is something to smelt, a forge in reach and a smithing tool.</summary>
    /// <param name="world">The world.</param>
    /// <returns>False when there is nothing to do here.</returns>
    public bool Begin(World world)
    {
        _pending.Clear();
        PlayerMobile player = world.Player;

        if (player.Backpack == null || !HarvestLoadHandler.HasForgeInReach(world) || CraftWorld.FindTool(player, _smithing) == null)
            return false;

        float skill = Math.Max(player.Skills[MINING_SKILL_INDEX].Value, player.Skills[_smithing.SkillIndex].Value);

        for (LinkedObject? node = player.Backpack.Items; node != null; node = node.Next)
        {
            var item = (Item)node;

            if (!_tried.Contains(item.Serial) && IsSmeltable(world, item, skill))
            {
                _pending.Add(item.Serial);
                _tried.Add(item.Serial);
            }
        }

        if (_pending.Count == 0)
            return false;

        _recycler.Begin(_smithing, _smeltConfig, _pending);
        return true;
    }

    /// <summary>Advances the pass by at most one action.</summary>
    /// <param name="world">The world.</param>
    /// <returns>True while still working.</returns>
    public bool Step(World world) => _recycler.Step(world, _craftWorld);

    /// <summary>Passes on a localized server message that may answer a smelt.</summary>
    /// <param name="cliloc">The cliloc number.</param>
    public void OnCliloc(int cliloc)
    {
        CraftResult result = CraftMessages.Classify(cliloc);

        if (result != CraftResult.None)
            _recycler.OnResult(result);
    }

    #endregion

    #region Private methods

    private static bool IsSmeltable(World world, Item item, float skill)
    {
        if (item.IsDestroyed || !item.ItemData.IsWearable || Array.IndexOf(_metalLayers, (Layer)item.ItemData.Layer) < 0)
            return false;

        if (HarvestData.IsPickaxe(item.Graphic) || HarvestData.IsShovel(item.Graphic) || HarvestData.IsAxe(item.Graphic)
            || _smithing.IsTool(item.Graphic))
            return false;

        ushort hue = (ushort)(item.Hue & 0x3FFF);

        // Dyed or magic-hued items are left to the server, which refuses metals the player can't work.
        if (_metalHueDifficulty.TryGetValue(hue, out float difficulty) && difficulty > skill)
            return false;

        // Unloaded properties could hide "blessed" or "insured"; such items wait for a later pass.
        if (!world.OPL.TryGetNameAndData(item.Serial, out _, out string data) || data == null)
            return false;

        return CraftWorld.IsDisposable(world, item.Serial)
               && !data.Contains(ARTIFACT_TEXT, StringComparison.OrdinalIgnoreCase)
               && !data.Contains(QUEST_TEXT, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
