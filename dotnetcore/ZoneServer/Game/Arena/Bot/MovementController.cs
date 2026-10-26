using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

using Axiom.Math;

namespace InfServer.Bots
{
	// MovementController Class
	/// Maintains the bot's object state based on simple movement instructions
	///////////////////////////////////////////////////////
    public class MovementController
	{	// Member variables
		///////////////////////////////////////////////////
		protected VehInfo.Car _type;					//The type of vehicle we represent
		protected Helpers.ObjectState _state;			//Our internal object state
		protected Arena _arena;                         //The arena we're in
		public bool bEnabled;							//Are we able to accept movement commands?

		internal Arena MovementArena => _arena;
		internal Helpers.ObjectState MovementState => _state;
		internal VehInfo.Car MovementCarType => _type;
        public bool bCollision;
        public Vector3 _lastCollision;

        //Hardcode the terrain for now until we get access to the map info!
        private int _terrainType = 0;
		
		//Our internal representation of position/velocity/direction
		//These are vectors using float precision
		private Vector2 _position;
		private Vector2 _velocity;
		private double _direction;
		private double _thrustDirection;

		//Our vehicle movement statistics, we use these as they may be
		//changed due to various things in the game world
		private int _rollTopSpeed;
		private int _rollThrust;
		private int _rollThrustStrafe;
		private int _rollThrustBack;
		private double _rollFriction;
		private int _rollRotate;

		//Internals which define what the bot should do each update
		private bool _isThrustingForward = false;
		private bool _isThrustingBackward = false;
		private bool _isStrafingLeft = false;
		private bool _isStrafingRight = false;
		private bool _isRotatingLeft = false;
		private bool _isRotatingRight = false;

		private int _tickMovementFrozenUntil;

		private int _tickStuckCheckStart;
		private Vector2 _stuckCheckPosition;
		private int _stuckCollisionCount;
		public bool IsStuck { get; private set; }
		private bool _isThrusting;
		private int _wallContactAxisX;
		private int _wallContactAxisY;

		protected float _analogForward;
		protected float _analogStrafe;
		protected float _analogRotate;
		protected bool _useAnalogDrive;
		/// <summary>Smoothed drive axes — survive ClearSteerInputs so Drive commits blend across polls.</summary>
		protected float _smoothForward;
		protected float _smoothStrafe;
		protected float _smoothRotate;

		private const int MaxFreezeDurationMs = 350;
		private const double AnalogDeadzone = 0.07;
		/// <summary>Blend drive inputs over several polls — high rates cause vel snap / client leaps.</summary>
		private const float AnalogBlendRate = 0.22f;

		/// <summary>Ignore further explosion recoil until this tick (weapon IgnoreTime).</summary>
		private int _tickRecoilIgnoreUntil;
		/// <summary>Sustained recoil acceleration (when WeaponDuration &gt; 0).</summary>
		private double _recoilAccelX;
		private double _recoilAccelY;
		private int _tickRecoilForceUntil;

        public bool IsMoving =>
			_isThrusting || _isStrafingLeft || _isStrafingRight ||
			_velocity.LengthSquared > 4 ||
			(_useAnalogDrive && (Math.Abs(_analogForward) > AnalogDeadzone || Math.Abs(_analogStrafe) > AnalogDeadzone));

		/// <summary>Current analog axes (for aim-only rotate without wiping drive).</summary>
		public float AnalogForward => _analogForward;
		public float AnalogStrafe => _analogStrafe;
		public float AnalogRotate => _analogRotate;
		// Member Functions
		///////////////////////////////////////////////////
		/// <summary>
		/// Generic Constructor
		/// </summary>
        public MovementController(VehInfo.Car type, Helpers.ObjectState state, Arena arena)
        {
			_type = type;
			_state = state;
            _arena = arena;
			bEnabled = true;

			_position = new Vector2(state.positionX, state.positionY);
			_velocity = new Vector2(state.velocityX, state.velocityY);
			_direction = state.yaw;

			//Copy over the relevant vehicle values
			SpeedValues t0 = type.TerrainSpeeds[0];

			_rollFriction = t0.RollFriction;
			_rollThrust = t0.RollThrust * 4800 / 128;
			_rollThrustStrafe = t0.StrafeThrust * 4800 / 128;
			_rollThrustBack = t0.BackwardThrust * 4800 / 128;
			_rollRotate = t0.RollRotate;
			_rollTopSpeed = t0.RollTopSpeed;
        }

