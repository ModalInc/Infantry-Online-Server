using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Fixed bot roster (local change 2026-11-14) - always 80 bots in the field:
    ///   2 pirate bases x 10, pirate roamers 5-8, human roamers 5-8, alien elite pack 5-8, alien pack 10,
    ///   an elite vehicle squad of 3 (Deathboard / Shimmerboard / Drop Pack), and mixed human roaming teams
    ///   (3-8 each, skill from ace down to a normal mix) that make up the rest.
    /// Sizes are rolled once per server run so the total stays at 80; the 2 assassin cells of 3 come on top
    /// while a 30k hunt is on (max 86).
    /// </summary>
    public partial class Script_Eol
    {
        public const int EolBotTotalTarget = 80;
        public const int RosterBaseTeams = 2;
        public const int RosterBaseSize = EolMaxConcurrentBaseDefenseBots;   // 10
        public const int RosterAlienMainSize = 10;
        public const int RosterVehicleSquadSize = 3;
        public const int RosterMixedTeamCount = 5;

        bool _rosterReady;
        int _rosterPirateRoamers, _rosterHumanRoamers, _rosterAlienElite;
        readonly int[] _rosterMixedSize = new int[RosterMixedTeamCount];
        readonly string[] _rosterMixedSkill = new string[RosterMixedTeamCount];

        public int RosterPirateRoamers { get { EnsureRoster(); return _rosterPirateRoamers; } }
        public int RosterHumanRoamers { get { EnsureRoster(); return _rosterHumanRoamers; } }
        public int RosterAlienElite { get { EnsureRoster(); return _rosterAlienElite; } }

        void EnsureRoster()
        {
            if (_rosterReady)
                return;
            _rosterReady = true;
            Random r = new Random(Environment.TickCount);
            _rosterPirateRoamers = r.Next(5, 9);
            _rosterHumanRoamers = r.Next(5, 9);
            _rosterAlienElite = r.Next(5, 9);

            int rest = EolBotTotalTarget - (RosterBaseTeams * RosterBaseSize + _rosterPirateRoamers + _rosterHumanRoamers
                + _rosterAlienElite + RosterAlienMainSize + RosterVehicleSquadSize);   // 23..32
            for (int i = 0; i < RosterMixedTeamCount; i++)
                _rosterMixedSize[i] = 3;
            rest -= 3 * RosterMixedTeamCount;
            for (int guard = 0; rest > 0 && guard < 1000; guard++)
            {
                int i = r.Next(RosterMixedTeamCount);
                if (_rosterMixedSize[i] >= 8)
                    continue;
                _rosterMixedSize[i]++;
                rest--;
            }

            // from elite level down to a normal mixed bag
            string[] skills = { "ace", "elite", "veteran", null, null };
            for (int i = skills.Length - 1; i > 0; i--)
            {
                int j = r.Next(i + 1);
                string t = skills[i]; skills[i] = skills[j]; skills[j] = t;
            }
            for (int i = 0; i < RosterMixedTeamCount; i++)
                _rosterMixedSkill[i] = skills[i % skills.Length];

            Log.write(TLog.Normal, "[Eol] Bot roster: bases {0}x{1}, pirate roamers {2}, human roamers {3}, aliens {4}+{5}, vehicle squad {6}, mixed teams [{7}] = {8}",
                RosterBaseTeams, RosterBaseSize, _rosterPirateRoamers, _rosterHumanRoamers, RosterAlienMainSize, _rosterAlienElite,
                RosterVehicleSquadSize, string.Join(",", _rosterMixedSize), EolBotTotalTarget);
        }

        /// <summary>
        /// Helmet drops (local 2026-11-14): player-class bots drop their class helmet (the .veh already sets it);
        /// pirate chassis have none set, so they drop the matching pirate / bot helmet; board riders are Jump
        /// Troopers. 0 = leave it to the vehicle's own drop (aliens etc.).
        /// </summary>
        static int PirateHelmetFor(Bots.Bot b)
        {
            if (b?._type == null)
                return 0;
            switch (b._type.Id)
            {
                case 142: return 1595;                                         // Captain Roamer -> Pirate Commander Helmet
                case 144: case 145: case 146: case 147: case 157: return 1598;   // Roamer -> Pirate Raider Helmet
                case 140: case 141: case 143: case 156: case 158: return 189;    // Defender -> Bot Defender Helmet
                case 162: case 415: case 437: return 185;                        // base Captain -> Bot Captain Helmet
                case 463: case 464: case 465: return 188;                        // Engineer -> Bot Engineer Helmet
                case 108: case 131: case 109: return 90;                         // board / pack rider -> Jump Trooper's Helmet
                default: return 0;
            }
        }

        static readonly Func<Bots.Bot, int> _helmetDropFn = PirateHelmetFor;

        /// <summary>Live (or about-to-revive) bots on every managed Eol team.</summary>
        int CountLiveRosterBots()
        {
            if (_arena == null)
                return 0;
            int n = 0;
            foreach (Bots.Bot b in _arena.ActiveBots)
            {
                if (b == null || b.bCondemned || !IsManagedEolBotFactionTeam(b._team))
                    continue;
                if (IsAssassinTeam(b._team))
                    continue;   // assassins come on top of the 80 (max 86 while a 30k hunt is on)
                if (!b.IsDead || b.bDropshipRespawnPending)
                    n++;
            }
            return n;
        }
    }
}
