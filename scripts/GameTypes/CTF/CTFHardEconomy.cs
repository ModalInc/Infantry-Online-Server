// Twin Peaks HARD (Travis 11-07): the grind economy for the new TP zone on its own zone id. Switched on by HardEconomy.txt (enabled=1) in this folder; no file = every
// call below returns "as before", so TP 2.0 itself (no file) is untouched.
//   * a build bought with ?b / ?bw / ?bwd / autobuy / the armory / ?v packs is CHARGED item by item at the item's buyPrice, and an item the player's skill logic does not allow is refused
//     (the engine's F10 store already does both; the script's build handler used to grant items straight into the inventory, which only worked because every player had 999,999,999 cash)
//   * a class change needs the experience its rpg row names ("@100" = player.Experience >= 100, InfServer/Logic/General/Assets.Skills.cs); arena mods are exempt
// One summary line per build: what was spent and what could not be bought.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Assets;
using InfServer.Game;
using InfServer.Logic;

namespace InfServer.Script.GameType_CTF
{
    static class HardEconomy
    {
        private const string CONFIG_PATH = "scripts/GameTypes/CTF/HardEconomy.txt";
        private static bool _loaded, _on, _debug;
        private static readonly object _lock = new object();

        private sealed class Tally
        {
            public int Spent;
            public readonly List<Lack> Short = new List<Lack>();        // one record per item that could not be (fully) bought
            public readonly List<string> Locked = new List<string>();
        }
        private sealed class Lack
        {
            public string Name;
            public int Price;
            public int Missing;
        }
        private static readonly Dictionary<Player, Tally> _tally = new Dictionary<Player, Tally>();

        // ?bw / ?bwd / a persistent build at respawn wipe the inventory FIRST and buy the build after: what the player held at the wipe is not
        // paid for twice (a second ?bw of the same build costs nothing; a respawn only re-buys what the life used up).
        private sealed class Credit
        {
            public readonly Dictionary<int, int> Held = new Dictionary<int, int>();
            public DateTime Until;
        }
        private static readonly Dictionary<Player, Credit> _credit = new Dictionary<Player, Credit>();

        /// <summary>True when HardEconomy.txt exists with enabled=1 (read once).</summary>
        internal static bool On
        {
            get
            {
                if (!_loaded)
                {
                    lock (_lock)
                    {
                        if (!_loaded)
                        {
                            try
                            {
                                if (File.Exists(CONFIG_PATH))
                                    foreach (string raw in File.ReadAllLines(CONFIG_PATH))
                                    {
                                        string l = raw;
                                        int c = l.IndexOf("//", StringComparison.Ordinal);
                                        if (c >= 0) l = l.Substring(0, c);
                                        l = l.Trim();
                                        if (l.StartsWith("enabled", StringComparison.OrdinalIgnoreCase) && l.EndsWith("1")) _on = true;
                                        if (l.StartsWith("debug", StringComparison.OrdinalIgnoreCase) && l.EndsWith("1")) _debug = true;
                                        ReadRewardKey(l);
                                    }
                            }
                            catch (Exception) { _on = false; }
                            _loaded = true;
                            if (_on) Console.WriteLine("[hardeconomy] ON: builds cost cash and skill logic, classes need experience (HardEconomy.txt)");
                        }
                    }
                }
                return _on;
            }
        }

        // ---------------------------------------------------------------- classes
        private static readonly Regex NeedExp = new Regex(@"@(\d+)", RegexOptions.Compiled);

        /// <summary>The message that blocks a change to this class (not enough experience), or null.</summary>
        internal static string ClassLocked(Player p, string skillName)
        {
            if (!On || p == null || String.IsNullOrEmpty(skillName)) return null;
            try
            {
                if (p.PermissionLevelLocal >= InfServer.Data.PlayerPermission.ArenaMod) return null;     // mods set classes for events and tests
                SkillInfo sk = AssetManager.Manager.getSkillByName(skillName);
                if (sk == null || String.IsNullOrWhiteSpace(sk.Logic)) return null;
                bool ok = Logic_Assets.SkillCheck(p, sk.Logic);
                if (_debug) Console.WriteLine("[hardeconomy] {0}: class {1} logic '{2}' exp {3} -> {4}", p._alias, sk.Name, sk.Logic, p.Experience, ok ? "open" : "locked");
                if (ok) return null;
                Match m = NeedExp.Match(sk.Logic);
                string need = m.Success ? m.Groups[1].Value : "more";
                return String.Format("*{0} unlocks at {1} experience (you have {2}). Every kill earns experience.", sk.Name, need, p.Experience);
            }
            catch (Exception e) { Console.WriteLine("[hardeconomy] ClassLocked failed for {0} / {1}: {2}", p._alias, skillName, e.Message); return null; }
        }