        /// <summary>
        /// Reapplies base terrain stats plus utility item movement bonuses.
        /// </summary>
        public void ApplyUtilityMovementModifiers(BotUtilityMovementModifiers mods)
        {
            SpeedValues t0 = _type.TerrainSpeeds[_terrainType];

            _rollFriction = t0.RollFriction;
            _rollThrust = (t0.RollThrust + mods.ThrustBonus + mods.ForwardThrustBonus) * 4800 / 128;
            _rollThrustStrafe = (t0.StrafeThrust + mods.StrafeThrustBonus) * 4800 / 128;
            _rollThrustBack = (t0.BackwardThrust + mods.ReverseThrustBonus) * 4800 / 128;
            _rollRotate = t0.RollRotate + mods.RotateBonus;
            _rollTopSpeed = t0.RollTopSpeed + mods.TopSpeedBonus;
        }

		/// <summary>
		/// Calculates the new angle direction based on a change
		/// </summary>
        public double calculateNewDirection(double direction, int change)
        {
            direction += change;
            direction %= 240;

            if (direction < 0)
                direction = 240 + direction;

            return direction;
        }

        /// <summary>
        /// Updates the state in accordance with movement instructions
        /// </summary>
        public virtual bool updateState(double delta)
        {   //If we're dead
            if (delta <= 0 || !bEnabled)
                return false;

            //Are we frozen?
            if (_tickMovementFrozenUntil != 0 && _tickMovementFrozenUntil > Environment.TickCount)
            {
                _isThrustingForward = false;
                _isThrustingBackward = false;
                _isStrafingLeft = false;
                _isStrafingRight = false;
                // Analog bots ignored freeze before — that let them keep accelerating
                // through "stationary fire" locks and look like hops.
                if (_useAnalogDrive)
                {
                    _analogForward = 0f;
                    _analogStrafe = 0f;
                    _smoothForward *= 0.5f;
                    _smoothStrafe *= 0.5f;
                }
            }
            else
                _tickMovementFrozenUntil = 0;

            //Get our speed values for our current terrain
            SpeedValues stats = _type.TerrainSpeeds[_terrainType];

            //Apply our rotation
            if (_useAnalogDrive)
            {
                if (Math.Abs(_analogRotate) > AnalogDeadzone)
                {
                    double rotateScale = Math.Min(1.0, Math.Abs(_analogRotate));
                    _direction += ((_rollRotate / 10000.0d) * delta) * rotateScale * Math.Sign(_analogRotate);
                }
            }
            else if (_isRotatingLeft)
                _direction -= ((_rollRotate / 10000.0d) * delta);
            else if (_isRotatingRight)
                _direction += ((_rollRotate / 10000.0d) * delta);

            _direction %= 240;

            if (_direction < 0)
                _direction = 240 + _direction;

            //Vectorize our direction, unf.
            Vector2 directionVector = Vector2.createUnitVector(_direction);
            Vector2 accelVector = Vector2.Zero;

            //Apply our thrusting instructions
            _thrustDirection = 0;
            _isThrusting = false;

            if (_useAnalogDrive)
                applyAnalogThrust(directionVector, ref accelVector);
            else
                applyBooleanThrust(directionVector, ref accelVector);

            //Apply our acceleration
            _velocity.x += accelVector.x * (delta / 1000.0d);
            _velocity.y += accelVector.y * (delta / 1000.0d);

            // Sustained explosion recoil (WeaponDuration), if any.
            if (_tickRecoilForceUntil != 0)
            {
                if (_tickRecoilForceUntil > Environment.TickCount)
                {
                    _velocity.x += _recoilAccelX * (delta / 1000.0d);
                    _velocity.y += _recoilAccelY * (delta / 1000.0d);
                }
                else
                {
                    _tickRecoilForceUntil = 0;
                    _recoilAccelX = 0;
                    _recoilAccelY = 0;
                }
            }

            //Apply friction
            double frictionMod = 1000 - ((((((double)_rollFriction) * 800) / 256) / 10) * (delta / 10.0d));

            _velocity.x = ((_velocity.x * frictionMod) / 1000.0d);
            _velocity.y = ((_velocity.y * frictionMod) / 1000.0d);

            //Clamp our resulting velocity
            if (_velocity.Length >= _rollTopSpeed / 1)
                _velocity.Normalize(_rollTopSpeed / 1);

            //Calculate our new position
            double xPerTick = _velocity.x / 10000.0d;
            double yPerTick = _velocity.y / 10000.0d;

            yPerTick *= 0.70f;          //Y coordinates are bigger than x by 0.7?

            Vector2 newPosition = new Vector2(_position.x + (xPerTick * delta), _position.y + (yPerTick * delta));

            //Clamp our position
            newPosition.x = Math.Min(newPosition.x, (AssetManager.Manager.Level.Width - 1) * 16);
            newPosition.x = Math.Max(newPosition.x, 0);
            newPosition.y = Math.Min(newPosition.y, (AssetManager.Manager.Level.Height - 1) * 16);
            newPosition.y = Math.Max(newPosition.y, 0);

            //Check for collisions
            int tileX = (int)Math.Floor(_position.x / 16);
            int tileY = (int)Math.Floor(_position.y / 16);
            int newTileX = (int)Math.Floor(newPosition.x / 16);
            int newTileY = (int)Math.Floor(newPosition.y / 16);

            LvlInfo.Tile tile = _arena._tiles[(newTileY * _arena._levelWidth) + newTileX];
            LvlInfo level = _arena._server._assets.Level;
            //Collision detection. Will expand on this later
            if (tile.Blocked && canRollOver((int)newPosition.x, (int)newPosition.y, _type))
            {
                bCollision = true;
                _lastCollision = new Vector3(((float)_position.x) / 100.0, ((float)newPosition.y) / 100.0, 0);

                _wallContactAxisX = Math.Abs(tileX - newTileX) > 0 ? Math.Sign(newPosition.x - _position.x) : 0;
                _wallContactAxisY = Math.Abs(tileY - newTileY) > 0 ? Math.Sign(newPosition.y - _position.y) : 0;

                // Slide along the wall instead of bouncing into it.
                if (_wallContactAxisX != 0 && _wallContactAxisY == 0)
                    _velocity.x *= 0.15;
                else if (_wallContactAxisY != 0 && _wallContactAxisX == 0)
                    _velocity.y *= 0.15;
                else
                {
                    _velocity.x *= 0.35;
                    _velocity.y *= 0.35;
                }

                _stuckCollisionCount++;

                _state.yaw = (byte)_direction;
                _state.direction = getDirection();
                if (_state.direction == Helpers.ObjectState.Direction.None
                    && _velocity.LengthSquared > 4)
                {
                    _state.direction = synthesizeDirectionFromVelocity();
                    if (_state.direction == Helpers.ObjectState.Direction.None)
                        _state.direction = Helpers.ObjectState.Direction.Forward;
                }

                //Clamp our resulting velocity
                if (_velocity.Length >= _rollTopSpeed / 1)
                    _velocity.Normalize(_rollTopSpeed / 1);

                //Calculate our new position
                xPerTick = _velocity.x / 10000.0d;
                yPerTick = _velocity.y / 10000.0d;

                yPerTick *= 0.70f;          //Y coordinates are bigger than x by 0.7?

                newPosition = new Vector2(_position.x + (xPerTick * delta), _position.y + (yPerTick * delta));

                //Clamp our position
                newPosition.x = Math.Min(newPosition.x, (AssetManager.Manager.Level.Width - 1) * 16);
                newPosition.x = Math.Max(newPosition.x, 0);
                newPosition.y = Math.Min(newPosition.y, (AssetManager.Manager.Level.Height - 1) * 16);
                newPosition.y = Math.Max(newPosition.y, 0);

                _position.x = newPosition.x;
                _position.y = newPosition.y;

                writeStateFromPhysics();
                return false;
            }
            else
                bCollision = false;
            
            updateStuckDetection(delta, bCollision);

            _position.x = newPosition.x;
            _position.y = newPosition.y;

            writeStateFromPhysics();

			//TODO: Apply vector tolerance to decide whether to update
			return true;
		}

