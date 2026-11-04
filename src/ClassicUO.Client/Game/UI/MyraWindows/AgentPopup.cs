#nullable enable

using ClassicUO.Configuration;
using ClassicUO.Game.UI.MyraWindows.Widgets;
using Myra.Graphics2D.UI;

namespace ClassicUO.Game.UI.MyraWindows;

/// <summary>
/// The one small window automation agents use when the player has to do something (move, restock,
/// fix a tool). A new message replaces the text of the window already on screen instead of stacking
/// another; routine progress belongs in the agent's status line, not here.
/// </summary>
/// <remarks>Main thread only. The window is centered when first shown and stays where the player drags it.</remarks>
public static class AgentPopup
{
    #region Private members

    private static InfoModal? _modal;
    private static MyraLabel? _message;
    private static string? _title;

    #endregion

    #region Public methods

    /// <summary>Shows <paramref name="message"/>, reusing the open popup when it has the same title.</summary>
    /// <param name="title">Window title, usually the agent's name.</param>
    /// <param name="message">What the player needs to do.</param>
    public static void Show(string title, string message)
    {
        if (_modal is { IsDisposed: false, IsClosing: false } && _message != null && _title == title)
        {
            _message.Text = message;
            _modal.BringOnTop();
            return;
        }

        Close();

        _title = title;
        _message = new MyraLabel(message, MyraLabel.TextStyle.P) { Wrap = true };

        var content = new VerticalStackPanel { Spacing = MyraStyle.STANDARD_SPACING };
        content.Widgets.Add(_message);
        content.Widgets.Add(new MyraButton(TazLang.Get("agentpopup_ok"), Close)
        {
            HorizontalAlignment = HorizontalAlignment.Center
        });

        _modal = new InfoModal(title, content, new InfoModalProps { Minimizable = false });
        _modal.Show();
    }

    /// <summary>Closes the popup if it is open.</summary>
    public static void Close()
    {
        if (_modal is { IsDisposed: false, IsClosing: false })
            _modal.Dispose();

        _modal = null;
        _message = null;
        _title = null;
    }

    #endregion
}
