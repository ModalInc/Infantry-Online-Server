using Assets;
using Axiom.Math;
using System;
using System.Collections.Generic;
using static Assets.LvlInfo;

namespace InfServer.Bots
{
    public class PathfindReq
    {
        public short startX;
        public short startY;
        public short endX;
        public short endY;

        public Action<List<Vector3>, int> callback;

        /// <summary>Bot vehicle id for coalesce (0 = no coalesce).</summary>
        public int botId;

        public PathPriority priority;

        /// <summary>Set when a newer request for the same bot replaces this one.</summary>
        public volatile bool Cancelled;

        /// <summary>
        /// Optional liveness check evaluated on the path worker before solving.
        /// Used to skip dead/condemned bots without allocating a path.
        /// </summary>
        public Func<bool> StillNeeded;
    }

    public interface IPathfinder
    {
        void beginThread();

        void queueRequest(short startX, short startY, short endX, short endY, Action<List<Vector3>, int> callback);

        void queueRequest(
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> callback,
            int botId,
            PathPriority priority);

        void queueRequest(
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> callback,
            int botId,
            PathPriority priority,
            Func<bool> stillNeeded);

        void updateTile(int tileX, int tileY, Tile tile);

        int queueCount();

        /// <summary>
        /// Drops all pending path requests and invokes each callback with a null path.
        /// Returns how many requests were discarded.
        /// </summary>
        int clearQueue();
    }
}