        // ---------------------------------------------------------------- items
        /// <summary>How many of <paramref name="qty"/> the player may take: the item's skill logic must allow it and the cash must cover it (charged here).
        /// Not hard mode: all of them, nothing charged.</summary>
        internal static int Grant(Player p, ItemInfo item, int qty)
        {
            if (!On || p == null || item == null || qty <= 0) return qty;
            try
            {
                if (!Logic_Assets.SkillCheck(p, item.skillLogic))
                {
                    if (_debug) Console.WriteLine("[hardeconomy] {0}: {1} x{2} refused (skill logic {3})", p._alias, item.name, qty, item.skillLogic);
                    Note(p, item, true); return 0;
                }
                int price = item.buyPrice;
                if (price <= 0) return qty;
                int kept = TakeCredit(p, item.id, qty);               // held at the wipe: put back for free
                int pay = qty - kept;
                int afford = (int)Math.Min((long)pay, Math.Max(0L, (long)p.Cash) / price);
                if (_debug) Console.WriteLine("[hardeconomy] {0}: {1} x{2} @ ${3} (kept {4}), cash ${5} -> {6}", p._alias, item.name, qty, price, kept, p.Cash, kept + afford);
                if (afford < pay) NoteShort(p, item, pay - afford);
                if (afford > 0)
                {
                    p.Cash -= price * afford;
                    lock (_lock) { Tally t = TallyFor(p); t.Spent += price * afford; }
                }
                return kept + afford;
            }
            catch (Exception e)
            {   // fail closed: an error here must not hand out free items
                Console.WriteLine("[hardeconomy] Grant failed for {0} / {1}: {2}", p._alias, item.name, e);
                return 0;
            }
        }

        private static Tally TallyFor(Player p)
        {
            Tally t;
            if (!_tally.TryGetValue(p, out t)) { t = new Tally(); _tally[p] = t; }
            return t;
        }

        private static void Note(Player p, ItemInfo item, bool locked)
        {
            lock (_lock)
            {
                Tally t = TallyFor(p);
                string n = (item.name ?? "item").Trim('"', ' ');
                if (!t.Locked.Contains(n) && t.Locked.Count < 8) t.Locked.Add(n);
            }
        }

        private static void NoteShort(Player p, ItemInfo item, int missing)
        {
            lock (_lock)
            {
                Tally t = TallyFor(p);
                string n = (item.name ?? "item").Trim('"', ' ');
                Lack found = null;
                foreach (Lack k in t.Short) if (k.Name == n) { found = k; break; }
                if (found == null) t.Short.Add(new Lack { Name = n, Price = item.buyPrice, Missing = missing });
                else found.Missing += missing;
            }
        }

        /// <summary>An inventory wipe that is followed by a build is about to run: remember what the player holds.</summary>
        internal static void NoteWipe(Player p)
        {
            if (!On || p == null) return;
            try
            {
                Credit c = new Credit { Until = DateTime.UtcNow.AddSeconds(20) };
                foreach (var kv in p._inventory)
                    if (kv.Value != null && kv.Value.quantity > 0) c.Held[kv.Key] = kv.Value.quantity;
                lock (_lock) { _credit[p] = c; }
                if (_debug) Console.WriteLine("[hardeconomy] {0}: wipe noted, {1} item kinds kept as credit", p._alias, c.Held.Count);
            }
            catch (Exception e) { Console.WriteLine("[hardeconomy] NoteWipe failed for {0}: {1}", p._alias, e.Message); }
        }

        private static int TakeCredit(Player p, int itemId, int qty)
        {
            lock (_lock)
            {
                Credit c;
                if (!_credit.TryGetValue(p, out c)) return 0;
                if (DateTime.UtcNow > c.Until) { _credit.Remove(p); return 0; }
                int have;
                if (!c.Held.TryGetValue(itemId, out have) || have <= 0) return 0;
                int use = Math.Min(have, qty);
                c.Held[itemId] = have - use;
                return use;
            }
        }

