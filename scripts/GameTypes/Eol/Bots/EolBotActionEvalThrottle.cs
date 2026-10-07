using InfServer.Game.Modules.BotActions;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Bot activity throttles — backed by zone <see cref="BotThrottleConfig"/> (server.xml modules/botThrottle).
    /// Evaluation queues stay in script; intervals are dialed per ZoneServer process.
    /// </summary>
    internal static class EolBotActionEvalThrottle
    {
        /// <summary>Updated when an arena loads modules (one ZoneServer = one server.xml).</summary>
        public static BotThrottleConfig Active
        {
            get { return BotThrottleConfig.Active; }
        }

        public static int HostileSenseReuseMs => Active.HostileSenseReuseMs;
        public static int CombatPlanBurstPolls => Active.CombatPlanBurstPolls;
        public static int PeaceSeekThrottleMultiplier => Active.PeaceSeekMultiplier;

        public static int RoamRepositionMs => Active.RepositionEvalMs;
        public static int RoamSeekMs => Active.SeekEvalMs;
        public static int RoamAnchorMs => Active.AnchorEvalMs;
        public static int RoamRepositionHealthBypass => Active.RepositionHealthBypass;

        public static int DefenseRetreatMs => Active.RetreatEvalMs;
        public static int DefenseRetreatHealthBypass => Active.RetreatHealthBypass;

        public static int AlienRepositionMs => Active.RepositionEvalMs;
        public static int AlienSeekMs => Active.SeekEvalMs;
        public static int AlienRepositionHealthBypass => Active.RepositionHealthBypass;

        public static int FireActionExecMinMs => Active.FireExecMinMs;
        public static int RetreatActionExecMinMs => Active.RetreatExecMinMs;
        public static int RoamSeekExecMinMs => Active.FireExecMinMs;
        public static int RoamRepositionExecMinMs => Active.RetreatExecMinMs;
        public static int RoamAnchorExecMinMs => Active.RetreatExecMinMs;

        public static int CombatMotionCommitMinMs => Active.CombatMotionCommitMs;
    }
}
