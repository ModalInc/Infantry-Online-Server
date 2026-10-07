using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Network;
using InfServer.Game;

using Assets;
using Axiom.Math;

namespace InfServer.Protocol
{	/// <summary>
	/// Provides a series of functions for easily serialization of packets
	/// </summary>
	public partial class Helpers
	{	// Member Classes
		//////////////////////////////////////////////////
		/// <summary>
		/// Contains positional and other information an object
		/// </summary>
		public class ObjectState
		{
			public Int16 health;
			public Int16 energy;

			public Int16 velocityX;			//Velocity info
			public Int16 velocityY;			//
			public Int16 velocityZ;			//

			public Int16 positionX;			//Positional info
			public Int16 positionY;			//
			public Int16 positionZ;			//

			public byte yaw;				//Our rotation
			public Direction direction;		//The direction we're attempting to move in
			public byte unk1;				//Unknown (flags?)

			public byte pitch;

			//
			public byte fireAngle;			//Used for computer vehicles
			public int lastUpdate;			//The last point this state was updated
			public int lastUpdateServer;	//The time at which this was received on the server
			public int updateNumber;		//The update counter, used for route range factoring

			//Assume the direction system is hardcoded for now
			public enum Direction : ushort
			{
				None = 0,
				Forward = 0x3C,
				Backward = 0xD3,
				StrafeLeft = 0xBF00,
				StrafeRight = 0x4100,
				NorthWest = 0xD32A,
				SouthWest = 0xD3E1,
				SouthEast = 0x2DE1,
				NorthEast = 0x2D2A,
			}

			/// <summary>
			/// Client run cycle dies on Direction.None. If the bot still has velocity,
			/// force a non-None facing so packets don't look like pose hops.
			/// </summary>
			public static ushort EnsureBotWireDirection(ushort direction, short velX, short velY, byte yaw)
			{
				if (direction != (ushort)Direction.None)
					return direction;

				long speed2 = (long)velX * velX + (long)velY * velY;
				if (speed2 < 4)
					return direction;

				double yawRad = yaw * (Math.PI / 120.0);
				double fx = Math.Sin(yawRad);
				double fy = Math.Cos(yawRad);
				double speed = Math.Sqrt(speed2);
				double nx = velX / speed;
				double ny = velY / speed;
				double forward = nx * fx + ny * fy;
				double strafe = nx * fy - ny * fx;

				const double axis = 0.35;
				if (forward > axis)
				{
					if (strafe > axis)
						return (ushort)Direction.NorthEast;
					if (strafe < -axis)
						return (ushort)Direction.NorthWest;
					return (ushort)Direction.Forward;
				}
				if (forward < -axis)
				{
					if (strafe > axis)
						return (ushort)Direction.SouthEast;
					if (strafe < -axis)
						return (ushort)Direction.SouthWest;
					return (ushort)Direction.Backward;
				}
				if (strafe > axis)
					return (ushort)Direction.StrafeRight;
				if (strafe < -axis)
					return (ushort)Direction.StrafeLeft;
				return (ushort)Direction.Forward;
			}

			//Converting objects to vector space
			public Vector3 velocity()
			{
				return new Vector3(((float)velocityX) / 1000, ((float)velocityY) / 1000, ((float)velocityZ) / 1000);
			}

			public Vector3 position()
			{
				if (lastUpdate != 0)
				{	//We want to find the player's -current- position
					double timeElapsed = (Environment.TickCount - lastUpdate) / 1000.0d;

					return new Vector3(	(((float)positionX) + ((((float)velocityX) / 10) * timeElapsed)) / 100,
										(((float)positionY) + ((((float)velocityY) / 10) * timeElapsed)) / 100,
										(((float)positionZ) + ((((float)velocityZ) / 10) * timeElapsed)) / 100);
				}
				else
					return new Vector3(((float)positionX) / 100, ((float)positionY) / 100, ((float)positionZ) / 100);
			}

