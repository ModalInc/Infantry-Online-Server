// CTF:X server-side bots - CLIENT MOVEMENT PHYSICS (port of the Infantry client integrator for bots).
//
// Reference: infantry-cfs-studio/src/lib/weaponTest/vehiclePhysics.ts tickVehicle (reverse-engineered from the real client),
// one call per 10 ms tick, delta = 1, substeps = 1, keyboard aim, rotate ramp 250 ms, open space (no tile friction table).
// CTFXMovePhysics is PURE math (no engine types) so the golden test can drive it by reflection. CTFXClientMove is the engine
// glue: it subclasses MovementController, runs fixed 10 ms steps and adds a SIMPLE tile collision.
//
// Units (same as the client): position = pixels x 10000, velocity = pixels per 10 ms tick x 10000 (so px/s = v / 100), heading =
// 240-unit yaw x 1000 (0 = north, 60 = east). Y velocity is kept in world space and squished by 7/10 only when moving.
//
// Not Ghidra-verified in the client file (kept behind const bools so they can be switched off later):
//  UseThrustRamp          thrust ramps 0..full over ThrustDelay*10*5/4 ms from the moment any thrust key goes down
//  UseThrustFrictionScale thrust is multiplied by (1000 - rollFriction) / 1000 before it is added to the velocity

using System;

using Assets;

using InfServer.Game;
using InfServer.Protocol;
using InfServer.Bots;

namespace InfServer.Script.GameType_CTF
{
    // Vehicle numbers taken from the veh row (TerrainSpeeds[0] plus the car fields)
    internal struct CTFXMoveBody
    {
        public int Thrust, Strafe, Back, TopSpeed, Friction, Rotate, Bounce, ThrustDelay, PhysR;
    }

    // Mutable per-bot physics state
    internal struct CTFXMoveState
    {
        public int Ix, Iy;          // position x 10000
        public int Ivx, Ivy;        // velocity, 1 unit = 0.01 px/s
        public int Irot;            // heading x 1000, 0..239999
        public int RotHeldTicks;    // ticks the rotate key has been held (ramp clock, 0 = released)
        public int ThrustStartMs;   // simulated clock value when thrust started, 0 = not thrusting
        public int NowMs;           // simulated clock, +10 per step
        public int RecoilUntilMs;   // knockback: the top speed clamp is off until this simulated time (client tickRecoilIgnore)
    }

    internal static class CTFXMovePhysics
    {
        public const bool UseThrustRamp = true;           // NOT Ghidra-verified
        public const bool UseThrustFrictionScale = true;  // NOT Ghidra-verified

        private const int VP = 10000;
        private const double ThrustFactor = 480.0 / 127.0;
        private const double DiagScale = 0.7;
        private const double SpeedFactor = 10.0;
        private const double FrictionScale = 800.0 / 255.0;
        private const double RotateTimeMs = 250.0;

        private static double Inf240ToRad(double dir)
        {
            return (dir / 240) * 2 * Math.PI - Math.PI / 2;
        }

