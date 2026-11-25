using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-26, owner: "make another team of 4. These guys have a chief engy and a gunner for both vehicles [the Harrier
    /// GV-s and the Harrier GV-s A2]. the harrier themselves should be able to fly over green physics and they should have
    /// really good aim ... put them on low life thugs" / "people cannot join them" / "if their vehicle dies, they are on foot
    /// (similar to how the vehicle team works)" / "the gunners can be a command class. Like Squad Lead and sarge" / "but 2
    /// chief engys" / "if they die, they spawn in the dropship, and when they come out chief and the command both get
    /// somewhere and then the chief engy makes the harrier, and they both get in")
    ///
    /// Low Life Thugs (botTeam6, bot-only): two crews of 2.
    ///   - Crew 1: Chief Engineer + Squad Leader, Harrier GV-s (Kamenev SCV mk2 + LAW Launcher).
    ///   - Crew 2: Chief Engineer + Sergeant, Harrier GV-s A2 (Flak Shell Launcher + A2 Grenade Launcher).
    /// A crew starts in the dropship; once both are out (they land together), the Chief Engineer builds the Harrier on the
    /// spot (HarrierBuildMs) and both get in: one Harrier bot under the Chief's name, the gunner's guns, "ultra" aim (the
    /// assassins' - the best there is). A bot IS its vehicle here, so the gunner rides inside it.
    /// Harrier destroyed: both get out on the spot (same names) and fight on foot; each one killed comes back through the
    /// dropship, and when both have been round and are out again they build a new Harrier.
    /// The Harriers' hover height (LowZ 17) is over green physics (16): bots fly over it like the boards do (BotCombat).
    /// </summary>
    public partial class Script_Eol
    {
        public const bool HarrierCrewEnabled = true;
        const ushort VehHarrierGvs = 121, VehHarrierGvsA2 = 126;
        const int HarrierBuildMs = 4000;
        const int HarrierCrewSize = 4;
        const int ItmKamenevScv = 3070, ItmLawLauncher = 3071, ItmFlakShell = 3074, ItmA2GrenadeLauncher = 3075;

        sealed class HarrierCrew
        {
            public ushort HarrierVeh;
            public int GunnerCls;
            public int CloseGun, FarGun;
            public RoamingAttacker Harrier, Chief, Gunner;
            public string GunnerAlias;
            public bool ChiefCycled, GunnerCycled;   // been through the dropship since they got out of the Harrier
            public Helpers.ObjectState BuildSpot;
            public int BuildDoneTick, NextSpawnTick;
            public string Sector;   // (2026-11-26) the sector it spawned in - its Harrier drives that sector's route
        }

        // ---------- (2026-11-26, owner) spawn points and routes ----------
        /// <summary>The owner's spawn points (tiles) per sector: the crew lands at one of them, in an open sector.</summary>
        static readonly Dictionary<string, short[][]> HarrierSpawnTiles = new Dictionary<string, short[][]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Sector A", new[] { new short[] { 10, 326 }, new short[] { 564, 9 }, new short[] { 737, 401 } } },
            { "Sector B", new[] { new short[] { 8, 640 }, new short[] { 619, 749 } } },
            { "Sector C", new[] { new short[] { 652, 746 }, new short[] { 1075, 853 }, new short[] { 1369, 392 }, new short[] { 1369, 238 }, new short[] { 671, 62 } } },
        };

        sealed class RouteWalk { public HarrierRoute Route; public int Node, Prev = -1, LastProgressTick; public short LastX, LastY; public readonly Queue<int> Recent = new Queue<int>(); }
        readonly Dictionary<Bots.Bot, RouteWalk> _routeWalk = new Dictionary<Bots.Bot, RouteWalk>();

        static HarrierRoute RouteOf(string sector)
        {
            foreach (HarrierRoute r in HarrierRoutes) if (string.Equals(r.Sector, sector, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        bool SectorOpen(string sector) { return _eol != null && _eol._activeSectors != null && sector != null && _eol._activeSectors.Contains(sector); }

        static Helpers.ObjectState RouteNodeRaw(HarrierRoute r, int i)
        {
            return new Helpers.ObjectState { positionX = (short)(r.X[i] * 16 + 8), positionY = (short)(r.Y[i] * 16 + 8) };
        }

        /// <summary>
        /// A Low Life Thugs Harrier out of a fight: where it drives next on its sector's route (the owner's drawn lines -
        /// Script_Eol.HarrierRoutes.cs). At each waypoint it carries on along the line (the way it's facing, a bit of chance
        /// at forks), never straight back unless it's a dead end. Null = not a Harrier / no open route.
        /// </summary>
        public Helpers.ObjectState HarrierRouteGoal(Bots.Bot b, int now)
        {
            if (b == null || b._state == null || b._type == null || !IsHarrierVeh(b._type.Id) || !IsHarrierCrewTeam(b._team))
                return null;
            HarrierCrew crew = _harrierCrews.Find(c => ReferenceEquals(c.Harrier, b));
            RouteWalk w;
            _routeWalk.TryGetValue(b, out w);
            string sector = crew != null && SectorOpen(crew.Sector) ? crew.Sector : null;
            if (sector == null)
            {   // its sector closed (or none yet): the open sector's route nearest to it
                double bestD = double.MaxValue;
                foreach (HarrierRoute r in HarrierRoutes)
                {
                    if (!SectorOpen(r.Sector)) continue;
                    double d = Helpers.distanceTo(b._state, RouteNodeRaw(r, NearestRouteNode(r, b._state)));
                    if (d < bestD) { bestD = d; sector = r.Sector; }
                }
                if (sector == null)
                    return null;
                if (crew != null) crew.Sector = sector;
            }
            HarrierRoute route = RouteOf(sector);
            if (route == null || route.X.Length == 0)
                return null;
            if (w == null || w.Route != route)
            {
                w = new RouteWalk { Route = route, Node = NearestRouteNode(route, b._state), LastProgressTick = now, LastX = b._state.positionX, LastY = b._state.positionY };
                _routeWalk[b] = w;
            }
            Helpers.ObjectState goal = RouteNodeRaw(route, w.Node);
            // at the waypoint (or stuck 6 s on the way): on to the next one
            if (Helpers.distanceTo(b._state.positionX, b._state.positionY, w.LastX, w.LastY) > 48)
            {
                w.LastX = b._state.positionX; w.LastY = b._state.positionY; w.LastProgressTick = now;
            }
            bool stuck = now - w.LastProgressTick > 6000;
            if (Helpers.distanceTo(b._state, goal) <= 72 || stuck)
            {
                int next = NextRouteNode(route, w, b);
                w.Recent.Enqueue(w.Node);
                while (w.Recent.Count > 8) w.Recent.Dequeue();
                w.Prev = w.Node;
                w.Node = next;
                w.LastProgressTick = now;
                goal = RouteNodeRaw(route, w.Node);
            }
            return goal;
        }

        /// <summary>A straight, body-wide run to <paramref name="to"/> for this bot's vehicle: walls it can't fly over block it,
        /// green physics doesn't for a Harrier (like a player driving one).</summary>
        public bool WideLineClearFor(Bots.Bot b, Helpers.ObjectState to)
        {
            if (b == null || b._state == null || to == null)
                return false;
            LvlInfo level = _arena._server._assets.Level;
            double x0 = b._state.positionX, y0 = b._state.positionY, dx = to.positionX - x0, dy = to.positionY - y0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1)
                return true;
            double ux = dx / len, uy = dy / len, nx = -uy * 24, ny = ux * 24;
            for (double d = 0; d <= len; d += 8)
                for (int k = -1; k <= 1; k++)
                {
                    int x = (int)(x0 + ux * d + nx * k), y = (int)(y0 + uy * d + ny * k);
                    if (x < 0 || y < 0)
                        return false;
                    if (InfServer.Bots.BotCombat.IsWallFor(level, _arena.getTile(x, y), b._type))
                        return false;
                }
            return true;
        }

        static int NearestRouteNode(HarrierRoute r, Helpers.ObjectState at)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < r.X.Length; i++)
            {
                double dx = r.X[i] * 16 + 8 - at.positionX, dy = r.Y[i] * 16 + 8 - at.positionY, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        int NextRouteNode(HarrierRoute r, RouteWalk w, Bots.Bot b)
        {
            short[] nb = r.Next[w.Node];
            if (nb == null || nb.Length == 0)
                return NearestRouteNode(r, b._state);
            double hx = 0, hy = 0;   // the way it is going (from the last waypoint)
            if (w.Prev >= 0) { hx = r.X[w.Node] - r.X[w.Prev]; hy = r.Y[w.Node] - r.Y[w.Prev]; }
            double hl = Math.Sqrt(hx * hx + hy * hy);
            int best = -1; double bs = double.MinValue;
            Random rnd = _arena._rand;
            foreach (short n in nb)
            {
                if (w.Recent.Contains(n) || n == w.Prev)
                    continue;
                double dx = r.X[n] - r.X[w.Node], dy = r.Y[n] - r.Y[w.Node], dl = Math.Sqrt(dx * dx + dy * dy);
                double score = (hl > 0 && dl > 0 ? (hx * dx + hy * dy) / (hl * dl) : 0) + rnd.NextDouble() * 0.6;
                if (score > bs) { bs = score; best = n; }
            }
            if (best >= 0)
                return best;
            // a dead end: turn round
            w.Recent.Clear();
            return w.Prev >= 0 ? w.Prev : nb[rnd.Next(nb.Length)];
        }

        readonly List<HarrierCrew> _harrierCrews = new List<HarrierCrew>
        {
            new HarrierCrew { HarrierVeh = VehHarrierGvs, GunnerCls = ClsSquadLeader, CloseGun = ItmKamenevScv, FarGun = ItmLawLauncher },
            new HarrierCrew { HarrierVeh = VehHarrierGvsA2, GunnerCls = ClsSergeant, CloseGun = ItmFlakShell, FarGun = ItmA2GrenadeLauncher },
        };

        sealed class PendingHarrierFoot { public HarrierCrew Crew; public short X, Y; public string ChiefAlias; public int Bounty; }
        readonly List<PendingHarrierFoot> _pendingHarrierFoot = new List<PendingHarrierFoot>();
        int _tickHarrierPoll;

        /// <summary>The Harrier crews' team (Low Life Thugs).</summary>
        public bool IsHarrierCrewTeam(Team t) { return t != null && HarrierCrewEnabled && t == botTeam6; }

        /// <summary>A Harrier (with its crew inside).</summary>
        public static bool IsHarrierVeh(int vehId) { return vehId == VehHarrierGvs || vehId == VehHarrierGvsA2; }

        static bool BotGone(Bots.Bot b)
        {
            return b == null || (b.bCondemned && !b.bDropshipRespawnPending) || (b.IsDead && !b.bDropshipRespawnPending);
        }

        void PollHarrierCrew(int now)
        {
            if (!HarrierCrewEnabled || botTeam6 == null || _arena == null || !_arena._bGameRunning || !EolHasArenaPresence())
                return;
            PollPendingHarrierFoot(now);
            if (now - _tickHarrierPoll < 500)
                return;
            _tickHarrierPoll = now;
            if (roamBots != null && !roamBots.ContainsKey(botTeam6))
                roamBots.Add(botTeam6, 0);

            foreach (HarrierCrew c in _harrierCrews)
            {
                if (c.Harrier != null)
                {
                    if (!BotGone(c.Harrier))
                        continue;
                    c.Harrier = null;   // (gone without the death hook - despawned, sector change): the crew starts again
                }
                if (_pendingHarrierFoot.Exists(p => p.Crew == c))
                    continue;
                if (BotGone(c.Chief)) c.Chief = null;
                if (BotGone(c.Gunner)) c.Gunner = null;
                // been round the dropship (dead and coming back, or walking out)
                if (c.Chief != null && (c.Chief.IsDead || IsDeploying(c.Chief))) c.ChiefCycled = true;
                if (c.Gunner != null && (c.Gunner.IsDead || IsDeploying(c.Gunner))) c.GunnerCycled = true;

                // nobody left: both in the dropship again
                if (c.Chief == null && c.Gunner == null)
                {
                    if (now - c.NextSpawnTick >= 0)
                        SpawnHarrierCrewInDropship(c, now);
                    continue;
                }
                // one of them gone for good (respawn refused): the other one comes back too, so they start together
                if (c.Chief == null || c.Gunner == null)
                {
                    RoamingAttacker left = c.Chief ?? c.Gunner;
                    if (!left.IsDead && !IsDeploying(left))
                    {   // a new partner in the dropship
                        if (now - c.NextSpawnTick >= 0)
                            SpawnHarrierCrewInDropship(c, now);
                    }
                    continue;
                }
                // both out, both been round: build the Harrier and get in
                bool ready = c.ChiefCycled && c.GunnerCycled && !c.Chief.IsDead && !c.Gunner.IsDead
                    && !IsDeploying(c.Chief) && !IsDeploying(c.Gunner);
                if (!ready)
                {
                    c.BuildDoneTick = 0;
                    continue;
                }
                if (c.BuildDoneTick == 0)
                {
                    c.BuildDoneTick = NonZeroTick(now + HarrierBuildMs);
                    Log.write(TLog.Normal, "[Eol] Low Life Thugs: {0} builds a {1}.", c.Chief._alias, HarrierName(c));
                    continue;
                }
                if (now - c.BuildDoneTick >= 0)
                    MountHarrier(c, now);
            }
        }

        string HarrierName(HarrierCrew c)
        {
            VehInfo v = _arena._server._assets.getVehicleByID(c.HarrierVeh);
            return v != null ? v.Name : "Harrier";
        }

        /// <summary>The crew (or the missing one) in the dropship; they land together (HarrierCrewLandingFor).</summary>
        void SpawnHarrierCrewInDropship(HarrierCrew c, int now)
        {
            if (DropshipBusy)
                return;
            c.BuildSpot = null;
            c.BuildDoneTick = 0;
            if (c.Chief == null)
            {
                c.Chief = NewHarrierCrewBot(ClsChiefEngineer, null, null, now);
                c.ChiefCycled = true;
            }
            if (c.Gunner == null)
            {
                c.Gunner = NewHarrierCrewBot(c.GunnerCls, null, null, now);
                c.GunnerCycled = true;
            }
            c.NextSpawnTick = now + 5000;   // (one try every 5 s while the dropship is busy)
        }

        /// <summary>A crew member on foot: in the dropship (at == null) or on the spot. Ace, its class's guns and armour,
        /// back through the dropship when killed.</summary>
        RoamingAttacker NewHarrierCrewBot(int cls, Helpers.ObjectState at, string alias, int now)
        {
            if (!CanSpawnBotOnTeam(botTeam6))
                return null;
            bool viaDropship = at == null;
            if (viaDropship)
                at = PickDropshipSpawn() ?? PickEliteSpawnPoint();
            if (at == null)
                return null;
            _arena.NextBotAlias = alias;
            RoamingAttacker bot = _arena.newBot(typeof(RoamingAttacker), (ushort)cls, botTeam6, null, at, this, null) as RoamingAttacker;
            _arena.NextBotAlias = null;
            if (bot == null)
                return null;
            bot.SkillModeOverride = "ace";
            ApplyHumanLoadout(bot, cls, botTeam6);
            EnableBotDropshipRespawn(bot, 0);   // (killed: back through the dropship, same bot)
            if (viaDropship && _eol != null && _eol.IsInPioneerDropshipSafeZone(at.positionX, at.positionY))
                BeginDeploy(bot, now);
            else
                bot.DamageImmuneUntilTick = NonZeroTick(now + 800);
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bot);
            if (roamBots != null && roamBots.ContainsKey(botTeam6))
                roamBots[botTeam6]++;
            return bot;
        }

        /// <summary>
        /// Out of the dropship portal: both crew members land at the same spot (the first one out picks it - open ground away
        /// from players), where the Chief Engineer builds their Harrier. Null = not a Harrier crew member.
        /// </summary>
        public Helpers.ObjectState HarrierCrewLandingFor(Bots.Bot b)
        {
            if (b == null || !IsHarrierCrewTeam(b._team))
                return null;
            foreach (HarrierCrew c in _harrierCrews)
            {
                if (!ReferenceEquals(c.Chief, b) && !ReferenceEquals(c.Gunner, b))
                    continue;
                RoamingAttacker other = ReferenceEquals(c.Chief, b) ? c.Gunner : c.Chief;
                // the partner is out already, not fighting: land beside it
                if (other != null && !other.IsDead && !IsDeploying(other) && other._state != null && !other.InLiveCombat)
                {
                    Helpers.ObjectState beside = OpenSpotNear(other._state.positionX, other._state.positionY, 160);
                    if (beside != null)
                        return beside;
                }
                if (c.BuildSpot == null)
                    c.BuildSpot = PickHarrierBuildSpot(c);
                return c.BuildSpot == null ? null : (OpenSpotNear(c.BuildSpot.positionX, c.BuildSpot.positionY, 160) ?? c.BuildSpot);
            }
            return null;
        }

        /// <summary>(2026-11-26, owner) one of the owner's spawn points in an open sector - another sector than the other crew's
        /// if one is open - on open ground with room for a Harrier. None open: open ground away from players.</summary>
        Helpers.ObjectState PickHarrierBuildSpot(HarrierCrew c)
        {
            List<string> open = new List<string>();
            foreach (string sec in HarrierSpawnTiles.Keys)
                if (SectorOpen(sec)) open.Add(sec);
            HarrierCrew other = _harrierCrews.Find(x => x != c);
            if (open.Count > 1 && other != null && other.Sector != null)
                open.Remove(other.Sector);
            while (open.Count > 0)
            {
                string sec = open[_arena._rand.Next(open.Count)];
                open.Remove(sec);
                short[][] spots = HarrierSpawnTiles[sec];
                foreach (int k in new[] { _arena._rand.Next(spots.Length) })
                    for (int j = 0; j < spots.Length; j++)
                    {
                        short[] t = spots[(k + j) % spots.Length];
                        Helpers.ObjectState at = findOpenWarpBot(_arena, (short)(t[0] * 16 + 8), (short)(t[1] * 16 + 8), 200, 56);
                        if (at == null || !_eol.IsInAnyActiveSector(at.positionX, at.positionY))
                            continue;
                        c.Sector = sec;
                        return at;
                    }
            }
            for (int i = 0; i < 8; i++)
            {
                Helpers.ObjectState s = PickEliteSpawnPoint();
                if (s == null)
                    continue;
                Helpers.ObjectState roomy = findOpenWarpBot(_arena, s.positionX, s.positionY, 160, 56);
                if (roomy != null && InPlayArea(roomy.positionX, roomy.positionY, 120))
                    return roomy;
            }
            return PickEliteSpawnPoint();
        }

        /// <summary>The Chief Engineer has built the Harrier: both get in - one Harrier bot under the Chief's name.</summary>
        void MountHarrier(HarrierCrew c, int now)
        {
            RoamingAttacker chief = c.Chief, gunner = c.Gunner;
            Helpers.ObjectState at = findOpenWarpBot(_arena, chief._state.positionX, chief._state.positionY, 200, 56)
                ?? new Helpers.ObjectState { positionX = chief._state.positionX, positionY = chief._state.positionY };
            int bounty = BotBountyOf(chief);
            string chiefAlias = _arena.HandOverBotAlias(chief);
            c.GunnerAlias = _arena.HandOverBotAlias(gunner) ?? c.GunnerAlias;
            EnqueueVehicleDespawn(chief, force: true);
            EnqueueVehicleDespawn(gunner, force: true);
            c.Chief = c.Gunner = null;
            c.ChiefCycled = c.GunnerCycled = false;
            c.BuildDoneTick = 0;
            c.BuildSpot = null;

            _arena.NextBotAlias = chiefAlias;
            RoamingAttacker h = _arena.newBot(typeof(RoamingAttacker), c.HarrierVeh, botTeam6, null, at, this, null) as RoamingAttacker;
            _arena.NextBotAlias = null;
            if (h == null)
            {
                Log.write(TLog.Warning, "[Eol] Low Life Thugs: {0} could not be made - the crew starts again.", HarrierName(c));
                c.NextSpawnTick = now + 5000;
                return;
            }
            h.SkillModeOverride = "ultra";   // (owner: "really good aim" - the assassins' tier, the best there is)
            EquipCloseFar(h, c.CloseGun, c.FarGun);
            h.bRespawn = false;   // (destroyed: the crew gets out - OnHarrierLost)
            h.DamageImmuneUntilTick = NonZeroTick(now + 1500);
            lock (_botBounty) _botBounty[h] = bounty;
            h.DisplayBounty = bounty;
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(h);
            c.Harrier = h;
            Log.write(TLog.Normal, "[Eol] Low Life Thugs: {0} and {1} got in their {2} at {3}.", chiefAlias, c.GunnerAlias,
                HarrierName(c), Helpers.posToLetterCoord(at.positionX, at.positionY));
        }

        /// <summary>
        /// Bot.Death: a Harrier destroyed - its crew gets out on the spot (no kill reward for the vehicle, no death for the
        /// crew). True = it was one.
        /// </summary>
        bool OnHarrierLost(Bots.Bot dead)
        {
            if (dead == null || !IsHarrierCrewTeam(dead._team) || dead._type == null || !IsHarrierVeh(dead._type.Id))
                return false;
            HarrierCrew c = _harrierCrews.Find(x => ReferenceEquals(x.Harrier, dead));
            if (c == null)
                return false;
            c.Harrier = null;
            short x0 = dead._state != null ? dead._state.positionX : (short)0, y0 = dead._state != null ? dead._state.positionY : (short)0;
            _pendingHarrierFoot.Add(new PendingHarrierFoot { Crew = c, X = x0, Y = y0, ChiefAlias = _arena.HandOverBotAlias(dead), Bounty = BotBountyOf(dead) });
            Log.write(TLog.Normal, "[Eol] Low Life Thugs: {0} destroyed - the crew is on foot.", HarrierName(c));
            return true;
        }

        void PollPendingHarrierFoot(int now)
        {
            if (_pendingHarrierFoot.Count == 0)
                return;
            List<PendingHarrierFoot> todo = new List<PendingHarrierFoot>(_pendingHarrierFoot);
            _pendingHarrierFoot.Clear();
            foreach (PendingHarrierFoot f in todo)
            {
                HarrierCrew c = f.Crew;
                Helpers.ObjectState a = findOpenWarpBot(_arena, f.X, f.Y, 64) ?? new Helpers.ObjectState { positionX = f.X, positionY = f.Y };
                Helpers.ObjectState b = findOpenWarpBot(_arena, f.X, f.Y, 96) ?? a;
                c.Chief = NewHarrierCrewBot(ClsChiefEngineer, a, f.ChiefAlias, now);
                c.Gunner = NewHarrierCrewBot(c.GunnerCls, b, c.GunnerAlias, now);
                c.ChiefCycled = c.GunnerCycled = false;   // (they get back in only after the dropship)
                c.BuildDoneTick = 0;
                if (c.Chief != null)
                {
                    lock (_botBounty) _botBounty[c.Chief] = f.Bounty;
                    c.Chief.DisplayBounty = f.Bounty;
                }
                c.NextSpawnTick = now + 5000;
            }
        }
    }
}
