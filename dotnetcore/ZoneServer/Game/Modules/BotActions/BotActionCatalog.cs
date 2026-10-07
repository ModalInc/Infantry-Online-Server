using System;
using System.Collections.Generic;
using InfServer.Protocol;

namespace InfServer.Game.Modules.BotActions
{
    /// <summary>
    /// Maps every <see cref="BotActionId"/> to a callable behavior on day one.
    /// </summary>
    public static class BotActionCatalog
    {
        static readonly Dictionary<int, Func<IBotBehavior>> _factories = new Dictionary<int, Func<IBotBehavior>>();
        static bool _ready;

        public static void EnsureInitialized()
        {
            if (_ready)
                return;
            lock (_factories)
            {
                if (_ready)
                    return;
                RegisterAll();
                _ready = true;
            }
        }

        public static IBotBehavior Get(BotActionId id)
        {
            EnsureInitialized();
            Func<IBotBehavior> factory;
            return _factories.TryGetValue((int)id, out factory) ? factory() : null;
        }

        static void Set(BotActionId id, Func<IBotBehavior> factory)
        {
            _factories[(int)id] = factory;
        }

        static void Alias(BotActionId id, BotActionId to)
            => Set(id, () => new AliasBehavior(id, to));

        static void Obj(BotActionId id, Func<BotActionContext, Helpers.ObjectState> resolve, int hold = 200)
            => Set(id, () => new GoToObjectiveBehavior(id, resolve, hold));

        static Helpers.ObjectState FlagEnemy(BotActionContext c)
        {
            Helpers.ObjectState pos;
            return c.Objectives != null && c.Objectives.TryGetEnemyFlag(c.Bot?._team, out pos) ? pos : null;
        }

        static Helpers.ObjectState FlagFriendly(BotActionContext c)
        {
            Helpers.ObjectState pos;
            return c.Objectives != null && c.Objectives.TryGetFriendlyFlag(c.Bot?._team, out pos) ? pos : null;
        }

        static Helpers.ObjectState Defend(BotActionContext c)
        {
            Helpers.ObjectState pos;
            if (c.Objectives != null && c.Objectives.TryGetDefendAnchor(c.Bot, out pos))
                return pos;
            return FlagFriendly(c);
        }

        static Helpers.ObjectState Hill(BotActionContext c)
        {
            Helpers.ObjectState pos;
            Team owner;
            return c.Objectives != null && c.Objectives.TryGetHill(out pos, out owner) ? pos : FlagEnemy(c);
        }

        static Helpers.ObjectState ControlPoint(BotActionContext c)
        {
            Helpers.ObjectState pos;
            Team owner;
            if (c.Objectives != null && c.Objectives.TryGetControlPoint(out pos, out owner))
                return pos;
            return FlagEnemy(c) ?? FlagFriendly(c);
        }

        static Helpers.ObjectState Payload(BotActionContext c)
        {
            Helpers.ObjectState pos;
            return c.Objectives != null && c.Objectives.TryGetPayload(out pos) ? pos : FlagEnemy(c);
        }

        static Helpers.ObjectState Bomb(BotActionContext c)
        {
            Helpers.ObjectState pos;
            return c.Objectives != null && c.Objectives.TryGetBombSite(out pos) ? pos : FlagEnemy(c);
        }

        static Helpers.ObjectState Lane(BotActionContext c)
        {
            Helpers.ObjectState pos;
            return c.Objectives != null && c.Objectives.TryGetLaneWaypoint(c.Bot, out pos) ? pos : FlagEnemy(c);
        }

        static Helpers.ObjectState Safe(BotActionContext c)
        {
            Helpers.ObjectState pos;
            return c.Objectives != null && c.Objectives.TryGetSafeZone(c.Bot, out pos) ? pos : Defend(c);
        }

        static Helpers.ObjectState FirstObjective(BotActionContext c)
            => FlagEnemy(c) ?? Hill(c) ?? ControlPoint(c) ?? Payload(c) ?? Bomb(c) ?? Defend(c);

