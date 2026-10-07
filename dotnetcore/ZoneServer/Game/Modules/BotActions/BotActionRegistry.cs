using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace InfServer.Game.Modules.BotActions
{
    public sealed class ActionConfig
    {
        [JsonProperty("actionId")]
        public int ActionId { get; set; }

        [JsonProperty("actionName")]
        public string ActionName { get; set; }

        [JsonProperty("used")]
        public bool Used { get; set; } = true;

        [JsonProperty("allowedVehicleIds")]
        public List<int> AllowedVehicleIds { get; set; } = new List<int>();

        [JsonProperty("priority")]
        public float Priority { get; set; } = 0.5f;

        [JsonProperty("cooldown")]
        public int Cooldown { get; set; } = 500;

        [JsonProperty("description")]
        public string Description { get; set; } = "";

        [JsonProperty("gameModes")]
        public List<string> GameModes { get; set; } = new List<string>();
    }

    public sealed class BotActionRegistryConfig
    {
        [JsonProperty("activeGameModes")]
        public List<string> ActiveGameModes { get; set; } = new List<string>();

        [JsonProperty("actions")]
        public List<ActionConfig> Actions { get; set; } = new List<ActionConfig>();
    }

    /// <summary>
    /// Loads action metadata. Always seeds the full catalog so every ID is usable day one.
    /// </summary>
    public sealed class BotActionRegistry
    {
        readonly Dictionary<int, ActionConfig> _byId = new Dictionary<int, ActionConfig>();
        readonly Dictionary<string, ActionConfig> _byName = new Dictionary<string, ActionConfig>(StringComparer.OrdinalIgnoreCase);
        List<string> _activeGameModes = new List<string>();

        public IReadOnlyList<string> ActiveGameModes => _activeGameModes;
        public IEnumerable<ActionConfig> AllActions => _byId.Values;
        public int ActionCount => _byId.Count;

        public BotActionRegistry()
        {
            SeedDefaults();
        }

        public void LoadFromFile(string path)
        {
            SeedDefaults();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                string json = File.ReadAllText(path);
                BotActionRegistryConfig cfg = JsonConvert.DeserializeObject<BotActionRegistryConfig>(json);
                if (cfg == null)
                    return;

                if (cfg.ActiveGameModes != null && cfg.ActiveGameModes.Count > 0)
                    _activeGameModes = cfg.ActiveGameModes.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();

                if (cfg.Actions == null)
                    return;

                foreach (ActionConfig action in cfg.Actions)
                {
                    if (action == null || action.ActionId <= 0)
                        continue;
                    _byId[action.ActionId] = action;
                    if (!string.IsNullOrEmpty(action.ActionName))
                        _byName[action.ActionName] = action;
                }

                Log.write(TLog.Normal, "BotActionRegistry loaded {0} action overrides from {1}", cfg.Actions.Count, path);
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "BotActionRegistry failed to load {0}: {1}", path, ex.Message);
            }
        }

        public ActionConfig Get(BotActionId id) => Get((int)id);

        public ActionConfig Get(int id)
        {
            ActionConfig cfg;
            return _byId.TryGetValue(id, out cfg) ? cfg : null;
        }

        public bool IsAllowed(BotActionId id, int vehicleId, string gameMode = null)
        {
            ActionConfig cfg = Get(id);
            if (cfg == null || !cfg.Used)
                return false;

            if (cfg.AllowedVehicleIds != null && cfg.AllowedVehicleIds.Count > 0
                && vehicleId > 0 && !cfg.AllowedVehicleIds.Contains(vehicleId))
                return false;

            if (_activeGameModes.Count > 0 && cfg.GameModes != null && cfg.GameModes.Count > 0)
            {
                bool modeOk = false;
                foreach (string mode in cfg.GameModes)
                {
                    if (_activeGameModes.Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))
                        || string.Equals(mode, "General", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))
                    {
                        modeOk = true;
                        break;
                    }
                }
                if (!modeOk && !string.IsNullOrEmpty(gameMode))
                    modeOk = cfg.GameModes.Any(m => string.Equals(m, gameMode, StringComparison.OrdinalIgnoreCase));
                if (!modeOk)
                    return false;
            }

            return true;
        }

        public float GetPriority(BotActionId id)
        {
            ActionConfig cfg = Get(id);
            return cfg != null ? cfg.Priority : 0.5f;
        }

        public int GetCooldown(BotActionId id)
        {
            ActionConfig cfg = Get(id);
            return cfg != null ? cfg.Cooldown : 500;
        }

        void SeedDefaults()
        {
            _byId.Clear();
            _byName.Clear();
            _activeGameModes = new List<string> { "General", "CTF", "KOTH", "DM", "MOBA", "Domination", "Payload", "SnD", "Survival", "Racing", "BaseBuild", "Infection" };

            foreach (BotActionId id in Enum.GetValues(typeof(BotActionId)))
            {
                if (id == BotActionId.None)
                    continue;

                ActionConfig cfg = new ActionConfig
                {
                    ActionId = (int)id,
                    ActionName = id.ToString(),
                    Used = true,
                    Priority = DefaultPriority(id),
                    Cooldown = DefaultCooldown(id),
                    Description = id.ToString(),
                    GameModes = DefaultModes(id)
                };
                _byId[cfg.ActionId] = cfg;
                _byName[cfg.ActionName] = cfg;
            }
        }

        static float DefaultPriority(BotActionId id)
        {
            int n = (int)id;
            if (n == 8 || n == 25) return 1.0f;          // Retreat / TacticalWithdrawal
            if (n == 1 || n == 15 || n == 63 || n == 199) return 0.85f; // Attack / Seek / Hunt / PressEngage
            if (n == 198) return 0.8f;                   // RaidObjective
            if (n == 197) return 0.35f;                  // PatrolHQ
            if (n >= 43 && n <= 52) return 0.8f;         // CTF
            if (n >= 53 && n <= 62) return 0.85f;        // KOTH
            if (n >= 173 && n <= 178) return 0.8f;       // combat movement
            if (n >= 183 && n <= 185) return 0.95f;      // medic
            return 0.5f;
        }

        static int DefaultCooldown(BotActionId id)
        {
            int n = (int)id;
            if (n == 1 || n == 15 || n == 199 || (n >= 173 && n <= 178)) return 0;
            if (n == 5 || n == 4 || n == 197) return 250;
            return 400;
        }

        static List<string> DefaultModes(BotActionId id)
        {
            int n = (int)id;
            if (n <= 42 || (n >= 173 && n <= 188))
                return new List<string> { "General" };
            if (n <= 52) return new List<string> { "CTF" };
            if (n <= 62) return new List<string> { "KOTH" };
            if (n <= 72) return new List<string> { "DM" };
            if (n <= 92) return new List<string> { "MOBA" };
            if (n <= 102) return new List<string> { "Domination" };
            if (n <= 112) return new List<string> { "Payload" };
            if (n <= 122) return new List<string> { "SnD" };
            if (n <= 132) return new List<string> { "DM", "CTF", "KOTH" };
            if (n <= 142) return new List<string> { "Survival" };
            if (n <= 152) return new List<string> { "Racing" };
            if (n <= 162) return new List<string> { "BaseBuild" };
            if (n <= 172) return new List<string> { "Infection" };
            if (n == 189) return new List<string> { "CTF" };
            if (n == 190) return new List<string> { "KOTH" };
            if (n == 191) return new List<string> { "Payload" };
            if (n == 192) return new List<string> { "SnD" };
            if (n == 193) return new List<string> { "Domination" };
            if (n == 195) return new List<string> { "Survival" };
            if (n == 196) return new List<string> { "Infection" };
            if (n == 197 || n == 198 || n == 199) return new List<string> { "General" };
            return new List<string> { "General" };
        }

        /// <summary>Write a full template next to the zone if missing.</summary>
        public void WriteTemplateIfMissing(string path)
        {
            if (string.IsNullOrEmpty(path) || File.Exists(path))
                return;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var cfg = new BotActionRegistryConfig
                {
                    ActiveGameModes = new List<string>(_activeGameModes),
                    Actions = _byId.Values.OrderBy(a => a.ActionId).ToList()
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                Log.write(TLog.Normal, "BotActionRegistry wrote template {0}", path);
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "BotActionRegistry could not write template: {0}", ex.Message);
            }
        }
    }
}
