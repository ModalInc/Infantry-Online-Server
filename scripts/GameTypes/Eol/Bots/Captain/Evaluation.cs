using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;


using InfServer.Game;
using InfServer.Protocol;
using InfServer.Scripting;
using InfServer.Bots;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Eol
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class Captain : Bot
    {
        protected bool bBoolFreezeActions;
        int _tickLastActionEvalRetreat;

        int _senseHostilesTick;
        bool _senseHostilesCache;
        List<InfServer.Game.Player> _senseEnemies;
        List<Vehicle> _senseEnemyBots;

        int _combatFireBurstRemain;
        Action.Priority _cachedFirePri;

        void RefreshHostileLists()
        {
            _senseEnemies = enemiesInRange();
            _senseEnemyBots = getEnemyBotsInRange();
        }

        void EnsureHostileLists(int now)
        {
            if (_senseHostilesTick == now && _senseEnemies != null && _senseEnemyBots != null)
                return;
            RefreshHostileLists();
        }

        protected bool HostilesNearbyCached(int now)
        {
            int ttl = EolBotActionEvalThrottle.HostileSenseReuseMs;
            if (_senseHostilesTick != 0 && now - _senseHostilesTick < ttl && now >= _senseHostilesTick)
                return _senseHostilesCache;

            _senseHostilesTick = now;
            RefreshHostileLists();
            bool closeEnemyBot = false;
            if (_senseEnemyBots != null)
            {
                for (int i = 0; i < _senseEnemyBots.Count; i++)
                {
                    Vehicle v = _senseEnemyBots[i];
                    if (v == null || v.IsDead)
                        continue;
                    if (Helpers.isInRange(Script_Eol.EolBotCloseAlertRangeRaw, _state, v._state))
                    {
                        closeEnemyBot = true;
                        break;
                    }
                }
            }
            _senseHostilesCache = (_senseEnemies != null && _senseEnemies.Count > 0) || closeEnemyBot;
            return _senseHostilesCache;
        }

        public void pollForActions(int now)
        {
            if (bBoolFreezeActions)
                return;

            bool hqUnderAttack = _baseScript != null && _baseScript.IsHqUnderAttack(_team, now);
            if (hqUnderAttack && _baseScript.HasHqThreatInBotEngageRange(
                    _team, _state.positionX, _state.positionY, c_playerRangeFire, Script_Eol.EolBotCombatSeekRangeRaw))
            {
                _cachedFirePri = Action.Priority.High;
                _combatFireBurstRemain = EolBotActionEvalThrottle.CombatPlanBurstPolls;
                newAction(Action.Priority.High, Action.Type.fireAtEnemy);
                return;
            }

            // Ally already fighting nearby — break patrol and help.
            if (_baseScript != null && _baseScript.TryAcceptCombatCallAssist(_team, this, now))
            {
                _cachedFirePri = Action.Priority.High;
                _combatFireBurstRemain = EolBotActionEvalThrottle.CombatPlanBurstPolls;
                newAction(Action.Priority.High, Action.Type.fireAtEnemy);
                return;
            }

            bool hostiles = HostilesNearbyCached(now);
            bool combatSticky = EolBotSenseUtil.ShouldBreakIdleForCombat(
                _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target);
            if (!hostiles && !combatSticky)
                _combatFireBurstRemain = 0;

            bool retreatAllowed = !hqUnderAttack
                && ((now - _tickLastActionEvalRetreat >= EolBotActionEvalThrottle.DefenseRetreatMs)
                || _state.health <= EolBotActionEvalThrottle.DefenseRetreatHealthBypass);
            Action.Priority retreatP = Action.Priority.None;
            if (retreatAllowed)
            {
                _tickLastActionEvalRetreat = now;
                if (hostiles || _state.health <= EolBotActionEvalThrottle.DefenseRetreatHealthBypass)
                {
                    retreatP = shouldRetreat(now);
                    if (retreatP == Action.Priority.High)
                    {
                        newAction(retreatP, Action.Type.retreat);
                        return;
                    }
                }
            }

            if (hostiles && _combatFireBurstRemain > 0)
            {
                _combatFireBurstRemain--;
                newAction(_cachedFirePri, Action.Type.fireAtEnemy);
                return;
            }

            Action.Priority fireP = shouldfireAtEnemy(now);
            if (fireP > Action.Priority.None)
            {
                if (hostiles)
                {
                    _cachedFirePri = fireP;
                    _combatFireBurstRemain = EolBotActionEvalThrottle.CombatPlanBurstPolls;
                }
                newAction(fireP, Action.Type.fireAtEnemy);
                return;
            }

            if (hqUnderAttack)
            {
                newAction(Action.Priority.Medium, Action.Type.retreat);
                return;
            }

            if (retreatP == Action.Priority.Medium || retreatP == Action.Priority.Low)
                newAction(retreatP, Action.Type.retreat);
        }

        public void newAction(Action.Priority priority, Action.Type type)
        {
            for (int i = _actionQueue.Count - 1; i >= 0; i--)
            {
                Action existing = _actionQueue[i];
                if (existing.type != type)
                    continue;
                if (priority <= existing.priority)
                    return;
                _actionQueue.RemoveAt(i);
                break;
            }
            _actionQueue.Add(new Action(priority, type));
        }




        public Action.Priority shouldfireAtEnemy(int now)
        {
            EnsureHostileLists(now);
            int inFireRange = 0;
            int inSeekRange = 0;
            foreach (InfServer.Game.Player enemy in _senseEnemies)
            {
                if (Helpers.isInRange(c_playerRangeFire, _state, enemy._state))
                    inFireRange++;
                else if (Helpers.isInRange(Script_Eol.EolBotCombatSeekRangeRaw, _state, enemy._state))
                    inSeekRange++;
            }
            foreach (Vehicle v in _senseEnemyBots)
            {
                if (Helpers.isInRange(c_playerRangeFire, _state, v._state)
                    || Helpers.isInRange(Script_Eol.EolBotCloseAlertRangeRaw, _state, v._state))
                    inFireRange++;
            }

            if (inFireRange > 0)
                return Action.Priority.High;
            int closeAlert = 0;
            foreach (InfServer.Game.Player enemy in _senseEnemies)
            {
                if (Helpers.isInRange(Script_Eol.EolBotCloseAlertRangeRaw, _state, enemy._state))
                    closeAlert++;
            }
            if (closeAlert > 0)
                return Action.Priority.High;
            if (inSeekRange > 0)
                return Action.Priority.High;
            return Action.Priority.None;
        }

        public Action.Priority shouldRetreat(int now)
        {
            EnsureHostileLists(now);
            List<InfServer.Game.Player> friendlies = friendliesInRange();
            List<Vehicle> friendlyBots = getFriendlyBotsInRange();
            int enemycount = _senseEnemies.Count + _senseEnemyBots.Count;
            int friendlycount = friendlies.Count + friendlyBots.Count;

            if (enemycount == 0)
                return Action.Priority.None;

            if (_state.health <= 6)
                return Action.Priority.High;

            bool threatInFireRange = false;
            foreach (InfServer.Game.Player enemy in _senseEnemies)
            {
                if (Helpers.isInRange(c_playerRangeFire, _state, enemy._state))
                {
                    threatInFireRange = true;
                    break;
                }
            }

            if (threatInFireRange)
            {
                if (enemycount >= 8 && friendlycount <= 1)
                    return Action.Priority.High;
                return Action.Priority.None;
            }

            if (_state.health <= 6)
                return Action.Priority.High;

            return Action.Priority.None;
        }
    }
}
