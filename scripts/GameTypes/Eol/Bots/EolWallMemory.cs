using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Axiom.Math;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Local planner with memory (local change 2026-11-14) - how a real player finds their way:
    /// every bot looks round before it moves, KNOWS where the walls are (any physics colour - red, purple,
    /// orange, yellow, green - and built walls / turrets), and remembers where it has already been. When its
    /// goal is behind something solid it picks open ground that (a) gets it closer, (b) ideally has a clear
    /// walk to the goal, and (c) is NOT somewhere it has just been - so it never walks up to a wall, turns
    /// away, then turns straight back to the same wall. Boxed in (a building, a corner) it heads for the
    /// longest open run - the doorway - to get out to open space, then carries on.
    /// </summary>
    public static class EolWallMemory
    {
        const int RayStep = 16;
        const int RayMax = 420;
        const int Directions = 24;
        const int WallBackOff = 26;          // stop this far short of a wall it saw
        const int CrumbSpacing = 56;          // a breadcrumb every this many px travelled
        const int CrumbCount = 40;
        const int CrumbLifeMs = 25000;        // remembers where it has been for ~25 s
        const int WaypointLifeMs = 3500;
        const int WaypointArriveRaw = 34;

        sealed class Mem
        {
            public readonly short[] Cx = new short[CrumbCount], Cy = new short[CrumbCount];
            public readonly int[] Ct = new int[CrumbCount];
            public int CNext;
            public short LastCx = short.MinValue, LastCy = short.MinValue;
            // dead ends: spots where it found itself walled in - avoided harder and longer
            public readonly short[] Dx = new short[8], Dy = new short[8];
            public readonly int[] Dt = new int[8];
            public int DNext;
            public bool HasWp;
            public short WpX, WpY;
            public int WpUntil;
            public short GoalX, GoalY;
            public int SeenEscapeTick;
        }

        static readonly ConditionalWeakTable<Bot, Mem> Table = new ConditionalWeakTable<Bot, Mem>();

        static Mem Get(Bot b) { return Table.GetValue(b, _ => new Mem()); }

        /// <summary>Drop breadcrumbs as the bot travels (call every steer tick - cheap).</summary>
        static void Remember(Bot bot, Mem m, int now)
        {
            short x = bot._state.positionX, y = bot._state.positionY;
            if (m.LastCx != short.MinValue)
            {
                int dx = x - m.LastCx, dy = y - m.LastCy;
                if (dx * dx + dy * dy < CrumbSpacing * CrumbSpacing)
                    return;
            }
            m.LastCx = x; m.LastCy = y;
            m.Cx[m.CNext] = x; m.Cy[m.CNext] = y; m.Ct[m.CNext] = now;
            m.CNext = (m.CNext + 1) % CrumbCount;
        }

        /// <summary>Mark the bot's current spot as a dead end (it had to back out of a corner here).</summary>
        public static void NoteDeadEnd(Bot bot)
        {
            if (bot?._state == null)
                return;
            Mem m = Get(bot);
            m.Dx[m.DNext] = bot._state.positionX; m.Dy[m.DNext] = bot._state.positionY; m.Dt[m.DNext] = Environment.TickCount;
            m.DNext = (m.DNext + 1) % m.Dx.Length;
            m.HasWp = false;
        }

        /// <summary>How "already explored" a spot is: recent breadcrumbs and dead ends near it.</summary>
        static double Familiarity(Mem m, int x, int y, int now)
        {
            double f = 0;
            for (int i = 0; i < CrumbCount; i++)
            {
                if (m.Ct[i] == 0 || now - m.Ct[i] > CrumbLifeMs)
                    continue;
                int dx = x - m.Cx[i], dy = y - m.Cy[i];
                int d2 = dx * dx + dy * dy;
                if (d2 < 110 * 110)
                    f += 1.0 - Math.Sqrt(d2) / 110.0;
            }
            for (int i = 0; i < m.Dx.Length; i++)
            {
                if (m.Dt[i] == 0 || now - m.Dt[i] > CrumbLifeMs * 2)
                    continue;
                int dx = x - m.Dx[i], dy = y - m.Dy[i];
                int d2 = dx * dx + dy * dy;
                if (d2 < 200 * 200)
                    f += 4.0 * (1.0 - Math.Sqrt(d2) / 200.0);
            }
            return f;
        }

        /// <summary>Free run along a direction (raw px) before the first thing it can't walk through.</summary>
        static int Ray(Bot bot, short x0, short y0, double ang, int max)
        {
            double ux = Math.Cos(ang), uy = Math.Sin(ang);
            for (int s = RayStep; s <= max; s += RayStep)
            {
                short x = (short)(x0 + ux * s), y = (short)(y0 + uy * s);
                if (BotWallAvoidance.IsImpassable(bot, x, y))
                    return s - RayStep;
            }
            return max;
        }

        /// <summary>
        /// Where to walk next toward <paramref name="dest"/> when the straight walk is blocked. Keeps the
        /// current waypoint until it's reached / stale / no longer clear; otherwise looks all round and picks
        /// the best open spot (closer to the goal, clear on to the goal, not somewhere it has just been).
        /// Null = nothing open at all (stand-off).
        /// </summary>
        public static Helpers.ObjectState NextWaypoint(Bot bot, Helpers.ObjectState dest)
        {
            if (bot?._arena == null || bot._state == null || dest == null)
                return null;
            int now = Environment.TickCount;
            Mem m = Get(bot);
            Remember(bot, m, now);
            short bx = bot._state.positionX, by = bot._state.positionY;

            // it just had to back out of a corner: remember that spot as a dead end
            if (bot.LastWallEscapeTick != 0 && bot.LastWallEscapeTick != m.SeenEscapeTick)
            {
                m.SeenEscapeTick = bot.LastWallEscapeTick;
                NoteDeadEnd(bot);
            }

            // a different goal: re-plan (memory of where it's been stays)
            int gdx = dest.positionX - m.GoalX, gdy = dest.positionY - m.GoalY;
            if (gdx * gdx + gdy * gdy > 160 * 160)
            {
                m.HasWp = false;
                m.GoalX = dest.positionX; m.GoalY = dest.positionY;
            }

            if (m.HasWp)
            {
                int wdx = m.WpX - bx, wdy = m.WpY - by;
                bool reached = wdx * wdx + wdy * wdy < WaypointArriveRaw * WaypointArriveRaw;
                bool stale = now - m.WpUntil > 0;
                bool blocked = !EolHqPatrol.TileLineClear(bot._arena, bx, by, m.WpX, m.WpY);
                if (!reached && !stale && !blocked)
                    return new Helpers.ObjectState { positionX = m.WpX, positionY = m.WpY };
                m.HasWp = false;
            }

            double toGoal = Math.Sqrt((double)(dest.positionX - bx) * (dest.positionX - bx) + (double)(dest.positionY - by) * (dest.positionY - by));
            double goalAng = Math.Atan2(dest.positionY - by, dest.positionX - bx);
            double bestScore = double.MinValue;
            short bestX = 0, bestY = 0;
            bool found = false;
            for (int k = 0; k < Directions; k++)
            {
                double ang = goalAng + k * (2 * Math.PI / Directions);
                int free = Ray(bot, bx, by, ang, RayMax);
                int len = free >= RayMax ? RayMax : free - WallBackOff;
                if (len < 48)
                    continue;   // a wall right there - it can see that, not an option
                short ex = (short)(bx + Math.Cos(ang) * len), ey = (short)(by + Math.Sin(ang) * len);
                double after = Math.Sqrt((double)(dest.positionX - ex) * (dest.positionX - ex) + (double)(dest.positionY - ey) * (dest.positionY - ey));
                double score = (toGoal - after);                                  // progress toward the goal
                if (EolHqPatrol.TileLineClear(bot._arena, ex, ey, dest.positionX, dest.positionY))
                    score += 260;                                                   // the way through
                score += len * 0.35;                                                // open runs (doorways / open ground)
                score -= Familiarity(m, ex, ey, now) * 150;                         // been there - it was walled in
                if (score > bestScore) { bestScore = score; bestX = ex; bestY = ey; found = true; }
            }
            if (!found)
                return null;
            m.HasWp = true;
            m.WpX = bestX; m.WpY = bestY;
            m.WpUntil = now + WaypointLifeMs;
            return new Helpers.ObjectState { positionX = bestX, positionY = bestY };
        }
    }
}
