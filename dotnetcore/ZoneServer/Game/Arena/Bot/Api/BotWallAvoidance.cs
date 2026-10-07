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

        public static bool IsImpassable(Bot bot, short rawX, short rawY)
        {
            if (bot?._arena == null || bot._type == null)
                return true;

            LvlInfo level = bot._arena._server._assets.Level;
            if (level == null)
                return true;

            try
            {
                return level.isTileBlocked(rawX, rawY, bot._type);
            }
            catch (ArgumentOutOfRangeException)
            {
                return true;
            }
        }

        public static bool ProbeAheadClear(Bot bot, int lookAheadTiles = DefaultLookAheadTiles)
        {
            if (bot == null)
                return false;

            if (bot._movement.bCollision)
                return false;

            float yawRad = bot._state.yaw * (float)(Math.PI / 120.0);
            float forwardX = (float)Math.Sin(yawRad);
            float forwardY = (float)Math.Cos(yawRad);

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
                float yawRad = bot._state.yaw * (float)(Math.PI / 120.0);
                float bodyForwardX = (float)Math.Sin(yawRad);
                float bodyForwardY = (float)Math.Cos(yawRad);
                float bodyRightX = bodyForwardY;
                float bodyRightY = -bodyForwardX;

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

            float w = movement.bCollision ? CollisionRepulsionWeight : RepulsionWeight;
            return steer + new Vector3(repulseX * w, repulseY * w, 0);
        }

        public static Helpers.ObjectState? FindEscapeWaypoint(Bot bot, int minRadiusRaw = 96, int maxRadiusRaw = 320)
        {
            if (bot?._arena == null)
                return null;

            short originX = bot._state.positionX;
            short originY = bot._state.positionY;

            for (int radius = maxRadiusRaw; radius >= minRadiusRaw; radius -= 32)
            {
                for (int i = 0; i < 12; i++)
                {
                    double angle = i * (Math.PI * 2.0 / 12.0);
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
                        blocked = level.isTileBlocked(sampleX, sampleY, type);
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

                    float strength = 1.0f / distSq;
                    repulseX += offX * strength;
                    repulseY += offY * strength;
                }
            }

            float yawRad = state.yaw * (float)(Math.PI / 120.0);
            float fwdX = (float)Math.Sin(yawRad);
            float fwdY = (float)Math.Cos(yawRad);

            for (int step = 1; step <= DefaultLookAheadTiles; step++)
            {
                int dist = step * TileSizeRaw;
                short px = (short)(state.positionX + fwdX * dist);
                short py = (short)(state.positionY + fwdY * dist * 0.7f);

                try
                {
                    if (level.isTileBlocked(px, py, type))
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
