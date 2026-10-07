using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>30-point walkable patrol loop around team HQ — bots pick random loop points and path between them.</summary>
    internal static class EolBasePatrolUtil
    {
        /// <summary>Maximum distance (raw map units) a defender may roam from HQ.</summary>
        public const int LeashRaw = 2800;

        /// <summary>Preferred ring radius for the shared HQ loop (outside typical base walls).</summary>
        public const int LoopRadiusRaw = 1700;

        /// <summary>Target number of waypoints around the HQ.</summary>
        public const int LoopPointCount = 24;

        /// <summary>New patrol legs should be at least this far from the bot's current tile.</summary>
        public const int MinPatrolLegSpacingRaw = 900;

        /// <summary>Scatter patrol annulus used by Captain / BasicDefense / aliens (real sector runs).</summary>
        public const int DefenseScatterMinRadiusRaw = 1200;
        public const int DefenseScatterMaxRadiusRaw = 2500;

        /// <summary>Hard peer push radius (raw) so patrol bots do not stack / bob.</summary>
        public const int PeerSeparationRaw = 520;

        /// <summary>Minimum forward thrust while patrolling — prevents rotate-only when facing the wrong way.</summary>
        public const float PatrolMinForward = 0.68f;

        /// <summary>Patrol path refresh interval (ms) — unused while following; kept for retreat helpers.</summary>
        public const int PatrolPathRequeueMs = 4000;

        /// <summary>Force a new patrol leg if the bot barely moves for this long (ms).</summary>
        public const int PatrolStuckTimeoutMs = 3200;

        /// <summary>Raw movement threshold to count as progress while patrolling.</summary>
        public const int PatrolStuckMoveThresholdRaw = 72;

        /// <summary>Raw arrival distance for completing a patrol leg.</summary>
        public const int PatrolArrivalRaw = 220;

        /// <summary>Brief pause after arriving before picking the next loop point.</summary>
        public const int PatrolDwellMs = 400;

        /// <summary>If we travel this far without closing on the target, treat as orbiting and re-pick.</summary>
        public const int PatrolOrbitMoveRaw = 520;
        public const int PatrolOrbitTimeoutMs = 4500;

        /// <summary>World-unit max distance for SteerForSeparation among defenders.</summary>
        public const float PatrolSeparationWorld = 6.5f;

        /// <summary>Legacy aliases used by retreat / radius helpers.</summary>
        public const int MinPatrolRadiusRaw = 1000;
        public const int MaxPatrolRadiusRaw = 2500;

        struct LoopCacheKey
        {
            public short HqTileX;
            public short HqTileY;
        }

        class LoopCacheEntry
        {
            public List<Helpers.ObjectState> Points;
            public int BuiltTick;
        }

        static readonly Dictionary<long, LoopCacheEntry> _loopCache = new Dictionary<long, LoopCacheEntry>();
        const int LoopCacheTtlMs = 120000;

        /// <summary>Bumped when loop build rules change so caches rebuild immediately.</summary>
        const int LoopBuildVersion = 4;

        static long MakeLoopKey(short hqX, short hqY)
        {
            short tx = (short)(hqX / 16);
            short ty = (short)(hqY / 16);
            return ((long)LoopBuildVersion << 48) | ((long)(ushort)tx << 32) | (ushort)ty;
        }

        public static bool IsBeyondLeash(Helpers.ObjectState bot, Helpers.ObjectState hq, int leashRaw)
        {
            if (bot == null || hq == null)
                return false;
            return Helpers.distanceTo(bot, hq) > leashRaw;
        }

        public static bool HasArrivedAtPatrolPoint(Helpers.ObjectState bot, Helpers.ObjectState target, int arrivalRaw = PatrolArrivalRaw)
        {
            if (bot == null || target == null)
                return false;
            return Helpers.distanceTo(bot, target) <= arrivalRaw;
        }

        /// <summary>Path start index — skip node 0 when it is the bot's current tile.</summary>
        public static int InitialPathTarget(int pathCount)
        {
            if (pathCount <= 0)
                return 0;
            return pathCount > 1 ? 1 : 0;
        }

        /// <summary>True when a path request is in flight.</summary>
        public static bool IsPathPending(int tickLastPath)
        {
            return tickLastPath == int.MaxValue;
        }

        /// <summary>True while a usable path list exists.</summary>
        public static bool HasActivePath(int pathCount, int pathTarget)
        {
            return pathCount > 0 && pathTarget < pathCount;
        }

        /// <summary>Build or reuse a ~30-point walkable ring around the HQ.</summary>
        public static List<Helpers.ObjectState> GetOrBuildPatrolLoop(Arena arena, short hqX, short hqY)
        {
            if (arena == null)
                return null;

            long key = MakeLoopKey(hqX, hqY);
            int now = Environment.TickCount;
            LoopCacheEntry entry;
            if (_loopCache.TryGetValue(key, out entry)
                && entry != null
                && entry.Points != null
                && entry.Points.Count > 0
                && now - entry.BuiltTick < LoopCacheTtlMs)
            {
                return entry.Points;
            }

            List<Helpers.ObjectState> points = BuildPatrolLoop(arena, hqX, hqY);
            _loopCache[key] = new LoopCacheEntry { Points = points, BuiltTick = now };
            return points;
        }

        static List<Helpers.ObjectState> BuildPatrolLoop(Arena arena, short hqX, short hqY)
        {
            var points = new List<Helpers.ObjectState>(LoopPointCount);
            Helpers.ObjectState hq = new Helpers.ObjectState { positionX = hqX, positionY = hqY };
            // Prefer true ring radii; fall inward only if needed so points stay outside the HQ pad.
            int[] radii = { LoopRadiusRaw + 140, LoopRadiusRaw, LoopRadiusRaw - 120, MinPatrolRadiusRaw + 160, MinPatrolRadiusRaw };

            for (int i = 0; i < LoopPointCount; i++)
            {
                double angle = i * (2.0 * Math.PI / LoopPointCount);
                Helpers.ObjectState snapped = null;
                for (int r = 0; r < radii.Length && snapped == null; r++)
                {
                    short px = (short)Math.Round(hqX + Math.Cos(angle) * radii[r]);
                    short py = (short)Math.Round(hqY + Math.Sin(angle) * radii[r]);
                    Helpers.ObjectState candidate = TrySnapWalkable(arena, px, py);
                    if (candidate == null)
                        continue;

                    double fromHq = Helpers.distanceTo(candidate, hq);
                    // Reject snaps that collapsed into the inner HQ pad — those cause orbit/spin near spawn.
                    if (fromHq < MinPatrolRadiusRaw)
                        continue;
                    if (fromHq > LeashRaw + 48)
                        continue;

                    snapped = candidate;
                }

                if (snapped == null)
                    continue;

                // Avoid stacking duplicates from snap collapsing nearby angles.
                bool tooClose = false;
                for (int p = 0; p < points.Count; p++)
                {
                    long dx = (long)snapped.positionX - points[p].positionX;
                    long dy = (long)snapped.positionY - points[p].positionY;
                    if (dx * dx + dy * dy < (long)140 * 140)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose)
                    points.Add(snapped);
            }

            return points;
        }

        /// <summary>Pick a random loop point far enough from the bot (and preferably not the last target).</summary>
        public static Helpers.ObjectState TryPickRandomLoopPoint(
            Arena arena, short hqX, short hqY, short botX, short botY,
            Random rng, short excludeX, short excludeY)
        {
            List<Helpers.ObjectState> loop = GetOrBuildPatrolLoop(arena, hqX, hqY);
            if (loop == null || loop.Count == 0)
                return TryGetLeashReturnPoint(arena, hqX, hqY, botX, botY, LeashRaw);

            if (rng == null)
                rng = arena._rand ?? new Random(Environment.TickCount);

            long minSpacingSq = (long)MinPatrolLegSpacingRaw * MinPatrolLegSpacingRaw;
            Helpers.ObjectState best = null;
            long bestDistSq = -1;

            int attempts = Math.Max(8, loop.Count * 2);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                Helpers.ObjectState pick = loop[rng.Next(loop.Count)];
                if (pick == null)
                    continue;
                if (loop.Count > 1 && pick.positionX == excludeX && pick.positionY == excludeY)
                    continue;

                long dx = (long)pick.positionX - botX;
                long dy = (long)pick.positionY - botY;
                long distSq = dx * dx + dy * dy;
                if (distSq < minSpacingSq)
                    continue;

                return pick;
            }

            // Fallback: farthest loop point from the bot.
            for (int i = 0; i < loop.Count; i++)
            {
                Helpers.ObjectState pick = loop[i];
                if (pick == null)
                    continue;
                if (loop.Count > 1 && pick.positionX == excludeX && pick.positionY == excludeY)
                    continue;

                long dx = (long)pick.positionX - botX;
                long dy = (long)pick.positionY - botY;
                long distSq = dx * dx + dy * dy;
                if (distSq > bestDistSq)
                {
                    bestDistSq = distSq;
                    best = pick;
                }
            }

            return best ?? TryGetLeashReturnPoint(arena, hqX, hqY, botX, botY, LeashRaw);
        }

        /// <summary>Pull an out-of-leash bot back toward the patrol ring.</summary>
        public static Helpers.ObjectState TryGetLeashReturnPoint(Arena arena, short hqX, short hqY, short botX, short botY, int leashRaw)
        {
            if (arena == null)
                return null;

            double dx = botX - hqX;
            double dy = botY - hqY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1.0)
                return null;

            double scale = (leashRaw * 0.82) / len;
            short px = (short)Math.Round(hqX + dx * scale);
            short py = (short)Math.Round(hqY + dy * scale);
            return TrySnapWalkable(arena, px, py);
        }

        /// <summary>
        /// Pick a walkable point in an annulus around an anchor (used by AlienHorde crash-ship patrol).
        /// </summary>
        public static Helpers.ObjectState TryPickScatterPatrolPoint(
            Arena arena, short hqX, short hqY, short botX, short botY, int botId,
            int minRadiusRaw, int maxRadiusRaw, int minSpacingFromBotRaw, int leashRaw = LeashRaw)
        {
            if (arena == null)
                return null;

            Random rng = arena._rand ?? new Random(Environment.TickCount ^ botId);
            int minR = Math.Max(64, Math.Min(minRadiusRaw, maxRadiusRaw));
            int maxR = Math.Max(minR + 32, maxRadiusRaw);
            if (maxR > leashRaw)
                maxR = Math.Max(minR + 16, leashRaw);
            long minSpacingSq = (long)Math.Max(48, minSpacingFromBotRaw) * Math.Max(48, minSpacingFromBotRaw);
            Helpers.ObjectState hq = new Helpers.ObjectState { positionX = hqX, positionY = hqY };
            Helpers.ObjectState best = null;
            long bestDistSq = -1;

            for (int attempt = 0; attempt < 28; attempt++)
            {
                double angle = rng.NextDouble() * (2.0 * Math.PI);
                // Bias by botId so peers start on different arcs without locking to a fixed ring.
                angle += ((botId * 0.37) % (2.0 * Math.PI));
                int radius = minR + rng.Next(Math.Max(1, maxR - minR + 1));
                short px = (short)Math.Round(hqX + Math.Cos(angle) * radius);
                short py = (short)Math.Round(hqY + Math.Sin(angle) * radius);
                Helpers.ObjectState snapped = TrySnapWalkable(arena, px, py);
                if (snapped == null)
                    continue;
                if (Helpers.distanceTo(snapped, hq) > leashRaw)
                    continue;

                long dx = (long)snapped.positionX - botX;
                long dy = (long)snapped.positionY - botY;
                long distSq = dx * dx + dy * dy;
                if (distSq < minSpacingSq)
                    continue;

                return snapped;
            }

            // Fallback: farthest of a few radial samples (or leash return).
            for (int i = 0; i < 12; i++)
            {
                double angle = i * (2.0 * Math.PI / 12.0) + (botId % 12) * 0.08;
                int radius = (minR + maxR) / 2;
                short px = (short)Math.Round(hqX + Math.Cos(angle) * radius);
                short py = (short)Math.Round(hqY + Math.Sin(angle) * radius);
                Helpers.ObjectState snapped = TrySnapWalkable(arena, px, py);
                if (snapped == null)
                    continue;
                if (Helpers.distanceTo(snapped, hq) > leashRaw)
                    continue;

                long dx = (long)snapped.positionX - botX;
                long dy = (long)snapped.positionY - botY;
                long distSq = dx * dx + dy * dy;
                if (distSq > bestDistSq)
                {
                    bestDistSq = distSq;
                    best = snapped;
                }
            }

            return best ?? TryGetLeashReturnPoint(arena, hqX, hqY, botX, botY, leashRaw);
        }

        /// <summary>Retreat target on the defensive ring, not on top of the anchor.</summary>
        public static Helpers.ObjectState TryGetRetreatOrbitPoint(Arena arena, short hqX, short hqY, short botX, short botY, int orbitRadiusRaw, int leashRaw = LeashRaw, int minPatrolRadiusRaw = MinPatrolRadiusRaw, int minLegSpacingRaw = MinPatrolLegSpacingRaw)
        {
            if (arena == null)
                return null;

            Helpers.ObjectState loopPoint = TryPickRandomLoopPoint(
                arena, hqX, hqY, botX, botY, arena._rand, short.MinValue, short.MinValue);
            if (loopPoint != null)
                return loopPoint;

            double dx = botX - hqX;
            double dy = botY - hqY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1.0)
                return null;

            double scale = Math.Max(minPatrolRadiusRaw, orbitRadiusRaw) / len;
            short px = (short)Math.Round(hqX + dx * scale);
            short py = (short)Math.Round(hqY + dy * scale);
            Helpers.ObjectState snapped = TrySnapWalkable(arena, px, py);
            if (snapped != null)
                return snapped;

            return TryGetLeashReturnPoint(arena, hqX, hqY, botX, botY, leashRaw);
        }

        public static bool PatrolProgressStalled(int now, int tickLastProgress, short progressX, short progressY, short curX, short curY)
        {
            if (tickLastProgress == 0 || now - tickLastProgress < PatrolStuckTimeoutMs)
                return false;

            long dx = (long)curX - progressX;
            long dy = (long)curY - progressY;
            long thresh = PatrolStuckMoveThresholdRaw;
            return dx * dx + dy * dy < thresh * thresh;
        }

        /// <summary>
        /// True when the bot is circling without closing on its destination (classic HQ donut).
        /// </summary>
        public static bool IsOrbitingDestination(
            int now, int tickLegStart, int legStartDistRaw,
            short legStartX, short legStartY,
            short botX, short botY, short destX, short destY)
        {
            if (tickLegStart == 0 || now - tickLegStart < PatrolOrbitTimeoutMs)
                return false;
            if (legStartDistRaw < PatrolArrivalRaw)
                return false;

            long curDx = (long)botX - destX;
            long curDy = (long)botY - destY;
            long curDistSq = curDx * curDx + curDy * curDy;
            long startDistSq = (long)legStartDistRaw * legStartDistRaw;
            // Still roughly as far from the destination as when the leg started.
            if (curDistSq < (startDistSq * 49) / 100) // closed >30%
                return false;

            long movedDx = (long)botX - legStartX;
            long movedDy = (long)botY - legStartY;
            long movedSq = movedDx * movedDx + movedDy * movedDy;
            long orbitSq = (long)PatrolOrbitMoveRaw * PatrolOrbitMoveRaw;
            return movedSq >= orbitSq;
        }

        /// <summary>Pick a far scatter destination outside the HQ pad for independent patrol legs.</summary>
        public static Helpers.ObjectState TryPickDefensePatrolPoint(
            Arena arena, short hqX, short hqY, short botX, short botY, int botId,
            Random rng, short excludeX, short excludeY)
        {
            Helpers.ObjectState pick = TryPickScatterPatrolPoint(
                arena, hqX, hqY, botX, botY, botId,
                DefenseScatterMinRadiusRaw, DefenseScatterMaxRadiusRaw,
                MinPatrolLegSpacingRaw, LeashRaw);
            if (pick != null && (pick.positionX != excludeX || pick.positionY != excludeY))
                return pick;

            return TryPickRandomLoopPoint(arena, hqX, hqY, botX, botY, rng, excludeX, excludeY);
        }

        /// <summary>
        /// Direct analog drive toward a patrol point with hard peer push.
        /// Does not use flocking Seek (which was rotating in place near HQ walls).
        /// </summary>
        public static void ApplyDefensePatrolDrive(Bot bot, Helpers.ObjectState dest, Arena arena, Team team)
        {
            if (bot == null || dest == null || arena == null || bot._state == null)
                return;

            double fx = dest.positionX - bot._state.positionX;
            double fy = dest.positionY - bot._state.positionY;

            double px = 0;
            double py = 0;
            int sep = PeerSeparationRaw;
            long sepSq = (long)sep * sep;

            List<Vehicle> peers = arena.getVehiclesInRange(
                bot._state.positionX, bot._state.positionY, sep + 64,
                delegate (Vehicle v)
                {
                    return v != null && v != bot && !v.IsDead
                        && (v is Captain || v is BasicDefense || v is AlienHorde || v is Engineer)
                        && (team == null || v._team == team);
                });

            if (peers != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    Vehicle peer = peers[i];
                    if (peer == null || peer._state == null)
                        continue;

                    long dx = (long)bot._state.positionX - peer._state.positionX;
                    long dy = (long)bot._state.positionY - peer._state.positionY;
                    long d2 = dx * dx + dy * dy;
                    if (d2 <= 0 || d2 >= sepSq)
                        continue;

                    double d = Math.Sqrt(d2);
                    double w = (sep - d) / sep;
                    // Strong push when overlapping — peel packs into real patrol lanes.
                    w = w * w * 3.8;
                    px += (dx / d) * w;
                    py += (dy / d) * w;
                }
            }

            // Peer push is strong enough to divert around clusters without cancelling the leg.
            double mx = fx + px * (sep * 1.35);
            double my = fy + py * (sep * 1.35);
            if (mx * mx + my * my < 4.0)
                return;

            double yawRad = Math.Atan2(mx, my);
            float desiredYaw = -(((float)(yawRad * (180.0 / Math.PI)) / 1.5f) - 120);
            double headingError = Helpers.calculateDifferenceInAngles(bot._state.yaw, desiredYaw);
            double headingRad = headingError * (Math.PI / 120.0);

            float forward = BotDrive.ClampAxis((float)Math.Cos(headingRad));
            if (forward < PatrolMinForward)
                forward = PatrolMinForward;
            float strafe = BotDrive.ClampAxis((float)(Math.Sin(headingRad) * 0.85f));
            float rotate = BotDrive.ClampAxis((float)(headingError / 40.0));

            if (bot.MovementMode == BotMovementMode.Api)
            {
                bot.SetDrive(forward, strafe, rotate);
                bot.SetAim(0, false);
            }
            else if (bot._movement is SteeringController steering)
            {
                BotWallAvoidance.AdjustDrive(bot, ref forward, ref strafe, ref rotate);
                BotDrive.SetDrive(steering, forward, strafe, rotate);
                steering.bSkipAim = false;
            }
        }

        // --- Legacy helpers kept so existing ctor orbit fields still compile if referenced ---

        public const int PreferredOrbitStepAdvance = 1;
        public const int OrbitSteps = LoopPointCount;

        public static int GetPatrolOrbitDirection(int botId)
        {
            return ((botId & 1) == 0) ? 1 : -1;
        }

        public static int GetPatrolOrbitRadius(int botId, int minRadiusRaw, int maxRadiusRaw)
        {
            int span = Math.Max(96, maxRadiusRaw - minRadiusRaw);
            return minRadiusRaw + ((botId * 53 + (botId >> 2) * 17) % span);
        }

        public static int GetPatrolOrbitStartStep(int botId)
        {
            return ((botId * 5) + (botId / 3)) % Math.Max(1, LoopPointCount);
        }

        public static int AdvanceOrbitStep(int step, int direction)
        {
            int n = Math.Max(1, LoopPointCount);
            return (step + direction + n * 8) % n;
        }

        static bool InBounds(Arena arena, short px, short py)
        {
            if (px < 0 || py < 0)
                return false;
            return (px / 16) < arena._levelWidth && (py / 16) < arena._levelHeight;
        }

        static Helpers.ObjectState TrySnapWalkable(Arena arena, short px, short py)
        {
            if (InBounds(arena, px, py) && !arena.getTile(px, py).Blocked)
                return new Helpers.ObjectState { positionX = px, positionY = py };

            for (int ring = 1; ring <= 10; ring++)
            {
                for (int ox = -ring; ox <= ring; ox++)
                {
                    for (int oy = -ring; oy <= ring; oy++)
                    {
                        if (Math.Abs(ox) != ring && Math.Abs(oy) != ring)
                            continue;

                        short tx = (short)(px + ox * 16);
                        short ty = (short)(py + oy * 16);
                        if (InBounds(arena, tx, ty) && !arena.getTile(tx, ty).Blocked)
                            return new Helpers.ObjectState { positionX = tx, positionY = ty };
                    }
                }
            }

            return null;
        }
    }
}
