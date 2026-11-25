using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Fixed bot roster (local change 2026-11-14; 80 -> 60 on 2026-11-15, the two bases unchanged) - always 60
    /// bots in the field (2026-11-16b: squads of 6-8):
    ///   2 pirate bases x 10, pirate roamers 6, alien elite pack 4, alien pack 8, an elite vehicle squad of 3
    ///   (Deathboard / Shimmerboard / Drop Pack), and 3 joinable human squads of 6-8 (Storm Rangers = a normal
    ///   mix, plus an ace and a veteran team) - commander, medic, heavy, marine, infantry, engineer + extras.
    /// The pirate raid (2 x 5) and the 2 assassin cells of 3 come on top while they run (70 cap, assassins aside).
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>(2026-11-21, owner: "3 bot bases team like eol classic", "both swarms out", "if sector C is chosen please add
        /// another 3 6man teams") 79 + the third base 10 + the King's swarm 12, + 18 while Sector C is open.</summary>
        public static int EolBotTotalTarget { get { return 100 + (s_sectorCOpen ? SectorCExtraSquads * SquadMaxSize : 0); } }   // (2026-11-26: the pirate squad of 6 -> the Harrier crews of 4; the swarm sites are still 2 x 12 - aliens + Pirate Raiders)
        /// <summary>Extra mixed squads while Sector C is open (the last ones of MixedTeamNames).</summary>
        public const int SectorCExtraSquads = 3;
        static bool s_sectorCOpen;
        /// <summary>Mixed squads that fill right now.</summary>
        int MixedTeamsActive { get { return RosterMixedTeamCount - (s_sectorCOpen ? 0 : SectorCExtraSquads); } }
        public const int RosterBaseTeams = 3;   // (2026-11-21: a third base, like Eol Classic - Steel Wardens)
        public const int RosterBaseSize = EolMaxConcurrentBaseDefenseBots;   // 10
        public const int RosterAlienMainSize = 8;
        public const int RosterVehicleSquadSize = 6;   // (2026-11-24, owner: "give them a captain on a pack ... so their team is 6")   // (2026-11-17: + the board medic on a Hoverboard) // (2026-11-17, owner: Deathboard, Shimmerboard, Drop Pack + a SciOps on a Flight Pack)
        /// <summary>(2026-11-16c) Storm Rangers + 5 mixed teams = 6 joinable human squads of 5-6.</summary>
        public const int RosterMixedTeamCount = 8;   // (2026-11-21: 5 + 3 that only fill while Sector C is open)
        /// <summary>(2026-11-16c, owner) a joinable squad is 5 to 6 strong, players + bots.</summary>
        public const int SquadMinSize = 5;

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
            // (2026-11-16c, owner) 2 bases of 10, the bug base of 10, and roaming squads of 5-6: pirate roamers 5, the six
            // human squads (Storm Rangers + 5 mixed) 5 each, the spare places spread out (6 at most) - 70 in all
            _rosterPirateRoamers = HarrierCrewSize;   // (2026-11-26: botTeam6 = the Harrier crews, 4; was the pirate squad of 6)
            // (2026-11-16c) the crash-ship alien packs are gone: their places are the Alien Swarm's 10 bugs
            _rosterAlienElite = EolAlienWavesEnabled ? 4 : 0;
            int aliens = EolAlienWavesEnabled ? _rosterAlienElite + RosterAlienMainSize : SwarmDefenders * SwarmRunCount;   // (2026-11-21: the Queen's and the King's swarm)
            _rosterHumanRoamers = SquadMaxSize;   // (2026-11-17, owner: "lets make teams of 6" - every human squad has its engineer)
            for (int i = 0; i < RosterMixedTeamCount; i++)
                _rosterMixedSize[i] = SquadMaxSize;
            int rest = 0;   // (2026-11-21: every squad is 6 already - the totals are worked out in EolBotTotalTarget)   // 0 (2026-11-17: 20 + 6 + 6 + 30 + 12 + 5 = 79)
            for (int guard = 0; rest > 0 && guard < 1000; guard++)
            {
                int i = r.Next(RosterMixedTeamCount + 1);
                if (i == RosterMixedTeamCount)
                {
                    if (_rosterHumanRoamers >= SquadMaxSize)
                        continue;
                    _rosterHumanRoamers++;
                }
                else
                {
                    if (_rosterMixedSize[i] >= SquadMaxSize)
                        continue;
                    _rosterMixedSize[i]++;
                }
                rest--;
            }

            // from elite level down to a normal mixed bag
            string[] skills = { "ace", "ace", "veteran", "veteran", null };   // (5 mixed teams; Storm Rangers = the normal mix)
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

        /// <summary>(2026-11-16, owner: 26 bots on one team) hard caps for every spawn path.</summary>
        public const int EolMaxBotsPerTeam = 10;
        public static int EolMaxBotsTotal { get { return EolBotTotalTarget + 2; } }   // (roster + a little room for bots changing over)   // (2026-11-17: 78 -> 80 with the swarm of 12)   // (roster 70 + a little room for bots changing over)

        /// <summary>
        /// True when one more bot may join <paramref name="team"/>: under 10 live (or about-to-revive) bots on that
        /// team and under 70 in the whole arena. The 2 assassin cells (bounty hunt) only obey the team cap.
        /// </summary>
        public bool CanSpawnBotOnTeam(Team team)
        {
            if (_arena == null || team == null)
                return false;
            int onTeam = 0, total = 0;
            foreach (Bots.Bot b in _arena.ActiveBots)
            {
                if (b == null || b.bCondemned || (b.IsDead && !b.bDropshipRespawnPending))
                    continue;
                if (b._team == team && !IsSwarmNest(b))   // (2026-11-17: the moving Queen isn't one of the 12)
                    onTeam++;
                if (!IsAssassinTeam(b._team))
                    total++;
            }
            if (onTeam >= (IsSwarmTeam(team) ? SwarmDefenders : EolMaxBotsPerTeam))   // (the alien base team: 12)
                return false;
            return IsAssassinTeam(team) || total < EolMaxBotsTotal;
        }

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
                    continue;   // assassins come on top of the 60 (max 66 while a 30k hunt is on)
                if (!b.IsDead || b.bDropshipRespawnPending)
                    n++;
            }
            return n;
        }
    }
}
