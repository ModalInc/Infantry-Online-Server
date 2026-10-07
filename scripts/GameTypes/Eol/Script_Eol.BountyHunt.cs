using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Game;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// 30k bounty hunt: FIFO queue of eligible 30k players. First in is hunted.
    /// On kill / spec / leave, advance to the next still-valid 30k player.
    /// Two pots (survivor + assassin). Assassin pot pays on top of normal kill rewards.
    /// </summary>
    public partial class Script_Eol
    {
        public const int BountyHuntThreshold = 30000;
        public const int BountyHuntPingIntervalMs = 2 * 60 * 1000;
        public const int BountyHuntTrickleIntervalMs = 20 * 1000;
        public const int BountyHuntMinMarkMsForFullPayout = 90 * 1000;
        public const int BountyHuntPairCooldownMs = 30 * 60 * 1000;
        public const int BountyHuntReciprocityWindowMs = 10 * 60 * 1000;
        public const int BountyHuntRemarkCooldownMs = 10 * 60 * 1000;
        public const int BountyHuntSpeedBonusWindowMs = 60 * 1000;
        /// <summary>How often the 30k queue is reconciled (add new / drop ineligible).</summary>
        public const int BountyHuntQueueRefreshMs = 5 * 1000;

        public const int BountyHuntTrickleCashPerTick = 150;
        public const int BountyHuntTrickleExpPerTick = 75;
        public const int BountyHuntTricklePointsPerTick = 25;

        public const int BountyHuntAssassinCashPerTick = 200;
        public const int BountyHuntAssassinExpPerTick = 100;
        public const int BountyHuntAssassinPointsPerTick = 40;

        public const int BountyHuntSurvivorCashCap = 25000;
        public const int BountyHuntSurvivorExpCap = 12000;
        public const int BountyHuntSurvivorPointsCap = 5000;

        public const int BountyHuntAssassinCashCap = 25000;
        public const int BountyHuntAssassinExpCap = 12000;
        public const int BountyHuntAssassinPointsCap = 5000;

        /// <summary>FIFO of 30k players — index 0 is current hunted when a hunt is active.</summary>
        readonly List<InfServer.Game.Player> _bountyHuntQueue = new List<InfServer.Game.Player>();
        int _bountyHuntNextQueueRefreshTick;

        InfServer.Game.Player _bountyHuntMarked;
        int _bountyHuntMarkStartedTick;
        int _bountyHuntNextPingTick;
        int _bountyHuntNextTrickleTick;
        int _bountyHuntDropshipEnteredTick;
        int _bountyHuntLastPingTick;

        int _bountyHuntSurvivorPotCash;
        int _bountyHuntSurvivorPotExp;
        int _bountyHuntSurvivorPotPoints;

        int _bountyHuntAssassinPotCash;
        int _bountyHuntAssassinPotExp;
        int _bountyHuntAssassinPotPoints;

        int _bountyHuntFieldAliveMs;
        int _bountyHuntLastFieldSampleTick;

        readonly Dictionary<int, Dictionary<int, int>> _bountyHuntPairCooldown =
            new Dictionary<int, Dictionary<int, int>>();

        /// <summary>playerId → earliest tick they may re-enter the hunt queue after being hunted.</summary>
        readonly Dictionary<int, int> _bountyHuntRemarkReady =
            new Dictionary<int, int>();

        readonly Dictionary<string, int> _bountyHuntRecentPvPKills =
            new Dictionary<string, int>();

        public bool IsBountyHuntActive
        {
            get { return _bountyHuntMarked != null; }
        }

        public InfServer.Game.Player BountyHuntMarked
        {
            get { return _bountyHuntMarked; }
        }

        public int BountyHuntAssassinPotCash
        {
            get { return _bountyHuntAssassinPotCash; }
        }

        public int BountyHuntSurvivorPotCash
        {
            get { return _bountyHuntSurvivorPotCash; }
        }

        public int BountyHuntQueueCount
        {
            get { return _bountyHuntQueue.Count; }
        }

        void PollBountyHunt(int now)
        {
            if (_arena == null || !_arena._bGameRunning)
            {
                if (_bountyHuntMarked != null || _bountyHuntQueue.Count > 0)
                    ClearBountyHunt("match ended", applyRemarkCooldown: false, advanceQueue: false);
                return;
            }

            PruneBountyHuntTracking(now);
            RefreshBountyHuntQueue(now);

            if (_bountyHuntMarked != null)
            {
                if (!IsBountyHuntMarkStillPresent(_bountyHuntMarked))
                {
                    InfServer.Game.Player gone = _bountyHuntMarked;
                    bool wasSpec = gone != null && gone.IsSpectator;
                    AdvanceBountyHuntFromMark(
                        wasSpec ? "target spectating" : "target left",
                        applyRemarkCooldown: !wasSpec,
                        now);
                }
                else
                {
                    PollBountyHuntMarked(now);
                }
                return;
            }

            // No active mark — promote the front of the queue if valid.
            TryPromoteNextBountyHuntTarget(now);
        }

        /// <summary>
        /// Periodically add new 30k players (FIFO) and remove anyone who is no longer
        /// in-game / not spectator-free / under threshold (except the active mark, which
        /// may briefly dip bounty on death before kill hooks fire).
        /// </summary>
        void RefreshBountyHuntQueue(int now)
        {
            if (now < _bountyHuntNextQueueRefreshTick)
                return;
            _bountyHuntNextQueueRefreshTick = now + BountyHuntQueueRefreshMs;

            // Drop ineligible entries (keep active mark until kill/spec handlers remove them).
            for (int i = _bountyHuntQueue.Count - 1; i >= 0; i--)
            {
                InfServer.Game.Player p = _bountyHuntQueue[i];
                if (p == _bountyHuntMarked)
                    continue;
                if (!IsValidBountyHuntQueueMember(p))
                    _bountyHuntQueue.RemoveAt(i);
            }

            // Append newly eligible 30k players (first time they qualify = back of line).
            if (_arena.PlayersIngame == null)
                return;

            foreach (InfServer.Game.Player p in _arena.PlayersIngame)
            {
                if (!IsValidBountyHuntQueueMember(p))
                    continue;
                if (IsPlayerInBountyHuntQueue(p))
                    continue;

                int ready;
                if (_bountyHuntRemarkReady.TryGetValue(p._id, out ready) && now < ready)
                    continue;

                _bountyHuntQueue.Add(p);
                if (_arena != null && _bountyHuntMarked != null)
                {
                    p.sendMessage(0, string.Format(
                        "You are on the HUNT list (#{0}). Survive with {1:N0}+ bounty — first in line is hunted.",
                        _bountyHuntQueue.Count, BountyHuntThreshold));
                }
            }

            InvalidateEolHudTickerCache();
        }

        bool IsPlayerInBountyHuntQueue(InfServer.Game.Player p)
        {
            if (p == null)
                return false;
            for (int i = 0; i < _bountyHuntQueue.Count; i++)
            {
                if (_bountyHuntQueue[i] == p)
                    return true;
            }
            return false;
        }

        void RemovePlayerFromBountyHuntQueue(InfServer.Game.Player p)
        {
            if (p == null)
                return;
            for (int i = _bountyHuntQueue.Count - 1; i >= 0; i--)
            {
                if (_bountyHuntQueue[i] == p)
                    _bountyHuntQueue.RemoveAt(i);
            }
        }

        /// <summary>In arena, not spectating, still holding the 30k threshold.</summary>
        bool IsValidBountyHuntQueueMember(InfServer.Game.Player p)
        {
            return p != null
                && !p.IsSpectator
                && p._arena == _arena
                && p.Bounty >= BountyHuntThreshold;
        }

        /// <summary>Before promoting to hunted — must be in-game and still 30k+.</summary>
        bool IsValidBountyHuntPromotionCandidate(InfServer.Game.Player p)
        {
            return IsValidBountyHuntQueueMember(p) && !p.IsDead;
        }

        /// <summary>Active mark stays present until kill/leave handlers finish (may be dead briefly).</summary>
        bool IsBountyHuntMarkStillPresent(InfServer.Game.Player p)
        {
            return p != null
                && !p.IsSpectator
                && p._arena == _arena;
        }

        bool IsBountyHuntMarkInFieldForSurvivorPot(InfServer.Game.Player mark)
        {
            return mark != null
                && !mark.IsSpectator
                && !mark.IsDead
                && !IsBountyHuntInDropship(mark);
        }

        /// <summary>Promote the next still-valid queue entry to hunted. Skips anyone who lost 30k / left / spec'd.</summary>
        bool TryPromoteNextBountyHuntTarget(int now)
        {
            while (_bountyHuntQueue.Count > 0)
            {
                InfServer.Game.Player next = _bountyHuntQueue[0];
                if (!IsValidBountyHuntPromotionCandidate(next))
                {
                    _bountyHuntQueue.RemoveAt(0);
                    continue;
                }

                BeginBountyHunt(next, now);
                return true;
            }

            return false;
        }

        /// <summary>
        /// End current mark (pots wipe), remove them from the queue, then promote the next valid 30k.
        /// </summary>
        void AdvanceBountyHuntFromMark(string reason, bool applyRemarkCooldown, int now)
        {
            InfServer.Game.Player mark = _bountyHuntMarked;
            if (mark != null && _arena != null)
            {
                _arena.sendArenaMessage(string.Format(
                    "&HUNT ended for {0} ({1}). Survivor pot C={2} | Assassin pot C={3} wiped.",
                    mark._alias, reason, _bountyHuntSurvivorPotCash, _bountyHuntAssassinPotCash),
                    _config != null ? _config.flag.periodicBong : 1);

                if (applyRemarkCooldown)
                    _bountyHuntRemarkReady[mark._id] = now + BountyHuntRemarkCooldownMs;
                else
                    _bountyHuntRemarkReady.Remove(mark._id);
            }

            RemovePlayerFromBountyHuntQueue(mark);
            ResetBountyHuntPots();
            _bountyHuntMarked = null;
            OnBountyHunterHuntEnded(now, announce: false);
            InvalidateEolHudTickerCache();
            Log.write(TLog.Normal, "[Eol] Bounty hunt advanced: {0}.", reason);

            // Force a queue pass so we don't promote stale entries.
            _bountyHuntNextQueueRefreshTick = 0;
            RefreshBountyHuntQueue(now);

            if (TryPromoteNextBountyHuntTarget(now))
            {
                // BeginBountyHunt → OnBountyHunterHuntStarted for the next mark.
            }
            else if (_arena != null)
            {
                _arena.sendArenaMessage("&HUNT queue empty — waiting for the next 30k bounty.",
                    _config != null ? _config.flag.periodicBong : 1);
            }
        }

        public void OnBountyHuntPlayerUnspec(InfServer.Game.Player player)
        {
            if (player == null)
                return;
            // Unspec with 30k can rejoin the queue on the next refresh (back of line).
            _bountyHuntNextQueueRefreshTick = 0;
            if (_arena != null && _arena._bGameRunning)
            {
                RefreshBountyHuntQueue(Environment.TickCount);
                if (_bountyHuntMarked == null)
                    TryPromoteNextBountyHuntTarget(Environment.TickCount);
            }
        }

        public void OnBountyHuntPlayerSpec(InfServer.Game.Player player)
        {
            if (player == null)
                return;

            if (_bountyHuntMarked == player)
            {
                AdvanceBountyHuntFromMark("target spectating", applyRemarkCooldown: false, Environment.TickCount);
                return;
            }

            // Waiting in line — just drop them; they can re-queue on unspec if still 30k.
            RemovePlayerFromBountyHuntQueue(player);
            InvalidateEolHudTickerCache();
        }

        void BeginBountyHunt(InfServer.Game.Player target, int now)
        {
            // Ensure they sit at the front of the queue.
            RemovePlayerFromBountyHuntQueue(target);
            _bountyHuntQueue.Insert(0, target);

            _bountyHuntMarked = target;
            _bountyHuntMarkStartedTick = now;
            _bountyHuntLastPingTick = now;
            _bountyHuntNextPingTick = now + BountyHuntPingIntervalMs;
            _bountyHuntNextTrickleTick = now + BountyHuntTrickleIntervalMs;
            _bountyHuntDropshipEnteredTick = 0;
            _bountyHuntSurvivorPotCash = 0;
            _bountyHuntSurvivorPotExp = 0;
            _bountyHuntSurvivorPotPoints = 0;
            _bountyHuntAssassinPotCash = 0;
            _bountyHuntAssassinPotExp = 0;
            _bountyHuntAssassinPotPoints = 0;
            _bountyHuntFieldAliveMs = 0;
            _bountyHuntLastFieldSampleTick = now;

            int waiting = Math.Max(0, _bountyHuntQueue.Count - 1);
            int bong = _config != null ? _config.flag.periodicBong : 1;
            _arena.sendArenaMessage(string.Format(
                "&HUNT: {0} is now HUNTED ({1:N0}+ bounty, first in line). {2} waiting. Survivor pot (field) + Assassin pot (kill bonus).",
                target._alias, BountyHuntThreshold, waiting), bong);
            target.sendMessage(0,
                "You are HUNTED. Survivor pot pays you in the field. Assassin pot grows for your killer (on top of normal kill rewards). Spec/leave/death passes the hunt to the next 30k.");

            if (waiting > 0)
                _arena.sendArenaMessage("&HUNT QUEUE: " + FormatBountyHuntQueuePreview(), 0);

            BroadcastBountyHuntIntel(target, now, first: true);
            OnBountyHunterHuntStarted(target, now);
            InvalidateEolHudTickerCache();
            Log.write(TLog.Normal, "[Eol] Bounty hunt started: {0} (bounty={1}, queue={2}).",
                target._alias, target.Bounty, _bountyHuntQueue.Count);
        }

        string FormatBountyHuntQueuePreview(int maxNames = 5)
        {
            if (_bountyHuntQueue.Count <= 1)
                return "(empty)";

            StringBuilder sb = new StringBuilder();
            int shown = 0;
            for (int i = 1; i < _bountyHuntQueue.Count && shown < maxNames; i++)
            {
                InfServer.Game.Player p = _bountyHuntQueue[i];
                if (p == null)
                    continue;
                if (shown > 0)
                    sb.Append(", ");
                sb.Append(p._alias);
                shown++;
            }
            int more = _bountyHuntQueue.Count - 1 - shown;
            if (more > 0)
                sb.Append(" +" + more);
            return sb.Length > 0 ? sb.ToString() : "(empty)";
        }

        void PollBountyHuntMarked(int now)
        {
            InfServer.Game.Player mark = _bountyHuntMarked;
            if (mark == null)
                return;

            if (mark.IsDead)
                return;

            // Dropship: pause survivor rewards only — hunt stays active and assassin pot keeps growing.
            bool inDropship = IsBountyHuntInDropship(mark);

            if (inDropship)
            {
                if (_bountyHuntDropshipEnteredTick == 0)
                {
                    _bountyHuntDropshipEnteredTick = now;
                    mark.sendMessage(0,
                        "Survivor pot paused in the dropship. Assassin pot keeps growing. Hunters are waiting in the open sectors.");
                }
                _bountyHuntLastFieldSampleTick = now;
            }
            else
            {
                if (_bountyHuntDropshipEnteredTick != 0)
                {
                    _bountyHuntDropshipEnteredTick = 0;
                    mark.sendMessage(0, "Back in the field — survivor pot resumes. Hunters are moving.");
                }
                if (_bountyHuntLastFieldSampleTick != 0)
                {
                    int delta = now - _bountyHuntLastFieldSampleTick;
                    if (delta > 0 && delta < 60000)
                        _bountyHuntFieldAliveMs += delta;
                }
                _bountyHuntLastFieldSampleTick = now;
            }

            if (now >= _bountyHuntNextTrickleTick)
            {
                ApplyBountyHuntTrickle(mark, grantSurvivor: !inDropship);
                while (_bountyHuntNextTrickleTick <= now)
                    _bountyHuntNextTrickleTick += BountyHuntTrickleIntervalMs;
                InvalidateEolHudTickerCache();
                // Push pots immediately — don't wait for the next HUD interval.
                _tickLastHudTickerUpdate = 0;
                updateTickers();
            }

            if (now >= _bountyHuntNextPingTick)
            {
                BroadcastBountyHuntIntel(mark, now, first: false);
                _bountyHuntLastPingTick = now;
                while (_bountyHuntNextPingTick <= now)
                    _bountyHuntNextPingTick += BountyHuntPingIntervalMs;
                InvalidateEolHudTickerCache();
                _tickLastHudTickerUpdate = 0;
                updateTickers();
            }
        }

        void ApplyBountyHuntTrickle(InfServer.Game.Player mark, bool grantSurvivor)
        {
            if (grantSurvivor && IsBountyHuntMarkInFieldForSurvivorPot(mark))
            {
                int cash = ApplyEolLifetimeRewardScale(mark, BountyHuntTrickleCashPerTick);
                int exp = ApplyEolLifetimeRewardScale(mark, BountyHuntTrickleExpPerTick);
                int points = ApplyEolLifetimeRewardScale(mark, BountyHuntTricklePointsPerTick);

                int roomCash = Math.Max(0, BountyHuntSurvivorCashCap - _bountyHuntSurvivorPotCash);
                int roomExp = Math.Max(0, BountyHuntSurvivorExpCap - _bountyHuntSurvivorPotExp);
                int roomPts = Math.Max(0, BountyHuntSurvivorPointsCap - _bountyHuntSurvivorPotPoints);
                cash = Math.Min(cash, roomCash);
                exp = Math.Min(exp, roomExp);
                points = Math.Min(points, roomPts);

                if (cash > 0 || exp > 0 || points > 0)
                {
                    mark.Cash += cash;
                    mark.Experience += exp;
                    mark.BonusPoints += points;
                    mark.syncState();

                    _bountyHuntSurvivorPotCash += cash;
                    _bountyHuntSurvivorPotExp += exp;
                    _bountyHuntSurvivorPotPoints += points;
                }
            }

            _bountyHuntAssassinPotCash = Math.Min(BountyHuntAssassinCashCap,
                _bountyHuntAssassinPotCash + BountyHuntAssassinCashPerTick);
            _bountyHuntAssassinPotExp = Math.Min(BountyHuntAssassinExpCap,
                _bountyHuntAssassinPotExp + BountyHuntAssassinExpPerTick);
            _bountyHuntAssassinPotPoints = Math.Min(BountyHuntAssassinPointsCap,
                _bountyHuntAssassinPotPoints + BountyHuntAssassinPointsPerTick);
        }

        void BroadcastBountyHuntIntel(InfServer.Game.Player mark, int now, bool first)
        {
            if (mark == null || _arena == null)
                return;

            short x = mark._state.positionX;
            short y = mark._state.positionY;
            int hpPct = GetPlayerHealthPercent(mark);
            int enPct = GetPlayerEnergyPercent(mark);
            string sector = DescribeBountyHuntSector(x, y);
            string loc = IsBountyHuntInDropship(mark)
                ? "DROPSHIP (survivor pot paused — assassin pot grows)"
                : string.Format("{0} ({1},{2})", sector, x, y);

            string prefix = first ? "&HUNT INTEL" : "&HUNT PING";
            int waiting = Math.Max(0, _bountyHuntQueue.Count - 1);
            int bong = _config != null ? _config.flag.victoryWarningBong : 5;
            _arena.sendArenaMessage(string.Format(
                "{0}: {1} | {2} | HP {3}% | EN {4}% | Survivor C={5} E={6} P={7} | Assassin C={8} E={9} P={10} | Next: {11} ({12} waiting)",
                prefix, mark._alias, loc, hpPct, enPct,
                _bountyHuntSurvivorPotCash, _bountyHuntSurvivorPotExp, _bountyHuntSurvivorPotPoints,
                _bountyHuntAssassinPotCash, _bountyHuntAssassinPotExp, _bountyHuntAssassinPotPoints,
                FormatBountyHuntQueuePreview(1), waiting), bong);
        }

        bool IsBountyHuntInDropship(InfServer.Game.Player p)
        {
            if (p == null || _eol == null || p.IsSpectator)
                return false;
            return _eol.IsInPioneerDropshipSafeZone(p._state.positionX, p._state.positionY);
        }

        /// <summary>
        /// Mark is on the playable map (not spec, not Pioneer dropship). Bounty bots wait until this is true.
        /// </summary>
        public bool IsBountyHunterMarkInField()
        {
            InfServer.Game.Player mark = _bountyHuntMarked;
            if (mark == null || mark.IsDead || mark.IsSpectator)
                return false;
            return !IsBountyHuntInDropship(mark);
        }

        static int GetPlayerHealthPercent(InfServer.Game.Player p)
        {
            if (p == null)
                return 0;
            Vehicle v = p.ActiveVehicle;
            int max = (v != null && v._type != null && v._type.Hitpoints > 0) ? v._type.Hitpoints : 1;
            int hp = p._state.health;
            if (hp < 0)
                hp = 0;
            int pct = (hp * 100) / max;
            if (pct > 100)
                pct = 100;
            return pct;
        }

        static int GetPlayerEnergyPercent(InfServer.Game.Player p)
        {
            if (p == null)
                return 0;
            Vehicle v = p.ActiveVehicle;
            int max = (v != null && v._type != null && v._type.EnergyMax > 0) ? v._type.EnergyMax : 1;
            int en = p._state.energy;
            if (en < 0)
                en = 0;
            int pct = (en * 100) / max;
            if (pct > 100)
                pct = 100;
            return pct;
        }

        string DescribeBountyHuntSector(short x, short y)
        {
            if (_eol == null || _eol._activeSectors == null)
                return "Map";
            foreach (string s in _eol._activeSectors)
            {
                short minX, maxX, minY, maxY;
                if (!_eol.TryGetSectorBounds(s, out minX, out maxX, out minY, out maxY))
                    continue;
                if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                    return s;
            }
            if (_eol.IsInPioneerDropshipSafeZone(x, y))
                return "Dropship";
            return "Open";
        }

        void OnBountyHuntPlayerKill(InfServer.Game.Player victim, InfServer.Game.Player killer)
        {
            if (victim == null || _bountyHuntMarked == null || victim != _bountyHuntMarked)
            {
                TrackBountyHuntPvPKill(killer, victim);
                return;
            }

            int now = Environment.TickCount;
            if (killer == null || killer._team == victim._team)
            {
                victim.sendMessage(0, "Hunt ended (assassin pot forfeited) — next in the 30k queue takes over.");
                AdvanceBountyHuntFromMark("no assassin payout", applyRemarkCooldown: true, now);
                return;
            }

            double engagement = ComputeBountyHuntEngagementMult(killer, victim, now);
            double speed = ComputeBountyHuntSpeedMult(now);
            double mult = engagement * speed;

            int cash = ApplyEolLifetimeRewardScale(killer, (int)Math.Round(_bountyHuntAssassinPotCash * mult));
            int exp = ApplyEolLifetimeRewardScale(killer, (int)Math.Round(_bountyHuntAssassinPotExp * mult));
            int points = ApplyEolLifetimeRewardScale(killer, (int)Math.Round(_bountyHuntAssassinPotPoints * mult));

            if (cash > 0 || exp > 0 || points > 0)
            {
                killer.Cash += cash;
                killer.Experience += exp;
                killer.BonusPoints += points;
                killer.syncState();
            }

            RecordBountyHuntPairPayout(killer._id, victim._id, now);
            TrackBountyHuntPvPKill(killer, victim);

            int bong = _config != null ? _config.flag.victoryBong : 1;
            _arena.sendArenaMessage(string.Format(
                "&HUNT OVER: {0} eliminated {1}! Assassin pot bonus Cash={2} Exp={3} Pts={4} (x{5:0.00}). Survivor pot was C={6}. Advancing queue…",
                killer._alias, victim._alias, cash, exp, points, mult,
                _bountyHuntSurvivorPotCash), bong);

            if (engagement < 0.5)
                killer.sendMessage(0, "Hunt assassin pot reduced (anti-farm / early kill / recent trade). Normal kill rewards still apply.");

            AdvanceBountyHuntFromMark("eliminated", applyRemarkCooldown: true, now);
        }

        void OnBountyHuntMarkDiedOther(InfServer.Game.Player victim)
        {
            if (victim == null || _bountyHuntMarked == null || victim != _bountyHuntMarked)
                return;
            if (_arena != null)
            {
                _arena.sendArenaMessage(string.Format(
                    "&HUNT ENDED: {0} died — assassin pot forfeited (C={1}). Advancing to next 30k…",
                    victim._alias, _bountyHuntAssassinPotCash),
                    _config != null ? _config.flag.periodicBong : 1);
            }
            AdvanceBountyHuntFromMark("non-pvp death", applyRemarkCooldown: true, Environment.TickCount);
        }

        /// <summary>Wipe hunt state entirely (match end). Does not promote the next target.</summary>
        void ClearBountyHunt(string reason, bool applyRemarkCooldown = true, bool advanceQueue = true)
        {
            if (advanceQueue && _bountyHuntMarked != null)
            {
                AdvanceBountyHuntFromMark(reason, applyRemarkCooldown, Environment.TickCount);
                return;
            }

            if (_bountyHuntMarked != null && _arena != null)
            {
                _arena.sendArenaMessage(string.Format(
                    "&HUNT cancelled ({0}). Survivor pot C={1} | Assassin pot C={2} wiped.",
                    reason, _bountyHuntSurvivorPotCash, _bountyHuntAssassinPotCash),
                    _config != null ? _config.flag.periodicBong : 1);
                if (applyRemarkCooldown)
                    _bountyHuntRemarkReady[_bountyHuntMarked._id] =
                        Environment.TickCount + BountyHuntRemarkCooldownMs;
            }

            ResetBountyHuntPots();
            _bountyHuntMarked = null;
            _bountyHuntQueue.Clear();
            OnBountyHunterHuntEnded(Environment.TickCount, announce: true);
            InvalidateEolHudTickerCache();
            Log.write(TLog.Normal, "[Eol] Bounty hunt cleared: {0}.", reason);
        }

        void ResetBountyHuntPots()
        {
            _bountyHuntSurvivorPotCash = 0;
            _bountyHuntSurvivorPotExp = 0;
            _bountyHuntSurvivorPotPoints = 0;
            _bountyHuntAssassinPotCash = 0;
            _bountyHuntAssassinPotExp = 0;
            _bountyHuntAssassinPotPoints = 0;
        }

        double ComputeBountyHuntSpeedMult(int now)
        {
            int sincePing = now - _bountyHuntLastPingTick;
            if (sincePing < 0)
                sincePing = 0;
            if (sincePing <= BountyHuntSpeedBonusWindowMs)
                return 1.5;
            if (sincePing >= BountyHuntPingIntervalMs)
                return 0.5;
            double t = (double)(sincePing - BountyHuntSpeedBonusWindowMs)
                / (double)(BountyHuntPingIntervalMs - BountyHuntSpeedBonusWindowMs);
            return 1.5 - (t * 1.0);
        }

        double ComputeBountyHuntEngagementMult(InfServer.Game.Player killer, InfServer.Game.Player victim, int now)
        {
            double mult = 1.0;

            int markedFor = now - _bountyHuntMarkStartedTick;
            if (markedFor < BountyHuntMinMarkMsForFullPayout)
                mult *= Math.Max(0.2, (double)markedFor / BountyHuntMinMarkMsForFullPayout);

            Dictionary<int, int> byVictim;
            int lastPair;
            if (_bountyHuntPairCooldown.TryGetValue(killer._id, out byVictim)
                && byVictim.TryGetValue(victim._id, out lastPair)
                && now - lastPair < BountyHuntPairCooldownMs)
            {
                mult *= 0.15;
            }

            if (HadRecentBountyHuntPvP(victim._id, killer._id, now)
                || HadRecentBountyHuntPvP(killer._id, victim._id, now))
            {
                mult *= 0.25;
            }

            if (_bountyHuntFieldAliveMs < BountyHuntTrickleIntervalMs
                && markedFor < BountyHuntMinMarkMsForFullPayout)
            {
                mult *= 0.35;
            }

            if (mult > 1.0)
                mult = 1.0;
            if (mult < 0.05)
                mult = 0.05;
            return mult;
        }

        void RecordBountyHuntPairPayout(int killerId, int victimId, int now)
        {
            Dictionary<int, int> byVictim;
            if (!_bountyHuntPairCooldown.TryGetValue(killerId, out byVictim))
            {
                byVictim = new Dictionary<int, int>();
                _bountyHuntPairCooldown[killerId] = byVictim;
            }
            byVictim[victimId] = now;
        }

        void TrackBountyHuntPvPKill(InfServer.Game.Player killer, InfServer.Game.Player victim)
        {
            if (killer == null || victim == null || killer._team == victim._team)
                return;
            string key = killer._id + ":" + victim._id;
            _bountyHuntRecentPvPKills[key] = Environment.TickCount;
        }

        bool HadRecentBountyHuntPvP(int killerId, int victimId, int now)
        {
            int tick;
            if (!_bountyHuntRecentPvPKills.TryGetValue(killerId + ":" + victimId, out tick))
                return false;
            return now - tick < BountyHuntReciprocityWindowMs;
        }

        void PruneBountyHuntTracking(int now)
        {
            if (_bountyHuntRecentPvPKills.Count > 0)
            {
                var stale = _bountyHuntRecentPvPKills
                    .Where(kv => now - kv.Value > BountyHuntReciprocityWindowMs * 2)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (string k in stale)
                    _bountyHuntRecentPvPKills.Remove(k);
            }

            if (_bountyHuntRemarkReady.Count > 0)
            {
                var done = _bountyHuntRemarkReady
                    .Where(kv => now >= kv.Value)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (int id in done)
                    _bountyHuntRemarkReady.Remove(id);
            }
        }

        string BuildEolBountyHuntTickerLine()
        {
            if (_bountyHuntMarked == null)
            {
                if (_bountyHuntQueue.Count == 0)
                    return null;
                return string.Format("HUNT Q:{0} waiting", _bountyHuntQueue.Count);
            }

            string ds = IsBountyHuntInDropship(_bountyHuntMarked) ? " DS" : "";
            int waiting = Math.Max(0, _bountyHuntQueue.Count - 1);
            return string.Format("HUNT {0}{1} Surv {2} Asn {3} Q+{4}",
                _bountyHuntMarked._alias,
                ds,
                _bountyHuntSurvivorPotCash,
                _bountyHuntAssassinPotCash,
                waiting);
        }

        string BuildEolBountyHuntTickerCompact()
        {
            if (_bountyHuntMarked == null)
            {
                if (_bountyHuntQueue.Count == 0)
                    return null;
                return string.Format("HUNT Q{0}", _bountyHuntQueue.Count);
            }
            return string.Format("HUNT {0} S{1}/A{2} +{3}",
                _bountyHuntMarked._alias,
                _bountyHuntSurvivorPotCash,
                _bountyHuntAssassinPotCash,
                Math.Max(0, _bountyHuntQueue.Count - 1));
        }

        void InvalidateEolHudTickerCache()
        {
            _tickNextEolScheduleTickerBuild = 0;
            _cachedEolMatchTickerLine = null;
            _cachedEolActivityTickerLine = null;
        }
    }
}
