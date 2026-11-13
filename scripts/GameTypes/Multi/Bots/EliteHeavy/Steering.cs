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

namespace InfServer.Script.GameType_Multi
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class EliteHeavy : Bot
    {
        #region Steer Delegates
        /// <summary>
        /// Moves the medic on a persuit course towards the player, while keeping seperated from other medics
        /// </summary>
        public Vector3 steerForPersuePlayer(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            List<Vehicle> marines = _arena.getVehiclesInRange(vehicle.state.positionX, vehicle.state.positionY, 500,
                                                                delegate (Vehicle v)
                                                                { return (v is Marine); });
            IEnumerable<IVehicle> marinebots = marines.ConvertAll<IVehicle>(
                delegate (Vehicle v)
                {
                    return (v as Marine).Abstract;
                }
            );

            Vector3 seperationSteer = vehicle.SteerForSeparation(_seperation, -0.707f, marinebots);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 0.2f);

            return (seperationSteer * 1.3f) + pursuitSteer;
        }

        /// <summary>
        /// Keeps the bot around a specific player
        /// </summary>
        public Vector3 steerForFollowOwner(InfantryVehicle vehicle)
        {
            if (_leader == null)
                return Vector3.Zero;

            Vector3 wanderSteer = vehicle.SteerForWander(0.5f);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_leader._baseVehicle.Abstract, 0.2f);

            return (wanderSteer * 1.6f) + pursuitSteer;
        }

        /// <summary>
        /// Keeps the bot around a specific player
        /// </summary>
        public Vector3 strafeForCombat(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            Vector3 wanderSteer = vehicle.SteerForWander(0.5f);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_leader._baseVehicle.Abstract, 0.2f);

            return (wanderSteer * 1.6f) + pursuitSteer;
        }

        #endregion
    }
}
