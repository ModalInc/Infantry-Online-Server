using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-27, owner: "we need basically 6 portals in the dropship now. each should be labeled appropriately so we should
    /// call each area SOMETHING sector ... the middle portal will be replaced by 6 portals essentially. where the real players
    /// can walk in to be teleported to the appropriate spawn points. (they should never warp in buildings)")
    ///
    /// Open world: the dropship's middle portal is gone; six lio portals (ids 201-206, two rows of three - north areas on top,
    /// south areas below) each land the player at one of the owner's six spawn points (the blue stars on the owner's
    /// "Eol spawn points" picture). The landing is a random open outdoor spot round the point - never a building floor, never
    /// walled in, clear of flags and turrets when it can be - so a group walking in together doesn't stack on one tile.
    /// Bots keep their own landings (Script_Eol.Deploy.cs).
    /// </summary>
    public partial class Script_Eol
    {
        sealed class SectorPortal
        {
            public int PortalId; public string Name; public short TileX, TileY;
        }

        /// <summary>The owner's six spawn points (tiles), read off the picture and moved onto open outdoor ground.</summary>
        static readonly SectorPortal[] SectorPortals =
        {
            new SectorPortal { PortalId = 201, Name = "Northgate Sector",  TileX = 163,  TileY = 114 },   // north-west streets (Sector A)
            new SectorPortal { PortalId = 202, Name = "Crossroads Sector", TileX = 541,  TileY = 399 },   // the middle junction (A / C)
            new SectorPortal { PortalId = 203, Name = "Airfield Sector",   TileX = 1046, TileY = 143 },   // north-east, by the runway (C)
            new SectorPortal { PortalId = 204, Name = "Lowtown Sector",    TileX = 200,  TileY = 681 },   // south-west blocks (B)
            new SectorPortal { PortalId = 205, Name = "Southgate Sector",  TileX = 627,  TileY = 749 },   // the south road (B / C)
            new SectorPortal { PortalId = 206, Name = "Eastgate Sector",   TileX = 1179, TileY = 517 },   // the east side (C)
        };

        /// <summary>Players land within this many tiles of the spawn point (then wider if it's all taken).</summary>
        static readonly int[] SectorPortalSpreadTiles = { 12, 22, 36 };

        static SectorPortal SectorPortalOf(LioInfo.Portal portal)
        {
            if (portal == null)
                return null;
            foreach (SectorPortal s in SectorPortals)
                if (s.PortalId == portal.GeneralData.Id)
                    return s;
            return null;
        }

        /// <summary>
        /// Open world: where this dropship portal lands the player (null = not one of the six, or no open ground found).
        /// </summary>
        Helpers.ObjectState PickSectorPortalLanding(SectorPortal sp)
        {
            if (sp == null || _arena == null)
                return null;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            int cx = sp.TileX * 16 + 8, cy = sp.TileY * 16 + 8;
            foreach (int spread in SectorPortalSpreadTiles)
                foreach (int clear in new[] { LandingClearRaw, LandingClearRaw / 2, 0 })
                    for (int tries = 0; tries < 60; tries++)
                    {
                        short x = (short)(cx + _rand.Next(-spread, spread + 1) * 16);
                        short y = (short)(cy + _rand.Next(-spread, spread + 1) * 16);
                        if (!SectorPortalSpotOk(x, y))
                            continue;
                        if (clear > 0 && LandingNearFlagOrTurret(x, y, clear))
                            continue;
                        return new Helpers.ObjectState { positionX = x, positionY = y };
                    }
            Helpers.ObjectState near = NearestOutdoors(cx, cy);
            return near != null && SectorPortalSpotOk(near.positionX, near.positionY) ? near : null;
        }

        /// <summary>Outdoors (no building floor), room to stand, and not a sealed pocket.</summary>
        bool SectorPortalSpotOk(short x, short y)
        {
            if (x < 32 || y < 32)
                return false;
            if (_arena.getTerrainID(x, y) == BuildingFloorTerrain || IsIndoors(x, y))
                return false;
            if (!TileOpenAround(x, y))
                return false;
            return IsOpenConnectedGround(x, y);
        }

        /// <summary>Player.Portal in the open world: handled (true) when it is one of the six sector portals.</summary>
        bool TrySectorPortal(InfServer.Game.Player player, LioInfo.Portal portal)
        {
            SectorPortal sp = SectorPortalOf(portal);
            if (sp == null)
                return false;
            Helpers.ObjectState at = PickSectorPortalLanding(sp);
            if (at == null)
            {
                Log.write(TLog.Warning, "[Eol] {0}: no open ground for {1}.", sp.Name, player._alias);
                player.sendMessage(-1, "Couldn't find a clear landing zone in " + sp.Name + " - try again in a moment.");
                return true;
            }
            warp(player, at);
            player.sendMessage(-1, "&Deployed to " + sp.Name + " (" + Helpers.posToLetterCoord(at.positionX, at.positionY) + ").");
            return true;
        }
    }
}