			//Converting to displayable coordinates
			public String letterCoord()
			{
				return Helpers.posToLetterCoord(positionX, positionY);
			}
		}

		///////////////////////////////////////////////////
		// Member Functions
		//////////////////////////////////////////////////
		/// <summary>Keep arena-join snapshots under one UDP frame so clients avoid DataStream alloc.</summary>
		const int ItemDropChunkSize = 40;
		/// <summary>Root vehicles per reliable packet (children serialize with their parent).</summary>
		const int VehicleChunkSize = 20;

		/// <summary>
		/// Notifies a bunch of players of item drops
		/// </summary>
		static public void Object_Items(IEnumerable<Player> players, IEnumerable<Arena.ItemDrop> drop)
		{
			if (players == null || drop == null)
				return;

			List<Arena.ItemDrop> list = drop as List<Arena.ItemDrop> ?? drop.ToList();
			foreach (List<Arena.ItemDrop> chunk in ChunkList(list, ItemDropChunkSize))
			{
				SC_Items items = new SC_Items();
				items.items = chunk;
				foreach (Player p in players)
				{
					if (p != null && p._client != null)
						p._client.sendReliable(items);
				}
			}
		}

		/// <summary>
		/// Notifies a player of item drops
		/// </summary>
		static public void Object_Items(Player p, IEnumerable<Arena.ItemDrop> drop)
		{
			if (p == null || p._client == null || drop == null)
				return;

			List<Arena.ItemDrop> list = drop as List<Arena.ItemDrop> ?? drop.ToList();
			foreach (List<Arena.ItemDrop> chunk in ChunkList(list, ItemDropChunkSize))
			{
				SC_Items items = new SC_Items();
				items.items = chunk;
				p._client.sendReliable(items);
			}
		}

		/// <summary>
		/// Updates the status of an item drop in the specified clients
		/// </summary>
		static public void Object_ItemDropUpdate(IEnumerable<Player> players, ushort dropID, ushort quantity)
		{	//Create the notification packet
			SC_ItemDrop destroy = new SC_ItemDrop();
			destroy.dropID = dropID;
			destroy.quantity = quantity;

			//Route the packet
			foreach (Player p in players)
				p._client.sendReliable(destroy);
		}

		/// <summary>
		/// Notifies a bunch of players of a new item drop
		/// </summary>
		static public void Object_ItemDrops(IEnumerable<Player> players, IEnumerable<Arena.ItemDrop> drop)
		{
			foreach (Arena.ItemDrop item in drop)
			{	//Create the notification packet
				SC_ItemDrop destroy = new SC_ItemDrop();
				destroy.dropID = item.id;
				destroy.quantity = (ushort)item.quantity;

				//Route the packet
				foreach (Player p in players)
					p._client.sendReliable(destroy);
			}
		}

		/// <summary>
		/// Notifies a bunch of players of a new item drop
		/// </summary>
		static public void Object_ItemDrop(IEnumerable<Player> players, Arena.ItemDrop drop)
		{	//Create the notification packet
			SC_Items items = new SC_Items();

			items.singleItem = drop;

			//Route the packet
			foreach (Player p in players)
				p._client.sendReliable(items);
		}
        /// <summary>
        /// Notifies a single player of an item drop
        /// </summary>
        static public void Object_ItemDrop(Player player, Arena.ItemDrop drop)
        {	//Create the notification packet
            SC_Items items = new SC_Items();

            items.singleItem = drop;

            player._client.sendReliable(items);
        }
		/// <summary>
		/// Instructs the client to reset and disable all flags
		/// </summary>
		static public void Object_FlagsReset(IEnumerable<Player> p)
		{	//Prepare the packet
			SC_Flags flags = new SC_Flags();
			flags.bDisable = true;

			//Send to each recipient
			foreach (Player player in p)
				player._client.sendReliable(flags);
		}

