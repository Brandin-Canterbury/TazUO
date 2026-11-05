#nullable enable

using System.Xml;
using ClassicUO.Assets;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.UI.Gumps;

/// <summary>
/// A small movable window showing "Agent: status" for one agent, refreshed every frame. Saved with the
/// layout, so it comes back where it was left. Right-click closes it.
/// </summary>
internal sealed class AgentStatusGump : Gump
{
    #region Private members

    private const int PADDING = 4;
    private const float FONT_SIZE = 16;
    private const long REFRESH_MS = 250;

    private AgentStatusSource? _source;
    private AlphaBlendControl? _background;
    private TextBox? _text;
    private string _shown = string.Empty;
    private long _nextRefresh;

    #endregion

    #region Public accessors

    public override GumpType GumpType => GumpType.AgentStatus;

    #endregion

    #region Ctor

    /// <summary>Empty gump for layout restore; <see cref="Restore"/> binds it.</summary>
    /// <param name="world">The world.</param>
    public AgentStatusGump(World world) : base(world, 0, 0)
    {
        CanMove = true;
        AcceptMouseInput = true;
        CanCloseWithRightClick = true;
    }

    private AgentStatusGump(World world, AgentStatusSource source, int x, int y) : this(world)
    {
        X = x;
        Y = y;
        Bind(source);
    }

    #endregion

    #region Public methods

    /// <summary>True when a status window for the agent is open.</summary>
    /// <param name="key">The agent's source key.</param>
    /// <returns>Whether one is open.</returns>
    public static bool IsOpen(string key) => Find(key) != null;

    /// <summary>Opens the agent's status window, or closes it when already open.</summary>
    /// <param name="world">The world.</param>
    /// <param name="key">The agent's source key.</param>
    public static void Toggle(World world, string key)
    {
        AgentStatusGump? open = Find(key);

        if (open != null)
        {
            open.Dispose();
            return;
        }

        AgentStatusSource? source = AgentStatusSources.Find(key);

        if (source != null)
            UIManager.Add(new AgentStatusGump(world, source, 100, 100));
    }

    public override void Update()
    {
        base.Update();

        if (_source == null || _text == null || IsDisposed || Time.Ticks < _nextRefresh)
            return;

        _nextRefresh = Time.Ticks + REFRESH_MS;

        string line = $"{_source.Name}: {_source.Status()}";

        if (line == _shown)
            return;

        _shown = line;
        _text.Text = line;
        Resize();
    }

    public override void Save(XmlTextWriter writer)
    {
        base.Save(writer);
        writer.WriteAttributeString("agent", _source?.Key ?? string.Empty);
    }

    public override void Restore(XmlElement xml)
    {
        base.Restore(xml);
        AgentStatusSource? source = AgentStatusSources.Find(xml.GetAttribute("agent"));

        if (source == null)
            Dispose();
        else
            Bind(source);
    }

    #endregion

    #region Private methods

    private static AgentStatusGump? Find(string key)
    {
        foreach (IGui gui in UIManager.Gumps)
        {
            if (gui is AgentStatusGump { IsDisposed: false } gump && gump._source?.Key == key)
                return gump;
        }

        return null;
    }

    private void Bind(AgentStatusSource source)
    {
        _source = source;
        _shown = $"{source.Name}: {source.Status()}";

        Add(_background = new AlphaBlendControl(0.7f));
        Add(_text = TextBox.GetOne(_shown, TrueTypeLoader.EMBEDDED_FONT, FONT_SIZE, Color.White, TextBox.RTLOptions.Default()));
        _text.X = PADDING;
        _text.Y = PADDING;
        _text.AcceptMouseInput = false;
        Resize();
    }

    private void Resize()
    {
        if (_text == null || _background == null)
            return;

        Width = _text.MeasuredSize.X + PADDING * 2;
        Height = _text.MeasuredSize.Y + PADDING * 2;
        _background.Width = Width;
        _background.Height = Height;
    }

    #endregion
}
