using System;
using System.Collections.Generic;
using InfServer.Bots;
using InfServer.Protocol;
using Axiom.Math;
using Assets;

namespace InfServer.Game.Modules.BotActions
{
    /// <summary>
    /// Shared targeting / engage helpers for all behaviors.
    /// </summary>
    public static class BotActionHelpers
    {
        public const int DefaultSeekRange = 6000;
        public const int HoldRadius = 180;
        public static readonly float OrbitForward = 0.62f;
        public static readonly float OrbitStrafe = 0.58f;

        public static BotThrottleConfig Throttle(BotActionContext ctx)
        {
            if (ctx?.Arena?.BotThrottle != null)
                return ctx.Arena.BotThrottle;
            return BotThrottleConfig.Active ?? BotThrottleConfig.Defaults;
        }

        /// <summary>Enemy bots beyond this raw range never steal a player lock.</summary>
        public const int HostileBotClosePreferRaw = 3200;

        public static bool RefreshHostileTarget(BotActionContext ctx, int range = -1)
        {
            if (ctx?.Bot == null || ctx.Arena == null)
                return false;

            BotThrottleConfig thr = Throttle(ctx);
            int seek = range > 0 ? range : (ctx.SeekRange > 0 ? ctx.SeekRange : thr.DefaultSeekRange);
            double stickySeek = seek * 1.5;

            // Sticky player — keep through brief LOS loss / cover; never yield to a distant bot.
            if (ctx.CurrentPlayerTarget != null
                && !ctx.CurrentPlayerTarget.IsDead
                && !ctx.CurrentPlayerTarget.IsSpectator
                && ctx.CurrentPlayerTarget._arena == ctx.Arena
                && (ctx.Bot._team == null || ctx.CurrentPlayerTarget._team != ctx.Bot._team))
            {
                double stickyDist = Helpers.distanceTo(ctx.Bot._state, ctx.CurrentPlayerTarget._state);
                if (stickyDist <= stickySeek)
                {
                    ctx.CurrentBotTarget = null;
                    return true;
                }
            }

            Player bestPlayer = null;
            double bestPlayerDist = double.MaxValue;
            List<Player> near = ctx.Arena.getPlayersInRange(
                ctx.Bot._state.positionX, ctx.Bot._state.positionY, seek);
            if (near != null)
            {
                for (int i = 0; i < near.Count; i++)
                {
                    Player p = near[i];
                    if (p == null || p.IsDead || p.IsSpectator)
                        continue;
                    if (ctx.Bot._team != null && p._team == ctx.Bot._team)
                        continue;
                    double d = Helpers.distanceTo(ctx.Bot._state, p._state);
                    if (d < bestPlayerDist)
                    {
                        bestPlayerDist = d;
                        bestPlayer = p;
                    }
                }
            }

            // Players always win over bots when any hostile player is in seek range.
            if (bestPlayer != null)
            {
                ctx.CurrentPlayerTarget = bestPlayer;
                ctx.CurrentBotTarget = null;
                return true;
            }

            // Sticky enemy bot — only while close AND clear weapon LOS (no wall-hug).
            if (ctx.CurrentBotTarget != null
                && !ctx.CurrentBotTarget.IsDead
                && !ctx.CurrentBotTarget.bCondemned
                && (ctx.Bot._team == null || ctx.CurrentBotTarget._team != ctx.Bot._team)
                && (ctx.Arena.BotVsBotHostilityFilter == null
                    || ctx.Arena.BotVsBotHostilityFilter(ctx.Bot, ctx.CurrentBotTarget)))
            {
                double stickyDist = Helpers.distanceTo(ctx.Bot._state, ctx.CurrentBotTarget._state);
                if (stickyDist <= HostileBotClosePreferRaw
                    && BotCombat.CanDirectEngage(ctx.Bot, ctx.CurrentBotTarget._state))
                {
                    ctx.CurrentPlayerTarget = null;
                    return true;
                }
                ctx.CurrentBotTarget = null;
            }

            Bots.Bot bestBot = null;
            double bestBotDist = double.MaxValue;
            Func<Bots.Bot, Bots.Bot, bool> botFilter = ctx.Arena.BotVsBotHostilityFilter;
            foreach (Bots.Bot other in ctx.Arena.ActiveBots)
            {
                if (other == null || other == ctx.Bot || other.IsDead || other.bCondemned)
                    continue;
                if (ctx.Bot._team != null && other._team == ctx.Bot._team)
                    continue;
                if (botFilter != null && !botFilter(ctx.Bot, other))
                    continue;
                double d = Helpers.distanceTo(ctx.Bot._state, other._state);
                if (d > HostileBotClosePreferRaw)
                    continue;
                if (!BotCombat.CanDirectEngage(ctx.Bot, other._state))
                    continue;
                if (d < bestBotDist)
                {
                    bestBotDist = d;
                    bestBot = other;
                }
            }

            if (bestBot != null)
            {
                ctx.CurrentBotTarget = bestBot;
                ctx.CurrentPlayerTarget = null;
                return true;
            }

            ctx.CurrentPlayerTarget = null;
            ctx.CurrentBotTarget = null;
            return false;
        }

