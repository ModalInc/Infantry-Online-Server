using System;
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
    public partial class RoamingCaptain : Bot
    {
        const int RoamCaptainPackSepRange = 640;
        const int RoamCaptainAttackerPackRange = 780;

        IEnumerable<IVehicle> BuildCaptainSepPack(short px, short py, int range, int maxPeers)
        {
            int now = Environment.TickCount;
            int rangeKey = range ^ 0x10000;
            return EolBotSenseUtil.GetSepPeers(_id, now, rangeKey, maxPeers, () =>
            {
                List<IVehicle> built = new List<IVehicle>(maxPeers);
                List<Vehicle> roamingCaptains = _arena.getVehiclesInRange(px, py, range,
                    delegate (Vehicle v) { return (v is RoamingCaptain); });
                for (int i = 0; i < roamingCaptains.Count && built.Count < maxPeers; i++)
                {
                    RoamingCaptain rc = roamingCaptains[i] as RoamingCaptain;
                    if (rc != null)
                        built.Add(rc.Abstract);
                }
                return built;
            });
        }

        IEnumerable<IVehicle> BuildAttackerSepPackForCaptain(short px, short py, int range, int maxPeers)
        {
            int now = Environment.TickCount;
            int rangeKey = range ^ 0x20000;
            return EolBotSenseUtil.GetSepPeers(_id, now, rangeKey, maxPeers, () =>
            {
                List<IVehicle> built = new List<IVehicle>(maxPeers);
                List<Vehicle> attackers = _arena.getVehiclesInRange(px, py, range,
                    delegate (Vehicle v) { return (v is RoamingAttacker); });
                for (int i = 0; i < attackers.Count && built.Count < maxPeers; i++)
                {
                    RoamingAttacker ra = attackers[i] as RoamingAttacker;
                    if (ra != null)
                        built.Add(ra.Abstract);
                }
                return built;
            });
        }

        float RaiderCombatLeashWeight(float roamWeight)
        {
            return _target != null ? roamWeight * 0.12f : roamWeight;
        }

        Vector3 RaiderPdbLeashSeek(InfantryVehicle vehicle, float weightScale)
        {
            if (_baseScript == null || !_baseScript.RaiderPdbAnchorValid)
                return Vector3.Zero;

            long dx = vehicle.state.positionX - _baseScript.RaiderPdbAnchorX;
            long dy = vehicle.state.positionY - _baseScript.RaiderPdbAnchorY;
            double dist = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (dist <= Script_Eol.RaiderPdbLeashSoft)
                return Vector3.Zero;

            double span = Math.Max(12.0, (double)Script_Eol.RaiderPdbLeashHard - Script_Eol.RaiderPdbLeashSoft);
            double t = (dist - Script_Eol.RaiderPdbLeashSoft) / span;
            if (t > 1.0)
                t = 1.0;
            t *= t;

            Vector3 pdb = new Vector3(
                ((float)_baseScript.RaiderPdbAnchorX) / 100.0f,
                ((float)_baseScript.RaiderPdbAnchorY) / 100.0f,
                0);
            return vehicle.SteerForSeek(pdb) * ((float)t * weightScale);
        }

        Vector3 CaptainCaptainFlock(InfantryVehicle vehicle)
        {
            IEnumerable<IVehicle> pack = BuildCaptainSepPack(
                vehicle.state.positionX, vehicle.state.positionY, RoamCaptainPackSepRange, Script_Eol.RaiderPackPeerMax);
            Vector3 sep = vehicle.SteerForSeparation(Script_Eol.RaiderPackSeparationWorld, -0.707f, pack);
            return sep * Script_Eol.BotPackSeparationWeight;
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

            Vector3 flock = CaptainCaptainFlock(vehicle);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(hostile.Abstract, 0.48f);
            Vector3 leash = RaiderPdbLeashSeek(vehicle, RaiderCombatLeashWeight(1.15f));
            return flock + pursuitSteer + leash;
        }

        public Vector3 steerForStandoffHostileVehicle(InfantryVehicle vehicle, Vehicle hostile)
        {
            if (hostile == null)
                return Vector3.Zero;

            Vector3 flock = CaptainCaptainFlock(vehicle);
            Vector3 wanderSteer = vehicle.SteerForWander(0.32f);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(hostile.Abstract, 0.28f);
            Vector3 leash = RaiderPdbLeashSeek(vehicle, RaiderCombatLeashWeight(0.85f));
            _bStrafe = true;
            return flock + wanderSteer + pursuitSteer + leash;
        }

        public Vector3 steerForPersuePlayer(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            Vector3 flock = CaptainCaptainFlock(vehicle);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 0.48f);
            Vector3 leash = RaiderPdbLeashSeek(vehicle, RaiderCombatLeashWeight(1.15f));
            return flock + pursuitSteer + leash;
        }

        public Vector3 steerForFollowOwner(InfantryVehicle vehicle)
        {
            if (_targetPoint == null)
                return Vector3.Zero;

            Vector3 pos = new Vector3(((float)_targetPoint.positionX) / 100.0, ((float)_targetPoint.positionY) / 100.0, 0);

            Vector3 seekSteer = vehicle.SteerForSeek(pos);
            Vector3 leash = RaiderPdbLeashSeek(vehicle, 0.95f);

            return seekSteer * 1.35f + leash;
        }

        /// <summary>Path to captain's current roam leg; seek only when no path yet.</summary>
        public Vector3 steerAlongPathRoamCaptain(InfantryVehicle vehicle)
        {
            Vector3 along = steerAlongPath(vehicle);
            if (along != Vector3.Zero)
                return along;

            if (_targetPoint != null)
            {
                Vector3 dest = new Vector3(
                    ((float)_targetPoint.positionX) / 100.0f,
                    ((float)_targetPoint.positionY) / 100.0f,
                    0);
                return vehicle.SteerForSeek(dest);
            }

            return Vector3.Zero;
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

            IEnumerable<IVehicle> pack = BuildAttackerSepPackForCaptain(
                vehicle.state.positionX, vehicle.state.positionY, RoamCaptainAttackerPackRange, Script_Eol.RaiderPackPeerMax);
            Vector3 seperationSteer = vehicle.SteerForSeparation(Script_Eol.RaiderPackSeparationWorld, -0.707f, pack);
            Vector3 wanderSteer = vehicle.SteerForWander(0.28f);
            Vector3 leash = RaiderPdbLeashSeek(vehicle, RaiderCombatLeashWeight(0.85f));

            float len = (vehicle.state.position() - _target._state.position()).Length;
            Vector3 orbitFlankSteer = EolBotCombatUtil.SteerCombatPass(
                this, vehicle, _target._state, preferOs, 0.95f);
            Vector3 pursuitSteer = Vector3.Zero;

            if (len > 0.08f && _target._baseVehicle != null)
                pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 1.0f);

            _bStrafe = true;

            return (seperationSteer * Script_Eol.BotPackSeparationWeight) + (wanderSteer * 0.20f) + (orbitFlankSteer * 1.35f) + (pursuitSteer * 1.0f) + leash;
        }

        #endregion
    }
}
