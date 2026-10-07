using System;
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
    public partial class RoamingAttacker : Bot
    {
        protected bool bBoolFreezeActions;
        int _tickLastActionEvalReposition;
        int _tickLastActionEvalSeek;

        int _senseHostilesTick;
        bool _senseHostilesCache;
        List<InfServer.Game.Player> _senseEnemies;
        List<Vehicle> _senseEnemyBots;

        int _combatSeekBurstRemain;
        bool _hasCombatSeekCopy;
        Action.Priority _cachedCombatSeekPri;

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

            bool hostile = false;
            foreach (InfServer.Game.Player p in _senseEnemies)
            {
                if (!PlayerNearRaiderObjectiveEngage(p))
                    continue;
                hostile = true;
                break;
            }
            if (!hostile)
            {
                foreach (Vehicle v in _senseEnemyBots)
                {
                    if (!BotNearRaiderObjectiveEngage(v))
                        continue;
                    hostile = true;
                    break;
                }
            }

            _senseHostilesCache = hostile;
            return hostile;
        }

        public void pollForActions(int now)
        {
            if (bBoolFreezeActions)
                return;

            // BHB: always chase the hunted mark — do not idle on pirate march / formation.
            if (_baseScript != null && _baseScript.IsBountyHunterBotTeam(_team)
                && _baseScript.BountyHunterSquadOperational)
            {
                newAction(Action.Priority.High, Action.Type.seekAndDestroy);
                return;
            }

            // March: skip peaceful reposition/seek plans, but still allow combat when hostiles are near.
            if (_baseScript != null && _baseScript.IsRaiderHordeMarchingToObjective(now)
                && !HostilesNearbyCached(now))
                return;

            // Ally combat call — peel off to help nearby allies.
            if (_baseScript != null && _baseScript.TryAcceptCombatCallAssist(_team, this, now))
            {
                _cachedCombatSeekPri = Action.Priority.High;
                _hasCombatSeekCopy = true;
                _combatSeekBurstRemain = EolBotActionEvalThrottle.CombatPlanBurstPolls;
                newAction(Action.Priority.High, Action.Type.seekAndDestroy);
                return;
            }

            bool hostiles = HostilesNearbyCached(now);
            if (!hostiles)
            {
                _combatSeekBurstRemain = 0;
                _hasCombatSeekCopy = false;
            }

            bool repositionAllowed = (now - _tickLastActionEvalReposition >= EolBotActionEvalThrottle.RoamRepositionMs)
                || _state.health <= EolBotActionEvalThrottle.RoamRepositionHealthBypass;
            Action.Priority repositionP = Action.Priority.None;
            if (repositionAllowed)
            {
                _tickLastActionEvalReposition = now;
                if (hostiles || _state.health <= EolBotActionEvalThrottle.RoamRepositionHealthBypass)
                {
                    repositionP = shouldReposition(now);
                    if (repositionP == Action.Priority.High)
                    {
                        newAction(repositionP, Action.Type.reposition);
                        return;
                    }
                }
            }

            int seekInterval = EolBotActionEvalThrottle.RoamSeekMs;
            if (!hostiles)
                seekInterval *= EolBotActionEvalThrottle.PeaceSeekThrottleMultiplier;

            if (hostiles || now - _tickLastActionEvalSeek >= seekInterval)
            {
                if (hostiles && _hasCombatSeekCopy && _combatSeekBurstRemain > 0)
                {
                    _combatSeekBurstRemain--;
                    newAction(_cachedCombatSeekPri, Action.Type.seekAndDestroy);
                    return;
                }

                _tickLastActionEvalSeek = now;

                Action.Priority seekP = shouldSeekAndDestroy(now);
                if (seekP > Action.Priority.None)
                {
                    _cachedCombatSeekPri = seekP;
                    _hasCombatSeekCopy = hostiles;
                    _combatSeekBurstRemain = hostiles ? EolBotActionEvalThrottle.CombatPlanBurstPolls : 0;
                    newAction(seekP, Action.Type.seekAndDestroy);
                    return;
                }

                _hasCombatSeekCopy = false;
                _combatSeekBurstRemain = 0;
            }

            if (repositionP == Action.Priority.Medium)
            {
                newAction(repositionP, Action.Type.reposition);
                return;
            }
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




        public Action.Priority shouldSeekAndDestroy(int now)
        {
            EnsureHostileLists(now);
            bool anyEngaged = false;
            int inFireRange = 0;
            int inSeekRange = 0;
            foreach (InfServer.Game.Player p in _senseEnemies)
            {
                if (!PlayerNearRaiderObjectiveEngage(p))
                    continue;
                anyEngaged = true;
                if (Helpers.isInRange(c_playerRangeFire, _state, p._state))
                    inFireRange++;
                else if (Helpers.isInRange(Script_Eol.EolBotCombatSeekRangeRaw, _state, p._state))
                    inSeekRange++;
            }
            foreach (Vehicle v in _senseEnemyBots)
            {
                if (!BotNearRaiderObjectiveEngage(v))
                    continue;
                anyEngaged = true;
                if (Helpers.isInRange(c_playerRangeFire, _state, v._state))
                    inFireRange++;
                else if (Helpers.isInRange(Script_Eol.EolBotCombatSeekRangeRaw, _state, v._state))
                    inSeekRange++;
            }
            if (!anyEngaged)
                return Action.Priority.None;
            if (inFireRange >= 1 || inSeekRange >= 1)
                return Action.Priority.High;
            return Action.Priority.Medium;
        }

        /// <summary>Only peel off when nearly dead and badly outnumbered — roamers are meant to press the attack.</summary>
        public Action.Priority shouldReposition(int now)
        {
            EnsureHostileLists(now);
            int threat = 0;
            foreach (InfServer.Game.Player p in _senseEnemies)
            {
                if (PlayerNearRaiderObjectiveEngage(p))
                    threat++;
            }
            foreach (Vehicle v in _senseEnemyBots)
            {
                if (BotNearRaiderObjectiveEngage(v))
                    threat++;
            }

            if (threat == 0)
                return Action.Priority.None;
            if (_state.health <= 8 && threat >= 3)
                return Action.Priority.High;
            return Action.Priority.None;
        }
    }
}
