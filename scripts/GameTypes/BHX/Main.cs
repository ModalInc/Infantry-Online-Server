using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Scripting;
using InfServer.Bots;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_BHX
{	// Script Class
	/// Provides the interface between the script and arena
	///////////////////////////////////////////////////////
	public partial class GameType_BHX : Scripts.IScript
	{	///////////////////////////////////////////////////
		// Member Variables
		///////////////////////////////////////////////////
		private Arena _arena;					//Pointer to our arena class
		private CfgInfo _config;				//The zone config
        public BotHandler _botHandler;

        public Team skrall;
        public Team marines;

        private int _jackpot;					//The game's jackpot so far
        private LioInfo.WarpField _lastMarineSpawn;
		private Team _victoryTeam;				//The team currently winning!
		private int _tickVictoryStart;			//The tick at which the victory countdown began
		private int _tickNextVictoryNotice;		//The tick at which we will next indicate imminent victory
        private Random _rand;
		private int _lastGameCheck;				//The tick at which we last checked for game viability
		private int _tickGameStarting;			//The tick at which the game began starting (0 == not initiated)
		private int _tickGameStart;				//The tick at which the game started (0 == stopped)
        private int _maxPlayerSkrall = 0;
        private int _tickLastSpawnChange;
        private int _tickLastHiveSpawn;
        private bool bJackPot;
        private int _tickLastRadarDot;

        //Object Collections
        private List<Nest> _nests;
        private List<Objective> _objectives;
        public Dictionary<string, PlayerStat> _savedPlayerStats;
        private List<SupplyDrop> _supplyDrops;
        public List<ElevatorPanel> _elevatorPanels;

        private Dictionary<Player, Team> _sideSelection;        //Each player's team selection. Cleared each game.

		//Settings
		private int _minPlayers;				//The minimum amount of players
        public double _scientistMultiplier = 1.0;
        public double _marinesMultiplier = 1.0;
        public double _skrallMultiplier = 1.0;

        public Dictionary<int, Arena.FlagState> _flags;

        ///////////////////////////////////////////////////
        // Member Functions
        ///////////////////////////////////////////////////

        #region Init and Polling
        /// <summary>
        /// Performs script initialization
        /// </summary>
        public bool init(IEventObject invoker)
		{	//Populate our variables
			_arena = invoker as Arena;
			_config = _arena._server._zoneConfig;
            _botHandler = new BotHandler(_arena);
            _nests = new List<Nest>();
            _savedPlayerStats = new Dictionary<string, PlayerStat>();
            _sideSelection = new Dictionary<Player, Team>();
            _rand = new Random();
            _supplyDrops = new List<SupplyDrop>();
            _elevatorPanels = new List<ElevatorPanel>();

            skrall = _arena.getTeamByName("Skrall");
            marines = _arena.getTeamByName("Marines");

            _objectives = new List<Objective>();
            _minPlayers = Int32.MaxValue;

			foreach (Arena.FlagState fs in _arena._flags.Values)
			{	//Determine the minimum number of players
				if (fs.flag.FlagData.MinPlayerCount < _minPlayers)
					_minPlayers = fs.flag.FlagData.MinPlayerCount;

				//Register our flag change events
				fs.TeamChange += onFlagChange;
			}

            if (_minPlayers == Int32.MaxValue)
                //No flags? Run blank games
                _minPlayers = 1;
			return true;
		}

		/// <summary>
		/// Allows the script to maintain itself
		/// </summary>
		public bool poll()
		{	//Should we check game state yet?
			int now = Environment.TickCount;

			if (now - _lastGameCheck <= Arena.gameCheckInterval)
				return true;
			_lastGameCheck = now;

			//Do we have enough players ingame?
			int playing = _arena.PlayerCount;

            _maxPlayerSkrall = (int)(playing);

			if ((_tickGameStart == 0 || _tickGameStarting == 0) && playing < _minPlayers)
			{	//Stop the game!
				_arena.setTicker(1, 1, 0, "Not Enough Players");
                bJackPot = false;
				_arena.gameReset();
			}
			//Do we have enough players to start a game?
			else if (_tickGameStart == 0 && _tickGameStarting == 0 && playing >= _minPlayers)
			{	//Great! Get going
				_tickGameStarting = now;
                _arena.sendArenaMessage("A game is starting soon, You will be placed on the Marine team by default unless you type ?skrall. Teams will be set when the game begins");
				_arena.setTicker(1, 1, _config.flag.startDelay * 100, "Next game: ",
					delegate()
					{	//Trigger the game start
						_arena.gameStart();
					}
				);
			}
            
			//Is anybody experiencing a victory?
			if (_tickVictoryStart != 0)
			{	//Have they won yet?
                if (now - _tickVictoryStart > (_config.flag.victoryHoldTime * 10) && _victoryTeam != null)
                {
                    //Yes! Trigger game victory
                    _arena.sendArenaMessage("Marines have captured all the data cores and are headed back to Titan, far far away from this hell.", 21);
                    gameVictory(_victoryTeam);
                }
			}

            if (_arena._bGameRunning)
            {

                foreach (Objective obj in _objectives)
                    obj.poll(now);

                if (now - _tickLastSpawnChange >= 30000)
                {
                    _lastMarineSpawn = null;
                    _tickLastSpawnChange = now;
                }


                updateTickers(now);


                bool bMarineVictory = true;

                foreach (Objective obj in _objectives)
                {

                    //Check for a victory
                    if (obj.bObjectiveRescued == false)
                    {
                        bMarineVictory = false;
                        break;
                    }
                }

                if (bMarineVictory)
                {
                    _victoryTeam = marines;
                    _arena.sendArenaMessage("Marines have won! All of the scientists have been rescued");
                    _arena.gameEnd();
                }

                if (now - _tickLastRadarDot >= 1000)
                {
                    radarPing(now);
                    _tickLastRadarDot = now;
                }

            }


            foreach (Nest nest in _nests)
                nest.poll(now);

            foreach (SupplyDrop drop in _supplyDrops)
                drop.poll(now);

           

            return true;
		}
        #endregion

        #region Events
        /// <summary>
        /// Called when a flag changes team
        /// </summary>
        public void onFlagChange(Arena.FlagState flag)
		{	//Does this team now have all the flags?
            Team victoryTeam = flag.team;

            foreach (Arena.FlagState fs in _arena._flags.Values)
                if (fs.bActive && fs.team != victoryTeam)
                    victoryTeam = null;

            //Annouce the flag captured to arena
            try
            {
                _arena.sendArenaMessage(flag.team._name + " have captured " + flag.flag.GeneralData.Name + "!", 21);
            }
            catch (Exception e)
            {
                Log.write(TLog.Exception, "exception in capture flag:: '{0}'",e);
            }
            if (victoryTeam != null)
            {	//Yes! Victory for them!
                _arena.setTicker(1, 1, _config.flag.victoryHoldTime, "Victory in ");
                _tickNextVictoryNotice = _tickVictoryStart = Environment.TickCount;
                _victoryTeam = victoryTeam;
            }
		}

		/// <summary>
		/// Called when the specified team have won
		/// </summary>
		public void gameVictory(Team victors)
		{
            //Stop the game
            try
            {
                _arena.gameEnd();
            }
            catch (Exception e)
            {
                Log.write(TLog.Warning, "Ending game with Marines winning" + e);
            }
		}

        public void radarPing(int now)
        {
            addsensorhelmet();
            IEnumerable<Vehicle> vehicles = _arena.Vehicles.Where(v => v._type.Id == 207);
            foreach (Vehicle veh in vehicles)
            {
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != veh._team & p.getInventoryAmount(1577) >= 1);
                Helpers.Player_RouteExplosion(enemies, 1130, veh._state.positionX, veh._state.positionY, 0, 0, 0);
            }

            IEnumerable<Vehicle> vehicles2 = _arena.Vehicles.Where(v => v._type.Id == 203);
            foreach (Vehicle veh in vehicles2)
            {
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != veh._team & p.getInventoryAmount(1577) >= 1);
                Helpers.Player_RouteExplosion(enemies, 1130, veh._state.positionX, veh._state.positionY, 0, 0, 0);
            }

            IEnumerable<Vehicle> vehicles3 = _arena.Vehicles.Where(v => v._type.Id == 202);
            foreach (Vehicle veh in vehicles3)
            {
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != veh._team & p.getInventoryAmount(1577) >= 1);
                Helpers.Player_RouteExplosion(enemies, 1130, veh._state.positionX, veh._state.positionY, 0, 0, 0);
            }

            IEnumerable<Vehicle> vehicles4 = _arena.Vehicles.Where(v => v._type.Id == 201);
            foreach (Vehicle veh in vehicles4)
            {
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != veh._team & p.getInventoryAmount(1577) >= 1);
                Helpers.Player_RouteExplosion(enemies, 1130, veh._state.positionX, veh._state.positionY, 0, 0, 0);
            }

            IEnumerable<Vehicle> vehicles5 = _arena.Vehicles.Where(v => v._type.Id == 106);
            foreach (Vehicle veh in vehicles5)
            {
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != veh._team & p.getInventoryAmount(1577) >= 1);
                Helpers.Player_RouteExplosion(enemies, 1130, veh._state.positionX, veh._state.positionY, 0, 0, 0);
            }

            IEnumerable<Vehicle> vehicles6 = _arena.Vehicles.Where(v => v._type.Id == 204);
            foreach (Vehicle veh in vehicles6)
            {
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != veh._team & p.getInventoryAmount(1577) >= 1);
                Helpers.Player_RouteExplosion(enemies, 1130, veh._state.positionX, veh._state.positionY, 0, 0, 0);
            }

            IEnumerable<Vehicle> vehicles7 = _arena.Vehicles.Where(v => v._type.Id == 200);
            foreach (Vehicle veh in vehicles7)
            {
                IEnumerable<Player> enemies = _arena.Players.Where(p => p._team != veh._team & p.getInventoryAmount(1577) >= 1);
                Helpers.Player_RouteExplosion(enemies, 1130, veh._state.positionX, veh._state.positionY, 0, 0, 0);
            }

        }

        #endregion

        #region Game Start/End/Reset

        /// <summary>
        /// Called when the game begins
        /// </summary>
        [Scripts.Event("Game.Start")]
        public bool gameStart()
        {   //We've started!
            _tickGameStart = Environment.TickCount;
            _tickGameStarting = 0;
            _lastMarineSpawn = null;
            bJackPot = true;

            foreach (var selection in _sideSelection)
            {
                Player player = selection.Key;
                Team teamWanted = selection.Value;

                if (player.IsSpectator)
                    continue;

                //When respawning
                //player.inventoryModify(1036, 1);

                //if (skrall.ActivePlayerCount >= _maxPlayerSkrall && teamWanted == skrall)
                //{
                //    player.sendMessage(-1, "There is the maximum amount of skrall for the current number of marines. The Marine Corp needs you!");
                //    if (player._team != marines)
                //        marines.addPlayer(player);

                //    continue;
                //}

                if (player._team == teamWanted)
                    continue;

                //Add him to his selection.
                teamWanted.addPlayer(player);
            }

            //Spawn our objects
            spawnHives();
            spawnScientists();
            spawnElevatorPanels();

            //Let everyone know
            _arena.sendArenaMessage("Game has started!", _config.flag.resetBong);

            //Send the marines some orders
            marines.sendArenaMessage("$*******Orders*******Orders*******Orders*******Orders*******Orders*******");
            marines.sendArenaMessage("&From: Echelon 1, 4th Platoon | Directive Dated:  4/21/2110");
            marines.sendArenaMessage("*The Skrall have overrun the research facility. We have reports of survivors within the complex. " +
                "Your mission is to enter the facility, rescue all of the survivors & bring them back to the DZ for extraction.");
            marines.sendArenaMessage("*Their locations have been uplinked to your radar.");
            marines.sendArenaMessage("*If you succeed, Compensation will be generous. Good luck!");

            //Alert any skrall
            skrall.sendArenaMessage("@These puny humans think they can exterminate us? Wake up brethren! Dinner is served.");

            _arena.setTicker(1, 1, 1800 * 100, "Time Left: ", delegate ()
            {	//Trigger game end.
                //Skrall have won
                _arena.sendArenaMessage("Skrall have resumed supreme control of the base, the marines have become nothing more than bones and bits.", 21);
                _victoryTeam = skrall;

                try
                {

                    _arena.gameEnd();
                }
                catch (Exception e)
                {
                    Log.write(TLog.Warning, "_arena.gameEnd() " + e);
                }
            }
            );


            _savedPlayerStats.Clear();
            foreach (Player p in _arena.Players)
            {

                //When respawning?
                //p.inventoryModify(1036, 1);

                //Biologist
                if (p.findSkill(-8) != null && p.ActiveVehicle._type.Name.Contains("Medic") /* restrict bonus to class */)
                {
                    p.inventoryModify(27, 1);
                    p.inventoryModify(1075, 1);
                }
                //Eagle-Eye
                if (p.findSkill(-9) != null && p.ActiveVehicle._type.Name.Contains("Scout") /* restrict bonus to class */)
                {
                    p.inventoryModify(1015, 1);
                    p.inventoryModify(2000, 10);
                }

                PlayerStat temp = new PlayerStat();
                temp.player = p;
                temp.teamname = p._team._name;
                temp.alias = p._alias;
                temp.points = 0;
                temp.assistPoints = 0;
                temp.bonusPoints = 0;
                temp.killPoints = 0;
                temp.playSeconds = 0;
                temp.kills = 0;
                temp.deaths = 0;
                temp.player = p;
                temp.hasPlayed = p.IsSpectator ? false : true;
                if (!p.IsSpectator)
                {
                    if (p._baseVehicle != null)
                        temp.classType = p._baseVehicle._type.Name;
                }

                temp.lastKillerCount = 0;
                temp.lastUsedWep = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                temp.potentialHealthHealed = 0;
                temp.onPlayingField = false;

                _savedPlayerStats.Add(p._alias, temp);
            }
            return true;
        }


        /// <summary>
        /// Called when the game ends
        /// </summary>
        [Scripts.Event("Game.End")]
        public bool gameEnd()
        {   //Game finished, perhaps start a new one
            _tickGameStart = 0;
            _tickGameStarting = 0;
            _tickVictoryStart = 0;
            _tickNextVictoryNotice = 0;


            Jackpot jackpot = Rewards.calculateJackpot(_savedPlayerStats.Values, _victoryTeam, false);

            //Are we counting this game for rewards?
            if (!bJackPot)
                return true;

            _arena.sendArenaMessage(String.Format("&Jackpot: {0}", jackpot.totalJackPot));

            List<Jackpot.Reward> rankers = new List<Jackpot.Reward>();

            foreach (Jackpot.Reward reward in jackpot._playerRewards.Values.OrderByDescending(r => r.MVP))
            {
                if (reward.Score == 0)
                    continue;

                rankers.Add(reward);
            }

            int idx = 3;
            foreach (Jackpot.Reward reward in rankers)
            {
                if (reward.player == null) continue;

                if (idx-- <= 0)
                    break;

                string placeWord = "&3rd";
                string format = " (MVP Percentage={0}%): {1}";
                switch (idx)
                {
                    case 2:
                        placeWord = "&1st";
                        break;
                    case 1:
                        placeWord = "&2nd";
                        break;
                }

                _arena.sendArenaMessage(string.Format(placeWord + format, Math.Round(reward.MVP * 100, 2), reward.player._alias));
            }

            idx = 1;
            foreach (Jackpot.Reward reward in rankers)
            {
                if (reward.player == null) continue;


                //reward.cash = Rewards.addCash(reward.player, reward.cash);
                reward.player.sendMessage(0, String.Format("Your personal Jackpot: (MVP={0}% Rank={1} Score={2}) Rewards: (Experience={3} Points={4})",
                Math.Round(reward.MVP * 100, 2), idx, reward.Score, reward.experience, reward.points));
                reward.player.Experience += reward.experience;
                reward.player.BonusPoints += reward.points;

                _victoryTeam = null;
            }

            _sideSelection.Clear();

            //Default everyone to marines
            foreach (Player player in _arena.Players)
                _sideSelection.Add(player, marines);


            return true;
        }

        /// <summary>
        /// Called when the statistical breakdown is displayed
        /// </summary>
        [Scripts.Event("Game.Breakdown")]
        public bool breakdown()
        {	
            //Allows additional "custom" breakdown information


            //Always return true;
            return true;
        }

		/// <summary>
		/// Called to reset the game state
		/// </summary>
		[Scripts.Event("Game.Reset")]
		public bool gameReset()
		{	//Game reset, perhaps start a new one
			_tickGameStart = 0;
			_tickGameStarting = 0;
			_tickVictoryStart = 0;
			_tickNextVictoryNotice = 0;

			_victoryTeam = null;

			return true;
		}
        #endregion

        #region Player Events
        public void addsensorhelmet()
        {
            foreach (Player player in _arena.Players)
            {
                if (player.findSkill(-20) != null)
                {
                    if (player.getInventoryAmount(1577) == 0)
                        player.inventoryModify(1577, 1);
                }
            }
        }
        /// <summary>
        /// Called when a player sends a chat command
        /// </summary>
        [Scripts.Event("Player.ChatCommand")]
        public bool playerChatCommand(Player player, Player recipient, string command, string payload)
        {
            if (command.Equals("skrall"))
            {
                _sideSelection[player] = skrall;
                player.sendMessage(0, "You have changed your team selection, You'll be switched at the start of the next game or the next time you unspec.");
            }
            if (command.Equals("marines"))
            {
                _sideSelection[player] = marines;
                player.sendMessage(0, "You have changed your team selection, You'll be switched at the start of the next game or the next time you unspec.");
            }

            return true;
        }

        /// <summary>
        /// Triggered only when a special mod command created here that isn't a server command.
        /// </summary>
        [Scripts.Event("Player.ModCommand")]
        public bool playerModcommand(Player player, Player recipient, string command, string payload)
        {

            if (command.Equals("supply"))
            {
                spawnSupplyDrop(player._state.positionX, player._state.positionY);
            }

            if (command.Equals("skrallmulti"))
            {
                if (!Double.TryParse(payload, out _skrallMultiplier))
                    return false;

                player.sendMessage(-1, String.Format("Skrall Multiplier set to {0}", _skrallMultiplier));
            }

            if (command.Equals("marinemulti"))
            {
                if (!Double.TryParse(payload, out _marinesMultiplier))
                    return false;

                player.sendMessage(-1, String.Format("Marine Multiplier set to {0}", _marinesMultiplier));
            }

            if (command.Equals("scimulti"))
            {
                if (!Double.TryParse(payload, out _scientistMultiplier))
                    return false;

                player.sendMessage(-1, String.Format("Scientist Multiplier set to {0}", _scientistMultiplier));
            }

            if (command.Equals("newbot"))
            {
                int vehID = 0;


                if (!Int32.TryParse(payload, out vehID))
                    return false;

                Configuration.BotType botType = (Configuration.BotType)vehID;

                if (vehID == 105)
                {
                    Bot bot = _botHandler.newScientist(player._state, marines, marines, player, null);
                    _botHandler._marines.Add(bot);
                    return false;
                }
                _botHandler.newBot(botType, player._state, skrall, marines, null, null);
            }
            return false;
        }

        /// <summary>
        /// Called when a player enters the game
        /// </summary>
        [Scripts.Event("Player.Enter")]
        public void playerEnter(Player player)
        {
            //Destroy all vehicles belonging to him
            foreach (Vehicle v in _arena.Vehicles)
                if (v._type.Type == VehInfo.Types.Computer && v._creator == player)
                    //Destroy it!
                    v.destroy(true);

            //Add them to the list if not in it
            if (!_savedPlayerStats.ContainsKey(player._alias))
            {
                PlayerStat temp = new PlayerStat();
                temp.player = player;
                temp.assistPoints = 0;
                temp.bonusPoints = 0;
                temp.killPoints = 0;
                temp.points = 0;
                temp.playSeconds = 0;
                temp.alias = player._alias;
                temp.deaths = 0;
                temp.kills = 0;
                temp.player = player;
                temp.onPlayingField = false;
                _savedPlayerStats.Add(player._alias, temp);
            }
            _savedPlayerStats[player._alias].teamname = player._team._name;
            _savedPlayerStats[player._alias].hasPlayed = player.IsSpectator ? false : true;

            if (player._baseVehicle != null)
                _savedPlayerStats[player._alias].classType = player._baseVehicle._type.Name;

            //Only when unspecing
            //player.inventoryModify(1036, 1);

            //if (skrall.ActivePlayerCount > _maxPlayerSkrall && player._team == skrall)
            //{
            //    player.sendMessage(-1, "There is the maximum amount of skrall for the current number of marines. The Marine Corp needs you!");
            //    if (player._team != marines)
            //        marines.addPlayer(player);
            //}
        }

        /// <summary>
        /// Called when a player leaves the game
        /// </summary>
        [Scripts.Event("Player.Leave")]
        public void playerLeave(Player player)
        {
        }

        public void gameSoon()
        {
        }

        /// <summary>
        /// Triggered when a player requests to pick up an item
        /// </summary>
        [Scripts.Event("Player.ItemPickup")]
		public bool playerItemPickup(Player player, Arena.ItemDrop drop, ushort quantity)
		{
            switch (drop.item.id)
            {
                //Scavenger Pack
                case 3:
                    {
                        Logic_Assets.RunEvent(player, player.ActiveVehicle._type.EventString3);

						//player.sendMessage(0, "DEBUG Scavenger Box: "+);

                        //Rocketeer
                        if (player.findSkill(-7) != null && player.ActiveVehicle._type.Name.Contains("Heavy Weapons") /* restrict bonus to class */)
                        {
                            player.inventoryModify(140, 1);
                        }
                        //Grenadier
                        if (player.findSkill(-7) != null && player.ActiveVehicle._type.Name.Contains("Marine") /* restrict bonus to class */)
                        {
                            player.inventoryModify(1423, 1);
                        }    

                    }
                        break;
            }
			return true;
		}

		/// <summary>
		/// Triggered when a player requests to drop an item
		/// </summary>
		[Scripts.Event("Player.ItemDrop")]
		public bool playerItemDrop(Player player, ItemInfo item, ushort quantity)
		{
			return true;
		}

		/// <summary>
		/// Handles a player's portal request
		/// </summary>
		[Scripts.Event("Player.Portal")]
		public bool playerPortal(Player player, LioInfo.Portal portal)
		{
            if (portal.GeneralData.Id == 87)
            {
                List<LioInfo.WarpField> warps = _arena._server._assets.Lios.getWarpGroupByID(portal.PortalData.DestinationWarpGroup);

                if (_lastMarineSpawn == null)
                {
                    LioInfo.WarpField warp = warps[_rand.Next(0, warps.Count)];

                    LvlInfo level = player._server._assets.Level;

                    int x = warp.GeneralData.OffsetX - (level.OffsetX * 16);
                    int y = warp.GeneralData.OffsetY - (level.OffsetY * 16);

                    //Resolve our box
                    short height = (short)(warp.GeneralData.Height / 2);
                    short width = (short)(warp.GeneralData.Width / 2);

                    //Check for an available spot
                    //This fixes warping onto physics
                    int attempts = 0;
                    for (; attempts < 10; attempts++)
                    {
                        short px = (short)x;
                        short py = (short)y;
                        if (!player._arena.getTile(px, py).Blocked)
                            break;

                        Helpers.randomPositionInArea(player._arena, ref px, ref py, width, height);
                    }

                    //Use our first warp!
                    player.warp(Helpers.ResetFlags.ResetNone,
                        (short)-1,
                        (short)(x - width), (short)(y - height),
                        (short)(x + width), (short)(y + height),
                        (short)0);

                    _lastMarineSpawn = warp;
                    return false;
                }
                else
                {
                    LioInfo.WarpField warp = _lastMarineSpawn;

                    LvlInfo level = player._server._assets.Level;

                    int x = warp.GeneralData.OffsetX - (level.OffsetX * 16);
                    int y = warp.GeneralData.OffsetY - (level.OffsetY * 16);

                    //Resolve our box
                    short height = (short)(warp.GeneralData.Height / 2);
                    short width = (short)(warp.GeneralData.Width / 2);

                    //Check for an available spot
                    //This fixes warping onto physics
                    int attempts = 0;
                    for (; attempts < 10; attempts++)
                    {
                        short px = (short)x;
                        short py = (short)y;
                        if (!player._arena.getTile(px, py).Blocked)
                            break;

                        Helpers.randomPositionInArea(player._arena, ref px, ref py, width, height);
                    }

                    //Use our first warp!
                    player.warp(Helpers.ResetFlags.ResetNone,
                        (short)-1,
                        (short)(x - width), (short)(y - height),
                        (short)(x + width), (short)(y + height),
                        (short)0);

                    _lastMarineSpawn = warp;
                    return false;
                }
            }

            if (portal.GeneralData.Id == 12 || portal.GeneralData.Id == 14)
            {
                Helpers.ObjectState warpTo = new Helpers.ObjectState();

                if (!findSpawnLocation(ref warpTo, 1, 80))
                    return true;

                player.warp(warpTo.positionX, warpTo.positionY);

                return false;
            }
             
			return true;
		}

		/// <summary>
		/// Handles a player's produce request
		/// </summary>
		[Scripts.Event("Player.Produce")]
		public bool playerProduce(Player player, Computer computer, VehInfo.Computer.ComputerProduct product)
		{

            switch (computer._type.Name)
            {
                case "Supply Drop":
                    {
                        if (product.Title == "Open Supplies")
                        {
                            SupplyDrop drop = _supplyDrops.First(s => s._computer == computer);

                            if (drop == null)
                                return true;

                            drop.open(player);
                            _supplyDrops.Remove(drop);
                        }
                    }
                    break;
                case "Elevator Control Panel":
                    {
                        ElevatorPanel panel = _elevatorPanels.FirstOrDefault(pnl => pnl._switch == computer);

                        if (panel == null)
                            return false;

                        panel.tryPanel(player, computer, product);

                        return false;
                    }
            }

            //Scientist NPC
            if (computer._type.Id == 403)
            {
                Objective objective = _objectives.FirstOrDefault(ob => ob._npc == computer);

                if (objective == null)
                    return false;

                objective._botScientist = _botHandler.newScientist(computer._state, marines, marines, player, objective);
                objective.bObjectiveFollowing = true;
                computer.destroy(false);
                return false;
            }

			return true;
		}

		/// <summary>
		/// Handles a player's switch request
		/// </summary>
		[Scripts.Event("Player.Switch")]
		public bool playerSwitch(Player player, LioInfo.Switch swi)
		{
            return true;
		}

		/// <summary>
		/// Handles a player's flag request
		/// </summary>
		[Scripts.Event("Player.FlagAction")]
		public bool playerFlagAction(Player player, bool bPickup, bool bInPlace, LioInfo.Flag flag)
		{
			return true;
		}

		/// <summary>
		/// Handles the spawn of a player
		/// </summary>
		[Scripts.Event("Player.Spawn")]
		public bool playerSpawn(Player player, bool bDeath)
		{
            //if (skrall.ActivePlayerCount > _maxPlayerSkrall && player._team == skrall)
            //{
            //    player.sendMessage(-1, "There is the maximum amount of skrall for the current number of marines. The Marine Corp needs you!");
            //    if (player._team != marines)
            //        marines.addPlayer(player);
            //}

            //Never?
            //player.inventoryModify(1036, 1);

            ////if (player._team == marines && player.findSkill(-8) != null && player.findSkill(2) != null && player._baseVehicle._type.ClassId == 1)

            ////Biologist
            //if (player.findSkill(-8) != null && player.ActiveVehicle._type.Name.Contains("Medic") /* restrict bonus to class */)
            //{
            //    player.inventoryModify(27, 1);
            //}
            ////Eagle-Eye
            //if (player.findSkill(-9) != null && player.ActiveVehicle._type.Name.Contains("Scout") /* restrict bonus to class */)
            //{
            //    player.inventoryModify(1015, 1);
            //    player.inventoryModify(2000, 10);
            //}

            return true;
		}

        /// <summary>
        /// Called when a player enters the arena
        /// </summary>
        [Scripts.Event("Player.EnterArena")]
        public void playerEnterArena(Player player)
        {
            if (!_sideSelection.ContainsKey(player))
                _sideSelection[player] = marines;
        }

        /// <summary>
        /// Triggered when a player wants to unspec and join the game
        /// </summary>
        [Scripts.Event("Player.JoinGame")]
		public bool playerJoinGame(Player player)
		{
            if (_sideSelection.ContainsKey(player))
                player.unspec(_sideSelection[player]);

                //Only when unspecing
                //player.inventoryModify(1036, 1);

                ////Biologist
                //if (player.findSkill(-8) != null && player.ActiveVehicle._type.Name.Contains("Medic") /* restrict bonus to class */)
                //{
                //    player.inventoryModify(27, 1);
                //}
                ////Eagle-Eye
                //if (player.findSkill(-9) != null && player.ActiveVehicle._type.Name.Contains("Scout") /* restrict bonus to class */)
                //{
                //    player.inventoryModify(1015, 1);
                //    player.inventoryModify(2000, 10);
                //}

            return true;
		}

		/// <summary>
		/// Triggered when a player wants to spec and leave the game
		/// </summary>
		[Scripts.Event("Player.LeaveGame")]
		public bool playerLeaveGame(Player player)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player wants to enter a vehicle
		/// </summary>
		[Scripts.Event("Player.EnterVehicle")]
		public bool playerEnterVehicle(Player player, Vehicle vehicle)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player wants to leave a vehicle
		/// </summary>
		[Scripts.Event("Player.LeaveVehicle")]
		public bool playerLeaveVehicle(Player player, Vehicle vehicle)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player notifies the server of an explosion
		/// </summary>
		[Scripts.Event("Player.Explosion")]
		public bool playerExplosion(Player player, ItemInfo.Projectile weapon, short posX, short posY, short posZ)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player has died, by any means
		/// </summary>
		/// <remarks>killer may be null if it wasn't a player kill</remarks>
		[Scripts.Event("Player.Death")]
		public bool playerDeath(Player victim, Player killer, Helpers.KillType killType, CS_VehicleDeath update)
		{
            //Respawn flag where it was initially spawn if a marine died carrying one
            try
            {
                _arena.flagResetPlayer(victim);
            }
            catch (Exception e)
            {
                Log.write(TLog.Exception, "exception in flagResetPlayer(victim):: '{0}'", e);
            }
            //Was it a computer kill?
            if (killType == Helpers.KillType.Computer)
            {
                //Let's find the vehicle!
                Computer cvehicle = victim._arena.Vehicles.FirstOrDefault(v => v._id == update.killerPlayerID) as Computer;
                Player vehKiller = cvehicle._creator;
                //Does it exist?
                if (cvehicle != null && vehKiller != null)
                {
                    //We'll take it from here...
                    update.type = Helpers.KillType.Player;
                    update.killerPlayerID = vehKiller._id;

                    //Don't reward for teamkills
                    if (vehKiller._team == victim._team)
                        Logic_Assets.RunEvent(vehKiller, _arena._server._zoneConfig.EventInfo.killedTeam);
                    else
                        Logic_Assets.RunEvent(vehKiller, _arena._server._zoneConfig.EventInfo.killedEnemy);

                    //Increase stats and notify arena of the kill!
                    vehKiller.Kills++;
                    victim.Deaths++;
                    Logic_Rewards.calculatePlayerKillRewards(victim, vehKiller, update);
                    return false;
                }
            }        
            return true;
		}

		/// <summary>
		/// Triggered when one player has killed another
		/// </summary>
		[Scripts.Event("Player.PlayerKill")]
		public bool playerPlayerKill(Player victim, Player killer)
		{
            if (_savedPlayerStats.ContainsKey(killer._alias))
                _savedPlayerStats[killer._alias].kills++;

            if (_savedPlayerStats.ContainsKey(victim._alias))
                _savedPlayerStats[victim._alias].deaths++;

            return true;
		}

		/// <summary>
		/// Triggered when a bot has killed a player
		/// </summary>
		[Scripts.Event("Player.BotKill")]
		public bool playerBotKill(Player victim, Bot bot)
		{
            if (_savedPlayerStats.ContainsKey(victim._alias))
                _savedPlayerStats[victim._alias].deaths++;

            victim.Deaths++;
            return true;
        }

        /// <summary>
        /// Triggered when a bot dies to a player
        /// </summary>
        [Scripts.Event("Bot.Death")]
        public bool botDeath(Bot victim, Player killer, int weaponID)
        {
            if (killer != null && _savedPlayerStats.ContainsKey(killer._alias) && killer._team != victim._team)
            {
                    Helpers.Vehicle_RouteDeath(_arena.Players, killer, victim, null);
                    if (killer != null && victim._team != killer._team)
                    {//Don't allow rewards for team kills
                        Rewards.calculateBotKillRewards(victim, killer);
                    }

                    killer.Kills++;
                    _savedPlayerStats[killer._alias].kills++;
            }


            //Let's check for any players in the nearby area with special traits
            IEnumerable<Player> playersInRange = _arena.getPlayersInRange(victim._state.positionX, victim._state.positionY, 500);
            foreach (Player player in playersInRange)
            {
                //Scavenger
                if (player.findSkill(-5) != null)
                {
                    _arena.itemSpawn(AssetManager.Manager.getItemByID(3), 1, victim._state.positionX, victim._state.positionY, null);
                    break;
                }
            }

            //Queen
            if (victim._type.Id == 106)
            {
                int hatchlingCount = 8;

                for (int i = 1; i <= hatchlingCount; i++)
                    _botHandler.newBot(Configuration.BotType.Arachnomorph, victim._state, victim._team, _arena.getTeamByName("Marines"), null, null);
            }

            return false;
        }

        /// <summary>
        /// Triggered when a computer vehicle has killed a player
        /// </summary>
        [Scripts.Event("Player.ComputerKill")]
		public bool playerComputerKill(Player victim, Computer computer)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player attempts to use a warp item
		/// </summary>
		[Scripts.Event("Player.WarpItem")]
		public bool playerWarpItem(Player player, ItemInfo.WarpItem item, ushort targetPlayerID, short posX, short posY)
		{                
			return true;
		}

		/// <summary>
		/// Triggered when a player attempts to use a warp item
		/// </summary>
		[Scripts.Event("Player.MakeVehicle")]
		public bool playerMakeVehicle(Player player, ItemInfo.VehicleMaker item, short posX, short posY)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player attempts to use a warp item
		/// </summary>
		[Scripts.Event("Player.MakeItem")]
		public bool playerMakeItem(Player player, ItemInfo.ItemMaker item, short posX, short posY)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player is buying an item from the shop
		/// </summary>
		[Scripts.Event("Shop.Buy")]
		public bool shopBuy(Player patron, ItemInfo item, int quantity)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a player is selling an item to the shop
		/// </summary>
		[Scripts.Event("Shop.Sell")]
		public bool shopSell(Player patron, ItemInfo item, int quantity)
		{
			return true;
		}

        /// <summary>
        /// Triggered when a player requests a skill from skill screen (F11)
        /// </summary>
        [Scripts.Event("Shop.SkillRequest")]
        public bool shopSkillRequest(Player player, SkillInfo skill)
        {
            //When buying a skill
            //player.inventoryModify(1036, 1);
            return true;
        }

        /// <summary>
        /// Triggered when a player successfully purchases a skill item from skill screen (F11)
        /// </summary>
        [Scripts.Event("Shop.SkillPurchase")]
        public void shopSkillPurchase(Player player, SkillInfo skill)
        {   //Is it a class or an attribute?
            if (skill.SkillId >= 0)
            {   //It's a class change, let's look for any computer vehicles he might have owned...
                foreach (Vehicle v in _arena.Vehicles)
                    if (v._type.Type == VehInfo.Types.Computer && v._creator == player)
                        //Destroy it!
                        v.destroy(true);
            }

            //Anytime skills are changed
            //player.inventoryModify(1036, 1);

            //Biologist
            if (player.findSkill(-8) != null && player.ActiveVehicle._type.Name.Contains("Medic") /* restrict bonus to class */)
            {
                player.inventoryModify(27, 1);
                player.inventoryModify(1075, 1);
            }
            //Eagle-Eye
            if (player.findSkill(-9) != null && player.ActiveVehicle._type.Name.Contains("Scout") /* restrict bonus to class */)
            {
                player.inventoryModify(1015, 1);
                player.inventoryModify(2000, 10);
            }

        }


        /// <summary>
        /// Triggered when a vehicle is created
        /// </summary>
        /// <remarks>Doesn't catch spectator or dependent vehicle creation</remarks>
        [Scripts.Event("Vehicle.Creation")]
		public bool vehicleCreation(Vehicle created, Team team, Player creator)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a vehicle dies
		/// </summary>
		[Scripts.Event("Vehicle.Death")]
		public bool vehicleDeath(Vehicle dead, Player killer)
		{
            if (dead._type.Id == 405)
            {
                Nest nest = _nests.FirstOrDefault(n => n._vehicle == dead);

                if (nest == null)
                    return true;

                //Remove it
                _nests.Remove(nest);
            }
			return true;
		}
        #endregion

        #region Spawning
        public void spawnSupplyDrop(short posX, short posY)
        {
            VehInfo supplyVehicle = AssetManager.Manager.getVehicleByID(404);
            Helpers.ObjectState objState = new Helpers.ObjectState();

            objState.positionX = posX;
            objState.positionY = posY;

            Computer newVehicle = _arena.newVehicle(supplyVehicle, marines, null, objState) as Computer;
            SupplyDrop newDrop = new SupplyDrop(marines, newVehicle, objState.positionX, objState.positionY);
            _supplyDrops.Add(newDrop);

            marines.sendArenaMessage(String.Format("&Supplies have been dropped at {0}.", newVehicle._state.letterCoord()), 4);
        }



        /// <summary>
        /// Obtains an appropriate spawn location for a zombie versus a team
        /// </summary>
        public bool findSpawnLocation(ref Helpers.ObjectState state, short spawnMinDist, short spawnMaxDist)
        {

            if (marines.ActivePlayerCount == 0)
                return false;

            /*
            Helpers.ObjectState average = new Helpers.ObjectState();
            marines.averagePosition(out average.positionX, out average.positionY);

            List<Vehicle> hives = _arena.Vehicles.Where(veh => veh._type.Id == 405 || veh._type.Id == 409).OrderBy(hi => Helpers.distanceTo(average, hi._state)).ToList();
            short posX = 0; short posY = 0;
            
            if (hives.Count() > 0)
            {
                posX = hives[0]._state.positionX;
                posY = hives[0]._state.positionY;
            }

            if (posX == 0 && posY == 0)
                return false;

            //sets the state spawning location to the unblocked tile
            state.positionX = posX;
            state.positionY = posY;
            */

            double shortestDistance = double.MaxValue;
            List<Vehicle> hives = _arena.Vehicles.Where(veh => veh._type.Id == 405 || veh._type.Id == 409).ToList();
            Vehicle closestHive = null;
            foreach (Vehicle hive in hives)
            {
                foreach (Player player in marines.ActivePlayers)
                {
                    double distance = Helpers.distanceTo(hive._state, player._state);

                    if (distance < shortestDistance)
                    {
                        shortestDistance = distance;
                        closestHive = hive;
                    }
                }
            }

            short posX = 0; short posY = 0;

            if (closestHive != null)
            {
                posX = closestHive._state.positionX;
                posY = closestHive._state.positionY;
            }

            if (posX == 0 && posY == 0)
                return false;

            state.positionX = posX;
            state.positionY = posY;

            return true;
        }


        public void spawnScientists()
        {
            _objectives = new List<Objective>();
            int levelX = _arena._server._assets.Level.OffsetX * 16;
            int levelY = _arena._server._assets.Level.OffsetY * 16;

            //Destroy any remaining hives
            foreach (Vehicle vehicle in _arena.Vehicles.Where(veh => veh._type.Id == 403))
                vehicle.destroy(false);

            List<LioInfo.Hide> hides = _arena._server._assets.Lios.Hides.Where(hide => -hide.HideData.HideId == 403).ToList();

            //_arena.sendArenaMessage(String.Format("Hive Count: {0}", hides.Count));

            foreach (LioInfo.Hide hs in hides)
            {

                Helpers.ObjectState newObj = new Helpers.ObjectState();
                newObj.positionX = (short)(hs.GeneralData.OffsetX - levelX);
                newObj.positionY = (short)(hs.GeneralData.OffsetY - levelY);

                Vehicle newScientist = _arena.newVehicle(AssetManager.Manager.getVehicleByID(403), marines, null, newObj);

                Objective newObjective = new Objective(_arena, this, null, newScientist, hs.GeneralData.Name);

                _objectives.Add(newObjective);

                _arena.sendArenaMessage(String.Format("The Scientist at {0} is now active", Helpers.posToLetterCoord(newScientist._state.positionX, newScientist._state.positionY)));
            }
        }

        public void spawnElevatorPanels()
        {
            _elevatorPanels = new List<ElevatorPanel>();
            int levelX = _arena._server._assets.Level.OffsetX * 16;
            int levelY = _arena._server._assets.Level.OffsetY * 16;

            //Destroy any remaining switches
            foreach (Vehicle vehicle in _arena.Vehicles.Where(veh => veh._type.Id == 408))
                vehicle.destroy(false);

            List<LioInfo.Hide> hides = _arena._server._assets.Lios.Hides.Where(hide => -hide.HideData.HideId == 408).ToList();

            int idx = 0;

            foreach (LioInfo.Hide hs in hides)
            {

                Helpers.ObjectState newObj = new Helpers.ObjectState();
                newObj.positionX = (short)(hs.GeneralData.OffsetX - levelX);
                newObj.positionY = (short)(hs.GeneralData.OffsetY - levelY);

                Vehicle panelVeh = _arena.newVehicle(AssetManager.Manager.getVehicleByID(408), marines, null, newObj);

                ElevatorPanel newPanel = new ElevatorPanel(_arena, this, panelVeh, idx);

                _elevatorPanels.Add(newPanel);
                idx++;
            }
        }

        public void spawnHives()
        {
            int levelX = _arena._server._assets.Level.OffsetX * 16;
            int levelY = _arena._server._assets.Level.OffsetY * 16;

            //Destroy any remaining hives
            foreach (Vehicle vehicle in _arena.Vehicles.Where(veh => veh._type.Id == 405))
                vehicle.destroy(false);


                _nests = new List<Nest>();

            List<LioInfo.Hide> hides = _arena._server._assets.Lios.Hides.Where(hide => -hide.HideData.HideId == 405 || -hide.HideData.HideId == 409).ToList();

            //_arena.sendArenaMessage(String.Format("Hive Count: {0}", hides.Count));

            foreach (LioInfo.Hide hs in hides)
            {
                Helpers.ObjectState newObj = new Helpers.ObjectState();
                newObj.positionX = (short)(hs.GeneralData.OffsetX - levelX);
                newObj.positionY = (short)(hs.GeneralData.OffsetY - levelY);
                Nest newNest;
                Vehicle hiveVeh;
                if (-hs.HideData.HideId == 409)
                {

                    hiveVeh = _arena.newVehicle(AssetManager.Manager.getVehicleByID(409), skrall, null, newObj);
                    newNest = new Nest(skrall, marines, _arena, _botHandler, hiveVeh, this, true);
                }
                else
                {
                    hiveVeh = _arena.newVehicle(AssetManager.Manager.getVehicleByID(405), skrall, null, newObj);
                    newNest = new Nest(skrall, marines, _arena, _botHandler, hiveVeh, this, false);
                }

                _nests.Add(newNest);

            }
        }
        #endregion
    }
}