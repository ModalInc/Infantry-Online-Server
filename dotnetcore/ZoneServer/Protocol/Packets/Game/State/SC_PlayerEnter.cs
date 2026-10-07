using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Network;
using InfServer.Game;
using InfServer.Bots;

namespace InfServer.Protocol
{	/// <summary>
	/// SC_PlayerEnter contains updates regarding players entering the arena 
	/// </summary>
	public class SC_PlayerEnter : PacketBase
	{	// Member Variables
		///////////////////////////////////////////////////
		public Player singlePlayer;
		public Player exclude;
		public IEnumerable<Player> players;
		public IEnumerable<Bot> bots;

		public const ushort TypeID = (ushort)Helpers.PacketIDs.S2C.PlayerEnter;


		///////////////////////////////////////////////////
		// Member Functions
		//////////////////////////////////////////////////
		/// <summary>
		/// Creates an empty packet of the specified type. This is used
		/// for constructing new packets for sending.
		/// </summary>
		public SC_PlayerEnter()
			: base(TypeID)
		{
		}

		/// <summary>
		/// Serializes the data stored in the packet class into a byte array ready for sending.
		/// </summary>
		public override void Serialize()
		{
			if (singlePlayer != null)
			{	//Just write a single entry
				Write((byte)TypeID);

				Write((singlePlayer._team != null ? singlePlayer._team._name : ""), 32);
				Write(singlePlayer._alias, 32);
				Write((singlePlayer._squad != null ? singlePlayer._squad : ""), 32);

				Write(singlePlayer._id);
				Write((ushort)singlePlayer._baseVehicle._type.Id);
				Write(singlePlayer._team._id);
				Write((UInt32)new Random().Next());
				Write((bool)true);

				return;
			}

			//Write out each asset
			bool bEmpty = true;

			if (players != null)
			{
				foreach (Player player in players)
				{	//Don't send to our exclude
					if (exclude == player)
						continue;
					
					//Not sure why it does this for each entry
					Write((byte)TypeID);

					Write((player._team != null ? player._team._name : ""), 32);
					Write(player._alias, 32);
					Write((player._squad != null ? player._squad : ""), 32);

					Write(player._id);
					Write((ushort)player._baseVehicle._type.Id);
					Write(player._team._id);
					Write((UInt32)new Random().Next());
					Write((bool)true);

					bEmpty = false;
				}
			}

			if (bots != null)
			{
				foreach (Bot bot in bots)
				{
					if (bot == null || string.IsNullOrEmpty(bot._alias))
						continue;

					Write((byte)TypeID);

					Write((bot._team != null ? bot._team._name : ""), 32);
					Write(bot._alias, 32);
					Write("", 32);

					Write(bot._id);
					Write((ushort)bot._type.Id);
					Write(bot._team != null ? bot._team._id : (short)0);
					Write((UInt32)new Random().Next());
					// Must be true: client PlayerEnter creates an inhabited hull only when
					// this flag is set (inhabitant = player/vehicle id). False → empty
					// vehicle and an invisible bot (shots still appear).
					Write((bool)true);

					bEmpty = false;
				}
			}

			if (bEmpty)
				Write((byte)TypeID);
		}

		/// <summary>
		/// Returns a meaningful of the packet's data
		/// </summary>
		public override string Dump
		{
			get
			{
				return "Player info.";
			}
		}

	}
}
