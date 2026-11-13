// CTF:X server-side bots - the DROPSHIP (Travis 10-12: "bots have to run out into the spawns in the dropship and the warp handles
// their spawn location ... to see how they navigate to their objectives from a more neutral standpoint").
//
// dropship=1: every body of a bot (the first spawn and each respawn) starts where a player's does in the CTF:X event, beside one of
// the dropship's MapEnter portals (CTFXEvent.txt portalspawn=). It gears up for a moment (as a player at the store), walks into the
// nearest MapEnter portal, and the portal sends it where it sends a player: a landing patch in one of the spawn areas the head
// count opens (CTFXEvent.txt mapspawn= / tier=, the rules of the CTF script's WarpToCTFXMapSpawn). From there it walks to its own
// objective. While in the dropship it neither shoots nor is shot at by bots.
// dropshipHeads=all counts the bots as heads for the tiers (more areas open: more fighting outside, Travis: "more chaos play
// outside"); players = only in-game humans, exactly as a player's own portal use counts them.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXDropship
    {
        readonly CTFXBotManager _m;
        internal bool On;
        internal bool HeadsAll = true;
        int _areaMax;                                                            // mapspawnmax: skip a landing area holding this many (0 = off)
        readonly List<int[]> _side = new List<int[]>();                          // portalspawn rects (tiles x1 x2 y1 y2)
        readonly Dictionary<char, List<int[]>> _land = new Dictionary<char, List<int[]>>();
        readonly List<KeyValuePair<int, string>> _tiers = new List<KeyValuePair<int, string>>();
        readonly List<int[]> _portals = new List<int[]>();                       // MapEnter portal rects, px: x0 y0 x1 y1
        readonly List<string> _portalNames = new List<string>();
        // stats
        internal int Spawns, Warps, Stuck, BrownLandings;
        internal long DwellMs;
        readonly Dictionary<char, int> _byArea = new Dictionary<char, int>();

        internal CTFXDropship(CTFXBotManager m) { _m = m; }

        internal bool Ready { get { return On && _side.Count > 0 && _portals.Count > 0 && _land.Count > 0; } }

        /// <summary>Read CTFXEvent.txt and find the MapEnter portals. Called at each start.</summary>
        internal void Load()
        {
            _side.Clear(); _land.Clear(); _tiers.Clear(); _portals.Clear(); _portalNames.Clear(); _byArea.Clear(); _areaMax = 0;
            Spawns = 0; Warps = 0; Stuck = 0; DwellMs = 0;
            _outNodes.Clear();
            string path = Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("CTFX", "CTFXEvent.txt"))));
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw;
                    int cut = line.IndexOf("//", StringComparison.Ordinal);
                    if (cut >= 0) line = line.Substring(0, cut);
                    line = line.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant(), val = line.Substring(eq + 1).Trim();
                    if (key == "portalspawn") { int[] r = Rect(val); if (r != null) _side.Add(r); }
                    else if (key == "portalring") { _ring.Clear(); foreach (string s in val.Split(',')) { int v; if (int.TryParse(s.Trim(), out v) && v > 0) _ring.Add(v); } }
                    else if (key == "portalclear") int.TryParse(val, out _ringClear);
                    else if (key == "portaledge") int.TryParse(val, out _ringEdge);
                    else if (key == "mapspawn" && val.Length > 2 && val[1] == ',')
                    {
                        int[] r = Rect(val.Substring(2));
                        if (r == null) continue;
                        char a = char.ToUpperInvariant(val[0]);
                        List<int[]> l;
                        if (!_land.TryGetValue(a, out l)) { l = new List<int[]>(); _land[a] = l; }
                        l.Add(r);
                    }
                    else if (key == "mapspawnmax") int.TryParse(val, out _areaMax);
                    else if (key == "tier")
                    {
                        int colon = val.IndexOf(':'), max;
                        if (colon > 0 && int.TryParse(val.Substring(0, colon).Trim(), out max))
                            _tiers.Add(new KeyValuePair<int, string>(max, val.Substring(colon + 1).Replace(",", "").Replace(" ", "").ToUpperInvariant()));
                    }
                }
                _tiers.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b) { return a.Key.CompareTo(b.Key); });
            }
            catch (Exception ex) { _m.Log("[CTFXBots] dropship: could not read " + path + ": " + ex.Message); }
            BuildOutside();
            if (!On) { _m.Log("[CTFXBots] dropship: off; outside areas " + OutsideText()); return; }
            FindPortals();
            BuildRing();
            StringBuilder sb = new StringBuilder("[CTFXBots] dropship: ");
            sb.Append(_side.Count).Append(" portal-side spawn spots, ").Append(_land.Count).Append(" landing areas, ").Append(_tiers.Count).Append(" tiers, heads ")
              .Append(HeadsAll ? "players + bots" : "players").Append(", portals");
            for (int i = 0; i < _portals.Count; i++)
                sb.Append(' ').Append(_portalNames[i]).Append(' ').Append(_portals[i][0] >> 4).Append(',').Append(_portals[i][1] >> 4)
                  .Append('-').Append(_portals[i][2] >> 4).Append(',').Append(_portals[i][3] >> 4);
            if (!Ready) sb.Append(" | NOT READY: bots spawn in their bases");
            sb.Append(" | outside areas ").Append(OutsideText());
            _m.Log(sb.ToString());
        }

        // portalring= (CTFXEvent.txt): the portal-side spawn spots are made here from the live map instead of the portalspawn= list.
        // Travis 10-20: players dropship-recalled and ran straight into a random MapEnter pad - the old ring (152-232 px from a pad's
        // centre, ~100 px from its edge) was too close, and a spot on or by a wall lets the client push the body around.
        readonly List<int> _ring = new List<int>();
        int _ringClear = 1, _ringEdge = 140;
        internal int RingSpots;                                                    // spots the ring made (0 = the portalspawn= list is used)

        /// <summary>The ring: 16 directions x portalring radii (px from each MapEnter pad's centre). A 2x2-tile spot is kept when every tile
        /// within portalclear tiles of it is open floor, it is portaledge px or more from EVERY pad's edge, and a body can walk straight to its
        /// pad. Fewer than 12 = keep the portalspawn= list.</summary>
        void BuildRing()
        {
            RingSpots = 0;
            if (_ring.Count == 0 || _portals.Count == 0) return;
            List<int[]> spots = new List<int[]>();
            HashSet<long> seen = new HashSet<long>();
            foreach (int[] p in _portals)
            {
                int cx = (p[0] + p[2]) / 2, cy = (p[1] + p[3]) / 2;
                foreach (int r in _ring)
                    for (int k = 0; k < 16; k++)
                    {
                        double a = k * Math.PI / 8.0;
                        int px = cx + (int)Math.Round(Math.Cos(a) * r), py = cy + (int)Math.Round(Math.Sin(a) * r);
                        int tx = (px >> 4) - 1, ty = (py >> 4) - 1;                // the 2x2 spot around that point
                        if (!seen.Add(((long)tx << 20) | (uint)ty)) continue;
                        if (!RingSpotOk(tx, ty, cx, cy)) continue;
                        spots.Add(new int[] { tx, tx + 1, ty, ty + 1 });
                    }
            }
            if (spots.Count < 12) { _m.Log("[CTFXBots] dropship ring: only " + spots.Count + " clear spots, the portalspawn list stays"); return; }
            _side.Clear();
            _side.AddRange(spots);
            RingSpots = spots.Count;
            _m.Log("[CTFXBots] dropship ring: " + spots.Count + " spawn spots at " + string.Join("/", _ring) + " px from the pads, "
                + _ringClear + " tile(s) of open floor round each, " + _ringEdge + "+ px from any pad");
        }

        bool RingSpotOk(int tx, int ty, int pcx, int pcy)
        {
            for (int y = ty - _ringClear; y <= ty + 1 + _ringClear; y++)
                for (int x = tx - _ringClear; x <= tx + 1 + _ringClear; x++)
                    if (_m.BlockedPx(x * 16 + 8, y * 16 + 8)) return false;
            int sx = tx * 16 + 16, sy = ty * 16 + 16;                                  // the spot's centre
            foreach (int[] q in _portals)
            {
                int dx = Math.Max(Math.Max(q[0] - sx, 0), sx - q[2]), dy = Math.Max(Math.Max(q[1] - sy, 0), sy - q[3]);
                if (dx * dx + dy * dy < _ringEdge * _ringEdge) return false;
            }
            double ex = pcx - sx, ey = pcy - sy, len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1.0) return false;
            int wx = pcx - (int)(ex / len * 64.0), wy = pcy - (int)(ey / len * 64.0);    // up to the pad's near side
            return _m.WalkLine(sx, sy, wx, wy);
        }

        /// <summary>The spawn spots humans use too (CTF.cs WarpToCTFXSpawn): the ring when it was made, else the portalspawn= list.</summary>
        internal List<int[]> SideSpots { get { return _side; } }

        static int[] Rect(string csv)
        {
            string[] v = csv.Split(',');
            int x1, x2, y1, y2;
            if (v.Length == 4 && int.TryParse(v[0].Trim(), out x1) && int.TryParse(v[1].Trim(), out x2)
                && int.TryParse(v[2].Trim(), out y1) && int.TryParse(v[3].Trim(), out y2))
                return new int[] { x1, x2, y1, y2 };
            return null;
        }

        /// <summary>The level's MapEnter portals (LIO rect: OffsetX / 16 - level offset = tile, as the doors). The LIO table is read
        /// through the asset manager's own id map: asking getLioByID id by id logs a warning for every missing id (10-12: 38,000).</summary>
        void FindPortals()
        {
            int lox = 0, loy = 0;
            try { lox = _m.Arena._server._assets.Level.OffsetX; loy = _m.Arena._server._assets.Level.OffsetY; } catch (Exception) { }
            System.Collections.IDictionary lios = null;
            try
            {
                FieldInfo f = _m.Arena._server._assets.GetType().GetField("_idToLio", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) lios = f.GetValue(_m.Arena._server._assets) as System.Collections.IDictionary;
            }
            catch (Exception) { }
            if (lios == null) { _m.Log("[CTFXBots] dropship: the LIO table is not readable here, no portals"); return; }
            foreach (object o in lios.Values)
            {
                LioInfo.Portal p = o as LioInfo.Portal;
                if (p == null || p.GeneralData.Name == null || p.GeneralData.Name.IndexOf("MapEnter", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int tx = p.GeneralData.OffsetX / 16 - lox, ty = p.GeneralData.OffsetY / 16 - loy;
                int w = Math.Max(16, (int)p.GeneralData.Width), h = Math.Max(16, (int)p.GeneralData.Height);
                _portals.Add(new int[] { tx * 16, ty * 16, tx * 16 + w, ty * 16 + h });
                _portalNames.Add(p.GeneralData.Name.Trim());
            }
        }

        // ================================================================ outside: the neutral landing areas (A-H)
        // Route nodes within 45 tiles of each landing area's centre (the strips and caves on the mountain sides between the buildings):
        // where the outside brawlers fight (CTFXCommand's outside activity, home=outside groups).
        readonly Dictionary<char, List<int>> _outNodes = new Dictionary<char, List<int>>();
        readonly Dictionary<char, int[]> _outCentre = new Dictionary<char, int[]>();

        internal bool HasOutside { get { return _outNodes.Count > 0; } }

        void BuildOutside()
        {
            _outCentre.Clear();
            CTFXRouteBook rb = _m.Routes;
            if (rb == null) return;
            foreach (KeyValuePair<char, List<int[]>> kv in _land)
            {
                long sx = 0, sy = 0;
                foreach (int[] r in kv.Value) { sx += (r[0] + r[1]) * 8 + 8; sy += (r[2] + r[3]) * 8 + 8; }
                int cx = (int)(sx / kv.Value.Count), cy = (int)(sy / kv.Value.Count);
                List<int> l = new List<int>();
                for (int n = 0; n < rb.N; n++)
                {
                    int dx = rb.X[n] - cx, dy = rb.Y[n] - cy;
                    if (dx * dx + dy * dy <= 720 * 720) l.Add(n);
                }
                if (l.Count == 0) continue;
                _outNodes[kv.Key] = l;
                _outCentre[kv.Key] = new int[] { cx, cy };
            }
        }

        /// <summary>The landing area (A-H) a point is in (within 45 tiles of its centre), ' ' = none.</summary>
        internal char OutsideArea(int x, int y)
        {
            foreach (KeyValuePair<char, int[]> kv in _outCentre)
            {
                int dx = kv.Value[0] - x, dy = kv.Value[1] - y;
                if (dx * dx + dy * dy <= 720 * 720) return kv.Key;
            }
            return ' ';
        }

        string OutsideText()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<char, List<int>> kv in _outNodes) sb.Append(sb.Length == 0 ? "" : " ").Append(kv.Key).Append(':').Append(kv.Value.Count);
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        /// <summary>An outside goal: a landing area (nearer likelier, one teammates already crowd less so), a random node of it.</summary>
        internal bool OutsideGoal(CTFXBot b, double herdK, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (_outNodes.Count == 0) return false;
            char best = ' ';
            double sum = 0;
            List<KeyValuePair<char, double>> w = new List<KeyValuePair<char, double>>();
            foreach (KeyValuePair<char, int[]> kv in _outCentre)
            {
                double dx = kv.Value[0] - b._state.positionX, dy = kv.Value[1] - b._state.positionY;
                int mates = 0;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o == b || o.IsDead || o.Released || o._team != b._team) continue;
                    double ex = o._state.positionX - kv.Value[0], ey = o._state.positionY - kv.Value[1];
                    if (ex * ex + ey * ey <= 640.0 * 640.0) mates++;
                }
                double x = 1.0 / (1.0 + Math.Sqrt(dx * dx + dy * dy) / 2000.0) / (1.0 + herdK * mates);
                w.Add(new KeyValuePair<char, double>(kv.Key, x)); sum += x;
            }
            double u = _m.Rng.NextDouble() * sum;
            foreach (KeyValuePair<char, double> kv in w) { u -= kv.Value; best = kv.Key; if (u <= 0) break; }
            List<int> l = _outNodes[best];
            int n = l[_m.Rng.Next(l.Count)];
            tx = _m.Routes.X[n] >> 4; ty = _m.Routes.Y[n] >> 4;
            return true;
        }

        // ================================================================ spawn
        /// <summary>A spawn tile beside a random MapEnter portal (a player's CTF:X spawn). False = not set up.</summary>
        internal bool SpawnTile(out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!Ready) return false;
            for (int i = 0; i < 12; i++)
            {
                int[] r = _side[_m.Rng.Next(_side.Count)];
                int x = r[0] + _m.Rng.Next(r[1] - r[0] + 1), y = r[2] + _m.Rng.Next(r[3] - r[2] + 1);
                if (_m.BodyFits(x * 16 + 8, y * 16 + 8)) { tx = x; ty = y; Spawns++; return true; }
            }
            return false;
        }

        // ================================================================ the walk to the portal
        /// <summary>The nearest MapEnter portal's centre (px).</summary>
        internal void NearestPortal(int x, int y, out int px, out int py)
        {
            px = x; py = y;
            double bd = double.MaxValue;
            foreach (int[] p in _portals)
            {
                int cx = (p[0] + p[2]) / 2, cy = (p[1] + p[3]) / 2;
                double dx = cx - x, dy = cy - y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; px = cx; py = cy; }
            }
        }

        /// <summary>Is (x, y) inside a MapEnter portal (8 px of slack: the body's edge counts, as a player's does)?</summary>
        internal bool InPortal(int x, int y)
        {
            foreach (int[] p in _portals)
                if (x >= p[0] - 8 && x <= p[2] + 8 && y >= p[1] - 8 && y <= p[3] + 8) return true;
            return false;
        }

        /// <summary>The portal's warp: a landing patch in a spawn area the head count opens. The bot leaves the dropship there.</summary>
        internal bool Warp(CTFXBot b, int now)
        {
            if (!Ready) return false;
            int heads = 0;
            foreach (Player p in _m.Arena.PlayersIngame) heads++;
            if (HeadsAll) foreach (CTFXBot o in _m.LiveBots) if (!o.IsDead && !o.Released) heads++;
            string letters = _tiers.Count > 0 ? _tiers[_tiers.Count - 1].Value : "";
            foreach (KeyValuePair<int, string> t in _tiers) if (heads <= t.Key) { letters = t.Value; break; }
            // brown-bound bots (the brown teams, and raiders headed for the focus base) mostly take the strips beside the brown
            // building, F (west) and C (east): Travis 10-13 wants top brown the biggest hotspot at all times, and a random landing on
            // the far side of the map kept a third of them walking back
            bool brownBound = b.Style == CTFXStyle.Maser || b.Group != null && (b.Group.Quad == "brown"
                || (b.Slot != null && b.Slot.Intent != null && b.Slot.Intent.Act == CTFXAct.Raid && b.Slot.Intent.RaidEnd == _m.FocusEnd));
            // Travis 10-14: "an unrealistically large amount of people fighting in the outdoors ... the spawns aren't random enough": 75 %
            // on C/F made a standing brawl beside brown. Now 20 %; the rest land where a player would and warp on (RejoinStep)
            if (brownBound && _m.Rng.NextDouble() < 0.0)
            {
                string near = "";
                foreach (char c in "CF") if (letters.IndexOf(c) >= 0 || _land.ContainsKey(c)) near += c;
                if (near.Length > 0) { letters = near; BrownLandings++; }
            }
            List<KeyValuePair<char, int[]>> pool = new List<KeyValuePair<char, int[]>>();
            foreach (char a in letters)
            {
                List<int[]> l;
                if (_land.TryGetValue(a, out l)) foreach (int[] r in l) pool.Add(new KeyValuePair<char, int[]>(a, r));
            }
            if (pool.Count == 0) foreach (KeyValuePair<char, List<int[]>> kv in _land) foreach (int[] r in kv.Value) pool.Add(new KeyValuePair<char, int[]>(kv.Key, r));
            if (pool.Count == 0) return false;
            if (_areaMax > 0 && pool.Count > 1)
            {   // as the original warp group: an area already holding mapspawnmax heads (players, and bots when they count) is skipped
                List<KeyValuePair<char, int[]>> open = pool.FindAll(delegate(KeyValuePair<char, int[]> q) { return HeadsIn(q.Value) < _areaMax; });
                if (open.Count > 0) pool = open; else Full++;
            }
            KeyValuePair<char, int[]> pick = pool[_m.Rng.Next(pool.Count)];
            int[] rc = pick.Value;
            int lx = rc[0] * 16 + 8, ly = rc[2] * 16 + 8;
            for (int i = 0; i < 40; i++)
            {
                int x = (rc[0] + _m.Rng.Next(rc[1] - rc[0] + 1)) * 16 + 8, y = (rc[2] + _m.Rng.Next(rc[3] - rc[2] + 1)) * 16 + 8;
                if (_m.BodyFits(x, y)) { lx = x; ly = y; break; }
            }
            b.Teleported(lx, ly, now);
            Warps++;
            int n;
            _byArea.TryGetValue(pick.Key, out n);
            _byArea[pick.Key] = n + 1;
            return true;
        }

        internal int Full;                                                      // warps where every open area was full

        int HeadsIn(int[] r)
        {
            int x0 = r[0] * 16, x1 = r[1] * 16 + 15, y0 = r[2] * 16, y1 = r[3] * 16 + 15, n = 0;
            foreach (Player p in _m.Arena.PlayersIngame)
                if (p._state.positionX >= x0 && p._state.positionX <= x1 && p._state.positionY >= y0 && p._state.positionY <= y1) n++;
            if (HeadsAll)
                foreach (CTFXBot o in _m.LiveBots)
                    if (!o.IsDead && !o.Released && o._state.positionX >= x0 && o._state.positionX <= x1 && o._state.positionY >= y0 && o._state.positionY <= y1) n++;
            return n;
        }

        internal string Summary()
        {
            if (!On) return "";
            StringBuilder sb = new StringBuilder(" | dropship spawns ").Append(Spawns).Append(", portal warps ").Append(Warps);
            if (_byArea.Count > 0)
            {
                sb.Append(" (");
                bool f = true;
                foreach (KeyValuePair<char, int> kv in _byArea) { sb.Append(f ? "" : " ").Append(kv.Key).Append(' ').Append(kv.Value); f = false; }
                sb.Append(')');
            }
            sb.Append(", avg in ship ").Append(Warps > 0 ? (DwellMs / Warps / 100 / 10.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s" : "-");
            if (Stuck > 0) sb.Append(", stuck ").Append(Stuck);
            sb.Append(", brown-bound landings beside brown ").Append(BrownLandings);
            return sb.ToString();
        }
    }
}
/* PARSER GUARD - keep last */
