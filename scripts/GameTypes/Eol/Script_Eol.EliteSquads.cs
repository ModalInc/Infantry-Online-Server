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
            // local 2026-11-14 roster: botTeam6 = the pirate roamers (5-8, Roamer chassis + Captain Roamer);
            // botTeam8 is now the elite vehicle squad (Script_Eol.StrikeTeams.cs)
            KeepEliteSquadFilled(botTeam6, now, RosterPirateRoamers);
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

        void KeepEliteSquadFilled(Team team, int now, int squadSize)
        {
            if (team == null)
                return;
            if (roamBots != null && !roamBots.ContainsKey(team))
                roamBots.Add(team, 0);

            List<RoamingAttacker> members = EliteSquadMembers(team);
            if (members.Count >= squadSize)
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
            if (leader != null && !leader.InLiveCombat && InPlayArea(leader._state.positionX, leader._state.positionY) && !PlayerNear(leader._state.positionX, leader._state.positionY, EliteSpawnAwayFromPlayersRaw))
                at = findOpenWarpBot(_arena, leader._state.positionX, leader._state.positionY, 200);
                if (at != null && (!InPlayArea(at.positionX, at.positionY, 120) || !IsOpenConnectedGround(at.positionX, at.positionY)))
                    at = null;
            if (at == null)
                at = PickEliteSpawnPoint();
            if (at == null)
                return;

            RoamingAttacker bot = _arena.newBot(typeof(RoamingAttacker), (ushort)vehId, team, null, at, this, null) as RoamingAttacker;
            if (bot == null)
                return;
            bot.SkillModeOverride = "elite";   // pirate roamers: veteran..ace
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bot);
            roamBots[team]++;
            // once a refill is due, the missing members come in quick succession
            if (members.Count + 1 >= squadSize)
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

        /// <summary>
        /// Inside the granted (active, non-toxic) play area with a margin - the point and a ring 200 px around it
        /// (local 2026-11-14: bots were spawning / wandering in the toxic zones).
        /// </summary>
        public bool InPlayArea(short x, short y, int margin = 200)
        {
            if (_eol == null)
                return true;
            if (!_eol.IsInAnyActiveSector(x, y))
                return false;
            if (margin <= 0)
                return true;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                if (!_eol.IsInAnyActiveSector((short)(x + Math.Cos(a) * margin), (short)(y + Math.Sin(a) * margin)))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when (x, y) is on open, connected ground - not inside a sealed room / pocket of physics (local fix
        /// 2026-11-14: a squad spawned inside an all-red building in Q3). Flood-fills walkable tiles from the spot;
        /// it has to reach <paramref name="needTiles"/> tiles (a big area) before it counts as open.
        /// </summary>
        public bool IsOpenConnectedGround(short x, short y, int needTiles = 700)
        {
            LvlInfo lvl = _arena?._server?._assets?.Level;
            if (lvl == null)
                return true;
            int w = lvl.Width, h = lvl.Height;
            int sx = x / 16, sy = y / 16;
            if (sx < 0 || sy < 0 || sx >= w || sy >= h)
                return false;
            HashSet<int> seen = new HashSet<int>();
            Queue<int> q = new Queue<int>();
            int start = sy * w + sx;
            if (_arena.getTile(x, y).Blocked)
                return false;
            seen.Add(start);
            q.Enqueue(start);
            while (q.Count > 0)
            {
                if (seen.Count >= needTiles)
                    return true;
                int c = q.Dequeue();
                int cx = c % w, cy = c / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = cy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int n = ny * w + nx;
                    if (seen.Contains(n))
                        continue;
                    if (_arena.getTile((short)(nx * 16 + 8), (short)(ny * 16 + 8)).Blocked)
                        continue;
                    if (_eol != null && !_eol.IsInAnyActiveSector((short)(nx * 16 + 8), (short)(ny * 16 + 8)))
                        continue;   // the toxic zone isn't "open ground" to escape into
                    seen.Add(n);
                    q.Enqueue(n);
                }
            }
            return false;   // ran out of tiles: a sealed pocket
        }

        /// <summary>
        /// An open spot inside the play area, away from players, and spread out from the bots already in the field
        /// (best of many samples = farthest from any other bot), so squads appear all around the area.
        /// </summary>
        Helpers.ObjectState PickEliteSpawnPoint()
        {
            LvlInfo lvl = _arena._server._assets.Level;
            if (lvl == null)
                return null;
            int w = lvl.Width * 16, h = lvl.Height * 16;
            Helpers.ObjectState best = null;
            double bestScore = -1;
            for (int i = 0, tries = 0; i < 320 && tries < 40; i++)   // more samples = squads spread wider over the map
            {
                short x = (short)_arena._rand.Next(128, Math.Max(256, w - 128));
                short y = (short)_arena._rand.Next(128, Math.Max(256, h - 128));
                if (_arena.getTile(x, y).Blocked)
                    continue;
                if (!InPlayArea(x, y))
                    continue;
                if (PlayerNear(x, y, EliteSpawnAwayFromPlayersRaw))
                    continue;
                Helpers.ObjectState at = findOpenWarpBot(_arena, x, y, 150);
                if (at == null || !InPlayArea(at.positionX, at.positionY, 120))
                    continue;
                if (!IsOpenConnectedGround(at.positionX, at.positionY))
                    continue;   // inside a sealed building / physics pocket
                tries++;
                double nearest = 4000;
                foreach (Bots.Bot b in _arena.ActiveBots)
                {
                    if (b == null || b.IsDead || b._state == null)
                        continue;
                    double d = Helpers.distanceTo(at.positionX, at.positionY, b._state.positionX, b._state.positionY);
                    if (d < nearest) nearest = d;
                }
                double score = nearest + _arena._rand.NextDouble() * 150;   // spread out, a little random
                if (score > bestScore) { bestScore = score; best = at; }
            }
            return best;
        }
    }
}
