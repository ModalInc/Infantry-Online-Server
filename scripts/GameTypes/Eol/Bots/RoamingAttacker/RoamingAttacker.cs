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
    public partial class RoamingAttacker : Bot
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
        private Team _targetTeam;
        private Vehicle _roamCapt;
        int _tickLastCaptainSummon;
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
        private WeaponController _weaponClose;  //Our weapon for close range
        private WeaponController _weaponFar;    //Our weapon for anything that is not close range

        private bool _bPatrolEnemy;
        protected SteeringController steering;	//System for controlling the bot's steering
        private float _seperation;
        public bool _bStrafe;
        private int _tickNextStrafeChange;
        private bool _bStrafeLeft;
        int _raidCircuitLastFlagX;
        int _raidCircuitLastFlagY;
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

        public RoamingAttacker(VehInfo.Car type, Helpers.ObjectState state, Arena arena, Script_Eol BaseScript, InfServer.Game.Player _owner)
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
            _raidCircuitLastFlagX = int.MinValue;
            _raidCircuitLastFlagY = int.MinValue;

            MovementMode = BotMovementMode.Api;

            BotHelpers.ApplyCaptureStatsFromVehicle(this);
        }

        public void init()
        {
            WeaponController.WeaponSettings settings = new WeaponController.WeaponSettings();
            settings.aimFuzziness = 54;

            _weapon.setSettings(settings);

            _tickLastCollision = 0;

            base.poll();
        }

        /// <summary>
        /// Allows the script to maintain itself
        /// </summary>
        public override bool poll()
        {
            short x = 0, y = 0;

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
            {
                ReleasePollScratch();
                steering.steerDelegate = null; //Stop movements
                if (bDropshipRespawnPending)
                    return base.poll();
                _baseScript.roamBots[_team]--; //Signal to our captain we died
                if (_baseScript.roamBots[_team] < 0)
                    _baseScript.roamBots[_team] = 0;
                bCondemned = true; //Make sure the bot gets removed in polling
                return base.poll();
            }

            Vehicle capV = _baseScript._pollCacheRoamCaptainVehicle;
            if (capV != null && capV._team == _team && !capV.IsDead)
            {
                _roamCapt = capV;
                x = capV._state.positionX;
                y = capV._state.positionY;
            }
            else
            {
                // Ranged type filter — avoid walking every arena vehicle each poll.
                _roamCapt = null;
                List<Vehicle> caps = _arena.getVehiclesInRange(_state.positionX, _state.positionY, 12000,
                    delegate (Vehicle v)
                    {
                        return v != null && v._type != null
                            && v._type.Id == _baseScript._roamCaptains
                            && v._team == _team
                            && !v.IsDead;
                    });
                if (caps != null && caps.Count > 0)
                {
                    _roamCapt = caps[0];
                    x = _roamCapt._state.positionX;
                    y = _roamCapt._state.positionY;
                }
            }

            // Captain gone: keep the squad up on the PDB / raid objective while the raid is live.
            if (_roamCapt == null)
            {
                if (!_baseScript.PirateRaidHordeOperational)
                {
                    kill(null);
                    ReleasePollScratch();
                    if (_baseScript.roamBots.ContainsKey(_team) && _baseScript.roamBots[_team] > 0)
                        _baseScript.roamBots[_team]--;
                    if (_baseScript.roamBots.ContainsKey(_team) && _baseScript.roamBots[_team] < 0)
                        _baseScript.roamBots[_team] = 0;
                    bCondemned = true;
                    return base.poll();
                }
                if (_baseScript.RaiderPdbAnchorValid)
                {
                    x = _baseScript.RaiderPdbAnchorX;
                    y = _baseScript.RaiderPdbAnchorY;
                }
                else
                    return base.poll();
            }

            //Find out if our owner is gone
            // Roam bot teams have no player owner — never call ActivePlayers.Last() on an empty set.
            if (owner == null
                && (_baseScript == null || !_baseScript.IsRoamBotTeam(_team))
                && (_baseScript == null || !_baseScript.IsBountyHunterBotTeam(_team)))
            {//Find a new owner if not a bot team
                if (_team.ActivePlayerCount > 0)
                    owner = _team.ActivePlayers.Last();
                else
                {
                    kill(null);
                    ReleasePollScratch();
                    if (_baseScript.roamBots[_team] > 0) _baseScript.roamBots[_team]--; //Signal to our captain we died
                    if (_baseScript.roamBots[_team] < 0) _baseScript.roamBots[_team] = 0; //Signal to our captain we died
                    bCondemned = true; //Make sure the bot gets removed in polling
                    return base.poll();
                }
            }

            // Despawn only when this squad's campaign ends (pirate raid or BHB hunt).
            bool squadLive = _baseScript.IsBountyHunterBotTeam(_team)
                ? _baseScript.BountyHunterSquadOperational
                : _baseScript.PirateRaidHordeOperational;
            if (!squadLive)
            {
                kill(null);
                ReleasePollScratch();
                if (_baseScript.roamBots[_team] > 0) _baseScript.roamBots[_team]--; //Signal to our captain we died
                if (_baseScript.roamBots[_team] < 0) _baseScript.roamBots[_team] = 0; //Signal to our captain we died
                bCondemned = true; //Make sure the bot gets removed in polling
                return base.poll();
            }

            int now = Environment.TickCount;
            InLiveCombat = false;
            bool walkPastHostile = BotShouldEngageInsteadOfIdle(now);

            SyncRaiderCollectiveRoamRevision();

            // Left behind (>500): warp to captain — never yank off a walk-by fight.
            bool fighting = _target != null && !_target.IsDead;
            if (!fighting
                && !walkPastHostile
                && _roamCapt != null
                && !_roamCapt.IsDead
                && (_tickLastCaptainSummon == 0 || now - _tickLastCaptainSummon >= Script_Eol.RaiderGruntSummonCooldownMs)
                && _baseScript.TrySummonRaiderGruntToCaptain(this, _roamCapt, now))
            {
                _tickLastCaptainSummon = now;
                _actionQueue.Clear();
                ClearActivePlayerTarget();
                followRaidCaptain(now);
                return base.poll();
            }

            // After a flag switch, leave the old pin — but still fight hostiles in range.
            // BHB never marches pirate flags — always chase the hunted player.
            if (!_baseScript.IsBountyHunterBotTeam(_team)
                && _baseScript.IsRaiderHordeMarchingToObjective(now) && !walkPastHostile)
            {
                _actionQueue.Clear();
                ClearActivePlayerTarget();
                _target = null;
                InLiveCombat = false;
                followRaidCaptain(now);
                return base.poll();
            }

            if (EolBotPhysicsUtil.StuckRecoveryEnabledRoamingAttacker
                && EolBotPhysicsUtil.HandleBotUnstickPoll(
                    this, _state, now, _arena, _baseScript, steering, _movement.bCollision,
                    ref _tickLastCollision, ref _tickStuckSample, ref _stuckSampleX, ref _stuckSampleY,
                    ref _unstickPathActive, ref _unstickEscapeTarget, ref _tickUnstickStarted, ref _tickLastUnstickNoCollision,
                    GetEolPathState, ApplyEolPathState,
                    steerAlongPath, EolBotPhysicsUtil.PathRequeueMinMsRoamGrunt,
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

            // Hostiles in aggro: stay on combat — do not fall back to formation/march.
            if (_target == null && !walkPastHostile)
            {
                InLiveCombat = false;
                if (_arena._bGameRunning && _actionQueue.Count == 0)
                {
                    if (_baseScript.IsBountyHunterBotTeam(_team))
                        seekAndDestroy(now);
                    else if (_baseScript.IsPirateRaidWaveActive && _team == _baseScript.botTeam4)
                        followRaidCaptain(now);
                    else
                        pushToCaptain(now, x, y);
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
                RequeueMinMs = EolBotPhysicsUtil.PathRequeueMinMsRoamGrunt,
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
                RequeueMinMs = EolBotPhysicsUtil.PathRequeueMinMsRoamGrunt,
                PathStartIndex = 1,
                Priority = PathPriority.Combat,
            });
        }
    }
}