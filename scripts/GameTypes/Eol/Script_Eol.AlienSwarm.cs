using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;
using InfServer.Scripting;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-16c, owner) THE ALIEN SWARM - a bug base that comes and goes:
    ///   - Bugs spawn in the zone owner's sealed bug cave (tile 1639,418 - red walls, dirt floor, east of the map):
    ///     a safe zone, nobody can hurt them there and they never fight each other. They walk to the cave's portal
    ///     and come out of the "worm tunnel" - the swarm's nest structure out in the field (Nydus-worm style).
    ///   - The nest appears at the next swarm site (first tile 76,178 in A3, then 55,419, then round again) with
    ///     "An Alien swarm has appeared in Eol!". It works like an HQ: 10 bugs defend it and are replaced while it
    ///     stands.
    ///   - Players destroy it: "The Alien swarm has been defeated by team X" and every player of team X gets
    ///     $90,000 + 13,000 xp. The swarm is gone for 5 minutes, then appears at the next site.
    /// No turrets or other bug structures (owner): the nest itself is defenceless, only its aliens defend it.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool AlienSwarmEnabled = true;
        /// <summary>
        /// The nest: the "Alien Queen" (veh 835, 2026-11-16e, owner: "the blue skrall") - the Predator's standing sprite
        /// (alien.blo AlienS, from the stationary Alien Chief 431) without its gun, 500 HP like an HQ, destroyable.
        /// </summary>
        const int SwarmNestVehId = 835;
        /// <summary>(2026-11-17, owner: "if the alien queen can move around") the moving Queen: a bot on veh 836 (the
        /// Predator's body, the Queen's HP / armour, BioBurst + Stomp; AS&D added as her misc gun) - Bots/AlienHorde/Queen.cs.
        /// The standing 835 is only the fallback if she can't be spawned.</summary>
        const ushort SwarmQueenBotVehId = 836;
        const int SwarmQueenMiscItemId = 1397;   // AS&D: Alien Hot Spit + a Wasp Drone out each side
        public Vehicle SwarmNest { get { return _swarmNest; } }
        /// <summary>Where the Queen came up - she wanders round it and never strays far.</summary>
        public Helpers.ObjectState SwarmHome { get { return _sw.Home; } private set { _sw.Home = value; } }

        // ---------- (2026-11-19, owner: "lets also do another alien swarm, this time its an alien King ... instead of the pink
        // weapon the queen has, he has an alien flame thrower [the Pirate Flamer, owner's pick]. and they should always spawn
        // on opposite sides of the map. make the king a different color too ... red") two swarms, the same code: all the
        // swarm's state lives in a SwarmRun; _sw is the one being worked on (every entry point picks it first). ----------
        /// <summary>The Alien King (veh 837 in ChaosInEol.veh): the Queen's body in red, the Pirate Flamer instead of BioBurst.</summary>
        const ushort SwarmKingBotVehId = 837;
        /// <summary>The King only on Chaos in Eol (Pioneer's .veh has no 837).</summary>
        static bool AlienKingEnabled { get { return true; } }   // (2026-11-21, owner: "bring in the alien king from our eol classic" - veh 837 added to PioneerStation.veh)
        class SwarmRun
        {
            public string Title;            // "Alien Queen" / "Alien King"
            public ushort BotVehId;
            public Team Team;
            public Vehicle Nest;
            public bool Active, Opening;
            public int NextTick, NextBugTick, SiteIndex, TickPoll, NestGoneTick, TickRally;
            public Helpers.ObjectState Home;
            public readonly Dictionary<Bots.Bot, int> Deploying = new Dictionary<Bots.Bot, int>();
            public List<Helpers.ObjectState> Posts;
            public Vehicle PostsFor;
            public readonly Dictionary<Bots.Bot, int> PostOf = new Dictionary<Bots.Bot, int>();
            public readonly Dictionary<Bots.Bot, int> PostUntil = new Dictionary<Bots.Bot, int>();
            public readonly Dictionary<Bots.Bot, Helpers.ObjectState> LoiterAt = new Dictionary<Bots.Bot, Helpers.ObjectState>();
            public readonly Dictionary<Bots.Bot, int> LoiterNext = new Dictionary<Bots.Bot, int>();
            public readonly Dictionary<Bots.Bot, int> ChaseUntil = new Dictionary<Bots.Bot, int>();
            public readonly HashSet<Bots.Bot> Returning = new HashSet<Bots.Bot>();
            public readonly Dictionary<Bots.Bot, StuckSample> Stuck = new Dictionary<Bots.Bot, StuckSample>();
        }
        readonly SwarmRun _queenRun = new SwarmRun { Title = "Alien Queen", BotVehId = SwarmQueenBotVehId };
        readonly SwarmRun _kingRun = new SwarmRun { Title = "Alien King", BotVehId = SwarmKingBotVehId };
        SwarmRun _swCur;
        SwarmRun _sw { get { return _swCur ?? _queenRun; } set { _swCur = value; } }
        IEnumerable<SwarmRun> SwarmRuns()
        {
            yield return _queenRun;
            if (AlienKingEnabled && _kingRun.Team != null)
                yield return _kingRun;
        }
        public int SwarmRunCount { get { return AlienKingEnabled ? 2 : 1; } }
        SwarmRun SwarmRunOf(Team t)
        {
            if (t == null) return null;
            foreach (SwarmRun r in SwarmRuns()) if (r.Team == t) return r;
            return null;
        }
        /// <summary>Works on the swarm this team belongs to; false = not a swarm team.</summary>
        bool UseSwarmOf(Team t)
        {
            SwarmRun r = SwarmRunOf(t);
            if (r == null) return false;
            _sw = r;
            return true;
        }
        /// <summary>The nest (Queen / King) of this swarm team, or null.</summary>
        public Vehicle SwarmNestOf(Team t) { SwarmRun r = SwarmRunOf(t); return r == null ? null : r.Nest; }
        /// <summary>Where this Queen / King came up.</summary>
        public Helpers.ObjectState SwarmHomeOf(Vehicle nest)
        {
            foreach (SwarmRun r in SwarmRuns()) if (r.Nest == nest) return r.Home;
            return null;
        }
        /// <summary>This swarm team's nest is up (its bugs come back through the cave).</summary>
        public bool SwarmAliveFor(Team t) { SwarmRun r = SwarmRunOf(t); return r != null && r.Active && r.Nest != null && !r.Nest.IsDead; }
        public bool SwarmBugDeploying(Bots.Bot b) { SwarmRun r = b == null ? null : SwarmRunOf(b._team); return r != null && r.Deploying.ContainsKey(b); }
        /// <summary>The live Queen / King close to a point, by name (for "killed by"), or null.</summary>
        public string SwarmNestNameNear(short x, short y, int range)
        {
            foreach (SwarmRun r in SwarmRuns())
                if (r.Nest != null && !r.Nest.IsDead && r.Nest._state != null
                    && Helpers.distanceTo(r.Nest._state, new Helpers.ObjectState { positionX = x, positionY = y }) < range)
                    return r.Title;
            return null;
        }
        /// <summary>The cave in tiles (Pioneer's cave box, moved with the cave on Classic).</summary>
        static int CaveTX(int pioneerTile) { return pioneerTile + EolMap.CaveTileX - 1639; }
        static int CaveTY(int pioneerTile) { return pioneerTile + EolMap.CaveTileY - 418; }
        public const int SwarmDefenders = 12;   // (2026-11-17, owner: "have the alien base team 12 instead"; was 10)
        public const int SwarmRewardCash = 24525, SwarmRewardExp = 6500,   /* (2026-11-17, owner; was 95k / 12k) (2026-11-19, owner: cash 31,200 -> 24,525) */ SwarmRewardPoints = EolBotHqDestroyPointsPerPlayer;   // (2026-11-16f, owner: was 90k / 13k)
        const int SwarmReturnMs = 5 * 60 * 1000;
        const int SwarmFirstDelayMs = 60 * 1000;      // first nest a minute after the match is running
        const int SwarmOpeningSpawnMs = 1200;         // the first 10 come quickly
        const int SwarmRefillMs = 12000;              // then a replacement every 12 s
        const int SwarmDeployTimeoutMs = 25000;
        /// <summary>The bug cave, in tiles: where bugs appear and the portal they walk into.</summary>
        static short SwarmCaveTileX { get { return EolMap.CaveTileX; } }
        static short SwarmCaveTileY { get { return EolMap.CaveTileY; } }
        /// <summary>(2026-11-16e) the zone owner's floor hole in the cave (9 x 9 tiles, x 1701-1709, y 412-420).</summary>
        static short SwarmCavePortalTileX { get { return EolMap.CavePortalTileX; } }
        static short SwarmCavePortalTileY { get { return EolMap.CavePortalTileY; } }
        const int SwarmCaveSpawnRadius = 160;
        /// <summary>Where the nest comes up, in tiles, in turn (the owner's spots: A3, then the south-west).</summary>
        static short[][] SwarmSitesTiles { get { return EolMap.SwarmSites; } }
        /// <summary>(2026-11-18, owner: "add a possible queen spawn in this area 342,260 and 448,259 and 230,246 and 629,64")
        /// the third site (index 2): one of these, at random among those in an open sector.</summary>
        static short[][] SwarmExtraSpotsTiles { get { return EolMap.SwarmExtraSpots; } }
        /// <summary>(2026-11-17, owner) the A3 site (index 0) is one of these, picked at random each time.</summary>
        static short[][] SwarmA3SpotsTiles { get { return EolMap.SwarmA3Spots; } }
        /// <summary>(2026-11-17, owner) the A6 site (index 1) is one of these, picked at random each time.</summary>
        static short[][] SwarmA6SpotsTiles { get { return EolMap.SwarmA6Spots; } }
        /// <summary>The A6 area (tiles) the swarm and the bases share: x 0-119, y 355-440 (round the four A6 spots).</summary>
        static short SwarmA6MinTX { get { return EolMap.SwarmA6MinTX; } }
        static short SwarmA6MaxTX { get { return EolMap.SwarmA6MaxTX; } }
        static short SwarmA6MinTY { get { return EolMap.SwarmA6MinTY; } }
        static short SwarmA6MaxTY { get { return EolMap.SwarmA6MaxTY; } }

        static bool InSwarmA6(int rawX, int rawY)
        {
            int tx = rawX / 16, ty = rawY / 16;
            return tx >= SwarmA6MinTX && tx <= SwarmA6MaxTX && ty >= SwarmA6MinTY && ty <= SwarmA6MaxTY;
        }

        /// <summary>A live HQ (bot or player base) in the A6 area: the Queen can't come up there while it stands.</summary>
        bool LiveHqInSwarmA6()
        {
            if (_arena == null)
                return false;
            foreach (Vehicle v in _arena.Vehicles)
                if (v != null && v._type != null && IsHqVehId(v._type.Id) && !v.IsDead && !v.bCondemned && v._state != null
                    && InSwarmA6(v._state.positionX, v._state.positionY))
                    return true;
            return false;
        }

        /// <summary>(2026-11-17, owner: "no human bases spawn in a6 if bugs are there") true = this spot is in A6 and the
        /// Queen is up in A6 - no base may go up there.</summary>
        public bool SwarmHoldsA6(short x, short y)
        {
            foreach (SwarmRun r in SwarmRuns())
                if (r.Active && r.Nest != null && !r.Nest.IsDead && r.Nest._state != null
                    && InSwarmA6(r.Nest._state.positionX, r.Nest._state.positionY) && InSwarmA6(x, y))
                    return true;
            return false;
        }
        /// <summary>Bug mix: Arachnomorph, Electromorph, Predator, Warrior, Brute - and one Alien Chief.</summary>
        /// (2026-11-17, owner: "bots can only be created car vehicles" in the log) the Alien Brute 433 is a Computer
        /// (turret) in the .veh - a bot can't use it, so every Brute spawn failed; a second Arachnomorph (the other heavy) instead.
        static readonly ushort[] SwarmBugPool = { 149, 153, 154, 155, 155, 149 };
        const ushort SwarmChiefVehId = 200;
        /// <summary>The four bug types, kept even (owner: "make sure its a good mix of aliens always").</summary>
        static readonly ushort[] SwarmBugTypes = { 149, 153, 154, 155 };

        /// <summary>The bug type the living swarm has fewest of (random between ties) - so 12 = Chief + about 3 of each.</summary>
        ushort PickSwarmBugType()
        {
            Dictionary<ushort, int> n = new Dictionary<ushort, int>();
            foreach (ushort t in SwarmBugTypes) n[t] = 0;
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._team == botTeamSwarm && !b.IsDead && !b.bCondemned && b._type != null && n.ContainsKey((ushort)b._type.Id))
                    n[(ushort)b._type.Id]++;
            int min = int.MaxValue;
            foreach (int c in n.Values) if (c < min) min = c;
            List<ushort> fewest = new List<ushort>();
            foreach (KeyValuePair<ushort, int> kv in n) if (kv.Value == min) fewest.Add(kv.Key);
            return fewest[_arena._rand.Next(fewest.Count)];
        }

        public Team botTeamSwarm { get { return _sw.Team; } }
        Vehicle _swarmNest { get { return _sw.Nest; } set { _sw.Nest = value; } }
        bool _swarmActive { get { return _sw.Active; } set { _sw.Active = value; } }
        int _swarmNextTick { get { return _sw.NextTick; } set { _sw.NextTick = value; } }
        int _swarmNextBugTick { get { return _sw.NextBugTick; } set { _sw.NextBugTick = value; } }
        int _swarmSiteIndex { get { return _sw.SiteIndex; } set { _sw.SiteIndex = value; } }
        int _tickSwarmPoll { get { return _sw.TickPoll; } set { _sw.TickPoll = value; } }
        Dictionary<Bots.Bot, int> _swarmDeploying { get { return _sw.Deploying; } }
        List<Helpers.ObjectState> _caveSpots;

        public bool IsSwarmTeam(Team t) { return t != null && SwarmRunOf(t) != null; }

        /// <summary>The swarm and the alien packs are all bugs: allies.</summary>
        public bool IsBugTeam(Team t) { return IsAlienBotTeam(t) || IsSwarmTeam(t); }

        static short TileRaw(short t) { return (short)(t * 16 + 8); }

        void CreateSwarmTeam()
        {
            _queenRun.Team = CreateAndRegisterBotTeam("Alien Swarm Bots");
            if (AlienKingEnabled)
                _kingRun.Team = CreateAndRegisterBotTeam("Alien King Swarm Bots");
        }

        /// <summary>(2026-11-21, owner: "actualy lets have both swarms out") the Queen's and the King's swarm both run.</summary>
        void PollAlienSwarm(int now)
        {
            foreach (SwarmRun r in new List<SwarmRun>(SwarmRuns()))
            {
                _sw = r;
                PollAlienSwarmRun(now);
            }
            _sw = null;
        }

        /// <summary>Every swarm off the map at once (sector change) - the next boss comes after the usual wait.</summary>
        void EndAllSwarms(int now)
        {
            SwarmRun ended = null;
            foreach (SwarmRun r in new List<SwarmRun>(SwarmRuns()))
                if (r.Active)
                {
                    _sw = r;
                    EndSwarm(now, null);
                    ended = r;
                }
            _sw = null;
        }

        void PollAlienSwarmRun(int now)
        {
            if (!AlienSwarmEnabled || botTeamSwarm == null || _arena == null || !_arena._bGameRunning || _bbetweengames
                || _eol == null || !_eol.MatchSectorPlayReady || !EolHasArenaPresence())
                return;
            if (now - _tickSwarmPoll < 500)
                return;
            _tickSwarmPoll = now;
            if (_swarmActive)
                PollSwarmRally(now);

            if (!_swarmActive)
            {
                if (_swarmNextTick == 0)
                    _swarmNextTick = NonZeroTick(now + SwarmFirstDelayMs);
                if (now - _swarmNextTick >= 0)
                    StartSwarm(now);
                return;
            }

            // the Queen is gone. (2026-11-16e, "killing it gave no reward") this poll used to end the swarm the moment
            // it saw her dead - before the Vehicle.Death event that knows the killer arrived, so nobody was paid. Now
            // it waits 3 s for that event; only if none comes does the last player team that hurt her win.
            if (_swarmNest == null || _swarmNest.bCondemned || _swarmNest.IsDead)
            {
                if (_swarmNestGoneTick == 0)
                    _swarmNestGoneTick = NonZeroTick(now);
                if (now - _swarmNestGoneTick < 3000)
                    return;
                EndSwarm(now, _swarmNest != null && _swarmNest._state != null && _swarmNest._state.health <= 0
                    ? ResolveSwarmKillers(_swarmNest, null) : null);
                return;
            }
            _swarmNestGoneTick = 0;

            // keep 10 defenders (bugs that are still on their way through the cave count)
            int live = 0;   // (dead ones waiting to come back through the cave count: no extra bugs)
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b != _swarmNest && b._team == botTeamSwarm && !b.bCondemned && (!b.IsDead || b.bDropshipRespawnPending))
                    live++;   // (2026-11-17: not the Queen herself - she's a bot now)
            if (live < SwarmDefenders && now - _swarmNextBugTick >= 0)
            {
                bool opening = live < SwarmDefenders && _swarmOpening;
                if (SpawnSwarmBug(now, needChief: !SwarmHasChief()))
                    _swarmNextBugTick = now + (opening ? SwarmOpeningSpawnMs : SwarmRefillMs);
                else
                    _swarmNextBugTick = now + 2000;
                if (live + 1 >= SwarmDefenders)
                    _swarmOpening = false;
            }
        }

        bool _swarmOpening { get { return _sw.Opening; } set { _sw.Opening = value; } }
        int _swarmNestGoneTick { get { return _sw.NestGoneTick; } set { _sw.NestGoneTick = value; } }

        bool SwarmHasChief()
        {
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._team == botTeamSwarm && !b.IsDead && b._type != null && b._type.Id == SwarmChiefVehId)
                    return true;
            return false;
        }

        /// <summary>A random spot of the list that lies in an open sector (null: none open).</summary>
        short[] PickOpenSwarmSpot(short[][] spots)
        {
            List<short[]> open = new List<short[]>();
            foreach (short[] t in spots)
                if (_eol != null && _eol.IsInAnyActiveSector(TileRaw(t[0]), TileRaw(t[1])))
                    open.Add(t);
            return open.Count == 0 ? null : open[_arena._rand.Next(open.Count)];
        }

        /// <summary>The owner's swarm squares per sector (map grid: 80 tiles a square, letter = column from A, number = row
        /// from 1).</summary>
        static readonly Dictionary<string, string[]> SwarmSquares = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Sector A", new[] { "A2", "A3", "B2", "B3", "A5", "A6", "B6", "E4", "F4" } },
            { "Sector B", new[] { "B10", "C10", "E11", "G11", "H11", "H8", "H9" } },
            { "Sector C", new[] { "K2", "L2", "K11", "I7", "P8", "Q3" } },
        };

        static void SquareTiles(string sq, out int x0, out int y0)
        {
            x0 = (char.ToUpperInvariant(sq[0]) - 'A') * 80;
            y0 = (int.Parse(sq.Substring(1)) - 1) * 80;
        }

        /// <summary>A spot in one of the open sectors' squares, as far as it can be from the other boss.</summary>
        Helpers.ObjectState PickSwarmSquareSite()
        {
            if (_eol == null || _eol._activeSectors == null)
                return null;
            List<KeyValuePair<string, string>> squares = new List<KeyValuePair<string, string>>();   // sector, square
            foreach (string sec in _eol._activeSectors)
            {
                string[] list;
                if (sec != null && SwarmSquares.TryGetValue(sec, out list))
                    foreach (string q in list)
                        squares.Add(new KeyValuePair<string, string>(sec, q));
            }
            if (squares.Count == 0)
                return null;
            Vehicle other = null;
            foreach (SwarmRun r in SwarmRuns())
                if (r != _sw && r.Active && r.Nest != null && !r.Nest.IsDead && r.Nest._state != null)
                    other = r.Nest;
            Func<KeyValuePair<string, string>, double> farFromOther = kv =>
            {
                if (other == null) return 0;
                int x0, y0; SquareTiles(kv.Value, out x0, out y0);
                return Helpers.distanceTo(x0 * 16 + 640, y0 * 16 + 640, other._state.positionX, other._state.positionY);
            };
            if (other != null)
                squares.Sort((a, b) => farFromOther(b).CompareTo(farFromOther(a)));
            else
                squares = squares.OrderBy(q => _arena._rand.Next()).ToList();
            // the two farthest first (in random order), then the rest
            if (other != null && squares.Count > 1 && _arena._rand.Next(2) == 1)
            {
                var t = squares[0]; squares[0] = squares[1]; squares[1] = t;
            }
            foreach (KeyValuePair<string, string> kv in squares)
            {
                int x0, y0; SquareTiles(kv.Value, out x0, out y0);
                for (int attempt = 0; attempt < 300; attempt++)
                {
                    int px = (x0 + _arena._rand.Next(80)) * 16 + 8, py = (y0 + _arena._rand.Next(80)) * 16 + 8;
                    if (!_eol.IsInSector(kv.Key, px, py) || !BossGroundClear(px, py, attempt < 200 ? 96 : 64))
                        continue;
                    Log.write(TLog.Normal, "[Eol] {0}: square {1} ({2}).", _sw.Title, kv.Value, kv.Key);
                    return new Helpers.ObjectState { positionX = (short)px, positionY = (short)py };
                }
            }
            Log.write(TLog.Warning, "[Eol] {0}: no open spot in any swarm square of the open sectors.", _sw.Title);
            return null;
        }

        bool BossGroundClear(int x, int y, int radius)
        {
            for (int dy = -radius; dy <= radius; dy += 16)
                for (int dx = -radius; dx <= radius; dx += 16)
                {
                    int ax = x + dx, ay = y + dy;
                    if (ax < 0 || ay < 0 || _arena.getTile(ax, ay).Blocked || _arena.getTerrainID(ax, ay) != 0)
                        return false;
                }
            return true;
        }

        void StartSwarm(int now)
        {
            VehInfo nestInfo = _arena._server._assets.getVehicleByID(SwarmNestVehId);
            if (nestInfo == null)
            {
                _swarmNextTick = now + SwarmReturnMs;
                return;
            }
            // the next site that is open right now (radiation sectors rotate)
            Helpers.ObjectState site = null;
            // (2026-11-19) Chaos in Eol: one of the owner's ten map squares, at random (no rotation, every sector is open)
            if (EolMap.SwarmQueenSpots.Length > 0)
            {
                short[] t = PickSwarmSpotOppositeOther(EolMap.SwarmQueenSpots);
                short qx = TileRaw(t[0]), qy = TileRaw(t[1]);
                site = findOpenWarpBot(_arena, qx, qy, 120) ?? new Helpers.ObjectState { positionX = qx, positionY = qy };
            }
            // (2026-11-21, owner: "in sector A have the possible spawn points for alien swarm at around a2/a3/b2/b3 and a5/6 and
            // b6, and e4/f4 and in sector B ... b10/c10 and e11, and g11/h11 and h8/h9. and in sector C ... K2/L2,K11, i7,P8,Q3
            // and have them always spawn away from eachother i.e the king and the queen") a square of an open sector - one of
            // the two farthest from the other boss - and an open spot in it
            if (site == null)
                site = PickSwarmSquareSite();
            for (int i = 0; i < SwarmSitesTiles.Length && site == null; i++)
            {
                int idx = (_swarmSiteIndex + i) % SwarmSitesTiles.Length;
                // (owner: "if a base is there the bugs can only spawn on the A3 site, until that base is destroyed")
                if (idx == 1 && LiveHqInSwarmA6())
                    continue;
                short[] tile = idx == 0 ? SwarmA3SpotsTiles[_arena._rand.Next(SwarmA3SpotsTiles.Length)]
                    : idx == 1 ? SwarmA6SpotsTiles[_arena._rand.Next(SwarmA6SpotsTiles.Length)]
                    : idx == 2 ? PickOpenSwarmSpot(SwarmExtraSpotsTiles) : SwarmSitesTiles[idx];
                if (tile == null)
                    continue;
                short x = TileRaw(tile[0]), y = TileRaw(tile[1]);
                if (!_eol.IsInAnyActiveSector(x, y))
                    continue;
                site = findOpenWarpBot(_arena, x, y, 120) ?? new Helpers.ObjectState { positionX = x, positionY = y };
                _swarmSiteIndex = (idx + 1) % SwarmSitesTiles.Length;
            }
            if (site == null)
            {   // no site in an open sector: try again in a minute
                _swarmNextTick = now + 60000;
                return;
            }
            SwarmHome = new Helpers.ObjectState { positionX = site.positionX, positionY = site.positionY };
            _swarmNest = SpawnSwarmQueen(site) ?? _arena.newVehicle(nestInfo, botTeamSwarm, null, site);
            if (_swarmNest == null)
            {
                _swarmNextTick = now + 60000;
                return;
            }
            _swarmActive = true;
            _swarmOpening = true;
            _swarmNextBugTick = now;
            EnqueueExplosionRoute(AlienCrashExplosionProjectileId, site.positionX, site.positionY, 0, 0, 0);
            _arena.sendArenaMessage("&An " + _sw.Title + " has appeared! (" + Helpers.posToLetterCoord(site.positionX, site.positionY) + ")",
                _config.flag.victoryWarningBong);
            Log.write(TLog.Normal, "[Eol] {0} swarm up at {1},{2}.", _sw.Title, site.positionX, site.positionY);
        }

        /// <summary>
        /// (2026-11-19, owner: "they should always spawn on opposite sides of the map") with the other swarm up, one of the
        /// two spots farthest from its Queen / King; otherwise any spot.
        /// </summary>
        short[] PickSwarmSpotOppositeOther(short[][] spots)
        {
            Vehicle other = null;
            foreach (SwarmRun r in SwarmRuns())
                if (r != _sw && r.Active && r.Nest != null && !r.Nest.IsDead && r.Nest._state != null)
                    other = r.Nest;
            if (other == null)
                return spots[_arena._rand.Next(spots.Length)];
            List<short[]> byFar = new List<short[]>(spots);
            short ox = other._state.positionX, oy = other._state.positionY;
            byFar.Sort((a, b) => Helpers.distanceTo(ox, oy, TileRaw(b[0]), TileRaw(b[1])).CompareTo(Helpers.distanceTo(ox, oy, TileRaw(a[0]), TileRaw(a[1]))));
            return byFar[_arena._rand.Next(Math.Min(2, byFar.Count))];
        }

        /// <summary>The moving Queen as a bot (null: her vehicle isn't a Car in this zone - the standing one is used).</summary>
        Vehicle SpawnSwarmQueen(Helpers.ObjectState site)
        {
            if (!(_arena._server._assets.getVehicleByID(_sw.BotVehId) is VehInfo.Car))
            {
                Log.write(TLog.Warning, "[Eol] {0} bot vehicle {1} missing / not a Car - the standing Queen is used.", _sw.Title, _sw.BotVehId);
                return null;
            }
            AlienHorde q = _arena.newBot(typeof(AlienHorde), _sw.BotVehId, botTeamSwarm, null, site, this, null) as AlienHorde;
            if (q == null)
                return null;
            ItemInfo mi = _arena._server._assets.getItemByID(SwarmQueenMiscItemId);
            WeaponController wm = new WeaponController(q._state, new WeaponController.WeaponSettings());
            if (mi != null && wm.equip(mi)) { wm.Owner = q; EolBotBrain.SetMiscWeapon(q, wm); }
            q.bRespawn = false;   // (she never comes back through the cave - killing her ends the swarm)
            q.ShowHealthToAll = true;   // (owner: her HP bar shows to everyone - other aliens' still doesn't)
            return q;
        }

        /// <summary>Vehicle.Death: the swarm's nest.</summary>
        public bool OnSwarmNestDeath(Vehicle dead, InfServer.Game.Player killer)
        {
            SwarmRun run = null;
            foreach (SwarmRun r in SwarmRuns()) if (dead != null && r.Nest == dead) run = r;
            if (run == null)
                return false;
            _sw = run;
            EndSwarm(Environment.TickCount, ResolveSwarmKillers(dead, killer));
            return true;
        }

        /// <summary>
        /// The killer's team, like an HQ kill; if a bot / turret / the swarm itself made the last hit, the last player
        /// team that hurt her.
        /// </summary>
        Team ResolveSwarmKillers(Vehicle dead, InfServer.Game.Player killer)
        {
            if (killer != null && killer._team != null && !killer._team.IsSpec && killer._team != botTeamSwarm)
                return killer._team;
            Team t = null;
            if (dead != null && dead._attackers != null)
                foreach (InfServer.Game.Player p in dead._attackers)
                    if (p != null && p._team != null && p._team != botTeamSwarm && !p._team.IsSpec)
                        t = p._team;   // (the last one that hurt it)
            return t;
        }

        /// <summary>Nest down (or gone): reward, clear the swarm, back in 5 minutes at the next site.</summary>
        void EndSwarm(int now, Team winners)
        {
            if (!_swarmActive)
                return;
            _swarmActive = false;
            _swarmNestGoneTick = 0;
            if (winners != null)
            {   // (2026-11-16e, owner: "same style as the human base HQ") every player of the killing team, scaled
                // like an HQ kill (newer players' lifetime scale), with the HQ kill's message; the rest of the arena
                // hears who did it
                foreach (InfServer.Game.Player p in winners.ActivePlayers)
                {
                    if (p == null)
                        continue;
                    int cash = ApplyEolLifetimeRewardScale(p, SwarmRewardCash);
                    int exp = ApplyEolLifetimeRewardScale(p, SwarmRewardExp);
                    int points = ApplyEolLifetimeRewardScale(p, SwarmRewardPoints);
                    p.BonusPoints += points;
                    p.Cash += cash;
                    p.Experience += exp;
                    p.syncState();
                    p.sendMessage(0, string.Format("&{3} - Your team killed the {3}! Cash={0} Experience={1} Points={2}",
                        cash, exp, points, _sw.Title));
                }
                foreach (Team t in _arena.Teams)
                    if (t != winners)
                        t.sendArenaMessage("&" + _sw.Title + " - The " + _sw.Title + "'s swarm has been defeated by " + winners._name + "!");
                Log.write(TLog.Normal, "[Eol] {0} killed by {1} ({2} players paid).", _sw.Title, winners._name, winners.ActivePlayerCount);
            }
            else
                _arena.sendArenaMessage("&The " + _sw.Title + "'s swarm has burrowed away.", _config.flag.periodicBong);

            // the swarm goes with its nest
            List<Vehicle> gone = new List<Vehicle>();
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._team == botTeamSwarm && !b.bCondemned)
                    gone.Add(b);
            foreach (Vehicle v in gone)
                EnqueueVehicleDespawn(v, force: true);
            if (_swarmNest != null && !_swarmNest.IsDead && !_swarmNest.bCondemned)
                EnqueueVehicleDespawn(_swarmNest, force: true);
            _swarmNest = null;
            _swarmDeploying.Clear();
            _swarmReturning.Clear();
            _swarmStuck.Clear();
            _swarmChaseUntil.Clear();
            _swarmLoiterAt.Clear();
            _swarmLoiterNext.Clear();
            _swarmNextTick = NonZeroTick(now + SwarmReturnMs);
            Log.write(TLog.Normal, "[Eol] {0} swarm over ({1}); back in 5 min.", _sw.Title, winners != null ? winners._name : "no winner");
        }

        /// <summary>One bug in the cave, untouchable, on its way to the cave portal.</summary>
        bool SpawnSwarmBug(int now, bool needChief)
        {
            if (!CanSpawnBotOnTeam(botTeamSwarm))
                return false;
            Helpers.ObjectState at = PickSwarmCaveSpot();
            if (at == null)
                return false;
            ushort vid = needChief ? SwarmChiefVehId : PickSwarmBugType();
            if (!(_arena._server._assets.getVehicleByID(vid) is VehInfo.Car))
            {   // (a bot needs a Car vehicle - never ask the engine for anything else)
                Log.write(TLog.Warning, "[Eol] swarm bug vehicle {0} is not a Car - skipped.", vid);
                return false;
            }
            AlienHorde bug = _arena.newBot(typeof(AlienHorde), vid, botTeamSwarm, null, at, this, null) as AlienHorde;
            if (bug == null)
                return false;
            bug.SkillModeOverride = "veteran";
            EnableBotDropshipRespawn(bug, 0);   // (2026-11-16d) dies -> corpse -> back in the cave, same bug, no leave
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bug);
            BeginSwarmDeploy(bug, now);
            return true;
        }

        /// <summary>A free spot on the cave floor with a straight run to the cave portal.</summary>
        Helpers.ObjectState PickSwarmCaveSpot()
        {
            if (_caveSpots == null)
                _caveSpots = BuildStraightSpots(CaveTX(1617) * 16 + 8, CaveTX(1715) * 16 + 8, CaveTY(394) * 16 + 8, CaveTY(437) * 16 + 8,
                    TileRaw(SwarmCavePortalTileX), TileRaw(SwarmCavePortalTileY), 150, 1600, null);
            return FacingPortal(PickSpreadSpot(_caveSpots, _swarmDeploying.Keys)
                ?? findOpenWarpBot(_arena, TileRaw(SwarmCaveTileX), TileRaw(SwarmCaveTileY), SwarmCaveSpawnRadius, 24),
                TileRaw(SwarmCavePortalTileX), TileRaw(SwarmCavePortalTileY));
        }

        /// <summary>The bug is in the cave: untouchable, walking to the portal.</summary>
        void BeginSwarmDeploy(Bots.Bot bug, int now)
        {
            _swarmDeploying[bug] = now;
            bug.DamageImmuneUntilTick = NonZeroTick(now + SwarmDeployTimeoutMs + 5000);
            try { bug.Navigation?.Clear(); } catch { }
        }

        /// <summary>
        /// First thing in a swarm bug's poll. True = still walking through the cave to its portal (poll done);
        /// on arrival it comes out of the worm tunnel beside the nest.
        /// </summary>
        public bool PollSwarmDeploying(Bots.Bot b, int now)
        {
            int t0;
            if (b == null || !UseSwarmOf(b._team) || !_swarmDeploying.TryGetValue(b, out t0))
                return false;
            if (b.IsDead || b.bCondemned || _swarmNest == null || _swarmNest.IsDead)
            {
                _swarmDeploying.Remove(b);
                return false;
            }
            short px = TileRaw(SwarmCavePortalTileX), py = TileRaw(SwarmCavePortalTileY);
            if (Helpers.distanceTo(b._state.positionX, b._state.positionY, px, py) <= 72 || now - t0 > SwarmDeployTimeoutMs)
            {
                // (2026-11-17, owner: "bugs spawn in A6 and get stuck in a choke between green physics") come out at a
                // free guard post - open ground with a clear run to the Queen - never in a pocket next to her
                Helpers.ObjectState land = FreeSwarmPostSpot(b)
                    ?? findOpenWarpBot(_arena, _swarmNest._state.positionX, _swarmNest._state.positionY, 220, 28);
                if (land == null)
                {
                    b.HoldPosition();
                    return true;
                }
                _swarmDeploying.Remove(b);
                WarpBot(b, land);
                b.DamageImmuneUntilTick = NonZeroTick(now + BotSpawnProtectMs);
                return false;
            }
            SteerStraight(b, px, py);   // (spawn spots have a clear straight line to the portal)
            return true;
        }

        // ---------- guard posts (2026-11-16e, owner: "aliens stuck in a choke point between green physics, stacked") ----------

        List<Helpers.ObjectState> _swarmPosts { get { return _sw.Posts; } set { _sw.Posts = value; } }
        Vehicle _swarmPostsFor { get { return _sw.PostsFor; } set { _sw.PostsFor = value; } }
        Dictionary<Bots.Bot, int> _swarmPostOf { get { return _sw.PostOf; } }
        Dictionary<Bots.Bot, int> _swarmPostUntil { get { return _sw.PostUntil; } }

        /// <summary>
        /// Posts round the Queen: 16 directions x 4 rings (150 / 280 / 420 / 560 px), each on open ground with a
        /// body-wide straight line to her - so no post sits behind a choke or a green-physics gap.
        /// (2026-11-17, owner: "make the aliens roam out a bit more so its real") the outer ring went 380 -> 560.
        /// </summary>
        List<Helpers.ObjectState> SwarmPosts()
        {
            if (_swarmNest == null)
                return null;
            if (_swarmPosts != null && _swarmPostsFor == _swarmNest)
                return _swarmPosts;
            _swarmPostsFor = _swarmNest;
            _swarmPosts = new List<Helpers.ObjectState>();
            _swarmPostOf.Clear();
            short nx = _swarmNest._state.positionX, ny = _swarmNest._state.positionY;
            foreach (int r in new[] { 150, 280, 420, 560 })
                for (int k = 0; k < 16; k++)
                {
                    double a = k * Math.PI / 8 + (r == 280 || r == 560 ? Math.PI / 16 : 0);
                    short x = (short)(nx + Math.Cos(a) * r), y = (short)(ny + Math.Sin(a) * r);
                    bool open = true;
                    for (int oy = -16; oy <= 16 && open; oy += 16)
                        for (int ox = -16; ox <= 16 && open; ox += 16)
                            if (_arena.getTile((short)(x + ox), (short)(y + oy)).Blocked)
                                open = false;
                    if (open && WideLineClear(nx, ny, x, y) && InPlayArea(x, y, 0) && !NearNewPlayerBuffer(x, y))   // (2026-11-19: no post by the players' landing zone)
                        _swarmPosts.Add(new Helpers.ObjectState { positionX = x, positionY = y });
                }
            Log.write(TLog.Normal, "[Eol] {0} guard posts: {1}", _sw.Title, _swarmPosts.Count);
            return _swarmPosts;
        }

        /// <summary>
        /// A swarm bug with nothing to fight PATROLS round the Queen (2026-11-17, owner: "have them do patrol like moves,
        /// so its not all just stuck in one place"): it walks to a free post, sniffs round it for 3-9 s, then moves on to
        /// a free post nearby (120-380 px on) - a winding beat over all four rings, never two bugs on one post.
        /// False = no posts (the old patrol takes over).
        /// </summary>
        public bool SwarmGuard(Bots.Bot b, int now)
        {
            if (b == null || !UseSwarmOf(b._team))
                return false;
            List<Helpers.ObjectState> posts = SwarmPosts();
            if (posts == null || posts.Count == 0)
                return false;
            int idx, until;
            if (!_swarmPostOf.TryGetValue(b, out idx) || idx >= posts.Count)
            {
                idx = PickSwarmPost(b, posts, -1);
                _swarmPostOf[b] = idx;
                _swarmPostUntil.Remove(b);
            }
            Helpers.ObjectState post = posts[idx];
            if (Helpers.distanceTo(b._state, post) <= 56)
            {   // at the post: look round a few seconds, then on to the next one
                if (!_swarmPostUntil.TryGetValue(b, out until))
                    _swarmPostUntil[b] = until = now + 3000 + _arena._rand.Next(6000);
                if (now - until < 0)
                {
                    SwarmLoiter(b, post, now);
                    return true;
                }
                idx = PickSwarmPost(b, posts, idx);
                _swarmPostOf[b] = idx;
                _swarmPostUntil.Remove(b);
                post = posts[idx];
            }
            if (SwarmRescueIfStuck(b, post, now))
                return true;
            if (Helpers.distanceTo(b._state, post) > 48)
            {
                if (WideLineClear(b._state.positionX, b._state.positionY, post.positionX, post.positionY))
                    SteerStraight(b, post.positionX, post.positionY);
                else
                    b.ApplyNavigationApproach(post, EolHqPatrol.MakeLineClearPendingSeek(b, () => post), new BotNavigateOptions
                    {
                        RequeueMinMs = 1500,
                        PathStartIndex = 0,
                        Priority = PathPriority.Idle,
                    });
            }
            else
                b.HoldPosition();   // (the natural idle shuffle - like a player waiting)
            return true;
        }

        /// <summary>
        /// The next post on a bug's beat: a free one 120-380 px from the one it is at (so it walks on, not across the
        /// map); none free nearby = any free post; none free at all = any post.
        /// </summary>
        int PickSwarmPost(Bots.Bot b, List<Helpers.ObjectState> posts, int from)
        {
            HashSet<int> taken = new HashSet<int>();
            foreach (KeyValuePair<Bots.Bot, int> kv in _swarmPostOf)
                if (kv.Key != b && kv.Key != null && !kv.Key.IsDead && !kv.Key.bCondemned)
                    taken.Add(kv.Value);
            List<int> near = new List<int>(), free = new List<int>();
            for (int i = 0; i < posts.Count; i++)
            {
                if (taken.Contains(i) || i == from)
                    continue;
                free.Add(i);
                if (from >= 0)
                {
                    double d = Helpers.distanceTo(posts[i], posts[from]);
                    if (d >= 120 && d <= 380)
                        near.Add(i);
                }
            }
            if (near.Count > 0)
                return near[_arena._rand.Next(near.Count)];
            if (free.Count > 0)
                return free[_arena._rand.Next(free.Count)];
            return _arena._rand.Next(posts.Count);
        }

        Dictionary<Bots.Bot, Helpers.ObjectState> _swarmLoiterAt { get { return _sw.LoiterAt; } }
        Dictionary<Bots.Bot, int> _swarmLoiterNext { get { return _sw.LoiterNext; } }

        /// <summary>At a post: small moves round it (a new spot 25-70 px out every 1.2-2.7 s), like an animal sniffing round.</summary>
        void SwarmLoiter(Bots.Bot b, Helpers.ObjectState post, int now)
        {
            Helpers.ObjectState at;
            int next;
            if (!_swarmLoiterAt.TryGetValue(b, out at) || !_swarmLoiterNext.TryGetValue(b, out next) || now - next >= 0
                || Helpers.distanceTo(at, post) > 90)
            {
                at = null;
                for (int tries = 0; tries < 6 && at == null; tries++)
                {
                    double a = _arena._rand.NextDouble() * Math.PI * 2, r = 25 + _arena._rand.Next(45);
                    short x = (short)(post.positionX + Math.Cos(a) * r), y = (short)(post.positionY + Math.Sin(a) * r);
                    if (!_arena.getTile(x, y).Blocked && WideLineClear(b._state.positionX, b._state.positionY, x, y))
                        at = new Helpers.ObjectState { positionX = x, positionY = y };
                }
                _swarmLoiterAt[b] = at ?? post;
                _swarmLoiterNext[b] = now + 1200 + _arena._rand.Next(1500);
                at = _swarmLoiterAt[b];
            }
            if (Helpers.distanceTo(b._state, at) > 14)
                SteerStraight(b, at.positionX, at.positionY);
            else
                b.HoldPosition();
        }

        /// <summary>Whoever last hurt this bot, as a position (player, bot or machine), or null.</summary>
        static Helpers.ObjectState HurtByState(Bots.Bot b)
        {
            object h = b?.LastHurtBy;
            if (h is InfServer.Game.Player p)
                return p.IsDead || p.IsSpectator ? null : p._state;
            if (h is Vehicle v)
                return v.IsDead || v.bCondemned ? null : v._state;
            return null;
        }

        // ---------- leash + stuck rescue (2026-11-17, owner: "make sure they do not get stuck here and they go back to
        // defending their queen properly. have a radius they stick around to defend") ----------

        /// <summary>Swarm bugs fight anything inside this radius round the Queen; past it they break off and go back.</summary>
        // (2026-11-18, owner: "it doesnt have to be a tight area either since the queen moves now. they can roam a bit")
        public const int SwarmLeashRadius = 1100;   // (was 750)
        /// <summary>Once sent back, a bug keeps walking home until it is this close to the Queen.</summary>
        const int SwarmLeashHomeRadius = 650;   // (was 450)
        /// <summary>
        /// (2026-11-17, owner: "if they are attacked they do go out of the radius to attack the person and then they come
        /// back") a bug hurt in the last 5 s goes after its attacker out to this far from the Queen, for up to 15 s after
        /// the last hit - then it gives up and walks home.
        /// </summary>
        // (2026-11-18, owner: "if someone shoots they should go out and kill them, and return back to the area") farther
        // and longer (was 1500 px / 15 s), and the whole pack near the one hit goes (PollSwarmRally)
        const int SwarmChaseRadius = 2800, SwarmChaseMs = 20000, SwarmHurtWindowMs = 5000;
        const int SwarmRallyRadius = 1000;   // bugs this close to a hurt bug / the Queen go after the shooter too
        int _tickSwarmRally { get { return _sw.TickRally; } set { _sw.TickRally = value; } }

        /// <summary>
        /// A bug or the Queen was just shot by a player / enemy bot: every bug within SwarmRallyRadius of it is let off the
        /// leash to chase (SwarmChaseMs) and the shooter goes out as the swarm's combat call, so they target him even
        /// before they see him. They walk home once the chase runs out.
        /// </summary>
        void PollSwarmRally(int now)
        {
            if (_swarmNest == null || _swarmNest.IsDead || now - _tickSwarmRally < 400)
                return;
            _tickSwarmRally = now;
            List<Bots.Bot> swarm = new List<Bots.Bot>();
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._team == botTeamSwarm && !b.IsDead && !b.bCondemned && b._state != null)
                    swarm.Add(b);
            foreach (Bots.Bot hurt in swarm)
            {
                if (hurt.LastHurtBy == null || now - hurt.LastHurtTick > 1500)
                    continue;
                InfServer.Game.Player byP = hurt.LastHurtBy as InfServer.Game.Player;
                Vehicle byV = byP == null ? hurt.LastHurtBy as Vehicle : null;
                Team byTeam = byP != null ? byP._team : byV != null ? byV._team : null;
                Helpers.ObjectState att = HurtByState(hurt);
                if (att == null || byTeam == botTeamSwarm || Helpers.distanceTo(att, _swarmNest._state) > SwarmChaseRadius)
                    continue;
                PublishCombatCall(botTeamSwarm, hurt, byP, byV, now);
                foreach (Bots.Bot b in swarm)
                    if (b != _swarmNest && Helpers.distanceTo(b._state, hurt._state) <= SwarmRallyRadius)
                        _swarmChaseUntil[b] = now + SwarmChaseMs;
            }
        }
        Dictionary<Bots.Bot, int> _swarmChaseUntil { get { return _sw.ChaseUntil; } }
        /// <summary>The choke the owner reported (tiles, A6): a bug found in it is moved straight back to a post.</summary>
        static short[][] SwarmChokeTiles { get { return EolMap.SwarmChokeTiles; } }
        const int SwarmChokeRadius = 10 * 16;

        HashSet<Bots.Bot> _swarmReturning { get { return _sw.Returning; } }
        class StuckSample { public short X, Y; public int Since; }
        Dictionary<Bots.Bot, StuckSample> _swarmStuck { get { return _sw.Stuck; } }

        /// <summary>
        /// First in a swarm bug's poll after its deploy: true = it is out past the leash (or in the choke), so it was
        /// sent back to its guard post this poll and must not chase anything.
        /// </summary>
        public bool SwarmLeash(Bots.Bot b, int now)
        {
            if (b == null || !UseSwarmOf(b._team) || _swarmNest == null || _swarmNest.IsDead || b.IsDead)
                return false;
            short bx = b._state.positionX, by = b._state.positionY;
            foreach (short[] c in SwarmChokeTiles)
                if (Helpers.distanceTo(bx, by, TileRaw(c[0]), TileRaw(c[1])) <= SwarmChokeRadius)
                {
                    Helpers.ObjectState spot = FreeSwarmPostSpot(b);
                    if (spot != null)
                    {
                        WarpBot(b, spot);
                        _swarmReturning.Remove(b);
                        _swarmStuck.Remove(b);
                        return true;
                    }
                }
            double d = Helpers.distanceTo(b._state, _swarmNest._state);
            // attacked: go after the attacker (outside the leash too), within the chase radius and time
            if (b.LastHurtBy != null && now - b.LastHurtTick < SwarmHurtWindowMs)
            {
                Helpers.ObjectState att = HurtByState(b);
                if (att != null && Helpers.distanceTo(att, _swarmNest._state) <= SwarmChaseRadius)
                    _swarmChaseUntil[b] = now + SwarmChaseMs;
            }
            int chaseUntil;
            if (_swarmChaseUntil.TryGetValue(b, out chaseUntil))
            {
                if (now - chaseUntil < 0 && d <= SwarmChaseRadius)
                {
                    _swarmReturning.Remove(b);
                    return false;   // (fight: its own combat code takes it to the attacker)
                }
                _swarmChaseUntil.Remove(b);
                if (d > SwarmLeashHomeRadius)
                    _swarmReturning.Add(b);   // chase over: all the way home
            }
            if (d > SwarmLeashRadius)
                _swarmReturning.Add(b);
            else if (d <= SwarmLeashHomeRadius)
                _swarmReturning.Remove(b);
            if (!_swarmReturning.Contains(b))
                return false;
            if (!SwarmGuard(b, now))
            {   // (no posts: straight for the Queen)
                Helpers.ObjectState q = new Helpers.ObjectState { positionX = _swarmNest._state.positionX, positionY = _swarmNest._state.positionY };
                b.ApplyNavigationApproach(q, EolHqPatrol.MakeLineClearPendingSeek(b, () => q), new BotNavigateOptions
                {
                    RequeueMinMs = 1500,
                    PathStartIndex = 0,
                    Priority = PathPriority.Combat,
                });
            }
            return true;
        }

        /// <summary>
        /// A bug on its way to its post that hasn't made 32 px of progress in 8 s is stuck (a choke, a green-physics
        /// gap): it is moved to a free post. True = moved this poll.
        /// </summary>
        bool SwarmRescueIfStuck(Bots.Bot b, Helpers.ObjectState post, int now)
        {
            if (Helpers.distanceTo(b._state, post) <= 48)
            {
                _swarmStuck.Remove(b);
                return false;
            }
            StuckSample s;
            if (!_swarmStuck.TryGetValue(b, out s)
                || Helpers.distanceTo(b._state.positionX, b._state.positionY, s.X, s.Y) >= 32)
            {
                _swarmStuck[b] = new StuckSample { X = b._state.positionX, Y = b._state.positionY, Since = now };
                return false;
            }
            if (now - s.Since < 8000)
                return false;
            _swarmStuck.Remove(b);
            Helpers.ObjectState spot = FreeSwarmPostSpot(b);
            if (spot == null)
                return false;
            WarpBot(b, spot);
            try { b.Navigation?.Clear(); } catch { }
            Log.write(TLog.Normal, "[Eol] swarm bug {0} stuck at {1},{2} - back to a guard post.", b._id, s.X / 16, s.Y / 16);
            return true;
        }

        /// <summary>An open spot at a guard post no other bug holds (the bug takes it as its post).</summary>
        Helpers.ObjectState FreeSwarmPostSpot(Bots.Bot b)
        {
            List<Helpers.ObjectState> posts = SwarmPosts();
            if (posts == null || posts.Count == 0)
                return null;
            HashSet<int> taken = new HashSet<int>();
            foreach (KeyValuePair<Bots.Bot, int> kv in _swarmPostOf)
                if (kv.Key != b && kv.Key != null && !kv.Key.IsDead && !kv.Key.bCondemned)
                    taken.Add(kv.Value);
            List<int> free = new List<int>();
            for (int i = 0; i < posts.Count; i++)
                if (!taken.Contains(i))
                    free.Add(i);
            int idx = free.Count > 0 ? free[_arena._rand.Next(free.Count)] : _arena._rand.Next(posts.Count);
            _swarmPostOf[b] = idx;
            _swarmPostUntil.Remove(b);   // (its look-round time starts when it is there)
            Helpers.ObjectState p = posts[idx];
            return findOpenWarpBot(_arena, p.positionX, p.positionY, 40, 20) ?? new Helpers.ObjectState { positionX = p.positionX, positionY = p.positionY };
        }

        /// <summary>The nest is not a machine: nobody can use it or take it over.</summary>
        public bool IsSwarmNest(Vehicle v)
        {
            if (v == null) return false;
            foreach (SwarmRun r in SwarmRuns()) if (r.Nest != null && r.Nest == v) return true;
            return false;
        }

        /// <summary>Player.Control (reprogramming items): never on the swarm's nest.</summary>
        [Scripts.Event("Player.Control")]
        public bool playerControl(InfServer.Game.Player player, ItemInfo.ControlItem item, UInt16 targetVehicle, short posX, short posY)
        {
            foreach (SwarmRun r in SwarmRuns())
                if (r.Nest != null && r.Nest._id == targetVehicle)
                {
                    if (player != null)
                        player.sendMessage(-1, "You can't take over the " + r.Title + " - kill it.");
                    return false;
                }
            return true;
        }

        /// <summary>Mod *swarm: bring the swarm up now (or report where it is).</summary>
        public string TryModStartSwarm()
        {
            // (both swarms: whichever is down comes up now)
            List<string> up = new List<string>();
            int started = 0;
            foreach (SwarmRun r in SwarmRuns())
            {
                if (r.Active)
                    up.Add(r.Title + " at " + (r.Nest != null && r.Nest._state != null ? Helpers.posToLetterCoord(r.Nest._state.positionX, r.Nest._state.positionY) : "?"));
                else
                { r.NextTick = NonZeroTick(Environment.TickCount); started++; }
            }
            return started > 0 ? null : "Already up: " + string.Join(", ", up.ToArray()) + ".";
        }
    }
}
