using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Axiom.Math;
using Assets;
using InfServer.Game;
using static Assets.LvlInfo;

namespace InfServer.Bots.NavMesh
{
    /// <summary>
    /// Navmesh-based pathfinder using triangle graph search and funnel smoothing.
    /// Builds separate meshes for raw and clearance-2 walkability tiers.
    /// </summary>
    public class NavMeshPathfinder : IPathfinder
    {
        private const int DefaultClearance = 3;

        private readonly LogClient _logger;
        private readonly LvlInfo _lvlInfo;

        private NavMeshData _clearanceMesh;
        private NavMeshData _rawMesh;
        private bool[] _clearanceMask;
        private bool[] _rawMask;

        private readonly object _meshLock = new object();
        private volatile bool _needsRebuild;

        private Thread _pathingThread;
        readonly PathfinderWorkQueue _workQueue;

        public NavMeshPathfinder(ZoneServer server, LogClient logger)
        {
            _logger = logger;
            _lvlInfo = server._assets.Level;
            // Defense packs request many routes; keep headroom so combat chase is not starved.
            _workQueue = new PathfinderWorkQueue(200, softDropThreshold: 160, logName: "NavMesh");

            _rawMask = NavMeshBuilder.BuildWalkableMask(_lvlInfo, 0);
            _clearanceMask = NavMeshBuilder.BuildWalkableMask(_lvlInfo, DefaultClearance);

            _rawMesh = NavMeshBuilder.Build(_lvlInfo, _rawMask);
            _clearanceMesh = NavMeshBuilder.Build(_lvlInfo, _clearanceMask);

            Log.write(TLog.Normal,
                "NavMesh built: raw={0} tris, clearance={1} tris (map {2}x{3})",
                _rawMesh.Triangles.Count,
                _clearanceMesh.Triangles.Count,
                _lvlInfo.Width,
                _lvlInfo.Height);
        }

        public void beginThread()
        {
            Log.assume(_logger);

            _pathingThread = new Thread(pathfinder);
            _pathingThread.IsBackground = true;
            _pathingThread.Name = "NavMeshPathfinding";
            _pathingThread.Start();
        }

        private void pathfinder(object obj)
        {
            _workQueue.RunWorker(
                isStillNeeded: null,
                process: req =>
                {
                    List<Vector3>? path = null;
                    int pathLength = 0;

                    if (calculatePath(req.startX, req.startY, req.endX, req.endY, out path, out pathLength))
                        req.callback?.Invoke(path, pathLength);
                    else
                        req.callback?.Invoke(null, 0);
                });
        }

        public void queueRequest(short startX, short startY, short endX, short endY, Action<List<Vector3>, int> callback)
        {
            queueRequest(startX, startY, endX, endY, callback, 0, PathPriority.Idle);
        }

        public void queueRequest(
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> callback,
            int botId,
            PathPriority priority)
        {
            queueRequest(startX, startY, endX, endY, callback, botId, priority, null);
        }

        public void queueRequest(
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> callback,
            int botId,
            PathPriority priority,
            Func<bool> stillNeeded)
        {
            // Rebuild check happens on the worker before each solve.
            _workQueue.Enqueue(startX, startY, endX, endY, callback, botId, priority, stillNeeded);
        }

        private bool calculatePath(short startX, short startY, short endX, short endY, out List<Vector3>? path, out int pathLength)
        {
            if (_needsRebuild)
                RebuildMeshes();

            path = null;
            pathLength = 0;

            try
            {
                NavMeshData clearanceMesh;
                NavMeshData rawMesh;
                lock (_meshLock)
                {
                    clearanceMesh = _clearanceMesh;
                    rawMesh = _rawMesh;
                }

                path = FindPathOnMesh(clearanceMesh, _clearanceMask, startX, startY, endX, endY);
                if (path == null)
                    path = FindPathOnMesh(rawMesh, _rawMask, startX, startY, endX, endY);

                if (path == null || path.Count == 0)
                    return false;

                pathLength = path.Count;
                return true;
            }
            catch (Exception e)
            {
                Log.write(TLog.Exception, "Error while navmesh pathfinding: " + e);
                return false;
            }
        }

        private List<Vector3>? FindPathOnMesh(
            NavMeshData mesh,
            bool[] walkableMask,
            short startX,
            short startY,
            short endX,
            short endY)
        {
            int startTri = mesh.FindNearestTriangle(startX, startY);
            int endTri = mesh.FindNearestTriangle(endX, endY);

            if (startTri < 0 || endTri < 0)
                return null;

            var triPath = mesh.FindTrianglePath(startTri, endTri);
            if (triPath == null)
                return null;

            NavVec2 start = mesh.TileToNavPoint(startX, startY);
            NavVec2 end = mesh.TileToNavPoint(endX, endY);

            var path = Funnel.BuildPath(start, end, mesh.Triangles, mesh.Vertices, triPath);
            return PathSmoothing.Prepare(path, walkableMask, _lvlInfo.Width, _lvlInfo.Height);
        }

        public int queueCount()
        {
            return _workQueue.Count;
        }

        /// <summary>
        /// Drain pending navmesh requests as failures so bots can re-pick instead of waiting forever.
        /// </summary>
        public int clearQueue()
        {
            int dropped = _workQueue.ClearAll();
            if (dropped > 0)
                Log.write(TLog.Warning, "Cleared {0} pending navmesh path request(s).", dropped);

            return dropped;
        }

        public void updateTile(int tileX, int tileY, Tile tile)
        {
            if (tileX < 0 || tileY < 0 || tileX >= _lvlInfo.Width || tileY >= _lvlInfo.Height)
                return;

            int idx = tileY * _lvlInfo.Width + tileX;
            _rawMask[idx] = !tile.Blocked;
            _needsRebuild = true;
        }

        private void RebuildMeshes()
        {
            lock (_meshLock)
            {
                _clearanceMask = NavMeshBuilder.BuildWalkableMask(_lvlInfo, DefaultClearance, _rawMask);
                _rawMesh = NavMeshBuilder.Build(_lvlInfo, _rawMask);
                _clearanceMesh = NavMeshBuilder.Build(_lvlInfo, _clearanceMask);
                _needsRebuild = false;
            }
        }
    }
}
