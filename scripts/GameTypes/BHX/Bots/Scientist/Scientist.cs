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
    public partial class Scientist : Bot
    {   ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        //private Bot _bot;							//Pointer to our bot class
        private Random _rand;

        private Player _target;                     //The player we're currently stalking
        private Vehicle _botTarget;
        protected bool bOverriddenPoll;         //Do we have custom actions for poll?

        protected int _lastHeal;
        protected int _lastEnergyRecharge;
        protected int _energy;
        private int _tickLastWander;
        private int _tickLastCollision;
        private Helpers.ObjectState _targetPoint;
        private Objective _objective;
        private int _tickArrivedAtDZ;

        private bool _bPatrolEnemy;
        protected SteeringController steering;	//System for controlling the bot's steering
        private float _seperation;
        private int _tickNextStrafeChange;          //The last time we changed strafe direction
        private bool _bStrafeLeft;                  //Are we strafing left or right?
        private bool _respawned;
        private int _tickLastWave;
        private int _tickLastHeal;

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


        public Scientist(VehInfo.Car type, Helpers.ObjectState state, Arena arena)
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

        }

        public void init(Objective objective)
        {
            _target = _creator;
            _objective = objective;
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
                return base.poll();
            }

            //Dead? Do nothing
            if (IsDead)
            {
                if (!_respawned)
                {
                    _objective.objectiveDeath();
                    _respawned = true;
                }
                HoldPosition();
                return base.poll();
            }

            // Steering is per-poll: anything not set again this poll stops.
            HoldPosition();

            int now = Environment.TickCount;

            if (_state.health < _type.Hitpoints && now - _tickLastHeal >= 2000)
            {
                _state.health++;
                _tickLastHeal = now;
            }

            if (now - _tickLastWave >= 1000)
            {
                Helpers.Player_RouteExplosion(_arena.getTeamByName("Marines").AllPlayers, 3060, _state.positionX, _state.positionY, 0, 0, 0);
                _tickLastWave = now;
            }

            if (_movement.bCollision)
            {
                //Try to update our path
                if (_target != null)
                    followPlayer(now);

                ApplySteer(delegate (InfantryVehicle vehicle)
                {
                    if (_target != null)
                    {
                        Vector3 wander = vehicle.SteerForWander(15.5f);
                        Vector3 seek = vehicle.SteerForFlee(steering._lastCollision);

                        return wander + (seek * 12.5f);
                    }
                    else
                        return Vector3.Zero;
                });


                _tickLastCollision = now;
                return base.poll();
            }

            if (now - _tickLastCollision < 600)
            {
                ApplySteer(delegate (InfantryVehicle vehicle)
                {
                    if (_target != null)
                    {
                        Vector3 wander = vehicle.SteerForWander(15.5f);
                        Vector3 seek = vehicle.SteerForFlee(steering._lastCollision);

                        return wander + (seek * 15.5f);
                    }
                    else
                        return Vector3.Zero;
                });
                return base.poll();
            }


            int terrainID = _arena.getTerrainID(_state.positionX, _state.positionY);      
            if (terrainID == 3)
            {

                if (_tickArrivedAtDZ == 0)
                {
                    _tickArrivedAtDZ = now;
                    _objective.arrivedAtDZ(now);
                }

                if (now - _tickArrivedAtDZ >= 20000)
                {
                    _objective.objectiveRescued();
                    this.destroy(false);
                }


                wander(now);
                return base.poll();
            }

            if (_target == null || !isValidFollowTarget(_target))
                _target = getTargetToFollow();

            if (_target != null)
            {

                bool bClearPath = Helpers.calcBresenhemsPredicate(_arena, _state.positionX, _state.positionY, _target._state.positionX, _target._state.positionY,
                delegate (LvlInfo.Tile t)
                {
                    return !t.Blocked;
                }
                );

                double distance = (_state.position() - _target._state.position()).Length;

                if (bClearPath)
                {
                    if (distance > 0.75f)
                        ApplySteer(steerForPersuePlayer);
                    else if (distance < 0.4f)
                    {

                        ApplySteer(delegate (InfantryVehicle vehicle)
                        {
                            if (_target != null)
                            {
                                Vector3 wander = vehicle.SteerForWander(15.5f);
                                Vector3 seek = vehicle.SteerForFlee(_target._state.position());

                                return wander + (seek * 12.5f);
                            }
                            else
                                return Vector3.Zero;
                        });
                    }
                    //Just right
                    else
                        HoldPosition();
                }
                else
                    followPlayer(now);
            }

            //Handle normal functionality
            return base.poll();
        }


        #region Locaters

        #region Player Follow
        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected bool isValidFollowTarget(Player target)
        {   //Don't shoot a dead zombie
            if (target.IsDead)
                return false;

            if (target._team != _team)
                return false;

            if (_arena.getTerrain(target._state.positionX, target._state.positionY).safety)
                return false;

            //Is it too far away?
            if (Helpers.distanceTo(this, target) >= 800)
                return false;

            //if (target.ActiveVehicle._type.Id != 101)
            //    return false;

            return true;
        }

        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected Player getTargetToFollow()
        {   //Find the closest valid target
            Vector3 selfpos = _state.position();
            IEnumerable<Player> targets = _arena.getPlayersInRange(_state.positionX, _state.positionY, 3000).OrderBy(t => Helpers.distanceTo(this, t));

            foreach (Player target in targets)
                if (isValidFollowTarget(target))
                    return target;

            return null;
        }
        #endregion
        public void wander(int now)
        {

            if (now - _tickLastWander < 6000)
                return;



            if (_targetPoint == null)
                _targetPoint = findWanderPoint();


            //What is our distance to the target?
            double distance = (_state.position() - _targetPoint.position()).Length;

            //Are we there yet?
            if (distance < patrolDist)
            {
                _targetPoint = null;
                _tickLastWander = now;
                return;
            }

            //Navigate to him
            navigateTo(_targetPoint, null, PathPriority.Idle);
        }

        public Helpers.ObjectState findWanderPoint()
        {
            Helpers.ObjectState target = new Helpers.ObjectState();

            int blockedAttempts = 30;
            short pX;
            short pY;
            while (true)
            {
                pX = _state.positionX;
                pY = _state.positionY;
                Helpers.randomPositionInArea(_arena, 800, ref pX, ref pY);
                if (_arena.getTile(pX, pY).Blocked)
                {
                    blockedAttempts--;
                    if (blockedAttempts <= 0)
                        //Consider the spawn to be blocked
                        return null;
                    continue;
                }

                if (_arena.getTerrainID(pX, pY) != 3)
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

        #endregion

        /// <summary>
        /// Keeps the combat bot around the engineer
        /// </summary>
        public Vector3 steerForFollowOwner(InfantryVehicle vehicle)
        {
            if (_target == null)
                return Vector3.Zero;

            Vector3 wanderSteer = vehicle.SteerForWander(1.3f);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 0.2f);

            return pursuitSteer;
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

            List<Vehicle> medics = _arena.getVehiclesInRange(vehicle.state.positionX, vehicle.state.positionY, 150,
                                                                delegate (Vehicle v)
                                                                { return (v is Scientist); });
            IEnumerable<IVehicle> medicbots = medics.ConvertAll<IVehicle>(
                delegate (Vehicle v)
                {
                    return (v as Scientist).Abstract;
                }
            );

            Vector3 seperationSteer = vehicle.SteerForSeparation(_seperation, -0.307f, medicbots);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 0.2f);

            return (seperationSteer * 0.3f) + pursuitSteer;
        }

        public void followPlayer(int now)
        {
            //Stay close to our owner
            bool bClearPath = Helpers.calcBresenhemsPredicate(_arena,
                    _state.positionX, _state.positionY, _target._state.positionX, _target._state.positionY,
                    delegate (LvlInfo.Tile t)
                    {
                        return !t.Blocked;
                    }
                );

            if (bClearPath)
                //Persue directly!
                ApplySteer(steerForFollowOwner);
            else
            {
                //Navigate to him
                navigateTo(_target._state, steerForPersuePlayer, PathPriority.Idle);
            }

            #endregion
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
    }
}
