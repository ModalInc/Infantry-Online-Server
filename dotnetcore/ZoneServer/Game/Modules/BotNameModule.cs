using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

using InfServer.Bots;

namespace InfServer.Game.Modules
{
    /// <summary>
    /// Per-arena display names for bots. Uses ~BotN (counting up); JSON/default name pools are unused.
    /// </summary>
    public class BotNameModule
    {
        readonly object _lock = new object();
        // Name-list pool kept for reference but not assigned (Bot mode).
        readonly List<string> _pool;
        readonly HashSet<string> _inUse;
        readonly Dictionary<Bot, string> _botToName;
        int _botSeq;

        /*
        static readonly string[] DefaultNames =
        {
            "Tom Bot", "Bob Bot", "Sandy Bot", "Betty Bot", "Dan Bot", "Jess Bot", "Jim Bot", "Ted Bot", "Sam Bot", "Adam Bot",
            "Luke Bot", "Lee Bot", "Kelly Bot", "Amy Bot", "Eva Bot", "Eli Bot", "Zack Bot", "Leo Bot", "Zoe Bot", "Ivy Bot",
            "Mia Bot", "Rory Bot", "Remi Bot", "Ian Bot", "Jude Bot", "Niko Bot", "Finn Bot", "Ares Bot", "Wade Bot", "Cali Bot",
            "Karen Bot", "Jeb Bot", "Frank Bot", "Karl Bot", "Tim Bot", "Bill Bot", "Mike Bot", "Captain Bot", "Queen Bot", "King Bot",
            "Leader Bot", "Fred Bot", "Phil Bot", "Ro Bot", "Archie Bot", "Tory Bot", "Alex Bot", "Nao Bot", "Milo Bot", "Doc Bot",
            "Ryder Bot", "Frodo Bot", "Joe Bot", "Evan Bot", "Hand Bot", "Steve Bot", "Rita Bot", "Shelly Bot", "Teller Bot", "Wick Bot",
            "Calvin Bot", "Habs Bot", "Roe Bot", "Ross Bot", "Gandalf Bot", "Sauron Bot", "Arwen Bot", "Bilbo Bot", "Gimli Bot", "Gollum Bot",
            "Legolas Bot", "Pippin Bot", "Elrond Bot", "Saruman Bot", "Samwise Bot", "Theoden Bot", "Boromir Bot", "Faramir Bot", "Galadriel Bot", "Eowyn Bot",
            "Merry Bot", "Harry Bot", "Ron Bot", "Hermione Bot", "Luna Bot", "Sirius Bot", "Draco Bot", "Cedric Bot", "Hagrid Bot", "Snape Bot",
            "Sin Bot", "Sun Bot", "Sharon Bot", "Ken Bot", "Barbie Bot", "Chuck Bot", "Orla Bot", "Lia Bot", "Sky Bot", "Bo Bot",
            "Peter Bot", "Pat Bot", "NOD Bot", "Mode Bot", "Ace Bot", "Jovan Bot", "GDI Bot", "Kaze Bot", "DP Bot", "Kev Bot",
            "Emp Bot", "Deeks Bot"
        };
        */

        public BotNameModule(string jsonPath)
        {
            _inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _botToName = new Dictionary<Bot, string>();
            // Pool load retained but unused while ~BotN naming is active.
            _pool = LoadPool(jsonPath);
            Log.write(TLog.Normal, "BotNameModule Bot mode (~BotN); pool size {0} unused.", _pool.Count);
        }

        static List<string> LoadPool(string jsonPath)
        {
            // Previously assigned names from bot_names.json / DefaultNames.
            // ~BotN mode does not consume this list.
            string baseDir = AppContext.BaseDirectory;
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(jsonPath))
            {
                paths.Add(jsonPath);
                if (!string.IsNullOrEmpty(baseDir))
                    paths.Add(Path.Combine(baseDir, jsonPath));
            }
            paths.Add("modules/bot_names.json");
            paths.Add("Modules/bot_names.json");
            if (!string.IsNullOrEmpty(baseDir))
            {
                paths.Add(Path.Combine(baseDir, "modules/bot_names.json"));
                paths.Add(Path.Combine(baseDir, "Modules/bot_names.json"));
            }

            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;

