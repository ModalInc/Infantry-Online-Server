using System;
using System.Collections.Generic;
using InfServer.Protocol;

namespace InfServer.Game.Modules.BotActions
{
    public enum BehaviorResult
    {
        Continue,
        Success,
        Failed
    }

    public interface IBotBehavior
    {
        BotActionId Id { get; }
        void Enter(BotActionContext ctx);
        BehaviorResult Tick(BotActionContext ctx, int now);
        void Exit(BotActionContext ctx);
    }

    /// <summary>
    /// Per-tick context shared by all behaviors.
    /// </summary>
    /// <summary>Sticky engage locomotion — avoids Flee↔Orbit↔Approach thrash.</summary>
    public enum EngageMotionMode
    {
        None = 0,
        Approach = 1,
        Orbit = 2,
        Flee = 3,
        Strafe = 4,
        Fade = 5,
    }

    public sealed class BotActionContext
    {
        public Bots.Bot Bot { get; set; }
        public Arena Arena { get; set; }
        public IArenaObjectives Objectives { get; set; }
        public BotActionRegistry Registry { get; set; }
        public Player CurrentPlayerTarget { get; set; }
        public Bots.Bot CurrentBotTarget { get; set; }
        public Helpers.ObjectState Anchor { get; set; }
        public int SeekRange { get; set; } = 2000;

        /// <summary>Last engage locomotion mode (hysteresis).</summary>
        public EngageMotionMode EngageMode { get; set; }
        /// <summary>Tick when <see cref="EngageMode"/> last changed.</summary>
        public int EngageModeTick { get; set; }
        /// <summary>Tick when LOS for orbit last failed (sticky exit).</summary>
        public int EngageLosLostTick { get; set; }
        /// <summary>Strafe / circle direction (±1), flipped on dance phase change.</summary>
        public float EngageStrafeSign { get; set; } = 1f;
        /// <summary>Tick when current fire burst ends (then suppress pause).</summary>
        public int EngageBurstEndTick { get; set; }
        /// <summary>Tick when suppress pause ends (then new burst).</summary>
        public int EngageBurstResumeTick { get; set; }
        /// <summary>Ideal orbit ring in OpenSteer units (min engage raw / 100).</summary>
        public float EngageOrbitRingOs { get; set; } = 0.15f;

        public Helpers.ObjectState GetPrimaryTargetState()
        {
            if (CurrentPlayerTarget != null && !CurrentPlayerTarget.IsDead)
                return CurrentPlayerTarget._state;
            if (CurrentBotTarget != null && !CurrentBotTarget.IsDead)
                return CurrentBotTarget._state;
            return null;
        }
    }

    /// <summary>
    /// Gametype boards — scripts register concrete objectives; defaults return false.
    /// </summary>
    public interface IArenaObjectives
    {
        bool TryGetEnemyFlag(Team forTeam, out Helpers.ObjectState pos);
        bool TryGetFriendlyFlag(Team forTeam, out Helpers.ObjectState pos);
        bool TryGetFlagCarrier(Team enemyOf, out Player carrier);
        bool TryGetFriendlyFlagCarrier(Team forTeam, out Player carrier);
        bool TryGetHill(out Helpers.ObjectState pos, out Team owner);
        bool TryGetPayload(out Helpers.ObjectState pos);
        bool TryGetBombSite(out Helpers.ObjectState pos);
        bool TryGetControlPoint(out Helpers.ObjectState pos, out Team owner);
        bool TryGetPatrolAnchor(Bots.Bot bot, out Helpers.ObjectState pos);
        bool TryGetDefendAnchor(Bots.Bot bot, out Helpers.ObjectState pos);
        bool TryGetSafeZone(Bots.Bot bot, out Helpers.ObjectState pos);
        bool TryGetLaneWaypoint(Bots.Bot bot, out Helpers.ObjectState pos);
        /// <summary>Shared raid / pirate collective objective (Eol raider roam point, etc.).</summary>
        bool TryGetRaidObjective(Bots.Bot bot, out Helpers.ObjectState pos);
    }

    public sealed class NullObjectives : IArenaObjectives
    {
        public static readonly NullObjectives Instance = new NullObjectives();

