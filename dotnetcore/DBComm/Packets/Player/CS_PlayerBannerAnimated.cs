﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Network;
using InfServer.Data;

namespace InfServer.Protocol
{	/// <summary>
    /// CS_PlayerBanner contains an update for a player's banner
    /// </summary>
    public class CS_PlayerBannerAnimated<T> : PacketBase
        where T : IClient
    {	// Member Variables
        ///////////////////////////////////////////////////
        public PlayerInstance player;		//The player instance we're referring to
        public long bannerID;
        public int framesPerSecond;
        public int curFrame;
        public byte[] banner;

        //Packet routing
        public const ushort TypeID = (ushort)DBHelpers.PacketIDs.C2S.PlayerBannerAnimated;
        static public event Action<CS_PlayerBannerAnimated<T>, T> Handlers;


        ///////////////////////////////////////////////////
        // Member Functions
        //////////////////////////////////////////////////
        /// <summary>
        /// Creates an empty packet of the specified type. This is used
        /// for constructing new packets for sending.
        /// </summary>
        public CS_PlayerBannerAnimated()
            : base(TypeID)
        {
            player = new PlayerInstance();
        }

        /// <summary>
        /// Creates an instance of the dummy packet used to debug communication or 
        /// to represent unknown packets.
        /// </summary>
        /// <param name="typeID">The type of the received packet.</param>
        /// <param name="buffer">The received data.</param>
        public CS_PlayerBannerAnimated(ushort typeID, byte[] buffer, int index, int count)
            : base(typeID, buffer, index, count)
        {
            player = new PlayerInstance();
        }

        /// <summary>
        /// Routes a new packet to various relevant handlers
        /// </summary>
        public override void Route()
        {	//Call all handlers!
            if (Handlers != null)
                Handlers(this, (_client as Client<T>)._obj);
        }

        /// <summary>
        /// Serializes the data stored in the packet class into a byte array ready for sending.
        /// </summary>
        public override void Serialize()
        {	//Type ID
            Write((byte)TypeID);

            Write(player.id);
            Write(player.magic);
            Write(bannerID);
            Write(framesPerSecond);
            Write(curFrame);
            Write(banner);
        }

        /// <summary>
        /// Deserializes the data present in the packet contents into data fields in the class.
        /// </summary>
        public override void Deserialize()
        {
            player.id = _contentReader.ReadUInt16();
            player.magic = _contentReader.ReadInt32();

            bannerID = _contentReader.ReadInt64();
            framesPerSecond = _contentReader.ReadInt32();
            curFrame = _contentReader.ReadInt32();
            banner = _contentReader.ReadBytes(8000);
        }

        /// <summary>
        /// Returns a meaningful of the packet's data
        /// </summary>
        public override string Dump
        {
            get
            {
                return "Player banner animation update";
            }
        }
    }
}
