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
        /// <summary>(2026-11-17) while following a player: fight only within this many px of that player.</summary>
        const int FollowLeash = 650;
        /// <summary>(2026-11-24, owner: "@home ... guard the area ... typical area like building size") the squad holds
        /// round the "@ home" spot within GuardRadius and fights anything that comes within GuardLeash of it - never further.</summary>
        const int GuardRadius = 420;
        const int GuardLeash = 750;
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

            // (2026-11-17) the Chief Engineer on a build order ("mg here" ...) runs to the spot and builds it
            if (_baseScript.PollEngineerBuild(this, now))
                return base.poll();

            // (2026-11-19, owner: "the bots when you summon them in the dropship, they start shooting others. the drop ship is a
            // safe zone so they should not do that. they should basically follow you outside the dropship. not shooting")
            // in the dropship: no target, no fighting - round the player who summoned it until they leave (then out through
            // the portal behind them, Deploy.cs), and that check now comes BEFORE the fight check (it used to come after)
            // (2026-11-19) carrying a flag stolen in a base raid: run it to the drop spot, no fighting (Script_Eol.BaseRaids.cs)
            Helpers.ObjectState stolenTo = _baseScript.StolenFlagDropFor(this);
            if (stolenTo != null)
            {
                ClearActivePlayerTarget();
                bool forceSt = now - _tickEliteNav > 2000;
                if (forceSt) _tickEliteNav = now;
                ApplyNavigationApproach(stolenTo, EolHqPatrol.MakeLineClearPendingSeek(this, () => stolenTo), new BotNavigateOptions
                {
                    RequeueMinMs = 1200,
                    PathStartIndex = 0,
                    ForceRequeue = forceSt,
                    Priority = PathPriority.Combat,
                });
                return base.poll();
            }

            Helpers.ObjectState shipHold = _baseScript.ShipSummonHold(this);
            if (shipHold != null || _baseScript.BotInDropshipSafeZone(this))
            {
                ClearActivePlayerTarget();
                if (shipHold != null)
                    DriftAround(shipHold.positionX, shipHold.positionY, now, followCentre: true);
                else
                    DriftAround(_state.positionX, _state.positionY, now);
                return base.poll();
            }

            // (2026-11-17) a medic called with H runs to that player even mid-fight (heals pulse once in reach)
            if (_baseScript.IsSquadMedic(this) && _baseScript.SquadHealCaller(_team, now) != null)
            {
                Helpers.ObjectState call = _baseScript.MedicMoveTarget(this);
                if (call == null || Helpers.distanceTo(_state, call) <= 80)
                {   // (2026-11-17, owner: "comes and actually heals you") there: it stays beside the caller while it heals
                    // (it used to go back to fighting and drift out of heal range)
                    HoldPosition();
                    return base.poll();
                }
                {
                    bool forceCall = now - _tickEliteNav > 1200;
                    if (forceCall) _tickEliteNav = now;
                    ApplyNavigationApproach(call, EolHqPatrol.MakeLineClearPendingSeek(this, () => call), new BotNavigateOptions
                    {
                        RequeueMinMs = 800,
                        PathStartIndex = 0,
                        ForceRequeue = forceCall,
                        Priority = PathPriority.Combat,
                    });
                    return base.poll();
                }
            }

            // (2026-11-17, owner: "when you say follow me ... they should follow you and then engage if enemy is around, not
            // go elsewhere immediately") while following: fight only what is near the player we follow - a target more than
            // FollowLeash px from that player is let go, and a bot that has fallen far behind comes back first
            // (2026-11-24, owner: "make the @home guard the area ... follow is basically YOU are the area that the bots follow,
            // and if fights happen they join the fight normally") one ANCHOR for the whole poll: the player we follow, their
            // last spot while they are dead / in the dropship, or the "@ home" guard spot. Fights, assists, turrets, repairs and
            // medic runs only count near the anchor - nothing pulls the squad off across the map any more.
            InfServer.Game.Player follow = _baseScript.SquadFollowTarget(_team, now);
            Helpers.ObjectState followWait = follow == null ? _baseScript.SquadFollowWaitSpot(_team, now) : null;
            Helpers.ObjectState guard = follow == null && followWait == null ? _baseScript.SquadDefendPoint(_team, now) : null;
            Helpers.ObjectState anchor = follow != null ? follow._state : (followWait ?? guard);
            int leash = guard != null ? GuardLeash : FollowLeash;
            bool farBehind = anchor != null && Helpers.distanceTo(_state, anchor) > leash + 150;
            if (anchor != null && _target != null && (farBehind || Helpers.distanceTo(_target._state, anchor) > leash))
                ClearActivePlayerTarget();

            // (2026-11-26, owner: "if it gets stuck it should go back to driving path") a stuck Harrier leaves the fight and
            // drives back to its route (Script_Eol.HarrierCrew.cs)
            Helpers.ObjectState unstick = _baseScript.HarrierUnstickGoal(this, now);
            if (unstick != null)
            {
                ClearActivePlayerTarget();
                bool forceU = now - _tickEliteNav > 1500;
                if (forceU) _tickEliteNav = now;
                ApplyNavigationApproach(unstick, EolHqPatrol.MakeLineClearPendingSeek(this, () => unstick), new BotNavigateOptions
                {
                    RequeueMinMs = 800,
                    PathStartIndex = 0,
                    ForceRequeue = forceU,
                    Priority = PathPriority.Combat,
                });
                return base.poll();
            }

            // (2026-11-26, owner: "mostly drive bys ... they dont engage for long") a Harrier that has fought a few seconds drives
            // on along its route, firing at its target as it passes
            if (_baseScript.HarrierDriveByActive(this, now))
            {
                Helpers.ObjectState onward = _baseScript.HarrierRouteGoal(this, now);
                if (onward != null)
                {
                    if (_baseScript.WideLineClearFor(this, onward))
                        Script_Eol.SteerStraight(this, onward.positionX, onward.positionY);
                    else
                    {
                        bool forceOn = now - _tickEliteNav > 1500;
                        if (forceOn) _tickEliteNav = now;
                        ApplyNavigationApproach(onward, EolHqPatrol.MakeLineClearPendingSeek(this, () => onward), new BotNavigateOptions
                        {
                            RequeueMinMs = 1000,
                            PathStartIndex = 0,
                            ForceRequeue = forceOn,
                            Priority = PathPriority.Combat,
                        });
                    }
                }
                if (_target != null && !_target.IsDead)
                    EolBotPhysicsUtil.TryFireOnlyWhileUnstick(this, _target);
                return base.poll();
            }

            // (2026-11-18, owner: "one bot ran in to fight and then bots just watched their team member get gunned down -
            // make sure they all engage if one is engaging") a team mate's combat call pulls the whole squad in
            bool assist = !farBehind && _baseScript.TryAcceptCombatCallAssist(_team, this, now);
            bool hostile = !farBehind && (assist || BotShouldEngageInsteadOfIdle(now) || (_target != null && !_target.IsDead));
            if (hostile)
            {
                seekAndDestroy(now);
                if (anchor != null && _target != null && Helpers.distanceTo(_target._state, anchor) > leash)
                    ClearActivePlayerTarget();   // (it picked something too far from the player / guard spot: stay)
                InLiveCombat = _target != null || InLiveCombat;
                if (InLiveCombat)
                    return base.poll();
                // (2026-11-18) helping but no shot yet (no line of sight): run at the fight until it can shoot
                Helpers.ObjectState fight;
                if (assist && _baseScript.TryGetCombatCallFightSpot(_team, this, now, out fight)
                    && (anchor == null || Helpers.distanceTo(fight, anchor) <= leash))
                {
                    bool forceFight = now - _tickEliteNav > 1200;
                    if (forceFight) _tickEliteNav = now;
                    ApplyNavigationApproach(fight, EolHqPatrol.MakeLineClearPendingSeek(this, () => fight), new BotNavigateOptions
                    {
                        RequeueMinMs = 800,
                        PathStartIndex = 0,
                        ForceRequeue = forceFight,
                        Priority = PathPriority.Combat,
                    });
                    return base.poll();
                }
            }

            if (!farBehind && EolBotBrain.TryEngageTurret(this, _baseScript, now))
                return base.poll();

            // (2026-11-17) a Chief Engineer out of a fight repairs its team's damaged turrets / HQ nearby
            // (2026-11-24: only while it is inside the area it follows / guards - no trips across the map)
            if ((anchor == null || Helpers.distanceTo(_state, anchor) <= leash * 0.6) && _baseScript.PollEngineerRepair(this, now))
                return base.poll();

            // (2026-11-16b) a squad medic out of a fight goes to whoever is hurt, or stays behind its player
            Helpers.ObjectState med = _baseScript.MedicMoveTarget(this);
            if (med != null && anchor != null && Helpers.distanceTo(med, anchor) > leash)
                med = null;   // (2026-11-24: the patient is outside the area we follow / guard - stay)
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

            // (2026-11-17) summoned into the new player zone: wait round that player until they leave it (the dropship: above)
            Helpers.ObjectState zoneHold = _baseScript.ShipSummonHold(this);
            if (zoneHold != null)
            {
                DriftAround(zoneHold.positionX, zoneHold.positionY, now, followCentre: true);
                return base.poll();
            }

            // (2026-11-26, owner drew them) a Low Life Thugs Harrier out of a fight drives its sector's route, looking for people
            Helpers.ObjectState route = _baseScript.HarrierRouteGoal(this, now);
            if (route != null)
            {
                if (EolHqPatrol.IsOpenForBot(this, route.positionX, route.positionY) && _baseScript.WideLineClearFor(this, route))
                    Script_Eol.SteerStraight(this, route.positionX, route.positionY);
                else
                {
                    bool forceRoute = now - _tickEliteNav > 3000;
                    if (forceRoute) _tickEliteNav = now;
                    ApplyNavigationApproach(route, EolHqPatrol.MakeLineClearPendingSeek(this, () => route), new BotNavigateOptions
                    {
                        RequeueMinMs = 1200,
                        PathStartIndex = 0,
                        ForceRequeue = forceRoute,
                        Priority = PathPriority.Idle,
                    });
                }
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

            // (2026-11-17, owner: "follow should get the bots to loosely follow them ... a good angle on a base") each bot
            // keeps its own spot round the player - behind and to the sides, 140-280 px out - and fights what it sees
            InfServer.Game.Player lead = _baseScript.SquadFollowTarget(_team, now);
            if (lead != null)
            {
                int fslot = Math.Abs(_id) % 6;
                double pyaw = lead._state.yaw * 1.5 * Math.PI / 180.0;
                double ang = pyaw + Math.PI + (fslot - 2.5) * 0.55;          // a fan behind the player
                double dist = 140 + (fslot % 3) * 70;
                short flx = (short)Math.Max(16, Math.Min(short.MaxValue - 16, lead._state.positionX + Math.Sin(ang) * dist));
                short fly = (short)Math.Max(16, Math.Min(short.MaxValue - 16, lead._state.positionY - Math.Cos(ang) * dist));
                if (BotWallAvoidance.IsImpassable(this, flx, fly))
                {
                    flx = lead._state.positionX;
                    fly = lead._state.positionY;
                }
                double fdx = _state.positionX - flx, fdy = _state.positionY - fly;
                if (fdx * fdx + fdy * fdy > 180.0 * 180.0)
                {
                    Helpers.ObjectState fdest = new Helpers.ObjectState { positionX = flx, positionY = fly };
                    bool forceF = now - _tickEliteNav > 2000;
                    if (forceF) _tickEliteNav = now;
                    ApplyNavigationApproach(fdest, EolHqPatrol.MakeLineClearPendingSeek(this, () => fdest), new BotNavigateOptions
                    {
                        RequeueMinMs = 1200,
                        PathStartIndex = 0,
                        ForceRequeue = forceF,
                        Priority = PathPriority.Combat,
                    });
                }
                else
                    DriftAround(flx, fly, now, followCentre: true);
                return base.poll();
            }

            // (2026-11-24) the player we follow is dead / in the dropship: wait round their last spot, don't wander off
            if (followWait != null)
            {
                if (Helpers.distanceTo(_state, followWait) > 260)
                {
                    bool forceW = now - _tickEliteNav > 2000;
                    if (forceW) _tickEliteNav = now;
                    ApplyNavigationApproach(followWait, EolHqPatrol.MakeLineClearPendingSeek(this, () => followWait), new BotNavigateOptions
                    {
                        RequeueMinMs = 1200,
                        PathStartIndex = 0,
                        ForceRequeue = forceW,
                        Priority = PathPriority.Combat,
                    });
                }
                else
                    DriftAround(followWait.positionX, followWait.positionY, now, followCentre: true);
                return base.poll();
            }

            // (2026-11-16d, owner: "@ home") told to defend a spot: every bot stays round it and fights what comes
            // (2026-11-24) guard area = GuardRadius round the spot (about a building); fights only within GuardLeash (above)
            Helpers.ObjectState hold = guard;
            if (hold != null)
            {
                if (Helpers.distanceTo(_state, hold) > GuardRadius)
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
                {   // (2026-11-24) each bot keeps its own post round the spot (about a building wide), not all on the centre
                    int gslot = Math.Abs(_id) % 8;
                    double ga = gslot * (Math.PI * 2 / 8) + (Math.Abs(_id) % 3) * 0.3;
                    double gr = 120 + (gslot % 3) * 90;
                    short gx = (short)Math.Max(16, Math.Min(short.MaxValue - 16, hold.positionX + Math.Cos(ga) * gr));
                    short gy = (short)Math.Max(16, Math.Min(short.MaxValue - 16, hold.positionY + Math.Sin(ga) * gr));
                    if (!EolHqPatrol.IsOpenForBot(this, gx, gy))
                    {
                        gx = hold.positionX;
                        gy = hold.positionY;
                    }
                    DriftAround(gx, gy, now, followCentre: true);
                }
                return base.poll();
            }

            // (2026-11-19, owner: "the roamer bots running around to start attacking that base to kill the HQ and to grab the
            // flags") raiding a player base: the leader marches on its HQ / the nearest roamer flag it holds, fights round it
            Helpers.ObjectState raidGoal = _baseScript.RaidGoalFor(this);
            if (raidGoal != null && (leader == null || leader == this))
            {
                if (Helpers.distanceTo(_state, raidGoal) > 300)
                {
                    bool forceR = now - _tickEliteNav > 3000;
                    if (forceR) _tickEliteNav = now;
                    ApplyNavigationApproach(raidGoal, EolHqPatrol.MakeLineClearPendingSeek(this, () => raidGoal), new BotNavigateOptions
                    {
                        RequeueMinMs = 1500,
                        PathStartIndex = 0,
                        ForceRequeue = forceR,
                        Priority = PathPriority.Combat,
                    });
                }
                else
                    EolBotBrain.Hunt(this, _baseScript, now, raidGoal);
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
