using System;
using System.Collections.Generic;

using InfServer;
using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Out → near HQ → out. <see cref="ReturningHome"/> false = outbound leg, true = return leg.
    /// </summary>
    internal sealed class EolHqPatrolState
    {
        /// <summary>True leg goal (outbound / near-HQ). Arrival flips only against this.</summary>
        public Helpers.ObjectState Dest;
        /// <summary>Optional short waypoint toward Dest after a path fail — never counts as leg done.</summary>
        public Helpers.ObjectState Hop;
        public bool ReturningHome;
        public short TrackedDestX;
        public short TrackedDestY;
        /// <summary>Last aim fed to Api Approach (hop or dest) — detect aim snaps for ForceRequeue.</summary>
        public short TrackedNavAimX;
        public short TrackedNavAimY;
        public int ConsecutivePathFails;
        /// <summary>True after this leg received a FollowPath — used to finish when the path ends near Dest.</summary>
        public bool SawPathThisLeg;
        /// <summary>True after we already logged PATH for this Dest aim.</summary>
        public bool LoggedPathForAim;
        /// <summary>Throttle for quiet progress polls.</summary>
        public int TickLastProgressLog;
        /// <summary>Throttle ISSUE lines.</summary>
        public int TickLastIssueLog;
        /// <summary>When current Hop was set (stall detection).</summary>
        public int TickHopStarted;
        /// <summary>Last measured dist^2 to Hop for stall detection.</summary>
        public long HopLastDistSq;
        /// <summary>How many hop re-picks without closing on Dest this leg.</summary>
        public int HopStallCount;
        /// <summary>Best (smallest) Dest distance this leg — detect hop loops.</summary>
        public long BestDestDistThisLeg;
        /// <summary>Last tick Dest distance materially improved (creep stall).</summary>
        public int TickLastLegProgress;
    }

    /// <summary>
    /// Simple HQ patrol:
    /// 1) Pick open point away from HQ
    /// 2) Path there; when close enough (or path ends near it) → flip to return
    /// 3) Pick open point near HQ; when close enough → flip to outbound again
    /// </summary>
    internal static class EolHqPatrol
    {
        // Outbound = real sector runs; return = mid ring (not the HQ/crash pad).
        public const int OutboundMinRadiusRaw = 1100;
        public const int OutboundMaxRadiusRaw = 2600;
        public const int NearHqMinRadiusRaw = 550;
        public const int NearHqMaxRadiusRaw = 1050;
        public const int MinOutboundLegRaw = 750;
        public const int MinNearHqLegRaw = 420;
        public const int MinPeerDestRaw = 520;
        public const int ArriveRaw = 160;
        /// <summary>Return legs finish farther out — wall pockets near HQ block the exact Dest.</summary>
        public const int ReturnArriveRaw = 280;
        /// <summary>
        /// While HQ recall is active, bots already this close to the anchor are not re-forced home
        /// (prevents forever-camping the pad when a player is nearby).
        /// </summary>
        public const int RecallHomeSatisfiedRadiusRaw = 750;
        public const int ArriveLooseRaw = 200;
        public const int HopArriveRaw = 90;
        public const int DefaultPathKeepMs = 3000;
        public const int MaxPathKeepMs = 4500;
        public const int MaxFailsSameLeg = 3;
        public const int MaxHopStallsSameLeg = 2;
        public const int ProgressLogIntervalMs = 8000;
        public const int IssueLogIntervalMs = 5000;
        public const int HopStallMs = 1200;
        /// <summary>Min ms on a hop before it can count as done (stops arrive/re-hop jiggle).</summary>
        public const int HopMinTravelMs = 500;
        /// <summary>No real progress toward Dest for this long → repick / arrive loose.</summary>
        public const int LegCreepStallMs = 2800;
        public const int MinLegProgressRaw = 80;
        /// <summary>Outbound blocked but this close — good enough to flip return.</summary>
        public const int OutboundArriveLooseRaw = 240;
        public const int HopProgressRaw = 64;
        public const int ClearHopMinRaw = 140;
        public const int ClearHopMaxRaw = 420;
        /// <summary>Reject hop re-picks this close to the previous hop (jiggle loop).</summary>
        public const int HopRepeatRaw = 64;

        static long DistToDest(Bot bot, EolHqPatrolState s)
        {
            if (s?.Dest == null || bot?._state == null)
                return -1;
            return (long)Math.Sqrt(DistSq(bot._state.positionX, bot._state.positionY, s.Dest.positionX, s.Dest.positionY));
        }

        static long DistToHq(Bot bot, Vehicle hq)
        {
            if (bot?._state == null || hq?._state == null)
                return -1;
            return (long)Math.Sqrt(DistSq(bot._state.positionX, bot._state.positionY,
                hq._state.positionX, hq._state.positionY));
        }

        static bool LegArrived(Bot bot, Vehicle hq, EolHqPatrolState s)
        {
            if (s?.Dest == null || bot?._state == null)
                return true;

            // Outbound: finish hop first unless already close to Dest.
            if (s.Hop != null)
            {
                if (!s.ReturningHome)
                {
                    if (!Arrived(bot._state, s.Dest, OutboundArriveLooseRaw))
                        return false;
                }
                else if (!Arrived(bot._state, s.Dest, ReturnArriveRaw) && !HasReturnHomeLoS(bot, hq))
                    return false;
            }

            if (Arrived(bot._state, s.Dest, ArriveRaw))
                return true;

            if (!s.ReturningHome)
            {
                // Wall pocket close to outbound Dest — don't jiggle forever (e.g. dist ~195).
                if (Arrived(bot._state, s.Dest, OutboundArriveLooseRaw))
                    return true;
                return false;
            }

            if (Arrived(bot._state, s.Dest, ReturnArriveRaw))
                return true;

            return HasReturnHomeLoS(bot, hq);
        }

        static bool HasReturnHomeLoS(Bot bot, Vehicle hq)
        {
            if (bot?._state == null || hq?._state == null || bot._arena == null)
                return false;

            long hqDist = DistToHq(bot, hq);
            if (hqDist < 0 || hqDist > NearHqMaxRadiusRaw + 40)
                return false;

            return TileLineClear(bot._arena, bot._state.positionX, bot._state.positionY,
                hq._state.positionX, hq._state.positionY);
        }

        static string LegName(EolHqPatrolState s)
        {
            return s != null && s.ReturningHome ? "return" : "outbound";
        }

        /// <summary>Quiet lifecycle logs only — DEST / PATH / PROGRESS / ISSUE / ARRIVE.</summary>
        static void PatrolLog(Bot bot, string eventName, string detail)
        {
            // Disabled for now — served its purpose; leave callers so we can re-enable quickly.
            // if (bot == null)
            //     return;
            // Log.write(TLog.Normal, "[EolHqPatrol] id={0} {1} {2}", bot._id, eventName, detail ?? "");
            _ = bot;
            _ = eventName;
            _ = detail;
        }

        static void LogDest(Bot bot, EolHqPatrolState s)
        {
            if (s?.Dest == null)
            {
                PatrolLog(bot, "DEST", LegName(s) + " FAILED — no point");
                return;
            }
            PatrolLog(bot, "DEST",
                string.Format("{0} ({1},{2}) dist={3}",
                    LegName(s), s.Dest.positionX, s.Dest.positionY, DistToDest(bot, s)));
        }

        static void LogIssue(Bot bot, EolHqPatrolState s, int now, string detail)
        {
            if (s == null)
                return;
            if (s.TickLastIssueLog != 0
                && now - s.TickLastIssueLog < IssueLogIntervalMs
                && now >= s.TickLastIssueLog)
                return;
            s.TickLastIssueLog = now == 0 ? 1 : now;
            PatrolLog(bot, "ISSUE", detail);
        }

        static void ResetLegLogFlags(EolHqPatrolState s)
        {
            if (s == null)
                return;
            s.LoggedPathForAim = false;
            s.TickLastProgressLog = Environment.TickCount;
            if (s.TickLastProgressLog == 0)
                s.TickLastProgressLog = 1;
            s.TickHopStarted = 0;
            s.HopLastDistSq = 0;
            s.HopStallCount = 0;
            s.BestDestDistThisLeg = 0;
            s.TickLastLegProgress = 0;
        }

        static void AssignHop(EolHqPatrolState s, Helpers.ObjectState hop, int now)
        {
            if (s == null || hop == null)
                return;
            bool newAim = s.Hop == null
                || s.Hop.positionX != hop.positionX
                || s.Hop.positionY != hop.positionY;
            s.Hop = hop;
            s.TrackedDestX = hop.positionX;
            s.TrackedDestY = hop.positionY;
            s.TickHopStarted = now == 0 ? 1 : now;
            s.HopLastDistSq = 0;
            if (newAim)
                s.LoggedPathForAim = false;
        }

        public static long DistSq(short ax, short ay, short bx, short by)
        {
            long dx = (long)ax - bx;
            long dy = (long)ay - by;
            return dx * dx + dy * dy;
        }

        public static bool Arrived(Helpers.ObjectState bot, Helpers.ObjectState dest, int arriveRaw = ArriveRaw)
        {
            if (bot == null || dest == null)
                return true;
            long a = arriveRaw;
            return DistSq(bot.positionX, bot.positionY, dest.positionX, dest.positionY) <= a * a;
        }

        public static bool TileLineClear(Arena arena, short x0, short y0, short x1, short y1)
        {
            if (arena == null)
                return false;
            return Helpers.calcBresenhemsPredicate(arena, x0, y0, x1, y1,
                delegate (LvlInfo.Tile t) { return !t.Blocked; });
        }

        public static bool IsOpenForBot(Bot bot, short rawX, short rawY)
        {
            if (bot?._arena == null)
                return false;
            if (BotWallAvoidance.IsImpassable(bot, rawX, rawY))
                return false;
            if (bot._arena.getTile(rawX, rawY).Blocked)
                return false;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    short sx = (short)(rawX + dx * 16);
                    short sy = (short)(rawY + dy * 16);
                    if (BotWallAvoidance.IsImpassable(bot, sx, sy))
                        return false;
                    if (bot._arena.getTile(sx, sy).Blocked)
                        return false;
                }
            }

            return true;
        }

        static bool IsStandable(Bot bot, short rawX, short rawY)
        {
            if (bot?._arena == null)
                return false;
            if (BotWallAvoidance.IsImpassable(bot, rawX, rawY))
                return false;
            return !bot._arena.getTile(rawX, rawY).Blocked;
        }

        public static Helpers.ObjectState AbsoluteFallback(Bot bot, Vehicle hq)
        {
            if (hq == null || bot == null)
                return null;

            Helpers.ObjectState botState = bot._state;
            long minAway = (long)ArriveRaw * ArriveRaw;

            for (int radius = 220; radius <= OutboundMaxRadiusRaw + 200; radius += 40)
            {
                for (int i = 0; i < 32; i++)
                {
                    double angle = ((bot._id * 11) + i) * (Math.PI * 2.0 / 32.0);
                    short pX = (short)(hq._state.positionX + Math.Cos(angle) * radius);
                    short pY = (short)(hq._state.positionY + Math.Sin(angle) * radius);
                    if (!IsStandable(bot, pX, pY))
                        continue;
                    if (DistSq(botState.positionX, botState.positionY, pX, pY) < minAway)
                        continue;
                    return new Helpers.ObjectState { positionX = pX, positionY = pY };
                }
            }

            double a = (bot._id % 16) * (Math.PI * 2.0 / 16.0);
            return new Helpers.ObjectState
            {
                positionX = (short)(hq._state.positionX + Math.Cos(a) * OutboundMinRadiusRaw),
                positionY = (short)(hq._state.positionY + Math.Sin(a) * OutboundMinRadiusRaw),
            };
        }

        static Helpers.ObjectState PickLegDest(Bot bot, Arena arena, Vehicle hq, bool returningHome)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                Helpers.ObjectState dest = returningHome
                    ? PickNearHq(bot, arena, hq)
                    : PickOutbound(bot, arena, hq);
                if (dest == null)
                    dest = AbsoluteFallback(bot, hq);
                if (dest != null && !Arrived(bot._state, dest))
                    return dest;
            }

            return AbsoluteFallback(bot, hq);
        }

        /// <summary>Finish current leg and start the opposite (outbound ↔ home).</summary>
        static Helpers.ObjectState NavTarget(EolHqPatrolState s)
        {
            if (s == null)
                return null;
            return s.Hop ?? s.Dest;
        }

        static void ClearHop(EolHqPatrolState s)
        {
            if (s != null)
            {
                s.Hop = null;
                s.TrackedNavAimX = 0;
                s.TrackedNavAimY = 0;
            }
        }

        static void StartNextLeg(Bot bot, Arena arena, Vehicle hq, EolHqPatrolState s)
        {
            s.ReturningHome = !s.ReturningHome;
            ClearHop(s);
            s.Dest = PickLegDest(bot, arena, hq, s.ReturningHome);
            s.ConsecutivePathFails = 0;
            s.SawPathThisLeg = false;
            ResetLegLogFlags(s);
            if (s.Dest != null)
            {
                s.TrackedDestX = s.Dest.positionX;
                s.TrackedDestY = s.Dest.positionY;
            }
            LogDest(bot, s);
        }

        /// <summary>Same leg, new point (unreachable Dest) — do not flip out/home.</summary>
        static void RePickSameLeg(Bot bot, Arena arena, Vehicle hq, EolHqPatrolState s)
        {
            ClearHop(s);
            s.Dest = PickLegDest(bot, arena, hq, s.ReturningHome);
            s.SawPathThisLeg = false;
            ResetLegLogFlags(s);
            if (s.Dest != null)
            {
                s.TrackedDestX = s.Dest.positionX;
                s.TrackedDestY = s.Dest.positionY;
            }
            LogDest(bot, s);
        }

        public static void AssignFreshDestination(Bot bot, Arena arena, Vehicle hq, EolHqPatrolState s, bool? returningHome = null)
        {
            if (returningHome.HasValue)
                s.ReturningHome = returningHome.Value;

            ClearHop(s);
            s.Dest = PickLegDest(bot, arena, hq, s.ReturningHome);
            s.ConsecutivePathFails = 0;
            s.SawPathThisLeg = false;
            ResetLegLogFlags(s);
            if (s.Dest != null)
            {
                s.TrackedDestX = s.Dest.positionX;
                s.TrackedDestY = s.Dest.positionY;
            }
            LogDest(bot, s);
        }

        /// <summary>
        /// Outbound patrol only: drop roam/hop and rush straight to HQ when hostiles breach the recall radius.
        /// Bots already near the pad are left alone so recall cannot pin them forever while a player is nearby.
        /// </summary>
        public static bool TryRecallToHqFromPatrol(Bot bot, Vehicle hq, EolHqPatrolState s, int now, bool recallActive)
        {
            if (!recallActive || bot == null || hq == null || hq.IsDead || s == null || s.ReturningHome)
                return false;

            // Already on the pad / near the shuttle — allow outbound legs & combat instead of flap-recalling.
            double distHome = Helpers.distanceTo(bot._state, hq._state);
            if (distHome <= RecallHomeSatisfiedRadiusRaw)
                return false;

            s.ReturningHome = true;
            ClearHop(s);
            s.Dest = new Helpers.ObjectState
            {
                positionX = hq._state.positionX,
                positionY = hq._state.positionY,
            };
            s.TrackedDestX = s.Dest.positionX;
            s.TrackedDestY = s.Dest.positionY;
            s.ConsecutivePathFails = 0;
            s.SawPathThisLeg = false;
            s.BestDestDistThisLeg = 0;
            s.HopStallCount = 0;
            s.HopLastDistSq = 0;
            s.TickHopStarted = 0;
            s.TickLastLegProgress = now == 0 ? 1 : now;
            ResetLegLogFlags(s);
            bot.Navigation?.Clear();
            PatrolLog(bot, "RECALL", "hostile near HQ — rush home");
            return true;
        }

        /// <summary>Defense patrol tick with HQ recall when an enemy player is near the base.</summary>
        public static void TickDefensePatrol(
            Bot bot,
            Arena arena,
            Vehicle hq,
            int now,
            EolHqPatrolState s,
            int pathKeepMs,
            Script_Eol script,
            Team team)
        {
            bool recall = script != null && team != null && script.IsHqPatrolRecallActive(team);
            TickDefensePatrol(bot, arena, hq, now, s, pathKeepMs, recall);
        }

        /// <summary>Defense/alien patrol with an explicit recall flag (hostile near anchor).</summary>
        public static void TickDefensePatrol(
            Bot bot,
            Arena arena,
            Vehicle hq,
            int now,
            EolHqPatrolState s,
            int pathKeepMs,
            bool recallActive)
        {
            TryRecallToHqFromPatrol(bot, hq, s, now, recallActive);
            Tick(bot, arena, hq, now, s, pathKeepMs);
        }

        static int ClampKeepMs(int pathKeepMs)
        {
            int keep = pathKeepMs > 0 ? pathKeepMs : DefaultPathKeepMs;
            if (keep > MaxPathKeepMs)
                keep = DefaultPathKeepMs;
            if (keep < 900)
                keep = 900;
            return keep;
        }

        /// <summary>
        /// Walk tile Bresenham toward Dest; return last standable tile center before a block.
        /// Used so pending seek never aims through walls (and never freezes on Zero).
        /// </summary>
        static Helpers.ObjectState LastClearAlongRay(Bot bot, short x0, short y0, short x1, short y1)
        {
            if (bot?._arena == null)
                return null;

            int t0x = x0 / 16;
            int t0y = y0 / 16;
            int t1x = x1 / 16;
            int t1y = y1 / 16;

            int dx = Math.Abs(t1x - t0x);
            int dy = Math.Abs(t1y - t0y);
            int sx = t0x < t1x ? 1 : -1;
            int sy = t0y < t1y ? 1 : -1;
            int err = dx - dy;

            short lastX = x0;
            short lastY = y0;
            int x = t0x;
            int y = t0y;

            while (!(x == t1x && y == t1y))
            {
                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }

                short rawX = (short)(x * 16 + 8);
                short rawY = (short)(y * 16 + 8);
                if (!IsStandable(bot, rawX, rawY))
                    break;

                lastX = rawX;
                lastY = rawY;
            }

            if (DistSq(x0, y0, lastX, lastY) < 48L * 48L)
                return null;

            return new Helpers.ObjectState { positionX = lastX, positionY = lastY };
        }

        /// <summary>
        /// Near, LOS-clear hop toward Dest (or a side jog) when long legs can't path.
        /// </summary>
        static Helpers.ObjectState PickClearHop(Bot bot, Helpers.ObjectState dest, Helpers.ObjectState avoidHop = null)
        {
            if (bot?._arena == null || bot._state == null || dest == null)
                return null;

            Arena arena = bot._arena;
            short bx = bot._state.positionX;
            short by = bot._state.positionY;
            long toDestSq = DistSq(bx, by, dest.positionX, dest.positionY);
            if (toDestSq < (long)ClearHopMinRaw * ClearHopMinRaw)
                return null;

            long avoidSq = (long)HopRepeatRaw * HopRepeatRaw;
            double baseAng = Math.Atan2(dest.positionY - by, dest.positionX - bx);
            Helpers.ObjectState best = null;
            double bestScore = -1;

            // Toward Dest, plus left/right jogs so we can leave wall pockets.
            double[] offsets = { 0, 0.55, -0.55, 1.0, -1.0, 1.4, -1.4, 1.8, -1.8 };
            int[] radii = { ClearHopMinRaw + 20, (ClearHopMinRaw + ClearHopMaxRaw) / 2, ClearHopMaxRaw - 10, ClearHopMaxRaw };

            for (int oi = 0; oi < offsets.Length; oi++)
            {
                double ang = baseAng + offsets[oi];
                for (int ri = 0; ri < radii.Length; ri++)
                {
                    int radius = radii[ri] + ((bot._id * 7 + oi * 11 + ri * 3) % 31);
                    if (radius < ClearHopMinRaw)
                        radius = ClearHopMinRaw;
                    if (radius > ClearHopMaxRaw)
                        radius = ClearHopMaxRaw;

                    short pX = (short)(bx + Math.Cos(ang) * radius);
                    short pY = (short)(by + Math.Sin(ang) * radius);
                    if (!IsStandable(bot, pX, pY))
                        continue;
                    if (!TileLineClear(arena, bx, by, pX, pY))
                        continue;
                    if (avoidHop != null && DistSq(pX, pY, avoidHop.positionX, avoidHop.positionY) <= avoidSq)
                        continue;

                    long fromBot = DistSq(bx, by, pX, pY);
                    long remain = DistSq(pX, pY, dest.positionX, dest.positionY);
                    // Prefer hops that clear LOS and still reduce distance to Dest.
                    double score = (toDestSq - remain) + (oi == 0 ? 80.0 : 0.0) + (fromBot / 20.0);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Helpers.ObjectState { positionX = pX, positionY = pY };
                    }
                }
            }

            return best;
        }

        static Vector3 SeekRaw(InfantryVehicle vehicle, short rawX, short rawY, float scale)
        {
            Vector3 pos = new Vector3(((float)rawX) / 100.0f, ((float)rawY) / 100.0f, 0);
            return vehicle.SteerForSeek(pos) * scale;
        }

        /// <summary>
        /// Strong seek toward Dest/Hop. Prefer clear aim; never return Zero (jiggle/freeze).
        /// </summary>
        public static Func<InfantryVehicle, Vector3> MakeLineClearPendingSeek(Bot bot, Func<Helpers.ObjectState> getDest)
        {
            return vehicle =>
            {
                Helpers.ObjectState dest = getDest?.Invoke();
                if (bot == null || dest == null || vehicle == null || bot._arena == null || bot._state == null)
                    return Vector3.Zero;

                short bx = bot._state.positionX;
                short by = bot._state.positionY;
                Helpers.ObjectState aim = dest;
                float scale = 1.15f;

                if (!TileLineClear(bot._arena, bx, by, dest.positionX, dest.positionY))
                {
                    aim = LastClearAlongRay(bot, bx, by, dest.positionX, dest.positionY);
                    if (aim == null)
                        aim = PickClearHop(bot, dest);
                    if (aim == null)
                        return SeekRaw(vehicle, dest.positionX, dest.positionY, 0.85f);
                    scale = 1.05f;
                }

                long aimDistSq = DistSq(bx, by, aim.positionX, aim.positionY);
                if (aimDistSq < 28L * 28L)
                {
                    if (aim != dest)
                        return SeekRaw(vehicle, dest.positionX, dest.positionY, 0.9f);
                    return SeekRaw(vehicle, dest.positionX, dest.positionY, 0.35f);
                }

                // Side slide when jammed — keep most of the forward seek.
                if (!BotWallAvoidance.ProbeAheadClear(bot, 2))
                    scale *= 0.8f;

                return SeekRaw(vehicle, aim.positionX, aim.positionY, scale);
            };
        }

        static void ApplyPatrolSteer(Bot bot, Func<InfantryVehicle, Vector3> pending)
        {
            if (bot == null || pending == null)
                return;
            if (bot.MovementMode == BotMovementMode.Api)
                bot.ApplySteer(pending);
            else if (bot._movement is SteeringController steering)
                steering.steerDelegate = pending;
        }

        /// <summary>
        /// Prefer Api corridor toward Dest/Hop; fall back to seek if navigation unavailable.
        /// Force-requeues only when the aim point moves (hop/dest change) so motion stays continuous.
        /// </summary>
        static void ApplyPatrolMotion(Bot bot, EolHqPatrolState s, Func<InfantryVehicle, Vector3> pending)
        {
            if (bot == null || pending == null)
                return;

            Helpers.ObjectState aim = NavTarget(s);
            if (bot.MovementMode != BotMovementMode.Api || aim == null)
            {
                ApplyPatrolSteer(bot, pending);
                return;
            }

            bool force = false;
            if (s.TrackedNavAimX != aim.positionX || s.TrackedNavAimY != aim.positionY)
            {
                if (s.TrackedNavAimX == 0 && s.TrackedNavAimY == 0)
                    force = true;
                else
                {
                    long d = DistSq(s.TrackedNavAimX, s.TrackedNavAimY, aim.positionX, aim.positionY);
                    force = d > (long)HopRepeatRaw * HopRepeatRaw;
                }
                s.TrackedNavAimX = aim.positionX;
                s.TrackedNavAimY = aim.positionY;
            }

            bot.ApplyNavigationApproach(aim, pending, new BotNavigateOptions
            {
                ForceRequeue = force,
                RequeueMinMs = 1600,
                PathStartIndex = 0,
                Priority = PathPriority.Idle
            });
        }

        public static void Tick(
            Bot bot,
            Arena arena,
            Vehicle hq,
            int now,
            EolHqPatrolState s,
            int pathKeepMs)
        {
            if (bot == null || arena == null || hq == null || s == null)
                return;

            // First Dest: always go outbound.
            if (s.Dest == null)
            {
                s.ReturningHome = false;
                s.Dest = PickLegDest(bot, arena, hq, false);
                if (s.Dest == null)
                {
                    PatrolLog(bot, "DEST", "outbound FAILED — no point");
                    return;
                }
                s.TrackedDestX = s.Dest.positionX;
                s.TrackedDestY = s.Dest.positionY;
                s.SawPathThisLeg = false;
                ResetLegLogFlags(s);
                LogDest(bot, s);
            }

            BotNavigator nav = bot.Navigation;

            // Only drop failed nav — keep Following corridors for smooth patrol.
            if (nav.State == BotNavigationState.Failed)
                bot.Navigation.Clear();

            // Intermediate hop done → resume Dest (min travel time avoids hop jiggle).
            if (s.Hop != null
                && Arrived(bot._state, s.Hop, HopArriveRaw)
                && s.TickHopStarted != 0
                && now - s.TickHopStarted >= HopMinTravelMs)
            {
                ClearHop(s);
                s.TickHopStarted = 0;
            }

            // True Dest arrival — return leg uses wider radius / near-HQ completion.
            if (LegArrived(bot, hq, s))
            {
                ClearHop(s);
                string finished = LegName(s);
                PatrolLog(bot, "ARRIVE",
                    string.Format("{0} done dist={1} hq={2} -> {3}",
                        finished, DistToDest(bot, s), DistToHq(bot, hq),
                        finished == "outbound" ? "return" : "outbound"));
                bot.Navigation.Clear();
                StartNextLeg(bot, arena, hq, s);
                if (s.Dest == null)
                    return;
            }
            else if (!IsStandable(bot, s.Dest.positionX, s.Dest.positionY))
            {
                bot.Navigation.Clear();
                RePickSameLeg(bot, arena, hq, s);
                if (s.Dest == null)
                    return;
            }

            long destDist = DistToDest(bot, s);
            if (destDist >= 0)
            {
                if (s.BestDestDistThisLeg == 0 || destDist + MinLegProgressRaw < s.BestDestDistThisLeg)
                {
                    s.BestDestDistThisLeg = destDist;
                    s.HopStallCount = 0;
                    s.TickLastLegProgress = now == 0 ? 1 : now;
                }
                else if (s.TickLastLegProgress != 0 && now - s.TickLastLegProgress >= LegCreepStallMs)
                {
                    ClearHop(s);
                    if (!s.ReturningHome)
                    {
                        LogIssue(bot, s, now, "creep-stall — new outbound point");
                        RePickSameLeg(bot, arena, hq, s);
                    }
                    else if (HasReturnHomeLoS(bot, hq))
                    {
                        LogIssue(bot, s, now, "creep-stall return near HQ — done");
                        StartNextLeg(bot, arena, hq, s);
                    }
                    else
                    {
                        LogIssue(bot, s, now, "creep-stall — new return point");
                        RePickSameLeg(bot, arena, hq, s);
                    }
                    if (s.Dest == null)
                        return;
                    s.TickLastLegProgress = now == 0 ? 1 : now;
                }
            }

            bool lineClearToDest = TileLineClear(arena, bot._state.positionX, bot._state.positionY,
                s.Dest.positionX, s.Dest.positionY);

            // No LOS to Dest → set / refresh a clear hop (never pathfind).
            if (!lineClearToDest)
            {
                bool needNewHop = s.Hop == null;
                if (s.Hop != null && bot._state != null)
                {
                    long hopDistSq = DistSq(bot._state.positionX, bot._state.positionY,
                        s.Hop.positionX, s.Hop.positionY);
                    long progressSlack = (long)HopProgressRaw * HopProgressRaw;
                    bool progressed = s.HopLastDistSq == 0 || hopDistSq + progressSlack < s.HopLastDistSq;
                    if (progressed)
                    {
                        s.HopLastDistSq = hopDistSq;
                    }
                    else if (s.TickHopStarted != 0 && now - s.TickHopStarted >= HopStallMs)
                    {
                        needNewHop = true;
                    }
                }

                if (needNewHop)
                {
                    Helpers.ObjectState hop = PickClearHop(bot, s.Dest, s.Hop)
                        ?? LastClearAlongRay(bot, bot._state.positionX, bot._state.positionY,
                            s.Dest.positionX, s.Dest.positionY);
                    if (hop != null
                        && s.Hop != null
                        && DistSq(hop.positionX, hop.positionY, s.Hop.positionX, s.Hop.positionY)
                            <= (long)HopRepeatRaw * HopRepeatRaw)
                    {
                        hop = null;
                    }

                    if (hop != null)
                    {
                        AssignHop(s, hop, now);
                        s.HopStallCount++;
                        LogIssue(bot, s, now,
                            string.Format("blocked -> hop ({0},{1}) keep dest dist={2}",
                                hop.positionX, hop.positionY, DistToDest(bot, s)));
                    }
                    else
                    {
                        ClearHop(s);
                        s.HopStallCount++;
                        s.ConsecutivePathFails++;

                        // Return near HQ — don't freeze in a hop pocket; flip outbound.
                        if (s.ReturningHome && HasReturnHomeLoS(bot, hq))
                        {
                            LogIssue(bot, s, now, "return near HQ — done");
                            StartNextLeg(bot, arena, hq, s);
                            s.ConsecutivePathFails = 0;
                            s.HopStallCount = 0;
                        }
                        else if (s.HopStallCount >= MaxHopStallsSameLeg
                            || s.ConsecutivePathFails >= MaxFailsSameLeg)
                        {
                            if (s.ReturningHome)
                            {
                                LogIssue(bot, s, now,
                                    s.HopStallCount >= MaxHopStallsSameLeg
                                        ? "hop-stall — skip return leg"
                                        : "blocked x" + MaxFailsSameLeg + " — skip return leg");
                                StartNextLeg(bot, arena, hq, s);
                            }
                            else
                            {
                                LogIssue(bot, s, now,
                                    s.HopStallCount >= MaxHopStallsSameLeg
                                        ? "hop-stall — new outbound point"
                                        : "blocked — new outbound point");
                                RePickSameLeg(bot, arena, hq, s);
                            }
                            s.ConsecutivePathFails = 0;
                            s.HopStallCount = 0;
                        }
                        else
                        {
                            LogIssue(bot, s, now, "blocked — new " + LegName(s) + " point");
                            RePickSameLeg(bot, arena, hq, s);
                        }

                        if (s.Dest == null)
                            return;
                    }
                }
            }
            else if (s.Hop != null)
            {
                ClearHop(s);
                s.TickHopStarted = 0;
                s.ConsecutivePathFails = 0;
            }

            if (!s.LoggedPathForAim)
            {
                s.LoggedPathForAim = true;
                s.SawPathThisLeg = true;
                PatrolLog(bot, "PATH",
                    string.Format("{0} {1} dist={2}",
                        LegName(s),
                        s.Hop != null ? "hop-run" : "clear-run",
                        DistToDest(bot, s)));
            }

            if (s.TickLastProgressLog == 0
                || now - s.TickLastProgressLog >= ProgressLogIntervalMs
                || now < s.TickLastProgressLog)
            {
                s.TickLastProgressLog = now;
                PatrolLog(bot, "PROGRESS",
                    string.Format("{0} dist={1} {2}",
                        LegName(s), DistToDest(bot, s), s.Hop != null ? "hop-run" : "clear-run"));
            }

            Func<InfantryVehicle, Vector3> pending = MakeLineClearPendingSeek(bot, () => NavTarget(s));
            ApplyPatrolMotion(bot, s, pending);
            _ = pathKeepMs;
        }

        public enum EolSteerTickResult
        {
            Moving,
            Arrived,
            NeedsRepick,
        }

        /// <summary>Clears steer-only roam / patrol state (combat handoff, new waypoint).</summary>
        public static void ResetSteerState(EolHqPatrolState s)
        {
            if (s == null)
                return;
            s.Dest = null;
            ClearHop(s);
            ResetLegLogFlags(s);
        }

        static bool SteerDestArrived(Bot bot, EolHqPatrolState s, int arriveRaw, int looseArriveRaw)
        {
            if (s?.Dest == null || bot?._state == null)
                return true;

            if (s.Hop != null && !Arrived(bot._state, s.Dest, looseArriveRaw))
                return false;

            if (Arrived(bot._state, s.Dest, arriveRaw))
                return true;

            return Arrived(bot._state, s.Dest, looseArriveRaw);
        }

        /// <summary>
        /// Steer-only move toward one Dest with hop / stall / creep recovery (raiders, etc.).
        /// HQ patrol uses <see cref="Tick"/> for outbound/return legs.
        /// </summary>
        public static EolSteerTickResult TickSteerToDest(
            Bot bot,
            Arena arena,
            int now,
            EolHqPatrolState s,
            Helpers.ObjectState dest,
            int arriveRaw = ArriveRaw,
            int looseArriveRaw = OutboundArriveLooseRaw,
            bool logLifecycle = false)
        {
            if (bot == null || arena == null || s == null || dest == null)
                return EolSteerTickResult.NeedsRepick;

            if (s.Dest == null
                || s.Dest.positionX != dest.positionX
                || s.Dest.positionY != dest.positionY)
            {
                ClearHop(s);
                s.Dest = new Helpers.ObjectState { positionX = dest.positionX, positionY = dest.positionY };
                ResetLegLogFlags(s);
            }

            BotNavigator nav = bot.Navigation;
            if (nav.State == BotNavigationState.Failed)
                bot.Navigation.Clear();

            if (s.Hop != null
                && Arrived(bot._state, s.Hop, HopArriveRaw)
                && s.TickHopStarted != 0
                && now - s.TickHopStarted >= HopMinTravelMs)
            {
                ClearHop(s);
                s.TickHopStarted = 0;
            }

            if (SteerDestArrived(bot, s, arriveRaw, looseArriveRaw))
            {
                ClearHop(s);
                bot.Navigation.Clear();
                return EolSteerTickResult.Arrived;
            }

            if (!IsStandable(bot, s.Dest.positionX, s.Dest.positionY))
            {
                bot.Navigation.Clear();
                return EolSteerTickResult.NeedsRepick;
            }

            long destDist = DistToDest(bot, s);
            if (destDist >= 0)
            {
                if (s.BestDestDistThisLeg == 0 || destDist + MinLegProgressRaw < s.BestDestDistThisLeg)
                {
                    s.BestDestDistThisLeg = destDist;
                    s.HopStallCount = 0;
                    s.TickLastLegProgress = now == 0 ? 1 : now;
                }
                else if (s.TickLastLegProgress != 0 && now - s.TickLastLegProgress >= LegCreepStallMs)
                {
                    ClearHop(s);
                    if (logLifecycle)
                        LogIssue(bot, s, now, "creep-stall — new roam point");
                    bot.Navigation.Clear();
                    return EolSteerTickResult.NeedsRepick;
                }
            }

            bool lineClearToDest = TileLineClear(arena, bot._state.positionX, bot._state.positionY,
                s.Dest.positionX, s.Dest.positionY);

            if (!lineClearToDest)
            {
                bool needNewHop = s.Hop == null;
                if (s.Hop != null && bot._state != null)
                {
                    long hopDistSq = DistSq(bot._state.positionX, bot._state.positionY,
                        s.Hop.positionX, s.Hop.positionY);
                    long progressSlack = (long)HopProgressRaw * HopProgressRaw;
                    bool progressed = s.HopLastDistSq == 0 || hopDistSq + progressSlack < s.HopLastDistSq;
                    if (progressed)
                        s.HopLastDistSq = hopDistSq;
                    else if (s.TickHopStarted != 0 && now - s.TickHopStarted >= HopStallMs)
                        needNewHop = true;
                }

                if (needNewHop)
                {
                    Helpers.ObjectState hop = PickClearHop(bot, s.Dest, s.Hop)
                        ?? LastClearAlongRay(bot, bot._state.positionX, bot._state.positionY,
                            s.Dest.positionX, s.Dest.positionY);
                    if (hop != null
                        && s.Hop != null
                        && DistSq(hop.positionX, hop.positionY, s.Hop.positionX, s.Hop.positionY)
                            <= (long)HopRepeatRaw * HopRepeatRaw)
                    {
                        hop = null;
                    }

                    if (hop != null)
                    {
                        AssignHop(s, hop, now);
                        s.HopStallCount++;
                        if (logLifecycle)
                        {
                            LogIssue(bot, s, now,
                                string.Format("blocked -> hop ({0},{1}) keep dest dist={2}",
                                    hop.positionX, hop.positionY, DistToDest(bot, s)));
                        }
                    }
                    else
                    {
                        ClearHop(s);
                        s.HopStallCount++;
                        s.ConsecutivePathFails++;
                        if (s.HopStallCount >= MaxHopStallsSameLeg
                            || s.ConsecutivePathFails >= MaxFailsSameLeg)
                        {
                            if (logLifecycle)
                            {
                                LogIssue(bot, s, now,
                                    s.HopStallCount >= MaxHopStallsSameLeg
                                        ? "hop-stall — new roam point"
                                        : "blocked — new roam point");
                            }
                            s.ConsecutivePathFails = 0;
                            s.HopStallCount = 0;
                            bot.Navigation.Clear();
                            return EolSteerTickResult.NeedsRepick;
                        }

                        if (logLifecycle)
                            LogIssue(bot, s, now, "blocked — retry roam point");
                        bot.Navigation.Clear();
                        return EolSteerTickResult.NeedsRepick;
                    }
                }
            }
            else if (s.Hop != null)
            {
                ClearHop(s);
                s.TickHopStarted = 0;
                s.ConsecutivePathFails = 0;
            }

            if (logLifecycle && !s.LoggedPathForAim)
            {
                s.LoggedPathForAim = true;
                PatrolLog(bot, "PATH",
                    string.Format("roam {0} dist={1}",
                        s.Hop != null ? "hop-run" : "clear-run", DistToDest(bot, s)));
            }

            Func<InfantryVehicle, Vector3> pending = MakeLineClearPendingSeek(bot, () => NavTarget(s));
            ApplyPatrolMotion(bot, s, pending);
            return EolSteerTickResult.Moving;
        }

        public static Helpers.ObjectState PickOutbound(Bot bot, Arena arena, Vehicle hq)
        {
            return PickInDisk(bot, arena, hq, OutboundMinRadiusRaw, OutboundMaxRadiusRaw, MinOutboundLegRaw, true)
                ?? AbsoluteFallback(bot, hq);
        }

        public static Helpers.ObjectState PickNearHq(Bot bot, Arena arena, Vehicle hq)
        {
            return PickInDisk(bot, arena, hq, NearHqMinRadiusRaw, NearHqMaxRadiusRaw, MinNearHqLegRaw, false)
                ?? AbsoluteFallback(bot, hq);
        }

        static Helpers.ObjectState PickInDisk(
            Bot bot,
            Arena arena,
            Vehicle hq,
            int minRadius,
            int maxRadius,
            int minFromBot,
            bool preferAwayFromPeers)
        {
            if (arena == null || hq == null || bot?._state == null)
                return null;

            Helpers.ObjectState botState = bot._state;
            long minFromBotSq = (long)minFromBot * minFromBot;
            long peerSq = (long)MinPeerDestRaw * MinPeerDestRaw;
            long arriveSq = (long)ArriveRaw * ArriveRaw;

            int wedges = 16;
            double wedge = (Math.PI * 2.0) / wedges;
            double homeAngle = (bot._id % wedges) * wedge
                + (((bot._id / wedges) & 1) == 0 ? wedge * 0.3 : -wedge * 0.3);

            Helpers.ObjectState best = null;
            double bestScore = -1;

            for (int i = 0; i < 140; i++)
            {
                double angle = homeAngle + (arena._rand.NextDouble() - 0.5) * wedge;
                if (i > 50)
                    angle = arena._rand.NextDouble() * Math.PI * 2.0;

                // Bias hard toward outer radii so patrol covers ground instead of hugging the pad.
                int span = maxRadius - minRadius;
                int radius = span <= 0
                    ? minRadius
                    : minRadius + (int)(span * (0.55 + 0.45 * arena._rand.NextDouble()));
                radius = Math.Min(maxRadius, radius + ((bot._id * 17 + i * 13) % 97));

                short pX = (short)(hq._state.positionX + Math.Cos(angle) * radius);
                short pY = (short)(hq._state.positionY + Math.Sin(angle) * radius);

                if (!IsOpenForBot(bot, pX, pY) && !IsStandable(bot, pX, pY))
                    continue;
                if (!IsStandable(bot, pX, pY))
                    continue;

                long fromBot = DistSq(botState.positionX, botState.positionY, pX, pY);
                // Reject Dest one hop can "arrive" into (minFromBot + arrive guard).
                long hopGap = ClearHopMaxRaw + ArriveRaw + 40;
                long minLeg = Math.Max(minFromBotSq, hopGap * hopGap);
                if (preferAwayFromPeers)
                {
                    if (fromBot < minLeg || fromBot <= arriveSq)
                        continue;
                }
                else if (fromBot < minFromBotSq || fromBot <= arriveSq)
                {
                    continue;
                }

                double peerClear = MinPeerClearance(bot, arena, pX, pY);
                bool lineOk = TileLineClear(arena, botState.positionX, botState.positionY, pX, pY);

                // Prefer a LOS-clear Dest — otherwise crawl along the ray, but keep min leg length.
                if (!lineOk)
                {
                    Helpers.ObjectState along = LastClearAlongRay(bot, botState.positionX, botState.positionY, pX, pY);
                    long alongDist = along == null
                        ? 0
                        : DistSq(botState.positionX, botState.positionY, along.positionX, along.positionY);
                    long need = preferAwayFromPeers ? minLeg : minFromBotSq;
                    if (along != null && alongDist > arriveSq && alongDist >= need)
                    {
                        pX = along.positionX;
                        pY = along.positionY;
                        fromBot = alongDist;
                        lineOk = true;
                        peerClear = MinPeerClearance(bot, arena, pX, pY);
                    }
                    else if (i < 100)
                    {
                        continue;
                    }
                }

                if (preferAwayFromPeers)
                {
                    if (fromBot < minLeg || fromBot <= arriveSq)
                        continue;
                }
                else if (fromBot < minFromBotSq || fromBot <= arriveSq)
                {
                    continue;
                }

                if (preferAwayFromPeers && peerClear * peerClear < peerSq)
                {
                    double crowdedScore = peerClear + (lineOk ? 200.0 : 0.0) + radius * 0.2;
                    if (crowdedScore > bestScore)
                    {
                        bestScore = crowdedScore;
                        best = new Helpers.ObjectState { positionX = pX, positionY = pY };
                    }
                    continue;
                }

                // LOS + farther from HQ preferred so bots leave the HQ pocket.
                double score = peerClear * 1.4 + (lineOk ? 600.0 : 0.0) + radius * 0.55
                    + Math.Sqrt(fromBot) * 0.35 + (arena._rand.NextDouble() * 40.0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new Helpers.ObjectState { positionX = pX, positionY = pY };
                }

                // Early accept only when LOS is clear and far enough out.
                if (i < 50 && lineOk && peerClear >= MinPeerDestRaw && IsOpenForBot(bot, pX, pY))
                    return best;
            }

            return best;
        }

        static double MinPeerClearance(Bot self, Arena arena, short x, short y)
        {
            double best = double.MaxValue;
            if (arena == null || self == null)
                return best;

            List<Vehicle> nearby = arena.getVehiclesInRange(x, y, 3600,
                delegate (Vehicle v)
                {
                    if (v == null || v == self || v.IsDead || v._team != self._team)
                        return false;
                    return v is BasicDefense || v is Captain || v is AlienHorde || v is Engineer;
                });

            foreach (Vehicle v in nearby)
            {
                Helpers.ObjectState peerDest = null;
                if (v is BasicDefense bd)
                    peerDest = bd._targetPoint;
                else if (v is Captain cap)
                    peerDest = cap._targetPoint;
                else if (v is AlienHorde ah)
                    peerDest = ah._targetPoint;

                // Prefer peer destination clearance; also keep clear of their live body.
                short px = peerDest != null ? peerDest.positionX : v._state.positionX;
                short py = peerDest != null ? peerDest.positionY : v._state.positionY;
                double dDest = Math.Sqrt(DistSq(x, y, px, py));
                double dBody = Math.Sqrt(DistSq(x, y, v._state.positionX, v._state.positionY));
                double d = Math.Min(dDest, dBody);
                if (d < best)
                    best = d;
            }

            return best;
        }
    }
}
