using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;
using Axiom.Math;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>Shared combat helpers for Eol defense bots. Core weapon/armor plumbing lives in BotWeaponUtil / BotLoadout.</summary>
    internal static class EolBotCombatUtil
    {
        /// <summary>Min time between non-forced Approach requeues while chasing without LOS.</summary>
        public const int CombatPathRequeueMinMs = 2800;

        /// <summary>
        /// How long LOS may stay broken with no usable route before a bot abandons the chase and
        /// falls back to patrol/anchor instead of grinding into a wall toward the target.
        /// </summary>
        public const int LosLostAbandonMs = 10000;

        /// <summary>How long LOS must stay broken before the first force-repath.</summary>
        public const int LosLostRepathArmMs = 600;

        /// <summary>
        /// Base cadence for force-repath while LOS stays broken.
        /// Prefer riding an existing route + local steer; only repath when the goal drifts or interval elapses.
        /// </summary>
        public const int LosLostRepathIntervalMs = 2800;

        /// <summary>
        /// Raw-pixel squared distance the chase goal must move before we invalidate the current route.
        /// (~512 raw ≈ 32 tiles)
        /// </summary>
        public const long CombatGoalDriftRawSq = 512L * 512L;

        /// <summary>Raw units the target must close before we cancel a dumpster backpedal.</summary>
        public const double PlayerClosingDistDeltaRaw = 10.0;

        sealed class LosRepathState
        {
            public int TickLost;
            public int TickLastForceRepath;
            public short LastGoalX;
            public short LastGoalY;
            public bool HasGoal;
        }

        static readonly Dictionary<int, LosRepathState> LosRepathByBot = new Dictionary<int, LosRepathState>();
        static readonly Dictionary<int, double> LastEngageDistByBot = new Dictionary<int, double>();

        sealed class CombatMotionState
        {
            public int TickLastDrive;
            public EngageMoveKind LastMove;
            public float LastForward;
            public float LastStrafe;
            public float LastRotate;
        }

        static readonly Dictionary<int, CombatMotionState> CombatMotionByBot = new Dictionary<int, CombatMotionState>();
        static readonly Dictionary<int, int> AlienPlasmaLastShotByBot = new Dictionary<int, int>();

        /// <summary>Fallback min ms between alien plasma shots when item fireDelay is shorter.</summary>
        public const int AlienPlasmaMinIntervalMs = 5000;

        /// <summary>Combat range at or above this is treated as plasma-cannon band for aliens.</summary>
        public const int AlienPlasmaRangeRaw = 420;

        /// <summary>How long each in-fight personality slot holds before rotating (staggered per bot).</summary>
        public const int CombatStylePhaseMs = 4200;

        /// <summary>Weapon hit-range scale (100 = full item range — bots press in and open up sooner).</summary>
        public const int EolBotFireRangePercent = 100;

        /// <summary>Extra raw units beyond scaled weapon range where shots may still land.</summary>
        public const int CombatFireEnvelopeSlackRaw = 90;

        /// <summary>Tile LOS required before any bot fires at a target.</summary>
        public static bool HasWeaponLineOfSight(Arena arena, Helpers.ObjectState from, Helpers.ObjectState to)
        {
            if (arena == null || from == null || to == null)
                return false;
            return EolBotTargetUtil.TileLineClearForWeaponFire(
                arena, from.positionX, from.positionY, to.positionX, to.positionY);
        }

        public static void ClearLosRepathState(int botId)
        {
            LosRepathByBot.Remove(botId);
        }

        /// <summary>
        /// Drop all per-bot combat scratch state. Call on death/despawn so static
        /// dictionaries do not accumulate across bot id reuse and uptime.
        /// </summary>
        public static void ForgetBot(int botId)
        {
            LosRepathByBot.Remove(botId);
            LastEngageDistByBot.Remove(botId);
            CombatMotionByBot.Remove(botId);
            AlienPlasmaLastShotByBot.Remove(botId);
        }

        /// <summary>
        /// True when this poll's distance is meaningfully shorter than the last engage sample
        /// (player stepping into the bot).
        /// </summary>
        public static bool IsTargetClosing(int botId, double distRaw)
        {
            double last;
            if (!LastEngageDistByBot.TryGetValue(botId, out last))
            {
                LastEngageDistByBot[botId] = distRaw;
                return false;
            }

            bool closing = distRaw < last - PlayerClosingDistDeltaRaw;
            LastEngageDistByBot[botId] = distRaw;
            return closing;
        }

        /// <summary>
        /// Chase a target after LOS is lost. Prefers local wall-slide steering and riding an
        /// existing route; only force-repaths when the goal drifted or the interval elapsed.
        /// Defers when the pathfinder queue is hot so packs do not stampede the shared pather.
        /// </summary>
        /// <returns><c>true</c> while the chase is still viable; <c>false</c> to abandon it.</returns>
        public static bool ChaseTargetWithoutLos(
            Bot bot,
            int now,
            Helpers.ObjectState dest,
            Func<InfantryVehicle, Vector3> pursueFallback)
        {
            if (bot == null || dest == null)
                return false;

            int botId = bot._id;
            LosRepathState st;
            if (!LosRepathByBot.TryGetValue(botId, out st) || st == null)
            {
                st = new LosRepathState { TickLost = now };
                LosRepathByBot[botId] = st;
            }
            else if (st.TickLost == 0)
                st.TickLost = now;

            bool losClear = bot._arena != null && bot._state != null
                && HasWeaponLineOfSight(bot._arena, bot._state, dest);

            // Re-sight resets the abandon / repath-arm clock so brief cover does not burn the timer.
            if (losClear)
                st.TickLost = now;
            else if (st.TickLost == 0)
                st.TickLost = now;

            // Only seek straight at the target when we can actually see it. When LOS is blocked,
            // hop/last-clear seek keeps them moving without a full mesh solve every tick.
            Func<InfantryVehicle, Vector3> fallback = (losClear && pursueFallback != null)
                ? pursueFallback
                : EolHqPatrol.MakeLineClearPendingSeek(bot, () => dest);

            bool hasPath = bot.MovementMode == BotMovementMode.Api && bot.Navigation.HasPath;
            bool goalDrifted = !st.HasGoal;
            if (st.HasGoal)
            {
                long dx = (long)dest.positionX - st.LastGoalX;
                long dy = (long)dest.positionY - st.LastGoalY;
                goalDrifted = dx * dx + dy * dy >= CombatGoalDriftRawSq;
            }

            int repathInterval = EolBotPathBudget.CombatLosRepathIntervalMs(bot._arena, botId);
            bool force = false;
            if (now - st.TickLost >= LosLostRepathArmMs)
            {
                bool intervalOk = st.TickLastForceRepath == 0 || now - st.TickLastForceRepath >= repathInterval;
                // Repath only when we have nothing to follow, or the live goal left the old route.
                if (intervalOk && (!hasPath || goalDrifted))
                {
                    force = true;
                    st.TickLastForceRepath = now;
                    st.LastGoalX = dest.positionX;
                    st.LastGoalY = dest.positionY;
                    st.HasGoal = true;
                }
            }

            if (force && !EolBotPathBudget.AllowCombatPath(bot._arena))
                force = false;

            if (bot.MovementMode == BotMovementMode.Api)
            {
                // LOS gone too long and we still have no route — abandon to patrol/anchor.
                if (!losClear && !hasPath && now - st.TickLost >= LosLostAbandonMs)
                {
                    if (bot._movement is SteeringController abandonSteer)
                        ClearCombatAim(bot, abandonSteer);
                    return false;
                }

                // Have a usable route and goal has not drifted — ride it; local fallback covers pending gaps.
                if (hasPath && !force)
                {
                    bot.ApplyNavigationApproach(dest, fallback, new BotNavigateOptions
                    {
                        RequeueMinMs = CombatPathRequeueMinMs,
                        PathStartIndex = 0,
                        ForceRequeue = false,
                        Priority = PathPriority.Combat,
                    });
                    return true;
                }

                // Queue saturated and no route yet — local steer only; retry path later.
                if (!force && !hasPath && !EolBotPathBudget.AllowCombatPath(bot._arena))
                {
                    bot.ApplySteer(fallback);
                    return true;
                }

                bot.ApplyNavigationApproach(dest, fallback, new BotNavigateOptions
                {
                    RequeueMinMs = CombatPathRequeueMinMs,
                    PathStartIndex = 0,
                    ForceRequeue = force,
                    Priority = PathPriority.Combat,
                });
                return true;
            }

            if (bot._movement is SteeringController steering && fallback != null)
                steering.steerDelegate = fallback;
            return true;
        }

        public static void RequestPathTo(
            Bot bot,
            int now,
            List<Vector3> existingPath,
            int pathTarget,
            int tickLastPath,
            Helpers.ObjectState dest,
            int requeueMinMs,
            Action<EolBotPathState> setPathState)
        {
            if (bot == null || bot._arena == null || dest == null || bot._arena._pathfinder == null || setPathState == null)
                return;
            if (existingPath != null && existingPath.Count > 0)
                return;
            if (tickLastPath == int.MaxValue)
                return;
            if (tickLastPath != 0 && now - tickLastPath < requeueMinMs)
                return;
            if (!EolBotPathBudget.AllowCombatPath(bot._arena))
                return;

            EolBotPathState pending = new EolBotPathState();
            pending.TickLastPath = int.MaxValue;
            setPathState(pending);

            bot._arena.QueueBotPathRequest(
                bot,
                (short)(bot._state.positionX / 16), (short)(bot._state.positionY / 16),
                (short)(dest.positionX / 16), (short)(dest.positionY / 16),
                delegate (List<Vector3> newPath, int pathLength)
                {
                    EolBotPathState result = new EolBotPathState();
                    result.TickLastPath = now;
                    result.AssignPath = true;
                    if (newPath != null)
                    {
                        result.Path = newPath;
                        result.PathTarget = 0;
                    }

                    setPathState(result);
                },
                PathPriority.Combat);
        }

        /// <summary>Queue a path around walls if needed; keep moving via fallback until the route arrives.</summary>
        public static void ApproachBlockedTarget(
            Bot bot,
            int now,
            List<Vector3> path,
            int pathTarget,
            int tickLastPath,
            Helpers.ObjectState dest,
            SteeringController steering,
            Func<InfantryVehicle, Vector3> steerAlongPath,
            Func<InfantryVehicle, Vector3> pursueFallback,
            Action<EolBotPathState> setPathState)
        {
            if (bot != null && bot.MovementMode == BotMovementMode.Api)
            {
                bool canQueue = EolBotPathBudget.AllowCombatPath(bot._arena);
                if (!canQueue && !bot.Navigation.HasPath)
                {
                    if (pursueFallback != null)
                        bot.ApplySteer(pursueFallback);
                    return;
                }

                bot.ApplyNavigationApproach(dest, pursueFallback, new BotNavigateOptions
                {
                    RequeueMinMs = CombatPathRequeueMinMs,
                    PathStartIndex = 0,
                    Priority = PathPriority.Combat,
                });
                return;
            }

            if (path != null && path.Count > 0 && steerAlongPath != null)
            {
                steering.steerDelegate = steerAlongPath;
                return;
            }

            RequestPathTo(bot, now, path, pathTarget, tickLastPath, dest, CombatPathRequeueMinMs, setPathState);
            steering.steerDelegate = pursueFallback;
        }

        /// <summary>Follow a pathfinder route; safe against null vehicle, empty path, and async path clears.</summary>
        public static Vector3 SteerAlongPath(
            InfantryVehicle vehicle,
            ref List<Vector3> path,
            ref int pathTarget,
            ref int tickLastPath)
        {
            if (vehicle == null)
                return Vector3.Zero;

            List<Vector3> route = path;
            if (route == null || route.Count == 0)
                return Vector3.Zero;

            int target = pathTarget;
            if (target < 0 || target >= route.Count)
            {
                path = null;
                pathTarget = 0;
                tickLastPath = 0;
                return Vector3.Zero;
            }

            Vector3 point = route[target];

            if (vehicle.Position.Distance(point) < 1.2f)
            {
                target++;
                pathTarget = target;
                if (target >= route.Count)
                {
                    path = null;
                    pathTarget = 0;
                    tickLastPath = 0;
                    return Vector3.Zero;
                }

                point = route[target];
            }

            return vehicle.SteerForSeek(point);
        }

        /// <summary>Follow an existing roam/combat path without queueing a new one.</summary>
        public static void FollowPathIfAny(
            SteeringController steering,
            List<Vector3> path,
            Func<InfantryVehicle, Vector3> steerAlongPath,
            Func<InfantryVehicle, Vector3> directFallback = null)
        {
            if (steering == null)
                return;
            if (path != null && path.Count > 0 && steerAlongPath != null)
                steering.steerDelegate = steerAlongPath;
            else if (directFallback != null)
                steering.steerDelegate = directFallback;
        }

        /// <summary>Long-range pursue; inside <paramref name="farDist"/> use standoff/strafe steering.</summary>
        public static void SetRaiderCombatSteerBand(
            SteeringController steering,
            double distance,
            float farDist,
            Func<InfantryVehicle, Vector3> pursueFar,
            Func<InfantryVehicle, Vector3> standoffClose)
        {
            if (steering == null)
                return;
            if (distance > farDist)
                steering.steerDelegate = pursueFar;
            else
                steering.steerDelegate = standoffClose;
        }

        /// <summary>
        /// When true, bots only use inventory slot 0 (short / close). Far weapon and swap are disabled.
        /// </summary>
        public const bool ShortWeaponOnly = false;

        /// <summary>
        /// Vehicle inventory: slot 0 = short/close, slot 1 = far (ignored when <see cref="ShortWeaponOnly"/>).
        /// </summary>
        public static void ResolveWeaponItems(VehInfo.Car type, out ItemInfo itemClose, out ItemInfo itemFar)
        {
            ResolveWeaponItems(type, out itemClose, out itemFar, includeFarSlot: !ShortWeaponOnly);
        }

        /// <summary>
        /// Vehicle inventory: slot 0 = short/close, slot 1 = far (ignored when ShortWeaponOnly unless includeFarSlot).
        /// </summary>
        public static void ResolveWeaponItems(VehInfo.Car type, out ItemInfo itemClose, out ItemInfo itemFar, bool includeFarSlot)
        {
            BotWeaponUtil.ResolveWeaponItems(type, out itemClose, out itemFar, includeFarSlot);
        }

        /// <summary>Equip short/close (slot 0). Far (slot 1) only when <see cref="ShortWeaponOnly"/> is false.</summary>
        public static void EquipBotWeapons(VehInfo.Car type, WeaponController weaponClose, WeaponController weaponFar)
        {
            BotWeaponUtil.EquipFromVehicleInventory(type, weaponClose, weaponFar, includeFarSlot: !ShortWeaponOnly);
        }

        /// <summary>
        /// Wire close/far controllers into the live AttackPlayer fire path and widen seek range.
        /// Refreshes server-side utility armor via <see cref="Bot.RefreshUtilityEffects"/>.
        /// </summary>
        public static void WireLiveCombatWeapons(
            Bot bot,
            WeaponController weaponClose,
            WeaponController weaponFar,
            int seekRangeRaw = Script_Eol.EolBotSelfAggroRange)
        {
            BotWeaponUtil.WireLiveCombatWeapons(
                bot, weaponClose, weaponFar,
                includeFarWeapon: !ShortWeaponOnly,
                seekRangeRaw: seekRangeRaw > 0 ? seekRangeRaw : Script_Eol.EolBotSelfAggroRange);
        }

        /// <summary>Network packet broadcast range — not combat hit range. Prefer <see cref="GetCombatRangeRaw"/>.</summary>
        public static int GetRouteRangeTicks(ItemInfo item)
        {
            return BotWeaponUtil.GetRouteRangeTicks(item);
        }

        /// <summary>Melee / bite / stomp band (raw). Below this → fight on top of the target.</summary>
        public const int MeleeCombatRangeRaw = 140;

        /// <summary>
        /// Approximate hit range in raw map units from projectile travel + damage radius.
        /// Do not use item routeRange here — that is client update broadcast distance.
        /// </summary>
        public static int GetCombatRangeRaw(ItemInfo item)
        {
            return BotWeaponUtil.GetCombatRangeRaw(item);
        }

        public static int MaxWeaponRangeTicks(ItemInfo primary, ItemInfo secondary)
        {
            return BotWeaponUtil.MaxWeaponRangeRaw(primary, secondary);
        }

        /// <summary>Weapon-derived engage distances (raw map units — same as Helpers.distanceTo).</summary>
        public struct WeaponEngageBands
        {
            /// <summary>Slot 0 close weapon hit range.</summary>
            public int CloseWeaponRaw;
            /// <summary>Slot 1 far weapon hit range.</summary>
            public int FarWeaponRaw;
            /// <summary>Hard hug — only here force dumpster backpedal.</summary>
            public int MinStandoffRaw;
            /// <summary>Ideal stand (melee on top; guns ~2 tiles).</summary>
            public int PreferCloseRaw;
            /// <summary>Legacy mid marker — now equals close max (no mid hold).</summary>
            public int PreferFarRaw;
            /// <summary>Outside this → chase into envelope.</summary>
            public int ApproachBeyondRaw;
            /// <summary>Inside this (and not hugging) may soft-kick; otherwise stay aggressive.</summary>
            public int BackpedalInsideRaw;
            /// <summary>True when close weapon is bite/stomp/melee length.</summary>
            public bool CloseIsMelee;
        }

        public enum EngageMoveKind
        {
            Approach,
            BackpedalHard,
            BackpedalSoft,
            /// <summary>Wide-angle close-in before the fight starts.</summary>
            FlankApproach,
            /// <summary>In the player's face with heavy strafe weave.</summary>
            FaceStrafe,
            /// <summary>Tight circle-strafe around the player.</summary>
            CircleStrafe,
            PressIn,
            RushFace,
            FlankStrafe,
            HoldOrbit,
        }

        /// <summary>
        /// Slot 0 = close, slot 1 = far. Uses real hit range (not network routeRange).
        /// Melee/bite/stomp → stand on the player; guns → close − 30%.
        /// Mid-range: brief far-weapon poke with LOS, then close again.
        /// </summary>
        public static WeaponEngageBands BuildWeaponEngageBands(ItemInfo itemFar, ItemInfo itemClose)
        {
            int closeRaw = ScaleBotFireRange(GetCombatRangeRaw(itemClose));
            if (closeRaw <= 0)
                closeRaw = ScaleBotFireRange(90);
            int farRaw = ShortWeaponOnly ? closeRaw : ScaleBotFireRange(GetCombatRangeRaw(itemFar));
            if (farRaw <= 0)
                farRaw = ShortWeaponOnly ? closeRaw : ScaleBotFireRange(1100);
            if (farRaw < closeRaw)
                farRaw = closeRaw;

            bool closeIsMelee = closeRaw <= MeleeCombatRangeRaw;
            int minStandoff = closeIsMelee ? 6 : 8;
            // Movement no longer uses PreferClose as a standoff ring — only aim bands reference it.
            int preferClose = closeIsMelee
                ? Math.Max(14, Math.Min(24, closeRaw))
                : Math.Min(32, Math.Max(20, closeRaw / 24));
            int preferFar = closeRaw;
            int approachBeyond = farRaw + CombatFireEnvelopeSlackRaw;
            int backpedalInside = minStandoff;

            return new WeaponEngageBands
            {
                CloseWeaponRaw = closeRaw,
                FarWeaponRaw = farRaw,
                MinStandoffRaw = minStandoff,
                PreferCloseRaw = preferClose,
                PreferFarRaw = preferFar,
                ApproachBeyondRaw = approachBeyond,
                BackpedalInsideRaw = backpedalInside,
                CloseIsMelee = closeIsMelee,
            };
        }

        /// <summary>In-fight personality — face/circle/flank/orbit while engaged.</summary>
        public enum CombatStyle
        {
            FaceStrafe,
            CircleStrafe,
            SoftKick,
            RushFace,
            FlankStrafe,
            OrbitMid,
        }

        /// <summary>
        /// Rotate combat personalities on a slow cadence (staggered per bot) so packs do not
        /// all swap strafe patterns on the same tick.
        /// </summary>
        public static CombatStyle PickCombatStyle(int botId, int now, int health, int softKickHealth)
        {
            if (health > 0 && health <= softKickHealth)
            {
                int lowPhase = ((now / CombatStylePhaseMs) + botId) & 1;
                return lowPhase == 0 ? CombatStyle.RushFace : CombatStyle.FaceStrafe;
            }

            int phase = ((now / CombatStylePhaseMs) + (botId * 3)) % 4;
            switch (phase)
            {
                case 0:
                    return CombatStyle.RushFace;
                case 1:
                    return CombatStyle.FaceStrafe;
                case 2:
                    return CombatStyle.RushFace;
                default:
                    return CombatStyle.FlankStrafe;
            }
        }

        static int ScaleBotFireRange(int raw)
        {
            if (raw <= 0)
                return raw;
            return Math.Max(1, raw * EolBotFireRangePercent / 100);
        }

        static CombatStyle NormalizeStyle(CombatStyle style)
        {
            switch (style)
            {
                case CombatStyle.SoftKick:
                    // Deprecated mid-range backpedal spring — treat as aggressive face fight.
                    return CombatStyle.FaceStrafe;
                default:
                    return style;
            }
        }

        static EngageMoveKind StyleToEngageMove(CombatStyle style)
        {
            switch (NormalizeStyle(style))
            {
                case CombatStyle.CircleStrafe:
                    return EngageMoveKind.CircleStrafe;
                case CombatStyle.FlankStrafe:
                    return EngageMoveKind.FlankStrafe;
                case CombatStyle.OrbitMid:
                    return EngageMoveKind.RushFace;
                case CombatStyle.RushFace:
                    return EngageMoveKind.RushFace;
                default:
                    return EngageMoveKind.FaceStrafe;
            }
        }

        /// <summary>At or below this % of Hitpoints, bots suicide-rush instead of peeling.</summary>
        public const int DyingRushHealthPercent = 20;

        /// <summary>
        /// Infantry yaw units (0–240 circle) of aim leeway while strafing.
        /// Tighter than the old 72-wide spray window; distance bands add a little slack at range.
        /// </summary>
        public const int CombatAimFuzziness = 21;

        public const int CombatAimFuzzinessClose = 17;
        public const int CombatAimFuzzinessMid = 21;
        public const int CombatAimFuzzinessFar = 26;

        /// <summary>Base defense bots — tighter gate so shots land while HQ-patrol strafe is active.</summary>
        public const int DefenseCombatAimFuzzinessClose = 11;
        public const int DefenseCombatAimFuzzinessMid = 13;
        public const int DefenseCombatAimFuzzinessFar = 16;

        static bool IsDefenseBaseBot(Bot bot)
        {
            return bot is BasicDefense || bot is Captain;
        }

        public static int GetCombatAimFuzziness(double distRaw, WeaponEngageBands bands)
        {
            return GetCombatAimFuzziness(null, distRaw, bands);
        }

        public static int GetCombatAimFuzziness(Bot bot, double distRaw, WeaponEngageBands bands)
        {
            bool defense = IsDefenseBaseBot(bot);
            if (defense)
            {
                if (distRaw <= bands.PreferCloseRaw + 40)
                    return DefenseCombatAimFuzzinessClose;
                if (distRaw <= bands.FarWeaponRaw)
                    return DefenseCombatAimFuzzinessMid;
                return DefenseCombatAimFuzzinessFar;
            }

            if (distRaw <= bands.PreferCloseRaw + 40)
                return CombatAimFuzzinessClose;
            if (distRaw <= bands.FarWeaponRaw)
                return CombatAimFuzzinessMid;
            return CombatAimFuzzinessFar;
        }

        public static bool IsBelowHealthPercent(int health, int maxHealth, int percent = DyingRushHealthPercent)
        {
            if (health <= 0)
                return false;
            if (maxHealth <= 0)
                return health <= 12;
            return health * 100 <= maxHealth * percent;
        }

        /// <summary>Backpedal removed — always false. Kept for call-site compatibility.</summary>
        public static bool ShouldBackpedalEngage(double distRaw, WeaponEngageBands bands, int health, int maxHealth)
        {
            return false;
        }

        /// <summary>True when yaw is close enough to fire — independent of strafe/plant rhythm.</summary>
        public static bool IsCombatAimed(int yaw, int aimAngle, int fuzziness = CombatAimFuzziness)
        {
            int angleDiffHi = aimAngle - yaw;
            int angleDiffLo = (angleDiffHi < 0) ? 240 - Math.Abs(angleDiffHi) : -(240 - Math.Abs(angleDiffHi));
            int angleDiff = Math.Abs(angleDiffHi) < Math.Abs(angleDiffLo) ? angleDiffHi : angleDiffLo;
            int leeway = Math.Max(1, fuzziness / 2);
            return Math.Abs(angleDiff) <= leeway;
        }

        /// <summary>Fire when ammo/cooldown allow and aim is on target (tighter than legacy spray).</summary>
        public static bool ShouldCombatFire(WeaponController weapon, Bot bot, int aimResult)
        {
            return ShouldCombatFire(weapon, bot, aimResult, -1, null);
        }

        public static bool ShouldCombatFire(
            WeaponController weapon,
            Bot bot,
            int aimResult,
            double distRaw,
            WeaponEngageBands? bands)
        {
            if (weapon == null || bot?._state == null || !weapon.ableToFire())
                return false;

            int fuzz = CombatAimFuzziness;
            if (distRaw >= 0 && bands.HasValue)
                fuzz = GetCombatAimFuzziness(bot, distRaw, bands.Value);
            return IsCombatAimed(bot._state.yaw, aimResult, fuzz);
        }

        public static EngageMoveKind ClassifyEngageMove(
            double distRaw,
            WeaponEngageBands bands,
            bool wantClosePressure,
            int health,
            int maxHealth)
        {
            CombatStyle style = wantClosePressure ? CombatStyle.FaceStrafe : CombatStyle.CircleStrafe;
            return ClassifyEngageMove(0, distRaw, bands, style, health, maxHealth);
        }

        public static EngageMoveKind ClassifyEngageMove(
            double distRaw,
            WeaponEngageBands bands,
            CombatStyle style,
            int health,
            int maxHealth)
        {
            return ClassifyEngageMove(0, distRaw, bands, style, health, maxHealth);
        }

        /// <summary>
        /// With clear LOS and already inside the weapon envelope, do not path-chase past the player —
        /// promote Approach into a direct press so yaw/aim stay on target immediately.
        /// </summary>
        public static EngageMoveKind PromoteApproachForDirectEngage(
            EngageMoveKind move,
            bool tileLineClear,
            double distRaw,
            int maxFireRaw)
        {
            if (move != EngageMoveKind.Approach)
                return move;
            if (!tileLineClear)
                return move;
            if (distRaw > maxFireRaw + CombatFireEnvelopeSlackRaw)
                return move;
            return EngageMoveKind.PressIn;
        }

        /// <summary>
        /// Outside weapon envelope → path/steer chase. Inside → close on target and fight (no standoff ring).
        /// </summary>
        public static EngageMoveKind ClassifyEngageMove(
            int botId,
            double distRaw,
            WeaponEngageBands bands,
            CombatStyle style,
            int health,
            int maxHealth)
        {
            int fireOuter = bands.FarWeaponRaw + CombatFireEnvelopeSlackRaw;
            if (distRaw > fireOuter)
                return EngageMoveKind.Approach;

            // In range — drive toward the target; style only tweaks strafe in ApplyStyleEngageDrive.
            return EngageMoveKind.RushFace;
        }

        public static EngageMoveKind ApplyLosPressure(
            EngageMoveKind move,
            bool hasLineOfSight,
            double distRaw,
            WeaponEngageBands bands)
        {
            if (hasLineOfSight)
                return move;

            // Lost angle before engagement: close the gap.
            if (distRaw > bands.FarWeaponRaw)
                return EngageMoveKind.Approach;

            // Already in weapon range but no lane: keep closing, do not orbit away.
            return EngageMoveKind.RushFace;
        }

        public static float PreferredEngageOpenSteer(WeaponEngageBands bands, bool wantClosePressure)
        {
            return PreferredEngageOpenSteer(bands,
                wantClosePressure ? CombatStyle.FaceStrafe : CombatStyle.CircleStrafe);
        }

        public static float PreferredEngageOpenSteer(WeaponEngageBands bands, CombatStyle style)
        {
            style = NormalizeStyle(style);
            int preferRaw = bands.PreferCloseRaw;
            // OpenSteer units = raw / 100. Knife-fight hug.
            float prefer = preferRaw / 100f;
            if (prefer < 0.10f)
                prefer = 0.10f;
            return prefer;
        }

        /// <summary>
        /// Shared engage movement for all Eol bots. In weapon range with LOS, always strafe-weave
        /// (never stand and shoot). Returns true when analog strafe drive was applied.
        /// </summary>
        public static bool ApplyEngageMoveSwitch(
            Bot bot,
            int now,
            Helpers.ObjectState targetState,
            EngageMoveKind move,
            float preferOpenSteer,
            bool tileLineClear,
            double distRaw,
            int maxFireRaw,
            Func<InfantryVehicle, Vector3> chaseFallback)
        {
            if (bot == null || targetState == null)
                return false;

            bool inFireEnvelope = tileLineClear && distRaw <= maxFireRaw + CombatFireEnvelopeSlackRaw;
            if (inFireEnvelope)
            {
                // In range — close on target while aiming; no fixed standoff distance.
                if (move == EngageMoveKind.Approach
                    || move == EngageMoveKind.BackpedalHard
                    || move == EngageMoveKind.BackpedalSoft
                    || move == EngageMoveKind.HoldOrbit
                    || move == EngageMoveKind.FlankApproach)
                    move = EngageMoveKind.RushFace;
            }

            switch (move)
            {
                case EngageMoveKind.Approach:
                    ChaseTargetWithoutLos(bot, now, targetState, chaseFallback);
                    return false;

                case EngageMoveKind.BackpedalHard:
                case EngageMoveKind.BackpedalSoft:
                    ApplyStyleEngageDrive(bot, EngageMoveKind.RushFace, targetState, preferOpenSteer);
                    return true;

                default:
                    ApplyStyleEngageDrive(bot, move, targetState, preferOpenSteer);
                    return true;
            }
        }

        /// <summary>Apply engaged movement. Approach / backpedal stay caller-side.</summary>
        public static void ApplyStyleEngageDrive(
            Bot bot,
            EngageMoveKind move,
            Helpers.ObjectState targetState,
            float preferOpenSteer)
        {
            if (bot?._arena != null && bot._state != null && targetState != null)
            {
                bool hasLineOfSight = HasWeaponLineOfSight(bot._arena, bot._state, targetState);
                if (!hasLineOfSight)
                {
                    switch (move)
                    {
                        case EngageMoveKind.FlankApproach:
                        case EngageMoveKind.FaceStrafe:
                        case EngageMoveKind.CircleStrafe:
                        case EngageMoveKind.RushFace:
                        case EngageMoveKind.FlankStrafe:
                        case EngageMoveKind.HoldOrbit:
                            move = EngageMoveKind.RushFace;
                            break;
                    }
                }
                // Strafe and shoot together — do not plant/freeze the weave to dump a burst.
            }

            float forwardScale = 1f;
            float strafeScale = 1f;
            if (IsDefenseBaseBot(bot))
            {
                forwardScale = 0.90f;
                strafeScale = 0.85f;
            }

            switch (move)
            {
                case EngageMoveKind.FlankApproach:
                case EngageMoveKind.FaceStrafe:
                case EngageMoveKind.RushFace:
                case EngageMoveKind.CircleStrafe:
                case EngageMoveKind.FlankStrafe:
                case EngageMoveKind.PressIn:
                case EngageMoveKind.HoldOrbit:
                default:
                    // Strong strafe bias — bots dance while shooting, not only charge.
                    DriveTowardCombatTarget(bot, targetState, move, 1.0f * forwardScale, 0.85f * strafeScale);
                    break;
            }
        }

        /// <summary>
        /// Knife-fight engage: press in to a tiny stand-off, then weave/strafe while firing.
        /// Never uses Cos(headingError) reverse (that looked like a mid-range tether).
        /// </summary>
        static void DriveTowardCombatTarget(
            Bot bot,
            Helpers.ObjectState targetState,
            EngageMoveKind move,
            float forwardStrength,
            float strafeStrength)
        {
            if (bot == null || targetState == null || bot._movement is not SteeringController)
                return;

            Vector3 myPos = bot._state.position();
            Vector3 targetPos = targetState.position();
            float len = (myPos - targetPos).Length;

            float desiredHeading = (float)Helpers.calculateDegreesBetweenPoints(
                bot._state.positionX, bot._state.positionY, targetState.positionX, targetState.positionY);
            double headingError = Helpers.calculateDifferenceInAngles(bot._state.yaw, desiredHeading);
            double headingRad = headingError * (Math.PI / 120.0);

            // Irregular phase so packs don't flip strafe in lockstep.
            int phaseMs = 520 + Math.Abs(bot._id % 7) * 95;
            int motionPhase = ((Environment.TickCount / phaseMs) + bot._id * 5) & 7;
            float sign = ((motionPhase + bot._id) & 1) == 0 ? 1f : -1f;
            // Occasional hard cut / pause in the weave.
            float weaveAmp = (motionPhase == 1 || motionPhase == 5) ? 1.15f
                : ((motionPhase == 3) ? 0.55f : 0.90f);

            // Tiny stand-off (~18–22 raw). Outside → charge; inside → strafe dance.
            const float hugPrefer = 0.20f;
            const float hugOverlap = 0.08f;

            float fwd;
            if (len > hugPrefer + 0.12f)
            {
                // Closing hard from outside knife range.
                fwd = Math.Min(1f, Math.Max(0.70f, forwardStrength + 0.30f));
            }
            else if (len > hugPrefer)
            {
                // Soft settle into the bubble — still slight press, mostly weave.
                fwd = Math.Max(0.12f, forwardStrength * 0.22f);
            }
            else if (len > hugOverlap)
            {
                // In the bubble: hold distance with strafe only (no charge, no tether reverse).
                fwd = (motionPhase == 0 || motionPhase == 4) ? 0.10f : 0f;
            }
            else
            {
                // Body-stacked: brief peel so they don't glue together.
                fwd = -0.28f;
            }

            float forward = BotDrive.ClampAxis(fwd);
            float strafe = BotDrive.ClampAxis(
                (float)(Math.Sin(headingRad) * 0.25f)
                + strafeStrength * sign * weaveAmp);
            float rotate = BotDrive.ClampAxis((float)(headingError / 36.0));
            if (IsDefenseBaseBot(bot))
                rotate *= 0.35f;

            TrySetCombatDrive(bot, move, forward, strafe, rotate);
        }

        /// <summary>
        /// Build a moving pass-by point beside the target. Unlike an orbit point, this never
        /// contains an outward radial component, so a healthy bot does not spring backward
        /// merely because the target stepped toward it.
        /// </summary>
        static Vector3 CombatPassPoint(
            Bot bot,
            Vector3 myPos,
            Vector3 targetPos,
            float preferredOpenSteer,
            float flankBias,
            out float distance)
        {
            Vector3 away = myPos - targetPos;
            distance = away.Length;

            int id = bot != null ? bot._id : 0;
            int phaseMs = 900 + Math.Abs(id % 5) * 170;
            int phase = ((Environment.TickCount / phaseMs) + id * 3) & 7;
            float sign = ((phase + id) & 1) == 0 ? 1f : -1f;

            if (distance < 0.08f)
            {
                Vector3 fallbackTangent = (id & 1) == 0
                    ? new Vector3(0, 1, 0)
                    : new Vector3(0, -1, 0);
                return targetPos + fallbackTangent * 0.28f;
            }

            away *= 1f / distance;
            Vector3 tangent = new Vector3(-away.y, away.x, 0) * sign;

            // Some phases rush almost through center; others make a wider lateral pass.
            float pattern;
            switch (phase)
            {
                case 0:
                case 4:
                    pattern = 0.12f;
                    break;
                case 1:
                case 6:
                    pattern = 0.48f;
                    break;
                case 2:
                case 5:
                    pattern = 0.38f;
                    break;
                default:
                    pattern = 0.30f;
                    break;
            }

            float baseOffset = Math.Max(0.08f, Math.Min(0.36f, preferredOpenSteer));
            float passOffset = Math.Max(0.06f, baseOffset * Math.Max(0.14f, flankBias) * pattern);

            // At range, reduce the side offset so the bot commits toward combat first.
            if (distance > Math.Max(1.2f, preferredOpenSteer * 2.0f))
                passOffset *= 0.45f;

            return targetPos + tangent * passOffset;
        }

        /// <summary>Legacy steering helper — now direct pursuit toward the target (no standoff ring).</summary>
        public static Vector3 SteerCombatPass(
            Bot bot,
            InfantryVehicle vehicle,
            Helpers.ObjectState targetState,
            float preferredOpenSteer,
            float flankBias = 0.70f)
        {
            if (vehicle == null || targetState == null)
                return Vector3.Zero;
            return vehicle.SteerForSeek(targetState.position());
        }

        /// <summary>
        /// Strafe through and around the target without holding a fixed range
        /// (OpenSteer units = raw/100).
        /// </summary>
        public static void DriveOrbitAtPreferredRange(
            Bot bot,
            Helpers.ObjectState targetState,
            float preferOpenSteer,
            float forwardStrength = 0.58f,
            float strafeStrength = 0.40f)
        {
            DriveOrbitAtPreferredRange(bot, targetState, EngageMoveKind.HoldOrbit, preferOpenSteer,
                forwardStrength, strafeStrength, 0.38f, true);
        }

        public static void DriveOrbitAtPreferredRange(
            Bot bot,
            Helpers.ObjectState targetState,
            EngageMoveKind move,
            float preferOpenSteer,
            float forwardStrength,
            float strafeStrength,
            float flankBias,
            bool allowReverse)
        {
            if (bot == null || targetState == null || bot._movement is not SteeringController steering)
                return;

            if (preferOpenSteer < 0.10f)
                preferOpenSteer = 0.10f;
            if (flankBias < 0.08f)
                flankBias = 0.08f;

            Vector3 myPos = bot._state.position();
            Vector3 targetPos = targetState.position();
            float len;
            Vector3 passPoint = CombatPassPoint(
                bot, myPos, targetPos, preferOpenSteer, flankBias, out len);
            short orbitX = (short)Math.Round(passPoint.x * 100.0);
            short orbitY = (short)Math.Round(passPoint.y * 100.0);
            float desiredHeading = (float)Helpers.calculateDegreesBetweenPoints(
                bot._state.positionX, bot._state.positionY, orbitX, orbitY);
            double headingError = Helpers.calculateDifferenceInAngles(bot._state.yaw, desiredHeading);
            double headingRad = headingError * (Math.PI / 120.0);

            int motionPhase = ((Environment.TickCount / (900 + Math.Abs(bot._id % 5) * 170)) + bot._id * 3) & 7;
            float phaseStrength = (motionPhase == 0 || motionPhase == 4)
                ? 1.0f
                : ((motionPhase == 2 || motionPhase == 5) ? 0.72f : 0.86f);
            float sign = ((motionPhase + bot._id) & 1) == 0 ? 1f : -1f;
            // Legacy orbit helper — same no-reverse rule as DriveTowardCombatTarget.
            float fwd = Math.Min(1f, Math.Max(0.50f, forwardStrength * phaseStrength + 0.20f));
            if (len > preferOpenSteer + 0.40f)
                fwd = Math.Min(1f, fwd + 0.30f);

            float forward = BotDrive.ClampAxis(fwd);
            float strafe = BotDrive.ClampAxis(
                (float)(Math.Sin(headingRad) * 0.55f) + strafeStrength * sign * 0.85f);
            float rotate = BotDrive.ClampAxis((float)(headingError / 36.0));
            if (IsDefenseBaseBot(bot))
                rotate *= 0.35f;

            TrySetCombatDrive(bot, move, forward, strafe, rotate);
        }

        /// <summary>
        /// Rate-limits analog drive commits so many bots weaving at once do not spike broadcasts.
        /// </summary>
        static void TrySetCombatDrive(Bot bot, EngageMoveKind move, float forward, float strafe, float rotate)
        {
            if (bot == null)
                return;

            int now = Environment.TickCount;
            int botId = bot._id;
            CombatMotionState st;
            if (!CombatMotionByBot.TryGetValue(botId, out st) || st == null)
            {
                st = new CombatMotionState();
                CombatMotionByBot[botId] = st;
            }

            bool moveChanged = st.LastMove != move;
            bool intervalOk = st.TickLastDrive == 0
                || now - st.TickLastDrive >= EolBotActionEvalThrottle.CombatMotionCommitMinMs;
            bool vectorChanged = Math.Abs(forward - st.LastForward) > 0.12f
                || Math.Abs(strafe - st.LastStrafe) > 0.12f
                || Math.Abs(rotate - st.LastRotate) > 0.12f;

            if (moveChanged || intervalOk || vectorChanged)
            {
                st.LastMove = move;
                st.TickLastDrive = now;
                st.LastForward = forward;
                st.LastStrafe = strafe;
                st.LastRotate = rotate;
            }
            else
            {
                // Re-commit last weave so ApplyMotionRequest does not zero drive this poll.
                forward = st.LastForward;
                strafe = st.LastStrafe;
                rotate = st.LastRotate;
            }

            if (bot.MovementMode == BotMovementMode.Api)
            {
                bot.SetDrive(forward, strafe, rotate);
                return;
            }

            if (bot._movement is SteeringController steering)
            {
                BotWallAvoidance.AdjustDrive(bot, ref forward, ref strafe, ref rotate);
                BotDrive.SetDrive(steering, forward, strafe, rotate);
            }
        }

        /// <summary>
        /// Keep body yaw tracking the lead aim angle while engaged (Api + legacy steering).
        /// Must run every combat poll in fire range — not only when <c>ableToFire()</c> —
        /// otherwise bots weave off-target during weapon cooldown and look like they are "thinking".
        /// </summary>
        public static void KeepCombatAim(Bot bot, SteeringController steering, int aimAngle)
        {
            if (bot?._state != null)
                bot._state.fireAngle = (byte)aimAngle;

            if (bot != null && bot.MovementMode == BotMovementMode.Api)
            {
                bot.SetAim(aimAngle, true);
                if (IsDefenseBaseBot(bot))
                    LockCombatBodyToAim(bot, aimAngle);
                return;
            }

            if (steering != null)
            {
                steering.bSkipAim = true;
                steering.angle = aimAngle;
            }
        }

        /// <summary>
        /// After <see cref="KeepCombatAim"/>, override orbit-heading rotate so the body snaps to the shot line.
        /// </summary>
        public static void LockCombatBodyToAim(Bot bot, int aimAngle)
        {
            if (bot == null || bot.MovementMode != BotMovementMode.Api || bot._state == null)
                return;

            double aimError = Helpers.calculateDifferenceInAngles(bot._state.yaw, aimAngle);
            float aimRotate = BotDrive.ClampAxis((float)(aimError / 26.0));
            bot.Rotate(aimRotate);
        }

        /// <summary>Lead aim + packet sync + optional defense body lock.</summary>
        public static int SyncCombatAim(Bot bot, SteeringController steering, WeaponController weapon, Helpers.ObjectState targetState)
        {
            if (weapon == null || targetState == null)
                return 0;

            int aimResult = weapon.getAimAngle(targetState);
            KeepCombatAim(bot, steering, aimResult);
            return aimResult;
        }

        public static void ClearCombatAim(Bot bot, SteeringController steering)
        {
            if (bot != null && bot.MovementMode == BotMovementMode.Api)
            {
                bot.SetAim(0, false);
                return;
            }

            if (steering != null)
                steering.bSkipAim = false;
        }

        /// <summary>Brief far-gun poke window while closing (ms). Long enough for ~5+ shots at typical fire delays.</summary>
        public const int FarPokeWindowMs = 2200;
        /// <summary>Full poke→close cycle (ms). After poke, force close priority.</summary>
        public const int FarPokeCycleMs = 3600;

        /// <summary>Legacy plant window — disabled so strafing never blocks continuous magazine dumps.</summary>
        public const int FireCommitWindowMs = 0;
        public const int FireCommitCycleMs = 3800;

        public static bool InFireCommitWindow(Bot bot, int now)
        {
            return false;
        }

        /// <summary>
        /// Formerly planted to dump shots. Always false — fire while strafing / pressing instead.
        /// </summary>
        public static bool ShouldPlantForFireBurst(Bot bot, int now, bool hasLineOfSight, double distRaw, int maxFireRaw)
        {
            return false;
        }

        /// <summary>
        /// Near-stationary drive while aiming — prevents movementCancelsPrefire from eating the burst.
        /// Tiny residual strafe so they do not look completely frozen.
        /// </summary>
        public static void ApplyPlantedFireDrive(Bot bot, Helpers.ObjectState targetState)
        {
            if (bot == null || targetState == null || bot._movement is not SteeringController steering)
                return;

            float desiredHeading = (float)Helpers.calculateDegreesBetweenPoints(
                bot._state.positionX, bot._state.positionY,
                targetState.positionX, targetState.positionY);
            double headingError = Helpers.calculateDifferenceInAngles(bot._state.yaw, desiredHeading);
            float rotate = BotDrive.ClampAxis((float)(headingError / 36.0));
            int sign = ((bot._id + (Environment.TickCount / 700)) & 1) == 0 ? 1 : -1;
            float forward = 0.04f;
            float strafe = 0.10f * sign;

            if (bot.MovementMode == BotMovementMode.Api)
            {
                bot.SetDrive(forward, strafe, rotate);
            }
            else
            {
                BotWallAvoidance.AdjustDrive(bot, ref forward, ref strafe, ref rotate);
                BotDrive.SetDrive(steering, forward, strafe, rotate);
            }
        }

        /// <summary>After a shot — honor movementCancelsPrefire like players (brief plant).</summary>
        public static void OnCombatShotFired(Bot bot, WeaponController weapon)
        {
            if (bot == null || weapon == null)
                return;
            if (weapon.RequiresStationaryFire)
                bot.freezeMovement(weapon.StationaryFireLockMs);
        }

        /// <summary>Weapon fireDelay in ms (matches <see cref="WeaponController"/> internal tick conversion).</summary>
        static int GetWeaponFireDelayMs(WeaponController weapon)
        {
            if (weapon?.Projectile == null)
                return 0;
            return Math.Max(0, weapon.Projectile.fireDelay * 10);
        }

        /// <summary>True when this item behaves like an alien plasma cannon (long-range slot-1 gun).</summary>
        public static bool IsAlienPlasmaLike(ItemInfo item, WeaponController weaponFar, WeaponController weapon)
        {
            if (weapon == null || item == null)
                return false;
            if (weaponFar != null && weapon == weaponFar)
                return true;
            return GetCombatRangeRaw(item) >= AlienPlasmaRangeRaw;
        }

        /// <summary>
        /// Alien plasma: honor weapon fireDelay via ableToFire(), plus at least
        /// <see cref="AlienPlasmaMinIntervalMs"/> between plasma-class shots.
        /// </summary>
        public static bool ShouldAlienCombatFire(
            Bot bot,
            int now,
            WeaponController weapon,
            WeaponController weaponFar,
            ItemInfo weaponItem,
            int aimResult,
            double distRaw,
            WeaponEngageBands bands)
        {
            if (!ShouldCombatFire(weapon, bot, aimResult, distRaw, bands))
                return false;

            if (!IsAlienPlasmaLike(weaponItem, weaponFar, weapon))
                return true;

            int minMs = Math.Max(AlienPlasmaMinIntervalMs, GetWeaponFireDelayMs(weapon));
            int last;
            if (AlienPlasmaLastShotByBot.TryGetValue(bot._id, out last) && now - last < minMs)
                return false;

            return true;
        }

        public static void OnAlienCombatShotFired(
            Bot bot,
            int now,
            WeaponController weapon,
            WeaponController weaponFar,
            ItemInfo weaponItem)
        {
            OnCombatShotFired(bot, weapon);
            if (IsAlienPlasmaLike(weaponItem, weaponFar, weapon))
                AlienPlasmaLastShotByBot[bot._id] = now;
        }

        /// <summary>
        /// Slot 0 = short/close. Far/poke disabled when <see cref="ShortWeaponOnly"/>.
        /// </summary>
        public static WeaponController SelectWeaponForDistance(
            Bot bot,
            int now,
            WeaponController weaponClose,
            WeaponController weaponFar,
            ItemInfo itemFar,
            ItemInfo itemClose,
            double distanceRaw,
            bool hasLineOfSight)
        {
            return SelectWeaponForDistance(bot, now, weaponClose, weaponFar, itemFar, itemClose,
                distanceRaw, hasLineOfSight, allowFarWeapon: !ShortWeaponOnly);
        }

        /// <summary>
        /// Slot 0 = short/close. Far disabled when <see cref="ShortWeaponOnly"/> unless
        /// <paramref name="allowFarWeapon"/> is true (alien horde plasma).
        /// Inside close range prefer close; outside use far. When both can reach, swap to
        /// whichever item is ready (fireDelay / reload via ableToFire).
        /// </summary>
        public static WeaponController SelectWeaponForDistance(
            Bot bot,
            int now,
            WeaponController weaponClose,
            WeaponController weaponFar,
            ItemInfo itemFar,
            ItemInfo itemClose,
            double distanceRaw,
            bool hasLineOfSight,
            bool allowFarWeapon)
        {
            if (ShortWeaponOnly && !allowFarWeapon)
                return weaponClose ?? weaponFar;

            if (weaponClose == null || itemClose == null)
                return weaponFar;

            int closeR = ScaleBotFireRange(GetCombatRangeRaw(itemClose));
            if (closeR <= 0)
                closeR = ScaleBotFireRange(90);
            int farR = ScaleBotFireRange(GetCombatRangeRaw(itemFar));
            if (farR <= 0)
                farR = ScaleBotFireRange(1100);
            if (farR < closeR)
                farR = closeR;

            return PickNearFarByRangeAndReady(
                weaponClose, weaponFar, closeR, farR, distanceRaw,
                bot?._combatWeaponLocked);
        }

        /// <summary>
        /// Near when inside close hit range; far when only far reaches.
        /// Both in range → prefer ready close, else ready far (item cooldowns/reloads).
        /// </summary>
        public static WeaponController PickNearFarByRangeAndReady(
            WeaponController close,
            WeaponController far,
            int closeR,
            int farR,
            double distanceRaw,
            WeaponController locked)
        {
            return BotWeaponUtil.PickNearFarByRangeAndReady(close, far, closeR, farR, distanceRaw, locked);
        }

        /// <summary>Apply fireDelayOther when the engage gun changes.</summary>
        public static WeaponController NotifyWeaponPick(Bot bot, WeaponController desired)
        {
            return BotWeaponUtil.CommitEngageWeapon(bot, desired);
        }

        /// <summary>Legacy overload — prefers close; mid-range uses ready far when both can hit.</summary>
        public static WeaponController SelectWeaponForDistance(
            WeaponController weaponClose,
            WeaponController weaponFar,
            ItemInfo itemFar,
            ItemInfo itemClose,
            double distanceRaw)
        {
            return SelectWeaponForDistance(null, Environment.TickCount, weaponClose, weaponFar,
                itemFar, itemClose, distanceRaw, true);
        }

        static bool InFarPokeWindow(Bot bot, int now)
        {
            int id = bot != null ? bot._id : 0;
            int phase = (now + id * 41) % FarPokeCycleMs;
            if (phase < 0)
                phase += FarPokeCycleMs;
            return phase < FarPokeWindowMs;
        }

        /// <summary>
        /// Face the target (aim) while steering away — SteeringController then uses backward thrust instead of turning and sprinting.
        /// </summary>
        public static void ApplyBackpedalSteer(
            Bot bot,
            SteeringController steering,
            Helpers.ObjectState state,
            WeaponController weapon,
            InfServer.Game.Player target)
        {
            if (target?._state == null)
                return;

            ApplyBackpedalSteer(bot, steering, state, weapon, target._state);
        }

        public static void ApplyBackpedalSteer(
            Bot bot,
            SteeringController steering,
            Helpers.ObjectState state,
            WeaponController weapon,
            Helpers.ObjectState targetState)
        {
            if (bot != null)
            {
                BotCombat.ApplyBackpedalSteer(bot, steering, state, weapon, targetState);
                return;
            }

            if (steering == null || state == null || targetState == null)
                return;

            BotDrive.BackpedalWhileAiming(null, weapon, targetState);
        }

        public static void ApplyBackpedalSteer(
            SteeringController steering,
            Helpers.ObjectState state,
            WeaponController weapon,
            InfServer.Game.Player target)
        {
            ApplyBackpedalSteer(null, steering, state, weapon, target);
        }

        public static void ApplyBackpedalSteer(
            SteeringController steering,
            Helpers.ObjectState state,
            WeaponController weapon,
            Helpers.ObjectState targetState)
        {
            ApplyBackpedalSteer(null, steering, state, weapon, targetState);
        }

        /// <summary>
        /// Bot <c>shotFired()</c> projectiles do not damage other bot vehicles on script bot teams.
        /// Apply explosion damage directly (same as turret projectiles hitting a bot vehicle).
        /// </summary>
        public static bool TryFireHostileBotVehicleShot(
            Script_Eol script,
            Bot shooter,
            Vehicle target,
            WeaponController weapon,
            bool lineClearVerified = false)
        {
            if (script == null || shooter == null || target == null || target.IsDead || weapon == null)
                return false;
            if (shooter._team == null || target._team == null || !script.ShouldEolBotTeamsFight(shooter._team, target._team))
                return false;

            ItemInfo.Projectile proj = weapon.Projectile;
            if (proj == null || !weapon.ableToFire())
                return false;
            if (!lineClearVerified
                && !EolBotTargetUtil.TileLineClearForWeaponFire(
                    shooter._arena,
                    shooter._state.positionX,
                    shooter._state.positionY,
                    target._state.positionX,
                    target._state.positionY))
                return false;

            int aimResult = SyncCombatAim(shooter, null, weapon, target._state);
            double distRaw = Helpers.distanceTo(shooter._state, target._state);
            ItemInfo itemClose;
            ItemInfo itemFar;
            ResolveWeaponItems(shooter._type, out itemClose, out itemFar);
            WeaponEngageBands bands = BuildWeaponEngageBands(itemFar, itemClose);
            if (!ShouldCombatFire(weapon, shooter, aimResult, distRaw, bands))
                return false;

            Bot victimBot = target as Bot;
            if (victimBot != null)
                script.NoteBotDamagedByBot(victimBot, shooter);

            target.applyExplosionDamage(true, null, target._state.positionX, target._state.positionY, proj);
            weapon.shotFired();
            OnCombatShotFired(shooter, weapon);
            return true;
        }
    }
}
