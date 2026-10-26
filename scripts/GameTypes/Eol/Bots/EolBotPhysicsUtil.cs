using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;
using Axiom.Math;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>Collision / stuck recovery via pathfinder escape. Combat may keep firing while escaping.</summary>
    internal static class EolBotPhysicsUtil
    {
        public const int CollisionUnstickCooldownMs = 900;

        public const int StuckCheckIntervalMs = 1800;

        public const int StuckMoveThresholdRaw = 52;

        /// <summary>Eol pathfinder unstick for roaming attackers (Api wall-escape still runs for everyone).</summary>
        public const bool StuckRecoveryEnabledRoamingAttacker = true;

        public const int PathRequeueMinMsRoamGrunt = 1400;
        public const int PathRequeueMinMsRoamCaptain = 1400;
        public const int PathRequeueMinMsAlien = 1400;
        public const int PathRequeueMinMsDefense = 1600;

        public const int UnstickPathRequeueMs = 900;

        public const int UnstickArrivalRaw = 96;

        public const int UnstickClearCollisionMs = 400;

        public const int UnstickMinDurationMs = 280;

        /// <summary>Hard cap — force open-tile warp and resume combat so bots cannot ignore forever.</summary>
        public const int UnstickMaxDurationMs = 2200;

        static readonly int[] UnstickWarpRadii = { 96, 160, 240, 360 };

        /// <summary>Direct pursuit/strafe when tile line to target is open (distance handled by combat action).</summary>
        public static bool PreferDirectEngageMovementSteering(bool tileLineClearToTarget, double distanceSteeringLength)
        {
            return tileLineClearToTarget;
        }

        /// <summary>
        /// Runs wall/stuck escape movement. Returns true while escape owns locomotion.
        /// Callers should still fire at sticky targets (do not skip combat entirely).
        /// </summary>
        public static bool HandleBotUnstickPoll(
            Bot bot,
            Helpers.ObjectState state,
            int now,
            Arena arena,
            Script_Eol baseScript,
            SteeringController steering,
            bool hasCollision,
            ref int tickLastCollision,
            ref int tickStuckSample,
            ref short stuckSampleX,
            ref short stuckSampleY,
            ref bool unstickPathActive,
            ref Helpers.ObjectState unstickEscapeTarget,
            ref int tickUnstickStarted,
            ref int tickLastUnstickNoCollision,
            Func<EolBotPathState> getPathState,
            Action<EolBotPathState> setPathState,
            Func<InfantryVehicle, Vector3> steerAlongPath,
            int pathRequeueMinMs,
            Action onUnstickBegin = null,
            Action onUnstickComplete = null,
            bool enableStuckRecovery = true,
            Helpers.ObjectState combatBiasToward = null)
        {
            if (state == null || arena == null || steering == null || steerAlongPath == null
                || getPathState == null || setPathState == null)
                return false;

            if (unstickPathActive)
            {
                if (hasCollision)
                    tickLastUnstickNoCollision = 0;
                else if (tickLastUnstickNoCollision == 0)
                    tickLastUnstickNoCollision = now;

                // Hard escape: warp to open tile so combat can resume.
                if (tickUnstickStarted != 0 && now - tickUnstickStarted >= UnstickMaxDurationMs)
                {
                    ForceWarpOffWall(bot, state, arena, baseScript, combatBiasToward, steering);
                    ForceFinishUnstick(ref unstickPathActive, ref unstickEscapeTarget, ref tickUnstickStarted,
                        ref tickLastUnstickNoCollision, setPathState, onUnstickComplete);
                    return false;
                }

                if (unstickEscapeTarget != null)
                    PollUnstickPath(bot, state, now, arena, steering, unstickEscapeTarget, getPathState, setPathState, steerAlongPath, pathRequeueMinMs);
                else
                    steering.steerDelegate = steerAlongPath;

                if (TryFinishUnstick(state, now, ref unstickPathActive, ref unstickEscapeTarget, ref tickUnstickStarted, ref tickLastUnstickNoCollision, getPathState, setPathState, onUnstickComplete))
                    return false;

                return true;
            }

            if (enableStuckRecovery && TryDetectStuck(state, now, steering, ref tickStuckSample, ref stuckSampleX, ref stuckSampleY))
            {
                if (TryBeginUnstickPath(bot, state, now, arena, baseScript, steering, ref tickLastCollision, ref unstickPathActive, ref unstickEscapeTarget, ref tickUnstickStarted, ref tickLastUnstickNoCollision, setPathState, onUnstickBegin, combatBiasToward))
                    return true;
            }

            if (hasCollision && (tickLastCollision == 0 || now - tickLastCollision >= CollisionUnstickCooldownMs))
            {
                tickLastCollision = now;
                if (TryBeginUnstickPath(bot, state, now, arena, baseScript, steering, ref tickLastCollision, ref unstickPathActive, ref unstickEscapeTarget, ref tickUnstickStarted, ref tickLastUnstickNoCollision, setPathState, onUnstickBegin, combatBiasToward))
                    return true;
            }

            return false;
        }

        /// <summary>Aim/fire only — does not steal unstick locomotion.</summary>
        public static bool TryFireOnlyWhileUnstick(Bot bot, InfServer.Game.Player target)
        {
            if (bot == null || target == null || target.IsDead || target._state == null)
                return false;
            if (bot._team != null && target._team == bot._team)
                return false;

            WeaponController gun = bot._combatWeaponLocked ?? bot._weaponClose ?? bot._weapon;
            if (gun == null || !gun.ableToFire())
                return false;

            return BotCombat.TryAimAndFire(bot, gun, target._state, requireLineOfSight: true, freezeMs: 0);
        }

        static bool TryDetectStuck(
            Helpers.ObjectState state,
            int now,
            SteeringController steering,
            ref int tickStuckSample,
            ref short stuckSampleX,
            ref short stuckSampleY)
        {
            // Api combat often uses analog drive without steerDelegate — still sample movement.
            // When neither steer nor recent motion intent exists, skip stuck detection
            // (collision path still starts unstick).
            if (tickStuckSample == 0)
            {
                tickStuckSample = now;
                stuckSampleX = state.positionX;
                stuckSampleY = state.positionY;
                return false;
            }

            int dt = now - tickStuckSample;
            if (dt < 0)
            {
                tickStuckSample = now;
                stuckSampleX = state.positionX;
                stuckSampleY = state.positionY;
                return false;
            }

            if (dt < StuckCheckIntervalMs)
                return false;

            int dx = state.positionX - stuckSampleX;
            int dy = state.positionY - stuckSampleY;
            int movedSq = dx * dx + dy * dy;
            int th = StuckMoveThresholdRaw;

            tickStuckSample = now;
            stuckSampleX = state.positionX;
            stuckSampleY = state.positionY;

            return movedSq < th * th;
        }

        static bool TryBeginUnstickPath(
            Bot bot,
            Helpers.ObjectState state,
            int now,
            Arena arena,
            Script_Eol baseScript,
            SteeringController steering,
            ref int tickLastCollision,
            ref bool unstickPathActive,
            ref Helpers.ObjectState unstickEscapeTarget,
            ref int tickUnstickStarted,
            ref int tickLastUnstickNoCollision,
            Action<EolBotPathState> setPathState,
            Action onUnstickBegin,
            Helpers.ObjectState combatBiasToward)
        {
            Helpers.ObjectState escape = PickEscapeWaypoint(state, arena, baseScript, steering, combatBiasToward);
            if (escape == null)
                return false;

            if (onUnstickBegin != null)
                onUnstickBegin();

            EolBotPathState cleared = new EolBotPathState();
            cleared.AssignPath = true;
            cleared.TickLastPath = 0;
            setPathState(cleared);

            unstickPathActive = true;
            unstickEscapeTarget = escape;
            tickUnstickStarted = now;
            tickLastUnstickNoCollision = 0;
            tickLastCollision = now;

            QueueUnstickPath(bot, state, now, arena, escape, setPathState);
            return true;
        }

        static Helpers.ObjectState PickEscapeWaypoint(
            Helpers.ObjectState state,
            Arena arena,
            Script_Eol baseScript,
            SteeringController steering,
            Helpers.ObjectState combatBiasToward)
        {
            if (baseScript == null)
                return null;

            Helpers.ObjectState best = null;
            double bestScore = double.MaxValue;

            foreach (int rad in UnstickWarpRadii)
            {
                Helpers.ObjectState candidate = baseScript.findOpenWarpBot(arena, state.positionX, state.positionY, rad, 14);
                if (candidate == null)
                    continue;

                long dx = (long)candidate.positionX - state.positionX;
                long dy = (long)candidate.positionY - state.positionY;
                long distSq = dx * dx + dy * dy;
                if (distSq <= 32L * 32L)
                    continue;

                double score = Math.Sqrt(distSq);
                // Prefer escapes that also close toward the fight (flank off the wall into the player).
                if (combatBiasToward != null)
                {
                    long tx = (long)candidate.positionX - combatBiasToward.positionX;
                    long ty = (long)candidate.positionY - combatBiasToward.positionY;
                    score += Math.Sqrt(tx * tx + ty * ty) * 0.45;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best != null)
                return best;

            Vector3 lc = steering != null ? steering._lastCollision : Vector3.Zero;
            if (lc.LengthSquared > 0.04f)
            {
                float awayX = state.positionX + (state.positionX - lc.x) * 0.35f;
                float awayY = state.positionY + (state.positionY - lc.y) * 0.35f;
                Helpers.ObjectState away = baseScript.findOpenWarpBot(arena, (short)awayX, (short)awayY, 160, 12);
                if (away != null)
                    return away;
            }

            return baseScript.findOpenWarpBot(arena, state.positionX, state.positionY, 280, 16);
        }

        static void ForceWarpOffWall(
            Bot bot,
            Helpers.ObjectState state,
            Arena arena,
            Script_Eol baseScript,
            Helpers.ObjectState combatBiasToward,
            SteeringController steering)
        {
            if (bot == null || state == null || baseScript == null)
                return;

            Helpers.ObjectState open = PickEscapeWaypoint(state, arena, baseScript, steering, combatBiasToward);
            if (open == null)
                open = baseScript.findOpenWarpBot(arena, state.positionX, state.positionY, 360, 20);
            if (open == null)
                return;

            state.positionX = open.positionX;
            state.positionY = open.positionY;
            state.velocityX = 0;
            state.velocityY = 0;
            if (bot._movement != null)
                bot._movement.bCollision = false;
        }

        static void QueueUnstickPath(
            Bot bot,
            Helpers.ObjectState state,
            int now,
            Arena arena,
            Helpers.ObjectState escape,
            Action<EolBotPathState> setPathState)
        {
            if (bot == null || escape == null || arena == null || setPathState == null)
                return;
            if (!EolBotPathBudget.AllowUnstickPath(arena))
                return;

            EolBotPathState pending = new EolBotPathState();
            pending.TickLastPath = int.MaxValue;
            setPathState(pending);

            arena.QueueBotPathRequest(
                bot,
                (short)(state.positionX / 16), (short)(state.positionY / 16),
                (short)(escape.positionX / 16), (short)(escape.positionY / 16),
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

        static void PollUnstickPath(
            Bot bot,
            Helpers.ObjectState state,
            int now,
            Arena arena,
            SteeringController steering,
            Helpers.ObjectState escape,
            Func<EolBotPathState> getPathState,
            Action<EolBotPathState> setPathState,
            Func<InfantryVehicle, Vector3> steerAlongPath,
            int pathRequeueMinMs)
        {
            EolBotPathState ps = getPathState();
            int requeueMs = pathRequeueMinMs < UnstickPathRequeueMs ? UnstickPathRequeueMs : pathRequeueMinMs;
            if (ps.Path == null && (ps.TickLastPath == 0 || now - ps.TickLastPath >= requeueMs))
                QueueUnstickPath(bot, state, now, arena, escape, setPathState);

            if (bot != null && bot.MovementMode == BotMovementMode.Api)
                bot.ApplySteer(steerAlongPath);
            else
                steering.steerDelegate = steerAlongPath;
        }

        static bool TryFinishUnstick(
            Helpers.ObjectState state,
            int now,
            ref bool unstickPathActive,
            ref Helpers.ObjectState unstickEscapeTarget,
            ref int tickUnstickStarted,
            ref int tickLastUnstickNoCollision,
            Func<EolBotPathState> getPathState,
            Action<EolBotPathState> setPathState,
            Action onUnstickComplete)
        {
            if (!unstickPathActive)
                return false;

            if (now - tickUnstickStarted < UnstickMinDurationMs)
                return false;

            if (tickLastUnstickNoCollision == 0 || now - tickLastUnstickNoCollision < UnstickClearCollisionMs)
                return false;

            EolBotPathState ps = getPathState();
            bool pathDone = ps.Path == null || ps.PathTarget >= ps.Path.Count;
            bool nearEscape = false;
            if (unstickEscapeTarget != null)
            {
                long edx = (long)state.positionX - unstickEscapeTarget.positionX;
                long edy = (long)state.positionY - unstickEscapeTarget.positionY;
                long arrSq = (long)UnstickArrivalRaw * UnstickArrivalRaw;
                nearEscape = edx * edx + edy * edy <= arrSq;
            }

            if (!pathDone && !nearEscape)
                return false;

            ForceFinishUnstick(ref unstickPathActive, ref unstickEscapeTarget, ref tickUnstickStarted,
                ref tickLastUnstickNoCollision, setPathState, onUnstickComplete);
            return true;
        }

        static void ForceFinishUnstick(
            ref bool unstickPathActive,
            ref Helpers.ObjectState unstickEscapeTarget,
            ref int tickUnstickStarted,
            ref int tickLastUnstickNoCollision,
            Action<EolBotPathState> setPathState,
            Action onUnstickComplete)
        {
            unstickPathActive = false;
            unstickEscapeTarget = null;
            tickUnstickStarted = 0;
            tickLastUnstickNoCollision = 0;

            if (setPathState != null)
            {
                EolBotPathState cleared = new EolBotPathState();
                setPathState(cleared);
            }

            if (onUnstickComplete != null)
                onUnstickComplete();
        }
    }
}