		/// <summary>
		/// Sends a flag update to the specified players
		/// </summary>
		static public void Object_Flags(IEnumerable<Player> p, Arena.FlagState update)
		{	//Prepare the packet
			SC_Flags flags = new SC_Flags();
			flags.singleUpdate = update;

			//Send to each recipient
			foreach (Player player in p)
				player._client.sendReliable(flags);
		}

		/// <summary>
		/// Sends a series of flag updates to the specified player
		/// </summary>
		static public void Object_Flags(Player p, IEnumerable<Arena.FlagState> update)
		{	//Prepare the packet
			SC_Flags flags = new SC_Flags();
			flags.flags = update;

			//Send to each recipient
			p._client.sendReliable(flags);
		}

		/// <summary>
		/// Sends a series of flag updates to the specified players
		/// </summary>
		static public void Object_Flags(IEnumerable<Player> p, IEnumerable<Arena.FlagState> update)
		{	//Prepare the packet
			SC_Flags flags = new SC_Flags();
			flags.flags = update;

			//Send to each recipient
			foreach (Player player in p)
				player._client.sendReliable(flags);
		}

		/// <summary>
		/// Sends a lio update to the specified players
		/// </summary>
		static public void Object_LIOs(IEnumerable<Player> p, Arena.SwitchState update)
		{	//Prepare the packet
			SC_LIOUpdates lios = new SC_LIOUpdates();
			lios.singleUpdate = update;

			//Send to each recipient
			foreach (Player player in p)
				player._client.sendReliable(lios);
		}

		/// <summary>
		/// Sends a series of lio updates to the specified player
		/// </summary>
		static public void Object_LIOs(Player p, IEnumerable<Arena.SwitchState> update)
		{	//Prepare the packet
			SC_LIOUpdates lios = new SC_LIOUpdates();
			lios.objects = update;

			//Send to each recipient
			p._client.sendReliable(lios);
		}

		/// <summary>
		/// Sends a series of lio updates to the specified players
		/// </summary>
		static public void Object_LIOs(IEnumerable<Player> p, IEnumerable<Arena.SwitchState> update)
		{	//Prepare the packet
			SC_LIOUpdates lios = new SC_LIOUpdates();
			lios.objects = update;

			//Send to each recipient
			foreach (Player player in p)
				player._client.sendReliable(lios);
		}

		/// <summary>
		/// Indicates the destruction of a vehicle
		/// </summary>
		static public void Object_VehicleDestroy(IEnumerable<Player> p, Vehicle vehicle)
		{	//Prepare the packet
			SC_VehicleDestroy des = new SC_VehicleDestroy();

			des.vehicleID = vehicle._id;

			//Send to recipient
			foreach (Player player in p)
				player._client.sendReliable(des);
		}

		/// <summary>
		/// Binds new information to a player's base vehicle
		/// </summary>
		static public void Object_VehicleBind(IEnumerable<Player> p, Vehicle vehicle1, short vehicleID)
		{	//Prepare the packet
			SC_BindVehicle bind = new SC_BindVehicle();

			bind.Vehicle1 = vehicle1;
			bind.vehicleID = vehicleID;

			//Send to recipient
			foreach (Player player in p)
				player._client.sendReliable(bind);
		}

		/// <summary>
		/// Binds new information to an existing vehicle
		/// </summary>
		static public void Object_VehicleBind(IEnumerable<Player> p, Vehicle vehicle)
		{	//Prepare the packet
			SC_BindVehicle bind = new SC_BindVehicle();

			bind.Vehicle1 = vehicle;
			bind.Vehicle2 = ((vehicle._inhabitant == null) ? null : vehicle._inhabitant._baseVehicle);

			//Send to recipient
			foreach (Player player in p)
				player._client.sendReliable(bind);
		}

