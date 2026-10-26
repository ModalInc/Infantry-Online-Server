 using System;
using System.Collections.Generic;
using System.Text;

using InfServer;
using InfServer.Game;
using InfServer.Protocol;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Bots
{
    // Bot Class
    /// Represents a bot, inherited from a vehicle
    ///////////////////////////////////////////////////////
    public class Bot : Vehicle, IEventObject
    {   // Member variables
        ///////////////////////////////////////////////////
        public new VehInfo.Car _type;                   //The car type we represent
        public string _alias;                           //Display name for killfeed and team list
        /// <summary>Current-game kills (score charts / Eol reflection).</summary>
        public int _kills;
        /// <summary>Current-game deaths (score charts / Eol reflection).</summary>
        public int _deaths;
        /// <summary>Current-game kill points for chart sorting.</summary>
        public int _killPoints;
        /// <summary>Experience used by SkillLogic (@N) for flag / item gates.</summary>
        public int _experience;
        /// <summary>Optional skill / attribute quantities for SkillLogic (bare id / -attr).</summary>
        public Dictionary<int, int> _skills;
        /// <summary>Snapshot of stats from the previous game (ScorePreviousGame chart).</summary>
        public Data.PlayerStats _statsLastGame;
        public MovementController _movement;            //Our movement controller
        public WeaponController _weapon;                //Active combat gun (may swap close/far each tick)
        /// <summary>Stable close / slot-0 controller (never overwritten by engage swaps).</summary>
        public WeaponController _weaponClose;
        /// <summary>Optional far weapon (slot 1). When set, engage uses range-based selection.</summary>
        public WeaponController _weaponFar;
        /// <summary>Item meta for <see cref="_weaponClose"/> (close).</summary>
        public ItemInfo _weaponCloseItem;
        /// <summary>Item meta for <see cref="_weaponFar"/>.</summary>
        public ItemInfo _weaponFarItem;
        /// <summary>Sticky combat gun (reload / fire-delay aware).</summary>
        public WeaponController _combatWeaponLocked;
        /// <summary>
        /// Optional override for combat weapon pick (e.g. alien lure filter).
        /// Args: bot, target state, has LOS. Return null to use default range select.
        /// </summary>
        public Func<Bot, Helpers.ObjectState, bool, WeaponController> CombatWeaponSelector;

        /// <summary>Combat drive commit — holds thrust/strafe briefly to reduce rubber-band vel flips.</summary>
        public int _combatDriveCommitTick;
        public float _combatDriveCommitF;
        public float _combatDriveCommitS;

        public int _itemUseID;                          //The item we're using

        public List<ItemInfo.UtilityItem> _activeEquip;	//Our active equipment

        /// <summary>Player ids that already received <c>SC_Vehicles</c> for this bot (ranged spawn / walk-in).</summary>
        readonly HashSet<int> _clientsAwareOfBot = new HashSet<int>();

        /// <summary>Spectators following this player-mimic bot hull (right-click spectate).</summary>
        public readonly List<Player> _spectators = new List<Player>();

        public BotMovementMode MovementMode { get; set; } = BotMovementMode.Legacy;

        public BotLoadout Loadout { get; private set; }

        public BotNavigator Navigation { get; private set; }

        /// <summary>Shared action runtime (AttackPlayer, CaptureFlag, …). Always available.</summary>
        public InfServer.Game.Modules.BotActions.BotActionRuntime Actions { get; private set; }

        private BotMotionRequest _motionRequest;
        private int _tickWallEscapeUntil;
        /// <summary>
        /// When true, death uses scripted revive at the spawn anchor (same identity/name) instead of permanent removal.
        /// Set at spawn by the game script; default false = die and leave arena lists.
        /// </summary>
        public bool bRespawn;
        /// <summary>
        /// Deathmatch-style life limit. <c>0</c> = unlimited respawns; <c>N &gt; 0</c> = leave the game after <c>N</c> deaths
        /// (no further anchor revive). Compared against <see cref="_deaths"/> after each kill.
        /// </summary>
        public int MaxDeaths;
        /// <summary>True when <see cref="MaxDeaths"/> &gt; 0 and <see cref="_deaths"/> has reached that limit.</summary>
        public bool HasExhaustedDeaths => MaxDeaths > 0 && _deaths >= MaxDeaths;
        /// <summary>Script-owned anchor revive in progress — corpse stays at death site until revived or cancelled.</summary>
        public bool bDropshipRespawnPending;
        private int[] _utilityArmorIgnore = new int[6];
        private int[] _utilityArmorPercent = new int[6];
        private BotUtilityMovementModifiers _utilityMovementMods;

        private int _tickLastPoll;                      //The last tick at which poll was called
        private int _tickLastUpdate;                    //HACK: For stemming the flow of updates
        private short _lastSentPosX;
        private short _lastSentPosY;
        private byte _lastSentYaw;
        /// <summary>Fake client tick (low 16 bits of player-style updateTick).</summary>
        internal ushort _clientTick;
        /// <summary>Last position sent through player-mimic route (warp audience).</summary>
        internal short _lastPlayerRoutePosX;
        internal short _lastPlayerRoutePosY;
        internal bool _hasPlayerRoutePos;
        /// <summary>Wall clock of last player-mimic route (for Δpos→wire velocity).</summary>
        internal int _tickLastPlayerRoute;
        /// <summary>Wire velocity overrides for the next CaptureBotPose (0 = use state).</summary>
        internal short _wireVelX;
        internal short _wireVelY;
        internal bool _wireVelOverride;

        /// <summary>
        /// Align outbound velocity with actual position travel so client coast matches
        /// the next absolute pose (kills residual lag when sim vel under-reports).
        /// </summary>
        public void PreparePlayerMimicWireVelocity()
        {
            _wireVelOverride = false;
            if (!_hasPlayerRoutePos || _tickLastPlayerRoute == 0 || _state == null)
                return;

            int now = Environment.TickCount;
            int dt = now - _tickLastPlayerRoute;
            if (dt < 8 || dt > 250)
                return;

            int ddx = _state.positionX - _lastPlayerRoutePosX;
            int ddy = _state.positionY - _lastPlayerRoutePosY;
            if (ddx == 0 && ddy == 0)
                return;

            // Server integrates: pos += (vel/10000)*dt ; Y also *= 0.70 in MovementController.
            long synX = (long)ddx * 10000L / dt;
            long synY = (long)(ddy * 10000L / (dt * 0.70));
            if (synX > short.MaxValue) synX = short.MaxValue;
            if (synX < short.MinValue) synX = short.MinValue;
            if (synY > short.MaxValue) synY = short.MaxValue;
            if (synY < short.MinValue) synY = short.MinValue;

            short simX = _state.velocityX;
            short simY = _state.velocityY;
            // Prefer synthesized when it better explains the observed travel.
            if (Math.Abs(synX) > Math.Abs(simX) + 40 || Math.Abs(synY) > Math.Abs(simY) + 40)
            {
                _wireVelX = (short)synX;
                _wireVelY = (short)synY;
                _wireVelOverride = true;
            }
        }

        /// <summary>
        /// Player-mimic updateTick: low 16 bits must be wall-clock ms (GetTickCount &amp; 0xFFFF),
        /// same as real <c>CS_PlayerUpdate.tickCount</c>. The client steps remote car physics
        /// by that delta — advancing +1 per packet under-stepped ~40x and looked laggy.
        /// </summary>
        public int AdvancePlayerStyleUpdateTick()
        {
            int now = Environment.TickCount;
            _clientTick = (ushort)(now & 0xFFFF);
            int updateTick = ((now >> 16) << 16) + _clientTick;
            // Do not overwrite _state.lastUpdate with the wire tick — server extrapolation
            // and bot AI use Environment.TickCount space via writeStateFromPhysics.
            _state.lastUpdateServer = now;
            return updateTick;
        }

        /// <summary>~60 Hz while moving under observation (player-like density without flood).</summary>
        private const int MovingUpdateIntervalMs = 16;
        /// <summary>~50 Hz while paused under observation (aim / fire stalls).</summary>
        private const int ObservedIdleUpdateIntervalMs = 20;
        /// <summary>No observer in weapon/view probe — radar-only pose.</summary>
        private const int RadarOnlyUpdateIntervalMs = 1500;
        /// <summary>Warps / teleports only; normal motion relies on <see cref="MovingUpdateIntervalMs"/>.</summary>
        private const short WarpPosDeltaForImmediateUpdate = 32;
        /// <summary>Any position change while observed (idle path).</summary>
        private const short MinPosDeltaForUpdate = 1;
        private const byte MinYawDeltaForUpdate = 1;
        /// <summary>Reuse observer sample briefly to avoid alloc every poll.</summary>
        private const int NearbyPlayerSampleMs = 40;

        int _tickNearbyPlayerSample;
        int _cachedNearbyPlayerCount;

        /// <summary>True if this client was already sent the bot vehicle create.</summary>
        public bool IsClientAwareOfBot(Player player)
        {
            return player != null && _clientsAwareOfBot.Contains(player._id);
        }

        /// <summary>Mark clients that received <c>SC_Vehicles</c> for this bot.</summary>
        public void MarkClientsAwareOfBot(IEnumerable<Player> players)
        {
            if (players == null)
                return;
            foreach (Player p in players)
            {
                if (p != null)
                    _clientsAwareOfBot.Add(p._id);
            }
        }

        public void MarkClientAwareOfBot(Player player)
        {
            if (player != null)
                _clientsAwareOfBot.Add(player._id);
        }

        public void ClearClientAwareOfBot(Player player)
        {
            if (player != null)
                _clientsAwareOfBot.Remove(player._id);
        }

        /// <summary>Forget every observer so the next in-range update can recreate the hull.</summary>
        public void ClearClientsAwareOfBot()
        {
            _clientsAwareOfBot.Clear();
        }

        int _hullReattachUntil;

        /// <summary>
        /// After a death graphic, clients drop the car body. For a short window, re-send
        /// <c>SC_Vehicles</c> without Leave/Enter (that prints "Player entered" and can desync).
        /// </summary>
        public void RequestHullReattach(int untilTick)
        {
            if (untilTick == 0)
                untilTick = 1;
            _hullReattachUntil = untilTick;
        }

        public bool NeedsHullReattach
        {
            get { return _hullReattachUntil != 0 && (Environment.TickCount - _hullReattachUntil) < 0; }
        }

        /// <summary>Score-chart points (kill + assist + bonus style for bots = kill points).</summary>
        public long Points => _killPoints;

        /// <summary>Experience for SkillLogic (@N). Defaults to 0 unless scripts award it.</summary>
        public int Experience => _experience;

        /// <summary>Look up a skill/attribute quantity for SkillLogic checks.</summary>
        public bool TryGetSkillQuantity(int skillId, out int quantity)
        {
            quantity = 0;
            if (_skills == null)
                return false;
            return _skills.TryGetValue(skillId, out quantity);
        }

        /// <summary>Grant or set a skill/attribute quantity used by SkillLogic.</summary>
        public void SetSkillQuantity(int skillId, int quantity)
        {
            if (_skills == null)
                _skills = new Dictionary<int, int>();
            if (quantity <= 0)
                _skills.Remove(skillId);
            else
                _skills[skillId] = quantity;
        }

        /// <summary>Record a kill for charts (optional kill-points for sorting).</summary>
        public void RecordKill(int killPoints = 0)
        {
            _kills++;
            if (killPoints > 0)
                _killPoints += killPoints;
            else
                _killPoints++;
        }

        /// <summary>Record a death for charts.</summary>
        public void RecordDeath()
        {
            _deaths++;
        }

        /// <summary>Build a PlayerStats row for SC_ScoreChart.</summary>
        public Data.PlayerStats GetChartStats()
        {
            return new Data.PlayerStats
            {
                kills = _kills,
                deaths = _deaths,
                killPoints = _killPoints,
                deathPoints = 0,
                assistPoints = 0,
                bonusPoints = 0,
                vehicleKills = 0,
                vehicleDeaths = 0,
                playSeconds = 0
            };
        }

        /// <summary>Snapshot current game into last-game and zero current counters.</summary>
        public void ClearGameStats()
        {
            _statsLastGame = GetChartStats();
            _kills = 0;
            _deaths = 0;
            _killPoints = 0;
        }

        #region EventObject
        /// <summary>
        /// The event logger, if exists, for this class
        /// </summary>
        public EventHandlers events
        {
            get;
            set;
        }

        #region ThreadedObject
        /// <summary>
        /// The event logger, if exists, for this class
        /// </summary>
        public LogClient _eventLogger
        {
            get;
            set;
        }

        /// <summary>
        /// The sync object for this class
        /// </summary>
        public object _sync
        {
            get;
            set;
        }
        #endregion

        /// <summary>
        /// Initializes events for the event object
        /// </summary>
        public void eventInit(bool bParseEvents)
        {
            EventObjects.eventInit(this, bParseEvents);
        }

        /// <summary>
        /// Triggers an event
        /// </summary>
        public void trigger(string name, params object[] args)
        {
            EventObjects.trigger(this, name, true, args);
        }

        /// <summary>
        /// Calls a singlecast event, returning a value
        /// </summary>
        public object call(string name, params object[] args)
        {
            return EventObjects.callsync(this, name, true, args);
        }

        /// <summary>
        /// Calls a singlecast event, returning a value
        /// </summary>
        public object callsync(string name, bool bSync, params object[] args)
        {
            return EventObjects.callsync(this, name, bSync, args);
        }

        /// <summary>
        /// Determines if a event type exists
        /// </summary>
        public bool exists(string name)
        {   //Does the event exist?
            HandlerList list;
            return events.TryGetValue(name, out list);
        }

        /// <summary>
        /// Flushes the handlerlist - removing all handlers
        /// </summary>
        public void flushEvents()
        {   //Kill all the handlers!
            using (DdMonitor.Lock(_sync))
                events.Clear();
        }
        #endregion

        ///////////////////////////////////////////////////
        // Member Functions
        ///////////////////////////////////////////////////
        /// <summary>
        /// Generic constructor
        /// </summary>
        public Bot(VehInfo.Car type, Arena arena)
            : base(type, arena)
        {   //Initialize the event object
            eventInit(true);

            //Populate variables
            _type = type;

            _movement = new MovementController(_type, _state, arena);
            _weapon = new WeaponController(_state, new WeaponController.WeaponSettings());
            _activeEquip = new List<ItemInfo.UtilityItem>();
            InitBotApi();

            _tickLastPoll = Environment.TickCount;
            _lastSentPosX = _state.positionX;
            _lastSentPosY = _state.positionY;
            _lastSentYaw = _state.yaw;

            _bBotVehicle = true;

            configureBot();
        }

        /// <summary>
        /// Generic constructor
        /// </summary>
        public Bot(VehInfo.Car type, Helpers.ObjectState state, Arena arena)
            : base(type, state, arena)
        {   //Initialize the event object
            eventInit(true);

            //Populate variables
            _type = type;

            _movement = new MovementController(_type, _state, arena);
            _weapon = new WeaponController(_state, new WeaponController.WeaponSettings());
            _activeEquip = new List<ItemInfo.UtilityItem>();
            InitBotApi();

            _tickLastPoll = Environment.TickCount;
            _lastSentPosX = _state.positionX;
            _lastSentPosY = _state.positionY;
            _lastSentYaw = _state.yaw;

            _bBotVehicle = true;

            configureBot();
        }



        /// <summary>
        /// Generic constructor
        /// </summary>
        public Bot(VehInfo.Car type, Helpers.ObjectState state, Arena arena, MovementController movement)
            : base(type, state, arena)
        {	//Initialize the event object
            eventInit(true);

            //Populate variables
            _type = type;

            _movement = movement;
            _weapon = new WeaponController(_state, new WeaponController.WeaponSettings());
            _activeEquip = new List<ItemInfo.UtilityItem>();
            InitBotApi();

            _tickLastPoll = Environment.TickCount;
            _lastSentPosX = _state.positionX;
            _lastSentPosY = _state.positionY;
            _lastSentYaw = _state.yaw;

            _bBotVehicle = true;

            configureBot();
        }
        /// <summary>
        /// Configures the bot based on the vehicle information (weapons + AlwaysOn utilities).
        /// </summary>
        protected virtual void configureBot()
        {
            Loadout.ConfigureFromVehicle();

            if (_type is VehInfo.Car car)
            {
                BotWeaponUtil.ResolveWeaponItems(car, out _, out ItemInfo farItem, includeFarSlot: true);
                if (farItem != null && _weaponFar == null)
                    _weaponFar = new WeaponController(_state, new WeaponController.WeaponSettings());
                BotWeaponUtil.EquipAndWire(this, _weapon, _weaponFar, includeFarWeapon: farItem != null);
            }
            else
            {
                RefreshUtilityEffects();
            }
        }

        private void InitBotApi()
        {
            Loadout = new BotLoadout(this);
            Navigation = new BotNavigator(this);

            var module = _arena?.BotActionModule;
            if (module != null)
                Actions = module.CreateRuntime(this);
            else
            {
                Actions = new InfServer.Game.Modules.BotActions.BotActionRuntime(
                    this,
                    new InfServer.Game.Modules.BotActions.BotActionRegistry(),
                    _arena != null
                        ? new InfServer.Game.Modules.BotActions.ArenaDefaultObjectives(_arena)
                        : InfServer.Game.Modules.BotActions.NullObjectives.Instance);
            }
        }

        public void RefreshUtilityEffects()
        {
            Loadout.SyncActiveEquipBroadcast();
            BotUtilityEffects.AccumulateUtilityArmor(Loadout.GetActiveUtilities(), out _utilityArmorIgnore, out _utilityArmorPercent);
            _utilityMovementMods = BotUtilityEffects.ComputeMovementModifiers(Loadout.GetActiveUtilities());
            _movement.ApplyUtilityMovementModifiers(_utilityMovementMods);
        }

        protected override void GetUtilityArmorForChannel(int channel, out int ignore, out int percent)
        {
            if (channel < 0 || channel >= 6)
            {
                ignore = 0;
                percent = 0;
                return;
            }

            ignore = _utilityArmorIgnore[channel];
            percent = _utilityArmorPercent[channel];
        }

        #region Bot API

        public void HoldPosition()
        {
            _motionRequest = BotMotionRequest.HoldPosition;
        }

        public void SetSteer(Func<InfantryVehicle, Vector3> steer, bool skipBodyRotate = false)
        {
            _motionRequest = BotMotionRequest.Custom(steer, skipBodyRotate);
        }

        public void FollowNavigationPath(Func<InfantryVehicle, Vector3>? fallbackWhilePending = null)
        {
            _motionRequest = BotMotionRequest.FollowNavigation();
            if (fallbackWhilePending != null)
                _motionRequest.Steer = fallbackWhilePending;
        }

        public void Approach(Helpers.ObjectState dest, Func<InfantryVehicle, Vector3>? fallbackSteer, BotNavigateOptions? options = null)
        {
            Navigation.Approach(dest, fallbackSteer, options);
            FollowNavigationPath(fallbackSteer);
        }

        public void SetAim(float angle, bool enabled = true)
        {
            _motionRequest.AimEnabled = enabled;
            if (enabled)
                _motionRequest.AimAngle = angle;
        }

        public void SetDrive(float forward, float strafe, float rotate)
        {
            BotWallAvoidance.AdjustDrive(this, ref forward, ref strafe, ref rotate);
            _motionRequest = BotMotionRequest.Drive(forward, strafe, rotate);
        }

        public void Strafe(float amount)
        {
            _motionRequest = BotMotionRequest.Drive(_motionRequest.DriveForward, amount, _motionRequest.DriveRotate);
            if (_motionRequest.Kind != BotMotionKind.Drive)
                _motionRequest = BotMotionRequest.Drive(0f, amount, 0f);
        }

        public void Thrust(float amount)
        {
            if (_motionRequest.Kind == BotMotionKind.Drive)
                _motionRequest = BotMotionRequest.Drive(amount, _motionRequest.DriveStrafe, _motionRequest.DriveRotate);
            else
                _motionRequest = BotMotionRequest.Drive(amount, 0f, 0f);
        }

        public void Rotate(float amount)
        {
            if (_motionRequest.Kind == BotMotionKind.Drive)
                _motionRequest = BotMotionRequest.Drive(_motionRequest.DriveForward, _motionRequest.DriveStrafe, amount);
            else
                _motionRequest = BotMotionRequest.Drive(0f, 0f, amount);
        }

        /// <summary>Request a catalog action (AttackPlayer, CaptureFlag, …).</summary>
        public bool RequestAction(InfServer.Game.Modules.BotActions.BotActionId action, bool ignoreCooldown = false)
            => Actions != null && Actions.Request(action, ignoreCooldown);

        /// <summary>Tick the current action (sets Bot API motion). Call before base.poll / ApplyMotionRequest.</summary>
        public InfServer.Game.Modules.BotActions.BehaviorResult TickAction(int now = 0)
            => Actions != null
                ? Actions.Tick(now)
                : InfServer.Game.Modules.BotActions.BehaviorResult.Failed;

        /// <summary>Request + tick in one step for script action bodies.</summary>
        public InfServer.Game.Modules.BotActions.BehaviorResult RunAction(
            InfServer.Game.Modules.BotActions.BotActionId action, int now = 0, bool ignoreCooldown = true)
        {
            if (!RequestAction(action, ignoreCooldown))
                return InfServer.Game.Modules.BotActions.BehaviorResult.Failed;
            return TickAction(now);
        }

        public void FollowPath(Func<InfantryVehicle, Vector3>? fallbackWhilePending = null)
        {
            FollowNavigationPath(fallbackWhilePending);
        }

        public bool TryAimAndFire(WeaponController weapon, Helpers.ObjectState targetState, bool requireLineOfSight = true, int freezeMs = 0)
        {
            return BotCombat.TryAimAndFire(this, weapon, targetState, requireLineOfSight, freezeMs);
        }

        /// <summary>
        /// Routes steering to the Bot API in Api mode, or directly to SteeringController in Legacy mode.
        /// </summary>
        public void ApplySteer(Func<InfantryVehicle, Vector3>? steer, bool skipBodyRotate = false)
        {
            if (MovementMode == BotMovementMode.Api)
            {
                if (steer == null)
                    HoldPosition();
                else
                    SetSteer(steer, skipBodyRotate);
            }
            else if (_movement is SteeringController steering)
            {
                steering.steerDelegate = steer;
            }
        }

        /// <summary>
        /// Queues navigation and applies follow-path motion (Api) or leaves path handling to the script (Legacy).
        /// </summary>
        public void ApplyNavigationApproach(Helpers.ObjectState dest, Func<InfantryVehicle, Vector3>? fallbackSteer, BotNavigateOptions? options = null)
        {
            if (MovementMode == BotMovementMode.Api)
            {
                Approach(dest, fallbackSteer, options);
                FollowNavigationPath(fallbackSteer);
            }
        }

        #endregion

        protected void ApplyMotionRequest()
        {
            if (MovementMode != BotMovementMode.Api)
                return;

            // Keep last Drive/Follow/Custom sticky across polls. Scripts throttle action
            // bodies (~50ms); clearing to Kind.None here made every other poll
            // ClearAnalogDrive → velocity dies → client hop + no run anim.
            BotMotionApplier.Apply(this, _motionRequest);
        }

        #region State
        /// <summary>
        /// Causes the vehicle to die
        /// </summary>
        public override void kill(Player killer, int weaponID)
        {   //Die, and then cease movement
            _state.health = 0;
            _tickDead = Environment.TickCount;

            _movement.stop();
            _movement.bEnabled = false;
            // Invalidate pending pathfinder work immediately so callbacks are ignored.
            Navigation?.Clear();
            if (_movement is SteeringController steeringKill)
                steeringKill.steerDelegate = null;

            //Notify the arena
            _arena.handleBotDeath(this, killer, weaponID);
        }

        /// <summary>
        /// The vehicle is being destroyed, clean up assets
        /// </summary>
        public override void destroy(bool bRestoreBase, bool bRemove)
        {
            // Drop path / steer before arena teardown (safe if kill() already cleared).
            try
            {
                ClearSpectators();
                Navigation?.Clear();
                Actions?.Clear();
                _motionRequest = BotMotionRequest.HoldPosition;
                if (_movement is SteeringController steeringDestroy)
                    steeringDestroy.steerDelegate = null;
            }
            catch
            {
                // Ignore teardown races.
            }

            bool alreadyCondemned = bCondemned;
            if (!alreadyCondemned)
                base.destroy(bRestoreBase, bRemove);
            else if (bRemove && _arena != null)
            {
                // Script flipped condemned without destroy — still notify clients / drop vehicle.
                try { _arena.lostVehicle(this, bRemove); }
                catch { /* teardown race */ }
            }

            // Drop from live bot tracker immediately (idempotent).
            _arena?.lostBot(this);
        }

        /// <summary>Drop any players currently spectating this bot hull.</summary>
        public void ClearSpectators()
        {
            if (_spectators == null || _spectators.Count == 0)
                return;

            foreach (Player p in _spectators.ToList())
            {
                if (p == null)
                    continue;
                if (p._spectatingBot == this)
                {
                    p._spectatingBot = null;
                    Helpers.Player_SpectateTarget(p, 0, stop: true);
                }
            }
            _spectators.Clear();
        }


        /// <summary>
        /// Looks after the bot's functionality
        /// </summary>
        public virtual bool poll()
        {   //Calculate our delta time..
            int tickCount = Environment.TickCount;
            int delta = tickCount - _tickLastPoll;

            //Don't need to update too much
            if (delta < 0)
                return false;

            _tickLastPoll = tickCount;

            //If it's a ridiculous delta, clamp rather than fully skipping the tick
            if (delta > 600)
            {
                // Log.write(TLog.Warning, "Encountered excessive bot delta of {0}", delta);
                delta = 100;
            }
            else if (delta > 100)
            {
                delta = 100;
            }

            //Are we dead?
            if (IsDead)
            {
                if (!bDropshipRespawnPending)
                {
                    //Drop our items
                    VehInfo vehicle = _arena._server._assets.getVehicleByID(_type.Id);
                    ItemInfo item = _arena._server._assets.getItemByID(vehicle.DropItemId);
                    if (item != null)
                        _arena.itemSpawn(item, (ushort)vehicle.DropItemQuantity, _state.positionX, _state.positionY, null);
                }

                //Should we remove ourself from the world?
                if (!bDropshipRespawnPending
                    && _type.RemoveDeadTimer != 0 && _tickDead != 0 &&
                    tickCount - _tickDead > (_type.RemoveDeadTimer * 1000))
                    destroy(true);
                else
                {   //Keep sending 'corpse' updates
                    if (tickCount - _tickLastUpdate > 1500)
                    {
                        _tickLastUpdate = tickCount;
                        return true;
                    }
                }
            }
            else
            {
                // Equip sync / utility armor refresh only on loadout change — not every poll.

                if (Actions != null && Actions.AutoTick && Actions.IsRunning)
                    Actions.Tick(tickCount);

                if (_tickWallEscapeUntil > tickCount)
                    FollowNavigationPath();

                ApplyMotionRequest();

                //Allow our controller to update our vehicle state
                _movement.updateState(delta);

                // Keep Api navigator path index in sync with what steering actually advanced.
                if (MovementMode == BotMovementMode.Api
                    && _movement is SteeringController steerSync)
                {
                    Navigation.SyncPathTargetFromSteering(steerSync.FollowPathTarget);
                }

                TryWallEscapeRecovery(tickCount);

                if (shouldBroadcastUpdate(tickCount))
                {
                    rememberBroadcastState();
                    return true;
                }
                return false;
            }

            return false;
        }

        private bool shouldBroadcastUpdate(int tickCount)
        {
            // Shots must go out immediately so clients see the fire.
            if (_itemUseID != 0)
            {
                _tickLastUpdate = tickCount;
                return true;
            }

            // Warps / large snaps — don't wait for the moving interval.
            if (hasWarpOrLargeSnap())
            {
                _tickLastUpdate = tickCount;
                return true;
            }

            int observers = countObservingPlayers(tickCount);
            bool observed = observers > 0;
            bool moving = _movement != null && (_movement.IsMoving
                || Math.Abs(_state.velocityX) > 2
                || Math.Abs(_state.velocityY) > 2);

            int interval = observed
                ? (moving ? MovingUpdateIntervalMs : ObservedIdleUpdateIntervalMs)
                : RadarOnlyUpdateIntervalMs;

            // interval 0 = every poll while moving under observation
            if (interval > 0 && tickCount - _tickLastUpdate <= interval)
                return false;

            // Under observation while moving: always send — skip delta gate so clients
            // never miss frames between tiny steps.
            if (observed && moving)
            {
                _tickLastUpdate = tickCount;
                return true;
            }

            if (!hasMeaningfulMoveOrTurn())
                return false;

            _tickLastUpdate = tickCount;
            return true;
        }

        /// <summary>
        /// In-game / spectating players who should get high-rate poses (route + weapon + sniper view).
        /// </summary>
        private int countObservingPlayers(int tickCount)
        {
            if (_arena == null)
                return 0;
            if (_tickNearbyPlayerSample != 0
                && tickCount - _tickNearbyPlayerSample >= 0
                && tickCount - _tickNearbyPlayerSample < NearbyPlayerSampleMs)
                return _cachedNearbyPlayerCount;

            int probe = Arena.GetBotObserverProbeRange();
            List<Player> candidates = _arena.getPlayersAndSpecInRange(
                _state.positionX, _state.positionY, probe);
            int count = 0;
            if (candidates != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    Player p = candidates[i];
                    if (p == null || p.IsDead)
                        continue;
                    if (p._state == null)
                        continue;

                    double dist = Helpers.distanceTo(_state, p._state);
                    int view = p.GetBotObservationRange();
                    if (dist <= view)
                        count++;
                }
            }

            _cachedNearbyPlayerCount = count;
            _tickNearbyPlayerSample = tickCount;
            return _cachedNearbyPlayerCount;
        }

        private bool hasWarpOrLargeSnap()
        {
            return Math.Abs(_state.positionX - _lastSentPosX) >= WarpPosDeltaForImmediateUpdate
                || Math.Abs(_state.positionY - _lastSentPosY) >= WarpPosDeltaForImmediateUpdate;
        }

        private bool hasMeaningfulMoveOrTurn()
        {
            if (Math.Abs(_state.positionX - _lastSentPosX) >= MinPosDeltaForUpdate)
                return true;
            if (Math.Abs(_state.positionY - _lastSentPosY) >= MinPosDeltaForUpdate)
                return true;

            // Keep velocity extrapolation fresh even when position rounds flat.
            if (Math.Abs(_state.velocityX) > 20 || Math.Abs(_state.velocityY) > 20)
                return true;

            int yawDelta = Math.Abs(_state.yaw - _lastSentYaw);
            if (yawDelta > 120)
                yawDelta = 240 - yawDelta;
            return yawDelta >= MinYawDeltaForUpdate;
        }

        private void TryWallEscapeRecovery(int tickCount)
        {
            if (MovementMode != BotMovementMode.Api)
                return;

            if (_tickWallEscapeUntil > tickCount)
                return;

            if (!_movement.bCollision)
                return;

            Helpers.ObjectState? escape = BotWallAvoidance.FindEscapeWaypoint(this);
            if (escape == null)
                return;

            Navigation.Clear();
            Navigation.NavigateTo(escape, new BotNavigateOptions
            {
                ForceRequeue = true,
                PathStartIndex = 0,
                RequeueMinMs = 400,
                Priority = PathPriority.Combat,
            });
            FollowNavigationPath();
            _tickWallEscapeUntil = tickCount + 700;
        }

        private void rememberBroadcastState()
        {
            _lastSentPosX = _state.positionX;
            _lastSentPosY = _state.positionY;
            _lastSentYaw = _state.yaw;
        }


        /// <summary>
        /// Handles damage from explosions triggered nearby
        /// </summary>		
        public override void applyExplosion(Player attacker, int dmgX, int dmgY, ItemInfo.Projectile wep)
        {
            applyExplosionDamage(true, attacker, dmgX, dmgY, wep);

            if (IsDead || _movement == null || wep == null)
                return;

            byte? blastYaw = attacker?._state != null ? attacker._state.yaw : (byte?)null;
            _movement.ApplyExplosionRecoil(dmgX, dmgY, wep, blastYaw);

            // Let knockback coast on the wire before combat drive re-commits.
            _combatDriveCommitTick = 0;
            _combatDriveCommitF = 0f;
            _combatDriveCommitS = 0f;
            _tickLastUpdate = 0;
        }

        /// <summary>
        /// Causes the zombie to cease movement for the specified period of time
        /// </summary>		
        public void freezeMovement(int duration)
        {
            _movement.freezeMovement(duration);
        }

        /// <summary>
        /// Queues a path request and enables smooth corridor following when the path arrives.
        /// </summary>
        protected void QueuePath(
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> onPathReceived,
            PathPriority priority = PathPriority.Idle)
        {
            if (_arena._pathfinder == null)
            {
                onPathReceived?.Invoke(null, 0);
                return;
            }

            _arena.QueueBotPathRequest(this, startX, startY, endX, endY, (path, pathLength) =>
            {
                onPathReceived?.Invoke(path, pathLength);

                if (MovementMode == BotMovementMode.Api && path != null && pathLength > 0)
                {
                    Navigation.OnPathReceived(path, pathLength, pathLength > 1 ? 1 : 0);
                }
            }, priority);
        }

        #endregion
    }
}