        // One 10 ms client tick in open space. keyThrust -1 back / 0 / +1 fwd, keyStrafe -1 left / +1 right, keyRotate -1 / 0 / +1.
        public static void Step10(ref CTFXMoveState s, CTFXMoveBody b, int keyThrust, int keyStrafe, int keyRotate)
        {
            s.NowMs += 10;
            int now = s.NowMs;

            // Rotate ramp: 20% of the rate at the press, linear to 100% at RotateTimeMs, reset on release
            double factor;
            if (keyRotate == 0) { s.RotHeldTicks = 0; factor = 0; }
            else
            {
                double elapsedMs = s.RotHeldTicks * 10;
                factor = Math.Min(1.0, (elapsedMs + RotateTimeMs / 4) / (RotateTimeMs * 1.25));
                s.RotHeldTicks += 1;
            }
            double effRate = (b.Rotate / 1000.0) * factor * 1.0;
            int iRate = (int)(effRate * 1000);
            if (keyRotate < 0) s.Irot -= iRate;
            else if (keyRotate > 0) s.Irot += iRate;
            s.Irot = ((s.Irot % 240000) + 240000) % 240000;
            double direction = s.Irot / 1000.0;

            // Pre-compute the thrust delta (applied after drag)
            double facingRad = Inf240ToRad(direction);
            bool thrusting = false;
            int dvx = 0, dvy = 0;
            bool hasFwd = keyThrust != 0, hasStr = keyStrafe != 0;
            double diag = (hasFwd && hasStr) ? DiagScale : 1.0;
            if (keyThrust < 0)
            {
                double r = Inf240ToRad(direction + 120);
                int t = Math.Min(b.Back == -1 ? b.Thrust : b.Back, 127);
                dvx += (int)(Math.Cos(r) * t * ThrustFactor * diag);
                dvy += (int)(Math.Sin(r) * t * ThrustFactor * diag);
                thrusting = true;
            }
            else if (keyThrust > 0)
            {
                int t = Math.Min(b.Thrust, 127);
                dvx += (int)(Math.Cos(facingRad) * t * ThrustFactor * diag);
                dvy += (int)(Math.Sin(facingRad) * t * ThrustFactor * diag);
                thrusting = true;
            }
            if (keyStrafe != 0)
            {
                double r = Inf240ToRad(keyStrafe < 0 ? direction - 60 : direction + 60);
                int t = Math.Min(b.Strafe, 127);
                dvx += (int)(Math.Cos(r) * t * ThrustFactor * diag);
                dvy += (int)(Math.Sin(r) * t * ThrustFactor * diag);
                thrusting = true;
            }

            if (thrusting)
            {
                if (s.ThrustStartMs == 0) s.ThrustStartMs = now;
                if (UseThrustRamp && b.ThrustDelay > 0)
                {
                    double rampMs = b.ThrustDelay * 10 * 5 / 4.0;
                    double elapsed = now - s.ThrustStartMs;
                    if (elapsed < rampMs)
                    {
                        double frac = elapsed / rampMs;
                        dvx = (int)(dvx * frac);
                        dvy = (int)(dvy * frac);
                    }
                }
            }
            else s.ThrustStartMs = 0;

            if (UseThrustFrictionScale && thrusting && b.Friction > 0)
            {
                long sc = Math.Max(0, 1000 - b.Friction);
                dvx = (int)((dvx * sc) / 1000);
                dvy = (int)((dvy * sc) / 1000);
            }

            // Substep (one): drag, THEN thrust, then the circular top speed clamp
            int tileFriction = (int)(b.Friction * FrictionScale);
            int dirDrag = 0;
            if (tileFriction > 0 && (s.Ivx != 0 || s.Ivy != 0))
            {
                double velRad = Math.Atan2(s.Ivy, s.Ivx);
                dirDrag = (int)(Math.Abs(Math.Sin(facingRad - velRad) * tileFriction) / 2);
            }
            int dragBase = Math.Max(0, VP - dirDrag - tileFriction);
            s.Ivx = (int)(((long)s.Ivx * dragBase) / VP);
            s.Ivy = (int)(((long)s.Ivy * dragBase) / VP);
            s.Ivx += dvx;
            s.Ivy += dvy;

            if (b.TopSpeed > 0 && now >= s.RecoilUntilMs)
            {
                int top = (int)(b.TopSpeed * SpeedFactor);
                double speedSq = (double)s.Ivx * s.Ivx + (double)s.Ivy * s.Ivy;
                if (speedSq > (double)top * top)
                {
                    double spd = Math.Sqrt(speedSq);
                    s.Ivx = (int)(((double)s.Ivx * top) / spd);
                    s.Ivy = (int)(((double)s.Ivy * top) / spd);
                }
            }

            // Position (Y squished 7/10) and the lower map bound
            s.Ix += s.Ivx;
            s.Iy += (int)(((long)s.Ivy * 7) / 10);
            if (s.Ix < 0) { s.Ix = 0; s.Ivx = 0; }
            if (s.Iy < 0) { s.Iy = 0; s.Ivy = 0; }
        }