		/// <summary>
		/// Binds new information to an existing vehicle
		/// </summary>
		static public void Object_VehicleBind(IEnumerable<Player> p, Vehicle vehicle1, Vehicle vehicle2)
		{	//Prepare the packet
			SC_BindVehicle bind = new SC_BindVehicle();

			bind.Vehicle1 = vehicle1;
			bind.Vehicle2 = vehicle2;

			//Send to recipient
			foreach (Player player in p)
				player._client.sendReliable(bind);
		}

		/// <summary>
		/// Binds new information to an existing vehicle
		/// </summary>
		static public void Object_VehicleBind(IEnumerable<Player> p, 
			Vehicle vehicle1, Vehicle vehicle2, Action completionCallback)
		{	//Prepare the packet
			SC_BindVehicle bind = new SC_BindVehicle();

			bind.Vehicle1 = vehicle1;
			bind.Vehicle2 = vehicle2;

			//Send to recipient
			foreach (Player player in p)
			{	//If this is the player's new vehicle, watch for completion
				if (vehicle1._inhabitant == player)
					player._client.sendReliable(bind, completionCallback);
				else
					player._client.sendReliable(bind);
			}
		}

		/// <summary>
		/// Sends a series of vehicle updates to the specified player
		/// </summary>
		static public void Object_Vehicles(Player p, IEnumerable<Vehicle> update)
		{
			if (p == null || p._client == null || update == null)
				return;

			sendVehicleChunks(p, update);
		}

		/// <summary>
		/// Sends a vehicle update to the specified players
		/// </summary>
		static public void Object_Vehicles(IEnumerable<Player> p, Vehicle update)
		{	//Prepare the packet
			SC_Vehicles vehicles = new SC_Vehicles();
			vehicles.singleUpdate = update;

			//Send to each recipient
			foreach (Player player in p)
			{
				if (player == null || player._client == null)
					continue;
				player._client.sendReliable(vehicles);
			}
		}

		/// <summary>
		/// Sends a series of vehicle updates to the specified players
		/// </summary>
		static public void Object_Vehicles(IEnumerable<Player> p, IEnumerable<Vehicle> update)
		{
			if (p == null || update == null)
				return;

			foreach (Player player in p)
			{
				if (player != null && player._client != null)
					sendVehicleChunks(player, update);
			}
		}

		static void sendVehicleChunks(Player p, IEnumerable<Vehicle> update)
		{
			// Dependents ride with their parent; only chunk root vehicles.
			List<Vehicle> roots = update
				.Where(v => v != null && v._type != null && v._type.Type != VehInfo.Types.Dependent)
				.ToList();

			if (roots.Count == 0)
			{
				SC_Vehicles empty = new SC_Vehicles();
				empty.vehicles = roots;
				p._client.sendReliable(empty);
				return;
			}

			foreach (List<Vehicle> chunk in ChunkList(roots, VehicleChunkSize))
			{
				SC_Vehicles vehicles = new SC_Vehicles();
				vehicles.vehicles = chunk;
				p._client.sendReliable(vehicles);
			}
		}

		static IEnumerable<List<T>> ChunkList<T>(List<T> source, int chunkSize)
		{
			if (source == null || source.Count == 0)
			{
				yield return source ?? new List<T>();
				yield break;
			}

			if (chunkSize <= 0)
				chunkSize = 1;

			for (int i = 0; i < source.Count; i += chunkSize)
			{
				int n = Math.Min(chunkSize, source.Count - i);
				yield return source.GetRange(i, n);
			}
		}

		/// <summary>
		/// Sends a player update to the specified player
		/// </summary>
		static public void Object_Players(Player p, Player update)
		{	//Sanity checks
			if (update._baseVehicle == null)
			{
				Log.write(TLog.Warning, "Unable to update a player '{0}' without a baseVehicle set!", update._alias);
				return;
			}

			//Prepare the packet
			SC_PlayerEnter players = new SC_PlayerEnter();
			players.singlePlayer = update;

			p._client.sendReliable(players);
		}

