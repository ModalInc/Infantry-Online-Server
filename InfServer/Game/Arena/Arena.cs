﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using InfServer.Network;
using InfServer.Protocol;
using InfServer.Logic;

using Assets;

namespace InfServer.Game
{
	// Arena Class
	/// Represents a single arena in the server
	///////////////////////////////////////////////////////
	public abstract partial class Arena : IChatTarget, IEventObject		
	{	// Member variables
		///////////////////////////////////////////////////
		public LogClient _logger;						//The logger we use for this arena!
		public volatile bool _bActive;					//Is the arena functioning, or condemned?

		public ZoneServer _server;						//The server we belong to

		protected ObjTracker<Player> _players;			//The list of players in this arena
		protected ObjTracker<Player> _playersIngame;	//The list of players currently ingame

		public string _name;							//The name of this arena

		public Random _rand;							//Our random seed

		public Dictionary<int, TickerInfo> _tickers;	//The tickers!
		public bool _bGameRunning;						//Is the game running?

		public int _levelWidth;
		public LvlInfo.Tile[] _tiles;					//The terrain tiles in the arena, can be updated to reflect switches, etc

		public Commands.Registrar _commandRegistrar;	//Our chat/mod command registrar

		//Events
		public event Action<Arena> Close;				//Called when an arena runs out of players and is closed

		//Settings
		static public int maxVehicles;					//The maximum amount of vehicles we can have active
		static public int maxItems;						//The maximum amount of items we can have laying about
		
		static public int gameCheckInterval;			//The frequency at which we check basic game state

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
		{	//Does the event exist?
			HandlerList list;
			return events.TryGetValue(name, out list);
		}

		/// <summary>
		/// Flushes the handlerlist - removing all handlers
		/// </summary>
		public void flushEvents()
		{	//Kill all the handlers!
			using (DdMonitor.Lock(_sync))
				events.Clear();
		}
		#endregion


		///////////////////////////////////////////////////
		// Accessors
		///////////////////////////////////////////////////
		/// <summary>
		/// Returns the amount of players that are actually ingame
		/// </summary>
		public int PlayerCount
		{
			get
			{
				return _playersIngame.Count;
			}
		}

		/// <summary>
		/// Returns a list of the active players in the arena
		/// </summary>
		/// 
		public IEnumerable<Player> PlayersIngame
		{
			get
			{
				return _playersIngame;
			}
		}

		/// <summary>
		/// Returns the total amount of players that are in the arena
		/// </summary>
		public int TotalPlayerCount
		{
			get
			{
				return _players.Count;
			}
		}

		/// <summary>
		/// Returns a list of the active players in the arena
		/// </summary>
		/// 
		public IEnumerable<Player> Players
		{
			get
			{
				return _players;
			}
		}

		/// <summary>
		/// Is this arena invisible to normal players?
		/// </summary>
		public bool IsPrivate
		{
			get
			{
				return _name[0] == '#';
			}
		}

		/// <summary>
		/// Gets the tile at the specified location
		/// </summary>
		/// <remarks>The position given should be in map ticks.</remarks>
		public LvlInfo.Tile getTile(int x, int y)
		{
			x /= 16;
			y /= 16;

			return _tiles[(y * _levelWidth) + x];
		}

		/// <summary>
		/// Gets the tile at the specified location
		/// </summary>
		/// <remarks>The position given should be in map ticks.</remarks>
		public CfgInfo.Terrain getTerrain(int x, int y)
		{	//Get the terrain type of the tile
			x /= 16;
			y /= 16;

			LvlInfo.Tile tile = _tiles[(y * _levelWidth) + x];

			//Find the associated terrain type
			return _server._zoneConfig.terrains[_server._assets.Level.TerrainLookup[tile.TerrainLookup]];
		}

		///////////////////////////////////////////////////
		// Member Classes
		///////////////////////////////////////////////////
		#region Member Classes
		/// <summary>
		/// Represents a dropped item
		/// </summary>
		public class ItemDrop
		{
			public ushort id;			//The ID of the item pile
			public ItemInfo item;		//The type of type
			
			public short quantity;		//The amount in the pile
			public short positionX;		//The location of the pile
			public short positionY;		//
		}

		/// <summary>
		/// Represents the state of a ticker
		/// </summary>
		public class TickerInfo
		{
			public string message;
			public int timer;
			public int idx;
			public byte colour;
			public Action callback;

			public TickerInfo(string _message, int _timer, int _idx, byte _colour, Action _callback)
			{
				message = _message;
				colour = _colour;
				callback = _callback;
				idx = _idx;

				//The timer has to be relative, so calculate
				timer = Environment.TickCount + (_timer * 10);
			}

			public void onExpire()
			{
				if (callback != null)
					callback();
			}
		}
		#endregion

		///////////////////////////////////////////////////
		// Member Functions
		///////////////////////////////////////////////////
		/// <summary>
		/// Generic constructor
		/// </summary>
		public Arena(ZoneServer server)
		{	//Initialize the event object
			eventInit(true);

			//Populate variables
			_sync = new object();
			_server = server;

			_rand = new Random();

			_tickers = new Dictionary<int, TickerInfo>();

			_players = new ObjTracker<Player>();
			_playersIngame = new ObjTracker<Player>();

			_teams = new Dictionary<string, Team>();
			_freqTeams = new SortedDictionary<int, Team>();

			_vehicles = new ObjTracker<Vehicle>();
			_lastVehicleKey = (ushort)5001;						//The vehicle IDs must start at 5001, everything before
																//is assumed to be a player base vehicle.
			_items = new SortedDictionary<ushort, ItemDrop>();
			_lastItemKey = 0;

			_bots = new ObjTracker<InfServer.Bots.Bot>();

			//Instance our tiles array
			LvlInfo lvl = server._assets.Level;
			_tiles = new LvlInfo.Tile[lvl.Tiles.Length];
			_levelWidth = lvl.Width;

			Array.Copy(lvl.Tiles, _tiles, lvl.Height * lvl.Width);

			//Initialize our command registrar
			_commandRegistrar = new InfServer.Game.Commands.Registrar();
			_commandRegistrar.register();
		}

