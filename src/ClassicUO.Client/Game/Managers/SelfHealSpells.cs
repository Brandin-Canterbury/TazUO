#nullable enable

using System.Collections.Generic;

namespace ClassicUO.Game.Managers
{
    /// <summary>One heal spell the self-heal loop can cast.</summary>
    /// <param name="Id">Global cast id, as <see cref="GameActions.CastSpell"/> expects.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="BaseCastSec">Cast time before Faster Casting.</param>
    /// <param name="Mana">Mana the server charges, before Lower Mana Cost.</param>
    /// <param name="Chivalry">Chivalry school (its FC cap and recovery base differ from Magery's).</param>
    public sealed record SelfHealSpell(int Id, string Name, double BaseCastSec, int Mana, bool Chivalry);

    /// <summary>The heal spells offered in the self-heal list, and the pick rule. Pure; unit-tested.</summary>
    public static class SelfHealSpells
    {
        /// <summary>Spells the player can put in the self-heal list.</summary>
        /// <remarks>Magery costs include this shard's +25% Magery mana cost.</remarks>
        public static readonly IReadOnlyList<SelfHealSpell> All =
        [
            new(SelfHealStateMachine.MageryGreaterHealSpellId, "Greater Heal", 1.75, 14, false),
            new(SelfHealStateMachine.MageryHealSpellId, "Heal", 1.0, 5, false),
            new(SelfHealStateMachine.ChivalryHealSpellId, "Close Wounds", 1.5, 10, true)
        ];

        /// <summary>The default list: Greater Heal, then Heal.</summary>
        /// <returns>A new list.</returns>
        public static List<int> DefaultOrder() =>
            [SelfHealStateMachine.MageryGreaterHealSpellId, SelfHealStateMachine.MageryHealSpellId];

        /// <summary>The catalog entry for a cast id, or null.</summary>
        /// <param name="id">The cast id.</param>
        /// <returns>The spell, or null when it is not a listed heal.</returns>
        public static SelfHealSpell? Find(int id)
        {
            foreach (SelfHealSpell spell in All)
            {
                if (spell.Id == id)
                    return spell;
            }

            return null;
        }

        /// <summary>
        /// The first spell in <paramref name="order"/> the player has the mana for. When none is affordable the
        /// last one is returned, so the cast still goes out and the server's mana message explains it.
        /// </summary>
        /// <param name="order">The player's list, first choice first.</param>
        /// <param name="mana">The player's current mana.</param>
        /// <param name="fallback">Used when the list holds no known spell.</param>
        /// <returns>The cast id.</returns>
        public static int Pick(IReadOnlyList<int>? order, int mana, int fallback)
        {
            int last = fallback;

            if (order == null)
                return fallback;

            foreach (int id in order)
            {
                SelfHealSpell? spell = Find(id);

                if (spell == null)
                    continue;

                if (mana >= spell.Mana)
                    return spell.Id;

                last = spell.Id;
            }

            return last;
        }
    }
}
