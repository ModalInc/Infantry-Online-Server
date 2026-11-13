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
    public partial class Medic : Bot
    {   ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        //private Bot _bot;							//Pointer to our bot class
        private Random _rand;

        private Player _target;                     //The player we're currently stalking
        private Vehicle _botTarget;
        private Coop _game;
        public BotType type;
        protected bool bOverriddenPoll;         //Do we have custom actions for poll?

        protected int _lastHeal;
        protected int _lastEnergyRecharge;
        protected int _energy;
        private int _tickLastRadarDot;


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


        public Medic(VehInfo.Car type, Helpers.ObjectState state, Arena arena)
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

        public void init(Coop coop)
        {
            _game = coop;
            _energy = c_maxEnergy;
            _game._medBotHealTargets.Add(_id, null);
            _game._medBotFollowTargets.Add(_id, null);
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

            //Dead? Do nothing
            if (IsDead)
            {
                HoldPosition();
                return base.poll();
            }

            // Steering is per-poll: anything not set again this poll stops.
            HoldPosition();
            int now = Environment.TickCount;

            //Radar Dot
            if (now - _tickLastRadarDot >= 900)
            {
                _tickLastRadarDot = now;
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != _team);
                //Helpers.Player_RouteExplosion(_team.ActivePlayers, 1131, _state.positionX, _state.positionY, 0, 0, 0);
                //Helpers.Player_RouteExplosion(enemies, 1130, _state.positionX, _state.positionY, 0, 0, 0);
            }

            //Maintain our bots energy
            if (_energy < c_maxEnergy)
            {
                if ((now - _lastEnergyRecharge) >= 499)
                {
                    _energy += (c_energyRechargeRate / 10);
                    _lastEnergyRecharge = now;

                    if (_energy > c_maxEnergy)
                        _energy = c_maxEnergy;
                }
            }

            //Do we have any projectiles to spoof damage for?
            if (_shotsFired.Count > 0)
            {
                Projectile proj = _shotsFired[0];

                //Ballpark figure for PDW
                if (now - proj.tickShotFired >= 700)
                {
                    //Is it still valid?
                    if (proj.target != null && !proj.target.IsDead)
                    {
                        //Simulate the damage if we're attacking a bot
                        if (proj.target._bBotVehicle)
                            proj.target.applyExplosionDamage(true, null, proj.target._state.positionX, proj.target._state.positionY, _weapon.Projectile);
                    }

                    //Erase it
                    _shotsFired.Remove(proj);
                }
            }

            bool bHealing = false;
            //Do we need to seek a new target?
            if (_target == null || !isValidTarget(_target))
            {
                _game._medBotHealTargets[_id] = null;
                _target = getTargetPlayer();
            }

            if (_target != null)
            {
                bHealing = true;
                //Too far?
                if (Helpers.distanceTo(this, _target) > 400)
                {
                    ApplySteer(steerForPersuePlayer);
                }
                else
                {
                    //Can we shoot?
                    if (_weapon.ableToFire())
                    {
                        if (_energy >= 75)
                            fireMedkit(now);
                    }
                    else
                        SetAim(0, false);
                }
            }
            bool BFollowing = false;
            //Are we not healing anyone? lets find a player to "latch" onto
            if (!bHealing)
            {
                if (_target == null || !isValidFollowTarget(_target))
                {
                    _game._medBotFollowTargets[_id] = null;
                    _target = getTargetToFollow();
                }

                if (_target != null)
                {
                    BFollowing = true;
                    if (Helpers.distanceTo(this, _target) > 100)
                    {
                        ApplySteer(steerForSeekTarget);
                    }

                }
            }
            bool bAttacking = false;
            bool bBackPedal = false;
            //Are we not healing or following anyone? lets shoot some bots!
            if (!bHealing && 1 < 0)
            {
                if (_botTarget == null || !isValidAttackTarget(_botTarget))
                {
                    _botTarget = getTargetToAttack();
                }

                if (_botTarget != null)
                {

                    bAttacking = true;
                    //Too far?
                    if (Helpers.distanceTo(this, _botTarget) > 520)
                    {
                        ApplySteer(steerForSeekBot);
                    }
                    else
                    {
                        HoldPosition();

                        bool bClearPath = Helpers.calcBresenhemsPredicate(_arena, _state.positionX, _state.positionY, _botTarget._state.positionX, _botTarget._state.positionY,
                            delegate (LvlInfo.Tile t)
                            {
                                return !t.Blocked;
                            }
                        );

                        if (bClearPath)
                        {

                            //Can we shoot?
                            if (_weapon.ableToFire() && !bBackPedal)
                            {
                                int aimResult = _weapon.getAimAngle(_botTarget._state);

                                if (_weapon.isAimed(aimResult))
                                {   //Spot on! Fire?
                                    _itemUseID = _weapon.ItemID;
                                    _weapon.shotFired();

                                    //Damage spoofing
                                    Projectile proj = new Projectile(_botTarget, now);
                                    _shotsFired.Add(proj);
                                }

                                SetAim(aimResult);
                            }
                            else
                                SetAim(0, false);
                        }
                    }
                    followBot(now);
                }
            }


            if (_target != null)
                followPlayer(now);

            //Handle normal functionality
            return base.poll();
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

        #region Locaters

        #region Player Heal
        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected bool isValidTarget(Player target)
        {   //Don't shoot a dead zombie
            if (target.IsDead)
                return false;

            if (target._team != _team)
                return false;

            if (target._state.health == target._baseVehicle._type.Hitpoints)
                return false;

            if (_arena.getTerrain(target._state.positionX, target._state.positionY).safety)
                return false;

            if (_game._medBotHealTargets.Where(t => t.Key != _id && t.Value == target).Count() > 0)
                return false;

            if (target._occupiedVehicle != null)
            {
                if (target._occupiedVehicle._type.Weight > 10)
                    return false;
            }
            //Is it too far away?
            if (Helpers.distanceTo(this, target) >= 1000)
                return false;

            return true;
        }

        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected Player getTargetPlayer()
        {   //Find the closest valid target
            Vector3 selfpos = _state.position();
            IEnumerable<Player> targets = _arena.getPlayersInRange(_state.positionX, _state.positionY, 1000).OrderBy(t => Helpers.distanceTo(this, t));

            foreach (Player target in targets)
                if (isValidTarget(target))
                {
                    _game._medBotHealTargets[_id] = target;
                    return target;
                }

            return null;
        }
        #endregion

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

            if (_game._medBotFollowTargets.Where(t => t.Key != _id && t.Value == target).Count() > 0)
                return false;

            if (target._occupiedVehicle != null)
            {
                if (target._occupiedVehicle._type.Weight > 10)
                    return false;
            }
            

            //Is it too far away?
            if (Helpers.distanceTo(this, target) >= 3000)
                return false;

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
                {
                    _game._medBotFollowTargets[_id] = target;
                    return target;
                }

            return null;
        }
        #endregion

        #region Bot Attack
        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected bool isValidAttackTarget(Vehicle target)
        {   //Don't shoot a dead zombie
            if (target.IsDead)
                return false;

            if (target._team == _team)
                return false;

            if (_arena.getTerrain(target._state.positionX, target._state.positionY).safety)
                return false;

            //Is it too far away?
            if (Helpers.distanceTo(this, target) >= 2300)
                return false;

            return true;
        }

        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected Vehicle getTargetToAttack()
        {   //Find the closest valid target
            Vector3 selfpos = _state.position();
            IEnumerable<Vehicle> targets = _arena.getVehiclesInRange(_state.positionX, _state.positionY, 2300).OrderBy(t => Helpers.distanceTo(this, t));

            foreach (Vehicle target in targets)
                if (isValidAttackTarget(target))
                    return target;

            return null;
        }
        #endregion
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

            return (wanderSteer * 0.75f) + pursuitSteer;
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
                                                                { return (v is Medic); });
            IEnumerable<IVehicle> medicbots = medics.ConvertAll<IVehicle>(
                delegate (Vehicle v)
                {
                    return (v as Medic).Abstract;
                }
            );

            Vector3 seperationSteer = vehicle.SteerForSeparation(_seperation, -0.307f, medicbots);
            Vector3 pursuitSteer = vehicle.SteerForPursuit(_target._baseVehicle.Abstract, 0.2f);

            return (seperationSteer * 0.3f) + pursuitSteer;
        }



        public void followBot(int now)
        {

            //Stay close to our owner
            bool bClearPath = Helpers.calcBresenhemsPredicate(_arena,
                    _state.positionX, _state.positionY, _botTarget._state.positionX, _botTarget._state.positionY,
                    delegate (LvlInfo.Tile t)
                    {
                        return !t.Blocked;
                    }
                );

            if (bClearPath)
                //Persue directly!
                ApplySteer(steerForFollowOwner);
            else
                navigateTo(_botTarget._state, steerForSeekBot, PathPriority.Combat);
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
                navigateTo(_target._state, steerForFollowOwner, PathPriority.Idle);

            #endregion
        }
    }
}
