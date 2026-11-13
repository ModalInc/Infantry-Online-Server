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
        int _idleSince, _idleCheckTick, _soloUntil, _driftPickTick;
        short _idleX, _idleY;
        Helpers.ObjectState _drift;

        /// <summary>
        /// Never standing around doing nothing (local 2026-11-14): sampled every second - a bot that hasn't moved
        /// 40 px and hasn't fired for 4 s breaks off and hunts on its own for ~8 s (a real player gets bored and
        /// goes looking).
        /// </summary>
        void IdleWatch(int now)
        {
            if (_idleCheckTick != 0 && now - _idleCheckTick < 1000)
                return;
            _idleCheckTick = now;
            int dx = _state.positionX - _idleX, dy = _state.positionY - _idleY;
            bool moved = dx * dx + dy * dy >= 40 * 40;
            bool shot = LastShotTick != 0 && now - LastShotTick < 4000;
            if (_idleSince == 0 || moved || shot)
            {
                _idleSince = now;
                _idleX = _state.positionX;
                _idleY = _state.positionY;
                return;
            }
            if (now - _idleSince < 4000)
                return;
            _idleSince = now;
            _idleX = _state.positionX;
            _idleY = _state.positionY;
            _soloUntil = now + 8000;
            _drift = null;
            Navigation.Clear();
            // frozen on a target it can't do anything about: let it go
            if (EolBotBrain.DropStaleTarget(this))
                _target = null;
            _idleKicks++;
            if (now - _tickIdleLog > 30000)
            {
                _tickIdleLog = now;
                Log.write(TLog.Normal, "[bot brain] {0} idle bots kicked into hunting so far", _idleKicks);
            }
        }

        static int _idleKicks, _tickIdleLog;

        /// <summary>
        /// Fight anything in sight; otherwise the leader hunts across the map and the others move in a loose
        /// formation on the leader (spread out, not stacked).
        /// </summary>
        bool EliteSquadPoll()
        {
            int now = Environment.TickCount;
            InLiveCombat = false;
            IdleWatch(now);

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
            if (leader == null || leader == this || now - _soloUntil < 0)
            {   // the leader - or a member that got bored standing around - goes hunting
                EolBotBrain.Hunt(this, _baseScript, now, null);
                return base.poll();
            }

            // leader in a fight: head over to it (move toward the leader, not the enemy) - each member only
            // engages what it can see itself; no shared knowledge of where the enemy is
            if (leader.InLiveCombat)
            {
                double ldx = _state.positionX - leader._state.positionX, ldy = _state.positionY - leader._state.positionY;
                if (ldx * ldx + ldy * ldy > 220.0 * 220.0)
                {
                    Helpers.ObjectState toLeader = new Helpers.ObjectState { positionX = leader._state.positionX, positionY = leader._state.positionY };
                    ApplyNavigationApproach(toLeader, EolHqPatrol.MakeLineClearPendingSeek(this, () => toLeader), new BotNavigateOptions
                    {
                        RequeueMinMs = 1200,
                        PathStartIndex = 0,
                        Priority = PathPriority.Combat,
                    });
                    return base.poll();
                }
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
            {
                // At its slot: never stand still - drift round the slot like a player waiting on the group,
                // a new spot every 1.5-3 s (open ground only)
                if (_drift == null || now - _driftPickTick > 1500 + (Math.Abs(_id) % 1500)
                    || Helpers.distanceTo(_state, _drift) < 30)
                {
                    _driftPickTick = now;
                    _drift = null;
                    for (int tries = 0; tries < 8 && _drift == null; tries++)
                    {
                        double a = _rand.NextDouble() * Math.PI * 2, r = 60 + _rand.Next(90);
                        short px = (short)Math.Max(16, Math.Min(short.MaxValue - 16, sx + Math.Cos(a) * r));
                        short py = (short)Math.Max(16, Math.Min(short.MaxValue - 16, sy + Math.Sin(a) * r));
                        if (EolHqPatrol.IsOpenForBot(this, px, py)
                            && EolHqPatrol.TileLineClear(_arena, _state.positionX, _state.positionY, px, py))
                            _drift = new Helpers.ObjectState { positionX = px, positionY = py };
                    }
                }
                if (_drift != null)
                {
                    Helpers.ObjectState d = _drift;
                    ApplyNavigationApproach(d, EolHqPatrol.MakeLineClearPendingSeek(this, () => d), new BotNavigateOptions
                    {
                        RequeueMinMs = 1200,
                        PathStartIndex = 0,
                        Priority = PathPriority.Idle,
                    });
                }
                else
                    HoldPosition();
            }

            return base.poll();
        }
    }
}
