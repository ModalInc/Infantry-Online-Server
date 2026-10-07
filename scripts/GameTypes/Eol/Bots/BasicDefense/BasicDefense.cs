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
        public Vehicle _targetLocation;
        private Team _targetTeam;
        private Vehicle vHq;                    //Our HQ
        public BotType type;
        protected bool bOverriddenPoll;         //Do we have custom actions for poll?

        protected List<Vector3> _path;			//The path to our destination
        protected int _pathTarget;				//The next target node of the path
        protected int _tickLastPath;			//The time at which we last made a path to the player
        public Script_Eol _baseScript;
        public int _tickLastWander;
        protected int _tickLastCollision;
        private int _tickStuckSample;
        private short _stuckSampleX;
        private short _stuckSampleY;
        bool _unstickPathActive;
        Helpers.ObjectState _unstickEscapeTarget;
        int _tickUnstickStarted;
        int _tickLastUnstickNoCollision;
        protected Vector3 roampos;
        public bool _bStrafe;                       //Should we be strafing?
        private int _tickNextStrafeChange;          //The last time we changed strafe direction
        private bool _bStrafeLeft;                  //Are we strafing left or right?

        private WeaponController _weaponClose;  //Our weapon for close range
        private WeaponController _weaponFar;    //Our weapon for anything that is not close range

        private bool _bPatrolEnemy;
        protected SteeringController steering;	//System for controlling the bot's steering
        private float _seperation;
        int _tickLastFireActionExec;
        int _tickLastRetreatActionExec;
        bool _combatMovedLastPoll;

        protected int _lawQuantity = 1;


        public BasicDefense(VehInfo.Car type, Helpers.ObjectState state, Arena arena, Script_Eol BaseScript, InfServer.Game.Player _owner)
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

            _tickLastCollision = 0;

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
            if (vHq != null && (vHq.IsDead || vHq._team != _team))
                vHq = null;
            if (vHq == null)
                vHq = _baseScript.FindHqVehicleForTeam(_team);

            // No live HQ → idle; Main.PruneOrphanedDefenseBaseBots / HQ wipe tears the squad down.
            // Captain death alone must NOT kill us while the HQ is still up.
            if (vHq == null)
                return base.poll();

            //double distance = Math.Pow((Math.Pow(_state.positionX - vHq._state.positionX, 2) + Math.Pow(_state.positionY - vHq._state.positionY, 2)) / 2, 0.5);
            double distance = (_state.position() - vHq._state.position()).Length;
            //Dead? Do nothing
            if (IsDead)
            {
                ReleasePollScratch();
                HoldPosition();
                if (bDropshipRespawnPending)
                    return base.poll();
                if (_baseScript != null)
                    _baseScript.SyncDefenseBotCount(_team);
                bCondemned = true; //Make sure the bot gets removed in polling
                return base.poll();
            }

            //Find out if our owner is gone
            // Bot-team defense units have no player owner — never call ActivePlayers.Last() on an empty set.
            if (owner == null
                && (_baseScript == null || !_baseScript.IsDefenseBotTeam(_team)))
            {//Find a new owner if not a bot team
                if (_team.ActivePlayerCount > 0)
                    owner = _team.ActivePlayers.Last();
                else
                {
                    kill(null);
                    ReleasePollScratch();
                    if (_baseScript != null)
                        _baseScript.SyncDefenseBotCount(_team);
                    bCondemned = true; //Make sure the bot gets removed in polling
                    return base.poll();
                }
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
                // Keep shooting while escaping the wall — do not ignore the player.
                if (_target != null)
                    EolBotPhysicsUtil.TryFireOnlyWhileUnstick(this, _target);
                else if (EolBotSenseUtil.ShouldBreakIdleForCombat(
                        _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target))
                    fireAtEnemy(now);
                return base.poll();
            }

            pollForActions(now);

            bool needPatrol = true;
            bool combatMoved = false;
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

                    switch (currentAction.type)
                    {
                        case Action.Type.fireAtEnemy:
                            // Drop patrol hops once when entering combat so we do not path past the player.
                            if (!_combatMovedLastPoll)
                                Navigation.Clear();
                            combatMoved = fireAtEnemy(now);
                            needPatrol = !combatMoved;
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
                    combatMoved = fireAtEnemy(now);
                    needPatrol = !combatMoved;
                }
                else if (currentAction.type == Action.Type.retreat)
                {
                    ReturnToHQ(now);
                    needPatrol = false;
                }
            }

            if (needPatrol
                && EolBotSenseUtil.ShouldBreakIdleForCombat(
                    _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target))
            {
                Navigation.Clear();
                combatMoved = fireAtEnemy(now);
                needPatrol = !combatMoved;
            }

            // Drop leftover combat routes so patrol can repath to its roam spot.
            if (needPatrol && _combatMovedLastPoll && !combatMoved)
                Navigation.Clear();
            _combatMovedLastPoll = combatMoved;
            // fireAtEnemy already set InLiveCombat for player or close enemy-bot locks.
            if (!combatMoved)
                InLiveCombat = false;

            if (needPatrol)
            {
                // Keep sticky target while a hostile is still in aggro — clearing every
                // patrol poll made bots look passive between legs.
                if (!EolBotSenseUtil.ShouldBreakIdleForCombat(
                        _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target))
                    ClearActivePlayerTarget();
                _bStrafe = false;
                patrolHQ(now);
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