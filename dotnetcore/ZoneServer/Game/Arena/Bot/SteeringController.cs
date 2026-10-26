using System;
using System.Collections.Generic;
using Axiom.Math;
using Assets;
using Bnoerj.AI.Steering;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Bots
{
	// SteeringController Class
	/// Maintains the bot's object state based on simple movement instructions
	///////////////////////////////////////////////////////
	public class SteeringController : MovementController
	{	// Member variables
		///////////////////////////////////////////////////
		private InfantryVehicle vehicle;					//Our vehicle to model steering behavior
		public bool bSkipAim;								//Skip aiming and use the given angle
		public bool bSkipRotate;                            //Don't turn to steer to the target, just strafe and thrust
		public bool bStrafeLeft;
		public bool bStrafeRight;
		public float angle;									//The given angle to use

		public Func<InfantryVehicle, Vector3> steerDelegate;//Delegate which calculates how much steering to apply

		/// <summary>
		/// When set, path following uses smooth corridor steering on these waypoints.
		/// </summary>
		public List<Vector3>? FollowPath { get; set; }
		public int FollowPathTarget { get; set; }

		public void ClearNavigationPath()
		{
			FollowPath = null;
			FollowPathTarget = 0;
		}

		public void ClearSteerInputs()
		{
			_useAnalogDrive = false;
			_analogForward = 0f;
			_analogStrafe = 0f;
			_analogRotate = 0f;
			steerDelegate = null;
			// Keep _smooth* so the next SetAnalogDrive blends from the last commit.
			// Do not clear FollowPath here — BotMotionApplier clears inputs every poll;
			// wiping the path before re-apply was rewinding Api navigation every tick.
			stopThrusting();
			stopStrafing();
			stopRotating();
			bStrafeLeft = false;
			bStrafeRight = false;
		}
		///////////////////////////////////////////////////
		// Member Functions
		///////////////////////////////////////////////////
		/// <summary>
		/// Generic Constructor
		/// </summary>
		public SteeringController(VehInfo.Car type, Helpers.ObjectState state, Arena arena)
			: base(type, state, arena)
		{
			vehicle = new InfantryVehicle(type, _state);

			//Set our position
			vehicle.Position = _state.position();
		}

		/// <summary>
		/// Updates the state in accordance with movement instructions
		/// </summary>
		public override bool updateState(double delta)
		{
			if (TryConsumeStuck())
			{
				SetAnalogDrive(0.85f, ((_state.positionX & 8) == 0) ? -0.55f : 0.55f, 0f);
				return base.updateState(delta);
			}

			double aimDifference = 0;
			if (bSkipAim)
				aimDifference = Helpers.calculateDifferenceInAngles(_state.yaw, angle);

			if (_useAnalogDrive)
			{
				if (bSkipAim && Math.Abs(aimDifference) > 2)
				{
					float aimRotate = BotDrive.ClampAxis((float)(aimDifference / 35.0));
					SetAnalogDrive(_analogForward, _analogStrafe, aimRotate);
				}

				bSkipAim = false;
				bSkipRotate = false;
				bStrafeLeft = false;
				bStrafeRight = false;
				steerDelegate = null;
				return base.updateState(delta);
			}

			if (steerDelegate == null && FollowPath == null)
			{
				if (bSkipAim && Math.Abs(aimDifference) > 2)
					SetAnalogDrive(0f, 0f, BotDrive.ClampAxis((float)(aimDifference / 35.0)));
				else
					ClearAnalogDrive();

				bSkipAim = false;
				bSkipRotate = false;
				bStrafeLeft = false;
				bStrafeRight = false;
				steerDelegate = null;
				return base.updateState(delta);
			}

			vehicle.Position = _state.position();
			vehicle.Speed = Math.Max(0.2f, MathF.Sqrt(
				_state.velocityX * _state.velocityX + _state.velocityY * _state.velocityY) / 10000f);

			Vector3 steer;
			if (FollowPath != null && FollowPath.Count > 0)
			{
				int pathTarget = FollowPathTarget;
				steer = PathNavigationHelper.SteerAlongPathSmooth(vehicle, FollowPath, ref pathTarget);
				FollowPathTarget = pathTarget;

				if (pathTarget >= FollowPath.Count)
					ClearNavigationPath();
			}
			else if (steerDelegate != null)
			{
				steer = steerDelegate(vehicle);
			}
			else
			{
				steer = Vector3.Zero;
			}

			steer = BotWallAvoidance.BlendRepulsion(this, steer);

			BotDrive.ApplySteerVectorAsDrive(
				this,
				_state.yaw,
				steer,
				aimDifference,
				prioritizeAim: bSkipAim);

			bSkipAim = false;
			bSkipRotate = false;
			bStrafeLeft = false;
			bStrafeRight = false;
			steerDelegate = null;
			return base.updateState(delta);
		}

		#region Utility functions

		#endregion
	}
}
