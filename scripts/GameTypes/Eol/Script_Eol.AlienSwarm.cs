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
        public Vehicle SwarmNest { get { return _swarmNest; } }
        public const int SwarmDefenders = 10;
        public const int SwarmRewardCash = 95000, SwarmRewardExp = 12000, SwarmRewardPoints = EolBotHqDestroyPointsPerPlayer;   // (2026-11-16f, owner: was 90k / 13k)
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
        /// <summary>Bug mix: Arachnomorph, Electromorph, Predator, Warrior, Brute - and one Alien Chief.</summary>
        static readonly ushort[] SwarmBugPool = { 149, 153, 154, 155, 155, 433 };
        const ushort SwarmChiefVehId = 200;

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
                if (b != null && b._team == botTeamSwarm && !b.bCondemned && (!b.IsDead || b.bDropshipRespawnPending))
                    live++;
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
                short x = TileRaw(SwarmSitesTiles[idx][0]), y = TileRaw(SwarmSitesTiles[idx][1]);
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
            _swarmNest = _arena.newVehicle(nestInfo, botTeamSwarm, null, site);
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
            ushort vid = needChief ? SwarmChiefVehId : SwarmBugPool[_arena._rand.Next(SwarmBugPool.Length)];
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
                Helpers.ObjectState land = findOpenWarpBot(_arena, _swarmNest._state.positionX, _swarmNest._state.positionY, 220, 28);
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
        /// Posts round the Queen: 16 directions x 3 rings (150 / 260 / 380 px), each on open ground with a body-wide
        /// straight line to her - so no post sits behind a choke or a green-physics gap.
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
            foreach (int r in new[] { 150, 260, 380 })
                for (int k = 0; k < 16; k++)
                {
                    double a = k * Math.PI / 8 + (r == 260 ? Math.PI / 16 : 0);
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
        /// A swarm bug with nothing to fight: go to its own post (one bug a post) and hang round it; every 25-45 s a
        /// different free post - roaming a little round the Queen, never piling into one spot. False = no posts
        /// (the old patrol takes over).
        /// </summary>
        public bool SwarmGuard(Bots.Bot b, int now)
        {
            if (b == null || !IsSwarmTeam(b._team))
                return false;
            List<Helpers.ObjectState> posts = SwarmPosts();
            if (posts == null || posts.Count == 0)
                return false;
            int idx, until;
            if (!_swarmPostOf.TryGetValue(b, out idx) || idx >= posts.Count
                || !_swarmPostUntil.TryGetValue(b, out until) || now - until >= 0)
            {
                HashSet<int> taken = new HashSet<int>();
                foreach (KeyValuePair<Bots.Bot, int> kv in _swarmPostOf)
                    if (kv.Key != b && kv.Key != null && !kv.Key.IsDead && !kv.Key.bCondemned)
                        taken.Add(kv.Value);
                List<int> free = new List<int>();
                for (int i = 0; i < posts.Count; i++)
                    if (!taken.Contains(i))
                        free.Add(i);
                idx = free.Count > 0 ? free[_arena._rand.Next(free.Count)] : _arena._rand.Next(posts.Count);
                _swarmPostOf[b] = idx;
                _swarmPostUntil[b] = now + 25000 + _arena._rand.Next(20000);
            }
            Helpers.ObjectState post = posts[idx];
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