        // Golden test entry: one scenario for one body in open space. Scenarios 1 fwd, 2 strafe right, 3 fwd+strafe (3000 ms each,
        // returns {speed px/s, ivx, ivy}); 4 strafe right 1000 ms then left (returns {zero crossing ms after the flip, speed at the flip});
        // 5 rotate right (returns {heading at 250, 500, 1000 ms}).
        public static double[] RunScenario(int thrust, int strafe, int back, int top, int fric, int rot, int bounce, int thrustDelay, int scenario)
        {
            CTFXMoveBody b = new CTFXMoveBody();
            b.Thrust = thrust; b.Strafe = strafe; b.Back = back; b.TopSpeed = top; b.Friction = fric; b.Rotate = rot;
            b.Bounce = bounce; b.ThrustDelay = thrustDelay; b.PhysR = 8;
            CTFXMoveState s = new CTFXMoveState();
            s.Ix = 8192 * VP; s.Iy = 8192 * VP;
            if (scenario <= 3)
            {
                for (int i = 0; i < 300; i++) Step10(ref s, b, scenario != 2 ? 1 : 0, scenario != 1 ? 1 : 0, 0);
                return new double[] { Math.Sqrt((double)s.Ivx * s.Ivx + (double)s.Ivy * s.Ivy) / 100.0, s.Ivx, s.Ivy };
            }
            if (scenario == 4)
            {
                for (int i = 0; i < 100; i++) Step10(ref s, b, 0, 1, 0);
                double atFlip = Math.Sqrt((double)s.Ivx * s.Ivx + (double)s.Ivy * s.Ivy) / 100.0;
                double cross = -1;
                for (int i = 1; i <= 100 && cross < 0; i++)
                {
                    Step10(ref s, b, 0, -1, 0);
                    if (s.Ivx <= 0) cross = i * 10;
                }
                return new double[] { cross, atFlip };
            }
            double h25 = 0, h50 = 0, h100 = 0;
            for (int i = 1; i <= 100; i++)
            {
                Step10(ref s, b, 0, 0, 1);
                if (i == 25) h25 = s.Irot / 1000.0;
                else if (i == 50) h50 = s.Irot / 1000.0;
                else if (i == 100) h100 = s.Irot / 1000.0;
            }
            return new double[] { h25, h50, h100 };
        }
    }

    // Engine glue. Only the Key* fields drive movement; the base class thrust/rotate/stop commands are ignored (it never runs).
    internal sealed class CTFXClientMove : MovementController
    {
        public int KeyThrust, KeyStrafe, KeyRotate;     // -1 / 0 / +1, set by the bot each poll

        private const int MaxSteps = 20;
        private const int PerpSample = 4;               // px, side samples next to the leading edge point

        private CTFXMoveBody _b;
        private CTFXMoveState _s;
        private double _accum;
        private short _lastPx, _lastPy, _lastVx, _lastVy;
        private byte _lastYaw;

        /// <summary>A movement pack mounted / left (CTFXPacks.cs): the new body's terrain-0 speeds, position and velocity kept.</summary>
        internal void Retune(VehInfo.Car type)
        {
            SpeedValues t0 = type.TerrainSpeeds[0];
            _b.Thrust = t0.RollThrust; _b.Strafe = t0.StrafeThrust; _b.Back = t0.BackwardThrust;
            _b.TopSpeed = t0.RollTopSpeed; _b.Friction = t0.RollFriction; _b.Rotate = t0.RollRotate;
            _b.Bounce = type.BouncePercent; _b.ThrustDelay = type.ThrustDelay;
            _b.PhysR = type.PhysicalRadius > 0 ? type.PhysicalRadius : 8;
        }

        public CTFXClientMove(VehInfo.Car type, Helpers.ObjectState state, Arena arena) : base(type, state, arena)
        {
            SpeedValues t0 = type.TerrainSpeeds[0];
            _b.Thrust = t0.RollThrust;
            _b.Strafe = t0.StrafeThrust;
            _b.Back = t0.BackwardThrust;
            _b.TopSpeed = t0.RollTopSpeed;
            _b.Friction = t0.RollFriction;
            _b.Rotate = t0.RollRotate;
            _b.Bounce = type.BouncePercent;
            _b.ThrustDelay = type.ThrustDelay;
            _b.PhysR = type.PhysicalRadius > 0 ? type.PhysicalRadius : 8;
            _s.Ix = state.positionX * 10000;
            _s.Iy = state.positionY * 10000;
            _s.Ivx = state.velocityX * 10;
            _s.Ivy = state.velocityY * 10;
            _s.Irot = state.yaw * 1000;
            _lastPx = state.positionX; _lastPy = state.positionY;
            _lastVx = state.velocityX; _lastVy = state.velocityY;
            _lastYaw = state.yaw;
        }

        public override bool updateState(double delta)
        {
            if (delta <= 0) return false;

            // Warps, respawns and knockback write _state from outside: take those values over
            if (_state.positionX != _lastPx || _state.positionY != _lastPy)
            {
                _s.Ix = _state.positionX * 10000;
                _s.Iy = _state.positionY * 10000;
            }
            if (_state.velocityX != _lastVx || _state.velocityY != _lastVy)
            {
                _s.Ivx = _state.velocityX * 10;
                _s.Ivy = _state.velocityY * 10;
            }
            if (_state.yaw != _lastYaw) _s.Irot = _state.yaw * 1000;

            _accum += delta;
            int steps = (int)(_accum / 10.0);
            if (steps > MaxSteps) { steps = MaxSteps; _accum = 0; }
            else _accum -= steps * 10.0;

            for (int i = 0; i < steps; i++)
            {
                int kt = KeyThrust, ks = KeyStrafe, kr = KeyRotate;
                if (!bEnabled) { kt = 0; ks = 0; kr = 0; }
                int ox = _s.Ix, oy = _s.Iy;
                CTFXMovePhysics.Step10(ref _s, _b, kt, ks, kr);
                MoveWithCollision(ox, oy);
            }

            _state.positionX = _lastPx = (short)(_s.Ix / 10000);
            _state.positionY = _lastPy = (short)(_s.Iy / 10000);
            _state.velocityX = _lastVx = ClampShort(_s.Ivx / 10);
            _state.velocityY = _lastVy = ClampShort(_s.Ivy / 10);
            _state.yaw = _lastYaw = (byte)(_s.Irot / 1000);
            _state.direction = KeyDirection();
            _state.lastUpdate = Environment.TickCount;
            return true;
        }

