using System;
using Axiom.Math;
using Bnoerj.AI.Steering;
using InfServer.Game.Modules.BotActions;
using InfServer.Protocol;

namespace InfServer.Bots
{
    /// <summary>
    /// Converts steering vectors and world targets into analog drive inputs.
    /// </summary>
    public static class BotDrive
    {
        private const double SteerAngleScale = 40.0;
        private const double AimAngleScale = 35.0;
        private const float MinSteerStrength = 0.06f;

        public static float ClampAxis(float value) => Math.Clamp(value, -1f, 1f);

        public static void SetDrive(SteeringController steering, float forward, float strafe, float rotate)
        {
            if (steering == null)
                return;

            // Do not ClearSteerInputs here — zeroing analogs every poll fought the smooth
            // blend and produced jittery vel on the wire.
            steering.steerDelegate = null;
            steering.SetAnalogDrive(ClampAxis(forward), ClampAxis(strafe), ClampAxis(rotate));
        }

        /// <summary>
        /// Project a world-space travel vector into body-relative forward/strafe for the given yaw.
        /// Avoids Cos(headingError) thrust flips when aim yaw lags travel yaw.
        /// </summary>
        public static void WorldToBodyAxes(byte yaw, Vector3 world, out float forward, out float strafe)
        {
            double yawRad = yaw * (Math.PI / 120.0);
            float fx = (float)Math.Sin(yawRad);
            float fy = (float)Math.Cos(yawRad);
            forward = ClampAxis(world.x * fx + world.y * fy);
            // Same lateral convention as MovementController.synthesizeDirectionFromVelocity.
            strafe = ClampAxis(world.x * fy - world.y * fx);
        }

        /// <summary>Public combat drive entry (rate-limited) for engage plant / helpers.</summary>
        public static void ApplyCombatDrivePublic(
            Bot bot, SteeringController steering, float forward, float strafe, float rotate)
            => ApplyCombatDrive(bot, steering, forward, strafe, rotate);

        /// <summary>
        /// Maps a desired travel heading (Infantry yaw units) into body-relative forward/strafe/rotate.
        /// </summary>
        public static void DriveTowardHeading(
            SteeringController steering,
            byte currentYaw,
            float desiredYaw,
            float strength = 1f)
        {
            if (steering == null)
                return;

            double headingError = Helpers.calculateDifferenceInAngles(currentYaw, desiredYaw);
            ApplyHeadingErrorAsDrive(steering, headingError, strength);
        }

        public static void ApplySteerAsDrive(
            SteeringController steering,
            double headingError,
            float steerStrength,
            double aimError = 0,
            bool prioritizeAim = false)
        {
            if (steering == null)
                return;

            float strength = Math.Clamp(steerStrength, 0f, 1.5f);
            if (strength < MinSteerStrength)
            {
                if (prioritizeAim && Math.Abs(aimError) > 2)
                {
                    // Aim-only rotate — keep last forward/strafe so run cycle / coast survive.
                    steering.SetAnalogDrive(
                        steering.AnalogForward, steering.AnalogStrafe,
                        ClampAxis((float)(aimError / AimAngleScale)));
                    return;
                }

                // Weak steer: do not zero drive (that pulsed hops between path samples).
                return;
            }

            double headingRad = headingError * (Math.PI / 120.0);
            float forward = ClampAxis((float)(Math.Cos(headingRad) * strength));
            float strafe = ClampAxis((float)(Math.Sin(headingRad) * strength * 0.9f));
            float rotate = prioritizeAim
                ? ClampAxis((float)(aimError / AimAngleScale))
                : ClampAxis((float)(headingError / SteerAngleScale));

            steering.SetAnalogDrive(forward, strafe, rotate);
        }

        public static void ApplySteerVectorAsDrive(
            SteeringController steering,
            byte currentYaw,
            Vector3 steer,
            double aimError = 0,
            bool prioritizeAim = false)
        {
            if (steering == null)
                return;

            if (steer.LengthSquared < 0.001f)
            {
                if (prioritizeAim && Math.Abs(aimError) > 2)
                {
                    steering.SetAnalogDrive(
                        steering.AnalogForward, steering.AnalogStrafe,
                        ClampAxis((float)(aimError / AimAngleScale)));
                }
                // else keep last drive — do not zero
                return;
            }

            double yaw = Utility.ATan2(steer.x, steer.y);
            float desiredYaw = -((Utility.RadiansToDegrees(yaw) / 1.5f) - 120);
            double headingError = Helpers.calculateDifferenceInAngles(currentYaw, desiredYaw);
            ApplySteerAsDrive(steering, headingError, steer.Length, aimError, prioritizeAim);
        }

