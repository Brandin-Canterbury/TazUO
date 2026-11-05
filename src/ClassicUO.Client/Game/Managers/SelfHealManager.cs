using System;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers.Hotkeys;
using ClassicUO.Game.Managers.SpellVisualRange;
using ClassicUO.Input;
using ClassicUO.Network;
using SDL3;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// Native hold-to-heal hotkey. While the bound key is held, spams Greater Heal on self when it is
    /// castable, else Heal (Cure when poisoned). Release stops after the in-flight cast.
    /// </summary>
    public static class SelfHealManager
    {
        /// <summary>Id this manager's hotkey is registered under in the central <see cref="HotKeys"/> registry.</summary>
        public const string SelfHealHotkeyId = "selfheal";

        private static readonly SelfHealStateMachine _machine = new();
        private static readonly LiveEnv _env = new();
        private static bool _held;
        private static bool _loaded;

        public static void Load()
        {
            if (!_loaded)
            {
                Keyboard.KeyUpEvent += OnKeyUp;
                _loaded = true;
            }

            RegisterHotkey();
        }

        /// <summary>
        /// (Re)register the self-heal hotkey with the central registry. The default is imported from
        /// the legacy <c>Profile.SelfHeal_Key/Mod</c> so existing binds survive the migration; the
        /// registry adopts the saved hotkeys.json binding when one exists.
        /// </summary>
        internal static HotKeyEntry RegisterHotkey()
        {
            Profile p = ProfileManager.CurrentProfile;
            var imported = new HotkeyBinding(
                (SDL.SDL_Keycode)(p?.SelfHeal_Key ?? 0),
                (SDL.SDL_Keymod)(p?.SelfHeal_Mod ?? 0));

            return HotKeys.Register(SelfHealHotkeyId, "Self Heal", imported, "Self Heal");
        }

        public static void Unload()
        {
            if (!_loaded) return;
            Keyboard.KeyUpEvent -= OnKeyUp;
            _held = false;
            _loaded = false;
        }

        public static void Update()
        {
            bool held = _held;

            // Keyboard bindings latch _held via the focus-gated key down/up events. Non-key bindings
            // (mouse button, controller, modifier-only) have no up/down pair to latch, so poll the
            // registry for them instead. (Wheel bindings are transient and can't be held.)
            HotKeyEntry entry = HotKeys.Get(SelfHealHotkeyId);
            if (entry != null && entry.Binding != null && !entry.Binding.HasKey && !entry.Binding.IsEmpty)
                held = entry.IsPressed();

            _machine.Tick(_env, held);
        }

        /// <summary>Called from GameSceneInputHandler.OnKeyDown (already focus-gated).</summary>
        public static void HandleKeyDown(SDL.SDL_Keycode key, SDL.SDL_Keymod mod, bool repeat)
        {
            if (repeat) return;

            Profile p = ProfileManager.CurrentProfile;
            if (p == null || !p.SelfHeal_Enabled || HotKeys.GloballyDisabled)
                return;

            HotKeyEntry entry = HotKeys.Get(SelfHealHotkeyId);
            if (entry == null || !entry.Enabled || entry.Binding.IsEmpty || !entry.Binding.HasKey)
                return;

            if (key != entry.Binding.Key)
                return;

            if (HotkeyUtil.NormalizeMods(mod) != entry.Binding.Mod)
                return;

            _held = true;
        }

        // Key-up via the raw keyboard event so release is caught even if focus changed.
        private static void OnKeyUp(string hotkey)
        {
            HotKeyEntry entry = HotKeys.Get(SelfHealHotkeyId);
            if (entry == null || entry.Binding.IsEmpty || !entry.Binding.HasKey)
                return;

            if (HotkeyUtil.TryParseKeycode(hotkey, out SDL.SDL_Keycode key) && key == entry.Binding.Key)
                _held = false;
        }

        private sealed class LiveEnv : ISelfHealEnv
        {
            public long Now => ClassicUO.Time.Ticks;

            public bool CanAct
            {
                get
                {
                    Profile p = ProfileManager.CurrentProfile;
                    if (p == null || !p.SelfHeal_Enabled || HotKeys.GloballyDisabled)
                        return false;

                    var player = Client.Game?.UO?.World?.Player;
                    return player != null && !player.IsDead;
                }
            }

            public bool IsPoisoned => Client.Game?.UO?.World?.Player?.IsPoisoned ?? false;

            // Recast (recovery) and cast-start grace are physical consequences of the spell school
            // + FC/FCR, not free-form preferences, so we derive them from SelfHealTimings at runtime
            // rather than a stored default. A stale stored grace (e.g. 800ms) can be shorter than the
            // actual Cure cast (~0.75s + latency), which let the loop time out mid-cast and recast on
            // top of the in-flight Cure (auto-fizzle). The formula always yields cast + a latency margin.
            public long RecastDelayMs => Timings.recastDelayMs;

            public long CastStartGraceMs => Timings.castStartGraceMs;

            private static (int recastDelayMs, int castStartGraceMs) Timings
            {
                get
                {
                    Profile p = ProfileManager.CurrentProfile;
                    if (p == null)
                        return ((int)SelfHealStateMachine.DefaultRecastDelayMs, (int)SelfHealStateMachine.DefaultCastStartGraceMs);
                    return SelfHealTimings.Compute(p.SelfHeal_UseChivalry, p.SelfHeal_FC, p.SelfHeal_FCR);
                }
            }

            public long CureVerifyMs
            {
                get
                {
                    int configured = (int)(ProfileManager.CurrentProfile?.SelfHeal_CureVerifyMs ?? SelfHealStateMachine.DefaultCureVerifyMs);
                    int ping = (int)(AsyncNetClient.Socket?.Statistics?.Ping ?? 0);
                    return SelfHealTimings.CureVerifyWindow(configured, ping);
                }
            }

            public long InterruptRetryMs =>
                ProfileManager.CurrentProfile?.SelfHeal_InterruptRetryMs ?? SelfHealStateMachine.DefaultInterruptRetryMs;

            public bool IsCasting => Client.Game?.UO?.World?.Player?.IsCasting ?? false;

            public long LastCastFailedAt => SpellVisualRangeManager.Instance?.LastCastFailedTick ?? 0;

            private static bool UseChivalry => ProfileManager.CurrentProfile?.SelfHeal_UseChivalry ?? false;

            public int HealSpellId
            {
                get
                {
                    if (UseChivalry)
                        return SelfHealStateMachine.ChivalryHealSpellId;

                    return CanCastGreaterHeal() ? SelfHealStateMachine.MageryGreaterHealSpellId : SelfHealStateMachine.MageryHealSpellId;
                }
            }

            private const int MAGERY_SKILL_INDEX = 25;
            private const float GREATER_HEAL_MIN_SKILL = 50f;   // below this Greater Heal fizzles too often to beat Heal
            private const int GREATER_HEAL_MIN_MANA = 14;       // 11 base plus the shard's +25% spell mana cost
            private const ushort MAGERY_BOOK_GRAPHIC = 0x0EFA;

            /// <summary>Greater Heal when skilled enough, the mana is there and the spellbook (if loaded) has it.</summary>
            private static bool CanCastGreaterHeal()
            {
                var player = Client.Game?.UO?.World?.Player;

                if (player == null || player.Mana < GREATER_HEAL_MIN_MANA)
                    return false;

                if (player.Skills.Length <= MAGERY_SKILL_INDEX || player.Skills[MAGERY_SKILL_INDEX].Value < GREATER_HEAL_MIN_SKILL)
                    return false;

                return !SpellbookLacks(player, SelfHealStateMachine.MageryGreaterHealSpellId);
            }

            /// <summary>
            /// True only when a Magery book's contents are known and none has the spell. A book never opened this
            /// session has no contents loaded, so it counts as having it.
            /// </summary>
            private static bool SpellbookLacks(GameObjects.PlayerMobile player, int spellId)
            {
                bool anyLoaded = false;

                foreach (GameObjects.Item book in MageryBooks(player))
                {
                    for (LinkedObject node = book.Items; node != null; node = node.Next)
                    {
                        anyLoaded = true;

                        if (((GameObjects.Item)node).Amount == spellId)
                            return false;
                    }
                }

                return anyLoaded;
            }

            private static System.Collections.Generic.IEnumerable<GameObjects.Item> MageryBooks(GameObjects.PlayerMobile player)
            {
                GameObjects.Item held = player.FindItemByLayer(Layer.OneHanded);

                if (held != null && held.Graphic == MAGERY_BOOK_GRAPHIC)
                    yield return held;

                if (player.Backpack == null)
                    yield break;

                for (LinkedObject node = player.Backpack.Items; node != null; node = node.Next)
                {
                    var item = (GameObjects.Item)node;

                    if (item.Graphic == MAGERY_BOOK_GRAPHIC)
                        yield return item;
                }
            }

            public int CureSpellId =>
                UseChivalry ? SelfHealStateMachine.ChivalryCureSpellId : SelfHealStateMachine.MageryCureSpellId;

            public bool IsTargetingAfterCast
            {
                get
                {
                    // Prefer the spell-visual detector (it respects cast timing), but fall back to
                    // the raw target-cursor flag so self-heal also works when Spell Indicators are
                    // disabled. Only consulted right after our own cast, so the raw flag is safe.
                    if (SpellVisualRangeManager.Instance?.IsTargetingAfterCasting() ?? false)
                        return true;

                    return Client.Game?.UO?.World?.TargetManager?.IsTargeting ?? false;
                }
            }

            public void Cast(int spellId) => GameActions.CastSpell(spellId);

            public void TargetSelf()
            {
                var world = Client.Game?.UO?.World;
                if (world?.Player != null)
                    world.TargetManager.Target(world.Player.Serial);
            }
        }
    }
}
