using System;

namespace InfServer.Bots
{
    /// <summary>
    /// Zone-wide switches for the shared CTF-tactics bot layer (skill tiers, dodging, wall probing,
    /// per-human pose budget). Off unless server.xml <c>modules/ctftactics</c> is true; tuned by
    /// the optional <c>modules/ctftacticsSettings</c> block.
    /// </summary>
    public static class BotFeatures
    {
        /// <summary>True when the <c>ctftactics</c> module is in use.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>
        /// Skill tier spread: <c>mix</c> (rookie 25 / regular 45 / veteran 22 / ace 8 %), a single tier
        /// (<c>rookie</c>, <c>regular</c>, <c>veteran</c>, <c>ace</c>), <c>elite</c> (veteran + ace),
        /// <c>weak</c> (rookie + regular), or <c>off</c> (legacy instant, exact aim).
        /// </summary>
        public static string SkillMode { get; private set; } = "off";

        public static bool SkillEnabled => SkillMode != "off";

        /// <summary>Api-mode bots weigh incoming shots and swerve or stop to make them miss.</summary>
        public static bool Dodge;

        /// <summary>Combat drive probes for walls before strafing / stepping, and pinned bots back out.</summary>
        public static bool WallProbe;

        /// <summary>Per human, only this many nearest moving bots get the full-rate pose stream (0 = all).</summary>
        public static int RouteNearCap;

        /// <summary>Pose interval for observed moving bots outside every human's near cap.</summary>
        public static int RouteSlowMs = 50;

        /// <summary>Turn the module on with its standard values (settings may then override them).</summary>
        public static void EnableModule()
        {
            Enabled = true;
            SkillMode = "mix";
            Dodge = true;
            WallProbe = true;
            RouteNearCap = 12;
            RouteSlowMs = 50;
        }

        /// <summary>Module not in use: every feature off, bots behave as before.</summary>
        public static void DisableModule()
        {
            Enabled = false;
            SkillMode = "off";
            Dodge = false;
            WallProbe = false;
            RouteNearCap = 0;
            RouteSlowMs = 50;
        }

        public static bool SetSkillMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
                return false;
            mode = mode.Trim().ToLowerInvariant();
            switch (mode)
            {
                case "mix":
                case "rookie":
                case "regular":
                case "veteran":
                case "ace":
                case "elite":
                case "weak":
                case "off":
                    SkillMode = mode;
                    return true;
            }
            return false;
        }
    }
}
