using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>HQ-under-attack state for defense bot teams.</summary>
    public partial class Script_Eol
    {
        public const int HqUnderAttackHoldMs = 5000;
        public const int HqDefenseRadiusRaw = 1500;
        /// <summary>Enemy player this close to HQ recalls outbound defense patrol bots.</summary>
        public const int HqPatrolRecallRadiusRaw = 900;

        struct DefenseTeamState
        {
            public int TickHqUnderAttackUntil;
            public short LastHqHealth;
            public bool HqHealthInitialized;
            public bool HostilePlayerNearHqRecall;
        }

        readonly Dictionary<Team, DefenseTeamState> _defenseTeamState = new Dictionary<Team, DefenseTeamState>();

        void ClearDefenseTeamState()
        {
            _defenseTeamState.Clear();
            ClearCombatCalls();
        }

        DefenseTeamState GetDefenseTeamState(Team team)
        {
            if (team == null)
                return default;

            if (!_defenseTeamState.TryGetValue(team, out DefenseTeamState state))
                state = default;
            return state;
        }

        void SetDefenseTeamState(Team team, DefenseTeamState state)
        {
            if (team == null)
                return;
            _defenseTeamState[team] = state;
        }

        public bool IsHqUnderAttack(Team team, int now)
        {
            if (team == null)
                return false;

            DefenseTeamState state = GetDefenseTeamState(team);
            return state.TickHqUnderAttackUntil != 0 && now < state.TickHqUnderAttackUntil;
        }

        /// <summary>Cached each <see cref="PollDefenseHqWatch"/> — enemy player within <see cref="HqPatrolRecallRadiusRaw"/> of HQ.</summary>
        public bool IsHqPatrolRecallActive(Team team)
        {
            if (team == null)
                return false;
            return GetDefenseTeamState(team).HostilePlayerNearHqRecall;
        }

        /// <summary>True when a hostile player is within <paramref name="radiusRaw"/> of the team's HQ.</summary>
        public bool HasHostilePlayerNearHq(Team team, int radiusRaw)
        {
            if (team == null || _arena == null || radiusRaw <= 0)
                return false;

            Vehicle hq = FindHqVehicleForTeam(team);
            if (hq == null || hq.IsDead)
                return false;

            List<InfServer.Game.Player> nearHq = _arena.getPlayersInRange(
                hq._state.positionX, hq._state.positionY, radiusRaw);
            for (int i = 0; i < nearHq.Count; i++)
            {
                InfServer.Game.Player p = nearHq[i];
                if (p == null || p.IsDead || p._team == null || p._team == team)
                    continue;
                if (!EolBotSenseUtil.BotCanSeePlayer(hq._state, p))
                    continue;
                return true;
            }

            return false;
        }

        public void SignalHqUnderAttack(Team team, int now)
        {
            if (team == null)
                return;

            DefenseTeamState state = GetDefenseTeamState(team);
            state.TickHqUnderAttackUntil = now + HqUnderAttackHoldMs;
            SetDefenseTeamState(team, state);
        }

        public Captain FindDefenseCaptainForTeam(Team team)
        {
            if (team == null || _bots == null)
                return null;

            foreach (Bot b in _bots)
            {
                if (b == null || b.IsDead || b._team != team)
                    continue;
                if (b is Captain captain)
                    return captain;
            }

            return null;
        }

        /// <summary>True when a hostile near HQ is within the bot's engage range.</summary>
        public bool HasHqThreatInBotEngageRange(Team team, short botX, short botY, int fireRangeRaw, int seekRangeRaw)
        {
            if (team == null || _arena == null)
                return false;

            Vehicle hq = FindHqVehicleForTeam(team);
            if (hq == null || hq.IsDead)
                return false;

            Helpers.ObjectState bot = new Helpers.ObjectState { positionX = botX, positionY = botY };

            List<InfServer.Game.Player> nearHq = _arena.getPlayersInRange(
                hq._state.positionX, hq._state.positionY, HqDefenseRadiusRaw);
            for (int i = 0; i < nearHq.Count; i++)
            {
                InfServer.Game.Player p = nearHq[i];
                if (p == null || p.IsDead || p._team == null || p._team == team)
                    continue;
                if (!EolBotSenseUtil.BotCanSeePlayer(bot, p))
                    continue;
                if (Helpers.isInRange(fireRangeRaw, bot, p._state))
                    return true;
                if (Helpers.isInRange(seekRangeRaw, bot, p._state))
                    return true;
            }

            List<Vehicle> nearBots = _arena.getVehiclesInRange(
                hq._state.positionX, hq._state.positionY, HqDefenseRadiusRaw,
                delegate (Vehicle v) { return v is Bot; });
            for (int i = 0; i < nearBots.Count; i++)
            {
                Vehicle v = nearBots[i];
                if (v == null || v.IsDead || !ShouldEolBotTeamsFight(team, v._team))
                    continue;
                if (Helpers.isInRange(fireRangeRaw, bot, v._state))
                    return true;
                if (Helpers.isInRange(seekRangeRaw, bot, v._state))
                    return true;
            }

            return false;
        }

        /// <summary>Watch HQ health and nearby hostiles; sets team defend-at-all-costs when HQ is threatened.</summary>
        public void PollDefenseHqWatch(int now)
        {
            if (_arena == null || !_arena._bGameRunning)
                return;

            foreach (Team team in _arena.ActiveTeams)
            {
                if (team == null || !IsDefenseBotTeam(team))
                    continue;

                Vehicle hq = FindHqVehicleForTeam(team);
                if (hq == null || hq.IsDead)
                    continue;

                DefenseTeamState state = GetDefenseTeamState(team);

                if (state.HqHealthInitialized && hq._state.health < state.LastHqHealth)
                    state.TickHqUnderAttackUntil = now + HqUnderAttackHoldMs;

                state.LastHqHealth = hq._state.health;
                state.HqHealthInitialized = true;

                state.HostilePlayerNearHqRecall = HasHostilePlayerNearHq(team, HqPatrolRecallRadiusRaw);

                if (state.TickHqUnderAttackUntil == 0 || now >= state.TickHqUnderAttackUntil)
                {
                    List<InfServer.Game.Player> nearHq = _arena.getPlayersInRange(
                        hq._state.positionX, hq._state.positionY, HqDefenseRadiusRaw);
                    for (int i = 0; i < nearHq.Count; i++)
                    {
                        InfServer.Game.Player p = nearHq[i];
                        if (p == null || p.IsDead || p._team == null || p._team == team)
                            continue;
                        if (!EolBotSenseUtil.BotCanSeePlayer(hq._state, p))
                            continue;
                        state.TickHqUnderAttackUntil = now + HqUnderAttackHoldMs;
                        break;
                    }
                }

                SetDefenseTeamState(team, state);
            }
        }
    }
}
