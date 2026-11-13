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
    public partial class Dropship : Bot
    {
        #region Steer Delegates
        /// <summary>
        /// Moves the medic on a persuit course towards the player, while keeping seperated from other medics
        /// </summary>
        public Vector3 steerForPersuePlayer(InfantryVehicle vehicle)
        {
            if (targetEnemy == null)
                return Vector3.Zero;

            List<Vehicle> dropships = _arena.getVehiclesInRange(vehicle.state.positionX, vehicle.state.positionY, 500,
                                                                delegate (Vehicle v)
                                                                { return (v is Dropship); });
            IEnumerable<IVehicle> dropshipbots = dropships.ConvertAll<IVehicle>(
                delegate (Vehicle v)
                {
                    return (v as Dropship).Abstract;
                }
            );

            Vector3 seperationSteer = vehicle.SteerForSeparation(_seperation, -0.707f, dropshipbots);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(targetEnemy.Abstract, 0.2f);

            return (seperationSteer * 1.3f) + pursuitSteer;
        }


        /// <summary>
        /// Keeps the combat bot around the engineer
        /// Change to keeping him around the HQ
        /// </summary>
        public Vector3 steerForFollowOwner(InfantryVehicle vehicle)
        {

            Vector3 pursuitSteer = vehicle.SteerForPursuit(_targetLocation.Abstract, 0.2f);
            return pursuitSteer;
        }

        #endregion
    }
}
