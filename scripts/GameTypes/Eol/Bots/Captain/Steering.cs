using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;


using InfServer.Game;
using InfServer.Protocol;
using InfServer.Scripting;
using InfServer.Bots;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Eol
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class Captain : Bot
    {
        const int CaptainSepPeerMax = 10;

        static bool IsAlliedCaptainPeer(Vehicle v)
        {
            return v is Captain || v is BasicDefense || v is Engineer;
        }

        IEnumerable<IVehicle> BuildCaptainSepPack(short px, short py, int range)
        {
            int now = Environment.TickCount;
            int rangeKey = range;
            return EolBotSenseUtil.GetSepPeers(_id, now, rangeKey, CaptainSepPeerMax, () =>
            {
                List<IVehicle> built = new List<IVehicle>(CaptainSepPeerMax);
                List<Vehicle> nearby = _arena.getVehiclesInRange(px, py, range,
                    delegate (Vehicle v)
                    {
                        return IsAlliedCaptainPeer(v)
                            && (_team == null || v._team == _team);
                    });
                for (int i = 0; i < nearby.Count && built.Count < CaptainSepPeerMax; i++)
                {
                    Bot peer = nearby[i] as Bot;
                    if (peer != null && peer.Abstract != null)
                        built.Add(peer.Abstract);
                }
                return built;
            });
        }

        Vector3 CaptainSepSteer(InfantryVehicle vehicle, int range)
        {
            IEnumerable<IVehicle> pack = BuildCaptainSepPack(vehicle.state.positionX, vehicle.state.positionY, range);
            return vehicle.SteerForSeparation(Script_Eol.BotPackSeparationWorld, -0.707f, pack)
                * Script_Eol.BotPackSeparationWeight;
        }

        #region Steer Delegates
        /// <summary>
        /// Steers the zombie along the defined path
        /// </summary>
        public Vector3 steerAlongPath(InfantryVehicle vehicle)
        {
            return EolBotCombatUtil.SteerAlongPath(vehicle, ref _path, ref _pathTarget, ref _tickLastPath);
        }

        /// <summary>
        /// Moves the medic on a persuit course towards the player, while keeping seperated from other medics
        /// </summary>
        public Vector3 steerForPursueHostileVehicle(InfantryVehicle vehicle, Vehicle hostile)
        {
            if (hostile == null)
                return Vector3.Zero;

            Vector3 seperationSteer = CaptainSepSteer(vehicle, 500);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(hostile.Abstract, 2.0f);
            return seperationSteer + pursuitSteer;
        }

        public Vector3 steerForPersuePlayer(InfantryVehicle vehicle)
        {
            if (_target == null || _target._baseVehicle == null)
                return Vector3.Zero;

            Vector3 seperationSteer = CaptainSepSteer(vehicle, 500);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 2.2f);

            return seperationSteer + pursuitSteer;
        }

        /// <summary>
        /// Seek the current patrol point; if none, wander near HQ without hard pursuit (avoids hamster orbits).
        /// </summary>
        public Vector3 steerForWalkabout(InfantryVehicle vehicle)
        {
            if (_targetPoint == null)
                return Vector3.Zero;
            Vector3 pos = new Vector3(((float)_targetPoint.positionX) / 100.0, ((float)_targetPoint.positionY) / 100.0, 0);
            return CaptainSepSteer(vehicle, 500) + vehicle.SteerForSeek(pos);
        }

        /// <summary>Legacy alias.</summary>
        public Vector3 steerForPatrol(InfantryVehicle vehicle)
        {
            return steerForWalkabout(vehicle);
        }

        public Vector3 steerForHQ(InfantryVehicle vehicle)
        {
            if (myvHq == null)
                return Vector3.Zero;

            Vector3 wanderSteer = vehicle.SteerForWander(0.5f);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(myvHq.Abstract, 0.2f);

            return CaptainSepSteer(vehicle, 500) + (wanderSteer * 1.6f) + pursuitSteer;

        }

        /// <summary>
        /// Keeps the bot around a specific player
        /// </summary>
        public Vector3 strafeForCombat(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            ItemInfo itemClose;
            ItemInfo itemFar;
            EolBotCombatUtil.ResolveWeaponItems(_type, out itemClose, out itemFar);
            EolBotCombatUtil.WeaponEngageBands bands = EolBotCombatUtil.BuildWeaponEngageBands(itemFar, itemClose);
            bool closePressure = WantClosePressure(Environment.TickCount);
            float preferOs = EolBotCombatUtil.PreferredEngageOpenSteer(bands, closePressure);

            Vector3 seperationSteer = CaptainSepSteer(vehicle, 1000);
            float len = (vehicle.state.position() - _target._state.position()).Length;
            Vector3 orbitFlankSteer = EolBotCombatUtil.SteerCombatPass(
                this, vehicle, _target._state, preferOs, 0.75f);
            Vector3 pursuitEnemy = Vector3.Zero;

            if (len > 0.08f && _target._baseVehicle != null)
                pursuitEnemy = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 1.0f);

            Vector3 wanderSteer = vehicle.SteerForWander(0.06f);
            _bStrafe = true;

            return seperationSteer + (orbitFlankSteer * 1.35f) + (pursuitEnemy * 1.0f) + (wanderSteer * 0.08f);
        }

        public Vector3 standStill(InfantryVehicle vehicle)
        {
            _path = null;
            _tickLastPath = 0;
            return Vector3.Zero;
        }
        #endregion

        bool WantClosePressure(int now)
        {
            int phase = ((now / 1600) + _id) % 5;
            return phase <= 2;
        }
    }
}
