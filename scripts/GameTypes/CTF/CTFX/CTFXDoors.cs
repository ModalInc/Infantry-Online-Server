// CTF:X server-side bots - DOORS (region graph, door planning, switch opening). The brain is CTFXBot.cs, the manager CTFXBots.cs.
//
// Why: the native pathfinder is built once from the static level where every door tile is solid, so it can never return a
// path through a door. We label the open areas of the map (regions; door rects count as walls), find the doors that join
// two regions and plan the door sequence ourselves. A bot paths (native pathfinder, same region) to a stand tile in front
// of the door, switchRequest(force) opens the door, then it steers straight through to the stand tile on the far side.
//
// Engine facts (verified by the caller against ArenaLio.cs, do not re-derive):
//  a. arena._doors and arena._switches are public. DoorState: Door, Opened, relativePhysicsX/Y, physicsWidth/Height,
//     inverseState, switchLink (the switches that operate the door). SwitchState: Switch, bOpen, lastOperation, closeDelay.
//  b. Door rect (the private updateDoors): x = OffsetX / 16 - Level.OffsetX + relativePhysicsX + i (i < physicsWidth),
//     y = OffsetY / 16 - Level.OffsetY + relativePhysicsY + j (j < physicsHeight), tile index = y * _levelWidth + x.
//  c. arena._tiles[index].Blocked is the LIVE state: an open door is not blocked, so bots can walk (and see) through it.
//  d. switchRequest(true, bOpen, player, sw) skips the range box, the switch delay and the validity checks, changes every
//     linked door, tells the clients, and the engine closes it again after the switch auto close delay. player may be null
//     for a switch with Frequency -1, AmmoId 0 and no skill logic (all CTF:X switches), else any arena player works.
//  e. updateDoors compares the request with DoorState.Opened BEFORE it applies inverseState, so an inverse door can ignore
//     bOpen = true. OpenDoor therefore trusts the tiles only: attempt 0 asks for open, attempt 1 for closed (inverse doors),
//     attempt 2 for open again (a switch whose own state was out of step). The caller retries once per second.
//  f. Everything here runs on the arena thread (bot polls). The pathing thread never touches this file.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>One door crossing of a plan: path to the near stand tile, open the door, steer straight to the far stand tile.</summary>
    internal struct CTFXDoorStep
    {
        public int DoorKey;                          // the engine door id
        public int NearRegion, FarRegion;
        public int NearTx, NearTy, FarTx, FarTy;     // stand tiles
    }

    /// <summary>Region graph of the CTF:X area joined by its switch-operated doors, plus the door opening calls.</summary>
    internal sealed class CTFXDoorNav
    {
        const int MinRegionTiles = 20;               // smaller open pockets are ignored unless a door touches them (airlocks)
        const int DoorPenalty = 160;                 // px added per door of a plan, so fewer doors win near ties
        const int WallLab = -1, SmallLab = -2, DoorLab = -4;

        sealed class EdgeRec { public int Ra, Rb, Door, Ax, Ay, Bx, By; }

        readonly Arena _arena;

        // results of the build
        internal bool Ready;                         // at least one door edge exists
        internal string Summary = "[CTFXBots] doors: not built";
        internal string Detail;
        internal int TotalDoors, UsableDoors, Regions, Edges, BuildMs;
        // stats line counters (the manager resets them at every start)
        internal int Opens, Fails;
        internal Action<string> Log;

        // label grid of the area: region id (> 0), WallLab, SmallLab (an open pocket we ignore), DoorLab (inside a door rect)
        int _x0, _y0, _w, _h;
        int[] _lab;

        // usable doors, by door index
        readonly Dictionary<int, int> _idxOfKey = new Dictionary<int, int>();
        int _nDoors;
        int[] _dKey, _dX0, _dY0, _dX1, _dY1;
        LioInfo.Switch[] _dSwitch;
        Arena.DoorState[] _dState;

        // region graph over compact nodes (only the regions an edge touches)
        int[] _nodeOf, _nodeRegion, _comp;        // _comp: connected component of a node over the door edges
        int[][] _adj;
        int[] _eRa, _eRb, _eDoor, _eAx, _eAy, _eBx, _eBy;
        // planner scratch (k nodes)
        int[] _dist, _prevEdge, _prevNode, _entX, _entY;
        bool[] _done;

        CTFXDoorNav(Arena arena) { _arena = arena; }
        CTFXProfile _prof = CTFXProfile.Default;     // the window the graph is built over (CTF:X: the big map; MiniTP: dropship + bases + field)

        // ================================================================ build
        /// <summary>Label the area, find the doors, build the graph. Never throws: a failure leaves an inert object (Ready false).</summary>
        internal static CTFXDoorNav Build(Arena arena, CTFXProfile prof)
        {
            CTFXDoorNav n = new CTFXDoorNav(arena);
            n._prof = prof ?? CTFXProfile.Default;
            Stopwatch sw = Stopwatch.StartNew();
            bool ok = true;
            string err = null;
            try { n.DoBuild(); }
            catch (Exception ex) { ok = false; err = ex.GetType().Name + " " + ex.Message; }
            sw.Stop();
            n.BuildMs = (int)sw.ElapsedMilliseconds;
            if (!ok)
            {
                n.Ready = false;
                n.Summary = "[CTFXBots] doors: build failed after " + n.BuildMs + " ms: " + err;
            }
            else
                n.Summary = "[CTFXBots] doors: " + n.UsableDoors + " usable doors, " + n.Regions + " regions, " + n.Edges + " edges, built in " + n.BuildMs + " ms";
            return n;
        }

        void DoBuild()
        {
            Dictionary<int, Arena.DoorState> doors = _arena._doors;
            TotalDoors = doors == null ? 0 : doors.Count;
            int lw = _arena._levelWidth, lh = _arena._levelHeight;
            _x0 = Math.Max(_prof.X0, 1);
            _y0 = Math.Max(_prof.Y0, 1);
            int x1 = Math.Min(_prof.X1, lw - 2), y1 = Math.Min(_prof.Y1, lh - 2);
            _w = x1 - _x0 + 1; _h = y1 - _y0 + 1;
            if (_w < 4 || _h < 4 || doors == null || doors.Count == 0)
                return;                                              // nothing to plan: inert

            int lox = 0, loy = 0;                                    // level offset in tiles (0,0 for ctfdls4, still applied)
            try { lox = _arena._server._assets.Level.OffsetX; loy = _arena._server._assets.Level.OffsetY; }
            catch (Exception) { }

            // ---- open / wall grid straight from the live tiles
            int total = _w * _h;
            _lab = new int[total];
            var tiles = _arena._tiles;
            for (int ty = 0; ty < _h; ty++)
            {
                int row = (ty + _y0) * lw + _x0, o = ty * _w;
                for (int tx = 0; tx < _w; tx++)
                    _lab[o + tx] = tiles[row + tx].Blocked ? WallLab : 0;
            }

            // ---- door rects: every door in the area is a wall, the ones with a switch are candidates
            int cap = doors.Count;
            _dKey = new int[cap]; _dX0 = new int[cap]; _dY0 = new int[cap]; _dX1 = new int[cap]; _dY1 = new int[cap];
            _dSwitch = new LioInfo.Switch[cap]; _dState = new Arena.DoorState[cap];
            int minCd = int.MaxValue, maxCd = int.MinValue;
            foreach (KeyValuePair<int, Arena.DoorState> kv in doors)
            {
                Arena.DoorState dd = kv.Value;
                if (dd == null || dd.Door == null || dd.physicsWidth <= 0 || dd.physicsHeight <= 0)
                    continue;
                int rx0 = dd.Door.GeneralData.OffsetX / 16 - lox + dd.relativePhysicsX;
                int ry0 = dd.Door.GeneralData.OffsetY / 16 - loy + dd.relativePhysicsY;
                int rx1 = rx0 + dd.physicsWidth - 1, ry1 = ry0 + dd.physicsHeight - 1;
                int mx0 = Math.Max(rx0, _x0), mx1 = Math.Min(rx1, _x0 + _w - 1);
                int my0 = Math.Max(ry0, _y0), my1 = Math.Min(ry1, _y0 + _h - 1);
                for (int ty = my0; ty <= my1; ty++)
                    for (int tx = mx0; tx <= mx1; tx++)
                        _lab[(ty - _y0) * _w + (tx - _x0)] = DoorLab;
                if (rx0 < _x0 || ry0 < _y0 || rx1 > _x0 + _w - 1 || ry1 > _y0 + _h - 1) continue;      // rect not fully inside the area
                if (dd.switchLink == null || dd.switchLink.Count == 0) continue;                       // nothing can open it
                LioInfo.Switch sw = null;
                int cd = 0;
                foreach (KeyValuePair<int, Arena.SwitchState> sk in dd.switchLink)
                    if (sk.Value != null && sk.Value.Switch != null) { sw = sk.Value.Switch; cd = sk.Value.closeDelay; break; }
                if (sw == null) continue;
                int di = _nDoors++;
                _dKey[di] = kv.Key; _dX0[di] = rx0; _dY0[di] = ry0; _dX1[di] = rx1; _dY1[di] = ry1;
                _dSwitch[di] = sw; _dState[di] = dd;
                _idxOfKey[kv.Key] = di;
                if (cd < minCd) minCd = cd;
                if (cd > maxCd) maxCd = cd;
            }

            // ---- regions: 4-neighbour flood fill over the open tiles (door rects are walls even when open)
            int[] queue = new int[total];
            int next = 1;
            for (int i = 0; i < total; i++)
            {
                if (_lab[i] != 0) continue;
                int head = 0, tail = 0;
                queue[tail++] = i; _lab[i] = next;
                bool touch = false;
                while (head < tail)
                {
                    int c = queue[head++];
                    int cx = c % _w, cy = c / _w;
                    int nb, l;
                    if (cx > 0)
                    {
                        nb = c - 1; l = _lab[nb];
                        if (l == 0) { _lab[nb] = next; queue[tail++] = nb; } else if (l == DoorLab) touch = true;
                    }
                    if (cx < _w - 1)
                    {
                        nb = c + 1; l = _lab[nb];
                        if (l == 0) { _lab[nb] = next; queue[tail++] = nb; } else if (l == DoorLab) touch = true;
                    }
                    if (cy > 0)
                    {
                        nb = c - _w; l = _lab[nb];
                        if (l == 0) { _lab[nb] = next; queue[tail++] = nb; } else if (l == DoorLab) touch = true;
                    }
                    if (cy < _h - 1)
                    {
                        nb = c + _w; l = _lab[nb];
                        if (l == 0) { _lab[nb] = next; queue[tail++] = nb; } else if (l == DoorLab) touch = true;
                    }
                }
                if (tail < MinRegionTiles && !touch)
                    for (int k = 0; k < tail; k++) _lab[queue[k]] = SmallLab;       // a tiny pocket no door touches: ignored
                else
                    next++;
            }
            queue = null;
            Regions = next - 1;

            // ---- edges: for every pair of regions that touch the same door rect
            List<EdgeRec> edges = new List<EdgeRec>();
            for (int di = 0; di < _nDoors; di++)
            {
                int bw = _dX1[di] - _dX0[di] + 1, bh = _dY1[di] - _dY0[di] + 1;
                int[] cTx = new int[2 * (bw + bh) + 4], cTy = new int[cTx.Length], cReg = new int[cTx.Length];
                int nc = 0;
                for (int y = _dY0[di]; y <= _dY1[di]; y++)
                {
                    AddCand(_dX0[di] - 1, y, cTx, cTy, cReg, ref nc);
                    AddCand(_dX1[di] + 1, y, cTx, cTy, cReg, ref nc);
                }
                for (int x = _dX0[di]; x <= _dX1[di]; x++)
                {
                    AddCand(x, _dY0[di] - 1, cTx, cTy, cReg, ref nc);
                    AddCand(x, _dY1[di] + 1, cTx, cTy, cReg, ref nc);
                }
                int[] regs = new int[6];
                int nr = 0;
                for (int i = 0; i < nc; i++)
                {
                    bool have = false;
                    for (int j = 0; j < nr; j++) if (regs[j] == cReg[i]) { have = true; break; }
                    if (!have && nr < regs.Length) regs[nr++] = cReg[i];
                }
                if (nr < 2) continue;
                bool any = false;
                for (int i = 0; i < nr; i++)
                    for (int j = i + 1; j < nr; j++)
                    {
                        int ax, ay, bx, by;
                        if (!PickStands(di, regs[i], regs[j], cTx, cTy, cReg, nc, out ax, out ay, out bx, out by)) continue;
                        EdgeRec er = new EdgeRec();
                        er.Ra = regs[i]; er.Rb = regs[j]; er.Door = di; er.Ax = ax; er.Ay = ay; er.Bx = bx; er.By = by;
                        edges.Add(er);
                        any = true;
                    }
                if (any) UsableDoors++;
            }
            Edges = edges.Count;
            if (Edges == 0)
                return;

            // ---- graph arrays
            _eRa = new int[Edges]; _eRb = new int[Edges]; _eDoor = new int[Edges];
            _eAx = new int[Edges]; _eAy = new int[Edges]; _eBx = new int[Edges]; _eBy = new int[Edges];
            _nodeOf = new int[Regions + 1];
            for (int i = 0; i < _nodeOf.Length; i++) _nodeOf[i] = -1;
            List<int> nodeRegion = new List<int>();
            for (int e = 0; e < Edges; e++)
            {
                EdgeRec er = edges[e];
                _eRa[e] = er.Ra; _eRb[e] = er.Rb; _eDoor[e] = er.Door;
                _eAx[e] = er.Ax; _eAy[e] = er.Ay; _eBx[e] = er.Bx; _eBy[e] = er.By;
                if (_nodeOf[er.Ra] < 0) { _nodeOf[er.Ra] = nodeRegion.Count; nodeRegion.Add(er.Ra); }
                if (_nodeOf[er.Rb] < 0) { _nodeOf[er.Rb] = nodeRegion.Count; nodeRegion.Add(er.Rb); }
            }
            _nodeRegion = nodeRegion.ToArray();
            int kn = _nodeRegion.Length;
            List<int>[] adjL = new List<int>[kn];
            for (int i = 0; i < kn; i++) adjL[i] = new List<int>();
            for (int e = 0; e < Edges; e++)
            {
                adjL[_nodeOf[_eRa[e]]].Add(e);
                adjL[_nodeOf[_eRb[e]]].Add(e);
            }
            _adj = new int[kn][];
            for (int i = 0; i < kn; i++) _adj[i] = adjL[i].ToArray();
            _comp = new int[kn];
            for (int i = 0; i < kn; i++) _comp[i] = -1;
            int[] stack = new int[kn];
            int ncomp = 0;
            for (int i = 0; i < kn; i++)
            {
                if (_comp[i] >= 0) continue;
                int sp = 0;
                stack[sp++] = i; _comp[i] = ncomp;
                while (sp > 0)
                {
                    int u = stack[--sp];
                    for (int q = 0; q < _adj[u].Length; q++)
                    {
                        int e = _adj[u][q];
                        int v = _nodeOf[_eRa[e]] == u ? _nodeOf[_eRb[e]] : _nodeOf[_eRa[e]];
                        if (_comp[v] < 0) { _comp[v] = ncomp; stack[sp++] = v; }
                    }
                }
                ncomp++;
            }
            _dist = new int[kn]; _prevEdge = new int[kn]; _prevNode = new int[kn]; _entX = new int[kn]; _entY = new int[kn];
            _done = new bool[kn];
            if (UsableDoors > 0 && minCd != int.MaxValue)
                Detail = "[CTFXBots] doors: switch auto close delay min " + minCd + " max " + maxCd + " (raw closeDelay of the first switch of each door)";
            Ready = true;
        }

        void AddCand(int tx, int ty, int[] cTx, int[] cTy, int[] cReg, ref int nc)
        {
            int l = LabAt(tx, ty);
            if (l <= 0) return;
            cTx[nc] = tx; cTy[nc] = ty; cReg[nc] = l; nc++;
        }

        int LabAt(int tx, int ty)
        {
            int lx = tx - _x0, ly = ty - _y0;
            if (lx < 0 || ly < 0 || lx >= _w || ly >= _h) return WallLab;
            return _lab[ly * _w + lx];
        }

        /// <summary>
        /// Stand tiles for the pair (ra, rb) around door di: the tile of each region next to the rect that is closest to the
        /// rect centre, with the line between the two tiles clear once the door is open (lateral offset is penalised so
        /// the two sides line up), then one tile further out on both sides when that still works.
        /// </summary>
        bool PickStands(int di, int ra, int rb, int[] cTx, int[] cTy, int[] cReg, int nc,
                        out int ax, out int ay, out int bx, out int by)
        {
            ax = 0; ay = 0; bx = 0; by = 0;
            double cx = (_dX0[di] + _dX1[di]) / 2.0, cy = (_dY0[di] + _dY1[di]) / 2.0;
            double best = double.MaxValue;
            int ba = -1, bb = -1;
            for (int i = 0; i < nc; i++)
            {
                if (cReg[i] != ra) continue;
                double da = Math.Sqrt((cTx[i] - cx) * (cTx[i] - cx) + (cTy[i] - cy) * (cTy[i] - cy));
                for (int j = 0; j < nc; j++)
                {
                    if (cReg[j] != rb) continue;
                    double db = Math.Sqrt((cTx[j] - cx) * (cTx[j] - cx) + (cTy[j] - cy) * (cTy[j] - cy));
                    int ddx = Math.Abs(cTx[i] - cTx[j]), ddy = Math.Abs(cTy[i] - cTy[j]);
                    double cost = da + db + 0.3 * Math.Min(ddx, ddy);
                    if (cost >= best) continue;
                    if (!SegClear(cTx[i], cTy[i], cTx[j], cTy[j], di)) continue;
                    best = cost; ba = i; bb = j;
                }
            }
            if (ba < 0) return false;
            ax = cTx[ba]; ay = cTy[ba]; bx = cTx[bb]; by = cTy[bb];
            int oax, oay, obx, oby;
            Outward(di, ax, ay, out oax, out oay);
            Outward(di, bx, by, out obx, out oby);
            int nax = ax + oax, nay = ay + oay, nbx = bx + obx, nby = by + oby;
            if (LabAt(nax, nay) == ra && LabAt(nbx, nby) == rb && SegClear(nax, nay, nbx, nby, di))
            {
                ax = nax; ay = nay; bx = nbx; by = nby;
            }
            return true;
        }

        /// <summary>Unit step away from the rect for a tile next to it.</summary>
        void Outward(int di, int tx, int ty, out int ox, out int oy)
        {
            ox = 0; oy = 0;
            if (tx < _dX0[di]) ox = -1;
            else if (tx > _dX1[di]) ox = 1;
            else if (ty < _dY0[di]) oy = -1;
            else oy = 1;
        }

        /// <summary>The straight line between two tile centres only crosses open tiles or the rect of door di (assumed open).</summary>
        bool SegClear(int atx, int aty, int btx, int bty, int di)
        {
            double x0 = atx * 16 + 8, y0 = aty * 16 + 8, dx = (btx - atx) * 16, dy = (bty - aty) * 16;
            double len = Math.Sqrt(dx * dx + dy * dy);
            int n = (int)(len / 4.0) + 1;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                int tx = (int)((x0 + dx * t) / 16.0), ty = (int)((y0 + dy * t) / 16.0);
                if (tx >= _dX0[di] && tx <= _dX1[di] && ty >= _dY0[di] && ty <= _dY1[di]) continue;
                int l = LabAt(tx, ty);
                if (l <= 0 && l != SmallLab) return false;
            }
            return true;
        }

        // ================================================================ runtime
        /// <summary>Region id of a tile: 0 = outside the area, a wall, a door rect or an ignored pocket.</summary>
        internal int RegionOf(int tx, int ty)
        {
            int lx = tx - _x0, ly = ty - _y0;
            if (_lab == null || lx < 0 || ly < 0 || lx >= _w || ly >= _h) return 0;
            int l = _lab[ly * _w + lx];
            return l > 0 ? l : 0;
        }

        /// <summary>True when the tiles are in different labelled regions that the doors can join (a door plan is the way).</summary>
        internal bool Separated(int atx, int aty, int btx, int bty)
        {
            if (!Ready) return false;
            int a = RegionOf(atx, aty), b = RegionOf(btx, bty);
            if (a <= 0 || b <= 0 || a == b) return false;
            int na = _nodeOf[a], nb = _nodeOf[b];
            return na >= 0 && nb >= 0 && _comp[na] == _comp[nb];
        }

        static int Len(int x0, int y0, int x1, int y1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            return (int)Math.Sqrt(dx * dx + dy * dy);
        }

        static bool IsBlack(Dictionary<int, int> black, int doorKey, int now)
        {
            int until;
            return black != null && black.TryGetValue(doorKey, out until) && now - until < 0;
        }

        /// <summary>
        /// Cheapest door sequence from region fromRegion (the bot stands at px, py) to region toRegion (goal tile gtx, gty):
        /// walking distance between the stand tiles plus a penalty per door, doors blacklisted for the bot skipped.
        /// False = no route (steps empty).
        /// </summary>
        internal bool PlanDoors(int fromRegion, int toRegion, int px, int py, int gtx, int gty,
                                List<CTFXDoorStep> steps, Dictionary<int, int> black, int now)
        {
            steps.Clear();
            if (!Ready || fromRegion <= 0 || toRegion <= 0 || fromRegion == toRegion) return false;
            if (fromRegion >= _nodeOf.Length || toRegion >= _nodeOf.Length) return false;
            int fromN = _nodeOf[fromRegion], toN = _nodeOf[toRegion];
            if (fromN < 0 || toN < 0 || _comp[fromN] != _comp[toN]) return false;
            const int Inf = int.MaxValue / 2;
            int k = _nodeRegion.Length;
            for (int i = 0; i < k; i++) { _dist[i] = Inf; _done[i] = false; _prevEdge[i] = -1; _prevNode[i] = -1; }
            _dist[fromN] = 0; _entX[fromN] = px; _entY[fromN] = py;
            int goalX = gtx * 16 + 8, goalY = gty * 16 + 8;
            int bestTotal = Inf, bestEdge = -1, bestNode = -1;
            for (;;)
            {
                int u = -1, ud = Inf;
                for (int i = 0; i < k; i++)
                    if (!_done[i] && _dist[i] < ud) { ud = _dist[i]; u = i; }
                if (u < 0 || ud >= bestTotal) break;
                _done[u] = true;
                int[] ea = _adj[u];
                for (int q = 0; q < ea.Length; q++)
                {
                    int e = ea[q];
                    if (IsBlack(black, _dKey[_eDoor[e]], now)) continue;
                    bool fwd = _nodeOf[_eRa[e]] == u;
                    int v = _nodeOf[fwd ? _eRb[e] : _eRa[e]];
                    int nx = (fwd ? _eAx[e] : _eBx[e]) * 16 + 8, ny = (fwd ? _eAy[e] : _eBy[e]) * 16 + 8;
                    int fx = (fwd ? _eBx[e] : _eAx[e]) * 16 + 8, fy = (fwd ? _eBy[e] : _eAy[e]) * 16 + 8;
                    int nc = _dist[u] + Len(_entX[u], _entY[u], nx, ny) + DoorPenalty;
                    if (v == toN)
                    {
                        int total = nc + Len(fx, fy, goalX, goalY);
                        if (total < bestTotal) { bestTotal = total; bestEdge = e; bestNode = u; }
                    }
                    else if (!_done[v] && nc < _dist[v])
                    {
                        _dist[v] = nc; _prevEdge[v] = e; _prevNode[v] = u; _entX[v] = fx; _entY[v] = fy;
                    }
                }
            }
            if (bestEdge < 0) return false;
            AddStep(steps, bestEdge, bestNode);
            int cur = bestNode, guard = 0;
            while (cur != fromN && guard++ < 64)
            {
                AddStep(steps, _prevEdge[cur], _prevNode[cur]);
                cur = _prevNode[cur];
            }
            steps.Reverse();
            return steps.Count > 0 && cur == fromN;
        }

        void AddStep(List<CTFXDoorStep> steps, int e, int fromNode)
        {
            bool fwd = _nodeOf[_eRa[e]] == fromNode;
            CTFXDoorStep s;
            s.DoorKey = _dKey[_eDoor[e]];
            s.NearRegion = fwd ? _eRa[e] : _eRb[e];
            s.FarRegion = fwd ? _eRb[e] : _eRa[e];
            s.NearTx = fwd ? _eAx[e] : _eBx[e]; s.NearTy = fwd ? _eAy[e] : _eBy[e];
            s.FarTx = fwd ? _eBx[e] : _eAx[e]; s.FarTy = fwd ? _eBy[e] : _eAy[e];
            steps.Add(s);
        }

        /// <summary>True when the door can be walked through now (every rect tile open, or the engine says it is open).</summary>
        internal bool IsOpen(int doorKey)
        {
            int di;
            if (!_idxOfKey.TryGetValue(doorKey, out di)) return true;
            return IsOpenIdx(di);
        }

        bool IsOpenIdx(int di)
        {
            int lw = _arena._levelWidth;
            var tiles = _arena._tiles;
            bool blocked = false;
            for (int ty = _dY0[di]; ty <= _dY1[di] && !blocked; ty++)
                for (int tx = _dX0[di]; tx <= _dX1[di]; tx++)
                    if (tiles[ty * lw + tx].Blocked) { blocked = true; break; }
            return !blocked || _dState[di].Opened;
        }

        /// <summary>Open the door through its first switch if it is shut. True = it can be walked through right now.</summary>
        internal bool OpenDoor(int doorKey)
        {
            return OpenDoor(doorKey, 0);
        }

        /// <summary>attempt 0 and 2 ask the switch to open, attempt 1 asks it to close (an inverse door opens that way, see fact e).</summary>
        internal bool OpenDoor(int doorKey, int attempt)
        {
            int di;
            if (!_idxOfKey.TryGetValue(doorKey, out di)) return true;
            if (IsOpenIdx(di)) return true;
            LioInfo.Switch sw = _dSwitch[di];
            if (sw == null) return false;
            if (SendSwitch(sw, (attempt & 1) == 0, doorKey))
                Opens++;
            return IsOpenIdx(di);
        }

        bool SendSwitch(LioInfo.Switch sw, bool open, int doorKey)
        {
            try
            {
                _arena.switchRequest(true, open, null, sw);
                return true;
            }
            catch (Exception ex1)
            {
                try
                {
                    Player stand = null;
                    foreach (Player p in _arena.Players)
                        if (p != null) { stand = p; break; }
                    if (stand != null)
                    {
                        _arena.switchRequest(true, open, stand, sw);
                        return true;
                    }
                    Note("door " + doorKey + ": switchRequest threw " + ex1.GetType().Name + " " + ex1.Message + " and no player is there to stand in");
                }
                catch (Exception ex2)
                {
                    Note("door " + doorKey + ": switchRequest threw with and without a player: " + ex2.GetType().Name + " " + ex2.Message);
                }
            }
            return false;
        }

        void Note(string msg)
        {
            if (Log != null) Log(msg);
        }

        internal void ResetCounters()
        {
            Opens = 0; Fails = 0;
        }
    }

    /// <summary>
    /// Per-bot door navigation state. Update() is called from the bot PathMove with the goal tile; it answers what the
    /// bot should do this poll. Plan = ordered CTFXDoorStep list between the region of the bot and the region of the goal.
    /// </summary>
    internal sealed class CTFXDoorPilot
    {
        public const int None = 0;        // no door in the way: navigate to the goal as usual
        public const int Approach = 1;    // path (native pathfinder) to TargetTx, TargetTy: the near stand tile of the next door
        public const int Steer = 2;       // steer straight at TargetPx, TargetPy (through the open door)
        public const int Hold = 3;        // stand still (waiting for the door to open)
        public const int Failed = 4;      // the door did not work: the caller picks a new goal

        const int PIdle = 0, PApproach = 1, PWait = 2, PCross = 3;
        const int WaitMaxMs = 3000, CrossMaxMs = 4000, ApproachMaxMs = 60000, BlackMs = 30000;

        public int TargetTx, TargetTy, TargetPx, TargetPy;
        public bool Changed;              // the goal handed to the path code changed: the caller cancels its path (then clears this)

        int _phase, _idx, _planGoalR, _phaseT0, _lastCall, _tries, _reopens;
        int _npFrom, _npTo, _npUntil;     // memo: no plan between these regions until the tick
        List<CTFXDoorStep> _steps;
        Dictionary<int, int> _black;      // door key -> tick until which this bot avoids the door

        public int Update(CTFXDoorNav nav, int now, int px, int py, int gtx, int gty)
        {
            if (nav == null || !nav.Ready) return None;
            if (_phase != PIdle && now - _lastCall > 1200) Drop();          // a gap (combat, other movement): plan again from where we stand
            _lastCall = now;
            int myR = nav.RegionOf(px >> 4, py >> 4);
            int goalR = nav.RegionOf(gtx, gty);
            if (_phase != PIdle)
            {
                if (goalR > 0 && goalR != _planGoalR) Drop();               // the goal moved to another region
                else if (_phase == PApproach && myR > 0 && myR == goalR) Drop();     // already where we wanted to be
            }
            if (_phase == PIdle && !Plan(nav, now, px, py, gtx, gty, myR, goalR)) return None;

            for (int guard = 0; guard < 8; guard++)
            {
                CTFXDoorStep s = _steps[_idx];
                if (_phase == PApproach)
                {
                    if (myR > 0 && myR != s.NearRegion)
                    {
                        if (myR == s.FarRegion) { if (!Advance(now)) return None; continue; }     // already on the far side
                        Drop();                                                                   // somewhere unexpected: plan again
                        if (!Plan(nav, now, px, py, gtx, gty, myR, goalR)) return None;
                        continue;
                    }
                    int ax = s.NearTx * 16 + 8 - px, ay = s.NearTy * 16 + 8 - py;
                    if (ax * ax + ay * ay > 32 * 32)                                              // not at the stand tile yet (2 tiles)
                    {
                        if (now - _phaseT0 > ApproachMaxMs) return Fail(nav, now, s.DoorKey);
                        TargetTx = s.NearTx; TargetTy = s.NearTy;
                        return Approach;
                    }
                    nav.OpenDoor(s.DoorKey, 0);
                    _phase = PWait; _phaseT0 = now; _tries = 1;
                    Changed = true;
                    continue;
                }
                if (_phase == PWait)
                {
                    if (nav.IsOpen(s.DoorKey)) { _phase = PCross; _phaseT0 = now; _reopens = 0; continue; }
                    if (now - _phaseT0 >= WaitMaxMs) return Fail(nav, now, s.DoorKey);
                    if (_tries < 3 && now - _phaseT0 >= _tries * 1000) { nav.OpenDoor(s.DoorKey, _tries); _tries++; }
                    return Hold;
                }
                int fx = s.FarTx * 16 + 8, fy = s.FarTy * 16 + 8;                                 // PCross
                int dx = fx - px, dy = fy - py;
                if (dx * dx + dy * dy <= 28 * 28) { if (!Advance(now)) return None; continue; }
                if (now - _phaseT0 > CrossMaxMs) return Fail(nav, now, s.DoorKey);
                if (_reopens < 3 && myR != s.FarRegion && !nav.IsOpen(s.DoorKey))                 // the engine closed it again on us
                {
                    _reopens++;
                    nav.OpenDoor(s.DoorKey, 0);
                }
                // line up on the centre line of the passage first (the near stand is on it), then straight through
                int nx = s.NearTx * 16 + 8, ny = s.NearTy * 16 + 8;
                double lx = fx - nx, ly = fy - ny, ll = lx * lx + ly * ly;
                if (ll > 0.0)
                {
                    double along = ((px - nx) * lx + (py - ny) * ly) / ll;
                    double lateral = Math.Abs((px - nx) * ly - (py - ny) * lx) / Math.Sqrt(ll);
                    if (along < 0.1 && lateral > 7.0) { TargetPx = nx; TargetPy = ny; return Steer; }
                }
                TargetPx = fx; TargetPy = fy;
                return Steer;
            }
            return None;
        }

        bool Plan(CTFXDoorNav nav, int now, int px, int py, int gtx, int gty, int myR, int goalR)
        {
            if (myR <= 0 || goalR <= 0 || myR == goalR) return false;
            if (now - _npUntil < 0 && _npFrom == myR && _npTo == goalR) return false;
            if (_steps == null) _steps = new List<CTFXDoorStep>();
            if (!nav.PlanDoors(myR, goalR, px, py, gtx, gty, _steps, _black, now))
            {
                _steps.Clear();
                _npFrom = myR; _npTo = goalR; _npUntil = now + 1500;
                return false;
            }
            _idx = 0; _phase = PApproach; _phaseT0 = now; _planGoalR = goalR;
            Changed = true;
            return true;
        }

        /// <summary>The step is done: next step (true) or the plan is finished (false, back to normal pathing).</summary>
        bool Advance(int now)
        {
            _idx++;
            Changed = true;
            if (_idx >= _steps.Count) { Drop(); return false; }
            _phase = PApproach; _phaseT0 = now;
            return true;
        }

        void Drop()
        {
            _phase = PIdle; _idx = 0;
            if (_steps != null) _steps.Clear();
            Changed = true;
        }

        int Fail(CTFXDoorNav nav, int now, int doorKey)
        {
            nav.Fails++;
            if (_black == null) _black = new Dictionary<int, int>();
            else if (_black.Count > 16) PurgeBlack(now);
            _black[doorKey] = now + BlackMs;
            Drop();
            return Failed;
        }

        void PurgeBlack(int now)
        {
            List<int> dead = new List<int>();
            foreach (KeyValuePair<int, int> kv in _black)
                if (now - kv.Value >= 0) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) _black.Remove(dead[i]);
        }
    }
}
/* PARSER GUARD - keep last */
