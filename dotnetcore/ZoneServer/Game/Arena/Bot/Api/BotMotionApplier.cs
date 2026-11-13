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
        /// <summary>
        /// Fire height used when the shooter isn't known (local change 2026-11-13): learnt from the bots that
        /// shoot, so low physics (green/yellow) that shots fly over doesn't count as cover.
        /// </summary>
        public static int DefaultFireHeight;

        /// <summary>Shots clear a tile when it isn't blocked, or its physics top is below the fire height.</summary>
        /// <summary>Shots always clear low physics up to this height (green 16 / yellow 32 on Pioneer Station).</summary>
        public static int ShotOverPhysicsHeight = 32;
        /// <summary>Sight is blocked only by physics at least this tall (full walls, 496); everything lower is see-through.</summary>
        public static int SightBlockPhysicsHeight = 400;

        static bool ShotPasses(Arena arena, LvlInfo.Tile t, int fireHeight)
        {
            if (!t.Blocked)
                return true;
            short[] high = arena._server._assets.Level.PhysicsHigh;
            if (high == null || t.Physics >= high.Length)
                return false;
            return high[t.Physics] <= ShotOverPhysicsHeight || (fireHeight > 0 && high[t.Physics] < fireHeight);
        }

        /// <summary>Can a bot SEE along this line? Only full-height walls block sight (local 2026-11-13).</summary>
        public static bool SightLineClear(Arena arena, short fromX, short fromY, short toX, short toY)
        {
            if (arena == null)
                return false;
            short[] high = arena._server._assets.Level.PhysicsHigh;
            return Helpers.calcBresenhemsPredicate(arena, fromX, fromY, toX, toY,
                delegate (LvlInfo.Tile t)
                {
                    if (!t.Blocked)
                        return true;
                    return high != null && t.Physics < high.Length && high[t.Physics] < SightBlockPhysicsHeight;
                });
        }

        public static bool TileLineClearForWeaponFire(Arena arena, short fromX, short fromY, short toX, short toY)
        {
            return ShotLineClear(arena, DefaultFireHeight, fromX, fromY, toX, toY);
        }

        /// <summary>Tile LOS for a projectile fired at <paramref name="fireHeight"/> (low walls don't block it).</summary>
        public static bool ShotLineClear(Arena arena, int fireHeight, short fromX, short fromY, short toX, short toY)
        {
            if (arena == null)
                return false;

            return Helpers.calcBresenhemsPredicate(arena, fromX, fromY, toX, toY,
                delegate (LvlInfo.Tile t) { return ShotPasses(arena, t, fireHeight); });
        }

        static int FireHeightOf(Bot bot)
        {
            int fh = bot?._type != null ? bot._type.FireHeight : 0;
            if (fh > 0 && fh > DefaultFireHeight)
                DefaultFireHeight = fh;
            return fh > 0 ? fh : DefaultFireHeight;
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

            // Maser carriers shoot through walls - anything within the maser's reach is "in the open" for them.
            WeaponController maser = BotWeaponUtil.WallPiercingWeapon(bot);
            if (maser != null)
            {
                int mr = maser.ItemID != 0 ? BotWeaponUtil.GetCombatRangeRaw(AssetManager.Manager?.getItemByID(maser.ItemID)) : 0;
                if (mr > 0 && Helpers.distanceTo(bot._state, targetState) <= Math.Max(mr * 1.5, 320))
                    return true;
            }

            return ShotLineClear(
                    bot._arena,
                    FireHeightOf(bot),
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

            if (requireLineOfSight && !BotWeaponUtil.IsWallPiercing(weapon)
                && !ShotLineClear(bot._arena, FireHeightOf(bot), bot._state.positionX, bot._state.positionY, targetState.positionX, targetState.positionY))
            {
                return false;
            }

            if (!weapon.ableToFire())
                return false;

            // Splash weapons: never point blank; and nobody fires beyond the gun's reach (safety net for every
            // fire path - bots were trading shots from miles away that could never land).
            double fireDist = Helpers.distanceTo(bot._state, targetState);
            int minRange = BotWeaponUtil.GetMinFireRangeRaw(weapon);
            if (minRange > 0 && fireDist < minRange)
                return false;
            int reach = weapon.ItemID != 0 ? BotWeaponUtil.GetCombatRangeRaw(AssetManager.Manager?.getItemByID(weapon.ItemID)) : 0;
            if (reach > 0 && fireDist > Math.Max(reach * 1.5, 320))
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
            try { BotShotFired?.Invoke(bot, weapon, targetState); }
            catch { /* script hook must never break firing */ }
            return true;
        }

        /// <summary>
        /// Raised after every bot shot (local change 2026-11-13). Bot projectiles are only hit-tested by player
        /// clients, so a shot at another BOT never lands on its own - the gametype resolves those hits here.
        /// </summary>
        public static Action<Bot, WeaponController, Helpers.ObjectState> BotShotFired;

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
