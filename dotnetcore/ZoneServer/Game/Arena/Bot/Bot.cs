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
    public partial class Bot : Vehicle, IEventObject
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

        /// <summary>
        /// Who last hurt this bot (a Player, a Bot or a turret Computer) and when (local change 2026-11-13) - lets
        /// scripts react like players: retaliate, or protect a teammate who is taking damage.
        /// </summary>
        public object LastHurtBy;
        public int LastHurtTick;
        public void NoteHurtBy(object who)
        {
            if (who == null || who == this)
                return;
            LastHurtBy = who;
            LastHurtTick = Environment.TickCount;
            if (LastHurtTick == 0) LastHurtTick = 1;
        }
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
        private int _stuckWatchTick;
        private short _stuckWatchX, _stuckWatchY;
        private int _stuckStrikes;
        private int _insidePhysicsTries;
        private int _droppedForDeathTick = int.MinValue;

        // ---- wall sense (local 2026-11-14): never face a wall and stay there
        private int _wallSenseTick, _wallSampleTick, _wallPressStrikes;
        private short _wallSampleX, _wallSampleY;
        private static int _wallSenseLogTick;
        private int _escapeStreak;
        private short _escapeX, _escapeY;

        /// <summary>Tick of this bot's last wall / corner escape (0 = never) - scripts drop the goal that walled it in.</summary>
        public int LastWallEscapeTick { get; private set; }

        /// <summary>How many of 8 directions are blocked within <paramref name="r"/> px (3+ = boxed into a corner).</summary>
        private int CountBlockedAround(int r)
        {
            int n = 0;
            for (int k = 0; k < 8; k++)
                if (BotWallAvoidance.ProbeOpen(this, _state.positionX, _state.positionY, k * 30.0, r) < r - 8)
                    n++;
            return n;
        }

        /// <summary>
        /// Every 0.4 s: a bot that wants to move, has a wall right ahead of where it's going, and has covered
        /// under 48 px in the last 1.2 s is "pressing a wall". After ~1 s of that it looks round 16 directions for
        /// the longest clear run (a doorway / open ground), prefers ones not straight back where it came from,
        /// drives through it for ~1 s, then re-paths from there. Applies to every bot, in or out of combat.
        /// </summary>
        private void WallSense(int now)
        {
            if (MovementMode != BotMovementMode.Api || _state == null || _movement == null || _arena == null || IsDead)
                return;
            if (_movement.IsMovementFrozen || now - _pinUntil < 0)
                return;
            if (_wallSenseTick != 0 && now - _wallSenseTick < 400)
                return;
            _wallSenseTick = now == 0 ? 1 : now;

            if (_wallSampleTick == 0 || now - _wallSampleTick >= 1200)
            {
                _wallSampleTick = now == 0 ? 1 : now;
                _wallSampleX = _state.positionX;
                _wallSampleY = _state.positionY;
            }

            SteeringController steering = _movement as SteeringController;
            bool wants = steering != null
                && (Math.Abs(steering.AnalogForward) > 0.25f || Math.Abs(steering.AnalogStrafe) > 0.25f
                    || steering.FollowPath != null || Navigation.HasPath);
            double travel = TravelYaw();
            if (double.IsNaN(travel))
            {
                // standing: the direction it is trying to go (body-relative drive), else where it faces
                if (steering != null && (Math.Abs(steering.AnalogForward) > 0.05f || Math.Abs(steering.AnalogStrafe) > 0.05f))
                    travel = BotWallAvoidance.WrapYaw(_state.yaw + Math.Atan2(steering.AnalogStrafe, steering.AnalogForward) * 120.0 / Math.PI);
                else
                    travel = _state.yaw;
            }
            int ahead = BotWallAvoidance.ProbeOpen(this, _state.positionX, _state.positionY, travel, 40);
            int mdx = _state.positionX - _wallSampleX, mdy = _state.positionY - _wallSampleY;
            bool slow = mdx * mdx + mdy * mdy < 48 * 48 && now - _wallSampleTick >= 800;
            // escaped far enough from the last escape spot: the streak is over
            int edx = _state.positionX - _escapeX, edy = _state.positionY - _escapeY;
            if (_escapeStreak > 0 && edx * edx + edy * edy > 200 * 200)
                _escapeStreak = 0;
            // Pressing a wall, OR wedged in a corner (wants to move, hardly moving - the wall may be off to the
            // side, not dead ahead): after ~1.2 s, scan for the way out.
            bool cornered = slow && CountBlockedAround(56) >= 3;
            if (!(wants && slow && (ahead < 28 || cornered)))
            {
                _wallPressStrikes = 0;
                return;
            }
            if (++_wallPressStrikes < 3)
                return;
            _wallPressStrikes = 0;

            // Scan all round (24 directions, out to 480 px) for the longest clear run - a doorway or open
            // ground - preferring roughly the way it wanted to go, never straight back into the same wall.
            double bestYaw = double.NaN;
            double bestScore = -1;
            for (int k = 0; k < 24; k++)
            {
                double yaw = BotWallAvoidance.WrapYaw(travel + k * 10.0);
                int free = BotWallAvoidance.ProbeOpen(this, _state.positionX, _state.positionY, yaw, 480);
                if (free < 64)
                    continue;
                double off = Math.Abs(Helpers.calculateDifferenceInAngles(travel, yaw));   // yaw units, 0..120
                double score = free * (1.0 - 0.25 * off / 120.0) + (HumanRng.NextDouble() * 24);
                if (off < 15)
                    score *= 0.5;   // that's the wall we're pressing
                if (score > bestScore) { bestScore = score; bestYaw = yaw; }
            }
            if (double.IsNaN(bestYaw))
                return;

            // Each repeat escape near the same spot commits longer, so it really leaves the corner instead of
            // being steered straight back in; the brain is told to drop that goal (LastWallEscapeTick).
            if (_escapeStreak == 0)
            {
                _escapeX = _state.positionX;
                _escapeY = _state.positionY;
            }
            _escapeStreak++;
            int hold = _escapeStreak <= 1 ? 900 + HumanRng.Next(400) : _escapeStreak == 2 ? 1700 : 2600;
            _pinYaw = bestYaw;
            _pinUntil = now + hold;
            LastWallEscapeTick = now == 0 ? 1 : now;
            Navigation.Clear();   // re-path from the new spot once through
            if (now - _wallSenseLogTick > 5000)
            {
                _wallSenseLogTick = now;
                Log.write(TLog.Normal, "[bot] {0} was pressing a wall - heading for the opening at yaw {1:F0}", _id, bestYaw);
            }
        }
        private int _tickHullRefresh;
        private readonly HashSet<int> _playersSeenDead = new HashSet<int>();

        /// <summary>
        /// No invisible bots (local 2026-11-14): a client can drop a bot's body (death graphic, respawn screen)
        /// while the server still believes it has it - the bot then shoots from nowhere. Every ~6 s re-send the
        /// hull (SC_Vehicles repair, no Leave/Enter) to players near the bot so it can never stay invisible.
        /// </summary>
        private void RefreshHullForNearbyClients(int now)
        {
            if (_arena == null || _state == null || IsDead)
                return;
            if (_tickHullRefresh == 0)
            {
                _tickHullRefresh = now + 3000 + (_id % 3000);   // stagger bots
                return;
            }
            if (now - _tickHullRefresh < 0)
                return;
            _tickHullRefresh = now + 500;
            // ONE re-send per player, at the moment they respawn (dead -> alive): that's when a client may have
            // dropped bot bodies, and the screen is changing anyway so nothing visibly jumps. No periodic
            // re-sends at all (those showed as bots "teleporting" - every 6 s, even while you were dead).
            List<Player> near = _arena.getPlayersAndSpecInRange(_state.positionX, _state.positionY, Arena.GetBotHighRateViewRange());
            if (near == null)
                return;
            bool any = false;
            foreach (Player p in near)
            {
                if (p == null)
                    continue;
                if (p.IsDead)
                {
                    _playersSeenDead.Add(p._id);
                    continue;
                }
                if (_playersSeenDead.Remove(p._id))
                {
                    ClearClientAwareOfBot(p);
                    any = true;
                }
            }
            if (any)
                RequestHullReattach(now + 3000);
        }

        /// <summary>Nearest spot (8 px steps out to <paramref name="maxR"/>) this bot can stand on with clear tiles around it.</summary>
        private Helpers.ObjectState? NearestFreeSpot(int maxR)
        {
            for (int r = 8; r <= maxR; r += 8)
            {
                for (int i = 0; i < 16; i++)
                {
                    double a = i * Math.PI / 8;
                    short x = (short)(_state.positionX + Math.Cos(a) * r);
                    short y = (short)(_state.positionY + Math.Sin(a) * r);
                    if (BotWallAvoidance.IsImpassable(this, x, y))
                        continue;
                    bool clear = true;
                    for (int k = 0; k < 8 && clear; k++)
                    {
                        double b = k * Math.PI / 4;
                        if (BotWallAvoidance.IsImpassable(this, (short)(x + Math.Cos(b) * 12), (short)(y + Math.Sin(b) * 12)))
                            clear = false;
                    }
                    if (clear)
                        return new Helpers.ObjectState { positionX = x, positionY = y };
                }
            }
            return null;
        }

        /// <summary>
        /// Last-resort stuck watchdog (local change 2026-11-13): a bot that wants to move but hasn't gone 20 px in
        /// ~2.4 s gets out - wedged inside / against physics it is nudged to the nearest open spot; otherwise it
        /// takes a fresh escape route. No bot stays stuck on a wall.
        /// </summary>
        private void StuckWatchdog(int now)
        {
            if (MovementMode != BotMovementMode.Api || _state == null || _movement == null || _arena == null)
                return;
            if (_stuckWatchTick != 0 && now - _stuckWatchTick < 600)
                return;

            // Standing ON physics it can't occupy (landed / knocked onto a green or yellow patch): walk off it
            // toward the nearest clear spot first; only if that fails twice, a tiny nudge (no visible "respawn").
            if (BotWallAvoidance.IsImpassable(this, _state.positionX, _state.positionY))
            {
                _stuckWatchTick = now == 0 ? 1 : now;
                Helpers.ObjectState free = NearestFreeSpot(96);
                if (free == null)
                    return;
                if (_insidePhysicsTries < 2)
                {
                    _insidePhysicsTries++;
                    double ddx = free.positionX - _state.positionX, ddy = free.positionY - _state.positionY;
                    _pinYaw = BotWallAvoidance.WrapYaw(Math.Atan2(ddx, -ddy) * 180.0 / Math.PI / 1.5);
                    _pinUntil = now + 500;
                    return;
                }
                _insidePhysicsTries = 0;
                Navigation.Clear();
                Log.write(TLog.Normal, "[bot] {0} (veh {1}) was on physics at ({2},{3}) - nudged to ({4},{5})",
                    _id, _type?.Id, _state.positionX, _state.positionY, free.positionX, free.positionY);
                _movement.warp(new Helpers.ObjectState { positionX = free.positionX, positionY = free.positionY, positionZ = _state.positionZ, yaw = _state.yaw });
                return;
            }
            _insidePhysicsTries = 0;
            if (_stuckWatchTick != 0 && now - _stuckWatchTick < 1200)
                return;

            SteeringController steering = _movement as SteeringController;
            bool wants = steering != null && !_movement.IsMovementFrozen
                && (Math.Abs(steering.AnalogForward) > 0.25f || Math.Abs(steering.AnalogStrafe) > 0.25f
                    || steering.FollowPath != null || Navigation.HasPath);
            int dx = _state.positionX - _stuckWatchX, dy = _state.positionY - _stuckWatchY;
            bool moved = _stuckWatchTick == 0 || dx * dx + dy * dy >= 20 * 20;
            _stuckWatchTick = now == 0 ? 1 : now;
            _stuckWatchX = _state.positionX;
            _stuckWatchY = _state.positionY;
            if (!wants || moved)
            {
                _stuckStrikes = 0;
                return;
            }
            if (++_stuckStrikes < 2)
                return;
            _stuckStrikes = 0;

            // Boxed in but not on physics: take a fresh route out (never teleport for this).
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
            _tickWallEscapeUntil = now + 900;
        }
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
        private const int MovingUpdateIntervalMs = 33;   // local 2026-11-13: 30 Hz (was 16 = 60 Hz) - smooth with client extrapolation, half the traffic
        /// <summary>~50 Hz while paused under observation (aim / fire stalls).</summary>
        private const int ObservedIdleUpdateIntervalMs = 50;   // local 2026-11-13 (was 20)
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
            if (ExtraArmorIgnore > 0 || ExtraArmorPercent > 0)
            {   // every damage channel, no immunity (local 2026-11-14: human-class bots as tough as the pirates)
                for (int c = 0; c < _utilityArmorIgnore.Length; c++)
                {
                    _utilityArmorIgnore[c] += ExtraArmorIgnore;
                    // channels 4-5 = bypass / energy (the pirates are immune there; human bots get a big cut)
                    int pct = c >= 4 && ExtraArmorPercentBypassEnergy > 0 ? ExtraArmorPercentBypassEnergy : ExtraArmorPercent;
                    _utilityArmorPercent[c] = Math.Min(1000, _utilityArmorPercent[c] + pct);
                }
            }
            _utilityMovementMods = BotUtilityEffects.ComputeMovementModifiers(Loadout.GetActiveUtilities());
            if (AgilityBonusPercent > 0 && _type != null && _type.TerrainSpeeds != null && _type.TerrainSpeeds.Length > 0)
            {
                SpeedValues t0 = _type.TerrainSpeeds[0];
                int p = AgilityBonusPercent;
                _utilityMovementMods.ThrustBonus += t0.RollThrust * p / 100;
                _utilityMovementMods.StrafeThrustBonus += t0.StrafeThrust * p / 100;
                _utilityMovementMods.ReverseThrustBonus += t0.BackwardThrust * p / 100;
                _utilityMovementMods.TopSpeedBonus += t0.RollTopSpeed * p / 100;
                _utilityMovementMods.RotateBonus += t0.RollRotate * p / 200;
            }
            _movement.ApplyUtilityMovementModifiers(_utilityMovementMods);
        }

        int _lastFiredItemId, _tickOtherWeaponReady;

        /// <summary>
        /// One trigger finger (local 2026-11-14): the same weapon follows its own cooldown; a DIFFERENT weapon has
        /// to wait the last weapon's fireDelayOther, exactly like a player switching guns.
        /// </summary>
        public bool CanFireWeaponNow(WeaponController w)
        {
            if (w == null)
                return false;
            if (_lastFiredItemId == 0 || w.ItemID == _lastFiredItemId)
                return true;
            return Environment.TickCount - _tickOtherWeaponReady >= 0;
        }

        public void NoteWeaponFired(WeaponController w)
        {
            if (w == null)
                return;
            _lastFiredItemId = w.ItemID;
            _tickOtherWeaponReady = Environment.TickCount + Math.Max(0, w.FireDelayOtherMs);
            LastShotTick = Environment.TickCount == 0 ? 1 : Environment.TickCount;
            LastFiredWeapon = w;
        }

        /// <summary>The weapon this bot fired last (keeps using it rather than paying a switch delay).</summary>
        public WeaponController LastFiredWeapon { get; private set; }

        /// <summary>Tick of this bot's last shot (0 = never) - scripts use it to spot bots doing nothing.</summary>
        public int LastShotTick { get; private set; }

        /// <summary>Script hook: item id this bot drops on death (0 = the vehicle's own DropItemId).</summary>
        public static Func<Bot, int> DeathDropResolver;

        int _extraArmorIgnore, _extraArmorPercent;
        /// <summary>Extra armour on every channel: damage ignored per hit (item damage units, 1000 = 1 HP).</summary>
        public int ExtraArmorIgnore
        {
            get { return _extraArmorIgnore; }
            set { _extraArmorIgnore = value; if (_movement != null && Loadout != null) RefreshUtilityEffects(); }
        }
        /// <summary>Extra armour on every channel: damage reduction in tenths of a percent (100 = 10 %).</summary>
        public int ExtraArmorPercent
        {
            get { return _extraArmorPercent; }
            set { _extraArmorPercent = value; if (_movement != null && Loadout != null) RefreshUtilityEffects(); }
        }

        int _extraArmorPercentBE;
        /// <summary>Damage reduction for bypass + energy damage (tenths of a percent; 0 = use ExtraArmorPercent).</summary>
        public int ExtraArmorPercentBypassEnergy
        {
            get { return _extraArmorPercentBE; }
            set { _extraArmorPercentBE = value; if (_movement != null && Loadout != null) RefreshUtilityEffects(); }
        }

        int _agilityBonusPercent;
        /// <summary>
        /// Extra agility, like a player's Deftness attribute (local 2026-11-14): percent bonus to thrust, strafe,
        /// reverse and top speed (half that to turning). Setting it re-applies movement immediately.
        /// </summary>
        public int AgilityBonusPercent
        {
            get { return _agilityBonusPercent; }
            set
            {
                if (_agilityBonusPercent == value)
                    return;
                _agilityBonusPercent = value;
                if (_movement != null && Loadout != null)
                    RefreshUtilityEffects();
            }
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
            // Natural idle (local 2026-11-14): a live bot never just stands there turning - it shuffles side to
            // side like a player waiting. Dead / condemned / frozen bots really stop.
            if (NaturalIdle && !IsDead && !bCondemned && MovementMode == BotMovementMode.Api
                && _movement != null && !_movement.IsMovementFrozen)
            {
                // Held still for more than 4 s: nobody just waits around - go roam to a point of interest the
                // script picks (a base, a flag, the ground between them; inside the open sectors). Local 2026-11-14.
                int now = Environment.TickCount;
                if (_holdSince == 0 || now - _lastHoldCall > 1500)
                    _holdSince = now;
                _lastHoldCall = now;
                if (IdleRoamTarget != null && now - _holdSince > 4000)
                {
                    if (_idleRoamDest == null || now - _idleRoamUntil > 0
                        || Helpers.distanceTo(_state, _idleRoamDest) < 60)
                    {
                        _idleRoamDest = null;
                        try { _idleRoamDest = IdleRoamTarget(this); } catch { }
                        _idleRoamUntil = now + 12000;
                    }
                    if (_idleRoamDest != null)
                    {
                        Helpers.ObjectState d = _idleRoamDest;
                        ApplyNavigationApproach(d, IdleRoamSteer != null ? IdleRoamSteer(this, d) : null, new BotNavigateOptions
                        {
                            RequeueMinMs = 1500,
                            PathStartIndex = 0,
                            Priority = PathPriority.Idle,
                        });
                        return;
                    }
                }
                SetDrive(0f, 0f, 0f);
                return;
            }
            _motionRequest = BotMotionRequest.HoldPosition;
        }

        int _holdSince, _lastHoldCall, _idleRoamUntil;
        Helpers.ObjectState? _idleRoamDest;

        /// <summary>Script hook: where an idle bot should roam to (null = keep the idle shuffle).</summary>
        public static Func<Bot, Helpers.ObjectState?>? IdleRoamTarget;
        /// <summary>Script hook: wall-aware steer toward a point while the path is pending.</summary>
        public static Func<Bot, Helpers.ObjectState, Func<InfantryVehicle, Vector3>?>? IdleRoamSteer;

        /// <summary>Live bots strafe instead of standing still (only weapon freezes really stop them).</summary>
        public static bool NaturalIdle = true;
        float _idleStrafeSign = 1f;
        int _idleFlipTick;
        float _idleForward;

        /// <summary>A small side-to-side shuffle with a hint of forward/back, re-rolled every 0.5-1.4 s.</summary>
        void ApplyIdleShuffle(ref float forward, ref float strafe)
        {
            int now = Environment.TickCount;
            if (_idleFlipTick == 0 || now - _idleFlipTick >= 0)
            {
                _humanRng ??= new Random(unchecked(_id * 31337 + now));
                _idleStrafeSign = -_idleStrafeSign;
                _idleForward = (float)(_humanRng.NextDouble() * 0.4 - 0.2);
                _idleFlipTick = now + 500 + _humanRng.Next(900);
            }
            strafe = 0.5f * _idleStrafeSign;
            forward = _idleForward;
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
            // never "stand still and only move the mouse": an idle or aim-only drive becomes a strafe shuffle
            if (NaturalIdle && !IsDead && !bCondemned && Math.Abs(forward) < 0.08f && Math.Abs(strafe) < 0.08f
                && _movement != null && !_movement.IsMovementFrozen)
                ApplyIdleShuffle(ref forward, ref strafe);
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

            // Scripts swap / create guns freely; every one picks up this bot's skill profile.
            WireWeaponOwners();

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
                // Drop our items ONCE per death (local fix 2026-11-13: this ran every poll while dead, spamming item
                // spawns to every client until the corpse was removed - with many kills it flooded and DC'd players).
                // (local 2026-11-14) also for bots that will respawn - every death drops its helmet, like a
                // player; a script can pick the item (DeathDropResolver: e.g. pirate helmets per class)
                if (_droppedForDeathTick != _tickDead)
                {
                    _droppedForDeathTick = _tickDead;
                    VehInfo vehicle = _arena._server._assets.getVehicleByID(_type.Id);
                    int dropId = vehicle != null ? vehicle.DropItemId : 0;
                    int qty = vehicle != null ? vehicle.DropItemQuantity : 0;
                    if (DeathDropResolver != null)
                    {
                        try
                        {
                            int over = DeathDropResolver(this);
                            if (over != 0) { dropId = over; qty = 1; }
                        }
                        catch { }
                    }
                    ItemInfo item = dropId != 0 ? _arena._server._assets.getItemByID(dropId) : null;
                    if (item != null)
                        _arena.itemSpawn(item, (ushort)Math.Max(1, qty), _state.positionX, _state.positionY, null);
                }

                //Should we remove ourself from the world?
                // (local 2026-11-14) at least 4.5 s on the ground so every client plays the death animation;
                // player classes have no remove timer at all (players respawn) - their bot corpses never went
                // away, so 6 s for those
                int corpseMs = _type.RemoveDeadTimer != 0 ? Math.Max(4500, _type.RemoveDeadTimer * 1000) : 6000;
                if (!bDropshipRespawnPending && _tickDead != 0 && tickCount - _tickDead > corpseMs)
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

                ApplyCombatSenseOverrides(tickCount);

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
                StuckWatchdog(tickCount);
                WallSense(tickCount);
                RefreshHullForNearbyClients(tickCount);
                if (MovementMode == BotMovementMode.Api)
                    EvaluatePinned(tickCount);

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
            // Only each human's nearest few bots get the full-rate stream; a crowd on screen cannot flood a client.
            if (observed && _arena != null && !_arena.IsBotFastRouted(_id, tickCount))
                interval = Math.Max(interval, BotFeatures.RouteSlowMs);

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
                    // Dead players still watch the screen (local fix 2026-11-13): skipping them dropped every
                    // bot to the 1.5 s radar rate while you were dead - everything jumped around.
                    if (p == null || p._state == null)
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
            if (attacker != null)
                NoteHurtBy(attacker);
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
