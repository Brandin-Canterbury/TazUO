#nullable enable

using ClassicUO.Common;
using ClassicUO.Configuration;
using ClassicUO.Game.Managers.Harvest;
using Myra.Graphics2D;
using Myra.Graphics2D.UI;

namespace ClassicUO.Game.UI.MyraWindows.Widgets.Assistant.Agents;

/// <summary>Assistant tab for the Harvest agent: mode, start/stop, live status and settings.</summary>
public static class HarvestAgentTabContent
{
    #region Public methods

    /// <summary>Builds the tab.</summary>
    /// <returns>The tab's root widget.</returns>
    public static Widget Build()
    {
        Profile? profile = ProfileManager.CurrentProfile;

        if (profile == null)
            return new MyraLabel(TazLang.Get("harvestagent_profilenotloaded"), MyraLabel.TextStyle.P);

        HarvestAgentConfig config = profile.HarvestAgent;
        var root = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

        root.Widgets.Add(new MyraLabel(TazLang.Get("harvestagent_desc"), MyraLabel.TextStyle.P) { Wrap = true });
        root.Widgets.Add(BuildControlRow());
        root.Widgets.Add(new HarvestStatusLabel());
        root.Widgets.Add(BuildModeGroup(config));
        root.Widgets.Add(BuildUnloadOptions(config));
        root.Widgets.Add(BuildHighlightGroup(config));
        root.Widgets.Add(new MyraLabel(TazLang.Get("harvestagent_usage"), MyraLabel.TextStyle.P) { Wrap = true });

        return root;
    }

    #endregion

    #region Private methods

    private static Widget BuildControlRow()
    {
        var row = new HorizontalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        row.Widgets.Add(new MyraButton(TazLang.Get("harvestagent_start"), () => HarvestAgent.Instance.Start()));
        row.Widgets.Add(new MyraButton(TazLang.Get("harvestagent_stop"), () => HarvestAgent.Instance.Stop()));
        return row;
    }

    private static Widget BuildModeGroup(HarvestAgentConfig config)
    {
        var oreMargin = new LabeledIntegerInput(TazLang.Get("harvestagent_oremargin"), config.OreWeightMargin, v => config.OreWeightMargin = v)
        {
            MinValue = 0,
            MaxValue = 200
        };

        var logMargin = new LabeledIntegerInput(TazLang.Get("harvestagent_logmargin"), config.LogWeightMargin, v => config.LogWeightMargin = v)
        {
            MinValue = 0,
            MaxValue = 200
        };

        Widget smelt = MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.SmeltOre),
            TazLang.Get("harvestagent_smelt"),
            TazLang.Get("harvestagent_smelt_tooltip")
        );

        Widget smeltItems = MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.SmeltItems),
            TazLang.Get("harvestagent_smeltitems"),
            TazLang.Get("harvestagent_smeltitems_tooltip")
        );

        var modes = new RadioGroup<HarvestMode>(
            TazLang.Get("harvestagent_mode_label"),
            config.Mode,
            new RadioOption<HarvestMode>(HarvestMode.Mining, TazLang.Get("harvestagent_mode_mining"), null, oreMargin, smelt, smeltItems),
            new RadioOption<HarvestMode>(HarvestMode.Lumberjacking, TazLang.Get("harvestagent_mode_lumberjacking"), null, logMargin)
        );

        modes.SelectionChanged += (_, mode) => config.Mode = mode;
        return modes;
    }

    private static Widget BuildUnloadOptions(HarvestAgentConfig config)
    {
        var panel = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

        panel.Widgets.Add(MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.UsePackAnimal),
            TazLang.Get("harvestagent_packanimal"),
            TazLang.Get("harvestagent_packanimal_tooltip")
        ));

        panel.Widgets.Add(MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.ClearHandsForTool),
            TazLang.Get("harvestagent_clearhands"),
            TazLang.Get("harvestagent_clearhands_tooltip")
        ));

        return panel;
    }

    private static Widget BuildHighlightGroup(HarvestAgentConfig config)
    {
        var hueRow = new HorizontalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        var hueSelector = new HueSelector(config.DepletedHue);
        hueSelector.HueChanged += (_, hue) => config.DepletedHue = hue;

        hueRow.Widgets.Add(new MyraLabel(TazLang.Get("harvestagent_highlight_hue"), MyraLabel.TextStyle.P));
        hueRow.Widgets.Add(hueSelector);
        hueRow.Widgets.Add(new MyraButton(
            TazLang.Get("harvestagent_clear_depleted"),
            () => HarvestDepletionTracker.Instance.Clear()
        ));

        return new CheckBoxGroup(
            new PropertyBinder(
                new Accessor<bool>(() => config.HighlightDepleted),
                TazLang.Get("harvestagent_highlight"),
                TazLang.Get("harvestagent_highlight_tooltip")
            ),
            hueRow
        );
    }

    #endregion

    /// <summary>Label that mirrors <see cref="HarvestAgent.Status"/> while it is on screen.</summary>
    /// <remarks>Polls at render time so the static agent never holds a reference to a closed tab.</remarks>
    private sealed class HarvestStatusLabel : MyraLabel
    {
        private string _shown = string.Empty;

        public HarvestStatusLabel() : base(string.Empty, TextStyle.P)
        {
            Wrap = true;
        }

        public override void InternalRender(RenderContext context)
        {
            string status = HarvestAgent.Instance.Status;

            if (!ReferenceEquals(status, _shown))
            {
                _shown = status;
                Text = TazLang.GetEx("harvestagent_status_label", "Status: {0}", [status]);
            }

            base.InternalRender(context);
        }
    }
}
