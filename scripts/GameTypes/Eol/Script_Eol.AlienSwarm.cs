using System;
using System.Collections.Generic;

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
        public Helpers.ObjectState SwarmHome { get; private set; }
        public const int SwarmDefenders = 12;   // (2026-11-17, owner: "have the alien base team 12 instead"; was 10)
        public const int SwarmRewardCash = 31200, SwarmRewardExp = 6500,   /* (2026-11-17, owner; was 95k / 12k) */ SwarmRewardPoints = EolBotHqDestroyPointsPerPlayer;   // (2026-11-16f, owner: was 90k / 13k)
        const int SwarmReturnMs = 5 * 60 * 1000;
        const int SwarmFirstDelayMs = 60 * 1000;      // first nest a minute after the match is running
        const int SwarmOpeningSpawnMs = 1200;         // the first 10 come quickly
        const int SwarmRefillMs = 12000;              // then a replacement every 12 s
        const int SwarmDeployTimeoutMs = 25000;
        /// <summary>The bug cave, in tiles: where bugs appear and the portal they walk into.</summary>
        const short SwarmCaveTileX = 1639, SwarmCaveTileY = 418;
        /// <summary>(2026-11-16e) the zone owner's floor hole in the cave (9 x 9 tiles, x 1701-1709, y 412-420).</summary>
        const short SwarmCavePortalTileX = 1705, SwarmCavePortalTileY = 416;
        const int SwarmCaveSpawnRadius = 160;
        /// <summary>Where the nest comes up, in tiles, in turn (the owner's spots: A3, then the south-west).</summary>
        static readonly short[][] SwarmSitesTiles = { new short[] { 76, 178 }, new short[] { 55, 419 } };
        /// <summary>(2026-11-17, owner) the A3 site (index 0) is one of these, picked at random each time.</summary>
        static readonly short[][] SwarmA3SpotsTiles = { new short[] { 96, 175 }, new short[] { 54, 156 }, new short[] { 17, 131 }, new short[] { 33, 213 } };
        /// <summary>(2026-11-17, owner) the A6 site (index 1) is one of these, picked at random each time.</summary>
        static readonly short[][] SwarmA6SpotsTiles = { new short[] { 21, 389 }, new short[] { 54, 375 }, new short[] { 52, 414 }, new short[] { 95, 416 } };
        /// <summary>The A6 area (tiles) the swarm and the bases share: x 0-119, y 355-440 (round the four A6 spots).</summary>
        const short SwarmA6MinTX = 0, SwarmA6MaxTX = 119, SwarmA6MinTY = 355, SwarmA6MaxTY = 440;

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
                if (v != null && v._type != null && v._type.Id == _hqVehId && !v.IsDead && !v.bCondemned && v._state != null
                    && InSwarmA6(v._state.positionX, v._state.positionY))
                    return true;
            return false;
        }

        /// <summary>(2026-11-17, owner: "no human bases spawn in a6 if bugs are there") true = this spot is in A6 and the
        /// Queen is up in A6 - no base may go up there.</summary>
        public bool SwarmHoldsA6(short x, short y)
        {
            return _swarmActive && _swarmNest != null && !_swarmNest.IsDead && _swarmNest._state != null
                && InSwarmA6(_swarmNest._state.positionX, _swarmNest._state.positionY) && InSwarmA6(x, y);
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

        public Team botTeamSwarm;
        Vehicle _swarmNest;
        bool _swarmActive;
        int _swarmNextTick, _swarmNextBugTick, _swarmSiteIndex, _tickSwarmPoll;
        readonly Dictionary<Bots.Bot, int> _swarmDeploying = new Dictionary<Bots.Bot, int>();
        List<Helpers.ObjectState> _caveSpots;

        public bool IsSwarmTeam(Team t) { return t != null && t == botTeamSwarm; }

        /// <summary>The swarm and the alien packs are all bugs: allies.</summary>
        public bool IsBugTeam(Team t) { return IsAlienBotTeam(t) || IsSwarmTeam(t); }

        static short TileRaw(short t) { return (short)(t * 16 + 8); }

        void CreateSwarmTeam()
        {
            botTeamSwarm = CreateAndRegisterBotTeam("Alien Swarm Bots");
        }

        void PollAlienSwarm(int now)
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

        bool _swarmOpening;
        int _swarmNestGoneTick;

        bool SwarmHasChief()
        {
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && b._team == botTeamSwarm && !b.IsDead && b._type != null && b._type.Id == SwarmChiefVehId)
                    return true;
            return false;
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
            for (int i = 0; i < SwarmSitesTiles.Length && site == null; i++)
            {
                int idx = (_swarmSiteIndex + i) % SwarmSitesTiles.Length;
                // (owner: "if a base is there the bugs can only spawn on the A3 site, until that base is destroyed")
                if (idx == 1 && LiveHqInSwarmA6())
                    continue;
                short[] tile = idx == 0 ? SwarmA3SpotsTiles[_arena._rand.Next(SwarmA3SpotsTiles.Length)]
                    : idx == 1 ? SwarmA6SpotsTiles[_arena._rand.Next(SwarmA6SpotsTiles.Length)] : SwarmSitesTiles[idx];
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
            _arena.sendArenaMessage("&An Alien Queen has appeared! (" + Helpers.posToLetterCoord(site.positionX, site.positionY) + ")",
                _config.flag.victoryWarningBong);
            Log.write(TLog.Normal, "[Eol] Alien swarm nest up at {0},{1}.", site.positionX, site.positionY);
        }

        /// <summary>The moving Queen as a bot (null: her vehicle isn't a Car in this zone - the standing one is used).</summary>
        Vehicle SpawnSwarmQueen(Helpers.ObjectState site)
        {
            if (!(_arena._server._assets.getVehicleByID(SwarmQueenBotVehId) is VehInfo.Car))
            {
                Log.write(TLog.Warning, "[Eol] Alien Queen bot vehicle {0} missing / not a Car - the standing Queen is used.", SwarmQueenBotVehId);
                return null;
            }
            AlienHorde q = _arena.newBot(typeof(AlienHorde), SwarmQueenBotVehId, botTeamSwarm, null, site, this, null) as AlienHorde;
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
            if (dead == null || dead != _swarmNest)
                return false;
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
                    p.sendMessage(0, string.Format("&Alien Queen - Your team killed the Alien Queen! Cash={0} Experience={1} Points={2}",
                        cash, exp, points));
                }
                foreach (Team t in _arena.Teams)
                    if (t != winners)
                        t.sendArenaMessage("&Alien Queen - The Alien swarm has been defeated by " + winners._name + "!");
                Log.write(TLog.Normal, "[Eol] Alien Queen killed by {0} ({1} players paid).", winners._name, winners.ActivePlayerCount);
            }
            else
                _arena.sendArenaMessage("&The Alien swarm has burrowed away.", _config.flag.periodicBong);

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
            Log.write(TLog.Normal, "[Eol] Alien swarm over ({0}); back in 5 min.", winners != null ? winners._name : "no winner");
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
                _caveSpots = BuildStraightSpots(1617 * 16 + 8, 1715 * 16 + 8, 394 * 16 + 8, 437 * 16 + 8,
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
            if (b == null || !_swarmDeploying.TryGetValue(b, out t0))
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

        List<Helpers.ObjectState> _swarmPosts;
        Vehicle _swarmPostsFor;
        readonly Dictionary<Bots.Bot, int> _swarmPostOf = new Dictionary<Bots.Bot, int>();
        readonly Dictionary<Bots.Bot, int> _swarmPostUntil = new Dictionary<Bots.Bot, int>();

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
                    if (open && WideLineClear(nx, ny, x, y) && InPlayArea(x, y, 0))
                        _swarmPosts.Add(new Helpers.ObjectState { positionX = x, positionY = y });
                }
            Log.write(TLog.Normal, "[Eol] Alien Queen guard posts: {0}", _swarmPosts.Count);
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
            if (b == null || !IsSwarmTeam(b._team))
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

        readonly Dictionary<Bots.Bot, Helpers.ObjectState> _swarmLoiterAt = new Dictionary<Bots.Bot, Helpers.ObjectState>();
        readonly Dictionary<Bots.Bot, int> _swarmLoiterNext = new Dictionary<Bots.Bot, int>();

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
        int _tickSwarmRally;

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
        readonly Dictionary<Bots.Bot, int> _swarmChaseUntil = new Dictionary<Bots.Bot, int>();
        /// <summary>The choke the owner reported (tiles, A6): a bug found in it is moved straight back to a post.</summary>
        static readonly short[][] SwarmChokeTiles = { new short[] { 145, 410 } };
        const int SwarmChokeRadius = 10 * 16;

        readonly HashSet<Bots.Bot> _swarmReturning = new HashSet<Bots.Bot>();
        class StuckSample { public short X, Y; public int Since; }
        readonly Dictionary<Bots.Bot, StuckSample> _swarmStuck = new Dictionary<Bots.Bot, StuckSample>();

        /// <summary>
        /// First in a swarm bug's poll after its deploy: true = it is out past the leash (or in the choke), so it was
        /// sent back to its guard post this poll and must not chase anything.
        /// </summary>
        public bool SwarmLeash(Bots.Bot b, int now)
        {
            if (b == null || !IsSwarmTeam(b._team) || _swarmNest == null || _swarmNest.IsDead || b.IsDead)
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
            return v != null && _swarmNest != null && v == _swarmNest;
        }

        /// <summary>Player.Control (reprogramming items): never on the swarm's nest.</summary>
        [Scripts.Event("Player.Control")]
        public bool playerControl(InfServer.Game.Player player, ItemInfo.ControlItem item, UInt16 targetVehicle, short posX, short posY)
        {
            if (_swarmNest != null && _swarmNest._id == targetVehicle)
            {
                if (player != null)
                    player.sendMessage(-1, "You can't take over the Alien Queen - kill her.");
                return false;
            }
            return true;
        }

        /// <summary>Mod *swarm: bring the swarm up now (or report where it is).</summary>
        public string TryModStartSwarm()
        {
            if (_swarmActive)
                return "The swarm is already up at " + (_swarmNest != null ? Helpers.posToLetterCoord(_swarmNest._state.positionX, _swarmNest._state.positionY) : "?") + ".";
            _swarmNextTick = NonZeroTick(Environment.TickCount);
            return null;
        }
    }
}
