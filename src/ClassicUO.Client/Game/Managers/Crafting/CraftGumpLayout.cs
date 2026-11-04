#nullable enable

using System;
using System.Collections.Generic;

namespace ClassicUO.Game.Managers.Crafting;

/// <summary>Kind of craft menu button, from ServUO <c>CraftGump.GetButtonID(type, index)</c>.</summary>
internal enum CraftButtonKind
{
    /// <summary>A category in the left column (type 0).</summary>
    Group = 0,

    /// <summary>"Make" for an item in the right column (type 1).</summary>
    Item = 1
}

/// <summary>
/// What the Crafting agent needs from a ServUO craft gump: which reply button sits next to each
/// localized label, and the notice the server put in the gump.
/// </summary>
/// <remarks>
/// Parsed from <c>Gump.PacketGumpText</c>, which keeps every layout command with its raw arguments, so
/// cliloc numbers are compared instead of translated text and server gump scaling does not matter.
/// ServUO's <c>CraftGump</c> adds each row as <c>AddButton(..., GetButtonID(type, index))</c> followed by
/// the row's <c>AddHtmlLocalized</c>, and builds IDs as <c>1 + type + index * 7</c>; the button just
/// before a label is therefore that label's button, and <c>(id - 1) % 7</c> tells category from item.
/// </remarks>
internal sealed class CraftGumpLayout
{
    #region Public accessors

    /// <summary>ServUO <c>CraftGump.GetButtonID(6, 2)</c>.</summary>
    public const int MAKE_LAST_BUTTON = 21;

    /// <summary>ServUO <c>CraftGump.GetButtonID(6, 1)</c>.</summary>
    public const int SMELT_BUTTON = 14;

    /// <summary>"CATEGORIES" header every craft gump carries.</summary>
    public const int CATEGORIES_CLILOC = 1044010;

    /// <summary>True when the gump carries the craft menu header.</summary>
    public bool IsCraftGump { get; private set; }

    /// <summary>Cliloc of the gump's title (identifies the craft system), or 0.</summary>
    public int TitleCliloc { get; private set; }

    /// <summary>Cliloc of the notice the server showed, or 0 for none or a plain-text notice.</summary>
    public int NoticeCliloc { get; private set; }

    /// <summary>True when the gump text says the player lacks mana (sent as plain text, not a cliloc).</summary>
    public bool NoticeLacksMana { get; private set; }

    #endregion

    #region Private members

    private const int BUTTON_ID_STRIDE = 7;
    private const int TITLE_X = 10;
    private const int TITLE_Y = 12;
    private const int NOTICE_X = 170;
    private const int NOTICE_Y = 295;
    private const string LACK_MANA_TEXT = "lack the required mana";

    /// <summary>Reply button IDs keyed by the cliloc of the label that follows them.</summary>
    private readonly List<(int Cliloc, int ButtonId)> _labelButtons = new();

    #endregion

    #region Ctor

    private CraftGumpLayout() { }

    #endregion

    #region Public methods

    /// <summary>Reads a server gump's layout text.</summary>
    /// <param name="packetGumpText">The gump's <c>PacketGumpText</c>.</param>
    /// <returns>The parsed layout; <see cref="IsCraftGump"/> is false for any other gump.</returns>
    public static CraftGumpLayout Parse(string? packetGumpText)
    {
        var layout = new CraftGumpLayout();

        if (string.IsNullOrEmpty(packetGumpText))
            return layout;

        layout.NoticeLacksMana = packetGumpText.Contains(LACK_MANA_TEXT, StringComparison.OrdinalIgnoreCase);
        int lastButton = 0;

        foreach (string line in packetGumpText.Split('\n'))
        {
            string[] tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
                continue;

            if (IsCommand(tokens, "button", 8) && int.TryParse(tokens[7], out int buttonId))
                lastButton = buttonId;
            else if (IsCommand(tokens, "xmfhtmlgumpcolor", 9) || IsCommand(tokens, "xmfhtmlgump", 8))
                layout.ReadLocalizedLabel(tokens, ref lastButton);
        }

        return layout;
    }

    /// <summary>Finds the reply button of a category or item row by its cliloc.</summary>
    /// <param name="cliloc">The label cliloc.</param>
    /// <param name="kind">Category or item.</param>
    /// <param name="buttonId">The reply button ID when found.</param>
    /// <returns>Whether the gump shows that row.</returns>
    public bool TryFindButton(int cliloc, CraftButtonKind kind, out int buttonId)
    {
        foreach ((int labelCliloc, int id) in _labelButtons)
        {
            if (labelCliloc == cliloc && id > 0 && (id - 1) % BUTTON_ID_STRIDE == (int)kind)
            {
                buttonId = id;
                return true;
            }
        }

        buttonId = 0;
        return false;
    }

    #endregion

    #region Private methods

    private static bool IsCommand(string[] tokens, string name, int minTokens) =>
        tokens.Length >= minTokens && string.Equals(tokens[0], name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Handles <c>xmfhtmlgump[color] x y w h cliloc ...</c>.</summary>
    private void ReadLocalizedLabel(string[] tokens, ref int lastButton)
    {
        if (!int.TryParse(tokens[1], out int x) || !int.TryParse(tokens[2], out int y))
            return;

        if (!int.TryParse(tokens[5].TrimStart('#'), out int cliloc))
            return;

        if (cliloc == CATEGORIES_CLILOC)
            IsCraftGump = true;
        else if (x == TITLE_X && y == TITLE_Y)
            TitleCliloc = cliloc;
        else if (x == NOTICE_X && y == NOTICE_Y)
            NoticeCliloc = cliloc;

        if (lastButton > 0)
        {
            _labelButtons.Add((cliloc, lastButton));
            lastButton = 0;
        }
    }

    #endregion
}
