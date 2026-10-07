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
    public partial class BasicDefense : Bot
    {
        const int DefenseSepPeerMax = 12;

        static bool IsAlliedDefensePeer(Vehicle v)
        {
            return v is BasicDefense || v is Captain || v is Engineer;
        }

        IEnumerable<IVehicle> BuildDefenseSepPack(short px, short py, int range)
        {
            int now = Environment.TickCount;
            int rangeKey = range;
            return EolBotSenseUtil.GetSepPeers(_id, now, rangeKey, DefenseSepPeerMax, () =>
            {
                List<IVehicle> built = new List<IVehicle>(DefenseSepPeerMax);
                List<Vehicle> defenders = _arena.getVehiclesInRange(px, py, range,
                    delegate (Vehicle v)
                    {
                        return IsAlliedDefensePeer(v)
                            && (_team == null || v._team == _team);
                    });
                for (int i = 0; i < defenders.Count && built.Count < DefenseSepPeerMax; i++)
                {
                    Bot peer = defenders[i] as Bot;
                    if (peer != null && peer.Abstract != null)
                        built.Add(peer.Abstract);
                }
                return built;
            });
        }

        Vector3 DefenseSepSteer(InfantryVehicle vehicle, int range)
        {
            IEnumerable<IVehicle> pack = BuildDefenseSepPack(vehicle.state.positionX, vehicle.state.positionY, range);
            return vehicle.SteerForSeparation(Script_Eol.BotPackSeparationWorld, -0.707f, pack)
                * Script_Eol.BotPackSeparationWeight;
        }

        #region Steer Delegates
        /// <summary>
        /// Steers the bot along the defined path
        /// </summary>
        public Vector3 steerAlongPath(InfantryVehicle vehicle)
        {
            return EolBotCombatUtil.SteerAlongPath(vehicle, ref _path, ref _pathTarget, ref _tickLastPath);
        }

        /// <summary>
        /// Moves the bot on a persuit course towards the player, while keeping seperated from other bots
        /// </summary>
        public Vector3 steerForPursueHostileVehicle(InfantryVehicle vehicle, Vehicle hostile)
        {
            if (hostile == null)
                return Vector3.Zero;

            Vector3 seperationSteer = DefenseSepSteer(vehicle, 500);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(hostile.Abstract, 0.35f);
            return seperationSteer + pursuitSteer;
        }

        public Vector3 steerForPersuePlayer(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            Vector3 seperationSteer = DefenseSepSteer(vehicle, 700);
            IVehicle enemy = (_target._baseVehicle != null) ? _target._baseVehicle.Abstract : null;
            Vector3 pursuitSteer = enemy != null
                ? vehicle.SteerForPursuit(enemy, 0.72f)
                : Vector3.Zero;

            return seperationSteer + (pursuitSteer * 1.85f);
        }

        /// <summary>
        /// Seek the current patrol point (or wander on HQ) while a path is pending.
        /// </summary>
        public Vector3 steerForWalkabout(InfantryVehicle vehicle)
        {
            // Patrol no longer uses this for motion (DriveTowardRaw). Keep a pure seek for any leftover callers.
            if (_targetPoint == null)
                return Vector3.Zero;
            Vector3 pos = new Vector3(((float)_targetPoint.positionX) / 100.0f, ((float)_targetPoint.positionY) / 100.0f, 0);
            return DefenseSepSteer(vehicle, 500) + vehicle.SteerForSeek(pos);
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

            return DefenseSepSteer(vehicle, 500) + (wanderSteer * 1.6f) + seekSteer;
        }

        /// <summary>
        /// Keeps the bot around a specific player
        /// </summary>
        public Vector3 strafeForCombat(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            double distEngage = Helpers.distanceTo(vehicle.state, _target._state);
            ItemInfo itemClose;
            ItemInfo itemFar;
            EolBotCombatUtil.ResolveWeaponItems(_type, out itemClose, out itemFar);
            EolBotCombatUtil.WeaponEngageBands bands = EolBotCombatUtil.BuildWeaponEngageBands(itemFar, itemClose);
            bool closePressure = WantClosePressure(Environment.TickCount);
            float preferOs = EolBotCombatUtil.PreferredEngageOpenSteer(bands, closePressure);
            bool engaged = distEngage <= bands.FarWeaponRaw;

            Vector3 seperationSteer = DefenseSepSteer(vehicle, 1000);

            float len = (vehicle.state.position() - _target._state.position()).Length;
            Vector3 orbitFlankSteer = EolBotCombatUtil.SteerCombatPass(
                this, vehicle, _target._state, preferOs, 0.75f);
            Vector3 pursuitEnemy = Vector3.Zero;
            Vector3 skirmishSteer = Vector3.Zero;

            if (len > 0.08f)
            {
                IVehicle enemyAbstract = (_target._baseVehicle != null) ? _target._baseVehicle.Abstract : null;
                if (enemyAbstract != null)
                    pursuitEnemy = vehicle.SteerForPursuit(enemyAbstract, 1.0f);
            }

            Vector3 wanderSteer = vehicle.SteerForWander(engaged ? 0.06f : 0.20f);
            _bStrafe = true;

            return seperationSteer
                + (orbitFlankSteer * 1.35f)
                + (pursuitEnemy * 1.0f)
                + (wanderSteer * 0.12f)
                + skirmishSteer;
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