        private void writeStateFromPhysics()
        {
            _state.positionX = (short)Math.Clamp(Math.Round(_position.x), short.MinValue, short.MaxValue);
            _state.positionY = (short)Math.Clamp(Math.Round(_position.y), short.MinValue, short.MaxValue);
            _state.velocityX = (short)Math.Clamp(Math.Round(_velocity.x), short.MinValue, short.MaxValue);
            _state.velocityY = (short)Math.Clamp(Math.Round(_velocity.y), short.MinValue, short.MaxValue);
            _state.yaw = (byte)_direction;
            _state.direction = getDirection();
            // Client run anim needs direction + velocity. Never wire Direction.None
            // while still sliding (combat aim often drops analog under deadzone).
            if (_state.direction == Helpers.ObjectState.Direction.None
                && _velocity.LengthSquared > 4)
            {
                _state.direction = synthesizeDirectionFromVelocity();
                if (_state.direction == Helpers.ObjectState.Direction.None)
                    _state.direction = Helpers.ObjectState.Direction.Forward;
            }
            _state.lastUpdate = Environment.TickCount;
        }

        public void SetAnalogDrive(float forward, float strafe, float rotate)
        {
            _useAnalogDrive = true;
            float tf = BotDrive.ClampAxis(forward);
            float ts = BotDrive.ClampAxis(strafe);
            float tr = BotDrive.ClampAxis(rotate);
            _smoothForward += (tf - _smoothForward) * AnalogBlendRate;
            _smoothStrafe += (ts - _smoothStrafe) * AnalogBlendRate;
            _smoothRotate += (tr - _smoothRotate) * AnalogBlendRate;
            _analogForward = BotDrive.ClampAxis(_smoothForward);
            _analogStrafe = BotDrive.ClampAxis(_smoothStrafe);
            _analogRotate = BotDrive.ClampAxis(_smoothRotate);
        }