        public static void StrafeOrbit(
            Bot bot,
            Helpers.ObjectState targetState,
            float forwardStrength = 0.5f,
            float strafeStrength = 0.42f,
            float orbitSign = 0f,
            float idealRingOs = 2.4f)
        {
            if (bot == null || targetState == null || bot._movement is not SteeringController steering)
                return;

            Vector3 myPos = bot._state.position();
            Vector3 targetPos = targetState.position();
            Vector3 fromTarget = myPos - targetPos;
            float len = fromTarget.Length;
            if (len < 0.05f)
            {
                ApplyCombatDrive(bot, steering, forwardStrength * 0.35f, strafeStrength * 0.8f, 0f);
                return;
            }

            fromTarget *= 1f / len;
            float sign = Math.Abs(orbitSign) > 0.1f
                ? Math.Sign(orbitSign)
                : (((bot._id & 1) != 0) ? 1f : -1f);
            float ring = idealRingOs > 0.2f ? idealRingOs : 2.4f;
            float radial = Math.Clamp((ring - len) * 0.55f, -0.42f, 0.42f);
            Vector3 tangent = new Vector3(-fromTarget.y, fromTarget.x, 0) * sign;
            Vector3 world = tangent * Math.Max(0.55f, strafeStrength)
                + (-fromTarget) * radial
                + (-fromTarget) * (forwardStrength * 0.18f);

            WorldToBodyAxes(bot._state.yaw, world, out float bodyF, out float bodyS);
            if (Math.Abs(bodyS) < 0.28f)
                bodyS = 0.28f * sign;

            int aimYaw = Helpers.computeLeadFireAngle(bot._state, targetState, 10);
            double aimError = Helpers.calculateDifferenceInAngles(bot._state.yaw, aimYaw);
            float rotate = ClampAxis((float)(aimError / AimAngleScale));
            ApplyCombatDrive(bot, steering, ClampAxis(bodyF), ClampAxis(bodyS), rotate);
        }

        /// <summary>Side-strafe while facing the target (no orbit point).</summary>
        public static void StrafeFace(
            Bot bot,
            Helpers.ObjectState targetState,
            float strafeSign,
            float strafeStrength = 0.85f,
            float forwardStrength = 0.25f)
        {
            if (bot == null || targetState == null || bot._movement is not SteeringController steering)
                return;

            int aimYaw = Helpers.computeLeadFireAngle(bot._state, targetState, 10);
            double aimError = Helpers.calculateDifferenceInAngles(bot._state.yaw, aimYaw);
            float rotate = ClampAxis((float)(aimError / AimAngleScale));
            float forward = ClampAxis(forwardStrength);
            float strafe = ClampAxis(strafeStrength * Math.Sign(strafeSign == 0 ? 1f : strafeSign));
            ApplyCombatDrive(bot, steering, forward, strafe, rotate);
        }

        /// <summary>Press straight in toward the target with light weave.</summary>
        public static void PressIn(
            Bot bot,
            Helpers.ObjectState targetState,
            float strafeSign,
            float forwardStrength = 0.62f,
            float strafeStrength = 0.32f)
        {
            if (bot == null || targetState == null || bot._movement is not SteeringController steering)
                return;

            Vector3 delta = targetState.position() - bot._state.position();
            if (delta.LengthSquared < 0.0001f)
            {
                StrafeFace(bot, targetState, strafeSign, 0.55f, 0.2f);
                return;
            }

            // Face target; drive body-relative toward it (no Cos(heading) path).
            WorldToBodyAxes(bot._state.yaw, delta, out float bodyF, out float bodyS);
            float len = delta.Length;
            if (len > 0.001f)
            {
                bodyF /= len;
                bodyS /= len;
            }

            int aimYaw = Helpers.computeLeadFireAngle(bot._state, targetState, 10);
            double aimError = Helpers.calculateDifferenceInAngles(bot._state.yaw, aimYaw);
            float rotate = ClampAxis((float)(aimError / AimAngleScale));

            float f = ClampAxis(bodyF * forwardStrength + 0.15f);
            float s = ClampAxis(bodyS * 0.35f + strafeStrength * Math.Sign(strafeSign == 0 ? 1f : strafeSign));
            ApplyCombatDrive(bot, steering, f, s, rotate);
        }

