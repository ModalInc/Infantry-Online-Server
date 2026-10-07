using System;
using System.Collections.Generic;
using Assets;

namespace InfServer.Bots.NavMesh
{
    /// <summary>
    /// Builds navmesh geometry from a tile walkability mask using greedy rectangle
    /// decomposition (each rectangle becomes two triangles).
    /// </summary>
    public static class NavMeshBuilder
    {
        public static bool[] BuildWalkableMask(LvlInfo level, int clearance)
        {
            int w = level.Width;
            int h = level.Height;
            var walkable = new bool[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    walkable[y * w + x] = !level.Tiles[y * w + x].Blocked;
            }

            return ApplyClearance(w, h, walkable, clearance);
        }

        /// <summary>
        /// Recomputes clearance from an existing raw walkability mask.
        /// </summary>
        public static bool[] BuildWalkableMask(LvlInfo level, int clearance, bool[] rawWalkable)
        {
            return ApplyClearance(level.Width, level.Height, (bool[])rawWalkable.Clone(), clearance);
        }

        private static bool[] ApplyClearance(int w, int h, bool[] walkable, int clearance)
        {
            if (clearance <= 0)
                return walkable;

            var blocked = new bool[w * h];
            for (int i = 0; i < walkable.Length; i++)
                blocked[i] = !walkable[i];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!blocked[y * w + x])
                        continue;

                    int yMax = Math.Min(h - 1, y + clearance + 1);
                    int xMax = Math.Min(w - 1, x + clearance + 1);

