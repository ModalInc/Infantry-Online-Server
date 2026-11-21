using System;
using System.IO;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Everything the Eol scripts place on the map, per map.
    ///
    /// Three cases, picked at init from the level:
    ///   - Pioneer Station alone (the values the scripts always had);
    ///   - Chaos in Eol alone (a level whose name has "chaosineol" / "classic"): the Classic map, no toxic zones;
    ///   - COMBINED (2026-11-21, the team: "the classic map IN pioneer ... command to rotate all the flags/spawn points to that
    ///     map or back"): one level with Pioneer on top and the whole Classic map below it (tile row ClassicRowOffset,
    ///     tools\combine_classic.py). Either half is live - EolMap.ChaosInEol = "the Classic half is live" - and a mod command
    ///     or a player vote switches (Script_Eol.Theatre.cs). Every Classic position is the Chaos in Eol value moved down by
    ///     ClassicRowOffset tiles, so everything sits exactly where it was on Classic. Shared by both halves: Pioneer's bug cave
    ///     and Pioneer's bot dropship (bots land wherever the live half needs them). Each half has its own player dropship.
    ///
    /// Chaos in Eol positions: the dropship room is ChaosInEol's own (tiles 1249-1386 x 120-238); everything placed on Pioneer's
    /// play map was scaled onto ChaosInEol's main map (x 0.835) and moved to the nearest open ground.
    /// Everything is a property (not a static field) so it is read live - after Init and after every switch.
    /// </summary>
    public static class EolMap
    {
        /// <summary>The Classic half is live (alone: always; combined: after a switch to Classic).</summary>
        public static bool ChaosInEol { get; private set; }
        /// <summary>One level holding both maps.</summary>
        public static bool Combined { get; private set; }
        /// <summary>Tiles the Classic map sits below the top of the level (0 when it is a level of its own).</summary>
        public static int ClassicRowOffset { get { return Combined ? CombinedClassicRow : 0; } }
        /// <summary>combine_classic.py: Pioneer's 909 rows + a 40-row gap.</summary>
        public const int CombinedClassicRow = 949;
        static int Oy { get { return ClassicRowOffset; } }          // tiles
        static int OyPx { get { return ClassicRowOffset * 16; } }   // pixels

        static string _stateFile;

        public static void Init(string lvlFile, int levelHeightTiles)
        {
            string f = lvlFile ?? "";
            bool classicName = f.IndexOf("chaosineol", StringComparison.OrdinalIgnoreCase) >= 0 || f.IndexOf("classic", StringComparison.OrdinalIgnoreCase) >= 0;
            Combined = !classicName && levelHeightTiles >= CombinedClassicRow + 700;
            if (Combined)
            {
                _stateFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eol-live-map.txt");
                bool classic = false;
                try { classic = File.Exists(_stateFile) && File.ReadAllText(_stateFile).Trim().Equals("classic", StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { }
                ChaosInEol = classic;
            }
            else
                ChaosInEol = classicName;
            Log.write(TLog.Normal, "[Eol] Map profile: {0} (level {1}, {2} tiles high).",
                Combined ? "Pioneer + Classic combined, live half = " + LiveName : ChaosInEol ? "Chaos in Eol - whole map open, no toxic zones" : "Pioneer Station",
                lvlFile, levelHeightTiles);
        }

        public static string LiveName { get { return ChaosInEol ? "Classic" : "Pioneer"; } }

        /// <summary>Combined level only: make the other half live, remembered over restarts.</summary>
        public static void SetLive(bool classic)
        {
            if (!Combined)
                return;
            ChaosInEol = classic;
            try { File.WriteAllText(_stateFile, classic ? "classic" : "pioneer"); }
            catch (Exception e) { Log.write(TLog.Warning, "[Eol] Could not save the live map: {0}", e.Message); }
        }

        /// <summary>A pixel position on the live half (combined level; always true otherwise).</summary>
        public static bool OnLiveHalf(int y)
        {
            if (!Combined)
                return true;
            return ChaosInEol ? y >= OyPx : y < CombinedClassicRow * 16 - 40 * 16 / 2;
        }

        /// <summary>No toxic zones: every sector is always open, no daily radiation change, nobody sent back to the dropship.</summary>
        public static bool AllSectorsOpen { get { return ChaosInEol; } }
        /// <summary>Chaos in Eol alone only (the Alien King is veh 837 in ChaosInEol.veh, not in PioneerStation.veh; the welcome text).</summary>
        public static bool ChaosZoneOnly { get { return ChaosInEol && !Combined; } }

        /// <summary>Old fixed coordinate lists drawn for Pioneer (pylons): scaled onto ChaosInEol's main map when that is the level.</summary>
        public static short P(int v) { return ChaosZoneOnly ? (short)Math.Round(v * 0.835) : (short)v; }

        static short Cy(int pxY) { return (short)(pxY + OyPx); }    // a Classic pixel y on this level
        static short[][] Shift(short[][] tiles)
        {
            if (Oy == 0)
                return tiles;
            short[][] o = new short[tiles.Length][];
            for (int i = 0; i < tiles.Length; i++)
                o[i] = new short[] { tiles[i][0], (short)(tiles[i][1] + Oy) };
            return o;
        }

        // ---- the PLAYER dropship of the live half (recall / respawn, the safe room, the helmet trader)
        public static short RecallX { get { return ChaosInEol ? (short)21160 : (short)27008; } }
        public static short RecallY { get { return ChaosInEol ? Cy(3400) : (short)3360; } }
        public static short SafeMinX { get { return ChaosInEol ? (short)19984 : (short)26048; } }
        public static short SafeMaxX { get { return ChaosInEol ? (short)22191 : (short)28080; } }
        public static short SafeMinY { get { return ChaosInEol ? Cy(1920) : (short)2160; } }
        public static short SafeMaxY { get { return ChaosInEol ? Cy(3823) : (short)3824; } }
        public static short HelmetTraderX { get { return (short)((ChaosInEol ? 1301 : 1667) * 16 + 8); } }
        public static short HelmetTraderY { get { return (short)(((ChaosInEol ? 167 + Oy : 165)) * 16 + 8); } }

        // ---- the BOT dropship: combined = Pioneer's for both halves (bots land wherever the live half needs them)
        static bool BotShipClassic { get { return ChaosZoneOnly; } }
        /// <summary>The portal the bots walk to (80 x 80; bots appear 29 tiles south of it).</summary>
        public static short DropshipPortalX { get { return BotShipClassic ? (short)21160 : (short)27048; } }
        public static short DropshipPortalY { get { return BotShipClassic ? (short)2936 : (short)2904; } }
        public static short DeploySpawnX { get { return BotShipClassic ? (short)21160 : (short)27016; } }
        public static short DeploySpawnY { get { return BotShipClassic ? (short)3400 : (short)3368; } }
        public static short BotRecallX { get { return BotShipClassic ? (short)21160 : (short)27008; } }
        public static short BotRecallY { get { return BotShipClassic ? (short)3400 : (short)3360; } }
        public static short BotSafeMinX { get { return BotShipClassic ? (short)19984 : (short)26048; } }
        public static short BotSafeMaxX { get { return BotShipClassic ? (short)22191 : (short)28080; } }
        public static short BotSafeMinY { get { return BotShipClassic ? (short)1920 : (short)2160; } }
        public static short BotSafeMaxY { get { return BotShipClassic ? (short)3823 : (short)3824; } }

        // ---- the play map / sector map of the live half
        public static int PlayMapMinTileY { get { return ChaosInEol ? Oy : 0; } }
        public static int PlayMapMaxTileX { get { return ChaosInEol ? 1159 : 1377; } }
        public static int PlayMapMaxTileY { get { return ChaosInEol ? 748 + Oy : 896; } }
        /// <summary>A point known to be on the play map (tests whether the sector map is built).</summary>
        public static short MapProbeX { get { return ChaosInEol ? (short)5272 : (short)6305; } }
        public static short MapProbeY { get { return ChaosInEol ? Cy(3576) : (short)4277; } }

        // ---- the bug cave (combined: Pioneer's, shared by both halves) + the swarm
        static bool CaveClassic { get { return ChaosZoneOnly; } }
        public static short CaveTileX { get { return CaveClassic ? (short)1290 : (short)1639; } }
        public static short CaveTileY { get { return CaveClassic ? (short)320 : (short)418; } }
        public static short CavePortalTileX { get { return CaveClassic ? (short)1356 : (short)1705; } }
        public static short CavePortalTileY { get { return CaveClassic ? (short)318 : (short)416; } }
        static readonly short[][] SitesP = { new short[] { 76, 178 }, new short[] { 55, 419 }, new short[] { 342, 260 } };
        static readonly short[][] SitesC = { new short[] { 57, 146 }, new short[] { 46, 355 }, new short[] { 286, 213 } };
        public static short[][] SwarmSites { get { return ChaosInEol ? Shift(SitesC) : SitesP; } }
        static readonly short[][] ExtraP = { new short[] { 342, 260 }, new short[] { 448, 259 }, new short[] { 230, 246 }, new short[] { 629, 64 } };
        static readonly short[][] ExtraC = { new short[] { 286, 213 }, new short[] { 370, 216 }, new short[] { 195, 195 }, new short[] { 525, 76 } };
        public static short[][] SwarmExtraSpots { get { return ChaosInEol ? Shift(ExtraC) : ExtraP; } }
        static readonly short[][] A3P = { new short[] { 96, 175 }, new short[] { 54, 156 }, new short[] { 17, 131 }, new short[] { 33, 213 } };
        static readonly short[][] A3C = { new short[] { 82, 147 }, new short[] { 45, 130 }, new short[] { 14, 109 }, new short[] { 33, 181 } };
        public static short[][] SwarmA3Spots { get { return ChaosInEol ? Shift(A3C) : A3P; } }
        static readonly short[][] A6P = { new short[] { 21, 389 }, new short[] { 54, 375 }, new short[] { 52, 414 }, new short[] { 95, 416 } };
        static readonly short[][] A6C = { new short[] { 18, 325 }, new short[] { 45, 313 }, new short[] { 43, 343 }, new short[] { 81, 347 } };
        public static short[][] SwarmA6Spots { get { return ChaosInEol ? Shift(A6C) : A6P; } }
        /// <summary>The area round the A6 spots (tiles) that the swarm and the bases share: min x, max x, min y, max y.</summary>
        public static short SwarmA6MinTX { get { return ChaosInEol ? short.MaxValue : (short)0; } }
        public static short SwarmA6MaxTX { get { return ChaosInEol ? (short)99 : (short)119; } }
        public static short SwarmA6MinTY { get { return ChaosInEol ? (short)(296 + Oy) : (short)355; } }
        public static short SwarmA6MaxTY { get { return ChaosInEol ? (short)(367 + Oy) : (short)440; } }
        /// <summary>(2026-11-19, owner: "alien queen can now spawn randomly in these spots: B3, D4, E4, F4, A5, C9, J9, J8, K2, K5")
        /// Classic: one open outdoor spot (tiles) in each of those map squares; the queen comes up at one of them at random.
        /// Empty on Pioneer (its rotating A3 / A6 / extra sites stay).</summary>
        static readonly short[][] QueenC = { new short[] { 130, 209 } /*B3*/, new short[] { 280, 280 } /*D4*/, new short[] { 352, 288 } /*E4*/, new short[] { 440, 280 } /*F4*/, new short[] { 43, 365 } /*A5*/, new short[] { 200, 680 } /*C9*/, new short[] { 762, 682 } /*J9*/, new short[] { 763, 596 } /*J8*/, new short[] { 840, 120 } /*K2*/, new short[] { 840, 378 } /*K5*/ };
        public static short[][] SwarmQueenSpots { get { return ChaosInEol ? Shift(QueenC) : None; } }
        /// <summary>(2026-11-19, owner: "have the middle dropship portal spawn G5,G6,H5,H6,I5,I6,J5,J6 for human players only")
        /// Classic: players who walk into the dropship portal land at random in map squares G5-J6 (tiles x 480-799,
        /// y 320-479). Bots keep their own landings. False on Pioneer (sector / spawn-zone landing).</summary>
        public static bool PlayerLandingBox(out short minX, out short maxX, out short minY, out short maxY)
        {
            minX = 480 * 16; maxX = 800 * 16 - 1; minY = (short)((320 + Oy) * 16); maxY = (short)((480 + Oy) * 16 - 1);
            return ChaosInEol;
        }
        static readonly short[][] None = new short[0][];
        static readonly short[][] SwarmChokeP = { new short[] { 145, 410 } };
        public static short[][] SwarmChokeTiles { get { return ChaosInEol ? None : SwarmChokeP; } }

        // ---- other map spots
        static readonly short[][] BotChokeP = { new short[] { 146, 411 } };
        public static short[][] BotChokeTiles { get { return ChaosInEol ? None : BotChokeP; } }
        static readonly short[][] NoSpawnP = { new short[] { 361, 348 } };
        public static short[][] NoBotSpawnBuildingSeeds { get { return ChaosInEol ? None : NoSpawnP; } }
        static readonly short[][] VendorP = { new short[] { 88, 299 }, new short[] { 309, 80 }, new short[] { 159, 684 }, new short[] { 1250, 384 }, new short[] { 1231, 65 }, new short[] { 812, 93 }, new short[] { 660, 800 }, new short[] { 1125, 704 }, new short[] { 869, 432 } };
        static readonly short[][] VendorC = { new short[] { 71, 12 }, new short[] { 208, 669 }, new short[] { 317, 350 }, new short[] { 580, 538 }, new short[] { 635, 211 }, new short[] { 850, 719 }, new short[] { 859, 15 }, new short[] { 1111, 440 }, new short[] { 1145, 58 } };
        // (2026-11-19, owner: "have these randomly across the map where it makes sense") 9 spread-out outdoor spots on the
        // main walkable area, each 25-70 tiles from a flag (near the fighting, not in a base)
        public static short[][] VendorTiles { get { return ChaosInEol ? Shift(VendorC) : VendorP; } }
    }
}
