#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Managers.Warmode;

/// <summary>
/// Fights for the player while war mode is on: attacks the highest-priority hostile creature in range, moves on
/// to the next as soon as one dies, and switches to a more dangerous creature when one shows up. Creatures the
/// player fights can be appended to the priority list automatically.
/// </summary>
/// <remarks>
/// Main thread only; <see cref="Update"/> runs every frame and returns at once unless the agent is enabled and
/// the player is in war mode. It never targets the player's own pets, party members, innocents or invulnerable
/// creatures, and human-shaped creatures (possibly players) only when they are on the list.
/// </remarks>
internal sealed class WarmodeAgent
{
    #region Public accessors

    public static WarmodeAgent Instance { get; } = new();

    /// <summary>Human-readable state for the agent tab.</summary>
    public string Status { get; private set; } = TazLang.Get("warmodeagent_status_disabled");

    #endregion

    #region Private members

    private const long TICK_MS = 250;
    private const long SPELL_CURSOR_WINDOW_MS = 8_000;
    private const long RESEND_MS = 1_500;

    private readonly List<WarmodeCandidate> _candidates = new();
    private long _nextTick;
    private uint _lastRequested;
    private long _lastRequestTicks;
    private uint _lastCombatant;

    #endregion

    #region Public methods

    /// <summary>Picks and attacks a target when due; call once per frame.</summary>
    public void Update()
    {
        WarmodeAgentConfig? config = ProfileManager.CurrentProfile?.WarmodeAgent;
        World? world = World.Instance;
        PlayerMobile? player = world?.Player;

        if (config == null || world == null || player == null || !config.Enabled)
        {
            Status = TazLang.Get("warmodeagent_status_disabled");
            return;
        }

        if (player.IsDead || !player.InWarMode)
        {
            Status = TazLang.Get("warmodeagent_status_waiting");
            return;
        }

        long now = Environment.TickCount64;

        if (now < _nextTick)
            return;

        _nextTick = now + TICK_MS;

        uint current = world.TargetManager.LastAttack;

        if (config.AutoAddAttackers && current != _lastCombatant)
            AutoAdd(world, config, current);

        _lastCombatant = current;

        CollectCandidates(world, player);
        uint pick = WarmodeTargetPicker.Choose(_candidates, config, Describe(world, player, current));

        if (pick == 0)
        {
            Status = TazLang.Get("warmodeagent_status_notargets");
            return;
        }

        bool resend = pick != _lastRequested || now - _lastRequestTicks >= RESEND_MS;

        if (pick != current && resend)
        {
            GameActions.Attack(world, pick);
            _lastRequested = pick;
            _lastRequestTicks = now;
        }

        string name = world.Mobiles.Get(pick)?.Name ?? string.Empty;
        Status = TazLang.GetEx("warmodeagent_status_fighting", "Fighting {0}", [name]);
    }

    /// <summary>
    /// The creature a harmful spell's target cursor should go to: the agent's current target, or its best pick.
    /// Only while the agent is on and in war mode, and only for a cursor that follows the player's own spell
    /// (one on the spell list, when the list is not empty).
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="targetType">The cursor's target type.</param>
    /// <param name="serial">The creature to target.</param>
    /// <returns>True when the cursor should be answered with <paramref name="serial"/>.</returns>
    public bool TryGetSpellTarget(World world, TargetType targetType, out uint serial)
    {
        serial = 0;
        WarmodeAgentConfig? config = ProfileManager.CurrentProfile?.WarmodeAgent;
        PlayerMobile? player = world.Player;

        if (config == null || player == null || !config.Enabled || !config.AutoTargetSpells || !player.InWarMode)
            return false;

        if (targetType != TargetType.Harmful || Time.Ticks - GameActions.LastSpellCastTicks > SPELL_CURSOR_WINDOW_MS)
            return false;

        if (config.TargetSpells.Count > 0 && !IsListedSpell(config, GameActions.LastSpellIndex))
            return false;

        CollectCandidates(world, player);
        serial = WarmodeTargetPicker.Choose(_candidates, config, Describe(world, player, world.TargetManager.LastAttack));
        return serial != 0;
    }

    private static bool IsListedSpell(WarmodeAgentConfig config, int spellIndex)
    {
        string name = Data.SpellDefinition.FullIndexGetSpell(spellIndex)?.Name ?? string.Empty;

        foreach (string entry in config.TargetSpells)
        {
            if (string.Equals(entry.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Adds a creature to the bottom of the list unless an entry already covers it.</summary>
    /// <param name="config">The agent settings.</param>
    /// <param name="name">The creature's name, with or without a leading "a"/"an".</param>
    /// <returns>False when the name is empty or already covered.</returns>
    public bool AddName(WarmodeAgentConfig config, string name)
    {
        string trimmed = StripArticle(name);

        if (trimmed.Length == 0 || WarmodeTargetPicker.Rank(config.Priorities, trimmed, out _) != WarmodeTargetPicker.UNLISTED)
            return false;

        config.Priorities.Add(new WarmodePriority { Name = trimmed });
        return true;
    }

    /// <summary>True for a creature the agent may consider at all: hostile, alive and not one of ours.</summary>
    /// <param name="world">The world.</param>
    /// <param name="mobile">The creature.</param>
    /// <returns>Whether it is a possible target.</returns>
    public static bool IsHostile(World world, Mobile mobile)
    {
        if (mobile.IsDestroyed || mobile.IsDead || mobile == world.Player || mobile.IsRenamable)
            return false;

        if (world.Party.Contains(mobile.Serial))
            return false;

        return mobile.NotorietyFlag is NotorietyFlag.Gray or NotorietyFlag.Criminal or NotorietyFlag.Enemy or NotorietyFlag.Murderer;
    }

    #endregion

    #region Private methods

    private void AutoAdd(World world, WarmodeAgentConfig config, uint serial)
    {
        Mobile? mobile = world.Mobiles.Get(serial);

        // Human-shaped attackers may be players; those are added by hand.
        if (mobile != null && !mobile.IsHuman && IsHostile(world, mobile))
            AddName(config, mobile.Name ?? string.Empty);
    }

    private void CollectCandidates(World world, PlayerMobile player)
    {
        _candidates.Clear();

        foreach (Mobile mobile in world.Mobiles.Values)
        {
            if (IsHostile(world, mobile))
                _candidates.Add(ToCandidate(mobile));
        }
    }

    /// <summary>The current target if it is still a live creature other than the player.</summary>
    private static WarmodeCandidate? Describe(World world, PlayerMobile player, uint serial)
    {
        Mobile? mobile = serial == 0 ? null : world.Mobiles.Get(serial);

        if (mobile == null || mobile.IsDestroyed || mobile.IsDead || mobile == player)
            return null;

        return ToCandidate(mobile);
    }

    private static WarmodeCandidate ToCandidate(Mobile mobile)
    {
        int hitsPercent = mobile.HitsMax > 0 ? mobile.Hits * 100 / mobile.HitsMax : 100;

        return new WarmodeCandidate(
            mobile.Serial,
            mobile.Name ?? string.Empty,
            mobile.Distance,
            mobile.IsHuman,
            mobile.InWarMode,
            hitsPercent
        );
    }

    private static string StripArticle(string name)
    {
        string trimmed = name.Trim();

        if (trimmed.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            return trimmed[2..].Trim();

        if (trimmed.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
            return trimmed[3..].Trim();

        return trimmed;
    }

    #endregion
}