        /// <summary>Legacy name — prefer <see cref="RefreshHostileTarget"/>.</summary>
        public static bool RefreshPlayerTarget(BotActionContext ctx, int range = -1)
            => RefreshHostileTarget(ctx, range);

        /// <summary>Stay in a dance phase at least this long (ms).</summary>
        const int EngageModeStickMs = 1100;
        /// <summary>Distance slack so band edges don't flicker.</summary>
        const int EngageDistSlackRaw = 120;
        /// <summary>Require LOS lost this long before leaving orbit for approach.</summary>
        const int EngageLosLostMs = 1600;
        /// <summary>How long a strafe / circle / fade / press phase lasts before cycling.</summary>
        const int EngageDancePhaseMs = 1600;
        /// <summary>Preferred inner engage ring in raw pixels (map units).</summary>
        public const int EngageMinRadiusRawDefault = 15;

        /// <summary>
        /// Aggressive engage: chase when the target leaves weapon range; only peel home
        /// when beyond the combat leash from HQ / crash / raid anchor.
        /// </summary>
        public static void EngageTarget(BotActionContext ctx, Helpers.ObjectState target, bool pressMode = false)
        {
            if (ctx?.Bot == null || target == null)
                return;

            Bot bot = ctx.Bot;
            if (bot._movement is not SteeringController steering)
                return;

            BotThrottleConfig thr = Throttle(ctx);
            double dist = Helpers.distanceTo(bot._state, target);

            ResolveEngageBand(bot, thr, out int minRaw, out int maxRaw, out int peelRaw);
            ctx.EngageOrbitRingOs = Math.Max(0.12f, minRaw / 100f);

            // Combat leash: chase freely inside the disk; peel to anchor when overextended.
            // Never peel while the hostile is still inside seek/aggro — walk-bys must fight
            // even when the bot is far from HQ / PDB / raid pin.
            Helpers.ObjectState anchor = ResolveCombatAnchor(ctx);
            int leashRaw = thr.CombatLeashRaw;
            int seekAggro = ctx.SeekRange > 0
                ? ctx.SeekRange
                : (thr.DefaultSeekRange > 0 ? thr.DefaultSeekRange : DefaultSeekRange);
            bool overLeash = false;
            if (anchor != null && leashRaw > 0)
            {
                double fromAnchor = Helpers.distanceTo(bot._state, anchor);
                if (fromAnchor > leashRaw && dist > seekAggro)
                    overLeash = true;
            }

            if (overLeash)
            {
                // Peel home — keep shooting only if still inside the weapon envelope.
                ApproachPoint(ctx, anchor);
                WeaponController leashGun = ResolveEngageWeapon(bot, target, BotCombat.CanDirectEngage(bot, target), dist, minRaw, maxRaw);
                if (leashGun != null)
                    bot._weapon = leashGun;
                if (leashGun != null && dist <= maxRaw + EngageDistSlackRaw && InEngageFireBurst(ctx, thr, Environment.TickCount))
                    BotCombat.TryAimAndFire(bot, leashGun, target, requireLineOfSight: true, freezeMs: 0);
                ctx.EngageMode = EngageMotionMode.Approach;
                return;
            }

            bool canDirect = BotCombat.CanDirectEngage(bot, target);
            int now = Environment.TickCount;
            if (now == 0) now = 1;
            if (canDirect)
                ctx.EngageLosLostTick = 0;
            else if (ctx.EngageLosLostTick == 0)
                ctx.EngageLosLostTick = now;

            if (Math.Abs(ctx.EngageStrafeSign) < 0.1f)
                ctx.EngageStrafeSign = ((bot._id & 1) != 0) ? 1f : -1f;

            if ((now / 2800 + bot._id) % 5 == 0 && (now - ctx.EngageModeTick) > 900)
            {
                if (((now / 50) & 3) == (bot._id & 3))
                    ctx.EngageStrafeSign = -ctx.EngageStrafeSign;
            }

            // Outside weapon range → always chase back in (never stick on fade/orbit).
            bool mustChase = dist > maxRaw || (!canDirect && dist > minRaw);
            EngageMotionMode desired = mustChase
                ? EngageMotionMode.Approach
                : PickEngageDance(ctx, dist, peelRaw, minRaw, maxRaw, canDirect, now, pressMode);

            EngageMotionMode mode = desired;
            if (!mustChase
                && desired != EngageMotionMode.Flee
                && ctx.EngageMode != EngageMotionMode.None
                && ctx.EngageMode != EngageMotionMode.Flee
                && desired != ctx.EngageMode)
            {
                int held = now - (ctx.EngageModeTick == 0 ? now : ctx.EngageModeTick);
                bool edgeSlack =
                    (ctx.EngageMode == EngageMotionMode.Approach && dist < maxRaw + EngageDistSlackRaw)
                    || ((ctx.EngageMode == EngageMotionMode.Orbit || ctx.EngageMode == EngageMotionMode.Strafe)
                        && dist > minRaw - EngageDistSlackRaw && dist < maxRaw + EngageDistSlackRaw)
                    || ((ctx.EngageMode == EngageMotionMode.Fade)
                        && dist < minRaw + EngageDistSlackRaw);
                if (held < EngageModeStickMs || edgeSlack)
                    mode = ctx.EngageMode;
            }

            if (mode != ctx.EngageMode)
            {
                ctx.EngageMode = mode;
                ctx.EngageModeTick = now;
                if (mode == EngageMotionMode.Flee || mode == EngageMotionMode.Fade || mode == EngageMotionMode.Strafe)
                    ctx.EngageStrafeSign = -ctx.EngageStrafeSign;
            }

            WeaponController gun = ResolveEngageWeapon(bot, target, canDirect, dist, minRaw, maxRaw);
            if (gun != null)
                bot._weapon = gun;

            bool holdForShot = gun != null && gun.RequiresStationaryFire && gun.ableToFire() && InEngageFireBurst(ctx, thr, now);
            if (holdForShot)
            {
                int aimYaw = Helpers.computeLeadFireAngle(bot._state, target, 10);
                double aimError = Helpers.calculateDifferenceInAngles(bot._state.yaw, aimYaw);
                float rotate = BotDrive.ClampAxis((float)(aimError / 35.0));
                BotDrive.ApplyCombatDrivePublic(bot, steering, 0f, 0f, rotate);
            }
            else if (mode == EngageMotionMode.Approach
                && (mustChase || !BotCombat.CanPressStraightIn(bot)))
            {
                // Close distance with mesh + seek fallback. Never null — a pending path with
                // no fallback freezes bots (looks like "sees you, won't chase").
                Helpers.ObjectState chaseDest = target;
                Func<InfantryVehicle, Vector3> pursueFallback = vehicle =>
                    vehicle.SteerForSeek(chaseDest.position());
                bot.ApplyNavigationApproach(chaseDest, pursueFallback, new BotNavigateOptions
                {
                    RequeueMinMs = Math.Max(600, thr.CombatPathRequeueMs / 2),
                    PathStartIndex = 0,
                    Priority = PathPriority.Combat,
                });
            }
            else
            {
                float chaseFwd = mustChase || mode == EngageMotionMode.Approach ? 1.0f : 0.82f;
                ApplyEngageDance(bot, steering, target, mode, ctx.EngageStrafeSign, ctx.EngageOrbitRingOs, chaseFwd);
            }

            if (gun != null && InEngageFireBurst(ctx, thr, now))
            {
                int freeze = holdForShot ? gun.StationaryFireLockMs : 0;
                BotCombat.TryAimAndFire(bot, gun, target, requireLineOfSight: true, freezeMs: freeze);
            }
        }

