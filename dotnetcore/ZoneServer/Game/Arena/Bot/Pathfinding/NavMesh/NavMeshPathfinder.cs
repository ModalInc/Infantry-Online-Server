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

        // ---- hover routes (2026-11-15): boards / drop packs fly over physics no taller than their LowZ (green), so
        // their routes come from a mesh where that physics is open. One mesh pair per LowZ, built on first use (on the
        // pathing thread) and dropped when tiles change. Bots tell us their LowZ as they ask for a route.
        static readonly ConcurrentDictionary<int, short> _botLowZ = new ConcurrentDictionary<int, short>();

        /// <summary>Called before a bot's path request: its vehicle's LowZ (0 = walks - the normal mesh).</summary>
        public static void NoteBotLowZ(int botId, short lowZ)
        {
            if (lowZ > 0)
                _botLowZ[botId] = lowZ;
            else
                _botLowZ.TryRemove(botId, out _);
        }

        sealed class HoverMeshes
        {
            public bool[] RawMask, ClearanceMask;
            public NavMeshData RawMesh, ClearanceMesh;
        }
        readonly Dictionary<short, HoverMeshes> _hover = new Dictionary<short, HoverMeshes>();   // (pathing thread only)

        HoverMeshes GetHoverMeshes(short lowZ)
        {
            HoverMeshes hm;
            if (_hover.TryGetValue(lowZ, out hm))
                return hm;
            short[] high = _lvlInfo.PhysicsHigh, low = _lvlInfo.PhysicsLow;
            bool[] raw;
            lock (_meshLock)
                raw = (bool[])_rawMask.Clone();
            bool any = false;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i])
                    continue;
                Tile t = _lvlInfo.Tiles[i];
                if (t.Blocked && t.Physics < high.Length && low[t.Physics] == 0 && high[t.Physics] <= lowZ)
                {
                    raw[i] = true;
                    any = true;
                }
            }
            if (!any)
            {
                _hover[lowZ] = null;   // nothing this vehicle can fly over: the normal meshes
                return null;
            }
            bool[] clear = NavMeshBuilder.BuildWalkableMask(_lvlInfo, DefaultClearance, raw);
            hm = new HoverMeshes { RawMask = raw, ClearanceMask = clear, RawMesh = NavMeshBuilder.Build(_lvlInfo, raw), ClearanceMesh = NavMeshBuilder.Build(_lvlInfo, clear) };
            _hover[lowZ] = hm;
            Log.write(TLog.Normal, "NavMesh (hover, LowZ {0}) built: raw={1} tris, clearance={2} tris", lowZ, hm.RawMesh.Triangles.Count, hm.ClearanceMesh.Triangles.Count);
            return hm;
        }

        private void pathfinder(object obj)
        {
            _workQueue.RunWorker(
                isStillNeeded: null,
                process: req =>
                {
                    List<Vector3>? path = null;
                    int pathLength = 0;

                    if (_needsRebuild)
                        RebuildMeshes();   // (tiles changed: normal meshes now, hover ones on next use)
                    short lowZ;
                    HoverMeshes hover = _botLowZ.TryGetValue(req.botId, out lowZ) ? GetHoverMeshes(lowZ) : null;
                    if (hover != null ? calculateHoverPath(hover, req.startX, req.startY, req.endX, req.endY, out path, out pathLength)
                        : calculatePath(req.startX, req.startY, req.endX, req.endY, out path, out pathLength))
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

        /// <summary>A route for a hover vehicle (its own meshes; tiles changed since: rebuilt first).</summary>
        private bool calculateHoverPath(HoverMeshes hover, short startX, short startY, short endX, short endY, out List<Vector3>? path, out int pathLength)
        {
            path = null;
            pathLength = 0;
            try
            {
                path = FindPathOnMesh(hover.ClearanceMesh, hover.ClearanceMask, startX, startY, endX, endY);
                if (path == null)
                    path = FindPathOnMesh(hover.RawMesh, hover.RawMask, startX, startY, endX, endY);
                if (path == null || path.Count == 0)
                    return false;
                pathLength = path.Count;
                return true;
            }
            catch (Exception e)
            {
                Log.write(TLog.Exception, "Error while navmesh (hover) pathfinding: " + e);
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
            _hover.Clear();   // (hover meshes come from the raw mask - rebuilt on next use)
        }
    }
}
