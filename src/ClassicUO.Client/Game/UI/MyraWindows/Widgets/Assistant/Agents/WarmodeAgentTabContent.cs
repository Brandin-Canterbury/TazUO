#nullable enable

using System.Collections.Generic;
using ClassicUO.Common;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers.Warmode;
using Myra.Graphics2D;
using Myra.Graphics2D.UI;

namespace ClassicUO.Game.UI.MyraWindows.Widgets.Assistant.Agents;

/// <summary>Assistant tab for the Warmode agent: on/off, live status, options and the priority list.</summary>
public static class WarmodeAgentTabContent
{
    #region Public methods

    /// <summary>Builds the tab.</summary>
    /// <returns>The tab's root widget.</returns>
    public static Widget Build()
    {
        Profile? profile = ProfileManager.CurrentProfile;

        if (profile == null)
            return new MyraLabel(TazLang.Get("warmodeagent_profilenotloaded"), MyraLabel.TextStyle.P);

        WarmodeAgentConfig config = profile.WarmodeAgent;
        var root = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

        root.Widgets.Add(new MyraLabel(TazLang.Get("warmodeagent_desc"), MyraLabel.TextStyle.P) { Wrap = true });
        root.Widgets.Add(MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.Enabled),
            TazLang.Get("warmodeagent_enable"),
            TazLang.Get("warmodeagent_enable_tooltip")
        ));
        root.Widgets.Add(new WarmodeStatusLabel());
        root.Widgets.Add(BuildOptions(config));
        root.Widgets.Add(BuildSpellSection(config));

        var list = new VerticalStackPanel { Spacing = 2 };
        root.Widgets.Add(new MyraLabel(TazLang.Get("warmodeagent_list_title"), MyraLabel.TextStyle.H2));
        root.Widgets.Add(new MyraLabel(TazLang.Get("warmodeagent_list_help"), MyraLabel.TextStyle.P) { Wrap = true });
        root.Widgets.Add(BuildAddRow(config, list));
        root.Widgets.Add(list);
        FillList(config, list);

        return root;
    }

    #endregion

    #region Private methods

    private static Widget BuildOptions(WarmodeAgentConfig config)
    {
        var panel = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

        panel.Widgets.Add(new LabeledIntegerInput(TazLang.Get("warmodeagent_range"), config.Range, v => config.Range = v)
        {
            MinValue = 1,
            MaxValue = 18
        });

        panel.Widgets.Add(MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.OnlyFighting),
            TazLang.Get("warmodeagent_onlyfighting"),
            TazLang.Get("warmodeagent_onlyfighting_tooltip")
        ));

        panel.Widgets.Add(MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.SwitchToHigherPriority),
            TazLang.Get("warmodeagent_switch"),
            TazLang.Get("warmodeagent_switch_tooltip")
        ));

        panel.Widgets.Add(MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.AutoAddAttackers),
            TazLang.Get("warmodeagent_autoadd"),
            TazLang.Get("warmodeagent_autoadd_tooltip")
        ));

        return panel;
    }

    private static Widget BuildSpellSection(WarmodeAgentConfig config)
    {
        var panel = new VerticalStackPanel { Spacing = 2 };
        var list = new VerticalStackPanel { Spacing = 2 };
        var row = new HorizontalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        var input = new MyraInputBox { Width = 180 };

        panel.Widgets.Add(MyraCheckButton.CreatePropBoundCheckButton(
            new Accessor<bool>(() => config.AutoTargetSpells),
            TazLang.Get("warmodeagent_spells"),
            TazLang.Get("warmodeagent_spells_tooltip")
        ));
        panel.Widgets.Add(new MyraLabel(TazLang.Get("warmodeagent_spells_help"), MyraLabel.TextStyle.P) { Wrap = true });

        row.Widgets.Add(input);
        row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_spells_add"), () =>
        {
            string name = (input.Text ?? string.Empty).Trim();

            if (name.Length > 0 && !config.TargetSpells.Exists(s => string.Equals(s, name, System.StringComparison.OrdinalIgnoreCase)))
                config.TargetSpells.Add(name);

            input.Text = string.Empty;
            FillSpellList(config, list);
        }));

        panel.Widgets.Add(row);
        panel.Widgets.Add(list);
        FillSpellList(config, list);
        return panel;
    }

    private static void FillSpellList(WarmodeAgentConfig config, VerticalStackPanel list)
    {
        list.Widgets.Clear();

        if (config.TargetSpells.Count == 0)
        {
            list.Widgets.Add(new MyraLabel(TazLang.Get("warmodeagent_spells_all"), MyraLabel.TextStyle.P));
            return;
        }

        for (int i = 0; i < config.TargetSpells.Count; i++)
        {
            int index = i;
            var row = new HorizontalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

            row.Widgets.Add(new MyraLabel(config.TargetSpells[i], MyraLabel.TextStyle.P) { Width = 200 });
            row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_remove"), () =>
            {
                if (index < config.TargetSpells.Count)
                    config.TargetSpells.RemoveAt(index);

                FillSpellList(config, list);
            }));

            list.Widgets.Add(row);
        }
    }

    private static Widget BuildAddRow(WarmodeAgentConfig config, VerticalStackPanel list)
    {
        var row = new HorizontalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        var input = new MyraInputBox { Width = 180 };

        row.Widgets.Add(input);
        row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_add"), () =>
        {
            if (WarmodeAgent.Instance.AddName(config, input.Text ?? string.Empty))
                input.Text = string.Empty;

            FillList(config, list);
        }));

        row.Widgets.Add(new TargetSelectionButton(
            serial =>
            {
                Mobile? mobile = serial is { } s ? World.Instance?.Mobiles.Get(s) : null;

                if (mobile != null)
                    WarmodeAgent.Instance.AddName(config, WarmodeAgent.Instance.ListNameFor(mobile));

                FillList(config, list);
            },
            entity => entity is Mobile,
            TazLang.Get("warmodeagent_addtarget"),
            TazLang.Get("warmodeagent_addtarget_tooltip")
        ));

        row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_refresh"), () => FillList(config, list)));
        return row;
    }

    private static void FillList(WarmodeAgentConfig config, VerticalStackPanel list)
    {
        list.Widgets.Clear();
        List<WarmodePriority> entries = config.Priorities;

        if (entries.Count == 0)
        {
            list.Widgets.Add(new MyraLabel(TazLang.Get("warmodeagent_list_empty"), MyraLabel.TextStyle.P));
            return;
        }

        for (int i = 0; i < entries.Count; i++)
            list.Widgets.Add(BuildEntryRow(config, list, i));
    }

    private static Widget BuildEntryRow(WarmodeAgentConfig config, VerticalStackPanel list, int index)
    {
        List<WarmodePriority> entries = config.Priorities;
        WarmodePriority entry = entries[index];
        var row = new HorizontalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

        row.Widgets.Add(new MyraLabel($"{index + 1}. {entry.Name}", MyraLabel.TextStyle.P) { Width = 200 });
        row.Widgets.Add(MyraCheckButton.CreateWithCallback(
            entry.Ignore,
            ignore => entry.Ignore = ignore,
            TazLang.Get("warmodeagent_ignore"),
            TazLang.Get("warmodeagent_ignore_tooltip")
        ));

        row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_top"), () => Move(config, list, index, 0)));
        row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_up"), () => Move(config, list, index, index - 1)));
        row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_down"), () => Move(config, list, index, index + 1)));
        row.Widgets.Add(new MyraButton(TazLang.Get("warmodeagent_remove"), () =>
        {
            if (index < entries.Count)
                entries.RemoveAt(index);

            FillList(config, list);
        }));

        return row;
    }

    private static void Move(WarmodeAgentConfig config, VerticalStackPanel list, int from, int to)
    {
        List<WarmodePriority> entries = config.Priorities;

        if (from < 0 || from >= entries.Count || to < 0 || to >= entries.Count || from == to)
            return;

        WarmodePriority entry = entries[from];
        entries.RemoveAt(from);
        entries.Insert(to, entry);
        FillList(config, list);
    }

    #endregion

    /// <summary>Label that mirrors <see cref="WarmodeAgent.Status"/> while it is on screen.</summary>
    /// <remarks>Polls at render time so the static agent never holds a reference to a closed tab.</remarks>
    private sealed class WarmodeStatusLabel : MyraLabel
    {
        private string _shown = string.Empty;

        public WarmodeStatusLabel() : base(string.Empty, TextStyle.P)
        {
            Wrap = true;
        }

        public override void InternalRender(RenderContext context)
        {
            string status = WarmodeAgent.Instance.Status;

            if (!ReferenceEquals(status, _shown))
            {
                _shown = status;
                Text = TazLang.GetEx("warmodeagent_status_label", "Status: {0}", [status]);
            }

            base.InternalRender(context);
        }
    }
}
