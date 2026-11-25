using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-16, owner) Human and pirate squad bots come in like players: they spawn inside the Pioneer Station
    /// dropship, walk to the map portal and are teleported to their landing spot (beside their squad, the raid
    /// anchor, or an open spot away from players). No more bots popping in and out of thin air in the field.
    /// Also here: every bot gets 35 % more armour (Vehicle.DamageTakenPercent) and 3 s of spawn protection, and
    /// takes no damage while walking through the (safe) dropship.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool DropshipDeployEnabled = true;
        /// <summary>Centre of the dropship's "MapPortal" (lio portal 1, 80 x 80 at 27008,2864).</summary>
        public static short DropshipPortalX { get { return EolMap.DropshipPortalX; } }
        public static short DropshipPortalY { get { return EolMap.DropshipPortalY; } }
        const int DeployPortalReachRaw = 72;
        /// <summary>Stuck in the dropship this long: sent through anyway.</summary>
        const int DeployTimeoutMs = 30000;
        /// <summary>Bots appear here (the dropship floor south of the portal, where players stand).</summary>
        static short DeploySpawnX { get { return EolMap.DeploySpawnX; } }
        static short DeploySpawnY { get { return EolMap.DeploySpawnY; } }
        const int DeploySpawnRadius = 220;
        public const int BotSpawnProtectMs = 3000;
        /// <summary>(2026-11-16, owner: "50% armor increase bots"; 2026-11-17 "reduce the 50% armor increase ... to 35%. bots are too tanky") damage taken = 1 / 1.35.</summary>
        // (2026-11-17, owner: "lets remove the added armor bonus") then (2026-11-18, owner: "bots are a bit too squishy again,
        // maybe increase armor by 20% ... make sure aliens have no armor increase") - 20 % more armour = 1/1.2 of the damage
        // then (2026-11-18, owner: "up the increase armor we did to 30% from 20%") - 1/1.3 of the damage
        // then (2026-11-18, owner: "if we drop to 0% and then let the armor it self do the defense") - no bonus: the armour
        // pieces do it, stacked realistically now (engine BotUtilityEffects.AccumulateUtilityArmor)
        public const int BotDamageTakenPercent = 100;
        public const int AlienDamageTakenPercent = 100;

        readonly Dictionary<Bots.Bot, int> _deployStartTick = new Dictionary<Bots.Bot, int>();   // (by object: ids are reused)
        readonly HashSet<Bots.Bot> _preparedBots = new HashSet<Bots.Bot>();
        readonly Dictionary<Bots.Bot, int> _deployRetryAt = new Dictionary<Bots.Bot, int>();
        int _tickPrepareBots, _tickPreparePrune;

        public bool IsDeploying(Bots.Bot b)
        {
            return b != null && _deployStartTick.ContainsKey(b);
        }

        /// <summary>
        /// A free spot on the dropship floor (null = none: spawn in the field as before). (2026-11-16d, owner: "laggy,
        /// weird pathing, all one spot") spots are spread all over the floor, each with a clear straight line to the
        /// portal, and never on top of another bot that is still walking - so the bot just walks straight there.
        /// </summary>
        Helpers.ObjectState PickDropshipSpawn()
        {
            if (!DropshipDeployEnabled || _arena == null || _eol == null)
                return null;
            if (_dropshipSpots == null)
                _dropshipSpots = BuildStraightSpots(DropshipSafeBoxMinX, DropshipSafeBoxMaxX, DropshipSafeBoxMinY, DropshipSafeBoxMaxY,
                    DropshipPortalX, DropshipPortalY, 180, 1100, (x, y) => x >= DropshipSafeBoxMinX && x <= DropshipSafeBoxMaxX && y >= DropshipSafeBoxMinY && y <= DropshipSafeBoxMaxY);   // (the BOT dropship: combined level = Pioneer's for both halves)
            return FacingPortal(PickSpreadSpot(_dropshipSpots, _deployStartTick.Keys), DropshipPortalX, DropshipPortalY);
        }

        /// <summary>The spawn faces the portal, so the bot just runs - no turning on the spot first.</summary>
        static Helpers.ObjectState FacingPortal(Helpers.ObjectState at, short tx, short ty)
        {
            if (at != null)
                at.yaw = (byte)((int)Math.Round(Helpers.calculateDegreesBetweenPoints(tx, ty, at.positionX, at.positionY)) % 240);
            return at;
        }

        // the dropship box (EolBoundaries' safe zone)
        static short DropshipSafeBoxMinX { get { return EolMap.BotSafeMinX; } }
        static short DropshipSafeBoxMaxX { get { return EolMap.BotSafeMaxX; } }
        static short DropshipSafeBoxMinY { get { return EolMap.BotSafeMinY; } }
        static short DropshipSafeBoxMaxY { get { return EolMap.BotSafeMaxY; } }
        List<Helpers.ObjectState> _dropshipSpots;

        /// <summary>
        /// Every 32 px across a box: open ground (3 x 3 tiles) with a body-wide clear straight line to the target,
        /// <paramref name="minD"/>..<paramref name="maxD"/> from it.
        /// </summary>
        List<Helpers.ObjectState> BuildStraightSpots(int minX, int maxX, int minY, int maxY, short tx, short ty, int minD, int maxD, Func<short, short, bool> inArea)
        {
            List<Helpers.ObjectState> list = new List<Helpers.ObjectState>();
            for (int y = minY; y <= maxY; y += 32)
                for (int x = minX; x <= maxX; x += 32)
                {
                    short sx = (short)x, sy = (short)y;
                    if (inArea != null && !inArea(sx, sy))
                        continue;
                    double d = Helpers.distanceTo(sx, sy, tx, ty);
                    if (d < minD || d > maxD)
                        continue;
                    bool open = true;
                    for (int oy = -16; oy <= 16 && open; oy += 16)
                        for (int ox = -16; ox <= 16 && open; ox += 16)
                            if (_arena.getTile((short)(sx + ox), (short)(sy + oy)).Blocked)
                                open = false;
                    if (!open || !WideLineClear(sx, sy, tx, ty))
                        continue;
                    list.Add(new Helpers.ObjectState { positionX = sx, positionY = sy });
                }
            Log.write(TLog.Normal, "[Eol] deploy spots toward {0},{1}: {2}", tx, ty, list.Count);
            return list;
        }

        /// <summary>A straight line a body wide (the centre and 12 px either side) with no wall on it.</summary>
        bool WideLineClear(short x0, short y0, short x1, short y1)
        {
            double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1)
                return true;
            double nx = -dy / len * 12, ny = dx / len * 12;
            for (int k = -1; k <= 1; k++)
                if (!EolHqPatrol.TileLineClear(_arena, (short)(x0 + nx * k), (short)(y0 + ny * k), (short)(x1 + nx * k), (short)(y1 + ny * k)))
                    return false;
            return true;
        }

        /// <summary>A random spot from <paramref name="spots"/> at least 70 px from every bot still walking in.</summary>
        Helpers.ObjectState PickSpreadSpot(List<Helpers.ObjectState> spots, IEnumerable<Bots.Bot> walking)
        {
            if (spots == null || spots.Count == 0)
                return null;
            Helpers.ObjectState fallback = null;
            for (int tries = 0; tries < 16; tries++)
            {
                Helpers.ObjectState s = spots[_arena._rand.Next(spots.Count)];
                fallback = fallback ?? s;
                bool clear = true;
                foreach (Bots.Bot b in walking)
                    if (b != null && b._state != null && Helpers.distanceTo(b._state.positionX, b._state.positionY, s.positionX, s.positionY) < 70)
                    {
                        clear = false;
                        break;
                    }
                if (clear)
                    return new Helpers.ObjectState { positionX = s.positionX, positionY = s.positionY };
            }
            return new Helpers.ObjectState { positionX = fallback.positionX, positionY = fallback.positionY };
        }

        /// <summary>
        /// Run straight at a point like a player: turn to face it and hold "forward" - no path search (the spot was
        /// picked with a clear line to it). (2026-11-16e, "weird paths, lost and confused, laggy") this was a seek
        /// steer, which the engine applies ON TOP of whatever keys the bot last held - usually the idle side-to-side
        /// shuffle - so bots wobbled forward and back; and the path was cleared every tick. A drive request replaces
        /// the keys outright: forward at full speed once facing the portal, slower while still turning.
        /// </summary>
        public static void SteerStraight(Bots.Bot b, short tx, short ty)
        {
            if (b == null || b._state == null)
                return;
            double want = Helpers.calculateDegreesBetweenPoints(tx, ty, b._state.positionX, b._state.positionY);
            double diff = Helpers.calculateDifferenceInAngles(b._state.yaw, want);   // (yaw units, 240 a turn)
            // (2026-11-16f, owner: "pirates walk like low FPS") the old steps - full speed under 20 units off, half
            // over - flipped every tick as the heading wobbled round 20, so the bot stutter-stepped. Now: a small
            // dead zone (no twitching), and speed that rises smoothly as it faces the portal.
            double ad = Math.Abs(diff);
            float rotate = ad < 2.5 ? 0f : (float)Math.Max(-1.0, Math.Min(1.0, diff / 14.0));
            float forward = (float)Math.Max(0.3, Math.Min(1.0, 1.0 - (ad - 8) / 50.0));
            b.SetDrive(forward, 0f, rotate);
        }

        /// <summary>Spawn point for a squad bot: the dropship (it then walks to the portal), else <paramref name="field"/>.</summary>
        Helpers.ObjectState SquadSpawnPoint(Func<Helpers.ObjectState> field, out bool viaDropship)
        {
            Helpers.ObjectState ds = PickDropshipSpawn();
            viaDropship = ds != null;
            return ds ?? field();
        }

        /// <summary>The new bot is in the dropship: walk to the portal, untouchable meanwhile.</summary>
        void BeginDeploy(Bots.Bot b, int now)
        {
            if (b == null)
                return;
            _deployStartTick[b] = now;
            b.DamageImmuneUntilTick = NonZeroTick(now + DeployTimeoutMs + 5000);
            try { b.Navigation?.Clear(); } catch { }   // (once: it runs straight at the portal from here)
        }

        /// <summary>
        /// (2026-11-16e, "ultra smooth like real people spawning") at most this many new bots walk through the
        /// dropship at once - a crowd there jostles and, past the engine's full-rate limit (the 10 nearest bots per
        /// player), the rest are sent less often and look choppy. New squad bots wait their turn.
        /// </summary>
        const int DropshipMaxWalking = 8;
        public bool DropshipBusy { get { return _deployStartTick.Count >= DropshipMaxWalking; } }

        static int NonZeroTick(int t) { return t == 0 ? 1 : t; }

        /// <summary>
        /// Called first in the squad poll. True = the bot is still walking to the portal (this poll is done);
        /// false = not deploying (any more) - normal behaviour.
        /// </summary>
        public bool PollDeploying(RoamingAttacker b, int now)
        {
            int t0;
            if (b == null || !_deployStartTick.TryGetValue(b, out t0))
                return false;
            if (b.IsDead || b.bCondemned)
            {
                _deployStartTick.Remove(b);
                return false;
            }
            double d = Helpers.distanceTo(b._state.positionX, b._state.positionY, DropshipPortalX, DropshipPortalY);
            if (d <= DeployPortalReachRaw || now - t0 > DeployTimeoutMs)
            {
                int retry;
                if (_deployRetryAt.TryGetValue(b, out retry) && now - retry < 0)
                {
                    b.HoldPosition();
                    return true;
                }
                Helpers.ObjectState land = PickDeployLanding(b);
                if (land == null && now - t0 > DeployTimeoutMs + 10000)
                    land = AnyOpenFieldSpot();   // (2026-11-16f: never stuck in the dropship) any open ground will do
                if (land == null)
                {   // nowhere to land right now (sector change...): wait at the portal and try again in a second
                    _deployRetryAt[b] = now + 1000;
                    b.HoldPosition();
                    return true;
                }
                _deployRetryAt.Remove(b);
                _deployStartTick.Remove(b);
                WarpBot(b, land);
                b.DamageImmuneUntilTick = NonZeroTick(now + BotSpawnProtectMs);
                return false;
            }
            b.ClearActivePlayerTarget();
            SteerStraight(b, DropshipPortalX, DropshipPortalY);
            return true;
        }

        /// <summary>
        /// Where a bot comes out of the portal: beside a player on its squad, beside its squad leader, at the raid
        /// anchor (pirate raid), else an open spot in the play area away from players and other bots.
        /// </summary>
        Helpers.ObjectState PickDeployLanding(Bots.Bot b)
        {
            Team team = b._team;
            Helpers.ObjectState at;

            // a player on this squad (joined it): come out beside them
            if (team != null && IsJoinableSquadTeam(team))
            {
                foreach (InfServer.Game.Player p in team.ActivePlayers)
                {
                    if (p == null || p.IsDead || p._state == null || !InPlayArea(p._state.positionX, p._state.positionY, 0))
                        continue;
                    at = OpenSpotNear(p._state.positionX, p._state.positionY, 220);
                    if (at != null)
                        return at;
                }
            }

            // (2026-11-26) a squad sent after a team's roamer flags: its staging spot near them
            at = RaidStageFor(b);
            if (at != null)
                return at;

            // the pirate raid: the raid's anchor
            if (IsPirateRaidTeam(team) && RaiderPdbAnchorValid)
            {
                at = FindOpenRaiderSpawnWithPeerGap(RaiderPdbAnchorX, RaiderPdbAnchorY);
                if (at != null && InPlayArea(at.positionX, at.positionY, 0))
                    return at;
            }

            // its squad leader, when it's out of a fight and away from players
            RoamingAttacker leader = EliteSquadLeader(team);
            if (leader != null && leader != b && !IsDeploying(leader) && !leader.InLiveCombat
                && InPlayArea(leader._state.positionX, leader._state.positionY)
                && !PlayerNear(leader._state.positionX, leader._state.positionY, EliteSpawnAwayFromPlayersRaw))
            {
                at = OpenSpotNear(leader._state.positionX, leader._state.positionY, 200);
                if (at != null)
                    return at;
            }

            return PickEliteSpawnPoint();
        }

        /// <summary>Last resort: an open tile anywhere in an open sector (null only if no sector is open at all).</summary>
        Helpers.ObjectState AnyOpenFieldSpot()
        {
            if (_eol == null || _eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return null;
            for (int i = 0; i < 12; i++)
            {
                string sec = _eol._activeSectors[_arena._rand.Next(_eol._activeSectors.Count)];
                short minX, maxX, minY, maxY;
                if (!_eol.TryGetSectorBounds(sec, out minX, out maxX, out minY, out maxY))
                    continue;
                Helpers.ObjectState at = findOpenWarpInAxisAlignedRect(_arena, minX, maxX, minY, maxY, 24);
                if (at != null && _eol.IsInAnyActiveSector(at.positionX, at.positionY))
                    return at;
            }
            return null;
        }

        int _tickStuckSweep;

        /// <summary>
        /// (2026-11-16f, owner: "make sure no bots get stuck in the drop ship") every 2 s: a squad / raid bot standing
        /// in the dropship that is not on its way out (or a swarm bug idle in the bug cave) is sent to the portal.
        /// </summary>
        void SweepStuckSpawnBots(int now)
        {
            if (_arena == null || _eol == null || now - _tickStuckSweep < 2000)
                return;
            _tickStuckSweep = now;
            foreach (Bots.Bot b in _arena.ActiveBots)
            {
                if (b == null || b.IsDead || b.bCondemned || b._state == null || b._team == null)
                    continue;
                short x = b._state.positionX, y = b._state.positionY;
                if (b is RoamingAttacker && (IsRoamingSquadTeam(b._team) || IsRoamBotTeam(b._team))
                    && _eol.IsInPioneerDropshipSafeZone(x, y) && !IsDeploying(b) && ShipSummonHold(b) == null)   // (not one summoned in by its player)
                    BeginDeploy(b, now);
                else if (b is AlienHorde && IsPirateRaidersTeam(b._team) && !SwarmBugDeploying(b)
                    && _eol.IsInPioneerDropshipSafeZone(x, y) && UseSwarmOf(b._team))
                    BeginSwarmDeploy(b, now);   // (2026-11-26: a Pirate Raider standing in the dropship)
                else if (b is AlienHorde && IsSwarmTeam(b._team) && !SwarmBugDeploying(b)
                    && x >= CaveTX(1617) * 16 && x <= CaveTX(1716) * 16 && y >= CaveTY(394) * 16 && y <= CaveTY(438) * 16
                    && UseSwarmOf(b._team))
                    BeginSwarmDeploy(b, now);
            }
        }

        Helpers.ObjectState OpenSpotNear(short x, short y, int radius)
        {
            Helpers.ObjectState at = findOpenWarpBot(_arena, x, y, radius);
            if (at == null || !InPlayArea(at.positionX, at.positionY, 0) || !IsOpenConnectedGround(at.positionX, at.positionY))
                return null;
            return at;
        }

        /// <summary>Teleport a live bot (portal exit, suit teleport): move it and show it to everyone nearby.</summary>
        public void WarpBot(Bots.Bot bot, Helpers.ObjectState at)
        {
            if (bot == null || at == null || bot.IsDead || _arena == null)
                return;
            at = OutsideNoSpawnBuilding(at);   // (2026-11-17) never into a no-spawn building (Script_Eol.NoSpawnBuildings.cs)
            putVehicleAtSpawnLocation(bot, at);
            // (2026-11-16d, owner: "lots of invisible bots fighting") the old teleport sent the body (SC_Vehicles)
            // straight to the players at the landing and marked them as having it - a client that had no player-list
            // entry for the bot yet got a body it can't draw ("hull-only" = invisible), and was never sent it again.
            // Now nothing is pushed by hand: every client is forgotten, and the bot's normal update sends each one in
            // range the player-list entry first (if it lacks it) and then the body, the engine's own safe order.
            bot.ClearClientsAwareOfBot();
            bot.RequestHullReattach(NonZeroTick(Environment.TickCount + 5000));
            try
            {
                bot.Navigation?.Clear();
                bot.HoldPosition();
            }
            catch { /* ignore */ }
            if (bot is RoamingAttacker ra)
            {
                ra.ClearActivePlayerTarget();
                ra._targetPoint = null;
            }
        }

        /// <summary>
        /// Every bot, once, right after it appears: 35 % more armour and 3 s of spawn protection (bots were dying the
        /// moment they spawned). A few times a second; forgotten ids are pruned now and then.
        /// </summary>
        void PrepareNewBots(int now)
        {
            if (_arena == null || now - _tickPrepareBots < 100)
                return;
            _tickPrepareBots = now;
            SweepStuckSpawnBots(now);
            foreach (Bots.Bot b in _arena.ActiveBots)
            {
                if (b == null || b.bCondemned || !_preparedBots.Add(b))
                    continue;
                PreparePirateRoamerArmor(b);   // (2026-11-17) pirate Roamers: player armour of their class
                if (!(b is AlienHorde)) b.DisplayBounty = BotBountyOf(b);   // (2026-11-17) its bounty next to its name
                if (b is AlienHorde)            // (aliens: no armour bonus)
                    b.DamageTakenPercent = AlienDamageTakenPercent;
                else if (!b.PlayerEnergy)       // (bots on player armour get it in ApplyPlayerArmor)
                    b.DamageTakenPercent = BotDamageTakenPercent;
                if (b.DamageImmuneUntilTick == 0 && now - b._tickCreation < BotSpawnProtectMs)
                    b.DamageImmuneUntilTick = NonZeroTick(b._tickCreation + BotSpawnProtectMs);
            }
            if (now - _tickPreparePrune < 10000)
                return;
            _tickPreparePrune = now;
            HashSet<Bots.Bot> live = new HashSet<Bots.Bot>();
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && !b.bCondemned)
                    live.Add(b);
            _preparedBots.RemoveWhere(x => !live.Contains(x));
            List<Bots.Bot> gone = null;
            foreach (Bots.Bot x in _deployStartTick.Keys)
                if (!live.Contains(x))
                    (gone ?? (gone = new List<Bots.Bot>())).Add(x);
            if (gone != null)
                foreach (Bots.Bot x in gone)
                    _deployStartTick.Remove(x);
            List<Bots.Bot> staleRetry = new List<Bots.Bot>();
            foreach (Bots.Bot x in _deployRetryAt.Keys)
                if (!_deployStartTick.ContainsKey(x))
                    staleRetry.Add(x);
            foreach (Bots.Bot x in staleRetry)
                _deployRetryAt.Remove(x);
            // (players who left: their adaptive-skill form and squad-order cooldowns)
            List<InfServer.Game.Player> left = new List<InfServer.Game.Player>();
            foreach (InfServer.Game.Player p in _form.Keys)
                if (p == null || p._arena != _arena)
                    left.Add(p);
            foreach (InfServer.Game.Player p in left)
            {
                _form.Remove(p);
                _squadOrderNext.Remove(p);
            }
        }
    }
}
