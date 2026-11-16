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
        /// <summary>Walking through the dropship to the map portal (Script_Eol.Deploy.cs): no fighting, just go.</summary>
        public void DeployWalk(Helpers.ObjectState portal, int now)
        {
            _target = null;
            InLiveCombat = false;
            bool force = now - _tickEliteNav > 2500;
            if (force) _tickEliteNav = now;
            ApplyNavigationApproach(portal, EolHqPatrol.MakeLineClearPendingSeek(this, () => portal), new BotNavigateOptions
            {
                RequeueMinMs = 1200,
                PathStartIndex = 0,
                ForceRequeue = force,
                Priority = PathPriority.Idle,
            });
        }

        bool EliteSquadPoll()
        {
            int now = Environment.TickCount;
            InLiveCombat = false;
            if (_baseScript.PollDeploying(this, now))
                return base.poll();
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

            // (2026-11-16b) a squad medic out of a fight goes to whoever is hurt, or stays behind its player
            Helpers.ObjectState med = _baseScript.MedicMoveTarget(this);
            if (med != null)
            {
                bool forceMed = now - _tickEliteNav > 1500;
                if (forceMed) _tickEliteNav = now;
                ApplyNavigationApproach(med, EolHqPatrol.MakeLineClearPendingSeek(this, () => med), new BotNavigateOptions
                {
                    RequeueMinMs = 900,
                    PathStartIndex = 0,
                    ForceRequeue = forceMed,
                    Priority = PathPriority.Combat,
                });
                return base.poll();
            }

            RoamingAttacker leader = _baseScript.EliteSquadLeader(_team);

            // (2026-11-16b) a squad cut down to under half waits for its reinforcements (from the dropship)
            // instead of feeding in one by one - the leader holds round its spot, the others close up on it
            if ((leader == null || leader == this) && _baseScript.SquadShouldRegroup(this, now))
            {
                DriftAround(_state.positionX, _state.positionY, now);
                return base.poll();
            }

            // (2026-11-16d, owner: "@ home") told to defend a spot: every bot stays round it and fights what comes
            Helpers.ObjectState hold = _baseScript.SquadDefendPoint(_team, now);
            if (hold != null)
            {
                if (Helpers.distanceTo(_state, hold) > 350)
                {
                    bool forceHold = now - _tickEliteNav > 2500;
                    if (forceHold) _tickEliteNav = now;
                    ApplyNavigationApproach(hold, EolHqPatrol.MakeLineClearPendingSeek(this, () => hold), new BotNavigateOptions
                    {
                        RequeueMinMs = 1500,
                        PathStartIndex = 0,
                        ForceRequeue = forceHold,
                        Priority = PathPriority.Combat,
                    });
                }
                else
                    DriftAround(hold.positionX, hold.positionY, now);
                return base.poll();
            }

            // (2026-11-16, owner) a player joined this squad: the bots' mission is to attack enemy bases with them -
            // the leader marches on the nearest bot base, fights round it once there, the others follow in formation
            if ((leader == null || leader == this) && _baseScript.SquadHumans(_team) > 0)
            {
                Helpers.ObjectState hq = _baseScript.NearestEnemyBaseHq(this);
                if (hq != null)
                {
                    if (Helpers.distanceTo(_state, hq) > 450)
                    {
                        bool force = now - _tickEliteNav > 3000;
                        if (force) _tickEliteNav = now;
                        ApplyNavigationApproach(hq, EolHqPatrol.MakeLineClearPendingSeek(this, () => hq), new BotNavigateOptions
                        {
                            RequeueMinMs = 1500,
                            PathStartIndex = 0,
                            ForceRequeue = force,
                            Priority = PathPriority.Combat,
                        });
                    }
                    else
                        EolBotBrain.Hunt(this, _baseScript, now, hq);
                    return base.poll();
                }
            }

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
                DriftAround(sx, sy, now, followCentre: true);   // (round the slot, as before: it moves with the leader)

            return base.poll();
        }

        short _driftCx, _driftCy;

        /// <summary>
        /// Never stand still: drift round a spot like a player waiting on the group, a new point 60-150 px out
        /// every 1.5-3 s (open ground only).
        /// </summary>
        void DriftAround(short sx, short sy, int now, bool followCentre = false)
        {
            if (followCentre)
            {
                _driftCx = sx;
                _driftCy = sy;
            }
            else if (Math.Abs(sx - _driftCx) > 200 || Math.Abs(sy - _driftCy) > 200)
            {   // (a new centre: don't keep drifting round the old one)
                _driftCx = sx;
                _driftCy = sy;
                _drift = null;
            }
            if (_drift == null || now - _driftPickTick > 1500 + (Math.Abs(_id) % 1500)
                || Helpers.distanceTo(_state, _drift) < 30)
            {
                _driftPickTick = now;
                _drift = null;
                for (int tries = 0; tries < 8 && _drift == null; tries++)
                {
                    double a = _rand.NextDouble() * Math.PI * 2, r = 60 + _rand.Next(90);
                    short px = (short)Math.Max(16, Math.Min(short.MaxValue - 16, _driftCx + Math.Cos(a) * r));
                    short py = (short)Math.Max(16, Math.Min(short.MaxValue - 16, _driftCy + Math.Sin(a) * r));
                    if (EolHqPatrol.IsOpenForBot(this, px, py)
                        && EolHqPatrol.TileLineClear(_arena, _state.positionX, _state.positionY, px, py, _type))
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
    }
}
