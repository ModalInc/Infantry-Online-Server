using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-27, owner: "how much bots can we actually have so the map feels alive? i was thinking atleast 2 in sector A area,
    /// 2 in sector B, and 4 in sector C. thats already 80 bots for bases. then we have 2 alien swarms, queen and king, and then 2
    /// pirate raiders ... then on top of that we need 6 man squads there to make it appear alive, with the vech team included ...
    /// maybe we have like... 4 6man squads in sector A and another 4 in sector C, and then 6 in sector C")
    ///
    /// The open world's bot plan (EolMap.OpenWorld):
    ///   - 8 bot bases of 10: 2 in Sector A, 2 in B, 4 in C (BaseSectorPlan - each base team always builds in its sector);
    ///   - 4 swarm sites of 12 + their boss: the Alien Queen, the Alien King and two Pirate Raider crews (AlienSwarm.cs);
    ///   - 14 squads of 6: 4 in A, 4 in B, 6 in C, the vehicle squad (StarFire Corps) one of C's (SquadHomeSectors);
    ///   - 4 Harriers (2 bots each): 1 in A, 1 in B, 2 in C, over two teams (HarrierCrew.cs).
    /// Squads and Harrier crews land in their home sector and roam it (BotMayGo / PickPointInTerritory); a squad with a
    /// player on it follows the player anywhere. Bases guard their HQ wherever it stands.
    /// Server load is written to server-eol\eol-perf.log every 30 s (PollOpenWorldPerf) to find how many bots it carries.
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>The five base teams added for the open world (their .cfg teams: TeamInfo22-26).</summary>
        static readonly string[] ExtraBaseTeamNames = { "Ceres Combine", "Io Vanguard", "Rhea Separatists", "Enceladus Front", "Triton Dominion" };
        /// <summary>Base team i builds in BaseSectorPlan[i] (teams in order: botTeam1-3, then ExtraBaseTeamNames).</summary>
        static readonly string[] BaseSectorPlan = { "Sector A", "Sector A", "Sector B", "Sector B", "Sector C", "Sector C", "Sector C", "Sector C" };
        readonly List<Team> _baseTeams = new List<Team>();

        /// <summary>(2026-11-27) the squads' home sectors, by team name: 4 in A, 4 in B, 6 in C (StarFire Corps = the vehicle squad).</summary>
        static readonly Dictionary<string, string> SquadHomeSectors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Titan Militia", "Sector A" }, { "Collective Military", "Sector A" }, { "Europan Mercs", "Sector A" }, { "Oberon Lancers", "Sector A" },
            { "Morgan Raiders", "Sector B" }, { "Tethys Consortium", "Sector B" }, { "United Moons", "Sector B" }, { "Hyperion Guard", "Sector B" },
            { "Phobos Militarius", "Sector C" }, { "Callisto Irregulars", "Sector C" }, { "Ganymede Rangers", "Sector C" },
            { "Deimos Hussars", "Sector C" }, { "Vesta Dragoons", "Sector C" }, { "StarFire Corps", "Sector C" },
        };

        /// <summary>Called right after botTeam1-3 are made: the base list, plus the open world's extra bases and second Harrier team.</summary>
        void CreateOpenWorldTeams()
        {
            _baseTeams.Clear();
            _baseTeams.Add(botTeam1);
            _baseTeams.Add(botTeam2);
            _baseTeams.Add(botTeam3);
            if (!EolMap.OpenWorld)
                return;
            foreach (string n in ExtraBaseTeamNames)
                _baseTeams.Add(CreateAndRegisterBotTeam(n));
            botTeamHarrier2 = CreateAndRegisterBotTeam(HarrierTeam2Name);
        }

        /// <summary>The base teams in play (3, or the open world's 8).</summary>
        IEnumerable<Team> ActiveBaseTeams()
        {
            int n = Math.Min(RosterBaseTeams, _baseTeams.Count);
            for (int i = 0; i < n; i++)
                if (_baseTeams[i] != null)
                    yield return _baseTeams[i];
        }

        /// <summary>Index of a base team in play, -1 = not a base team.</summary>
        int BaseTeamIndex(Team t)
        {
            if (t == null)
                return -1;
            if (_baseTeams.Count == 0)   // (before the teams are made)
                return t == botTeam1 ? 0 : t == botTeam2 ? 1 : t == botTeam3 ? 2 : -1;
            int n = Math.Min(RosterBaseTeams, _baseTeams.Count);
            for (int i = 0; i < n; i++)
                if (_baseTeams[i] == t)
                    return i;
            return -1;
        }

        /// <summary>(2026-11-27, owner: "bot bases can choose any major resource points at random spread out across map. so its
        /// even") off: the bases go up at any Major Resource Point - the free one farthest from the other live bases (one of the
        /// two farthest, at random: PickSpreadHqSlot), so they spread evenly. True = the fixed A,A,B,B,C,C,C,C plan.</summary>
        const bool BaseSectorPlanEnabled = false;

        /// <summary>Open world: the sector this base team builds in (null = anywhere).</summary>
        string BaseSectorOf(Team t)
        {
            if (!EolMap.OpenWorld || !BaseSectorPlanEnabled)
                return null;
            int i = BaseTeamIndex(t);
            return i >= 0 && i < BaseSectorPlan.Length ? BaseSectorPlan[i] : null;
        }

        /// <summary>Open world: the sector a squad team lives in (null = none / not open world).</summary>
        static string SquadHomeSectorOf(Team t)
        {
            string s;
            return EolMap.OpenWorld && t != null && t._name != null && SquadHomeSectors.TryGetValue(t._name, out s) ? s : null;
        }

        /// <summary>
        /// Open world: the sector this bot lands in and roams - its squad's (while no player is on the squad) or its Harrier
        /// crew's. Null = no home (bases, swarms, assassins, a squad following its players...).
        /// </summary>
        string HomeSectorOf(Bot b)
        {
            if (!EolMap.OpenWorld || b == null || b._team == null)
                return null;
            if (IsHarrierCrewTeam(b._team))
            {
                HarrierCrew c = HarrierCrewOf(b);
                return c != null ? c.HomeSector : null;
            }
            string home = SquadHomeSectorOf(b._team);
            if (home == null)
                return null;
            foreach (InfServer.Game.Player p in b._team.ActivePlayers)
                if (p != null && !p.IsSpectator)
                    return null;   // (a player leads it: it goes where they go)
            return home;
        }

        /// <summary>A random open spot in a sector for a roaming bot (null = none found).</summary>
        Helpers.ObjectState PickPointInSector(Bot b, string sector, Random r)
        {
            short minX, maxX, minY, maxY;
            if (_eol == null || r == null || !_eol.TryGetSectorBounds(sector, out minX, out maxX, out minY, out maxY))
                return null;
            for (int tries = 0; tries < 40; tries++)
            {
                short x = (short)r.Next(Math.Max(64, (int)minX), Math.Max(65, (int)maxX));
                short y = (short)r.Next(Math.Max(64, (int)minY), Math.Max(65, (int)maxY));
                if (!_eol.IsInSector(sector, x, y) || NearNewPlayerBuffer(x, y))
                    continue;
                if (!InPlayArea(x, y, 0) || (b != null && !EolHqPatrol.IsOpenForBot(b, x, y)))
                    continue;
                return new Helpers.ObjectState { positionX = x, positionY = y };
            }
            return null;
        }

        // ---------- server load (2026-11-27, owner: "how much bots can we actually have") ----------
        const bool OpenWorldPerfLog = true;
        int _perfTickLast, _perfWindowStart, _perfTicks, _perfMaxGap;
        long _perfGapSum;
        string _perfFile;

        /// <summary>Every poll: the gap since the last poll (the server's tick). Every 30 s one line to eol-perf.log: average and
        /// worst tick gap, live bots, players. Gaps growing well past ~30 ms with more bots = the server is running out of room.</summary>
        void PollOpenWorldPerf(int now)
        {
            if (!OpenWorldPerfLog || _arena == null)
                return;
            if (_perfTickLast != 0)
            {
                int gap = now - _perfTickLast;
                _perfGapSum += gap;
                _perfTicks++;
                if (gap > _perfMaxGap) _perfMaxGap = gap;
            }
            _perfTickLast = now;
            if (_perfWindowStart == 0)
                _perfWindowStart = now;
            if (now - _perfWindowStart < 30000 || _perfTicks == 0)
                return;
            try
            {
                int bots = 0, players = 0;
                foreach (Bot b in _arena.ActiveBots)
                    if (b != null && !b.IsDead && !b.bCondemned)
                        bots++;
                foreach (InfServer.Game.Player p in _arena.PlayersIngame)
                    if (p != null)
                        players++;
                if (_perfFile == null)
                    _perfFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eol-perf.log");
                File.AppendAllText(_perfFile, string.Format("{0:yyyy-MM-dd HH:mm:ss} bots {1} (target {2}) players {3} vehicles {4} | tick avg {5:0.0} ms, worst {6} ms{7}",
                    DateTime.Now, bots, EolBotTotalTarget, players, _arena.Vehicles.Count(), (double)_perfGapSum / _perfTicks, _perfMaxGap, Environment.NewLine));
            }
            catch (Exception) { }
            _perfWindowStart = now;
            _perfTicks = 0;
            _perfGapSum = 0;
            _perfMaxGap = 0;
        }
    }
}
