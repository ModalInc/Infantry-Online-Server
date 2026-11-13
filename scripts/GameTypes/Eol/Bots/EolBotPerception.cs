using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// What a bot actually knows about players (local change 2026-11-13). Before this, bots "sensed" every player
    /// within 6000 px straight through walls and stood behind cover aiming at them. Now, like a player, a bot knows
    /// about you only if it:
    ///  - sees you: line of sight within <see cref="SightRaw"/> (about a screen) - only full-height walls block
    ///    sight; green / yellow / orange / low physics is see-through - or
    ///  - saw you within the last <see cref="MemoryMs"/> (it chases round the corner you ducked behind), or
    ///  - was just shot by you (it turns to find the shooter), or a teammate it protects was (see EolBotBrain).
    /// Everything that asks "can this bot see that player?" goes through here (EolBotSenseUtil.BotCanSeePlayer and
    /// the engine's target picker), so all bot types follow the same rules.
    /// </summary>
    internal static class EolBotPerception
    {
        public const bool Enabled = true;
        /// <summary>Line-of-sight sight range, raw px (roughly what a player sees on screen).</summary>
        public const int SightRaw = 950;
        /// <summary>How long a bot remembers a player after losing sight of them.</summary>
        public const int MemoryMs = 10000;   // time to go and check where it last saw you
        /// <summary>Getting shot by a player reveals them this long.</summary>
        public const int HurtRevealMs = 1500;
        const int LosCacheMs = 150;

        /// <summary>Where (and how fast / which way) a player was when last seen.</summary>
        public struct Sighting
        {
            public short X, Y;
            public short VelX, VelY;
            public int Tick;
        }

        sealed class Knowledge
        {
            public readonly Dictionary<Player, Sighting> Where = new Dictionary<Player, Sighting>();
            public readonly Dictionary<Player, int> LastSeen = new Dictionary<Player, int>();
            public readonly Dictionary<Player, int> LosTick = new Dictionary<Player, int>();
            public readonly Dictionary<Player, bool> LosVal = new Dictionary<Player, bool>();
        }

        // Keyed by the looker's state object (a bot's _state, or an HQ vehicle's) - dies with it.
        static readonly ConditionalWeakTable<Helpers.ObjectState, Knowledge> Table = new ConditionalWeakTable<Helpers.ObjectState, Knowledge>();
        static readonly ConditionalWeakTable<Helpers.ObjectState, Bot> BotByState = new ConditionalWeakTable<Helpers.ObjectState, Bot>();

        static int Now() { int t = Environment.TickCount; return t == 0 ? 1 : t; }

        /// <summary>Tie a bot to its state so "was I just shot by them?" can be answered (called from the brain poll).</summary>
        public static void Register(Bot b)
        {
            if (b?._state == null)
                return;
            Bot existing;
            if (!BotByState.TryGetValue(b._state, out existing))
                BotByState.Add(b._state, b);
        }

        /// <summary>The bot whose state object this is (null when it isn't a registered bot).</summary>
        public static Bot BotAt(Helpers.ObjectState state)
        {
            Bot b;
            return state != null && BotByState.TryGetValue(state, out b) ? b : null;
        }

        /// <summary>Make the looker aware of <paramref name="p"/> now (e.g. it's protecting a teammate p is shooting).</summary>
        public static void Reveal(Helpers.ObjectState from, Player p)
        {
            if (from == null || p == null)
                return;
            Table.GetValue(from, _ => new Knowledge()).LastSeen[p] = Now();
        }

        /// <summary>Drop what the looker knows about <paramref name="p"/> (gave up the chase).</summary>
        public static void Forget(Helpers.ObjectState from, Player p)
        {
            Knowledge k;
            if (from == null || p == null || !Table.TryGetValue(from, out k))
                return;
            k.LastSeen.Remove(p);
            k.LosTick.Remove(p);
            k.Where.Remove(p);
        }

        /// <summary>Line of sight within sight range right now (cached briefly).</summary>
        public static bool SeesNow(Helpers.ObjectState from, Player p)
        {
            if (from == null || p?._state == null || p._arena == null)
                return false;
            Knowledge k = Table.GetValue(from, _ => new Knowledge());
            int now = Now();
            int t; bool v;
            if (k.LosTick.TryGetValue(p, out t) && now - t >= 0 && now - t < LosCacheMs && k.LosVal.TryGetValue(p, out v))
                return v;
            // nobody sees through walls (masers included) - local 2026-11-14
            v = Helpers.isInRange(SightRaw, from, p._state)
                && BotCombat.SightLineClear(p._arena, from.positionX, from.positionY, p._state.positionX, p._state.positionY);
            k.LosTick[p] = now;
            k.LosVal[p] = v;
            if (v)
            {
                k.LastSeen[p] = now;
                k.Where[p] = new Sighting
                {
                    X = p._state.positionX, Y = p._state.positionY,
                    VelX = p._state.velocityX, VelY = p._state.velocityY, Tick = now,
                };
            }
            return v;
        }

        /// <summary>Where the looker last actually saw <paramref name="p"/> (false if never).</summary>
        public static bool TryGetLastSighting(Helpers.ObjectState from, Player p, out Sighting s)
        {
            s = default(Sighting);
            Knowledge k;
            return from != null && p != null && Table.TryGetValue(from, out k) && k.Where.TryGetValue(p, out s);
        }

        /// <summary>True when the looker sees, remembers, or was just shot by <paramref name="p"/>.</summary>
        public static bool Knows(Helpers.ObjectState from, Player p)
        {
            if (!Enabled)
                return true;
            if (from == null || p?._state == null)
                return false;
            if (SeesNow(from, p))
                return true;

            int now = Now();
            Knowledge k = Table.GetValue(from, _ => new Knowledge());
            Bot self;
            if (BotByState.TryGetValue(from, out self) && ReferenceEquals(self.LastHurtBy, p)
                && now - self.LastHurtTick >= 0 && now - self.LastHurtTick <= HurtRevealMs)
            {
                // got shot: it knows roughly WHERE the shot came from (that spot - it searches there), nothing more
                k.LastSeen[p] = now;
                if (!k.Where.ContainsKey(p) || now - k.Where[p].Tick > HurtRevealMs)
                    k.Where[p] = new Sighting { X = p._state.positionX, Y = p._state.positionY, Tick = now };
                return true;
            }

            int last;
            if (k.LastSeen.TryGetValue(p, out last))
            {
                if (now - last >= 0 && now - last <= MemoryMs)
                    return true;
                k.LastSeen.Remove(p);
            }
            return false;
        }

        /// <summary>Hook the engine's target picker to this (idempotent; called from the brain poll).</summary>
        public static void InstallEngineHook()
        {
            if (InfServer.Game.Modules.BotActions.BotActionHelpers.PlayerPerception == null)
                InfServer.Game.Modules.BotActions.BotActionHelpers.PlayerPerception =
                    (bot, player) => bot?._state != null && EolBotSenseUtil.BotCanSeePlayer(bot._state, player);
        }
    }
}