		/// <summary>
		/// Sends a series of player updates to the specified player
		/// </summary>
		static public void Object_Players(IEnumerable<Player> p, Player update)
		{	//Sanity checks
			if (update._baseVehicle == null)
			{
				Log.write(TLog.Warning, "Unable to update a player '{0}' without a baseVehicle set!", update._alias);
				return;
			}

			//Prepare the packet
			SC_PlayerEnter players = new SC_PlayerEnter();
			players.singlePlayer = update;

			//Send to each recipient
			foreach (Player player in p)
				if (player != update)
					player._client.sendReliable(players);	
		}

		/// <summary>
		/// Sends a series of player updates to the specified player
		/// </summary>
		static public void Object_Players(Player p, IEnumerable<Player> updates)
		{	//Prepare the packet
			SC_PlayerEnter players = new SC_PlayerEnter();

			players.exclude = p;
			players.players = updates;

			//Done!
			if (updates.Any(plyr => plyr != null))
				p._client.sendReliable(players);
		}

		/// <summary>
		/// Notifies the alighting of a certain player
		/// </summary>
		static public void Object_PlayerLeave(Player p, Player leaving)
		{	//Prepare the packet
			SC_PlayerLeave update = new SC_PlayerLeave();
			update.playerID = leaving._id;

			p._client.sendReliable(update);
		}

		/// <summary>
		/// Notifies the alighting of a certain player
		/// </summary>
		static public void Object_PlayerLeave(IEnumerable<Player> p, Player leaving)
		{	//Prepare the packet
			SC_PlayerLeave update = new SC_PlayerLeave();
			update.playerID = leaving._id;

			foreach (Player player in p)
				player._client.sendReliable(update);
		}

        /// <summary>
        /// Instructs the client to reset and disable a single active ball
        /// </summary>
        static public void Object_BallReset(IEnumerable<Player> players, Ball bState)
        {   //Prepare the packet
            SC_BallState ball = new SC_BallState();
            ball.bDisable = true;
            ball.singleUpdate = bState;

            //Send it
            foreach (Player p in players)
            {
                if (p._gotBallID == bState._id)
                    p._gotBallID = 999;
                p._client.sendReliable(ball);
            }
        }

        /// <summary>
        /// Instructs the client to reset and disable any active balls
        /// </summary>
        static public void Object_BallReset(IEnumerable<Player> players, IEnumerable<Ball> bState)
        {   //Prepare the packet
            SC_BallState ball = new SC_BallState();
            ball.bDisable = true;
            ball.balls = bState;

            //Send it
            foreach (Player p in players)
            {
                //Update server side ball handlers
                foreach (Ball b in bState)
                {
                    if (p._gotBallID == b._id)
                        p._gotBallID = 999;
                }
                p._client.sendReliable(ball);
            }
        }

        /// <summary>
        /// Sends a ball update to a specific player
        /// </summary>
        static public void Object_Ball(Player player, IEnumerable<Ball> bStates)
        {   //Prepare the packet
            SC_BallState ball = new SC_BallState();
            ball.balls = bStates;

            //Send it
            player._client.sendReliable(ball);
        }

        /// <summary>
        /// Sends a ball update to active players
        /// </summary>
        static public void Object_Ball(IEnumerable<Player> players, Ball bState)
        {   //Prepare the packet
            SC_BallState ball = new SC_BallState();
            ball.singleUpdate = bState;

            //Send it
            foreach (Player p in players)
                p._client.sendReliable(ball);
        }

        /// <summary>
        /// Sends a series of ball updates to active players
        /// </summary>
        static public void Object_Ball(IEnumerable<Player> players, IEnumerable<Ball> bStates)
        {   //Prepare the packet
            SC_BallState ball = new SC_BallState();
            ball.balls = bStates;

            //Send it
            foreach (Player p in players)
                p._client.sendReliable(ball);
        }
	}
}
