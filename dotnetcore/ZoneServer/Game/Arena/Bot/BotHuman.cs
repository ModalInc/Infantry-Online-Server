using System;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Bots
{
    /// <summary>
    /// Player-like combat layer shared by every bot: skill-tier aim (reaction, settle, held bias,
    /// lead error, shot noise), projectile dodging and pinned-against-a-wall recovery.
    /// Aim runs through <see cref="WeaponController"/>, so script bots get it without changes;
    /// dodge and pinned recovery apply to <see cref="BotMovementMode.Api"/> bots.
    /// </summary>
    public partial class Bot
    {
        BotSkillProfile _skill;
        string _skillMode;
        string _skillAlias;
        Random _humanRng;

        /// <summary>
        /// Set false for bot scripts that run their own reaction / aim-error model, so the shared
        /// skill layer does not stack on top of it.
        /// </summary>
        public bool UseSharedSkillLayer { get; set; } = true;

        /// <summary>Force this bot's skill tier ("ace", "veteran", …) regardless of the zone mix (local 2026-11-13).</summary>
        public string SkillModeOverride { get; set; }
        string _skillOverrideUsed;

        /// <summary>This bot's combat traits; null when <c>arena/bots/skill</c> is <c>off</c> or the bot opted out.</summary>
        public BotSkillProfile Skill
        {
            get
            {
                if (!BotFeatures.SkillEnabled || !UseSharedSkillLayer)
                    return null;
                // Re-roll if the alias or zone mode changed (alias is assigned after construction).
                if (_skill == null || _skillMode != BotFeatures.SkillMode || !string.Equals(_skillAlias, _alias, StringComparison.Ordinal)
                    || !string.Equals(_skillOverrideUsed, SkillModeOverride, StringComparison.Ordinal))
                {
                    _skillMode = BotFeatures.SkillMode;
                    _skillAlias = _alias;
                    _skillOverrideUsed = SkillModeOverride;
                    _skill = BotSkillProfile.Roll(_alias, _id * 7919 + 17, SkillModeOverride);
                }
                return _skill;
            }
        }

        Random HumanRng => _humanRng ??= new Random(unchecked(_id * 31337 + Environment.TickCount));

        #region Aim

        Helpers.ObjectState _aimTarget;
        Helpers.ObjectState _aimPrevTarget;
        int _aimLastTick;
        int _aimPrevLastTick;
        int _aimFirstShotAt;
        int _aimTrackAt;
        int _aimBiasAt;
        double _aimBias;
        double _aimLeadBias;
        double _aimShotNoise;
        /// <summary>A short gap (reload, brief cover) keeps the same engagement.</summary>
        const int AimEngagementKeepMs = 4000;

        void WireWeaponOwners()
        {
            if (_weapon != null && _weapon.Owner == null) _weapon.Owner = this;
            if (_weaponClose != null && _weaponClose.Owner == null) _weaponClose.Owner = this;
            if (_weaponFar != null && _weaponFar.Owner == null) _weaponFar.Owner = this;
            if (_combatWeaponLocked != null && _combatWeaponLocked.Owner == null) _combatWeaponLocked.Owner = this;
        }

        /// <summary>
        /// Exact lead angle → where this bot actually aims: straight line plus the lead misjudged by the
        /// engagement's lead error, plus a held bias (re-drawn every 0.6–1.6 s) and per-shot noise. Both
        /// errors shrink ×0.88 per second while it keeps tracking the same target.
        /// </summary>
        internal int ApplyHumanAim(Helpers.ObjectState target, int leadYaw)
        {
            BotSkillProfile sk = Skill;
            if (sk == null || target == null || _state == null)
                return leadYaw;

            int now = Environment.TickCount;
            double straight = Helpers.calculateDegreesBetweenPoints(
                target.positionX, target.positionY, _state.positionX, _state.positionY);

            if (!ReferenceEquals(target, _aimTarget) || now - _aimLastTick > AimEngagementKeepMs)
            {
                bool backToPrev = ReferenceEquals(target, _aimPrevTarget) && now - _aimPrevLastTick <= AimEngagementKeepMs;
                Helpers.ObjectState old = _aimTarget;
                int oldTick = _aimLastTick;
                if (!backToPrev)
                    BeginAimEngagement(sk, now, straight);
                _aimPrevTarget = old;
                _aimPrevLastTick = oldTick;
                _aimTarget = target;
            }
            _aimLastTick = now;

            int dt = now - _aimTrackAt;
            if (dt > 0)
            {
                double f = Math.Pow(0.88, dt / 1000.0);
                _aimBias *= f;
                _aimLeadBias *= f;
                _aimTrackAt = now;
            }
            if (now - _aimBiasAt >= 0)
            {
                _aimBias = BotSkillProfile.Gauss(HumanRng, 0.0, sk.AimSd);
                _aimBiasAt = now + 600 + HumanRng.Next(1001);
            }

            double leadPart = Helpers.calculateDifferenceInAngles(straight, leadYaw);
            double yaw = straight + leadPart * (1.0 + _aimLeadBias) + _aimBias + _aimShotNoise;
            return (int)Math.Round(BotWallAvoidance.WrapYaw(yaw)) % 240;
        }

        void BeginAimEngagement(BotSkillProfile sk, int now, double straightYaw)
        {
            double turnDeg = Math.Abs(Helpers.calculateDifferenceInAngles(_state.yaw, straightYaw)) * 1.5;
            int wait = sk.ReactionMs(HumanRng, turnDeg) + sk.SettleMs(HumanRng);
            _aimFirstShotAt = (now + wait) | 1;
            _aimBias = BotSkillProfile.Gauss(HumanRng, 0.0, sk.AimSd * 1.4);
            _aimBiasAt = now + 700 + HumanRng.Next(600);
            _aimLeadBias = BotSkillProfile.Gauss(HumanRng, 0.0, sk.LeadSd);
            _aimShotNoise = 0;
            _aimTrackAt = now;
        }

        /// <summary>False until the reaction + settle after picking up a new target have passed.</summary>
        internal bool HumanReadyToFire()
        {
            if (Skill == null || _aimFirstShotAt == 0)
                return true;
            return Environment.TickCount - _aimFirstShotAt >= 0;
        }

        internal void OnHumanShotFired()
        {
            BotSkillProfile sk = Skill;
            _aimShotNoise = sk != null ? BotSkillProfile.Gauss(HumanRng, 0.0, sk.ShotSd) : 0;
        }

        #endregion

        #region Dodge

        readonly double[] _inboundBuf = new double[8 * 6];
        int _dodgeNext;
        int _dodgeUntil;
        double _dodgeYaw;
        int _dodgeStandUntil;

        static readonly double[] DodgeOffsetsDash = { 0, 20, -20, 40, -40, 60, -60 };
        static readonly double[] DodgeOffsetsFight = { 0, 30, -30, 60, -60, 90, -90, 120 };

        /// <summary>Penetration of the tracked shots into a body that sets off along yaw (NaN = stands) over the next 0.7 s.</summary>
        static double DodgeCost(double[] s, int n, double yaw, int reactTicks)
        {
            double dx = 0, dy = 0;
            if (!double.IsNaN(yaw))
            {
                double r = yaw * 1.5 * Math.PI / 180.0;
                dx = Math.Sin(r) * 2.4;
                dy = -Math.Cos(r) * 2.4;
            }
            double cost = 0;
            for (int j = 0; j < n; j++)
            {
                int o = j * 6;
                double life = s[o + 5];
                for (int t = Math.Max(3, reactTicks); t <= 70 && t <= life; t += 4)
                {
                    int m = t < 25 ? t : 25;   // at speed after 0.25 s
                    double rx = s[o] + s[o + 2] * t - dx * m;
                    double ry = s[o + 1] + s[o + 3] * t - dy * m;
                    double pen = s[o + 4] - Math.Sqrt(rx * rx + ry * ry);
                    if (pen > 0)
                        cost += pen;
                }
            }
            return cost;
        }

        /// <summary>World travel yaw from the current velocity; NaN when (almost) standing.</summary>
        double TravelYaw()
        {
            double vx = _state.velocityX, vy = _state.velocityY;
            if (vx * vx + vy * vy < 200.0 * 200.0)
                return double.NaN;
            double deg = Math.Atan2(vx, -vy) * 180.0 / Math.PI;
            return BotWallAvoidance.WrapYaw(deg / 1.5);
        }

        /// <summary>
        /// Weighs the current move (and alternatives around it) against enemy shots flying at the body and
        /// swerves, or stops, when that clearly helps. Travelling bots pay more to leave their route.
        /// </summary>
        void ApplyDodge(int now)
        {
            if (!BotFeatures.Dodge || _arena == null)
                return;

            if (now - _dodgeUntil < 0)
            {
                OverrideDriveToward(_dodgeYaw);
                return;
            }
            if (now - _dodgeStandUntil < 0)
            {
                OverrideStand();
                return;
            }
            if (now - _dodgeNext < 0)
                return;
            _dodgeNext = now + 70;

            int n = _arena.CollectInboundShots(this, now, _inboundBuf, 8);
            if (n == 0)
                return;

            BotSkillProfile sk = Skill;
            int reactTicks = (int)((sk != null ? sk.ReactMean : 260) * 0.5 / 10.0);
            double planned = TravelYaw();
            bool moving = !double.IsNaN(planned);
            double baseCost = DodgeCost(_inboundBuf, n, planned, reactTicks);
            if (baseCost < 6.0)
                return;
            if (HumanRng.NextDouble() > (sk != null ? sk.TierPick(0.30, 0.90) : 0.6))
                return;   // did not notice this time

            bool dash = _motionRequest.Kind == BotMotionKind.FollowNavigation;
            bool firing = _motionRequest.AimEnabled || _itemUseID != 0;
            double lambda = dash ? 3.0 : 0.4;
            double noise = sk != null ? sk.TierPick(14.0, 0.0) : 7.0;
            double from = moving ? planned : _state.yaw;

            double best = double.NaN;
            double bestScore = baseCost;
            bool standBest = false;
            if (!dash && !firing && moving)
            {
                double c0 = DodgeCost(_inboundBuf, n, double.NaN, reactTicks);
                if (c0 < bestScore - 4.0)
                {
                    bestScore = c0;
                    standBest = true;
                }
            }

            int px = _state.positionX, py = _state.positionY;
            double[] offs = dash ? DodgeOffsetsDash : DodgeOffsetsFight;
            for (int i = 0; i < offs.Length; i++)
            {
                double y = BotWallAvoidance.WrapYaw(from + offs[i]);
                if (BotWallAvoidance.ProbeOpen(this, px, py, y, 32) < 32)
                    continue;
                double c = DodgeCost(_inboundBuf, n, y, reactTicks) + lambda * Math.Abs(offs[i]) * 0.5
                    + (noise > 0 ? BotSkillProfile.Gauss(HumanRng, 0.0, noise) : 0.0);
                if (c < bestScore - 4.0)
                {
                    bestScore = c;
                    best = y;
                    standBest = false;
                }
            }

            if (standBest)
            {
                _dodgeStandUntil = now + 120;
                OverrideStand();
                return;
            }
            if (double.IsNaN(best) || bestScore > baseCost * 0.8)
                return;   // not clearly better than the plan

            _dodgeYaw = best;
            _dodgeUntil = now + 160;
            OverrideDriveToward(best);
        }

        #endregion

        #region Pinned recovery

        /// <summary>Combat strafe held flipped until this tick after a wall probe (0 = none).</summary>
        internal int _probeStrafeFlipUntil;

        int _pinT;
        int _pinUntil;
        short _pinX, _pinY;
        bool _pinWarm;
        bool _pinKeysDown;
        double _pinYaw;

        /// <summary>
        /// After movement: keys down yet under 16 px moved in 400 ms (the first window after the keys go
        /// down is thrust warm-up) → back out toward the most open compass direction for 300 ms.
        /// </summary>
        void EvaluatePinned(int now)
        {
            if (!BotFeatures.WallProbe || !(_movement is SteeringController steering))
                return;
            if (now - _pinUntil < 0)
                return;

            bool keysDown = !_movement.IsMovementFrozen
                && (Math.Abs(steering.AnalogForward) > 0.25f || Math.Abs(steering.AnalogStrafe) > 0.25f);
            if (!keysDown)
            {
                _pinKeysDown = false;
                return;
            }
            if (!_pinKeysDown)
            {
                _pinKeysDown = true;
                _pinWarm = true;
                _pinT = now;
                _pinX = _state.positionX;
                _pinY = _state.positionY;
                return;
            }
            if (now - _pinT < 400)
                return;

            int mdx = _state.positionX - _pinX, mdy = _state.positionY - _pinY;
            bool stuck = mdx * mdx + mdy * mdy < 16 * 16;
            bool warm = _pinWarm;
            _pinWarm = false;
            _pinT = now;
            _pinX = _state.positionX;
            _pinY = _state.positionY;
            if (!stuck || warm)
                return;

            int bestRoom = 0;
            double bestYaw = 0;
            for (int k = 0; k < 8; k++)
            {
                double yaw = k * 30.0;
                int room = BotWallAvoidance.ProbeOpen(this, _state.positionX, _state.positionY, yaw, 64);
                if (room > bestRoom)
                {
                    bestRoom = room;
                    bestYaw = yaw;
                }
            }
            if (bestRoom < 32)
                return;
            _pinYaw = bestYaw;
            _pinUntil = now + 300;
        }

        #endregion

        /// <summary>Runs before the motion request is applied (Api mode only).</summary>
        void ApplyCombatSenseOverrides(int now)
        {
            if (MovementMode != BotMovementMode.Api || _movement == null || _movement.IsMovementFrozen)
                return;

            if (BotFeatures.WallProbe && now - _pinUntil < 0)
            {
                OverrideDriveToward(_pinYaw);
                return;
            }
            if (_tickWallEscapeUntil > now)
                return;   // wall-escape path owns movement

            ApplyDodge(now);
        }

        /// <summary>Drive toward a world yaw while keeping whatever aim the script set (fire on the move).</summary>
        void OverrideDriveToward(double yaw)
        {
            double err = Helpers.calculateDifferenceInAngles(_state.yaw, yaw) * (Math.PI / 120.0);
            float forward = BotDrive.ClampAxis((float)Math.Cos(err));
            float strafe = BotDrive.ClampAxis((float)Math.Sin(err));
            float rotate = 0f;
            bool aim = _motionRequest.AimEnabled;
            float aimAngle = _motionRequest.AimAngle;
            BotWallAvoidance.AdjustDrive(this, ref forward, ref strafe, ref rotate);
            _motionRequest = BotMotionRequest.Drive(forward, strafe, rotate);
            _motionRequest.AimEnabled = aim;
            _motionRequest.AimAngle = aimAngle;
        }

        void OverrideStand()
        {
            bool aim = _motionRequest.AimEnabled;
            float aimAngle = _motionRequest.AimAngle;
            _motionRequest = BotMotionRequest.Drive(0f, 0f, 0f);
            _motionRequest.AimEnabled = aim;
            _motionRequest.AimAngle = aimAngle;
        }
    }
}
