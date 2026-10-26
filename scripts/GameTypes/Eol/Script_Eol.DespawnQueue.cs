using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Bots;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Paces mass vehicle/bot teardown so clients are not flooded with VehicleDestroy packets
    /// in a single tick (HQ wipe, sector rotation, game end, raid/alien abort).
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>How many queued vehicles to destroy each drain pulse.</summary>
        public const int DespawnPerDrain = 3;
        /// <summary>Minimum ms between drain pulses (even if poll runs faster).</summary>
        public const int DespawnMinIntervalMs = 40;

        readonly Queue<Vehicle> _despawnQueue = new Queue<Vehicle>(64);
        readonly HashSet<int> _despawnQueuedIds = new HashSet<int>();
        int _tickLastDespawnDrain;

        public bool HasPendingDespawns
        {
            get { return _despawnQueue.Count > 0; }
        }

        public bool IsVehicleDespawnQueued(Vehicle v)
        {
            return v != null && _despawnQueuedIds.Contains(v._id);
        }

        public bool TeamHasPendingDespawns(Team team)
        {
            if (team == null || _despawnQueue.Count == 0)
                return false;
            foreach (Vehicle v in _despawnQueue)
            {
                if (v != null && !v.IsDead && v._team == team)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Queue a vehicle/bot for paced destroy. Script bot bookkeeping is cleared immediately
        /// so spawn/cap logic does not treat it as live. Actual <c>destroy</c> (and the client
        /// packet) happens in <see cref="DrainDespawnQueue"/>.
        /// </summary>
        public void EnqueueVehicleDespawn(Vehicle v)
        {
            if (v == null || v.IsDead)
                return;
            // Already gone / being torn down by the engine.
            if (v.bCondemned)
                return;
            if (!_despawnQueuedIds.Add(v._id))
                return;

            Bots.Bot bot = v as Bots.Bot;
            if (bot != null)
            {
                if (_bots != null)
                    _bots.Remove(bot);
                ForgetScriptBotBookkeeping(bot);
                SilenceBotForQueuedDespawn(bot);
            }

            _despawnQueue.Enqueue(v);
        }

        public void EnqueueVehicleDespawnMany(IEnumerable<Vehicle> vehicles)
        {
            if (vehicles == null)
                return;
            foreach (Vehicle v in vehicles)
                EnqueueVehicleDespawn(v);
        }

        void SilenceBotForQueuedDespawn(Bots.Bot bot)
        {
            if (bot == null)
                return;
            try
            {
                bot.Navigation?.Clear();
                bot.HoldPosition();
                bot._itemUseID = 0;
            }
            catch
            {
                // Teardown races — ignore.
            }
        }

        /// <summary>Destroy up to <see cref="DespawnPerDrain"/> queued vehicles if the interval has elapsed.</summary>
        public void DrainDespawnQueue(int now)
        {
            if (_despawnQueue.Count == 0)
                return;
            if (_tickLastDespawnDrain != 0 && now - _tickLastDespawnDrain < DespawnMinIntervalMs)
                return;

            _tickLastDespawnDrain = now;
            int budget = DespawnPerDrain;
            while (budget-- > 0 && _despawnQueue.Count > 0)
            {
                Vehicle v = _despawnQueue.Dequeue();
                if (v == null)
                    continue;

                _despawnQueuedIds.Remove(v._id);

                if (v.IsDead || v.bCondemned)
                    continue;

                try
                {
                    v.destroy(true);
                }
                catch
                {
                    // Ignore teardown races.
                }
            }
        }

        /// <summary>
        /// Empty the queue immediately (rare — only when a following spawn must not collide
        /// with still-queued vehicles, e.g. border-wall rebuild). Prefer paced drain.
        /// </summary>
        public void FlushDespawnQueueImmediate()
        {
            while (_despawnQueue.Count > 0)
            {
                Vehicle v = _despawnQueue.Dequeue();
                if (v == null)
                    continue;
                _despawnQueuedIds.Remove(v._id);
                if (v.IsDead || v.bCondemned)
                    continue;
                try { v.destroy(true); }
                catch { /* ignore */ }
            }
            _despawnQueuedIds.Clear();
        }
    }
}
