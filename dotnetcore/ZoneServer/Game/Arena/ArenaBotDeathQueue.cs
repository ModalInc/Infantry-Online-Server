using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game.Modules;
using InfServer.Protocol;

namespace InfServer.Game
{
    /// <summary>
    /// Instant bot death FX for nearby clients; paced arena-wide killfeed text.
    /// </summary>
    public abstract partial class Arena
    {
        struct BotDeathKillfeedEvent
        {
            public Team deadTeam;
            public string botName;
            public string killerName;
        }

        readonly Dictionary<Bot, Bot> _lastBotDamagerByVictim = new Dictionary<Bot, Bot>();
        readonly Queue<BotDeathKillfeedEvent> _botDeathKillfeedQueue = new Queue<BotDeathKillfeedEvent>(256);
        int _tickLastBotDeathKillfeedDrain;
        int _tickLastBotDeathRateLog;
        int _botDeathsSinceLastRateLog;
        int _tickLastBotDeathQueueSpikeLog;

        const int BotDeathKillfeedMinIntervalMs = 25;
        const int BotDeathKillfeedOpsPerDrain = 3;
        const int BotDeathRateLogIntervalMs = 1000;
        const int BotDeathQueueSpikeLogMinIntervalMs = 1500;

        /// <summary>Remember which bot last damaged <paramref name="victim"/> (bot-on-bot kills).</summary>
        public void NoteBotDamagedByBot(Bot victim, Bot shooter)
        {
            if (victim == null || shooter == null || victim == shooter)
                return;
            _lastBotDamagerByVictim[victim] = shooter;
        }

        /// <summary>Peek at the last bot damager without consuming the entry.</summary>
        public bool TryPeekLastBotDamager(Bot victim, out Bot shooter)
        {
            shooter = null;
            if (victim == null)
                return false;
            return _lastBotDamagerByVictim.TryGetValue(victim, out shooter) && shooter != null;
        }

        /// <summary>Take and clear the last bot damager for a victim.</summary>
        public bool TryTakeLastBotDamager(Bot victim, out Bot shooter)
        {
            shooter = null;
            if (victim == null)
                return false;
            if (!_lastBotDamagerByVictim.TryGetValue(victim, out shooter))
                return false;
            _lastBotDamagerByVictim.Remove(victim);
            return shooter != null;
        }

        /// <summary>Drop damager bookkeeping when a bot is removed.</summary>
        public void ForgetBotDamagerBookkeeping(Bot bot)
        {
            if (bot == null)
                return;

            _lastBotDamagerByVictim.Remove(bot);

            if (_lastBotDamagerByVictim.Count == 0)
                return;

            List<Bot> staleVictims = null;
            foreach (KeyValuePair<Bot, Bot> kv in _lastBotDamagerByVictim)
            {
                if (kv.Value != bot)
                    continue;
                if (staleVictims == null)
                    staleVictims = new List<Bot>();
                staleVictims.Add(kv.Key);
            }

            if (staleVictims != null)
            {
                for (int i = 0; i < staleVictims.Count; i++)
                    _lastBotDamagerByVictim.Remove(staleVictims[i]);
            }
        }

        /// <summary>Send death FX immediately to lifecycle audience; queue killfeed for paced drain.</summary>
        public void QueueBotDeathKillfeed(Bot dead, Player killer)
        {
            if (dead == null)
                return;

            Bot killerBot = null;
            if (killer == null)
                TryPeekLastBotDamager(dead, out killerBot);

            uint killerId = killer == null ? 0u : (uint)killer._id;
            SendBotDeathGraphicImmediate(
                dead._id,
                killerId,
                dead._state.positionX,
                dead._state.positionY,
                (byte)dead._state.yaw);

            _botDeathKillfeedQueue.Enqueue(new BotDeathKillfeedEvent
            {
                deadTeam = dead._team,
                botName = BotNameModule.GetKillfeedName(dead),
                killerName = BotNameModule.GetKillerName(killer, killerBot),
            });

            int now = Environment.TickCount;
            _botDeathsSinceLastRateLog++;
            if (_tickLastBotDeathRateLog == 0)
                _tickLastBotDeathRateLog = now;

            if (now - _tickLastBotDeathRateLog >= BotDeathRateLogIntervalMs)
            {
                // Debug (disabled): bot death rate / killfeed backlog.
                // Log.write(TLog.Normal, "[Arena] botDeathsRate: {0}/s (killfeed queue={1})",
                //     _botDeathsSinceLastRateLog, _botDeathKillfeedQueue.Count);
                _tickLastBotDeathRateLog = now;
                _botDeathsSinceLastRateLog = 0;
            }

            if (_botDeathKillfeedQueue.Count >= 50 &&
                (_tickLastBotDeathQueueSpikeLog == 0 || now - _tickLastBotDeathQueueSpikeLog >= BotDeathQueueSpikeLogMinIntervalMs))
            {
                _tickLastBotDeathQueueSpikeLog = now;
                // Log.write(TLog.Normal, "[Arena] botDeathKillfeedQueue SPIKE: count={0}", _botDeathKillfeedQueue.Count);
            }
        }

        /// <summary>Ranged reliable death graphic — not arena-wide.</summary>
        void SendBotDeathGraphicImmediate(ushort vehicleID, uint killerID, short posX, short posY, byte yaw)
        {
            var not = new SC_VehicleDeath
            {
                // Same ids as a player in their own hull. playerID 0 is a vehicle-only
                // kill: the client unlinks the car and never draws the body again.
                type = Helpers.KillType.Player,
                playerID = vehicleID,
                vehicleID = vehicleID,
                killerID = killerID,
                positionX = posX,
                positionY = posY,
                yaw = yaw,
            };

            List<Player> deathAudience = GetBotLifecycleAudience(posX, posY);
            if (deathAudience == null)
                return;

            foreach (Player p in deathAudience)
            {
                if (p?._client == null)
                    continue;
                p._client.sendReliable(not);
            }
        }

        /// <summary>Immediate killfeed when a bot kills a player.</summary>
        public void BroadcastPlayerKilledByBot(Player victim, Bot bot)
        {
            if (victim == null)
                return;

            string botName = BotNameModule.GetKillfeedName(bot);
            foreach (Player p in Players)
            {
                if (p == null)
                    continue;
                byte color = (victim._team != null && victim._team == p._team) ? (byte)4 : (byte)0;
                p.triggerMessage(color, 500, string.Format("{0} killed by {1}", victim._alias, botName));
            }
        }

        /// <summary>Drain paced killfeed lines during arena poll.</summary>
        public void DrainBotDeathNetQueue(int now)
        {
            if (_botDeathKillfeedQueue.Count == 0)
                return;

            if (_tickLastBotDeathKillfeedDrain != 0 && now - _tickLastBotDeathKillfeedDrain < BotDeathKillfeedMinIntervalMs)
                return;

            _tickLastBotDeathKillfeedDrain = now;
            int budget = BotDeathKillfeedOpsPerDrain;

            while (budget-- > 0 && _botDeathKillfeedQueue.Count > 0)
            {
                BotDeathKillfeedEvent ev = _botDeathKillfeedQueue.Dequeue();

                foreach (Player p in Players)
                {
                    if (p == null)
                        continue;
                    byte color = (ev.deadTeam != null && ev.deadTeam == p._team) ? (byte)4 : (byte)0;
                    p.triggerMessage(color, 500, string.Format("{0} killed by {1}", ev.botName, ev.killerName));
                }
            }
        }
    }
}
