using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// Per-player ROLE PREFERENCES for mixes and OvD: an ORDERED list of the builds a player is
    /// willing and able to play, most-preferred first. Captains read this while drafting so a pick
    /// is an informed one rather than a guess, and so the community's real vocabulary ("dinfcaw",
    /// "ohvy", "slbonds") is what everyone speaks.
    ///
    /// THE BUILD LIST IS NOT INVENTED HERE. It is the zone's own build table - the `buildInputs`
    /// list in Script_CTF.init() that already powers ?build / ?b (22 builds as of 2026-08-23, each
    /// carrying its class). This class only validates against it, so adding a build to the zone
    /// automatically makes it a selectable role with no change here.
    ///
    /// A preference therefore carries three things at once, with no separate taxonomy to maintain:
    ///   build  "dinfcaw"        - what the player actually runs
    ///   class  "Infantry"       - from the zone's own build table
    ///   side   defense          - from the build's o*/d* prefix, else the class default
    ///
    /// Ordering is the whole point: prefs[0] is a MAIN, everything after is a SECONDARY in
    /// descending willingness. A captain filling a comp hole reads down the list.
    ///
    /// Storage: scripts/GameTypes/CTF/MixRolePrefs.txt, tab separated `alias<TAB>b1,b2,b3`.
    /// The file doubles as the SEED list - hand-written entries for known players are picked up on
    /// load and are indistinguishable from ones players set themselves, which is what makes
    /// pre-populating regulars possible.
    /// </summary>
    class CTFRolePrefs
    {
        private Arena _arena;
        private Script_CTF _script;

        private static readonly string PREFS_PATH = Path.Combine("scripts", "GameTypes", "CTF", "MixRolePrefs.txt");
        private const int MAX_PREFS = 6;    // beyond this it stops being a preference and becomes a shrug

        // alias -> ordered build names, most preferred first. Alias-keyed: never holds a Player.
        private Dictionary<string, List<string>> _prefs;
        /// <summary>"alias|role" -> preferred build for that player in that role. Optional.</summary>
        private Dictionary<string, string> _buildPrefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A ROLE is a side plus a class - that is the whole definition. It is NOT a build.
        /// "dinf" is the role Defensive Infantry; "dinfcaw" is a BUILD someone playing that role
        /// might run. Related, not the same.
        /// </summary>
        private class RoleDef
        {
            public string Name;      // short name the community uses: oinf, dinf, sl, med ...
            public string Side;      // "offense" | "defense"
            public string Class;     // the game class this role plays
            public string Label;     // human readable, e.g. "Offense Infantry"
            public RoleDef(string name, string side, string cls, string label)
            { Name = name; Side = side; Class = cls; Label = label; }
        }

        /// <summary>
        /// The nine roles. Squad Leader is almost never defence, medic and engineer almost never
        /// offence, and infiltrator / jump trooper are usually offence - so those five exist on one
        /// side only. Infantry and Heavy Weapons genuinely go both ways, which is precisely why the
        /// community splits them into oinf/dinf and ohvy/dhvy.
        /// </summary>
        private static readonly List<RoleDef> ROLES = new List<RoleDef>
        {
            new RoleDef("oinf",  "offense", "Infantry",        "Offense Infantry"),
            new RoleDef("dinf",  "defense", "Infantry",        "Defense Infantry"),
            new RoleDef("ohvy",  "offense", "Heavy Weapons",   "Offense Heavy Weapons"),
            new RoleDef("dhvy",  "defense", "Heavy Weapons",   "Defense Heavy Weapons"),
            new RoleDef("sl",    "offense", "Squad Leader",    "Offense Squad Leader"),
            new RoleDef("infil", "offense", "Infiltrator",     "Offense Infiltrator"),
            new RoleDef("jt",    "offense", "Jump Trooper",    "Offense Jump Trooper"),
            new RoleDef("med",   "defense", "Field Medic",     "Defense Medic"),
            new RoleDef("eng",   "defense", "Combat Engineer", "Defense Engineer"),
        };

        private static RoleDef Role(string name)
        {
            if (String.IsNullOrEmpty(name))
                return null;
            string n = name.Trim();
            foreach (RoleDef r in ROLES)
                if (r.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    return r;
            return null;
        }

        /// <summary>
        /// The role a BUILD belongs to. A build implies a role (it is a loadout for one class, and
        /// its name usually carries the side), so "dinfcaw" -> dinf, "slBR" -> sl, "medstandard" ->
        /// med. Falls back to the class's single-sided role when the name carries no o/d prefix.
        /// </summary>
        private RoleDef RoleForBuild(string build)
        {
            string cls = _script.GetBuildClass(build);
            if (cls == null)
                return null;

            string lower = build.ToLower();
            List<RoleDef> forClass = ROLES.Where(r => r.Class.Equals(cls, StringComparison.OrdinalIgnoreCase)).ToList();
            if (forClass.Count == 0)
                return null;
            if (forClass.Count == 1)
                return forClass[0];   // med / eng / sl / infil / jt - only one side exists

            // Infantry and Heavy Weapons: the build name carries the side.
            if (lower.StartsWith("o")) return forClass.FirstOrDefault(r => r.Side == "offense");
            if (lower.StartsWith("d")) return forClass.FirstOrDefault(r => r.Side == "defense");
            return forClass[0];
        }

        public CTFRolePrefs(Arena arena, Script_CTF script)
        {
            _arena = arena;
            _script = script;
        }

        #region Lookup

        /// <summary>This player's ordered build preferences, or an empty list if they never set any.</summary>
        public List<string> Get(string alias)
        {
            EnsureLoaded();
            List<string> list;
            return _prefs.TryGetValue(alias, out list) ? list : new List<string>();
        }

        public bool Has(string alias)
        {
            return Get(alias).Count > 0;
        }

        /// <summary>Top preference (their "main"), or null.</summary>
        public string Primary(string alias)
        {
            List<string> list = Get(alias);
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>Compact one-line summary for a pool listing, e.g. "dinfcaw &gt; oinf &gt; dhvy".</summary>
        public string Describe(string alias, int max)
        {
            List<string> list = Get(alias);
            if (list.Count == 0)
                return "no prefs set";
            return String.Join(" > ", list.Take(max).ToArray()) + (list.Count > max ? " ..." : "");
        }

        /// <summary>The class a ROLE plays (falls back to treating the name as a build).</summary>
        public string ClassOf(string roleOrBuild)
        {
            RoleDef r = Role(roleOrBuild);
            if (r != null)
                return r.Class;
            return _script.GetBuildClass(roleOrBuild);
        }

        /// <summary>Human-readable role label, e.g. "Defense Infantry".</summary>
        public string LabelOf(string roleName)
        {
            RoleDef r = Role(roleName);
            return r != null ? r.Label : roleName;
        }

        /// <summary>Is this a role name we know?</summary>
        public bool IsRole(string name)
        {
            return Role(name) != null;
        }

        /// <summary>"offense" or "defense" for a role (or for a build, via its role).</summary>
        public string SideOf(string roleOrBuild)
        {
            RoleDef r = Role(roleOrBuild) ?? RoleForBuild(roleOrBuild);
            return r != null ? r.Side : "either";
        }

        #endregion

        #region Command

        /// <summary>
        /// ?role / ?roles. Deliberately usable at ANY time, not only during a mix - a player should
        /// be able to set what they play whenever they think of it, not just when a draft is open.
        /// </summary>
        public bool HandleChatCommand(Player player, string command, string payload)
        {
            if (command != "role" && command != "roles")
                return false;

            EnsureLoaded();
            string arg = (payload ?? "").Trim();

            if (arg.Length == 0)
            {
                ShowMine(player);
                return true;
            }

            string lower = arg.ToLower();

            if (lower == "list" || lower == "builds" || lower == "?")
            {
                ShowCatalogue(player);
                return true;
            }

            if (lower == "clear" || lower == "none" || lower == "off")
            {
                _prefs.Remove(player._alias);
                Save();
                player.sendMessage(0, "Your role preferences are cleared. Captains will see 'no prefs set'.");
                return true;
            }

            // Anything else is an ordered preference list: "dinfcaw, oinf, dhvy" (commas or spaces)
            SetFromInput(player, arg);
            return true;
        }

        private void SetFromInput(Player player, string arg)
        {
            string[] tokens = arg.Split(new char[] { ',', ' ', '>' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> accepted = new List<string>();
            List<string> rejected = new List<string>();

            foreach (string raw in tokens)
            {
                string tok = raw.Trim();
                if (tok.Length == 0)
                    continue;

                // "dinf" (a role) and "dinf:dinfcaw" (role with a preferred build) are both valid,
                // and a bare build name resolves to the role it belongs to.
                string rolePart = tok, buildPart = null;
                int colon = tok.IndexOf(':');
                if (colon > 0)
                {
                    rolePart = tok.Substring(0, colon).Trim();
                    buildPart = tok.Substring(colon + 1).Trim();
                }

                string canonical = null;
                RoleDef rd = Role(rolePart);
                if (rd != null)
                {
                    canonical = rd.Name;
                }
                else
                {
                    // Not a role - maybe they named a build. Map it back to its role.
                    string b = _script.ResolveBuildName(rolePart);
                    RoleDef viaBuild = b == null ? null : RoleForBuild(b);
                    if (viaBuild != null) { canonical = viaBuild.Name; buildPart = buildPart ?? b; }
                }

                if (canonical == null)
                {
                    rejected.Add(tok);
                    continue;
                }

                if (buildPart != null)
                {
                    string vb = _script.ResolveBuildName(buildPart);
                    if (vb != null)
                        _buildPrefs[player._alias + "|" + canonical] = vb;
                }
                // Preserve first-mention order and ignore repeats - the order IS the preference
                if (!accepted.Any(a => a.Equals(canonical, StringComparison.OrdinalIgnoreCase)))
                    accepted.Add(canonical);
                if (accepted.Count >= MAX_PREFS)
                    break;
            }

            if (accepted.Count == 0)
            {
                player.sendMessage(-1, String.Format("No builds recognised in '{0}'. Try ?role list to see them all.", arg));
                return;
            }

            _prefs[player._alias] = accepted;
            Save();

            player.sendMessage(0, String.Format("Your roles: {0}", DescribeWithClass(accepted)));
            player.sendMessage(0, String.Format("~Main: {0}. {1}",
                accepted[0],
                accepted.Count > 1
                    ? "Secondary: " + String.Join(", ", accepted.Skip(1).ToArray())
                    : "Add more with ?role dinfcaw, oinf, dhvy - order matters."));

            if (rejected.Count > 0)
                player.sendMessage(-1, String.Format("Not recognised (ignored): {0}. ?role list shows valid names.",
                    String.Join(", ", rejected.ToArray())));
        }

        private void ShowMine(Player player)
        {
            List<string> mine = Get(player._alias);
            if (mine.Count == 0)
            {
                player.sendMessage(0, "You have no role preferences set - captains cannot see what you play.");
                player.sendMessage(0, "~Set them in order, best first:  ?role dinfcaw, oinf, dhvy");
                player.sendMessage(0, "~?role list  shows every build in the zone, grouped by class.");
                return;
            }

            player.sendMessage(0, String.Format("Your roles (in order): {0}", DescribeWithClass(mine)));
            player.sendMessage(0, String.Format("~Main: {0}{1}", mine[0],
                mine.Count > 1 ? " | Secondary: " + String.Join(", ", mine.Skip(1).ToArray()) : ""));
            player.sendMessage(0, "~?role <list in order> to change, ?role clear to remove, ?role list for all builds.");
        }

        private string _lastDescribeAlias = "";
        private string DescribeWithClass(List<string> builds)
        {
            List<string> parts = new List<string>();
            foreach (string r in builds)
            {
                RoleDef rd = Role(r);
                string build;
                bool hasBuild = _buildPrefs.TryGetValue(_lastDescribeAlias + "|" + r, out build);
                parts.Add(rd == null ? r
                    : hasBuild ? String.Format("{0} [{1}]", rd.Label, build)
                    : rd.Label);
            }
            return String.Join(" > ", parts.ToArray());
        }

        /// <summary>Class name shortened for inline display - full names make the lines wrap.</summary>
        public static string ShortClass(string cls)
        {
            if (String.IsNullOrEmpty(cls))
                return "?";
            switch (cls.ToLower())
            {
                case "infantry":        return "Inf";
                case "heavy weapons":   return "HW";
                case "squad leader":    return "SL";
                case "field medic":     return "Med";
                case "combat engineer": return "Eng";
                case "jump trooper":    return "JT";
                case "infiltrator":     return "Infil";
                default:                return cls;
            }
        }

        private void ShowCatalogue(Player player)
        {
            player.sendMessage(0, "ROLES - a role is a SIDE plus a CLASS. List them best first:");
            player.sendMessage(0, "~Offense: " + String.Join(", ",
                ROLES.Where(r => r.Side == "offense").Select(r => r.Name).ToArray()));
            player.sendMessage(0, "Defense: " + String.Join(", ",
                ROLES.Where(r => r.Side == "defense").Select(r => r.Name).ToArray()));
            player.sendMessage(0, "&Example:  ?role dinf, oinf, dhvy      (main first)");
            player.sendMessage(0, "~A BUILD is a loadout for a class, not a role. Attach one with a colon:");
            player.sendMessage(0, "&          ?role dinf:dinfcaw, ohvy");

            var byClass = _script.AllBuildsByClass();
            if (byClass != null && byClass.Count > 0)
            {
                player.sendMessage(0, "Builds available (optional):");
                foreach (var kv in byClass.OrderBy(k => k.Key))
                    player.sendMessage(0, String.Format("~  {0}: {1}", kv.Key, String.Join(", ", kv.Value.ToArray())));
            }
        }

        #endregion

        #region Pool display helpers

        /// <summary>
        /// The roster block captains actually plan a comp from: one player per line with their
        /// ordered preferences. Kept separate from the compact ?l grid on purpose - ?l is for
        /// picking fast by number, this is for deciding WHO to pick.
        /// </summary>
        public void SendPoolRoles(Player viewer, List<Player> pool, Dictionary<string, int> poolNumbers)
        {
            if (pool == null || pool.Count == 0)
            {
                viewer.sendMessage(0, "Pool is empty.");
                return;
            }

            viewer.sendMessage(0, String.Format("Pool roles ({0}) - main first:", pool.Count));

            // Players who told us what they play come first; unknowns sink to the bottom where they
            // read as exactly what they are - a gamble for the captain.
            List<Player> known = pool.Where(p => Has(p._alias)).ToList();
            List<Player> unknown = pool.Where(p => !Has(p._alias)).ToList();

            int width = pool.Max(p => (PoolNum(poolNumbers, p) + "." + p._alias).Length);
            int row = 0;
            foreach (Player p in known)
            {
                string label = (PoolNum(poolNumbers, p) + "." + p._alias).PadRight(width);
                viewer.sendMessage(0, String.Format("{0}{1}  {2}",
                    (row++ % 2 == 1) ? "~" : "", label, Describe(p._alias, 4)));
            }

            if (unknown.Count > 0)
            {
                viewer.sendMessage(0, String.Format("!No prefs set ({0}): {1}",
                    unknown.Count,
                    String.Join(", ", unknown.Select(p => PoolNum(poolNumbers, p) + "." + p._alias).ToArray())));
            }
        }

        private static string PoolNum(Dictionary<string, int> nums, Player p)
        {
            int n;
            return (nums != null && nums.TryGetValue(p._alias, out n)) ? n.ToString() : "-";
        }

        /// <summary>
        /// What a drafted team currently looks like, by class, so a captain can see the hole they
        /// still need to fill. Advisory only - nothing here enforces a composition.
        /// </summary>
        public string SummariseTeam(IEnumerable<Player> members)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int unknown = 0;
            foreach (Player p in members)
            {
                string prim = Primary(p._alias);
                string cls = prim == null ? null : ClassOf(prim);
                if (cls == null) { unknown++; continue; }
                string key = ShortClass(cls);
                counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
            }

            List<string> parts = counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key)
                .Select(k => String.Format("{0}x{1}", k.Value, k.Key)).ToList();
            if (unknown > 0)
                parts.Add(unknown + "x?");
            return parts.Count == 0 ? "(empty)" : String.Join(" ", parts.ToArray());
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Loads MixRolePrefs.txt. `alias&lt;TAB&gt;build1,build2,...`, `#` comments, blank lines fine.
        /// Unknown build names are dropped on load rather than rejecting the whole line, so a build
        /// renamed or retired in the zone config degrades one entry instead of a player's whole set.
        /// </summary>
        private void EnsureLoaded()
        {
            if (_prefs != null)
                return;
            _prefs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(PREFS_PATH))
                    return;

                foreach (string raw in File.ReadAllLines(PREFS_PATH))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 2)
                    {
                        // tolerate "alias = a,b,c" as a hand-written seed form
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                            continue;
                        parts = new string[] { line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim() };
                    }

                    string alias = parts[0].Trim();
                    if (alias.Length == 0)
                        continue;

                    List<string> builds = new List<string>();
                    foreach (string tok in parts[1].Split(new char[] { ',', ' ', '>' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        // Accept a ROLE code (oinf, dhvy, sl ...) as well as a build name.
                        //
                        // ResolveBuildName is an exact lookup against the zone's 22 builds, so a role
                        // code was silently dropped on load. That matters for preferences inferred
                        // from history: the data tells us a player's CLASS and SIDE - i.e. a role -
                        // never which specific build they ran. Squad Leader was the worst case, since
                        // the only SL builds are player-specific (slbonds/slbr/slherth) and there is
                        // no generic one to fall back to, so 8% of all games were unusable.
                        // Everything downstream already treats the two interchangeably: Role(x) ??
                        // RoleForBuild(x).
                        string prefTok = tok.Trim();
                        string canonical = _script.ResolveBuildName(prefTok);
                        if (canonical == null)
                        {
                            RoleDef asRole = Role(prefTok);
                            if (asRole != null)
                                canonical = asRole.Name;
                        }
                        if (canonical != null && !builds.Any(b => b.Equals(canonical, StringComparison.OrdinalIgnoreCase)))
                            builds.Add(canonical);
                        if (builds.Count >= MAX_PREFS)
                            break;
                    }
                    if (builds.Count > 0)
                        _prefs[alias] = builds;
                }
            }
            catch { /* unreadable prefs = everyone starts blank */ }
        }

        private void Save()
        {
            try
            {
                // The header is rewritten in full every save. This file is meant to be hand-edited
                // (that is how regulars get seeded), so it has to stay self-documenting rather than
                // decaying into a bare list the first time a player sets their own roles.
                List<string> lines = new List<string>();
                lines.Add("# CTF mix / OvD role preferences - ORDERED, most preferred FIRST.");
                lines.Add("#");
                lines.Add("# Format:   alias<TAB>build1,build2,build3");
                lines.Add("# Also accepted (easier to hand-write):   alias = build1, build2, build3");
                lines.Add("# Comments start with #. Blank lines are fine.");
                lines.Add("#");
                lines.Add("# The FIRST build is the player's MAIN; the rest are SECONDARIES in descending");
                lines.Add("# willingness. Captains read down that list when filling a hole, so order matters.");
                lines.Add("#");
                lines.Add("# In game:  ?role list                  - every build, grouped by class");
                lines.Add("#           ?role dinfcaw, oinf, dhvy   - set your own, main first");
                lines.Add("#           ?role clear                 - remove yours");
                lines.Add("#           ?lr                         - the whole pool's roles during a draft");
                lines.Add("#");
                lines.Add("# Valid names are the zone's OWN builds (same list ?build uses). Unknown names are");
                lines.Add("# dropped on load rather than voiding the line. This file is re-read at the start of");
                lines.Add("# every mix, so edits take effect without a zone restart.");
                lines.Add("#");
                lines.Add("# Builds currently loaded in this zone:");
                try
                {
                    var cat = _script.AllBuildsByClass();
                    foreach (var kv in cat.OrderBy(k => k.Key))
                        lines.Add(String.Format("#   {0,-16} {1}", kv.Key, String.Join(", ", kv.Value.ToArray())));
                }
                catch { /* catalogue is a nicety, never worth losing the data over */ }
                lines.Add("#");
                foreach (var kv in _prefs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    lines.Add(kv.Key + "\t" + String.Join(",", kv.Value.ToArray()));

                File.WriteAllLines(PREFS_PATH, lines.ToArray());
            }
            catch { /* a read-only disk must not break a mix */ }
        }

        /// <summary>Re-read from disk, so seed entries edited live take effect without a restart.</summary>
        public void Reload()
        {
            _prefs = null;
            EnsureLoaded();
        }

        public int Count
        {
            get { EnsureLoaded(); return _prefs.Count; }
        }

        #endregion
    }
}
