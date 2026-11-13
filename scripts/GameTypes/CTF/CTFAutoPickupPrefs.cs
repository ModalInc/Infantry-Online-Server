using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// Remembered ?ap lists (Travis, 2026-10-26: players asked "Do you need to turn that on every time you log in?"). A list a player sets with
    /// ?ap item:qty, or with ?ap alone while they have no build (what they carry), is kept per alias AND class: it comes back when they rejoin
    /// or change back to that class. ?ap off forgets the list of the class they are. Static, so every arena shares it; AutoPickupPrefs.txt is
    /// written by the zone (never push it from local). Line: alias TAB class TAB item:qty, item:qty [TAB carry]. "carry" = a snapshot of what
    /// they carried, which (unlike a typed list) still respects their deliberate drops.
    /// </summary>
    public static class AutoPickupPrefs
    {
        private static readonly string PREFS_PATH = Path.Combine("scripts", "GameTypes", "CTF", "AutoPickupPrefs.txt");
        private static readonly object _lock = new object();
        private static Dictionary<string, Dictionary<string, int>> _lists;   // "alias|class" -> item -> quantity
        private static HashSet<string> _carry;                              // "alias|class" saved from ?ap alone (what they carried)

        private static string Key(string alias, string cls) { return (alias ?? "") + "|" + (cls ?? ""); }

        /// <summary>a copy of the remembered list, or null</summary>
        public static Dictionary<string, int> Get(string alias, string cls, out bool carry)
        {
            lock (_lock)
            {
                LoadPrefs();
                Dictionary<string, int> list;
                carry = _carry.Contains(Key(alias, cls));
                return _lists.TryGetValue(Key(alias, cls), out list) ? new Dictionary<string, int>(list, StringComparer.OrdinalIgnoreCase) : null;
            }
        }

        public static void Set(string alias, string cls, Dictionary<string, int> list, bool carry)
        {
            if (string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(cls) || list == null || list.Count == 0)
                return;
            lock (_lock)
            {
                LoadPrefs();
                _lists[Key(alias, cls)] = new Dictionary<string, int>(list, StringComparer.OrdinalIgnoreCase);
                if (carry) _carry.Add(Key(alias, cls)); else _carry.Remove(Key(alias, cls));
                SavePrefs();
            }
        }

        /// <summary>true when there was a list to forget</summary>
        public static bool Forget(string alias, string cls)
        {
            lock (_lock)
            {
                LoadPrefs();
                _carry.Remove(Key(alias, cls));
                if (!_lists.Remove(Key(alias, cls)))
                    return false;
                SavePrefs();
                return true;
            }
        }

        private static void LoadPrefs()
        {
            if (_lists != null) return;
            _lists = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            _carry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(PREFS_PATH)) return;
                foreach (string raw in File.ReadAllLines(PREFS_PATH))
                {
                    if (raw.StartsWith("#")) continue;
                    string[] f = raw.Split('\t');
                    if (f.Length < 3) continue;
                    var list = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (string part in f[2].Split(','))
                    {
                        int colon = part.LastIndexOf(':'), q;
                        if (colon <= 0 || !int.TryParse(part.Substring(colon + 1).Trim(), out q) || q <= 0) continue;
                        list[part.Substring(0, colon).Trim()] = q;
                    }
                    if (list.Count == 0) continue;
                    string key = Key(f[0].Trim(), f[1].Trim());
                    _lists[key] = list;
                    if (f.Length > 3 && f[3].Trim().Equals("carry", StringComparison.OrdinalIgnoreCase)) _carry.Add(key);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("AutoPickupPrefs.txt not read: " + ex.Message);
            }
        }

        private static void SavePrefs()
        {
            try
            {
                var lines = new List<string> { "# alias<TAB>class<TAB>item:qty, ...[<TAB>carry] - remembered ?ap lists, written by the zone. Do not push from local." };
                foreach (var kv in _lists.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    int bar = kv.Key.LastIndexOf('|');
                    lines.Add(kv.Key.Substring(0, bar) + "\t" + kv.Key.Substring(bar + 1) + "\t" + string.Join(", ", kv.Value.Select(i => i.Key + ":" + i.Value))
                        + (_carry.Contains(kv.Key) ? "\tcarry" : ""));
                }
                File.WriteAllLines(PREFS_PATH, lines);
            }
            catch (Exception ex)
            {
                Console.WriteLine("AutoPickupPrefs.txt not written: " + ex.Message);
            }
        }
    }
}