        /// <summary>The amount a set-to-N grant may reach: the current amount plus what can be paid for. Not hard mode: N.</summary>
        internal static int Target(Player p, int itemId, int want)
        {
            if (!On || p == null) return want;
            int cur = p.getInventoryAmount(itemId);
            if (want <= cur) return want;
            ItemInfo it = p._server._assets.getItemByID(itemId);
            return it == null ? want : cur + Grant(p, it, want - cur);
        }

        // The respawn / dropship-recall rebuy (autobuy) re-buys the whole build every time, and with cash short it printed a "Could not buy ..." line per item on EVERY death (Pub 11-08, Waylander:
        // "when i die it seems to be trying to buy everything again"). It is marked here and Summarize prints it as ONE line.
        private static readonly HashSet<Player> _autobuy = new HashSet<Player>();

        /// <summary>The next purchase summary of this player is an autobuy rebuy: one compact line.</summary>
        internal static void NoteAutobuy(Player p)
        {
            if (!On || p == null) return;
            lock (_lock) { _autobuy.Add(p); }
        }

        /// <summary>One line after a build: what it cost and what could not be bought.</summary>
        internal static void Summarize(Player p)
        {
            if (!On || p == null) return;
            Tally t = null;
            bool compact;
            lock (_lock)
            {
                _credit.Remove(p);
                if (_tally.TryGetValue(p, out t)) _tally.Remove(p);
                compact = _autobuy.Remove(p);
            }
            if (t == null) return;
            if (compact)
            {
                string cost = t.Spent > 0 ? String.Format("Rebuy: ${0:N0}", t.Spent) : "Rebuy";
                if (t.Short.Count == 0) { if (t.Spent > 0) p.sendMessage(0, String.Format("{0}. Cash left: ${1:N0}.", cost, p.Cash)); }
                else
                {
                    List<string> names = new List<string>();
                    foreach (Lack k in t.Short) { if (names.Count >= 4) break; names.Add(k.Name + (k.Missing > 1 ? " x" + k.Missing : "")); }
                    p.sendMessage(-1, String.Format("{0}; could not afford {1}{2}. You have ${3:N0}.", cost, String.Join(", ", names.ToArray()), t.Short.Count > 4 ? " (+" + (t.Short.Count - 4) + " more)" : "", p.Cash));
                }
                if (t.Locked.Count > 0) p.sendMessage(-1, String.Format("Not available to your class or rank: {0}.", String.Join(", ", t.Locked.ToArray())));
                return;
            }
            if (t.Spent > 0) p.sendMessage(0, String.Format("Bought for ${0:N0}. Cash left: ${1:N0}.", t.Spent, p.Cash));
            int shown = 0;
            foreach (Lack k in t.Short)
            {   // one warning per item that could not be bought (Travis 11-07)
                if (shown++ >= 10) { p.sendMessage(-1, String.Format("...and {0} more items you could not afford.", t.Short.Count - 10)); break; }
                p.sendMessage(-1, String.Format("Could not buy {0}{1}: ${2:N0} each, you have ${3:N0}.", k.Name, k.Missing > 1 ? " x" + k.Missing : "", k.Price, p.Cash));
            }
            if (t.Locked.Count > 0) p.sendMessage(-1, String.Format("Not available to your class or rank: {0}.", String.Join(", ", t.Locked.ToArray())));
        }

        // ---------------------------------------------------------------- old (pre-Pub) characters
        // The zone id was borrowed from an alpha zone: its old characters carry whatever that zone left them (999,999,999 cash for some, 0 cash for
        // others, an alpha starter kit). Each alias is looked at ONCE, the first time it enters this arena (PubChecked.txt, written by the zone): an
        // old character - one that is not brand new and has no experience - is reset to the Pub start, exactly what ?wipecharacter yes does.
        // Nobody is ever looked at twice, so cash earned in play is never taken back. A new Pub player is entered in the list on the first entry.
        private const int START_CASH = 50000;
        private const string CHECKED_PATH = "scripts/GameTypes/CTF/PubChecked.txt";
        private static HashSet<string> _checked;