		#region State
		/// <summary>
		/// Initializes arena details
		/// </summary>
		public virtual void init()
		{	//Initialize our subsections
			initState();
			initLio();
		}

		/// <summary>
		/// Allows the arena to keep it's game state up-to-date
		/// </summary>
		public virtual void poll()
		{	//Make sure we're synced
			using (DdMonitor.Lock(_sync))
			{	//Look after our players
				int now = Environment.TickCount;

				foreach (Player player in PlayersIngame)
				{	//Is he awaiting a respawn?
					if (player._deathTime != 0 && now - player._deathTime > 10000)
					{	//So spawn him!
						player._deathTime = 0;
						handlePlayerSpawn(player, true);
					}
				}

				//Keep our tickers in line
				foreach (TickerInfo ticker in _tickers.Values)
				{	//If it's timed out
					if (ticker.timer != -1 && ticker.timer < now)
					{	//Ticker has expired
						ticker.timer = -1;
						ticker.onExpire();
					}
				}

				//Keep car vehicles in line
				List<Vehicle> condemned = new List<Vehicle>();

				foreach (Vehicle vehicle in _vehicles)
				{	//We don't need to bother maintaining bot vehicles
					if (vehicle._bBotVehicle)
						continue;

					//What sort of vehicle is it?
					switch (vehicle._type.Type)
					{
						case VehInfo.Types.Car:
							{	//Get our information
								VehInfo.Car carInfo = vehicle._type as VehInfo.Car;
								if (carInfo == null)
									continue;

								//Check for expiration timers
								if (carInfo.RemoveGlobalTimer != 0 && vehicle._tickCreation != 0 &&
									now - vehicle._tickCreation > (carInfo.RemoveGlobalTimer * 1000))
									condemned.Add(vehicle);
								else if (carInfo.RemoveDeadTimer != 0 && vehicle._tickDead != 0 &&
									now - vehicle._tickDead > (carInfo.RemoveDeadTimer * 1000))
									condemned.Add(vehicle);
								else if (carInfo.RemoveUnoccupiedTimer != 0 && vehicle._tickUnoccupied != 0 &&
									now - vehicle._tickUnoccupied > (carInfo.RemoveUnoccupiedTimer * 1000))
									condemned.Add(vehicle);
							}
							break;
					}
				}

				foreach (Vehicle vehicle in condemned)
					vehicle.destroy(true);

				//Look after our lio objects
				pollLio();

				// Aim and fire turrets!
				pollComputers();

				//Handle the bots!
				pollBots();
			}
		}

		/// <summary>
		/// Cleans up the arena and removes it from the zone server list
		/// </summary>
		public virtual void close()
		{	//Call our close event
			if (Close != null)
				Close(this);
		}
		#endregion

		#region Players
		public List<Player> getPlayersInRange(int posX, int posY, int range)
		{
			return _playersIngame.getObjsInRange(posX, posY, range);
		}

		public List<Player> getPlayersInBox(int posX, int posY, int width, int height)
		{	
			//Extrapolate
			width /= 2;
			height /= 2;
			return getPlayersInArea(posX - width, posY - height, posX + width, posY + height);
		}

		public List<Player> getPlayersInArea(int topX, int topY, int bottomX, int bottomY)
		{
			return _playersIngame.getObjsInArea(topX, topY, bottomX, bottomY);
		}

		#endregion

		#region Accessors
		/// <summary>
		/// Obtains a team by name
		/// </summary>
		public Team getTeamByName(string name)
		{	//Attempt to find it
			Team team;

			if (!_teams.TryGetValue(name.ToLower(), out team))
				return null;
			return team;
		}
		#endregion

		#region Events
		/// <summary>
		/// Called when a player enters the game
		/// </summary>
		public virtual void playerEnter(Player player)
		{	//The player has joined the game! Add him
			_playersIngame.Add(player);
		}

		/// <summary>
		/// Called when a player leaves the game
		/// </summary>
		public virtual void playerLeave(Player player)
		{	//He's left, remove him
			_playersIngame.Remove(player);
		}

		/// <summary>
		/// Called when the game begins
		/// </summary>
		public virtual void gameStart() 
		{	//We're running!
			_bGameRunning = true;

			//Reset the game state
			flagReset();
			resetItems();
			resetVehicles();

			//Perform our initial hide spawns
			initialHideSpawns();

			//Execute the start game event
			string startGame = _server._zoneConfig.EventInfo.startGame;
			foreach (Player player in PlayersIngame)
				Logic_Assets.RunEvent(player, startGame);
		}

		/// <summary>
		/// Called when the game ends
		/// </summary>
		public virtual void gameEnd()
		{	//We've stopped
			_bGameRunning = false;

			//Reset the game state
			flagReset();
			resetItems();
			resetVehicles();

			//Execute the end game event
			string endGame = _server._zoneConfig.EventInfo.endGame;
			foreach (Player player in PlayersIngame)
				Logic_Assets.RunEvent(player, endGame);
		}

		/// <summary>
		/// Called to reset the game state
		/// </summary>
		public virtual void gameReset()
		{	//Reset the game state
			flagReset();
			resetItems();
			resetVehicles();
		}
		#endregion
	}
}