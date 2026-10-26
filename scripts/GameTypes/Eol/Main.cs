using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Scripting;
using InfServer.Bots;
using InfServer.Protocol;

using Assets;
using InfServer.Game.Modules;

namespace InfServer.Script.GameType_Eol
{	// Script Class
    /// Provides the interface between the script and arena
    ///////////////////////////////////////////////////////
    public partial class Script_Eol : Scripts.IScript
    {	///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        private Arena _arena;					//Pointer to our arena class
        private CfgInfo _config;				//The zone config
        private Points _points;                 //Our points
        private Teams _tm;                      //Teams logic
        private ZoneServer _server;

        // Live-ops static flags: each is "= true" so every feature starts enabled at zone load (mods may toggle at runtime).
        /// <summary>When false, KOTH auto-lobby, crown rounds, and ticker KOTH line are off; mod *startkoth still works.</summary>
        public static bool EolKothSystemEnabled = true;
        /// <summary>When false, alien waves and *startaliens are off; use *enablealiens to turn back on.</summary>
        public static bool EolAlienWavesEnabled = true;
        /// <summary>When false, pirate raid auto-arm and *startraiders are off; use *enableraiders to turn back on.</summary>
        public static bool EolPirateRaidsEnabled = true;
        /// <summary>When false, defense captains and BasicDefense grunts are not spawned; use *enablebasebots to turn back on.</summary>
        public static bool EolBaseBotsEnabled = true;
        /// <summary>When false, automatic bot HQ anchor placement (from sector templates) is skipped; use *enablebasesetup to turn back on.</summary>
        public static bool EolBaseSetupEnabled = true;

        static Script_Eol()
        {
            EolKothSystemEnabled = true;
            EolAlienWavesEnabled = true;
            EolPirateRaidsEnabled = true;
            EolBaseBotsEnabled = true;
            EolBaseSetupEnabled = true;
        }

        /// <summary>HQ chassis id — physical anchor for base bots (469). No leveling/bounty script layer.</summary>
        public int _hqVehId = 469;
        /// <summary>Per-player payout when the killer team destroys a defense bot HQ (469).</summary>
        public const int EolBotHqDestroyCashPerPlayer = 11250;
        public const int EolBotHqDestroyExpPerPlayer = 1875;
        public const int EolBotHqDestroyPointsPerPlayer = 750;
        /// <summary>Lifetime <see cref="InfServer.Game.Player.ExperienceTotal"/> below this gets a 1.5× cash/exp/points bonus.</summary>
        public const int EolRewardScaleHighCutoffExp = 75000;
        /// <summary>Lifetime exp at or above this is 1.0×; between the two cutoffs is 1.25×.</summary>
        public const int EolRewardScaleNormalCutoffExp = 100000;
        /// <summary>Global multiplier applied to periodic flag cash/exp/points after per-flag sum.</summary>
        public const int EolFlagRewardGlobalMult = 4;
        /// <summary>Happy hour windows in US Eastern (hour inclusive start, exclusive end). ×<see cref="EolHappyHourRewardMult"/>.</summary>
        static readonly int[] EolHappyHourStartHoursEt = { 9, 11, 18 };
        static readonly int[] EolHappyHourEndHoursEt = { 10, 13, 19 };
        public const double EolHappyHourRewardMult = 2.0;

        static TimeZoneInfo _eolUsEasternTz;
        bool _eolHappyHourActive;

        public int _roamCaptains;
        public int _roamChiefs;

        public EolBoundaries _eol;

        //public BotSettings BotSettings;

        private int _lastGameCheck;				//The tick at which we last checked for game viability


        //KOTH
        private Team _victoryKothTeam;			//The team currently winning!
        private int _tickGameLastTickerUpdate;  //The tick at which the ticker was last updated
        private long _tickKOTHGameStarting;
        private long _tickKOTHGameStart;				//The tick at which the game started (0 == stopped)
        private int _tickEolGameStart;
        private int _tickKothGameStart;
        private int _minPlayers;                //The minimum amount of players needed for a KOTH game
        /// <summary>How often open radiation sectors reshuffle during a live match.</summary>
        public const int SectorRotationIntervalMs = 6 * 60 * 60 * 1000;
        int _tickNextSectorRotation;
        private bool _kothGameRunning;
        private Dictionary<InfServer.Game.Player, Team> _startTeams;
        private int _lastKOTHGameCheck;
        private int _lastRoamAlert;
        /// <summary>After KOTH ends, lobby countdown waits until this tick (TickCount). 0 = no cooldown.</summary>
        int _tickNextKothEligible;
        const int KothBetweenRoundsCooldownMinMs = 4 * 60 * 1000;
        const int KothBetweenRoundsCooldownMaxMs = 12 * 60 * 1000;

        //EOL
        private int _tickGameStarting;			//The tick at which the game began starting (0 == not initiated)
        private int _tickGameStart;				//The tick at which the game started (0 == stopped)
        /// <summary>Match schedule (KOTH + flag) — short arena-wide line.</summary>
        const int EolMatchScheduleTickerIdx = 0;
        /// <summary>CTF lobby “next game” / flag “Victory in” — do not put crown timers here.</summary>
        const int CtfNextGameTickerIdx = 1;
        /// <summary>KOTH lobby / sector 90s warning when idle; per-player crown timers while KOTH is live (mutually exclusive).</summary>
        const int KothLobbyTickerIdx = 2;
        /// <summary>Same slot as <see cref="KothLobbyTickerIdx"/> — only used while <see cref="_kothGameRunning"/>.</summary>
        const int KothCrownTimerTickerIdx = KothLobbyTickerIdx;
        /// <summary>Personal K/D — per-player row 3 (arena sectors also use row 3 on change only).</summary>
        const int CtfPersonalScoreTickerIdx = 3;
        /// <summary>Raiders / aliens / base bots — client HUD row 2 (0–3 only; never use idx 5).</summary>
        const int EolActivityTickerIdx = 2;
        const int KothLobbyPollMs = 10 * 1000;
        int _tickLastKothLobbyPoll;
        /// <summary>True while <see cref="preNewSector"/> owns ticker (1,<see cref="KothLobbyTickerIdx"/>); blocks <see cref="PollKothLobby"/> from overwriting it.</summary>
        bool _sectorChangeCountdownActive;
        const int CtfNextGameCountdownMs = 15 * 1000;
        const int CtfLobbyStaleMs = 45000;

        static int CeilSecondsRemaining(int totalMs, int elapsedMs)
        {
            int rem = totalMs - elapsedMs;
            if (rem <= 0)
                return 0;
            return (rem + 999) / 1000;
        }

        //CTF
        private int _jackpot;					//The game's jackpot so far
        private bool _firstGame;
        private Team _victoryTeam;				//The team currently winning!
        private int _tickVictoryStart;			//The tick at which the victory countdown began
        private int _tickNextVictoryNotice;		//The tick at which we will next indicate imminent victory
        private int _victoryNotice;				//The number of victory notices we've done
        private int _lastFlagCheck;
        private bool _gameWon = false;
        private int _minPlayersCTF;
        private int _lastFlagReward;
        private bool _flagReward5MinNoticeDone;
        private bool _flagReward1MinNoticeDone;
        /// <summary>Successful 10-minute flag payouts since the last flag reset (reset after <see cref="FlagRewardsBeforeReset"/>).</summary>
        private int _flagRewardsSinceReset;
        private const int FlagRewardsBeforeReset = 3;

        //Flag Checks for roaming captains
        public List<Arena.FlagState> _flags;

        //Settings
        private int _pointSmallChange;                  //The small change to # of points (ex: kills, turret kills, etc)
        private int _pointPeriodicChange;               //The change to # of points based on periodic flag rewards
        public int _pylonLocation;
        public int _minX;
        public int _maxX;
        public int _minY;
        public int _maxY;
        public int _tickLastMinorPoll;

        //Scores
        private Dictionary<InfServer.Game.Player, int> _healingDone;   //Keep track of healing done by players

        //KillRewards
        public const int c_baseReward = 5;
        public const double c_pointMultiplier = 2;
        public const double c_cashMultiplier = 1;
        public const double c_expMultiplier = 0.5;
        public const int c_percentOfVictim = 50;
        public const int c_percentOfOwn = 3;
        public const int c_percentOfOwnIncrease = 5;

        private class PlayerCrownStatus
        {
            public bool crown;                  //Player has crown?
            public int crownKills;              //Crown kills without a crown
            public int crownDeaths;             //Times died with a crown (counted until they lose it)
            public int expireTime;              //When the crown will expire
            public PlayerCrownStatus(bool bCrown)
            {
                crown = bCrown;
            }
            public PlayerCrownStatus()
            {
                crown = true;
            }
        }

        private Dictionary<InfServer.Game.Player, PlayerCrownStatus> _playerCrownStatus;

        public List<InfServer.Game.Player> _activeCrowns //List of people with a crown
        {
            get
            {
                List<InfServer.Game.Player> list = new List<InfServer.Game.Player>();
                foreach (KeyValuePair<InfServer.Game.Player, PlayerCrownStatus> kv in _playerCrownStatus)
                {
                    if (kv.Value.crown)
                        list.Add(kv.Key);
                }
                return list;
            }
        }

        private List<InfServer.Game.Player> _noCrowns //List of people with no crowns
        {
            get
            {
                List<InfServer.Game.Player> list = new List<InfServer.Game.Player>();
                foreach (KeyValuePair<InfServer.Game.Player, PlayerCrownStatus> kv in _playerCrownStatus)
                {
                    if (!kv.Value.crown)
                        list.Add(kv.Key);
                }
                return list;
            }
        }

        private List<Team> _crownTeams;

        /// Stores our player streak information
        private class PlayerStreak
        {
            public ItemInfo.Projectile lastUsedWeap { get; set; }
            public int lastUsedWepKillCount { get; set; }
            public long lastUsedWepTick { get; set; }
            public int lastKillerCount { get; set; }
        }


        private Dictionary<string, PlayerStreak> killStreaks;
        private InfServer.Game.Player lastKiller;
        /// <summary>Set in <see cref="playerDeath"/> when killer is null so <see cref="playerBotKill"/> can read death coords for taunts.</summary>
        private CS_VehicleDeath _pendingBotDeathUpdate;
        public bool _bbetweengames;
        public bool Min1Timer;
        public bool Min5Timer;
        public bool Min1Send;
        public bool Min5Send;

        public string _currentSector1;
        public string _currentSector2;

        //Bots
        private int _lastBotCheck;
        private int[] _coolArray;
        int _flagchecking = 82000;

        public int _botlocX;
        public int _botlocY;

        public class pylonObject
        {
            short x;        // HQ anchor X (template coord)
            short y;        // HQ anchor Y (template coord)
            /// <summary>True = this slot may still receive a bot HQ; false = already built or consumed.</summary>
            bool exists;

            public pylonObject(short xPos, short yPos)
            {
                exists = true;
                x = xPos;
                y = yPos;
            }
            public short getX()
            { return x; }
            public short getY()
            { return y; }
            public bool bExists()
            { return exists; }
            public void setExists(bool bExists)
            { exists = bExists; }
        }

        public Dictionary<int, pylonObject> _usedpylons;
        string _botHqAnchorSectorKey;
        public Dictionary<int, pylonObject> _pylons;
        public Dictionary<int, pylonObject> _pylonsA;
        public Dictionary<int, pylonObject> _pylonsB;
        public Dictionary<int, pylonObject> _pylonsC;
        public Dictionary<int, pylonObject> _pylonsD;
        public Dictionary<int, pylonObject> _pylonsAB;
        public Dictionary<int, pylonObject> _pylonsCD;
        public Dictionary<int, pylonObject> _pylonsAC;
        public Dictionary<int, pylonObject> _pylonsBD;

        //Bots
        //Perimeter defense Bots
        public const float c_defenseInitialAmountPP = 2.5f;		//The amount of defense bots per player initially spawned (minimum of 1)
        public const int c_defenseAddTimerGrowth = 18;			//The amount of seconds to add to the new bot timer for each person missing from the team
        public const int c_defenseAddTimer = 36;			    //The amount of seconds between allowing new defense bots
        public const int c_defenseRespawnTimeGrowth = 400;		//The amount of time to add to the respawn timer for each missing player
        public const int c_defenseRespawnTime = 600;		    //The amount of ms between spawning new zombies
        public const int c_defenseMinRespawnDist = 900;			//The minimum distance bot can be spawned from the players
        public const int c_defenseMaxRespawnDist = 1500;		//The maximum distance bot can be spawned from the players
        public const int c_defenseMaxPath = 350;				//The maximum path length before a bot will request a respawn
        public const int c_defensePathUpdateInterval = 2000;	//The amount of ticks before a bot will renew it's path
        public const int c_defenseRoamPathUpdateInterval = 2000;
        public const int c_chiefPathUpdateInterval = 2000;
        public const int c_defenseDistanceLeeway = 500;			//The maximum distance leeway a bot can be from the team before it is respawned
        public const int _checkCaptain = 100000;                 //The tick at which we log captain/HQ status (spawn uses TrySpawnDefenseCaptainIfNeeded)
        public const int DefenseCaptainRespawnDelayMs = 2 * 60 * 1000;   // After a defense Captain dies, wait before deploying another
        public const int DefenseCaptainSpawnAttemptIntervalMs = 4000;   // Throttle warp lookups while waiting for HQ / respawn window
        public const int _checkEngineer = 75000;                //The tick at which we check for an engineer 70000
        public const int _checkRoamingCaptain = 20000;          //The tick at which we check for a roaming captain  140000
        public const int _checkRoamingChief = 100000;          //The tick at which we check for a roaming captain  100000
        protected int _tickLastEngineer = 0;                    //Last time we checked for an engineer
        protected int _tickLastCaptain = 0;                     //Last time we checked for a captain
        int _tickDefenseCaptainRespawnEligible;                 // 0 = may spawn (or no death cooldown); else TickCount must reach this after a Captain death
        int _tickLastDefenseCaptainSpawnAttempt;              // Throttle TrySpawnDefenseCaptainIfNeeded (0 = allow first try immediately)
        public int _tickLastChief = 0;                       //Last time we checked for an alien chief
        protected int _tickLastRoamingCaptain = 0;              //Last time we checked for a roaming captain
        protected int _lastPylonCheck = 0;                      // Last time we refreshed bot HQ slot templates (legacy name).
        /// <summary>Last destroyed bot HQ position — prefer a different anchor on redeploy when possible.</summary>
        short _lastDestroyedBotHqX;
        short _lastDestroyedBotHqY;
        bool _hasLastDestroyedBotHq;

        public const int c_CaptainPathUpdateInterval = 7500;	//The amount of ticks before an engineer's combat bot updates it's path
        public const int c_CaptainRoamPathUpdateInterval = 5000;	//The amount of ticks before a roaming bot looks to it's captain and updates it's path

        public Dictionary<Team, int> botCount;
        public Dictionary<Team, int> captainBots;
        public Dictionary<Team, int> roamBots;
        public Dictionary<Team, int> capRoamBots;
        public List<Team> roamingCaptianBots;
        public List<Team> engineerBots;
        /// <summary>Computers/walls placed with a defense bot HQ — wiped and cleared when that base dies.</summary>
        Dictionary<Team, List<Vehicle>> _defenseBaseVehicles;
        public Dictionary<Team, int> alienBots;
        public Dictionary<Team, int> alienChiefBots;
        public List<Team> roamingAlienBots;

        public List<Bots.Bot> _bots;
        public List<Bots.Bot> _condemnedBots;

        /// <summary>Remember which bot last damaged <paramref name="victim"/> for killfeed naming.</summary>
        public void NoteBotDamagedByBot(Bots.Bot victim, Bots.Bot shooter)
        {
            _arena?.NoteBotDamagedByBot(victim, shooter);
        }

        /// <summary>
        /// Release name registry, combat scratch, and damager bookkeeping for a bot.
        /// Does not remove from <see cref="_bots"/> — callers clear or remove that list themselves.
        /// Safe to call more than once (name release is idempotent).
        /// </summary>
        void ForgetScriptBotBookkeeping(Bots.Bot bot)
        {
            if (bot == null)
                return;

            _arena?.ForgetBotDamagerBookkeeping(bot);

            EolBotCombatUtil.ForgetBot(bot._id);
            EolBotSenseUtil.ForgetBot(bot._id);
            ClearBotActivePlayerTarget(bot);
            ClearCombatCallsForBot(bot);
        }

        /// <summary>
        /// Queue every bot tracked in <see cref="_bots"/> for paced destroy and clear tracking lists.
        /// Used on sector rotation / game end / game reset so historical bots do not accumulate.
        /// </summary>
        void DestroyAllTrackedScriptBots()
        {
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            if (_condemnedBots == null)
                _condemnedBots = new List<Bots.Bot>();

            // Snapshot then clear tracking — EnqueueVehicleDespawn removes from _bots and bookkeeping.
            List<Bots.Bot> snapshot = new List<Bots.Bot>(_bots);
            _bots.Clear();
            _condemnedBots.Clear();
            ClearBotDropshipRespawnState();

            for (int i = 0; i < snapshot.Count; i++)
                EnqueueVehicleDespawn(snapshot[i]);

            // Drop pending path solves / arena callbacks so mass despawn cannot leave a backlog.
            try { _arena?.ClearBotPathQueue(); }
            catch { /* ignore */ }
        }

        public int _maxEngineers;                               //Maximum amount of engineer bots that will spawn in game
        public int _maxRoamCaptains;                            //Maximum amount of roaming captain bots that will spawn in game
        public int _maxRoamingBots;
        public int _maxRoamChief;                            //Maximum amount of roaming chief alien bots that will spawn in game
        public int _maxRoamingAliens;
        public int _maxDefenseBots;
        public int _maxDefPerTeam;
        public int _maxRoamPerTeam;
        public int _maxRoamAliensPerTeam;
        public int _currentEngineers = 0;                       //Current amount of engineer bots playing in the game
        public int _currentRoamCaptains = 0;
        public int _currentRoamChief = 0;
        public int[] _lastPylon;                                //Array of all pylons that are being used

        private int _lastPvpRoamHourAlert;

        Dictionary<int, string> startTime;
        Dictionary<int, string> endTime;

        List<Vehicle> _activehPoints;

        //Bot teams
        public Team botTeam1;
        public Team botTeam2;
        public Team botTeam3;
        public Team botTeam4;
        public Team botTeam5;
        public Team botTeam6;
        public Team botTeam7;
        public Team botTeam8;
        public Team botTeam9;
        /// <summary>Owns static radiation-border wall vehicles (not a player faction).</summary>
        Team _eolBorderWallTeam;
        List<Vehicle> _eolBorderWallVehicles;

        /// <summary>Physical radius of both border hide vehicles (.veh); used to inset wall centers from the sector AABB corners.</summary>
        public const int EolBorderWallHideRadius = 20;
        /// <summary>Center-to-center step on top/bottom runs — tighter than 2×<see cref="EolBorderWallHideRadius"/> so segments overlap and close gaps players could walk through.</summary>
        public const int EolBorderWallSpacingHorizontal = 20;
        /// <summary>Center-to-center step on left/right runs.</summary>
        public const int EolBorderWallSpacingVertical = 20;
        /// <summary>.veh ID for top/bottom runs (horizontal-oriented hide, 471).</summary>
        public const int EolBorderWallVehicleHorizontal = 471;
        /// <summary>.veh ID for left/right runs (vertical-oriented hide, 470).</summary>
        public const int EolBorderWallVehicleVertical = 470;
        public const short EolBorderWallYawHorizontalTop = 0;
        public const short EolBorderWallYawHorizontalBottom = 0;
        public const short EolBorderWallYawVerticalLeft = 0;
        public const short EolBorderWallYawVerticalRight = 0;

        public string botTeamName1 = "Bot - Base";
        public string botTeamName2 = "Bot - Base 2";
        public string botTeamName3 = "Bot - Base 3";
        public string botTeamName4 = "Bot - Raiders";
        public string botTeamName5 = "Bot - BHB";
        public string botTeamName6 = null; // unused — reserved
        public string botTeamName7 = "Bot - Aliens";
        public string botTeamName8 = null; // unused — reserved
        public string botTeamName9 = null; // unused — reserved

        /// <summary>
        /// Creates a public bot faction, assigns a unique frequency id, and registers it
        /// immediately so the next team gets a different <see cref="Team._id"/>.
        /// </summary>
        Team CreateAndRegisterBotTeam(string name)
        {
            Team team = new Team(_arena, _arena._server);
            team._name = name;
            // Must assign id from current Teams.Count then createTeam before the next faction —
            // otherwise every bot team shares one freq and SC_PlayerEnter collapses them client-side.
            team._id = (short)_arena.Teams.Count();
            team._password = "jojotheClown";
            team._owner = null;
            // Public so the team appears in the client team list when botnames is on.
            // Player.JoinTeam / ?team block humans from joining or creating "Bot*" names.
            team._isPrivate = false;
            _arena.createTeam(team);
            return team;
        }

        Random _rand;

        public Dictionary<string, Helpers.ObjectState> _lastSpawn;

        // Define the time spans using TimeSpanRange objects
        TimeSpanRange[] timeSpans = new TimeSpanRange[]
        {
            new TimeSpanRange(1, 2),
            new TimeSpanRange(4, 5),
            new TimeSpanRange(7, 8),
            new TimeSpanRange(10, 11),
            new TimeSpanRange(13, 14),
            new TimeSpanRange(16, 17),
            new TimeSpanRange(19, 20),
            new TimeSpanRange(22, 23) // Special case for span crossing midnight
        };

        bool isRoamActive = false;
        int nextSpanStart = -1;
        int currentHour;

        /// <summary>Raid wander / engage anchor for roaming pirate AI (set when a raid wave starts or relocates).</summary>
        public bool RaiderPdbAnchorValid;
        public short RaiderPdbAnchorX;
        public short RaiderPdbAnchorY;
        /// <summary>Peaceful pirate raid roam objective — captain picks; grunts path here and wander locally.</summary>
        public bool RaiderCollectiveRoamValid;
        public short RaiderCollectiveRoamX;
        public short RaiderCollectiveRoamY;
        /// <summary>Bumped when the horde objective changes — bots clear stale paths.</summary>
        public int RaiderCollectiveRoamRevision;
        /// <summary>While <c>now</c> is below this tick, roam bots march to the collective flag instead of fighting at the last one.</summary>
        int _tickRaiderObjectiveMarchUntil;
        int _tickRaiderHordeAtObjective;
        short _raiderHordeAtObjectiveX;
        short _raiderHordeAtObjectiveY;
        /// <summary>Last player-held flag the captain routed the horde toward (avoids immediate re-pick).</summary>
        int RaiderCaptainCircuitLastFlagX = int.MinValue;
        int RaiderCaptainCircuitLastFlagY = int.MinValue;
        /// <summary>Grunt local wander radius (raw units) around <see cref="RaiderCollectiveRoamX"/>.</summary>
        public const int RaiderCollectiveRoamLocalRadius = 380;
        /// <summary>Captain may pick a new collective roam point after this long at the current one (peaceful only).</summary>
        public const int RaiderCollectiveRoamCaptainRefreshMs = 14000;
        /// <summary>Defenders / aliens begin closing on hostiles within this raw range — aligned with bot visibility / route band.</summary>
        public const int EolBotCombatSeekRangeRaw = 6000;
        /// <summary>All Eol bots engage hostiles within this raw range of themselves (seek / assist / sticky combat).</summary>
        public const int EolBotSelfAggroRange = EolBotCombatSeekRangeRaw;
        /// <summary>Close alert — High-priority attack when a hostile is this near (raw).</summary>
        public const int EolBotCloseAlertRangeRaw = 3200;
        /// <summary>Extended weapon-engage band for action scoring (raw units) — roam / alien / captain fire priority.</summary>
        public const int EolBotCombatFireRangeRaw = 1100;
        /// <summary>Defense grunt / captain high-priority fire band.</summary>
        public const int EolBotCombatFireRangeDefenseRaw = 1600;
        /// <summary>Legacy PDB-radius seek; raiders now fight hostiles within <see cref="RaiderSelfAggroRange"/> of themselves.</summary>
        public const int RaiderEngageRadiusFromPdb = 1500;
        /// <summary>Raiders fight players/bots within this raw range of themselves (aligned with seek range).</summary>
        public const int RaiderSelfAggroRange = EolBotSelfAggroRange;
        /// <summary>Soft leash radius (raw units) — steering nudges back toward <see cref="RaiderPdbAnchorX"/>.</summary>
        public const int RaiderPdbLeashSoft = 2000;
        /// <summary>Hard leash (raw units) — stronger pull toward PDB anchor (wide enough for sector waypoint legs).</summary>
        public const int RaiderPdbLeashHard = 2800;
        /// <summary>Pirate raid grunts: each wander leg must be farther than this (raw units) from the bot’s current position.</summary>
        public const int RaiderRoamWaypointMinSpacing = 801;
        /// <summary>Pirate raid grunts: waypoint reached within this raw distance → pick the next leg.</summary>
        public const int RaiderRoamWaypointArrivalRaw = 120;
        /// <summary>Tight arrival for claiming / captain on the flag pin.</summary>
        public const int RaiderCaptainObjectiveArrivalRaw = 100;
        /// <summary>Legacy alias — prefer <see cref="RaiderHordeGroupArrivalRaw"/> for pack checks.</summary>
        public const int RaiderHordeObjectiveArrivalRaw = RaiderCaptainObjectiveArrivalRaw;
        /// <summary>How close a raider must be to count as “with the pack” at the current flag (raw units).</summary>
        public const int RaiderHordeGroupArrivalRaw = 480;
        /// <summary>Unused when 0 — advance needs any 1 live raider at the flag (no pack majority stall).</summary>
        public const int RaiderMinRaidersNearObjectiveToAdvance = 0;
        /// <summary>Pause at the flag after the pack is together, before the next flag.</summary>
        public const int RaiderObjectiveArrivalDwellMs = 3500;
        /// <summary>Prefer the next flag at least this far from the current one when the sector is fully owned.</summary>
        public const int RaiderFlagRepickMinSeparationRaw = 300;
        /// <summary>Minimum center-to-center gap between bots (raw / pixel units).</summary>
        public const int BotPeerMinGapRaw = 80;
        /// <summary>SteerForSeparation radius in Abstract units (raw÷100). Keeps packs from stacking.</summary>
        public const float BotPackSeparationWorld = 0.55f;
        /// <summary>Extra weight on separation so bots peel apart when overlapping.</summary>
        public const float BotPackSeparationWeight = 7.5f;
        /// <summary>Minimum center-to-center gap between raider bots (raw / pixel units).</summary>
        public const int RaiderBotPeerMinGapRaw = BotPeerMinGapRaw;
        /// <summary>SteerForSeparation radius in Abstract units (raw÷100). Slightly above min gap so hulls do not stack.</summary>
        public const float RaiderPackSeparationWorld = BotPackSeparationWorld;
        /// <summary>Spawn at least this far from the chosen flag so the horde has to run to it.</summary>
        public const int RaiderSpawnMinDistFromFlag = 500;
        /// <summary>Spawn no farther than this from the chosen flag (same open sector).</summary>
        public const int RaiderSpawnMaxDistFromFlag = 1800;
        /// <summary>After advancing, march toward the new flag until arrival (or this max ms).</summary>
        public const int RaiderObjectiveMarchHoldMs = 20000;
        /// <summary>Grunts path toward the captain while farther than this (raw units).</summary>
        public const int RaiderGruntFollowCaptainRaw = 500;
        /// <summary>If a grunt is farther than this from the captain, warp them to the captain.</summary>
        public const int RaiderGruntSummonBehindCaptainRaw = 500;
        /// <summary>Min ms between summon warps for one grunt.</summary>
        public const int RaiderGruntSummonCooldownMs = 2000;
        /// <summary>Grunts spread out if closer than this to the captain (raw units).</summary>
        public const int RaiderGruntFollowMinSpacingRaw = 220;
        /// <summary>Base trail distance behind captain along the route to the current flag.</summary>
        public const int RaiderGruntFormationLagBaseRaw = 180;
        public const int RaiderGruntFormationLagStepRaw = 52;
        /// <summary>Lateral offset per formation slot (raw units).</summary>
        public const int RaiderGruntFormationLateralStepRaw = 88;
        /// <summary>Roaming captain/attacker flock peers (self + this many neighbors).</summary>
        public const int RaiderPackPeerMax = 6;
        int _tickLastRaiderHordePlaceAttempt;
        const int RaiderHordePlaceRetryMs = 2500;

        /// <summary>Delay between pirate raid grunt spawns (separate from <see cref="AlienSpawnIntervalMinMs"/>).</summary>
        public const int PirateRaidGruntSpawnIntervalMinMs = 3000;
        public const int PirateRaidGruntSpawnIntervalMaxMs = 7000;
        /// <summary>Roaming grunts (RoamingAttacker) spawned this pirate raid; total cap <see cref="PirateRaidTotalSpawns"/>.</summary>
        public const int PirateRaidTotalSpawns = 60;
        /// <summary>Never more than this many pirate combat bots (captain + grunts) alive at once during a raid wave.</summary>
        public const int PirateRaidMaxConcurrentAlive = 20;

        /// <summary>Max concurrent defense combat bots per team (Captain + Engineer + BasicDefense).</summary>
        public const int EolMaxConcurrentBaseDefenseBots = 20;
        /// <summary>Three reinforcement waves of up to <see cref="PirateRaidMaxConcurrentAlive"/> concurrent (same cadence idea as alien wave).</summary>
        public const int PirateRaidSpawnBatchCount = 3;
        /// <summary>After all <see cref="PirateRaidTotalSpawns"/> grunts have been spawned and eliminated, raid cleanup runs.</summary>
        public const int PirateRaidCooldownMinMs = 20 * 60 * 1000;
        public const int PirateRaidCooldownMaxMs = 45 * 60 * 1000;
        bool _pirateRaidWaveActive;
        public bool IsPirateRaidWaveActive { get { return _pirateRaidWaveActive; } }
        /// <summary>Grunts and pirate captain stay on-map while a raid wave is armed and the raid anchor is valid (no PDB/468 required).</summary>
        public bool PirateRaidHordeOperational { get { return _pirateRaidWaveActive && RaiderPdbAnchorValid; } }
        /// <summary>Raid wander objective: anchor tile, or legacy 468 vehicle if still present.</summary>
        public bool TryGetPirateRaidHordeReferencePoint(out short x, out short y)
        {
            if (RaiderPdbAnchorValid)
            {
                x = RaiderPdbAnchorX;
                y = RaiderPdbAnchorY;
                return true;
            }
            if (_pollCacheFirst468 != null)
            {
                x = _pollCacheFirst468._state.positionX;
                y = _pollCacheFirst468._state.positionY;
                return true;
            }
            x = 0;
            y = 0;
            return false;
        }
        int _pirateRaidGruntsSpawnedThisWave;
        /// <summary>Raid grunt quota left on the HUD (starts at <see cref="PirateRaidTotalSpawns"/>; −1 per permanent grunt death).</summary>
        int _pirateRaidGruntsRemainingThisWave;
        bool _pirateRaidQuotaMet;
        int _tickNextPirateGruntSpawn;
        int _tickPirateRaidInterBatchGateUntil;
        /// <summary>Tick when automatic pirate raid may spawn; 0 = between matches (blocked). Match start arms first raid delay (same range as post-wave cooldown).</summary>
        int _tickNextPirateRaidAllowed;

        /// <summary>Single-pass vehicle scan per script <see cref="poll"/> — ticker + bots avoid repeated LINQ over <see cref="Arena.Vehicles"/>.</summary>
        public bool _pollCacheAny468;
        public int _pollCache468Count;
        public Vehicle _pollCacheFirst468;
        public Vehicle _pollCacheRoamCaptainVehicle;
        public Vehicle _pollCacheAlienCrashVehicle;
        int _tickNextEolScheduleTickerBuild;
        string _cachedEolMatchTickerLine;
        string _cachedEolActivityTickerLine;
        const int EolScheduleTickerCacheMs = 2000;
        const int EolScheduleTickerCoarseCacheMs = 60000;
        int _tickLastHudTickerUpdate;
        const int HudTickerUpdateMs = 2500;
        string _lastHudMatchTickerSent;
        string _lastHudActivityTickerSent;
        /// <summary>
        /// Match/activity HUD rows use timer=0 (no client countdown clock).
        /// <see cref="updateTickers"/> re-pushes often enough that the line stays visible.
        /// </summary>
        const int EolStaticTickerDuration = 0;
        readonly Dictionary<InfServer.Game.Player, int> _lastKothCrownTickerSec = new Dictionary<InfServer.Game.Player, int>();
        readonly Dictionary<InfServer.Game.Player, string> _lastCtfPersonalScoreTicker = new Dictionary<InfServer.Game.Player, string>();
        int _tickLastCtfTickerUpdate;
        const int CtfTickerUpdateMs = 10000;
        /// <summary>Aligns with <see cref="Arena.gameCheckInterval"/> — full vehicle scan is not needed every tick.</summary>
        int _tickLastRefreshPollVehicleCache;
        /// <summary>Last HUD second shown for CTF lobby countdown; avoids setTicker spam every tick.</summary>
        int _lastCtfLobbyCountdownSecondShown = -1;
        /// <summary>Throttle <see cref="EolBoundaries.Poll"/> / roam side of <see cref="PollEolWhileRunning"/> to game-check cadence.</summary>
        int _tickLastEolWhileRunningPoll;

