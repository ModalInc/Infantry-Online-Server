using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// SPAWN zones (2026-11-15, the zone owner's drawing "boundary.png", the green areas marked SPAWN):
    ///   - no bot goes into any of them (roaming, chasing or spawning) - the brain turns bots back near the edge;
    ///   - the dropship teleport lands players inside the zone of the sector it sends them to. For now only Sector A
    ///     (the big blue building and the open ground around it, not the buildings to its right); B and C keep the
    ///     old landing (anywhere open in the sector) until the owner says so - see DropshipZoneSectors.
    /// Traced from the drawing (radar image pixel = tile + (7,25)), filled, in cells of 4x4 tiles:
    /// "cellRow:firstCell-lastCell,..." per row.
    /// </summary>
    public partial class Script_Eol
    {
        const int SpawnZoneCell = 4;

        /// <summary>Sectors whose dropship landing is inside their spawn zone.</summary>
        static readonly string[] DropshipZoneSectors = { "Sector A", "Sector B", "Sector C" };   // (2026-11-21, owner: "make the spawn points we mentioned before in boundary.png" - B and C too)

        static readonly string[] SpawnZoneA =
        {
            "68:134-135", "69:133-136", "70:132-137", "71:132-137", "72:132-137", "73:132-137", "74:132-137", "75:132-137",
            "76:132-137", "77:132-137", "78:132-138", "79:132-138", "80:132-138", "81:132-138", "82:132-138", "83:132-139",
            "84:132-139", "85:132-139", "86:132-139", "87:132-139", "88:132-139", "89:132-140", "90:131-140", "91:130-130,132-140,167-173",
            "92:114-118,129-140,166-173", "93:112-121,126-140,166-174", "94:111-139,163-174", "95:109-140,162-175", "96:108-140,161-180", "97:105-141,160-182", "98:102-184", "99:102-105,109-184",
            "100:101-185", "101:101-184", "102:101-148,152-171", "103:101-141", "104:102-111,113-129,131-141", "105:102-110,112-141", "106:102-109,111-141", "107:102-108,110-141",
            "108:102-107,109-141", "109:102-106,108-109,112-114,118-129,131-141", "110:101-105,107-142", "111:101-104,106-144", "112:101-103,105-108,111-111,117-120,122-129,131-136,138-150", "113:101-103,105-108,111-111,117-120,122-158", "114:101-108,112-113,117-120,122-158", "115:101-120,122-136,138-142,144-157",
            "116:101-129,131-154", "117:101-145,149-153", "118:101-136,138-140,151-152", "119:100-110,112-118,120-121,123-140", "120:100-110,112-119,121-122,124-139", "121:100-120,122-123,125-139", "122:100-121,123-139", "123:100-122,124-125,127-140",
            "124:100-140", "125:101-140", "126:102-140", "127:103-140", "128:105-136,138-140", "129:108-140", "130:118-134,136-136",
        };
        static readonly string[] SpawnZoneB =
        {
            "148:91-93,105-115", "149:91-130", "150:92-131", "151:92-132", "152:93-93,95-96,99-101,105-106,110-130,132-132", "153:93-111,113-134", "154:93-134", "155:93-93,95-115,117-121,123-135",
            "156:93-116,118-134", "157:93-96,100-104,108-108,110-117,120-121,123-135", "158:93-108,110-135", "159:93-93,95-96,98-108,110-131,133-135", "160:93-97,99-107,109-111,114-120,123-136", "161:93-97,99-136", "162:93-93,95-131,133-136", "163:93-97,99-100,106-135",
            "164:93-110,112-115,118-119,123-127,129-133", "165:93-116,118-134", "166:93-93,95-98,100-103,105-134", "167:93-134", "168:93-110,112-115,117-134", "169:93-98,100-102,104-127,129-134", "170:93-94,97-97,99-106,108-120,122-135", "171:93-106,108-110,113-120,123-135",
            "172:93-106,108-135", "173:94-100,103-107,109-121,123-135", "174:94-108,118-135", "175:94-127,129-133", "176:94-121,123-134", "177:94-94,96-109,111-134", "178:94-94,96-101,105-134", "179:94-94,96-101,103-134",
            "180:94-121,123-127,129-134", "181:94-135", "182:94-135", "183:94-94,96-101,103-121,123-126,128-132,134-135", "184:96-135", "185:108-130", "186:115-128", "187:119-127",
        };
        static readonly string[] SpawnZoneC =
        {
            "8:273-275,278-281", "9:261-261,267-267,269-282,284-286", "10:261-261,264-276,278-282,284-287", "11:261-262,264-270,272-276,284-288", "12:261-263,265-277,283-288", "13:261-263,266-273,275-288", "14:261-263,266-271,278-287", "15:261-262,266-270,279-287",
            "16:261-262,265-269,280-288", "17:261-261,264-268,280-288", "18:263-267,280-289", "19:262-267,280-289", "20:261-267,278-278,281-283,285-290", "21:261-268,278-284,286-291", "22:261-270,278-291", "23:261-269,277-281,287-292",
            "24:261-269,276-280,287-292", "25:261-269,275-279,287-292", "26:261-269,274-278,287-293", "27:262-269,272-276,286-294", "28:262-276,285-296", "29:261-276,282-288,295-298", "30:263-276,281-288,296-300", "31:263-283,286-287,296-301",
            "32:261-283,299-302", "33:263-283,299-302", "34:263-273,275-283,299-303", "35:261-287,299-304", "36:261-270,273-287,299-305", "37:263-287,298-306", "38:263-287,295-304", "39:261-287,294-305,307-307",
            "40:262-287,293-305,307-308", "41:263-296,298-305,307-308", "42:263-296,299-305,307-309", "43:261-297,306-309", "44:263-299,306-309", "45:260-309", "46:258-280,282-309", "47:255-281,284-308",
            "48:251-281,283-308", "49:250-308", "50:249-267,269-273,275-277,279-284,286-288,290-308", "51:248-307", "52:247-267,269-273,275-277,279-307", "53:247-255,257-267,269-307", "54:246-268,270-306", "55:246-306",
            "56:246-257,260-270,272-305", "57:246-305", "58:246-305", "59:246-305", "60:247-305", "61:247-306", "62:248-303", "63:250-297,300-302",
            "64:291-297,300-302", "65:291-297",
        };

        /// <summary>Roam spots and spawns stay this far clear of a zone's edge (the brain turns bots back at 450).</summary>
        const int NwBufferKeepClearRaw = 600;
        /// <summary>Gaps up to this many cells inside a row are part of the zone (walls / buildings inside the outline).</summary>
        const int SpawnZoneGapCells = 6;

        // tile maps, built once: zone sector (1 A, 2 B, 3 C, 0 none) and signed distance to the nearest zone edge (raw)
        static byte[] s_zoneOf;
        static short[] s_zoneDist;
        static int s_zoneW, s_zoneH;
        static readonly object s_zoneLock = new object();

        static void EnsureSpawnZones()
        {
            if (s_zoneDist != null)
                return;
            lock (s_zoneLock)
            {
                if (s_zoneDist != null)
                    return;
                int w = 1378, h = 897;
                byte[] zone = new byte[w * h];
                MarkZone(zone, w, h, SpawnZoneA, 1);
                MarkZone(zone, w, h, SpawnZoneB, 2);
                MarkZone(zone, w, h, SpawnZoneC, 3);

                // chamfer distance (3-4) to the nearest tile on the other side of the edge, both ways
                const int Inf = 30000;
                int[] dOut = new int[w * h], dIn = new int[w * h];
                for (int i = 0; i < zone.Length; i++) { dOut[i] = zone[i] != 0 ? 0 : Inf; dIn[i] = zone[i] != 0 ? Inf : 0; }
                Chamfer(dOut, w, h);
                Chamfer(dIn, w, h);
                short[] dist = new short[w * h];
                for (int i = 0; i < zone.Length; i++)
                {
                    int d = zone[i] != 0 ? -dIn[i] : dOut[i];   // chamfer units: 3 per tile
                    d = d * 16 / 3;                              // -> raw
                    dist[i] = (short)Math.Max(short.MinValue + 1, Math.Min(short.MaxValue, d));
                }
                s_zoneW = w; s_zoneH = h;
                s_zoneOf = zone;
                s_zoneDist = dist;
            }
        }

        static void MarkZone(byte[] zone, int w, int h, string[] rows, byte id)
        {
            foreach (string row in rows)
            {
                int colon = row.IndexOf(':');
                int cy = int.Parse(row.Substring(0, colon));
                int lastEnd = int.MinValue;
                foreach (string run in row.Substring(colon + 1).Split(','))
                {
                    string[] se = run.Split('-');
                    int a = int.Parse(se[0]), b = int.Parse(se[1]);
                    if (lastEnd != int.MinValue && a - lastEnd - 1 <= SpawnZoneGapCells)
                        a = lastEnd + 1;   // (a small gap = a wall or building inside the outline)
                    for (int cx = a; cx <= b; cx++)
                        for (int ty = cy * SpawnZoneCell; ty < cy * SpawnZoneCell + SpawnZoneCell && ty < h; ty++)
                            for (int tx = cx * SpawnZoneCell; tx < cx * SpawnZoneCell + SpawnZoneCell && tx < w; tx++)
                                zone[ty * w + tx] = id;
                    lastEnd = b;
                }
            }
        }

        static void Chamfer(int[] d, int w, int h)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, v = d[i];
                    if (x > 0) v = Math.Min(v, d[i - 1] + 3);
                    if (y > 0)
                    {
                        v = Math.Min(v, d[i - w] + 3);
                        if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
                        if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
                    }
                    d[i] = v;
                }
            for (int y = h - 1; y >= 0; y--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x, v = d[i];
                    if (x < w - 1) v = Math.Min(v, d[i + 1] + 3);
                    if (y < h - 1)
                    {
                        v = Math.Min(v, d[i + w] + 3);
                        if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
                        if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
                    }
                    d[i] = v;
                }
        }

        /// <summary>(2026-11-21, owner: "remove all bot friendly zones, they can shoot anywhere and go anywhere") false = the
        /// spawn zones only decide where players land from the dropship; bots roam, chase, fight and spawn in them too.</summary>
        public const bool BotNoGoZones = false;

        public static bool InNewPlayerBuffer(int x, int y)
        {
            return BotNoGoZones && BufferEdgeDistance(x, y) < 0;
        }

        /// <summary>What the bots' brain sees as the distance to a no-go zone (far away when there are none).</summary>
        public static double BotBufferEdgeDistance(int x, int y)
        {
            return BotNoGoZones ? BufferEdgeDistance(x, y) : 30000;
        }

        /// <summary>Distance (raw) from a point to the nearest spawn zone's edge (negative inside).</summary>
        public static double BufferEdgeDistance(int x, int y)
        {
            EnsureSpawnZones();
            int tx = x >> 4, ty = y >> 4;
            if (tx < 0 || ty < 0 || tx >= s_zoneW || ty >= s_zoneH)
                return 30000;   // (outside the play map)
            return s_zoneDist[ty * s_zoneW + tx];
        }

        /// <summary>Too close to a spawn zone for a bot to roam to or spawn at.</summary>
        public static bool NearNewPlayerBuffer(int x, int y)
        {
            return BotNoGoZones && BufferEdgeDistance(x, y) < NwBufferKeepClearRaw;
        }

        static int ZoneIdOfSector(string sector)
        {
            if (string.Equals(sector, "Sector A", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(sector, "Sector B", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(sector, "Sector C", StringComparison.OrdinalIgnoreCase)) return 3;
            return 0;
        }

        /// <summary>Does the dropship land players in this sector's spawn zone?</summary>
        public static bool DropshipLandsInZone(string sector)
        {
            foreach (string s in DropshipZoneSectors)
                if (string.Equals(s, sector, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// A random open spot for a player inside a sector's spawn zone (the dropship landing); null = the sector has
        /// no zone landing / no open spot found.
        /// </summary>
        public Helpers.ObjectState PickPlayerSpawnInZone(string sector)
        {
            if (_arena == null || !DropshipLandsInZone(sector))
                return null;
            int id = ZoneIdOfSector(sector);
            if (id == 0)
                return null;
            EnsureSpawnZones();
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            // the zone's tiles (cached per sector)
            List<int> tiles;
            if (!_zoneTiles.TryGetValue(id, out tiles))
            {
                tiles = new List<int>();
                for (int i = 0; i < s_zoneOf.Length; i++)
                    if (s_zoneOf[i] == id && s_zoneDist[i] <= -32)   // (at least 2 tiles in from the edge)
                        tiles.Add(i);
                _zoneTiles[id] = tiles;
            }
            if (tiles.Count == 0)
                return null;

            for (int tries = 0; tries < 80; tries++)
            {
                int t = tiles[_rand.Next(tiles.Count)];
                int tx = t % s_zoneW, ty = t / s_zoneW;
                short x = (short)(tx * 16 + 8), y = (short)(ty * 16 + 8);
                if (!TileOpenAround(x, y))
                    continue;
                if (_eol != null && !_eol.IsInAnyActiveSector(x, y))
                    continue;
                return new Helpers.ObjectState { positionX = x, positionY = y };
            }
            return null;
        }
        readonly Dictionary<int, List<int>> _zoneTiles = new Dictionary<int, List<int>>();

        /// <summary>The tile and its neighbours are clear (room to stand).</summary>
        bool TileOpenAround(short x, short y)
        {
            int maxX = _arena._levelWidth * 16 - 17, maxY = _arena._levelHeight * 16 - 17;
            for (int dy = -16; dy <= 16; dy += 16)
                for (int dx = -16; dx <= 16; dx += 16)
                {
                    int px = x + dx, py = y + dy;
                    if (px < 16 || py < 16 || px > maxX || py > maxY)
                        return false;
                    if (_arena.getTile(px, py).Blocked)
                        return false;
                }
            return true;
        }
    }
}
