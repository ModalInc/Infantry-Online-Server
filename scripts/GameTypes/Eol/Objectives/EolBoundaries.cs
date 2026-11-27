using System;
using System.Linq;
using System.Collections.Generic;
using InfServer.Logic;
using InfServer.Game;
using InfServer.Scripting;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    public partial class EolBoundaries
    {
        ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        private Arena _arena;					//Pointer to our arena class
        private CfgInfo _config;                //The zone config
        public Script_Eol _baseScript;

        private int _tickSectorExpand;			//The tick at which we last checked for sector expansion
        public int _tickEolGameStart;				//The tick at which the game started (0 == stopped)
        private int _tickSectorScramble;        // The tick at which we check for scrambling of area
        /// <summary>Priority order A–C for this match; open count = Take(N). Shuffled once in gameStart when null. Poll/portal never reshuffle.</summary>
        private List<string> _matchSectorOrder;
        /// <summary>Suppress duplicate arena lines if layout logic re-runs with the same sectors.</summary>
        private string _lastSectorLayoutBroadcastKey;
        /// <summary>Sectors a moderator forced open until <see cref="_modForcedClosedSectors"/> or match reset removes them.</summary>
        readonly HashSet<string> _modForcedOpenSectors = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Sectors a moderator forced closed — excluded from layout even if population would open them.</summary>
        readonly HashSet<string> _modForcedClosedSectors = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>True after the match sector layout has been chosen for this run; cleared only on boundary end/reset. Stops duplicate Game.Start / Poll / portal from re-applying.</summary>
        private bool _sectorLayoutCommitted;
        public bool _bBoundariesDrawn;
        public bool _gameBegun;
        public bool bOneSector;
        public bool bTwoSector;
        public bool bAllSectors;
        public bool bbetweengames;
        private int _boundarynotify = 21;
        const int ClosedSectorSweepIntervalMs = 1500;
        const int ClosedSectorRecallCountdownMs = 3000;
        const short PioneerStationX = 27008;
        const short PioneerStationY = 3360;
        int _tickLastClosedSectorSweep;
        /// <summary>Player id → tick (Environment.TickCount) when to beam to Pioneer Station.</summary>
        readonly Dictionary<int, int> _dropshipRecallDueByPlayerId = new Dictionary<int, int>();
        /// <summary>
        /// Axis-aligned union of <c>PioneerRecallA</c>–<c>D</c> in <c>PioneerStation.lio</c> (dropship / neutral station).
        /// It is outside Eol radiation sectors A–C, so <see cref="IsInAnyActiveSector"/> is false there without this check.
        /// </summary>
        const short DropshipSafeMinX = 26048;
        const short DropshipSafeMaxX = 28080;
        const short DropshipSafeMinY = 2160;
        const short DropshipSafeMaxY = 3824;
        /// <summary>Padding beyond LIO recall rects so edge movement does not flicker OOB.</summary>
        const short DropshipSafePadding = 128;

        public short _top;
        public short _bottom;
        public short _left;
        public short _right;

        private List<Team> _activeTeams;
        public List<string> _activeSectors;
        /// <summary>True once sector layout is committed and at least one sector is open — used to arm flag / alien / roam side timers only after playfield is valid.</summary>
        public bool MatchSectorPlayReady { get { return _sectorLayoutCommitted && _activeSectors != null && _activeSectors.Count > 0; } }
        public string sectUnder30;
        public string sectUnder60;
        Random _rand;

        private int _minPlayers = 1;				//The minimum amount of players
        private int _gameCount = 0;

        public short _topLeftx;
        public short _topLefty;
        public short _bottomLeftx;
        public short _bottomLefty;
        public short _topRightx;
        public short _topRighty;
        public short _bottomRightx;
        public short _bottomRighty;

        public struct Point
        {
            public short x;
            public short y;
            public Point(short x, short y)
            {
                this.x = x;
                this.y = y;
            }
        }

        #region boundary points
        public string sectorA = "Sector A";
        public string sectorB = "Sector B";
        public string sectorC = "Sector C";
        public string sectorD = "Sector D";
        public string allSector = "All Sectors";

        // Tile borders (×16 = raw): vertical seam tile X=557 → 8912; horizontal seam tile Y=394 → 6304;
        // map extents tile (1377,896) → raw (22032,14336).
        Point aTL = new Point(1, 1); // Top left of Sector A
        Point aBL = new Point(1, 6304); // Bottom Left of Sector A
        Point aTR = new Point(8912, 1); // Top Right of Sector A
        Point aBR = new Point(8912, 6304); // Bottom Right of Sector A
        Point bTL = new Point(1, 6304);
        Point bBL = new Point(1, 14336);
        Point bTR = new Point(8912, 6304);
        Point bBR = new Point(8912, 14336);
        Point cTL = new Point(8912, 1);
        Point cBL = new Point(8912, 6304);
        Point cTR = new Point(22032, 1);
        Point cBR = new Point(22032, 6304);
        Point dTL = new Point(8912, 6304);
        Point dBL = new Point(8912, 14336);
        Point dTR = new Point(22032, 6304);
        Point dBR = new Point(22032, 14336);
        Point fTL = new Point(1, 1);
        Point fBL = new Point(1, 14336);
        Point fTR = new Point(22032, 1);
        Point fBR = new Point(22032, 14336);
        Point emptyp = new Point(0, 0);
        Point _tLeft;
        Point _tRight;
        Point _bLeft;
        Point _bRight;
        #endregion 

        public EolBoundaries(Arena arena, Script_Eol baseScript)
        {
            _arena = arena;
            _config = arena._server._zoneConfig;
            _baseScript = baseScript;
            _rand = new Random();
            _bBoundariesDrawn = false;
            _activeTeams = new List<Team>();
            _activeSectors = new List<string>();
            _tickSectorScramble = Environment.TickCount;
            _matchSectorOrder = null;
            _sectorLayoutCommitted = false;
            _gameBegun = false;
            bOneSector = false;
            bTwoSector = false;
            bAllSectors = false;
            bbetweengames = false;
            Sectors(emptyp, emptyp, emptyp, emptyp);
            EnsureSectorMap();   // (the barrier-wall sector map, built once up front)
        }

        /// <summary>
        /// Returns true if the given coordinate lies inside any currently active sector.
        /// Falls back to the current rectangle if no active sector list has been built yet.
        /// </summary>
        public bool IsInAnyActiveSector(short x, short y)
        {
            // If we don't have explicit active sectors, fall back to the current arena boundary rectangle
            if (_activeSectors == null || _activeSectors.Count == 0)
            {
                return x >= _left && x <= _right && y >= _top && y <= _bottom;
            }

            // the sector map (the zone owner's barrier walls); outside the play map = in no sector
            int here = SectorIndexAt(x, y);
            if (here != 0 || _sectorOfTile != null)
            {
                if (here == 0)
                    return false;
                foreach (string s in _activeSectors)
                    if (s != null && SectorIndexOfName(s) == here)
                        return true;
                return false;
            }

            foreach (string s in _activeSectors)
            {
                if (s == null)
                    continue;
                if (TryGetSectorRect(s, out short minX, out short maxX, out short minY, out short maxY))
                {
                    if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Toxic = inside a sector of the play map that is closed. Outside the map (Pioneer Station, the dropship) is
        /// never toxic.
        /// </summary>
        public bool IsInClosedSector(short x, short y)
        {
            if (_activeSectors == null || _activeSectors.Count == 0)
                return false;
            int here = SectorIndexAt(x, y);
            if (here == 0)
                return false;
            foreach (string s in _activeSectors)
                if (s != null && SectorIndexOfName(s) == here)
                    return false;
            return true;
        }

        /// <summary>
        /// True while the player is in the Pioneer Station / dropship area (neutral safe zone), per LIO recall layout.
        /// </summary>
        public bool IsInPioneerDropshipSafeZone(short x, short y)
        {
            int minX = DropshipSafeMinX - DropshipSafePadding;
            int maxX = DropshipSafeMaxX + DropshipSafePadding;
            int minY = DropshipSafeMinY - DropshipSafePadding;
            int maxY = DropshipSafeMaxY + DropshipSafePadding;
            return x >= minX && x <= maxX && y >= minY && y <= maxY;
        }

        /// <summary>
        /// Axis-aligned bounds for a sector label; matches <see cref="IsInAnyActiveSector"/> (portal / Main). Sector name match is ordinal case-insensitive.
        /// </summary>
        public bool TryGetSectorBounds(string sectorName, out short minX, out short maxX, out short minY, out short maxY)
        {
            return TryGetSectorRect(sectorName, out minX, out maxX, out minY, out maxY);
        }

        /// <summary>Bounds for A–C (the sector map's bounding box); name match is ordinal case-insensitive.</summary>
        private bool TryGetSectorRect(string sectorName, out short minX, out short maxX, out short minY, out short maxY)
        {
            if (TryGetSectorBox(sectorName, out minX, out maxX, out minY, out maxY))
                return true;
            minX = maxX = minY = maxY = 0;
            if (SectorIndexOfName(sectorName) == 0)
                return false;
            // (sector map not built: the old straight seams)
            if (string.Equals(sectorName, sectorA, StringComparison.OrdinalIgnoreCase))
            {
                minX = aTL.x; maxX = aTR.x; minY = aTL.y; maxY = aBL.y;
                return true;
            }
            if (string.Equals(sectorName, sectorB, StringComparison.OrdinalIgnoreCase))
            {
                minX = bTL.x; maxX = bTR.x; minY = bTL.y; maxY = bBL.y;
                return true;
            }
            if (string.Equals(sectorName, sectorC, StringComparison.OrdinalIgnoreCase))
            {
                minX = cTL.x; maxX = cTR.x; minY = cTL.y; maxY = cBL.y;
                return true;
            }
            return false;
        }

        /// <summary>Two sectors border each other (A, B and C all do on the three-sector map).</summary>
        private bool SectorsShareEdge(string sectorName1, string sectorName2)
        {
            int a = SectorIndexOfName(sectorName1), b = SectorIndexOfName(sectorName2);
            return a != 0 && b != 0 && a != b;
        }

        /// <summary>True when <paramref name="candidate"/> shares an edge with any sector in <paramref name="openSectors"/>.</summary>
        private bool IsSectorAdjacentToAnyOpen(string candidate, IEnumerable<string> openSectors)
        {
            if (openSectors == null)
                return false;
            foreach (string open in openSectors)
            {
                if (open != null && SectorsShareEdge(candidate, open))
                    return true;
            }
            return false;
        }

        private bool TryGetSectorKeyFromLetter(char letter, out string sectorName)
        {
            sectorName = null;
            switch (char.ToLowerInvariant(letter))
            {
                case 'a': sectorName = sectorA; return true;
                case 'b': sectorName = sectorB; return true;
                case 'c': sectorName = sectorC; return true;
                default: return false;
            }
        }

        /// <summary>Stable multiset key for comparing open sector sets regardless of list order.</summary>
        private static string CanonicalActiveSetKey(IReadOnlyList<string> sectors)
        {
            if (sectors == null || sectors.Count == 0)
                return "";
            string[] copy = new string[sectors.Count];
            for (int i = 0; i < sectors.Count; i++)
                copy[i] = sectors[i] ?? "";
            Array.Sort(copy, StringComparer.OrdinalIgnoreCase);
            return string.Join("|", copy);
        }

        /// <summary>Maps any case variant of A–C to the canonical public label string.</summary>
        private string NormalizeSectorNameToCanonical(string raw)
        {
            if (raw == null)
                return null;
            if (string.Equals(raw, sectorA, StringComparison.OrdinalIgnoreCase)) return sectorA;
            if (string.Equals(raw, sectorB, StringComparison.OrdinalIgnoreCase)) return sectorB;
            if (string.Equals(raw, sectorC, StringComparison.OrdinalIgnoreCase)) return sectorC;
            return raw;
        }

        private bool ActiveSectorsContainsName(string sectorName)
        {
            if (_activeSectors == null || sectorName == null)
                return false;
            foreach (string s in _activeSectors)
            {
                if (s != null && string.Equals(s, sectorName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the union of these sectors is exactly one axis-aligned rectangle (so one outer outline is correct).
        /// Diagonal pairs, all triples, etc. are false — draw per-sector outlines instead.
        /// </summary>
        private bool ActiveUnionIsSingleAxisRectangle(IReadOnlyList<string> sectors)
        {
            // (the sectors follow the barrier walls now - no union of them is a rectangle except one alone or all)
            return sectors == null || sectors.Count <= 1 || sectors.Count >= SectorCount;
        }

        /// <summary>Ensure <see cref="_activeSectors"/> is filled when the match is running (portal may run before Poll).</summary>
        public void EnsureActiveSectorsReadyForPortal()
        {
            if (_activeSectors == null || _activeSectors.Count == 0)
            {
                if (_gameBegun)
                    EnsureSectorLayoutCommittedOnce();
            }
        }

        /// <summary>
        /// Random active sector; center and a square span that covers the sector AABB (for legacy center+radius warps).
        /// Dropship portal uses <see cref="Script_Eol.findOpenWarpInAxisAlignedRect"/> on full bounds instead.
        /// </summary>
        public bool TryPickPortalSectorAnchor(Random rng, out short anchorX, out short anchorY, out int searchRadius)
        {
            anchorX = anchorY = 0;
            searchRadius = 0;
            if (_activeSectors == null || _activeSectors.Count == 0)
                return false;

            string sector = _activeSectors[rng.Next(_activeSectors.Count)];
            short minX;
            short maxX;
            short minY;
            short maxY;
            if (!TryGetSectorRect(sector, out minX, out maxX, out minY, out maxY))
                return false;

            anchorX = (short)((minX + maxX) / 2);
            anchorY = (short)((minY + maxY) / 2);
            int w = maxX - minX;
            int h = maxY - minY;
            // Square side >= max(w,h) so randomPositionInArea(center, radius) covers the whole sector.
            searchRadius = Math.Max(64, Math.Max(w, h) + 64);
            return true;
        }

        public bool Poll(int now)
        {
            if (_gameBegun && !_sectorLayoutCommitted)
                EnsureSectorLayoutCommittedOnce();

            if (_arena._bGameRunning && _sectorLayoutCommitted)
                PollClosedSectorDropshipRecall(now);

            return true;
        }

        /// <summary>
        /// Frequent soft scan for players who hopped into closed sectors (e.g. via buildings on seams).
        /// Schedules a 3s Pioneer recall; pending recalls are processed every poll.
        /// </summary>
        void PollClosedSectorDropshipRecall(int now)
        {
            ProcessPendingDropshipRecalls(now);

            if (_tickLastClosedSectorSweep == 0)
                _tickLastClosedSectorSweep = now;
            if (now - _tickLastClosedSectorSweep < ClosedSectorSweepIntervalMs)
                return;

            _tickLastClosedSectorSweep = now;
            // Soft schedule — gives a short warning instead of an instant yank every scan.
            SweepPlayersInClosedSectorsToDropship(now, immediate: false);
        }

        /// <summary>True if the coordinate is outside all open sectors and not in the dropship safe zone.</summary>
        bool PlayerPositionRequiresDropshipRecall(short x, short y)
        {
            if (IsInPioneerDropshipSafeZone(x, y))
                return false;
            if (_sectorOfTile != null || SectorIndexAt(x, y) != 0)
                return IsInClosedSector(x, y);   // (outside the play map is never toxic)
            return !IsInAnyActiveSector(x, y);
        }

        bool PlayerRequiresDropshipRecall(Player player)
        {
            if (player == null || player.IsSpectator)
                return false;
            return PlayerPositionRequiresDropshipRecall(player._state.positionX, player._state.positionY);
        }

        void ClearDropshipRecallState()
        {
            _tickLastClosedSectorSweep = 0;
            _dropshipRecallDueByPlayerId.Clear();
        }

        /// <summary>After sector layout shrinks, give affected players a 3s warning then recall.</summary>
        void ScheduleDropshipRecallForPlayersInClosedAreas(int now)
        {
            bool any = false;
            int scheduled = 0;
            foreach (Player player in _arena.PlayersIngame)
            {
                if (!PlayerRequiresDropshipRecall(player))
                    continue;

                int id = player._id;
                _dropshipRecallDueByPlayerId[id] = now + ClosedSectorRecallCountdownMs;
                player.sendMessage(-1, "&Radiation — returning to Pioneer Station in 3 seconds!");
                any = true;
                scheduled++;
            }

            if (any)
            {
                Log.write(TLog.Normal, "[Eol] Dropship recall scheduled: {0} players (countClosedKey={1})", scheduled, _dropshipRecallDueByPlayerId.Count);
                _arena.sendArenaMessage("&Radiation sectors closed — players in closed areas will be recalled to Pioneer Station in 3 seconds.", _config.flag.victoryWarningBong);
            }
        }

        /// <summary>Soft scan: schedule 3s Pioneer recall for OOB players (or immediate if requested).</summary>
        void SweepPlayersInClosedSectorsToDropship(int now, bool immediate)
        {
            foreach (Player player in _arena.PlayersIngame)
            {
                if (!PlayerRequiresDropshipRecall(player))
                    continue;

                int id = player._id;
                if (_dropshipRecallDueByPlayerId.ContainsKey(id))
                    continue;

                if (immediate)
                {
                    if (TryRecallPlayerToDropship(player))
                        _dropshipRecallDueByPlayerId.Remove(id);
                }
                else
                {
                    _dropshipRecallDueByPlayerId[id] = now + ClosedSectorRecallCountdownMs;
                    player.sendMessage(-1, "&Radiation — you left the open sectors. Returning to the dropship in 3 seconds!");
                }
            }
        }

        void ProcessPendingDropshipRecalls(int now)
        {
            if (_dropshipRecallDueByPlayerId.Count == 0)
                return;

            List<int> done = null;
            foreach (KeyValuePair<int, int> kv in _dropshipRecallDueByPlayerId)
            {
                Player player = FindIngamePlayerById(kv.Key);
                if (player == null || player.IsSpectator)
                {
                    if (done == null)
                        done = new List<int>();
                    done.Add(kv.Key);
                    continue;
                }

                if (!PlayerRequiresDropshipRecall(player))
                {
                    if (done == null)
                        done = new List<int>();
                    done.Add(kv.Key);
                    continue;
                }

                if (now < kv.Value)
                    continue;

                if (TryRecallPlayerToDropship(player))
                {
                    if (done == null)
                        done = new List<int>();
                    done.Add(kv.Key);
                }
            }

            if (done != null)
            {
                foreach (int id in done)
                    _dropshipRecallDueByPlayerId.Remove(id);
            }
        }

        Player FindIngamePlayerById(int playerId)
        {
            if (_arena.PlayersIngame == null)
                return null;
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p != null && p._id == playerId)
                    return p;
            }
            return null;
        }

        bool TryRecallPlayerToDropship(Player player)
        {
            return TryWarpPlayerToDropship(player, announce: true);
        }

        /// <summary>
        /// Warp a player to an open tile at Pioneer Station / dropship.
        /// Used for radiation recall and as a safety net after unspec.
        /// </summary>
        public bool TryWarpPlayerToDropship(Player player, bool announce = false)
        {
            if (player == null || _baseScript == null)
                return false;

            // (players reach the sectors through the dropship teleport, which lands them in the sector's spawn zone -
            // see Script_Eol.SpawnZones.cs and the portal)
            Helpers.ObjectState warpPoint = _baseScript.findOpenWarp(player, _arena, PioneerStationX, PioneerStationY, 300);
            if (warpPoint == null)
            {
                Log.write(TLog.Warning, "[Eol] Dropship warp: no open tile for {0}.", player._alias);
                if (announce)
                    player.sendMessage(-1, "Could not find a clear spot at the dropship — contact staff.");
                return false;
            }

            _baseScript.warp(player, warpPoint);
            if (announce)
                player.sendMessage(-1, "&You have been returned to Pioneer Station.");
            return true;
        }

        #region Sector workings
        /// <summary>
        /// Same basis as <see cref="Script_Eol.RebuildBotHqSlotTemplates"/> in Main (PlayersIngame). Arena.PlayerCount can include specs / not-yet-ingame
        /// and opens extra sectors vs pylons/ticker — portals then randomize among those and can drop you in a "secondary" sector.
        /// </summary>
        private int PlayingCountForSectorTiers()
        {
            if (_arena.PlayersIngame == null)
                return _arena.PlayerCount;
            return _arena.PlayersIngame.Count();
        }

        private static int DesiredSectorCountForPlayers(int playing)
        {
            if (EolMap.AllSectorsOpen)
                return SectorCount;   // (2026-11-27) open world: every sector, whatever the population
            if (playing < 30)
                return 1;
            if (playing < 46)
                return 2;
            return SectorCount;
        }

        /// <summary>
        /// The sector that always opens first (2026-11-15, the zone owner: "for now only sector A"); null = random,
        /// and the daily rotation picks another sector again.
        /// </summary>
        public static string PinnedFirstSector = "Sector A";

        /// <summary>Random shuffle once per fresh match (only from gameStart when order is null after reset).</summary>
        private void BeginNewMatchSectorOrder()
        {
            _matchSectorOrder = new List<string>(AllSectorNames);
            for (int i = _matchSectorOrder.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                string tmp = _matchSectorOrder[i];
                _matchSectorOrder[i] = _matchSectorOrder[j];
                _matchSectorOrder[j] = tmp;
            }
            PinFirstSector(_matchSectorOrder);
        }

        /// <summary>Move the pinned sector to the front of the open order (no-op when nothing is pinned).</summary>
        void PinFirstSector(List<string> order)
        {
            if (order == null || string.IsNullOrEmpty(PinnedFirstSector))
                return;
            int at = order.FindIndex(s => string.Equals(s, PinnedFirstSector, StringComparison.OrdinalIgnoreCase));
            if (at <= 0)
                return;
            string pinned = order[at];
            order.RemoveAt(at);
            order.Insert(0, pinned);
        }

        /// <summary>
        /// One-time pick + apply per match. Safe to call from gameStart, Poll, or portal; no-ops once committed unless _activeSectors was cleared.
        /// </summary>
        private void EnsureSectorLayoutCommittedOnce()
        {
            if (_sectorLayoutCommitted && _activeSectors != null && _activeSectors.Count > 0)
                return;

            if (!_sectorLayoutCommitted && _matchSectorOrder == null)
                BeginNewMatchSectorOrder();

            if (_matchSectorOrder == null || _matchSectorOrder.Count < SectorCount)
                return;

            ApplySectorsFromMatchOrder(DesiredSectorCountForPlayers(PlayingCountForSectorTiers()));
            _sectorLayoutCommitted = true;
            _bBoundariesDrawn = true;
        }

        /// <summary>
        /// Population prefix from <see cref="_matchSectorOrder"/> plus moderator forced opens minus forced closes, ordered by match shuffle.
        /// </summary>
        private List<string> BuildMergedChosenSectors(int desiredCount)
        {
            var auto = _matchSectorOrder.Take(desiredCount).ToList();
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string s in auto)
                set.Add(s);
            foreach (string s in _modForcedOpenSectors)
                set.Add(s);
            foreach (string s in _modForcedClosedSectors)
                set.Remove(s);
            if (set.Count == 0 && auto.Count > 0)
                set.Add(auto[0]);

            var ordered = new List<string>();
            foreach (string s in _matchSectorOrder)
            {
                if (set.Contains(s))
                    ordered.Add(s);
            }
            return ordered;
        }

        /// <summary>
        /// Applies an explicit open sector list (union rect, tickers, border vehicles).
        /// </summary>
        private void ApplyChosenSectorLayout(List<string> chosen, bool forceArenaAnnounce)
        {
            string broadcastKey = chosen.Count + "|" + string.Join(",", chosen);
            bool announce = forceArenaAnnounce || broadcastKey != _lastSectorLayoutBroadcastKey;

            List<string> previousActive = null;
            if (_activeSectors != null && _activeSectors.Count > 0)
                previousActive = new List<string>(_activeSectors);
            bool anySectorClosed = false;
            if (previousActive != null && CanonicalActiveSetKey(previousActive) != CanonicalActiveSetKey(chosen))
            {
                foreach (string s in previousActive)
                {
                    if (!chosen.Any(c => string.Equals(c, s, StringComparison.OrdinalIgnoreCase)))
                    {
                        anySectorClosed = true;
                        break;
                    }
                }
            }

            sectUnder30 = chosen.Count > 0 ? chosen[0] : "";
            sectUnder60 = chosen.Count > 1 ? chosen[1] : "";

            if (_activeSectors == null)
                _activeSectors = new List<string>();
            else
                _activeSectors.Clear();
            foreach (string s in chosen)
                _activeSectors.Add(NormalizeSectorNameToCanonical(s));

            short minX = short.MaxValue;
            short maxX = short.MinValue;
            short minY = short.MaxValue;
            short maxY = short.MinValue;

            foreach (string s in chosen)
            {
                if (!TryGetSectorRect(s, out short mix, out short maxx, out short miy, out short may))
                    continue;
                if (mix < minX) minX = mix;
                if (maxx > maxX) maxX = maxx;
                if (miy < minY) minY = miy;
                if (may > maxY) maxY = may;
            }

            if (chosen.Count >= SectorCount)
            {
                Sectors(fTL, fBL, fTR, fBR);
                if (announce)
                {
                    string open = EolMap.OpenWorld ? "The whole of Eol is open - pick your sector portal in the dropship"
                        : "All Sectors are currently open with low radiation levels";
                    _arena.sendArenaMessage(open, _config.flag.victoryWarningBong);
                    _arena.setTicker(1, 3, 0, EolMap.OpenWorld ? "" : open);   // (2026-11-27: open world - no sector line on the HUD)
                    _lastSectorLayoutBroadcastKey = broadcastKey;
                }
                bOneSector = false;
                bTwoSector = false;
                bAllSectors = true;
                _baseScript._minX = fTL.x;
                _baseScript._maxX = fTR.x;
                _baseScript._minY = fTL.y;
                _baseScript._maxY = fBL.y;
            }
            else
            {
                Point tl = new Point(minX, minY);
                Point bl = new Point(minX, maxY);
                Point tr = new Point(maxX, minY);
                Point br = new Point(maxX, maxY);

                Sectors(tl, bl, tr, br);

                string msg;
                if (chosen.Count == 1)
                {
                    msg = "Radiation warning! Only " + chosen[0] + " is open";
                    bOneSector = true;
                    bTwoSector = false;
                    bAllSectors = false;
                }
                else
                {
                    msg = "Radiation warning! Only " + string.Join(", ", chosen) + " are open";
                    bOneSector = false;
                    bTwoSector = (chosen.Count == 2);
                    bAllSectors = false;
                }

                if (announce)
                {
                    _arena.sendArenaMessage(msg, _config.flag.victoryWarningBong);
                    _arena.setTicker(1, 3, 0, msg);
                    _lastSectorLayoutBroadcastKey = broadcastKey;
                }

                _baseScript._minX = minX;
                _baseScript._maxX = maxX;
                _baseScript._minY = minY;
                _baseScript._maxY = maxY;
            }

            Log.write(TLog.Normal, "[Eol] Open sectors: {0} (sector map {1}).", string.Join(", ", chosen),
                EnsureSectorMap() ? "from the barrier walls" : "NOT BUILT - old straight seams");

            if (anySectorClosed && _arena._bGameRunning)
                ScheduleDropshipRecallForPlayersInClosedAreas(Environment.TickCount);

            _baseScript.OnEolActiveSectorsChanged();
        }

        /// <summary>
        /// Sets how many sectors are open using the fixed match order plus moderator overrides. No-op (no messages) if unchanged unless <paramref name="forceArenaAnnounce"/>.
        /// </summary>
        public void ApplySectorsFromMatchOrder(int desiredCount, bool forceArenaAnnounce = false)
        {
            if (_matchSectorOrder == null || _matchSectorOrder.Count < SectorCount)
                return;

            if (desiredCount < 1)
                desiredCount = 1;
            if (desiredCount > SectorCount)
                desiredCount = SectorCount;

            List<string> chosen = BuildMergedChosenSectors(desiredCount);
            if (!forceArenaAnnounce && _activeSectors != null
                && CanonicalActiveSetKey(_activeSectors) == CanonicalActiveSetKey(chosen))
                return;

            ApplyChosenSectorLayout(chosen, forceArenaAnnounce);
        }

        /// <summary>Moderator: open a sector by letter A–C. Must share an edge with a sector already open.</summary>
        /// <returns>null on success, otherwise an error message.</returns>
        public string TryModOpenSectorByLetter(char letter)
        {
            if (!TryGetSectorKeyFromLetter(letter, out string sectorName))
                return "Unknown sector key (use A, B or C).";
            if (!_sectorLayoutCommitted || _matchSectorOrder == null || _matchSectorOrder.Count < SectorCount)
                return "Sector layout not ready yet.";
            if (_activeSectors == null || _activeSectors.Count == 0)
                return "No sectors are open yet.";
            if (ActiveSectorsContainsName(sectorName))
                return null;
            if (!IsSectorAdjacentToAnyOpen(sectorName, _activeSectors))
                return "That sector is not adjacent to an open sector.";

            _modForcedOpenSectors.Add(sectorName);
            _modForcedClosedSectors.Remove(sectorName);
            ApplySectorsFromMatchOrder(DesiredSectorCountForPlayers(PlayingCountForSectorTiers()), true);
            return null;
        }

        /// <summary>Moderator: close a sector by letter A–C. Cannot remove the last open sector.</summary>
        /// <returns>null on success, otherwise an error message.</returns>
        public string TryModCloseSectorByLetter(char letter)
        {
            if (!TryGetSectorKeyFromLetter(letter, out string sectorName))
                return "Unknown sector key (use A, B or C).";
            if (!_sectorLayoutCommitted || _matchSectorOrder == null || _matchSectorOrder.Count < SectorCount)
                return "Sector layout not ready yet.";
            if (_activeSectors == null || !ActiveSectorsContainsName(sectorName))
                return "That sector is not open.";

            _modForcedClosedSectors.Add(sectorName);
            _modForcedOpenSectors.Remove(sectorName);
            int n = DesiredSectorCountForPlayers(PlayingCountForSectorTiers());
            List<string> preview = BuildMergedChosenSectors(n);
            if (preview.Count == 0)
            {
                _modForcedClosedSectors.Remove(sectorName);
                return "Cannot close the last sector.";
            }

            ApplySectorsFromMatchOrder(n, true);
            return null;
        }

        /// <summary>Recompute how many sectors are open from current population; same match order, no reshuffle. No-op if layout never committed.</summary>
        public void whichSector()
        {
            if (!_sectorLayoutCommitted || _matchSectorOrder == null || _matchSectorOrder.Count < SectorCount)
                return;
            ApplySectorsFromMatchOrder(DesiredSectorCountForPlayers(PlayingCountForSectorTiers()));
        }

        /// <summary>
        /// Reshuffle open sectors by population, preferring sectors that are not currently open
        /// so a rotation actually changes the playable map when alternatives exist.
        /// </summary>
        public void RotateMatchSectors()
        {
            int n = DesiredSectorCountForPlayers(PlayingCountForSectorTiers());
            if (n < 1)
                n = 1;
            if (n > SectorCount)
                n = SectorCount;

            var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_activeSectors != null)
            {
                foreach (string s in _activeSectors)
                {
                    if (!string.IsNullOrEmpty(s))
                        previous.Add(NormalizeSectorNameToCanonical(s));
                }
            }

            string[] all = AllSectorNames;
            var prefer = new List<string>(4);
            var fallback = new List<string>(4);
            foreach (string s in all)
            {
                string canon = NormalizeSectorNameToCanonical(s);
                if (previous.Contains(canon))
                    fallback.Add(canon);
                else
                    prefer.Add(canon);
            }

            ShuffleSectorList(prefer);
            ShuffleSectorList(fallback);

            // Prefer newly closed→open picks; only reuse a currently-open sector if we need more slots.
            var ordered = new List<string>(4);
            ordered.AddRange(prefer);
            ordered.AddRange(fallback);
            // If prefer was empty (all four already open), still reshuffle so Take(n) order can change.
            if (prefer.Count == 0)
                ShuffleSectorList(ordered);
            PinFirstSector(ordered);   // (the pinned sector stays open through the daily change)

            _matchSectorOrder = ordered;
            _sectorLayoutCommitted = true;
            _lastSectorLayoutBroadcastKey = null;
            ApplySectorsFromMatchOrder(n, true);
        }

        void ShuffleSectorList(List<string> list)
        {
            if (list == null || list.Count < 2)
                return;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                string tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        public void Sectors(Point topLeft, Point bottomLeft, Point topRight, Point bottomRight)
        {

            _top = topLeft.y;
            _bottom = bottomLeft.y;
            _left = topLeft.x;
            _right = topRight.x;

            _topLeftx = topLeft.x;
            _topLefty = topLeft.y;
            _topRightx = topRight.x;
            _topRighty = topRight.y;
            _bottomLeftx = bottomLeft.x;
            _bottomLefty = bottomLeft.y;
            _bottomRightx = bottomRight.x;
            _bottomRighty = bottomRight.y;
            RebuildBorderWallVehicles();
        }

        // (2026-11-15: the LIO seam doors along the old straight seams - and their red energy-door strip - were removed
        // from PioneerStation.lio. The zone owner's barrier walls separate the three sectors now; a closed sector is
        // kept clear by the radiation recall, see IsInClosedSector.)

        /// <summary>
        /// No-op. The barrier walls in the map separate the sectors; the hide-vehicle border ring is no longer spawned
        /// or cleared. Kept so existing callers compile.
        /// </summary>
        public void RebuildBorderWallVehicles()
        {
        }
        #endregion

        /// <summary>
        /// Called once from arena <see cref="Script_Eol.gameStart"/> when a match begins — not from Poll every tick.
        /// </summary>
        public void gameStart()
        {	//We've started!
            _tickEolGameStart = Environment.TickCount;
            _gameBegun = true;
            bbetweengames = false;
            ClearDropshipRecallState();
            _tickLastClosedSectorSweep = Environment.TickCount;
            EnsureSectorLayoutCommittedOnce();
            //_arena.flagSpawn();
        }

        public bool boundarygameEnd()
        {
            _tickEolGameStart = 0;
            _bBoundariesDrawn = false;
            _gameBegun = false;
            _sectorLayoutCommitted = false;
            _matchSectorOrder = null;
            _lastSectorLayoutBroadcastKey = null;
            _modForcedOpenSectors.Clear();
            _modForcedClosedSectors.Clear();
            if (_activeSectors != null)
                _activeSectors.Clear();
            Sectors(emptyp, emptyp, emptyp, emptyp);
            sectUnder30 = "";
            sectUnder60 = "";
            _arena.setTicker(1, 3, 0, "No Sectors currently open");
            bbetweengames = true;
            ClearDropshipRecallState();
            //_arena.flagReset();
            return true;
        }

        public bool boundarygameReset()
        {	//Game reset, perhaps start a new one
            _tickEolGameStart = 0;
            _bBoundariesDrawn = false;
            _gameBegun = false;
            _sectorLayoutCommitted = false;
            _matchSectorOrder = null;
            _lastSectorLayoutBroadcastKey = null;
            _modForcedOpenSectors.Clear();
            _modForcedClosedSectors.Clear();
            if (_activeSectors != null)
                _activeSectors.Clear();
            Sectors(emptyp, emptyp, emptyp, emptyp);
            sectUnder30 = "";
            sectUnder60 = "";
            _arena.setTicker(1, 3, 0, "No Sectors currently open");
            bbetweengames = true;
            ClearDropshipRecallState();
            //_arena.flagReset();
            return true;
        }
    }
}
