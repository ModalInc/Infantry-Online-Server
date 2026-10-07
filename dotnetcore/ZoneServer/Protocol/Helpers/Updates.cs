using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Network;
using InfServer.Game;
using InfServer.Bots;

using Assets;

namespace InfServer.Protocol
{	/// <summary>
	/// Provides a series of functions for easily serialization of packets
	/// </summary>
	public partial class Helpers
	{	///////////////////////////////////////////////////
		// Member Functions
		//////////////////////////////////////////////////
		/// <summary>
		/// Snapshot a live equip list so deferred packet serialization cannot race
		/// against Clear/Add on the source (bots sync utilities on another path).
		/// </summary>
		static List<ItemInfo.UtilityItem> SnapshotActiveUtilities(List<ItemInfo.UtilityItem> source)
		{
			if (source == null)
				return null;

			lock (source)
				return new List<ItemInfo.UtilityItem>(source);
		}

		static List<ushort> SnapshotActiveEquip(List<ushort> source)
		{
			if (source == null)
				return null;

			lock (source)
				return new List<ushort>(source);
		}

		/// <summary>
		/// Obtains a list of valid targets to route the update to
		/// </summary>
		static public List<Player> getRouteList(Arena arena, Helpers.ObjectState state, bool bWeapon, int weaponRouteRange, IEnumerable<Player> additionals, int oldPosX, int oldPosY)
		{	//Which range does it qualify for?
			int range = Arena.routeRange;

			//If the weapon has a route range, use it
			if (bWeapon && weaponRouteRange != 0)
				range = weaponRouteRange;
			else
			{
				if (bWeapon)
					range = Arena.routeWeaponRange;
				else if (state.updateNumber % Arena.routeRadarRangeFarFactor == 0)
					range = Arena.routeRadarRangeFar;
				else if (state.updateNumber % Arena.routeRadarRangeFactor == 0)
					range = Arena.routeRadarRange;
			}

			//Get the eligible players
			List<Player> audience = arena.getPlayersAndSpecInRange(state.positionX, state.positionY, range);

			if (additionals != null)
			{
				foreach (Player p in additionals)
					if (!audience.Contains(p))
						audience.Add(p);
			}

			//Is our old position far from our new? (warped?)
			if (Math.Sqrt(Math.Pow(state.positionX - oldPosX, 2) + Math.Pow(state.positionY - oldPosY, 2)) > Arena.routeRange)
			{	//We want our old audience to be notified too
				List<Player> extraAudience = arena.getPlayersAndSpecInRange(oldPosX, oldPosY, Arena.routeRadarRangeFar);
				foreach (Player p in extraAudience)
					if (!audience.Contains(p))
						audience.Add(p);
			}

			return audience;
		}

		/// <summary>
		/// Provides an easy means of routing computer vehicle update messages
		/// </summary>
		static public void Update_RouteComputer(Computer update)
		{	//Prepare the appropriate packets
			SC_PlayerTeamUpdate tu = new SC_PlayerTeamUpdate();

			tu.tickUpdate = Environment.TickCount;
			tu.player = null;
			tu.vehicle = update;
			tu.itemID = (short)(update._shouldFire ? update._primaryGun.id : -1);

            //We only route active utilities if we're not broken
            if (update._state.health > update._type.HitpointsRequiredToOperate)
                tu.activeUtilities = SnapshotActiveUtilities(update._activeEquip);

			//TODO: Should we use SC_PlayerUpdate for computers too?

			//Get the audience
			int weaponRouteRange = 0;
			if (update._primaryGun != null)
				update._primaryGun.getRouteRange(out weaponRouteRange);

			List<Player> audience = getRouteList(update._arena, update._state, update._shouldFire, weaponRouteRange, null, 
				update._state.positionX, update._state.positionY);

			//Send our updates..
			foreach (Player player in audience)
				player._client.send(tu);				

			// Disable fire
			update._shouldFire = false;
			update._sendUpdate = false;
		}