        public void ClearAnalogDrive()
        {
            _useAnalogDrive = false;
            _analogForward = 0f;
            _analogStrafe = 0f;
            _analogRotate = 0f;
            _smoothForward = 0f;
            _smoothStrafe = 0f;
            _smoothRotate = 0f;
            stop();
        }

        private void applyBooleanThrust(Vector2 directionVector, ref Vector2 accelVector)
        {
            if (_isThrustingForward)
            {
                accelVector.x += directionVector.x * _rollThrust;
                accelVector.y += directionVector.y * _rollThrust;
                _thrustDirection = _direction;
                _isThrusting = true;
            }
            else if (_isThrustingBackward)
            {
                accelVector.x += directionVector.x * -_rollThrustBack;
                accelVector.y += directionVector.y * -_rollThrustBack;
                _thrustDirection = calculateNewDirection(_direction, 120);
                _isThrusting = true;
            }

            if (_isStrafingLeft)
            {
                double tmpX = directionVector.x;
                accelVector.x += directionVector.y * _rollThrustStrafe;
                accelVector.y += -tmpX * _rollThrustStrafe;
            }
            else if (_isStrafingRight)
            {
                double tmpX = directionVector.x;
                accelVector.x += -directionVector.y * _rollThrustStrafe;
                accelVector.y += tmpX * _rollThrustStrafe;
            }
        }

        private void applyAnalogThrust(Vector2 directionVector, ref Vector2 accelVector)
        {
            if (_analogForward > AnalogDeadzone)
            {
                accelVector.x += directionVector.x * (_rollThrust * _analogForward);
                accelVector.y += directionVector.y * (_rollThrust * _analogForward);
                _thrustDirection = _direction;
                _isThrusting = true;
            }
            else if (_analogForward < -AnalogDeadzone)
            {
                double backScale = -_analogForward;
                accelVector.x += directionVector.x * (-_rollThrustBack * backScale);
                accelVector.y += directionVector.y * (-_rollThrustBack * backScale);
                _thrustDirection = calculateNewDirection(_direction, 120);
                _isThrusting = true;
            }

            if (_analogStrafe > AnalogDeadzone)
            {
                double tmpX = directionVector.x;
                double scale = _analogStrafe;
                accelVector.x += -directionVector.y * (_rollThrustStrafe * scale);
                accelVector.y += tmpX * (_rollThrustStrafe * scale);
                _isThrusting = true;
            }
            else if (_analogStrafe < -AnalogDeadzone)
            {
                double tmpX = directionVector.x;
                double scale = -_analogStrafe;
                accelVector.x += directionVector.y * (_rollThrustStrafe * scale);
                accelVector.y += -tmpX * (_rollThrustStrafe * scale);
                _isThrusting = true;
            }
        }