        /// <summary>HQ / crash ship / raid PDB / explicit ctx.Anchor.</summary>
        static Helpers.ObjectState ResolveCombatAnchor(BotActionContext ctx)
        {
            if (ctx == null)
                return null;
            if (ctx.Anchor != null)
                return ctx.Anchor;

            Helpers.ObjectState pos = null;
            if (ctx.Objectives != null)
            {
                if (ctx.Objectives.TryGetDefendAnchor(ctx.Bot, out pos) && pos != null)
                    return pos;
                if (ctx.Objectives.TryGetPatrolAnchor(ctx.Bot, out pos) && pos != null)
                    return pos;
                if (ctx.Objectives.TryGetRaidObjective(ctx.Bot, out pos) && pos != null)
                    return pos;
            }
            return null;
        }

        static void ResolveEngageBand(Bot bot, BotThrottleConfig thr, out int minRaw, out int maxRaw, out int peelRaw)
        {
            minRaw = thr.EngageMinRadiusRaw > 0 ? thr.EngageMinRadiusRaw : EngageMinRadiusRawDefault;
            peelRaw = thr.EngageTooCloseRaw > 0 ? thr.EngageTooCloseRaw : 8;
            if (peelRaw >= minRaw)
                peelRaw = Math.Max(4, minRaw - 1);

            int farR = BotWeaponUtil.GetCombatRangeRaw(bot?._weaponFarItem);
            int closeR = BotWeaponUtil.GetCombatRangeRaw(bot?._weaponCloseItem);
            if (farR <= 0 && bot?._weaponFar?.EquippedItem != null)
                farR = BotWeaponUtil.GetCombatRangeRaw(bot._weaponFar.EquippedItem);
            if (closeR <= 0)
            {
                ItemInfo closeEq = bot?._weaponClose?.EquippedItem ?? bot?._weapon?.EquippedItem;
                if (closeEq != null)
                    closeR = BotWeaponUtil.GetCombatRangeRaw(closeEq);
            }

            // Movement / chase envelope hugs close-weapon range so bots press in instead of
            // parking at far-gun max. Far gun still fires when selected in ResolveEngageWeapon.
            int danceMax = closeR > 0 ? closeR + 60 : thr.EngageIdealMaxRaw;
            if (danceMax < minRaw + 80)
                danceMax = Math.Max(minRaw + 120, 220);
            // Cap so long-range inventory items don't keep them skirmishing at 1000+.
            if (danceMax > 420)
                danceMax = 420;

            maxRaw = danceMax;
            if (farR > 0 && maxRaw > farR)
                maxRaw = farR;
        }