		/// <summary>
		/// Provides an easy means of routing movement update messages between players
		/// </summary>
		static public void Update_RoutePlayer(Player update, CS_PlayerUpdate pkt, int updateTick, int oldPosX, int oldPosY)
		{	//Prepare the appropriate packets
			SC_PlayerTeamUpdate tu = new SC_PlayerTeamUpdate();

			tu.tickUpdate = updateTick;
			tu.player = update;
			tu.vehicle = update.ActiveVehicle;
			tu.itemID = pkt.itemID;
			
			tu.activeEquip = SnapshotActiveEquip(pkt.activeEquip);

			SC_PlayerUpdate up = new SC_PlayerUpdate();

			up.tickUpdate = updateTick;
			up.player = update;
			up.vehicle = update.ActiveVehicle;
			up.itemID = pkt.itemID;

			up.activeEquip = SnapshotActiveEquip(pkt.activeEquip);

            //Get the routing audience

            bool itemUpdate = false;
            bool itemRouteFriendly = false;
            int weaponRouteRange = 0;

            if (pkt.itemID != 0)
            {
                itemUpdate = true;
                ItemInfo item = _server._assets.getItemByID(pkt.itemID);
                if (item != null)
                {
                    itemRouteFriendly = item.routeFriendly;
                    item.getRouteRange(out weaponRouteRange);
                }
                else
                {
                    Log.write(TLog.Warning, "Update_RoutePlayer(): item is null");
                }
            }

			List<Player> audience = getRouteList(update._arena, update._state, itemUpdate, weaponRouteRange, update._spectators, oldPosX, oldPosY);

			//Send our updates..
			foreach (Player player in audience)
			{	//Don't send duplicates
				if (player == update)
					continue;

                //Are we updating an item?
                if (itemUpdate)
                {
                    //Route friendly?
                    if (itemRouteFriendly)
                    {
                        //Route only to teammates.
                        if (player._team == update._team)
                            player._client.send(tu);
                        continue;
                    }
                }
                
				if (player.IsSpectator || player._team == update._team)
					player._client.send(tu);
				else
					player._client.send(up);
			}
		}

		/// <summary>
		/// Soft player-mimic: bots egress like real players —
		/// same updateTick formula, getRouteList audience, SC_PlayerEnter+Vehicles create,
		/// and SC_PlayerUpdate with bBot=false / wirePlayerId = bot id.
		/// </summary>
		static public void Update_RouteBot(Bot update)
		{
			if (update?._arena == null || update._state == null)
				return;

			int updateTick = update.AdvancePlayerStyleUpdateTick();
			update.PreparePlayerMimicWireVelocity();

			int oldPosX = update._hasPlayerRoutePos ? update._lastPlayerRoutePosX : update._state.positionX;
			int oldPosY = update._hasPlayerRoutePos ? update._lastPlayerRoutePosY : update._state.positionY;

			bool itemUpdate = update._itemUseID != 0;
			int weaponRouteRange = 0;
			if (itemUpdate && _server?._assets != null)
			{
				ItemInfo item = _server._assets.getItemByID(update._itemUseID);
				if (item != null)
					item.getRouteRange(out weaponRouteRange);
			}

			// Same audience rules as Update_RoutePlayer (incl. radar factor ticks).
            List<Player> audience = getRouteList(
				update._arena, update._state, itemUpdate, weaponRouteRange,
				update._spectators, oldPosX, oldPosY);

			RouteBotAsPlayerToAudience(update, audience, updateTick);

			update._lastPlayerRoutePosX = update._state.positionX;
			update._lastPlayerRoutePosY = update._state.positionY;
			update._hasPlayerRoutePos = true;
			update._tickLastPlayerRoute = Environment.TickCount;
			update._wireVelOverride = false;
		}

