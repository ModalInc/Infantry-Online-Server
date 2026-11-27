using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-27, owner: "make a harrier spawn at 9,509 too. the one with the cannon. this is for real players. make it so if
    /// its left un attended else where, it respawns back here. i.e for 15minutes if its elsewhere and no1 is driving it, spawn it
    /// back there ... another one spawn at 1074,852 ... the GL type one at 1362,393 ... also at 730,15 for the GL type. then make
    /// an atlas spawn at 767,144")
    ///
    /// Parked vehicles for players (no team - anyone can take one, like the map's own vehicle spawns). Each one has a home
    /// spot: taken away and then left empty away from home for 15 minutes, it goes back home (the old one is removed, a fresh
    /// one parks at home). Destroyed: a new one parks at home a minute later. Their guns need no ammo (.itm).
    /// </summary>
    public partial class Script_Eol
    {
        sealed class FieldVehicle
        {
            public ushort VehId; public short TileX, TileY; public string Name;
            public bool Exact;   // park exactly on the tile (a row of bikes stays a row)
            public Vehicle V; public int AwaySince, GoneSince;
        }

        static readonly ushort VehAtlas = 122, VehDart = 132;
        readonly List<FieldVehicle> _fieldVehicles = new List<FieldVehicle>
        {
            new FieldVehicle { VehId = VehHarrierGvs,   TileX = 9,    TileY = 509, Name = "Harrier GV-s (cannon)" },
            new FieldVehicle { VehId = VehHarrierGvs,   TileX = 1074, TileY = 852, Name = "Harrier GV-s (cannon)" },
            new FieldVehicle { VehId = VehHarrierGvsA2, TileX = 1362, TileY = 393, Name = "Harrier GV-s A2 (grenade launcher)" },
            new FieldVehicle { VehId = VehHarrierGvsA2, TileX = 730,  TileY = 15,  Name = "Harrier GV-s A2 (grenade launcher)" },
            // (2026-11-28 owner) the Atlas at 767,144 is gone: the map already has Atlas spawns at 717,138 and in I9
        };

        /// <summary>(2026-11-27, owner: "spawn dart bikes, 6 of them ... side by side ... stack them to the east from that") each row
        /// starts at this tile: 6 Darts, DartRowGapTiles apart, going east, all facing north.</summary>
        static readonly short[][] DartRows = { new short[] { 1241, 387 }, new short[] { 1172, 189 }, new short[] { 778, 13 },
            new short[] { 72, 122 }, new short[] { 403, 98 }, new short[] { 70, 299 },
            new short[] { 343, 454 }, new short[] { 650, 415 },
            new short[] { 1152, 759 }, new short[] { 800, 558 },   // (owner said 808,558 - moved 8 tiles west: the last two hit a wall)
            new short[] { 724, 722 }, new short[] { 790, 871 },
            new short[] { 455, 802 }, new short[] { 58, 609 },   // (owner said 62,609 - 4 tiles west: the last one hit a wall)
            new short[] { 229, 770 } };
        const int DartsPerRow = 6, DartRowGapTiles = 4;
        /// <summary>(2026-11-27) off while we find the client crash on joining (the Dart's sprite may be the cause).</summary>
        const bool DartRowsEnabled = true;
        bool _dartRowsAdded;

        void EnsureDartRows()
        {
            if (_dartRowsAdded || !DartRowsEnabled)
                return;
            _dartRowsAdded = true;
            foreach (short[] r in DartRows)
                for (int i = 0; i < DartsPerRow; i++)
                    _fieldVehicles.Add(new FieldVehicle { VehId = VehDart, TileX = (short)(r[0] + i * DartRowGapTiles), TileY = r[1], Name = "Dart", Exact = true });
        }

        const int FieldVehicleAbandonMs = 15 * 60 * 1000;   // empty and away from home this long: back home
        const int FieldVehicleRespawnMs = 60 * 1000;        // destroyed: a new one after a minute
        const int FieldVehicleHomeRadius = 320;              // within this (px) of home = at home (a Dart: 48)
        int _tickFieldVehicles;

        /// <summary>(2026-11-27) OFF: clients crashed on joining while these were parked (spawned with no team).</summary>
        const bool FieldVehiclesEnabled = true;

        void PollFieldVehicles(int now)
        {
            if (!FieldVehiclesEnabled)
                return;
            if (_arena == null || !_arena._bGameRunning || _bbetweengames || EolMap.ChaosInEol)
                return;
            if (now - _tickFieldVehicles < 5000)
                return;
            _tickFieldVehicles = now;
            EnsureDartRows();
            foreach (FieldVehicle f in _fieldVehicles)
            {
                short hx = (short)(f.TileX * 16 + 8), hy = (short)(f.TileY * 16 + 8);
                Vehicle v = f.V;
                if (v == null || v.IsDead || v.bCondemned)
                {
                    f.V = null;
                    if (v != null && f.GoneSince == 0)
                        f.GoneSince = NonZeroTick(now);   // (destroyed / removed: wait a minute)
                    if (f.GoneSince != 0 && now - f.GoneSince < FieldVehicleRespawnMs)
                        continue;
                    SpawnFieldVehicle(f, hx, hy);
                    continue;
                }
                bool manned = FieldVehicleManned(v);
                // (2026-11-27) the veh RemoveUnoccupiedTimer (Dart: 60 s) deleted parked field vehicles a minute after they
                // spawned, so they blinked in and out; this script's own 15-minute rule is the only cleanup for these
                if (!manned)
                    v._tickUnoccupied = Environment.TickCount;
                bool home = v._state != null && Helpers.distanceTo(v._state.positionX, v._state.positionY, hx, hy) <= (f.Exact ? 48 : FieldVehicleHomeRadius);
                if (manned || home)
                {
                    f.AwaySince = 0;
                    continue;
                }
                if (f.AwaySince == 0)
                {
                    f.AwaySince = NonZeroTick(now);
                    continue;
                }
                if (now - f.AwaySince < FieldVehicleAbandonMs)
                    continue;
                // left empty away from home for 15 minutes: back home
                Log.write(TLog.Normal, "[Eol] Field vehicle: {0} left at {1} - back to {2},{3}.", f.Name,
                    Helpers.posToLetterCoord(v._state.positionX, v._state.positionY), f.TileX, f.TileY);
                EnqueueVehicleDespawn(v, force: true);
                f.V = null;
                f.AwaySince = 0;
                f.GoneSince = 0;
                SpawnFieldVehicle(f, hx, hy);
            }
        }

        /// <summary>Someone is in it (the driver, or a gunner / passenger in one of its seats).</summary>
        static bool FieldVehicleManned(Vehicle v)
        {
            if (v._inhabitant != null)
                return true;
            if (v._childs != null)
                foreach (Vehicle c in v._childs)
                    if (c != null && c._inhabitant != null)
                        return true;
            return false;
        }

        void SpawnFieldVehicle(FieldVehicle f, short hx, short hy)
        {
            VehInfo info = _arena._server._assets.getVehicleByID(f.VehId);
            if (info == null)
                return;
            Helpers.ObjectState at = (f.Exact && !_arena.getTile(hx, hy).Blocked) ? null : findOpenWarpBot(_arena, hx, hy, 160, 56);
            at = at ?? new Helpers.ObjectState { positionX = hx, positionY = hy };
            at.yaw = 0;
            Vehicle v = _arena.newVehicle(info, null, null, at);   // (no team: any player can take it)
            if (v == null)
                return;
            f.V = v;
            f.GoneSince = 0;
            f.AwaySince = 0;
        }
    }
}