        public bool TryGetEnemyFlag(Team forTeam, out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetFriendlyFlag(Team forTeam, out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetFlagCarrier(Team enemyOf, out Player carrier) { carrier = null; return false; }
        public bool TryGetFriendlyFlagCarrier(Team forTeam, out Player carrier) { carrier = null; return false; }
        public bool TryGetHill(out Helpers.ObjectState pos, out Team owner) { pos = null; owner = null; return false; }
        public bool TryGetPayload(out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetBombSite(out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetControlPoint(out Helpers.ObjectState pos, out Team owner) { pos = null; owner = null; return false; }
        public bool TryGetPatrolAnchor(Bots.Bot bot, out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetDefendAnchor(Bots.Bot bot, out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetSafeZone(Bots.Bot bot, out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetLaneWaypoint(Bots.Bot bot, out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetRaidObjective(Bots.Bot bot, out Helpers.ObjectState pos) { pos = null; return false; }
    }

    /// <summary>
    /// Default flag/hill lookups from arena state when scripts do not override.
    /// </summary>
    public sealed class ArenaDefaultObjectives : IArenaObjectives
    {
        readonly Arena _arena;

        public ArenaDefaultObjectives(Arena arena)
        {
            _arena = arena;
        }

        public bool TryGetEnemyFlag(Team forTeam, out Helpers.ObjectState pos)
        {
            pos = null;
            if (_arena?._flags == null)
                return false;
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs?.flag == null || !fs.bActive)
                    continue;
                if (forTeam != null && fs.team == forTeam)
                    continue;
                if (fs.carrier != null)
                    continue;
                pos = new Helpers.ObjectState { positionX = fs.posX, positionY = fs.posY };
                return true;
            }
            return false;
        }

        public bool TryGetFriendlyFlag(Team forTeam, out Helpers.ObjectState pos)
        {
            pos = null;
            if (_arena?._flags == null || forTeam == null)
                return false;
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs?.flag == null || fs.team != forTeam || fs.carrier != null)
                    continue;
                pos = new Helpers.ObjectState { positionX = fs.posX, positionY = fs.posY };
                return true;
            }
            return false;
        }

        public bool TryGetFlagCarrier(Team enemyOf, out Player carrier)
        {
            carrier = null;
            if (_arena?._flags == null)
                return false;
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs?.carrier == null || fs.carrier.IsDead)
                    continue;
                if (enemyOf != null && fs.carrier._team == enemyOf)
                    continue;
                carrier = fs.carrier;
                return true;
            }
            return false;
        }

        public bool TryGetFriendlyFlagCarrier(Team forTeam, out Player carrier)
        {
            carrier = null;
            if (_arena?._flags == null || forTeam == null)
                return false;
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs?.carrier == null || fs.carrier.IsDead)
                    continue;
                if (fs.carrier._team != forTeam)
                    continue;
                carrier = fs.carrier;
                return true;
            }
            return false;
        }

        public bool TryGetHill(out Helpers.ObjectState pos, out Team owner)
        {
            pos = null;
            owner = null;
            return false;
        }

        public bool TryGetPayload(out Helpers.ObjectState pos) { pos = null; return false; }
        public bool TryGetBombSite(out Helpers.ObjectState pos) { pos = null; return false; }

        public bool TryGetControlPoint(out Helpers.ObjectState pos, out Team owner)
        {
            // Control points often reuse flag objects in Infantry zones.
            owner = null;
            return TryGetEnemyFlag(null, out pos);
        }

        public bool TryGetPatrolAnchor(Bots.Bot bot, out Helpers.ObjectState pos)
        {
            pos = null;
            if (bot?._state == null)
                return false;
            pos = bot._state;
            return true;
        }

        public bool TryGetDefendAnchor(Bots.Bot bot, out Helpers.ObjectState pos)
            => TryGetFriendlyFlag(bot?._team, out pos) || TryGetPatrolAnchor(bot, out pos);

        public bool TryGetSafeZone(Bots.Bot bot, out Helpers.ObjectState pos)
            => TryGetPatrolAnchor(bot, out pos);

        public bool TryGetLaneWaypoint(Bots.Bot bot, out Helpers.ObjectState pos)
            => TryGetEnemyFlag(bot?._team, out pos) || TryGetPatrolAnchor(bot, out pos);

        public bool TryGetRaidObjective(Bots.Bot bot, out Helpers.ObjectState pos)
            => TryGetEnemyFlag(bot?._team, out pos);
    }
}
