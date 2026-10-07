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
using System.Collections.Specialized;

namespace InfServer.Script.GameType_Eol
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class Captain : Bot
    {   ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        //private Bot _bot;							//Pointer to our bot class
        private Random _rand;

        private InfServer.Game.Player _target;                     //The player we're currently stalking
        /// <summary>Live player combat target (for engage-cap accounting).</summary>
        public InfServer.Game.Player ActivePlayerTarget { get { return _target; } }
        public bool InLiveCombat;
        public void ClearActivePlayerTarget() { _target = null; InLiveCombat = false; }
        public InfServer.Game.Player owner;
        public Helpers.ObjectState _targetPoint;
        /// <summary>False = heading to a roam pick; true = returning near HQ afterward.</summary>
        bool _patrolReturning;
        readonly EolHqPatrolState _hqPatrol = new EolHqPatrolState();
        private Vehicle _targetLocation;
        private Vehicle _roamMarker;
        private Team _targetTeam;
        private Vehicle myvHq;
        public BotType type;
        protected bool bOverriddenPoll;         //Do we have custom actions for poll?

        protected List<Vector3> _path;			//The path to our destination
        protected int _pathTarget;				//The next target node of the path
        protected int _tickLastPath;			//The time at which we last made a path to the player
        public Script_Eol _baseScript;
        public int _tickLastWander;
        public int _tickLastReturn;
        protected int _tickLastSpawn;               //Tick at which we spawned a roaming attacker bot
        protected int _tickLastCollision;
        private int _tickStuckSample;
        private short _stuckSampleX;
        private short _stuckSampleY;
        bool _unstickPathActive;
        Helpers.ObjectState _unstickEscapeTarget;
        int _tickUnstickStarted;
        int _tickLastUnstickNoCollision;
        private WeaponController _weaponClose;  //Our weapon for close range
        private WeaponController _weaponFar;    //Our weapon for anything that is not close range
        public bool _bStrafe;                       //Should we be strafing?
        private int _tickNextStrafeChange;          //The last time we changed strafe direction
        private bool _bStrafeLeft;                  //Are we strafing left or right?

        private bool _bPatrolEnemy;
        protected SteeringController steering;	//System for controlling the bot's steering
        private float _seperation;
        private Vector3 roampos;

        /// <summary>Last time <see cref="fireAtEnemy"/> ran after dequeue (action-exec throttle).</summary>
        int _tickLastFireActionExec;
        /// <summary>Last time <see cref="ReturnToHQ"/> ran after dequeue.</summary>
        int _tickLastRetreatActionExec;
        bool _combatMovedLastPoll;

        List<Arena.FlagState> capturedflags;


        public Captain(VehInfo.Car type, Helpers.ObjectState state, Arena arena, Script_Eol BaseScript, InfServer.Game.Player _owner)
            : base(type, state, arena,
            new SteeringController(type, state, arena))
        {
            Random rnd = new Random();
            _seperation = (float)rnd.NextDouble();
            steering = _movement as SteeringController;
            _rand = new Random();
            //Our weapon to use when we are close to the enemy
            _weaponClose = new WeaponController(_state, new WeaponController.WeaponSettings());
            _weaponFar = new WeaponController(_state, new WeaponController.WeaponSettings());

            // Slot 0 = close, slot 1 = far.
            EolBotCombatUtil.EquipBotWeapons(type, _weaponClose, _weaponFar);
            EolBotCombatUtil.WireLiveCombatWeapons(this, _weaponClose, _weaponFar);

            _actionQueue = new List<Action>();

            capturedflags = new List<Arena.FlagState>();

            _baseScript = BaseScript;
            owner = _owner;

            MovementMode = BotMovementMode.Api;

        }

        public void init()
        {
            WeaponController.WeaponSettings settings = new WeaponController.WeaponSettings();
            settings.aimFuzziness = 32;

            _weapon.setSettings(settings);

            _weaponFar.setSettings(settings);
            _weaponClose.setSettings(settings);

            _tickLastWander = Environment.TickCount;
            _tickLastSpawn = Environment.TickCount;
            _tickLastCollision = 0;
            _tickLastReturn = Environment.TickCount;

            base.poll();
        }

        /// <summary>
        /// Allows the script to maintain itself
        /// </summary>
        public override bool poll()
        {
            if (bOverriddenPoll)
                return base.poll();
            if (_baseScript != null && _baseScript.IsVehicleDespawnQueued(this))
            {
                HoldPosition();
                return false;
            }
            _bStrafe = false;

            if (myvHq != null && (myvHq.IsDead || myvHq._team != _team))
                myvHq = null;
            if (myvHq == null)
                myvHq = _baseScript.FindHqVehicleForTeam(_team);

            //Check if an HQ was found, if not return to polling
            if (myvHq == null)
                return base.poll();

            //Dead? Do nothing
            if (IsDead)
            {//Dead
                ReleasePollScratch();
                steering.steerDelegate = null; //Stop movements
                if (bDropshipRespawnPending)
                    return base.poll();
                bCondemned = true; //Make sure the bot gets removed in polling
                _baseScript.captainBots.Remove(_team);
                return base.poll();
            }

            int now = Environment.TickCount;
            InLiveCombat = false;

            if (EolBotPhysicsUtil.HandleBotUnstickPoll(
                    this, _state, now, _arena, _baseScript, steering, _movement.bCollision,
                    ref _tickLastCollision, ref _tickStuckSample, ref _stuckSampleX, ref _stuckSampleY,
                    ref _unstickPathActive, ref _unstickEscapeTarget, ref _tickUnstickStarted, ref _tickLastUnstickNoCollision,
                    GetEolPathState, ApplyEolPathState,
                    steerAlongPath, EolBotPhysicsUtil.PathRequeueMinMsDefense,
                    delegate { _targetPoint = null; },
                    null,
                    true,
                    _target != null ? _target._state : null))
            {
                _movement.bCollision = false;
                if (_target != null)
                    EolBotPhysicsUtil.TryFireOnlyWhileUnstick(this, _target);
                else if (EolBotSenseUtil.ShouldBreakIdleForCombat(
                        _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target))
                    fireAtEnemy(now);
                return base.poll();
            }

            pollForActions(now);

            bool needPatrol = true;
            if (_actionQueue.Count > 0)
            {
                int bestIdx = 0;
                for (int i = 1; i < _actionQueue.Count; i++)
                {
                    if (_actionQueue[i].priority > _actionQueue[bestIdx].priority)
                        bestIdx = i;
                }
                Action currentAction = _actionQueue[bestIdx];
                bool bypass = currentAction.priority == Action.Priority.High;
                bool fireThrottled = !bypass && currentAction.type == Action.Type.fireAtEnemy
                    && _tickLastFireActionExec != 0
                    && now - _tickLastFireActionExec < EolBotActionEvalThrottle.FireActionExecMinMs;
                bool retreatThrottled = !bypass && currentAction.type == Action.Type.retreat
                    && _tickLastRetreatActionExec != 0
                    && now - _tickLastRetreatActionExec < EolBotActionEvalThrottle.RetreatActionExecMinMs;

                if (!fireThrottled && !retreatThrottled)
                {
                    _actionQueue.RemoveAt(bestIdx);
                    if (currentAction.type == Action.Type.fireAtEnemy)
                        _tickLastFireActionExec = now;
                    else if (currentAction.type == Action.Type.retreat)
                        _tickLastRetreatActionExec = now;

                    //Maintain defense bots — cap includes captain + engineer + grunts on this team.
                    if (owner == null && _baseScript.CanSpawnDefenseCombatBot(_team) && now - _tickLastSpawn > 2500)
                    {
                        _baseScript.addBot(null, _state, _team);
                        _tickLastSpawn = now;
                    }

                    switch (currentAction.type)
                    {
                        case Action.Type.fireAtEnemy:
                            if (!_combatMovedLastPoll)
                                Navigation.Clear();
                            needPatrol = !fireAtEnemy(now);
                            break;
                        case Action.Type.retreat:
                            ReturnToHQ(now);
                            needPatrol = false;
                            break;
                    }
                }
                else if (currentAction.type == Action.Type.fireAtEnemy)
                {
                    if (!_combatMovedLastPoll)
                        Navigation.Clear();
                    needPatrol = !fireAtEnemy(now);
                }
                else if (currentAction.type == Action.Type.retreat)
                {
                    ReturnToHQ(now);
                    needPatrol = false;
                }
            }

            bool combatMoved = !needPatrol;
            if (needPatrol
                && EolBotSenseUtil.ShouldBreakIdleForCombat(
                    _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target))
            {
                Navigation.Clear();
                needPatrol = !fireAtEnemy(now);
                combatMoved = !needPatrol;
            }

            if (needPatrol && _combatMovedLastPoll && !combatMoved)
                Navigation.Clear();
            _combatMovedLastPoll = combatMoved;
            if (!combatMoved)
                InLiveCombat = false;

            if (needPatrol)
            {
                if (!EolBotSenseUtil.ShouldBreakIdleForCombat(
                        _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target))
                    ClearActivePlayerTarget();
                _bStrafe = false;
                if (_arena._bGameRunning)
                {
                    if (owner == null && _baseScript.CanSpawnDefenseCombatBot(_team) && now - _tickLastSpawn > 2500)
                    {
                        _baseScript.addBot(null, _state, _team);
                        _tickLastSpawn = now;
                    }
                    patrolHQ(now);
                }
            }
            else if (_bStrafe)
            {
                if (now > _tickNextStrafeChange)
                {
                    _tickNextStrafeChange = now + _arena._rand.Next(600, 2000);
                    _bStrafeLeft = !_bStrafeLeft;
                }

                if (_bStrafeLeft)
                    steering.bStrafeLeft = true;
                else
                    steering.bStrafeRight = true;
            }

            //Handle normal functionality
            return base.poll();
        }

        public void updatePath(int now)
        {
            if (_target == null)
                return;

            ApplyNavigationApproach(_target._state, steerForPersuePlayer, new BotNavigateOptions
            {
                RequeueMinMs = c_pathUpdateInterval,
                PathStartIndex = 1,
                Priority = PathPriority.Combat,
            });
        }

        public void updatePathToVehicle(int now, Vehicle veh)
        {
            if (veh == null)
                return;

            ApplyNavigationApproach(veh._state, iv => steerForPursueHostileVehicle(iv, veh), new BotNavigateOptions
            {
                RequeueMinMs = c_pathUpdateInterval,
                PathStartIndex = 1,
                Priority = PathPriority.Combat,
            });
        }
    }
}