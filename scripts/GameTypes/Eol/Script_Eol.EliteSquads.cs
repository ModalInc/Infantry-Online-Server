using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Two always-on ELITE roaming squads (local change 2026-11-13): "Bot - Elite Alpha" and "Bot - Elite Bravo",
    /// 5 bots each - a commander plus infantry / heavy weapons - all at ace skill. No base: the commander leads the
    /// squad hunting across the map (players first, then other bot teams), the rest move in formation on it and
    /// everyone fights whatever they see. Dead members are replaced after a delay at a spot away from players.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool EliteSquadsEnabled = true;
        public const int EliteSquadSize = 5;
        /// <summary>Commander chassis ("Captain Roamer").</summary>
        public const int EliteCommanderVehId = 142;
        /// <summary>Infantry / heavy weapons chassis (grenadiers still capped by <see cref="MaxGrenadiersPerTeam"/>).</summary>
        static readonly int[] EliteMemberVehIds = { 144, 145, 146, 147, 157 };
        const int EliteRespawnDelayMs = 20000;
        const int EliteSpawnAwayFromPlayersRaw = 1400;

        int _tickElitePoll;
        readonly Dictionary<Team, int> _eliteNextSpawnTick = new Dictionary<Team, int>();

        void PollEliteSquads(int now)
        {
            if (!EliteSquadsEnabled || _arena == null || !_arena._bGameRunning || !EolHasArenaPresence())
                return;
            if (now - _tickElitePoll < 1500)
                return;
            _tickElitePoll = now;
            KeepEliteSquadFilled(botTeam6, now);
            KeepEliteSquadFilled(botTeam8, now);
        }

        /// <summary>Live members of an elite squad.</summary>
        public List<RoamingAttacker> EliteSquadMembers(Team team)
        {
            List<RoamingAttacker> list = new List<RoamingAttacker>();
            if (team == null || _arena == null)
                return list;
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b is RoamingAttacker ra && b._team == team && !b.IsDead && !b.bCondemned)
                    list.Add(ra);
            return list;
        }

        /// <summary>Squad leader: the commander if alive, otherwise the longest-serving member.</summary>
        public RoamingAttacker EliteSquadLeader(Team team)
        {
            RoamingAttacker best = null;
            foreach (RoamingAttacker ra in EliteSquadMembers(team))
            {
                bool cmd = ra._type != null && IsSquadLeaderVehicle(ra._type.Id);
                bool bestCmd = best != null && best._type != null && IsSquadLeaderVehicle(best._type.Id);
                if (best == null || (cmd && !bestCmd) || (cmd == bestCmd && ra._id < best._id))
                    best = ra;
            }
            return best;
        }

        void KeepEliteSquadFilled(Team team, int now)
        {
            if (team == null)
                return;
            if (roamBots != null && !roamBots.ContainsKey(team))
                roamBots.Add(team, 0);

            List<RoamingAttacker> members = EliteSquadMembers(team);
            if (members.Count >= EliteSquadSize)
            {
                _eliteNextSpawnTick.Remove(team);
                return;
            }

            // first fill is immediate; replacements wait (like a respawn)
            int due;
            if (!_eliteNextSpawnTick.TryGetValue(team, out due))
            {
                due = members.Count == 0 ? now : now + EliteRespawnDelayMs;
                _eliteNextSpawnTick[team] = due;
            }
            if (now - due < 0)
                return;

            bool hasCommander = members.Exists(m => m._type != null && m._type.Id == EliteCommanderVehId);
            int vehId = hasCommander
                ? LimitGrenadiers(team, EliteMemberVehIds[_arena._rand.Next(EliteMemberVehIds.Length)], EliteMemberVehIds)
                : EliteCommanderVehId;

            // spawn beside the squad if it's out of combat and away from players; otherwise somewhere quiet
            Helpers.ObjectState at = null;
            RoamingAttacker leader = EliteSquadLeader(team);
            if (leader != null && !leader.InLiveCombat && !PlayerNear(leader._state.positionX, leader._state.positionY, EliteSpawnAwayFromPlayersRaw))
                at = findOpenWarpBot(_arena, leader._state.positionX, leader._state.positionY, 200);
            if (at == null)
                at = PickEliteSpawnPoint();
            if (at == null)
                return;

            RoamingAttacker bot = _arena.newBot(typeof(RoamingAttacker), (ushort)vehId, team, null, at, this, null) as RoamingAttacker;
            if (bot == null)
                return;
            bot.SkillModeOverride = "ace";
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bot);
            roamBots[team]++;
            // once a refill is due, the missing members come in quick succession
            if (members.Count + 1 >= EliteSquadSize)
                _eliteNextSpawnTick.Remove(team);
            else
                _eliteNextSpawnTick[team] = now + 600;
        }

        bool PlayerNear(short x, short y, int range)
        {
            foreach (Player p in _arena.getPlayersInRange(x, y, range))
                if (p != null && !p.IsDead && !p.IsSpectator)
                    return true;
            return false;
        }

        /// <summary>A random open spot in play, well away from players.</summary>
        Helpers.ObjectState PickEliteSpawnPoint()
        {
            LvlInfo lvl = _arena._server._assets.Level;
            if (lvl == null)
                return null;
            int w = lvl.Width * 16, h = lvl.Height * 16;
            for (int i = 0; i < 80; i++)
            {
                short x = (short)_arena._rand.Next(128, Math.Max(256, w - 128));
                short y = (short)_arena._rand.Next(128, Math.Max(256, h - 128));
                if (_arena.getTile(x, y).Blocked)
                    continue;
                if (_eol != null && !_eol.IsInAnyActiveSector(x, y))
                    continue;
                if (PlayerNear(x, y, EliteSpawnAwayFromPlayersRaw))
                    continue;
                Helpers.ObjectState at = findOpenWarpBot(_arena, x, y, 150);
                if (at != null)
                    return at;
            }
            return null;
        }
    }
}
