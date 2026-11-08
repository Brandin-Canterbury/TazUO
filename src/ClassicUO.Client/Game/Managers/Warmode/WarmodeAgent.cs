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

    private const int AREA_SPELL_RANGE = 12;
    private const long AUTO_ADD_WAIT_MS = 3_000;

    private readonly List<WarmodeCandidate> _candidates = new();
    private readonly HashSet<uint> _migrationChecked = new();
    private uint _pendingAutoAdd;
    private long _pendingAutoAddSince;
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

        if (config.AutoAddAttackers)
            ResolvePendingAutoAdd(world, config);

        CollectCandidates(world, player, config);
        CreatureInfoCache.Instance.Flush();
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

        if (Time.Ticks - GameActions.LastSpellCastTicks > SPELL_CURSOR_WINDOW_MS)
            return false;

        // Area spells (Meteor Swarm, fields, Hail Storm...) open a neutral ground cursor; they still count when
        // the spell itself is harmful, and go to the nearest enemy instead of the priority pick.
        bool areaSpell = targetType == TargetType.Neutral
            && Data.SpellDefinition.FullIndexGetSpell(GameActions.LastSpellIndex)?.TargetType == TargetType.Harmful;

        if (targetType != TargetType.Harmful && !areaSpell)
            return false;

        if (config.TargetSpells.Count > 0 && !IsListedSpell(config, GameActions.LastSpellIndex))
            return false;

        CollectCandidates(world, player, config);
        CreatureInfoCache.Instance.Flush();

        if (areaSpell)
        {
            int best = int.MaxValue;

            foreach (WarmodeCandidate c in _candidates)
            {
                if (c.Distance <= AREA_SPELL_RANGE && c.Distance < best)
                {
                    best = c.Distance;
                    serial = c.Serial;
                }
            }

            return serial != 0;
        }

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

    /// <summary>
    /// What to put on the list for a creature: its type and role from the server when known ("orc mage"),
    /// otherwise its name.
    /// </summary>
    /// <param name="mobile">The creature.</param>
    /// <returns>The entry text.</returns>
    public string ListNameFor(Mobile mobile) =>
        CreatureInfoCache.Instance.TryGet(mobile.Serial, out CreatureInfo info) && info.Kind.Length > 0
            ? info.Kind
            : mobile.Name ?? string.Empty;

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

    // Adds the creature's type, never its personal name; waits briefly for the server's answer.
    private void AutoAdd(World world, WarmodeAgentConfig config, uint serial)
    {
        Mobile? mobile = world.Mobiles.Get(serial);

        // Human-shaped attackers may be players; those are added by hand.
        if (mobile == null || mobile.IsHuman || !IsHostile(world, mobile))
            return;

        if (CreatureInfoCache.Instance.TryGet(serial, out CreatureInfo info) && info.Kind.Length > 0)
        {
            AddName(config, info.Kind);
            return;
        }

        CreatureInfoCache.Instance.Request(serial);
        _pendingAutoAdd = serial;
        _pendingAutoAddSince = Environment.TickCount64;
    }

    private void ResolvePendingAutoAdd(World world, WarmodeAgentConfig config)
    {
        if (_pendingAutoAdd == 0)
            return;

        if (CreatureInfoCache.Instance.TryGet(_pendingAutoAdd, out CreatureInfo info) && info.Kind.Length > 0)
        {
            AddName(config, info.Kind);
            _pendingAutoAdd = 0;
            return;
        }

        if (Environment.TickCount64 - _pendingAutoAddSince < AUTO_ADD_WAIT_MS)
            return;

        // A server without creature info: fall back to the name.
        if (!CreatureInfoCache.Instance.ServerSupports)
        {
            Mobile? mobile = world.Mobiles.Get(_pendingAutoAdd);

            if (mobile != null)
                AddName(config, mobile.Name ?? string.Empty);
        }

        _pendingAutoAdd = 0;
    }

    private void CollectCandidates(World world, PlayerMobile player, WarmodeAgentConfig config)
    {
        _candidates.Clear();
        CreatureInfoCache cache = CreatureInfoCache.Instance;

        foreach (Mobile mobile in world.Mobiles.Values)
        {
            if (!IsHostile(world, mobile))
                continue;

            cache.Request(mobile.Serial);
            bool known = cache.TryGet(mobile.Serial, out CreatureInfo info);

            // On a server that answers, wait for the type and owner before picking a creature.
            if (cache.ServerSupports && !known)
                continue;

            if (known && IsFriendlyOwned(world, player, info.Master))
                continue;

            if (known && info.ProperName && _migrationChecked.Add(mobile.Serial))
                ReplaceNamedEntry(config, mobile.Name ?? string.Empty, info.Kind);

            _candidates.Add(ToCandidate(mobile, known ? info.Kind : string.Empty));
        }

        if (_migrationChecked.Count > 4096)
            _migrationChecked.Clear();
    }

    /// <summary>A summon or pet of the player, a party member, or any player the agent would not attack.</summary>
    private static bool IsFriendlyOwned(World world, PlayerMobile player, uint master)
    {
        if (master == 0)
            return false;

        if (master == player.Serial || world.Party.Contains(master))
            return true;

        Mobile? owner = world.Mobiles.Get(master);
        return owner != null && !IsHostile(world, owner);
    }

    /// <summary>
    /// Turns a list entry that is a creature's personal name ("Grukk") into its type ("orc warrior"), or drops it
    /// when the type is already listed. Cleans lists filled before entries were added by type.
    /// </summary>
    private static void ReplaceNamedEntry(WarmodeAgentConfig config, string name, string kind)
    {
        string trimmed = StripArticle(name);

        if (trimmed.Length == 0 || kind.Length == 0)
            return;

        int index = -1;
        bool kindListed = false;

        for (int i = 0; i < config.Priorities.Count; i++)
        {
            string entry = config.Priorities[i].Name.Trim();

            if (index < 0 && !config.Priorities[i].Ignore && string.Equals(entry, trimmed, StringComparison.OrdinalIgnoreCase))
                index = i;

            if (string.Equals(entry, kind, StringComparison.OrdinalIgnoreCase))
                kindListed = true;
        }

        if (index < 0)
            return;

        if (kindListed)
            config.Priorities.RemoveAt(index);
        else
            config.Priorities[index].Name = kind;
    }

    /// <summary>The current target if it is still a live creature other than the player.</summary>
    private static WarmodeCandidate? Describe(World world, PlayerMobile player, uint serial)
    {
        Mobile? mobile = serial == 0 ? null : world.Mobiles.Get(serial);

        if (mobile == null || mobile.IsDestroyed || mobile.IsDead || mobile == player)
            return null;

        string kind = CreatureInfoCache.Instance.TryGet(serial, out CreatureInfo info) ? info.Kind : string.Empty;
        return ToCandidate(mobile, kind);
    }

    private static WarmodeCandidate ToCandidate(Mobile mobile, string kind)
    {
        int hitsPercent = mobile.HitsMax > 0 ? mobile.Hits * 100 / mobile.HitsMax : 100;

        return new WarmodeCandidate(
            mobile.Serial,
            mobile.Name ?? string.Empty,
            mobile.Distance,
            mobile.IsHuman,
            mobile.InWarMode,
            hitsPercent,
            kind
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
