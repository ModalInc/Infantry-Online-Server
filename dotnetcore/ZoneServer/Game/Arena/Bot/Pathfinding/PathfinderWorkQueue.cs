using System;
using System.Collections.Concurrent;
using System.Threading;
using Axiom.Math;
using InfServer.Game;

namespace InfServer.Bots
{
    /// <summary>Path request urgency — combat is drained before idle/patrol.</summary>
    public enum PathPriority : byte
    {
        Idle = 0,
        Combat = 1,
    }

    /// <summary>
    /// Dual-priority path request queue with per-bot coalescing.
    /// Keeps a single background worker; combat work is preferred over idle.
    /// At most one non-cancelled pending request per bot id.
    /// </summary>
    public sealed class PathfinderWorkQueue
    {
        readonly BlockingCollection<PathfindReq> _high;
        readonly BlockingCollection<PathfindReq> _low;
        readonly ConcurrentDictionary<int, PathfindReq> _pendingByBot = new ConcurrentDictionary<int, PathfindReq>();
        readonly int _softDropThreshold;
        readonly string _logName;
        int _logCounter = 1;
        volatile bool _completed;

        public PathfinderWorkQueue(int capacity, int softDropThreshold, string logName)
        {
            // Split capacity: combat gets half (at least 20), idle gets the rest.
            int highCap = Math.Max(20, capacity / 2);
            int lowCap = Math.Max(20, capacity - highCap);
            _high = new BlockingCollection<PathfindReq>(highCap);
            _low = new BlockingCollection<PathfindReq>(lowCap);
            _softDropThreshold = softDropThreshold;
            _logName = logName ?? "Pathfinder";
        }

        public int Count => _high.Count + _low.Count;

        /// <summary>
        /// Worker loop: always prefer combat, then idle. Skips cancelled (coalesced) requests
        /// without running pathfinding. Calls <paramref name="process"/> for live work.
        /// </summary>
        public void RunWorker(Func<PathfindReq, bool> isStillNeeded, Action<PathfindReq> process, CancellationToken cancel = default)
        {
            while (!_completed && !cancel.IsCancellationRequested)
            {
                PathfindReq req;
                if (!_high.TryTake(out req, 0))
                {
                    if (!_low.TryTake(out req, 50))
                        continue;
                }

                if (req == null)
                    continue;

                // Coalesce / dead: discard silently — do NOT callback(null).
                // Null callbacks re-arm Pending and fuel requeue storms under combat load.
                if (req.Cancelled
                    || (req.StillNeeded != null && !req.StillNeeded())
                    || (isStillNeeded != null && !isStillNeeded(req)))
                {
                    DiscardCancelled(req);
                    continue;
                }

                try
                {
                    process(req);
                }
                finally
                {
                    ClearPendingSlot(req);
                    req.callback = null;
                }
            }
        }

        public void CompleteAdding()
        {
            _completed = true;
            _high.CompleteAdding();
            _low.CompleteAdding();
        }

        public void Enqueue(
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> callback,
            int botId,
            PathPriority priority,
            Func<bool> stillNeeded = null)
        {
            if (callback == null)
                return;

            var req = new PathfindReq
            {
                startX = startX,
                startY = startY,
                endX = endX,
                endY = endY,
                callback = callback,
                botId = botId,
                priority = priority,
                StillNeeded = stillNeeded,
            };

            // Coalesce: one pending request per bot — cancel the older one (worker will no-op it).
            if (botId != 0)
            {
                if (_pendingByBot.TryGetValue(botId, out PathfindReq existing) && existing != null)
                    existing.Cancelled = true;
                _pendingByBot[botId] = req;
            }

            MakeRoomIfNeeded(priority);

            BlockingCollection<PathfindReq> target = priority == PathPriority.Combat ? _high : _low;
            if (!TryAdd(target, req, 30))
            {
                // Prefer dropping idle work to free a slot.
                DropOne(preferIdle: true);
                if (!TryAdd(target, req, 20))
                {
                    ClearPendingSlot(req);
                    try { callback(null, 0); }
                    catch { /* ignore */ }
                }
            }
        }

        public int ClearAll()
        {
            int dropped = 0;
            PathfindReq req;
            while (_high.TryTake(out req))
            {
                dropped++;
                FailAndClear(req);
            }
            while (_low.TryTake(out req))
            {
                dropped++;
                FailAndClear(req);
            }
            _pendingByBot.Clear();
            return dropped;
        }

        void MakeRoomIfNeeded(PathPriority incoming)
        {
            if (Count <= _softDropThreshold)
                return;

            _logCounter++;
            if (_logCounter >= 20)
            {
                Log.write(TLog.Warning, "Excessive {0} pathing queue count: {1}", _logName, Count);
                _logCounter = 1;
            }

            // Always shed idle first when saturated; only touch combat if idle is empty.
            DropOne(preferIdle: true);
        }

        void DropOne(bool preferIdle)
        {
            PathfindReq dropped;
            if (preferIdle && _low.TryTake(out dropped, TimeSpan.FromMilliseconds(15)))
            {
                FailAndClear(dropped);
                return;
            }
            if (_high.TryTake(out dropped, TimeSpan.FromMilliseconds(15)))
            {
                FailAndClear(dropped);
                return;
            }
            if (_low.TryTake(out dropped, TimeSpan.FromMilliseconds(10)))
                FailAndClear(dropped);
        }

        static bool TryAdd(BlockingCollection<PathfindReq> queue, PathfindReq req, int timeoutMs)
        {
            try
            {
                return queue.TryAdd(req, timeoutMs);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Coalesce supersede or dead-bot skip — drop without notifying the bot.
        /// A newer request (if any) still owns the pending slot and will deliver a result.
        /// </summary>
        void DiscardCancelled(PathfindReq req)
        {
            if (req == null)
                return;
            ClearPendingSlot(req);
            req.callback = null;
        }

        void FailAndClear(PathfindReq req)
        {
            if (req == null)
                return;
            req.Cancelled = true;

            // Soft-drop while a newer request for the same bot is already pending — silent.
            bool superseded = false;
            if (req.botId != 0
                && _pendingByBot.TryGetValue(req.botId, out PathfindReq current)
                && current != null
                && !ReferenceEquals(current, req))
            {
                superseded = true;
            }

            ClearPendingSlot(req);
            if (!superseded)
            {
                try { req.callback?.Invoke(null, 0); }
                catch (Exception ex)
                {
                    Log.write(TLog.Exception, "{0} drop path callback failed: {1}", _logName, ex);
                }
            }
            req.callback = null;
        }

        void ClearPendingSlot(PathfindReq req)
        {
            if (req == null || req.botId == 0)
                return;
            _pendingByBot.TryGetValue(req.botId, out PathfindReq current);
            if (ReferenceEquals(current, req))
                _pendingByBot.TryRemove(req.botId, out _);
        }
    }
}
