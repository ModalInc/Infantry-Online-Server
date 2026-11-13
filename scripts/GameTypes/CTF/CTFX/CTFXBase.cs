// CTF:X server-side bots - BASE SHAPES (Travis 10-16 morning: "the chokes used to be heavily contested and it used to be a longer
// war of attrition of gaining ground, and most of the fighting did NOT occur in those hallways"; "there is also the back corridor";
// attach corners "more cornered and behind large objects to be protected by physics"). CTFXBases.txt gives each brown end its room
// row, the three lanes out of the hall into its front corridor, its doors in the order an attack takes them, its side doors and its
// base-runner attach corners.
//  - GROUND: every 2 s, per base and per team with bots near it, how many of its doors in order that team holds: the furthest door
//    with one of its bots within 180 px and no more enemies than its own there (humans count). A door won stays won while nobody
//    retakes it (20 s without a bot of that team past it, or enemies outnumbering it there, and it is lost again).
//  - ATTACK: a raider, or a flag runner for a flag in that base, first comes down its own lane into the front corridor (lanes are
//    shared out per bot and re-dealt every 2 minutes, a crowded one skipped), then fights for the next door its team has not taken,
//    from the attackers' side of it; with every door taken, the flag.
//  - LINES: the defenders' three lines are the base's own: the lane mouths in the front corridor (outer, each watching up its lane),
//    the entrance and the second door (mid), the deeper doors and the side doors (inner). CTFXLines falls back and pushes up as before.
//  - RUNNER: an Infiltrator's safe spot for a flag in the base is an attach corner, the one with the fewest enemies near it.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.IO;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXBaseDef
    {
        public string End;
        public int RX0, RY0, RX1, RY1;
        public readonly List<string> LaneNames = new List<string>();
        public readonly List<int[]> Lanes = new List<int[]>();            // hall end x, y, corridor mouth x, y (tiles)
        public readonly List<string> ChokeNames = new List<string>();
        public readonly List<int[]> Chokes = new List<int[]>();           // door x, y, attackers' side x, y (tiles), in attack order
        public readonly List<string> SideNames = new List<string>();
        public readonly List<int[]> Sides = new List<int[]>();
        public readonly List<int[]> Attach = new List<int[]>();
        public int FX, FY;

        public bool InRow(int px, int py)
        {
            int tx = px >> 4, ty = py >> 4;
            return tx >= RX0 && tx <= RX1 && ty >= RY0 && ty <= RY1;
        }
    }

    internal sealed partial class CTFXBot
    {
        Func<int, int, float, float> _edgeCostFn;

        /// <summary>The route edge cost hook for this bot's plans (CTFXRouteBook.Plan).</summary>
        Func<int, int, float, float> EdgeCostFn
        {
            get
            {
                if (_edgeCostFn == null) { CTFXBot me = this; _edgeCostFn = delegate(int n, int t, float len) { return me.Mgr.RouteEdgeCost(me, n, t, len, Environment.TickCount); }; }
                return _edgeCostFn;
            }
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal long EdgeHeatCosts;
        float[] _nodeHeat;
        bool[] _nodeOut;
        int _nodeHeatAt;

        /// <summary>Every 2 s: each waypoint's fight cost (up to 250 px, from CTFXCommand heat) and, once, whether it lies outdoors.</summary>
        void NodeHeatStep(int now)
        {
            if (Routes == null || Cmdr == null || now - _nodeHeatAt < 2000) return;
            _nodeHeatAt = now;
            int n = Routes.N;
            if (_nodeHeat == null || _nodeHeat.Length != n)
            {
                _nodeHeat = new float[n]; _nodeOut = new bool[n];
                for (int i = 0; i < n; i++) _nodeOut[i] = AreaAt(Routes.X[i], Routes.Y[i]) == "out";
            }
            for (int i = 0; i < n; i++)
            {
                double heat = Cmdr.HeatAtPx(Routes.X[i], Routes.Y[i]);
                _nodeHeat[i] = heat > 0.05 ? (float)(250.0 * (1.0 - Math.Exp(-heat / 5.0))) : 0f;
            }
        }

        /// <summary>
        /// Travis 10-16: "not utilizing all of the pathing ... or the pocket areas to have travel paths to get to the bases through other
        /// routes / non-fighting routes". Every route segment costs a bot its own taste (+-25 % of its length, fixed per bot and segment
        /// for 3 minutes), so near-equal ways are shared out, plus up to 400 px through the fights of the last minute (CTFXCommand heat):
        /// it goes round by a quiet way when there is one, and only fights where it is going.
        /// </summary>
        internal float RouteEdgeCost(CTFXBot b, int n, int t, float len, int now)
        {
            unchecked
            {
                int a = Math.Min(n, t), c = Math.Max(n, t);
                uint h = (uint)(b.Slot != null ? b.Slot.FixedId : b._id) * 2654435761u ^ (uint)(a * 40503 + c * 9973) ^ (uint)((now / 180000) * 7919);
                h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12;
                double cost = ((h % 1000) / 1000.0 - 0.5) * 0.5 * len;
                if (_nodeHeat != null && n < _nodeHeat.Length && t < _nodeHeat.Length)
                {
                    float heat = (_nodeHeat[n] + _nodeHeat[t]) * 0.5f;
                    if (heat > 0.5f) { cost += heat; EdgeHeatCosts++; }
                    if (_nodeOut[n] && _nodeOut[t]) cost += 0.35 * len;                          // round the outside: the building's own ways first
                }
                return (float)cost;
            }
        }

        readonly Dictionary<string, CTFXBaseDef> _bases = new Dictionary<string, CTFXBaseDef>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _ground = new Dictionary<string, int>(StringComparer.Ordinal);       // "end|team" -> doors held
        readonly Dictionary<string, int> _groundAt = new Dictionary<string, int>(StringComparer.Ordinal);     // last tick it was confirmed
        bool _basesLoaded;
        int _baseAt;
        internal int GroundGains, GroundLosses, LaneGoals, DoorGoals, BaseFlagGoals, AttachPicks;

        static int[] Ints(string[] v, int from, int n)
        {
            int[] r = new int[n];
            for (int i = 0; i < n; i++) r[i] = int.Parse(v[from + i].Trim());
            return r;
        }

        internal void LoadBases()
        {
            _basesLoaded = true;
            _bases.Clear();
            string path = Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("CTFX", "CTFXBases.txt"))));
            if (!File.Exists(path)) { Out("[CTFXBots] bases: no CTFXBases.txt"); return; }
            CTFXBaseDef d = null;
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
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string[] v = line.Substring(eq + 1).Split(',');
                    if (key == "base") { d = new CTFXBaseDef(); d.End = v[0].Trim(); _bases[d.End] = d; continue; }
                    if (d == null) continue;
                    switch (key)
                    {
                        case "row": { int[] r = Ints(v, 0, 4); d.RX0 = r[0]; d.RY0 = r[1]; d.RX1 = r[2]; d.RY1 = r[3]; break; }
                        case "lane": d.LaneNames.Add(v[0].Trim()); d.Lanes.Add(Ints(v, 1, 4)); break;
                        case "choke": d.ChokeNames.Add(v[0].Trim()); d.Chokes.Add(Ints(v, 1, 4)); break;
                        case "side": d.SideNames.Add(v[0].Trim()); d.Sides.Add(Ints(v, 1, 4)); break;
                        case "attach": d.Attach.Add(Ints(v, 0, 2)); break;
                        case "flag": { int[] f = Ints(v, 0, 2); d.FX = f[0]; d.FY = f[1]; break; }
                    }
                }
            }
            catch (Exception ex) { LogErr("bases", ex); }
            foreach (CTFXBaseDef b in _bases.Values)
                Out("[CTFXBots] bases: " + b.End + " - " + b.Lanes.Count + " lanes, doors " + string.Join(" > ", b.ChokeNames.ToArray()) + (b.Sides.Count > 0 ? " (side " + string.Join(", ", b.SideNames.ToArray()) + ")" : "")
                    + ", " + b.Attach.Count + " attach corners, flag " + b.FX + "," + b.FY);
        }

        internal CTFXBaseDef BaseOf(string end)
        {
            if (!_basesLoaded) LoadBases();
            CTFXBaseDef d;
            return end != null && _bases.TryGetValue(end, out d) ? d : null;
        }

        /// <summary>The base whose room row holds (x, y) px, or whose flag corner is within 300 px of it.</summary>
        internal CTFXBaseDef BaseAt(int x, int y)
        {
            if (!_basesLoaded) LoadBases();
            foreach (CTFXBaseDef d in _bases.Values)
            {
                if (d.InRow(x, y)) return d;
                double dx = d.FX * 16 + 8 - x, dy = d.FY * 16 + 8 - y;
                if (dx * dx + dy * dy <= 300.0 * 300.0) return d;
            }
            return null;
        }

        internal int GroundOf(Team t, CTFXBaseDef d)
        {
            int g;
            return t != null && d != null && _ground.TryGetValue(d.End + "|" + t._name, out g) ? g : 0;
        }

        void Count(Team t, int x, int y, double rMine, double rFoe, out int mine, out int foes)
        {
            mine = 0; foes = 0;
            foreach (CTFXBot o in LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship) continue;
                double dx = o._state.positionX - x, dy = o._state.positionY - y, d2 = dx * dx + dy * dy;
                if (o._team == t) { if (d2 <= rMine * rMine) mine++; }
                else if (d2 <= rFoe * rFoe) foes++;
            }
            foreach (Player p in Arena.PlayersIngame)
            {
                if (p == null || p.IsDead || p._state == null) continue;
                double dx = p._state.positionX - x, dy = p._state.positionY - y, d2 = dx * dx + dy * dy;
                if (p._team == t) { if (d2 <= rMine * rMine) mine++; }
                else if (d2 <= rFoe * rFoe) foes++;
            }
        }

        internal void BaseStep(int now)
        {
            if (!_basesLoaded) LoadBases();
            NodeHeatStep(now);
            if (_bases.Count == 0 || now - _baseAt < 2000) return;
            _baseAt = now;
            foreach (CTFXBaseDef d in _bases.Values)
            {
                if (d.Chokes.Count == 0) continue;
                int cx = (d.RX0 + d.RX1) * 8, cy = (d.RY0 + d.RY1) * 8;
                HashSet<Team> near = new HashSet<Team>();
                foreach (CTFXBot o in LiveBots)
                {
                    if (o.IsDead || o.InDropship || o._team == null) continue;
                    double dx = o._state.positionX - cx, dy = o._state.positionY - cy;
                    if (dx * dx + dy * dy <= 1700.0 * 1700.0) near.Add(o._team);
                }
                foreach (Team t in near)
                {
                    string key = d.End + "|" + t._name;
                    int old, at, g = 0;
                    _ground.TryGetValue(key, out old);
                    _groundAt.TryGetValue(key, out at);
                    for (int k = d.Chokes.Count - 1; k >= 0; k--)
                    {   // the furthest door it holds now
                        int mine, foes;
                        Count(t, d.Chokes[k][0] * 16 + 8, d.Chokes[k][1] * 16 + 8, 180.0, 220.0, out mine, out foes);
                        if (mine > 0 && foes <= mine) { g = k + 1; break; }
                    }
                    if (g >= old) { if (g > 0) _groundAt[key] = now; }
                    else if (old > 0)
                    {   // nobody on the frontier: it stays won until retaken, or 20 s without a confirmation
                        int mine, foes;
                        int[] c = d.Chokes[old - 1];
                        Count(t, c[0] * 16 + 8, c[1] * 16 + 8, 400.0, 220.0, out mine, out foes);
                        if (foes <= mine && now - at < 20000) g = old;
                    }
                    if (g > old)
                    {
                        GroundGains++;
                        if (GroundGains <= 40) Out("[CTFXBots] ground: " + t._name + " takes " + d.ChokeNames[g - 1] + " of " + d.End + (g == d.Chokes.Count ? " - the flag room is open" : ""));
                    }
                    else if (g < old) GroundLosses++;
                    if (g == 0) _ground.Remove(key); else _ground[key] = g;
                }
            }
        }

        int[] LaneFor(CTFXBot b, CTFXBaseDef d, int now)
        {
            int n = d.Lanes.Count;
            int k = (int)(((uint)(b.Slot != null ? b.Slot.FixedId : b._id) + (uint)(now / 120000)) % (uint)n);
            int[] l = d.Lanes[k];
            if (CrowdAt(l[2] * 16 + 8, l[3] * 16 + 8, 180.0) >= 7)
            {   // a jammed lane: the emptiest one
                int best = int.MaxValue;
                foreach (int[] o in d.Lanes) { int c = CrowdAt(o[2] * 16 + 8, o[3] * 16 + 8, 180.0); if (c < best) { best = c; l = o; } }
            }
            return l;
        }

        /// <summary>
        /// An attacker's next goal in base d: down its lane into the front corridor while it is still coming, then the next door its
        /// team has not taken (from the attackers' side), then the flag at (fx, fy) px.
        /// </summary>
        internal bool AttackGoal(CTFXBot b, CTFXBaseDef d, int fx, int fy, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (b == null || d == null || d.Chokes.Count == 0) return false;
            int now = Environment.TickCount;
            int px = b._state.positionX, py = b._state.positionY;
            int ground = GroundOf(b._team, d);
            if (!d.InRow(px, py) && ground == 0)
            {
                int[] c1 = d.Chokes[0];
                int[] lane = LaneFor(b, d, now);
                double dc = Math.Sqrt(Math.Pow(px - (c1[0] * 16 + 8), 2) + Math.Pow(py - (c1[1] * 16 + 8), 2));
                double dm = Math.Sqrt(Math.Pow((lane[2] - c1[0]) * 16, 2) + Math.Pow((lane[3] - c1[1]) * 16, 2));
                if (dc > dm + 48)
                {
                    if (!NearestOpen(lane[2] + Rng.Next(-1, 2), lane[3] + Rng.Next(-1, 2), 3, out tx, out ty)) { tx = lane[2]; ty = lane[3]; }
                    LaneGoals++;
                    return true;
                }
            }
            if (ground >= d.Chokes.Count)
            {
                NearestOpenPx(fx + Rng.Next(-24, 25), fy + Rng.Next(-24, 25), out tx, out ty);
                BaseFlagGoals++;
                return true;
            }
            int[] c = d.Chokes[ground];
            double vx = c[2] - c[0], vy = c[3] - c[1], vl = Math.Max(1.0, Math.Sqrt(vx * vx + vy * vy));
            int off = 1 + Rng.Next(3);
            int gx = c[0] + (int)Math.Round(vx / vl * off) + Rng.Next(-2, 3), gy = c[1] + (int)Math.Round(vy / vl * off) + Rng.Next(-2, 3);
            if (!NearestOpen(gx, gy, 3, out tx, out ty)) { tx = c[0]; ty = c[1]; }
            DoorGoals++;
            return true;
        }

        /// <summary>The base runner's spot: the attach corner with the fewest enemies within 350 px.</summary>
        internal bool AttachCorner(CTFXBot b, CTFXBaseDef d, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (d == null || d.Attach.Count == 0) return false;
            int best = -1;
            double bs = double.MaxValue;
            for (int i = 0; i < d.Attach.Count; i++)
            {
                int mine, foes;
                Count(b._team, d.Attach[i][0] * 16 + 8, d.Attach[i][1] * 16 + 8, 120.0, 350.0, out mine, out foes);
                double sc = foes * 3 + mine * 2 + Rng.NextDouble();                // quiet, and not where a teammate already is
                if (sc < bs) { bs = sc; best = i; }
            }
            tx = d.Attach[best][0]; ty = d.Attach[best][1];
            AttachPicks++;
            return true;
        }

        internal string BaseText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(" | bases: ground gains " + GroundGains + " (lost " + GroundLosses + "), lane goals " + LaneGoals + ", door goals " + DoorGoals
                + ", flag-room goals " + BaseFlagGoals + ", attach corners picked " + AttachPicks + ", sweep stops " + (Cmdr != null ? Cmdr.BaseSweepSteps : 0) + ", route legs costed for fights " + EdgeHeatCosts + ", held now");
            foreach (KeyValuePair<string, int> kv in _ground) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }
    }

    internal sealed partial class CTFXCommand
    {
        internal int BaseSweepSteps;

        internal double HeatAtPx(int x, int y) { return HeatAt(x, y); }

        /// <summary>
        /// Travis 10-16: after the defenders clear their base they sweep it once, inside out - the flag room's door, the deeper doors, the
        /// second door, the entrance, the front corridor - about 8 s a stop (each bot at its own place in the order), then back to their
        /// stations: no standing around in a swept area. Only in the 60 s after the last attacker was inside (PushOut).
        /// </summary>
        internal bool BaseSweepGoal(CTFXTeamCmd t, CTFXBot b, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (t == null || b == null || !PushOut(t, now)) return false;
            CTFXBaseDef d = _m.BaseOf(t.End);
            if (d == null || d.Chokes.Count == 0) return false;
            List<int[]> stops = new List<int[]>();
            for (int k = d.Chokes.Count - 1; k >= 0; k--) stops.Add(d.Chokes[k]);
            foreach (int[] s in d.Sides) stops.Insert(1, s);
            foreach (int[] l in d.Lanes) stops.Add(new int[] { l[2], l[3], l[0], l[1] });
            int step = (now - t.LastThreatAt) / 8000 + (int)(b._id % 3);
            if (step >= stops.Count) return false;                                          // the pass is done: its station
            int[] c = stops[step];
            if (!_m.NearestOpen(c[0] + _m.Rng.Next(-2, 3), c[1] + _m.Rng.Next(-2, 3), 3, out tx, out ty)) { tx = c[0]; ty = c[1]; }
            BaseSweepSteps++;
            return true;
        }

        /// <summary>The base's own lines (CTFXBases.txt): lane mouths, then the entrance and the second door, then the rest.</summary>
        bool BuildBaseLines(CTFXTeamCmd t, LineSet ls)
        {
            CTFXBaseDef d = _m.BaseOf(t.End);
            if (d == null || d.Chokes.Count == 0) return false;
            foreach (int[] l in d.Lanes)
                AddLinePt(ls.L[0], l[2] * 16 + 8, l[3] * 16 + 8, l[0] * 16 + 8, l[1] * 16 + 8, "lane");
            for (int k = 0; k < d.Chokes.Count; k++)
            {
                int[] c = d.Chokes[k];
                double vx = c[2] - c[0], vy = c[3] - c[1], vl = Math.Max(1.0, Math.Sqrt(vx * vx + vy * vy));
                int wx = c[0] * 16 + 8 + (int)(vx / vl * 400), wy = c[1] * 16 + 8 + (int)(vy / vl * 400);
                AddLinePt(ls.L[k < 2 ? 1 : 2], c[0] * 16 + 8, c[1] * 16 + 8, wx, wy, d.ChokeNames[k]);
            }
            foreach (int[] c in d.Sides)
            {
                double vx = c[2] - c[0], vy = c[3] - c[1], vl = Math.Max(1.0, Math.Sqrt(vx * vx + vy * vy));
                AddLinePt(ls.L[2], c[0] * 16 + 8, c[1] * 16 + 8, c[0] * 16 + 8 + (int)(vx / vl * 400), c[1] * 16 + 8 + (int)(vy / vl * 400), "side");
            }
            return true;
        }
    }
}
/* PARSER GUARD - keep last */
