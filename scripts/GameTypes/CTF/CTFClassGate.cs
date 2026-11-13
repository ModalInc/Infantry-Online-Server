using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Assets;
using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// What it costs to change class: ?swap, ?b / ?bw / ?bwd (when the build needs another class), the armory
    /// class tiles and the F11 skill shop all go through here.
    ///
    ///   energy    (default) the player needs N energy and pays it. N is 500 unless changed.
    ///   cooldown  no energy cost. A class you have USED (left the dropship / been warped in it) can't be switched
    ///             back to for N seconds after you leave it (default 20). Switching to a class you haven't
    ///             played yet is instant, a swap you haven't used yet can be undone straight away (wrong
    ///             macro), and dying clears it all. Built for the cases players raised (2026-10-07): rep
    ///             cycling through infil, an engineer swapping away and back to reuse a turret box, and
    ///             people stuck in the dropship after hitting the wrong macro.
    ///   off       free and instant (for testing).
    ///
    /// Cooldown mode is only enforced while a round is actually being played (see Script_CTF.ClassGateRoundLive:
    /// a running OvD clock, or a live game that has had its first kill / flag pickup). Between games, during a
    /// draft or while a lobby is still sorting itself out, players can swap freely; a swap that WOULD have been
    /// blocked says so, and does not start the cooldown. *classgate downtime off enforces it everywhere.
    ///
    /// Static, so every arena in the zone process shares one setting. A mod changes it in game with *classgate and
    /// it is written to ClassGateConfig.txt at once, so it survives restarts. No file means the historical rule:
    /// 500 energy. The F11 energy gate used to live in the client (SkillScreenEnergyNeeded in the cfg, now 0) and
    /// is applied here instead, in Shop.SkillRequest / Shop.SkillPurchase.
    /// </summary>
    static class ClassGate
    {
        private const string CONFIG_PATH = "scripts/GameTypes/CTF/ClassGateConfig.txt";
        private const int DEFAULT_ENERGY = 500;
        private const int DEFAULT_COOLDOWN_S = 20;

        private static readonly object _lock = new object();
        private static bool _loaded;
        private static string _mode = "energy";
        private static int _energy = DEFAULT_ENERGY;
        private static int _cooldownS = DEFAULT_COOLDOWN_S;
        private static bool _downtimeFree = true;
        // Cooldown mode, per player, keyed by alias (never by Player, so a disconnect cannot pin a player object).
        private class SwapState
        {
            public string Current;        // base class they are on, once a change has been seen
            public string Before;         // their class when the last change was checked (the one being left)
            public bool Used = true;      // played the current class (left the dropship / warped) since the last change
            public readonly Dictionary<string, DateTime> LeftAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
        private static readonly Dictionary<string, SwapState> _swaps = new Dictionary<string, SwapState>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] BaseClasses =
            { "Infiltrator", "Infantry", "Heavy Weapons", "Squad Leader", "Field Medic", "Combat Engineer", "Jump Trooper" };

        /// <summary>"Infantry Offense Cmp6/PF" -> "Infantry": premades and variants count as their base class.</summary>
        public static string BaseClass(string skillName)
        {
            if (String.IsNullOrEmpty(skillName)) return null;
            foreach (string b in BaseClasses)
                if (skillName.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0) return b;
            return skillName;
        }

        private static SwapState StateFor(string alias)
        {
            SwapState st;
            if (!_swaps.TryGetValue(alias, out st)) { st = new SwapState(); _swaps[alias] = st; }
            return st;
        }

        /// <summary>
        /// The player is out playing their class (off the dropship / store, or warped in): their next change
        /// is a real one, so the class they leave starts its return timer. Called from the arena's 1Hz poll.
        /// </summary>
        public static void MarkUsed(string alias)
        {
            lock (_lock)
            {
                SwapState st;
                if (_swaps.TryGetValue(alias, out st)) st.Used = true;
            }
        }

        /// <summary>Death clears every timer, and the respawn in the dropship counts as unused (free undo).</summary>
        public static void OnDeath(string alias)
        {
            lock (_lock)
            {
                var st = StateFor(alias);
                st.LeftAt.Clear();
                st.Current = null;
                st.Before = null;
                st.Used = false;
            }
        }

        /// <summary>
        /// True for the skills that are a class change in the F11 shop: the seven classes and the premade
        /// kits. Event classes, staff/referee flags and attributes are not gated.
        /// </summary>
        public static bool IsClassSkill(SkillInfo skill)
        {
            if (skill == null || String.IsNullOrEmpty(skill.Category)) return false;
            return skill.Category.Equals("Classes", StringComparison.OrdinalIgnoreCase)
                || skill.Category.Equals("Classes - Premades", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Energy the swap takes: 0 unless the gate is in energy mode.</summary>
        public static int EnergyCost
        {
            get { EnsureLoaded(); lock (_lock) { return _mode == "energy" ? _energy : 0; } }
        }

        /// <summary>
        /// Null when the player may change class now, otherwise the message to show them. notice is a courtesy line
        /// for a swap that is allowed only because no round is being played (the cooldown would have blocked it).
        /// </summary>
        public static string Check(Player player, string targetSkill, string currentSkill, bool roundLive, out string notice)
        {
            notice = null;
            EnsureLoaded();
            lock (_lock)
            {
                if (_mode == "energy")
                {
                    if (player._state.energy < _energy)
                        return String.Format("*You need {0} energy to change your skill. Current energy: {1}.", _energy, player._state.energy);
                    return null;
                }
                if (_mode == "cooldown")
                {
                    var st = StateFor(player._alias);
                    st.Before = BaseClass(currentSkill) ?? st.Current;
                    // Only a class you actually PLAYED gets a return timer (see Commit), so undoing a wrong macro is
                    // free on its own: the class you go back to was never played. An engineer who played, swapped
                    // away in the dropship and swaps straight back still waits - that is the turret-box chain.
                    string target = BaseClass(targetSkill);
                    DateTime left;
                    if (target != null && st.LeftAt.TryGetValue(target, out left))
                    {
                        double wait = _cooldownS - (DateTime.UtcNow - left).TotalSeconds;
                        if (wait > 0)
                        {
                            int secs = (int)Math.Ceiling(wait);
                            if (!roundLive && _downtimeFree)
                            {
                                notice = String.Format("*Between rounds: the {0}s return-to-class timer is not enforced now (it would have blocked {1} for {2}s).", _cooldownS, target, secs);
                                return null;
                            }
                            return String.Format("*You just played {0}: you can switch back to it in {1} second{2}. Any other class is fine now.", target, secs, secs == 1 ? "" : "s");
                        }
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Call after a class change actually happened. The class left behind starts its return timer only if it
        /// was played (an unused swap is an undo) and a round is live (or downtime_free is off).
        /// </summary>
        public static void Commit(Player player, string newSkill, bool roundLive)
        {
            EnsureLoaded();
            lock (_lock)
            {
                if (_mode != "cooldown") return;
                var st = StateFor(player._alias);
                string next = BaseClass(newSkill);
                string prev = st.Before ?? st.Current;
                if (st.Used && prev != null && !prev.Equals(next, StringComparison.OrdinalIgnoreCase) && (roundLive || !_downtimeFree))
                    st.LeftAt[prev] = DateTime.UtcNow;
                st.Current = next;
                st.Before = null;
                st.Used = false;
            }
        }

        public static void Forget(string alias)
        {
            lock (_lock) { _swaps.Remove(alias); }
        }

        #region Config file

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (!_loaded) Load();
            }
        }

        private static void Load()
        {
            _loaded = true;
            _mode = "energy"; _energy = DEFAULT_ENERGY; _cooldownS = DEFAULT_COOLDOWN_S; _downtimeFree = true;
            try
            {
                if (!File.Exists(CONFIG_PATH)) return;
                foreach (string raw in File.ReadAllLines(CONFIG_PATH))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int hash = line.IndexOf('#');
                    if (hash > 0) line = line.Substring(0, hash).Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    int n;
                    switch (key)
                    {
                        case "mode":
                            val = val.ToLowerInvariant();
                            if (val == "energy" || val == "cooldown" || val == "off") _mode = val;
                            break;
                        case "energy":
                            if (Int32.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 1000) _energy = n;
                            break;
                        case "cooldown_seconds":
                            if (Int32.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 3600) _cooldownS = n;
                            break;
                        case "downtime_free":
                            val = val.ToLowerInvariant();
                            _downtimeFree = !(val == "0" || val == "off" || val == "false" || val == "no");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ClassGate] Could not read " + CONFIG_PATH + ": " + ex.Message);
            }
            Console.WriteLine("[ClassGate] " + Describe());
        }

        private static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Class change gate for ?swap, ?b / ?bw / ?bwd, armory class tiles and the F11 shop. Written by the *classgate mod command.");
                sb.AppendLine("# mode: energy (pay 'energy' energy) | cooldown (can't switch back to a class you just played for 'cooldown_seconds'; free undo; death clears) | off");
                sb.AppendLine("# downtime_free: 1 = the cooldown is not enforced between rounds or during drafts (it only counts while a round is being played)");
                sb.AppendLine("mode=" + _mode);
                sb.AppendLine("energy=" + _energy.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("cooldown_seconds=" + _cooldownS.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("downtime_free=" + (_downtimeFree ? "1" : "0"));
                File.WriteAllText(CONFIG_PATH, sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ClassGate] Could not save " + CONFIG_PATH + ": " + ex.Message);
            }
        }

        private static string Describe()
        {
            if (_mode == "cooldown")
                return String.Format("COOLDOWN: {0}s before switching back to a class you just played (free undo, death clears it), no energy cost, {1}", _cooldownS,
                    _downtimeFree ? "only enforced while a round is being played" : "enforced everywhere");
            if (_mode == "off") return "OFF: class changes are free and instant";
            return String.Format("ENERGY: {0} energy per class change", _energy);
        }

        #endregion

        /// <summary>
        /// *classgate                   show the current rule
        /// *classgate energy [N]        pay N energy (default 500)
        /// *classgate cooldown [S]      wait S seconds, no energy (default 20)
        /// *classgate downtime [on|off] cooldown free between rounds (default on)
        /// *classgate off               free and instant
        /// *classgate reset [alias]     clear cooldown timers (everyone, or one player) for testing
        /// *classgate reload            re-read ClassGateConfig.txt
        /// </summary>
        public static void Command(Player mod, string payload)
        {
            EnsureLoaded();
            string[] parts = (payload ?? "").Trim().Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            lock (_lock)
            {
                int n;
                switch (sub)
                {
                    case "":
                        mod.sendMessage(0, "Class change gate - " + Describe() + ".");
                        mod.sendMessage(0, "*classgate energy [N] | cooldown [S] | downtime [on|off] | off | reset [alias] | reload  (saved to ClassGateConfig.txt)");
                        return;

                    case "energy":
                        if (arg.Length > 0 && !(Int32.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 1000))
                        { mod.sendMessage(-1, "Energy must be a number from 1 to 1000."); return; }
                        _mode = "energy";
                        _energy = arg.Length > 0 ? Int32.Parse(arg, CultureInfo.InvariantCulture) : DEFAULT_ENERGY;
                        break;

                    case "cooldown":
                        if (arg.Length > 0 && !(Int32.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 3600))
                        { mod.sendMessage(-1, "Cooldown must be a number of seconds from 1 to 3600."); return; }
                        _mode = "cooldown";
                        _cooldownS = arg.Length > 0 ? Int32.Parse(arg, CultureInfo.InvariantCulture) : DEFAULT_COOLDOWN_S;
                        _swaps.Clear();
                        break;

                    case "downtime":
                        arg = arg.ToLowerInvariant();
                        if (arg.Length == 0) { mod.sendMessage(0, "Cooldown between rounds: " + (_downtimeFree ? "NOT enforced (free)" : "enforced") + ". *classgate downtime on|off"); return; }
                        if (arg != "on" && arg != "off") { mod.sendMessage(-1, "Say *classgate downtime on  (free between rounds) or off  (enforce everywhere)."); return; }
                        _downtimeFree = arg == "on";
                        break;

                    case "off":
                        _mode = "off";
                        break;

                    case "reset":
                        if (arg.Length == 0) { _swaps.Clear(); mod.sendMessage(0, "All class change cooldowns cleared."); }
                        else { _swaps.Remove(arg); mod.sendMessage(0, "Class change cooldown cleared for " + arg + "."); }
                        return;

                    case "reload":
                        Load();
                        mod.sendMessage(0, "Reloaded - " + Describe() + ".");
                        return;

                    default:
                        mod.sendMessage(-1, "Unknown option. *classgate energy [N] | cooldown [S] | downtime [on|off] | off | reset [alias] | reload");
                        return;
                }

                Save();
                Console.WriteLine("[ClassGate] " + mod._alias + " set: " + Describe());
                mod.sendMessage(0, "Class change gate is now " + Describe() + ". Saved - it persists across restarts.");
            }
        }
    }
}
