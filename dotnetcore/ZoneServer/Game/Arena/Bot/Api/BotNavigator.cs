using System;
using System.Collections.Generic;
using Axiom.Math;
using Bnoerj.AI.Steering;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Bots
{
    public sealed class BotNavigateOptions
    {
        public int RequeueMinMs { get; set; } = 3200;
        public int PathStartIndex { get; set; } = 0;
        public bool ForceRequeue { get; set; }
        /// <summary>Combat routes are drained before idle/patrol on the shared pathfinder.</summary>
        public PathPriority Priority { get; set; } = PathPriority.Idle;
    }

    /// <summary>
    /// Server-owned path state and pathfinder requests for a bot.
    /// </summary>
    public sealed class BotNavigator
    {
        private readonly Bot _bot;
        private List<Vector3>? _path;
        private int _pathTarget;
        private int _tickLastPath;
        private int _requeueMinMs = 3200;
        private Func<InfantryVehicle, Vector3>? _pendingFallback;
        /// <summary>Bumped on Clear / ForceRequeue so stale pathfinder callbacks are ignored.</summary>
        private int _pathRequestGeneration;
        short _goalX;
        short _goalY;
        bool _hasGoal;
        /// <summary>Repath when Approach dest drifts this far from last queued goal (raw^2).</summary>
        const long GoalDriftRawSq = 96L * 96L;

        public BotNavigator(Bot bot)
        {
            _bot = bot;
        }

        public BotNavigationState State
        {
            get
            {
                if (_tickLastPath == int.MaxValue)
                    return BotNavigationState.Pending;

                if (_path != null && _path.Count > 0 && _pathTarget < _path.Count)
                    return BotNavigationState.Following;

                if (_tickLastPath != 0 && _path == null)
                    return BotNavigationState.Failed;

                return BotNavigationState.Idle;
            }
        }

        public bool HasPath => _path != null && _path.Count > 0 && _pathTarget < _path.Count;

        public IReadOnlyList<Vector3>? Path => _path;

        public int PathTarget => _pathTarget;

        public void Clear()
        {
            _pathRequestGeneration++;
            _path = null;
            _pathTarget = 0;
            _tickLastPath = 0;
            _pendingFallback = null;
            _hasGoal = false;
            ClearSteeringPath();
        }

        public void NavigateToTile(short tileX, short tileY, BotNavigateOptions? options = null)
        {
            NavigateTo(
                (short)(tileX * 16), (short)(tileY * 16),
                options);
        }

        public void NavigateTo(short destX, short destY, BotNavigateOptions? options = null)
        {
            var opts = options ?? new BotNavigateOptions();
            _requeueMinMs = opts.RequeueMinMs;
            _goalX = destX;
            _goalY = destY;
            _hasGoal = true;

            if (!opts.ForceRequeue && HasPath)
                return;

            RequestPath(
                (short)(_bot._state.positionX / 16), (short)(_bot._state.positionY / 16),
                (short)(destX / 16), (short)(destY / 16),
                opts.PathStartIndex,
                opts.Priority);
        }

        public void NavigateTo(Helpers.ObjectState dest, BotNavigateOptions? options = null)
        {
            if (dest == null)
                return;

            NavigateTo(dest.positionX, dest.positionY, options);
        }

        /// <summary>
        /// Uses an existing path when available; otherwise queues one and uses fallback steering until it arrives.
        /// Repaths when the destination drifts far from the last queued goal (moving chase / patrol hops).
        /// </summary>
        public void Approach(
            Helpers.ObjectState dest,
            Func<InfantryVehicle, Vector3>? fallbackSteer,
            BotNavigateOptions? options = null)
        {
            var opts = options ?? new BotNavigateOptions();
            _pendingFallback = fallbackSteer;

            // Finished / stale path that never cleared — drop it so we can requeue.
            if (_path != null && (_path.Count == 0 || _pathTarget >= _path.Count))
            {
                _path = null;
                _pathTarget = 0;
                ClearSteeringPath();
            }

            if (opts.ForceRequeue)
            {
                _pathRequestGeneration++;
                _path = null;
                _pathTarget = 0;
                _tickLastPath = 0;
                ClearSteeringPath();
            }

            bool goalDrifted = false;
            if (dest != null && _hasGoal)
            {
                long dx = (long)dest.positionX - _goalX;
                long dy = (long)dest.positionY - _goalY;
                goalDrifted = dx * dx + dy * dy >= GoalDriftRawSq;
            }

            if (HasPath && !opts.ForceRequeue && !goalDrifted)
                return;

            int now = Environment.TickCount;

            if (_tickLastPath == int.MaxValue)
                return;

            if (!opts.ForceRequeue && _tickLastPath != 0 && now - _tickLastPath < opts.RequeueMinMs)
                return;

            if (goalDrifted && HasPath)
            {
                _pathRequestGeneration++;
                _path = null;
                _pathTarget = 0;
                ClearSteeringPath();
            }

            NavigateTo(dest, opts);
        }

        public bool TileLineClearForWeaponFire(short toX, short toY)
        {
            return BotCombat.TileLineClearForWeaponFire(_bot._arena, _bot._state.positionX, _bot._state.positionY, toX, toY);
        }

        internal void OnPathReceived(List<Vector3>? path, int pathLength, int pathStartIndex, int requestGeneration)
        {
            // Stale callback after Clear / ForceRequeue / death — ignore.
            if (requestGeneration != _pathRequestGeneration)
                return;
            if (_bot == null || _bot.IsDead || _bot.bCondemned)
                return;

            _tickLastPath = Environment.TickCount;

            if (path != null && pathLength > 0)
            {
                _path = path;
                _pathTarget = pathStartIndex;
                if (_pathTarget >= _path.Count)
                    _pathTarget = _path.Count > 1 ? 1 : 0;

                ApplyPathToSteering();
            }
            else
            {
                _path = null;
                _pathTarget = 0;
                ClearSteeringPath();
            }
        }

        internal void OnPathReceived(List<Vector3>? path, int pathLength, int pathStartIndex)
        {
            OnPathReceived(path, pathLength, pathStartIndex, _pathRequestGeneration);
        }

        internal void ApplyToSteering(SteeringController steering, InfantryVehicle vehicle)
        {
            if (steering == null)
                return;

            if (HasPath)
            {
                // Keep the steering controller's advanced path index when following the same path.
                bool samePath = steering.FollowPath != null
                    && ReferenceEquals(steering.FollowPath, _path)
                    && steering.FollowPathTarget > 0
                    && steering.FollowPathTarget < _path.Count;
                if (samePath)
                    _pathTarget = steering.FollowPathTarget;

                steering.FollowPath = _path;
                steering.FollowPathTarget = _pathTarget;
                return;
            }

            if (_pendingFallback != null)
                steering.steerDelegate = _pendingFallback;
        }

        internal Vector3 SteerAlongPath(InfantryVehicle vehicle)
        {
            if (_path == null || _path.Count == 0)
                return Vector3.Zero;

            Vector3 steer = PathNavigationHelper.SteerAlongPathSmooth(vehicle, _path, ref _pathTarget);

            if (_pathTarget >= _path.Count)
            {
                _path = null;
                _pathTarget = 0;
                _tickLastPath = 0;
                ClearSteeringPath();
            }
            else if (_bot._movement is SteeringController steering)
            {
                steering.FollowPathTarget = _pathTarget;
            }

            return steer;
        }

        internal void SyncPathTargetFromSteering(int pathTarget)
        {
            _pathTarget = pathTarget;
            if (_path != null && _pathTarget >= _path.Count)
            {
                _path = null;
                _pathTarget = 0;
                _tickLastPath = 0;
                ClearSteeringPath();
            }
        }

        private void RequestPath(short startX, short startY, short endX, short endY, int pathStartIndex, PathPriority priority)
        {
            if (_bot._arena == null)
            {
                OnPathReceived(null, 0, pathStartIndex);
                return;
            }

            // Soft-refuse when the shared pathfinder is saturated — avoid Pending+null storms.
            // Match script HardQueueCap (must be below Pathfinder drop threshold of ~60 on 80-slot queues).
            if (_bot._arena.BotPathQueueCount >= 55)
            {
                if (_tickLastPath == int.MaxValue)
                    _tickLastPath = Environment.TickCount;
                else if (_tickLastPath == 0)
                    _tickLastPath = Environment.TickCount;
                return;
            }

            // Bump generation so any in-flight / coalesce-discarded callbacks cannot clear Pending.
            _pathRequestGeneration++;
            _tickLastPath = int.MaxValue;
            int requestGen = _pathRequestGeneration;

            _bot._arena.QueueBotPathRequest(_bot, startX, startY, endX, endY, (path, pathLength) =>
            {
                OnPathReceived(path, pathLength, pathStartIndex, requestGen);
            }, priority);
        }

        private void ApplyPathToSteering()
        {
            if (_bot._movement is SteeringController steering)
            {
                steering.FollowPath = _path;
                steering.FollowPathTarget = _pathTarget;
            }
        }

        private void ClearSteeringPath()
        {
            if (_bot._movement is SteeringController steering)
                steering.ClearNavigationPath();
        }
    }
}
