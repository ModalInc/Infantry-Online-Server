using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;

using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Eol
{
    public partial class Engineer : Bot
    {
        const int EngSepPeerMax = 8;

        static bool IsAlliedEngineerPeer(Vehicle v)
        {
            return v is Engineer || v is BasicDefense || v is Captain;
        }

        Vector3 SteerTowardVehicle(InfantryVehicle vehicle, Vehicle target)
        {
            if (target == null || target.IsDead || target.Abstract == null)
                return Vector3.Zero;

            int now = Environment.TickCount;
            IEnumerable<IVehicle> peers = EolBotSenseUtil.GetSepPeers(_id, now, 500 ^ (7 << 16), EngSepPeerMax, () =>
            {
                List<IVehicle> built = new List<IVehicle>(EngSepPeerMax);
                List<Vehicle> nearby = _arena.getVehiclesInRange(
                    vehicle.state.positionX, vehicle.state.positionY, 500,
                    v => IsAlliedEngineerPeer(v) && (_team == null || v._team == _team));
                for (int i = 0; i < nearby.Count && built.Count < EngSepPeerMax; i++)
                {
                    Bot peer = nearby[i] as Bot;
                    if (peer != null && peer.Abstract != null)
                        built.Add(peer.Abstract);
                }
                return built;
            });

            Vector3 separation = vehicle.SteerForSeparation(Script_Eol.BotPackSeparationWorld, -0.707f, peers)
                * Script_Eol.BotPackSeparationWeight;
            Vector3 pursuit = vehicle.SteerForPursuit(target.Abstract, 0.35f);
            Vector3 wander = vehicle.SteerForWander(0.08f);
            return separation + pursuit + (wander * 0.25f);
        }

        /// <summary>Direct pursuit toward the active repair target.</summary>
        public Vector3 steerForFollowTurret(InfantryVehicle vehicle)
        {
            return SteerTowardVehicle(vehicle, _nextRet);
        }

        /// <summary>Pursuit toward the current healthy-circuit turret stop.</summary>
        public Vector3 steerForCircuit(InfantryVehicle vehicle)
        {
            return SteerTowardVehicle(vehicle, _circuitTarget);
        }
    }
}
