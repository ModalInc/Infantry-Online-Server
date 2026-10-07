using System;
using System.Collections.Generic;
using InfServer.Bots;

namespace InfServer.Game.Modules.BotActions
{
    /// <summary>
    /// Per-bot action executor. Scripts call <see cref="Request"/> then <see cref="Tick"/> each poll
    /// (or enable <see cref="AutoTick"/> and let <see cref="Bot.poll"/> drive it).
    /// </summary>
    public sealed class BotActionRuntime
    {
        readonly Bot _bot;
        readonly BotActionRegistry _registry;
        readonly Dictionary<int, int> _lastRequestTick = new Dictionary<int, int>();

        IBotBehavior _current;
        BotActionContext _ctx;
        IArenaObjectives _objectives;

        public BotActionRuntime(Bot bot, BotActionRegistry registry, IArenaObjectives objectives = null)
        {
            _bot = bot;
            _registry = registry ?? new BotActionRegistry();
            _objectives = objectives ?? NullObjectives.Instance;
            BotActionCatalog.EnsureInitialized();
            RebuildContext();
            if (_ctx != null && bot?._arena?.BotThrottle != null)
                _ctx.SeekRange = bot._arena.BotThrottle.DefaultSeekRange;
        }

        public BotActionId Current { get; private set; } = BotActionId.None;
        public bool IsRunning => Current != BotActionId.None && _current != null;

        /// <summary>When true, <see cref="Bot.poll"/> ticks the current action before applying motion.</summary>
        public bool AutoTick { get; set; }

        public IArenaObjectives Objectives
        {
            get => _objectives;
            set
            {
                _objectives = value ?? NullObjectives.Instance;
                if (_ctx != null)
                    _ctx.Objectives = _objectives;
            }
        }

        public void SetSeekRange(int range)
        {
            if (_ctx != null)
                _ctx.SeekRange = range > 0 ? range : BotActionHelpers.DefaultSeekRange;
        }

        public void SetAnchor(Protocol.Helpers.ObjectState anchor)
        {
            if (_ctx != null)
                _ctx.Anchor = anchor;
        }

        public bool Request(BotActionId id, bool ignoreCooldown = false)
        {
            if (_bot == null || id == BotActionId.None)
            {
                Clear();
                return false;
            }

            int vehicleId = _bot._type != null ? _bot._type.Id : 0;
            if (_registry != null && !_registry.IsAllowed(id, vehicleId))
                return false;

            int now = Environment.TickCount;
            if (!ignoreCooldown && _registry != null)
            {
                int cd = _registry.GetCooldown(id);
                int last;
                if (cd > 0 && _lastRequestTick.TryGetValue((int)id, out last) && now - last < cd
                    && Current == id)
                    return true;
            }

            if (Current == id && _current != null)
            {
                _lastRequestTick[(int)id] = now;
                return true;
            }

            ClearCurrentBehavior();
            _current = BotActionCatalog.Get(id);
            if (_current == null)
                return false;

            Current = id;
            RebuildContext();
            _current.Enter(_ctx);
            _lastRequestTick[(int)id] = now;
            return true;
        }

        public BehaviorResult Tick(int now = 0)
        {
            if (_current == null || Current == BotActionId.None)
                return BehaviorResult.Failed;

            if (now == 0)
                now = Environment.TickCount;

            // Pick up script-updated arena objectives (e.g. EolObjectives after init).
            if (_bot?._arena?.BotActionModule != null)
                _objectives = _bot._arena.BotActionModule.Objectives;

            RebuildContext();
            BehaviorResult result = _current.Tick(_ctx, now);
            if (result == BehaviorResult.Success || result == BehaviorResult.Failed)
                ClearCurrentBehavior();
            return result;
        }

        public Player LastPlayerTarget => _ctx?.CurrentPlayerTarget;
        public Bots.Bot LastBotTarget => _ctx?.CurrentBotTarget;

        /// <summary>
        /// Seed / refresh the sticky player lock before AttackPlayer / SeekEnemy so a brief
        /// failed retarget does not wipe script-held combat focus.
        /// </summary>
        public void SuggestPlayerTarget(Player player)
        {
            RebuildContext();
            if (_ctx == null)
                return;
            if (player == null || player.IsDead || player.IsSpectator)
                return;
            _ctx.CurrentPlayerTarget = player;
            _ctx.CurrentBotTarget = null;
        }

        public void Clear()
        {
            ClearCurrentBehavior();
        }

        void ClearCurrentBehavior()
        {
            if (_current != null && _ctx != null)
            {
                try { _current.Exit(_ctx); }
                catch { /* teardown */ }
            }
            _current = null;
            Current = BotActionId.None;
        }

        void RebuildContext()
        {
            if (_ctx == null)
            {
                _ctx = new BotActionContext
                {
                    Bot = _bot,
                    Arena = _bot?._arena,
                    Registry = _registry,
                    Objectives = _objectives,
                    SeekRange = BotActionHelpers.DefaultSeekRange
                };
            }
            else
            {
                _ctx.Bot = _bot;
                _ctx.Arena = _bot?._arena;
                _ctx.Registry = _registry;
                _ctx.Objectives = _objectives;
            }
        }
    }
}
