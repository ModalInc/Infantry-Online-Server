using System;
using System.Collections.Generic;
using Axiom.Math;

namespace InfServer.Bots.NavMesh
{
    /// <summary>
    /// Simple Stupid Funnel algorithm for smoothing a polygon path through portals.
    /// </summary>
    public static class Funnel
    {
        private struct Portal
        {
            public NavVec2 Left;
            public NavVec2 Right;
        }

        /// <summary>
        /// Converts a triangle path into steering-space waypoints using funnel string pulling.
        /// </summary>
        public static List<Vector3> BuildPath(
            NavVec2 start,
            NavVec2 end,
            IReadOnlyList<NavTriangle> triangles,
            IReadOnlyList<NavVec2> vertices,
            IReadOnlyList<int> trianglePath)
        {
            var result = new List<Vector3>();

            if (trianglePath == null || trianglePath.Count == 0)
            {
                result.Add(ToSteering(start));
                result.Add(ToSteering(end));
                return result;
            }

            if (trianglePath.Count == 1)
            {
                result.Add(ToSteering(start));
                result.Add(ToSteering(end));
                return result;
            }

            var portals = new List<Portal>(trianglePath.Count);
            portals.Add(new Portal { Left = start, Right = start });

            for (int i = 0; i < trianglePath.Count - 1; i++)
            {
                if (!TryGetPortal(triangles, vertices, trianglePath[i], trianglePath[i + 1], out NavVec2 left, out NavVec2 right))
                {
                    var tri = triangles[trianglePath[i]];
                    left = vertices[tri.V0];
                    right = vertices[tri.V0];
                }

                portals.Add(new Portal { Left = left, Right = right });
            }

            portals.Add(new Portal { Left = end, Right = end });

            var smoothed = StringPull(portals);
            if (smoothed.Count == 0)
            {
                result.Add(ToSteering(start));
                result.Add(ToSteering(end));
                return result;
            }

            foreach (var point in smoothed)
                result.Add(ToSteering(point));

            if (result[^1].Distance(ToSteering(end)) > 0.05f)
                result.Add(ToSteering(end));

            return result;
        }

        private static List<NavVec2> StringPull(List<Portal> portals)
        {
            var path = new List<NavVec2>();

            if (portals.Count < 2)
                return path;

            NavVec2 portalApex = portals[0].Left;
            NavVec2 portalLeft = portals[0].Left;
            NavVec2 portalRight = portals[0].Right;
            int apexIndex = 0;
            int leftIndex = 0;
            int rightIndex = 0;

            path.Add(portalApex);

            for (int i = 1; i < portals.Count; i++)
            {
                NavVec2 newLeft = portals[i].Left;
                NavVec2 newRight = portals[i].Right;

                if (TriArea2(portalApex, portalRight, newRight) <= 0)
                {
                    if (Approximately(portalApex, portalRight) || TriArea2(portalApex, portalLeft, newRight) > 0)
                    {
                        portalRight = newRight;
                        rightIndex = i;
                    }
                    else
                    {
                        path.Add(portalLeft);
                        portalApex = portalLeft;
                        apexIndex = leftIndex;
                        portalLeft = portalApex;
                        portalRight = portalApex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }

                if (TriArea2(portalApex, portalLeft, newLeft) >= 0)
                {
                    if (Approximately(portalApex, portalLeft) || TriArea2(portalApex, portalRight, newLeft) < 0)
                    {
                        portalLeft = newLeft;
                        leftIndex = i;
                    }
                    else
                    {
                        path.Add(portalRight);
                        portalApex = portalRight;
                        apexIndex = rightIndex;
                        portalLeft = portalApex;
                        portalRight = portalApex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }
            }

            if (path.Count == 0 || path[^1].Distance(portals[^1].Left) > 0.001f)
                path.Add(portals[^1].Left);

            return path;
        }

        private static bool TryGetPortal(
            IReadOnlyList<NavTriangle> triangles,
            IReadOnlyList<NavVec2> vertices,
            int triAIndex,
            int triBIndex,
            out NavVec2 left,
            out NavVec2 right)
        {
            var triA = triangles[triAIndex];
            var triB = triangles[triBIndex];

            int shared0 = -1, shared1 = -1;

            int[] aVerts = { triA.V0, triA.V1, triA.V2 };
            int[] bVerts = { triB.V0, triB.V1, triB.V2 };

            foreach (int av in aVerts)
            {
                foreach (int bv in bVerts)
                {
                    if (av != bv)
                        continue;

                    if (shared0 < 0)
                        shared0 = av;
                    else
                        shared1 = av;
                }
            }

            if (shared0 < 0 || shared1 < 0)
            {
                left = right = vertices[triA.V0];
                return false;
            }

            NavVec2 p0 = vertices[shared0];
            NavVec2 p1 = vertices[shared1];

            NavVec2 centerA = new NavVec2(triA.CentroidX, triA.CentroidY);
            NavVec2 centerB = new NavVec2(triB.CentroidX, triB.CentroidY);
            NavVec2 travelDir = centerB - centerA;

            NavVec2 edgeDir = p1 - p0;
            float cross = travelDir.X * edgeDir.Y - travelDir.Y * edgeDir.X;

            if (cross >= 0)
            {
                left = p0;
                right = p1;
            }
            else
            {
                left = p1;
                right = p0;
            }

            return true;
        }

        private static float TriArea2(NavVec2 a, NavVec2 b, NavVec2 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        }

        private static bool Approximately(NavVec2 a, NavVec2 b)
        {
            return a.DistanceSquared(b) < 0.0001f;
        }

        public static Vector3 ToSteering(NavVec2 tilePoint)
        {
            float x = (tilePoint.X * 16.0f) / 100.0f;
            float y = (tilePoint.Y * 16.0f) / 100.0f;
            return new Vector3(x, y, 0);
        }
    }
}