        /// <summary>Soft fade out while still facing / shooting.</summary>
        public static void FadeOut(
            Bot bot,
            Helpers.ObjectState targetState,
            float strafeSign,
            float backStrength = 0.55f,
            float strafeStrength = 0.5f)
        {
            if (bot == null || targetState == null || bot._movement is not SteeringController steering)
                return;

            int aimYaw = Helpers.computeLeadFireAngle(bot._state, targetState, 10);
            double aimError = Helpers.calculateDifferenceInAngles(bot._state.yaw, aimYaw);
            float rotate = ClampAxis((float)(aimError / AimAngleScale));
            ApplyCombatDrive(bot, steering,
                ClampAxis(-Math.Abs(backStrength)),
                ClampAxis(strafeStrength * Math.Sign(strafeSign == 0 ? 1f : strafeSign)),
                rotate);
        }

        private static void ApplyCombatDrive(Bot bot, SteeringController steering, float forward, float strafe, float rotate)
        {
            // Rate-limit thrust/strafe so mode changes (press↔fade) can't reverse vel in one poll.
            // Client extrapolates bot velocity — a snap reverse reads as a large leap.
            int commitMs = Math.Max(40, BotThrottleConfig.Active?.CombatMotionCommitMs ?? 160);
            int now = Environment.TickCount;
            if (now == 0) now = 1;

            float targetF = ClampAxis(forward);
            float targetS = ClampAxis(strafe);
            float cr = ClampAxis(rotate);

            float cf = bot._combatDriveCommitF;
            float cs = bot._combatDriveCommitS;
            if (bot._combatDriveCommitTick == 0)
            {
                cf = targetF;
                cs = targetS;
                bot._combatDriveCommitTick = now;
            }
            else
            {
                int dt = now - bot._combatDriveCommitTick;
                if (dt < 0) dt = 0;
                // ~0.55 full-scale change per second → ~0.03–0.06 per poll at 50–100ms.
                float maxStep = Math.Clamp(dt / (float)commitMs, 0.04f, 0.18f);
                cf = StepToward(cf, targetF, maxStep);
                cs = StepToward(cs, targetS, maxStep);
                bot._combatDriveCommitTick = now;
            }

            bot._combatDriveCommitF = cf;
            bot._combatDriveCommitS = cs;

            if (bot.MovementMode == BotMovementMode.Api)
                bot.SetDrive(cf, cs, cr);
            else
            {
                BotWallAvoidance.AdjustDrive(bot, ref cf, ref cs, ref cr);
                SetDrive(steering, cf, cs, cr);
            }
        }

        static float StepToward(float current, float target, float maxStep)
        {
            float d = target - current;
            if (d > maxStep) return current + maxStep;
            if (d < -maxStep) return current - maxStep;
            return target;
        }

        public static void BackpedalWhileAiming(
            Bot bot,
            WeaponController weapon,
            Helpers.ObjectState targetState,
            float backStrength = 0.72f)
        {
            if (bot == null || targetState == null)
                return;

            const int leadVel = 10000 / 1000;
            bot._state.fireAngle = Helpers.computeLeadFireAngle(bot._state, targetState, leadVel);
            int aimResult = weapon?.Projectile != null
                ? weapon.getAimAngle(targetState)
                : Helpers.computeLeadFireAngle(bot._state, targetState, leadVel);

            double aimError = Helpers.calculateDifferenceInAngles(bot._state.yaw, aimResult);
            float rotate = ClampAxis((float)(aimError / AimAngleScale));

            if (bot.MovementMode == BotMovementMode.Api)
            {
                bot.SetDrive(-backStrength, 0f, rotate);
                bot.SetAim(aimResult, true);
            }
            else if (bot._movement is SteeringController steering)
            {
                steering.bSkipAim = true;
                steering.angle = aimResult;
                float forward = -backStrength;
                float strafe = 0f;
                BotWallAvoidance.AdjustDrive(bot, ref forward, ref strafe, ref rotate);
                SetDrive(steering, forward, strafe, rotate);
            }
        }

        private static void ApplyHeadingErrorAsDrive(SteeringController steering, double headingError, float strength)
        {
            ApplySteerAsDrive(steering, headingError, strength);
        }
    }
}
