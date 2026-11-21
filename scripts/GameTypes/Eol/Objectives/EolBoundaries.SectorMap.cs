using System;
using System.Collections.Generic;

using InfServer.Game;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// The sectors follow the zone owner's barrier walls (2026-11-15 map: three sectors, the walls of barbed wire and
    /// red physics). Each sector is the open ground reachable from its seed tile without crossing a wall; wall tiles and
    /// sealed pockets go to the nearest sector. Anything outside the play map (Pioneer Station, the dropship) is in no
    /// sector - never toxic, never "open play area".
    ///   Sector A = north-west, Sector B = south-west, Sector C = the whole east side.
    /// Built once from the level's physics (about 1.6M tiles, well under a second).
    /// </summary>
    public partial class EolBoundaries
    {
        /// <summary>The play map in tiles (outside it: no sector).</summary>
        const int PlayMapMaxTileX = 1377, PlayMapMaxTileY = 896;

        /// <summary>A seed tile on open ground in each sector (A: the NW dropship buffer, B: the SW open ground, C: the NE buffer).</summary>
        static readonly int[,] SectorSeedTiles = { { 394, 267 }, { 250, 800 }, { 932, 127 } };

        byte[] _sectorOfTile;          // 0 = none, 1 = A, 2 = B, 3 = C
        int _sectorMapWidth, _sectorMapHeight;
        readonly short[,] _sectorBox = new short[4, 4];   // [sector, minX/maxX/minY/maxY] raw
        bool _sectorMapFailed;

        public string[] AllSectorNames { get { return new[] { sectorA, sectorB, sectorC }; } }
        public const int SectorCount = 3;

        string SectorNameOfIndex(int i)
        {
            switch (i) { case 1: return sectorA; case 2: return sectorB; case 3: return sectorC; default: return null; }
        }

        int SectorIndexOfName(string name)
        {
            if (string.Equals(name, sectorA, StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(name, sectorB, StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(name, sectorC, StringComparison.OrdinalIgnoreCase)) return 3;
            return 0;
        }

        /// <summary>The sector at a raw map point; null = outside the play map (station, dropship) or the map isn't built.</summary>
        public string SectorAt(int x, int y)
        {
            return SectorNameOfIndex(SectorIndexAt(x, y));
        }

        /// <summary>(2026-11-21) True when the point (raw) lies in that sector of the sector map.</summary>
        public bool IsInSector(string sectorName, int x, int y)
        {
            int want = SectorIndexOfName(sectorName);
            return want != 0 && SectorIndexAt(x, y) == want;
        }

        int SectorIndexAt(int x, int y)
        {
            if (!EnsureSectorMap())
                return 0;
            int tx = x >> 4, ty = y >> 4;
            if (tx < 0 || ty < 0 || tx >= _sectorMapWidth || ty >= _sectorMapHeight)
                return 0;
            return _sectorOfTile[ty * _sectorMapWidth + tx];
        }

        bool EnsureSectorMap()
        {
            if (_sectorOfTile != null)
                return true;
            if (_sectorMapFailed)
                return false;
            try
            {
                LvlInfo lvl = _arena == null ? null : _arena._server._assets.Level;
                if (lvl == null || lvl.Tiles == null)
                    return false;
                BuildSectorMap(lvl);
                return true;
            }
            catch (Exception e)
            {
                _sectorMapFailed = true;
                Log.write(TLog.Error, "[Eol] Sector map could not be built: {0}", e);
                return false;
            }
        }

        void BuildSectorMap(LvlInfo lvl)
        {
            int w = Math.Min(lvl.Width, PlayMapMaxTileX + 1), h = Math.Min(lvl.Height, PlayMapMaxTileY + 1);
            byte[] map = new byte[w * h];
            Queue<int> q = new Queue<int>();

            // 1) each sector's open ground: flood from its seed, never through a wall
            for (int s = 0; s < SectorSeedTiles.GetLength(0); s++)
            {
                int sx = SectorSeedTiles[s, 0], sy = SectorSeedTiles[s, 1];
                if (sx >= w || sy >= h || IsWallTile(lvl, sx, sy) || map[sy * w + sx] != 0)
                {
                    Log.write(TLog.Warning, "[Eol] Sector map: seed for {0} at tile {1},{2} is not open ground.", SectorNameOfIndex(s + 1), sx, sy);
                    continue;
                }
                byte id = (byte)(s + 1);
                map[sy * w + sx] = id;
                q.Enqueue(sy * w + sx);
                while (q.Count > 0)
                {
                    int t = q.Dequeue(), x = t % w, y = t / w;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                            continue;
                        int n = ny * w + nx;
                        if (map[n] != 0 || IsWallTile(lvl, nx, ny))
                            continue;
                        map[n] = id;
                        q.Enqueue(n);
                    }
                }
            }

            // 2) walls and sealed pockets: the nearest sector (grow every sector outwards together)
            for (int i = 0; i < map.Length; i++)
                if (map[i] != 0)
                    q.Enqueue(i);
            while (q.Count > 0)
            {
                int t = q.Dequeue(), x = t % w, y = t / w;
                byte id = map[t];
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int n = ny * w + nx;
                    if (map[n] != 0)
                        continue;
                    map[n] = id;
                    q.Enqueue(n);
                }
            }

            // 3) each sector's bounding box (raw) - for "pick a random point, then test SectorAt" callers
            for (int s = 1; s <= 3; s++) { _sectorBox[s, 0] = short.MaxValue; _sectorBox[s, 1] = short.MinValue; _sectorBox[s, 2] = short.MaxValue; _sectorBox[s, 3] = short.MinValue; }
            int[] count = new int[4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = map[y * w + x];
                    if (s == 0)
                        continue;
                    count[s]++;
                    short rx0 = (short)(x * 16), rx1 = (short)(x * 16 + 15), ry0 = (short)(y * 16), ry1 = (short)(y * 16 + 15);
                    if (rx0 < _sectorBox[s, 0]) _sectorBox[s, 0] = rx0;
                    if (rx1 > _sectorBox[s, 1]) _sectorBox[s, 1] = rx1;
                    if (ry0 < _sectorBox[s, 2]) _sectorBox[s, 2] = ry0;
                    if (ry1 > _sectorBox[s, 3]) _sectorBox[s, 3] = ry1;
                }

            _sectorMapWidth = w;
            _sectorMapHeight = h;
            _sectorOfTile = map;
            for (int s = 1; s <= 3; s++)
                Log.write(TLog.Normal, "[Eol] Sector map: {0} = {1} tiles, box raw x {2}-{3} y {4}-{5}.",
                    SectorNameOfIndex(s), count[s], _sectorBox[s, 0], _sectorBox[s, 1], _sectorBox[s, 2], _sectorBox[s, 3]);
        }

        static bool IsWallTile(LvlInfo lvl, int x, int y)
        {
            int phy = lvl.Tiles[y * lvl.Width + x].Physics;
            return lvl.PhysicsHigh[phy] > 0;
        }

        /// <summary>Raw bounding box of a sector (from the sector map).</summary>
        bool TryGetSectorBox(string sectorName, out short minX, out short maxX, out short minY, out short maxY)
        {
            minX = maxX = minY = maxY = 0;
            int s = SectorIndexOfName(sectorName);
            if (s == 0 || !EnsureSectorMap() || _sectorBox[s, 1] < _sectorBox[s, 0])
                return false;
            minX = _sectorBox[s, 0]; maxX = _sectorBox[s, 1]; minY = _sectorBox[s, 2]; maxY = _sectorBox[s, 3];
            return true;
        }
    }
}
