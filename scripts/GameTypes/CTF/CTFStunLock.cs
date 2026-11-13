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
    /// A stunned player cannot enter or exit a vehicle. Getting in or out gives invulnerability frames, which is
    /// how players used to shrug off a Tranq.
    ///
    /// How a stun is detected (data driven, no item names): every projectile in the .itm carries antiEffects
    /// fields. antiEffectsThrust is "this blast locks the target's movement for N ticks" and antiEffectsRadius is
    /// how close the target has to be. Tranq, Stunner, Disruptor, EZ Stun Grenade, Zap Glove, Staff Gas Mine,
    /// Blackhole and friends all have thrust &gt; 0; Haywire / EMP Grenade have only fire / recharge effects and do
    /// not lock movement. When a player's explosion reports one, every ENEMY playing within the radius is marked
    /// stunned for thrust * tickMs. The client does the real stun; the server never hears about it, so this is an
    /// estimate, tuned with *stunlock margin / tick and checked with *stunlock debug on.
    ///
    /// Static: one tracker for the whole zone process, keyed by alias so a disconnect cannot pin a Player.
    /// Settings persist in StunLockConfig.txt (mod command *stunlock).
    /// </summary>
    static class StunLock
    {
        private const string CONFIG_PATH = "scripts/GameTypes/CTF/StunLockConfig.txt";

        private static readonly object _lock = new object();
        private static bool _loaded;
        private static bool _enabled = true;
        private static bool _debug;
        private static int _tickMs = 10;       // one anti-effect tick, in ms (item timings are centiseconds)
        private static int _margin = 8;        // extra pixels on top of antiEffectsRadius
        private static int _minTicks = 50;     // ignore effects shorter than this (BioLabs GP6 has a 20 tick graze)
        private static readonly Dictionary<string, int> _until = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _lastNotice = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Called from Player.Explosion for every explosion a player reports.</summary>
        public static void OnExplosion(Arena arena, Player shooter, ItemInfo.Projectile wep, short posX, short posY)
        {
            if (wep == null || shooter == null || wep.antiEffectsThrust < 1) return;
            EnsureLoaded();
            int radius, ticks, tickMs;
            lock (_lock)
            {
                if (!_enabled || wep.antiEffectsThrust < _minTicks) return;
                radius = wep.antiEffectsRadius + _margin;
                ticks = wep.antiEffectsThrust;
                tickMs = _tickMs;
            }
            if (shooter._team == null) return;

            List<Player> near = arena.getPlayersInRange(posX, posY, radius);
            if (near == null) return;
            foreach (Player p in near)
            {
                if (p == null || p == shooter || p.IsSpectator || p.IsDead || p._team == null) continue;
                if (p._team == shooter._team) continue;   // anti effects are for the other side

                int until = Environment.TickCount + ticks * tickMs;
                lock (_lock)
                {
                    int current;
                    // keep the longer of two overlapping stuns (wrap-safe compare)
                    if (_until.TryGetValue(p._alias, out current) && unchecked(current - until) > 0) continue;
                    _until[p._alias] = until;
                }
                Debug(arena, String.Format("[stun] {0} stunned by {1}'s {2} for {3:F1}s (radius {4}, thrust {5})",
                    p._alias, shooter._alias, wep.name, ticks * tickMs / 1000.0, radius, ticks));
            }
        }

        /// <summary>True while the player is inside a stun window; msLeft is what remains of it.</summary>
        public static bool IsStunned(Player p, out int msLeft)
        {
            msLeft = 0;
            if (p == null) return false;
            EnsureLoaded();
            lock (_lock)
            {
                if (!_enabled) return false;
                int until;
                if (!_until.TryGetValue(p._alias, out until)) return false;
                int left = unchecked(until - Environment.TickCount);
                if (left <= 0) { _until.Remove(p._alias); return false; }
                msLeft = left;
                return true;
            }
        }

        /// <summary>
        /// For Player.EnterVehicle / Player.LeaveVehicle: true when the request must be refused. Tells the player,
        /// at most once every 1.5 seconds so a mashed key does not flood their chat.
        /// </summary>
        public static bool Deny(Player p, string what)
        {
            int msLeft;
            if (!IsStunned(p, out msLeft)) return false;

            int now = Environment.TickCount, last;
            bool say;
            lock (_lock)
            {
                say = !_lastNotice.TryGetValue(p._alias, out last) || unchecked(now - last) > 1500;
                if (say) _lastNotice[p._alias] = now;
            }
            if (say)
                p.sendMessage(-1, String.Format("*You are stunned - you can't {0} a vehicle for {1:F1} more seconds.", what, msLeft / 1000.0));
            Debug(p._arena, String.Format("[stun] denied {0} {1} a vehicle ({2:F1}s left)", p._alias, what, msLeft / 1000.0));
            return true;
        }

        public static void Forget(string alias)
        {
            lock (_lock) { _until.Remove(alias); _lastNotice.Remove(alias); }
        }

        // With *stunlock debug on, staff in the arena see every stun and every refusal, so the radius / duration can be tuned live.
        private static void Debug(Arena arena, string text)
        {
            if (!_debug || arena == null) return;
            foreach (Player m in arena.Players)
                if (m != null && (m.PermissionLevel > 0 || m._permissionTemp > 0))
                    m.sendMessage(0, "&" + text);
        }

        #region Config + mod command

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
            _enabled = true; _debug = false; _tickMs = 10; _margin = 8; _minTicks = 50;
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
                    string val = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    int n;
                    bool isNum = Int32.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                    switch (key)
                    {
                        case "enabled": _enabled = !(val == "0" || val == "off" || val == "false" || val == "no"); break;
                        case "debug": _debug = (val == "1" || val == "on" || val == "true" || val == "yes"); break;
                        case "tick_ms": if (isNum && n >= 1 && n <= 1000) _tickMs = n; break;
                        case "margin": if (isNum && n >= 0 && n <= 200) _margin = n; break;
                        case "min_ticks": if (isNum && n >= 0 && n <= 5000) _minTicks = n; break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[StunLock] Could not read " + CONFIG_PATH + ": " + ex.Message);
            }
        }

        private static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Stun lock: a stunned player cannot enter or exit vehicles. Written by the *stunlock mod command.");
                sb.AppendLine("# tick_ms = milliseconds per anti-effect tick (item data is in centiseconds, so 10); margin = extra pixels on antiEffectsRadius;");
                sb.AppendLine("# min_ticks = ignore effects shorter than this many ticks");
                sb.AppendLine("enabled=" + (_enabled ? "1" : "0"));
                sb.AppendLine("debug=" + (_debug ? "1" : "0"));
                sb.AppendLine("tick_ms=" + _tickMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("margin=" + _margin.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("min_ticks=" + _minTicks.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(CONFIG_PATH, sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine("[StunLock] Could not save " + CONFIG_PATH + ": " + ex.Message);
            }
        }

        private static string Describe()
        {
            return String.Format("{0}, debug {1}, 1 tick = {2}ms, margin {3}px, ignores effects under {4} ticks",
                _enabled ? "ON (stunned players cannot enter or exit vehicles)" : "OFF", _debug ? "on" : "off", _tickMs, _margin, _minTicks);
        }

        /// <summary>
        /// *stunlock                  show the settings
        /// *stunlock on | off         turn the rule on or off
        /// *stunlock debug on|off     staff in the arena see every stun and every refusal
        /// *stunlock tick N           ms per anti-effect tick (10 = item data is centiseconds)
        /// *stunlock margin N         extra pixels added to each weapon's antiEffectsRadius
        /// *stunlock clear [alias]    drop a stun (or all stuns) for testing
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
                        mod.sendMessage(0, "Stun lock - " + Describe() + ".");
                        mod.sendMessage(0, "*stunlock on|off | debug on|off | tick N | margin N | clear [alias]  (saved to StunLockConfig.txt)");
                        return;
                    case "on": _enabled = true; break;
                    case "off": _enabled = false; _until.Clear(); break;
                    case "debug":
                        if (arg != "on" && arg != "off") { mod.sendMessage(-1, "Say *stunlock debug on  or  *stunlock debug off."); return; }
                        _debug = arg == "on";
                        break;
                    case "tick":
                        if (!(Int32.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 1000)) { mod.sendMessage(-1, "Tick must be milliseconds from 1 to 1000 (10 is the default)."); return; }
                        _tickMs = n;
                        break;
                    case "margin":
                        if (!(Int32.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 200)) { mod.sendMessage(-1, "Margin must be pixels from 0 to 200 (8 is the default)."); return; }
                        _margin = n;
                        break;
                    case "clear":
                        if (arg.Length == 0) { _until.Clear(); mod.sendMessage(0, "All stuns cleared."); }
                        else { _until.Remove(arg); mod.sendMessage(0, "Stun cleared for " + arg + "."); }
                        return;
                    default:
                        mod.sendMessage(-1, "Unknown option. *stunlock on|off | debug on|off | tick N | margin N | clear [alias]");
                        return;
                }
                Save();
                mod.sendMessage(0, "Stun lock - " + Describe() + ". Saved.");
            }
        }

        #endregion
    }
}
