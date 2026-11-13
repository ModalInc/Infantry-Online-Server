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

namespace InfServer.Script.GameType_OGCTF
{	// Script Class
	/// Provides the interface between the script and arena
	///////////////////////////////////////////////////////
    class Script_OGCTF : Scripts.IScript
	{	///////////////////////////////////////////////////
		// Member Variables
		///////////////////////////////////////////////////
		private Arena _arena;					//Pointer to our arena class
		private CfgInfo _config;				//The zone config

		private int _jackpot;					//The game's jackpot so far

		private int _lastGameCheck;				//The tick at which we last checked for game viability
		private int _tickGameStarting;			//The tick at which the game began starting (0 == not initiated)
		private int _tickGameStart;				//The tick at which the game started (0 == stopped)

        private Arena.FlagState titanFlag;
        private Arena.FlagState collFlag;
        
        // Victory timer variables
        private int _victoryHoldTime;           // Time required to hold all flags for victory (in milliseconds)
        private int _tickVictoryStart;          // When the victory timer started (0 == not started)
        private Team _victoryTeam;              // Team that might win

		//Settings
		private int _minPlayers;				//The minimum amount of players


		///////////////////////////////////////////////////
		// Member Functions
		///////////////////////////////////////////////////
		/// <summary>
		/// Performs script initialization
		/// </summary>
		public bool init(IEventObject invoker)
		{	//Populate our variables
			_arena = invoker as Arena;
			_config = _arena._server._zoneConfig;

            _minPlayers = Int32.MaxValue;
            
            // Initialize victory timer settings (90 seconds default)
            _victoryHoldTime = 90000;
            _tickVictoryStart = 0;
            _victoryTeam = null;

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

			if ((_tickGameStart == 0 || _tickGameStarting == 0) && playing < _minPlayers)
			{	//Stop the game!
				_arena.setTicker(1, 1, 0, "Not Enough Players");
				_arena.gameReset();
			}
			//Do we have enough players to start a game?
			else if (_tickGameStart == 0 && _tickGameStarting == 0 && playing >= _minPlayers)
			{	//Great! Get going
				_tickGameStarting = now;
				_arena.setTicker(1, 1, _config.flag.startDelay * 100, "Next game: ",
					delegate()
					{	//Trigger the game start
						_arena.gameStart();
					}
				);
			}

            // Check victory timer if it's running
            if (_tickVictoryStart != 0 && _victoryTeam != null)
            {
                int timeRemaining = _victoryHoldTime - (now - _tickVictoryStart);
                
                if (timeRemaining <= 0)
                {
                    // Victory time has elapsed!
                    gameVictory(_victoryTeam);
                }
                else
                {
                    // Update the ticker with remaining time
                    int secondsRemaining = timeRemaining / 1000;
                    _arena.setTicker(1, 1, 0, string.Format("{0} victory in: {1}", _victoryTeam._name, secondsRemaining));
                }
            }

			return true;
		}

		#region Events
		/// <summary>
		/// Called when a flag changes team
		/// </summary>
		public void onFlagChange(Arena.FlagState flag)
		{	
            // Check if game is active
            if (_tickGameStart == 0)
                return;

            // Check victory conditions
            checkVictoryConditions();
		}

        /// <summary>
        /// Checks if any team has achieved victory conditions
        /// </summary>
        private void checkVictoryConditions()
        {
            if (_arena._flags.Count == 0)
                return;

            // Group flags by team
            var flagsByTeam = _arena._flags.Values.GroupBy(f => f.team).ToList();
            
            // Find if any team owns all flags
            foreach (var teamGroup in flagsByTeam)
            {
                if (teamGroup.Key != null && teamGroup.Count() == _arena._flags.Count)
                {
                    // This team owns all flags!
                    Team owningTeam = teamGroup.Key;
                    
                    if (_victoryTeam != owningTeam)
                    {
                        // New team has taken control, start victory timer
                        _victoryTeam = owningTeam;
                        _tickVictoryStart = Environment.TickCount;
                        
                        _arena.sendArenaMessage(string.Format("{0} has captured all flags! Victory in {1} seconds!", 
                            owningTeam._name, _victoryHoldTime / 1000), _config.flag.victoryBong);
                        
                        // Set ticker to show countdown
                        _arena.setTicker(1, 1, 0, string.Format("{0} victory in: {1}", owningTeam._name, _victoryHoldTime / 1000));
                    }
                    return;
                }
            }
            
            // No team owns all flags, cancel victory timer if running
            if (_tickVictoryStart != 0)
            {
                _arena.sendArenaMessage("Victory timer stopped! Flags have been contested.", _config.flag.victoryBong);
                _tickVictoryStart = 0;
                _victoryTeam = null;
                _arena.setTicker(1, 1, 0, "");
            }
        }

