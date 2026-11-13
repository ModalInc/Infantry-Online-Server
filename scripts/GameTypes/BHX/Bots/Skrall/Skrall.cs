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

namespace InfServer.Script.GameType_BHX
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class Skrall : Bot
    {   ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        //private Bot _bot;							//Pointer to our bot class
        private Random _rand;

        private Player _target;                     //The player we're currently stalking
        private Vehicle _botTarget;
        private Team _targetTeam;
        protected bool bOverriddenPoll;         //Do we have custom actions for poll?

        protected int _lastHeal;
        protected int _tickLastUpdate;
        protected int _energy;
        protected double _getEnergy;                    //For energy repair
        private int _tickLastRadarDot;
        private int _tickLastCollision;
        private int _tickLastTargetSeen;

        private bool _bPatrolEnemy;
        protected SteeringController steering;	//System for controlling the bot's steering
        private float _seperation;
        private int _tickNextStrafeChange;          //The last time we changed strafe direction
        private bool _bStrafeLeft;                  //Are we strafing left or right?

        private class Projectile
        {
            public Projectile(Vehicle vehicle, int tickCreation)
            {
                target = vehicle;
                tickShotFired = tickCreation;
            }

            public Vehicle target; //The target we were shooting at.
            public int tickShotFired; //The time at which we fired our projectile
        }
        private List<Projectile> _shotsFired;


        public Skrall(VehInfo.Car type, Helpers.ObjectState state, Arena arena)
            : base(type, state, arena,
            new SteeringController(type, state, arena))
        {
            Random rnd = new Random();
            _seperation = (float)rnd.NextDouble();
            steering = _movement as SteeringController;
            MovementMode = BotMovementMode.Api;
            _rand = new Random();

            if (type.InventoryItems[0] != 0)
                _weapon.equip(AssetManager.Manager.getItemByID(type.InventoryItems[0]));

            _shotsFired = new List<Projectile>();
            _state.energy = (short)_type.EnergyMax;

        }

        public void init(Team targetTeam)
        {
            _targetTeam = targetTeam;
  
        }



        /// <summary>
        /// Allows the script to maintain itself
        /// </summary>
        public override bool poll()
        {
            if (bOverriddenPoll)
            {
                HoldPosition();
                return base.poll();
            }

            if (_movement == null)
            {
                _movement = new MovementController(_type, _state, _arena);
                steering = _movement as SteeringController;
                _movement.bEnabled = true;
                _movement.updateState(1);
                steering.bEnabled = true;
                return base.poll();
            }

            bool bClearPath = false;

            //Dead? Do nothing
            if (IsDead)
            {
                HoldPosition();
                return base.poll();
            }

            // Steering is per-poll: anything not set again this poll stops.
            HoldPosition();

            int now = Environment.TickCount;

            if (now - _tickLastTargetSeen >= 8000 && _tickLastTargetSeen != 0)
                destroy(false);

            //Maintain our energy
            if (_type.EnergyMax > 0 && _type.EnergyRate > 0 && now - _tickLastUpdate >= (_type.EnergyRate * 10))
            {
                double _incEnergy = ((double)_type.EnergyRate / 10);
                _tickLastUpdate = now;
                _state.energy += (short)_incEnergy;

                if (_state.energy > _type.EnergyMax)
                    _state.energy = (short)_type.EnergyMax;
            }

            if (_movement.bCollision)
            {
                //Try to update our path
                if (_target != null)
                    Navigation.Approach(_target._state, null, new BotNavigateOptions
                    {
                        RequeueMinMs = c_pathUpdateInterval,
                        PathStartIndex = 1,
                        Priority = PathPriority.Combat,
                        ForceRequeue = true,
                    });

                ApplySteer(delegate (InfantryVehicle vehicle)
                {
                    Vector3 seek = vehicle.SteerForFlee(steering._lastCollision);
                    return seek;
                });


                
                return base.poll();
            }
           
            if (_movement.bCollision && now - _tickLastCollision < 500)
            {
                ApplySteer(delegate (InfantryVehicle vehicle)
                {
                    Vector3 seek = vehicle.SteerForFlee(steering._lastCollision);
                    return seek;
                });

                _tickLastCollision = now;
                return base.poll();
            }
            

            //Allows the bot to update the target every poll
            _target = null;

            //Get the closest player
             bClearPath = false;
            _target = getTargetPlayer(ref bClearPath, _targetTeam);

            if (_target != null)
            {
                if (bClearPath)
                {   //What is our distance to the target?
                    double distance = (_state.position() - _target._state.position()).Length;
                    bool bFleeing = false;

                    //Too far?
                    if (distance > pursueDist)
                        ApplySteer(steerForPersuePlayer);
                    //Quite short?
                    else if (distance < shortDist)
                    {
                        ApplySteer(delegate (InfantryVehicle vehicle)
                        {
                            if (_target != null)
                                return vehicle.SteerForFlee(_target._state.position());
                            else
                                return Vector3.Zero;
                        }, skipBodyRotate: true);
                    }
                    //Just right
                    else
                        HoldPosition();




                    if (!bFleeing)
                    {
                        //Should we be firing our ranged attack?
                        if (distance <= fireDist && distance > minRangedAttack)
                        {
                            int energyCost = 0;

                            ItemInfo item = AssetManager.Manager.getItemByID(_type.InventoryItems[3]);
                            switch (item.itemType)
                            {
                                case ItemInfo.ItemType.MultiUse:
                                    {
                                        ItemInfo.MultiUse mu = item as ItemInfo.MultiUse;
                                        energyCost = mu.energyCostTerrain0 / 1000;
                                    }
                                    break;
                                case ItemInfo.ItemType.Projectile:
                                    {
                                        ItemInfo.Projectile projectile = item as ItemInfo.Projectile;
                                        energyCost = projectile.terrain0EnergyCost / 1000;
                                    }
                                    break;
                            }
                            if (_weapon.ItemID != item.id)
                                _weapon.equip(item);

                            if (_weapon.ableToFire() && _state.energy > energyCost)
                            {
                                int aimResult = _weapon.getAimAngle(_target._state);

                                if (_weapon.isAimed(aimResult))
                                {   //Spot on! Fire?
                                    _itemUseID = _weapon.ItemID;
                                    _weapon.shotFired();
                                    _state.energy -= (short)energyCost;
                                }

                                SetAim(aimResult);
                            }
                        }
                        //Should we be firing our melee?
                        else if (distance <= minMeleeDist)
                        {
                            if (_weapon.ItemID != 3007)
                                _weapon.equip(AssetManager.Manager.getItemByID(3007));

                            if (_weapon.ableToFire())
                            {
                                int aimResult = _weapon.getAimAngle(_target._state);

                                if (_weapon.isAimed(aimResult))
                                {   //Spot on! Fire?
                                    _itemUseID = _weapon.ItemID;
                                    _weapon.shotFired();
                                }

                                SetAim(aimResult);
                            }
                            else
                                SetAim(0, false);
                        }
                    }
                }
                else
                {
                    //Navigate to him
                    navigateTo(_target._state, steerForPersuePlayer, PathPriority.Combat);
                }
            }

                //Handle normal functionality
                return base.poll();
        }

        public Helpers.ObjectState findOpenPath()
        {
            Helpers.ObjectState target = new Helpers.ObjectState();

            int blockedAttempts = 30;
            short pX;
            short pY;
            while (true)
            {
                pX = _state.positionX;
                pY = _state.positionY;
                Helpers.randomPositionInArea(_arena, 200, ref pX, ref pY);
                if (_arena.getTile(pX, pY).Blocked)
                {
                    blockedAttempts--;
                    if (blockedAttempts <= 0)
                        //Consider the spawn to be blocked
                        return null;
                    continue;
                }

                bool bClearPath = Helpers.calcBresenhemsPredicate(_arena,
                _state.positionX, _state.positionY, pX, pY,
                delegate (LvlInfo.Tile t)
                {
                    return !t.Blocked;
                }
                );

                if (!bClearPath)
                {
                    blockedAttempts--;
                    if (blockedAttempts <= 0)
                        //Consider the spawn to be blocked
                        return null;
                    continue;
                }

                target.positionX = pX;
                target.positionY = pY;
                break;
            }
            return target;
        }


        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected Player getTargetPlayer(ref bool bInSight, Team targetTeam)
        {
            //Look at the players on the target team
            if (targetTeam == null)
                return null;

            Player target = null;
            double lastDist = double.MaxValue;
            bInSight = false;

            IEnumerable<Player> trackingRange = _arena.getPlayersInRange(_state.positionX, _state.positionY, 2000).Where(plyr => plyr._team == targetTeam);
            foreach (Player p in trackingRange)
            {   //Find the closest player
                if (p.IsDead)
                    continue;

                if (_arena.getTerrainID(p._state.positionX, p._state.positionY) == 1)
                    continue;

                int distance = (int)(_state.position().Distance(p._state.position()) * 100);
                if (p.activeUtilities.Any(util => util != null && distance >= util.cloakDistance && util.cloakDistance != -1))
                    continue;

                double dist = Helpers.distanceSquaredTo(_state, p._state);
                bool bClearPath = Helpers.calcBresenhemsPredicate(_arena, _state.positionX, _state.positionY, p._state.positionX, p._state.positionY,
                    delegate (LvlInfo.Tile t)
                    {
                        return !t.Blocked;
                    }
                );

                if ((!bInSight || (bInSight && bClearPath)) && lastDist > dist)
                {
                    bInSight = bClearPath;
                    lastDist = dist;
                    target = p;
                }
            }

            if (target != null)
                _tickLastTargetSeen = Environment.TickCount;


            return target;
        }

        #region Steer Delegates
        public Vector3 steerForSeekTarget(InfantryVehicle vehicle)
        {
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 0.2f);
            return pursuitSteer;
        }

        public Vector3 steerForSeekBot(InfantryVehicle vehicle)
        {
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_botTarget.Abstract, 0.2f);
            return pursuitSteer;
        }

        /// <summary>
        /// Moves the medic on a persuit course towards the player, while keeping seperated from other medics
        /// </summary>
        public Vector3 steerForPersuePlayer(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            List<Vehicle> medics = _arena.getVehiclesInRange(vehicle.state.positionX, vehicle.state.positionY, 200,
                                                                delegate (Vehicle v)
                                                                { return (v is Skrall); });
            IEnumerable<IVehicle> medicbots = medics.ConvertAll<IVehicle>(
                delegate (Vehicle v)
                {
                    return (v as Skrall).Abstract;
                }
            );

            Vector3 seperationSteer = vehicle.SteerForSeparation(_seperation, -0.307f, medicbots);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 0.2f);

            return (seperationSteer * 1.2f) + pursuitSteer;
        }




        /// <summary>Path toward <paramref name="dest"/>; walks <paramref name="fallback"/> until the route arrives.</summary>
        void navigateTo(Helpers.ObjectState dest, Func<InfantryVehicle, Vector3> fallback, PathPriority priority)
        {
            ApplyNavigationApproach(dest, fallback, new BotNavigateOptions
            {
                RequeueMinMs = c_pathUpdateInterval,
                PathStartIndex = 1,
                Priority = priority,
            });
        }

       
        #endregion
    }
}
