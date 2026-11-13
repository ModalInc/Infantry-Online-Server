// CTF:X bot shopping and item usage (Travis 10-13 overnight runs: "the full meta itemset across every class, noting underused items
// each round and why they do not get purchased").
//
// SHOP: CTFXBuilds.txt holds the zone builds (copied from CTF.cs buildInputs). Each build's ?buy string is expanded the zone way
//   (an item name, or a macro MultiItem whose slots are item ids, a repeat = one more), into a bot kit: the weapons in it (a
//   hand-tuned range profile when another kit already uses the item, else one worked out from the projectile's speed x life),
//   at most 4, and the ammo counts it buys. What a bot cannot use is written down with the reason (utility item, mine, a 5th
//   weapon...). shop=<share> in CTFXBots.txt: that share of lives of the classes with builds roll one of their class's builds.
// USAGE: per item lives carried, shots, hits that took health, kills, refusals (energy, terrain, dry); per build, class and style:
//   lives, kills, deaths, time alive. Written with the summary line to logs/ctfxround.json each minute (the overnight harness).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal static class CTFXShop
    {
        internal const int Base = 100;                                      // shop kit ids start here (CTFXBot.KitAt)

        internal sealed class Build
        {
            public string Name, ClassName;
            public int Cls;
            public CTFXWep[] Kit;
            public readonly Dictionary<int, int> Ammo = new Dictionary<int, int>();
            public readonly List<string> Unused = new List<string>();      // "item: why the bot does not use it"
            // MiniTP (Travis 11-08: "armour/gen/cell from the build", mines): the suit, generator, energy item and cloak the ?buy string names (0 = none), and its mines (item id -> how many it buys)
            public int ArmorId, GenId, CellId, CloakId;
            public readonly List<int> Mines = new List<int>();
        }

        static readonly List<Build> _builds = new List<Build>();
        static bool _loaded;
        internal static string LoadNote = "shop: not loaded";

        static int ClassIdOf(string c)
        {
            switch (c.Trim().ToLowerInvariant())
            {
                case "heavy weapons": return CTFXBot.SK_HEAVY;
                case "infantry": return CTFXBot.SK_INF;
                case "squad leader": return CTFXBot.SK_SL;
                case "field medic": return CTFXBot.SK_MEDIC;
                case "jump trooper": return CTFXBot.SK_JT;
                case "combat engineer": return CTFXBot.SK_ENGY;
                case "infiltrator": return CTFXBot.SK_INFIL;
                case "sciops": case "science ops": return CTFXBot.SK_SCIOPS;
                case "sergeant": return CTFXBot.SK_SGT;
                case "captain": return CTFXBot.SK_CAPT;
                case "biochem": return CTFXBot.SK_BIO;
            }
            return 0;
        }

        internal static void Load(string path, Func<int, CTFXWep> known)
        {
            lock (_builds)
            {
                if (_loaded) return;
                _loaded = true;
                if (!File.Exists(path)) { LoadNote = "shop: no " + path; return; }
                foreach (string raw in File.ReadAllLines(path))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l.StartsWith("//")) continue;
                    string[] f = l.Split('|');
                    if (f.Length < 3) continue;
                    Build b = new Build();
                    b.Name = f[0].Trim(); b.ClassName = f[1].Trim(); b.Cls = ClassIdOf(b.ClassName);
                    if (b.Cls == 0) continue;
                    List<CTFXWep> sure = new List<CTFXWep>(), auto = new List<CTFXWep>();
                    HashSet<int> seen = new HashSet<int>();
                    string buy = f[2].Trim();
                    if (buy.StartsWith("?buy")) buy = buy.Substring(4);
                    foreach (string part in buy.Split(','))
                    {
                        string nm = part.Trim();
                        int cnt = 1, c = nm.IndexOf(':');
                        if (c >= 0) { if (!int.TryParse(nm.Substring(c + 1).Trim().TrimStart('#'), out cnt)) cnt = 1; nm = nm.Substring(0, c).Trim(); }
                        if (nm.Length == 0) continue;
                        ItemInfo it = AssetManager.Manager.getItemByName(nm);
                        if (it == null) { b.Unused.Add(nm + ": no item of that name"); continue; }
                        Add(b, it, Math.Max(1, cnt), sure, auto, seen, known, 0);
                    }
                    List<CTFXWep> all = new List<CTFXWep>(sure);
                    all.AddRange(auto);
                    for (int i = 4; i < all.Count; i++) b.Unused.Add(Name(all[i].Id) + ": a 5th weapon (a bot kit holds 4)");
                    List<CTFXWep> kit = all.Count > 4 ? all.GetRange(0, 4) : all;
                    // the mines the build buys ride at the END of the kit (at most 2 kinds): the weapon pick skips them (CTFXBot.IsMine), only the MiniTP mine rule lays them (CTFXMini.MineStep)
                    if (kit.Count > 0) for (int mi = 0; mi < b.Mines.Count && mi < 2; mi++) kit.Add(new CTFXWep(b.Mines[mi], 0, 0, 0, 0, false));
                    b.Kit = kit.ToArray();
                    if (b.Kit.Length == 0) { b.Unused.Add("(whole build): no weapon a bot can fire"); }
                    _builds.Add(b);
                }
                StringBuilder sb = new StringBuilder("shop: " + _builds.Count + " builds:");
                foreach (Build b in _builds)
                {
                    sb.Append(' ').Append(b.Name).Append('[');
                    foreach (CTFXWep w in b.Kit) sb.Append(Name(w.Id)).Append('/');
                    sb.Append(']');
                }
                LoadNote = sb.ToString();
            }
        }

        static string Name(int id)
        {
            ItemInfo it = AssetManager.Manager.getItemByID(id);
            return it != null ? it.name : "#" + id;
        }

        static void Add(Build b, ItemInfo it, int cnt, List<CTFXWep> sure, List<CTFXWep> auto, HashSet<int> seen, Func<int, CTFXWep> known, int depth)
        {
            if (depth > 4) return;
            ItemInfo.MultiItem mi = it as ItemInfo.MultiItem;
            if (mi != null)
            {
                foreach (ItemInfo.MultiItem.Slot s in mi.slots)
                {
                    if (s.value <= 0) continue;
                    ItemInfo ch = AssetManager.Manager.getItemByID(s.value);
                    if (ch != null) Add(b, ch, cnt, sure, auto, seen, known, depth + 1);
                }
                return;
            }
            if (it is ItemInfo.Ammo) { int have; b.Ammo.TryGetValue(it.id, out have); b.Ammo[it.id] = have + cnt; return; }
            ItemInfo.UtilityItem ut = it as ItemInfo.UtilityItem;
            if (ut != null && CTFXSkillLogic.Allowed(it.id, b.Cls))
            {   // the slots (ctfpl.itm maxSlottedCategoryCode): 10 armour (the cloaks too), 20 generator, 30 energy; the first one of each the build names is the one worn
                if (ut.cloakDistance > 0) { if (b.CloakId == 0) b.CloakId = it.id; }
                else if (ut.maxSlottedCategoryCode == 10) { if (b.ArmorId == 0) b.ArmorId = it.id; }
                else if (ut.maxSlottedCategoryCode == 20) { if (b.GenId == 0) b.GenId = it.id; }
                else if (ut.maxSlottedCategoryCode == 30) { if (b.CellId == 0) b.CellId = it.id; }
            }
            if (CTFXBot.IsMine(it.id) && it.id != 1062 && CTFXSkillLogic.Allowed(it.id, b.Cls))
            {   // a mine: kept for the MiniTP mine rule (its count is the stock it carries); the weapon list never holds it
                if (!b.Mines.Contains(it.id)) b.Mines.Add(it.id);
                int hv; b.Ammo.TryGetValue(it.id, out hv); b.Ammo[it.id] = hv + cnt;
            }
            if (!seen.Add(it.id)) return;
            CTFXWep w = known(it.id);
            if (w == null) w = AutoW(it);
            if (w != null && !CTFXSkillLogic.Allowed(it.id, b.Cls)) return;      // a macro alternative for another class (eb = every beamer)
            if (w != null && w.Max == 0) { b.Unused.Add(it.name + ": mine (only the trap engineer lays mines)"); return; }
            if (w != null && it.id == 1049) { b.Unused.Add(it.name + ": kept as the panic tool (every kit gets one)"); return; }
            if (w != null) { (it.name.IndexOf("Grenade", StringComparison.OrdinalIgnoreCase) >= 0 ? auto : sure).Add(w); return; }   // guns first, grenades last
            b.Unused.Add(it.name + ": " + Why(it));
        }

        /// <summary>A range profile from the projectile itself: reach = speed (px per 10 ms tick) x life; prefers half of it.</summary>
        internal static CTFXWep AutoW(ItemInfo it)
        {
            ItemInfo.Projectile p = it as ItemInfo.Projectile;
            if (p == null) return null;
            double reach = p.muzzleVelocity / 1000.0 * Math.Abs(p.aliveTime);
            if (reach < 40.0) return null;
            int max = (int)Math.Min(900.0, reach * 0.9);
            return new CTFXWep(it.id, 0, 9999, Math.Max(30, max / 2), max, false);
        }

        /// <summary>The wall-passing reach of a weapon (bounceCount -1: the shot flies through walls, CTFXShots), 0 = none.</summary>
        internal static double WallReach(int id)
        {
            ItemInfo.Projectile p = AssetManager.Manager.getItemByID(id) as ItemInfo.Projectile;
            if (p == null || p.bounceCount != -1) return 0.0;
            return Math.Min(900.0, p.muzzleVelocity / 1000.0 * Math.Abs(p.aliveTime) * 0.9);
        }

        static string Why(ItemInfo it)
        {
            if (it is ItemInfo.UtilityItem) return "utility (armour is baked into the bot body; shields, cells, cloaks, generators have no bot logic)";
            if (it is ItemInfo.RepairItem) return "repair item: bots only use the medikits and stims of their own heal rules";
            if (it is ItemInfo.WarpItem) return "warp item: bots warp by their own squad rules, not from a build";
            if (it is ItemInfo.MultiUse) return "multi-shot item with no bot range profile yet";
            ItemInfo.Projectile p = it as ItemInfo.Projectile;
            if (p != null) return "mine or point-blank projectile (reach under 40 px): only the trap engineer lays mines";
            return it.GetType().Name + ": no bot logic";
        }

        internal static Build Roll(int cls, Random rng)
        {
            List<Build> l = new List<Build>();
            foreach (Build b in _builds) if (b.Cls == cls && b.Kit.Length > 0) l.Add(b);
            return l.Count == 0 ? null : l[rng.Next(l.Count)];
        }

        internal static int KitIdOf(Build b) { return Base + _builds.IndexOf(b); }

        /// <summary>A build by its CTFXBuilds.txt name (MiniTP seats name theirs), or null.</summary>
        internal static Build ByName(string name)
        {
            lock (_builds)
            {
                foreach (Build b in _builds) if (b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return b;
            }
            return null;
        }

        internal static Build Of(int kitId)
        {
            int i = kitId - Base;
            return i >= 0 && i < _builds.Count ? _builds[i] : null;
        }

        internal static IEnumerable<Build> All { get { return _builds; } }
    }

    internal sealed class CTFXUsage
    {
        sealed class It { public int Lives, Shots, Hits, Kills, Energy, Terrain, Dry; }
        sealed class Grp { public int Lives, Kills, Deaths; public long LifeMs; }

        readonly Dictionary<int, It> _items = new Dictionary<int, It>();
        readonly Dictionary<string, Grp> _kits = new Dictionary<string, Grp>(), _classes = new Dictionary<string, Grp>(), _styles = new Dictionary<string, Grp>();
        internal int HumanKills;

        It I(int id) { It x; if (!_items.TryGetValue(id, out x)) { x = new It(); _items[id] = x; } return x; }
        static Grp G(Dictionary<string, Grp> d, string k) { Grp x; if (!d.TryGetValue(k, out x)) { x = new Grp(); d[k] = x; } return x; }

        internal void Life(CTFXBot b, string kit, CTFXWep[] weps)
        {
            G(_kits, kit).Lives++;
            G(_classes, CTFXBot.ClassName(b.ClassId)).Lives++;
            G(_styles, b.Style.ToString()).Lives++;
            if (weps != null) foreach (CTFXWep w in weps) I(w.Id).Lives++;
        }

        internal void Shot(int id) { I(id).Shots++; }
        internal void Hit(int id) { if (id != 0) I(id).Hits++; }
        internal void Refuse(int id, int why) { It x = I(id); if (why == 0) x.Energy++; else if (why == 1) x.Terrain++; else x.Dry++; }

        internal void Death(CTFXBot v, CTFXBot k, int item, int lifeMs, bool human)
        {
            Grp g = G(_kits, v.KitName ?? "?"); g.Deaths++; g.LifeMs += lifeMs;
            g = G(_classes, CTFXBot.ClassName(v.ClassId)); g.Deaths++; g.LifeMs += lifeMs;
            g = G(_styles, v.Style.ToString()); g.Deaths++; g.LifeMs += lifeMs;
            if (human) { HumanKills++; return; }
            if (k == null) return;
            if (item != 0) I(item).Kills++;
            G(_kits, k.KitName ?? "?").Kills++;
            G(_classes, CTFXBot.ClassName(k.ClassId)).Kills++;
            G(_styles, k.Style.ToString()).Kills++;
        }

        static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        static void Groups(StringBuilder sb, string key, Dictionary<string, Grp> d)
        {
            sb.Append(Q(key)).Append(":{");
            bool first = true;
            foreach (KeyValuePair<string, Grp> kv in d)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Q(kv.Key)).Append(":{\"lives\":").Append(kv.Value.Lives).Append(",\"kills\":").Append(kv.Value.Kills)
                  .Append(",\"deaths\":").Append(kv.Value.Deaths).Append(",\"lifeMs\":").Append(kv.Value.LifeMs).Append('}');
            }
            sb.Append('}');
        }

        internal string Json(string summary, string extra)
        {
            StringBuilder sb = new StringBuilder("{");
            sb.Append("\"at\":").Append(Q(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))).Append(",\"humanKills\":").Append(HumanKills).Append(",\"items\":{");
            bool first = true;
            foreach (KeyValuePair<int, It> kv in _items)
            {
                if (!first) sb.Append(',');
                first = false;
                ItemInfo it = AssetManager.Manager.getItemByID(kv.Key);
                It x = kv.Value;
                sb.Append(Q(kv.Key.ToString())).Append(":{\"name\":").Append(Q(it != null ? it.name : "?")).Append(",\"lives\":").Append(x.Lives)
                  .Append(",\"shots\":").Append(x.Shots).Append(",\"hits\":").Append(x.Hits).Append(",\"kills\":").Append(x.Kills)
                  .Append(",\"energy\":").Append(x.Energy).Append(",\"terrain\":").Append(x.Terrain).Append(",\"dry\":").Append(x.Dry).Append('}');
            }
            sb.Append("},");
            Groups(sb, "kits", _kits); sb.Append(',');
            Groups(sb, "classes", _classes); sb.Append(',');
            Groups(sb, "styles", _styles); sb.Append(',');
            sb.Append("\"builds\":[");
            first = true;
            foreach (CTFXShop.Build b in CTFXShop.All)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(Q(b.Name)).Append(",\"class\":").Append(Q(b.ClassName)).Append(",\"kit\":[");
                for (int i = 0; i < b.Kit.Length; i++) { if (i > 0) sb.Append(','); sb.Append(b.Kit[i].Id); }
                sb.Append("],\"unused\":[");
                for (int i = 0; i < b.Unused.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Q(b.Unused[i])); }
                sb.Append("]}");
            }
            sb.Append("],\"extra\":").Append(Q(extra)).Append(",\"summary\":").Append(Q(summary)).Append('}');
            return sb.ToString();
        }
    }
}
