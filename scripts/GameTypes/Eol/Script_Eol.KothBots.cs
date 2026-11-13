using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Bots in KOTH (local change 2026-11-14). When a KOTH round starts every human / pirate bot in the field gets a
    /// crown too (aliens don't wear crowns), shown on clients like a player's. The same rules as players:
    ///   - a crowned bot that dies loses its crown (after the zone's death count - one life for a bot);
    ///   - a player who kills a crowned bot gets a crown kill (enough of them win a crown back), a crowned
    ///     player's kill refreshes their crown timer;
    ///   - a crowned bot that kills refreshes its own crown timer; a bot that kills a crowned player takes that
    ///     player's crown death like any killer;
    ///   - crowns expire on the zone's timer; the last team holding crowns (players or bots) wins the round.
    /// KOTH no longer needs 5 players - with the bots there is always a field.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool KothBotsParticipate = true;
        /// <summary>Players needed for the auto KOTH lobby when bots take part (was 5 players).</summary>
        public const int KothMinPlayersWithBots = 1;

        readonly Dictionary<Bot, int> _botCrownExpire = new Dictionary<Bot, int>();

        int KothCrownMs { get { return Math.Max(10, _config.king.expireTime) * 1000; } }

        bool BotCanWearCrown(Bot b)
        {
            return b != null && !b.IsDead && !b.bCondemned && b._team != null
                && IsManagedEolBotFactionTeam(b._team) && !IsAlienBotTeam(b._team);
        }

        void SendBotCrowns(bool crown, List<short> ids)
        {
            if (ids == null || ids.Count == 0 || _arena == null)
                return;
            SC_PlayerCrowns pkt = new SC_PlayerCrowns { bCrown = crown, players = ids };
            foreach (InfServer.Game.Player p in _arena.Players)
                p?._client?.sendReliable(pkt);
        }

        /// <summary>Round start: crown every eligible bot.</summary>
        void KothBotsStart()
        {
            _botCrownExpire.Clear();
            if (!KothBotsParticipate || _arena == null)
                return;
            int until = Environment.TickCount + KothCrownMs;
            List<short> ids = new List<short>();
            foreach (Bot b in _arena.ActiveBots)
            {
                if (!BotCanWearCrown(b))
                    continue;
                _botCrownExpire[b] = until;
                ids.Add((short)b._id);
            }
            SendBotCrowns(true, ids);
            Log.write(TLog.Normal, "[Eol] KOTH: {0} bots crowned.", ids.Count);
        }

        /// <summary>Round over: take every bot crown off.</summary>
        void KothBotsEnd()
        {
            if (_botCrownExpire.Count == 0)
                return;
            List<short> ids = new List<short>();
            foreach (Bot b in _botCrownExpire.Keys)
                if (b != null)
                    ids.Add((short)b._id);
            _botCrownExpire.Clear();
            SendBotCrowns(false, ids);
        }

        void RemoveBotCrown(Bot b)
        {
            if (b == null || !_botCrownExpire.Remove(b))
                return;
            SendBotCrowns(false, new List<short> { (short)b._id });
        }

        /// <summary>Expire / drop stale bot crowns; teams still holding one are added to <paramref name="teams"/>.</summary>
        int KothBotsPoll(int now, List<Team> teams)
        {
            if (_botCrownExpire.Count == 0)
                return 0;
            List<Bot> drop = null;
            foreach (KeyValuePair<Bot, int> kv in _botCrownExpire)
            {
                Bot b = kv.Key;
                if (b == null || b.bCondemned || b.IsDead || now - kv.Value > 0)
                {
                    if (drop == null) drop = new List<Bot>();
                    drop.Add(b);
                    continue;
                }
                if (teams != null && b._team != null && !teams.Contains(b._team))
                    teams.Add(b._team);
            }
            if (drop != null)
                foreach (Bot b in drop)
                    RemoveBotCrown(b);
            return _botCrownExpire.Count;
        }

        /// <summary>A bot died during KOTH (any killer).</summary>
        void KothOnBotDeath(Bot dead, InfServer.Game.Player killer, Bot killerBot)
        {
            if (!_kothGameRunning || dead == null)
                return;
            bool wasCrowned = _botCrownExpire.ContainsKey(dead);
            RemoveBotCrown(dead);

            if (killer != null && _playerCrownStatus != null && _playerCrownStatus.TryGetValue(killer, out PlayerCrownStatus ks))
            {
                if (ks.crown)
                    updateCrownTime(killer);
                else if (wasCrowned)
                {
                    ks.crownKills++;
                    if (_config.king.crownRecoverKills != 0 && ks.crownKills >= _config.king.crownRecoverKills)
                        giveCrown(killer);
                }
            }
            if (killerBot != null && _botCrownExpire.ContainsKey(killerBot))
                _botCrownExpire[killerBot] = Environment.TickCount + KothCrownMs;
        }

        /// <summary>A bot killed a player during KOTH: the player's crown death counts like any kill.</summary>
        void KothOnPlayerKilledByBot(InfServer.Game.Player victim, Bot killerBot)
        {
            if (!_kothGameRunning || victim == null || _playerCrownStatus == null)
                return;
            PlayerCrownStatus vs;
            if (_playerCrownStatus.TryGetValue(victim, out vs) && vs.crown)
            {
                vs.crownDeaths++;
                if (vs.crownDeaths >= _config.king.deathCount)
                {
                    vs.crown = false;
                    QueueKothCrownBroadcast(false, _noCrowns);
                }
            }
            if (killerBot != null && _botCrownExpire.ContainsKey(killerBot))
                _botCrownExpire[killerBot] = Environment.TickCount + KothCrownMs;
        }

        /// <summary>A player who joins mid-round sees the bots' crowns.</summary>
        void KothBotsSyncTo(InfServer.Game.Player p)
        {
            if (p?._client == null || _botCrownExpire.Count == 0)
                return;
            List<short> ids = new List<short>();
            foreach (Bot b in _botCrownExpire.Keys)
                if (b != null) ids.Add((short)b._id);
            p._client.sendReliable(new SC_PlayerCrowns { bCrown = true, players = ids });
        }
    }
}