        static void RegisterAll()
        {
            _factories.Clear();

            Set(BotActionId.AttackPlayer, () => new AttackPlayerBehavior());
            Set(BotActionId.SeekEnemy, () => new SeekEnemyBehavior());
            Set(BotActionId.Retreat, () => new RetreatBehavior(BotActionId.Retreat));
            Set(BotActionId.TacticalWithdrawal, () => new RetreatBehavior(BotActionId.TacticalWithdrawal));
            Set(BotActionId.TakeCover, () => new RetreatBehavior(BotActionId.TakeCover));
            Set(BotActionId.SniperFindCover, () => new RetreatBehavior(BotActionId.SniperFindCover));
            Set(BotActionId.Patrol, () => new PatrolBehavior(BotActionId.Patrol));
            Set(BotActionId.PatrolHQ, () => new PatrolBehavior(BotActionId.PatrolHQ));
            Set(BotActionId.RaidObjective, () => new RaidObjectiveBehavior());
            Set(BotActionId.PressEngage, () => new PressEngageBehavior());
            Set(BotActionId.DefendPosition, () => new DefendPositionBehavior(BotActionId.DefendPosition));
            Set(BotActionId.SecureArea, () => new DefendPositionBehavior(BotActionId.SecureArea));
            Set(BotActionId.Overwatch, () => new DefendPositionBehavior(BotActionId.Overwatch));
            Set(BotActionId.Ambush, () => new DefendPositionBehavior(BotActionId.Ambush));
            Set(BotActionId.EstablishBeachhead, () => new DefendPositionBehavior(BotActionId.EstablishBeachhead));

            Set(BotActionId.FollowPlayer, () => new FollowPlayerBehavior(BotActionId.FollowPlayer));
            Set(BotActionId.EscortPlayer, () => new FollowPlayerBehavior(BotActionId.EscortPlayer));
            Set(BotActionId.Formation, () => new FollowPlayerBehavior(BotActionId.Formation));
            Alias(BotActionId.FollowBot, BotActionId.FollowPlayer);

            Set(BotActionId.StrafeAndFire, () => new StrafeFireBehavior(BotActionId.StrafeAndFire));
            Set(BotActionId.CircleStrafe, () => new StrafeFireBehavior(BotActionId.CircleStrafe));
            Set(BotActionId.Strafe, () => new StrafeFireBehavior(BotActionId.Strafe));
            Set(BotActionId.Suppress, () => new StrafeFireBehavior(BotActionId.Suppress));
            Set(BotActionId.SuppressEnemy, () => new StrafeFireBehavior(BotActionId.SuppressEnemy));
            Set(BotActionId.CoverFire, () => new StrafeFireBehavior(BotActionId.CoverFire));
            Set(BotActionId.Crossfire, () => new StrafeFireBehavior(BotActionId.Crossfire));
            Set(BotActionId.CounterAttack, () => new StrafeFireBehavior(BotActionId.CounterAttack));

            Set(BotActionId.MaintainCombatDistance, () => new MaintainDistanceBehavior(BotActionId.MaintainCombatDistance));
            Set(BotActionId.SniperMaintainDistance, () => new MaintainDistanceBehavior(BotActionId.SniperMaintainDistance, 650, 420, 900));
            Set(BotActionId.CloseMeleeCombat, () => new MaintainDistanceBehavior(BotActionId.CloseMeleeCombat, 120, 40, 220));
            Set(BotActionId.Flank, () => new MaintainDistanceBehavior(BotActionId.Flank, 400, 220, 650));
            Set(BotActionId.FlankEnemy, () => new MaintainDistanceBehavior(BotActionId.FlankEnemy, 400, 220, 650));
            Set(BotActionId.DodgeAndCounter, () => new MaintainDistanceBehavior(BotActionId.DodgeAndCounter, 350, 180, 550));
            Set(BotActionId.EvadeAndReposition, () => new MaintainDistanceBehavior(BotActionId.EvadeAndReposition, 480, 250, 750));
            Set(BotActionId.SniperMoveBetweenShots, () => new MaintainDistanceBehavior(BotActionId.SniperMoveBetweenShots, 600, 400, 850));
            Set(BotActionId.Feint, () => new MaintainDistanceBehavior(BotActionId.Feint, 380, 200, 600));
            Set(BotActionId.Pincer, () => new MaintainDistanceBehavior(BotActionId.Pincer, 420, 240, 700));

            foreach (BotActionId holdId in new[]
            {
                BotActionId.Communicate, BotActionId.SignalTeam, BotActionId.Resupply, BotActionId.DeployEquipment,
                BotActionId.Repair, BotActionId.RepairVehicle, BotActionId.Heal,
                BotActionId.MedicHealTeam, BotActionId.MedicPrioritizeCritical, BotActionId.MedicCombatHeal,
                BotActionId.ReplaceTurret, BotActionId.EngineerRepairTurrets, BotActionId.EngineerMaintainTurrets,
                BotActionId.WardMap, BotActionId.ClearWards, BotActionId.UseVehicleAbilities,
                BotActionId.GatherResources, BotActionId.BuildStructures, BotActionId.CoordinateEconomicStrategies,
                BotActionId.FormTemporaryAlliances, BotActionId.BetrayAllies,
                BotActionId.CoordinateTeamMovements, BotActionId.CoordinateStrike, BotActionId.CoordinateGank,
                BotActionId.CoordinateHillDefense, BotActionId.CoordinatePointDefense, BotActionId.CoordinateBombTactics,
                BotActionId.CoordinateObjectiveDefense, BotActionId.CoordinateSurvivalTactics,
                BotActionId.CoordinateVehicleStrategies, BotActionId.CoordinateVehicleDefense,
                BotActionId.CoordinateWithSurvivors, BotActionId.CoordinateSurvivalDefense, BotActionId.CoordinateTeamKills
            })
            {
                BotActionId captured = holdId;
                Set(captured, () => new HoldBehavior(captured));
            }

            Alias(BotActionId.Reconnaissance, BotActionId.Patrol);
            Alias(BotActionId.RoamMap, BotActionId.Patrol);
            Alias(BotActionId.EstablishVision, BotActionId.Patrol);

            Obj(BotActionId.AttackFlag, FlagEnemy);
            Obj(BotActionId.DefendFlag, FlagFriendly);
            Obj(BotActionId.HeadToFlag, FlagEnemy);
            Obj(BotActionId.ReturnFlag, FlagFriendly);
            Obj(BotActionId.SeekObjective, FirstObjective);
            Obj(BotActionId.HeadToTurret, Defend);
            Obj(BotActionId.CaptureFlag, FlagEnemy);
            Obj(BotActionId.DefendBase, Defend);
            Obj(BotActionId.GuardFlagSpawn, FlagFriendly);
            Obj(BotActionId.ClearFlagPath, FlagEnemy);
            Obj(BotActionId.EstablishFlagRoute, FlagEnemy);
            Obj(BotActionId.DistractEnemyTeam, FlagEnemy);
            Obj(BotActionId.SecureFlagZone, FlagFriendly);

            Set(BotActionId.EscortFlagCarrier, () => new FollowPlayerBehavior(BotActionId.EscortFlagCarrier, true));
            Set(BotActionId.CoverFlagCarrier, () => new FollowPlayerBehavior(BotActionId.CoverFlagCarrier, true));
            Set(BotActionId.InterceptFlagCarrier, () => new FollowPlayerBehavior(BotActionId.InterceptFlagCarrier, false));
            Set(BotActionId.EscortVIP, () => new FollowPlayerBehavior(BotActionId.EscortVIP, true));
            Set(BotActionId.CoverPayloadCarrier, () => new FollowPlayerBehavior(BotActionId.CoverPayloadCarrier, true));
            Set(BotActionId.ProtectCarry, () => new FollowPlayerBehavior(BotActionId.ProtectCarry, true));

            foreach (BotActionId hillId in new[]
            {
                BotActionId.CaptureHill, BotActionId.DefendHill, BotActionId.ContestHill, BotActionId.EstablishHillControl,
                BotActionId.ClearHillApproach, BotActionId.MaintainHillPresence, BotActionId.ReinforceHill,
                BotActionId.InterceptHillContesters, BotActionId.SecureHillPerimeter, BotActionId.CombatHillControl
            })
            {
                BotActionId captured = hillId;
                Obj(captured, Hill);
            }

            Alias(BotActionId.HuntPlayers, BotActionId.SeekEnemy);
            Alias(BotActionId.MaintainKillStreak, BotActionId.AttackPlayer);
            Alias(BotActionId.InterceptFleeingPlayers, BotActionId.SeekEnemy);
            Alias(BotActionId.GankEnemy, BotActionId.SeekEnemy);
            Alias(BotActionId.TeamFight, BotActionId.AttackPlayer);
            Alias(BotActionId.BaitEnemy, BotActionId.Feint);
            Alias(BotActionId.PrioritizeHighValueTargets, BotActionId.SeekEnemy);
            Alias(BotActionId.SniperTargetPriority, BotActionId.SeekEnemy);
            Alias(BotActionId.HuntInfected, BotActionId.SeekEnemy);
            Alias(BotActionId.InterceptInfected, BotActionId.SeekEnemy);
            Alias(BotActionId.SurviveAsLastPlayer, BotActionId.Retreat);
            Alias(BotActionId.BalanceKillsAndObjectives, BotActionId.SeekObjective);
            Alias(BotActionId.CombatInfection, BotActionId.SeekEnemy);
            Alias(BotActionId.CombatSurvival, BotActionId.DefendPosition);

            Obj(BotActionId.SecureKillZone, Defend);
            Obj(BotActionId.EstablishKillRoute, c => FlagEnemy(c) ?? Defend(c));
            Obj(BotActionId.AmbushSpawnPoints, Defend);
            Obj(BotActionId.ControlHighGround, Defend);
            Obj(BotActionId.SecurePowerPositions, Defend);
            Obj(BotActionId.EstablishKillChokePoints, Defend);

            Obj(BotActionId.PushLane, Lane);
            Obj(BotActionId.DefendLane, Defend);
            Obj(BotActionId.SecureJungle, Lane);
            Obj(BotActionId.ContestObjective, FirstObjective);
            Obj(BotActionId.SplitPush, Lane);
            Obj(BotActionId.Backdoor, FlagEnemy);
            Obj(BotActionId.CounterJungle, Lane);
            Obj(BotActionId.SecureBuffs, Lane);
            Obj(BotActionId.DefendTower, Defend);
            Obj(BotActionId.PushTower, Lane);
            Obj(BotActionId.RotateLanes, Lane);

            foreach (BotActionId cp in new[]
            {
                BotActionId.CaptureControlPoint, BotActionId.DefendControlPoint, BotActionId.RotateBetweenPoints,
                BotActionId.EstablishPointControl, BotActionId.ClearPointApproach, BotActionId.MaintainPointPresence,
                BotActionId.ReinforcePoint, BotActionId.InterceptPointContesters, BotActionId.SecurePointPerimeter,
                BotActionId.CombatControlPoint
            })
                Obj(cp, ControlPoint);

            foreach (BotActionId pl in new[]
            {
                BotActionId.PushPayload, BotActionId.DefendPayload, BotActionId.InterceptPayload,
                BotActionId.GuardPayloadRoute, BotActionId.ClearPayloadPath, BotActionId.EstablishPayloadRoute,
                BotActionId.DistractPayloadDefenders, BotActionId.SecurePayloadZone, BotActionId.CombatPayloadDefense
            })
                Obj(pl, Payload);

            foreach (BotActionId bomb in new[]
            {
                BotActionId.PlantBomb, BotActionId.DefuseBomb, BotActionId.GuardBombSite, BotActionId.AttackBombSite,
                BotActionId.EstablishBombRoute, BotActionId.ClearBombApproach, BotActionId.MaintainBombPresence,
                BotActionId.ReinforceBombSite, BotActionId.CombatBombSite
            })
                Obj(bomb, Bomb);

            Set(BotActionId.InterceptBombCarrier, () => new FollowPlayerBehavior(BotActionId.InterceptBombCarrier, false));

            foreach (BotActionId obj in new[]
            {
                BotActionId.EstablishMapControl, BotActionId.ClearObjectiveApproach, BotActionId.MaintainObjectivePresence,
                BotActionId.ReinforceObjective, BotActionId.InterceptObjectiveContesters, BotActionId.SecureObjectivePerimeter,
                BotActionId.CombatObjectiveDefense
            })
                Obj(obj, FirstObjective);

            Obj(BotActionId.ManageResources, Safe);
            Obj(BotActionId.AvoidDangerZones, Safe);
            Obj(BotActionId.EstablishSafeZone, Safe);
            Obj(BotActionId.ClearResourceApproach, Safe);
            Obj(BotActionId.MaintainResourcePresence, Safe);
            Obj(BotActionId.ReinforceSafeZone, Safe);
            Obj(BotActionId.InterceptResourceContesters, Safe);
            Obj(BotActionId.EstablishSurvivalControl, Safe);
            Obj(BotActionId.ClearInfectionApproach, Safe);
            Obj(BotActionId.MaintainSurvivalPresence, Safe);
            Obj(BotActionId.ReinforceSurvivors, Safe);
            Obj(BotActionId.SecureSurvivalPerimeter, Safe);

            Obj(BotActionId.NavigateRaceTrack, Lane);
            Obj(BotActionId.EstablishVehicleControl, Defend);
            Obj(BotActionId.ClearVehicleApproach, Lane);
            Obj(BotActionId.MaintainVehiclePresence, Defend);
            Obj(BotActionId.ReinforceVehiclePosition, Defend);
            Obj(BotActionId.InterceptVehicleContesters, Lane);
            Obj(BotActionId.SecureVehiclePerimeter, Defend);

            Obj(BotActionId.DefendStructures, Defend);
            Obj(BotActionId.EstablishBaseControl, Defend);
            Obj(BotActionId.ClearBaseApproach, Defend);
            Obj(BotActionId.MaintainBasePresence, Defend);
            Obj(BotActionId.ReinforceBase, Defend);
            Obj(BotActionId.InterceptBaseContesters, Defend);
            Obj(BotActionId.SecureBasePerimeter, Defend);
            Obj(BotActionId.EngineerProtectTurrets, Defend);

            Set(BotActionId.CombatFlagCarrier, () => new FollowPlayerBehavior(BotActionId.CombatFlagCarrier, false));

            // Ensure every enum value has a factory (safety net).
            foreach (BotActionId id in Enum.GetValues(typeof(BotActionId)))
            {
                if (id == BotActionId.None)
                    continue;
                if (!_factories.ContainsKey((int)id))
                    Alias(id, BotActionId.AttackPlayer);
            }
        }
    }
}
