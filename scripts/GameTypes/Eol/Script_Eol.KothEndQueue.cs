using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Paces reliable crown/ticker/reward packets when a KOTH round ends so clients are not
    /// flooded in a single poll (mirrors <see cref="Script_Eol.DespawnQueue"/>).
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Reliable KOTH end operations drained each pulse.</summary>
        public const int KothEndOpsPerDrain = 1;
        /// <summary>Minimum ms between KOTH end drain pulses.</summary>
        public const int KothEndMinIntervalMs = 90;

        enum KothEndOpKind
        {
            CrownToClient,
            PlayerMessage,
            CrownTickerClearToClient,
            ArenaMessageToClient,
        }

        struct KothEndOp
        {
            public KothEndOpKind Kind;
            public bool CrownState;
            public List<short> CrownPlayerIds;
            public InfServer.Game.Player Player;
            public string Message;
            public int Bong;
        }

        readonly Queue<KothEndOp> _kothEndQueue = new Queue<KothEndOp>(48);
        int _tickLastKothEndDrain;
        int _tickLastKothEndQueueLog;

        public bool HasPendingKothEndUpdates
        {
            get { return _kothEndQueue.Count > 0; }
        }

        void EnqueueKothEndOp(KothEndOp op)
        {
            _kothEndQueue.Enqueue(op);
        }

        static List<short> CollectPlayerIds(IList<InfServer.Game.Player> players)
        {
            var ids = new List<short>();
            if (players == null)
                return ids;

            for (int i = 0; i < players.Count; i++)
            {
                InfServer.Game.Player p = players[i];
                if (p != null)
                    ids.Add((short)p._id);
            }
            return ids;
        }

        /// <summary>
        /// Queue one <see cref="SC_PlayerCrowns"/> per arena client (paced one reliable per drain).
        /// </summary>
        void QueueKothCrownBroadcast(bool bCrown, IList<InfServer.Game.Player> players)
        {
            if (_arena == null)
                return;

            List<short> ids = CollectPlayerIds(players);
            if (ids.Count == 0)
                return;

            foreach (InfServer.Game.Player recipient in _arena.Players)
            {
                if (recipient?._client == null)
                    continue;

                EnqueueKothEndOp(new KothEndOp
                {
                    Kind = KothEndOpKind.CrownToClient,
                    CrownState = bCrown,
                    CrownPlayerIds = ids,
                    Player = recipient,
                });
            }
        }

        /// <summary>Queue live crown resync (true + false lists) using current crown status.</summary>
        void QueueKothCrownResync()
        {
            QueueKothCrownBroadcast(true, _activeCrowns);
            QueueKothCrownBroadcast(false, _noCrowns);
        }

        void QueueKothGlobalCrownTickerClear()
        {
            if (_arena == null)
                return;

            foreach (InfServer.Game.Player p in _arena.Players)
            {
                if (p?._client == null)
                    continue;
                EnqueueKothEndOp(new KothEndOp
                {
                    Kind = KothEndOpKind.CrownTickerClearToClient,
                    Player = p,
                });
            }
        }

        /// <summary>Queue paced per-client crown ticker clear and false-only uncrown at round end.</summary>
        void QueueKothRoundEndClientUpdates(IList<InfServer.Game.Player> hadCrownPlayers)
        {
            if (_arena == null)
                return;

            QueueKothGlobalCrownTickerClear();

            if (hadCrownPlayers != null && hadCrownPlayers.Count > 0)
                QueueKothCrownBroadcast(false, hadCrownPlayers);
        }

        void QueueKothArenaMessage(string message, int bong = 0)
        {
            if (_arena == null || string.IsNullOrEmpty(message))
                return;

            foreach (InfServer.Game.Player p in _arena.Players)
            {
                if (p?._client == null)
                    continue;
                EnqueueKothEndOp(new KothEndOp
                {
                    Kind = KothEndOpKind.ArenaMessageToClient,
                    Player = p,
                    Message = message,
                    Bong = bong,
                });
            }
        }

        /// <summary>Chat only — cash/exp already applied server-side; no padded inventory/skills sync.</summary>
        void QueueKothVictoryPlayerNotify(InfServer.Game.Player player, string message)
        {
            if (player == null || string.IsNullOrEmpty(message))
                return;

            EnqueueKothEndOp(new KothEndOp
            {
                Kind = KothEndOpKind.PlayerMessage,
                Player = player,
                Message = message,
            });
        }

        /// <summary>Process queued KOTH end reliable packets (call every poll).</summary>
        public void DrainKothEndQueue(int now)
        {
            if (_kothEndQueue.Count == 0)
                return;
            if (_tickLastKothEndDrain != 0 && now - _tickLastKothEndDrain < KothEndMinIntervalMs)
                return;

            _tickLastKothEndDrain = now;
            int budget = KothEndOpsPerDrain;
            while (budget-- > 0 && _kothEndQueue.Count > 0)
            {
                KothEndOp op = _kothEndQueue.Dequeue();
                switch (op.Kind)
                {
                    case KothEndOpKind.CrownToClient:
                        if (op.Player?._client != null && op.CrownPlayerIds != null && op.CrownPlayerIds.Count > 0)
                        {
                            var pkt = new SC_PlayerCrowns();
                            pkt.bCrown = op.CrownState;
                            pkt.players = op.CrownPlayerIds;
                            op.Player._client.sendReliable(pkt);
                        }
                        break;
                    case KothEndOpKind.PlayerMessage:
                        if (op.Player != null && !string.IsNullOrEmpty(op.Message))
                            op.Player.sendMessage(0, op.Message);
                        break;
                    case KothEndOpKind.CrownTickerClearToClient:
                        SendPlayerTicker(op.Player, KothCrownTimerTickerIdx, 1, "");
                        break;
                    case KothEndOpKind.ArenaMessageToClient:
                        if (op.Player?._client != null && !string.IsNullOrEmpty(op.Message))
                        {
                            var chat = new SC_Chat();
                            chat.bong = (byte)op.Bong;
                            chat.chatType = Helpers.Chat_Type.Arena;
                            chat.message = op.Message;
                            chat.from = "";
                            op.Player._client.sendReliable(chat);
                        }
                        break;
                }
            }

            if (_kothEndQueue.Count >= 32
                && (_tickLastKothEndQueueLog == 0 || now - _tickLastKothEndQueueLog >= 5000))
            {
                _tickLastKothEndQueueLog = now;
                Log.write(TLog.Normal, "[Eol] KOTH end queue depth: {0}", _kothEndQueue.Count);
            }
        }

        public void ClearKothEndQueue()
        {
            _kothEndQueue.Clear();
            _tickLastKothEndDrain = 0;
            _tickLastKothEndQueueLog = 0;
        }
    }
}
