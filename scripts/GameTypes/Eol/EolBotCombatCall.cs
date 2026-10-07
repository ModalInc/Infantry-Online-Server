using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Short-lived team combat calls so nearby allies can assist a bot already fighting.
    /// </summary>
    public partial class Script_Eol
    {
        public const int CombatCallHoldMs = 8000;
        public const int CombatCallPublishMinMs = 400;
        /// <summary>Inside this range a bot must stop and fight — patrol/march cannot ignore the player.</summary>
        public const int WalkPastEngageRaw = EolBotSelfAggroRange;
        /// <summary>Ally assist radius when a teammate starts fighting (all bot types).</summary>
        public const int CombatCallAssistRange = EolBotSelfAggroRange;

        struct CombatCallState
        {
            public int TickUntil;
            public int TickLastPublish;
            public int CallerBotId;
            public short FightX;
            public short FightY;
            public InfServer.Game.Player PlayerTarget;
            public Vehicle VehicleTarget;
            public HashSet<int> HelperBotIds;
        }

        readonly Dictionary<Team, CombatCallState> _combatCallsByTeam = new Dictionary<Team, CombatCallState>();

        void ClearCombatCalls()
        {
            _combatCallsByTeam.Clear();
        }

        const int CombatCallPruneIntervalMs = 1000;
        int _tickLastCombatCallPrune;

        /// <summary>Drop expired combat-call entries so helper sets do not linger on idle teams.</summary>
        void PruneExpiredCombatCalls(int now)
        {
            if (_combatCallsByTeam.Count == 0)
                return;
            if (_tickLastCombatCallPrune != 0 && now - _tickLastCombatCallPrune < CombatCallPruneIntervalMs)
                return;
            _tickLastCombatCallPrune = now;

            List<Team> remove = null;
            foreach (KeyValuePair<Team, CombatCallState> kv in _combatCallsByTeam)
            {
                CombatCallState st = kv.Value;
                if (st.TickUntil == 0 || now >= st.TickUntil)
                {
                    if (remove == null)
                        remove = new List<Team>();
                    remove.Add(kv.Key);
                }
            }
            if (remove == null)
                return;
            for (int i = 0; i < remove.Count; i++)
                _combatCallsByTeam.Remove(remove[i]);
        }

        /// <summary>Drop any call owned by or assisted by this bot, and clear targets that are this bot.</summary>
        public void ClearCombatCallsForBot(Bot bot)
        {
            if (bot == null || _combatCallsByTeam.Count == 0)
                return;

            List<Team> remove = null;
            List<KeyValuePair<Team, CombatCallState>> updates = null;

            foreach (KeyValuePair<Team, CombatCallState> kv in _combatCallsByTeam)
            {
                CombatCallState st = kv.Value;
                bool changed = false;
                if (st.CallerBotId == bot._id)
                {
                    if (remove == null)
                        remove = new List<Team>();
                    remove.Add(kv.Key);
                    continue;
                }
                if (st.VehicleTarget == bot)
                {
                    st.VehicleTarget = null;
                    changed = true;
                }
                if (st.HelperBotIds != null && st.HelperBotIds.Remove(bot._id))
                    changed = true;

                if (changed)
                {
                    if (st.PlayerTarget == null && st.VehicleTarget == null)
                    {
                        if (remove == null)
                            remove = new List<Team>();
                        remove.Add(kv.Key);
                    }
                    else
                    {
                        if (updates == null)
                            updates = new List<KeyValuePair<Team, CombatCallState>>();
                        updates.Add(new KeyValuePair<Team, CombatCallState>(kv.Key, st));
                    }
                }
            }

            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                    _combatCallsByTeam.Remove(remove[i]);
            }
            if (updates != null)
            {
                for (int i = 0; i < updates.Count; i++)
                    _combatCallsByTeam[updates[i].Key] = updates[i].Value;
            }
        }

        /// <summary>Clear calls targeting a player who just died / left.</summary>
        public void ClearCombatCallsForPlayer(InfServer.Game.Player player)
        {
            if (player == null || _combatCallsByTeam.Count == 0)
                return;

            List<Team> remove = null;
            foreach (KeyValuePair<Team, CombatCallState> kv in _combatCallsByTeam)
            {
                if (kv.Value.PlayerTarget == player)
                {
                    if (remove == null)
                        remove = new List<Team>();
                    remove.Add(kv.Key);
                }
            }
            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                    _combatCallsByTeam.Remove(remove[i]);
            }
        }

        /// <summary>
        /// Publish or refresh a team combat call. Same-target refreshes extend the timer without
        /// resetting helper slots. New targets replace the call.
        /// </summary>
        public void PublishCombatCall(Team team, Bot caller, InfServer.Game.Player playerTarget, Vehicle vehicleTarget, int now)
        {
            if (team == null || caller == null || caller.IsDead)
                return;
            if (playerTarget == null && vehicleTarget == null)
                return;
            if (playerTarget != null && (playerTarget.IsDead || playerTarget._team == team))
                return;
            if (vehicleTarget != null && (vehicleTarget.IsDead || !ShouldEolBotTeamsFight(team, vehicleTarget._team)))
                return;

            CombatCallState st;
            bool existed = _combatCallsByTeam.TryGetValue(team, out st);
            bool sameTarget = existed
                && st.PlayerTarget == playerTarget
                && st.VehicleTarget == vehicleTarget
                && st.TickUntil != 0
                && now < st.TickUntil;

            if (sameTarget)
            {
                if (st.TickLastPublish != 0 && now - st.TickLastPublish < CombatCallPublishMinMs)
                {
                    // Still extend expiry lightly so the call does not flap while throttled.
                    st.TickUntil = now + CombatCallHoldMs;
                    st.FightX = caller._state.positionX;
                    st.FightY = caller._state.positionY;
                    _combatCallsByTeam[team] = st;
                    return;
                }
            }
            else
            {
                st = new CombatCallState();
                st.HelperBotIds = new HashSet<int>();
            }

            if (st.HelperBotIds == null)
                st.HelperBotIds = new HashSet<int>();

            st.TickUntil = now + CombatCallHoldMs;
            st.TickLastPublish = now;
            st.CallerBotId = caller._id;
            st.FightX = caller._state.positionX;
            st.FightY = caller._state.positionY;
            st.PlayerTarget = playerTarget;
            st.VehicleTarget = vehicleTarget;
            _combatCallsByTeam[team] = st;
        }

        public int GetCombatCallAssistRange(Bot bot)
        {
            return CombatCallAssistRange;
        }

        /// <summary>
        /// True when <paramref name="helper"/> should break patrol / soft idle to help an ally fight.
        /// Registers the helper into the call's slot list.
        /// </summary>
        public bool TryAcceptCombatCallAssist(Team team, Bot helper, int now)
        {
            InfServer.Game.Player unusedP;
            Vehicle unusedV;
            return TryGetCombatCallAssistTarget(team, helper, now, out unusedP, out unusedV, registerHelper: true);
        }

        /// <summary>
        /// Resolves an active assist target for targeting / evaluation. When <paramref name="registerHelper"/>
        /// is true, registers the helper (once per bot).
        /// </summary>
        public bool TryGetCombatCallAssistTarget(
            Team team,
            Bot helper,
            int now,
            out InfServer.Game.Player playerTarget,
            out Vehicle vehicleTarget,
            bool registerHelper = false)
        {
            playerTarget = null;
            vehicleTarget = null;
            if (team == null || helper == null || helper.IsDead)
                return false;

            CombatCallState st;
            if (!_combatCallsByTeam.TryGetValue(team, out st))
                return false;
            if (st.TickUntil == 0 || now >= st.TickUntil)
            {
                _combatCallsByTeam.Remove(team);
                return false;
            }
            if (st.CallerBotId == helper._id)
                return false;

            // Caller's fight must still be near enough for this helper.
            int assistRange = GetCombatCallAssistRange(helper);
            Helpers.ObjectState fight = new Helpers.ObjectState
            {
                positionX = st.FightX,
                positionY = st.FightY
            };
            if (!Helpers.isInRange(assistRange, helper._state, fight))
                return false;

            // Validate live targets.
            if (st.PlayerTarget != null && !st.PlayerTarget.IsDead && st.PlayerTarget._team != team)
                playerTarget = st.PlayerTarget;
            if (st.VehicleTarget != null && !st.VehicleTarget.IsDead
                && ShouldEolBotTeamsFight(team, st.VehicleTarget._team))
                vehicleTarget = st.VehicleTarget;

            if (playerTarget == null && vehicleTarget == null)
            {
                _combatCallsByTeam.Remove(team);
                return false;
            }

            if (registerHelper)
            {
                if (st.HelperBotIds == null)
                    st.HelperBotIds = new HashSet<int>();
                // Drop dead/condemned helpers so slots reopen as soon as a bot dies.
                PruneDeadCombatCallHelpers(ref st);
                if (!st.HelperBotIds.Contains(helper._id))
                    st.HelperBotIds.Add(helper._id);
                _combatCallsByTeam[team] = st;
            }

            return true;
        }

        /// <summary>
        /// Removes helper IDs whose bots are gone, dead, or condemned so assist slots free up.
        /// </summary>
        void PruneDeadCombatCallHelpers(ref CombatCallState st)
        {
            if (st.HelperBotIds == null || st.HelperBotIds.Count == 0)
                return;

            List<int> remove = null;
            foreach (int id in st.HelperBotIds)
            {
                Bots.Bot b = FindTrackedBotById(id);
                if (b == null || b.IsDead || b.bCondemned)
                {
                    if (remove == null)
                        remove = new List<int>();
                    remove.Add(id);
                }
            }
            if (remove == null)
                return;
            for (int i = 0; i < remove.Count; i++)
                st.HelperBotIds.Remove(remove[i]);
        }

        Bots.Bot FindTrackedBotById(int botId)
        {
            if (_bots == null)
                return null;
            foreach (Bots.Bot b in _bots)
            {
                if (b != null && b._id == botId)
                    return b;
            }
            return null;
        }

        /// <summary>True when a bot may engage this player (no per-player attacker cap).</summary>
        public bool CanBotEngagePlayer(Bot self, InfServer.Game.Player player)
        {
            return player != null && !player.IsDead;
        }

        /// <summary>Live bot currently fighting this player this poll (patrol leftovers do not count).</summary>
        static bool IsBotActivelyEngaging(Bots.Bot bot, InfServer.Game.Player player)
        {
            if (bot == null || player == null || bot.IsDead || bot.bCondemned)
                return false;
            if (!GetBotInLiveCombat(bot))
                return false;
            return BotTargetsPlayer(bot, player);
        }

        static bool GetBotInLiveCombat(Bots.Bot bot)
        {
            if (bot is BasicDefense bd)
                return bd.InLiveCombat;
            if (bot is Captain cap)
                return cap.InLiveCombat;
            if (bot is AlienHorde ah)
                return ah.InLiveCombat;
            if (bot is RoamingAttacker ra)
                return ra.InLiveCombat;
            if (bot is RoamingCaptain rc)
                return rc.InLiveCombat;
            return false;
        }

        static bool BotTargetsPlayer(Bots.Bot bot, InfServer.Game.Player player)
        {
            if (bot == null || player == null)
                return false;
            InfServer.Game.Player t = GetBotActivePlayerTarget(bot);
            return t == player;
        }

        static InfServer.Game.Player GetBotActivePlayerTarget(Bots.Bot bot)
        {
            if (bot is BasicDefense bd)
                return bd.ActivePlayerTarget;
            if (bot is Captain cap)
                return cap.ActivePlayerTarget;
            if (bot is AlienHorde ah)
                return ah.ActivePlayerTarget;
            if (bot is RoamingAttacker ra)
                return ra.ActivePlayerTarget;
            if (bot is RoamingCaptain rc)
                return rc.ActivePlayerTarget;
            return null;
        }

        /// <summary>Clear stalked player on death/remove so engage-cap recounts immediately.</summary>
        static void ClearBotActivePlayerTarget(Bots.Bot bot)
        {
            if (bot is BasicDefense bd)
                bd.ClearActivePlayerTarget();
            else if (bot is Captain cap)
                cap.ClearActivePlayerTarget();
            else if (bot is AlienHorde ah)
                ah.ClearActivePlayerTarget();
            else if (bot is RoamingAttacker ra)
                ra.ClearActivePlayerTarget();
            else if (bot is RoamingCaptain rc)
                rc.ClearActivePlayerTarget();
        }

        /// <summary>Prefer the combat-call player when still valid and in consideration range.</summary>
        public InfServer.Game.Player PreferCombatCallPlayer(
            Team team,
            Bot self,
            int now,
            int maxRangeRaw,
            InfServer.Game.Player fallback)
        {
            InfServer.Game.Player callP;
            Vehicle callV;
            if (!TryGetCombatCallAssistTarget(team, self, now, out callP, out callV, registerHelper: false))
                return fallback;
            if (callP == null || callP.IsDead)
                return fallback;
            if (maxRangeRaw > 0 && !Helpers.isInRange(maxRangeRaw, self._state, callP._state))
                return fallback;
            return callP;
        }
    }
}
