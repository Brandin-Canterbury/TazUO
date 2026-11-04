#nullable enable

using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Managers.Crafting;
using Microsoft.Xna.Framework;
using Myra.Graphics2D;
using Myra.Graphics2D.UI;

namespace ClassicUO.Game.UI.MyraWindows.Widgets.Assistant.Agents;

/// <summary>
/// The training path of one craft skill as a table, with the row for the player's current skill
/// highlighted.
/// </summary>
/// <remarks>Polls the player's skill at render time, so it never needs to be told about gains.</remarks>
internal sealed class CraftingPathTable : MyraGrid
{
    #region Private members

    private static readonly Color HighlightColor = Color.Gold;

    private readonly CraftSkill _skill;
    private readonly List<MyraLabel[]> _rowLabels = new();
    private Color _normalColor = Color.White;
    private int _highlighted = -1;

    #endregion

    #region Ctor

    /// <param name="skill">The craft skill whose path to show.</param>
    public CraftingPathTable(CraftSkill skill)
    {
        _skill = skill;

        SetupWithHeaders(
            GridColumnInfo.Auto(TazLang.Get("craftagent_table_skill")),
            GridColumnInfo.Fill(TazLang.Get("craftagent_table_item"), 2),
            GridColumnInfo.Auto(TazLang.Get("craftagent_table_range")),
            GridColumnInfo.Fill(TazLang.Get("craftagent_table_cost"))
        );

        IReadOnlyList<CraftPathStep> steps = CraftTrainingPath.For(skill);

        for (int i = 0; i < steps.Count; i++)
            AddStepRow(steps[i], i + 1);
    }

    #endregion

    #region Public methods

    /// <inheritdoc />
    public override void InternalRender(RenderContext context)
    {
        UpdateHighlight();
        base.InternalRender(context);
    }

    #endregion

    #region Private methods

    private void AddStepRow(CraftPathStep step, int row)
    {
        MyraLabel[] labels =
        [
            Cell($"{step.FromSkill:0}-{step.ToSkill:0}"),
            Cell(ItemText(step)),
            Cell($"{step.ItemMin:0.#}-{step.ItemMax:0.#}"),
            Cell(CostText(step))
        ];

        for (int col = 0; col < labels.Length; col++)
            AddWidget(labels[col], row, col);

        _normalColor = labels[0].TextColor;
        _rowLabels.Add(labels);
    }

    private static MyraLabel Cell(string text) => new(text, MyraLabel.TextStyle.P) { Wrap = true };

    private static string ItemText(CraftPathStep step)
    {
        string item = Client.Game.UO.FileManager.Clilocs.GetString(step.ItemCliloc) ?? step.ItemCliloc.ToString();
        string group = Client.Game.UO.FileManager.Clilocs.GetString(step.GroupCliloc) ?? string.Empty;

        return TazLang.GetEx("craftagent_table_itemfmt", "{0} ({1})", [item, StripHtml(group)]);
    }

    private static string CostText(CraftPathStep step)
    {
        string material = TazLang.Get("craftagent_material_" + step.Material.ToString().ToLowerInvariant());
        string cost = TazLang.GetEx("craftagent_table_costfmt", "{0} {1}", [step.Amount.ToString(), material]);

        if (step.ManaCost > 0)
            cost = TazLang.GetEx("craftagent_table_manafmt", "{0}, {1} mana", [cost, step.ManaCost.ToString()]);

        return cost;
    }

    /// <summary>Category clilocs carry centering tags for the craft gump; the table wants plain text.</summary>
    private static string StripHtml(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        bool inTag = false;

        foreach (char character in text)
        {
            if (character == '<')
                inTag = true;
            else if (character == '>')
                inTag = false;
            else if (!inTag)
                builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private void UpdateHighlight()
    {
        float? skillValue = CurrentSkill();
        int index = skillValue == null ? -1 : CraftTrainingPath.FindIndex(_skill, skillValue.Value);

        if (index == _highlighted)
            return;

        if (_highlighted >= 0 && _highlighted < _rowLabels.Count)
            SetRowColor(_rowLabels[_highlighted], _normalColor);

        if (index >= 0 && index < _rowLabels.Count)
            SetRowColor(_rowLabels[index], HighlightColor);

        _highlighted = index;
    }

    private float? CurrentSkill()
    {
        Game.GameObjects.PlayerMobile? player = World.Instance?.Player;
        int skillIndex = CraftSkillInfo.Get(_skill).SkillIndex;

        if (player == null || skillIndex >= player.Skills.Length || player.Skills[skillIndex] == null)
            return null;

        return player.Skills[skillIndex].Value;
    }

    private static void SetRowColor(MyraLabel[] labels, Color color)
    {
        foreach (MyraLabel label in labels)
            label.TextColor = color;
    }

    #endregion
}
