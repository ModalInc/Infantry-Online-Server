using System;

namespace InfServer.Bots.NavMesh
{
    /// <summary>
    /// 2D vector in tile space for navmesh geometry.
    /// </summary>
    public readonly struct NavVec2
    {
        public readonly float X;
        public readonly float Y;

        public NavVec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float DistanceSquared(NavVec2 other)
        {
            float dx = X - other.X;
            float dy = Y - other.Y;
            return dx * dx + dy * dy;
        }

        public float Distance(NavVec2 other)
        {
            return MathF.Sqrt(DistanceSquared(other));
        }

        public static NavVec2 operator +(NavVec2 a, NavVec2 b) => new NavVec2(a.X + b.X, a.Y + b.Y);
        public static NavVec2 operator -(NavVec2 a, NavVec2 b) => new NavVec2(a.X - b.X, a.Y - b.Y);
        public static NavVec2 operator *(NavVec2 a, float s) => new NavVec2(a.X * s, a.Y * s);
    }
}