		#region Movement Controls
		/// <summary>
		/// Apply explosion knockback from a projectile blast (server-side stand-in for
		/// client explosionRecoil* physics). Uses radial falloff inside RecoilRadius.
		/// </summary>
		public void ApplyExplosionRecoil(
			int blastX,
			int blastY,
			ItemInfo.Projectile wep,
			byte? blastDirectionYaw = null)
		{
			if (!bEnabled || wep == null)
				return;
			if (wep.explosionRecoilRadius <= 0)
				return;

			// Bots are vehicles: many weapons (GLs) put knockback in Vehicle with Weapon=0.
			// Repulsors put the big shove in Weapon. Use the stronger of the two.
			int weaponRecoil = wep.explosionRecoilWeapon;
			int vehicleRecoil = wep.explosionRecoilVehicle;
			int strengthBase = Math.Abs(weaponRecoil) >= Math.Abs(vehicleRecoil)
				? weaponRecoil
				: vehicleRecoil;
			if (strengthBase == 0)
				return;

			int now = Environment.TickCount;
			if (_tickRecoilIgnoreUntil != 0 && now < _tickRecoilIgnoreUntil)
				return;

			double dist = Helpers.distanceTo(blastX, blastY, (int)Math.Round(_position.x), (int)Math.Round(_position.y));
			dist -= _type.TriggerRadius;
			if (dist < 0)
				dist = 0;
			if (dist > wep.explosionRecoilRadius)
				return;

			double falloff = 1.0 - (dist / wep.explosionRecoilRadius);
			if (falloff <= 0)
				return;

			// Radial: away from blast centre.
			double dx = _position.x - blastX;
			double dy = _position.y - blastY;
			double len = Math.Sqrt(dx * dx + dy * dy);
			if (len < 1.0)
			{
				// Standing on the blast — push along facing (or blast yaw).
				byte yaw = blastDirectionYaw ?? (byte)_direction;
				double yawRad = yaw * (Math.PI / 120.0);
				dx = Math.Sin(yawRad);
				dy = Math.Cos(yawRad);
				len = 1.0;
			}
			dx /= len;
			dy /= len;

			// Blend radial with weapon/attacker facing when DirectionPercent is set.
			int dirPct = wep.explosionRecoilDirectionPercent;
			if (dirPct < 0) dirPct = 0;
			if (dirPct > 100) dirPct = 100;
			if (dirPct > 0 && blastDirectionYaw.HasValue)
			{
				double yawRad = blastDirectionYaw.Value * (Math.PI / 120.0);
				double wx = Math.Sin(yawRad);
				double wy = Math.Cos(yawRad);
				double t = dirPct / 100.0;
				dx = dx * (1.0 - t) + wx * t;
				dy = dy * (1.0 - t) + wy * t;
				len = Math.Sqrt(dx * dx + dy * dy);
				if (len > 0.0001)
				{
					dx /= len;
					dy /= len;
				}
			}

			double strength = strengthBase * falloff;
			bool absolute = wep.explosionRecoilWeaponAbsolute || wep.explosionRecoilVehicleAbsolute;
			int durationMs = wep.explosionRecoilWeaponDuration > 0
				? wep.explosionRecoilWeaponDuration * 10
				: 0;

			// Soften AI drive so thrust doesn't instantly cancel the shove.
			_smoothForward *= 0.25f;
			_smoothStrafe *= 0.25f;
			_analogForward *= 0.25f;
			_analogStrafe *= 0.25f;

			if (durationMs >= 40 && !absolute)
			{
				// Spread impulse over WeaponDuration (centiseconds → ms).
				_recoilAccelX = dx * strength * (1000.0 / durationMs);
				_recoilAccelY = dy * strength * (1000.0 / durationMs);
				_tickRecoilForceUntil = now + durationMs;
			}
			else if (absolute)
			{
				_velocity.x = dx * strength;
				_velocity.y = dy * strength;
				_tickRecoilForceUntil = 0;
				_recoilAccelX = 0;
				_recoilAccelY = 0;
			}
			else
			{
				_velocity.x += dx * strength;
				_velocity.y += dy * strength;
				_tickRecoilForceUntil = 0;
				_recoilAccelX = 0;
				_recoilAccelY = 0;
			}

			if (_velocity.Length >= _rollTopSpeed / 1)
				_velocity.Normalize(_rollTopSpeed / 1);

			writeStateFromPhysics();

			int ignoreCs = wep.explosionRecoilIgnoreTime;
			if (ignoreCs > 0)
				_tickRecoilIgnoreUntil = now + Math.Max(40, ignoreCs * 10);
			else
				_tickRecoilIgnoreUntil = now + 40;
		}

