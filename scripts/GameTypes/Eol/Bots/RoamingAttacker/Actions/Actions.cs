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
    public partial class RoamingAttacker : Bot
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

        /// <summary>Acquire line of sight, close distance, and destroy the target — aggressive pursuit and pressure.</summary>
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
                        RequeueMinMs = EolBotPhysicsUtil.PathRequeueMinMsRoamGrunt,
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

        /// <summary>Pressure the target — raiders challenge, they do not full-retreat on first contact.</summary>
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

        /// <summary>
        /// Pirate raid idle: stay with the captain. Captain alone drives the flag objective.
        /// </summary>
        public void roamRaidOpenWaypoint(int now)
        {
            followRaidCaptain(now);
        }

        /// <summary>Always path / steer to the live roam captain (formation trail, not hull pursuit).</summary>
        void followRaidCaptain(int now)
        {
            if (_roamCapt == null || _roamCapt.IsDead)
            {
                // No captain yet — drive the collective flag / PDB so the pack does not idle-stack.
                if (_baseScript != null && _baseScript.RaiderCollectiveRoamValid)
                    anchorRoamNearPdb(now);
                return;
            }

            short slotX;
            short slotY;
            GetRaiderFormationSlot(out slotX, out slotY);

            long dx = (long)_state.positionX - slotX;
            long dy = (long)_state.positionY - slotY;
            long distSq = dx * dx + dy * dy;
            // Park band around the slot — do not Clear() nav every tick (that caused the hump).
            long parkSq = 90L * 90L;

            if (distSq <= parkSq)
            {
                ApplySteer(steerForFormationHold);
                return;
            }

            if (_targetPoint == null
                || Math.Abs(_targetPoint.positionX - slotX) > 48
                || Math.Abs(_targetPoint.positionY - slotY) > 48)
            {
                _targetPoint = new Helpers.ObjectState { positionX = slotX, positionY = slotY };
                _path = null;
                _tickLastPath = 0;
                Navigation.Clear();
                EolHqPatrol.ResetSteerState(_roamSteer);
            }
            else
            {
                _targetPoint.positionX = slotX;
                _targetPoint.positionY = slotY;
            }

            steerTowardRawDestination(now, slotX, slotY, steerForFormationHold);
        }

        /// <summary>Trail slot behind the captain toward the current flag (or behind facing).</summary>
        void GetRaiderFormationSlot(out short slotX, out short slotY)
        {
            short cx = _roamCapt._state.positionX;
            short cy = _roamCapt._state.positionY;
            slotX = cx;
            slotY = cy;

            double fx = 0, fy = -1;
            short ox, oy;
            if (_baseScript != null && _baseScript.TryGetRaiderCollectiveRoamPoint(out ox, out oy))
            {
                fx = ox - cx;
                fy = oy - cy;
            }
            else
            {
                fx = cx - _state.positionX;
                fy = cy - _state.positionY;
            }

            double len = Math.Sqrt(fx * fx + fy * fy);
            if (len < 1.0)
            {
                fx = 0;
                fy = -1;
                len = 1.0;
            }
            fx /= len;
            fy /= len;
            double px = -fy;
            double py = fx;

            int slot = _id % 12;
            int row = slot / 4;
            int col = slot % 4;
            double lag = Script_Eol.RaiderGruntFormationLagBaseRaw
                + row * Script_Eol.RaiderGruntFormationLagStepRaw;
            double lat = (col - 1.5) * Script_Eol.RaiderGruntFormationLateralStepRaw;

            slotX = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue,
                (int)Math.Round(cx - fx * lag + px * lat)));
            slotY = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue,
                (int)Math.Round(cy - fy * lag + py * lat)));
        }

        void steerTowardRoamWaypoint(int now)
        {
            if (_targetPoint == null)
            {
                ApplySteer(steerForRaidLeashedWander);
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

        void steerTowardRawDestination(int now, short destX, short destY, Func<InfantryVehicle, Vector3> directFallback)
        {
            Helpers.ObjectState destState = new Helpers.ObjectState { positionX = destX, positionY = destY };

            if (_targetPoint == null
                || _targetPoint.positionX != destX
                || _targetPoint.positionY != destY)
            {
                _targetPoint = destState;
                Navigation.Clear();
                EolHqPatrol.ResetSteerState(_roamSteer);
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
                if (result == EolHqPatrol.EolSteerTickResult.NeedsRepick && directFallback != null)
                    ApplySteer(directFallback);
            }
        }

        /// <summary>Legacy alias — grunts follow the captain, not the flag pin.</summary>
        void roamRaidCollectivePoint(int now)
        {
            followRaidCaptain(now);
        }

        Helpers.ObjectState getRaidRoamFallbackWaypointFarFromCurrent()
        {
            if (_baseScript == null || _arena == null)
                return null;

            short ox = _baseScript.RaiderPdbAnchorX;
            short oy = _baseScript.RaiderPdbAnchorY;
            long minSq = (long)Script_Eol.RaiderRoamWaypointMinSpacing * Script_Eol.RaiderRoamWaypointMinSpacing;

            for (int i = 0; i < 55; i++)
            {
                short px = ox;
                short py = oy;
                int radius = 1400 + _arena._rand.Next(0, 900);
                Helpers.randomPositionInArea(_arena, radius, ref px, ref py);
                if (_arena.getTile(px, py).Blocked)
                    continue;
                if (_baseScript._eol != null && !_baseScript._eol.IsInAnyActiveSector(px, py))
                    continue;

                long dx = (long)px - _state.positionX;
                long dy = (long)py - _state.positionY;
                if (dx * dx + dy * dy <= minSq)
                    continue;

                Helpers.ObjectState o = new Helpers.ObjectState();
                o.positionX = px;
                o.positionY = py;
                return o;
            }

            return null;
        }

        public void pushToCaptain(int now, short x, short y)
        {
            if (_team == _baseScript.botTeam4 && _baseScript.IsPirateRaidWaveActive)
            {
                followRaidCaptain(now);
                return;
            }

            if (_team == _baseScript.botTeam4 && !_baseScript.IsPirateRaidWaveActive)
            {
                _targetPoint = null;
                _path = null;
                _tickLastPath = 0;
                Navigation.Clear();
            }

            if (_roamCapt == null || _roamCapt.IsDead)
                return;

            long dx = (long)_state.positionX - _roamCapt._state.positionX;
            long dy = (long)_state.positionY - _roamCapt._state.positionY;
            double distance = Math.Sqrt((double)dx * dx + (double)dy * dy);

            if (distance > 2000)
            {
                HoldPosition();
                if (_baseScript.roamBots.ContainsKey(_team))
                {
                    _baseScript.roamBots[_team]--;
                    if (_baseScript.roamBots[_team] < 0)
                        _baseScript.roamBots[_team] = 0;
                }
                // Must kill() so Bot.Death → ForgetScriptBotBookkeeping runs; condemn-only orphans the bot.
                kill(null);
                ReleasePollScratch();
                bCondemned = true;
                return;
            }

            if (distance <= 100)
            {
                if (_targetPoint != null)
                {
                    _targetPoint = null;
                    Navigation.Clear();
                }

                if (_targetPoint == null)
                    _targetPoint = getTargetPoint();

                if (_targetPoint == null)
                {
                    ApplySteer(steerForFollowOwner);
                    return;
                }

                steerTowardRawDestination(now, _targetPoint.positionX, _targetPoint.positionY, steerForFollowOwner);
                return;
            }

            steerTowardRawDestination(now, _roamCapt._state.positionX, _roamCapt._state.positionY, steerForFollowOwner);
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

        public Helpers.ObjectState getTargetPoint()
        {
            Helpers.ObjectState target = new Helpers.ObjectState();

            int blockedAttempts = 30;
            short pX;
            short pY;
            while (true)
            {
                if (_roamCapt != null && !_roamCapt.IsDead)
                {
                    pX = _roamCapt._state.positionX;
                    pY = _roamCapt._state.positionY;
                }
                else if (_baseScript != null && _baseScript.RaiderPdbAnchorValid)
                {
                    pX = _baseScript.RaiderPdbAnchorX;
                    pY = _baseScript.RaiderPdbAnchorY;
                }
                else
                    return null;

                Helpers.randomPositionInArea(_arena, 1000, ref pX, ref pY);
                if (_arena.getTile(pX, pY).Blocked)
                {
                    blockedAttempts--;
                    if (blockedAttempts <= 0)
                        return null;
                    continue;
                }

                target.positionX = pX;
                target.positionY = pY;
                break;
            }
            return target;
        }
    }

    public static class MiscExtensions
    {
        // Ex: collection.TakeLast(5);
        public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> source, int N)
        {
            return source.Skip(Math.Max(0, source.Count() - N));
        }
    }
}
