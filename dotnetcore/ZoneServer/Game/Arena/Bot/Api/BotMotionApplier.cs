using System;
using Axiom.Math;
using Bnoerj.AI.Steering;
using Assets;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Bots
{
    internal enum BotMotionKind
    {
        None,
        Hold,
        FollowNavigation,
        CustomSteer,
        Drive,
    }

    internal struct BotMotionRequest
    {
        public BotMotionKind Kind;
        public Func<InfantryVehicle, Vector3>? Steer;
        public bool SkipBodyRotate;
        public bool AimEnabled;
        public float AimAngle;
        public float DriveForward;
        public float DriveStrafe;
        public float DriveRotate;

        public static BotMotionRequest HoldPosition => new BotMotionRequest { Kind = BotMotionKind.Hold };

        public static BotMotionRequest FollowNavigation() =>
            new BotMotionRequest { Kind = BotMotionKind.FollowNavigation };

        public static BotMotionRequest Custom(Func<InfantryVehicle, Vector3> steer, bool skipBodyRotate = false) =>
            new BotMotionRequest { Kind = BotMotionKind.CustomSteer, Steer = steer, SkipBodyRotate = skipBodyRotate };

        public static BotMotionRequest Drive(float forward, float strafe, float rotate) =>
            new BotMotionRequest
            {
                Kind = BotMotionKind.Drive,
                DriveForward = forward,
                DriveStrafe = strafe,
                DriveRotate = rotate,
            };
    }

    internal static class BotMotionApplier
    {
        public static void Apply(Bot bot, BotMotionRequest motion)
        {
            if (bot._movement is not SteeringController steering)
                return;

            // Drive / CustomSteer / None: keep analog alive across polls (ClearSteerInputs zeros it).
            // Multi Legacy rewrites steerDelegate every poll without wiping coast — match that.
            if (motion.Kind != BotMotionKind.Drive
                && motion.Kind != BotMotionKind.None
                && motion.Kind != BotMotionKind.CustomSteer)
                steering.ClearSteerInputs();

            if (motion.AimEnabled)
            {
                steering.bSkipAim = true;
                steering.angle = motion.AimAngle;
            }

            steering.bSkipRotate = motion.SkipBodyRotate;

            switch (motion.Kind)
            {
                case BotMotionKind.Hold:
                    steering.ClearAnalogDrive();
                    steering.ClearNavigationPath();
                    break;

                case BotMotionKind.Drive:
                    steering.ClearNavigationPath();
                    steering.SetAnalogDrive(motion.DriveForward, motion.DriveStrafe, motion.DriveRotate);
                    break;

                case BotMotionKind.FollowNavigation:
                    if (bot.Navigation.HasPath)
                    {
                        bot.Navigation.ApplyToSteering(steering, null!);
                    }
                    else if (motion.Steer != null)
                    {
                        steering.ClearNavigationPath();
                        steering.steerDelegate = motion.Steer;
                    }
                    else
                    {
                        bot.Navigation.ApplyToSteering(steering, null!);
                    }
                    break;

                case BotMotionKind.CustomSteer:
                    steering.ClearNavigationPath();
                    steering.steerDelegate = motion.Steer;
                    break;

                default:
                    if (bot.Navigation.HasPath)
                        bot.Navigation.ApplyToSteering(steering, null!);
                    break;
            }
        }
    }

    /// <summary>
    /// Combat helpers for bot scripts (LOS, aim, fire).
    /// </summary>
    public static class BotCombat
    {
        public static bool TileLineClearForWeaponFire(Arena arena, short fromX, short fromY, short toX, short toY)
        {
            if (arena == null)
                return false;

            return Helpers.calcBresenhemsPredicate(arena, fromX, fromY, toX, toY,
                delegate (LvlInfo.Tile t) { return !t.Blocked; });
        }

        public static void SetAim(SteeringController steering, float angle, bool enabled = true)
        {
            if (steering == null)
                return;

            steering.bSkipAim = enabled;
            if (enabled)
                steering.angle = angle;
        }

        public static void StrafeOrbit(Bot bot, Helpers.ObjectState targetState, float forwardStrength = 0.5f, float strafeStrength = 0.42f, float orbitSign = 0f)
        {
            BotDrive.StrafeOrbit(bot, targetState, forwardStrength, strafeStrength, orbitSign);
        }

        /// <summary>True when tile LOS allows shooting (walls ahead of feet do not block fire).</summary>
        public static bool CanDirectEngage(Bot bot, Helpers.ObjectState targetState)
        {
            if (bot == null || targetState == null)
                return false;

            return TileLineClearForWeaponFire(
                    bot._arena,
                    bot._state.positionX,
                    bot._state.positionY,
                    targetState.positionX,
                    targetState.positionY);
        }

        /// <summary>True when the bot can press straight in without immediately ramming a wall.</summary>
        public static bool CanPressStraightIn(Bot bot)
        {
            return bot != null && BotWallAvoidance.ProbeAheadClear(bot);
        }

        public static void EngageOrApproach(
            Bot bot,
            Helpers.ObjectState targetState,
            Func<InfantryVehicle, Vector3>? fallbackSteer,
            float orbitForward = 0.5f,
            float orbitStrafe = 0.42f)
        {
            if (bot == null || targetState == null)
                return;

            if (CanDirectEngage(bot, targetState))
                StrafeOrbit(bot, targetState, orbitForward, orbitStrafe);
            else if (bot.MovementMode == BotMovementMode.Api)
                bot.ApplyNavigationApproach(targetState, fallbackSteer, new BotNavigateOptions { RequeueMinMs = 1100, PathStartIndex = 0 });
            else if (fallbackSteer != null && bot._movement is SteeringController steering)
                steering.steerDelegate = fallbackSteer;
        }

        public static bool TryAimAndFire(
            Bot bot,
            WeaponController weapon,
            Helpers.ObjectState targetState,
            bool requireLineOfSight = true,
            int freezeMs = 0)
        {
            if (bot == null || weapon == null || targetState == null || bot._movement is not SteeringController steering)
                return false;

            if (requireLineOfSight &&
                !TileLineClearForWeaponFire(bot._arena, bot._state.positionX, bot._state.positionY, targetState.positionX, targetState.positionY))
            {
                return false;
            }

            if (!weapon.ableToFire())
                return false;

            int aimResult = weapon.getAimAngle(targetState);

            if (bot.MovementMode == BotMovementMode.Api)
                bot.SetAim(aimResult, true);
            else
                SetAim(steering, aimResult, true);

            if (!weapon.isAimed(aimResult))
                return false;

            if (freezeMs > 0)
                bot.freezeMovement(freezeMs);

            bot._itemUseID = weapon.ItemID;
            weapon.shotFired();
            return true;
        }

        public static void ApplyBackpedalSteer(
            Bot bot,
            SteeringController steering,
            Helpers.ObjectState botState,
            WeaponController weapon,
            Helpers.ObjectState targetState)
        {
            if (bot != null && bot.MovementMode == BotMovementMode.Api)
            {
                BotDrive.BackpedalWhileAiming(bot, weapon, targetState);
                return;
            }

            if (steering == null || botState == null || targetState == null)
                return;

            BotDrive.BackpedalWhileAiming(bot, weapon, targetState);
        }

        public static void ApplyBackpedalSteer(
            SteeringController steering,
            Helpers.ObjectState botState,
            WeaponController weapon,
            Helpers.ObjectState targetState)
        {
            ApplyBackpedalSteer(null, steering, botState, weapon, targetState);
        }
    }
}