        /// <summary>Burst fire windows — still aim/move during suppress pauses (pin pressure).</summary>
        static bool InEngageFireBurst(BotActionContext ctx, BotThrottleConfig thr, int now)
        {
            if (ctx == null)
                return true;

            int onMs = thr.EngageBurstOnMs > 0 ? thr.EngageBurstOnMs : 1400;
            int offMs = thr.EngageBurstOffMs > 0 ? thr.EngageBurstOffMs : 90;
            // Jitter per bot so packs don't sync bursts.
            onMs += (ctx.Bot._id % 7) * 40;
            offMs += (ctx.Bot._id % 5) * 25;

            if (ctx.EngageBurstEndTick == 0 && ctx.EngageBurstResumeTick == 0)
            {
                ctx.EngageBurstEndTick = now + onMs;
                return true;
            }

            if (now < ctx.EngageBurstEndTick)
                return true;

            if (ctx.EngageBurstResumeTick == 0)
                ctx.EngageBurstResumeTick = now + offMs;

            if (now < ctx.EngageBurstResumeTick)
                return false;

            ctx.EngageBurstEndTick = now + onMs;
            ctx.EngageBurstResumeTick = 0;
            return true;
        }

        static EngageMotionMode PickEngageDance(
            BotActionContext ctx, double dist, int peelRaw, int minRaw, int maxRaw,
            bool canDirect, int now, bool pressMode)
        {
            // Only peel when nearly overlapping — stay aggressive inside the min ring.
            if (dist < peelRaw)
                return EngageMotionMode.Flee;

            if (!canDirect)
            {
                int losLostFor = ctx.EngageLosLostTick == 0 ? 0 : now - ctx.EngageLosLostTick;
                if (losLostFor >= EngageLosLostMs || dist > maxRaw)
                    return EngageMotionMode.Approach;
            }

            if (dist > maxRaw)
                return EngageMotionMode.Approach;

            // Inside min ring: circle / strafe — skip Fade when pressMode (keep pressure).
            if (dist < minRaw)
            {
                int closePhase = Math.Abs(now / EngageDancePhaseMs + ctx.Bot._id) % 3;
                if (pressMode)
                {
                    return closePhase switch
                    {
                        0 => EngageMotionMode.Orbit,
                        1 => EngageMotionMode.Strafe,
                        _ => EngageMotionMode.Approach,
                    };
                }
                return closePhase switch
                {
                    0 => EngageMotionMode.Orbit,
                    1 => EngageMotionMode.Strafe,
                    _ => EngageMotionMode.Fade,
                };
            }

            // Between min and max: outer half presses in; inner half circles (no Fade in pressMode).
            double mid = (minRaw + maxRaw) * 0.5;
            int phase = Math.Abs(now / EngageDancePhaseMs + ctx.Bot._id * 3) % 4;
            if (dist > mid)
            {
                return phase switch
                {
                    0 => EngageMotionMode.Approach,
                    1 => EngageMotionMode.Orbit,
                    2 => EngageMotionMode.Strafe,
                    _ => EngageMotionMode.Approach,
                };
            }

            if (pressMode)
            {
                return phase switch
                {
                    0 => EngageMotionMode.Orbit,
                    1 => EngageMotionMode.Strafe,
                    2 => EngageMotionMode.Approach,
                    _ => EngageMotionMode.Orbit,
                };
            }

            return phase switch
            {
                0 => EngageMotionMode.Orbit,
                1 => EngageMotionMode.Strafe,
                2 => EngageMotionMode.Fade,
                _ => EngageMotionMode.Orbit,
            };
        }

