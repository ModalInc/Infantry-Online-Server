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
        public Vehicle _Leader;
        public Helpers.ObjectState _targetPoint;
        private Team _targetTeam;
        public BotType type;
        protected bool bOverriddenPoll;         //Do we have custom actions for poll?

        protected List<Vector3> _path;			//The path to our destination
        protected int _pathTarget;				//The next target node of the path
        protected int _tickLastPath;			//The time at which we last made a path to the player
        public Script_Eol _baseScript;
        public int _tickLastWander;
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

        private bool _bPatrolEnemy;
        protected SteeringController steering;	//System for controlling the bot's steering
        private float _seperation;
        public bool _bStrafe;
        private int _tickNextStrafeChange;
        private bool _bStrafeLeft;
        int _tickAnchorRoamPick;
        int _tickLastSeekActionExec;
        int _tickLastRepositionActionExec;
        int _tickLastAnchorRoamActionExec;
        int _lastRaiderCollectiveRoamRevision = -1;
        readonly EolHqPatrolState _roamSteer = new EolHqPatrolState();

        void SyncRaiderCollectiveRoamRevision()
        {
            if (_baseScript == null)
                return;
            if (_lastRaiderCollectiveRoamRevision == _baseScript.RaiderCollectiveRoamRevision)
                return;
            _lastRaiderCollectiveRoamRevision = _baseScript.RaiderCollectiveRoamRevision;
            _targetPoint = null;
            _path = null;
            _pathTarget = 0;
            _tickLastPath = 0;
            Navigation.Clear();
            EolHqPatrol.ResetSteerState(_roamSteer);
        }

        List<Arena.FlagState> capturedflags;


        public RoamingCaptain(VehInfo.Car type, Helpers.ObjectState state, Arena arena, Script_Eol BaseScript)
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

            MovementMode = BotMovementMode.Api;

            BotHelpers.ApplyCaptureStatsFromVehicle(this);
        }

        public void init()
        {
            WeaponController.WeaponSettings settings = new WeaponController.WeaponSettings();
            settings.aimFuzziness = 54;

            _weapon.setSettings(settings);

            _tickLastWander = Environment.TickCount;
            _tickLastSpawn = Environment.TickCount;
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

            //Dead? Do nothing
            if (IsDead)
            {//Dead
                ReleasePollScratch();
                steering.steerDelegate = null; //Stop movements
                if (bDropshipRespawnPending)
                    return base.poll();
                bCondemned = true; //Make sure the bot gets removed in polling
                _baseScript.roamingCaptianBots.Remove(_team);
                _baseScript.capRoamBots.Remove(_team);
                if (!_baseScript.IsBountyHunterBotTeam(_team))
                    _baseScript._currentRoamCaptains--;
                return base.poll();
            }

            bool squadLive = _baseScript.IsBountyHunterBotTeam(_team)
                ? _baseScript.BountyHunterSquadOperational
                : _baseScript.PirateRaidHordeOperational;
            if (!squadLive)
            {
                kill(null);
                ReleasePollScratch();
                bCondemned = true;
                _baseScript.roamingCaptianBots.Remove(_team);
                _baseScript.capRoamBots.Remove(_team);
                if (!_baseScript.IsBountyHunterBotTeam(_team))
                    _baseScript._currentRoamCaptains--;
                return false;
            }

            int now = Environment.TickCount;
            InLiveCombat = false;
            bool walkPastHostile = BotShouldEngageInsteadOfIdle(now);

            SyncRaiderCollectiveRoamRevision();

            // After a flag switch, leave the old pin — but still fight hostiles in range.
            if (!_baseScript.IsBountyHunterBotTeam(_team)
                && _baseScript.IsRaiderHordeMarchingToObjective(now) && !walkPastHostile)
            {
                _actionQueue.Clear();
                ClearActivePlayerTarget();
                _target = null;
                InLiveCombat = false;
                anchorRoamCollectivePoint(now);
                return base.poll();
            }

            if (EolBotPhysicsUtil.HandleBotUnstickPoll(
                    this, _state, now, _arena, _baseScript, steering, _movement.bCollision,
                    ref _tickLastCollision, ref _tickStuckSample, ref _stuckSampleX, ref _stuckSampleY,
                    ref _unstickPathActive, ref _unstickEscapeTarget, ref _tickUnstickStarted, ref _tickLastUnstickNoCollision,
                    GetEolPathState, ApplyEolPathState,
                    steerAlongPath, EolBotPhysicsUtil.PathRequeueMinMsRoamCaptain,
                    delegate { _targetPoint = null; },
                    null,
                    true,
                    _target != null ? _target._state : null))
            {
                _movement.bCollision = false;
                if (_target != null)
                    EolBotPhysicsUtil.TryFireOnlyWhileUnstick(this, _target);
                else
                    seekAndDestroy(now);
                return base.poll();
            }

            pollForActions(now);

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
                bool seekThrottled = !bypass && currentAction.type == Action.Type.seekAndDestroy
                    && _tickLastSeekActionExec != 0
                    && now - _tickLastSeekActionExec < EolBotActionEvalThrottle.RoamSeekExecMinMs;
                bool repoThrottled = !bypass && currentAction.type == Action.Type.reposition
                    && _tickLastRepositionActionExec != 0
                    && now - _tickLastRepositionActionExec < EolBotActionEvalThrottle.RoamRepositionExecMinMs;
                bool anchorThrottled = !bypass && currentAction.type == Action.Type.anchorRoam
                    && _tickLastAnchorRoamActionExec != 0
                    && now - _tickLastAnchorRoamActionExec < EolBotActionEvalThrottle.RoamAnchorExecMinMs;

                if (!seekThrottled && !repoThrottled && !anchorThrottled)
                {
                    _actionQueue.RemoveAt(bestIdx);
                    if (currentAction.type == Action.Type.seekAndDestroy)
                        _tickLastSeekActionExec = now;
                    else if (currentAction.type == Action.Type.reposition)
                        _tickLastRepositionActionExec = now;
                    else if (currentAction.type == Action.Type.anchorRoam)
                        _tickLastAnchorRoamActionExec = now;

                    switch (currentAction.type)
                    {
                        case Action.Type.seekAndDestroy:
                            seekAndDestroy(now);
                            InLiveCombat = _target != null;
                            break;
                        case Action.Type.reposition:
                            repositionFromDanger(now);
                            InLiveCombat = _target != null;
                            break;
                        case Action.Type.anchorRoam:
                            anchorRoamNearPdb(now);
                            break;
                    }
                }
            }

            if (_target == null && walkPastHostile)
            {
                Navigation.Clear();
                seekAndDestroy(now);
                InLiveCombat = _target != null;
            }

            // Hostiles in aggro: stay on combat — do not resume flag march / pin anchor.
            if (_target == null && !walkPastHostile)
            {
                InLiveCombat = false;
                if (_actionQueue.Count == 0)
                {
                    if (_baseScript.IsBountyHunterBotTeam(_team))
                        seekAndDestroy(now);
                    else if (_baseScript.PirateRaidHordeOperational)
                        anchorRoamCollectivePoint(now);
                    else
                        pushToEnemyFlag(now);
                }
            }

            if (_bStrafe)
            {
                if (now > _tickNextStrafeChange)
                {
                    _tickNextStrafeChange = now + _arena._rand.Next(500, 1600);
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
                RequeueMinMs = EolBotPhysicsUtil.PathRequeueMinMsRoamCaptain,
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
                RequeueMinMs = EolBotPhysicsUtil.PathRequeueMinMsRoamCaptain,
                PathStartIndex = 1,
                Priority = PathPriority.Combat,
            });
        }
    }
}