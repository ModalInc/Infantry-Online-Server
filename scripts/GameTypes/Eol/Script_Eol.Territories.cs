using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Bot territories (2026-11-15, the zone owner's map "check.jpg"). NW (= Sector A, raw 0-8912 x 0-6304) first:
    ///   - HUMAN PATROLS: the rest of NW - human / pirate roamers and strike teams roam here;
    ///   - ALIEN ROAM: the west side (A2-B5), the top right (E1-F1) and the east edge (G3-H6) - aliens roam here;
    ///   - ELITE: the bottom-left corner (A6-B6) - the elite squads hold it (the two pirate bases rotate over all the
    ///     Sector A HQ anchors outside the spawn zone, see HqSlotAllowed);
    ///   - NEW PLAYER BUFFER: the circle around the NW dropship - no bot goes in (roaming, chasing or spawning).
    /// Rectangles were measured off the map image (NW = image px 35-695 x 37-525, so ~13.5 raw per px across and
    /// ~12.9 down). Other sectors have no territory yet - bots there roam as before. A bot that is inside NW keeps to
    /// its own territory there; one outside NW is not limited (it walks in and then keeps to it).
    /// </summary>
    public partial class Script_Eol
    {
        public enum BotTerritory { None, Human, Alien, Elite }

        struct TRect
        {
            public short MinX, MinY, MaxX, MaxY;
            public TRect(int minX, int minY, int maxX, int maxY) { MinX = (short)minX; MinY = (short)minY; MaxX = (short)maxX; MaxY = (short)maxY; }
            public bool Has(int x, int y) { return x >= MinX && x <= MaxX && y >= MinY && y <= MaxY; }
            public long Area { get { return (long)(MaxX - MinX) * (MaxY - MinY); } }
        }

        /// <summary>Old NW rectangle - only used until the sector map is built (then Sector A on the map is "NW").</summary>
        static readonly TRect NwSectorFallback = new TRect(1, 1, 8912, 6304);
        /// <summary>The boundaries object with the barrier-wall sector map (set in WireTerritoryHooks).</summary>
        static EolBoundaries s_sectorMap;

        static bool InNw(int x, int y)
        {
            EolBoundaries m = s_sectorMap;
            string s = m == null ? null : m.SectorAt(x, y);
            if (s != null)
                return s == m.sectorA;
            if (m != null && m.SectorAt(6305, 4277) != null)
                return false;   // (map built: outside the play map)
            return NwSectorFallback.Has(x, y);
        }

        /// <summary>Sector A's bounding box (the old rectangle until the map is built).</summary>
        static TRect NwBox()
        {
            short minX, maxX, minY, maxY;
            EolBoundaries m = s_sectorMap;
            if (m != null && m.TryGetSectorBounds(m.sectorA, out minX, out maxX, out minY, out maxY))
                return new TRect(minX, minY, maxX, maxY);
            return NwSectorFallback;
        }
        static readonly TRect[] NwAlien =
        {
            new TRect(1, 1460, 2228, 4173),      // west (A2-B4)
            new TRect(1, 4173, 2633, 5150),      // west, down to the base (A5-B5)
            new TRect(4860, 1, 7088, 814),       // top right (E1-F1)
            new TRect(7020, 1977, 8912, 6304),   // east edge (G3-H6)
        };
        static readonly TRect[] NwElite =
        {
            new TRect(1, 5150, 3100, 6304),      // bottom-left corner (A6-B6, out to the base pylon at 2736,5600)
        };
        // (the spawn zones - the no-bot buffers and the dropship landing areas - are in Script_Eol.SpawnZones.cs)

        /// <summary>The territory at a map point; None = no territory defined there (outside NW).</summary>
        public static BotTerritory TerritoryAt(int x, int y)
        {
            if (!InNw(x, y))
                return BotTerritory.None;
            foreach (TRect r in NwElite) if (r.Has(x, y)) return BotTerritory.Elite;
            foreach (TRect r in NwAlien) if (r.Has(x, y)) return BotTerritory.Alien;
            return BotTerritory.Human;
        }

        /// <summary>Which territory a bot belongs to.</summary>
        BotTerritory TerritoryOf(Bot b)
        {
            if (b == null || b._team == null)
                return BotTerritory.None;
            if (IsAssassinTeam(b._team))
                return BotTerritory.None;   // (bounty assassins go wherever their mark is)
            if (IsAlienBotTeam(b._team))
                return BotTerritory.Alien;
            // (2026-11-15) pirate base teams guard their own HQ wherever it stands (bases rotate over the Sector A
            // objectives) - no territory; the two elite roaming squads hold the base corner
            if (b._team == botTeam1 || b._team == botTeam2 || b._team == botTeam3)
                return BotTerritory.None;
            if (IsEliteSquadTeam(b._team))
                return BotTerritory.Elite;
            return BotTerritory.Human;
        }

        /// <summary>May this bot go to (x,y)? Never into the new player buffer; inside NW only its own territory.</summary>
        bool BotMayGo(Bot b, short x, short y)
        {
            if (NearNewPlayerBuffer(x, y))
                return false;
            if (b == null || b._state == null || !InNw(b._state.positionX, b._state.positionY))
                return true;   // (not in NW: no territory yet)
            BotTerritory mine = TerritoryOf(b), there = TerritoryAt(x, y);
            return mine == BotTerritory.None || there == BotTerritory.None || mine == there;
        }

        /// <summary>A random open spot in the bot's NW territory (outside the buffer); null when it has none.</summary>
        Helpers.ObjectState PickPointInTerritory(Bot b, Random r)
        {
            if (b == null || _arena == null || r == null)
                return null;
            TRect[] rects;
            switch (TerritoryOf(b))
            {
                case BotTerritory.Alien: rects = NwAlien; break;
                case BotTerritory.Elite: rects = NwElite; break;
                case BotTerritory.Human: rects = new[] { NwBox() }; break;
                default: return null;
            }
            long total = 0;
            foreach (TRect t in rects) total += Math.Max(1, t.Area);
            for (int tries = 0; tries < 40; tries++)
            {
                // a rectangle by its size, then a point in it
                long pick = (long)(r.NextDouble() * total);
                TRect rc = rects[0];
                foreach (TRect t in rects) { if (pick < Math.Max(1, t.Area)) { rc = t; break; } pick -= Math.Max(1, t.Area); }
                short x = (short)r.Next(Math.Max(64, (int)rc.MinX), Math.Max(65, (int)rc.MaxX));
                short y = (short)r.Next(Math.Max(64, (int)rc.MinY), Math.Max(65, (int)rc.MaxY));
                if (!BotMayGoInto(b, x, y))
                    continue;
                if (!InPlayArea(x, y, 0) || !EolHqPatrol.IsOpenForBot(b, x, y))
                    continue;
                return new Helpers.ObjectState { positionX = x, positionY = y };
            }
            return null;
        }

        /// <summary>BotMayGo for a point picked in the bot's own territory (the territory test without "is the bot in NW").</summary>
        bool BotMayGoInto(Bot b, short x, short y)
        {
            if (NearNewPlayerBuffer(x, y))
                return false;
            BotTerritory there = TerritoryAt(x, y);
            return there != BotTerritory.None && there == TerritoryOf(b);
        }

        /// <summary>HQ slots: any objective anchor away from the spawn zones (2026-11-15: Sector A holds two bases that
        /// rotate over its anchors - it used to allow only the bottom-left corner, so just one base ever went up).</summary>
        static bool HqSlotAllowed(short x, short y)
        {
            return !NearNewPlayerBuffer(x, y);
        }

        /// <summary>HQ anchor slots usable for a bot base in the open sectors (-1 = templates not built yet: no cap).</summary>
        int CountAllowedHqSlots()
        {
            if (_usedpylons == null || _usedpylons.Count == 0 || _eol == null)
                return -1;
            int n = 0;
            foreach (KeyValuePair<int, pylonObject> kv in _usedpylons)
            {
                if (kv.Value == null)
                    continue;
                short px = kv.Value.getX(), py = kv.Value.getY();
                if (_eol.IsInAnyActiveSector(px, py) && HqSlotAllowed(px, py))
                    n++;
            }
            return n;
        }

        /// <summary>Hooks for the bot brain (re-set every poll so they survive script reloads).</summary>
        void WireTerritoryHooks()
        {
            s_sectorMap = _eol;
            if (_territoryFn == null)
                _territoryFn = BotMayGo;
            if (_territoryPointFn == null)
                _territoryPointFn = PickPointInTerritory;
            EolBotBrain.Territory = _territoryFn;
            EolBotBrain.TerritoryPoint = _territoryPointFn;
            EolBotBrain.BufferEdge = _bufferEdgeFn;
        }
        Func<Bot, short, short, bool> _territoryFn;
        Func<Bot, Random, Helpers.ObjectState> _territoryPointFn;
        static readonly Func<short, short, double> _bufferEdgeFn = (x, y) => BufferEdgeDistance(x, y);
    }
}