        internal static void LegacyReset(Player p)
        {
            if (!On || p == null || String.IsNullOrEmpty(p._alias)) return;
            if (_debug)
                try
                {   // ammo boxes (vehicles 430 / 434) are meant to be gone from the Pub's LIO
                    int boxes = 0, all = 0;
                    Dictionary<string, int> kinds = new Dictionary<string, int>();
                    foreach (Vehicle v in p._arena.Vehicles)
                    {
                        all++;
                        string k = v._type == null ? "?" : v._type.Id + " " + v._type.Name;
                        int n; kinds.TryGetValue(k, out n); kinds[k] = n + 1;
                        if (v._type != null && (v._type.Id == 430 || v._type.Id == 434)) boxes++;
                    }
                    int spawners = 0, hides = 0;
                    foreach (var hs in p._arena._hides) { hides++; int hid = hs.Hide.HideData.HideId; if (hid == -430 || hid == -434) spawners++; }
                    Console.WriteLine("[hardeconomy] ammo box spawners loaded: {0} of {1} hide objects; ammo box vehicles alive: {2} (of {3} vehicles: {4})", spawners, hides, boxes, all, String.Join("; ", kinds.Select(kv => kv.Key + " x" + kv.Value).ToArray()));
                }
                catch (Exception e) { Console.WriteLine("[hardeconomy] ammo box count failed: " + e.Message); }
            try
            {
                string key = p._alias.Trim().ToLowerInvariant();
                lock (_lock)
                {
                    if (_checked == null)
                    {
                        _checked = new HashSet<string>();
                        if (File.Exists(CHECKED_PATH))
                            foreach (string raw in File.ReadAllLines(CHECKED_PATH))
                            {
                                string l = raw.Trim().ToLowerInvariant();
                                if (l.Length > 0 && !l.StartsWith("//")) _checked.Add(l);
                            }
                    }
                    if (!_checked.Add(key)) return;                         // looked at before
                    try { File.AppendAllText(CHECKED_PATH, key + "\r\n"); }
                    catch (Exception e) { Console.WriteLine("[hardeconomy] PubChecked.txt not writable: " + e.Message); }
                }
                if (p.firstTimePlayer) return;                                // brand new: the normal first-time setup is coming
                int cash = p.Cash, exp = p.Experience;
                if (_debug) Console.WriteLine("[hardeconomy] {0}: checked once, cash ${1}, exp {2}", p._alias, cash, exp);
                if (exp > 0) return;                                          // played here already (or on a zone with real experience): keep it
                Console.WriteLine("[hardeconomy] old character reset: {0} (cash ${1}, exp {2}) -> Pub start", p._alias, cash, exp);
                p.assignFirstTimeStats(false);                                // clean stats / inventory / skills, then the zone's own first-time setup
                Logic_Assets.RunEvent(p, p._server._zoneConfig.EventInfo.firstTimeSkillSetup);
                Logic_Assets.RunEvent(p, p._server._zoneConfig.EventInfo.firstTimeInvSetup);
                p.setDefaultSkill();
                p.firstTimePlayer = false;
                p.syncInventory();
                p.syncState();
                p.sendMessage(0, String.Format("&Welcome to the Pub: your old character was reset once to the fresh start - ${0:N0} cash and the starter kit.", START_CASH));
            }
            catch (Exception e) { Console.WriteLine("[hardeconomy] LegacyReset failed for {0}: {1}", p._alias, e.Message); }
        }

        /// <summary>The engine keeps Player.firstTimePlayer true until the player is "in game" at an arena entry, so a NEW player's first arena change
        /// re-ran the first-time setup: cash back to the start, kit and class reset (Pub chat 11-07: "my inventory/cash reset when I tried to change arenas").
        /// Called at the script's EnterArena, i.e. after the engine ran the setup for this entry: clear the flag so it runs once.</summary>
        internal static void NoRerun(Player p)
        {
            if (!On || p == null) return;
            try { p.firstTimePlayer = false; } catch (Exception) { }
        }

        // ---------------------------------------------------------------- end-of-game reward
        private static double _rCash = 120, _rExp = 3, _rPts = 6, _rWin = 0.5, _rMax = 120;
        private static int _rMinSecs = 120, _rMinPlayers = 4;

        private static void ReadRewardKey(string line)
        {
            int e = line.IndexOf('=');
            if (e <= 0) return;
            string k = line.Substring(0, e).Trim().ToLowerInvariant(), v = line.Substring(e + 1).Trim();
            double d;
            if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return;
            switch (k)
            {
                case "rewardcashpermin": _rCash = d; break;
                case "rewardexppermin": _rExp = d; break;
                case "rewardpointspermin": _rPts = d; break;
                case "rewardwinnerbonus": _rWin = d; break;
                case "rewardmaxminutes": _rMax = d; break;
                case "rewardminseconds": _rMinSecs = (int)d; break;
                case "rewardminplayers": _rMinPlayers = (int)d; break;
            }
        }