                    for (int cy = Math.Max(0, y - clearance); cy < yMax; cy++)
                    {
                        for (int cx = Math.Max(0, x - clearance); cx < xMax; cx++)
                            walkable[cy * w + cx] = false;
                    }
                }
            }

            return walkable;
        }

        public static NavMeshData Build(LvlInfo level, bool[] walkable)
        {
            int w = level.Width;
            int h = level.Height;
            var mesh = new NavMeshData(w, h);

            var visited = new bool[w * h];
            var vertexIndex = new Dictionary<(int x, int y), int>();

            int GetOrAddVertex(int x, int y)
            {
                var key = (x, y);
                if (vertexIndex.TryGetValue(key, out int idx))
                    return idx;

                idx = mesh.Vertices.Count;
                mesh.Vertices.Add(new NavVec2(x, y));
                vertexIndex[key] = idx;
                return idx;
            }

            bool Free(int cx, int cy) => walkable[cy * w + cx] && !visited[cy * w + cx];

            bool RowClear(int startX, int rowY, int width)
            {
                for (int dx = 0; dx < width; dx++)
                    if (!Free(startX + dx, rowY))
                        return false;
                return true;
            }

            bool ColumnClear(int colX, int startY, int height)
            {
                for (int dy = 0; dy < height; dy++)
                    if (!Free(colX, startY + dy))
                        return false;
                return true;
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (!walkable[idx] || visited[idx])
                        continue;

                    // Candidate A: grow width first, then height while the full row stays clear.
                    int widthFirstW = 1;
                    while (x + widthFirstW < w && Free(x + widthFirstW, y))
                        widthFirstW++;

                    int widthFirstH = 1;
                    while (y + widthFirstH < h && RowClear(x, y + widthFirstH, widthFirstW))
                        widthFirstH++;

                    // Candidate B: grow height first, then width while the full column stays clear.
                    int heightFirstH = 1;
                    while (y + heightFirstH < h && Free(x, y + heightFirstH))
                        heightFirstH++;

                    int heightFirstW = 1;
                    while (x + heightFirstW < w && ColumnClear(x + heightFirstW, y, heightFirstH))
                        heightFirstW++;

                    // Keep whichever decomposition covers more area so open ground
                    // collapses into fewer, larger triangles instead of thin strips.
                    int rectW, rectH;
                    if (widthFirstW * widthFirstH >= heightFirstW * heightFirstH)
                    {
                        rectW = widthFirstW;
                        rectH = widthFirstH;
                    }
                    else
                    {
                        rectW = heightFirstW;
                        rectH = heightFirstH;
                    }

                    for (int dy = 0; dy < rectH; dy++)
                    {
                        for (int dx = 0; dx < rectW; dx++)
                            visited[(y + dy) * w + x + dx] = true;
                    }

                    int x1 = x;
                    int y1 = y;
                    int x2 = x + rectW;
                    int y2 = y + rectH;

                    int bl = GetOrAddVertex(x1, y2);
                    int br = GetOrAddVertex(x2, y2);
                    int tr = GetOrAddVertex(x2, y1);
                    int tl = GetOrAddVertex(x1, y1);

                    // Alternate the diagonal in a checkerboard pattern so paths are not
                    // biased along a single diagonal direction across the whole map.
                    int tri0;
                    if (((x1 + y1) & 1) == 0)
                    {
                        tri0 = mesh.AddTriangle(bl, br, tr);
                        mesh.AddTriangle(bl, tr, tl);
                    }
                    else
                    {
                        tri0 = mesh.AddTriangle(tl, bl, br);
                        mesh.AddTriangle(tl, br, tr);
                    }

                    float cx = x + rectW * 0.5f;
                    float cy = y + rectH * 0.5f;

                    for (int dy = 0; dy < rectH; dy++)
                    {
                        for (int dx = 0; dx < rectW; dx++)
                            mesh.TileToTriangle[(y + dy) * w + x + dx] = tri0;
                    }

                    mesh.TileCenters.Add(new NavVec2(cx, cy));
                }
            }

            mesh.BuildAdjacency();
            return mesh;
        }
    }

    /// <summary>
    /// Runtime navmesh with triangle graph search and tile lookup.
    /// </summary>
    public class NavMeshData
    {
        public int Width { get; }
        public int Height { get; }
        public List<NavVec2> Vertices { get; } = new List<NavVec2>();
        public List<NavTriangle> Triangles { get; } = new List<NavTriangle>();
        public int[] TileToTriangle { get; }
        public List<NavVec2> TileCenters { get; } = new List<NavVec2>();

        public NavMeshData(int width, int height)
        {
            Width = width;
            Height = height;
            TileToTriangle = new int[width * height];
            Array.Fill(TileToTriangle, -1);
        }

        public int AddTriangle(int v0, int v1, int v2)
        {
            var tri = new NavTriangle(v0, v1, v2);
            var a = Vertices[v0];
            var b = Vertices[v1];
            var c = Vertices[v2];
            tri.CentroidX = (a.X + b.X + c.X) / 3f;
            tri.CentroidY = (a.Y + b.Y + c.Y) / 3f;

            int index = Triangles.Count;
            Triangles.Add(tri);
            return index;
        }

        public void BuildAdjacency()
        {
            var edgeMap = new Dictionary<long, int>();

            for (int i = 0; i < Triangles.Count; i++)
            {
                var tri = Triangles[i];
                RegisterEdge(edgeMap, tri.V0, tri.V1, i, 0);
                RegisterEdge(edgeMap, tri.V1, tri.V2, i, 1);
                RegisterEdge(edgeMap, tri.V2, tri.V0, i, 2);
            }
        }

        private void RegisterEdge(Dictionary<long, int> edgeMap, int va, int vb, int triIndex, int edgeSlot)
        {
            long key = EdgeKey(va, vb);
            if (edgeMap.TryGetValue(key, out int otherTri))
            {
                var tri = Triangles[triIndex];
                SetNeighbor(ref tri, edgeSlot, otherTri);
                Triangles[triIndex] = tri;

                int otherSlot = FindSharedEdgeSlot(Triangles[otherTri], va, vb);
                if (otherSlot >= 0)
                {
                    var other = Triangles[otherTri];
                    SetNeighbor(ref other, otherSlot, triIndex);
                    Triangles[otherTri] = other;
                }
            }
            else
            {
                edgeMap[key] = triIndex;
            }
        }

        private static int FindSharedEdgeSlot(NavTriangle tri, int va, int vb)
        {
            if (SharesEdge(tri.V0, tri.V1, va, vb)) return 0;
            if (SharesEdge(tri.V1, tri.V2, va, vb)) return 1;
            if (SharesEdge(tri.V2, tri.V0, va, vb)) return 2;
            return -1;
        }

        private static bool SharesEdge(int e0, int e1, int va, int vb)
        {
            return (e0 == va && e1 == vb) || (e0 == vb && e1 == va);
        }

        private static void SetNeighbor(ref NavTriangle tri, int slot, int neighbor)
        {
            switch (slot)
            {
                case 0: tri.N0 = neighbor; break;
                case 1: tri.N1 = neighbor; break;
                case 2: tri.N2 = neighbor; break;
            }
        }

        private static long EdgeKey(int a, int b)
        {
            if (a > b)
                (a, b) = (b, a);
            return ((long)a << 32) | (uint)b;
        }

        public int FindTriangleAt(int tileX, int tileY)
        {
            if (tileX < 0 || tileY < 0 || tileX >= Width || tileY >= Height)
                return -1;

            return TileToTriangle[tileY * Width + tileX];
        }

        public NavVec2 TileToNavPoint(int tileX, int tileY)
        {
            return new NavVec2(tileX + 0.5f, tileY + 0.5f);
        }

        public int FindNearestTriangle(int tileX, int tileY, int maxRadius = 8)
        {
            int direct = FindTriangleAt(tileX, tileY);
            if (direct >= 0)
                return direct;

            float bestDist = float.MaxValue;
            int bestTri = -1;

            for (int r = 1; r <= maxRadius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                            continue;

                        int x = tileX + dx;
                        int y = tileY + dy;
                        int tri = FindTriangleAt(x, y);
                        if (tri < 0)
                            continue;

                        var triData = Triangles[tri];
                        float dist = (triData.CentroidX - tileX) * (triData.CentroidX - tileX)
                                   + (triData.CentroidY - tileY) * (triData.CentroidY - tileY);

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestTri = tri;
                        }
                    }
                }

                if (bestTri >= 0)
                    return bestTri;
            }

            return bestTri;
        }

        public List<int>? FindTrianglePath(int startTri, int endTri)
        {
            if (startTri < 0 || endTri < 0)
                return null;

            if (startTri == endTri)
                return new List<int> { startTri };

            int count = Triangles.Count;
            var gScore = new float[count];
            var fScore = new float[count];
            var cameFrom = new int[count];
            var closed = new bool[count];

            Array.Fill(gScore, float.MaxValue);
            Array.Fill(fScore, float.MaxValue);
            Array.Fill(cameFrom, -1);

            gScore[startTri] = 0;
            fScore[startTri] = Heuristic(startTri, endTri);

            var open = new PriorityQueue<int, float>();
            open.Enqueue(startTri, fScore[startTri]);

            while (open.Count > 0)
            {
                int current = open.Dequeue();
                if (current == endTri)
                    return ReconstructPath(cameFrom, current);

                if (closed[current])
                    continue;

                closed[current] = true;
                var tri = Triangles[current];

                VisitNeighbor(current, tri.N0, endTri, gScore, fScore, cameFrom, closed, open);
                VisitNeighbor(current, tri.N1, endTri, gScore, fScore, cameFrom, closed, open);
                VisitNeighbor(current, tri.N2, endTri, gScore, fScore, cameFrom, closed, open);
            }

            return null;
        }

        private void VisitNeighbor(
            int current,
            int neighbor,
            int endTri,
            float[] gScore,
            float[] fScore,
            int[] cameFrom,
            bool[] closed,
            PriorityQueue<int, float> open)
        {
            if (neighbor < 0 || closed[neighbor])
                return;

            float tentative = gScore[current] + EdgeCost(current, neighbor);
            if (tentative >= gScore[neighbor])
                return;

            cameFrom[neighbor] = current;
            gScore[neighbor] = tentative;
            fScore[neighbor] = tentative + Heuristic(neighbor, endTri);
            open.Enqueue(neighbor, fScore[neighbor]);
        }

        private float Heuristic(int fromTri, int toTri)
        {
            var a = Triangles[fromTri];
            var b = Triangles[toTri];
            float dx = a.CentroidX - b.CentroidX;
            float dy = a.CentroidY - b.CentroidY;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private float EdgeCost(int fromTri, int toTri)
        {
            return Heuristic(fromTri, toTri);
        }

        private static List<int> ReconstructPath(int[] cameFrom, int current)
        {
            var path = new List<int>();
            while (current >= 0)
            {
                path.Add(current);
                current = cameFrom[current];
            }

            path.Reverse();
            return path;
        }
    }
}
