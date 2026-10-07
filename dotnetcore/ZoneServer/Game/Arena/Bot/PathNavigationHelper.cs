using System;
using System.Collections.Generic;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Bots
{
    /// <summary>
    /// Shared helpers for bot path following and stuck recovery.
    /// </summary>
    public static class PathNavigationHelper
    {
        private const float PathRadius = 0.55f;
        private const float PredictionTime = 0.45f;

        /// <summary>
        /// Wraps a pathfinder callback to enable smooth corridor following on the bot.
        /// </summary>
        public static Action<List<Vector3>, int> CreatePathCallback(Bot bot, Action<List<Vector3>, int>? onPath = null)
        {
            return (path, pathLength) =>
            {
                onPath?.Invoke(path, pathLength);

                if (bot._movement is not SteeringController steering)
                    return;

                if (path != null && pathLength > 0)
                {
                    steering.FollowPath = path;
                    steering.FollowPathTarget = pathLength > 1 ? 1 : 0;
                }
                else
                {
                    steering.ClearNavigationPath();
                }
            };
        }

        /// <summary>
        /// Clears an active path when the movement controller reports the bot is stuck.
        /// </summary>
        public static bool InvalidatePathIfStuck(
            MovementController movement,
            ref List<Vector3>? path,
            ref int tickLastPath,
            ref int pathTarget)
        {
            if (movement == null || !movement.TryConsumeStuck())
                return false;

            path = null;
            tickLastPath = 0;
            pathTarget = 0;

            if (movement is SteeringController steering)
                steering.ClearNavigationPath();

            return true;
        }

        /// <summary>
        /// Smooth path following using a polyline corridor instead of point seeking.
        /// </summary>
        public static Vector3 SteerAlongPathSmooth(
            InfantryVehicle vehicle,
            IReadOnlyList<Vector3> path,
            ref int pathTarget)
        {
            if (path == null || path.Count == 0)
                return Vector3.Zero;

            AdvancePathTarget(vehicle, path, ref pathTarget);

            var pathway = new PolylinePathway(path as List<Vector3> ?? new List<Vector3>(path), PathRadius, false);
            Vector3 steer = vehicle.SteerToFollowPath(1, PredictionTime, pathway);

            if (steer.LengthSquared < 0.01f && pathTarget < path.Count)
                steer = vehicle.SteerForSeek(path[pathTarget]);

            return steer;
        }

        /// <summary>
        /// Legacy-style seek steering with a larger arrival radius for smoother transitions.
        /// </summary>
        public static Vector3 SteerAlongPathSeek(
            InfantryVehicle vehicle,
            IReadOnlyList<Vector3> path,
            ref int pathTarget,
            float arrivalRadius = 1.1f)
        {
            if (path == null || path.Count == 0)
                return Vector3.Zero;

            if (pathTarget >= path.Count)
                return Vector3.Zero;

            Vector3 point = path[pathTarget];

            if (pathTarget < path.Count && vehicle.Position.Distance(point) < arrivalRadius)
                point = path[++pathTarget];

            if (pathTarget >= path.Count)
                return Vector3.Zero;

            return vehicle.SteerForSeek(point);
        }

        private static void AdvancePathTarget(
            InfantryVehicle vehicle,
            IReadOnlyList<Vector3> path,
            ref int pathTarget)
        {
            if (pathTarget >= path.Count)
                return;

            while (pathTarget < path.Count - 1 &&
                   vehicle.Position.Distance(path[pathTarget]) < PathRadius)
            {
                pathTarget++;
            }
        }
    }
}
