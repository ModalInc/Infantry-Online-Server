using System;
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
    public partial class RoamingCaptain : Bot
    {

        private List<Action> _actionQueue;
        /// <summary>Last combat target id (player or vehicle) â€” only wipe Navigation when it changes.</summary>
        int _combatNavTargetId;

        /// <summary>Drop poll/AI scratch on death/despawn so lists and paths do not linger.</summary>
        void ReleasePollScratch()
        {
            if (_actionQueue != null)
                _actionQueue.Clear();
            _path = null;
            _senseEnemies = null;
            _senseEnemyBots = null;
            _senseHostilesTick = 0;
            _combatSeekBurstRemain = 0;
            _hasCombatSeekCopy = false;
            _combatNavTargetId = 0;
            ClearActivePlayerTarget();
            try { Navigation?.Clear(); }
            catch { /* teardown */ }
            HoldPosition();
        }

        public void seekAndDestroy(int now)
        {
            if (_baseScript != null && _baseScript.IsBountyHunterBotTeam(_team))
            {
                InfServer.Game.Player mark = _baseScript.BountyHuntMarked;
                if (!_baseScript.IsBountyHunterMarkInField())
                {
                    _target = null;
                    InLiveCombat = false;
                    HoldPosition();
                    return;
                }
                if (mark != null && !mark.IsDead && !mark.IsSpectator)
                {
                    _target = mark;
                    _baseScript.NoteBountyHunterMarkSighting(mark);
                    Bot ignoreBot;
                    EolBotActionBridge.RunCombatKeepSticky(
                        this, InfServer.Game.Modules.BotActions.BotActionId.SeekEnemy,
                        ref _target, out ignoreBot, now);
                    _target = mark;
                    EolBotCombatUtil.ChaseTargetWithoutLos(this, now, mark._state, steerForPersuePlayer);
                    InLiveCombat = true;
                    _baseScript.PublishCombatCall(_team, this, mark, null, now);
                    return;
                }
                short lx, ly;
                if (_baseScript.TryGetBountyHunterLastKnown(out lx, out ly))
                {
                    Helpers.ObjectState last = new Helpers.ObjectState { positionX = lx, positionY = ly };
                    ApplyNavigationApproach(last, steerForPersuePlayer, new BotNavigateOptions
                    {
                        RequeueMinMs = EolBotPhysicsUtil.PathRequeueMinMsRoamCaptain,
                        PathStartIndex = 1,
                        Priority = PathPriority.Combat,
                    });
                    InLiveCombat = false;
                    return;
                }
            }

            Bot botTarget;
            EolBotActionBridge.RunCombatKeepSticky(
                this, InfServer.Game.Modules.BotActions.BotActionId.SeekEnemy,
                ref _target, out botTarget, now);
            if (_target != null && !_target.IsDead)
            {
                EolBotCombatUtil.ChaseTargetWithoutLos(this, now, _target._state, steerForPersuePlayer);
                InLiveCombat = true;
                if (_baseScript != null)
                    _baseScript.PublishCombatCall(_team, this, _target, null, now);
                return;
            }
            if (botTarget != null)
            {
                EolBotCombatUtil.ChaseTargetWithoutLos(this, now, botTarget._state, steerForPersuePlayer);
                InLiveCombat = true;
                if (_baseScript != null)
                    _baseScript.PublishCombatCall(_team, this, null, botTarget, now);
                return;
            }
            InLiveCombat = false;
        }

        public void repositionFromDanger(int now)
        {
            bool ok = EolBotActionBridge.Run(this, InfServer.Game.Modules.BotActions.BotActionId.PressEngage, now);
            Bot botTarget;
            EolBotActionBridge.SyncCombatTargetsFromActions(this, ref _target, out botTarget);
            InLiveCombat = ok && ((_target != null && !_target.IsDead) || botTarget != null);
        }

        public void anchorRoamNearPdb(int now)
        {
            EolBotActionBridge.Run(this, InfServer.Game.Modules.BotActions.BotActionId.RaidObjective, now);
        }

        void anchorRoamCollectivePoint(int now)
        {
            SyncRaiderCollectiveRoamRevision();

            if (!_baseScript.RaiderCollectiveRoamValid)
            {
                _baseScript.CaptainInitRaiderFlagCircuit(_state.positionX, _state.positionY);
                _tickAnchorRoamPick = now;
            }

            short cx;
            short cy;
            if (!_baseScript.TryGetRaiderCollectiveRoamPoint(out cx, out cy))
            {
                HoldPosition();
                return;
            }

            // Captain always drives straight at the selected flag (no local wander hop).
            if (_targetPoint == null
                || _targetPoint.positionX != cx
                || _targetPoint.positionY != cy)
            {
                _targetPoint = new Helpers.ObjectState { positionX = cx, positionY = cy };
                _path = null;
                _tickLastPath = 0;
                Navigation.Clear();
                EolHqPatrol.ResetSteerState(_roamSteer);
            }

            steerTowardRoamWaypoint(now);
        }

        void steerTowardRoamWaypoint(int now)
        {
            if (_targetPoint == null)
            {
                HoldPosition();
                return;
            }

            EolHqPatrol.EolSteerTickResult result = EolHqPatrol.TickSteerToDest(
                this, _arena, now, _roamSteer, _targetPoint,
                Script_Eol.RaiderRoamWaypointArrivalRaw,
                EolHqPatrol.OutboundArriveLooseRaw);

            _path = null;
            _pathTarget = 0;

            if (result == EolHqPatrol.EolSteerTickResult.Arrived
                || result == EolHqPatrol.EolSteerTickResult.NeedsRepick)
            {
                _targetPoint = null;
                _tickLastPath = 0;
                Navigation.Clear();
                EolHqPatrol.ResetSteerState(_roamSteer);
            }
        }

        void PublishRoamNavState()
        {
            _pathTarget = Navigation.PathTarget;
            if (Navigation.Path is List<Vector3> list)
                _path = list;
            else if (Navigation.Path != null)
                _path = new List<Vector3>(Navigation.Path);
            else
                _path = null;
        }

        public void pushToEnemyFlag(int now)
        {
            // Maintain roaming bots — pirate raid grunts spawn from Script_Eol.PollPirateRaidGruntSpawn (batched cap).
            // Cap is captain + grunts combined (not grunts alone).
            bool roomForMoreRoams = _team == _baseScript.botTeam4
                ? (!_baseScript.IsPirateRaidWaveActive && _baseScript.CanSpawnRoamCombatBot(_team))
                : _baseScript.CanSpawnRoamCombatBot(_team);
            if (_baseScript.capRoamBots.ContainsKey(_team) && roomForMoreRoams && now - _tickLastSpawn > 15000)
            {
                _baseScript.addBotRoam(null, _state, _team);
                _tickLastSpawn = now;
            }

            short hx, hy;
            if (!_baseScript.TryGetPirateRaidHordeReferencePoint(out hx, out hy))
                return;

            {
                Helpers.ObjectState target = new Helpers.ObjectState();
                target.positionX = hx;
                target.positionY = hy;


                Double dis = Math.Pow((Math.Pow(_state.positionX - target.positionX, 2) + Math.Pow(_state.positionY - target.positionY, 2)) / 2, 0.5);

                if (dis < 100)
                {
                    _targetPoint = getTargetPoint();
                    Navigation.Clear();
                }
                else
                {
                    Helpers.ObjectState hPoint = getHPoint();
                    if (_targetPoint == null || hPoint == null
                        || _targetPoint.positionX != hPoint.positionX
                        || _targetPoint.positionY != hPoint.positionY)
                    {
                        _targetPoint = hPoint;
                        Navigation.Clear();
                    }
                    else
                        _targetPoint = hPoint;
                }

                if (_targetPoint == null)
                    return;

                bool bClearPath = Helpers.calcBresenhemsPredicate(
                       _arena, _state.positionX, _state.positionY, _targetPoint.positionX, _targetPoint.positionY,
                       delegate (LvlInfo.Tile t)
                       {
                           return !t.Blocked;
                       }
                   );
                double distSteerFlag = (_state.position() - _targetPoint.position()).Length;
                if (EolBotPhysicsUtil.PreferDirectEngageMovementSteering(bClearPath, distSteerFlag))
                {
                    ApplySteer(steerForFollowOwner);
                }
                else
                {
                    if (Navigation.State == BotNavigationState.Failed)
                        Navigation.Clear();

                    if (!EolBotPathBudget.AllowIdlePath(_arena))
                    {
                        ApplySteer(steerForFollowOwner);
                        return;
                    }

                    ApplyNavigationApproach(_targetPoint, null, new BotNavigateOptions
                    {
                        RequeueMinMs = EolBotPhysicsUtil.PathRequeueMinMsRoamCaptain,
                        PathStartIndex = 0,
                    });
                    PublishRoamNavState();
                }
            }
        }

        public Helpers.ObjectState getTargetPoint()
        {
            Helpers.ObjectState target = new Helpers.ObjectState();

            short ox, oy;
            if (!_baseScript.TryGetPirateRaidHordeReferencePoint(out ox, out oy))
                return null;

            int blockedAttempts = 30;
            short pX;
            short pY;
            while (true)
            {
                pX = ox;
                pY = oy;

                Helpers.randomPositionInArea(_arena, 1000, ref pX, ref pY);
                if (_arena.getTile(pX, pY).Blocked)
                {
                    blockedAttempts--;
                    if (blockedAttempts <= 0)
                        //Consider the spawn to be blocked
                        return null;
                    continue;
                }

                target.positionX = pX;
                target.positionY = pY;
                break;
            }
            return target;
        }

        public Helpers.ObjectState getHPoint()
        {
            Helpers.ObjectState target = new Helpers.ObjectState();

            short ox, oy;
            if (!_baseScript.TryGetPirateRaidHordeReferencePoint(out ox, out oy))
                return null;

            target.positionX = ox;
            target.positionY = oy;
            return target;
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
                seekAndDestroy,
                reposition,
                anchorRoam
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