        /// <summary>
        /// The public arena's end-of-game reward, paid from the script's Game.End. The engine's own jackpot (cfg [Flag] UseJackpot) is switched off in the Pub cfg:
        /// it is players^2 x percent / 1000 in integer maths, so the loser share (750 / 1000) came out as 0 and the time played never counted (Pub night 11-07: "an 80 minute
        /// game and no reward even though we lost"). Here everybody who played gets a reward per minute played this game, capped; the winning team gets it x (1 + winnerBonus).
        /// </summary>
        internal static void PayGameRewards(Arena arena, Team winner)
        {
            if (!On || arena == null) return;
            try
            {
                int now = Environment.TickCount;
                int gameStart = arena._tickGameStarted;
                int secs = unchecked(now - gameStart) / 1000;
                List<Player> ps = new List<Player>();
                foreach (Player p in arena.Players)
                    if (p != null && !p.IsSpectator && p._team != null && !p._team.IsSpec) ps.Add(p);
                if (secs < _rMinSecs || ps.Count < _rMinPlayers)
                {
                    Console.WriteLine("[hardeconomy] game reward skipped: {0} s, {1} players (needs {2} s, {3} players)", secs, ps.Count, _rMinSecs, _rMinPlayers);
                    string why = secs < _rMinSecs ? string.Format("the game ran under {0} seconds", _rMinSecs) : string.Format("fewer than {0} players", _rMinPlayers);
                    foreach (Player p in ps) p.sendMessage(0, "No game reward this time: " + why + ".");      // 11-08: it used to be silent
                    return;
                }
                foreach (Player p in ps)
                {
                    // time played this game: from when he joined play (never before the game began) to now; the engine's own count only if it ever is larger
                    int since;
                    bool have;
                    lock (_lock) { have = _since.TryGetValue(p, out since); }
                    int from = have && unchecked(since - gameStart) > 0 ? since : gameStart;
                    double min = unchecked(now - from) / 60000.0;
                    int engine = p.StatsCurrentGame == null ? 0 : p.StatsCurrentGame.playSeconds;
                    if (engine / 60.0 > min) min = engine / 60.0;
                    min = Math.Min(_rMax, Math.Max(0.0, min));
                    if (min < 1.0)
                    {
                        p.sendMessage(0, "No game reward: you played under a minute of this game.");
                        continue;
                    }
                    bool won = winner != null && p._team == winner;
                    double mult = won ? 1.0 + _rWin : 1.0;
                    int cash = (int)(min * _rCash * mult), exp = (int)(min * _rExp * mult), pts = (int)(min * _rPts * mult);
                    p.Cash += cash;
                    p.Experience += exp;
                    p.BonusPoints += pts;
                    p.sendMessage(0, String.Format("Your Personal Reward: Points={0} Cash={1} Experience={2}  ({3:0} min played{4})", pts, cash, exp, min, won ? ", winner bonus" : ""));
                    if (_debug) Console.WriteLine("[hardeconomy] reward {0}: {1:0.0} min, {2}: ${3} {4} exp {5} pts", p._alias, min, won ? "won" : "lost", cash, exp, pts);
                }
            }
            catch (Exception e) { Console.WriteLine("[hardeconomy] PayGameRewards failed: " + e.Message); }
        }


        // ---- play time, kept here: the engine's StatsCurrentGame.playSeconds is never incremented in this build (the script keeps its own class clocks for the site stats for that reason),
        // so a reward built on it paid nobody and said nothing (Pub 11-08). A player's clock starts when he joins play (Player.Enter) and stops when he specs or leaves.
        private static readonly Dictionary<Player, int> _since = new Dictionary<Player, int>();

        /// <summary>Player.Enter: he is in play from now.</summary>
        internal static void NoteJoin(Player p)
        {
            if (!On || p == null) return;
            lock (_lock) { _since[p] = Environment.TickCount | 1; }
        }

        /// <summary>Player.Leave (a spec or a quit): his clock stops.</summary>
        internal static void NoteLeave(Player p)
        {
            if (p == null) return;
            lock (_lock) { _since.Remove(p); }
        }

        /// <summary>Forget a player's open tally (he left).</summary>
        internal static void Forget(Player p) { lock (_lock) { _tally.Remove(p); _credit.Remove(p); _since.Remove(p); _autobuy.Remove(p); } }
    }
}
