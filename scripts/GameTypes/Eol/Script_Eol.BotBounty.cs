using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;
using InfServer.Game.Modules;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "i want to make it so bots can also bounty too") every bot carries a bounty, built the way a
    /// player's is: it starts at its base (its settings= bounty, else the zone's [Bot] bounty, at least 100) and each kill
    /// adds 5 % of its own plus 50 % of its victim's (a player's real bounty, or another bot's). Killing a bot pays like a
    /// player kill: 50 % of its bounty goes into the killer's cash / exp / points and onto the killer's bounty. A bot's
    /// bounty dies with it. Bots passing 1,000 / 2,500 / 5,000 / 10,000 are announced once each.
    /// </summary>
    public partial class Script_Eol
    {
        static readonly Dictionary<Bots.Bot, int> _botBounty = new Dictionary<Bots.Bot, int>();
        static readonly Dictionary<Bots.Bot, int> _botBountyAnnounced = new Dictionary<Bots.Bot, int>();
        static readonly int[] BotBountyAnnounceAt = { 1000, 2500, 5000, 10000 };
        const int BotBountyMinBase = 100;

        static int BotBaseBounty(Bots.Bot b)
        {
            int baseB = BotBountyMinBase;
            try
            {
                BotSettings s = b != null ? b.Settings() : null;
                if (s != null && s.Bounty > baseB) baseB = s.Bounty;
            }
            catch { }
            return baseB;
        }

        /// <summary>The bot's bounty now (its base until it has killed something).</summary>
        public static int BotBountyOf(Bots.Bot b)
        {
            if (b == null) return 0;
            int v;
            lock (_botBounty)
                return _botBounty.TryGetValue(b, out v) ? v : BotBaseBounty(b);
        }

        /// <summary>A bot made a kill: + 5 % of its own bounty + 50 % of the victim's.</summary>
        void AddBotKillBounty(Bots.Bot killer, int victimBounty)
        {
            if (killer == null || killer.IsDead || killer.bCondemned)
                return;
            int now;
            lock (_botBounty)
            {
                int own = BotBountyOf(killer);
                now = own + (int)Math.Round(own / 100.0 * c_percentOfOwnIncrease) + (int)Math.Round(Math.Max(0, victimBounty) / 100.0 * c_percentOfVictim);
                _botBounty[killer] = now;
            }
            killer.DisplayBounty = now;   // shown next to its name (engine pose packets)
            AnnounceBotBounty(killer, now);
        }

        void AnnounceBotBounty(Bots.Bot b, int bounty)
        {
            int done;
            _botBountyAnnounced.TryGetValue(b, out done);
            int tier = done;
            foreach (int t in BotBountyAnnounceAt) if (bounty >= t && t > tier) tier = t;
            if (tier == done || _arena == null)
                return;
            _botBountyAnnounced[b] = tier;
            string name = BotNameModule.GetDisplayName(b);
            if (string.IsNullOrEmpty(name) || name == "?") name = b._type != null ? b._type.Name : "A bot";
            string team = b._team != null ? b._team._name : "";
            _arena.sendArenaMessage(String.Format("&WANTED: {0}{1} has a {2:N0} bounty on its head.", name, team.Length > 0 ? " (" + team + ")" : "", bounty), 0);
        }

        /// <summary>A bot killed a player (from Player.Death): the bot takes its share of the player's bounty.</summary>
        void OnBotKilledPlayer(InfServer.Game.Player victim, CS_VehicleDeath update)
        {
            if (victim == null || update == null || _arena == null)
                return;
            Bots.Bot kb = null;
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._id == update.killerPlayerID) { kb = b; break; }
            if (kb == null || kb._team == victim._team)
                return;
            AddBotKillBounty(kb, victim.Bounty);
        }

        /// <summary>A bot died: bot-on-bot kills pass on the bounty, then the dead bot's bounty is gone.</summary>
        void OnBotDiedBounty(Bots.Bot dead, Bots.Bot killerBot)
        {
            if (dead == null)
                return;
            if (killerBot != null && killerBot != dead && killerBot._team != dead._team)
                AddBotKillBounty(killerBot, BotBountyOf(dead));
            lock (_botBounty) _botBounty.Remove(dead);
            _botBountyAnnounced.Remove(dead);
            if (_botBounty.Count > 256)
                lock (_botBounty)
                {
                    List<Bots.Bot> gone = new List<Bots.Bot>();
                    foreach (Bots.Bot b in _botBounty.Keys) if (b == null || b.bCondemned) gone.Add(b);
                    foreach (Bots.Bot b in gone) { _botBounty.Remove(b); _botBountyAnnounced.Remove(b); }
                }
        }
    }
}
