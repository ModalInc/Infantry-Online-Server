using Assets;
using Axiom.Math;
using EpPathFinding.cs;
using InfServer.Bots;
using InfServer.Game;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Assets.LvlInfo;

namespace InfServer.Bots
{
    public class Pathfinder2 : IPathfinder
    {
        private LogClient _logger;
        private LvlInfo lvlInfo;

        JumpPointParam clearanceJpParam;
        BaseGrid clearanceGrid;

        BaseGrid rawGrid;
        JumpPointParam rawJpParam;

        private Thread pathingThread;
        readonly PathfinderWorkQueue _workQueue;

        public Pathfinder2(ZoneServer server, LogClient logger)
        {
            _logger = logger;

            lvlInfo = server._assets.Level;
            _workQueue = new PathfinderWorkQueue(80, softDropThreshold: 60, logName: "Pathfinder2");

            var clearances = new bool[lvlInfo.Width][];

            for (int i = 0; i < lvlInfo.Width; ++i)
            {
                clearances[i] = new bool[lvlInfo.Height];

                for (int j = 0; j < lvlInfo.Height; ++j)
                {
                    clearances[i][j] = !lvlInfo.Tiles[(j * lvlInfo.Width) + i].Blocked;
                }
            }

            rawGrid = new StaticGrid(lvlInfo.Width, lvlInfo.Height, clearances);
            rawJpParam = new JumpPointParam(rawGrid, EndNodeUnWalkableTreatment.DISALLOW, DiagonalMovement.Always);

            // Give a 2-tile "clearance" around blocked tiles for larger vehicles.

            int clearance = 2;

            for (int i = 0; i < lvlInfo.Height; ++i)
            {
                for (int j = 0; j < lvlInfo.Width; ++j)
                {
                    if (lvlInfo.Tiles[(i * lvlInfo.Width) + j].Blocked)
                    {   //Block tiles around it for clearance
                        int yMax = Math.Min(lvlInfo.Height - 1, (i + clearance + 1));
                        int xMax = Math.Min(lvlInfo.Width - 1, (j + clearance + 1));

                        for (int y = Math.Max(0, i - clearance); y < yMax; ++y)
                        {
                            for (int x = Math.Max(0, j - clearance); x < xMax; ++x)
                            {
                                clearances[x][y] = false;
                            }
                        }
                    }
                }
            }

            clearanceGrid = new StaticGrid(lvlInfo.Width, lvlInfo.Height, clearances);
            clearanceJpParam = new JumpPointParam(clearanceGrid, EndNodeUnWalkableTreatment.DISALLOW, DiagonalMovement.Always);
        }

        /// <summary>
        /// Generic Destructor
        /// </summary>
        ~Pathfinder2()
        {
            // FIXME: Time to use cancellation tokens.

            // pathingThread.Abort();
        }

        /// <summary>
        /// Begins the thread used to perform all pathfinding
        /// </summary>
        public void beginThread()
        {
            Log.assume(_logger);

            pathingThread = new Thread(pathfinder);
            pathingThread.IsBackground = true;
            pathingThread.Name = "Pathfinding";
            pathingThread.Start(pathingThread);
        }

        /// <summary>
        /// Begins the thread used to perform all pathfinding
        /// </summary>
        private void pathfinder(Object obj)
        {
            _workQueue.RunWorker(
                isStillNeeded: null,
                process: req =>
                {
                    List<GridPos> path;
                    if (!calculatePath(req.startX, req.startY, req.endX, req.endY, out path))
                    {
                        req.callback?.Invoke(null, 0);
                        return;
                    }

                    req.callback?.Invoke(createSteerablePath(path), path.Count);
                });
        }

        /// <summary>
        /// Queues a pathfinding operation
        /// </summary>
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
            _workQueue.Enqueue(startX, startY, endX, endY, callback, botId, priority, stillNeeded);
        }

        /// <summary>
        /// Calculates a path from start to finish
        /// </summary>
        private bool calculatePath(short startX, short startY, short endX, short endY, out List<GridPos> path)
        {
            var start = new GridPos(startX, startY);
            var end = new GridPos(endX, endY);

            try
            {
                clearanceJpParam.Reset(start, end);

                path = JumpPointFinder.FindPath(clearanceJpParam);

                if (path.Count > 0)
                {
                    return true;
                }
                else
                {
                    rawJpParam.Reset(start, end);

                    path = JumpPointFinder.FindPath(rawJpParam);

                    if (path.Count > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
               // Debug (disabled):
               // Console.WriteLine("Error while pathfinding: " + e.ToString());
            }

            //var startStr = $"Start: ({startX}, {startY})";
            //var endStr = $"End: ({endX}, {endY})";

            //var walkableStart = rawGrid.IsWalkableAt(start) ? "Start Walkable" : "Start not walkable";
            //var walkableEnd = rawGrid.IsWalkableAt(end) ? "End Walkable" : "End not walkable";

            //Console.WriteLine($"No path found for: {startStr} => {endStr} ({walkableStart}, {walkableEnd})");

            path = null;
            return false;
        }

        /// <summary>
        /// Calculates a path from start to finish
        /// </summary>
        private List<Vector3> createSteerablePath(List<GridPos> path)
        {
            if (path.Count == 0)
            {
                return null;
            }     

            List<Vector3> points = new List<Vector3>(path.Count);

            foreach(var p in path)
            {
                //
                // NOTE: "Why is this 100.0f here?"
                //
                //       The `Position` value in Helpers.ObjectState is also divided by 100.0f which is often where
                //       this value is used. We believe the original implementor scaled the value to make it play
                //       nicer with the steering library.
                //

                float x = (p.x * 16.0f) / 100.0f;
                float y = (p.y * 16.0f) / 100.0f;

                points.Add(new Vector3(x, y, 0));
            }

            return points;
        }

        /// <summary>
        /// Counts paths in queuelist
        /// </summary>
        public int queueCount()
        {
            return _workQueue.Count;
        }

        public int clearQueue()
        {
            int dropped = _workQueue.ClearAll();
            if (dropped > 0)
                Log.write(TLog.Warning, "Cleared {0} pending pathfinder2 request(s).", dropped);

            return dropped;
        }

        public void updateTile(int tileX, int tileY, Tile tile)
        {
            rawGrid.SetWalkableAt(tileX, tileY, !tile.Blocked);
            clearanceGrid.SetWalkableAt(tileX, tileY, !tile.Blocked);
        }
    }
}