        static void ApplyEngageDance(
            Bot bot, SteeringController steering, Helpers.ObjectState target,
            EngageMotionMode mode, float strafeSign, float orbitRingOs, float approachForward = 0.82f)
        {
            switch (mode)
            {
                case EngageMotionMode.Flee:
                    BotDrive.FadeOut(bot, target, strafeSign, backStrength: 0.38f, strafeStrength: 0.55f);
                    break;
                case EngageMotionMode.Approach:
                    BotDrive.PressIn(bot, target, strafeSign,
                        forwardStrength: approachForward > 0.1f ? approachForward : 0.82f,
                        strafeStrength: 0.42f);
                    break;
                case EngageMotionMode.Strafe:
                    BotDrive.StrafeFace(bot, target, strafeSign, strafeStrength: 0.85f, forwardStrength: 0.28f);
                    break;
                case EngageMotionMode.Fade:
                    BotDrive.FadeOut(bot, target, strafeSign, backStrength: 0.34f, strafeStrength: 0.58f);
                    break;
                default:
                    BotDrive.StrafeOrbit(bot, target, OrbitForward, OrbitStrafe, strafeSign, orbitRingOs);
                    break;
            }
        }

        /// <summary>
        /// Range-based close/far pick with reload stick. Scripts may override via
        /// <see cref="Bot.CombatWeaponSelector"/>.
        /// </summary>
        public static WeaponController ResolveEngageWeapon(
            Bot bot, Helpers.ObjectState target, bool hasLos, double distanceRaw)
        {
            ResolveEngageBand(bot, BotThrottleConfig.Active ?? BotThrottleConfig.Defaults,
                out int minRaw, out int maxRaw, out _);
            return ResolveEngageWeapon(bot, target, hasLos, distanceRaw, minRaw, maxRaw);
        }

