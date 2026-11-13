// CTF:X server-side bots - ROUTES. The bots walk precomputed waypoint routes instead of the engine pathfinder (Travis 10-11:
// the pathfinder hurts bot performance and is being rewritten). LOCAL ONLY, like the other CTFXBot files.
//
// CTFXRoutes.txt is generated offline (scratchpad routes/gen_routes2.mts): tile-level A* between the key points of each base
// end (flag room, halls, entrances, spawn tiles, caves, plaza), string-pulled into straight segments a bot body fits along.
// A segment through a door carries the door's master LIO id (the door a switch opens) and starts at a waypoint in front of
// the door, so a bot can wait there while the door opens.
//
// Runtime: every distinct waypoint is a node and consecutive waypoints of a route are an edge (both ways). A plan is Dijkstra
// on that small graph (about a thousand nodes) from the nearest node the bot can walk straight to, to the node nearest the
// goal. Everything runs on the arena thread (bot polls); there is no engine pathfinder call anywhere.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXRouteBook
    {
        internal int N;
        internal int[] X, Y;                                 // node centre, px (tile * 16 + 8)
        int[] _adjStart, _adjTo, _adjDoor;                   // compressed adjacency: edges of node n are [_adjStart[n], _adjStart[n + 1])
        float[] _adjLen;
        internal readonly Dictionary<string, int[]> Routes = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal bool[] GridOnly;                                      // a waypoint only the generated grid has (walk through it, never a goal)
        internal bool IsGrid(int n) { return GridOnly != null && n >= 0 && n < GridOnly.Length && GridOnly[n]; }
        internal int EdgeCount, DoorEdges;
        internal string Summary = "routes: not loaded";

        const int Cell = 128;                                 // px, spatial buckets for nearest-node queries
        readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();

        // Dijkstra buffers, reused (arena thread only)
        float[] _dist;
        int[] _prev, _seen, _heap;
        float[] _heapKey;
        int _heapN, _stamp;

        static long CellKey(int cx, int cy) { return ((long)cx << 32) ^ (uint)cy; }

        internal static CTFXRouteBook Load(string path, out string error)
        {
            return Load(path, null, 0, out error);
        }

        /// <summary>
        /// Load the routes. With <paramref name="clear"/> (a straight-walk test on the live tiles) waypoints of different routes that
        /// are <paramref name="joinPx"/> or closer and walkable between are joined too: routes otherwise meet only where they share a
        /// waypoint, so two routes through the same hall would not connect and a plan zig-zags between their ends. A shut door
        /// fails the walk, so no junction crosses one (the doors are shut at attach).
        /// </summary>
        internal static CTFXRouteBook Load(string path, Func<int, int, int, int, bool> clear, int joinPx, out string error)
        {
            error = null;
            if (!File.Exists(path)) { error = "no " + path; return null; }
            CTFXRouteBook rb = new CTFXRouteBook();
            Dictionary<long, int> nodeOf = new Dictionary<long, int>();
            List<int> xs = new List<int>(), ys = new List<int>();
            Dictionary<long, int> edgeDoor = new Dictionary<long, int>();     // undirected edge key -> door (0 = none)
            Dictionary<long, float> edgeLen = new Dictionary<long, float>();
            HashSet<int> gridSeen = new HashSet<int>(), realSeen = new HashSet<int>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (!line.StartsWith("route=", StringComparison.OrdinalIgnoreCase)) continue;
                int comma = line.IndexOf(','), colon = comma < 0 ? -1 : line.IndexOf(':', comma + 1);   // names hold ':' (blue-left:patrol)
                if (comma < 0 || colon < 0) continue;
                string name = line.Substring(6, comma - 6).Trim(), kind = line.Substring(comma + 1, colon - comma - 1).Trim();
                List<int> seq = new List<int>();
                int pendingDoor = 0, prev = -1;
                foreach (string tok in line.Substring(colon + 1).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (tok[0] == 'D' || tok[0] == 'd')
                    {
                        int d;
                        if (pendingDoor == 0 && int.TryParse(tok.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) pendingDoor = d;
                        continue;
                    }
                    int c2 = tok.IndexOf(',');
                    int tx, ty;
                    if (c2 <= 0 || !int.TryParse(tok.Substring(0, c2), NumberStyles.Integer, CultureInfo.InvariantCulture, out tx)
                        || !int.TryParse(tok.Substring(c2 + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ty)) continue;
                    long key = ((long)tx << 32) ^ (uint)ty;
                    int n;
                    if (!nodeOf.TryGetValue(key, out n)) { n = xs.Count; nodeOf[key] = n; xs.Add(tx * 16 + 8); ys.Add(ty * 16 + 8); }
                    if (prev >= 0 && prev != n)
                    {
                        long ek = prev < n ? ((long)prev << 32) | (uint)n : ((long)n << 32) | (uint)prev;
                        float len = (float)Math.Sqrt((double)(xs[n] - xs[prev]) * (xs[n] - xs[prev]) + (double)(ys[n] - ys[prev]) * (ys[n] - ys[prev]));
                        if (kind == "cave") len *= 3f;              // cave runs are for minerals: raids take the bridges (Travis 10-11)
                        float have;
                        if (!edgeLen.TryGetValue(ek, out have) || len < have) edgeLen[ek] = len;
                        int hd;
                        if (pendingDoor != 0 || !edgeDoor.TryGetValue(ek, out hd)) edgeDoor[ek] = pendingDoor != 0 ? pendingDoor : (edgeDoor.TryGetValue(ek, out hd) ? hd : 0);
                    }
                    pendingDoor = 0;
                    if (seq.Count == 0 || seq[seq.Count - 1] != n) seq.Add(n);
                    prev = n;
                }
                if (seq.Count >= 2) { rb.Routes[name] = seq.ToArray(); rb.Kinds[name] = kind; }
                foreach (int q in seq) { if (kind == "grid") gridSeen.Add(q); else realSeen.Add(q); }
            }
            rb.N = xs.Count;
            if (rb.N == 0) { error = "no waypoints in " + path; return null; }
            rb.X = xs.ToArray(); rb.Y = ys.ToArray();
            rb.GridOnly = new bool[rb.N];
            foreach (int q in gridSeen) if (!realSeen.Contains(q)) rb.GridOnly[q] = true;

            // junctions between routes (see the summary of this Load)
            int joins = 0;
            if (clear != null && joinPx > 0)
            {
                Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
                for (int i = 0; i < rb.N; i++)
                {
                    long ck = CellKey(rb.X[i] / joinPx, rb.Y[i] / joinPx);
                    List<int> l;
                    if (!grid.TryGetValue(ck, out l)) { l = new List<int>(); grid[ck] = l; }
                    l.Add(i);
                }
                double lim = (double)joinPx * joinPx;
                for (int i = 0; i < rb.N; i++)
                {
                    int cx = rb.X[i] / joinPx, cy = rb.Y[i] / joinPx;
                    for (int gx = cx - 1; gx <= cx + 1; gx++)
                        for (int gy = cy - 1; gy <= cy + 1; gy++)
                        {
                            List<int> l;
                            if (!grid.TryGetValue(CellKey(gx, gy), out l)) continue;
                            foreach (int j in l)
                            {
                                if (j <= i) continue;
                                double dx = rb.X[j] - rb.X[i], dy = rb.Y[j] - rb.Y[i], d2 = dx * dx + dy * dy;
                                long ek = ((long)i << 32) | (uint)j;
                                if (d2 > lim || edgeLen.ContainsKey(ek) || !clear(rb.X[i], rb.Y[i], rb.X[j], rb.Y[j])) continue;
                                edgeLen[ek] = (float)Math.Sqrt(d2);
                                edgeDoor[ek] = 0;
                                joins++;
                            }
                        }
                }
            }

            // compressed adjacency
            int[] deg = new int[rb.N];
            foreach (long ek in edgeLen.Keys) { deg[(int)(ek >> 32)]++; deg[(int)(ek & 0xffffffffL)]++; }
            rb._adjStart = new int[rb.N + 1];
            for (int i = 0; i < rb.N; i++) rb._adjStart[i + 1] = rb._adjStart[i] + deg[i];
            int m = rb._adjStart[rb.N];
            rb._adjTo = new int[m]; rb._adjDoor = new int[m]; rb._adjLen = new float[m];
            int[] fill = new int[rb.N];
            foreach (KeyValuePair<long, float> kv in edgeLen)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffffL), door = edgeDoor[kv.Key];
                int ia = rb._adjStart[a] + fill[a]++, ib = rb._adjStart[b] + fill[b]++;
                rb._adjTo[ia] = b; rb._adjDoor[ia] = door; rb._adjLen[ia] = kv.Value;
                rb._adjTo[ib] = a; rb._adjDoor[ib] = door; rb._adjLen[ib] = kv.Value;
                if (door != 0) rb.DoorEdges++;
            }
            rb.EdgeCount = edgeLen.Count;
            for (int i = 0; i < rb.N; i++)
            {
                long ck = CellKey(rb.X[i] / Cell, rb.Y[i] / Cell);
                List<int> l;
                if (!rb._cells.TryGetValue(ck, out l)) { l = new List<int>(); rb._cells[ck] = l; }
                l.Add(i);
            }
            rb._dist = new float[rb.N]; rb._prev = new int[rb.N]; rb._seen = new int[rb.N];
            rb._heap = new int[m + rb.N + 4]; rb._heapKey = new float[m + rb.N + 4];
            rb.Summary = "routes: " + rb.Routes.Count + " routes, " + rb.N + " waypoints, " + rb.EdgeCount + " segments (" + rb.DoorEdges + " through doors, "
                + joins + " junctions between routes)";
            return rb;
        }

        /// <summary>The door (master LIO id) the segment a-b goes through, 0 = none.</summary>
        internal int DoorBetween(int a, int b)
        {
            if (a < 0 || b < 0 || a >= N || b >= N) return 0;
            for (int k = _adjStart[a]; k < _adjStart[a + 1]; k++)
                if (_adjTo[k] == b) return _adjDoor[k];
            return 0;
        }

        /// <summary>
        /// Nearest node within maxPx; with a walk test, the nearest one the test accepts (a straight walk from px,py), else -1.
        /// </summary>
        internal int Nearest(int px, int py, int maxPx, Func<int, int, int, int, bool> walk)
        {
            int cx = px / Cell, cy = py / Cell, rings = maxPx / Cell + 1;
            List<int> cand = null;
            List<long> d2s = null;
            for (int r = 0; r <= rings; r++)
            {
                for (int oy = -r; oy <= r; oy++)
                    for (int ox = -r; ox <= r; ox++)
                    {
                        if (Math.Max(Math.Abs(ox), Math.Abs(oy)) != r) continue;
                        List<int> l;
                        if (!_cells.TryGetValue(CellKey(cx + ox, cy + oy), out l)) continue;
                        foreach (int n in l)
                        {
                            long dx = X[n] - px, dy = Y[n] - py, d2 = dx * dx + dy * dy;
                            if (d2 > (long)maxPx * maxPx) continue;
                            if (cand == null) { cand = new List<int>(); d2s = new List<long>(); }
                            int at = 0;
                            while (at < d2s.Count && d2s[at] <= d2) at++;
                            cand.Insert(at, n); d2s.Insert(at, d2);
                        }
                    }
                // a ring further out cannot hold anything closer than the best candidate of this ring once it is far enough
                if (cand != null && cand.Count >= 6 && d2s[0] < (long)(r * Cell) * (r * Cell)) break;
            }
            if (cand == null) return -1;
            if (walk == null) return cand[0];
            int tries = 0;
            foreach (int n in cand)
            {
                if (walk(px, py, X[n], Y[n])) return n;
                if (++tries >= 10) break;                   // bounded work per query
            }
            return -1;
        }

        /// <summary>Shortest path from node a to node b into path (cleared first). False = not connected.</summary>
        internal bool Plan(int a, int b, List<int> path) { return Plan(a, b, path, null); }

        /// <summary>As Plan, a door segment costing doorCost(door, near node) px more (CTFXBrawl.cs: crowded doors, a bot's taste).</summary>
        internal bool Plan(int a, int b, List<int> path, Func<int, int, float> doorCost) { return Plan(a, b, path, doorCost, null); }

        /// <summary>As Plan, every segment n-t also costing edgeCost(n, t, its length) px more (CTFXBase.cs: a bot's taste, fights on the way).</summary>
        internal bool Plan(int a, int b, List<int> path, Func<int, int, float> doorCost, Func<int, int, float, float> edgeCost)
        {
            path.Clear();
            if (a < 0 || b < 0 || a >= N || b >= N) return false;
            if (a == b) { path.Add(a); return true; }
            _stamp++;
            if (_stamp == int.MaxValue) { Array.Clear(_seen, 0, _seen.Length); _stamp = 1; }
            _heapN = 0;
            Touch(a); _dist[a] = 0f; _prev[a] = -1; Push(a, 0f);
            while (_heapN > 0)
            {
                float d; int n = Pop(out d);
                if (d > _dist[n]) continue;
                if (n == b) break;
                for (int k = _adjStart[n]; k < _adjStart[n + 1]; k++)
                {
                    int t = _adjTo[k];
                    float nd = d + _adjLen[k];
                    if (doorCost != null && _adjDoor[k] != 0) nd += doorCost(_adjDoor[k], n);
                    if (edgeCost != null) nd += edgeCost(n, t, _adjLen[k]);
                    if (_seen[t] != _stamp) { Touch(t); }
                    if (nd < _dist[t]) { _dist[t] = nd; _prev[t] = n; Push(t, nd); }
                }
            }
            if (_seen[b] != _stamp || float.IsPositiveInfinity(_dist[b])) return false;
            for (int n = b; n >= 0; n = _prev[n]) path.Add(n);
            path.Reverse();
            return true;
        }

        void Touch(int n) { _seen[n] = _stamp; _dist[n] = float.PositiveInfinity; _prev[n] = -1; }

        void Push(int n, float key)
        {
            if (_heapN >= _heap.Length) return;             // cannot happen: each edge pushes at most once per relaxation
            int i = _heapN++;
            _heap[i] = n; _heapKey[i] = key;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_heapKey[p] <= _heapKey[i]) break;
                int tn = _heap[p]; _heap[p] = _heap[i]; _heap[i] = tn;
                float tk = _heapKey[p]; _heapKey[p] = _heapKey[i]; _heapKey[i] = tk;
                i = p;
            }
        }

        int Pop(out float key)
        {
            int top = _heap[0]; key = _heapKey[0];
            _heapN--;
            if (_heapN > 0)
            {
                _heap[0] = _heap[_heapN]; _heapKey[0] = _heapKey[_heapN];
                int i = 0;
                for (;;)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _heapN && _heapKey[l] < _heapKey[m]) m = l;
                    if (r < _heapN && _heapKey[r] < _heapKey[m]) m = r;
                    if (m == i) break;
                    int tn = _heap[m]; _heap[m] = _heap[i]; _heap[i] = tn;
                    float tk = _heapKey[m]; _heapKey[m] = _heapKey[i]; _heapKey[i] = tk;
                    i = m;
                }
            }
            return top;
        }
    }
}
/* PARSER GUARD - keep last */
