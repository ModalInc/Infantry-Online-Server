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
{
    /// <summary>Same behavior as <see cref="BasicDefense"/>; <see cref="vHq"/> is the crashed alien spacecraft (not a player HQ).</summary>
    public partial class AlienHorde : Bot
    {
        private Random _rand;

        private InfServer.Game.Player _target;
        /// <summary>Live player combat target (for engage-cap accounting).</summary>
        public InfServer.Game.Player ActivePlayerTarget { get { return _target; } }
        public bool InLiveCombat;
        public void ClearActivePlayerTarget() { _target = null; InLiveCombat = false; }
        public InfServer.Game.Player owner;
        public Helpers.ObjectState _targetPoint;
        /// <summary>False = heading to a roam pick; true = returning near the crash ship afterward.</summary>
        bool _patrolReturning;
        readonly EolHqPatrolState _hqPatrol = new EolHqPatrolState();
        public Vehicle _targetLocation;
        private Team _targetTeam;
        /// <summary>Crashed alien ship vehicle — same role as <see cref="BasicDefense.vHq"/> (defender HQ).</summary>
        public Vehicle vHq;
        public BotType type;
        protected bool bOverriddenPoll;

        protected List<Vector3> _path;
        protected int _pathTarget;
        protected int _tickLastPath;
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
        public bool _bStrafe;
        private int _tickNextStrafeChange;
        private bool _bStrafeLeft;

        private WeaponController _weaponClose;
        private WeaponController _weaponFar;

        private bool _bPatrolEnemy;
        protected SteeringController steering;
        private float _seperation;
        private int _tickLastVhqLookup;
        private Vehicle _cachedCrashShip;
        int _tickLastFireActionExec;
        int _tickLastRetreatActionExec;
        bool _combatMovedLastPoll;

        protected int _lawQuantity = 1;

        /// <summary>Alien lure chassis (fires item <see cref="AlienLureItemId"/>).</summary>
        public const int AlienLureBotVehId = 152;
        /// <summary>Lure deploy / projectile — only one active at a time per lure bot.</summary>
        public const int AlienLureItemId = 1250;
        Vehicle _activeAlienLure;
        int _tickAlienLureBusyUntil;
        int _alienLureSpawnVehId = -1;

        public AlienHorde(VehInfo.Car type, Helpers.ObjectState state, Arena arena, Script_Eol BaseScript, InfServer.Game.Player _owner)
            : base(type, state, arena,
            new SteeringController(type, state, arena))
        {
            Random rnd = new Random();
            _seperation = (float)rnd.NextDouble();
            steering = _movement as SteeringController;
            _rand = new Random();
            _weaponClose = new WeaponController(_state, new WeaponController.WeaponSettings());
            _weaponFar = new WeaponController(_state, new WeaponController.WeaponSettings());

            // Slot 0 = close, slot 1 = far.
            EolBotCombatUtil.EquipBotWeapons(type, _weaponClose, _weaponFar);
            EquipAlienFarWeapon(type);
            EolBotCombatUtil.WireLiveCombatWeapons(this, _weaponClose, _weaponFar);
            CombatWeaponSelector = (bot, target, los) =>
            {
                ItemInfo itemClose = bot._weaponCloseItem;
                ItemInfo itemFar = bot._weaponFarItem;
                return SelectCombatWeaponRespectingLure(
                    Environment.TickCount, itemFar, itemClose,
                    Helpers.distanceTo(bot._state, target), los);
            };

            _actionQueue = new List<Action>();

            _baseScript = BaseScript;
            owner = _owner;

            MovementMode = BotMovementMode.Api;

            CacheAlienLureSpawnVehicleId();
        }

        void CacheAlienLureSpawnVehicleId()
        {
            _alienLureSpawnVehId = 0;
            if (_type == null || _type.Id != AlienLureBotVehId || _arena?._server?._assets == null)
                return;
            ItemInfo lureItem = _arena._server._assets.getItemByID(AlienLureItemId);
            if (lureItem == null)
                return;
            ItemInfo.VehicleMaker vm = lureItem as ItemInfo.VehicleMaker;
            if (vm == null && lureItem.relativeID != 0)
                vm = _arena._server._assets.getItemByID(lureItem.relativeID) as ItemInfo.VehicleMaker;
            if (vm != null && vm.vehicleID != 0)
                _alienLureSpawnVehId = vm.vehicleID;
            else if (lureItem.relativeID > 0
                && _arena._server._assets.getVehicleByID(lureItem.relativeID) != null)
                _alienLureSpawnVehId = lureItem.relativeID;
        }

        bool IsAlienLureBot()
        {
            return _type != null && _type.Id == AlienLureBotVehId;
        }

        static bool IsAlienLureWeapon(WeaponController weapon)
        {
            return weapon != null && weapon.ItemID == AlienLureItemId;
        }

        /// <summary>True when this lure bot already has a live lure (vehicle or exclusive fire lock).</summary>
        bool HasActiveAlienLure(int now)
        {
            if (!IsAlienLureBot())
                return false;

            if (_activeAlienLure != null)
            {
                if (!_activeAlienLure.IsDead && !_activeAlienLure.bCondemned)
                    return true;
                _activeAlienLure = null;
            }

            return _tickAlienLureBusyUntil != 0 && now < _tickAlienLureBusyUntil;
        }

        /// <summary>Pick close/far weapon but never the lure item while one is still active.</summary>
        WeaponController SelectCombatWeaponRespectingLure(
            int now,
            ItemInfo itemFar,
            ItemInfo itemClose,
            double distRaw,
            bool hasLos)
        {
            WeaponController chosen = EolBotCombatUtil.SelectWeaponForDistance(
                this, now, _weaponClose, _weaponFar, itemFar, itemClose, distRaw, hasLos, allowFarWeapon: true);
            if (!IsAlienLureWeapon(chosen) || !HasActiveAlienLure(now))
                return chosen;

            // Lure already out — use the other gun.
            if (chosen == _weaponFar && _weaponClose != null && _weaponClose.Projectile != null
                && !IsAlienLureWeapon(_weaponClose))
                return _weaponClose;
            if (chosen == _weaponClose && _weaponFar != null && _weaponFar.Projectile != null
                && !IsAlienLureWeapon(_weaponFar))
                return _weaponFar;
            return null;
        }

        /// <summary>Aliens always equip inventory slot 1 (plasma) even when global ShortWeaponOnly is on.</summary>
        void EquipAlienFarWeapon(VehInfo.Car type)
        {
            if (type?.InventoryItems == null || type.InventoryItems.Length <= 1 || type.InventoryItems[1] == 0)
                return;
            ItemInfo farItem = _arena?._server?._assets?.getItemByID(type.InventoryItems[1]);
            if (farItem != null && _weaponFar != null)
                _weaponFar.equip(farItem);
        }

        void OnAlienLureShotFired(int now)
        {
            if (!IsAlienLureBot())
                return;

            // Server-side chassis when the item maps to a vehicle — one live lure per bot.
            if (_alienLureSpawnVehId > 0 && _arena != null && _activeAlienLure == null)
            {
                VehInfo vi = _arena._server._assets.getVehicleByID((ushort)_alienLureSpawnVehId);
                if (vi != null)
                {
                    Helpers.ObjectState at = new Helpers.ObjectState
                    {
                        positionX = _state.positionX,
                        positionY = _state.positionY,
                        positionZ = _state.positionZ,
                        yaw = _state.yaw
                    };
                    try
                    {
                        _activeAlienLure = _arena.newVehicle(vi, _team, null, at);
                    }
                    catch
                    {
                        _activeAlienLure = null;
                    }
                }
            }

            int lockMs = 8000;
            ItemInfo lureItem = _arena?._server?._assets?.getItemByID(AlienLureItemId);
            if (lureItem != null)
            {
                if (lureItem.expireTimer > 0)
                    lockMs = Math.Max(lockMs, lureItem.expireTimer * 10);
                ItemInfo.Projectile proj = lureItem as ItemInfo.Projectile;
                if (proj == null)
                {
                    ItemInfo.MultiUse mug = lureItem as ItemInfo.MultiUse;
                    if (mug != null && mug.childItems != null && mug.childItems.Count > 0)
                        proj = _arena._server._assets.getItemByID(mug.childItems[0].id) as ItemInfo.Projectile;
                }
                if (proj != null && proj.aliveTime > 0)
                    lockMs = Math.Max(lockMs, proj.aliveTime);
            }
            _tickAlienLureBusyUntil = now + lockMs;
        }

        public void init()
        {
            WeaponController.WeaponSettings settings = new WeaponController.WeaponSettings();
            settings.aimFuzziness = 54;

            _weapon.setSettings(settings);

            _weaponFar.setSettings(settings);
            _weaponClose.setSettings(settings);

            _tickLastCollision = 0;

            base.poll();
        }

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

            int now = Environment.TickCount;
            InLiveCombat = false;

            if (_tickLastVhqLookup == 0 || now - _tickLastVhqLookup >= AlienVhqRescanMs)
            {
                _tickLastVhqLookup = now;
                _cachedCrashShip = _baseScript != null ? _baseScript._pollCacheAlienCrashVehicle : null;
                if (_cachedCrashShip != null && (_cachedCrashShip.IsDead || _cachedCrashShip._team != _baseScript.botTeam7))
                    _cachedCrashShip = null;
                if (_cachedCrashShip == null && _arena != null)
                {
                    List<Vehicle> ships = _arena.getVehiclesInRange(_state.positionX, _state.positionY, 16000,
                        delegate (Vehicle v)
                        {
                            return v != null && v._type != null
                                && v._type.Id == Script_Eol.AlienCrashVehicleId
                                && _baseScript != null
                                && v._team == _baseScript.botTeam7
                                && !v.IsDead;
                        });
                    if (ships != null && ships.Count > 0)
                        _cachedCrashShip = ships[0];
                }
            }
            vHq = _cachedCrashShip;

            if (vHq == null)
                return base.poll();

            if (IsDead)
            {
                ReleasePollScratch();
                steering.steerDelegate = null;
                if (bDropshipRespawnPending)
                    return base.poll();
                if (_baseScript.botCount != null && _baseScript.botCount.ContainsKey(_team))
                {
                    _baseScript.botCount[_team]--;
                    if (_baseScript.botCount[_team] < 0)
                        _baseScript.botCount[_team] = 0;
                }
                bCondemned = true;
                return base.poll();
            }

            if (owner == null
                && !_baseScript.IsAlienBotTeam(_team))
            {
                if (_team.ActivePlayerCount > 0)
                    owner = _team.ActivePlayers.Last();
                else
                {
                    kill(null);
                    ReleasePollScratch();
                    if (_baseScript.botCount != null && _baseScript.botCount.ContainsKey(_team))
                    {
                        _baseScript.botCount[_team]--;
                        if (_baseScript.botCount[_team] < 0)
                            _baseScript.botCount[_team] = 0;
                    }
                    bCondemned = true;
                    return base.poll();
                }
            }

            if (!_baseScript.IsAlienBotTeam(_team) && !_baseScript.TeamHasLiveHqVehicle(_team))
            {
                kill(null);
                ReleasePollScratch();
                bCondemned = true;
                if (_baseScript.botCount != null && _baseScript.botCount.ContainsKey(_team))
                {
                    _baseScript.botCount[_team]--;
                    if (_baseScript.botCount[_team] < 0)
                        _baseScript.botCount[_team] = 0;
                }
                return false;
            }


            if (EolBotPhysicsUtil.HandleBotUnstickPoll(
                    this, _state, now, _arena, _baseScript, steering, _movement.bCollision,
                    ref _tickLastCollision, ref _tickStuckSample, ref _stuckSampleX, ref _stuckSampleY,
                    ref _unstickPathActive, ref _unstickEscapeTarget, ref _tickUnstickStarted, ref _tickLastUnstickNoCollision,
                    GetEolPathState, ApplyEolPathState,
                    steerAlongPath, EolBotPhysicsUtil.PathRequeueMinMsAlien,
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
            // Full self-aggro — aliens must break patrol for any hostile in visual/seek range.
            bool stickyCombat = EolBotSenseUtil.ShouldBreakIdleForCombat(
                _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target);

            if (needPatrol && stickyCombat)
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
                if (!stickyCombat)
                    ClearActivePlayerTarget();
                _bStrafe = false;
                patrolHQ(now);
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
                RequeueMinMs = c_pathUpdateInterval,
                PathStartIndex = 1,
                Priority = PathPriority.Combat,
            });
        }
    }
}
