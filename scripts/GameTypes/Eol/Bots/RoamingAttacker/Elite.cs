using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>Elite roaming squad behaviour (local change 2026-11-13).</summary>
    public partial class RoamingAttacker : Bot
    {
        int _tickEliteNav;

        /// <summary>
        /// Fight anything in sight; otherwise the leader hunts across the map and the others move in a loose
        /// formation on the leader (spread out, not stacked).
        /// </summary>
        bool EliteSquadPoll()
        {
            int now = Environment.TickCount;
            InLiveCombat = false;

            bool hostile = BotShouldEngageInsteadOfIdle(now) || (_target != null && !_target.IsDead);
            if (hostile)
            {
                seekAndDestroy(now);
                InLiveCombat = _target != null || InLiveCombat;
                if (InLiveCombat)
                    return base.poll();
            }

            if (EolBotBrain.TryEngageTurret(this, _baseScript, now))
                return base.poll();

            RoamingAttacker leader = _baseScript.EliteSquadLeader(_team);
            if (leader == null || leader == this)
            {
                EolBotBrain.Hunt(this, _baseScript, now, null);
                return base.poll();
            }

            // leader in a fight: everyone piles in to help
            if (leader.InLiveCombat && leader.ActivePlayerTarget != null && !leader.ActivePlayerTarget.IsDead)
            {
                EolBotPerception.Reveal(_state, leader.ActivePlayerTarget);
                _target = leader.ActivePlayerTarget;
                seekAndDestroy(now);
                InLiveCombat = _target != null;
                return base.poll();
            }

            // formation slot: fanned out behind the leader
            int slot = Math.Abs(_id) % 4;
            double lx = leader._state.positionX, ly = leader._state.positionY;
            double yaw = leader._state.yaw * 1.5 * Math.PI / 180.0;
            double fx = Math.Sin(yaw), fy = -Math.Cos(yaw);       // leader forward (screen coords)
            double rx = -fy, ry = fx;                              // leader right
            double back = 110 + (slot / 2) * 70;
            double side = (slot % 2 == 0 ? -1 : 1) * (90 + (slot / 2) * 40);
            short sx = (short)Math.Max(16, Math.Min(short.MaxValue - 16, lx - fx * back + rx * side));
            short sy = (short)Math.Max(16, Math.Min(short.MaxValue - 16, ly - fy * back + ry * side));
            if (BotWallAvoidance.IsImpassable(this, sx, sy))
            {
                sx = leader._state.positionX;
                sy = leader._state.positionY;
            }

            double dx = _state.positionX - sx, dy = _state.positionY - sy;
            if (dx * dx + dy * dy > 150.0 * 150.0)
            {
                Helpers.ObjectState dest = new Helpers.ObjectState { positionX = sx, positionY = sy };
                bool force = now - _tickEliteNav > 2500;
                if (force) _tickEliteNav = now;
                ApplyNavigationApproach(dest, EolHqPatrol.MakeLineClearPendingSeek(this, () => dest), new BotNavigateOptions
                {
                    RequeueMinMs = 1500,
                    PathStartIndex = 0,
                    ForceRequeue = force,
                    Priority = PathPriority.Idle,
                });
            }
            else
                HoldPosition();

            return base.poll();
        }
    }
}
