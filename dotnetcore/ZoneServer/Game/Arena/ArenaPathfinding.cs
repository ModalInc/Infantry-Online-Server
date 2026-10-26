using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Axiom.Math;
using InfServer.Bots;

namespace InfServer.Game
{
    public partial class Arena
    {
        private readonly ConcurrentQueue<BotPathCallback> _botPathCallbacks = new();

        /// <summary>Soft cap on arena-thread path results waiting to be applied.</summary>
        const int MaxBotPathCallbacks = 128;

        private struct BotPathCallback
        {
            public Bot Bot;
            public Action<List<Vector3>, int> Callback;
            public List<Vector3>? Path;
            public int PathLength;
        }

        /// <summary>
        /// Drains pathfinder callbacks onto the arena thread. Call before polling bots.
        /// Bounded per tick so a backed-up path queue cannot stall the arena (and delay kills).
        /// Pass maxPerPoll &lt;= 0 to drain everything (used by ClearBotPathQueue).
        /// </summary>
        public void FlushBotPathCallbacks(int maxPerPoll = 64)
        {
            int processed = 0;
            bool unbounded = maxPerPoll <= 0;

            while ((unbounded || processed < maxPerPoll) && _botPathCallbacks.TryDequeue(out BotPathCallback entry))
            {
                processed++;

                // Dead / condemned bots — discard without applying (avoids holding work on the arena thread).
                if (entry.Bot == null || entry.Bot.IsDead || entry.Bot.bCondemned)
                    continue;

                try
                {
                    entry.Callback?.Invoke(entry.Path, entry.PathLength);
                }
                catch (Exception ex)
                {
                    Log.write(TLog.Exception, "Bot path callback failed: {0}", ex);
                }
            }
        }

        /// <summary>
        /// Queues a path request; the callback runs on the arena thread when the path is ready.
        /// </summary>
        public void QueueBotPathRequest(
            Bot bot,
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> callback)
        {
            QueueBotPathRequest(bot, startX, startY, endX, endY, callback, PathPriority.Idle);
        }

        /// <summary>
        /// Queues a path request with priority. Combat routes drain ahead of idle/patrol.
        /// Per-bot requests coalesce so a bot never stacks multiple pending solves.
        /// </summary>
        public void QueueBotPathRequest(
            Bot bot,
            short startX,
            short startY,
            short endX,
            short endY,
            Action<List<Vector3>, int> callback,
            PathPriority priority)
        {
            if (_pathfinder == null)
            {
                callback?.Invoke(null, 0);
                return;
            }

            if (bot == null || bot.IsDead || bot.bCondemned)
            {
                callback?.Invoke(null, 0);
                return;
            }

            int botId = bot._id;
            Func<bool> stillNeeded = () => bot != null && !bot.IsDead && !bot.bCondemned;

            _pathfinder.queueRequest(startX, startY, endX, endY, (path, pathLength) =>
            {
                // Skip marshalling results for bots that died / were cleared while solving.
                if (bot.IsDead || bot.bCondemned)
                    return;

                // Bound pending arena callbacks — drop oldest so a path storm cannot pin RAM / stall polls.
                while (_botPathCallbacks.Count >= MaxBotPathCallbacks
                       && _botPathCallbacks.TryDequeue(out _))
                {
                }

                _botPathCallbacks.Enqueue(new BotPathCallback
                {
                    Bot = bot,
                    Callback = callback,
                    Path = path,
                    PathLength = pathLength,
                });
            }, botId, priority, stillNeeded);
        }

        /// <summary>Pending pathfinder requests waiting on the pathing thread.</summary>
        public int BotPathQueueCount => _pathfinder?.queueCount() ?? 0;

        /// <summary>
        /// Drops stale arena path results and all pending pathfinder requests.
        /// Cleared requests complete as failed (null path) so bots leave Pending and can re-pick.
        /// </summary>
        public int ClearBotPathQueue()
        {
            // Discard results that finished but have not been applied yet (likely obsolete).
            int staleResults = 0;
            while (_botPathCallbacks.TryDequeue(out _))
                staleResults++;

            // Each dropped request invokes its wrapper, which re-enqueues a failed result.
            int dropped = _pathfinder?.clearQueue() ?? 0;

            // Apply those failure callbacks now so Pending bots are not stuck until next poll.
            FlushBotPathCallbacks(0);

            return dropped + staleResults;
        }
    }
}
