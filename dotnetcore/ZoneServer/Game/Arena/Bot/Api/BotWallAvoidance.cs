using System;
using Axiom.Math;
using Assets;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Bots
{
    /// <summary>
    /// Tile-based wall avoidance for combat and patrol drive inputs.
    /// </summary>
    public static class BotWallAvoidance
    {
        private const int TileSizeRaw = 16;
        private const int DefaultLookAheadTiles = 2;
        private const int RepulsionRadiusTiles = 3;
        private const float RepulsionWeight = 0.85f;
        private const float CollisionRepulsionWeight = 1.35f;
        private const float RepulsionScale = 0.01f;
        private const float MaxRepulsion = 0.7f;

        /// <summary>A ground-level wall at this raw point (overhead / light-blue physics is not a wall). Off-map = wall.</summary>
        static bool WallAt(LvlInfo level, int rawX, int rawY)
        {
            int tx = rawX / 16, ty = rawY / 16;
            if (rawX < 0 || rawY < 0 || tx >= level.Width || ty >= level.Height)
                return true;
            return BotCombat.IsGroundWall(level, level.Tiles[ty * level.Width + tx]);
        }

        public static bool IsImpassable(Bot bot, short rawX, short rawY)
        {
            if (bot?._arena == null || bot._type == null)
                return true;

            LvlInfo level = bot._arena._server._assets.Level;
            if (level == null)
                return true;

            try
            {
                // any physics is a wall for a bot, whatever it rides (local 2026-11-14 - boards glided through red);
                // built walls / turrets near the bot count too, so the escape scan sees them
                return WallAt(level, rawX, rawY) || (bot._movement != null && bot._movement.IsSolidObjectAt(rawX, rawY));
            }
            catch (ArgumentOutOfRangeException)
            {
                return true;
            }
        }

        /// <summary>
        /// Free run in raw px from (x, y) along Infantry yaw (0 = north, 1 unit = 1.5°), sampled one tile
        /// apart up to <paramref name="dist"/>: the distance of the last open sample before a wall
        /// (<paramref name="dist"/> when nothing is in the way). Map y is squished 7/10.
        /// </summary>
        public static int ProbeOpen(Bot bot, int x, int y, double yaw, int dist)
        {
            double r = yaw * 1.5 * Math.PI / 180.0;
            double ux = Math.Sin(r);
            double uy = -0.7 * Math.Cos(r);
            double n = Math.Sqrt(ux * ux + uy * uy);
            if (n < 1e-6)
                return dist;
            ux /= n;
            uy /= n;
            for (int s = TileSizeRaw; s <= dist; s += TileSizeRaw)
            {
                if (IsImpassable(bot, (short)(x + (int)Math.Round(ux * s)), (short)(y + (int)Math.Round(uy * s))))
                    return s - TileSizeRaw;
            }
            return dist;
        }

        /// <summary>Wrap a yaw into [0, 240).</summary>
        public static double WrapYaw(double yaw)
        {
            yaw %= 240.0;
            return yaw < 0 ? yaw + 240.0 : yaw;
        }

        /// <summary>
        /// <see cref="ProbeOpen"/> without a Bot (for the movement controller): free run in raw px along a yaw.
        /// </summary>
        public static int ProbeOpenRaw(Arena arena, VehInfo.Car type, int x, int y, double yaw, int dist)
        {
            LvlInfo level = arena?._server?._assets?.Level;
            if (level == null || type == null)
                return 0;
            double r = yaw * 1.5 * Math.PI / 180.0;
            double ux = Math.Sin(r), uy = -0.7 * Math.Cos(r);
            double n = Math.Sqrt(ux * ux + uy * uy);
            if (n < 1e-6)
                return dist;
            ux /= n; uy /= n;
            for (int s = TileSizeRaw; s <= dist; s += TileSizeRaw)
            {
                bool blocked;
                try { blocked = WallAt(level, x + (int)Math.Round(ux * s), y + (int)Math.Round(uy * s)); }
                catch (ArgumentOutOfRangeException) { blocked = true; }
                if (blocked)
                    return s - TileSizeRaw;
            }
            return dist;
        }

        public static bool ProbeAheadClear(Bot bot, int lookAheadTiles = DefaultLookAheadTiles)
        {
            if (bot == null)
                return false;

            if (bot._movement.bCollision)
                return false;

            // Screen coords: yaw 0 = north = -y (local fix 2026-11-13; was +y, which probed BEHIND for north/south).
            float yawRad = bot._state.yaw * (float)(Math.PI / 120.0);
            float forwardX = (float)Math.Sin(yawRad);
            float forwardY = -(float)Math.Cos(yawRad);

            for (int step = 1; step <= lookAheadTiles; step++)
            {
                int dist = step * TileSizeRaw;
                short probeX = (short)(bot._state.positionX + forwardX * dist);
                short probeY = (short)(bot._state.positionY + forwardY * dist * 0.7f);

                if (IsImpassable(bot, probeX, probeY))
                    return false;

                // Diagonal probes at ±35°
                float cosA = 0.82f;
                float sinA = 0.57f;
                if (IsImpassable(bot,
                        (short)(bot._state.positionX + (forwardX * cosA - forwardY * sinA) * dist),
                        (short)(bot._state.positionY + (forwardX * sinA + forwardY * cosA) * dist * 0.7f)))
                    return false;

                if (IsImpassable(bot,
                        (short)(bot._state.positionX + (forwardX * cosA + forwardY * sinA) * dist),
                        (short)(bot._state.positionY + (-forwardX * sinA + forwardY * cosA) * dist * 0.7f)))
                    return false;
            }

            return true;
        }

        public static void AdjustDrive(Bot bot, ref float forward, ref float strafe, ref float rotate)
        {
            if (bot == null)
                return;

            float repulseX = 0f;
            float repulseY = 0f;
            AccumulateRepulsion(bot, ref repulseX, ref repulseY, out int blockedAhead);

            float weight = bot._movement.bCollision ? CollisionRepulsionWeight : RepulsionWeight;
            if (Math.Abs(repulseX) > 0.001f || Math.Abs(repulseY) > 0.001f)
            {
                // Screen coords (y down): forward = (sin, -cos), right = (cos, sin). The old +cos forward
                // mirrored north/south, so repulsion from a north or south wall drove the bot INTO it.
                float yawRad = bot._state.yaw * (float)(Math.PI / 120.0);
                float bodyForwardX = (float)Math.Sin(yawRad);
                float bodyForwardY = -(float)Math.Cos(yawRad);
                float bodyRightX = -bodyForwardY;
                float bodyRightY = bodyForwardX;

                float avoidForward = repulseX * bodyForwardX + repulseY * bodyForwardY;
                float avoidStrafe = repulseX * bodyRightX + repulseY * bodyRightY;

                forward = BotDrive.ClampAxis(forward + avoidForward * weight);
                strafe = BotDrive.ClampAxis(strafe + avoidStrafe * weight);
            }

            if (blockedAhead > 0 || bot._movement.bCollision)
            {
                forward *= blockedAhead >= 2 ? 0.15f : 0.45f;
                if (Math.Abs(strafe) < 0.2f && (repulseX != 0 || repulseY != 0))
                {
                    float sign = Math.Sign(repulseX != 0 ? repulseX : repulseY);
                    if (sign == 0)
                        sign = ((bot._id & 1) == 0) ? 1f : -1f;
                    strafe = BotDrive.ClampAxis(strafe + sign * 0.55f);
                }
            }
        }

        public static Vector3 BlendRepulsion(MovementController movement, Vector3 steer)
        {
            if (movement == null || steer.LengthSquared < 0.001f)
                return steer;

            float repulseX = 0f, repulseY = 0f;
            SampleRepulsionAt(movement.MovementArena, movement.MovementState, movement.MovementCarType, ref repulseX, ref repulseY, out _);

            if (Math.Abs(repulseX) < 0.001f && Math.Abs(repulseY) < 0.001f)
                return steer;

            // Repulsion is already a bounded nudge (see SampleRepulsionAt); scale it to the steer so it
            // bends the route away from walls without overpowering it (it used to be ~10x the steer).
            float w = movement.bCollision ? CollisionRepulsionWeight : RepulsionWeight;
            float steerLen = Math.Max(0.5f, steer.Length);
            return steer + new Vector3(repulseX * w * steerLen, repulseY * w * steerLen, 0);
        }

        public static Helpers.ObjectState? FindEscapeWaypoint(Bot bot, int minRadiusRaw = 96, int maxRadiusRaw = 320)
        {
            if (bot?._arena == null)
                return null;

            short originX = bot._state.positionX;
            short originY = bot._state.positionY;

            // Try directions AWAY from the nearby walls first (it used to always try east first), with a
            // little per-attempt variety so stuck bots don't all leave the same way.
            float rx = 0f, ry = 0f;
            SampleRepulsionAt(bot._arena, bot._state, bot._type, ref rx, ref ry, out _);
            double away = (Math.Abs(rx) + Math.Abs(ry)) > 0.001f
                ? Math.Atan2(ry, rx)
                : ((Environment.TickCount / 97 + bot._id * 37) % 12) * (Math.PI * 2.0 / 12.0);
            double[] order = { 0, 0.52, -0.52, 1.05, -1.05, 1.57, -1.57, 2.09, -2.09, 2.62, -2.62, Math.PI };
            bool flip = ((Environment.TickCount >> 9) & 1) == 0;

            for (int radius = maxRadiusRaw; radius >= minRadiusRaw; radius -= 32)
            {
                for (int i = 0; i < 12; i++)
                {
                    double angle = away + (flip ? -order[i] : order[i]);
                    short x = (short)(originX + Math.Cos(angle) * radius);
                    short y = (short)(originY + Math.Sin(angle) * radius);

                    if (IsImpassable(bot, x, y))
                        continue;

                    if (!HasClearanceAround(bot, x, y, 1))
                        continue;

                    return new Helpers.ObjectState { positionX = x, positionY = y };
                }
            }

            return null;
        }

        private static bool HasClearanceAround(Bot bot, short rawX, short rawY, int tileRadius)
        {
            int cx = rawX / TileSizeRaw;
            int cy = rawY / TileSizeRaw;

            for (int dy = -tileRadius; dy <= tileRadius; dy++)
            {
                for (int dx = -tileRadius; dx <= tileRadius; dx++)
                {
                    if (IsImpassable(bot, (short)((cx + dx) * TileSizeRaw + 8), (short)((cy + dy) * TileSizeRaw + 8)))
                        return false;
                }
            }

            return true;
        }

        private static void AccumulateRepulsion(Bot bot, ref float repulseX, ref float repulseY, out int blockedAhead)
        {
            blockedAhead = 0;
            SampleRepulsionAt(bot._arena, bot._state, bot._type, ref repulseX, ref repulseY, out blockedAhead);
        }

        private static void SampleRepulsionAt(
            Arena arena,
            Helpers.ObjectState state,
            VehInfo.Car type,
            ref float repulseX,
            ref float repulseY,
            out int blockedAhead)
        {
            blockedAhead = 0;
            if (arena == null || state == null || type == null)
                return;

            LvlInfo level = arena._server._assets.Level;
            int centerTileX = state.positionX / TileSizeRaw;
            int centerTileY = state.positionY / TileSizeRaw;

            float myX = state.positionX / 100.0f;
            float myY = state.positionY / 100.0f;

            for (int dy = -RepulsionRadiusTiles; dy <= RepulsionRadiusTiles; dy++)
            {
                for (int dx = -RepulsionRadiusTiles; dx <= RepulsionRadiusTiles; dx++)
                {
                    int tx = centerTileX + dx;
                    int ty = centerTileY + dy;
                    if (tx < 0 || ty < 0 || tx >= level.Width || ty >= level.Height)
                        continue;

                    short sampleX = (short)(tx * TileSizeRaw + 8);
                    short sampleY = (short)(ty * TileSizeRaw + 8);

                    bool blocked;
                    try
                    {
                        blocked = WallAt(level, sampleX, sampleY);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        blocked = true;
                    }

                    if (!blocked)
                        continue;

                    float tileCenterX = (tx + 0.5f) * TileSizeRaw / 100.0f;
                    float tileCenterY = (ty + 0.5f) * TileSizeRaw / 100.0f;
                    float offX = myX - tileCenterX;
                    float offY = myY - tileCenterY;
                    float distSq = offX * offX + offY * offY;
                    if (distSq < 0.01f)
                        distSq = 0.01f;

                    // 1/d^2 falloff (was 1/d): walls right beside the bot matter, walls 3 tiles off barely do.
                    float strength = 1.0f / (distSq * (float)Math.Sqrt(distSq));
                    repulseX += offX * strength;
                    repulseY += offY * strength;
                }
            }

            // Bound the total push: ~0.7 when touching a wall, ~0.1 three tiles away (was unbounded, ~10+).
            float mag = (float)Math.Sqrt(repulseX * repulseX + repulseY * repulseY);
            if (mag > 0.0001f)
            {
                float scaled = Math.Min(MaxRepulsion, mag * RepulsionScale);
                repulseX = repulseX / mag * scaled;
                repulseY = repulseY / mag * scaled;
            }

            float yawRad = state.yaw * (float)(Math.PI / 120.0);
            float fwdX = (float)Math.Sin(yawRad);
            float fwdY = -(float)Math.Cos(yawRad);

            for (int step = 1; step <= DefaultLookAheadTiles; step++)
            {
                int dist = step * TileSizeRaw;
                short px = (short)(state.positionX + fwdX * dist);
                short py = (short)(state.positionY + fwdY * dist * 0.7f);

                try
                {
                    if (WallAt(level, px, py))
                        blockedAhead++;
                }
                catch (ArgumentOutOfRangeException)
                {
                    blockedAhead++;
                }
            }
        }
    }
}