        // Explosion knockback in client velocity units (CTFXBot.Knockback): add, or set when absolute, and lift the top speed clamp
        // for ignoreMs so a big push travels its full distance. Capped at MaxKnock so one 10 ms step can never carry the bot
        // through a 16 px wall tile (the simple collision only tests where a step lands).
        public void Knock(int dIvx, int dIvy, bool absolute, int ignoreMs)
        {
            if (absolute) { _s.Ivx = dIvx; _s.Ivy = dIvy; }
            else { _s.Ivx += dIvx; _s.Ivy += dIvy; }
            double spd = Math.Sqrt((double)_s.Ivx * _s.Ivx + (double)_s.Ivy * _s.Ivy);
            if (spd > MaxKnock)
            {
                _s.Ivx = (int)(_s.Ivx * MaxKnock / spd);
                _s.Ivy = (int)(_s.Ivy * MaxKnock / spd);
            }
            if (ignoreMs > 0 && _s.NowMs + ignoreMs > _s.RecoilUntilMs) _s.RecoilUntilMs = _s.NowMs + ignoreMs;
        }

        private const double MaxKnock = 12 * 10000;     // 12 px per 10 ms tick

        private static short ClampShort(int v)
        {
            return (short)(v > 32767 ? 32767 : (v < -32768 ? -32768 : v));
        }

        // Same mapping as MovementController.getDirection, from our keys
        private Helpers.ObjectState.Direction KeyDirection()
        {
            if (KeyThrust > 0)
                return KeyStrafe < 0 ? Helpers.ObjectState.Direction.NorthWest : (KeyStrafe > 0 ? Helpers.ObjectState.Direction.NorthEast : Helpers.ObjectState.Direction.Forward);
            if (KeyThrust < 0)
                return KeyStrafe < 0 ? Helpers.ObjectState.Direction.SouthWest : (KeyStrafe > 0 ? Helpers.ObjectState.Direction.SouthEast : Helpers.ObjectState.Direction.Backward);
            if (KeyStrafe < 0) return Helpers.ObjectState.Direction.StrafeLeft;
            if (KeyStrafe > 0) return Helpers.ObjectState.Direction.StrafeRight;
            return Helpers.ObjectState.Direction.None;
        }

        private bool SolidAt(int px, int py)
        {
            if (px < 0 || py < 0) return true;
            int tx = px >> 4, ty = py >> 4;
            if (tx >= _arena._levelWidth || ty >= _arena._levelHeight) return true;
            return _arena._tiles[ty * _arena._levelWidth + tx].Blocked;
        }

        // Step10 moved the free-space position; redo it per axis from (ox, oy). Leading edge = centre +- physR on the moving axis
        // (centre and +-PerpSample across it). A hit keeps the old coordinate and bounces that axis velocity.
        private void MoveWithCollision(int ox, int oy)
        {
            int tx = _s.Ix, ty = _s.Iy;
            _s.Ix = ox; _s.Iy = oy;
            int r = _b.PhysR;
            if (tx != ox)
            {
                int lead = (tx / 10000) + (tx > ox ? r : -r), cy = oy / 10000;
                if (SolidAt(lead, cy) || SolidAt(lead, cy - PerpSample) || SolidAt(lead, cy + PerpSample))
                    _s.Ivx = (int)(-(long)_s.Ivx * _b.Bounce / 1000);
                else _s.Ix = tx;
            }
            if (ty != oy)
            {
                int lead = (ty / 10000) + (ty > oy ? r : -r), cx = _s.Ix / 10000;
                if (SolidAt(cx, lead) || SolidAt(cx - PerpSample, lead) || SolidAt(cx + PerpSample, lead))
                    _s.Ivy = (int)(-(long)_s.Ivy * _b.Bounce / 1000);
                else _s.Iy = ty;
            }
        }
    }
}
/* PARSER GUARD - keep last */
