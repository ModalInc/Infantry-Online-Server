using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Network;
using InfServer.Game;

using Assets;

namespace InfServer.Protocol
{	/// <summary>
	/// SC_PlayerUpdate is used to inform clients of their enemies' movements 
	/// </summary>
	public class SC_PlayerUpdate : PacketBase
	{	// Member Variables
		///////////////////////////////////////////////////
		public int tickUpdate;								//The time when this update was made

		public Player player;								//The player we're updating, if any
		public Vehicle vehicle;								//The vehicle we're updating

		public Int16 itemID;								//Id of the item we fired
		public List<ushort> activeEquip;					//Any additional equipment we may have active
		public List<ItemInfo.UtilityItem> activeUtilities;	//Alternative list type

		public bool bBot;									//Is this a bot?
		/// <summary>When non-zero, written as the player id (namelist / player-smooth bot pose).</summary>
		public ushort wirePlayerId;
		/// <summary>When set, serialize uses frozen pose fields (bots) instead of live vehicle state.</summary>
		public bool bPoseSnapshot;
		public short snapHealth;
		public short snapVelX, snapVelY, snapVelZ;
		public short snapPosX, snapPosY, snapPosZ;
		public byte snapYaw;
		public ushort snapDirection;
		public byte snapUnk1;
		public short snapPitch;
		public short snapEnergy;
		public short snapTeamId;
		public short snapFireAngle;

		public const ushort TypeID = (ushort)Helpers.PacketIDs.S2C.PlayerUpdate;

		/// <summary>Freeze current vehicle pose for deferred bot sends (includes real velocity for client coast/run).</summary>
		public void CaptureBotPose(Vehicle v)
		{
			if (v?._state == null)
				return;
			bPoseSnapshot = true;
			snapHealth = v._state.health;
			snapVelX = v._state.velocityX;
			snapVelY = v._state.velocityY;
			snapVelZ = v._state.velocityZ;
			if (v is InfServer.Bots.Bot bot && bot._wireVelOverride)
			{
				snapVelX = bot._wireVelX;
				snapVelY = bot._wireVelY;
			}
			snapPosX = v._state.positionX;
			snapPosY = v._state.positionY;
			snapPosZ = v._state.positionZ;
			snapYaw = v._state.yaw;
			snapDirection = Helpers.ObjectState.EnsureBotWireDirection(
				(ushort)v._state.direction, snapVelX, snapVelY, v._state.yaw);
			snapUnk1 = (byte)v._state.unk1;
			snapPitch = v._state.pitch;
			snapFireAngle = v._state.fireAngle;
			snapTeamId = (v._team != null) ? v._team._id : (short)-1;
		}


		///////////////////////////////////////////////////
		// Member Functions
		//////////////////////////////////////////////////
		/// <summary>
		/// Creates an empty packet of the specified type. This is used
		/// for constructing new packets for sending.
		/// </summary>
		public SC_PlayerUpdate()
			: base(TypeID)
		{
		}

		/// <summary>
		/// Serializes the data stored in the packet class into a byte array ready for sending.
		/// </summary>
		public override void Serialize()
		{
			Write((byte)TypeID);

			Write((byte)0);			//Really not sure what this is

			if (player != null)
				Write((itemID == 0) ? (short)(-player.Bounty) : itemID);
			else
				Write(itemID);

			Write(vehicle._id);
			Write(tickUpdate);

			if (wirePlayerId != 0)
				Write(wirePlayerId);
			else if (!bBot)
				Write((player == null ? (ushort)0xFFFF : player._id));
			else
				Write(vehicle._id);

			switch (vehicle._type.Type)
			{
				case VehInfo.Types.Car:
					if (bPoseSnapshot)
					{
						Write(snapHealth);
						Write(snapVelX);
						Write(snapVelY);
						Write(snapVelZ);
						Write(snapPosX);
						Write(snapPosY);
						Write(snapPosZ);
						Write(snapYaw);
						Write(snapDirection);
						Write(snapUnk1);
					}
					else
					{
						Write(vehicle._state.health);
						Write(vehicle._state.velocityX);
						Write(vehicle._state.velocityY);
						Write(vehicle._state.velocityZ);
						Write(vehicle._state.positionX);
						Write(vehicle._state.positionY);
						Write(vehicle._state.positionZ);
						Write(vehicle._state.yaw);
						Write((UInt16)vehicle._state.direction);
						Write((byte)vehicle._state.unk1);
					}
					break;

				case VehInfo.Types.Dependent:
					if (bPoseSnapshot)
					{
						Write(snapVelX);
						Write(snapVelY);
						Write(snapVelZ);
						Write(snapPosX);
						Write(snapPosY);
						Write(snapPosZ);
						Write(snapHealth);
						Write(snapPitch);
						Write(snapYaw);
						Write(snapYaw);
						Write(snapUnk1);
					}
					else
					{
						Write(vehicle._state.velocityX);
						Write(vehicle._state.velocityY);
						Write(vehicle._state.velocityZ);
						Write(vehicle._state.positionX);
						Write(vehicle._state.positionY);
						Write(vehicle._state.positionZ);
						Write(vehicle._state.health);
						Write(vehicle._state.pitch);
						Write(vehicle._state.yaw);
						Write(vehicle._state.yaw);
						Write((byte)vehicle._state.unk1);
					}
					break;

				case VehInfo.Types.Computer:
					Write(vehicle._state.health);
					Write((vehicle._team != null) ? vehicle._team._id : (short)-1);
					Write(vehicle._state.positionX);
					Write(vehicle._state.positionY);
					Write(vehicle._state.positionZ);
					Write((short)64);
					Write((short)1);
					Write((short)0);
					Write(vehicle._state.yaw);	//Base yaw?
					Write(vehicle._state.yaw);	//Turret yaw?
					Write((ushort)206);
					break;
			}

			//Write in all additional equipment
			if (activeEquip != null)
			{
				List<ushort> equipSnapshot;
				lock (activeEquip)
					equipSnapshot = new List<ushort>(activeEquip);
				foreach (ushort equip in equipSnapshot)
					Write(equip);
			}

			if (activeUtilities != null)
			{
				List<ItemInfo.UtilityItem> utilSnapshot;
				lock (activeUtilities)
					utilSnapshot = new List<ItemInfo.UtilityItem>(activeUtilities);
				foreach (ItemInfo.UtilityItem util in utilSnapshot)
					Write((ushort)util.id);
			}
		}

		/// <summary>
		/// Returns a meaningful of the packet's data
		/// </summary>
		public override string Dump
		{
			get
			{
				return "Player update.";
			}
		}
	}
}