		/// <summary>
		/// Causes the zombie to cease movement for the specified period of time
		/// </summary>		
		public void freezeMovement(int duration)
		{
            if (duration > MaxFreezeDurationMs)
                duration = MaxFreezeDurationMs;

			_tickMovementFrozenUntil = Environment.TickCount + duration;
		}

		/// <summary>
		/// Causes the bot to thrust forward
		/// </summary>
		public void thrustForward()
        {
            _isThrustingForward = true;
			_isThrustingBackward = false;
        }

		/// <summary>
		/// Causes the bot to thrust backwards
		/// </summary>
        public void thrustBackward()
        {
			_isThrustingForward = false;
            _isThrustingBackward = true;
        }

		/// <summary>
		/// Causes the bot to strafe left
		/// </summary>
        public void strafeLeft()
        {
            _isStrafingLeft = true;
			_isStrafingRight = false;
        }

		/// <summary>
		/// Causes the bot to strafe right
		/// </summary>
        public void strafeRight()
        {
			_isStrafingLeft = false;
            _isStrafingRight = true;
        }

		/// <summary>
		/// Stops all thrust movement
		/// </summary>
        public void stopThrusting()
        {
            _isThrustingForward = false;
            _isThrustingBackward = false;
        }

		/// <summary>
		/// Stops all strafe movement
		/// </summary>
        public void stopStrafing()
        {
            _isStrafingLeft = false;
            _isStrafingRight = false;
        }

		/// <summary>
		/// Causes the bot to rotate left
		/// </summary>
        public void rotateLeft()
        {
            _isRotatingLeft = true;
			_isRotatingRight = false;
        }

		/// <summary>
		/// Causes the bot to rotate right
		/// </summary>
        public void rotateRight()
        {
			_isRotatingLeft = false;
            _isRotatingRight = true;
        }

		/// <summary>
		/// Stops all rotational movement
		/// </summary>
        public void stopRotating()
        {
            _isRotatingLeft = false;
            _isRotatingRight = false;
        }

		/// <summary>
		/// Stops all movement completely
		/// </summary>
		/// <remarks>May take a while for the bot to come to a stop.</remarks>
        public void stop()
        {
            stopThrusting();
            stopStrafing();
            stopRotating();
		}
        #endregion

        #region Utility functions


        public bool canRollOver(int x, int y, VehInfo type)
        {
            LvlInfo level = _arena._server._assets.Level;

            bool blocked = level.isTileBlocked(x, y, type);
            if (blocked)
            {
                x /= 16;
                y /= 16;
                int phy = level.Tiles[y * level.Width + x].Physics;
                short low = level.PhysicsLow[phy];
                short high = level.PhysicsHigh[phy];

                //Typical man vehicle: LowZ = 0 HighZ = 55
                //Physic (Green): LowZ = 0 HighZ = 16

                //Can we pass under or over?
                if (_state.positionZ > low)
                    blocked = false;   //Not blocked
            }
            return blocked;
        }
        /// <summary>
        /// Determines the hardcoded direction value from our movement instructions
        /// </summary>
        public Helpers.ObjectState.Direction getDirection()
		{
            if (_useAnalogDrive)
                return getDirectionFromAnalog();

			if (_isThrustingForward)
			{
				if (_isStrafingLeft)
					return Helpers.ObjectState.Direction.NorthWest;
				else if (_isStrafingRight)
					return Helpers.ObjectState.Direction.NorthEast;
				else
					return Helpers.ObjectState.Direction.Forward;
			}
			else if (_isThrustingBackward)
			{
				if (_isStrafingLeft)
					return Helpers.ObjectState.Direction.SouthWest;
				else if (_isStrafingRight)
					return Helpers.ObjectState.Direction.SouthEast;
				else
					return Helpers.ObjectState.Direction.Backward;
			}
			else if (_isStrafingLeft)
				return Helpers.ObjectState.Direction.StrafeLeft;
			else if (_isStrafingRight)
				return Helpers.ObjectState.Direction.StrafeRight;

			return Helpers.ObjectState.Direction.None;
		}

