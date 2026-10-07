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
using InfServer.Game.Modules.BotActions;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Eol
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class Captain : Bot
    {

        private List<Action> _actionQueue;

        /// <summary>Drop poll/AI scratch on death/despawn so lists and paths do not linger.</summary>
        void ReleasePollScratch()
        {
            if (_actionQueue != null)
                _actionQueue.Clear();
            _path = null;
            _senseEnemies = null;
            _senseEnemyBots = null;
            _senseHostilesTick = 0;
            _combatFireBurstRemain = 0;
            ClearActivePlayerTarget();
            try { Navigation?.Clear(); }
            catch { /* teardown */ }
            HoldPosition();
        }
        /// <summary>Returns true when a target was locked and combat movement ran.</summary>
        public bool fireAtEnemy(int now)
        {
            Bot botTarget;
            if (!EolBotActionBridge.RunCombatKeepSticky(
                    this, InfServer.Game.Modules.BotActions.BotActionId.AttackPlayer,
                    ref _target, out botTarget, now))
            {
                bool inSight = false;
                _target = getTargetPlayer(ref inSight);
            }

            if (_target != null && !_target.IsDead)
            {
                EolBotCombatUtil.ChaseTargetWithoutLos(this, now, _target._state, steerForPersuePlayer);
                InLiveCombat = true;
                if (_baseScript != null)
                    _baseScript.PublishCombatCall(_team, this, _target, null, now);
                return true;
            }

            if (botTarget != null)
            {
                EolBotCombatUtil.ChaseTargetWithoutLos(this, now, botTarget._state, steerForPersuePlayer);
                InLiveCombat = true;
                if (_baseScript != null)
                    _baseScript.PublishCombatCall(_team, this, null, botTarget, now);
                return true;
            }

            InLiveCombat = false;
            return false;
        }

        public void patrolHQ(int now)
        {
            if (myvHq == null)
                return;

            EolHqPatrol.TickDefensePatrol(
                this, _arena, myvHq, now, _hqPatrol, c_pathUpdateInterval, _baseScript, _team);
            PublishPatrolState();
        }

        public void ReturnToHQ(int now)
        {
            if (myvHq == null)
                return;
            EolBotActionBridge.Run(this, InfServer.Game.Modules.BotActions.BotActionId.Retreat, now);
        }

        void PublishPatrolState()
        {
            _targetPoint = _hqPatrol.Dest;
            _patrolReturning = _hqPatrol.ReturningHome;
            _pathTarget = Navigation.PathTarget;
            if (Navigation.Path is List<Vector3> list)
                _path = list;
            else if (Navigation.Path != null)
                _path = new List<Vector3>(Navigation.Path);
            else
                _path = null;
        }
        public Helpers.ObjectState getTargetPoint()
        {
            return EolHqPatrol.PickOutbound(this, _arena, myvHq);
        }

        public Helpers.ObjectState getTargetHQ()
        {
            return EolHqPatrol.PickNearHq(this, _arena, myvHq);
        }
        public class Action
        {
            public Priority priority;
            public Type type;

            public Action(Priority pr, Type ty)
            {
                type = ty;
                priority = pr;
            }

            public enum Priority
            {
                None,
                Low,
                Medium,
                High
            }

            public enum Type
            {
                fireAtEnemy,
                retreat
            }
        }

        void ApplyEolPathState(EolBotPathState s)
        {
            _tickLastPath = s.TickLastPath;
            if (s.AssignPath)
            {
                _path = s.Path;
                _pathTarget = s.PathTarget;
            }
        }

        EolBotPathState GetEolPathState()
        {
            EolBotPathState s = new EolBotPathState();
            s.Path = _path;
            s.PathTarget = _pathTarget;
            s.TickLastPath = _tickLastPath;
            return s;
        }
    }
}