        public static WeaponController ResolveEngageWeapon(
            Bot bot, Helpers.ObjectState target, bool hasLos, double distanceRaw,
            int minRaw, int maxRaw)
        {
            if (bot == null)
                return null;

            if (bot.CombatWeaponSelector != null)
            {
                WeaponController custom = bot.CombatWeaponSelector(bot, target, hasLos);
                if (custom != null)
                    return BotWeaponUtil.CommitEngageWeapon(bot, custom);
            }

            WeaponController close = bot._weaponClose ?? bot._weapon;
            WeaponController far = bot._weaponFar;
            // Engage may assign the active gun onto _weapon — never treat far as close.
            if (close != null && far != null && ReferenceEquals(close, far))
                close = bot._weaponClose;
            if (far == null || bot._weaponFarItem == null)
                return BotWeaponUtil.CommitEngageWeapon(bot, close ?? far);

            ItemInfo itemClose = bot._weaponCloseItem;
            ItemInfo itemFar = bot._weaponFarItem;

            int closeR = BotWeaponUtil.GetCombatRangeRaw(itemClose);
            if (closeR <= 0) closeR = 90;
            int farR = BotWeaponUtil.GetCombatRangeRaw(itemFar);
            if (farR <= 0) farR = Math.Max(maxRaw, 1100);
            if (farR < closeR) farR = closeR;

            WeaponController desired = BotWeaponUtil.PickNearFarByRangeAndReady(
                close, far, closeR, farR, distanceRaw, bot._combatWeaponLocked);
            return BotWeaponUtil.CommitEngageWeapon(bot, desired);
        }

        static void ApplyPursueSteer(Bot bot, SteeringController steering, Helpers.ObjectState target)
        {
            BotDrive.PressIn(bot, target, ((bot._id & 1) != 0) ? 1f : -1f);
        }

        static void ApplyFleeSteer(Bot bot, SteeringController steering, Helpers.ObjectState threat)
        {
            BotDrive.FadeOut(bot, threat, ((bot._id & 1) != 0) ? 1f : -1f, backStrength: 0.8f);
        }

        public static void ApproachPoint(BotActionContext ctx, Helpers.ObjectState dest, int requeueMs = -1)
        {
            if (ctx?.Bot == null || dest == null)
                return;

            // Patrol / non-combat: Api uses nav; Legacy continuous seek (Multi-style).
            if (ctx.Bot.MovementMode != BotMovementMode.Api)
            {
                if (ctx.Bot._movement is SteeringController steering)
                {
                    steering.steerDelegate = vehicle => vehicle.SteerForSeek(dest.position());
                }
                return;
            }

            int rq = requeueMs > 0 ? requeueMs : Math.Max(1500, Throttle(ctx).CombatPathRequeueMs);
            Func<InfantryVehicle, Vector3> fallback = vehicle =>
                vehicle.SteerForSeek(dest.position());
            ctx.Bot.ApplyNavigationApproach(dest, fallback, new BotNavigateOptions
            {
                RequeueMinMs = rq,
                PathStartIndex = 0,
                Priority = PathPriority.Idle
            });
        }

        public static bool IsNear(Helpers.ObjectState a, Helpers.ObjectState b, int radius)
        {
            if (a == null || b == null)
                return false;
            return Helpers.distanceTo(a, b) <= radius;
        }

        public static void Hold(BotActionContext ctx)
        {
            ctx?.Bot?.HoldPosition();
        }

        public static void FleeFrom(BotActionContext ctx, Helpers.ObjectState threat)
        {
            if (ctx?.Bot == null || threat == null)
                return;
            if (ctx.Bot._movement is not SteeringController steering)
                return;
            ApplyFleeSteer(ctx.Bot, steering, threat);
        }

        public static void CircleStrafe(BotActionContext ctx, Helpers.ObjectState target)
        {
            if (ctx?.Bot == null || target == null)
                return;
            BotCombat.StrafeOrbit(ctx.Bot, target, OrbitForward, OrbitStrafe);
            double dist = Helpers.distanceTo(ctx.Bot._state, target);
            WeaponController gun = ResolveEngageWeapon(ctx.Bot, target, true, dist);
            if (gun != null)
            {
                ctx.Bot._weapon = gun;
                int freeze = gun.RequiresStationaryFire && gun.ableToFire() ? gun.StationaryFireLockMs : 0;
                BotCombat.TryAimAndFire(ctx.Bot, gun, target, requireLineOfSight: true, freezeMs: freeze);
            }
        }
    }
}