        private Helpers.ObjectState.Direction getDirectionFromAnalog()
        {
            if (_analogForward > AnalogDeadzone)
            {
                if (_analogStrafe > AnalogDeadzone)
                    return Helpers.ObjectState.Direction.NorthEast;
                if (_analogStrafe < -AnalogDeadzone)
                    return Helpers.ObjectState.Direction.NorthWest;
                return Helpers.ObjectState.Direction.Forward;
            }

            if (_analogForward < -AnalogDeadzone)
            {
                if (_analogStrafe > AnalogDeadzone)
                    return Helpers.ObjectState.Direction.SouthWest;
                if (_analogStrafe < -AnalogDeadzone)
                    return Helpers.ObjectState.Direction.SouthEast;
                return Helpers.ObjectState.Direction.Backward;
            }

            if (_analogStrafe > AnalogDeadzone)
                return Helpers.ObjectState.Direction.StrafeRight;
            if (_analogStrafe < -AnalogDeadzone)
                return Helpers.ObjectState.Direction.StrafeLeft;

            return Helpers.ObjectState.Direction.None;
        }

        /// <summary>
        /// Map world velocity into body-relative direction flags for client run anim
        /// when analog inputs are below the deadzone.
        /// </summary>
        Helpers.ObjectState.Direction synthesizeDirectionFromVelocity()
        {
            if (_velocity.LengthSquared < 4)
                return Helpers.ObjectState.Direction.None;

            double yawRad = _direction * (Math.PI / 120.0);
            double fx = Math.Sin(yawRad);
            double fy = Math.Cos(yawRad);
            double speed = Math.Sqrt(_velocity.x * _velocity.x + _velocity.y * _velocity.y);
            if (speed < 1)
                return Helpers.ObjectState.Direction.Forward;

            double nx = _velocity.x / speed;
            double ny = _velocity.y / speed;
            double forward = nx * fx + ny * fy;
            double strafe = nx * fy - ny * fx;

            const double axis = 0.35;
            if (forward > axis)
            {
                if (strafe > axis)
                    return Helpers.ObjectState.Direction.NorthEast;
                if (strafe < -axis)
                    return Helpers.ObjectState.Direction.NorthWest;
                return Helpers.ObjectState.Direction.Forward;
            }
            if (forward < -axis)
            {
                if (strafe > axis)
                    return Helpers.ObjectState.Direction.SouthEast;
                if (strafe < -axis)
                    return Helpers.ObjectState.Direction.SouthWest;
                return Helpers.ObjectState.Direction.Backward;
            }
            if (strafe > axis)
                return Helpers.ObjectState.Direction.StrafeRight;
            if (strafe < -axis)
                return Helpers.ObjectState.Direction.StrafeLeft;
            return Helpers.ObjectState.Direction.Forward;
        }
        #endregion

        /// <summary>
        /// Returns true once when the bot appears stuck against geometry while trying to move.
        /// </summary>
        public bool TryConsumeStuck()
        {
            if (!IsStuck)
                return false;

            IsStuck = false;
            return true;
        }

        private void updateStuckDetection(double delta, bool collided)
        {
            int now = Environment.TickCount;

            if (_tickStuckCheckStart == 0)
            {
                _tickStuckCheckStart = now;
                _stuckCheckPosition = _position;
                return;
            }

            if (now - _tickStuckCheckStart < 300)
                return;

            double moved = (_position - _stuckCheckPosition).Length;

            if (_isThrusting && (moved < 10 || _stuckCollisionCount >= 4))
                IsStuck = true;

            _tickStuckCheckStart = now;
            _stuckCheckPosition = _position;
            _stuckCollisionCount = collided ? _stuckCollisionCount : 0;
        }

        #region State Modifiers

        /// <summary>
        /// Warps the bot to the specified location
        /// </summary>
        public void warp(Helpers.ObjectState warpTo)
        {
            //Modify it's position first
            _position = new Vector2(warpTo.positionX, warpTo.positionY);
            _state.positionX = warpTo.positionX;
            _state.positionY = warpTo.positionY;

            //Update
            updateState(0);
        }

        #endregion
    }
}