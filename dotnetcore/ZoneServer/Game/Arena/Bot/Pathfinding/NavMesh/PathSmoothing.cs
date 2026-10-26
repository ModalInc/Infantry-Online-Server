using System;
using System.Collections.Generic;
using Axiom.Math;

namespace InfServer.Bots.NavMesh
{
    /// <summary>
    /// Post-processes navmesh paths for smoother bot movement.
    /// </summary>
    public static class PathSmoothing
    {
        private const float SteeringUnitsPerTile = 16.0f / 100.0f;
        private const float MaxSegmentLength = 0.75f;
        private const float CornerBevelDistance = 0.38f;
        private const float WallInsetTiles = 0.55f;
        private const float MinPointSpacing = 0.85f;

        public static List<Vector3> Prepare(
            List<Vector3> path,
            bool[] walkableMask,
            int mapWidth,
            int mapHeight)
        {
            if (path == null || path.Count < 2)
                return path ?? new List<Vector3>();

            var working = RemoveNearDuplicates(path);
            working = InsetFromWalls(working, walkableMask, mapWidth, mapHeight);
            working = BevelCorners(working);
            working = SubdivideSegments(working);
            working = RemoveNearDuplicates(working);

            return working;
        }

        private static List<Vector3> RemoveNearDuplicates(List<Vector3> path)
        {
            var result = new List<Vector3>(path.Count) { path[0] };

            for (int i = 1; i < path.Count; i++)
            {
                if (result[^1].Distance(path[i]) >= MinPointSpacing)
                    result.Add(path[i]);
            }

            if (result.Count == 1 && path.Count > 1)
                result.Add(path[^1]);

            return result;
        }

        private static List<Vector3> InsetFromWalls(
            List<Vector3> path,
            bool[] walkableMask,
            int mapWidth,
            int mapHeight)
        {
            if (walkableMask == null)
                return path;

            var result = new List<Vector3>(path.Count);

            foreach (var point in path)
            {
                float tileX = point.x / SteeringUnitsPerTile;
                float tileY = point.y / SteeringUnitsPerTile;

                float pushX = 0;
                float pushY = 0;

                if (IsBlocked(walkableMask, mapWidth, mapHeight, (int)MathF.Floor(tileX - 1), (int)MathF.Floor(tileY)))
                    pushX += WallInsetTiles;
                if (IsBlocked(walkableMask, mapWidth, mapHeight, (int)MathF.Floor(tileX + 1), (int)MathF.Floor(tileY)))
                    pushX -= WallInsetTiles;
                if (IsBlocked(walkableMask, mapWidth, mapHeight, (int)MathF.Floor(tileX), (int)MathF.Floor(tileY - 1)))
                    pushY += WallInsetTiles;
                if (IsBlocked(walkableMask, mapWidth, mapHeight, (int)MathF.Floor(tileX), (int)MathF.Floor(tileY + 1)))
                    pushY -= WallInsetTiles;

                float length = MathF.Sqrt(pushX * pushX + pushY * pushY);
                if (length > 0.001f)
                {
                    pushX /= length;
                    pushY /= length;
                }

                result.Add(new Vector3(
                    point.x + pushX * SteeringUnitsPerTile,
                    point.y + pushY * SteeringUnitsPerTile,
                    point.z));
            }

            return result;
        }

        private static bool IsBlocked(bool[] mask, int width, int height, int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return true;

            return !mask[y * width + x];
        }

        private static List<Vector3> BevelCorners(List<Vector3> path)
        {
            if (path.Count < 3)
                return path;

            var result = new List<Vector3>(path.Count * 2) { path[0] };

            for (int i = 1; i < path.Count - 1; i++)
            {
                Vector3 prev = path[i - 1];
                Vector3 corner = path[i];
                Vector3 next = path[i + 1];

                Vector3 toPrev = prev - corner;
                Vector3 toNext = next - corner;

                float prevLen = toPrev.Length;
                float nextLen = toNext.Length;

                if (prevLen < 0.001f || nextLen < 0.001f)
                {
                    result.Add(corner);
                    continue;
                }

                toPrev *= 1.0f / prevLen;
                toNext *= 1.0f / nextLen;

                float dot = toPrev.x * toNext.x + toPrev.y * toNext.y;
                if (dot > 0.95f)
                {
                    result.Add(corner);
                    continue;
                }

                float bevel = MathF.Min(CornerBevelDistance, MathF.Min(prevLen, nextLen) * 0.45f);
                result.Add(corner + toPrev * bevel);
                result.Add(corner + toNext * bevel);
            }

            result.Add(path[^1]);
            return result;
        }

        private static List<Vector3> SubdivideSegments(List<Vector3> path)
        {
            var result = new List<Vector3>(path.Count * 2);

            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 a = path[i];
                Vector3 b = path[i + 1];
                result.Add(a);

                float length = a.Distance(b);
                if (length <= MaxSegmentLength)
                    continue;

                int steps = (int)MathF.Ceiling(length / MaxSegmentLength);
                for (int step = 1; step < steps; step++)
                {
                    float t = step / (float)steps;
                    result.Add(new Vector3(
                        a.x + (b.x - a.x) * t,
                        a.y + (b.y - a.y) * t,
                        a.z + (b.z - a.z) * t));
                }
            }

            result.Add(path[^1]);
            return result;
        }
    }
}
