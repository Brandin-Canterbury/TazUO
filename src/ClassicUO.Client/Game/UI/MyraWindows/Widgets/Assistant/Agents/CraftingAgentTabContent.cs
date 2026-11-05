#nullable enable

using System;
using ClassicUO.Common;
using ClassicUO.Configuration;
using ClassicUO.Game.Managers.Crafting;
using Myra.Graphics2D;
using Myra.Graphics2D.UI;

namespace ClassicUO.Game.UI.MyraWindows.Widgets.Assistant.Agents;

/// <summary>Assistant tab for the Crafting agent: skill, training path, start/stop, status and settings.</summary>
public static class CraftingAgentTabContent
{
    #region Private members

    private const int MAX_TARGET_SKILL = 150;
    private const int MAX_RECYCLE_EVERY = 500;
    private const int MAX_WEIGHT_MARGIN = 200;

    #endregion

    #region Public methods

    /// <summary>Builds the tab.</summary>
    /// <returns>The tab's root widget.</returns>
    public static Widget Build()
    {
        Profile? profile = ProfileManager.CurrentProfile;

        if (profile == null)
            return new MyraLabel(TazLang.Get("craftagent_profilenotloaded"), MyraLabel.TextStyle.P);

        CraftingAgentConfig config = profile.CraftingAgent;
        var root = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        var pathHolder = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        pathHolder.Widgets.Add(new CraftingPathTable(config.Skill));

        root.Widgets.Add(new MyraLabel(TazLang.Get("craftagent_desc"), MyraLabel.TextStyle.P) { Wrap = true });
        root.Widgets.Add(BuildControlRow());
        root.Widgets.Add(new CraftingStatusLabel());
        root.Widgets.Add(BuildSkillGroup(config, pathHolder));
        root.Widgets.Add(new MyraLabel(TazLang.Get("craftagent_path_heading"), MyraLabel.TextStyle.H4));
        root.Widgets.Add(new MyraLabel(TazLang.Get("craftagent_path_desc"), MyraLabel.TextStyle.P) { Wrap = true });
        root.Widgets.Add(pathHolder);
        root.Widgets.Add(BuildOptions(config));
        root.Widgets.Add(new MyraLabel(TazLang.Get("craftagent_usage"), MyraLabel.TextStyle.P) { Wrap = true });

        return root;
    }

    #endregion

    #region Private methods

    private static Widget BuildControlRow()
    {
        var row = new HorizontalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        row.Widgets.Add(new MyraButton(TazLang.Get("craftagent_start"), () => CraftingAgent.Instance.Start()));
        row.Widgets.Add(new MyraButton(TazLang.Get("craftagent_stop"), () => CraftingAgent.Instance.Stop()));
        return row;
    }

    private static Widget BuildSkillGroup(CraftingAgentConfig config, VerticalStackPanel pathHolder)
    {
        CraftSkill[] all = Enum.GetValues<CraftSkill>();
        var options = new RadioOption<CraftSkill>[all.Length];

        for (int i = 0; i < all.Length; i++)
            options[i] = new RadioOption<CraftSkill>(all[i], TazLang.Get(CraftSkillInfo.Get(all[i]).NameKey));

        var skills = new RadioGroup<CraftSkill>(TazLang.Get("craftagent_skill_label"), config.Skill, options);

        skills.SelectionChanged += (_, skill) =>
        {
            config.Skill = skill;
            pathHolder.Widgets.Clear();
            pathHolder.Widgets.Add(new CraftingPathTable(skill));
        };

        return skills;
    }

    private static Widget BuildOptions(CraftingAgentConfig config)
    {
        var panel = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

        panel.Widgets.Add(new LabeledIntegerInput(
            TazLang.Get("craftagent_target"),
            (int)config.TargetSkill,
            value => config.TargetSkill = value
        ) { MinValue = 0, MaxValue = MAX_TARGET_SKILL });

        var recycleEvery = new LabeledIntegerInput(
            TazLang.Get("craftagent_recycle_every"),
            config.RecycleEvery,
            value => config.RecycleEvery = value
        ) { MinValue = 1, MaxValue = MAX_RECYCLE_EVERY };

        panel.Widgets.Add(new CheckBoxGroup(
            new PropertyBinder(
                new Accessor<bool>(() => config.Recycle),
                TazLang.Get("craftagent_recycle"),
                TazLang.Get("craftagent_recycle_tooltip")
            ),
            recycleEvery
        ));

        var disposal = new RadioGroup<CraftDisposal>(
            TazLang.Get("craftagent_disposal_label"),
            config.Disposal,
            new RadioOption<CraftDisposal>(CraftDisposal.Keep, TazLang.Get("craftagent_disposal_keep")),
            new RadioOption<CraftDisposal>(CraftDisposal.DropAtFeet, TazLang.Get("craftagent_disposal_drop")),
            new RadioOption<CraftDisposal>(
                CraftDisposal.TrashBarrel,
                TazLang.Get("craftagent_disposal_trash"),
                TazLang.Get("craftagent_disposal_trash_tooltip")
            )
        );

        disposal.SelectionChanged += (_, value) => config.Disposal = value;
        panel.Widgets.Add(disposal);

        var restockCrafts = new LabeledIntegerInput(
            TazLang.Get("craftagent_restock_crafts"),
            config.RestockCrafts,
            value => config.RestockCrafts = value
        ) { MinValue = 1, MaxValue = 500 };

        panel.Widgets.Add(new CheckBoxGroup(
            new PropertyBinder(
                new Accessor<bool>(() => config.PullFromContainers),
                TazLang.Get("craftagent_restock"),
                TazLang.Get("craftagent_restock_tooltip")
            ),
            restockCrafts
        ));

        panel.Widgets.Add(new LabeledIntegerInput(
            TazLang.Get("craftagent_weightmargin"),
            config.WeightMargin,
            value => config.WeightMargin = value
        ) { MinValue = 0, MaxValue = MAX_WEIGHT_MARGIN });

        return panel;
    }

    #endregion

    /// <summary>Label that mirrors <see cref="CraftingAgent.Status"/> while it is on screen.</summary>
    /// <remarks>Polls at render time so the static agent never holds a reference to a closed tab.</remarks>
    private sealed class CraftingStatusLabel : MyraLabel
    {
        private string _shownStatus = string.Empty;
        private int _shownAttempts = -1;

        public CraftingStatusLabel() : base(string.Empty, TextStyle.P)
        {
            Wrap = true;
        }

        public override void InternalRender(RenderContext context)
        {
            CraftingAgent agent = CraftingAgent.Instance;

            if (!ReferenceEquals(agent.Status, _shownStatus) || agent.Attempts != _shownAttempts)
            {
                _shownStatus = agent.Status;
                _shownAttempts = agent.Attempts;
                Text = TazLang.GetEx("craftagent_status_label", "Status: {0} ({1} attempts)", [_shownStatus, _shownAttempts.ToString()]);
            }

            base.InternalRender(context);
        }
    }
}
