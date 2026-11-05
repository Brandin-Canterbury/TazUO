#nullable enable

using ClassicUO.Configuration;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using Myra.Graphics2D.UI;

namespace ClassicUO.Game.UI.MyraWindows.Widgets.Assistant.Agents;

/// <summary>Assistant tab that opens or closes a small movable status window for each agent.</summary>
public static class AgentStatusWindowsTabContent
{
    /// <summary>Builds the tab.</summary>
    /// <returns>The tab's root widget.</returns>
    public static Widget Build()
    {
        var root = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };

        root.Widgets.Add(new MyraLabel(TazLang.Get("agentstatus_desc"), MyraLabel.TextStyle.P) { Wrap = true });

        foreach (AgentStatusSource source in AgentStatusSources.All)
        {
            string key = source.Key;

            root.Widgets.Add(MyraCheckButton.CreateWithCallback(
                AgentStatusGump.IsOpen(key),
                open =>
                {
                    if (open != AgentStatusGump.IsOpen(key) && World.Instance != null)
                        AgentStatusGump.Toggle(World.Instance, key);
                },
                source.Name
            ));
        }

        return root;
    }
}
