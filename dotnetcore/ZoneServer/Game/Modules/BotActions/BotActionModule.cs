using System;
using System.IO;
using InfServer.Bots;

namespace InfServer.Game.Modules.BotActions
{
    /// <summary>
    /// Arena-scoped bot action module (registry + objectives + throttle).
    /// </summary>
    public sealed class BotActionModule
    {
        public BotActionRegistry Registry { get; private set; }
        public IArenaObjectives Objectives { get; set; }
        public BotThrottleConfig Throttle { get; private set; }
        public bool Enabled { get; private set; }

        /// <summary>Resolved config path attempted at construction (may not exist if disabled).</summary>
        public string ConfigPath { get; private set; }

        public BotActionModule(bool enabled, string configPath, Arena arena, BotThrottleConfig throttle = null)
        {
            Enabled = enabled;
            Throttle = throttle ?? BotThrottleConfig.Defaults;
            Registry = new BotActionRegistry();
            Objectives = arena != null
                ? (IArenaObjectives)new ArenaDefaultObjectives(arena)
                : NullObjectives.Instance;
            ConfigPath = ResolveConfigPath(configPath);

            if (!enabled)
            {
                BotActionCatalog.EnsureInitialized();
                return;
            }

            if (string.IsNullOrEmpty(ConfigPath))
            {
                Enabled = false;
                BotActionCatalog.EnsureInitialized();
                return;
            }

            if (!File.Exists(ConfigPath))
                Registry.WriteTemplateIfMissing(ConfigPath);

            if (!File.Exists(ConfigPath))
            {
                // Template write failed or path unusable — disable the module.
                Enabled = false;
                BotActionCatalog.EnsureInitialized();
                return;
            }

            Registry.LoadFromFile(ConfigPath);
            BotActionCatalog.EnsureInitialized();
        }

        public BotActionRuntime CreateRuntime(Bot bot)
        {
            if (bot == null)
                return null;
            return new BotActionRuntime(bot, Registry, Objectives);
        }

        public void SetObjectives(IArenaObjectives objectives)
        {
            Objectives = objectives ?? NullObjectives.Instance;
        }

        static string ResolveConfigPath(string configPath)
        {
            string[] paths =
            {
                configPath,
                "modules/bot_actions.json",
                "Modules/bot_actions.json"
            };

            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path))
                    continue;
                if (File.Exists(path))
                    return path;
            }

            return string.IsNullOrEmpty(configPath) ? "modules/bot_actions.json" : configPath;
        }
    }
}
