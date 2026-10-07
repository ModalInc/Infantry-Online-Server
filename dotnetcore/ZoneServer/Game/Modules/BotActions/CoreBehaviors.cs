using System;
using InfServer.Bots;
using InfServer.Protocol;

namespace InfServer.Game.Modules.BotActions
{
    /// <summary>Engage nearest hostile player.</summary>
    public sealed class AttackPlayerBehavior : IBotBehavior
    {
        public BotActionId Id => BotActionId.AttackPlayer;

        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            if (!BotActionHelpers.RefreshHostileTarget(ctx))
                return BehaviorResult.Failed;
            Helpers.ObjectState target = ctx.GetPrimaryTargetState();
            if (target == null)
                return BehaviorResult.Failed;
            BotActionHelpers.EngageTarget(ctx, target, pressMode: true);
            return BehaviorResult.Continue;
        }
    }

    public sealed class SeekEnemyBehavior : IBotBehavior
    {
        public BotActionId Id => BotActionId.SeekEnemy;
        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            BotThrottleConfig thr = BotActionHelpers.Throttle(ctx);
            if (!BotActionHelpers.RefreshHostileTarget(ctx, Math.Max(ctx.SeekRange, thr.DefaultSeekRange + 800)))
                return BehaviorResult.Failed;
            Helpers.ObjectState target = ctx.GetPrimaryTargetState();
            if (target == null)
                return BehaviorResult.Failed;
            BotActionHelpers.EngageTarget(ctx, target, pressMode: true);
            return BehaviorResult.Continue;
        }
    }

    public sealed class PressEngageBehavior : IBotBehavior
    {
        public BotActionId Id => BotActionId.PressEngage;
        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            if (!BotActionHelpers.RefreshHostileTarget(ctx))
                return BehaviorResult.Failed;
            Helpers.ObjectState target = ctx.GetPrimaryTargetState();
            if (target == null)
                return BehaviorResult.Failed;
            // Aggressive: never full-flee; keep pressing.
            BotActionHelpers.EngageTarget(ctx, target, pressMode: true);
            return BehaviorResult.Continue;
        }
    }

    public sealed class RetreatBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        public RetreatBehavior(BotActionId id = BotActionId.Retreat) { Id = id; }
        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            Helpers.ObjectState threat = ctx.GetPrimaryTargetState();
            if (threat == null && BotActionHelpers.RefreshPlayerTarget(ctx))
                threat = ctx.CurrentPlayerTarget._state;

            Helpers.ObjectState anchor;
            if (ctx.Objectives != null && ctx.Objectives.TryGetPatrolAnchor(ctx.Bot, out anchor) && anchor != null)
            {
                BotActionHelpers.ApproachPoint(ctx, anchor, 1800);
                return BehaviorResult.Continue;
            }

            if (threat != null)
            {
                BotActionHelpers.FleeFrom(ctx, threat);
                return BehaviorResult.Continue;
            }

            BotActionHelpers.Hold(ctx);
            return BehaviorResult.Failed;
        }
    }

    public sealed class PatrolBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        int _tickNextHop;
        Helpers.ObjectState _hop;

        public PatrolBehavior(BotActionId id = BotActionId.Patrol)
        {
            Id = id;
        }

        public void Enter(BotActionContext ctx)
        {
            _tickNextHop = 0;
            _hop = null;
        }

        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            Helpers.ObjectState anchor;
            if (ctx.Objectives == null || !ctx.Objectives.TryGetPatrolAnchor(ctx.Bot, out anchor) || anchor == null)
            {
                BotActionHelpers.Hold(ctx);
                return BehaviorResult.Failed;
            }

            int hopRadius = Id == BotActionId.PatrolHQ ? 220 : 360;
            int hopTtl = Id == BotActionId.PatrolHQ ? 2800 : 3500;

            if (_hop == null || now - _tickNextHop > hopTtl || BotActionHelpers.IsNear(ctx.Bot._state, _hop, BotActionHelpers.HoldRadius))
            {
                _tickNextHop = now;
                short ox = (short)((now / 17 + ctx.Bot._id * 13) % (hopRadius * 2 + 1) - hopRadius);
                short oy = (short)((now / 23 + ctx.Bot._id * 29) % (hopRadius * 2 + 1) - hopRadius);
                _hop = new Helpers.ObjectState
                {
                    positionX = (short)(anchor.positionX + ox),
                    positionY = (short)(anchor.positionY + oy)
                };
            }

            // Full seek/aggro — patrol must not ignore walk-bys outside a short bubble.
            BotThrottleConfig thr = BotActionHelpers.Throttle(ctx);
            int peel = Math.Max(ctx.SeekRange, thr.DefaultSeekRange > 0 ? thr.DefaultSeekRange : BotActionHelpers.DefaultSeekRange);
            if (BotActionHelpers.RefreshHostileTarget(ctx, peel))
            {
                Helpers.ObjectState t = ctx.GetPrimaryTargetState();
                if (t != null)
                    BotActionHelpers.EngageTarget(ctx, t, pressMode: true);
                return BehaviorResult.Continue;
            }

            BotActionHelpers.ApproachPoint(ctx, _hop, 2500);
            return BehaviorResult.Continue;
        }
    }

    public sealed class RaidObjectiveBehavior : IBotBehavior
    {
        public BotActionId Id => BotActionId.RaidObjective;
        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            Helpers.ObjectState dest;
            if (ctx.Objectives == null || !ctx.Objectives.TryGetRaidObjective(ctx.Bot, out dest) || dest == null)
                return BehaviorResult.Failed;

            // Combat first at full seek — never keep marching the pin while a hostile is in range.
            BotThrottleConfig thr = BotActionHelpers.Throttle(ctx);
            int peel = Math.Max(ctx.SeekRange, thr.DefaultSeekRange > 0 ? thr.DefaultSeekRange : BotActionHelpers.DefaultSeekRange);
            if (BotActionHelpers.RefreshHostileTarget(ctx, peel))
            {
                Helpers.ObjectState t = ctx.GetPrimaryTargetState();
                if (t != null)
                    BotActionHelpers.EngageTarget(ctx, t, pressMode: true);
                return BehaviorResult.Continue;
            }

            BotActionHelpers.ApproachPoint(ctx, dest, 1800);
            return BehaviorResult.Continue;
        }
    }

    public sealed class DefendPositionBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        public DefendPositionBehavior(BotActionId id = BotActionId.DefendPosition) { Id = id; }
        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            Helpers.ObjectState anchor = ctx.Anchor;
            if (anchor == null)
                ctx.Objectives?.TryGetDefendAnchor(ctx.Bot, out anchor);
            if (anchor == null)
                ctx.Objectives?.TryGetPatrolAnchor(ctx.Bot, out anchor);

            BotThrottleConfig thr = BotActionHelpers.Throttle(ctx);
            int peel = Math.Max(ctx.SeekRange, thr.DefaultSeekRange > 0 ? thr.DefaultSeekRange : BotActionHelpers.DefaultSeekRange);
            if (BotActionHelpers.RefreshHostileTarget(ctx, peel))
            {
                Helpers.ObjectState t = ctx.GetPrimaryTargetState();
                if (t != null)
                    BotActionHelpers.EngageTarget(ctx, t, pressMode: true);
                return BehaviorResult.Continue;
            }

            if (anchor == null)
            {
                BotActionHelpers.Hold(ctx);
                return BehaviorResult.Failed;
            }

            if (!BotActionHelpers.IsNear(ctx.Bot._state, anchor, BotActionHelpers.HoldRadius + 40))
                BotActionHelpers.ApproachPoint(ctx, anchor, 2000);
            else
                BotActionHelpers.Hold(ctx);
            return BehaviorResult.Continue;
        }
    }

    public sealed class FollowPlayerBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        readonly bool _friendlyCarrier;

        public FollowPlayerBehavior(BotActionId id, bool friendlyCarrier = false)
        {
            Id = id;
            _friendlyCarrier = friendlyCarrier;
        }

        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            Player follow = null;
            if (_friendlyCarrier)
                ctx.Objectives?.TryGetFriendlyFlagCarrier(ctx.Bot._team, out follow);
            else if (ctx.Objectives != null)
                ctx.Objectives.TryGetFlagCarrier(ctx.Bot._team, out follow);

            if (follow == null)
            {
                // Escort/follow without carrier: stick to nearest teammate if any, else fail.
                follow = FindNearestTeammate(ctx);
            }

            if (follow == null || follow.IsDead)
                return BehaviorResult.Failed;

            if (BotActionHelpers.IsNear(ctx.Bot._state, follow._state, 140))
            {
                if (BotActionHelpers.RefreshHostileTarget(ctx, 1000))
                {
                    Helpers.ObjectState t = ctx.GetPrimaryTargetState();
                    if (t != null)
                        BotActionHelpers.EngageTarget(ctx, t);
                }
                else
                    BotActionHelpers.Hold(ctx);
            }
            else
                BotActionHelpers.ApproachPoint(ctx, follow._state, 1200);

            return BehaviorResult.Continue;
        }

        static Player FindNearestTeammate(BotActionContext ctx)
        {
            if (ctx.Arena == null || ctx.Bot?._team == null)
                return null;
            Player best = null;
            double bestDist = double.MaxValue;
            foreach (Player p in ctx.Arena.PlayersIngame)
            {
                if (p == null || p.IsDead || p._team != ctx.Bot._team)
                    continue;
                double d = Helpers.distanceTo(ctx.Bot._state, p._state);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }
    }

    /// <summary>Go to an objective point; engage hostiles while moving / holding.</summary>
    public sealed class GoToObjectiveBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        readonly Func<BotActionContext, Helpers.ObjectState> _resolve;
        readonly int _holdRadius;

        public GoToObjectiveBehavior(BotActionId id, Func<BotActionContext, Helpers.ObjectState> resolve, int holdRadius = 200)
        {
            Id = id;
            _resolve = resolve;
            _holdRadius = holdRadius;
        }

        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            Helpers.ObjectState dest = _resolve?.Invoke(ctx);
            if (dest == null)
                return BehaviorResult.Failed;

            if (BotActionHelpers.RefreshHostileTarget(ctx, 1100))
            {
                Helpers.ObjectState t = ctx.GetPrimaryTargetState();
                if (t != null)
                    BotActionHelpers.EngageTarget(ctx, t);
                // Still nudge toward objective if far.
                if (!BotActionHelpers.IsNear(ctx.Bot._state, dest, _holdRadius + 400))
                    BotActionHelpers.ApproachPoint(ctx, dest, 1600);
                return BehaviorResult.Continue;
            }

            if (!BotActionHelpers.IsNear(ctx.Bot._state, dest, _holdRadius))
                BotActionHelpers.ApproachPoint(ctx, dest, 2000);
            else
                BotActionHelpers.Hold(ctx);
            return BehaviorResult.Continue;
        }
    }

    public sealed class StrafeFireBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        public StrafeFireBehavior(BotActionId id = BotActionId.StrafeAndFire) { Id = id; }
        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            if (!BotActionHelpers.RefreshHostileTarget(ctx))
                return BehaviorResult.Failed;
            Helpers.ObjectState t = ctx.GetPrimaryTargetState();
            if (t == null)
                return BehaviorResult.Failed;
            BotActionHelpers.CircleStrafe(ctx, t);
            return BehaviorResult.Continue;
        }
    }

    public sealed class MaintainDistanceBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        readonly int _ideal;
        readonly int _close;
        readonly int _far;

        public MaintainDistanceBehavior(BotActionId id, int ideal = 450, int close = 280, int far = 700)
        {
            Id = id;
            _ideal = ideal;
            _close = close;
            _far = far;
        }

        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            if (!BotActionHelpers.RefreshHostileTarget(ctx))
                return BehaviorResult.Failed;

            Helpers.ObjectState t = ctx.GetPrimaryTargetState();
            if (t == null)
                return BehaviorResult.Failed;
            double dist = Helpers.distanceTo(ctx.Bot._state, t);
            if (dist < _close)
                BotActionHelpers.FleeFrom(ctx, t);
            else if (dist > _far)
                BotActionHelpers.ApproachPoint(ctx, t, 1200);
            else
                BotActionHelpers.CircleStrafe(ctx, t);

            if (ctx.Bot._weapon != null)
                BotCombat.TryAimAndFire(ctx.Bot, ctx.Bot._weapon, t, requireLineOfSight: true);
            return BehaviorResult.Continue;
        }
    }

    public sealed class HoldBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        public HoldBehavior(BotActionId id) { Id = id; }
        public void Enter(BotActionContext ctx) { }
        public void Exit(BotActionContext ctx) { }

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            BotActionHelpers.Hold(ctx);
            return BehaviorResult.Success;
        }
    }

    /// <summary>Fallback that tries AttackPlayer then Hold — keeps every ID callable.</summary>
    public sealed class AliasBehavior : IBotBehavior
    {
        public BotActionId Id { get; }
        readonly BotActionId _alias;
        IBotBehavior _inner;

        public AliasBehavior(BotActionId id, BotActionId alias)
        {
            Id = id;
            _alias = alias;
        }

        public void Enter(BotActionContext ctx)
        {
            _inner = BotActionCatalog.Get(_alias);
            _inner?.Enter(ctx);
        }

        public void Exit(BotActionContext ctx) => _inner?.Exit(ctx);

        public BehaviorResult Tick(BotActionContext ctx, int now)
        {
            if (_inner == null)
                _inner = BotActionCatalog.Get(_alias);
            return _inner != null ? _inner.Tick(ctx, now) : BehaviorResult.Failed;
        }
    }
}