		/// <summary>
		/// Called when the specified team have won
		/// </summary>
		public void gameVictory(Team victors)
		{
            // Stop victory timer
            _tickVictoryStart = 0;
            _victoryTeam = null;
            
            // Announce victory
            _arena.sendArenaMessage(string.Format("Victory! {0} has won the game!", victors._name), _config.flag.victoryBong);
            
            //Stop the game
            _arena.gameEnd();
		}

        /// <summary>
        /// Called when a player sends a chat command
        /// </summary>
        [Scripts.Event("Player.ChatCommand")]
        public bool playerChatCommand(Player player, Player recipient, string command, string payload)
        {
            if (command.ToLower() == "test")
            {
                player.sendMessage(0, "Test");
            }
            return true;
        }

		/// <summary>
		/// Called when a player enters the game
		/// </summary>
		[Scripts.Event("Player.Enter")]
		public void playerEnter(Player player)
		{
		}

		/// <summary>
		/// Called when a player leaves the game
		/// </summary>
		[Scripts.Event("Player.Leave")]
		public void playerLeave(Player player)
		{
		}

		/// <summary>
		/// Called when the game begins
		/// </summary>
		[Scripts.Event("Game.Start")]
		public bool gameStart()
		{	//We've started!
			_tickGameStart = Environment.TickCount;
			_tickGameStarting = 0;
			
			// Reset victory timer
			_tickVictoryStart = 0;
			_victoryTeam = null;

            //Scramble the teams!
            //ScriptHelpers.scrambleTeams(_arena, 2, true);

			//Spawn our flags!
			_arena.flagSpawn();

            // Debug: Show available flags
            // _arena.sendArenaMessage("=== Available Flags ===");
            // foreach (var flag in _arena._flags.Values)
            // {
            //     _arena.sendArenaMessage(string.Format("Flag ID: {0}, Name: {1}", 
            //         flag.flag.GeneralData.Id, flag.flag.GeneralData.Name));
            // }

            // Find flags by ID, use FirstOrDefault to avoid exceptions
            titanFlag = _arena._flags.Values.Where(f => f.flag.GeneralData.Id == 15).FirstOrDefault();
            collFlag = _arena._flags.Values.Where(f => f.flag.GeneralData.Id == 7).FirstOrDefault();

            // If flags with specific IDs don't exist, try to assign available flags
            if (titanFlag == null || collFlag == null)
            {
                var availableFlags = _arena._flags.Values.ToList();
                if (availableFlags.Count >= 2)
                {
                    titanFlag = availableFlags[0];
                    collFlag = availableFlags[1];
                }
                else if (availableFlags.Count == 1)
                {
                    // Only one flag available - this might be a single flag map
                    titanFlag = availableFlags[0];
                    collFlag = null;
                }
            }

            // Assign teams to flags if they exist
            if (titanFlag != null)
            {
                var titanTeam = _arena.getTeamByName("Titan Militia");
                if (titanTeam != null)
                    titanFlag.team = titanTeam;
            }
            
            if (collFlag != null)
            {
                var collTeam = _arena.getTeamByName("Collective Military");
                if (collTeam != null)
                    collFlag.team = collTeam;
            }



			//Let everyone know
			_arena.sendArenaMessage("Game has started!", _config.flag.resetBong);

			return true;
		}

		/// <summary>
		/// Called when the game ends
		/// </summary>
		[Scripts.Event("Game.End")]
		public bool gameEnd()
		{	//Game finished, perhaps start a new one
			_tickGameStart = 0;
			_tickGameStarting = 0;
			
			// Reset victory timer
			_tickVictoryStart = 0;
			_victoryTeam = null;

			return true;
		}

        /// <summary>
        /// Called when the statistical breakdown is displayed
        /// </summary>
        [Scripts.Event("Game.Breakdown")]
        public bool breakdown()
        {	//Allows additional "custom" breakdown information


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
			
			// Reset victory timer
			_tickVictoryStart = 0;
			_victoryTeam = null;

			return true;
		}

		/// <summary>
		/// Triggered when a player requests to pick up an item
		/// </summary>
		[Scripts.Event("Player.ItemPickup")]
		public bool playerItemPickup(Player player, Arena.ItemDrop drop, ushort quantity)
		{	
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
		/// Handles a player's produce request
		/// </summary>
		[Scripts.Event("Player.Produce")]
		public bool playerProduce(Player player, Computer computer, VehInfo.Computer.ComputerProduct product)
		{
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
			return true;
		}

		/// <summary>
		/// Triggered when a player wants to unspec and join the game
		/// </summary>
		[Scripts.Event("Player.JoinGame")]
		public bool playerJoinGame(Player player)
		{
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
			return true;
		}

		/// <summary>
		/// Triggered when one player has killed another
		/// </summary>
		[Scripts.Event("Player.PlayerKill")]
		public bool playerPlayerKill(Player victim, Player killer)
		{
			return true;
		}

		/// <summary>
		/// Triggered when a bot has killed a player
		/// </summary>
		[Scripts.Event("Player.BotKill")]
		public bool playerBotKill(Player victim, Bot bot)
		{
			return true;
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
			return true;
		}
		#endregion
	}
}