                try
                {
                    string json = File.ReadAllText(path);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("names", out JsonElement namesEl) &&
                        namesEl.ValueKind == JsonValueKind.Array)
                    {
                        var loaded = namesEl.EnumerateArray()
                            .Select(e => NormalizeName(e.GetString()))
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (loaded.Count > 0)
                            return loaded;
                    }
                }
                catch (Exception ex)
                {
                    Log.write(TLog.Warning, "BotNameModule could not read {0}: {1}", path, ex.Message);
                }
            }

            return new List<string>();
        }

        static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            name = name.Trim();
            return name.Length > 31 ? name.Substring(0, 31) : name;
        }

        /// <summary>Give bots realistic names (callsigns / Skrall names) instead of ~BotN.</summary>
        public static bool UseClassNames = true;

        readonly Random _nameRng = new Random();

        static readonly string[] Callsigns =
        {
            "Havoc", "Razor", "Ghost", "Nomad", "Viper", "Reaper", "Maverick", "Blitz", "Rook", "Outlaw",
            "Saber", "Talon", "Spectre", "Grim", "Hawk", "Jackal", "Kestrel", "Lynx", "Mako", "Onyx",
            "Phantom", "Quake", "Rogue", "Shade", "Torque", "Vandal", "Wraith", "Zealot", "Apex", "Brick",
            "Cinder", "Dagger", "Echo", "Flint", "Gunner", "Hex", "Iron", "Jinx", "Kilo", "Lancer",
            "Mantis", "Nova", "Oxide", "Pyro", "Raptor", "Sly", "Tango", "Uzi", "Vex", "Warden",
            "Xenon", "Yeti", "Zulu", "Ajax", "Bishop", "Cobra", "Dusk", "Ember", "Fang", "Grizzly",
            "Hunter", "Ion", "Jester", "Knox", "Locust", "Mercer", "Nightshade", "Omen", "Prowler", "Quill",
            "Rampage", "Sniper", "Thorn", "Umbra", "Valkyrie", "Wolf", "Axel", "Boomer", "Crusader", "Drifter",
            "Enigma", "Frost", "Gator", "Hydra", "Icarus", "Juggernaut", "Kraken", "Legion", "Moth", "Nitro",
            "Orca", "Pike", "Rebel", "Scythe", "Titan", "Vulture", "Wasp", "Zero", "Ash", "Bane",
            "Colt", "Diesel", "Edge", "Fury", "Gauge", "Hammer", "Ivy", "Jolt", "Krait", "Lotus",
            "Mongoose", "Nash", "Ozzy", "Patch", "Riot", "Steel", "Trigger", "Venom", "Whisper", "Zane",
        };

        static readonly string[] AlienStart = { "Vr", "Zh", "Thr", "Kr", "Sk", "Gr", "Xa", "Ny", "Qu", "Sz", "Dr", "Ul", "Ix", "Vh", "Kz" };
        static readonly string[] AlienMid = { "a", "u", "o", "ae", "ix", "ul", "ar", "ee", "or", "y" };
        static readonly string[] AlienEnd = { "sk", "kar", "xis", "rath", "zz", "gul", "mok", "thar", "vex", "nak", "ss", "rr", "lith", "krai" };

        string AlienName()
        {
            string n = AlienStart[_nameRng.Next(AlienStart.Length)] + AlienMid[_nameRng.Next(AlienMid.Length)] + AlienEnd[_nameRng.Next(AlienEnd.Length)];
            return char.ToUpper(n[0]) + n.Substring(1);
        }

        /// <summary>Assign the next ~BotN name to a bot.</summary>
        public string Acquire(Bot bot, Func<string, bool> isTakenElsewhere = null)
        {
            if (bot == null)
                return null;

            lock (_lock)
            {
                if (_botToName.TryGetValue(bot, out string existing) && !string.IsNullOrEmpty(existing))
                    return existing;

                string chosen = null;

                // Realistic names, no numbers (local 2026-11-14): player-style callsigns for humans, Skrall-style
                // names for aliens. Unique; once single callsigns run out, two are combined ("Ghost Viper").
                if (UseClassNames)
                {
                    bool alien = bot._type?.Name != null && bot._type.Name.StartsWith("Alien", StringComparison.OrdinalIgnoreCase);
                    for (int attempt = 0; attempt < 400 && chosen == null; attempt++)
                    {
                        string candidate;
                        if (alien)
                            candidate = AlienName();
                        else if (attempt < 150)
                            candidate = Callsigns[_nameRng.Next(Callsigns.Length)];
                        else
                            candidate = Callsigns[_nameRng.Next(Callsigns.Length)] + " " + Callsigns[_nameRng.Next(Callsigns.Length)];
                        candidate = NormalizeName("{" + candidate + "}");   // {Name} marks a bot (local 2026-11-14)
                        if (string.IsNullOrEmpty(candidate) || _inUse.Contains(candidate))
                            continue;
                        if (isTakenElsewhere != null && isTakenElsewhere(candidate))
                            continue;
                        chosen = candidate;
                    }
                }

                for (int attempt = 0; attempt < 256 && chosen == null; attempt++)
                {
                    int n = Interlocked.Increment(ref _botSeq);
                    string candidate = "~Bot" + n;
                    if (_inUse.Contains(candidate))
                        continue;
                    if (isTakenElsewhere != null && isTakenElsewhere(candidate))
                        continue;
                    chosen = candidate;
                    break;
                }

                if (chosen == null)
                    chosen = "~Bot" + Interlocked.Increment(ref _botSeq);

                _inUse.Add(chosen);
                _botToName[bot] = chosen;
                return chosen;
            }
        }

        /// <summary>Return a ~Bot name to the free list.</summary>
        public void Release(Bot bot)
        {
            if (bot == null)
                return;

            lock (_lock)
            {
                if (!_botToName.TryGetValue(bot, out string name))
                    return;

                _botToName.Remove(bot);
                if (string.IsNullOrEmpty(name))
                    return;

                _inUse.Remove(name);
            }
        }

        public static string GetDisplayName(Bot bot)
        {
            if (bot == null)
                return "?";
            if (!string.IsNullOrEmpty(bot._alias))
                return bot._alias;
            if (bot._type != null && !string.IsNullOrEmpty(bot._type.Name))
                return bot._type.Name;
            return "Bot " + bot._id;
        }

        public static string GetKillfeedName(Bot bot)
        {
            if (bot == null)
                return "Bot";
            string name = GetDisplayName(bot);
            return string.IsNullOrEmpty(name) || name == "?" ? "Bot " + bot._id : name;
        }

        public static string GetKillerName(Player killer, Bot killerBot)
        {
            if (killer != null)
                return killer._alias;
            if (killerBot != null)
                return GetKillfeedName(killerBot);
            return "Unknown";
        }
    }
}