		/// <summary>
		/// Ensure each observer has PlayerEnter + Vehicles (human base-vehicle layout),
		/// then send player-style pose packets (never bBot).
		/// </summary>
		static void RouteBotAsPlayerToAudience(Bot update, List<Player> audience, int updateTick)
		{
			if (audience == null || audience.Count == 0)
				return;

			if (string.IsNullOrEmpty(update._alias))
				update._alias = "Bot " + update._id;

			short itemID = (short)update._itemUseID;
			bool onNamelist = Arena.ShouldBotAppearOnNamelist(update);
			bool alreadyNamelist = update._arena != null
				&& update._arena.IsBotNamelistAdvertised(update._id);

			// Corpse after MaxDeaths (or otherwise off-roster) must not re-SC_PlayerEnter.
			if (!onNamelist && alreadyNamelist && update._arena != null)
			{
				update._arena.RemoveBotFromNamelist(update);
				alreadyNamelist = false;
			}

			for (int i = 0; i < audience.Count; i++)
			{
				Player player = audience[i];
				if (player?._client == null)
					continue;

				if (!update.IsClientAwareOfBot(player))
				{
					// Soft-mimic: never send Vehicles without a prior PlayerEnter — empty / hull-only
					// state is a known client crash. Off-roster corpses must not invent a new entity.
					if (!onNamelist)
						continue;

					if (string.IsNullOrEmpty(update._alias))
						update._alias = "Bot " + update._id;

					// After death, the client still has the namelist row but dropped the car body.
					// Re-send Vehicles only — Leave/Enter reprints "Player entered" and can desync.
					bool known = update._arena != null
						&& update._arena.IsBotNamelistKnownBy(player, update._id);
					bool repair = update.NeedsHullReattach && known;
					if (!repair)
						update._arena?.SendBotNamelistEntryTo(player, update, leaveFirst: known);

					SC_Vehicles vehicles = new SC_Vehicles();
					vehicles.singleUpdate = update;
					if (player._client.sendReliable(vehicles))
						update.MarkClientAwareOfBot(player);
				}

				// Off-roster corpses: no player-wire pose (that recreates the namelist entity).
				if (!onNamelist)
					continue;

				if (player.IsSpectator || player._team == update._team)
				{
					SC_PlayerTeamUpdate tu = new SC_PlayerTeamUpdate
					{
						tickUpdate = updateTick,
						player = null,
						vehicle = update,
						itemID = itemID,
						activeUtilities = null,
						activeEquip = null,
						bBot = false,
						wirePlayerId = update._id
					};
					tu.CaptureBotPose(update);
					player._client.send(tu);
				}
				else
				{
					SC_PlayerUpdate up = new SC_PlayerUpdate
					{
						tickUpdate = updateTick,
						player = null,
						vehicle = update,
						itemID = itemID,
						activeUtilities = null,
						activeEquip = null,
						bBot = false,
						wirePlayerId = update._id
					};
					up.CaptureBotPose(update);
					player._client.send(up);
				}
			}

			// LogBotWirePoseThrottled(update, updateTick);
		}

		// Debug (disabled) — BotWire pose logging helpers.
		// static int _tickLastBotWirePoseLog;
		// static ushort _lastBotWirePoseLogId;

		static void LogBotWirePoseThrottled(Bot update, int tickUpdate)
		{
			// Debug (disabled) — restore call site + fields above to re-enable.
			/*
			if (update?._state == null)
				return;

			int now = Environment.TickCount;
			bool moving = Math.Abs(update._state.velocityX) > 2 || Math.Abs(update._state.velocityY) > 2;
			if (!moving && now - _tickLastBotWirePoseLog < 5000)
				return;
			if (now - _tickLastBotWirePoseLog < 2000)
				return;

			_tickLastBotWirePoseLog = now;
			_lastBotWirePoseLogId = update._id;
			short vx = update._wireVelOverride ? update._wireVelX : update._state.velocityX;
			short vy = update._wireVelOverride ? update._wireVelY : update._state.velocityY;
			Log.write(TLog.Normal,
				"[BotWire] id={0} style=playerMimic tickMs={1} vx={2} vy={3} dir={4} yaw={5} item={6} synVel={7}",
				update._id,
				tickUpdate & 0xFFFF,
				vx,
				vy,
				(ushort)update._state.direction,
				update._state.yaw,
				update._itemUseID,
				update._wireVelOverride ? 1 : 0);
			*/
		}
	}
}
