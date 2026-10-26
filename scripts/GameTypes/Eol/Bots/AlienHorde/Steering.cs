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
{
    public partial class AlienHorde : Bot
    {
        const int AlienSepPeerMax = 14;

        List<IVehicle> BuildAlienSepPack(short px, short py, int range)
        {
            int now = Environment.TickCount;
            return EolBotSenseUtil.GetSepPeers(_id, now, range, AlienSepPeerMax, () =>
            {
                List<IVehicle> built = new List<IVehicle>(AlienSepPeerMax);
                List<Vehicle> nearby = _arena.getVehiclesInRange(px, py, range,
                    delegate (Vehicle v) { return (v is AlienHorde); });
                for (int i = 0; i < nearby.Count && built.Count < AlienSepPeerMax; i++)
                {
                    AlienHorde a = nearby[i] as AlienHorde;
                    if (a != null)
                        built.Add(a.Abstract);
                }
                return built;
            });
        }

        Vector3 AlienSepSteer(InfantryVehicle vehicle, int range)
        {
            List<IVehicle> pack = BuildAlienSepPack(vehicle.state.positionX, vehicle.state.positionY, range);
            return vehicle.SteerForSeparation(Script_Eol.BotPackSeparationWorld, -0.707f, pack)
                * Script_Eol.BotPackSeparationWeight;
        }

        #region Steer Delegates
        public Vector3 steerAlongPath(InfantryVehicle vehicle)
        {
            return EolBotCombatUtil.SteerAlongPath(vehicle, ref _path, ref _pathTarget, ref _tickLastPath);
        }

        public Vector3 steerForPursueHostileVehicle(InfantryVehicle vehicle, Vehicle hostile)
        {
            if (hostile == null)
                return Vector3.Zero;

            Vector3 seperationSteer = AlienSepSteer(vehicle, 500);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(hostile.Abstract, 0.35f);
            return seperationSteer + pursuitSteer;
        }

        public Vector3 steerForPersuePlayer(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            // Pursue the player — never the crash ship (old bug steered packs back to HQ mid-fight).
            Vector3 seperationSteer = AlienSepSteer(vehicle, 700);
            IVehicle enemy = (_target._baseVehicle != null) ? _target._baseVehicle.Abstract : null;
            Vector3 pursuitSteer = enemy != null
                ? vehicle.SteerForPursuit(enemy, 0.85f)
                : Vector3.Zero;

            return seperationSteer + (pursuitSteer * 1.9f);
        }

        /// <summary>
        /// Seek the current patrol point; otherwise soft wander near the crash ship.
        /// </summary>
        public Vector3 steerForWalkabout(InfantryVehicle vehicle)
        {
            if (_targetPoint == null)
                return Vector3.Zero;
            Vector3 pos = new Vector3(((float)_targetPoint.positionX) / 100.0f, ((float)_targetPoint.positionY) / 100.0f, 0);
            return AlienSepSteer(vehicle, 500) + vehicle.SteerForSeek(pos);
        }

        /// <summary>Legacy alias for walkabout.</summary>
        public Vector3 steerForPatrol(InfantryVehicle vehicle)
        {
            return steerForWalkabout(vehicle);
        }

        public Vector3 steerForHQ(InfantryVehicle vehicle)
        {
            if (_targetPoint == null)
                return Vector3.Zero;

            Vector3 pos = new Vector3(((float)_targetPoint.positionX) / 100.0, ((float)_targetPoint.positionY) / 100.0, 0);
            Vector3 wanderSteer = vehicle.SteerForWander(0.5f);
            Vector3 seekSteer = vehicle.SteerForSeek(pos);

            return AlienSepSteer(vehicle, 500) + (wanderSteer * 1.2f) + seekSteer;
        }

        public Vector3 strafeForCombat(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            ItemInfo itemClose;
            ItemInfo itemFar;
            EolBotCombatUtil.ResolveWeaponItems(_type, out itemClose, out itemFar);
            EolBotCombatUtil.WeaponEngageBands bands = EolBotCombatUtil.BuildWeaponEngageBands(itemFar, itemClose);
            float preferOs = EolBotCombatUtil.PreferredEngageOpenSteer(bands, false);

            // Separation only — cohesion was stacking the horde on top of each other.
            Vector3 seperationSteer = AlienSepSteer(vehicle, 1000);

            float len = (vehicle.state.position() - _target._state.position()).Length;
            Vector3 orbitFlankSteer = EolBotCombatUtil.SteerCombatPass(
                this, vehicle, _target._state, preferOs, 0.82f);
            Vector3 pursuitEnemy = Vector3.Zero;

            if (len > 0.08f && _target._baseVehicle != null)
                pursuitEnemy = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 1.0f);

            Vector3 wanderSteer = vehicle.SteerForWander(0.06f);
            _bStrafe = true;

            return seperationSteer
                + (orbitFlankSteer * 1.15f)
                + (pursuitEnemy * 1.35f)
                + (wanderSteer * 0.08f);
        }

        public Vector3 standStill(InfantryVehicle vehicle)
        {
            _path = null;
            _tickLastPath = 0;
            return Vector3.Zero;
        }

        #endregion
    }
}