        void RefreshPollVehicleCache(int now, bool force = false)
        {
            if (!force && _tickLastRefreshPollVehicleCache != 0 && now - _tickLastRefreshPollVehicleCache < Arena.gameCheckInterval)
                return;
            _tickLastRefreshPollVehicleCache = now;

            _pollCacheAny468 = false;
            _pollCache468Count = 0;
            _pollCacheFirst468 = null;
            _pollCacheRoamCaptainVehicle = null;
            _pollCacheAlienCrashVehicle = null;
            if (_arena == null)
                return;
            int capId = _roamCaptains;
            int crashId = AlienCrashVehicleId;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v._type == null)
                    continue;
                int id = v._type.Id;
                if (id == 468)
                {
                    _pollCache468Count++;
                    _pollCacheAny468 = true;
                    if (_pollCacheFirst468 == null)
                        _pollCacheFirst468 = v;
                }
                else if (_pollCacheRoamCaptainVehicle == null && id == capId && v._team == botTeam4)
                    _pollCacheRoamCaptainVehicle = v;
                else if (_pollCacheAlienCrashVehicle == null && id == crashId && botTeam7 != null && v._team == botTeam7)
                    _pollCacheAlienCrashVehicle = v;
            }
        }

        ///////////////////////////////////////////////////
        // Member Functions
        ///////////////////////////////////////////////////
        /// <summary>
        /// Performs script initialization
        /// </summary>
        public bool init(IEventObject invoker)
        {	//Populate our variables
            currentHour = DateTime.Now.Hour;
            _arena = invoker as Arena;
            _config = _arena._server._zoneConfig;
            _arena.setTicker(0, 2, 0, "");
            if (_arena.BotActionModule != null)
                _arena.BotActionModule.SetObjectives(new EolObjectives(_arena, this));
            // Cross-faction bot combat: Base / Raiders / Aliens fight; same-faction teams do not.
            _arena.BotVsBotHostilityFilter = (a, b) =>
                a != null && b != null && ShouldEolBotTeamsFight(a._team, b._team);
            _lastKOTHGameCheck = Environment.TickCount;
            _tickGameStarting = 0;
            _tickGameStart = 0;
            _minPlayers = Int32.MaxValue;
            _minPlayersCTF = Int32.MaxValue;
            _activehPoints = new List<Vehicle>();
            //Load up our gametype handlers
            _eol = new EolBoundaries(_arena, this);
            _tm = new Teams(_arena, this);
            _tickNextSectorRotation = 0;
            _kothGameRunning = false;
            Min1Timer = false;
            Min5Timer = false;
            Min1Send = false;
            Min5Send = false;
            _flagReward5MinNoticeDone = false;
            _flagReward1MinNoticeDone = false;
            _flagRewardsSinceReset = 0;

            _playerCrownStatus = new Dictionary<InfServer.Game.Player, PlayerCrownStatus>();
            _crownTeams = new List<Team>();
            _startTeams = new Dictionary<InfServer.Game.Player, Team>();

            startTime = new Dictionary<int, string>();
            endTime = new Dictionary<int, string>();

            foreach (Arena.FlagState fs in _arena._flags.Values)
            {	//Determine the minimum number of players
                if (fs.flag.FlagData.MinPlayerCount < _minPlayersCTF)
                    _minPlayersCTF = fs.flag.FlagData.MinPlayerCount;

                //Register our flag change events
                fs.TeamChange += onFlagChange;
            }
            killStreaks = new Dictionary<string, PlayerStreak>();

            //Handle bots
            _roamCaptains = 142;
            _roamChiefs = 200;
            captainBots = new Dictionary<Team, int>();  //Keeps track of captain bots
            botCount = new Dictionary<Team, int>(); // Defense combat bots per team (Captain + Engineer + BasicDefense)
            roamBots = new Dictionary<Team, int>();
            capRoamBots = new Dictionary<Team, int>();
            engineerBots = new List<Team>();
            _defenseBaseVehicles = new Dictionary<Team, List<Vehicle>>();
            roamingCaptianBots = new List<Team>();
            alienBots = new Dictionary<Team, int>();
            alienChiefBots = new Dictionary<Team, int>();
            roamingAlienBots = new List<Team>();
            _currentRoamChief = 0;
            _currentEngineers = 0;                      //The number of engineers currently alive
            _currentRoamCaptains = 0;                   //The number of roaming captains currently alive
            _maxDefPerTeam = EolMaxConcurrentBaseDefenseBots;
            _maxRoamPerTeam = PirateRaidMaxConcurrentAlive;
            _maxRoamAliensPerTeam = 10;
            if (_bots == null)
                _bots = new List<Bots.Bot>();

            _condemnedBots = new List<Bots.Bot>();

            // Assign a unique frequency id then register immediately — batching all _id =
            // Teams.Count() before createTeam gave every bot faction the same client freq.
            botTeam1 = CreateAndRegisterBotTeam(botTeamName1);
            botTeam2 = CreateAndRegisterBotTeam(botTeamName2);
            botTeam3 = CreateAndRegisterBotTeam(botTeamName3);
            botTeam4 = CreateAndRegisterBotTeam(botTeamName4);
            botTeam5 = CreateAndRegisterBotTeam(botTeamName5);
            botTeam6 = null;
            botTeam7 = CreateAndRegisterBotTeam(botTeamName7);
            botTeam8 = null;
            botTeam9 = null;

            _eolBorderWallTeam = new Team(_arena, _arena._server);
            _eolBorderWallTeam._name = "Eol Radiation Border";
            _eolBorderWallTeam._id = (short)_arena.Teams.Count();
            _eolBorderWallTeam._password = "eolborder";
            _eolBorderWallTeam._owner = null;
            _eolBorderWallTeam._isPrivate = true;
            _arena.createTeam(_eolBorderWallTeam);
            _eolBorderWallVehicles = new List<Vehicle>();

            _pylons = new Dictionary<int, pylonObject>();
            _pylons.Add(0, new pylonObject(512, 480)); // Sector A
            _pylons.Add(1, new pylonObject(2736, 5600)); // Sector A
            _pylons.Add(2, new pylonObject(4901, 4740)); // Sector A
            _pylons.Add(3, new pylonObject(7445, 2356)); // Sector A
            _pylons.Add(4, new pylonObject(5504, 7040)); // Sector B
            _pylons.Add(5, new pylonObject(8304, 11008));// Sector B
            _pylons.Add(6, new pylonObject(6784, 13808));// Sector B
            _pylons.Add(7, new pylonObject(4117, 9956));// Sector B
            _pylons.Add(8, new pylonObject(13765, 1236)); // Sector C
            _pylons.Add(9, new pylonObject(17093, 5076)); // Sector C
            _pylons.Add(10, new pylonObject(12565, 5252)); // Sector C
            _pylons.Add(11, new pylonObject(18117, 3316)); // Sector C
            _pylons.Add(12, new pylonObject(20661, 7924)); // Sector D
            _pylons.Add(13, new pylonObject(16981, 10580)); // Sector D
            _pylons.Add(14, new pylonObject(18064, 7584)); // Sector D
            _pylons.Add(15, new pylonObject(11957, 13604)); // Sector D

            _pylonsA = new Dictionary<int, pylonObject>();
            _pylonsA.Add(0, new pylonObject(512, 480)); // Sector A
            _pylonsA.Add(1, new pylonObject(2736, 5600)); // Sector A
            _pylonsA.Add(2, new pylonObject(4901, 4740)); // Sector A
            _pylonsA.Add(3, new pylonObject(7445, 2356)); // Sector A

            _pylonsB = new Dictionary<int, pylonObject>();
            _pylonsB.Add(0, new pylonObject(5504, 7040)); // Sector B
            _pylonsB.Add(1, new pylonObject(8304, 11008));// Sector B
            _pylonsB.Add(2, new pylonObject(6784, 13808));// Sector B
            _pylonsB.Add(3, new pylonObject(4117, 9956));// Sector B

            _pylonsC = new Dictionary<int, pylonObject>();
            _pylonsC.Add(0, new pylonObject(13765, 1236)); // Sector C
            _pylonsC.Add(1, new pylonObject(17093, 5076)); // Sector C
            _pylonsC.Add(2, new pylonObject(12565, 5252)); // Sector C
            _pylonsC.Add(3, new pylonObject(18117, 3316)); // Sector C

            _pylonsD = new Dictionary<int, pylonObject>();
            _pylonsD.Add(0, new pylonObject(20661, 7924)); // Sector D
            _pylonsD.Add(1, new pylonObject(16981, 10580)); // Sector D
            _pylonsD.Add(2, new pylonObject(18064, 7584)); // Sector D
            _pylonsD.Add(3, new pylonObject(11957, 13604)); // Sector D

            _pylonsAB = new Dictionary<int, pylonObject>();
            _pylonsAB.Add(0, new pylonObject(512, 480)); // Sector A
            _pylonsAB.Add(1, new pylonObject(2736, 5600)); // Sector A
            _pylonsAB.Add(2, new pylonObject(4901, 4740)); // Sector A
            _pylonsAB.Add(3, new pylonObject(7445, 2356)); // Sector A
            _pylonsAB.Add(4, new pylonObject(5504, 7040)); // Sector B
            _pylonsAB.Add(5, new pylonObject(8304, 11008));// Sector B
            _pylonsAB.Add(6, new pylonObject(6784, 13808));// Sector B
            _pylonsAB.Add(7, new pylonObject(4117, 9956));// Sector B

            _pylonsAC = new Dictionary<int, pylonObject>();
            _pylonsAC.Add(0, new pylonObject(512, 480)); // Sector A
            _pylonsAC.Add(1, new pylonObject(2736, 5600)); // Sector A
            _pylonsAC.Add(2, new pylonObject(4901, 4740)); // Sector A
            _pylonsAC.Add(3, new pylonObject(7445, 2356)); // Sector A
            _pylonsAC.Add(4, new pylonObject(13765, 1236)); // Sector C
            _pylonsAC.Add(5, new pylonObject(17093, 5076)); // Sector C
            _pylonsAC.Add(6, new pylonObject(12565, 5252)); // Sector C
            _pylonsAC.Add(7, new pylonObject(18117, 3316)); // Sector C

            _pylonsCD = new Dictionary<int, pylonObject>();
            _pylonsCD.Add(0, new pylonObject(13765, 1236)); // Sector C
            _pylonsCD.Add(1, new pylonObject(17093, 5076)); // Sector C
            _pylonsCD.Add(2, new pylonObject(12565, 5252)); // Sector C
            _pylonsCD.Add(3, new pylonObject(18117, 3316)); // Sector C
            _pylonsCD.Add(4, new pylonObject(20661, 7924)); // Sector D
            _pylonsCD.Add(5, new pylonObject(16981, 10580)); // Sector D
            _pylonsCD.Add(6, new pylonObject(18064, 7584)); // Sector D
            _pylonsCD.Add(7, new pylonObject(11957, 13604)); // Sector D

            _pylonsBD = new Dictionary<int, pylonObject>();
            _pylonsBD.Add(0, new pylonObject(5504, 7040)); // Sector B
            _pylonsBD.Add(1, new pylonObject(8304, 11008));// Sector B
            _pylonsBD.Add(2, new pylonObject(6784, 13808));// Sector B
            _pylonsBD.Add(3, new pylonObject(4117, 9956));// Sector B
            _pylonsBD.Add(4, new pylonObject(20661, 7924)); // Sector D
            _pylonsBD.Add(5, new pylonObject(16981, 10580)); // Sector D
            _pylonsBD.Add(6, new pylonObject(18064, 7584)); // Sector D
            _pylonsBD.Add(7, new pylonObject(11957, 13604)); // Sector D

            _usedpylons = new Dictionary<int, pylonObject>();
            _lastPylon = null;

            isRoamActive = false;
            nextSpanStart = -1;
            return true;
        }

        private struct PylonCoordEntry
        {
            public short X;
            public short Y;
            public int Id;
            public PylonCoordEntry(short x, short y, int id) { X = x; Y = y; Id = id; }
        }

        private static readonly PylonCoordEntry[] s_pylonLocationByCoord = new PylonCoordEntry[]
        {
            new PylonCoordEntry(512, 480, 1), new PylonCoordEntry(2736, 5600, 2), new PylonCoordEntry(4901, 4740, 3), new PylonCoordEntry(7445, 2356, 4),
            new PylonCoordEntry(5504, 7040, 5), new PylonCoordEntry(8304, 11008, 6), new PylonCoordEntry(6784, 13808, 7), new PylonCoordEntry(4117, 9956, 8),
            new PylonCoordEntry(13765, 1236, 9), new PylonCoordEntry(17093, 5076, 10), new PylonCoordEntry(12565, 5252, 11), new PylonCoordEntry(18117, 3316, 12),
            new PylonCoordEntry(20661, 7924, 13), new PylonCoordEntry(16981, 10580, 14), new PylonCoordEntry(18064, 7584, 15), new PylonCoordEntry(11957, 13604, 16),
        };

        private bool TryApplyPylonLocationFromXY(short hx, short hy)
        {
            _botlocX = hx;
            _botlocY = hy;
            for (int i = 0; i < s_pylonLocationByCoord.Length; i++)
            {
                PylonCoordEntry e = s_pylonLocationByCoord[i];
                if (hx == e.X && hy == e.Y)
                {
                    _pylonLocation = e.Id;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Sets layout id from template coords; no-op match leaves prior <see cref="_pylonLocation"/> unchanged.</summary>
        private void ApplyPylonLocationFromXY(short hx, short hy)
        {
            TryApplyPylonLocationFromXY(hx, hy);
        }

        private void EnsureKillStreak(InfServer.Game.Player player)
        {
            if (!killStreaks.ContainsKey(player._alias))
            {
                PlayerStreak temp = new PlayerStreak();
                temp.lastKillerCount = 0;
                temp.lastUsedWeap = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                killStreaks.Add(player._alias, temp);
            }
        }

        private bool PollSkipDueToKothInterval(int now)
        {
            if (now - _lastKOTHGameCheck <= Arena.gameCheckInterval)
                return true;
            _lastKOTHGameCheck = now;
            return false;
        }

        private void PollEolWhileRunning(int now)
        {
            if (!_arena._bGameRunning)
                return;
            if (_tickLastEolWhileRunningPoll != 0 && now - _tickLastEolWhileRunningPoll < Arena.gameCheckInterval)
                return;
            _tickLastEolWhileRunningPoll = now;

            _eol.Poll(now);
            if (_lastPvpRoamHourAlert == 0)
            {
                _lastPvpRoamHourAlert = now;
                _lastRoamAlert = now;
            }
            if (EolSideTimersActive())
            {
                RoamSpawnCheck(now);
                spawnRoam(now);
            }
        }

        void ClearKothLobbyTickerIfSet()
        {
            if (_arena == null || _arena._tickers == null)
                return;
            Arena.TickerInfo t;
            if (!_arena._tickers.TryGetValue(KothLobbyTickerIdx, out t) || t == null)
                return;
            if (string.IsNullOrEmpty(t.message) && t.customTicker == null)
                return;

            // Row 2 is shared with the bot activity HUD. Only clear KOTH/sector-owned content —
            // blanking any text here made Raiders/Aliens/Base vanish until the next count change.
            if (!IsKothOwnedLobbyTicker(t))
                return;

            if (EolActivityTickerRowAvailable())
            {
                PushEolActivityTicker(force: true);
                return;
            }

            _arena.setTicker(1, KothLobbyTickerIdx, 0, "");
            _lastHudActivityTickerSent = null;
        }

        /// <summary>True when idx 2 is a KOTH lobby countdown, sector warning, or crown timer — not bot activity.</summary>
        static bool IsKothOwnedLobbyTicker(Arena.TickerInfo t)
        {
            if (t == null)
                return false;
            string m = t.message ?? "";
            if (m.Length == 0)
                return false;
            // Do not key off expireCallback — activity renewals also use a callback.
            if (m.StartsWith("KOTH", StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.StartsWith("New sectors", StringComparison.OrdinalIgnoreCase))
                return true;
            if (m.StartsWith("Crown:", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>Write the bot/wave activity line to HUD row 2 when that row is free.</summary>
        void PushEolActivityTicker(bool force)
        {
            if (_arena == null || !EolActivityTickerRowAvailable())
                return;

            RebuildEolHudTickerCacheIfDue();
            string activity = _cachedEolActivityTickerLine ?? "";

            Arena.TickerInfo live = null;
            bool haveLive = _arena._tickers != null
                && _arena._tickers.TryGetValue(EolActivityTickerIdx, out live)
                && live != null;
            bool expired = !haveLive || live.timer == -1;
            bool sameText = string.Equals(activity, _lastHudActivityTickerSent, StringComparison.Ordinal)
                && haveLive
                && string.Equals(live.message, activity, StringComparison.Ordinal);

            // timer=0: no client countdown; re-push when expired or text changes (updateTickers keeps it alive).
            if (!force && !expired && sameText)
                return;

            _lastHudActivityTickerSent = activity;
            _arena.setTicker(0, EolActivityTickerIdx, EolStaticTickerDuration, activity,
                RenewEolActivityTickerIfStillAvailable);
        }

        void RenewEolActivityTickerIfStillAvailable()
        {
            if (EolActivityTickerRowAvailable())
                PushEolActivityTicker(force: true);
        }

        private void PollKothLobby(int now, int playing)
        {
            if (_tickLastKothLobbyPoll != 0 && now - _tickLastKothLobbyPoll < KothLobbyPollMs)
                return;
            _tickLastKothLobbyPoll = now;

            if (!EolKothSystemEnabled)
            {
                if (_tickKOTHGameStarting != 0)
                    _tickKOTHGameStarting = 0;
                ClearKothLobbyTickerIfSet();
                return;
            }

            if (_kothGameRunning)
                return;

            if (_sectorChangeCountdownActive)
                return;

            if (playing < 5)
            {
                // Never touch (1, CtfNextGameTickerIdx) here — that row is the main match countdown (same as MOBA).
                ClearKothLobbyTickerIfSet();
                if (_tickKOTHGameStarting != 0)
                    _tickKOTHGameStarting = 0;
                return;
            }

            if (_tickKOTHGameStarting != 0)
                return;

            if (_tickNextKothEligible != 0 && now < _tickNextKothEligible)
            {
                ClearKothLobbyTickerIfSet();
                return;
            }

            _tickNextKothEligible = 0;
            _tickKOTHGameStarting = now;
            _lastHudActivityTickerSent = null;
            _arena.setTicker(1, KothLobbyTickerIdx, 15 * 100, "KOTH - Next game: ",
                delegate ()
                {
                    startKOTH();
                }
            );
        }

        void ScheduleKothCooldown(int tickNow)
        {
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            _tickNextKothEligible = tickNow + _rand.Next(KothBetweenRoundsCooldownMinMs, KothBetweenRoundsCooldownMaxMs + 1);
        }

        private void PollMainGameStartTicker(int now, int playing)
        {
            // Match already running — never leave a stale lobby countdown on the HUD.
            if (_arena._bGameRunning || _tickGameStart != 0)
            {
                if (_tickGameStarting != 0 || _lastCtfLobbyCountdownSecondShown >= 0)
                    ClearCtfNextGameLobbyTicker();
                return;
            }

            if (playing < 1)
            {
                // Specs alone still count — start/keep lobby countdown so bots can run for watchers.
                if (EolHasArenaPresence())
                {
                    // fall through to countdown / start
                }
                else
                {
                    if (_tickGameStarting != 0 || _lastCtfLobbyCountdownSecondShown >= 0)
                        ClearCtfNextGameLobbyTicker();
                    return;
                }
            }

            // Poll-driven countdown: refresh every poll with remaining whole seconds; match start fires here (not via setTicker callback).
            if (_tickGameStarting != 0)
            {
                int elapsed = now - _tickGameStarting;
                if (elapsed < 0)
                    elapsed = 0;
                if (elapsed > CtfLobbyStaleMs)
                {
                    ClearCtfNextGameLobbyTicker();
                    return;
                }
                if (elapsed >= CtfNextGameCountdownMs)
                {
                    ClearCtfNextGameLobbyTicker();
                    if (!_arena._bGameRunning && EolHasArenaPresence())
                        _arena.gameStart();
                    return;
                }
                int sec = CeilSecondsRemaining(CtfNextGameCountdownMs, elapsed);
                if (sec < 1)
                    sec = 1;
                if (sec != _lastCtfLobbyCountdownSecondShown)
                {
                    _lastCtfLobbyCountdownSecondShown = sec;
                    _arena.setTicker(1, CtfNextGameTickerIdx, 0, string.Format("Next game in {0}s", sec));
                }
                return;
            }

            _tickGameStarting = now;
            _lastCtfLobbyCountdownSecondShown = CeilSecondsRemaining(CtfNextGameCountdownMs, 0);
            _arena.setTicker(1, CtfNextGameTickerIdx, 0, string.Format("Next game in {0}s", _lastCtfLobbyCountdownSecondShown));
        }

        void ClearCtfNextGameLobbyTicker()
        {
            if (_arena != null)
                _arena.setTicker(1, CtfNextGameTickerIdx, 0, "");
            _tickGameStarting = 0;
            _lastCtfLobbyCountdownSecondShown = -1;
        }

        /// <summary>Last player in <see cref="Team.ActivePlayers"/> order — foreach only (no LINQ on ActivePlayers).</summary>
        static InfServer.Game.Player GetLastActivePlayerOnTeam(Team team)
        {
            if (team == null)
                return null;
            InfServer.Game.Player last = null;
            foreach (InfServer.Game.Player p in team.ActivePlayers)
                last = p;
            return last;
        }

        private void PollBalancePrivateTeams(int playing)
        {
            if (playing <= 5)
            {
                foreach (Team team in _arena.ActiveTeams)
                {
                    if (team.ActivePlayerCount <= 1)
                        continue;
                    int numToRemove = team.ActivePlayerCount - 3;
                    for (int i = 0; i < numToRemove; i++)
                    {
                        InfServer.Game.Player lastJoined = GetLastActivePlayerOnTeam(team);
                        if (lastJoined == null)
                            continue;
                        pickTeam(lastJoined);
                        lastJoined.sendMessage(0, "You've been moved to a new team — you were the last to join and this team is over the limit (3 players).");
                    }
                }
            }
            else if (playing >= 6 && playing <= 15)
            {
                foreach (Team team in _arena.ActiveTeams)
                {
                    if (team.ActivePlayerCount <= 1)
                        continue;
                    int numToRemove = team.ActivePlayerCount - 4;
                    for (int i = 0; i < numToRemove; i++)
                    {
                        InfServer.Game.Player lastJoined = GetLastActivePlayerOnTeam(team);
                        if (lastJoined == null)
                            continue;
                        pickTeam(lastJoined);
                        lastJoined.sendMessage(0, "You've been moved to a new team — you were the last to join and this team is over the limit (4 players).");
                    }
                }
            }
        }

        private void PollBotPopulationCaps(int playing)
        {
            _maxRoamCaptains = 1;
            _maxRoamingBots = 30;
            if (playing < 30)
            {
                _maxDefenseBots = EolMaxConcurrentBaseDefenseBots;
                _maxRoamingBots = 20;
                _maxRoamingAliens = 20;
            }
            if (playing >= 30 && playing < 60)
            {
                _maxDefenseBots = EolMaxConcurrentBaseDefenseBots;
                _maxRoamingBots = 25;
                _maxRoamingAliens = 20;
            }
            if (playing >= 60)
            {
                _maxDefenseBots = EolMaxConcurrentBaseDefenseBots;
                _maxRoamingBots = 30;
                _maxRoamingAliens = 20;
            }
            _maxDefPerTeam = EolMaxConcurrentBaseDefenseBots;
            _maxRoamPerTeam = PirateRaidMaxConcurrentAlive;
        }

        /// <summary>Match is running and Eol sectors are committed — periodic flag rewards, alien waves, and roam captain spawning use this.</summary>
        bool EolSideTimersActive()
        {
            return _arena != null && _arena._bGameRunning && !_bbetweengames
                && _eol != null && _eol.MatchSectorPlayReady
                && EolHasArenaPresence();
        }

        /// <summary>Anyone in the arena (playing or spectator). Bots stay active while this is &gt; 0.</summary>
        int EolArenaPresenceCount()
        {
            if (_arena == null)
                return 0;
            return _arena.TotalPlayerCount;
        }

        bool EolHasArenaPresence()
        {
            if (Arena.persistentArena)
                return true;
            return EolArenaPresenceCount() > 0;
        }

        /// <summary>Population used for bot caps/HQ counts — treat spectators as presence so bots do not stall when everyone is in spec.</summary>
        int EolBotPopulationForCaps(int playingIngame)
        {
            if (playingIngame > 0)
                return playingIngame;
            return EolHasArenaPresence() ? 1 : 0;
        }

        /// <summary>True if any active team currently owns an on-map flag (periodic payout only runs when this is true at cycle end).</summary>
        static bool AnyActiveTeamOwnsArenaFlag(Arena arena)
        {
            if (arena == null || arena._flags == null)
                return false;
            foreach (Arena.FlagState fs in arena._flags.Values)
            {
                if (fs == null || !fs.bActive || fs.team == null)
                    continue;
                if (IsBotFactionTeamForFlagRewards(fs.team))
                    continue;
                foreach (Team t in arena.ActiveTeams)
                {
                    if (t != null && TeamOwnsArenaFlag(t, fs))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Bot teams may hold flags but must not drive periodic rewards or CTF victory.</summary>
        static bool IsBotFactionTeamForFlagRewards(Team team)
        {
            if (team == null)
                return false;
            return team._name != null && team._name.StartsWith("Bot", StringComparison.OrdinalIgnoreCase);
        }

        void ResetFlagRewardCycleNotices()
        {
            Min1Timer = false;
            Min5Timer = false;
            Min1Send = false;
            Min5Send = false;
            _flagReward5MinNoticeDone = false;
            _flagReward1MinNoticeDone = false;
        }

        /// <summary>One pass over active teams and flags — only at 5 min / 1 min milestones.</summary>
        void SendFlagRewardTimeWarningToFlagHoldingTeams(int minutesRemaining)
        {
            if (_arena == null || _arena._flags == null)
                return;
            foreach (Team t in _arena.ActiveTeams)
            {
                if (t == null || !TeamQualifiesForFlagRewards(t))
                    continue;
                bool holds = false;
                foreach (Arena.FlagState fs in _arena._flags.Values)
                {
                    if (!TeamOwnsArenaFlag(t, fs))
                        continue;
                    holds = true;
                    break;
                }
                if (holds)
                {
                    string suffix = minutesRemaining == 1 ? "" : "s";
                    t.sendArenaMessage("Your next flag reward is in " + minutesRemaining + " minute" + suffix, _arena._server._zoneConfig.flag.periodicBong);
                }
            }
        }

        private void PollFlagRewardTimers(int now)
        {
            if (!EolSideTimersActive() || _lastFlagReward == 0)
                return;

            int elapsed = now - _lastFlagReward;
            if (elapsed < 0)
            {
                _lastFlagReward = now;
                ResetFlagRewardCycleNotices();
                return;
            }

            if (elapsed >= 600000)
            {
                if (!_flagReward5MinNoticeDone)
                {
                    _flagReward5MinNoticeDone = true;
                    Min5Timer = true;
                    SendFlagRewardTimeWarningToFlagHoldingTeams(5);
                }
                if (!_flagReward1MinNoticeDone)
                {
                    _flagReward1MinNoticeDone = true;
                    Min1Timer = true;
                    SendFlagRewardTimeWarningToFlagHoldingTeams(1);
                }

                if (!AnyActiveTeamOwnsArenaFlag(_arena))
                {
                    ResetFlagRewardCycleNotices();
                    _lastFlagReward = now;
                    Log.write(TLog.Normal, "[Eol] Flag reward cycle reset: no team held a flag at the payout window.");
                }
                else
                {
                    rewards();
                    ResetFlagRewardCycleNotices();
                }
                return;
            }

            if (elapsed >= 300000 && !_flagReward5MinNoticeDone)
            {
                _flagReward5MinNoticeDone = true;
                Min5Timer = true;
                SendFlagRewardTimeWarningToFlagHoldingTeams(5);
            }
            if (elapsed >= 540000 && !_flagReward1MinNoticeDone)
            {
                _flagReward1MinNoticeDone = true;
                Min1Timer = true;
                SendFlagRewardTimeWarningToFlagHoldingTeams(1);
            }
        }

        private void PollPylonPeriodic(int now)
        {
            if ((now - _lastPylonCheck >= 36000000) && _arena._bGameRunning)
            {
                if (EolBaseSetupEnabled && (_usedpylons == null || _usedpylons.Count == 0))
                    RebuildBotHqSlotTemplates();
                _lastPylonCheck = now;
            }
        }

        private void PollEolSectorWind(int now)
        {
            if (_arena == null || !_arena._bGameRunning || _bbetweengames)
                return;
            if (_tickNextSectorRotation == 0)
                return;
            if (_sectorChangeCountdownActive)
                return;
            if (now < _tickNextSectorRotation)
                return;

            // Live KOTH round blocks rotation; lobby/cooldown do not. Stay due until idle.
            if (_kothGameRunning)
                return;

            preNewSector();
            _tickNextSectorRotation = now + SectorRotationIntervalMs;
        }

        private bool PollKothCrownsAndVictory(int now, int playingCount)
        {
            if (!EolKothSystemEnabled)
                return false;

            _minPlayers = _config.king.minimumPlayers;
            if (_minPlayers > playingCount)
            {
                if (_playerCrownStatus == null)
                    _playerCrownStatus = new Dictionary<InfServer.Game.Player, PlayerCrownStatus>();
                if (_crownTeams == null)
                    _crownTeams = new List<Team>();
            }

            // Only poll crowns while a KOTH round is live.
            if (_tickKothGameStart <= 0 || !_kothGameRunning)
                return false;

            if (_playerCrownStatus == null)
                return false;

            // No opposing teams left — cancel without awarding a winner.
            if (_arena.ActiveTeams.Count() <= 1)
            {
                _arena.sendArenaMessage("KOTH cancelled — only one team remains. No winner.");
                endKOTH();
                return true;
            }

            // Expire crowns on timer only — never use CTF `_victoryTeam` (that was wiping crowns
            // after a KOTH win announcement and then declaring "no winner").
            bool crownStateChanged = false;
            foreach (var p in _playerCrownStatus)
            {
                if (p.Value.crown && now > p.Value.expireTime)
                {
                    p.Value.crown = false;
                    crownStateChanged = true;
                }
            }
            if (crownStateChanged)
                QueueKothCrownResync();

            if (_crownTeams == null)
                _crownTeams = new List<Team>();
            _crownTeams.Clear();
            foreach (InfServer.Game.Player p in _activeCrowns)
            {
                if (p == null || p._team == null)
                    continue;
                if (!_crownTeams.Contains(p._team))
                    _crownTeams.Add(p._team);
            }

            // One team still holding crown(s) among multiple active teams — award and end via kothVictory.
            if (_crownTeams.Count == 1)
            {
                Team winners = _crownTeams[0];
                if (winners != null && !winners.IsSpec)
                {
                    _victoryKothTeam = winners;
                    QueueKothArenaMessage(winners._name + " is the winner");
                    kothVictory(winners);
                    return true;
                }
            }

            // No crowns left and nobody won — draw / wipe.
            if (_activeCrowns.Count == 0)
            {
                QueueKothArenaMessage("There was no winner");
                endKOTH();
                return true;
            }

            return false;
        }

        /// <summary>True if <paramref name="team"/> already has a live defense <see cref="Captain"/> bot.</summary>
        bool TeamHasLiveDefenseCaptain(Team team)
        {
            if (team == null || _bots == null)
                return false;
            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b._team != team || !(b is Captain))
                    continue;
                if (!b.IsDead || b.bDropshipRespawnPending)
                    return true;
            }
            return false;
        }

        /// <summary>True if <paramref name="team"/> already has a live defense <see cref="Engineer"/> bot.</summary>
        bool TeamHasLiveDefenseEngineer(Team team)
        {
            if (team == null || _bots == null)
                return false;
            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b._team != team || !(b is Engineer))
                    continue;
                if (!b.IsDead || b.bDropshipRespawnPending)
                    return true;
            }
            return false;
        }

        /// <summary>HQ / turret layout vehicles for a defense bot team (heal targets for Engineer).</summary>
        public List<Vehicle> GetDefenseBaseVehiclesForTeam(Team team)
        {
            if (team == null || _defenseBaseVehicles == null)
                return null;
            List<Vehicle> list;
            if (!_defenseBaseVehicles.TryGetValue(team, out list))
                return null;
            return list;
        }

        /// <summary>Bookkeeping when a defense engineer dies permanently (not dropship-pending).</summary>
        public void OnDefenseEngineerLost(Engineer eng)
        {
            if (eng == null || eng._team == null)
                return;
            if (_currentEngineers > 0)
                _currentEngineers--;
            if (engineerBots != null)
                engineerBots.Remove(eng._team);
            SyncDefenseBotCount(eng._team);
        }

        /// <summary>Kills defense <see cref="Captain"/> / <see cref="BasicDefense"/> / <see cref="Engineer"/> bots on teams with no live HQ (469).</summary>
        void PruneOrphanedDefenseBaseBots()
        {
            if (_bots == null)
                return;

            List<Bots.Bot> toRemove = new List<Bots.Bot>();
            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b.IsDead || b._team == null || !IsDefenseBotTeam(b._team))
                    continue;
                if (!(b is Captain || b is BasicDefense || b is Engineer))
                    continue;
                if (TeamHasLiveHqVehicle(b._team))
                    continue;
                toRemove.Add(b);
            }

            foreach (Bots.Bot b in toRemove)
            {
                if (b != null && b.bDropshipRespawnPending)
                {
                    FinalizeBotDropshipRespawnCancelled(b);
                    continue;
                }
                if (b._team != null)
                {
                    if (captainBots != null)
                        captainBots.Remove(b._team);
                    if (b is Engineer)
                        OnDefenseEngineerLost(b as Engineer);
                }
                EnqueueVehicleDespawn(b);
            }
        }

        /// <summary>
        /// Queues defense bots plus tracked base vehicles (HQ turrets/walls) for paced despawn when
        /// that team's HQ dies. Clears the per-team base vehicle list.
        /// Do not call this when only the captain dies — grunts stay while the HQ is live.
        /// </summary>
        void WipeDefenseBotTeamAssets(Team team, Vehicle exceptVehicle)
        {
            if (team == null || _arena == null)
                return;

            // Script-tracked bots first (bookkeeping via EnqueueVehicleDespawn).
            if (_bots != null)
            {
                List<Bots.Bot> teamBots = new List<Bots.Bot>();
                foreach (Bots.Bot b in _bots)
                {
                    if (b == null || b._team != team)
                        continue;
                    if (exceptVehicle != null && ReferenceEquals(b, exceptVehicle))
                        continue;
                    teamBots.Add(b);
                }
                for (int i = 0; i < teamBots.Count; i++)
                    EnqueueVehicleDespawn(teamBots[i]);
            }

            // Base layout vehicles registered at place time (turrets / walls / HQ chassis).
            List<Vehicle> baseVehs;
            if (_defenseBaseVehicles != null && _defenseBaseVehicles.TryGetValue(team, out baseVehs) && baseVehs != null)
            {
                for (int i = 0; i < baseVehs.Count; i++)
                {
                    Vehicle v = baseVehs[i];
                    if (v == null)
                        continue;
                    if (exceptVehicle != null && ReferenceEquals(v, exceptVehicle))
                        continue;
                    EnqueueVehicleDespawn(v);
                }
                baseVehs.Clear();
                _defenseBaseVehicles.Remove(team);
            }

            if (captainBots != null)
                captainBots.Remove(team);
            if (engineerBots != null)
                engineerBots.Remove(team);
            if (botCount != null)
                botCount.Remove(team);
        }

        void RegisterDefenseBaseVehicle(Team team, Vehicle v)
        {
            if (team == null || v == null || !IsDefenseBotTeam(team))
                return;
            if (_defenseBaseVehicles == null)
                _defenseBaseVehicles = new Dictionary<Team, List<Vehicle>>();
            List<Vehicle> list;
            if (!_defenseBaseVehicles.TryGetValue(team, out list) || list == null)
            {
                list = new List<Vehicle>(16);
                _defenseBaseVehicles[team] = list;
            }
            list.Add(v);
        }

        void ClearAllDefenseBaseVehicleLists()
        {
            if (_defenseBaseVehicles == null)
                return;
            foreach (KeyValuePair<Team, List<Vehicle>> kv in _defenseBaseVehicles)
            {
                if (kv.Value != null)
                    kv.Value.Clear();
            }
            _defenseBaseVehicles.Clear();
        }

        /// <summary>Clears defense base when that team's HQ dies. Completes Base threat mode (rotation) if active.</summary>
        void OnDefenseBotHqDestroyed(Team team, Vehicle deadHq)
        {
            if (team == null || !IsDefenseBotTeam(team))
                return;
            // Duplicate HQ spawns are destroyed in Vehicle.Creation while another live HQ remains.
            if (TeamHasLiveHqVehicle(team))
                return;

            // Clear the whole base (bots + turrets) before engineer redeploy can place a new HQ.
            WipeDefenseBotTeamAssets(team, deadHq);

            // Base bots stay in the match — allow engineer poll to place a replacement HQ.
            _tickDefenseCaptainRespawnEligible = 0;
            _tickLastDefenseCaptainSpawnAttempt = 0;
            _tickLastEngineer = 0;
        }

        /// <summary>Clear tile near a bot HQ (turrets/pylons make a tight 150-radius search fail often).</summary>
        Helpers.ObjectState FindDefenseCaptainSpawnNearHq(short hqX, short hqY)
        {
            Helpers.ObjectState pt = findOpenWarpBot(_arena, hqX, hqY, 160, 36);
            if (pt != null)
                return pt;
            pt = findOpenWarpBot(_arena, hqX, hqY, 360, 56);
            if (pt != null)
                return pt;
            short[] ring = { 128, -128, 200, -200 };
            foreach (short off in ring)
            {
                pt = findOpenWarpBot(_arena, (short)(hqX + off), hqY, 120, 28);
                if (pt != null)
                    return pt;
                pt = findOpenWarpBot(_arena, hqX, (short)(hqY + off), 120, 28);
                if (pt != null)
                    return pt;
            }
            return findOpenWarpBot(_arena, hqX, hqY, 520, 72);
        }

        /// <summary>Snapshot of live HQ chassis — do not foreach <see cref="Arena.Vehicles"/> during <see cref="Vehicle.Creation"/>.</summary>
        List<Vehicle> GetLiveHqVehiclesSnapshot()
        {
            var list = new List<Vehicle>();
            if (_arena == null)
                return list;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v._type == null || v.IsDead || v._type.Id != _hqVehId)
                    continue;
                if (v.bCondemned || IsVehicleDespawnQueued(v))
                    continue;
                list.Add(v);
            }
            return list;
        }

        /// <summary>Deploys a defense Captain near each HQ on a defense bot team that does not already have a captain.</summary>
        void TrySpawnDefenseCaptainIfNeeded(int now)
        {
            if (!EolBaseBotsEnabled)
                return;
            if (!_arena._bGameRunning || captainBots == null)
                return;
            if (_tickDefenseCaptainRespawnEligible != 0 && now < _tickDefenseCaptainRespawnEligible)
                return;
            if (_tickLastDefenseCaptainSpawnAttempt != 0 && now - _tickLastDefenseCaptainSpawnAttempt < DefenseCaptainSpawnAttemptIntervalMs)
                return;

            _tickLastDefenseCaptainSpawnAttempt = now;

            foreach (Vehicle hq in GetLiveHqVehiclesSnapshot())
            {
                Team team = hq._team;
                if (team == null || !IsDefenseBotTeam(team))
                    continue;

                if (TeamHasPendingDespawns(team))
                    continue;

                if (!TeamHasLiveHqVehicle(team))
                    continue;

                if (TeamHasLiveDefenseCaptain(team))
                    continue;
                if (captainBots != null && captainBots.ContainsKey(team))
                    captainBots.Remove(team);

                if (_bots == null)
                    _bots = new List<Bots.Bot>();

                short hqX = hq._state.positionX;
                short hqY = hq._state.positionY;
                if (_eol != null && !_eol.IsInAnyActiveSector(hqX, hqY))
                    continue;
                Helpers.ObjectState openPoint = FindDefenseCaptainSpawnNearHq(hqX, hqY);
                if (openPoint == null)
                {
                    Log.write(TLog.Warning, "[Eol] Defense captain: no clear spawn near HQ at ({0},{1}) for {2}.", hqX, hqY, team._name);
                    continue;
                }

                const int captainVehId = 437;
                Captain captain = _arena.newBot(typeof(Captain), (ushort)captainVehId, team, null, openPoint, this, null) as Captain;
                if (captain == null)
                {
                    Log.write(TLog.Warning, "[Eol] Defense captain: newBot failed (veh {0}) for {1} at ({2},{3}).", captainVehId, team._name, openPoint.positionX, openPoint.positionY);
                    continue;
                }

                EnableBotDropshipRespawn(captain, BotMaxDeathsBase);

                if (!TeamHasLiveHqVehicle(team))
                {
                    ForgetScriptBotBookkeeping(captain);
                    captain.destroy(true);
                    Log.write(TLog.Warning, "[Eol] Defense captain aborted — no live HQ for {0}.", team._name);
                    continue;
                }

                _arena.sendArenaMessage("A new base Captain has been deployed from Pioneer Station.");
                if (!captainBots.ContainsKey(team))
                    captainBots.Add(team, 0);
                _bots.Add(captain);
                // Keep existing squad when the captain dies/respawns — recount all combat bots, do not wipe to 0.
                SyncDefenseBotCount(team);
                _tickDefenseCaptainRespawnEligible = 0;
                Log.write(TLog.Normal, "[Eol] Defense captain deployed at ({0},{1}) for {2}.", openPoint.positionX, openPoint.positionY, team._name);

                TrySpawnDefenseEngineerForTeam(team, hq);
                SyncDefenseBotCount(team);
            }
        }

        /// <summary>Deploys one Engineer (veh 463) per defense HQ that does not already have one.</summary>
        void TrySpawnDefenseEngineerIfNeeded(int now)
        {
            if (!EolBaseBotsEnabled)
                return;
            if (!_arena._bGameRunning || _bots == null)
                return;

            foreach (Vehicle hq in GetLiveHqVehiclesSnapshot())
            {
                Team team = hq._team;
                if (team == null || !IsDefenseBotTeam(team))
                    continue;
                if (TeamHasPendingDespawns(team))
                    continue;
                if (!TeamHasLiveHqVehicle(team))
                    continue;
                TrySpawnDefenseEngineerForTeam(team, hq);
            }
        }

        void TrySpawnDefenseEngineerForTeam(Team team, Vehicle hq)
        {
            if (team == null || hq == null || _arena == null)
                return;
            if (!EolBaseBotsEnabled)
                return;
            if (TeamHasLiveDefenseEngineer(team))
                return;
            // Engineer counts toward the per-team combat cap with captain + grunts.
            if (CountLiveDefenseBotsForTeam(team) >= _maxDefPerTeam)
                return;

            if (_bots == null)
                _bots = new List<Bots.Bot>();

            Helpers.ObjectState openPoint = FindDefenseCaptainSpawnNearHq(hq._state.positionX, hq._state.positionY);
            if (openPoint == null)
            {
                Log.write(TLog.Warning, "[Eol] Defense engineer: no clear spawn near HQ for {0}.", team._name);
                return;
            }

            Engineer eng = _arena.newBot(typeof(Engineer), Engineer.EngineerVehicleId, team, null, openPoint, this) as Engineer;
            if (eng == null)
            {
                Log.write(TLog.Warning, "[Eol] Defense engineer: newBot failed (veh {0}) for {1}.", Engineer.EngineerVehicleId, team._name);
                return;
            }

            EnableBotDropshipRespawn(eng, BotMaxDeathsBase);
            _bots.Add(eng);
            _currentEngineers++;
            if (engineerBots != null && !engineerBots.Contains(team))
                engineerBots.Add(team);
            SyncDefenseBotCount(team);

            Log.write(TLog.Normal, "[Eol] Defense engineer deployed at ({0},{1}) for {2}.",
                openPoint.positionX, openPoint.positionY, team._name);
        }

        private void PollCaptainSpawning(int now)
        {
            PruneOrphanedDefenseBaseBots();
            TrySpawnDefenseCaptainIfNeeded(now);
            TrySpawnDefenseEngineerIfNeeded(now);

            if (now - _tickLastCaptain > _checkCaptain)
                _tickLastCaptain = now;
        }

        /// <summary>Allows the script to maintain itself.</summary>
        public bool poll()
        {	//Should we check game state yet?
            int now = Environment.TickCount;
            int playing = _arena.PlayerCount;
            int botPop = EolBotPopulationForCaps(playing);

            PruneExpiredCombatCalls(now);
            PollBotDropshipRespawns(now);
            SyncIngameBountyStats();
            PollBountyHunt(now);
            PollBountyHunterSquad(now);
            PollEolHappyHour(now);
            RefreshPollVehicleCache(now);
            DrainDespawnQueue(now);
            DrainKothEndQueue(now);
            DrainExplosionNetQueue(now);
            PollAlienCrashEvent(now);
            PollEolWhileRunning(now);
            // Lobby tickers must run every poll; PollSkipDueToKothInterval throttles the rest of poll().
            PollMainGameStartTicker(now, playing);
            PollKothLobby(now, playing);
            // Captain deploy must run every poll — not behind KOTH throttle (HQ can sit without a captain for minutes otherwise).
            PollCaptainSpawning(now);
            PollDefenseHqWatch(now);

            // HUD (match / activity / bounty hunt pots) must not sit behind the KOTH throttle —
            // PollBountyHunt runs every poll and pot counters go stale otherwise.
            int hudInterval = IsBountyHuntActive ? 1000 : HudTickerUpdateMs;
            if (now - _tickLastHudTickerUpdate >= hudInterval)
            {
                _tickLastHudTickerUpdate = now;
                updateTickers();
            }

            if (PollSkipDueToKothInterval(now))
                return true;

            PollBalancePrivateTeams(playing);
            PollBotPopulationCaps(botPop);

            if (_arena._bGameRunning)
                PollFlagRewardTimers(now);

            PollPylonPeriodic(now);
            PollEolSectorWind(now);

            if (PollKothCrownsAndVictory(now, playing))
                return true;

            if (now - _tickLastCtfTickerUpdate >= CtfTickerUpdateMs)
            {
                UpdateCTFTickers();
                UpdateKillStreaks();
            }

            PollEngineerSpawning(now, botPop);

            return true;
        }

        private void PollEngineerSpawning(int now, int playing)
        {
            if (!EolBaseSetupEnabled)
                return;

            if (!_arena._bGameRunning || !EolHasArenaPresence())
                return;

            // Place/replace when we are under the population HQ cap AND a defense team still needs one.
            // Cap stops the redeploy path from filling botTeam2/3 while solo / low-pop.
            int playingForHq = playing;
            if (playingForHq < 1 && EolHasArenaPresence())
                playingForHq = 1;
            if (_arena.PlayersIngame != null && playing > 0)
            {
                try { playingForHq = _arena.PlayersIngame.Count(); }
                catch { playingForHq = playing; }
            }
            int desiredHqs = DesiredBotHqCountForPlayers(playingForHq);
            int liveHqs = CountLiveDefenseBotHqs();
            if (liveHqs >= desiredHqs)
                return;

            Team teamNeedingHq = null;
            if (!TeamHasLiveHqVehicle(botTeam1))
                teamNeedingHq = botTeam1;
            else if (!TeamHasLiveHqVehicle(botTeam2))
                teamNeedingHq = botTeam2;
            else if (!TeamHasLiveHqVehicle(botTeam3))
                teamNeedingHq = botTeam3;

            if (teamNeedingHq == null)
                return;

            // Wait until paced HQ-wipe despawns finish so a new base is not placed on leftovers.
            if (TeamHasPendingDespawns(teamNeedingHq))
            {
                _tickLastEngineer = now - (_checkEngineer - 3000);
                return;
            }

            // Soft-retry window: do not burn the full engineer interval on empty sector templates / failed placement.
            const int EngineerSoftRetryMs = 8000;
            if (_tickLastEngineer != 0 && now - _tickLastEngineer <= _checkEngineer)
                return;

            if (_bots == null)
                _bots = new List<Bots.Bot>();

            RebuildBotHqSlotTemplatesIfNeeded();
            EnsureBotHqAnchorSlotsAvailable();

            if (_usedpylons == null || _usedpylons.Count == 0)
            {
                // Templates not ready yet — try again soon instead of waiting a full engineer cycle.
                _tickLastEngineer = now - (_checkEngineer - EngineerSoftRetryMs);
                return;
            }

            List<KeyValuePair<int, pylonObject>> slotCandidates = new List<KeyValuePair<int, pylonObject>>();
            foreach (KeyValuePair<int, pylonObject> kv in _usedpylons)
            {
                if (!kv.Value.bExists())
                    continue;
                short px = kv.Value.getX();
                short py = kv.Value.getY();
                if (_eol != null && !_eol.IsInAnyActiveSector(px, py))
                    continue;
                // Never stack a new HQ on top of a live one (stale exists flag / same coords).
                if (IsLiveHqNearAnchor(px, py))
                    continue;
                slotCandidates.Add(kv);
            }

            if (slotCandidates.Count == 0)
            {
                // All anchors consumed or occupied — reopen unused-by-live-HQ slots and retry soon.
                EnsureBotHqAnchorSlotsAvailable(forceReopenConsumed: true);
                slotCandidates.Clear();
                foreach (KeyValuePair<int, pylonObject> kv in _usedpylons)
                {
                    if (!kv.Value.bExists())
                        continue;
                    short px = kv.Value.getX();
                    short py = kv.Value.getY();
                    if (_eol != null && !_eol.IsInAnyActiveSector(px, py))
                        continue;
                    if (IsLiveHqNearAnchor(px, py))
                        continue;
                    slotCandidates.Add(kv);
                }

                if (slotCandidates.Count == 0)
                {
                    int dormant = 0;
                    if (_usedpylons != null)
                    {
                        foreach (KeyValuePair<int, pylonObject> kv in _usedpylons)
                        {
                            if (kv.Value.bExists())
                                dormant++;
                        }
                    }
                    if (dormant > 0)
                        Log.write(TLog.Warning, "[Eol] {0} bot HQ anchor slot(s) in template but none are usable in active sectors — check sector bounds vs anchor positions.", dormant);
                    else
                        Log.write(TLog.Warning, "[Eol] No free bot HQ anchor slots available for redeploy.");
                    _tickLastEngineer = now - (_checkEngineer - EngineerSoftRetryMs);
                    return;
                }
            }

            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            // Prefer a different anchor than the one just destroyed when multiple slots are free.
            if (_hasLastDestroyedBotHq && slotCandidates.Count > 1)
            {
                List<KeyValuePair<int, pylonObject>> elsewhere = new List<KeyValuePair<int, pylonObject>>();
                long nearSq = (long)BotHqAnchorNearRaw * BotHqAnchorNearRaw;
                foreach (KeyValuePair<int, pylonObject> kv in slotCandidates)
                {
                    long dx = (long)kv.Value.getX() - _lastDestroyedBotHqX;
                    long dy = (long)kv.Value.getY() - _lastDestroyedBotHqY;
                    if (dx * dx + dy * dy > nearSq)
                        elsewhere.Add(kv);
                }
                if (elsewhere.Count > 0)
                    slotCandidates = elsewhere;
            }

            KeyValuePair<int, pylonObject> pick = slotCandidates[_rand.Next(slotCandidates.Count)];

            Team team = teamNeedingHq;

            short hx = pick.Value.getX();
            short hy = pick.Value.getY();
            if (!TryApplyPylonLocationFromXY(hx, hy))
            {
                Log.write(TLog.Warning, "[Eol] Bot HQ: no turret layout id for anchor ({0},{1}) — HQ will still place, turrets skipped.", hx, hy);
            }

            // Exact layout anchor — do not search for an open tile (that shifts HQ and all turret offsets).
            Helpers.ObjectState pylonBase = new Helpers.ObjectState();
            pylonBase.positionX = hx;
            pylonBase.positionY = hy;
            pylonBase.positionZ = 0;
            pylonBase.yaw = 0;

            Vehicle hqVehicle = null;
            try
            {
                hqVehicle = createVehicleAtObjectState(469, 0, 0, team, pylonBase);
            }
            catch (Exception ex)
            {
                Log.write(TLog.Exception, "[Eol] Bot HQ spawn threw at ({0},{1}): {2}", hx, hy, ex.Message);
                _tickLastEngineer = now - (_checkEngineer - EngineerSoftRetryMs);
                return;
            }

            if (hqVehicle == null || hqVehicle.IsDead)
            {
                Log.write(TLog.Warning, "[Eol] Bot HQ: vehicle 469 failed to spawn for {0} at ({1},{2}).", team._name, pylonBase.positionX, pylonBase.positionY);
                _tickLastEngineer = now - (_checkEngineer - EngineerSoftRetryMs);
                return;
            }

            // Successful placement attempt — full interval before next base.
            _tickLastEngineer = now;

            Log.write(TLog.Normal, "[Eol] Bot HQ online: {0} at ({1},{2}) layout {3}.",
                team._name, pylonBase.positionX, pylonBase.positionY, _pylonLocation);

            #region bot turrets

                        _arena.sendArenaMessage("$A new Headquarters has been dispatched by Pioneer Station");
                        switch (_pylonLocation)
                        {
                            case 1:
                                createVehicleAtObjectState(467, -133, 20, team, pylonBase);
                                createVehicleAtObjectState(457, -264, 84, team, pylonBase);
                                createVehicleAtObjectState(457, 245, 84, team, pylonBase);
                                createVehicleAtObjectState(467, 0, 50, team, pylonBase);
                                createVehicleAtObjectState(460, 0, 132, team, pylonBase);
                                createVehicleAtObjectState(823, 229, -156, team, pylonBase);
                                break;
                            case 2:
                                createVehicleAtObjectState(467, 293, 4, team, pylonBase);
                                createVehicleAtObjectState(457, 168, -92, team, pylonBase);
                                createVehicleAtObjectState(457, 168, 96, team, pylonBase);
                                createVehicleAtObjectState(466, 50, 0, team, pylonBase);
                                createVehicleAtObjectState(460, 90, 0, team, pylonBase);
                                createVehicleAtObjectState(823, 195, -100, team, pylonBase);
                                break;
                            case 3:
                                createVehicleAtObjectState(467, 320, -32, team, pylonBase);
                                createVehicleAtObjectState(457, 90, 0, team, pylonBase);
                                createVehicleAtObjectState(457, 368, 0, team, pylonBase);
                                createVehicleAtObjectState(466, 0, -80, team, pylonBase);
                                createVehicleAtObjectState(460, 208, -64, team, pylonBase);
                                createVehicleAtObjectState(823, -48, -32, team, pylonBase);
                                break;
                            case 4:
                                createVehicleAtObjectState(467, -80, 0, team, pylonBase);
                                createVehicleAtObjectState(457, 0, -80, team, pylonBase);
                                createVehicleAtObjectState(457, 0, 80, team, pylonBase);
                                createVehicleAtObjectState(466, -240, -90, team, pylonBase);
                                createVehicleAtObjectState(460, -195, -100, team, pylonBase);
                                createVehicleAtObjectState(823, -195, 100, team, pylonBase);
                                break;
                            case 5:
                                createVehicleAtObjectState(467, -75, -316, team, pylonBase);
                                createVehicleAtObjectState(457, 229, -204, team, pylonBase);
                                createVehicleAtObjectState(457, 181, -428, team, pylonBase);
                                createVehicleAtObjectState(466, 101, -108, team, pylonBase);
                                createVehicleAtObjectState(460, 69, 84, team, pylonBase);
                                createVehicleAtObjectState(823, -91, -44, team, pylonBase);
                                break;
                            case 6:
                                createVehicleAtObjectState(467, -75, -35, team, pylonBase);
                                createVehicleAtObjectState(457, -75, 50, team, pylonBase);
                                createVehicleAtObjectState(457, 75, 150, team, pylonBase);
                                createVehicleAtObjectState(466, -45, 50, team, pylonBase);
                                createVehicleAtObjectState(460, 75, -50, team, pylonBase);
                                createVehicleAtObjectState(823, 35, 80, team, pylonBase);
                                break;
                            case 7:
                                createVehicleAtObjectState(467, -107, -332, team, pylonBase);
                                createVehicleAtObjectState(457, -411, -332, team, pylonBase);
                                createVehicleAtObjectState(457, -155, 36, team, pylonBase);
                                createVehicleAtObjectState(466, 101, 36, team, pylonBase);
                                createVehicleAtObjectState(460, -155, -140, team, pylonBase);
                                createVehicleAtObjectState(823, -59, 52, team, pylonBase);
                                break;
                            case 8:
                                createVehicleAtObjectState(467, -80, -112, team, pylonBase);
                                createVehicleAtObjectState(457, -304, 64, team, pylonBase);
                                createVehicleAtObjectState(457, 320, -64, team, pylonBase);
                                createVehicleAtObjectState(466, -128, 48, team, pylonBase);
                                createVehicleAtObjectState(460, -176, -48, team, pylonBase);
                                createVehicleAtObjectState(823, 128, 48, team, pylonBase);
                                break;
                            case 9:
                                createVehicleAtObjectState(467, -144, 64, team, pylonBase);
                                createVehicleAtObjectState(457, -160, -208, team, pylonBase);
                                createVehicleAtObjectState(457, 80, -64, team, pylonBase);
                                createVehicleAtObjectState(466, 336, -16, team, pylonBase);
                                createVehicleAtObjectState(460, 176, 0, team, pylonBase);
                                createVehicleAtObjectState(823, 64, 128, team, pylonBase);
                                break;
                            case 10:
                                createVehicleAtObjectState(467, -96, -48, team, pylonBase);
                                createVehicleAtObjectState(457, -96, 176, team, pylonBase);
                                createVehicleAtObjectState(457, -96, 528, team, pylonBase);
                                createVehicleAtObjectState(466, 96, -50, team, pylonBase);
                                createVehicleAtObjectState(460, -96, 400, team, pylonBase);
                                createVehicleAtObjectState(823, 96, 112, team, pylonBase);
                                break;
                            case 11:
                                createVehicleAtObjectState(467, -96, -0, team, pylonBase);
                                createVehicleAtObjectState(457, 112, -128, team, pylonBase);
                                createVehicleAtObjectState(457, 112, 128, team, pylonBase);
                                createVehicleAtObjectState(466, -96, -176, team, pylonBase);
                                createVehicleAtObjectState(460, -48, -144, team, pylonBase);
                                createVehicleAtObjectState(823, 48, 144, team, pylonBase);
                                break;
                            case 12:
                                createVehicleAtObjectState(467, 336, -16, team, pylonBase);
                                createVehicleAtObjectState(457, 336, 224, team, pylonBase);
                                createVehicleAtObjectState(457, -48, -48, team, pylonBase);
                                createVehicleAtObjectState(466, -48, 80, team, pylonBase);
                                createVehicleAtObjectState(460, -80, 224, team, pylonBase);
                                createVehicleAtObjectState(823, 112, 224, team, pylonBase);
                                break;
                            case 13:
                                createVehicleAtObjectState(467, 112, 144, team, pylonBase);
                                createVehicleAtObjectState(457, 256, 112, team, pylonBase);
                                createVehicleAtObjectState(457, 256, -112, team, pylonBase);
                                createVehicleAtObjectState(466, -64, 0, team, pylonBase);
                                createVehicleAtObjectState(460, -20, 112, team, pylonBase);
                                createVehicleAtObjectState(823, -20, -112, team, pylonBase);
                                break;
                            case 14:
                                createVehicleAtObjectState(467, -384, 0, team, pylonBase);
                                createVehicleAtObjectState(457, -400, 144, team, pylonBase);
                                createVehicleAtObjectState(457, 304, 16, team, pylonBase);
                                createVehicleAtObjectState(466, 48, -80, team, pylonBase);
                                createVehicleAtObjectState(460, -272, -50, team, pylonBase);
                                createVehicleAtObjectState(823, 256, 144, team, pylonBase);
                                break;
                            case 15:
                                createVehicleAtObjectState(467, 117, 212, team, pylonBase);
                                createVehicleAtObjectState(457, 117, -60, team, pylonBase);
                                createVehicleAtObjectState(457, -219, 132, team, pylonBase);
                                createVehicleAtObjectState(466, -395, 196, team, pylonBase);
                                createVehicleAtObjectState(460, 69, 308, team, pylonBase);
                                createVehicleAtObjectState(823, -75, -76, team, pylonBase);
                                break;
                            case 16:
                                createVehicleAtObjectState(467, -96, 16, team, pylonBase);
                                createVehicleAtObjectState(457, 192, -240, team, pylonBase);
                                createVehicleAtObjectState(457, 192, -96, team, pylonBase);
                                createVehicleAtObjectState(466, 80, 64, team, pylonBase);
                                createVehicleAtObjectState(460, -64, 64, team, pylonBase);
                                createVehicleAtObjectState(823, 352, -160, team, pylonBase);
                                break;

                        }
                    #endregion

            pick.Value.setExists(false);

            if (!TeamHasLiveHqVehicle(team))
            {
                Log.write(TLog.Warning, "[Eol] Bot HQ: chassis 469 not live after base layout for {0} — skipping captain deploy.", team._name);
                return;
            }

            if (EolBaseBotsEnabled)
            {
                _tickDefenseCaptainRespawnEligible = 0;
                _tickLastDefenseCaptainSpawnAttempt = 0;
                TrySpawnDefenseCaptainIfNeeded(now);
            }
        }
        

        #region ChangeSectors

        public void preNewSector()
        {
            Log.write(TLog.Normal, "[Eol] Sector rotation: 90s warning (radiation wind change).");
            _arena.sendArenaMessage("&Radiation wind change — new sectors in 90 seconds. Get safe. You will be sent to Pioneer Station before the change. Bases and bots in old sectors will clear.", _config.flag.victoryWarningBong);
            _sectorChangeCountdownActive = true;
            _lastHudActivityTickerSent = null;
            _arena.setTicker(1, KothLobbyTickerIdx, 90 * 100, "New sectors in: ",
                delegate ()
                {
                    _sectorChangeCountdownActive = false;
                    newSectorDSRecall();
                }
            );
        }

        public void newSectorDSRecall()
        {
            Log.write(TLog.Normal, "[Eol] Sector rotation: recalling players to Pioneer Station.");
            int playersIngame = _arena != null && _arena.PlayersIngame != null ? _arena.PlayersIngame.Count() : 0;
            int playersTotal = _arena != null && _arena.Players != null ? _arena.Players.Count() : 0;
            int vehiclesTotal = _arena != null && _arena.Vehicles != null ? _arena.Vehicles.Count() : 0;
            Log.write(TLog.Normal, "[Eol] Sector rotation recall burst: playersIngame={0}, playersTotal={1}, vehicles={2}", playersIngame, playersTotal, vehiclesTotal);
            Helpers.ObjectState warpPoint;
            foreach (InfServer.Game.Player player in _arena.PlayersIngame)
            {
                warpPoint = findOpenWarp(player, _arena, 27008, 3360, 300);
                if (warpPoint == null)
                {
                    Log.write(TLog.Warning, "[Eol] Sector recall: no open warp for {0} (blocked).", player._alias);
                    player.sendMessage(-1, "There was an issue sending you back to Pioneer Station, please contact the dev team about this issue.");
                    continue;
                }
                warp(player, warpPoint);
            }
            foreach (InfServer.Game.Player player in _arena.Players)
            {
                player.clearProjectiles();
            }

            _bbetweengames = true;
            _eol.bbetweengames = true;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v._type.Type == VehInfo.Types.Computer)
                    EnqueueVehicleDespawn(v);
            }
            _usedpylons.Clear();
            DestroyAllTrackedScriptBots();
            ClearAllDefenseBaseVehicleLists();

            _minX = 0;
            _maxX = 0;
            _minY = 0;
            _maxY = 0;

            if (_eol != null)
            {
                _eol.RotateMatchSectors();
                _eol.RebuildBorderWallVehicles();
            }

            _bbetweengames = false;
            if (_eol != null)
                _eol.bbetweengames = false;
            if (EolBaseSetupEnabled)
                RebuildBotHqSlotTemplates();
            Log.write(TLog.Normal, "[Eol] Sector rotation complete; next rotation in 6 hours.");
        }

        #endregion

        #region AddingBots

        /// <summary>Linear scan for this team's HQ chassis — used by defense bots instead of per-poll LINQ over <see cref="Arena.Vehicles"/>.</summary>
        public Vehicle FindHqVehicleForTeam(Team team)
        {
            if (_arena == null || team == null)
                return null;
            int hqId = _hqVehId;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v._type == null || v.IsDead || v.bCondemned)
                    continue;
                if (IsVehicleDespawnQueued(v))
                    continue;
                if (v._type.Id == hqId && v._team == team)
                    return v;
            }
            return null;
        }

        /// <summary>True if <paramref name="team"/> has a live HQ chassis (<see cref="_hqVehId"/>) in the arena.</summary>
        public bool TeamHasLiveHqVehicle(Team team)
        {
            Vehicle v = FindHqVehicleForTeam(team);
            return v != null && !v.IsDead;
        }

        /// <summary>How many live bot HQs (469) currently exist on defense bot teams 1–3.</summary>
        int CountLiveDefenseBotHqs()
        {
            int n = 0;
            if (TeamHasLiveHqVehicle(botTeam1)) n++;
            if (TeamHasLiveHqVehicle(botTeam2)) n++;
            if (TeamHasLiveHqVehicle(botTeam3)) n++;
            return n;
        }

        /// <summary>
        /// Desired concurrent bot bases by population — mirrors sector tiers so a solo/low-pop
        /// match only gets one HQ (the redeploy fix must not fill all three teams).
        /// </summary>
        static int DesiredBotHqCountForPlayers(int playing)
        {
            if (playing < 30)
                return 1;
            if (playing < 46)
                return 2;
            return 3;
        }

        /// <summary>
        /// True if <paramref name="team"/> has a live HQ chassis (<see cref="_hqVehId"/>) OTHER than
        /// <paramref name="ignore"/>. Used during Vehicle.Creation, where the freshly created HQ is already in
        /// <see cref="Arena.Vehicles"/> and would otherwise match itself as a duplicate.
        /// </summary>
        public bool TeamHasOtherLiveHqVehicle(Team team, Vehicle ignore)
        {
            if (_arena == null || team == null)
                return false;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v == ignore || v._type == null || v.IsDead || v.bCondemned)
                    continue;
                if (IsVehicleDespawnQueued(v))
                    continue;
                if (v._type.Id == _hqVehId && v._team == team)
                    return true;
            }
            return false;
        }

        /// <summary>Match radius (raw units) between a live HQ chassis and a template anchor.</summary>
        const int BotHqAnchorNearRaw = 160;

        /// <summary>True if any live HQ chassis sits on/near this template anchor.</summary>
        bool IsLiveHqNearAnchor(short anchorX, short anchorY)
        {
            if (_arena == null)
                return false;
            long nearSq = (long)BotHqAnchorNearRaw * BotHqAnchorNearRaw;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v._type == null || v.IsDead || v._state == null)
                    continue;
                if (v._type.Id != _hqVehId)
                    continue;
                long dx = (long)v._state.positionX - anchorX;
                long dy = (long)v._state.positionY - anchorY;
                if (dx * dx + dy * dy <= nearSq)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Reopens consumed HQ anchor slots that are not currently occupied by a live HQ so
        /// destroyed bases can redeploy elsewhere. No-op while free candidates already exist,
        /// unless <paramref name="forceReopenConsumed"/> is set.
        /// </summary>
        void EnsureBotHqAnchorSlotsAvailable(bool forceReopenConsumed = false)
        {
            if (_usedpylons == null || _usedpylons.Count == 0)
                return;

            if (!forceReopenConsumed)
            {
                foreach (KeyValuePair<int, pylonObject> kv in _usedpylons)
                {
                    if (!kv.Value.bExists())
                        continue;
                    short px = kv.Value.getX();
                    short py = kv.Value.getY();
                    if (_eol != null && !_eol.IsInAnyActiveSector(px, py))
                        continue;
                    if (IsLiveHqNearAnchor(px, py))
                        continue;
                    return; // at least one free usable slot already
                }
            }

            int reopened = 0;
            foreach (KeyValuePair<int, pylonObject> kv in _usedpylons)
            {
                short px = kv.Value.getX();
                short py = kv.Value.getY();
                if (IsLiveHqNearAnchor(px, py))
                {
                    kv.Value.setExists(false);
                    continue;
                }
                if (!kv.Value.bExists())
                {
                    kv.Value.setExists(true);
                    reopened++;
                }
            }

            if (reopened > 0)
                Log.write(TLog.Normal, "[Eol] Reopened {0} bot HQ anchor slot(s) for redeploy (live HQs kept reserved).", reopened);
        }

        //Creates a turrent, offsets are from HQ
        public void createVehicle(int id, int x_offset, int y_offset, Team botTeam, Vehicle homeloc)
        {
            if (homeloc != null && botTeam != null)
            {
                VehInfo vehicle = _arena._server._assets.getVehicleByID(Convert.ToInt32(id));
                Helpers.ObjectState newState = new Protocol.Helpers.ObjectState();
                newState.positionX = Convert.ToInt16(homeloc._state.positionX + x_offset);
                newState.positionY = Convert.ToInt16(homeloc._state.positionY + y_offset);
                newState.positionZ = homeloc._state.positionZ;
                newState.yaw = (byte)homeloc._state.yaw;

                Vehicle placed = _arena.newVehicle(vehicle, botTeam, null, newState);
                RegisterDefenseBaseVehicle(botTeam, placed);
            }
        }


        private Vehicle createVehicleAtObjectState(int id, int x_offset, int y_offset, Team botTeam, Helpers.ObjectState homeloc)
        {
            if (homeloc == null || botTeam == null || _arena == null)
                return null;
            VehInfo vehicle = _arena._server._assets.getVehicleByID(Convert.ToInt32(id));
            if (vehicle == null)
                return null;
            Helpers.ObjectState newState = new Protocol.Helpers.ObjectState();
            newState.positionX = Convert.ToInt16(homeloc.positionX + x_offset);
            newState.positionY = Convert.ToInt16(homeloc.positionY + y_offset);
            newState.positionZ = homeloc.positionZ;
            newState.yaw = (byte)homeloc.yaw;
            Vehicle placed = _arena.newVehicle(vehicle, botTeam, null, newState);
            RegisterDefenseBaseVehicle(botTeam, placed);
            return placed;
        }

        /// <summary>
        /// Places an existing vehicle at map coordinates using the same core fields as hide / <c>Arena.newVehicle(..., state)</c>,
        /// then notifies the arena (<see cref="Vehicle.update"/>). For bots, also syncs <see cref="MovementController"/> so sim and clients agree.
        /// </summary>
        /// <param name="v">Typically an unoccupied bot, computer, or static wall vehicle — not a player-occupied chassis.</param>
        /// <param name="at">At least position; Z and yaw are kept from <paramref name="v"/> so open-tile picks need not fill them.</param>
        public void putVehicleAtSpawnLocation(Vehicle v, Helpers.ObjectState at)
        {
            if (v == null || at == null || v.IsDead || _arena == null)
                return;

            Helpers.ObjectState merged = new Helpers.ObjectState();
            merged.positionX = at.positionX;
            merged.positionY = at.positionY;
            merged.positionZ = v._state.positionZ;
            merged.yaw = v._state.yaw;

            v._state.positionX = merged.positionX;
            v._state.positionY = merged.positionY;
            v._state.positionZ = merged.positionZ;
            v._state.yaw = merged.yaw;
            v._state.velocityX = 0;
            v._state.velocityY = 0;
            v._state.velocityZ = 0;

            Bot bot = v as Bot;
            if (bot != null)
                bot._movement.warp(merged);

            v.update(false);
        }

        public void ClearEolBorderWallVehicles()
        {
            if (_eolBorderWallVehicles == null)
                return;
            foreach (Vehicle v in _eolBorderWallVehicles)
            {
                if (v != null && !v.IsDead)
                    v.destroy(true, true);
            }
            _eolBorderWallVehicles.Clear();
        }

        void SpawnOneEolBorderWall(VehInfo vi, short px, short py, short yaw)
        {
            if (vi == null || _eolBorderWallTeam == null)
                return;
            Helpers.ObjectState st = new Helpers.ObjectState();
            st.positionX = px;
            st.positionY = py;
            st.positionZ = 0;
            st.yaw = (byte)yaw;
            _arena.newVehicle(vi, _eolBorderWallTeam, null, st);
        }

        /// <summary>
        /// Spawns hide-style wall vehicles along one axis-aligned rectangle (append only; call <see cref="ClearEolBorderWallVehicles"/> first).
        /// </summary>
        public void SpawnEolBorderWallRing(short left, short top, short right, short bottom)
        {
            if (_eolBorderWallTeam == null || _arena == null)
                return;
            if (right <= left || bottom <= top)
                return;

            int r = EolBorderWallHideRadius;
            int stepH = Math.Max(1, EolBorderWallSpacingHorizontal);
            int stepV = Math.Max(1, EolBorderWallSpacingVertical);
            int hId = EolBorderWallVehicleHorizontal;
            int vId = EolBorderWallVehicleVertical;

            if (hId != 0)
            {
                VehInfo viH = _arena._server._assets.getVehicleByID(hId);
                if (viH != null)
                {
                    if (left + r <= right - r)
                    {
                        for (int xi = left + r; xi <= right - r; xi += stepH)
                            SpawnOneEolBorderWall(viH, (short)xi, top, EolBorderWallYawHorizontalTop);
                        for (int xi = left + r; xi <= right - r; xi += stepH)
                            SpawnOneEolBorderWall(viH, (short)xi, bottom, EolBorderWallYawHorizontalBottom);
                    }
                    else
                    {
                        short mx = (short)((left + right) / 2);
                        SpawnOneEolBorderWall(viH, mx, top, EolBorderWallYawHorizontalTop);
                        SpawnOneEolBorderWall(viH, mx, bottom, EolBorderWallYawHorizontalBottom);
                    }
                }
            }

            if (vId != 0)
            {
                VehInfo viV = _arena._server._assets.getVehicleByID(vId);
                if (viV != null)
                {
                    if (top + r <= bottom - r)
                    {
                        for (int yi = top + r; yi <= bottom - r; yi += stepV)
                            SpawnOneEolBorderWall(viV, left, (short)yi, EolBorderWallYawVerticalLeft);
                        for (int yi = top + r; yi <= bottom - r; yi += stepV)
                            SpawnOneEolBorderWall(viV, right, (short)yi, EolBorderWallYawVerticalRight);
                    }
                    else
                    {
                        short my = (short)((top + bottom) / 2);
                        SpawnOneEolBorderWall(viV, left, my, EolBorderWallYawVerticalLeft);
                        SpawnOneEolBorderWall(viV, right, my, EolBorderWallYawVerticalRight);
                    }
                }
            }
        }

        //Creates a turrent, offsets are from HQ
        public void createPDB(int id, int x_offset, int y_offset, Team botTeam, Helpers.ObjectState homeloc)
        {
            createVehicleAtObjectState(id, x_offset, y_offset, botTeam, homeloc);
        }

        //Creates a turrent, offsets are from HQ
        public void createSDU(int id, int x_offset, int y_offset, Team botTeam, Helpers.ObjectState homeloc)
        {
            createVehicleAtObjectState(id, x_offset, y_offset, botTeam, homeloc);
        }
        /// <summary>Previous poll had local clock hour inside any roam span — edge detection for arena messages and cleanup.</summary>
        bool _lastPollHourInRoamSpan;

        public void RoamSpawnCheck(int now)
        {
            currentHour = DateTime.Now.Hour;

            bool inAnySpan = false;
            for (int i = 0; i < timeSpans.Length; i++)
            {
                if (timeSpans[i].IsHourInRange(currentHour))
                {
                    inAnySpan = true;
                    break;
                }
            }

            if (inAnySpan)
            {
                if (!_lastPollHourInRoamSpan && now - _lastRoamAlert > 120 * 1000)
                {
                    _arena.sendArenaMessage("&Pirate raiders are roaming Eol, hunting player-held objectives.", 28);
                    _lastRoamAlert = now;
                }
                isRoamActive = true;
            }
            else
            {
                if (_lastPollHourInRoamSpan)
                {
                    IEnumerable<Vehicle> points = _arena.Vehicles.Where((Vehicle v) => v._type.Id == 468);
                    IEnumerable<Vehicle> pbb1 = _arena.Vehicles.Where((Vehicle v) => v._type.Id == 159);
                    IEnumerable<Vehicle> pbb2 = _arena.Vehicles.Where((Vehicle v) => v._type.Id == 160);
                    IEnumerable<Vehicle> pbb3 = _arena.Vehicles.Where((Vehicle v) => v._type.Id == 161);

                    if (points.Count() > 0)
                    {
                        foreach (Vehicle v in points) { EnqueueVehicleDespawn(v); }
                        foreach (Vehicle v1 in pbb1) { EnqueueVehicleDespawn(v1); }
                        foreach (Vehicle v2 in pbb2) { EnqueueVehicleDespawn(v2); }
                        foreach (Vehicle v3 in pbb3) { EnqueueVehicleDespawn(v3); }
                    }
                }
                isRoamActive = false;

                int nextStart = -1;
                for (int i = 0; i < timeSpans.Length; i++)
                {
                    if (currentHour < timeSpans[i].Start && (nextStart == -1 || timeSpans[i].Start < nextStart))
                        nextStart = timeSpans[i].Start;
                }
                if (nextStart == -1 && currentHour > timeSpans[timeSpans.Length - 1].End)
                    nextStart = timeSpans[0].Start;
                nextSpanStart = nextStart;
            }

            _lastPollHourInRoamSpan = inAnySpan;
            _lastPvpRoamHourAlert = now;
        }

        bool IsAnyRoamScheduledHour(int hour)
        {
            for (int i = 0; i < timeSpans.Length; i++)
            {
                if (timeSpans[i].IsHourInRange(hour))
                    return true;
            }
            return false;
        }

        /// <summary>Milliseconds from now until the next time local clock hour enters a pirate roam schedule window (top-of-hour).</summary>
        int GetMsUntilNextRoamHourStart()
        {
            DateTime n = DateTime.Now;
            if (IsAnyRoamScheduledHour(n.Hour))
                return 0;

            long best = long.MaxValue;
            for (int hi = 0; hi < 24; hi++)
            {
                if (!IsAnyRoamScheduledHour(hi))
                    continue;
                DateTime candidate = new DateTime(n.Year, n.Month, n.Day, hi, 0, 0);
                if (candidate <= n)
                    candidate = candidate.AddDays(1);
                double ms = (candidate - n).TotalMilliseconds;
                if (ms > 0 && ms < best)
                    best = (long)ms;
            }
            if (best == long.MaxValue)
                return 0;
            return (int)Math.Min(int.MaxValue, best);
        }

        /// <summary>While inside a roam hour window, time until the current span ends (start of first hour no longer in any span).</summary>
        int GetMsUntilCurrentRoamSpanEnd()
        {
            DateTime n = DateTime.Now;
            for (int i = 0; i < timeSpans.Length; i++)
            {
                TimeSpanRange span = timeSpans[i];
                if (!span.IsHourInRange(n.Hour))
                    continue;
                int endExclusiveHour = span.End + 1;
                DateTime end;
                if (endExclusiveHour < 24)
                    end = new DateTime(n.Year, n.Month, n.Day, endExclusiveHour, 0, 0);
                else
                    end = new DateTime(n.Year, n.Month, n.Day, 0, 0, 0).AddDays(1);
                double ms = (end - n).TotalMilliseconds;
                if (ms < 0)
                    return 0;
                return (int)Math.Min(int.MaxValue, ms);
            }
            return 0;
        }

        static string FormatEolHudCountdown(int ms)
        {
            if (ms <= 0)
                return "0s";
            int s = (ms + 999) / 1000;
            int m = s / 60;
            s %= 60;
            int h = m / 60;
            m %= 60;
            if (h > 0)
                return String.Format("{0}h {1}m {2}s", h, m, s);
            if (m > 0)
                return String.Format("{0}m {1}s", m, s);
            return String.Format("{0}s", s);
        }

        static string FormatEolHudCountdownCoarse(int ms)
        {
            if (ms <= 0)
                return "0s";
            if (ms >= 3600000)
            {
                int hours = ms / 3600000;
                int mins = ((ms % 3600000) + 59999) / 60000;
                if (mins >= 60)
                {
                    hours++;
                    mins = 0;
                }
                if (mins <= 0)
                    return String.Format("{0}h", hours);
                return String.Format("{0}h {1}m", hours, mins);
            }
            if (ms >= 60000)
            {
                int mins = (ms + 59999) / 60000;
                return String.Format("{0}m", mins);
            }
            int s = (ms + 999) / 1000;
            return String.Format("{0}s", s);
        }

        /// <summary>
        /// Permanent leave (no dropship revive) — tick the wave quota down for HUD (aliens handled in <see cref="FreeAlienWaveSlot"/>).
        /// </summary>
        void NoteWaveBotEliminatedForTicker(Bots.Bot dead)
        {
            if (dead == null)
                return;

            // Pirate grunts only (captain is not part of PirateRaidTotalSpawns).
            if (dead is RoamingAttacker && _pirateRaidWaveActive && dead._team == botTeam4)
            {
                if (_pirateRaidGruntsRemainingThisWave > 0)
                    _pirateRaidGruntsRemainingThisWave--;
            }
        }

        /// <summary>Live defense bots on the team list: <see cref="Captain"/> + <see cref="Engineer"/> + <see cref="BasicDefense"/> (includes dropship-pending).</summary>
        int GetEolBaseDefenseBotsAliveCount()
        {
            if (_bots == null)
                return 0;
            int n = 0;
            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b._team == null)
                    continue;
                if (!(b is Captain || b is Engineer || b is BasicDefense))
                    continue;
                if (!IsDefenseBotTeam(b._team))
                    continue;
                // Match namelist: alive or waiting at Pioneer for redeploy.
                if (b.IsDead && !b.bDropshipRespawnPending)
                    continue;
                n++;
            }
            return n;
        }

        /// <summary>True for defense combat units that consume the per-team bot cap.</summary>
        static bool IsDefenseCombatBot(Bots.Bot b)
        {
            return b is Captain || b is Engineer || b is BasicDefense;
        }

        /// <summary>Live defense combat bots on one team (Captain + Engineer + BasicDefense), including dropship-pending.</summary>
        public int CountLiveDefenseBotsForTeam(Team team)
        {
            if (team == null || _bots == null)
                return 0;
            int n = 0;
            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b._team != team || !IsDefenseCombatBot(b))
                    continue;
                if (b.IsDead && !b.bDropshipRespawnPending)
                    continue;
                n++;
            }
            return n;
        }

        /// <summary>Refresh <see cref="botCount"/> for a defense team from live combat bots.</summary>
        public void SyncDefenseBotCount(Team team)
        {
            if (team == null || botCount == null)
                return;
            int n = CountLiveDefenseBotsForTeam(team);
            if (botCount.ContainsKey(team))
                botCount[team] = n;
            else
                botCount.Add(team, n);
        }

        /// <summary>True if this defense team can spawn another combat bot under <see cref="_maxDefPerTeam"/>.</summary>
        public bool CanSpawnDefenseCombatBot(Team team)
        {
            if (team == null || !IsDefenseBotTeam(team))
                return false;
            return CountLiveDefenseBotsForTeam(team) < _maxDefPerTeam;
        }

        /// <summary>Legacy alias — now counts all defense combat bots on defense teams (not grunts only).</summary>
        public int GetEolBaseDefenseGruntAliveCount()
        {
            if (_bots == null)
                return 0;
            int n = 0;
            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b._team == null || !IsDefenseCombatBot(b))
                    continue;
                if (!IsDefenseBotTeam(b._team))
                    continue;
                if (b.IsDead && !b.bDropshipRespawnPending)
                    continue;
                n++;
            }
            return n;
        }

        void RebuildEolHudTickerCacheIfDue()
        {
            int now = Environment.TickCount;
            if (_cachedEolMatchTickerLine != null && now < _tickNextEolScheduleTickerBuild)
                return;

            _cachedEolMatchTickerLine = BuildEolMatchTickerLine();
            _cachedEolActivityTickerLine = BuildEolActivityTickerLine();

            int nextMs = EolScheduleTickerCacheMs;
            if (EolSideTimersActive())
            {
                if (_lastFlagReward != 0)
                {
                    int rem = 600000 - (now - _lastFlagReward);
                    if (rem > 60000)
                        nextMs = Math.Min(nextMs, EolScheduleTickerCoarseCacheMs);
                }
                if (_tickNextAlienWave > 0)
                {
                    int remA = _tickNextAlienWave - now;
                    if (remA > 60000)
                        nextMs = Math.Min(nextMs, EolScheduleTickerCoarseCacheMs);
                }
            }

            // Live threat / hunt / happy hour — refresh a bit sooner, but not every poll.
            if (_activeThreat != EolThreatMode.None || IsBountyHuntActive || IsEolHappyHourNow())
                nextMs = Math.Min(nextMs, HudTickerUpdateMs);

            if (nextMs <= 0)
                nextMs = EolScheduleTickerCacheMs;
            _tickNextEolScheduleTickerBuild = now + nextMs;
        }

        string BuildEolMatchTickerLine()
        {
            int now = Environment.TickCount;

            string kothPart;
            if (!EolKothSystemEnabled)
                kothPart = "KOTH: Off";
            else if (_kothGameRunning)
                kothPart = "KOTH: Active";
            else if (_tickKOTHGameStarting != 0)
            {
                long elapsed = now - _tickKOTHGameStarting;
                if (elapsed < 0)
                    elapsed = 0;
                long rem = 15L * 1000L - elapsed;
                if (rem < 0)
                    rem = 0;
                kothPart = rem > 0 ? "KOTH: Starting in " + FormatEolHudCountdownCoarse((int)rem) : "KOTH: Starting…";
            }
            else if (_tickNextKothEligible != 0 && now < _tickNextKothEligible)
            {
                int remK = _tickNextKothEligible - now;
                if (remK < 0)
                    remK = 0;
                kothPart = "KOTH: Cooldown " + FormatEolHudCountdownCoarse(remK);
            }
            else
                kothPart = "KOTH: Idle";

            string flagPart;
            if (_arena == null || !EolSideTimersActive() || _lastFlagReward == 0)
                flagPart = "Flag: --";
            else
            {
                int elapsed = now - _lastFlagReward;
                int rem = 600000 - elapsed;
                if (rem < 0)
                    rem = 0;
                flagPart = "Flag: " + FormatEolHudCountdownCoarse(rem);
            }

            string line = kothPart + " | " + flagPart;
            if (IsEolHappyHourNow())
                line = line + " | Happy Hour 2x";
            // Hunt / threat live on activity row (or compact onto this row only while KOTH owns row 2).
            return line;
        }

        /// <summary>Compact open-sector label for HUD, e.g. <c>Open: A+C</c>.</summary>
        string FormatEolOpenSectorsTickerPrefix()
        {
            if (_eol == null || _eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return "Open Sector(s): --";

            var letters = new List<string>(_eol._activeSectors.Count);
            foreach (string s in _eol._activeSectors)
            {
                if (string.IsNullOrEmpty(s))
                    continue;
                string t = s.Trim();
                // "Sector A" → "A"
                int sp = t.LastIndexOf(' ');
                string letter = sp >= 0 && sp + 1 < t.Length ? t.Substring(sp + 1) : t;
                if (letter.Length > 1)
                    letter = letter.Substring(0, 1);
                letters.Add(letter.ToUpperInvariant());
            }
            if (letters.Count == 0)
                return "Open Sector(s): --";
            return "Open Sector(s): " + string.Join("+", letters.ToArray());
        }

        /// <summary>Active bot threat summary for HUD — base always, plus rotating raiders/aliens, plus BHB.</summary>
        string BuildEolThreatTickerLine()
        {
            if (!EolSideTimersActive())
                return "Threat: --";

            string basePart;
            if (!EolBaseBotsEnabled)
                basePart = "Base: Off";
            else
            {
                int aliveB = GetEolBaseDefenseBotsAliveCount();
                int capB = _maxDefenseBots > 0 ? _maxDefenseBots : EolMaxConcurrentBaseDefenseBots;
                if (aliveB < 0)
                    aliveB = 0;
                if (aliveB > capB)
                    aliveB = capB;
                basePart = string.Format("Base: {0}/{1}", aliveB, capB);
            }

            string rotatePart = null;
            switch (_activeThreat)
            {
                case EolThreatMode.Raiders:
                    if (!EolPirateRaidsEnabled)
                        rotatePart = "Raiders: Off";
                    else if (_pirateRaidWaveActive)
                    {
                        int aliveR = (roamBots != null && roamBots.ContainsKey(botTeam4)) ? roamBots[botTeam4] : 0;
                        if (aliveR < 0)
                            aliveR = 0;
                        if (aliveR > PirateRaidMaxConcurrentAlive)
                            aliveR = PirateRaidMaxConcurrentAlive;
                        int leftR = _pirateRaidGruntsRemainingThisWave;
                        if (leftR < 0)
                            leftR = 0;
                        if (leftR > PirateRaidTotalSpawns)
                            leftR = PirateRaidTotalSpawns;
                        rotatePart = string.Format("Raiders: {0}/{1}", aliveR, leftR);
                    }
                    else if (PirateRaidHordeOperational || _currentRoamCaptains > 0 || _pollCacheAny468)
                        rotatePart = "Raiders: Active";
                    else
                        rotatePart = "Raiders: Mobilizing";
                    break;

                case EolThreatMode.Aliens:
                    if (!EolAlienWavesEnabled && !_alienWaveActive)
                        rotatePart = "Aliens: Off";
                    else if (_alienWaveActive)
                    {
                        int aliveA = _aliensAliveThisWave;
                        if (aliveA < 0)
                            aliveA = 0;
                        if (aliveA > AlienWaveMaxConcurrentAlive)
                            aliveA = AlienWaveMaxConcurrentAlive;
                        int leftA = _aliensRemainingThisWave;
                        if (leftA < 0)
                            leftA = 0;
                        if (leftA > AlienWaveTotalSpawns)
                            leftA = AlienWaveTotalSpawns;
                        rotatePart = string.Format("Aliens: {0}/{1}", aliveA, leftA);
                    }
                    else
                        rotatePart = "Aliens: Incoming";
                    break;

                default:
                    if (EolPirateRaidsEnabled || EolAlienWavesEnabled)
                        rotatePart = "Threat: Idle";
                    break;
            }

            string bhbPart = null;
            if (_bountyHunterWaveActive || IsBountyHuntActive)
            {
                int aliveH = GetBountyHunterAliveCount();
                if (aliveH < 0)
                    aliveH = 0;
                string markName = (_bountyHuntMarked != null) ? _bountyHuntMarked._alias : "?";
                bhbPart = string.Format("BHB: {0}/{1} → {2}",
                    aliveH, BountyHunterSquadSize, markName);
            }

            return JoinEolTickerParts(basePart, rotatePart, bhbPart);
        }

        string BuildEolActivityTickerLine()
        {
            string open = FormatEolOpenSectorsTickerPrefix();
            string threat = BuildEolThreatTickerLine();
            string hunt = IsBountyHuntActive ? BuildEolBountyHuntTickerLine() : null;
            return JoinEolTickerParts(open, threat, hunt);
        }

        /// <summary>Short bot/wave (+ hunt) summary for ticker row 0 while row 2 is used for crown timers.</summary>
        string BuildEolActivityTickerCompact()
        {
            string open = FormatEolOpenSectorsTickerPrefix();

            string basePart;
            if (!EolBaseBotsEnabled)
                basePart = "Base Off";
            else
            {
                int aliveB = GetEolBaseDefenseBotsAliveCount();
                int capB = _maxDefenseBots > 0 ? _maxDefenseBots : EolMaxConcurrentBaseDefenseBots;
                if (aliveB < 0)
                    aliveB = 0;
                basePart = string.Format("Base {0}/{1}", aliveB, capB);
            }

            string rotatePart = null;
            switch (_activeThreat)
            {
                case EolThreatMode.Raiders:
                    if (!EolPirateRaidsEnabled)
                        rotatePart = "Ra Off";
                    else if (_pirateRaidWaveActive || PirateRaidHordeOperational || _currentRoamCaptains > 0)
                    {
                        int aliveR = 0;
                        if (_pirateRaidWaveActive && roamBots != null && roamBots.ContainsKey(botTeam4))
                            aliveR = roamBots[botTeam4];
                        if (aliveR < 0)
                            aliveR = 0;
                        int leftR = _pirateRaidGruntsRemainingThisWave;
                        if (leftR < 0)
                            leftR = 0;
                        rotatePart = _pirateRaidWaveActive
                            ? string.Format("Ra {0}/{1}", aliveR, leftR)
                            : (aliveR > 0 ? "Ra " + aliveR : "Ra On");
                    }
                    else
                        rotatePart = "Ra ...";
                    break;
                case EolThreatMode.Aliens:
                    if (!EolAlienWavesEnabled && !_alienWaveActive)
                        rotatePart = "Ali Off";
                    else if (_alienWaveActive)
                    {
                        int aliveA = _aliensAliveThisWave;
                        if (aliveA < 0)
                            aliveA = 0;
                        int leftA = _aliensRemainingThisWave;
                        if (leftA < 0)
                            leftA = 0;
                        rotatePart = string.Format("Ali {0}/{1}", aliveA, leftA);
                    }
                    else
                        rotatePart = "Ali ...";
                    break;
            }

            string bhbPart = null;
            if (_bountyHunterWaveActive)
            {
                int aliveH = GetBountyHunterAliveCount();
                if (aliveH < 0)
                    aliveH = 0;
                bhbPart = "BHB " + aliveH;
            }

            string hunt = IsBountyHuntActive ? BuildEolBountyHuntTickerCompact() : null;
            return JoinEolTickerParts(open, basePart, rotatePart, bhbPart, hunt);
        }

        /// <summary>Join non-empty ticker segments with spaced pipes.</summary>
        static string JoinEolTickerParts(params string[] parts)
        {
            if (parts == null || parts.Length == 0)
                return "";
            System.Text.StringBuilder sb = null;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (string.IsNullOrEmpty(p))
                    continue;
                if (sb == null)
                {
                    sb = new System.Text.StringBuilder(p);
                    continue;
                }
                sb.Append(" | ");
                sb.Append(p);
            }
            return sb != null ? sb.ToString() : "";
        }

        bool EolActivityTickerRowAvailable()
        {
            return !_kothGameRunning
                && !_sectorChangeCountdownActive
                && _tickKOTHGameStarting == 0;
        }

        /// <summary>True while the raid can still spawn grunts from the script poll (captain no longer spawns grunts during raids).</summary>
        public bool CanSpawnPirateRaidGrunt()
        {
            if (!_pirateRaidWaveActive)
                return false;
            int alive = GetPirateRaidAliveGruntCount();
            return !_pirateRaidQuotaMet
                && _pirateRaidGruntsSpawnedThisWave < PirateRaidTotalSpawns
                && alive < PirateRaidMaxConcurrentAlive
                && (_tickPirateRaidInterBatchGateUntil == 0 || Environment.TickCount >= _tickPirateRaidInterBatchGateUntil);
        }

        int GetPirateRaidAliveGruntCount()
        {
            // Count live pirate combat bots (grunts + captain). Cap is per-team total, not grunts alone.
            int n = 0;
            int grunts = 0;
            if (_bots != null)
            {
                for (int i = 0; i < _bots.Count; i++)
                {
                    Bot b = _bots[i];
                    if (b == null || b._team != botTeam4)
                        continue;
                    if (!(b is RoamingAttacker || b is RoamingCaptain))
                        continue;
                    if (b.bCondemned)
                        continue;
                    if (b.IsDead && !b.bDropshipRespawnPending)
                        continue;
                    n++;
                    if (b is RoamingAttacker)
                        grunts++;
                }
            }
            if (roamBots != null && botTeam4 != null)
            {
                if (!roamBots.ContainsKey(botTeam4))
                    roamBots.Add(botTeam4, 0);
                roamBots[botTeam4] = grunts;
            }
            return n;
        }

        /// <summary>Live roam combat bots on a team (RoamingCaptain + RoamingAttacker).</summary>
        public int CountLiveRoamCombatBotsForTeam(Team team)
        {
            if (team == null || _bots == null)
                return 0;
            int n = 0;
            for (int i = 0; i < _bots.Count; i++)
            {
                Bot b = _bots[i];
                if (b == null || b._team != team)
                    continue;
                if (!(b is RoamingAttacker || b is RoamingCaptain))
                    continue;
                if (b.bCondemned)
                    continue;
                if (b.IsDead && !b.bDropshipRespawnPending)
                    continue;
                n++;
            }
            return n;
        }

        /// <summary>True if this roam team can spawn another attacker under <see cref="_maxRoamPerTeam"/>.</summary>
        public bool CanSpawnRoamCombatBot(Team team)
        {
            if (team == null)
                return false;
            return CountLiveRoamCombatBotsForTeam(team) < _maxRoamPerTeam;
        }

        void PollPirateRaidGruntSpawn(int now)
        {
            if (!_pirateRaidWaveActive || !RaiderPdbAnchorValid)
                return;

            EnforcePirateRaidConcurrentCap();

            if (_pirateRaidQuotaMet)
                return;

            if (_tickPirateRaidInterBatchGateUntil != 0 && now >= _tickPirateRaidInterBatchGateUntil)
                _tickPirateRaidInterBatchGateUntil = 0;

            if (now < _tickNextPirateGruntSpawn)
                return;
            if (GetPirateRaidAliveGruntCount() >= PirateRaidMaxConcurrentAlive)
                return;
            if (_tickPirateRaidInterBatchGateUntil != 0 && now < _tickPirateRaidInterBatchGateUntil)
                return;
            if (_pirateRaidGruntsSpawnedThisWave >= PirateRaidTotalSpawns)
                return;

            TrySpawnOnePirateRaidGrunt(now);
        }

        void TrySpawnOnePirateRaidGrunt(int now)
        {
            if (GetPirateRaidAliveGruntCount() >= PirateRaidMaxConcurrentAlive)
                return;

            short ax = RaiderPdbAnchorX;
            short ay = RaiderPdbAnchorY;
            RoamingCaptain liveCap = FindLiveRaiderCaptain();
            if (liveCap != null && !liveCap.IsDead)
            {
                ax = liveCap._state.positionX;
                ay = liveCap._state.positionY;
            }

            Helpers.ObjectState openPoint = FindOpenRaiderSpawnWithPeerGap(ax, ay);
            if (openPoint == null)
            {
                openPoint = findOpenWarpBot(_arena, ax, ay, 140);
                if (openPoint == null)
                {
                    _tickNextPirateGruntSpawn = now + 900;
                    return;
                }
            }

            int id = 144;
            Random roamtype = new Random();
            int _randomroam = roamtype.Next(1, 6);
            switch (_randomroam)
            {
                case 1: id = 144; break;
                case 2: id = 145; break;
                case 3: id = 146; break;
                case 4: id = 147; break;
                case 5: id = 157; break;
            }

            if (_bots == null)
                _bots = new List<Bots.Bot>();

            // Hard re-check after open-tile search — another poll may have filled the cap.
            if (GetPirateRaidAliveGruntCount() >= PirateRaidMaxConcurrentAlive)
                return;

            RoamingAttacker dBot = _arena.newBot(typeof(RoamingAttacker), (ushort)id, botTeam4, null, openPoint, this, null) as RoamingAttacker;
            if (dBot == null)
            {
                _tickNextPirateGruntSpawn = now + 900;
                return;
            }

            EnableBotDropshipRespawn(dBot, BotMaxDeathsRaider);
            if (!roamBots.ContainsKey(botTeam4))
                roamBots.Add(botTeam4, 0);
            if (!_bots.Contains(dBot))
                _bots.Add(dBot);
            roamBots[botTeam4] = CountLiveRoamCombatBotsForTeam(botTeam4);

            _pirateRaidGruntsSpawnedThisWave++;
            if ((_pirateRaidGruntsSpawnedThisWave == PirateRaidMaxConcurrentAlive || _pirateRaidGruntsSpawnedThisWave == PirateRaidMaxConcurrentAlive * 2)
                && _pirateRaidGruntsSpawnedThisWave < PirateRaidTotalSpawns)
            {
                if (_rand == null)
                    _rand = new Random(Environment.TickCount);
                _tickPirateRaidInterBatchGateUntil = now + _rand.Next(6500, 12001);
            }

            if (_pirateRaidGruntsSpawnedThisWave >= PirateRaidTotalSpawns)
                _pirateRaidQuotaMet = true;

            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            _tickNextPirateGruntSpawn = now + _rand.Next(PirateRaidGruntSpawnIntervalMinMs, PirateRaidGruntSpawnIntervalMaxMs + 1);
        }

        /// <summary>Find an open anchor tile in a random active sector (no PDB/468 spawn).</summary>
        bool TryFindOpenRaiderAnchorInActiveSectors(out Helpers.ObjectState anchor)
        {
            anchor = null;
            if (_eol == null || _arena == null)
                return false;
            _eol.EnsureActiveSectorsReadyForPortal();
            if (_eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return false;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            const int maxAttempts = 200;
            int[] warpRadii = { 120, 180, 240, 300 };

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                string sec = _eol._activeSectors[_rand.Next(_eol._activeSectors.Count)];
                short minX;
                short maxX;
                short minY;
                short maxY;
                if (!_eol.TryGetSectorBounds(sec, out minX, out maxX, out minY, out maxY))
                    continue;

                Helpers.ObjectState coarse = findOpenWarpInAxisAlignedRect(_arena, minX, maxX, minY, maxY, 24);
                if (coarse == null || !_eol.IsInAnyActiveSector(coarse.positionX, coarse.positionY))
                    continue;

                Helpers.ObjectState pdbBase = null;
                foreach (int rad in warpRadii)
                {
                    pdbBase = findOpenWarpBot(_arena, coarse.positionX, coarse.positionY, rad);
                    if (pdbBase != null)
                        break;
                }
                if (pdbBase == null)
                    continue;

                anchor = pdbBase;
                return true;
            }

            return false;
        }

        /// <summary>Open tile in the flag's sector, far enough that raiders must run to the objective instead of spawning on it.</summary>
        bool TryFindRaiderSpawnAwayFromFlag(short flagX, short flagY, out Helpers.ObjectState spawn)
        {
            spawn = null;
            if (_eol == null || _arena == null)
                return false;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            _eol.EnsureActiveSectorsReadyForPortal();

            string sector = null;
            short sMinX = 0;
            short sMaxX = 0;
            short sMinY = 0;
            short sMaxY = 0;
            if (_eol._activeSectors != null)
            {
                foreach (string s in _eol._activeSectors)
                {
                    short minX;
                    short maxX;
                    short minY;
                    short maxY;
                    if (!_eol.TryGetSectorBounds(s, out minX, out maxX, out minY, out maxY))
                        continue;
                    if (flagX >= minX && flagX <= maxX && flagY >= minY && flagY <= maxY)
                    {
                        sector = s;
                        sMinX = minX;
                        sMaxX = maxX;
                        sMinY = minY;
                        sMaxY = maxY;
                        break;
                    }
                }
            }

            long minSq = (long)RaiderSpawnMinDistFromFlag * RaiderSpawnMinDistFromFlag;
            long maxSq = (long)RaiderSpawnMaxDistFromFlag * RaiderSpawnMaxDistFromFlag;
            long arrivalSq = (long)RaiderCaptainObjectiveArrivalRaw * RaiderCaptainObjectiveArrivalRaw;
            int[] warpRadii = { 120, 180, 240, 300 };

            for (int attempt = 0; attempt < 220; attempt++)
            {
                short px;
                short py;
                if (sector != null)
                {
                    int spanX = (int)sMaxX - sMinX;
                    int spanY = (int)sMaxY - sMinY;
                    if (spanX < 0)
                        spanX = 0;
                    if (spanY < 0)
                        spanY = 0;
                    px = (short)(sMinX + _rand.Next(spanX + 1));
                    py = (short)(sMinY + _rand.Next(spanY + 1));
                }
                else
                {
                    px = flagX;
                    py = flagY;
                    Helpers.randomPositionInArea(_arena, RaiderSpawnMaxDistFromFlag, ref px, ref py);
                }

                if (_arena.getTile(px, py).Blocked)
                    continue;
                if (!_eol.IsInAnyActiveSector(px, py))
                    continue;

                long dx = (long)px - flagX;
                long dy = (long)py - flagY;
                long dsq = dx * dx + dy * dy;
                if (dsq < minSq || dsq > maxSq)
                    continue;

                Helpers.ObjectState warped = null;
                foreach (int rad in warpRadii)
                {
                    warped = findOpenWarpBot(_arena, px, py, rad);
                    if (warped != null)
                        break;
                }
                if (warped == null)
                    continue;
                if (!_eol.IsInAnyActiveSector(warped.positionX, warped.positionY))
                    continue;

                long wdx = (long)warped.positionX - flagX;
                long wdy = (long)warped.positionY - flagY;
                if (wdx * wdx + wdy * wdy <= arrivalSq)
                    continue;

                spawn = warped;
                return true;
            }

            Helpers.ObjectState fallback = findOpenWarpBot(_arena, flagX, flagY, 900);
            if (fallback != null && _eol.IsInAnyActiveSector(fallback.positionX, fallback.positionY))
            {
                spawn = fallback;
                return true;
            }
            return false;
        }

        /// <summary>True if <paramref name="t"/> may hold a flag we treat as a player objective for pirate raid routing.</summary>
        bool TeamIsRaidCircuitFlagOwner(Team t)
        {
            return t != null && !IsDefenseBotTeam(t) && !IsRoamBotTeam(t) && !IsAlienBotTeam(t);
        }

        bool FlagIsRaiderCircuitOwned(Arena.FlagState fs)
        {
            return fs != null && TeamIsRaidCircuitFlagOwner(fs.team);
        }

        bool FlagIsRaiderCircuitUnowned(Arena.FlagState fs)
        {
            return fs != null && fs.team == null;
        }

        /// <summary>Pirate grunt roam leg: prefer owned flags in open sectors, else random unowned flag there.</summary>
        public Helpers.ObjectState TryPickRaiderRaidRoamWaypoint(short fromX, short fromY, int minDistanceRaw, int circuitLastFlagX, int circuitLastFlagY, out bool pickedFromPlayerFlag, out short flagBaseX, out short flagBaseY)
        {
            pickedFromPlayerFlag = false;
            flagBaseX = 0;
            flagBaseY = 0;

            if (_arena == null || _arena._flags == null || _eol == null || !_pirateRaidWaveActive)
                return TryPickRaiderRoamWaypointFarFrom(fromX, fromY, minDistanceRaw);

            _eol.EnsureActiveSectorsReadyForPortal();

            List<Arena.FlagState> ownedCandidates = new List<Arena.FlagState>();
            List<Arena.FlagState> unownedCandidates = new List<Arena.FlagState>();
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs == null || !fs.bActive)
                    continue;
                if (!_eol.IsInAnyActiveSector(fs.posX, fs.posY))
                    continue;

                if (FlagIsRaiderCircuitOwned(fs))
                    ownedCandidates.Add(fs);
                else if (FlagIsRaiderCircuitUnowned(fs))
                    unownedCandidates.Add(fs);
            }

            List<Arena.FlagState> pickPool = ownedCandidates.Count > 0 ? ownedCandidates : unownedCandidates;
            if (pickPool.Count > 1 && circuitLastFlagX != int.MinValue)
            {
                List<Arena.FlagState> filtered = new List<Arena.FlagState>(pickPool);
                filtered.RemoveAll(f => f != null && f.posX == circuitLastFlagX && f.posY == circuitLastFlagY);
                if (filtered.Count > 0)
                    pickPool = filtered;
            }

            if (pickPool.Count > 0)
            {
                if (_rand == null)
                    _rand = new Random(Environment.TickCount);
                Arena.FlagState fs = pickPool[_rand.Next(pickPool.Count)];
                Helpers.ObjectState wp = findOpenWarpBot(_arena, fs.posX, fs.posY, 200);
                if (wp == null)
                    wp = findOpenWarpBot(_arena, fs.posX, fs.posY, 380);
                if (wp != null)
                {
                    RaiderPdbAnchorX = fs.posX;
                    RaiderPdbAnchorY = fs.posY;
                    RaiderPdbAnchorValid = true;
                    pickedFromPlayerFlag = ownedCandidates.Contains(fs);
                    flagBaseX = fs.posX;
                    flagBaseY = fs.posY;
                    return wp;
                }
            }

            return TryPickRaiderRoamWaypointFarFrom(fromX, fromY, minDistanceRaw);
        }

        /// <summary>
        /// Arms a pirate raid wave and raid wander anchor in a random active sector (no PDB / 468 vehicles).
        /// </summary>
        /// <param name="bypassRaidCooldown">Mod / debug: ignore <see cref="_tickNextPirateRaidAllowed"/>.</param>
        bool TryPlaceRaiderPdbHordeInActiveSectors(bool bypassRaidCooldown = false)
        {
            if (!EolPirateRaidsEnabled && !bypassRaidCooldown)
                return false;

            int tClock = Environment.TickCount;

            // Stuck alien wave must not permanently block raider threat rotation.
            if (IsAlienInvasionLive())
            {
                if (_activeThreat == EolThreatMode.Raiders)
                {
                    Log.write(TLog.Warning, "[Eol] Raider arm: clearing leftover alien wave so raiders can deploy.");
                    AbortAlienInvasionNow(tClock, armNextRandomWaveIfSystemEnabled: false);
                }
                else
                {
                    Log.write(TLog.Warning, "[Eol] Raider arm blocked — alien invasion still live.");
                    return false;
                }
            }

            if (!bypassRaidCooldown && (_tickNextPirateRaidAllowed == 0 || tClock < _tickNextPirateRaidAllowed))
                return false;

            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            RebuildRaiderSectorFlags();

            // Spawn randomly in open sector space, then captain's objective is a random flag.
            Helpers.ObjectState pdbBase;
            if (!TryFindOpenRaiderAnchorInActiveSectors(out pdbBase) || pdbBase == null)
            {
                // Fallback: open tile near any known sector flag.
                if (_raiderSectorFlags.Count > 0)
                {
                    RaiderSectorFlag near = _raiderSectorFlags[_rand.Next(_raiderSectorFlags.Count)];
                    pdbBase = findOpenWarpBot(_arena, near.X, near.Y, 400)
                        ?? findOpenWarpBot(_arena, near.X, near.Y, 900);
                }
            }
            if (pdbBase == null)
            {
                Log.write(TLog.Warning, "[Eol] Raider arm failed — no open anchor in active sectors (flags={0}).",
                    _raiderSectorFlags.Count);
                return false;
            }

            if (_raiderSectorFlags.Count > 0)
            {
                RaiderSectorFlag flagPick = _raiderSectorFlags[_rand.Next(_raiderSectorFlags.Count)];
                RaiderPdbAnchorX = pdbBase.positionX;
                RaiderPdbAnchorY = pdbBase.positionY;
                RaiderPdbAnchorValid = true;
                _pirateRaidWaveActive = true;
                SetRaiderCollectiveObjective(flagPick.X, flagPick.Y, tClock);
            }
            else
            {
                // No flags yet — still deploy; objective is the spawn anchor until flags appear.
                RaiderPdbAnchorX = pdbBase.positionX;
                RaiderPdbAnchorY = pdbBase.positionY;
                RaiderPdbAnchorValid = true;
                _pirateRaidWaveActive = true;
                SetRaiderCollectiveObjective(pdbBase.positionX, pdbBase.positionY, tClock);
                Log.write(TLog.Warning, "[Eol] Raider arm: no sector flags — using spawn anchor as objective.");
            }

            _arena.sendArenaMessage("&Pirate raiders are pushing into Eol. Up to " + PirateRaidTotalSpawns + " hostiles in waves of " + PirateRaidMaxConcurrentAlive + ".");
            _pirateRaidGruntsSpawnedThisWave = 0;
            _pirateRaidGruntsRemainingThisWave = PirateRaidTotalSpawns;
            _pirateRaidQuotaMet = false;
            _tickPirateRaidInterBatchGateUntil = 0;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            // Captain first so grunts have a lead target (avoids spawn-blob + march with no leader).
            TrySpawnRaiderCaptainImmediate(tClock);

            // Opening batch: fill concurrent cap (captain + grunts ≤ 20).
            EnforcePirateRaidConcurrentCap();
            for (int i = 0; i < PirateRaidMaxConcurrentAlive; i++)
            {
                if (GetPirateRaidAliveGruntCount() >= PirateRaidMaxConcurrentAlive)
                    break;
                if (_pirateRaidGruntsSpawnedThisWave >= PirateRaidTotalSpawns)
                    break;
                TrySpawnOnePirateRaidGrunt(tClock);
            }
            EnforcePirateRaidConcurrentCap();
            _tickNextPirateGruntSpawn = tClock + _rand.Next(PirateRaidGruntSpawnIntervalMinMs, PirateRaidGruntSpawnIntervalMaxMs + 1);
            InvalidateEolHudTickerCache();
            return true;
        }

        /// <summary>Spawn the pirate captain at the raid anchor if under the roam-captain cap.</summary>
        void TrySpawnRaiderCaptainImmediate(int now)
        {
            if (_arena == null || !PirateRaidHordeOperational)
                return;
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            if (_maxRoamCaptains < 1)
                _maxRoamCaptains = 1;

            // Always advance the poll timer so a missed find / blocked spawn cannot
            // re-enter every tick and spam captains / arena messages.
            _tickLastRoamingCaptain = now;

            // Sticky counter can desync when a captain dies dropship-pending (poll skips decrement)
            // or is destroyed without poll — always resync from live/pending before gating.
            SyncRaiderCaptainCountFromOccupying();
            if (_currentRoamCaptains >= _maxRoamCaptains)
                return;
            if (FindLiveRaiderCaptain() != null)
                return;
            if (CountOccupyingRaiderCaptains() > 0)
                return;

            // Room for captain under the team-wide 20 cap (cull a grunt if filled with attackers).
            EnforcePirateRaidConcurrentCap(reserveCaptainSlot: true);
            if (GetPirateRaidAliveGruntCount() >= PirateRaidMaxConcurrentAlive)
                return;

            Helpers.ObjectState openPoint = findOpenWarpBot(_arena, RaiderPdbAnchorX, RaiderPdbAnchorY, 180);
            if (openPoint == null)
                openPoint = findOpenWarpBot(_arena, RaiderCollectiveRoamX, RaiderCollectiveRoamY, 400);
            if (openPoint == null)
                return;

            // Final guard — another path may have spawned while we searched for a warp.
            SyncRaiderCaptainCountFromOccupying();
            if (CountOccupyingRaiderCaptains() > 0 || FindLiveRaiderCaptain() != null)
                return;

            Team team = botTeam4;
            RoamingCaptain Flagger = _arena.newBot(typeof(RoamingCaptain), (ushort)142, team, null, openPoint, this) as RoamingCaptain;
            if (Flagger == null)
                return;

            EnableBotDropshipRespawn(Flagger, BotMaxDeathsRaider);
            if (!_bots.Contains(Flagger))
                _bots.Add(Flagger);
            _arena.sendArenaMessage("$A rogue pirate captain has arrived in Eol, hunting player-held objectives.");
            SyncRaiderCaptainCountFromOccupying();
            if (!roamingCaptianBots.Contains(team))
                roamingCaptianBots.Add(team);
            if (!capRoamBots.ContainsKey(team))
                capRoamBots.Add(team, 0);
            if (roamBots.ContainsKey(team))
                roamBots[team] = CountLiveRoamCombatBotsForTeam(team);
            else
                roamBots.Add(team, 0);
        }

        /// <summary>
        /// Despawn excess pirate raiders so live+pending never exceeds <see cref="PirateRaidMaxConcurrentAlive"/>.
        /// Prefer culling grunts (not the captain). When <paramref name="reserveCaptainSlot"/>, leave one slot free.
        /// </summary>
        void EnforcePirateRaidConcurrentCap(bool reserveCaptainSlot = false)
        {
            if (botTeam4 == null || _bots == null)
                return;

            int target = PirateRaidMaxConcurrentAlive;
            if (reserveCaptainSlot && CountOccupyingRaiderCaptains() == 0)
                target = Math.Max(0, PirateRaidMaxConcurrentAlive - 1);

            int safety = 40;
            while (GetPirateRaidAliveGruntCount() > target && safety-- > 0)
            {
                RoamingAttacker cull = null;
                for (int i = 0; i < _bots.Count; i++)
                {
                    RoamingAttacker g = _bots[i] as RoamingAttacker;
                    if (g == null || g._team != botTeam4)
                        continue;
                    if (g.bCondemned && !g.bDropshipRespawnPending)
                        continue;
                    if (g.IsDead && !g.bDropshipRespawnPending)
                        continue;
                    cull = g;
                    break;
                }
                if (cull == null)
                    break;
                EnqueueVehicleDespawn(cull);
                if (_bots.Contains(cull))
                    _bots.Remove(cull);
            }

            if (roamBots != null && botTeam4 != null)
                roamBots[botTeam4] = CountLiveRoamCombatBotsForTeam(botTeam4);
        }

        void PollPirateRaidWaveEnded(int now)
        {
            if (!_pirateRaidWaveActive || !_pirateRaidQuotaMet)
                return;
            int aliveGrunts = 0;
            if (roamBots != null && roamBots.ContainsKey(botTeam4))
                aliveGrunts = roamBots[botTeam4];
            if (aliveGrunts > 0)
                return;
            EndPirateRaidWaveAfterGruntsCleared(now);
        }

        /// <summary>Force-clear pirate raid state, grunts/captain on <see cref="botTeam4"/>, legacy PDB/468 on <see cref="botTeam3"/>.</summary>
        void ModAbortPirateRaidWave(int now, bool sendFinishedLine, bool armCooldownForNextRaid)
        {
            if (_arena == null)
                return;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            if (armCooldownForNextRaid)
                _tickNextPirateRaidAllowed = now + _rand.Next(PirateRaidCooldownMinMs, PirateRaidCooldownMaxMs + 1);
            else
                _tickNextPirateRaidAllowed = 0;

            _pirateRaidWaveActive = false;
            _pirateRaidQuotaMet = false;
            _pirateRaidGruntsSpawnedThisWave = 0;
            _pirateRaidGruntsRemainingThisWave = 0;
            _tickNextPirateGruntSpawn = 0;
            _tickPirateRaidInterBatchGateUntil = 0;
            RaiderPdbAnchorValid = false;
            ClearRaiderCollectiveRoamPoint();

            foreach (Vehicle v in _arena.Vehicles.ToList())
            {
                if (v == null || v._type == null)
                    continue;
                int id = v._type.Id;
                if ((id == 468 || id == 159 || id == 160 || id == 161) && v._team == botTeam3)
                    EnqueueVehicleDespawn(v);
            }

            if (_bots != null)
            {
                foreach (Bots.Bot b in _bots.ToList())
                {
                    if (b == null || b._team != botTeam4)
                        continue;
                    if (b is RoamingCaptain || b is RoamingAttacker)
                        EnqueueVehicleDespawn(b);
                }
            }

            if (roamBots != null && roamBots.ContainsKey(botTeam4))
                roamBots[botTeam4] = 0;

            if (sendFinishedLine)
            {
                if (armCooldownForNextRaid && _tickNextPirateRaidAllowed != 0)
                {
                    int remMs = _tickNextPirateRaidAllowed - now;
                    if (remMs < 0)
                        remMs = 0;
                    _arena.sendArenaMessage("&Pirate raid wave finished. Next raid in " + FormatEolHudCountdownCoarse(remMs) + ".", 1);
                }
                else
                    _arena.sendArenaMessage("&Pirate raid ended.", 1);
            }

            Log.write(TLog.Normal, "[Eol] Pirate raid cleared (cooldown_armed={0}).", armCooldownForNextRaid);
        }

        /// <summary>All <see cref="PirateRaidTotalSpawns"/> grunts have been spawned and eliminated — rotate threat.</summary>
        void EndPirateRaidWaveAfterGruntsCleared(int now)
        {
            if (_activeThreat == EolThreatMode.Raiders)
            {
                CompleteThreatMode(EolThreatMode.Raiders);
                return;
            }
            ModAbortPirateRaidWave(now, true, true);
            Log.write(TLog.Normal, "[Eol] Pirate raid wave ended; next_allowed_tick={0} (~{1} min).", _tickNextPirateRaidAllowed, Math.Max(1, (_tickNextPirateRaidAllowed - now) / 60000));
        }

        public void spawnRoam(int now)
        {
            if (!EolSideTimersActive())
                return;

            if (!EolPirateRaidsEnabled || _activeThreat != EolThreatMode.Raiders)
            {
                if (_pirateRaidWaveActive && _activeThreat != EolThreatMode.Raiders)
                    ModAbortPirateRaidWave(now, false, false);
                else if (!EolPirateRaidsEnabled && _pirateRaidWaveActive)
                    ModAbortPirateRaidWave(now, false, false);
                return;
            }

            if (!_pirateRaidWaveActive)
            {
                _activehPoints.Clear();
                // Threat rotation arms raids immediately; retry if placement failed.
                if (now - _tickLastRaiderHordePlaceAttempt >= RaiderHordePlaceRetryMs)
                {
                    _tickLastRaiderHordePlaceAttempt = now;
                    if (!TryPlaceRaiderPdbHordeInActiveSectors(bypassRaidCooldown: true))
                        Log.write(TLog.Warning, "[Eol] Raider horde: could not arm raid anchor in active sectors (will retry).");
                }
            }

            if (now - _tickLastRoamingCaptain > _checkRoamingCaptain && PirateRaidHordeOperational)
                TrySpawnRaiderCaptainImmediate(now);

            PollPirateRaidGruntSpawn(now);
            PollRaiderFlagObjectiveAdvance(now);
            PollPirateRaidWaveEnded(now);
        }

        /// <summary>Mod *startraiders — forces roaming pirate window on and arms a raid + captain if none is active.</summary>
        public string TryModForceStartRaiders()
        {
            if (_arena == null || !_arena._bGameRunning || _bbetweengames)
                return "A match must be running.";
            if (_eol == null || !_eol.MatchSectorPlayReady)
                return "Sectors are not open yet — wait for the match layout.";
            if (!EolPirateRaidsEnabled)
                return "Pirate raids are disabled — use *enableraiders first.";
            if (_activeThreat == EolThreatMode.Raiders && PirateRaidHordeOperational)
                return "A pirate raid is already underway.";

            StartThreatMode(EolThreatMode.Raiders, announce: true);
            if (_activeThreat != EolThreatMode.Raiders || (!_pirateRaidWaveActive && !PirateRaidHordeOperational))
                return "Could not arm a pirate raid in active sectors — try again.";

            Log.write(TLog.Normal, "[Eol] Pirate raid armed (mod command); anchor=({0},{1}).", RaiderPdbAnchorX, RaiderPdbAnchorY);
            return null;
        }

        /// <summary>Mod *stopraiders — ends active raid, kills pirate roam bots, clears anchor.</summary>
        public string TryModStopRaiders()
        {
            if (_arena == null)
                return "Arena not ready.";
            if (_activeThreat == EolThreatMode.Raiders)
            {
                CompleteThreatMode(EolThreatMode.Raiders);
                return null;
            }
            int now = Environment.TickCount;
            ModAbortPirateRaidWave(now, true, false);
            return null;
        }

        /// <summary>Mod *disableraiders — blocks automatic pirate raids and *startraiders; clears any active raid.</summary>
        public string TryModDisablePirateRaids()
        {
            // Disable first so CompleteThreatMode cannot re-pick Raiders.
            EolPirateRaidsEnabled = false;
            if (_activeThreat == EolThreatMode.Raiders)
                CompleteThreatMode(EolThreatMode.Raiders);
            int now = Environment.TickCount;
            if (_arena != null && _pirateRaidWaveActive)
                ModAbortPirateRaidWave(now, false, false);
            return null;
        }

        /// <summary>Mod *enableraiders — re-enables raiders for threat rotation (does not force-start).</summary>
        public string TryModEnablePirateRaids()
        {
            EolPirateRaidsEnabled = true;
            if (_arena != null && _arena._bGameRunning && !_bbetweengames && _eol != null
                && _eol.MatchSectorPlayReady && _activeThreat == EolThreatMode.None)
                BeginInitialThreatRotation();
            return null;
        }

        /// <summary>Kills defense <see cref="Captain"/>, <see cref="BasicDefense"/>, and <see cref="Engineer"/> bots on <see cref="IsDefenseBotTeam"/> teams; clears spawn tracking so a new captain can deploy when re-enabled.</summary>
        void ModClearBaseDefenseBots()
        {
            if (_bots != null)
            {
                foreach (Bots.Bot b in _bots.ToList())
                {
                    if (b == null || b._team == null)
                        continue;
                    if (!IsDefenseBotTeam(b._team))
                        continue;
                    if (b is Captain || b is BasicDefense || b is Engineer)
                        EnqueueVehicleDespawn(b);
                }
            }

            if (captainBots != null)
            {
                List<Team> capKeys = captainBots.Keys.ToList();
                foreach (Team t in capKeys)
                {
                    if (t != null && IsDefenseBotTeam(t))
                        captainBots.Remove(t);
                }
            }

            if (botCount != null)
            {
                List<Team> bcKeys = botCount.Keys.ToList();
                foreach (Team t in bcKeys)
                {
                    if (t != null && IsDefenseBotTeam(t))
                        botCount[t] = 0;
                }
            }

            if (engineerBots != null)
                engineerBots.Clear();
            _currentEngineers = 0;

            _tickDefenseCaptainRespawnEligible = 0;
            _tickLastDefenseCaptainSpawnAttempt = 0;
            Log.write(TLog.Normal, "[Eol] Base defense bots cleared (captain + grunts + engineer).");
        }

        /// <summary>Mod *stopbasebots — removes active defense captains and BasicDefense bots without changing enable flags.</summary>
        public string TryModStopBaseBots()
        {
            if (_arena == null)
                return "Arena not ready.";
            ModClearBaseDefenseBots();
            TeardownAllDefenseBases(Environment.TickCount);
            _arena.sendArenaMessage("&Base defense bots cleared by a moderator.", 1);
            return null;
        }

        /// <summary>Mod *disablebasebots — stops spawning and clears any active defense captain / grunt bots.</summary>
        public string TryModDisableBaseBots()
        {
            EolBaseBotsEnabled = false;
            if (_arena != null)
            {
                ModClearBaseDefenseBots();
                TeardownAllDefenseBases(Environment.TickCount);
            }
            return null;
        }

        /// <summary>Mod *enablebasebots — base bots spawn continuously while enabled.</summary>
        public string TryModEnableBaseBots()
        {
            EolBaseBotsEnabled = true;
            _tickLastEngineer = 0;
            _tickDefenseCaptainRespawnEligible = 0;
            _tickLastDefenseCaptainSpawnAttempt = 0;
            return null;
        }

        /// <summary>Mod *disablebasesetup — stops automatic bot HQ placement from sector templates (does not remove existing HQs).</summary>
        public string TryModDisableBaseSetup()
        {
            EolBaseSetupEnabled = false;
            return null;
        }

        /// <summary>Mod *enablebasesetup — allows automatic bot HQ placement from templates again.</summary>
        public string TryModEnableBaseSetup()
        {
            EolBaseSetupEnabled = true;
            if (_arena != null && _arena._bGameRunning && !_bbetweengames && _eol != null
                && _eol.MatchSectorPlayReady && _activeThreat == EolThreatMode.None)
                BeginInitialThreatRotation();
            return null;
        }

        public void addBot(InfServer.Game.Player owner, Helpers.ObjectState state, Team team)
        {
            if (_bots == null)
                _bots = new List<Bots.Bot>();

            int id = 140;
            Helpers.ObjectState openPoint = new Helpers.ObjectState();

            openPoint = findOpenWarpBot(_arena, state.positionX, state.positionY, 150);

            Random coolbots = new Random();
            int _randombot = coolbots.Next(1, 5);

            switch (_randombot)
            {
                case 1:
                    id = 140;
                    break;
                case 2:
                    id = 141;
                    break;
                case 3:
                    id = 143;
                    break;
                case 4:
                    id = 156;
                    break;
                case 5:
                    id = 158;
                    break;
            }
            if (owner == null)
            {//This is a bot team
             //Spawn a random bot in their faction
                if (IsDefenseBotTeam(team) && (!EolBaseBotsEnabled || !TeamHasLiveHqVehicle(team)))
                    return;
                // Cap is per-team and includes captain + engineer + grunts.
                if (IsDefenseBotTeam(team) && !CanSpawnDefenseCombatBot(team))
                    return;
                if (openPoint == null)
                    return;
                BasicDefense dBot = _arena.newBot(typeof(BasicDefense), (ushort)id, team, null, openPoint, this, null) as BasicDefense;
                if (dBot == null)
                    return;
                EnableBotDropshipRespawn(dBot, BotMaxDeathsBase);
                _bots.Add(dBot);
                SyncDefenseBotCount(team);
            }
        }

        public void addBotRoam(InfServer.Game.Player owner, Helpers.ObjectState state, Team team)
        {
            if (_bots == null)
                _bots = new List<Bots.Bot>();

            if (owner == null && team == botTeam4 && _pirateRaidWaveActive)
                return;

            if (owner == null && team == botTeam4 && GetPirateRaidAliveGruntCount() >= PirateRaidMaxConcurrentAlive)
                return;

            if (owner == null && !CanSpawnRoamCombatBot(team))
                return;

            Helpers.ObjectState openPoint = findOpenWarpBot(_arena, state.positionX, state.positionY, 150);
            if (openPoint == null)
                return;

            int id = 144;
            Random roamtype = new Random();
            int _randomroam = roamtype.Next(1, 6);

            switch (_randomroam)
            {
                case 1:
                    id = 144;
                    break;
                case 2:
                    id = 145;
                    break;
                case 3:
                    id = 146;
                    break;
                case 4:
                    id = 147;
                    break;
                case 5:
                    id = 157;
                    break;

            }
            if (owner == null)
            {
                RoamingAttacker dBot = _arena.newBot(typeof(RoamingAttacker), (ushort)id, team, null, openPoint, this, null) as RoamingAttacker;
                if (dBot == null)
                    return;

                EnableBotDropshipRespawn(dBot, BotMaxDeathsRaider);
                if (!roamBots.ContainsKey(team))
                    roamBots.Add(team, 0);
                roamBots[team]++;

                _bots.Add(dBot);
            }
        }
        #endregion

        #region Pylons
        private Dictionary<int, pylonObject> GetPylonsTemplateForSectorName(string sec)
        {
            if (sec == _eol.sectorA) return _pylonsA;
            if (sec == _eol.sectorB) return _pylonsB;
            if (sec == _eol.sectorC) return _pylonsC;
            if (sec == _eol.sectorD) return _pylonsD;
            return null;
        }

        string CurrentBotHqAnchorSectorKey()
        {
            if (_eol == null || _eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return "";
            List<string> names = new List<string>(_eol._activeSectors);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", names);
        }

        /// <summary>Rebuild HQ anchors when empty or when the open-sector set no longer matches the cached slots.</summary>
        void RebuildBotHqSlotTemplatesIfNeeded()
        {
            string key = CurrentBotHqAnchorSectorKey();
            if (_usedpylons != null && _usedpylons.Count > 0
                && string.Equals(_botHqAnchorSectorKey, key, StringComparison.Ordinal))
                return;
            RelocateDefenseBasesForActiveSectors();
        }

        /// <summary>
        /// Rebuilds bot HQ anchor slot list from <see cref="EolBoundaries._activeSectors"/> (hardcoded template coords only).
        /// HQ and turrets are placed by <see cref="PollEngineerSpawning"/>; no placeholder vehicles.
        /// </summary>
        public void RebuildBotHqSlotTemplates()
        {
            if (!EolBaseSetupEnabled || _arena == null || _eol == null)
                return;
            _eol.EnsureActiveSectorsReadyForPortal();
            if (_eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return;

            _currentSector1 = _eol.sectUnder30;
            _currentSector2 = _eol.sectUnder60;

            _usedpylons = new Dictionary<int, pylonObject>();
            int idx = 0;
            foreach (string sec in _eol._activeSectors)
            {
                Dictionary<int, pylonObject> tpl = GetPylonsTemplateForSectorName(sec);
                if (tpl == null)
                    continue;
                foreach (KeyValuePair<int, pylonObject> kv in tpl.OrderBy((KeyValuePair<int, pylonObject> k) => k.Key))
                {
                    pylonObject proto = kv.Value;
                    _usedpylons.Add(idx, new pylonObject(proto.getX(), proto.getY()));
                    idx++;
                }
            }
            _botHqAnchorSectorKey = CurrentBotHqAnchorSectorKey();
        }

        /// <summary>
        /// After a sector open/close, drop bot HQs that sit in closed sectors and rebuild
        /// HQ anchors from the current open set so captains/grunts spawn in the right place.
        /// </summary>
        public void RelocateDefenseBasesForActiveSectors()
        {
            if (!EolBaseSetupEnabled || _arena == null || _eol == null)
                return;

            int removed = 0;
            List<Vehicle> staleHqs = new List<Vehicle>();
            foreach (Vehicle hq in GetLiveHqVehiclesSnapshot())
            {
                if (hq == null || hq._state == null || !IsDefenseBotTeam(hq._team))
                    continue;
                if (_eol.IsInAnyActiveSector(hq._state.positionX, hq._state.positionY))
                    continue;
                staleHqs.Add(hq);
            }

            foreach (Vehicle hq in staleHqs)
            {
                EnqueueVehicleDespawn(hq);
                removed++;
            }

            RebuildBotHqSlotTemplates();
            EnsureBotHqAnchorSlotsAvailable(true);

            _tickLastEngineer = 0;
            _tickDefenseCaptainRespawnEligible = 0;
            _tickLastDefenseCaptainSpawnAttempt = 0;

            Log.write(TLog.Normal,
                "[Eol] Sector change: removed {0} off-sector bot HQ(s); rebuilt {1} HQ anchor slot(s).",
                removed, _usedpylons != null ? _usedpylons.Count : 0);
        }
        #endregion

        #region KOTH
        /// <summary>
        /// Called when KOTH game has ended
        /// </summary>
        public void endKOTH()
        {
            QueueKothArenaMessage("KOTH has ended");

            List<InfServer.Game.Player> hadCrownSnapshot = _activeCrowns;
            QueueKothRoundEndClientUpdates(hadCrownSnapshot);

            _tickKothGameStart = 0;
            _tickKOTHGameStarting = 0;
            _victoryKothTeam = null;
            _crownTeams = null;

            _kothGameRunning = false;
            if (EolKothSystemEnabled)
                ScheduleKothCooldown(Environment.TickCount);
            else
                _tickNextKothEligible = 0;
            Log.write(TLog.Normal, "[Eol] KOTH ended; random cooldown before next lobby.");

            _playerCrownStatus.Clear();
            _lastKothCrownTickerSec.Clear();
        }

        /// <summary>
        /// Called when KOTH game has been restarted
        /// </summary>
        public void resetKOTH()
        {//Game reset, perhaps start a new one
            _tickKothGameStart = 0;
            _tickKOTHGameStarting = 0;
            Log.write(TLog.Normal, "[Eol] KOTH state reset.");

            _victoryKothTeam = null;

            List<InfServer.Game.Player> hadCrownSnapshot = _activeCrowns;
            QueueKothRoundEndClientUpdates(hadCrownSnapshot);

            _playerCrownStatus.Clear();
            _lastKothCrownTickerSec.Clear();
        }

        /// <summary>
        /// Called when KOTH game has started
        /// </summary>
        public void startKOTH(bool modForced = false)
        {
            if (!EolKothSystemEnabled && !modForced)
                return;

            //We've started!
            _arena.setTicker(1, KothLobbyTickerIdx, 0, "");
            _lastHudActivityTickerSent = null;
            _tickKothGameStart = Environment.TickCount;
            _tickKOTHGameStarting = 0;
            _tickNextKothEligible = 0;
            _victoryKothTeam = null;
            _playerCrownStatus.Clear();
            _kothGameRunning = true;

            //Let everyone know
            _arena.sendArenaMessage("A new KOTH Game has started!", 1);

            _crownTeams = new List<Team>();
            _playerCrownStatus = new Dictionary<InfServer.Game.Player, PlayerCrownStatus>();
            List<InfServer.Game.Player> crownPlayers = (_config.king.giveSpecsCrowns ? _arena.Players : _arena.PlayersIngame).ToList();

            foreach (var p in crownPlayers)
            {
                _playerCrownStatus[p] = new PlayerCrownStatus();
                giveCrown(p, false);
            }
            // One paced arena-wide crown packet — giveCrown used to broadcast per player (N² reliable).
            QueueKothCrownBroadcast(true, crownPlayers);
            Log.write(TLog.Normal, "[Eol] KOTH started ({0} players with crowns).", crownPlayers.Count);
        }

        /// <summary>Mod *startkoth — starts KOTH immediately (requires system enabled).</summary>
        public string TryModStartKoth()
        {
            if (_arena == null || !_arena._bGameRunning || _bbetweengames)
                return "A match must be running.";
            if (!EolKothSystemEnabled)
                return "KOTH is disabled — use *enablekoth first.";
            if (_kothGameRunning)
                return "KOTH is already running.";
            if (_sectorChangeCountdownActive)
                return "Sector change in progress — try again shortly.";
            if (_arena.PlayerCount < 5)
                return "Need at least 5 players in the arena for KOTH.";
            startKOTH(modForced: true);
            return null;
        }

        /// <summary>Mod *endkoth — ends active KOTH or cancels the lobby countdown.</summary>
        public string TryModEndKoth()
        {
            if (_arena == null)
                return "Arena not ready.";
            if (_kothGameRunning)
                endKOTH();
            else
            {
                bool hadLobby = _tickKOTHGameStarting != 0;
                _tickKOTHGameStarting = 0;
                _tickNextKothEligible = 0;
                _arena.setTicker(1, KothLobbyTickerIdx, 0, "");
                _lastHudActivityTickerSent = null;
                PushEolActivityTicker(force: true);
                if (hadLobby)
                    _arena.sendArenaMessage("&KOTH countdown cancelled by a moderator.", 1);
            }
            return null;
        }

        /// <summary>Mod *disablekoth — turns off KOTH auto-lobby and *startkoth; ends any active round.</summary>
        public string TryModDisableKoth()
        {
            EolKothSystemEnabled = false;
            if (_arena == null)
                return null;
            if (_kothGameRunning)
                endKOTH();
            else
            {
                _tickKOTHGameStarting = 0;
                _tickNextKothEligible = 0;
                _arena.setTicker(1, KothLobbyTickerIdx, 0, "");
                _lastHudActivityTickerSent = null;
                PushEolActivityTicker(force: true);
            }
            return null;
        }

        /// <summary>Mod *enablekoth — re-enables the KOTH system (auto lobby resumes when population allows).</summary>
        public string TryModEnableKoth()
        {
            EolKothSystemEnabled = true;
            _tickLastKothLobbyPoll = 0;
            return null;
        }

        /// <summary>
        /// Called when the specified team have won
        /// </summary>
        /// <summary>
        /// Awards the KOTH winning team and ends the round.
        /// </summary>
        public void kothVictory(Team victors)
        {
            if (victors == null)
            {
                endKOTH();
                return;
            }

            // Mark victory immediately so a concurrent poll cannot race into "no winner".
            _victoryKothTeam = victors;

            int rewardedPlayers = 0;
            foreach (InfServer.Game.Player p in victors.AllPlayers)
            {
                if (p == null || p.IsSpectator)
                    continue;

                int cashReward = _config.king.cashReward * _arena.PlayerCount;
                int experienceReward = _config.king.experienceReward * _arena.PlayerCount;
                int pointReward = _config.king.pointReward * _arena.PlayerCount;

                p.Cash += cashReward;
                p.Experience += experienceReward;
                p.BonusPoints += pointReward;
                QueueKothVictoryPlayerNotify(p,
                    String.Format("Your Personal Reward: Points={0} Cash={1} Experience={2}", pointReward, cashReward, experienceReward));
                rewardedPlayers++;
            }

            Log.write(TLog.Normal, "[Eol] KOTH victory: {0} ({1} players rewarded).", victors._name, rewardedPlayers);
            endKOTH();
        }


        /// <summary>
        /// Updates HUD tickers (match row 0, activity row 2 when free, crown timers per-player on row 2 during KOTH).
        /// </summary>
        public void updateTickers()
        {
            RefreshKothCrownTimerTickers();

            RebuildEolHudTickerCacheIfDue();

            // Hunt pot counters change on trickle; always rebuild that segment live.
            if (IsBountyHuntActive)
                _cachedEolActivityTickerLine = BuildEolActivityTickerLine();

            string match = _cachedEolMatchTickerLine ?? "";
            if (_kothGameRunning)
            {
                string compact = BuildEolActivityTickerCompact();
                if (!string.IsNullOrEmpty(compact))
                    match = JoinEolTickerParts(match, compact);
            }

            // timer=0 rows: re-push every HUD update so they stay visible without a countdown clock.
            _lastHudMatchTickerSent = match;
            _arena.setTicker(0, EolMatchScheduleTickerIdx, EolStaticTickerDuration, match);

            if (EolActivityTickerRowAvailable())
                PushEolActivityTicker(force: true);
        }

        /// <summary>Per-player crown countdown on <see cref="KothCrownTimerTickerIdx"/> — only when the displayed second changes.</summary>
        void RefreshKothCrownTimerTickers()
        {
            if (!_kothGameRunning || _playerCrownStatus == null)
                return;

            int now = Environment.TickCount;
            foreach (KeyValuePair<InfServer.Game.Player, PlayerCrownStatus> kv in _playerCrownStatus)
            {
                InfServer.Game.Player p = kv.Key;
                if (p == null)
                    continue;
                int sec = (kv.Value != null && kv.Value.crown)
                    ? Math.Max(0, (kv.Value.expireTime - now) / 1000)
                    : -1;
                int last;
                if (_lastKothCrownTickerSec.TryGetValue(p, out last) && last == sec)
                    continue;
                _lastKothCrownTickerSec[p] = sec;
                SendPlayerTicker(p, KothCrownTimerTickerIdx, 1, sec >= 0 ? "Crown: " + sec : "");
            }
        }

        static void SendPlayerTicker(InfServer.Game.Player player, int idx, byte colour, string text)
        {
            if (player == null || player._client == null)
                return;
            var msg = new SC_ArenaMessage();
            msg.colour = colour;
            msg.tickerMessage = "*" + idx + (text ?? "");
            msg.timer = 0;
            player._client.sendReliable(msg);
        }

        /// <summary>
        /// Local crown packet routing (broadcast and single-client) using <see cref="InfServer.Game.Player"/> only,
        /// avoiding <c>Helpers.Player_Crowns</c> overload / list-type mismatches in script compilation.
        /// Arena-wide updates should prefer <see cref="QueueKothCrownBroadcast"/> for pacing.
        /// </summary>
        static void RoutePlayerCrownsToClient(Arena arena, bool bCrown, List<InfServer.Game.Player> players, InfServer.Game.Player target)
        {
            if (arena == null || players == null || players.Count == 0)
                return;
            var pkt = new SC_PlayerCrowns();
            pkt.bCrown = bCrown;
            pkt.players = players.Select((InfServer.Game.Player p) => (short)p._id).ToList();
            if (target == null)
            {
                foreach (InfServer.Game.Player p in arena.Players)
                    p._client.sendReliable(pkt);
            }
            else
                target._client.sendReliable(pkt);
        }

        /// <summary>Immediate arena-wide crown blast — prefer <see cref="QueueKothCrownBroadcast"/> for live play.</summary>
        static void RoutePlayerCrownsBroadcast(Arena arena, bool bCrown, List<InfServer.Game.Player> players)
        {
            RoutePlayerCrownsToClient(arena, bCrown, players, null);
        }

        static void RouteVehicleDeathToPlayers(IEnumerable<InfServer.Game.Player> recipients, InfServer.Game.Player killer, Vehicle dead, InfServer.Game.Player occupier)
        {
            var not = new SC_VehicleDeath();
            not.type = Helpers.KillType.Player;
            not.playerID = (occupier == null) ? (ushort)0 : occupier._id;
            not.vehicleID = dead._id;
            not.killerID = (killer == null) ? 0 : (uint)killer._id;
            not.positionX = dead._state.positionX;
            not.positionY = dead._state.positionY;
            not.yaw = (byte)dead._state.yaw;
            foreach (InfServer.Game.Player pl in recipients)
                pl._client.sendReliable(not);
        }

        /// <summary>
        /// Gives a crown to the specified player
        /// </summary>
        public void giveCrown(InfServer.Game.Player p, bool broadcast = true)
        {//Give the player a crown; startKOTH batches the arena packet.
            var v = _playerCrownStatus[p];
            v.crown = true;
            v.crownDeaths = 0;
            v.crownKills = 0;
            updateCrownTime(p);
            if (broadcast)
                QueueKothCrownBroadcast(true, _activeCrowns);
        }

        /// <summary>
        /// Updates the crown time for the specified player
        /// </summary>
        public void updateCrownTime(InfServer.Game.Player p)
        {   //Reset the player's counter
            _playerCrownStatus[p].expireTime = Environment.TickCount + (_config.king.expireTime * 1000);
        }

        #endregion

        #region CTF

        /// <summary>
        /// Called when a flag changes team
        /// </summary>
        public void onFlagChange(Arena.FlagState flag)
        {   //Does this team now have all the flags?
            if (flag == null || flag.team == null || !TeamQualifiesForFlagRewards(flag.team))
                return;

            Team victoryTeam = flag.team;


            foreach (Arena.FlagState fs in _arena._flags.Values)
                if (fs.bActive && fs.team != victoryTeam)
                    victoryTeam = null;

            if (victoryTeam != null)
            {   //Yes! Victory for them!
                if (!TeamQualifiesForFlagRewards(victoryTeam))
                    return;
                _arena.setTicker(1, 1, _config.flag.victoryHoldTime, "Victory in ");
                _tickNextVictoryNotice = _tickVictoryStart = Environment.TickCount;
                _victoryTeam = victoryTeam;
            }
            else
            {   //Aborted?
                if (_victoryTeam != null && !_gameWon)
                {
                    _tickVictoryStart = 0;
                    _tickNextVictoryNotice = 0;
                    _victoryTeam = null;
                    _victoryNotice = 0;

                    _arena.sendArenaMessage("Victory has been aborted.", _config.flag.victoryAbortedBong);
                    _arena.setTicker(1, 1, 0, "");
                }
            }
        }

        #endregion

        #region Warping
        /// <summary>
        /// Finds a specific point within a radius with no physics for a player to warp to
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="posX"></param>
        /// <param name="posY"></param>
        /// <param name="radius"></param>
        /// <returns></returns>
        public Helpers.ObjectState findOpenWarp(InfServer.Game.Player player, Arena _arena, short posX, short posY, int radius, int maxBlockedAttempts = 10)
        {
            Helpers.ObjectState warpPoint = null;

            try
            {
                int blockedAttempts = maxBlockedAttempts;

                short pX;
                short pY;

                while (true)
                {
                    pX = posX;
                    pY = posY;
                    Helpers.randomPositionInArea(_arena, radius, ref pX, ref pY);
                    if (_arena.getTile(pX, pY).Blocked)
                    {
                        blockedAttempts--;
                        if (blockedAttempts <= 0)
                            //Consider the area to be blocked
                            return null;
                        else
                            continue;
                    }
                    warpPoint = new Helpers.ObjectState();
                    warpPoint.positionX = pX;
                    warpPoint.positionY = pY;
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Exception, "[Eol] findOpenWarp: " + ex.Message);
            }
            return warpPoint;
        }

        /// <summary>
        /// Picks random unblocked tiles inside an axis-aligned rectangle (e.g. full Eol sector bounds).
        /// Unlike <see cref="findOpenWarp"/>, does not use a small square around a center — covers the whole rect.
        /// </summary>
        public Helpers.ObjectState findOpenWarpInAxisAlignedRect(Arena arena, short minX, short maxX, short minY, short maxY, int maxAttempts)
        {
            if (arena == null || maxAttempts <= 0 || maxX < minX || maxY < minY)
                return null;

            try
            {
                int loX = minX;
                int hiX = maxX;
                int loY = minY;
                int hiY = maxY;

                for (int a = 0; a < maxAttempts; a++)
                {
                    short pX = (short)arena._rand.Next(loX, hiX + 1);
                    short pY = (short)arena._rand.Next(loY, hiY + 1);
                    if (arena.getTile(pX, pY).Blocked)
                        continue;

                    Helpers.ObjectState pt = new Helpers.ObjectState();
                    pt.positionX = pX;
                    pt.positionY = pY;
                    return pt;
                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Exception, "[Eol] findOpenWarpInAxisAlignedRect: " + ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Random unblocked tile in an active sector, at least <paramref name="minDistanceRaw"/> from
        /// <paramref name="fromX"/>/<paramref name="fromY"/> (for pirate raid wander legs).
        /// </summary>
        public Helpers.ObjectState TryPickRaiderRoamWaypointFarFrom(short fromX, short fromY, int minDistanceRaw, int maxAttempts = 140)
        {
            if (_eol == null || _arena == null)
                return null;
            _eol.EnsureActiveSectorsReadyForPortal();
            if (_eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return null;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            long minSq = (long)minDistanceRaw * minDistanceRaw;
            for (int a = 0; a < maxAttempts; a++)
            {
                string sec = _eol._activeSectors[_rand.Next(_eol._activeSectors.Count)];
                short minX;
                short maxX;
                short minY;
                short maxY;
                if (!_eol.TryGetSectorBounds(sec, out minX, out maxX, out minY, out maxY))
                    continue;

                Helpers.ObjectState pt = findOpenWarpInAxisAlignedRect(_arena, minX, maxX, minY, maxY, 20);
                if (pt == null || !_eol.IsInAnyActiveSector(pt.positionX, pt.positionY))
                    continue;

                long dx = (long)pt.positionX - fromX;
                long dy = (long)pt.positionY - fromY;
                if (dx * dx + dy * dy <= minSq)
                    continue;

                return pt;
            }

            return null;
        }

        public void ClearRaiderCollectiveRoamPoint()
        {
            RaiderCollectiveRoamValid = false;
            RaiderCollectiveRoamX = 0;
            RaiderCollectiveRoamY = 0;
            RaiderCollectiveRoamRevision = 0;
            RaiderCaptainCircuitLastFlagX = int.MinValue;
            RaiderCaptainCircuitLastFlagY = int.MinValue;
            ClearRaiderFlagCircuit();
            ClearDefenseTeamState();
            _tickRaiderObjectiveMarchUntil = 0;
            _tickRaiderHordeAtObjective = 0;
            _raiderHordeAtObjectiveX = 0;
            _raiderHordeAtObjectiveY = 0;
        }

        public bool IsRaiderHordeMarchingToObjective(int now)
        {
            if (!_pirateRaidWaveActive || !RaiderCollectiveRoamValid)
                return false;
            if (_tickRaiderObjectiveMarchUntil == 0 || now >= _tickRaiderObjectiveMarchUntil)
                return false;

            // Arrived at the new pin — end march so claim / local combat can run.
            RoamingCaptain cap = FindLiveRaiderCaptain();
            if (cap != null
                && IsNearCurrentRaiderFlagObjective(
                    cap._state.positionX, cap._state.positionY, RaiderHordeGroupArrivalRaw))
                return false;

            return true;
        }

        public bool TryGetRaiderCollectiveRoamPoint(out short x, out short y)
        {
            x = RaiderCollectiveRoamX;
            y = RaiderCollectiveRoamY;
            return RaiderCollectiveRoamValid;
        }

        public bool IsNearRaiderCollectiveRoamPoint(short px, short py, int arrivalRaw = -1)
        {
            if (!RaiderCollectiveRoamValid)
                return false;
            if (arrivalRaw < 0)
                arrivalRaw = RaiderRoamWaypointArrivalRaw;
            long dx = (long)px - RaiderCollectiveRoamX;
            long dy = (long)py - RaiderCollectiveRoamY;
            long arrSq = (long)arrivalRaw * arrivalRaw;
            return dx * dx + dy * dy <= arrSq;
        }

        /// <summary>Roaming captain only — ensure a shared objective exists; pack poll advances flags.</summary>
        public void CaptainPickRaiderCollectiveRoamPoint(short fromX, short fromY)
        {
            if (!RaiderCollectiveRoamValid)
                CaptainInitRaiderFlagObjective(fromX, fromY);
        }

        /// <summary>Random open tile near the current collective roam point (active sector).</summary>
        public Helpers.ObjectState TryPickRaiderRoamNearCollectivePoint(int maxAttempts = 35)
        {
            if (!RaiderCollectiveRoamValid || _arena == null)
                return null;

            short cx = RaiderCollectiveRoamX;
            short cy = RaiderCollectiveRoamY;
            for (int i = 0; i < maxAttempts; i++)
            {
                short px = cx;
                short py = cy;
                Helpers.randomPositionInArea(_arena, RaiderCollectiveRoamLocalRadius, ref px, ref py);
                if (_arena.getTile(px, py).Blocked)
                    continue;
                if (_eol != null && !_eol.IsInAnyActiveSector(px, py))
                    continue;

                Helpers.ObjectState o = new Helpers.ObjectState();
                o.positionX = px;
                o.positionY = py;
                return o;
            }

            return null;
        }

        public Helpers.ObjectState findOpenRoamBot(Arena _arena, short posX, short posY, int radius)
        {
            Helpers.ObjectState warpPoint = null;
            try
            {
                int blockedAttempts = 40;

                short pX;
                short pY;

                while (true)
                {
                    pX = posX;
                    pY = posY;
                    CfgInfo.Terrain terrain = _arena.getTerrain(pX, pY);

                    Helpers.randomPositionInArea(_arena, radius, ref pX, ref pY);
                    if (_arena.getTile(pX, pY).Blocked && (terrain.message != "Roadway/Cement" || terrain.message != "Clear Terrain"))
                    {
                        blockedAttempts--;
                        if (blockedAttempts <= 0)
                            //Consider the area to be blocked
                            return null;
                        else
                            continue;
                    }
                    warpPoint = new Helpers.ObjectState();
                    warpPoint.positionX = pX;
                    warpPoint.positionY = pY;
                    break;

                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Exception, "[Eol] findOpenRoamBot: " + ex.Message);
            }
            return warpPoint;
        }

        public Helpers.ObjectState findOpenWarpBot(Arena _arena, short posX, short posY, int radius, int maxBlockedAttempts = 10)
        {
            Helpers.ObjectState warpPoint = null;

            try
            {
                if (maxBlockedAttempts < 1)
                    maxBlockedAttempts = 1;

                short pX;
                short pY;

                while (maxBlockedAttempts-- > 0)
                {
                    pX = posX;
                    pY = posY;
                    Helpers.randomPositionInArea(_arena, radius, ref pX, ref pY);
                    if (_arena.getTile(pX, pY).Blocked)
                        continue;
                    warpPoint = new Helpers.ObjectState();
                    warpPoint.positionX = pX;
                    warpPoint.positionY = pY;
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.write(TLog.Exception, "[Eol] findOpenWarpBot: " + ex.Message);
            }
            return warpPoint;
        }

        public Helpers.ObjectState findFlagWarp(Team team, bool bBot)
        {
            Helpers.ObjectState warpPoint = null;
            List<Arena.FlagState> sortedFlags = new List<Arena.FlagState>();

            if (!team._name.StartsWith("Bot", StringComparison.OrdinalIgnoreCase))
                sortedFlags = _arena._flags.Values.ToList();

            int count = sortedFlags.Count;

            //No flags for some reason?
            if (count == 0)
                return null;


            Random r = new Random();
            int _randFlag = r.Next(0, sortedFlags.Count);
            _flagchecking = _coolArray[_randFlag];

            warpPoint = new Helpers.ObjectState();
            warpPoint.positionX = sortedFlags[_randFlag].posX;
            warpPoint.positionY = sortedFlags[_randFlag].posY;

            short _randomDist = 0;
            Random rn = new Random();
            int _randomNo = rn.Next(50, 450);
            _randomDist = Convert.ToInt16(_randomNo);

            int spawnPos = 0;

            Random rnd = new Random();
            int _randPos = rnd.Next(0, 4);
            spawnPos = _randPos;

            switch (spawnPos)
            {
                case 1:
                    warpPoint.positionX = (short)(warpPoint.positionX - _randomDist);
                    break;
                case 2:
                    warpPoint.positionX = (short)(warpPoint.positionX + _randomDist);
                    break;
                case 3:
                    warpPoint.positionY = (short)(warpPoint.positionY - _randomDist);
                    break;
                case 4:
                    warpPoint.positionY = (short)(warpPoint.positionY + _randomDist);
                    break;
            }


            return warpPoint;
        }


        public void warp(InfServer.Game.Player player, Helpers.ObjectState warpTo)
        {
            player.warp(warpTo.positionX, warpTo.positionY);
        }

        #endregion

        #region Tickers and Updaters

        private void UpdateCTFTickers()
        {
            int now = Environment.TickCount;
            if (now - _tickLastCtfTickerUpdate < CtfTickerUpdateMs)
                return;
            _tickLastCtfTickerUpdate = now;

            // Personal K/D only — change-gated per player (no arena-wide ranked blast on this slot).
            foreach (InfServer.Game.Player p in _arena.Players)
            {
                if (p == null || p._client == null)
                    continue;

                string text;
                if (p.StatsCurrentGame == null)
                    text = "Score: K=0 D=0";
                else
                    text = string.Format("Score: K={0} D={1}", p.StatsCurrentGame.kills, p.StatsCurrentGame.deaths);

                string last;
                if (_lastCtfPersonalScoreTicker.TryGetValue(p, out last) && string.Equals(last, text, StringComparison.Ordinal))
                    continue;

                _lastCtfPersonalScoreTicker[p] = text;
                SendPlayerTicker(p, CtfPersonalScoreTickerIdx, 2, text);
            }
        }

        /// <summary>
        /// Updates our players kill streak timer
        /// </summary>
        private void UpdateKillStreaks()
        {
            foreach (KeyValuePair<string, PlayerStreak> p in killStreaks)
            {
                if (p.Value.lastUsedWepTick == -1)
                    continue;

                if (Environment.TickCount - p.Value.lastUsedWepTick <= 0)
                    ResetWeaponTicker(p.Key);
            }
        }


        /// <summary>
        /// Updates the last killer
        /// </summary>
        private void ResetKiller(InfServer.Game.Player killer)
        {
            lastKiller = killer;
        }

        /// <summary>
        /// Resets the weapon ticker to default (Time Expired)
        /// </summary>
        private void ResetWeaponTicker(string targetAlias)
        {
            if (killStreaks.ContainsKey(targetAlias))
            {
                killStreaks[targetAlias].lastUsedWeap = null;
                killStreaks[targetAlias].lastUsedWepKillCount = 0;
                killStreaks[targetAlias].lastUsedWepTick = -1;
            }
        }

        /// <summary>
        /// Updates the killer and their kill counter
        /// </summary>
        private void UpdateKiller(InfServer.Game.Player killer)
        {
            if (killStreaks.ContainsKey(killer._alias))
            {
                killStreaks[killer._alias].lastKillerCount++;
                switch (killStreaks[killer._alias].lastKillerCount)
                {
                    case 6:
                        _arena.sendArenaMessage(string.Format("{0} is on fire!", killer._alias), 8);
                        break;
                    case 8:
                        _arena.sendArenaMessage(string.Format("Someone kill {0}!", killer._alias), 9);
                        break;
                }
            }
            //Is this first blood?
            if (lastKiller == null)
            {
                //It is, lets make the sound
                _arena.sendArenaMessage(string.Format("{0} has drawn first blood.", killer._alias), 7);
            }
            lastKiller = killer;
        }

        /// <summary>
        /// Player killed a bot: streak / team points / crown timer only. Counts on CFG Name10 (ZoneStat11), not Kills.
        /// </summary>
        private void OnPlayerKilledBot(InfServer.Game.Player killer, Bots.Bot dead)
        {
            if (killer == null || dead == null || dead._team == killer._team)
                return;

            killer.ZoneStat11++;
            UpdateKiller(killer);

            if (_points != null)
                _points[killer._team] += _pointSmallChange;

            if (_kothGameRunning && _playerCrownStatus != null && _playerCrownStatus.ContainsKey(killer))
            {
                if (_playerCrownStatus[killer].crown)
                    updateCrownTime(killer);
            }
        }

        /// <summary>
        /// Updates the victim's kill streak and notifies the public.
        /// </summary>
        /// <param name="nonPlayerKillerKillfeedName">When <paramref name="killer"/> is null (e.g. killed by a bot), name shown as streak ender.</param>
        private void UpdateDeath(InfServer.Game.Player victim, InfServer.Game.Player killer, string nonPlayerKillerKillfeedName = null)
        {
            if (killStreaks.ContainsKey(victim._alias))
            {
                if (killStreaks[victim._alias].lastKillerCount >= 6)
                {
                    string msg;
                    if (killer != null)
                        msg = string.Format("{0} has ended {1}'s kill streak.", killer._alias, victim._alias);
                    else if (!string.IsNullOrEmpty(nonPlayerKillerKillfeedName))
                        msg = string.Format("{0} has ended {1}'s kill streak.", nonPlayerKillerKillfeedName, victim._alias);
                    else
                        msg = victim._alias + "'s kill streak has ended.";
                    _arena.sendArenaMessage(msg, 6);
                }
                killStreaks[victim._alias].lastKillerCount = 0;
            }
        }

        /// <summary>
        /// Updates the last fired weapon and its ticker
        /// </summary>
        private void UpdateWeapon(InfServer.Game.Player from, ItemInfo.Projectile usedWep, int aliveTime)
        {
            if (killStreaks.ContainsKey(from._alias))
            {
                killStreaks[from._alias].lastUsedWeap = usedWep;
                killStreaks[from._alias].lastUsedWepTick = DateTime.Now.AddTicks(aliveTime).Ticks;
            }
        }

        /// <summary>
        /// Updates the last weapon used and kill count then announcing it to the public
        /// </summary>
        private void UpdateWeaponKill(InfServer.Game.Player from)
        {
            if (killStreaks.ContainsKey(from._alias))
            {
                if (killStreaks[from._alias].lastUsedWeap == null)
                    return;

                killStreaks[from._alias].lastUsedWepKillCount++;
                ItemInfo.Projectile lastUsedWep = killStreaks[from._alias].lastUsedWeap;
                switch (killStreaks[from._alias].lastUsedWepKillCount)
                {
                    case 2:
                        _arena.sendArenaMessage(string.Format("{0} just got a double {1} kill.", from._alias, lastUsedWep.name), 17);
                        break;
                    case 3:
                        _arena.sendArenaMessage(string.Format("{0} just got a triple {1} kill!", from._alias, lastUsedWep.name), 18);
                        break;
                    case 4:
                        _arena.sendArenaMessage(string.Format("A 4 {0} kill by {0}?!?", lastUsedWep.name, from._alias), 19);
                        break;
                    case 5:
                        _arena.sendArenaMessage(string.Format("Unbelievable! {0} with the 5 {1} kill?", from._alias, lastUsedWep.name), 20);
                        break;
                }
            }
        }

        /// <summary>Unused CTF/points hook — KOTH wins go through <see cref="kothVictory"/>.</summary>
        public void gameVictory(Team victors)
        {

        }

        public void rewards()
        {
            int teamsPaid = 0;
            int playersIngame = 0;
            try { playersIngame = _arena.PlayersIngame.Count(); }
            catch { playersIngame = 0; }

            foreach (Team rewardees in _arena.ActiveTeams)
            {
                if (!TeamQualifiesForFlagRewards(rewardees))
                    continue;

                int cashReward = 0;
                int expReward = 0;
                int pointReward = 0;
                int cashUnits = 0;
                int expUnits = 0;
                int pointUnits = 0;
                int flagsOwned = 0;
                int flagsPaying = 0;

                foreach (Arena.FlagState fs in _arena._flags.Values)
                {
                    if (!TeamOwnsArenaFlag(rewardees, fs) || fs.flag == null)
                        continue;

                    flagsOwned++;

                    LioInfo.Flag.FlagSettings flagSettings = fs.flag.FlagData;
                    // Periodic reward?
                    if (flagSettings.PeriodicCashReward == 0 && flagSettings.PeriodicExperienceReward == 0 && flagSettings.PeriodicPointsReward == 0)
                        continue;

                    flagsPaying++;

                    // Cash — negative LIO values are thousandths × players; accumulate then divide once.
                    if (flagSettings.PeriodicCashReward < 0)
                        cashUnits += Math.Abs(flagSettings.PeriodicCashReward) * playersIngame;
                    else
                        cashReward += Math.Abs(flagSettings.PeriodicCashReward);
                    // Experience
                    if (flagSettings.PeriodicExperienceReward < 0)
                        expUnits += Math.Abs(flagSettings.PeriodicExperienceReward) * playersIngame;
                    else
                        expReward += Math.Abs(flagSettings.PeriodicExperienceReward);
                    // Points
                    if (flagSettings.PeriodicPointsReward < 0)
                        pointUnits += Math.Abs(flagSettings.PeriodicPointsReward) * playersIngame;
                    else
                        pointReward += Math.Abs(flagSettings.PeriodicPointsReward);
                }

                cashReward += cashUnits / 1000;
                expReward += expUnits / 1000;
                pointReward += pointUnits / 1000;

                // Zero-periodic owned pins still count as owned — value them at the average of paying pins
                // so [N flags] matches the map and the payout.
                if (flagsPaying > 0 && flagsOwned > flagsPaying)
                {
                    cashReward = (cashReward * flagsOwned) / flagsPaying;
                    expReward = (expReward * flagsOwned) / flagsPaying;
                    pointReward = (pointReward * flagsOwned) / flagsPaying;
                }

                if (cashReward == 0 && expReward == 0 && pointReward == 0)
                    continue;

                cashReward = ScaleEolFlagPeriodicReward(cashReward, flagsOwned);
                expReward = ScaleEolFlagPeriodicReward(expReward, flagsOwned);
                pointReward = ScaleEolFlagPeriodicReward(pointReward, flagsOwned);

                teamsPaid++;

                // Count this payout toward the next flag reset (shared across teams for this cycle).
                int rewardsTowardReset = _flagRewardsSinceReset + 1;
                bool willResetFlags = rewardsTowardReset >= FlagRewardsBeforeReset;
                int remainingBeforeReset = FlagRewardsBeforeReset - rewardsTowardReset;

                string format;
                double holdMult = GetEolFlagHoldMultiplier(flagsOwned);
                if (willResetFlags)
                {
                    format = String.Format(
                        "&Reward (Cash={0} Experience={1} Points={2}) [{3} flags, ×{4:0.##}] Flags reset. Next reward in {5} minutes.",
                        cashReward, expReward, pointReward, flagsOwned,
                        EolFlagRewardGlobalMult * holdMult, 10);
                }
                else
                {
                    string rewardWord = remainingBeforeReset == 1 ? "reward" : "rewards";
                    format = String.Format(
                        "&Reward (Cash={0} Experience={1} Points={2}) [{3} flags, ×{4:0.##}] Next reward in {5} minutes. Flags reset after {6} more {7}.",
                        cashReward, expReward, pointReward, flagsOwned,
                        EolFlagRewardGlobalMult * holdMult, 10, remainingBeforeReset, rewardWord);
                }

                //Send it.
                rewardees.sendArenaMessage(format, _arena._server._zoneConfig.flag.periodicBong);

                //Reward each player on the rewardees team (lifetime-exp scale is per recipient).
                foreach (InfServer.Game.Player player in rewardees.ActivePlayers)
                {
                    int scaledCash = ApplyEolLifetimeRewardScale(player, cashReward);
                    int scaledExp = ApplyEolLifetimeRewardScale(player, expReward);
                    int scaledPoints = ApplyEolLifetimeRewardScale(player, pointReward);
                    player.Cash += scaledCash;
                    player.Experience += scaledExp;
                    player.BonusPoints += scaledPoints;
                    player.syncState();
                }
            }

            if (teamsPaid > 0)
            {
                _flagRewardsSinceReset++;
                if (_flagRewardsSinceReset >= FlagRewardsBeforeReset)
                {
                    _arena.flagReset();
                    _arena.flagSpawn();
                    _flagRewardsSinceReset = 0;
                    Log.write(TLog.Normal, "[Eol] Flag periodic payout: {0} team(s) rewarded; flags reset after {1} rewards.", teamsPaid, FlagRewardsBeforeReset);
                }
                else
                {
                    Log.write(TLog.Normal, "[Eol] Flag periodic payout: {0} team(s) rewarded; flags reset in {1} more reward(s).",
                        teamsPaid, FlagRewardsBeforeReset - _flagRewardsSinceReset);
                }
            }

            // Always re-arm the 10-minute window when this method runs (including zero payout: held flags but no periodic values, or config mismatch).
            _lastFlagReward = Environment.TickCount;
        }

        static public void calculatePlayerKillRewards(InfServer.Game.Player victim, InfServer.Game.Player killer)
        {
            CfgInfo cfg = victim._server._zoneConfig;

            int killerBounty = 0;
            int killerBountyIncrease = 0;
            int victimBounty = 0;
            int killerCash = 0;
            int killerExp = 0;
            int killerPoints = 0;

            //Fake it to make it
            CS_VehicleDeath update = new CS_VehicleDeath(0, new byte[0], 0, 0);
            update.killedID = victim._id;
            update.killerPlayerID = killer._id;
            update.positionX = victim._state.positionX;
            update.positionY = victim._state.positionY;
            update.type = Helpers.KillType.Player;

            if (killer._team != victim._team)
            {
                killerBounty = Convert.ToInt32(((double)killer.Bounty / 100) * c_percentOfOwn);
                killerBountyIncrease = Convert.ToInt32(((double)killer.Bounty / 100) * c_percentOfOwnIncrease);
                victimBounty = Convert.ToInt32(((double)victim.Bounty / 100) * c_percentOfVictim);

                killerPoints = Convert.ToInt32((c_baseReward + killerBounty + victimBounty) * c_pointMultiplier);
                killerCash = Convert.ToInt32((c_baseReward + killerBounty + victimBounty) * c_cashMultiplier);
                killerExp = Convert.ToInt32((c_baseReward + killerBounty + victimBounty) * c_expMultiplier);

            }
            else
            {
                foreach (InfServer.Game.Player p in victim._arena.Players)
                    Helpers.Player_RouteKill(p, update, victim, 0, 0, 0, 0);
                return;
            }

            int baseCash = killerCash;
            int baseExp = killerExp;
            int basePoints = killerPoints;
            killerCash = ApplyEolLifetimeRewardScale(killer, baseCash);
            killerExp = ApplyEolLifetimeRewardScale(killer, baseExp);
            killerPoints = ApplyEolLifetimeRewardScale(killer, basePoints);

            //Inform the killer
            Helpers.Player_RouteKill(killer, update, victim, killerCash, killerPoints, killerPoints, killerExp);

            //Update some statistics
            killerCash = addCash(killer, killerCash);
            killer.Experience += killerExp;
            killer.KillPoints += killerPoints;
            victim.DeathPoints += killerPoints;

            //Update his bounty
            killer.Bounty += (killerBountyIncrease + victimBounty);

            //Check for players in the share radius
            List<InfServer.Game.Player> sharedCash = victim._arena.getPlayersInRange(update.positionX, update.positionY, cfg.cash.shareRadius).ToList();
            List<InfServer.Game.Player> sharedExp = victim._arena.getPlayersInRange(update.positionX, update.positionY, cfg.experience.shareRadius).ToList();
            List<InfServer.Game.Player> sharedPoints = victim._arena.getPlayersInRange(update.positionX, update.positionY, cfg.point.shareRadius).ToList();
            Dictionary<int, int> cashRewards = new Dictionary<int, int>();
            Dictionary<int, int> expRewards = new Dictionary<int, int>();
            Dictionary<int, int> pointRewards = new Dictionary<int, int>();
            //Set up our shared math from unscaled base so assists are not double-scaled.
            int CashShare = (int)((((float)baseCash) / 1000) * cfg.cash.sharePercent);
            int ExpShare = (int)((((float)baseExp) / 1000) * cfg.experience.sharePercent);
            int PointsShare = (int)((((float)basePoints) / 1000) * cfg.point.sharePercent);
            int BtyShare = (int)((basePoints * (((float)cfg.bounty.percentToAssistBounty) / 1000)));

            foreach (InfServer.Game.Player p in sharedCash)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                cashRewards[p._id] = CashShare;
                expRewards[p._id] = 0;
                pointRewards[p._id] = 0;
            }

            foreach (InfServer.Game.Player p in sharedExp)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                expRewards[p._id] = ExpShare;
                if (!cashRewards.ContainsKey(p._id))
                    cashRewards[p._id] = 0;
                if (!pointRewards.ContainsKey(p._id))
                    pointRewards[p._id] = 0;
            }

            foreach (InfServer.Game.Player p in sharedPoints)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                pointRewards[p._id] = PointsShare;
                if (!cashRewards.ContainsKey(p._id))
                    cashRewards[p._id] = 0;
                if (!expRewards.ContainsKey(p._id))
                    expRewards[p._id] = 0;

                //Share bounty within the experience radius, Dunno if there is a sharebounty radius?
                p.Bounty += BtyShare;
            }

            //Sent reward notices to our lucky witnesses
            List<int> sentTo = new List<int>();
            foreach (InfServer.Game.Player p in sharedCash)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                GrantEolScaledSharedKillRewards(p, update, victim, cashRewards, expRewards, pointRewards, killerPoints);
                sentTo.Add(p._id);
            }

            foreach (InfServer.Game.Player p in sharedExp)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                if (!sentTo.Contains(p._id))
                {
                    GrantEolScaledSharedKillRewards(p, update, victim, cashRewards, expRewards, pointRewards, killerPoints);

                    sentTo.Add(p._id);
                }
            }

            foreach (InfServer.Game.Player p in sharedPoints)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                if (!sentTo.Contains(p._id))
                {   //Update the assist bounty
                    p.Bounty += BtyShare;

                    GrantEolScaledSharedKillRewards(p, update, victim, cashRewards, expRewards, pointRewards, killerPoints);

                    sentTo.Add(p._id);
                }
            }

            //Shared kills anyone?
            Vehicle sharedveh = killer._occupiedVehicle;
            //are we in a vehicle?
            if (sharedveh != null)
            {
                //Was this a child vehicle? If so, re-route us to the parent
                if (sharedveh._parent != null)
                    sharedveh = sharedveh._parent;

                //Can we even share kills?
                if (sharedveh._type.SiblingKillsShared > 0)
                {   //Yep!
                    //Does this vehicle have any childs?
                    if (sharedveh._childs.Count > 0)
                    {
                        //Cycle through each child and reward them
                        foreach (Vehicle child in sharedveh._childs)
                        {
                            //Anyone home?
                            if (child._inhabitant == null)
                                continue;

                            //Can we share?
                            if (child._type.SiblingKillsShared == 0)
                                continue;

                            //Skip our killer
                            if (child._inhabitant == killer)
                                continue;

                            //Give them a kill!
                            child._inhabitant.Kills++;

                            //Show the message
                            child._inhabitant.triggerMessage(2, 500,
                                String.Format("Sibling Assist: Kills=1 (Points={0} Exp={1} Cash={2})",
                                CashShare, ExpShare, PointsShare));
                        }
                    }
                }
            }

            //Route the kill to the rest of the arena
            foreach (InfServer.Game.Player p in victim._arena.Players.ToList())
            {   //As long as we haven't already declared it, send
                if (p == null)
                    continue;

                if (p == killer)
                    continue;

                if (sentTo.Contains(p._id))
                    continue;

                Helpers.Player_RouteKill(p, update, victim, 0, killerPoints, 0, 0);
            }
        }

        /// <summary>
        /// Per-recipient cash/exp/points scale from lifetime experience:
        /// under 75k → 1.5×, 75k–99,999 → 1.25×, 100k+ → 1.0×.
        /// </summary>
        static public double GetEolLifetimeRewardScale(InfServer.Game.Player player)
        {
            if (player == null)
                return 1.0;
            int lifetime = player.ExperienceTotal;
            if (lifetime < EolRewardScaleHighCutoffExp)
                return 1.5;
            if (lifetime < EolRewardScaleNormalCutoffExp)
                return 1.25;
            return 1.0;
        }

        static public int ApplyEolLifetimeRewardScale(InfServer.Game.Player player, int amount)
        {
            if (amount == 0 || player == null)
                return amount;
            double scale = GetEolLifetimeRewardScale(player) * GetEolHappyHourMultiplier();
            return Convert.ToInt32(amount * scale);
        }

        static TimeZoneInfo GetEolUsEasternTimeZone()
        {
            if (_eolUsEasternTz != null)
                return _eolUsEasternTz;
            try
            {
                _eolUsEasternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                try
                {
                    _eolUsEasternTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                }
                catch (TimeZoneNotFoundException)
                {
                    _eolUsEasternTz = TimeZoneInfo.Utc;
                    Log.write(TLog.Warning, "[Eol] US Eastern timezone not found — happy hour uses UTC.");
                }
            }
            catch (InvalidTimeZoneException)
            {
                _eolUsEasternTz = TimeZoneInfo.Utc;
                Log.write(TLog.Warning, "[Eol] Invalid US Eastern timezone — happy hour uses UTC.");
            }
            return _eolUsEasternTz;
        }

        /// <summary>True during any configured US Eastern happy-hour window.</summary>
        public static bool IsEolHappyHourNow()
        {
            int h = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetEolUsEasternTimeZone()).Hour;
            return TryGetEolHappyHourWindowForHour(h, out _, out _);
        }

        static bool TryGetEolHappyHourWindowForHour(int hourEt, out int startHour, out int endHourExclusive)
        {
            startHour = 0;
            endHourExclusive = 0;
            int n = Math.Min(EolHappyHourStartHoursEt.Length, EolHappyHourEndHoursEt.Length);
            for (int i = 0; i < n; i++)
            {
                int start = EolHappyHourStartHoursEt[i];
                int end = EolHappyHourEndHoursEt[i];
                if (hourEt >= start && hourEt < end)
                {
                    startHour = start;
                    endHourExclusive = end;
                    return true;
                }
            }
            return false;
        }

        public static double GetEolHappyHourMultiplier()
        {
            return IsEolHappyHourNow() ? EolHappyHourRewardMult : 1.0;
        }

        static string FormatEolHappyHourClock(int hourEt24)
        {
            int h12 = hourEt24 % 12;
            if (h12 == 0)
                h12 = 12;
            string ampm = hourEt24 >= 12 ? "PM" : "AM";
            return string.Format("{0} {1}", h12, ampm);
        }

        static string FormatEolHappyHourSchedule()
        {
            int n = Math.Min(EolHappyHourStartHoursEt.Length, EolHappyHourEndHoursEt.Length);
            var parts = new System.Collections.Generic.List<string>(n);
            for (int i = 0; i < n; i++)
            {
                parts.Add(string.Format("{0}-{1}",
                    FormatEolHappyHourClock(EolHappyHourStartHoursEt[i]),
                    FormatEolHappyHourClock(EolHappyHourEndHoursEt[i])));
            }
            return string.Join(", ", parts.ToArray()) + " ET";
        }

        void PollEolHappyHour(int now)
        {
            bool active = IsEolHappyHourNow();
            if (active == _eolHappyHourActive)
                return;

            _eolHappyHourActive = active;
            InvalidateEolHudTickerCache();

            if (_arena == null)
                return;

            int bong = _config != null ? _config.flag.periodicBong : 1;
            if (active)
            {
                int hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetEolUsEasternTimeZone()).Hour;
                int end;
                TryGetEolHappyHourWindowForHour(hour, out _, out end);
                _arena.sendArenaMessage(string.Format(
                    "&Happy Hour! Double cash/exp/points until {0} ET. Daily windows: {1}.",
                    FormatEolHappyHourClock(end),
                    FormatEolHappyHourSchedule()), bong);
            }
            else
            {
                _arena.sendArenaMessage(
                    "&Happy Hour has ended. Next windows: " + FormatEolHappyHourSchedule() + ".", bong);
            }
        }

        /// <summary>Hold-count multiplier on top of <see cref="EolFlagRewardGlobalMult"/> (1–2: ×1, 3–4: ×1.25, 5–7: ×1.5, 8+: ×2).</summary>
        static double GetEolFlagHoldMultiplier(int flagsOwned)
        {
            if (flagsOwned >= 8)
                return 2.0;
            if (flagsOwned >= 5)
                return 1.5;
            if (flagsOwned >= 3)
                return 1.25;
            return 1.0;
        }

        /// <summary>×4 global, then monopoly bonus by owned flag count.</summary>
        static int ScaleEolFlagPeriodicReward(int amount, int flagsOwned)
        {
            if (amount <= 0)
                return amount;
            return (int)Math.Round(amount * EolFlagRewardGlobalMult * GetEolFlagHoldMultiplier(flagsOwned));
        }

        static void GrantEolScaledSharedKillRewards(
            InfServer.Game.Player p,
            CS_VehicleDeath update,
            InfServer.Game.Player victim,
            Dictionary<int, int> cashRewards,
            Dictionary<int, int> expRewards,
            Dictionary<int, int> pointRewards,
            int killerPoints)
        {
            int cash = ApplyEolLifetimeRewardScale(p, cashRewards[p._id]);
            int exp = ApplyEolLifetimeRewardScale(p, expRewards[p._id]);
            int points = ApplyEolLifetimeRewardScale(p, pointRewards[p._id]);
            cashRewards[p._id] = addCash(p, cash);
            p.Experience += exp;
            p.AssistPoints += points;
            Helpers.Player_RouteKill(p, update, victim, cashRewards[p._id], killerPoints, points, exp);
        }

        static public int addCash(InfServer.Game.Player player, int quantity)
        {
            //Any rare cash item?
            double multiplier = 1.0;
            int cash = 0;

            
            cash = Convert.ToInt32(quantity * multiplier);

            player.Cash += cash;
            player.syncState();

            return cash;
        }

        public class TimeSpanRange
        {
            public int Start { get; private set; }
            public int End { get; private set; }

            public TimeSpanRange(int start, int end)
            {
                Start = start;
                End = end;
            }

            public bool IsHourInRange(int hour)
            {
                if (Start <= End)
                {
                    // Normal case where the range does not cross midnight
                    return hour >= Start && hour <= End;
                }
                else
                {
                    // Special case where the range crosses midnight
                    return hour >= Start || hour <= End;
                }
            }
        }
        #endregion

        #region game start/end/reset
        /// <summary>
        /// Called when the game begins
        /// </summary>
        [Scripts.Event("Game.Start")]
        public bool gameStart()
        {   //We've started!
            Log.write(TLog.Normal,
                "[Eol] gameStart() entry: players={0} (KOTH={1}, Aliens={2}, Raiders={3}, BaseBots={4}, BaseSetup={5})",
                _arena != null ? _arena.PlayerCount : 0,
                EolKothSystemEnabled, EolAlienWavesEnabled, EolPirateRaidsEnabled, EolBaseBotsEnabled, EolBaseSetupEnabled);
            _tickGameStarting = 0;
            _lastCtfLobbyCountdownSecondShown = -1;
            ClearCtfNextGameLobbyTicker();
            _lastKOTHGameCheck = 0;
            _tickKOTHGameStarting = 0;
            _tickNextKothEligible = 0;
            _tickLastMinorPoll = Environment.TickCount;
            _tickGameLastTickerUpdate = Environment.TickCount;
            // Allow first HQ/turret placement on the next poll (do not wait a full engineer cycle).
            _tickLastEngineer = 0;
            _tickLastCaptain = Environment.TickCount;
            _tickLastChief = Environment.TickCount;
            _eol.gameStart();
            _arena.flagReset();
            _arena.flagSpawn();
            _lastPvpRoamHourAlert = 0;
            _lastRoamAlert = 0;
            ResetKiller(null);
            killStreaks.Clear();
            _tickEolGameStart = Environment.TickCount;
            nextSpanStart = -1;
            isRoamActive = IsAnyRoamScheduledHour(DateTime.Now.Hour);
            _lastPollHourInRoamSpan = isRoamActive;
            foreach (InfServer.Game.Player p in _arena.Players)
            {
                if (p == null)
                    continue;
                PlayerStreak temp = new PlayerStreak();
                temp.lastKillerCount = 0;
                temp.lastUsedWeap = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                killStreaks[p._alias] = temp;
            }
            //Let everyone know
            _arena.sendArenaMessage("A new game has started!", _config.flag.victoryWarningBong);
            int nowArm = Environment.TickCount;
            _tickNextSectorRotation = nowArm + SectorRotationIntervalMs;
            _arena.sendArenaMessage("&Radiation sectors rotate every 6 hours. A 90-second warning will be given before the change.", _config.flag.periodicBong);
            _lastFlagReward = nowArm;
            ResetFlagRewardCycleNotices();
            _flagRewardsSinceReset = 0;
            _tickLastRoamingCaptain = nowArm;
            _tickDefenseCaptainRespawnEligible = 0;
            _tickLastDefenseCaptainSpawnAttempt = 0;
            //Start keeping track of healing
            _healingDone = new Dictionary<InfServer.Game.Player, int>();
            _bbetweengames = false;
            nextSpanStart = -1;
            _tickGameStart = nowArm;
            _tickKothGameStart = nowArm;
            RaiderPdbAnchorValid = false;
            ClearRaiderCollectiveRoamPoint();
            _tickLastRaiderHordePlaceAttempt = 0;
            _pirateRaidWaveActive = false;
            _pirateRaidQuotaMet = false;
            _pirateRaidGruntsSpawnedThisWave = 0;
            _pirateRaidGruntsRemainingThisWave = 0;
            _tickNextPirateGruntSpawn = 0;
            _tickPirateRaidInterBatchGateUntil = 0;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            // One exclusive threat at a time (base / raiders / aliens) — no parallel timers.
            BeginInitialThreatRotation();
            Log.write(TLog.Normal, "[Eol] Match started (players={0}); threat={1}.", _arena.PlayerCount, _activeThreat);
            return true;
        }

        /// <summary>
        /// Called when the game ends
        /// </summary>
        [Scripts.Event("Game.End")]
        public bool gameEnd()
        {   //Game finished, perhaps start a new one
            Log.write(TLog.Normal, "[Eol] Match ended (players={0}).", _arena.PlayerCount);
            ClearExplosionNetQueue();
            ClearEolBorderWallVehicles();
            ClearThreatRotationState();
            _arena.setTicker(1, CtfNextGameTickerIdx, 0, "");
            _arena.setTicker(1, KothLobbyTickerIdx, 0, "");
            _arena.setTicker(0, EolMatchScheduleTickerIdx, 0, "");
            _arena.setTicker(0, EolActivityTickerIdx, 0, "");
            _lastHudMatchTickerSent = null;
            _lastHudActivityTickerSent = null;
            _lastCtfPersonalScoreTicker.Clear();
            _lastKothCrownTickerSec.Clear();
            _sectorChangeCountdownActive = false;
            _arena._bGameRunning = false;
            _kothGameRunning = false;
            ClearKothEndQueue();
            _tickGameStart = 0;
            _tickGameStarting = 0;
            _lastCtfLobbyCountdownSecondShown = -1;
            _tickKothGameStart = 0;
            _lastKOTHGameCheck = 0;
            _tickKOTHGameStarting = 0;
            _tickNextKothEligible = 0;
            _maxRoamCaptains = 0;
            _maxEngineers = 0;
            _tickLastMinorPoll = 0;
            _tickLastEngineer = 0;
            _tickLastCaptain = 0;
            _tickLastRoamingCaptain = 0;
            _tickLastChief = 0;
            _tickGameLastTickerUpdate = 0;
            _lastFlagReward = 0;
            ResetFlagRewardCycleNotices();
            _flagRewardsSinceReset = 0;
            _currentRoamCaptains = 0;
            _currentRoamChief = 0;
            _lastPvpRoamHourAlert = 0;
            _healingDone = null;
            _eol.boundarygameEnd();
            _tickEolGameStart = 0;
            _tickNextSectorRotation = 0;
            _bbetweengames = true;
            nextSpanStart = -1;
            isRoamActive = false;
            _lastRoamAlert = 0;
            foreach (Vehicle v in _arena.Vehicles)
                if (v._type.Type == VehInfo.Types.Computer)
                    EnqueueVehicleDespawn(v);
            _usedpylons.Clear();
            _bbetweengames = true;
            DestroyAllTrackedScriptBots();
            ClearAllDefenseBaseVehicleLists();

            captainBots.Clear();
            capRoamBots.Clear();
            engineerBots.Clear();
            roamingCaptianBots.Clear();
            alienChiefBots.Clear();
            roamingAlienBots.Clear();

            _activehPoints.Clear();

            _minX = 0;
            _maxX = 0;
            _minY = 0;
            _maxY = 0;

            nextSpanStart = -1;
            _tickDefenseCaptainRespawnEligible = 0;
            _tickLastDefenseCaptainSpawnAttempt = 0;
            ResetAlienWaveState();
            RaiderPdbAnchorValid = false;
            ClearRaiderCollectiveRoamPoint();
            _tickLastRaiderHordePlaceAttempt = 0;
            _pirateRaidWaveActive = false;
            _pirateRaidQuotaMet = false;
            _pirateRaidGruntsSpawnedThisWave = 0;
            _pirateRaidGruntsRemainingThisWave = 0;
            _tickNextPirateGruntSpawn = 0;
            _tickPirateRaidInterBatchGateUntil = 0;
            _tickNextPirateRaidAllowed = 0;
            return true;
        }

        /// <summary>
        /// Called to reset the game state
        /// </summary>
        [Scripts.Event("Game.Reset")]
        public bool gameReset()
        {   //Game reset, perhaps start a new one
            _arena.flagReset();
            _arena.flagSpawn();
            _arena.setTicker(1, CtfNextGameTickerIdx, 0, "");
            _arena.setTicker(1, KothLobbyTickerIdx, 0, "");
            _arena.setTicker(0, EolMatchScheduleTickerIdx, 0, "");
            _arena.setTicker(0, EolActivityTickerIdx, 0, "");
            _lastHudMatchTickerSent = null;
            _lastHudActivityTickerSent = null;
            _lastCtfPersonalScoreTicker.Clear();
            _lastKothCrownTickerSec.Clear();
            _sectorChangeCountdownActive = false;
            _arena._bGameRunning = false;
            _kothGameRunning = false;
            ClearKothEndQueue();
            _tickGameStart = 0;
            _tickGameStarting = 0;
            _lastCtfLobbyCountdownSecondShown = -1;
            _tickKothGameStart = 0;
            _lastKOTHGameCheck = 0;
            _tickKOTHGameStarting = 0;
            _tickNextKothEligible = 0;
            _tickLastMinorPoll = 0;
            _maxRoamCaptains = 0;
            _maxEngineers = 0;
            _tickLastEngineer = 0;
            _tickLastCaptain = 0;
            _tickLastRoamingCaptain = 0;
            _tickLastChief = 0;
            _tickGameLastTickerUpdate = 0;
            _lastFlagReward = 0;
            ResetFlagRewardCycleNotices();
            _flagRewardsSinceReset = 0;
            _healingDone = null;
            _eol.boundarygameReset();
            _tickEolGameStart = 0;
            _tickNextSectorRotation = 0;
            _lastPvpRoamHourAlert = 0;
            _bbetweengames = true;
            nextSpanStart = -1;
            isRoamActive = false;
            _lastRoamAlert = 0;
            foreach (Vehicle v in _arena.Vehicles)
                if (v._type.Type == VehInfo.Types.Computer)
                    EnqueueVehicleDespawn(v);
            _usedpylons.Clear();
            _bbetweengames = true;
            DestroyAllTrackedScriptBots();
            ClearAllDefenseBaseVehicleLists();

            captainBots.Clear();
            capRoamBots.Clear();
            engineerBots.Clear();
            roamingCaptianBots.Clear();
            alienChiefBots.Clear();
            roamingAlienBots.Clear();
            _currentRoamCaptains = 0;
            _currentRoamChief = 0;

            _activehPoints.Clear();

            _minX = 0;
            _maxX = 0;
            _minY = 0;
            _maxY = 0;

            nextSpanStart = -1;
            _tickDefenseCaptainRespawnEligible = 0;
            _tickLastDefenseCaptainSpawnAttempt = 0;
            ResetAlienWaveState();
            RaiderPdbAnchorValid = false;
            ClearRaiderCollectiveRoamPoint();
            _tickLastRaiderHordePlaceAttempt = 0;
            _pirateRaidWaveActive = false;
            _pirateRaidQuotaMet = false;
            _pirateRaidGruntsSpawnedThisWave = 0;
            _pirateRaidGruntsRemainingThisWave = 0;
            _tickNextPirateGruntSpawn = 0;
            _tickPirateRaidInterBatchGateUntil = 0;
            _tickNextPirateRaidAllowed = 0;
            ClearExplosionNetQueue();
            ClearThreatRotationState();
            Log.write(TLog.Normal, "[Eol] Arena reset (game state cleared).");
            return true;
        }

        #endregion

        #region Player Events
        /// <summary>
        /// Calculates and rewards a players for a bot kill
        /// </summary>
        static public void calculateBotKillRewards(Bots.Bot victim, InfServer.Game.Player killer)
        {
            CfgInfo cfg = killer._server._zoneConfig;
            int killerCash = 0;
            int killerExp = 0;
            int killerPoints = 0;
            int killerBounty = 0;
            int victimBounty = 0;
            int killerBountyIncrease = 0;

            BotSettings settings = victim.Settings();
            if (settings != null)
            {
                killerBounty = Convert.ToInt32(((double)killer.Bounty / 100) * c_percentOfOwn);
                killerPoints = Convert.ToInt32((settings.Points) + (killerBounty * c_pointMultiplier));
                killerCash = Convert.ToInt32((settings.Cash) + (killerBounty * c_cashMultiplier));
                killerExp = Convert.ToInt32((settings.Experience) + (killerBounty * c_expMultiplier));
                victimBounty = settings.Bounty;
                killerBountyIncrease = 0;
            }
            else
            {
                killerBounty = Convert.ToInt32(((double)killer.Bounty / 100) * c_percentOfOwn);
                killerPoints = Convert.ToInt32(((int)cfg.bot.pointsKillReward) + (killerBounty * c_pointMultiplier));
                killerCash = Convert.ToInt32(((int)cfg.bot.cashKillReward) + (killerBounty * c_cashMultiplier));
                killerExp = Convert.ToInt32(((int)cfg.bot.expKillReward) + (killerBounty * c_expMultiplier));
                victimBounty = (int)cfg.bot.fixedBountyToKiller;
                killerBountyIncrease = 0;
            }

            //Update his stats
            int baseCash = killerCash;
            int baseExp = killerExp;
            int basePoints = killerPoints;
            killerCash = ApplyEolLifetimeRewardScale(killer, baseCash);
            killerExp = ApplyEolLifetimeRewardScale(killer, baseExp);
            killerPoints = ApplyEolLifetimeRewardScale(killer, basePoints);

            killerCash = addCash(killer, killerCash);
            killer.Experience += killerExp;
            killer.KillPoints += killerPoints;
            killer.Bounty += (killerBountyIncrease + victimBounty) / 2;

            //Inform the killer..
            string victimName = BotNameModule.GetDisplayName(victim);
            killer.triggerMessage(1, 500,
                String.Format("{0} killed by {1} (Cash={2} Exp={3} Points={4})",
                victimName, killer._alias,
                killerCash, killerExp, killerPoints));

            //Sync his state
            killer.syncState();

            //Check for players in the share radius
            List<InfServer.Game.Player> sharedRewards = victim._arena.getPlayersInRange(victim._state.positionX, victim._state.positionY, cfg.bot.shareRadius);

            foreach (InfServer.Game.Player p in sharedRewards)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                int shareCash = ApplyEolLifetimeRewardScale(p, (int)((((float)baseCash) / 1000) * cfg.bot.sharePercent));
                int shareExp = ApplyEolLifetimeRewardScale(p, (int)((((float)baseExp) / 1000) * cfg.bot.sharePercent));
                int sharePoints = ApplyEolLifetimeRewardScale(p, (int)((((float)basePoints) / 1000) * cfg.bot.sharePercent));
                shareCash = addCash(p, shareCash);
                p.Experience += shareExp;
                p.AssistPoints += sharePoints;
                p.triggerMessage(2, 500,
                    String.Format("Assist: {0} (Cash={1} Exp={2} Points={3})",
                    victimName, shareCash, shareExp, sharePoints));
                p.syncState();
            }
        }

        /// <summary>
        /// DB bot stats by registry name (not <c>Bot._alias</c>). Uses reflection so script compile need not see <c>Logic_BotStats</c>.
        /// </summary>
        static MethodInfo _cachedUpdateBotStats;
        static bool _updateBotStatsResolved;

        void TryUpdateBotStatsByRegistryName(string statsKey, int kills, int deaths)
        {
            if (string.IsNullOrEmpty(statsKey) || statsKey == "?" || _server == null)
                return;

            if (!_updateBotStatsResolved)
            {
                _updateBotStatsResolved = true;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType("InfServer.Logic.Logic_BotStats");
                    if (t == null)
                        continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "updateBotStats")
                            continue;
                        _cachedUpdateBotStats = m;
                        break;
                    }
                    if (_cachedUpdateBotStats != null)
                        break;
                }
            }

            if (_cachedUpdateBotStats == null)
                return;

            try
            {
                _cachedUpdateBotStats.Invoke(null, new object[] { statsKey, kills, deaths, _server });
            }
            catch
            {
                // Stats are best-effort; never stall death routing on DB reflection failure.
            }
        }

        /// <summary>
        /// Triggered when a vehicle dies
        /// </summary>
        [Scripts.Event("Bot.Death")]
        public bool botDeath(Bots.Bot dead, InfServer.Game.Player killer, int weaponID)
        {
            // Rewards + death counter first so MaxDeaths / alien wave slots see the updated count.
            if (killer != null && dead != null && dead._team != killer._team)
            {
                calculateBotKillRewards(dead, killer);
                OnPlayerKilledBot(killer, dead);
            }

            Bots.Bot killerBot = null;
            if (killer == null && dead != null)
                _arena.TryTakeLastBotDamager(dead, out killerBot);

            if (dead != null)
            {
                EolBotFieldAccess.IncrementDeaths(dead);
                string key = BotNameModule.GetDisplayName(dead);
                if (!string.IsNullOrEmpty(key) && key != "?")
                    TryUpdateBotStatsByRegistryName(key, 0, 1);
            }

            if (killerBot != null)
            {
                EolBotFieldAccess.IncrementKills(killerBot);
                string killerKey = BotNameModule.GetDisplayName(killerBot);
                if (!string.IsNullOrEmpty(killerKey) && killerKey != "?")
                    TryUpdateBotStatsByRegistryName(killerKey, 1, 0);

                // Bot-on-bot kill taunt at the victim's death site (not on the killer's own death).
                if (dead != null && dead._team != killerBot._team && dead._state != null)
                    QueueBotKillTauntExplosion(killerBot, dead._state.positionX, dead._state.positionY);
            }

            OnAlienHordeBotDeath(dead, killer);

            // Captain death alone must not wipe the squad while the HQ is still up — only clear captain
            // tracking and arm respawn. Grunts stay until HQ dies (WipeDefenseBotTeamAssets / prune).
            // Anchor-respawn captains keep their slot until revive or cancel.
            if (dead is Captain && dead._team != null && IsDefenseBotTeam(dead._team)
                && !(dead.bRespawn && CanBotUseDropshipRespawn(dead)))
            {
                if (captainBots != null)
                    captainBots.Remove(dead._team);
                if (TeamHasLiveHqVehicle(dead._team))
                    SyncDefenseBotCount(dead._team);
                _tickDefenseCaptainRespawnEligible = Environment.TickCount + DefenseCaptainRespawnDelayMs;
            }

            // Killfeed is queued server-side in ScriptArena.handleBotDeath; death FX is immediate there.

            if (dead != null)
            {
                // Deathmatch: after MaxDeaths kills, stop reviving and drop from the game.
                if (dead.HasExhaustedDeaths)
                {
                    dead.bRespawn = false;
                    dead.bDropshipRespawnPending = false;
                    // Drop the team-list name immediately — do not wait for corpse RemoveDeadTimer.
                    _arena?.RemoveBotFromNamelist(dead);
                    if (dead is RoamingCaptain && !IsBountyHunterBotTeam(dead._team))
                        SyncRaiderCaptainCountFromOccupying();
                }

                if (dead.bRespawn && CanBotUseDropshipRespawn(dead))
                {
                    // Corpse stays at death site ~10s, then revive at HQ / captain / raid / crash anchor.
                    BeginBotDropshipRespawn(dead, Environment.TickCount);
                }
                else
                {
                    dead.bDropshipRespawnPending = false;
                    FreeAlienWaveSlot(dead);
                    NoteWaveBotEliminatedForTicker(dead);
                    if (_bots != null)
                        _bots.Remove(dead);
                    ForgetScriptBotBookkeeping(dead);
                    // Non-revive deaths (incl. MaxDeaths) — ensure leave even if handleBotDeath races.
                    if (!dead.bDropshipRespawnPending)
                        _arena?.RemoveBotFromNamelist(dead);
                    if (dead is RoamingCaptain && !IsBountyHunterBotTeam(dead._team))
                        SyncRaiderCaptainCountFromOccupying();
                }
            }
            return false;
        }

        /// <summary>
        /// Called when a teams points have been modified
        /// </summary>
        public void onPointModify(Team team, int points)
        {
            //Update the tickers
            updateTickers();

            //Check for game victory here
            if (points >= _points.MaxPoints)
                //They were the first team to reach max points!
                gameVictory(team);
        }

        static bool IsEolModerator(InfServer.Game.Player player)
        {
            return player != null && player.PermissionLevelLocal >= Data.PlayerPermission.Mod;
        }

        /// <summary>Called when a player sends a mod command (<c>*command</c>).</summary>
        [Scripts.Event("Player.ModCommand")]
        public bool playerModCommand(InfServer.Game.Player player, InfServer.Game.Player recipient, string command, string payload)
        {
            string cmd = command.ToLowerInvariant();
            if (cmd.Equals("startaliens"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                string err = TryModForceStartAlienWave();
                player.sendMessage(0, err == null ? "&Alien crash wave started." : ("&" + err));
                return true;
            }
            if (cmd.Equals("startraiders"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                string err = TryModForceStartRaiders();
                player.sendMessage(0, err == null ? "&Pirate raiders deployed." : ("&" + err));
                return true;
            }
            if (cmd.Equals("stopaliens"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                string err = TryModStopAlienWave();
                player.sendMessage(0, err == null ? "&Alien wave stopped." : ("&" + err));
                return true;
            }
            if (cmd.Equals("disablealiens"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModDisableAlienWaves();
                player.sendMessage(0, "&Alien waves disabled.");
                return true;
            }
            if (cmd.Equals("enablealiens"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModEnableAlienWaves();
                player.sendMessage(0, "&Alien waves enabled.");
                return true;
            }
            if (cmd.Equals("stopraiders"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                string err = TryModStopRaiders();
                player.sendMessage(0, err == null ? "&Pirate raid cleared." : ("&" + err));
                return true;
            }
            if (cmd.Equals("disableraiders"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModDisablePirateRaids();
                player.sendMessage(0, "&Pirate raids disabled.");
                return true;
            }
            if (cmd.Equals("enableraiders"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModEnablePirateRaids();
                player.sendMessage(0, "&Pirate raids enabled.");
                return true;
            }
            if (cmd.Equals("startkoth"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                string err = TryModStartKoth();
                player.sendMessage(0, err == null ? "&KOTH started." : ("&" + err));
                return true;
            }
            if (cmd.Equals("endkoth"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                string err = TryModEndKoth();
                player.sendMessage(0, err == null ? "&KOTH ended or lobby cleared." : ("&" + err));
                return true;
            }
            if (cmd.Equals("disablekoth"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModDisableKoth();
                player.sendMessage(0, "&KOTH system disabled.");
                return true;
            }
            if (cmd.Equals("enablekoth"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModEnableKoth();
                player.sendMessage(0, "&KOTH system enabled.");
                return true;
            }
            if (cmd.Equals("stopbasebots"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                string err = TryModStopBaseBots();
                player.sendMessage(0, err == null ? "&Base defense bots cleared." : ("&" + err));
                return true;
            }
            if (cmd.Equals("disablebasebots"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModDisableBaseBots();
                player.sendMessage(0, "&Base defense bots disabled.");
                return true;
            }
            if (cmd.Equals("enablebasebots"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModEnableBaseBots();
                player.sendMessage(0, "&Base defense bots enabled.");
                return true;
            }
            if (cmd.Equals("disablebasesetup"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModDisableBaseSetup();
                player.sendMessage(0, "&Bot HQ auto-placement from templates disabled (existing bases unchanged).");
                return true;
            }
            if (cmd.Equals("enablebasesetup"))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                TryModEnableBaseSetup();
                player.sendMessage(0, "&Bot HQ auto-placement from templates enabled.");
                return true;
            }
            if (cmd.Length == 11 && cmd.StartsWith("opensector", StringComparison.Ordinal))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                char letter = cmd[10];
                string err = _eol != null ? _eol.TryModOpenSectorByLetter(letter) : "Eol boundaries not initialized.";
                player.sendMessage(0, err == null ? ("&" + char.ToUpperInvariant(letter) + " opened (merged with population tiers).") : ("&" + err));
                return true;
            }
            if (cmd.Length == 12 && cmd.StartsWith("closesector", StringComparison.Ordinal))
            {
                if (!IsEolModerator(player))
                {
                    player.sendMessage(-1, "Moderators only.");
                    return true;
                }
                char letter = cmd[11];
                string err = _eol != null ? _eol.TryModCloseSectorByLetter(letter) : "Eol boundaries not initialized.";
                player.sendMessage(0, err == null ? ("&" + char.ToUpperInvariant(letter) + " closed (stays out until reopened or match reset).") : ("&" + err));
                return true;
            }

            return false;
        }

        /// <summary>Called when a player sends a chat command (<c>?command</c>).</summary>
        [Scripts.Event("Player.ChatCommand")]
        public bool playerChatCommand(InfServer.Game.Player player, InfServer.Game.Player recipient, string command, string payload)
        {
            if (command.ToLower().Equals("crown"))
            {   //Give them their crown time if KOTH is enabled
                if (_minPlayers <= 0)
                    player.sendMessage(0, "&KOTH is not enabled in this zone");

                else
                    if (_playerCrownStatus.ContainsKey(player))
                    player.sendMessage(0, "&Crown kills: " + _playerCrownStatus[player].crownKills);
            }

            if (command.ToLower().Equals("co"))
            {
                player.sendMessage(0, "X: " + player._state.positionX + " Y: " + player._state.positionY);
            }

            if (command.ToLower().Equals("arena"))
            {
                player.sendMessage(0, "&No Private Arena are can be used in Eol: Pioneer Station");
                return false;
            }

            return true;
        }
        /// <summary>
        /// Called when a player enters the arena
        /// </summary>
        [Scripts.Event("Player.EnterArena")]
        public void playerEnter(InfServer.Game.Player player)
        {
            //Send them the crowns if KOTH is enabled
            if (_minPlayers > 0)
                if (_playerCrownStatus == null)
                    _playerCrownStatus = new Dictionary<InfServer.Game.Player, PlayerCrownStatus>();

            if (!_playerCrownStatus.ContainsKey(player))
            {
                _playerCrownStatus[player] = new PlayerCrownStatus(false);
                RoutePlayerCrownsToClient(_arena, true, _activeCrowns, player);
            }

            //Add them to the list if its not in it
            if (!killStreaks.ContainsKey(player._alias))
            {
                PlayerStreak temp = new PlayerStreak();
                temp.lastKillerCount = 0;
                temp.lastUsedWeap = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                killStreaks.Add(player._alias, temp);
            }

            ClampBountyToZero(player);

            player.sendMessage(4,
                      "#This zone is currently in Beta testing. While it's under development, Please wipecharacter if you need starting cash and exp " +
                      "please leave any bug finds or feedback on our discord, in the Eol Channel. Thanks!");

            /*if (_bRoam)
                player.sendMessage(0, "&A Pirate Roam is currently roaming Eol, Enjoy!");
            else
            {
                TimeSpan remaining = timeTo(_RoamStart);
                player.sendMessage(0, String.Format("&A Pirate Roam will roam in {0} hours & {1} minutes", remaining.Hours, remaining.Minutes));
            }*/
        }

        int BountyStartValue()
        {
            if (_arena != null && _arena._server != null && _arena._server._zoneConfig != null)
                return _arena._server._zoneConfig.bounty.start;
            return 0;
        }

        /// <summary>
        /// Persist live bounty into CFG Name6 / ZoneStat7. Never overwrite a stored bounty with
        /// the engine's unspec/leave 0 — only a death clears the column.
        /// </summary>
        void SaveBountyToStat(InfServer.Game.Player player)
        {
            if (player == null)
                return;
            if (player._deathTime != 0)
            {
                if (player.ZoneStat7 != 0)
                    player.ZoneStat7 = 0;
                return;
            }
            int start = BountyStartValue();
            int live = player.Bounty;
            if (live < 0)
                live = 0;
            // Keep the higher of live bounty and already-stored so spec/leave 0 cannot wipe it.
            int persist = live > player.ZoneStat7 ? live : player.ZoneStat7;
            if (persist < 0)
                persist = 0;
            if (persist <= start)
            {
                if (player.ZoneStat7 < 0)
                    player.ZoneStat7 = 0;
                return;
            }
            if (player.ZoneStat7 != persist)
                player.ZoneStat7 = persist;
        }

        /// <summary>CFG Name6 / ZoneStat7 and live bounty cannot be negative — floor both at 0.</summary>
        void ClampBountyToZero(InfServer.Game.Player player)
        {
            if (player == null)
                return;
            if (player.ZoneStat7 < 0)
                player.ZoneStat7 = 0;
            if (player.Bounty < 0)
                player.Bounty = 0;
        }

        /// <summary>Reload persisted bounty onto the player after unspec (engine sets Bounty = start).</summary>
        void RestoreBountyFromStat(InfServer.Game.Player player)
        {
            if (player == null || player._deathTime != 0)
                return;
            ClampBountyToZero(player);
            int start = BountyStartValue();
            if (player.ZoneStat7 > start && player.Bounty <= start)
                player.Bounty = player.ZoneStat7;
        }

        void SyncIngameBountyStats()
        {
            if (_arena == null)
                return;
            int start = BountyStartValue();
            foreach (InfServer.Game.Player p in _arena.Players)
            {
                if (p == null)
                    continue;
                ClampBountyToZero(p);
                if (p.IsSpectator || p._deathTime != 0)
                    continue;
                RestoreBountyFromStat(p);
                if (p.Bounty > start && p.ZoneStat7 != p.Bounty)
                    p.ZoneStat7 = p.Bounty;
            }
        }

        /// <summary>
        /// Called when a player leaves the game
        /// </summary>
        [Scripts.Event("Player.Leave")]
        public void playerLeave(InfServer.Game.Player player)
        {//Find out if KOTH is enabled
            SaveBountyToStat(player);
            // Spec ends the hunt so pots wipe; unspec can start a fresh hunt.
            OnBountyHuntPlayerSpec(player);
            if (_kothGameRunning)
            {
                if (_playerCrownStatus != null && _playerCrownStatus.ContainsKey(player))
                {
                    _playerCrownStatus[player].crown = false;
                    QueueKothCrownBroadcast(false, _noCrowns);
                }
            }
            if (_playerCrownStatus != null)
                _playerCrownStatus.Remove(player);
            _lastKothCrownTickerSec.Remove(player);
            _lastCtfPersonalScoreTicker.Remove(player);
            if (_startTeams != null)
                _startTeams.Remove(player);
            ClearCombatCallsForPlayer(player);
            if (_healingDone != null)
                _healingDone.Remove(player);
            if (killStreaks != null && player != null && !string.IsNullOrEmpty(player._alias))
                killStreaks.Remove(player._alias);
            // Keep player-built computers/walls on the map (team-owned). Clear creator so reconnect
            // quotas work; they are removed on sector rotation (or bot-base wipe if defense layout).
            if (_arena != null)
            {
                foreach (Vehicle v in _arena.Vehicles)
                {
                    if (v == null || v._type == null)
                        continue;
                    if (v._type.Type != VehInfo.Types.Computer || v._creator != player)
                        continue;
                    v._creator = null;
                }
            }

        }

        /// <summary>
        /// Handles a player's portal request. Always returns <c>false</c> so the server does not run
        /// <c>Logic_Lio.Warp</c> (LIO warp group on the portal). Warps use <see cref="findOpenWarp"/> into
        /// <see cref="EolBoundaries._activeSectors"/> only; locked paths return <c>false</c> with no warp.
        /// </summary>
        [Scripts.Event("Player.Portal")]
        public bool playerPortal(InfServer.Game.Player player, LioInfo.Portal portal)
        {
            List<Arena.FlagState> carried = new List<Arena.FlagState>();
            foreach (Arena.FlagState flag in _arena._flags.Values)
            {
                if (flag.carrier == player)
                    carried.Add(flag);
            }

            foreach (Arena.FlagState carry in carried)
            {   //If the terrain number is 0-15

                int terrainNum = player._arena.getTerrainID(player._state.positionX, player._state.positionY);
                if (terrainNum >= 0 && terrainNum <= 15)
                {   //Check the FlagDroppableTerrains for that specific terrain id

                    if (carry.flag.FlagData.FlagDroppableTerrains[terrainNum] == 0)
                        _arena.flagResetPlayer(player);
                }
            }

            if (_eol.bbetweengames || !_arena._bGameRunning || !_eol._gameBegun)
            {
                player.sendMessage(-1, "The dropship is locked until a match is running and safe sectors are open.");
                return false;
            }

            _eol.EnsureActiveSectorsReadyForPortal();
            if (_eol._activeSectors == null || _eol._activeSectors.Count == 0)
            {
                Log.write(TLog.Warning, "[Eol] Portal: no active sectors for warp ({0}).", player._alias);
                player.sendMessage(-1, "The dropship is locked until radiation sectors are assigned for this match.");
                return false;
            }

            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            // Sample across the full sector AABB each try (dropship exits were clustering near sector center).
            const int portalWarpAttempts = 100;
            var sectors = _eol._activeSectors;

            Helpers.ObjectState warpPoint = null;
            for (int attempt = 0; attempt < portalWarpAttempts; attempt++)
            {
                string sec = sectors[_rand.Next(sectors.Count)];
                short minX;
                short maxX;
                short minY;
                short maxY;
                if (!_eol.TryGetSectorBounds(sec, out minX, out maxX, out minY, out maxY))
                    continue;

                warpPoint = findOpenWarpInAxisAlignedRect(_arena, minX, maxX, minY, maxY, 1);
                if (warpPoint != null && _eol.IsInAnyActiveSector(warpPoint.positionX, warpPoint.positionY))
                    break;
                warpPoint = null;
            }

            if (warpPoint != null)
            {
                warp(player, warpPoint);
                return false;
            }

            Log.write(TLog.Warning, "[Eol] Portal: no clear tile in active sectors ({0}).", player._alias);
            player.sendMessage(-1, "Couldn't find a clear landing zone — try again in a moment.");
            return false;
        }

        /// <summary>
        /// Triggered when a vehicle is created
        /// </summary>
        [Scripts.Event("Vehicle.Creation")]
        public bool vehicleCreation(Vehicle created, Team team, InfServer.Game.Player creator)
        {
            if (_eolBorderWallTeam != null && team == _eolBorderWallTeam
                && (created._type.Id == EolBorderWallVehicleHorizontal || created._type.Id == EolBorderWallVehicleVertical))
            {
                if (_eolBorderWallVehicles == null)
                    _eolBorderWallVehicles = new List<Vehicle>();
                _eolBorderWallVehicles.Add(created);
                return true;
            }

            //Are they trying to create a headquarters?
            if (created._type.Id == _hqVehId)
            {
                // The engine adds the new vehicle to _arena.Vehicles BEFORE firing this event, so a plain
                // TeamHasLiveHqVehicle(team) check would match the HQ we're creating right now and wrongly
                // destroy it. Only treat it as a duplicate if the team has ANOTHER live HQ besides this one.
                if (!TeamHasOtherLiveHqVehicle(team, created))
                {
                    team.sendArenaMessage("&Headquarters - Your team has created a headquarters at " + Helpers.posToLetterCoord(created._state.positionX, created._state.positionY));
                }
                else
                {
                    if (creator != null)
                        creator.sendMessage(-1, "Your team already has a headquarters");
                    created.destroy(false, true);
                    return false;
                }

                // Captain deploy runs from PollCaptainSpawning / PollEngineerSpawning after all vehicles
                // are created — not here (iterating Vehicles during Vehicle.Creation throws).
            }

            return true;
        }

        /// <summary>
        /// Triggered when a vehicle dies
        /// </summary>
        [Scripts.Event("Vehicle.Death")]
        public bool vehicleDeath(Vehicle dead, InfServer.Game.Player killer)
        {

            //Did they just kill an HQ?!
            if (dead._type.Id == _hqVehId)
            {
                if (dead._state != null && IsDefenseBotTeam(dead._team))
                {
                    _lastDestroyedBotHqX = dead._state.positionX;
                    _lastDestroyedBotHqY = dead._state.positionY;
                    _hasLastDestroyedBotHq = true;
                }

                OnDefenseBotHqDestroyed(dead._team, dead);

                if (killer == null)
                    return true;

                Team killers = killer._team;

                //Check if it was a team kill
                if (dead._team == killer._team)
                {   //Cheaters! Reward the last people to hurt the vehicle if it exists
                    IEnumerable<InfServer.Game.Player> attackers = dead._attackers;
                    attackers.Reverse();
                    foreach (InfServer.Game.Player p in attackers)
                        if (p._team != dead._team)
                            killers = p._team;

                    //Did we find a suitable killer?
                    if (killers == dead._team)
                    {   //Nope! Looks like nobody has ever hit their HQ... do nothing I guess.
                        _arena.sendArenaMessage("&Headquarters - " + killers._name + " destroyed their own HQ.");
                        return true;
                    }
                }

                if (dead._team != killers && IsDefenseBotTeam(dead._team))
                {
                    foreach (InfServer.Game.Player p in killers.ActivePlayers)
                    {
                        int scaledCash = ApplyEolLifetimeRewardScale(p, EolBotHqDestroyCashPerPlayer);
                        int scaledExp = ApplyEolLifetimeRewardScale(p, EolBotHqDestroyExpPerPlayer);
                        int scaledPoints = ApplyEolLifetimeRewardScale(p, EolBotHqDestroyPointsPerPlayer);
                        p.BonusPoints += scaledPoints;
                        p.Cash += scaledCash;
                        p.Experience += scaledExp;
                        p.sendMessage(0, string.Format("&Headquarters - Your team destroyed {0}'s bot HQ! Cash={1} Experience={2} Points={3}",
                            dead._team._name, scaledCash, scaledExp, scaledPoints));
                    }
                }

                //Notify the rest of the arena
                foreach (Team t in _arena.Teams)
                {
                    if (t == killers)
                        continue;
                    if (IsDefenseBotTeam(dead._team) && dead._team != killers)
                        t.sendArenaMessage("&Headquarters - " + dead._team._name + " bot HQ was destroyed by " + killers._name + "!");
                    else
                        t.sendArenaMessage("&Headquarters - " + dead._team._name + " HQ was destroyed by " + killers._name + "!");
                }

                if (dead._team == botTeam1 || dead._team == botTeam2 || dead._team == botTeam3)
                {
                    if (_currentEngineers > 0)
                        _currentEngineers--;
                    if (engineerBots != null)
                        engineerBots.Remove(dead._team);
                }
            }
            return true;
        }

        /// <summary>
        /// Triggered when a player has died, by any means
        /// </summary>
        /// <remarks>killer may be null if it wasn't a player kill</remarks>
        [Scripts.Event("Player.Death")]
        public bool playerDeath(InfServer.Game.Player victim, InfServer.Game.Player killer, Helpers.KillType killType, CS_VehicleDeath update)
        {
            if (victim != null && victim.ZoneStat7 != 0)
                victim.ZoneStat7 = 0;
            ClearCombatCallsForPlayer(victim);
            _pendingBotDeathUpdate = update;

            // Mark died to non-player (or unresolved) — forfeit assassin pot. PvP handled in playerPlayerKill.
            if (victim != null && _bountyHuntMarked == victim
                && (killer == null || killType != Helpers.KillType.Player))
            {
                OnBountyHuntMarkDiedOther(victim);
            }

            if (killer == null)
                return true;

            try
            {

            //Was it a player kill?
            if (killType == Helpers.KillType.Player)
            {   //No team killing!
                if (victim._team != killer._team)
                {
                    if (_points != null)
                        _points[killer._team] += _pointSmallChange;
                }
                // Crowns / streaks: Player.PlayerKill (avoid double-counting).
                return true;
            }

            //Was it a computer kill?
            if (killType == Helpers.KillType.Computer)
            {
                Computer cvehicle = null;
                foreach (Vehicle v in victim._arena.Vehicles)
                {
                    if (v._id == update.killerPlayerID)
                    {
                        cvehicle = v as Computer;
                        break;
                    }
                }
                if (cvehicle != null && cvehicle._creator != null)
                {
                    InfServer.Game.Player vehKiller = cvehicle._creator;
                    update.type = Helpers.KillType.Player;
                    update.killerPlayerID = vehKiller._id;

                    //Don't reward for teamkills
                    if (vehKiller._team == victim._team)
                        Logic_Assets.RunEvent(vehKiller, _arena._server._zoneConfig.EventInfo.killedTeam);
                    else
                        Logic_Assets.RunEvent(vehKiller, _arena._server._zoneConfig.EventInfo.killedEnemy);

                    Logic_Rewards.calculatePlayerKillRewards(victim, vehKiller, update);
                    return false;
                }
            }

            // Non-PvP death (bot/env) — streak end announce if needed.
            UpdateDeath(victim, killer);
            return true;

            }
            finally
            {
                // Player.BotKill runs after Death for soft-mimic bots — keep coords for GG/EZ taunts.
                if (killer != null)
                    _pendingBotDeathUpdate = null;
            }

        }
        /// <summary>
        /// Triggered when one player has killed another
        /// </summary>
        [Scripts.Event("Player.PlayerKill")]
        public bool playerPlayerKill(InfServer.Game.Player victim, InfServer.Game.Player killer)
        {
            // Eol owns cash/exp/points (lifetime + happy hour). Return false so ScriptArena
            // does not also run Logic_Rewards (would double-pay / double killfeed).
            // Killfeed comes once from calculatePlayerKillRewards' Player_RouteKill.

            if (killStreaks.ContainsKey(victim._alias))
            {
                long wepTick = killStreaks[victim._alias].lastUsedWepTick;
                if (wepTick != -1)
                    UpdateWeaponKill(killer);
            }

            if (victim._team == killer._team)
            {
                Logic_Assets.RunEvent(victim, _arena._server._zoneConfig.EventInfo.killedTeam);
                // Own the kill path (return false) — still need one killfeed, no rewards.
                CS_VehicleDeath tkUpdate = new CS_VehicleDeath(0, new byte[0], 0, 0);
                tkUpdate.killedID = victim._id;
                tkUpdate.killerPlayerID = killer._id;
                tkUpdate.positionX = victim._state.positionX;
                tkUpdate.positionY = victim._state.positionY;
                tkUpdate.type = Helpers.KillType.Player;
                foreach (InfServer.Game.Player p in _arena.Players)
                {
                    if (p != null)
                        Helpers.Player_RouteKill(p, tkUpdate, victim, 0, 0, 0, 0);
                }
            }
            else
            {
                Logic_Assets.RunEvent(victim, _arena._server._zoneConfig.EventInfo.killedEnemy);
                calculatePlayerKillRewards(victim, killer);
            }

            OnBountyHuntPlayerKill(victim, killer);

            killer.Kills++;
            victim.Deaths++;

            UpdateKiller(killer);
            UpdateDeath(victim, killer);

            if (_arena != null)
            {
                _arena.flagHandleDeath(victim, killer);
                if (killer.IsDead)
                    _arena.flagResetPlayer(killer);
                _arena.ballResetPlayer(victim, killer);
            }

            if (_kothGameRunning && _activeCrowns.Count == 0)
                return false;

            if (_kothGameRunning)
            {
                if (_playerCrownStatus[victim].crown)
                {
                    _playerCrownStatus[victim].crownDeaths++;

                    if (_playerCrownStatus[victim].crownDeaths >= _config.king.deathCount)
                    {
                        _playerCrownStatus[victim].crown = false;
                        QueueKothCrownBroadcast(false, _noCrowns);
                    }

                    if (!_playerCrownStatus[killer].crown)
                        _playerCrownStatus[killer].crownKills++;
                }

                if (_playerCrownStatus[killer].crown)
                    updateCrownTime(killer);
                else if (_config.king.crownRecoverKills != 0)
                {
                    if (_playerCrownStatus[killer].crownKills >= _config.king.crownRecoverKills)
                    {
                        _playerCrownStatus[killer].crown = true;
                        giveCrown(killer);
                    }
                }
            }
            return false;
        }


        /// <summary>
        /// Called when the statistical breakdown is displayed
        /// </summary>
        [Scripts.Event("Player.Breakdown")]
        public bool playerBreakdown(InfServer.Game.Player from, bool bCurrent)
        {   //Allows additional "custom" breakdown information
            //List the best healers by sorting them according to healingdone
            from.sendMessage(0, "#Healer Breakdown");
            if (_healingDone != null && _healingDone.Count > 0)
            {
                List<KeyValuePair<InfServer.Game.Player, int>> healers = _healingDone.ToList();
                healers.Sort((a, b) => { return a.Value.CompareTo(b.Value); });
                healers.Reverse();

                int i = 1;
                foreach (KeyValuePair<InfServer.Game.Player, int> healer in healers)
                {   //Display top 3 healers in arena
                    from.sendMessage(0, String.Format("!{0} (Healed={1}): {2}",
                        ScriptHelpers.toOrdinal(i), healer.Value, healer.Key._alias));
                    if (i++ > 3)
                        break;
                }
            }

            //List teams by most points
            from.sendMessage(0, "#Team Breakdown");
            if (_points != null)
            {
                List<Team> teams = new List<Team>();
                foreach (Team t in _arena.Teams)
                {
                    if (_points[t] != 0)
                        teams.Add(t);
                }
                teams.Sort((Team a, Team b) => b._currentGameKills.CompareTo(a._currentGameKills));

                int j = 1;
                foreach (Team t in teams)
                {
                    from.sendMessage(0, String.Format("!{0} (Points={1} Kills={2} Deaths={3}): {4}",
                        ScriptHelpers.toOrdinal(j), _points[t], t._currentGameKills, t._currentGameDeaths, t._name));
                    j++;
                }
            }

            from.sendMessage(0, "#Player Breakdown");
            int k = 1;
            List<InfServer.Game.Player> breakdownPlayers = new List<InfServer.Game.Player>();
            foreach (InfServer.Game.Player pl in _arena.PlayersIngame)
                breakdownPlayers.Add(pl);
            breakdownPlayers.Sort((InfServer.Game.Player a, InfServer.Game.Player b) =>
            {
                int ka = bCurrent ? a.StatsCurrentGame.kills : a.StatsLastGame.kills;
                int kb = bCurrent ? b.StatsCurrentGame.kills : b.StatsLastGame.kills;
                return kb.CompareTo(ka);
            });
            foreach (InfServer.Game.Player p in breakdownPlayers)
            {   //Display top 3 players
                from.sendMessage(0, String.Format("!{0} (K={1} D={2}): {3}",
                    ScriptHelpers.toOrdinal(k),
                    (bCurrent ? p.StatsCurrentGame.kills : p.StatsLastGame.kills),
                    (bCurrent ? p.StatsCurrentGame.deaths : p.StatsLastGame.deaths),
                    p._alias));
                if (k++ > 3)
                    break;
            }
            //Display his score
            from.sendMessage(0, String.Format("@Personal Score: (K={0} D={1})",
                (bCurrent ? from.StatsCurrentGame.kills : from.StatsLastGame.kills),
                (bCurrent ? from.StatsCurrentGame.deaths : from.StatsLastGame.deaths)));

            //return true to avoid another breakdown from showing
            return true;
        }


        /// <summary>
        /// Triggered when a player requests to pick up an item
        /// </summary>
        [Scripts.Event("Player.ItemPickup")]
        public bool playerItemPickup(InfServer.Game.Player player, Arena.ItemDrop drop, ushort quantity)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player requests to drop an item
        /// </summary>
        [Scripts.Event("Player.ItemDrop")]
        public bool playerItemDrop(InfServer.Game.Player player, ItemInfo item, ushort quantity)
        {
            return true;
        }

        /// <summary>
        /// Handles a player's produce request
        /// </summary>
        [Scripts.Event("Player.Produce")]
        public bool playerProduce(InfServer.Game.Player player, Computer computer, VehInfo.Computer.ComputerProduct product)
        {
            return true;
        }

        /// <summary>
        /// Handles a player's switch request
        /// </summary>
        [Scripts.Event("Player.Switch")]
        public bool playerSwitch(InfServer.Game.Player player, LioInfo.Switch swi)
        {
            // Sector seam switches (910-913) are driven by code only - never let a player toggle them.
            if (swi != null && swi.GeneralData.Id >= 910 && swi.GeneralData.Id <= 913)
                return false;
            return true;
        }

        /// <summary>
        /// Handles a player's flag request
        /// </summary>
        [Scripts.Event("Player.FlagAction")]
        public bool playerFlagAction(InfServer.Game.Player player, bool bPickup, bool bInPlace, LioInfo.Flag flag)
        {
            return true;
        }

        /// <summary>
        /// Handles the spawn of a player
        /// </summary>
        [Scripts.Event("Player.Spawn")]
        public bool playerSpawn(InfServer.Game.Player player, bool bDeath)
        {
            if (bDeath && player != null)
            {
                // Engine then sets Bounty = start (0). Clear the persisted column so they cannot reclaim it.
                if (player.ZoneStat7 != 0)
                    player.ZoneStat7 = 0;
            }
            return true;
        }

        /// <summary>
        /// Called when a player enters the arena
        /// </summary>
        [Scripts.Event("Player.EnterArena")]
        public void playerEnterArena(InfServer.Game.Player player)
        {
            EnsureKillStreak(player);

            //Send them the crowns..
            if (!_playerCrownStatus.ContainsKey(player))
            {
                _playerCrownStatus[player] = new PlayerCrownStatus(false);
                RoutePlayerCrownsToClient(_arena, true, _activeCrowns, player);
            }

        }
        /// <summary>
        /// Triggered when a player is about to join a team. Return false to cancel.
        /// </summary>
        [Scripts.Event("Player.JoinTeam")]
        public bool playerJoinTeam(InfServer.Game.Player player, Team team, bool manualJoin)
        {
            if (team == null || string.IsNullOrEmpty(team._name))
                return true;

            // Players must not join managed bot frequencies (Base / Raiders / Aliens / BHB).
            if (team._name.StartsWith("Bot", StringComparison.OrdinalIgnoreCase))
            {
                if (player != null)
                    player.sendMessage(-1, "You cannot join bot teams.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Triggered when a player wants to unspec and join the game
        /// </summary>
        [Scripts.Event("Player.JoinGame")]
        public bool playerJoinGame(InfServer.Game.Player player)
        {
            // Still spectator here — stash live bounty before unspec sets Bounty = start.
            SaveBountyToStat(player);
            ClampBountyToZero(player);
            EnsureKillStreak(player);

            //Find if player has more than 2 skills and remove them, giving back money spent and defaulting to conscript
            List<InfServer.Game.Player.SkillItem> removes = new List<InfServer.Game.Player.SkillItem>();
            foreach (InfServer.Game.Player.SkillItem skl in player._skills.Values)
            {
                if (skl.skill.SkillId >= 0)
                    removes.Add(skl);
            }
            if (removes.Count >= 2)
            {
                foreach (InfServer.Game.Player.SkillItem skl in removes)
                {
                    player._skills.Remove(skl.skill.SkillId);
                }
                player.Cash += 15000;
                player.sendMessage(0, String.Format("Reset skill to Conscript and refunded $15000 due to having multiple classes"));
                SkillInfo skill = _arena._server._assets.getSkillByName("Conscript");
                if (skill != null)
                    player.skillModify(skill, 1);
                player.syncState();
            }

            if (_kothGameRunning)
            {
                if (!_startTeams.ContainsKey(player))
                {
                    _startTeams[player] = null;
                }
            }

            return true;
        }

        /// <summary>
        /// Triggered when a player wants to spec and leave the game
        /// </summary>
        [Scripts.Event("Player.Enter")]
        public void playerEnterGame(InfServer.Game.Player player)
        {
            SaveBountyToStat(player);
        }

        /// <summary>Runs after engine sets Bounty = start on unspec — put the saved bounty back.</summary>
        [Scripts.Event("Player.Unspec")]
        public void playerUnspec(InfServer.Game.Player player)
        {
            RestoreBountyFromStat(player);
            // Spec ended any prior hunt; if they still qualify, start a fresh one with empty pots.
            OnBountyHuntPlayerUnspec(player);

            // Safety net: CFG exitSpectatorMode warp can miss when team id / LIO filters fail,
            // leaving the player at 0,0 or free-cam coords. Always place at Pioneer Station.
            if (player != null && !player.IsSpectator && _eol != null)
                _eol.TryWarpPlayerToDropship(player, announce: false);
        }

        [Scripts.Event("Player.LeaveArena")]
        public void playerLeaveArena(InfServer.Game.Player player)
        {
            SaveBountyToStat(player);
            if (_bountyHuntMarked == player)
                AdvanceBountyHuntFromMark("target left arena", applyRemarkCooldown: true, Environment.TickCount);
            else
                RemovePlayerFromBountyHuntQueue(player);
        }

        [Scripts.Event("Player.LeaveGame")]
        public bool playerLeaveGame(InfServer.Game.Player player)
        {
            SaveBountyToStat(player);
            // Going to spectator ends the hunt (fresh pots on next unspec if still eligible).
            OnBountyHuntPlayerSpec(player);
            if (_playerCrownStatus != null && _playerCrownStatus.ContainsKey(player))
            {
                _playerCrownStatus[player].crown = false;
                QueueKothCrownBroadcast(false, _noCrowns);
                _playerCrownStatus.Remove(player);
                _lastKothCrownTickerSec.Remove(player);
                _lastCtfPersonalScoreTicker.Remove(player);
            }
            if (_startTeams != null)
                _startTeams.Remove(player);
            return true;
        }
        /// <summary>
        /// Triggered when a player wants to enter a vehicle
        /// </summary>
        [Scripts.Event("Player.EnterVehicle")]
        public bool playerEnterVehicle(InfServer.Game.Player player, Vehicle vehicle)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player wants to leave a vehicle
        /// </summary>
        [Scripts.Event("Player.LeaveVehicle")]
        public bool playerLeaveVehicle(InfServer.Game.Player player, Vehicle vehicle)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player notifies the server of an explosion
        /// </summary>
        [Scripts.Event("Player.Explosion")]
        public bool playerExplosion(InfServer.Game.Player player, ItemInfo.Projectile weapon, short posX, short posY, short posZ)
        {
            // Player fire near a bot — that bot's team can call for help even before it acquires the player.
            if (player != null && !player.IsDead && _arena != null)
            {
                int now = Environment.TickCount;
                List<Vehicle> near = _arena.getVehiclesInRange(posX, posY, CombatCallAssistRange,
                    delegate (Vehicle v) { return v is Bot; });
                for (int i = 0; i < near.Count; i++)
                {
                    Bot bot = near[i] as Bot;
                    if (bot == null || bot.IsDead || bot._team == null)
                        continue;
                    if (player._team != null && bot._team == player._team)
                        continue;
                    PublishCombatCall(bot._team, bot, player, null, now);
                }
            }
            return true;
        }

        /// <summary>
        /// Triggered when a computer vehicle has killed a player
        /// </summary>
        [Scripts.Event("Player.ComputerKill")]
        public bool playerComputerKill(InfServer.Game.Player victim, Computer computer)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player attempts to use a warp item
        /// </summary>
        [Scripts.Event("Player.WarpItem")]
        public bool playerWarpItem(InfServer.Game.Player player, ItemInfo.WarpItem item, ushort targetPlayerID, short posX, short posY)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player attempts to use a warp item
        /// </summary>
        [Scripts.Event("Player.MakeVehicle")]
        public bool playerMakeVehicle(InfServer.Game.Player player, ItemInfo.VehicleMaker item, short posX, short posY)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player attempts to use a warp item
        /// </summary>
        [Scripts.Event("Player.MakeItem")]
        public bool playerMakeItem(InfServer.Game.Player player, ItemInfo.ItemMaker item, short posX, short posY)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player uses a repair item
        /// </summary>
        [Scripts.Event("Player.Repair")]
        public bool playerRepair(InfServer.Game.Player player, ItemInfo.RepairItem item, UInt16 targetVehicle, short posX, short posY)
        {
            int healamount = 0;
            //Let's try to credit him for the heal
            if (item.repairType == 0 || item.repairType == 2)
            {   //It's a player heal!
                if (item.repairDistance > 0)
                    //Credit him for a single heal
                    healamount = (item.repairAmount == 0) ? item.repairPercentage : item.repairAmount;
                else if (item.repairDistance < 0)
                    //Credit him for everybody he healed
                    healamount = (item.repairAmount == 0)
                        ? item.repairPercentage * _arena.getPlayersInRange(player._state.positionX, player._state.positionY, -item.repairDistance).Count
                        : item.repairAmount * _arena.getPlayersInRange(player._state.positionX, player._state.positionY, -item.repairDistance).Count;
            }

            //Keep track of it, mang
            if (_healingDone != null)
                if (_healingDone.ContainsKey(player))
                    _healingDone[player] += healamount;
                else
                    _healingDone.Add(player, healamount);
            return true;
        }

        /// <summary>
        /// Triggered when a player is buying an item from the shop
        /// </summary>
        [Scripts.Event("Shop.Buy")]
        public bool shopBuy(InfServer.Game.Player patron, ItemInfo item, int quantity)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player is selling an item to the shop
        /// </summary>
        [Scripts.Event("Shop.Sell")]
        public bool shopSell(InfServer.Game.Player patron, ItemInfo item, int quantity)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player requests a skill from skill screen (F11)
        /// </summary>
        [Scripts.Event("Shop.SkillRequest")]
        public bool shopSkillRequest(InfServer.Game.Player player, SkillInfo skill)
        {
            return true;
        }

        /// <summary>
        /// Triggered when a player successfully purchases a skill item from skill screen (F11)
        /// </summary>
        [Scripts.Event("Shop.SkillPurchase")]
        public void shopSkillPurchase(InfServer.Game.Player player, SkillInfo skill)
        {   // Class/attr purchase — do not tear down team computers (persist until sector rotate).
        }

        /// <summary>
        /// Triggered when a bot has killed a player
        /// </summary>
        [Scripts.Event("Player.BotKill")]
        public bool playerBotKill(InfServer.Game.Player victim, Bots.Bot bot)
        {
            string botName = bot != null ? bot._alias : "Bot";
            if (string.IsNullOrEmpty(botName))
                botName = BotNameModule.GetKillfeedName(bot);

            UpdateDeath(victim, null, botName);

            victim.ZoneStat12++;
            if (bot != null)
            {
                EolBotFieldAccess.IncrementKills(bot);
                string key = BotNameModule.GetDisplayName(bot);
                if (!string.IsNullOrEmpty(key) && key != "?")
                    TryUpdateBotStatsByRegistryName(key, 1, 0);
            }

            CS_VehicleDeath u = _pendingBotDeathUpdate;
            if (bot != null && victim != null && victim._team != bot._team)
                QueueBotKillTauntExplosion(bot, victim, u);

            _pendingBotDeathUpdate = null;

            // true = ScriptArena BroadcastPlayerKilledByBot + Player_RouteKill
            return true;
        }

        public void pickTeam(InfServer.Game.Player player)
        {
            List<Team> potentialTeams = new List<Team>();
            foreach (Team t in _arena.ActiveTeams)
            {
                if (t._name.StartsWith("Public") && t.ActivePlayerCount < 1)
                    potentialTeams.Add(t);
            }

            if (potentialTeams.Count == 0)
            {
                foreach (Team t in _arena.PublicTeams)
                {
                    if (t._name.StartsWith("Public") && t.ActivePlayerCount == 0)
                    {
                        t.addPlayer(player);
                        return;
                    }
                }
            }
            else
                potentialTeams[0].addPlayer(player);
        }

        #endregion
    }
}