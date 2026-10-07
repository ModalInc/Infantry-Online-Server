using System;

namespace InfServer.Game.Modules.BotActions
{
    /// <summary>
    /// Zone-wide bot activity budget. Loaded from server.xml <c>modules/botThrottle</c>.
    /// Dial these per zone to trade responsiveness vs lag.
    /// </summary>
    public sealed class BotThrottleConfig
    {
        public static BotThrottleConfig Defaults { get; } = new BotThrottleConfig();

        /// <summary>Last loaded zone throttle (set from Arena.initModules).</summary>
        public static BotThrottleConfig Active { get; set; } = Defaults;

        /// <summary>Reuse hostility probe within this TTL (ms).</summary>
        public int HostileSenseReuseMs { get; set; } = 180;

        /// <summary>Skip re-scoring for N polls after a heavy combat enqueue while hostiles stay true.</summary>
        public int CombatPlanBurstPolls { get; set; } = 20;

        /// <summary>Multiply seek/anchor eval intervals when no hostiles.</summary>
        public int PeaceSeekMultiplier { get; set; } = 5;

        public int SeekEvalMs { get; set; } = 180;
        public int RetreatEvalMs { get; set; } = 400;
        public int RepositionEvalMs { get; set; } = 520;
        public int AnchorEvalMs { get; set; } = 2200;

        public int FireExecMinMs { get; set; } = 1;
        public int RetreatExecMinMs { get; set; } = 100;

        public int RetreatHealthBypass { get; set; } = 10;
        public int RepositionHealthBypass { get; set; } = 15;

        /// <summary>Min ms between large analog drive vector changes while engaged.</summary>
        public int CombatMotionCommitMs { get; set; } = 160;

        /// <summary>Min ms between combat pathfinder requeues.</summary>
        public int CombatPathRequeueMs { get; set; } = 2800;

        /// <summary>Default seek range for AttackPlayer / SeekEnemy.</summary>
        public int DefaultSeekRange { get; set; } = 6000;

        /// <summary>Engage: peel only when this close (raw pixels) — otherwise stay aggressive in the dance band.</summary>
        public int EngageTooCloseRaw { get; set; } = 8;

        /// <summary>Engage: preferred inner ring around target (raw pixels).</summary>
        public int EngageMinRadiusRaw { get; set; } = 15;

        /// <summary>Engage: fallback outer dance max when no far weapon range is known (raw).</summary>
        public int EngageIdealMaxRaw { get; set; } = 1200;

        /// <summary>Ms of continuous fire in a burst window.</summary>
        public int EngageBurstOnMs { get; set; } = 1400;

        /// <summary>Ms of suppress / pin pause between bursts (still moving + aiming).</summary>
        public int EngageBurstOffMs { get; set; } = 90;

        /// <summary>
        /// Max distance from combat anchor (HQ / crash / raid PDB) while chasing.
        /// Beyond this the bot peels home instead of pursuing further. 0 = no leash.
        /// Default matches seek/aggro so walk-bys past the base get chased out of the disk.
        /// </summary>
        public int CombatLeashRaw { get; set; } = 6500;

        public static BotThrottleConfig LoadFromServerConfig(ConfigSetting root)
        {
            BotThrottleConfig cfg = new BotThrottleConfig();
            if (root == null)
                return cfg;

            try
            {
                ConfigSetting t = root["modules/botThrottle"];
                if (t == null)
                    return cfg;

                cfg.HostileSenseReuseMs = ReadInt(t, "hostileSenseReuseMs", cfg.HostileSenseReuseMs);
                cfg.CombatPlanBurstPolls = ReadInt(t, "combatPlanBurstPolls", cfg.CombatPlanBurstPolls);
                cfg.PeaceSeekMultiplier = ReadInt(t, "peaceSeekMultiplier", cfg.PeaceSeekMultiplier);
                cfg.SeekEvalMs = ReadInt(t, "seekEvalMs", cfg.SeekEvalMs);
                cfg.RetreatEvalMs = ReadInt(t, "retreatEvalMs", cfg.RetreatEvalMs);
                cfg.RepositionEvalMs = ReadInt(t, "repositionEvalMs", cfg.RepositionEvalMs);
                cfg.AnchorEvalMs = ReadInt(t, "anchorEvalMs", cfg.AnchorEvalMs);
                cfg.FireExecMinMs = ReadInt(t, "fireExecMinMs", cfg.FireExecMinMs);
                cfg.RetreatExecMinMs = ReadInt(t, "retreatExecMinMs", cfg.RetreatExecMinMs);
                cfg.RetreatHealthBypass = ReadInt(t, "retreatHealthBypass", cfg.RetreatHealthBypass);
                cfg.RepositionHealthBypass = ReadInt(t, "repositionHealthBypass", cfg.RepositionHealthBypass);
                cfg.CombatMotionCommitMs = ReadInt(t, "combatMotionCommitMs", cfg.CombatMotionCommitMs);
                cfg.CombatPathRequeueMs = ReadInt(t, "combatPathRequeueMs", cfg.CombatPathRequeueMs);
                cfg.DefaultSeekRange = ReadInt(t, "defaultSeekRange", cfg.DefaultSeekRange);
                cfg.EngageTooCloseRaw = ReadInt(t, "engageTooCloseRaw", cfg.EngageTooCloseRaw);
                cfg.EngageMinRadiusRaw = ReadInt(t, "engageMinRadiusRaw", cfg.EngageMinRadiusRaw);
                cfg.EngageIdealMaxRaw = ReadInt(t, "engageIdealMaxRaw", cfg.EngageIdealMaxRaw);
                cfg.EngageBurstOnMs = ReadInt(t, "engageBurstOnMs", cfg.EngageBurstOnMs);
                cfg.EngageBurstOffMs = ReadInt(t, "engageBurstOffMs", cfg.EngageBurstOffMs);
                cfg.CombatLeashRaw = ReadInt(t, "combatLeashRaw", cfg.CombatLeashRaw);
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "BotThrottleConfig load failed, using defaults: {0}", ex.Message);
            }

            return cfg;
        }

        static int ReadInt(ConfigSetting parent, string name, int fallback)
        {
            try
            {
                ConfigSetting child = parent[name];
                if (child == null)
                    return fallback;
                int v = child.intValue;
                return v > 0 ? v : fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
