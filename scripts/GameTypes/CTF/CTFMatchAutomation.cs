using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// League match automation, zone side. The contract is docs/zone-match-automation.md in the
    /// gaming-perks-shop repo (freeinf.org).
    ///
    /// One shared poller per zone process fetches /api/matches/zone-queue every 30s. Any CTF arena's
    /// 1Hz poll opens the match arenas that are due (locked, spec quiet). Each match arena then
    /// places its own players from the queue's desired state, re-applies when updated_at changes
    /// (subs), and reports its game to /api/matches/{id}/game at start and at the end.
    ///
    /// Config: scripts/GameTypes/CTF/MatchAutomationConfig.txt (see the .example.txt). No file or no
    /// client_key = off. The file holds the site's MATCH_CLIENT_KEY: never pushed by Deploy-CTF.ps1.
    /// Runtime: *matchauto [on|off|apply|reload|ask].
    ///
    /// Captain's plan: a captain can plan a side (offense / defense) and a class for each player on the
    /// match page. Before kick-off the zone whispers each player their own plan; ?y accepts (and switches
    /// their class), ?n [o|d] [class] declines, optionally suggesting what they'd rather play. The answer
    /// goes back to the site (/api/matches/{id}/plan-response), where the captain sees it.
    ///
    /// Also: zone-wide reminders (*zone) at 30 / 15 / 5 minutes before kick-off, announce.one_minute in the arena a minute
    /// before kick-off, whispers (with a bong) to captains who still have to pick the side / set a lineup,
    /// ?side titan|collective for the home captain, *allowspec set once per player in match arenas, subs queued during
    /// the match until the player going out is dead or on the dropship, ?sub in:out / ?cancel for captains, and *mvp
    /// for powered aliases.
    /// </summary>
    static class MatchQueue
    {
        public class DesiredPlayer
        {
            public string Alias;
            public string Team;
            public bool Spec;
            // The captain's plan for this player (site: match_lineups.plan_side / plan_class), and their answer.
            public string PlanSide;    // "O", "D" or null
            public string PlanClass;   // "INF", "HVY", "SL", "MED", "ENG", "IFL", "JT" or null
            public string PlanAck;     // "yes", "no" or null (not answered yet)
            public bool HasPlan { get { return PlanSide != null || PlanClass != null; } }
            /// <summary>What they are being asked; a change means a new question.</summary>
            public string PlanKey { get { return (PlanSide ?? "-") + "/" + (PlanClass ?? "-"); } }
        }

        public class SubInfo
        {
            public string Id;
            public string Message;   // ready-made by the site, e.g. "[TSTA] Sub: anjro out, Soup in"
            public string OutAlias;
            public string InAlias;
            public string SquadTag;
        }

        /// <summary>A squad that still has something to do before players can be placed (site: announce.captains).</summary>
        public class CaptainReminder
        {
            public string SquadTag;
            public List<string> Aliases = new List<string>();
            public string Missing;   // "side,lineup": a change means progress, so the next whisper goes out at once
            public string Message;
        }

        public class QueuedMatch
        {
            public List<SubInfo> Subs = new List<SubInfo>();
            public string AnnounceOpened;      // *zone message when the arena opens (site-worded)
            // Zone-wide reminders, site-worded: minutes before kick-off -> message (30 / 15 / 5).
            public SortedDictionary<int, string> Reminders = new SortedDictionary<int, string>();
            public string AnnounceOneMinute;   // *arena message a minute before the kick-off countdown ends
            public List<CaptainReminder> Captains = new List<CaptainReminder>();
            public List<string> HomeLeaders = new List<string>();   // home captain + co-captains: who may ?side
            // Each squad's captain + co-captains by tag: who is told when a sub can't be made.
            public Dictionary<string, List<string>> SquadLeaders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public string Id;
            public string Arena;
            public string Status;
            public string UpdatedAt;
            public string ScheduledAtRaw;     // exactly as the site sent it; a change means staff moved the time
            public DateTime ScheduledAtUtc;   // DateTime.MinValue when missing or unparsable
            /// <summary>The kick-off instant as a key: the same time written in another format is not a change.</summary>
            public string ScheduleKey { get { return ScheduledAtUtc != DateTime.MinValue ? ScheduledAtUtc.Ticks.ToString(CultureInfo.InvariantCulture) : ScheduledAtRaw ?? ""; } }
            // Late start (site: hold / late). Held = the match waits for an earlier one with the same people to finish.
            public bool Held;
            public string HeldFor;            // hold.waiting_on.title, for *matchauto only
            public int SetupMin = 10;         // hold.setup_min, else late.setup_min: the fresh countdown once the wait ends
            public double StartsInMin;
            public bool SideReleased;
            public bool ClientReady;
            public List<string> Teams = new List<string>();
            public List<DesiredPlayer> Players = new List<DesiredPlayer>();
        }

        private const string CONFIG_PATH = "scripts/GameTypes/CTF/MatchAutomationConfig.txt";
        private const string DEFAULT_BASE_URL = "https://www.freeinf.org";
        private const int POLL_INTERVAL_MS = 30000;
        // While a match is live (side released, not finished) a sub must reach the game in a second or two.
        // The site's /zone-queue/changes endpoint is a tiny "did anything change?" read, so it is polled every
        // second and the full queue is fetched only when it disagrees with what we last loaded. If that
        // endpoint is not there (older site build) the full queue is polled every 5 seconds instead.
        private const int LIVE_LIGHT_INTERVAL_MS = 1000;
        private const int LIVE_FULL_INTERVAL_MS = 5000;
        private const int LIGHT_UNAVAILABLE_BACKOFF_MIN = 5;
        private const int SIG_MISMATCH_BACKOFF_S = 30;
        private const int OPEN_CHECK_INTERVAL_MS = 5000;
        private const int REOPEN_BACKOFF_MS = 60000;
        public const double OPEN_BEFORE_MIN = 30;

        private static readonly HttpClient _http = new HttpClient() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly object _configLock = new object();

        private static bool _configLoaded;
        private static bool _enabled;
        private static string _key;
        private static string _baseUrl = DEFAULT_BASE_URL;
        private static string _league = "ctfdl";

        private static volatile List<QueuedMatch> _matches = new List<QueuedMatch>();
        private static volatile bool _siteAutomationOff;
        private static string _fullSig = "";            // fingerprint of the last full queue we loaded
        private static DateTime _lastLight = DateTime.MinValue;
        private static DateTime _lightDisabledUntil = DateTime.MinValue;
        private static DateTime _mismatchBackoffUntil = DateTime.MinValue;
        private static string _lastMismatchSig;
        private static int _lightBusy;
        /// <summary>True when the site answered automation off: staff turned zone automation off, so there is nothing to do.</summary>
        public static bool SiteAutomationOff { get { return _siteAutomationOff; } }
        private static DateTime _lastFetchAttempt = DateTime.MinValue;
        private static DateTime _lastFetchOk = DateTime.MinValue;
        private static string _lastError;
        private static int _fetching;
        private static DateTime _lastOpenCheck = DateTime.MinValue;
        private static readonly Dictionary<string, DateTime> _openAttempts = new Dictionary<string, DateTime>();
        // Arenas this automation opened. The server only closes a named arena when a player LEAVES it empty,
        // so one nobody ever entered (or whose match left the queue) would sit in the list until a restart.
        private static readonly HashSet<string> _opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool Enabled { get { LoadConfig(false); return _enabled; } }
        /// <summary>The game_mode league games are recorded under ("CTFDL"), from the configured league slug.</summary>
        public static string LeagueMode { get { return (_league ?? "ctfdl").ToUpperInvariant(); } }
        public static DateTime LastFetchOk { get { return _lastFetchOk; } }
        public static string LastError { get { return _lastError; } }
        public static List<QueuedMatch> Matches { get { return _matches; } }

        #region Config

        public static void LoadConfig(bool force)
        {
            if (_configLoaded && !force) return;
            lock (_configLock)
            {
                if (_configLoaded && !force) return;
                _configLoaded = true;
                _enabled = true;
                _key = null;
                _baseUrl = DEFAULT_BASE_URL;
                _league = "ctfdl";
                try
                {
                    if (!File.Exists(CONFIG_PATH))
                    {
                        _enabled = false;
                        Console.WriteLine("[MatchAuto] No " + CONFIG_PATH + " - league match automation off.");
                        return;
                    }
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
                        switch (key)
                        {
                            case "enabled": _enabled = !(val.Equals("false", StringComparison.OrdinalIgnoreCase) || val == "0" || val.Equals("off", StringComparison.OrdinalIgnoreCase)); break;
                            case "client_key": _key = val.Length > 0 ? val : null; break;
                            case "base_url": if (val.Length > 0) _baseUrl = val.TrimEnd('/'); break;
                            case "league": if (val.Length > 0) _league = val; break;
                        }
                    }
                    if (_key == null) _enabled = false;
                }
                catch (Exception ex)
                {
                    _enabled = false;
                    Console.WriteLine("[MatchAuto] Config load error: " + ex.Message);
                }
                Console.WriteLine(String.Format("[MatchAuto] enabled={0} base={1} league={2} key={3}",
                    _enabled, _baseUrl, _league, _key == null ? "MISSING" : "set"));
                _matches = new List<QueuedMatch>();
                _lastFetchAttempt = DateTime.MinValue;
            }
        }

        #endregion

        #region Queue

        /// <summary>Starts a background fetch if one is due. Cheap; called from every arena's 1Hz poll.</summary>
        public static void PollIfDue()
        {
            if (!Enabled) return;
            DateTime now = DateTime.UtcNow;
            bool live = IsLive();
            bool lightOk = now >= _lightDisabledUntil;

            int fullEvery = (live && !lightOk) ? LIVE_FULL_INTERVAL_MS : POLL_INTERVAL_MS;
            if ((now - _lastFetchAttempt).TotalMilliseconds >= fullEvery) { StartFullFetch(); return; }

            if (live && lightOk && (now - _lastLight).TotalMilliseconds >= LIVE_LIGHT_INTERVAL_MS)
                StartLightCheck();
        }

        /// <summary>A queued match whose side is released and which has no result yet: lineups and subs can change any second.</summary>
        private static bool IsLive()
        {
            foreach (var m in _matches)
                if (m.SideReleased && (m.Status == "scheduled" || m.Status == "in_progress"))
                    return true;
            return false;
        }

        private static void StartFullFetch()
        {
            if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) return;
            _lastFetchAttempt = DateTime.UtcNow;

            Task.Run(async () =>
            {
                try { await Fetch(); }
                catch (Exception ex) { _lastError = ex.Message; Console.WriteLine("[MatchAuto] Queue fetch failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref _fetching, 0); }
            });
        }

        /// <summary>The same fields the site's light endpoint returns, in a stable order, so the two can be compared as strings.</summary>
        private static string Sig(string id, string status, string scheduledAt, string updatedAt, bool sideReleased)
        {
            return id + "|" + status + "|" + scheduledAt + "|" + updatedAt + "|" + (sideReleased ? "1" : "0");
        }

        /// <summary>
        /// One cheap request: has anything in the queue moved since the full load? If so, load the full queue
        /// right away. A signature that stays different after a reload (a match the full payload leaves out)
        /// backs off for 30s instead of reloading every second.
        /// </summary>
        private static void StartLightCheck()
        {
            if (Interlocked.CompareExchange(ref _lightBusy, 1, 0) != 0) return;
            _lastLight = DateTime.UtcNow;

            Task.Run(async () =>
            {
                try
                {
                    string url = String.Format("{0}/api/matches/zone-queue/changes?league={1}&hours=6&past=2", _baseUrl, Uri.EscapeDataString(_league));
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Add("X-Client-Key", _key);
                    var resp = await _http.SendAsync(req);
                    int code = (int)resp.StatusCode;
                    if (code == 404 || code == 405)
                    {
                        _lightDisabledUntil = DateTime.UtcNow.AddMinutes(LIGHT_UNAVAILABLE_BACKOFF_MIN);
                        Console.WriteLine("[MatchAuto] Site has no /zone-queue/changes yet - polling the full queue every " + (LIVE_FULL_INTERVAL_MS / 1000) + "s while a match is live.");
                        return;
                    }
                    if (!resp.IsSuccessStatusCode) return;

                    var root = MiniJson.Parse(await resp.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                    var parts = new List<string>();
                    foreach (var mo in MiniJson.Arr(root, "matches"))
                    {
                        var m = mo as Dictionary<string, object>;
                        if (m == null) continue;
                        string id = MiniJson.Str(m, "id");
                        if (String.IsNullOrEmpty(id)) continue;
                        parts.Add(Sig(id, MiniJson.Str(m, "status") ?? "", MiniJson.Str(m, "scheduled_at") ?? "",
                            MiniJson.Str(m, "updated_at") ?? "", MiniJson.Bool(m, "side_released")));
                    }
                    parts.Sort(StringComparer.Ordinal);
                    string sig = String.Join(";", parts.ToArray());

                    if (sig == _fullSig) { _lastMismatchSig = null; return; }
                    DateTime now = DateTime.UtcNow;
                    if (now < _mismatchBackoffUntil || (now - _lastFetchAttempt).TotalMilliseconds < 1000) return;
                    if (sig == _lastMismatchSig)
                    {   // we already reloaded for exactly this and still differ: the full payload omits something
                        _mismatchBackoffUntil = now.AddSeconds(SIG_MISMATCH_BACKOFF_S);
                        return;
                    }
                    _lastMismatchSig = sig;
                    StartFullFetch();
                }
                catch (Exception ex) { Console.WriteLine("[MatchAuto] Change check failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref _lightBusy, 0); }
            });
        }

        private static async Task Fetch()
        {
            string url = String.Format("{0}/api/matches/zone-queue?league={1}&hours=6&past=2", _baseUrl, Uri.EscapeDataString(_league));
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Client-Key", _key);
            var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _lastError = String.Format("HTTP {0}{1}", (int)resp.StatusCode, (int)resp.StatusCode == 403 ? " (client key rejected)" : "");
                Console.WriteLine("[MatchAuto] Queue " + _lastError + ": " + Clip(body, 200));
                return;
            }

            var root = MiniJson.Parse(body) as Dictionary<string, object>;
            var list = new List<QueuedMatch>();
            // The site can switch automation off (an empty queue with automation off). That is nothing to do:
            // an empty list below hands every match arena back and closes the empty ones.
            string automation = MiniJson.Str(root, "automation");
            bool off = automation != null && automation.Equals("off", StringComparison.OrdinalIgnoreCase);
            if (off != _siteAutomationOff)
                Console.WriteLine("[MatchAuto] Site automation is now " + (off ? "OFF - nothing to do." : "ON."));
            _siteAutomationOff = off;
            foreach (var mo in MiniJson.Arr(root, "matches"))
            {
                var m = mo as Dictionary<string, object>;
                if (m == null) continue;
                var q = new QueuedMatch
                {
                    Id = MiniJson.Str(m, "id"),
                    Arena = MiniJson.Str(m, "arena"),
                    Status = MiniJson.Str(m, "status"),
                    UpdatedAt = MiniJson.Str(m, "updated_at") ?? "",
                    ScheduledAtRaw = MiniJson.Str(m, "scheduled_at") ?? "",
                    StartsInMin = MiniJson.Num(m, "starts_in_min"),
                    SideReleased = MiniJson.Bool(m, "side_released"),
                };
                var client = MiniJson.Obj(m, "client");
                if (client != null)
                {
                    q.ClientReady = MiniJson.Bool(client, "ready");
                    foreach (var t in MiniJson.Arr(client, "teams"))
                        if (t is string && ((string)t).Length > 0) q.Teams.Add((string)t);
                    foreach (var po in MiniJson.Arr(client, "players"))
                    {
                        var p = po as Dictionary<string, object>;
                        if (p == null) continue;
                        string alias = MiniJson.Str(p, "alias");
                        string team = MiniJson.Str(p, "team");
                        if (String.IsNullOrEmpty(alias) || String.IsNullOrEmpty(team)) continue;
                        q.Players.Add(new DesiredPlayer
                        {
                            Alias = alias, Team = team, Spec = MiniJson.Bool(p, "spec"),
                            PlanSide = MiniJson.Str(p, "plan_side"),
                            PlanClass = MiniJson.Str(p, "plan_class"),
                            PlanAck = MiniJson.Str(p, "plan_ack"),
                        });
                    }
                }
                foreach (var so in MiniJson.Arr(m, "subs"))
                {
                    var s = so as Dictionary<string, object>;
                    if (s == null) continue;
                    object rawId;
                    string id = null;
                    if (s.TryGetValue("id", out rawId) && rawId != null)
                        id = rawId is double ? ((double)rawId).ToString("R", CultureInfo.InvariantCulture) : rawId as string;
                    if (String.IsNullOrEmpty(id)) continue;
                    string message = MiniJson.Str(s, "message");
                    if (String.IsNullOrWhiteSpace(message))
                    {
                        string tag = MiniJson.Str(s, "squad_tag");
                        message = String.Format("{0}Sub: {1} out, {2} in", String.IsNullOrEmpty(tag) ? "" : "[" + tag + "] ",
                            MiniJson.Str(s, "out_alias") ?? "?", MiniJson.Str(s, "in_alias") ?? "?");
                    }
                    q.Subs.Add(new SubInfo { Id = id, Message = message, OutAlias = MiniJson.Str(s, "out_alias"), InAlias = MiniJson.Str(s, "in_alias"), SquadTag = MiniJson.Str(s, "squad_tag") });
                }
                var announce = MiniJson.Obj(m, "announce");
                if (announce != null)
                {
                    q.AnnounceOpened = MiniJson.Str(announce, "opened");
                    q.AnnounceOneMinute = MiniJson.Str(announce, "one_minute");
                    foreach (var ro in MiniJson.Arr(announce, "reminders"))
                    {
                        var r = ro as Dictionary<string, object>;
                        int at = (int)MiniJson.Num(r, "at_min");
                        string text = MiniJson.Str(r, "message");
                        if (at > 0 && !String.IsNullOrWhiteSpace(text)) q.Reminders[at] = text;
                    }
                    // An older site sends only `opened`: that is the 30-minute one.
                    if (q.Reminders.Count == 0 && !String.IsNullOrWhiteSpace(q.AnnounceOpened)) q.Reminders[(int)OPEN_BEFORE_MIN] = q.AnnounceOpened;
                    foreach (var co in MiniJson.Arr(announce, "captains"))
                    {
                        var c = co as Dictionary<string, object>;
                        string text = MiniJson.Str(c, "message");
                        if (c == null || String.IsNullOrWhiteSpace(text)) continue;
                        var r = new CaptainReminder { SquadTag = MiniJson.Str(c, "squad_tag") ?? "", Message = text };
                        foreach (var a in MiniJson.Arr(c, "aliases"))
                            if (a is string && ((string)a).Length > 0) r.Aliases.Add((string)a);
                        r.Missing = String.Join(",", MiniJson.Arr(c, "missing").OfType<string>().ToArray());
                        if (r.Aliases.Count > 0) q.Captains.Add(r);
                    }
                }
                foreach (var a in MiniJson.Arr(MiniJson.Obj(m, "home"), "leaders"))
                    if (a is string && ((string)a).Length > 0) q.HomeLeaders.Add((string)a);
                foreach (string side in new[] { "home", "away" })
                {
                    var block = MiniJson.Obj(m, side);
                    string tag = MiniJson.Str(block, "tag");
                    if (String.IsNullOrEmpty(tag)) continue;
                    q.SquadLeaders[tag] = MiniJson.Arr(block, "leaders").OfType<string>().Where(x => x.Length > 0).ToList();
                }
                var hold = MiniJson.Obj(m, "hold");
                var late = MiniJson.Obj(m, "late");
                q.Held = hold != null;
                if (hold != null) q.HeldFor = MiniJson.Str(MiniJson.Obj(hold, "waiting_on"), "title");
                int setupMin = (int)MiniJson.Num(hold ?? late, "setup_min");
                if (setupMin > 0 && setupMin <= 60) q.SetupMin = setupMin;
                DateTime sched;
                if (DateTime.TryParse(q.ScheduledAtRaw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out sched))
                    q.ScheduledAtUtc = sched;
                if (off) continue;   // belt and braces: an off reply is treated as an empty queue
                if (!String.IsNullOrEmpty(q.Id) && !String.IsNullOrEmpty(q.Arena))
                    list.Add(q);
            }

            var sigParts = new List<string>();
            foreach (var q in list) sigParts.Add(Sig(q.Id, q.Status ?? "", q.ScheduledAtRaw ?? "", q.UpdatedAt ?? "", q.SideReleased));
            sigParts.Sort(StringComparer.Ordinal);
            _fullSig = String.Join(";", sigParts.ToArray());

            _matches = list;
            _lastFetchOk = DateTime.UtcNow;
            _lastError = null;
        }

        public static QueuedMatch FindByArena(string arenaName)
        {
            return _matches.FirstOrDefault(m => m.Arena.Equals(arenaName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Closes arenas we opened whose match has left the queue, once nobody is in them.</summary>
        private static void CloseStaleArenas(ZoneServer server)
        {
            if (_opened.Count == 0) return;
            // Only act on a fresh, successful queue read: a failed fetch leaves _matches stale or empty.
            if ((DateTime.UtcNow - _lastFetchOk).TotalSeconds > 90) return;

            foreach (string name in _opened.ToList())
            {
                if (_matches.Any(m => m.Arena.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                Arena found = null;
                using (DdMonitor.Lock(server._arenas))
                {
                    foreach (var kv in server._arenas)
                        if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) { found = kv.Value; break; }
                }
                if (found == null) { _opened.Remove(name); continue; }   // already gone
                if (found.TotalPlayerCount > 0) continue;                // closes when the last player leaves

                _opened.Remove(name);
                try
                {
                    found.close();
                    Console.WriteLine("[MatchAuto] Closed empty arena '" + name + "' (match left the queue).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[MatchAuto] Could not close '" + name + "': " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Opens match arenas that are due and not open yet. Runs on ZoneArenas.handleArenas (the arena
        /// poll thread), the same thread the server opens arenas on. A new named arena isn't polled until
        /// someone enters it, so its own placement starts then.
        /// </summary>
        public static void OpenDueArenas(ZoneServer server)
        {
            if (!Enabled) return;
            if ((DateTime.UtcNow - _lastOpenCheck).TotalMilliseconds < OPEN_CHECK_INTERVAL_MS) return;
            _lastOpenCheck = DateTime.UtcNow;

            CloseStaleArenas(server);

            foreach (var m in _matches)
            {
                if (m.StartsInMin > OPEN_BEFORE_MIN) continue;

                try { AnnounceOpened(server, m); RemindCaptains(server, m); }
                catch (Exception ex) { Console.WriteLine("[MatchAuto] Announcements failed for " + m.Id + ": " + ex.Message); }

                bool open;
                using (DdMonitor.Lock(server._arenas))
                    open = server._arenas.Keys.Any(k => k.Equals(m.Arena, StringComparison.OrdinalIgnoreCase));
                if (open) continue;

                DateTime last;
                if (_openAttempts.TryGetValue(m.Id, out last) && (DateTime.UtcNow - last).TotalMilliseconds < REOPEN_BACKOFF_MS) continue;
                _openAttempts[m.Id] = DateTime.UtcNow;

                try
                {
                    Arena arena = server.newArena(m.Arena, true);
                    _opened.Add(m.Arena);
                    arena._bLocked = true;
                    arena._specQuiet = true;
                    EnsureTeams(arena, m.Teams);
                    Console.WriteLine(String.Format("[MatchAuto] Opened '{0}' (match {1}, starts in {2:F0} min)", m.Arena, m.Id, m.StartsInMin));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(String.Format("[MatchAuto] Could not open '{0}': {1}", m.Arena, ex.Message));
                }
            }
        }

        // Announcements already made: match id + kick-off instant + mark, so a match staff moves is announced again.
        private static readonly HashSet<string> _openedAnnounced = new HashSet<string>();
        // Last zone-wide announcement per match: never two within ZONE_ANNOUNCE_GAP_MIN, whatever the queue says
        // (10-19: a kick-off time rewritten every 30s re-sent a reminder to every arena each time).
        private static readonly Dictionary<string, DateTime> _lastZoneAnnounce = new Dictionary<string, DateTime>();
        private const double ZONE_ANNOUNCE_GAP_MIN = 4;
        // Captain whispers: match id + squad tag -> when last sent and what was missing then.
        private static readonly Dictionary<string, KeyValuePair<DateTime, string>> _captainReminded = new Dictionary<string, KeyValuePair<DateTime, string>>();

        /// <summary>Kick-off still ahead (the side and lineups lock at the scheduled time).</summary>
        public static bool Upcoming(QueuedMatch m)
        {
            if (m.ScheduledAtUtc != DateTime.MinValue) return DateTime.UtcNow < m.ScheduledAtUtc;
            return m.Status == "scheduled" && m.StartsInMin > 0;
        }

        /// <summary>
        /// The zone-wide reminders (announce.reminders: 30, 15 and 5 minutes before kick-off) as *zone messages,
        /// each once. The text is re-read every poll, so it says what is still missing at that moment. A zone that
        /// starts late sends only the latest mark that has passed (one message, not three). Each mark goes out once
        /// per kick-off time: new text for a mark already sent is not sent again. Nothing while the match is held.
        /// </summary>
        private static void AnnounceOpened(ZoneServer server, QueuedMatch m)
        {
            if (m.Held || m.Reminders.Count == 0 || !Upcoming(m) || m.ScheduledAtUtc == DateTime.MinValue) return;
            double minsLeft = (m.ScheduledAtUtc - DateTime.UtcNow).TotalMinutes;
            string due = null;
            foreach (var kv in m.Reminders)   // ascending: the first mark at or above minsLeft is the current one
            {
                if (kv.Key < minsLeft) continue;
                if (!_openedAnnounced.Add(m.Id + "|" + m.ScheduleKey + "|" + kv.Key)) continue;
                if (due == null) due = kv.Value;   // the later marks are marked sent without sending
            }
            if (due == null) return;
            DateTime lastSent;
            if (_lastZoneAnnounce.TryGetValue(m.Id, out lastSent) && (DateTime.UtcNow - lastSent).TotalMinutes < ZONE_ANNOUNCE_GAP_MIN)
            {
                Console.WriteLine("[MatchAuto] Zone announcement for match " + m.Id + " skipped: one went out " + (int)(DateTime.UtcNow - lastSent).TotalSeconds + "s ago.");
                return;
            }
            _lastZoneAnnounce[m.Id] = DateTime.UtcNow;
            int bong;
            string text = SafeChat(SplitBong(due, out bong), CHAT_MAX - 7);   // room for "[Zone] "
            // Same shape as the *zone command: a leading colour code stays in front of the [Zone] tag.
            string line = text.Length > 0 && "~!@#$%^&*".IndexOf(text[0]) >= 0
                ? text[0] + "[Zone] " + text.Substring(1)
                : "[Zone] " + text;
            List<Arena> arenas;
            using (DdMonitor.Lock(server._arenas))
                arenas = server._arenas.Values.Where(a => a != null).ToList();
            foreach (Arena a in arenas)
                a.sendArenaMessage(line, bong);
            Console.WriteLine("[MatchAuto] Zone announcement for match " + m.Id + ": " + text);
        }

        /// <summary>
        /// Whispers the captains / co-captains of a squad that still has to pick the side or set its lineup:
        /// nobody can be placed until both are done. More often and louder as kick-off nears: every 5 min,
        /// every 2 min inside 10 min, every minute inside 5 min; bong %1 .. %4.
        /// </summary>
        private static void RemindCaptains(ZoneServer server, QueuedMatch m)
        {
            if (m.Held || m.Captains.Count == 0 || !Upcoming(m)) return;
            double minsLeft = m.ScheduledAtUtc != DateTime.MinValue ? (m.ScheduledAtUtc - DateTime.UtcNow).TotalMinutes : m.StartsInMin;
            double every = minsLeft > 10 ? 5 : minsLeft > 5 ? 2 : 1;
            int bong = minsLeft > 15 ? 1 : minsLeft > 10 ? 2 : minsLeft > 5 ? 3 : 4;

            foreach (var r in m.Captains)
            {
                string key = m.Id + "|" + r.SquadTag;
                KeyValuePair<DateTime, string> last;
                if (_captainReminded.TryGetValue(key, out last) && last.Value == r.Missing
                    && (DateTime.UtcNow - last.Key).TotalMinutes < every) continue;

                int sent = 0;
                foreach (string alias in r.Aliases)
                {
                    Player p = server.getPlayer(alias);
                    if (p == null) continue;
                    try { p.sendMessage(bong, "&" + SafeChat(r.Message, CHAT_MAX - 1)); sent++; }
                    catch (Exception ex) { Console.WriteLine("[MatchAuto] Captain reminder to " + alias + " failed: " + ex.Message); }
                }
                // Nobody online: try again on the next check (5s), so a captain who logs in hears it straight away.
                if (sent == 0) continue;
                _captainReminded[key] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, r.Missing);
                Console.WriteLine(String.Format("[MatchAuto] Reminded {0} captain(s) of [{1}] ({2}, {3:F0} min to go).", sent, r.SquadTag, r.Missing, minsLeft));
            }
        }

        /// <summary>
        /// Splits a trailing bong off site text ("... have fun!!! %30" -> text, 30). In game a typed %N becomes the
        /// message's bong; text the zone sends would show it literally.
        /// </summary>
        public static string SplitBong(string text, out int bong)
        {
            bong = 0;
            if (text == null) return "";
            var mm = System.Text.RegularExpressions.Regex.Match(text, @"\s*%(\d{1,3})\s*$");
            int n;
            if (mm.Success && Int32.TryParse(mm.Groups[1].Value, out n) && n <= 255)
            {
                bong = n;
                return text.Substring(0, mm.Index);
            }
            return text;
        }

        /// <summary>Longest chat line the zone sends from site text (the site caps its texts at 180).</summary>
        public const int CHAT_MAX = 200;

        /// <summary>
        /// Site text made safe for one chat packet: plain ASCII (the server sizes a string packet by its char count but
        /// writes UTF-8, so every non-ASCII char puts the packet out by a byte), no control chars, at most max chars.
        /// </summary>
        public static string SafeChat(string text, int max)
        {
            if (String.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(Math.Min(text.Length, max + 8));
            foreach (char c in text)
            {
                if (c >= 32 && c < 127) { sb.Append(c); continue; }
                switch (c)
                {
                    case '\u00B7': case '\u2022': case '\u2013': case '\u2014': case '\u2212': sb.Append('-'); break;
                    case '\u2018': case '\u2019': case '\u00B4': sb.Append('\''); break;
                    case '\u201C': case '\u201D': sb.Append('"'); break;
                    case '\u2026': sb.Append("..."); break;
                    case '\u00A0': case '\t': case '\r': case '\n': sb.Append(' '); break;
                    default: if (c >= 127) sb.Append('?'); break;   // other control chars are dropped
                }
            }
            string s = sb.ToString();
            return s.Length > max ? s.Substring(0, Math.Max(0, max - 3)) + "..." : s;
        }

        /// <summary>Re-read the queue on the next poll (after the zone itself changed something on the site).</summary>
        public static void RefreshSoon() { _lastFetchAttempt = DateTime.MinValue; }

        /// <summary>POSTs json to a site path with the client key. done(status, parsed reply or null); status 0 = unreachable.</summary>
        public static void Post(string path, string json, Action<int, Dictionary<string, object>> done)
        {
            if (!Enabled) { if (done != null) done(0, null); return; }
            Task.Run(async () =>
            {
                int code = 0;
                Dictionary<string, object> reply = null;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                    req.Headers.Add("X-Client-Key", _key);
                    var resp = await _http.SendAsync(req);
                    string body = await resp.Content.ReadAsStringAsync();
                    code = (int)resp.StatusCode;
                    Console.WriteLine(String.Format("[MatchAuto] POST {0} -> HTTP {1}: {2}", path, code, Clip(body, 200)));
                    try { reply = MiniJson.Parse(body) as Dictionary<string, object>; } catch { }
                }
                catch (Exception ex) { Console.WriteLine("[MatchAuto] POST " + path + " failed: " + ex.Message); }
                if (done != null)
                    try { done(code, reply); } catch (Exception ex) { Console.WriteLine("[MatchAuto] POST " + path + " callback: " + ex.Message); }
            });
        }

        /// <summary>
        /// The arena's team for a name from the site, ignoring case. The cfg's own teams ("Can T", "kuk C")
        /// are mixed case while the site upper-cases tags ("CAN T"); an exact lookup missed them and made a
        /// bare duplicate team with no colors or spawn.
        /// </summary>
        public static Team FindTeam(Arena arena, string name)
        {
            Team t = arena.getTeamByName(name);
            if (t != null) return t;
            return arena.Teams.FirstOrDefault(x => x != null && x._name != null && x._name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // Legacy squad slots in ctfpl.cfg that no league squad uses. A squad tag the cfg doesn't have yet borrows one
        // (renamed in that match arena only), because a team made on the fly gets an id past the cfg's team list and the
        // engine then throws in Player.unspec for its players: in the game but untracked, ~40 warnings a second (10-18, DF).
        // Players see the slot's cfg name and colours until the tag is added to ctfpl.cfg.
        private static readonly string[] SpareSlots = { "MOON", "Mir", "Juicy Fruit", "Smurfs", "kuk", "Red" };

        public static void EnsureTeams(Arena arena, IEnumerable<string> teams)
        {
            foreach (string name in teams)
            {
                if (FindTeam(arena, name) != null) continue;

                Team spare = SpareSlot(arena, name, teams);
                if (spare != null)
                {
                    string was = spare._name;
                    spare._name = name;
                    Console.WriteLine(String.Format("[MatchAuto] {0}: '{1}' has no team in ctfpl.cfg - playing in the '{2}' slot (id {3}). Add it to the cfg.",
                        arena._name, name, was, spare._id));
                    arena.sendArenaMessage(SafeChat(String.Format("&{0} has no team in this zone's cfg yet: it plays in the {1} slot ({1} colours).", name, was), CHAT_MAX));
                    continue;
                }

                // No spare slot (or a name without " T" / " C"): the old on-the-fly team, players on it partly invisible.
                Console.WriteLine(String.Format("[MatchAuto] {0}: WARNING '{1}' has no team in ctfpl.cfg and no spare slot is free - made one on the fly (id {2}).",
                    arena._name, name, arena.Teams.Count()));
                Team team = new Team(arena, arena._server)
                {
                    _name = name,
                    _isPrivate = false,
                    _id = (short)arena.Teams.Count()
                };
                arena.createTeam(team);
            }
        }

        /// <summary>An empty cfg team of a spare legacy squad on the same side (" T" / " C") as name, not used by this match.</summary>
        private static Team SpareSlot(Arena arena, string name, IEnumerable<string> matchTeams)
        {
            string side = name.EndsWith(" T", StringComparison.OrdinalIgnoreCase) ? " T"
                        : name.EndsWith(" C", StringComparison.OrdinalIgnoreCase) ? " C" : null;
            if (side == null) return null;
            int cfgTeams = arena._server._zoneConfig.teams.Count;
            foreach (string tag in SpareSlots)
            {
                // The engine's name index keeps the OLD name after a rename, so a slot already lent out in this arena is
                // still found here: it must also still carry its cfg name.
                Team t = FindTeam(arena, tag + side);
                if (t == null || !String.Equals(t._name, tag + side, StringComparison.OrdinalIgnoreCase)) continue;
                if (t._id < 0 || t._id >= cfgTeams || t.ActivePlayerCount > 0) continue;
                if (matchTeams.Any(m => m.Equals(t._name, StringComparison.OrdinalIgnoreCase))) continue;
                return t;
            }
            return null;
        }

        #endregion

        #region Game report

        /// <summary>POSTs the game report. For "played", retries while the stat rows haven't landed yet.</summary>
        public static void ReportGame(string matchId, string gameId, bool played, Action<string> announce)
        {
            if (!Enabled) return;
            Task.Run(async () =>
            {
                int attempts = played ? 6 : 3;
                for (int i = 0; i < attempts; i++)
                {
                    // The stats upload runs async at game end; give it a head start before "played".
                    int delay = played ? (i == 0 ? 15000 : 60000) : (i == 0 ? 0 : 15000);
                    if (delay > 0) await Task.Delay(delay);
                    try
                    {
                        string url = String.Format("{0}/api/matches/{1}/game", _baseUrl, Uri.EscapeDataString(matchId));
                        string json = String.Format("{{\"game_id\":\"{0}\",\"status\":\"{1}\"}}",
                            MiniJson.Escape(gameId), played ? "played" : "in_progress");
                        var req = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                        req.Headers.Add("X-Client-Key", _key);
                        var resp = await _http.SendAsync(req);
                        string body = await resp.Content.ReadAsStringAsync();
                        Console.WriteLine(String.Format("[MatchAuto] Report {0} {1} -> HTTP {2}: {3}",
                            played ? "played" : "in_progress", gameId, (int)resp.StatusCode, Clip(body, 300)));
                        if (!resp.IsSuccessStatusCode) continue;
                        if (!played) return;

                        var reply = MiniJson.Parse(body) as Dictionary<string, object>;
                        var result = MiniJson.Obj(reply, "result");
                        if (result != null && MiniJson.Bool(result, "recorded"))
                        {
                            announce("&Result recorded on freeinf.org. Standings updated.");
                            return;
                        }
                        string reason = result != null ? MiniJson.Str(result, "reason") : MiniJson.Str(reply, "ignored");
                        if (reason == null || !reason.Contains("no stat rows"))
                        {
                            announce(SafeChat("The site did not record the result (" + (reason ?? "unknown") + "). Staff can enter it in the match manager.", CHAT_MAX));
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[MatchAuto] Report failed: " + ex.Message);
                    }
                }
                if (played) announce("The site never got this game's stats. Staff can enter the result in the match manager.");
            });
        }

        #endregion

        #region Plan answers

        /// <summary>
        /// Sends a player's ?y / ?n to the site. done(null) on success, done(message) when it failed.
        /// side / cls are only sent with a "no" (what they would rather play).
        /// </summary>
        public static void PostPlanReply(string matchId, string alias, bool yes, string side, string cls, Action<string> done)
        {
            if (!Enabled) { done("match automation is off"); return; }
            Task.Run(async () =>
            {
                string error = null;
                try
                {
                    string url = String.Format("{0}/api/matches/{1}/plan-response", _baseUrl, Uri.EscapeDataString(matchId));
                    var sb = new StringBuilder();
                    sb.Append("{\"alias\":\"").Append(MiniJson.Escape(alias)).Append("\",\"answer\":\"").Append(yes ? "yes" : "no").Append('"');
                    if (side != null) sb.Append(",\"side\":\"").Append(side).Append('"');
                    if (cls != null) sb.Append(",\"class\":\"").Append(cls).Append('"');
                    sb.Append('}');
                    var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json") };
                    req.Headers.Add("X-Client-Key", _key);
                    var resp = await _http.SendAsync(req);
                    string body = await resp.Content.ReadAsStringAsync();
                    Console.WriteLine(String.Format("[MatchAuto] Plan answer {0} {1} -> HTTP {2}: {3}", alias, yes ? "yes" : "no", (int)resp.StatusCode, Clip(body, 200)));
                    if (!resp.IsSuccessStatusCode)
                    {
                        var reply = MiniJson.Parse(body) as Dictionary<string, object>;
                        error = MiniJson.Str(reply, "error") ?? ("HTTP " + (int)resp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    error = "could not reach freeinf.org";
                    Console.WriteLine("[MatchAuto] Plan answer failed: " + ex.Message);
                }
                try { done(error); } catch (Exception ex) { Console.WriteLine("[MatchAuto] Plan answer callback: " + ex.Message); }
            });
        }

        #endregion

        private static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }
    }

    /// <summary>
    /// Per-arena side: owned by the arena's Script_CTF and driven from its 1Hz poll.
    /// </summary>
    class MatchAutomationArena
    {
        private readonly Arena _arena;
        private string _matchId;
        private string _appliedUpdatedAt;
        private string _gameId;
        private bool _paused;
        private readonly HashSet<string> _arrivals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Players still to be verified in place, with tries so far. A placement can silently fail: the engine
        // refuses an unspec ("no spectator vehicle") when it comes the instant someone enters, so a move is
        // retried each poll until the player is where the lineup says, up to RETRY_LIMIT tries.
        private readonly Dictionary<string, int> _retry = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const int RETRY_LIMIT = 12;
        // Kick-off countdown (the timer command equivalent). Remember which scheduled_at it was set for: only a change
        // (staff moved the time) sets it again, so a mod typing the timer command is never fought.
        private string _timerForSchedule;
        private bool _kickoffEnd;   // the countdown reached zero and ended the warm-up: not a no-winner game worth announcing
        private bool _kickoffDone;  // our countdown for this scheduled_at has run out: the 1-minute warning is over with
        private bool _oneMinuteArmed;   // the countdown was seen above a minute: warn when it drops under
        private bool _oneMinuteSent;
        // Late start: this arena saw the match held (site hold != null), so the next poll without it is the release.
        private bool _holdSeen;
        private const int HOLD_TOPUP_MS = 120000;   // a held countdown under 2 minutes goes back to setup_min
        private DateTime _countdownEndsUtc = DateTime.MinValue;   // when the countdown we last set runs out
        private readonly HashSet<string> _announcedSubs = new HashSet<string>();
        // The match this arena last ran: *mvp comes after the game, when the match has already left the queue.
        private string _lastMatchId;
        // A non-sub move held back because the player carries a flag (alias -> reason), and players waiting on
        // one of them. Retried every poll, without the RETRY_LIMIT. (Rare: lineups are locked once it starts.)
        private readonly Dictionary<string, string> _held = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Subs being undone on the site (?cancel, or replaced by a new ?sub): neither player is moved meanwhile.
        private readonly HashSet<string> _reverting = new HashSet<string>();
        // Players a queued sub takes out who are still in play (recomputed every Reconcile), and the subs whose
        // captains have been told they are queued.
        private readonly HashSet<string> _waitOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _queuedTold = new HashSet<string>();
        private string _heldReported = "";
        private DateTime _heldReportedAt = DateTime.MinValue;
        private const int HELD_RESEND_S = 60;   // the site ignores a report older than 3 min
        private readonly HashSet<string> _allowSpecApplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The script's own Player.JoinGame / Player.LeaveGame handlers. Placement runs them in the order
        // ScriptArena.handlePlayerJoin does, so a placed player goes through the same path as one who
        // clicked into a team, instead of a bare Player.unspec / spec.
        private readonly Func<Player, bool> _onJoinGame;
        private readonly Func<Player, bool> _onLeaveGame;
        private readonly Action _onPlaced;
        // Sends a placed player to their side's dropship; no-op when they are already on dropship terrain.
        private readonly Action<Player> _toDropship;
        // Switches a player's class (the ?swap path, free): given the ?swap alias ("med", "hvy"...).
        private readonly Action<Player, string> _swapClass;
        // Captain's plan: who has been asked what (alias -> PlanKey), and who has answered it.
        private readonly Dictionary<string, string> _planAsked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _planAnswered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public MatchAutomationArena(Arena arena, Func<Player, bool> onJoinGame, Func<Player, bool> onLeaveGame, Action onPlaced, Action<Player> toDropship, Action<Player, string> swapClass)
        {
            _arena = arena;
            _onJoinGame = onJoinGame;
            _onLeaveGame = onLeaveGame;
            _onPlaced = onPlaced;
            _toDropship = toDropship;
            _swapClass = swapClass;
        }

        // Site class codes -> the ?swap alias and the name players know.
        private static readonly Dictionary<string, string[]> PlanClasses = new Dictionary<string, string[]>
        {
            { "INF", new[] { "inf", "Infantry" } },
            { "HVY", new[] { "hvy", "Heavy Weapons" } },
            { "SL",  new[] { "sl", "Squad Leader" } },
            { "MED", new[] { "med", "Field Medic" } },
            { "ENG", new[] { "eng", "Combat Engineer" } },
            { "IFL", new[] { "infil", "Infiltrator" } },
            { "JT",  new[] { "jt", "Jump Trooper" } },
        };
        // What a player may type after ?n to suggest a class.
        private static readonly Dictionary<string, string> ClassWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "inf", "INF" }, { "infantry", "INF" },
            { "hvy", "HVY" }, { "heavy", "HVY" },
            { "sl", "SL" }, { "squad", "SL" }, { "squadleader", "SL" },
            { "med", "MED" }, { "medic", "MED" },
            { "eng", "ENG" }, { "engineer", "ENG" },
            { "infil", "IFL" }, { "infiltrator", "IFL" }, { "10man", "IFL" },
            { "jt", "JT" }, { "jump", "JT" }, { "jumptrooper", "JT" },
        };

        private static string SideName(string s) { return s == "O" ? "OFFENSE" : s == "D" ? "DEFENSE" : null; }
        private static string ClassName(string c) { string[] v; return c != null && PlanClasses.TryGetValue(c, out v) ? v[1] : null; }
        private static string PlanText(string side, string cls)
        {
            string sn = SideName(side), cn = ClassName(cls);
            if (sn != null && cn != null) return sn + " as " + cn;
            return sn ?? cn ?? "?";
        }

        /// <summary>Before kick-off only: a reply after it would be a free class swap mid-match.</summary>
        private static bool BeforeKickoff(MatchQueue.QueuedMatch m)
        {
            if (m.ScheduledAtUtc != DateTime.MinValue) return DateTime.UtcNow < m.ScheduledAtUtc;
            return m.Status == "scheduled";
        }

        /// <summary>Whispers each player in the arena their captain's plan once (again if the captain changes it).</summary>
        private void AskPlans(MatchQueue.QueuedMatch match)
        {
            if (!BeforeKickoff(match)) return;
            foreach (Player p in _arena.Players.ToList())
            {
                var d = match.Players.FirstOrDefault(x => x.Alias.Equals(p._alias, StringComparison.OrdinalIgnoreCase));
                if (d == null || !d.HasPlan || d.PlanAck != null) continue;
                string asked, answered;
                if (_planAnswered.TryGetValue(p._alias, out answered) && answered == d.PlanKey) continue;
                if (_planAsked.TryGetValue(p._alias, out asked) && asked == d.PlanKey) continue;
                _planAsked[p._alias] = d.PlanKey;
                try
                {
                    p.sendMessage(0, "&Your captain has requested you play " + PlanText(d.PlanSide, d.PlanClass) + ".");
                    p.sendMessage(0, d.PlanClass != null
                        ? "Type ?y to accept (switches your class now), or ?n to decline. Suggest instead: ?n d med / ?n o / ?n hvy"
                        : "Type ?y to accept, or ?n to decline. Suggest instead: ?n d med / ?n o / ?n hvy");
                }
                catch (Exception ex) { Console.WriteLine("[MatchAuto] Plan ask failed for " + p._alias + ": " + ex.Message); }
            }
        }

        /// <summary>?y / ?n [o|d] [class]. Returns true when handled (always, for these two commands).</summary>
        public bool HandlePlanReply(Player player, string command, string payload)
        {
            bool yes = command.Equals("y", StringComparison.OrdinalIgnoreCase);
            var match = _matchId != null ? MatchQueue.FindByArena(_arena._name) : null;
            var d = match != null ? match.Players.FirstOrDefault(x => x.Alias.Equals(player._alias, StringComparison.OrdinalIgnoreCase)) : null;
            if (d == null || !d.HasPlan)
            {
                player.sendMessage(-1, "Nothing to answer: your captain hasn't set a side or class for you.");
                return true;
            }
            if (!BeforeKickoff(match))
            {
                player.sendMessage(-1, "The match has started. Use ?swap to change class.");
                return true;
            }

            string side = null, cls = null;
            if (!yes)
            {
                foreach (string raw in (payload ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string w = raw.Trim().ToLowerInvariant();
                    if (w == "o" || w == "off" || w == "offense" || w == "offence") side = "O";
                    else if (w == "d" || w == "def" || w == "defense" || w == "defence") side = "D";
                    else if (ClassWords.ContainsKey(w)) cls = ClassWords[w];
                    else
                    {
                        player.sendMessage(-1, "Didn't understand '" + raw + "'. Try ?n, ?n d, ?n med or ?n o hvy.");
                        return true;
                    }
                }
            }

            string key = d.PlanKey;
            string matchId = _matchId;
            Player who = player;
            MatchQueue.PostPlanReply(matchId, player._alias, yes, side, cls, error =>
            {
                if (error != null) { who.sendMessage(-1, MatchQueue.SafeChat("Couldn't send your answer (" + error + "). Try again.", MatchQueue.CHAT_MAX)); return; }
                _planAnswered[who._alias] = key;
                if (yes) who.sendMessage(0, "&Accepted: " + PlanText(d.PlanSide, d.PlanClass) + ". Your captain can see it on freeinf.org.");
                else if (side != null || cls != null) who.sendMessage(0, "&Sent to your captain: you'd rather play " + PlanText(side, cls) + ".");
                else who.sendMessage(0, "&Declined. Your captain can see it on freeinf.org.");
            });

            // ?y switches them to the planned class straight away (free: their captain asked for it).
            if (yes && d.PlanClass != null && _swapClass != null)
            {
                string[] v;
                if (PlanClasses.TryGetValue(d.PlanClass, out v))
                {
                    try { _swapClass(player, v[0]); }
                    catch (Exception ex) { Console.WriteLine("[MatchAuto] Plan class swap failed for " + player._alias + ": " + ex.Message); }
                }
            }
            return true;
        }

        /// <summary>game_mode for stats of the current game when it is a league match game (e.g. "CTFDL"), else null.</summary>
        public string LeagueGameMode { get { return _gameId != null ? MatchQueue.LeagueMode : null; } }

        /// <summary>The id this game's stats must be uploaded under, or null when it isn't a league match game.</summary>
        public string CurrentGameId { get { return _gameId; } }

        public void Poll()
        {
            MatchQueue.PollIfDue();
            MatchQueue.OpenDueArenas(_arena._server);
            if (!MatchQueue.Enabled) return;

            var match = MatchQueue.FindByArena(_arena._name);
            if (match == null)
            {
                // Match completed or cancelled on the site: hand the arena back. Only trust a fresh queue.
                if (_matchId != null && (DateTime.UtcNow - MatchQueue.LastFetchOk).TotalSeconds < 90)
                {
                    _arena._bLocked = false;
                    _arena._specQuiet = false;
                    _arena.sendArenaMessage(MatchQueue.SiteAutomationOff
                        ? "League match automation was turned off on freeinf.org. Arena unlocked."
                        : "This match is no longer run by freeinf.org automation (closed, cancelled or taken manual). Arena unlocked.");
                    // stop the kick-off countdown we started, so it cannot end a game nobody scheduled
                    if (_timerForSchedule != null) _arena.setTicker(1, _arena.playtimeTickerIdx, 0, "");
                    _held.Clear();
                    _waiting.Clear();
                    _waitOut.Clear();
                    if (_heldReported != "") ReportHolds(true);
                    lock (_reverting) _reverting.Clear();
                    _queuedTold.Clear();
                    _lastMatchId = _matchId;
                    _matchId = null;
                    _appliedUpdatedAt = null;
                    _gameId = null;
                    _timerForSchedule = null;
                    _kickoffEnd = false;
                    _holdSeen = false;
                    _countdownEndsUtc = DateTime.MinValue;
                    _retry.Clear();
                    _announcedSubs.Clear();
                    _planAsked.Clear();
                    _planAnswered.Clear();
                }
                return;
            }

            if (_matchId != match.Id)
            {
                _matchId = match.Id;
                _lastMatchId = match.Id;
                _appliedUpdatedAt = null;
                _timerForSchedule = null;
                _holdSeen = false;
                _countdownEndsUtc = DateTime.MinValue;
                _announcedSubs.Clear();
                _planAsked.Clear();
                _planAnswered.Clear();
                _held.Clear();
                _waiting.Clear();
                _waitOut.Clear();
                _heldReported = "";
                lock (_reverting) _reverting.Clear();
                _queuedTold.Clear();
                _allowSpecApplied.Clear();
                _arena._bLocked = true;
                _arena._specQuiet = true;
            }

            // Kick-off countdown: the equivalent of the timer command with starts_in_min. At zero it ends the warm-up
            // game and the arena's own loop starts the match with the placed players. Set once when the arena is
            // first seen and again only if staff moved scheduled_at (the instant, not its spelling); never once the
            // match is in progress.
            if (!_paused && match.Status == "scheduled" && match.ScheduleKey != _timerForSchedule)
            {
                _timerForSchedule = match.ScheduleKey;
                double secs = match.ScheduledAtUtc == DateTime.MinValue ? 0 : (match.ScheduledAtUtc - DateTime.UtcNow).TotalSeconds;
                if (secs > 1) SetCountdown(secs, "kick-off countdown set");
            }

            HoldCountdown(match);
            EnforceAllowSpec();
            OneMinuteWarning(match);

            if (_paused || !match.SideReleased || !match.ClientReady) return;

            MatchQueue.EnsureTeams(_arena, match.Teams);

            if (match.UpdatedAt != _appliedUpdatedAt)
            {
                bool first = _appliedUpdatedAt == null;
                int moved = Reconcile(match, null);
                _appliedUpdatedAt = match.UpdatedAt;
                _arrivals.Clear();
                foreach (Player p in _arena.Players.ToList()) _retry[p._alias] = 0;
                if (moved > 0 && _onPlaced != null) _onPlaced();

                // Subs the site already made before this arena's first apply are baked into the lineup:
                // mark them announced rather than replaying them. Later ones are announced by SubNotices
                // when the swap actually happens.
                if (first)
                {
                    foreach (var sub in match.Subs) _announcedSubs.Add(sub.Id);
                    _arena.sendArenaMessage("&Lineups applied from freeinf.org: starters are on their teams, the bench is in spec.");
                }
                else if (moved > 0 && match.Subs.All(x => _announcedSubs.Contains(x.Id)))
                    _arena.sendArenaMessage(String.Format("&Lineup updated from freeinf.org ({0} player{1} moved).", moved, moved == 1 ? "" : "s"));
            }
            else
            {
                foreach (string a in _arrivals) _retry[a] = 0;
                _arrivals.Clear();
                if (_retry.Count > 0)
                {
                    var keys = new HashSet<string>(_retry.Keys, StringComparer.OrdinalIgnoreCase);
                    if (Reconcile(match, keys) > 0 && _onPlaced != null) _onPlaced();
                    var desired = new Dictionary<string, MatchQueue.DesiredPlayer>(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in match.Players) desired[d.Alias] = d;
                    foreach (string alias in keys)
                    {
                        Player pl = _arena.Players.FirstOrDefault(x => x._alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
                        // A queued sub / a move held for a flag waits as long as it takes, not RETRY_LIMIT polls.
                        if (pl != null && (_held.ContainsKey(alias) || _waiting.Contains(alias))) continue;
                        if (pl == null || IsPlaced(pl, desired) || ++_retry[alias] >= RETRY_LIMIT)
                            _retry.Remove(alias);
                    }
                }
            }

            SubNotices(match);
            ReportHolds(false);
            AskPlans(match);

            // The match teams are made on the fly, so the engine has no respawn warp for their team id and
            // an unspecced player is left at 0,0. Until the game is running, anyone playing who is not on
            // dropship terrain goes to their side's dropship (the same warp the game start uses).
            if (_toDropship != null && !_arena._bGameRunning)
            {
                foreach (Player p in _arena.Players.ToList())
                {
                    try { if (!p.IsSpectator && !p.IsDead && p._team != null) _toDropship(p); }
                    catch (Exception ex) { Console.WriteLine("[MatchAuto] Dropship warp failed for " + p._alias + ": " + ex.Message); }
                }
            }
        }

        public void OnPlayerEnterArena(Player player)
        {
            if (_matchId != null) _arrivals.Add(player._alias);
        }

        /// <summary>
        /// Moves only the players whose team/spec differs from the desired state. Returns how many moved.
        /// Two passes: first everyone who leaves a team, then the spectators who come in, so a sub's swap
        /// happens out-then-in. During the match a sub is queued: the player going out stays in until they are
        /// dead or on the dropship (so never off a flag or mid-fight), then the two swap.
        /// </summary>
        private int Reconcile(MatchQueue.QueuedMatch match, HashSet<string> only)
        {
            var desired = new Dictionary<string, MatchQueue.DesiredPlayer>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in match.Players) desired[d.Alias] = d;
            var players = _arena.Players.ToList();

            // Who each sub brought in for whom (a later sub of the same player wins), and both players of a sub
            // being cancelled, who are left exactly where they are.
            var inFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var frozen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in match.Subs)
            {
                if (String.IsNullOrEmpty(s.InAlias) || String.IsNullOrEmpty(s.OutAlias)) continue;
                inFor[s.InAlias] = s.OutAlias;
                if (IsReverting(s.Id)) { frozen.Add(s.InAlias); frozen.Add(s.OutAlias); }
            }

            // Queued subs: during the match, a player a sub takes out stays in until dead or on the dropship.
            _waitOut.Clear();
            if (SubsWaitForBreak(match))
            {
                foreach (Player p in players)
                {
                    MatchQueue.DesiredPlayer d;
                    bool off = !desired.TryGetValue(p._alias, out d) || d.Spec;
                    if (off && InPlay(p) && PendingSubOut(match, p._alias) != null) _waitOut.Add(p._alias);
                }
            }

            int moved = 0;

            // Pass 1: off a team (to spec, to the bench, or to the other team).
            foreach (Player player in players)
            {
                if (only != null && !only.Contains(player._alias)) continue;
                try
                {
                    MatchQueue.DesiredPlayer d;
                    desired.TryGetValue(player._alias, out d);
                    Team target = d != null ? MatchQueue.FindTeam(_arena, d.Team) : null;
                    if (d != null && target == null) continue;

                    if (player.IsSpectator)
                    {   // already off: only a bench player sitting on the wrong bench team name to fix
                        _held.Remove(player._alias);
                        if (d != null && d.Spec && player._team != target) { SpecViaScript(player, target); moved++; }
                        continue;
                    }
                    if (d != null && !d.Spec && player._team == target) { _held.Remove(player._alias); continue; }
                    if (frozen.Contains(player._alias)) continue;
                    if (_waitOut.Contains(player._alias)) { _waiting.Add(player._alias); continue; }   // queued sub
                    if (CarriesFlag(player)) { Hold(player, "flag"); continue; }                     // any other move
                    _held.Remove(player._alias);
                    _waiting.Remove(player._alias);

                    if (d == null) SpecViaScript(player, null);          // not part of this match: watch from spec
                    else if (d.Spec) SpecViaScript(player, target);      // to the bench
                    else { SpecViaScript(player, null); UnspecViaScript(player, target); }   // to the other team
                    moved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(String.Format("[MatchAuto] Could not place {0}: {1}", player._alias, ex.Message));
                }
            }

            // Pass 2: spectators who should be playing. One coming in for a player who is still in waits for them.
            foreach (Player player in players)
            {
                if (only != null && !only.Contains(player._alias)) continue;
                try
                {
                    MatchQueue.DesiredPlayer d;
                    if (!desired.TryGetValue(player._alias, out d) || d.Spec || !player.IsSpectator) { _waiting.Remove(player._alias); continue; }
                    if (frozen.Contains(player._alias)) continue;
                    Team target = MatchQueue.FindTeam(_arena, d.Team);
                    if (target == null) continue;

                    string outAlias;
                    if (inFor.TryGetValue(player._alias, out outAlias) && (_waitOut.Contains(outAlias) || _held.ContainsKey(outAlias)))
                    {
                        _waiting.Add(player._alias);
                        continue;
                    }
                    _waiting.Remove(player._alias);
                    UnspecViaScript(player, target);
                    moved++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(String.Format("[MatchAuto] Could not place {0}: {1}", player._alias, ex.Message));
                }
            }
            return moved;
        }

        private bool CarriesFlag(Player player)
        {
            try
            {
                if (_arena._flags == null) return false;
                foreach (var f in _arena._flags.Values.ToList())
                    if (f != null && f.carrier == player) return true;
            }
            catch (Exception ex) { Console.WriteLine("[MatchAuto] Flag check failed for " + player._alias + ": " + ex.Message); }
            return false;
        }

        private void Hold(Player player, string reason)
        {
            if (_held.ContainsKey(player._alias)) return;
            _held[player._alias] = reason;
            _arena.sendArenaMessage("&" + player._alias + " has a flag: their move waits until it's dropped.");
            Console.WriteLine("[MatchAuto] " + _arena._name + ": holding " + player._alias + "'s move (" + reason + ").");
        }

        private bool IsReverting(string subId)
        {
            lock (_reverting) return _reverting.Contains(subId);
        }

        /// <summary>The latest sub taking this player out: the one the lineup is waiting on.</summary>
        private static MatchQueue.SubInfo PendingSubOut(MatchQueue.QueuedMatch match, string alias)
        {
            return match.Subs.LastOrDefault(x => x.OutAlias != null && x.OutAlias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Alive, off the dropship and not in spec: a sub taking this player out waits.</summary>
        private bool InPlay(Player p)
        {
            return p != null && !p.IsSpectator && !p.IsDead
                && _arena.getTerrainID(p._state.positionX, p._state.positionY) != 4;
        }

        /// <summary>During the match itself subs wait for a break; before kick-off and between games they are made at once.</summary>
        private bool SubsWaitForBreak(MatchQueue.QueuedMatch match)
        {
            return _arena._bGameRunning && !BeforeKickoff(match);
        }

        private Player InArena(string alias)
        {
            return alias == null ? null : _arena.Players.FirstOrDefault(x => x._alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The squad tag this alias is captain / co-captain of, or null.</summary>
        private static string SquadOf(MatchQueue.QueuedMatch match, string alias)
        {
            foreach (var kv in match.SquadLeaders)
                if (kv.Value.Any(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase))) return kv.Key;
            return null;
        }

        /// <summary>A squad's latest sub that hasn't happened in the arena yet (the player going out is still playing).</summary>
        private MatchQueue.SubInfo LatestQueuedFor(MatchQueue.QueuedMatch match, string squadTag)
        {
            for (int i = match.Subs.Count - 1; i >= 0; i--)
            {
                var s = match.Subs[i];
                if (s.OutAlias == null || !String.Equals(s.SquadTag, squadTag, StringComparison.OrdinalIgnoreCase) || IsReverting(s.Id)) continue;
                var d = match.Players.FirstOrDefault(x => x.Alias.Equals(s.OutAlias, StringComparison.OrdinalIgnoreCase));
                if (d != null && !d.Spec) continue;   // a later sub brought them back
                Player outP = InArena(s.OutAlias);
                if (outP != null && !outP.IsSpectator) return s;
            }
            return null;
        }

        /// <summary>Private message to a squad's captain + co-captains who are online anywhere in the zone.</summary>
        private void TellCaptains(MatchQueue.QueuedMatch match, string squadTag, string text, int bong)
        {
            List<string> leaders;
            if (squadTag == null || !match.SquadLeaders.TryGetValue(squadTag, out leaders)) return;
            foreach (string alias in leaders)
            {
                Player p = _arena._server.getPlayer(alias);
                if (p == null) continue;
                try { p.sendMessage(bong, text); }
                catch (Exception ex) { Console.WriteLine("[MatchAuto] Message to " + alias + " failed: " + ex.Message); }
            }
        }

        /// <summary>
        /// Each sub is announced when it actually happens in the arena ("&KEVI (Kev) OUT: anjro --- IN: Soup").
        /// While it waits for the player going out to die or reach the dropship, that squad's captains are told
        /// once that it is queued and how to cancel it.
        /// </summary>
        private void SubNotices(MatchQueue.QueuedMatch match)
        {
            foreach (var sub in match.Subs)
            {
                if (sub.OutAlias == null || _announcedSubs.Contains(sub.Id) || IsReverting(sub.Id)) continue;
                var d = match.Players.FirstOrDefault(x => x.Alias.Equals(sub.OutAlias, StringComparison.OrdinalIgnoreCase));
                if (d != null && !d.Spec) { _announcedSubs.Add(sub.Id); continue; }   // superseded by a later sub
                Player outP = InArena(sub.OutAlias);
                if (outP == null || outP.IsSpectator)
                {
                    _announcedSubs.Add(sub.Id);
                    string text = MatchQueue.SafeChat(sub.Message, 120);
                    _arena.sendArenaMessage("&" + text);
                    continue;
                }
                if (_queuedTold.Add(sub.Id))
                    TellCaptains(match, sub.SquadTag, String.Format("&Sub queued: {0} in for {1} once {1} is dead or on the dropship. ?cancel to cancel.",
                        sub.InAlias, sub.OutAlias), 1);
            }
        }

        /// <summary>Undoes a sub on the site (it never happened in the arena). done(null) on success, done(error) if not.</summary>
        private void RevertSub(MatchQueue.QueuedMatch match, MatchQueue.SubInfo sub, Action<string> done)
        {
            lock (_reverting) _reverting.Add(sub.Id);
            MatchQueue.Post("/api/matches/" + Uri.EscapeDataString(match.Id) + "/sub",
                "{\"revert_sub_id\":\"" + MiniJson.Escape(sub.Id) + "\"}",
                (code, reply) =>
                {
                    if ((code >= 200 && code < 300) || code == 404)   // 404: already undone
                    {
                        MatchQueue.RefreshSoon();
                        done(null);
                        return;
                    }
                    lock (_reverting) _reverting.Remove(sub.Id);
                    done(MiniJson.Str(reply, "error") ?? (code == 0 ? "could not reach freeinf.org" : "HTTP " + code));
                });
        }

        /// <summary>?cancel (or ?sub in:out cancel): cancels the caller's squad's latest queued sub.</summary>
        private void CancelQueuedSub(Player player, MatchQueue.QueuedMatch match)
        {
            string tag = SquadOf(match, player._alias);
            if (tag == null) { player.sendMessage(-1, "Only a captain or co-captain can cancel a sub."); return; }
            var sub = LatestQueuedFor(match, tag);
            if (sub == null) { player.sendMessage(-1, "No queued sub to cancel."); return; }
            Player who = player;
            var m = match;
            RevertSub(match, sub, error =>
            {
                if (error != null) { who.sendMessage(-1, MatchQueue.SafeChat("Couldn't cancel the sub: " + error, MatchQueue.CHAT_MAX)); return; }
                TellCaptains(m, tag, String.Format("&Sub cancelled: {0} stays in, {1} stays out.", sub.OutAlias, sub.InAlias), 0);
            });
        }

        public bool HandleCancelCommand(Player player)
        {
            if (!MatchQueue.Enabled) return false;
            var match = _matchId != null ? MatchQueue.FindByArena(_arena._name) : null;
            if (match == null) { player.sendMessage(-1, "?cancel works in your league match arena."); return true; }
            CancelQueuedSub(player, match);
            return true;
        }

        /// <summary>
        /// ?sub playerIn:playerOut - a captain / co-captain subs in game, in their match arena. The site checks who may
        /// (captain of that team, both on its roster, out is starting). During the match the swap is queued until
        /// the player going out is dead or on the dropship. A new ?sub replaces the squad's queued one;
        /// ?sub in:out cancel (or ?cancel) cancels it.
        /// </summary>
        public bool HandleSubCommand(Player player, string payload)
        {
            if (!MatchQueue.Enabled) return false;
            var match = _matchId != null ? MatchQueue.FindByArena(_arena._name) : null;
            if (match == null) { player.sendMessage(-1, "?sub works in your league match arena."); return true; }

            string arg = (payload ?? "").Trim();
            if (arg.Equals("cancel", StringComparison.OrdinalIgnoreCase) || arg.EndsWith(" cancel", StringComparison.OrdinalIgnoreCase))
            {
                CancelQueuedSub(player, match);
                return true;
            }
            string[] parts = arg.Split(':');
            if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
            {
                player.sendMessage(-1, "Usage: ?sub playerIn:playerOut   (?cancel cancels a queued sub)");
                return true;
            }
            string inA = parts[0].Trim(), outA = parts[1].Trim();

            Player who = player;
            var m = match;
            string tag = SquadOf(match, player._alias);
            Action send = () => MatchQueue.Post("/api/matches/" + Uri.EscapeDataString(m.Id) + "/sub",
                "{\"alias\":\"" + MiniJson.Escape(who._alias) + "\",\"in_alias\":\"" + MiniJson.Escape(inA) + "\",\"out_alias\":\"" + MiniJson.Escape(outA) + "\"}",
                (code, reply) =>
                {
                    if (code >= 200 && code < 300) { MatchQueue.RefreshSoon(); return; }   // queued / announced by SubNotices
                    who.sendMessage(-1, MatchQueue.SafeChat("Sub not made: " + (MiniJson.Str(reply, "error") ?? (code == 0 ? "could not reach freeinf.org" : "HTTP " + code)), MatchQueue.CHAT_MAX));
                });

            // Only one queued sub per squad from ?sub: a new request replaces the one still waiting.
            var prev = tag != null ? LatestQueuedFor(match, tag) : null;
            if (prev == null) { send(); return true; }
            RevertSub(match, prev, error =>
            {
                if (error != null) { who.sendMessage(-1, MatchQueue.SafeChat("Couldn't replace the queued sub: " + error, MatchQueue.CHAT_MAX)); return; }
                TellCaptains(m, tag, String.Format("&Queued sub replaced ({0} for {1} cancelled).", prev.InAlias, prev.OutAlias), 0);
                send();
            });
            return true;
        }

        /// <summary>
        /// Tells the site which moves are waiting (/api/matches/{id}/placement) so the match page says "waiting on the
        /// arena": on any change, and every minute while something waits (the site ignores reports over 3 min old).
        /// </summary>
        private void ReportHolds(bool force)
        {
            if (_matchId == null) return;
            var items = new List<KeyValuePair<string, string>>();
            foreach (var kv in _held) if (InArena(kv.Key) != null) items.Add(new KeyValuePair<string, string>(kv.Key, "flag"));
            foreach (string a in _waitOut)
            {   // only someone who is really still in (they may have left or died since the last Reconcile)
                Player p = InArena(a);
                if (p != null && InPlay(p) && !_held.ContainsKey(a)) items.Add(new KeyValuePair<string, string>(a, "is still in play"));
            }
            items.Sort((x, y) => String.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase));

            string sig = String.Join(";", items.Select(kv => kv.Key + ":" + kv.Value).ToArray());
            bool due = sig != _heldReported || (sig != "" && (DateTime.UtcNow - _heldReportedAt).TotalSeconds >= HELD_RESEND_S);
            if (!force && !due) return;
            _heldReported = sig;
            _heldReportedAt = DateTime.UtcNow;

            var sb = new StringBuilder("{\"held\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"alias\":\"").Append(MiniJson.Escape(items[i].Key)).Append("\",\"reason\":\"").Append(MiniJson.Escape(items[i].Value)).Append("\"}");
            }
            sb.Append("]}");
            MatchQueue.Post("/api/matches/" + Uri.EscapeDataString(_matchId) + "/placement", sb.ToString(), null);
        }

        /// <summary>
        /// *allowspec for a match arena: each player's spectating is switched on once, when they are first seen in
        /// it for this match. Someone who then deliberately switches it off is left alone.
        /// </summary>
        private void EnforceAllowSpec()
        {
            if (_matchId == null || !_arena._server._zoneConfig.arena.allowSpectating) return;
            foreach (Player p in _arena.Players.ToList())
                if (_allowSpecApplied.Add(p._alias)) p._bAllowSpectator = true;
        }

        /// <summary>Starts (or replaces) the kick-off countdown: at zero it ends the warm-up and the match starts.</summary>
        private void SetCountdown(double secs, string why)
        {
            _kickoffDone = false;
            _oneMinuteArmed = false;
            _oneMinuteSent = false;
            _countdownEndsUtc = DateTime.UtcNow.AddSeconds(secs);
            _arena.setTicker(1, _arena.playtimeTickerIdx, (int)(secs * 100), "Time Remaining: ",
                delegate ()
                {
                    _kickoffEnd = true;
                    _kickoffDone = true;
                    _arena.gameEnd();
                });
            Console.WriteLine(String.Format("[MatchAuto] {0}: {1} {2:F0}s.", _arena._name, why, secs));
        }

        /// <summary>ms left on the arena's playtime countdown (ours or a mod's *timer); -1 when none is running.</summary>
        private int CountdownLeftMs()
        {
            Arena.TickerInfo t;
            if (_arena._tickers == null || !_arena._tickers.TryGetValue(_arena.playtimeTickerIdx, out t) || t == null || t.timer == -1) return -1;
            return Math.Max(0, t.timer - Environment.TickCount);
        }

        /// <summary>
        /// Late start (site: hold). While the match is held its countdown never runs out: under 2 minutes it goes back to
        /// setup_min, and nothing is announced. When hold goes back to null, one fresh setup_min countdown; the one-minute
        /// warning and the kick-off then run on it as usual. A match that is never held is untouched.
        /// </summary>
        private void HoldCountdown(MatchQueue.QueuedMatch match)
        {
            if (_paused || match.Status != "scheduled") return;
            if (match.Held)
            {
                bool first = !_holdSeen;
                _holdSeen = true;
                int left = CountdownLeftMs();
                // No countdown running: start one only on the first held poll, unless ours already ran out (kick-off).
                // That covers a zone restart mid-hold; a mod who stops the timer later is not fought.
                if (left >= 0 ? left < HOLD_TOPUP_MS : (first && !_kickoffDone))
                    SetCountdown(match.SetupMin * 60, "held for " + (match.HeldFor ?? "an earlier match") + ", countdown set to");
                return;
            }
            if (!_holdSeen) return;
            _holdSeen = false;
            SetCountdown(match.SetupMin * 60, "hold released, fresh countdown");
        }

        /// <summary>announce.one_minute as an *arena message when the kick-off countdown drops under a minute.</summary>
        private void OneMinuteWarning(MatchQueue.QueuedMatch match)
        {
            if (_kickoffDone || String.IsNullOrWhiteSpace(match.AnnounceOneMinute)) return;
            // Only around kick-off: a mod may re-set the countdown (*timer), but not hours later. A late start moves
            // kick-off to the end of our own last countdown.
            DateTime kickoff = _countdownEndsUtc > match.ScheduledAtUtc ? _countdownEndsUtc : match.ScheduledAtUtc;
            if (kickoff != DateTime.MinValue && DateTime.UtcNow > kickoff.AddMinutes(20)) return;
            Arena.TickerInfo t;
            if (_arena._tickers == null || !_arena._tickers.TryGetValue(_arena.playtimeTickerIdx, out t) || t == null) return;
            int left = t.timer == -1 ? 0 : t.timer - Environment.TickCount;
            if (left <= 0)
            {   // the countdown we watched (ours or a mod's *timer) ran out: kick-off, no more warnings
                if (_oneMinuteArmed || _oneMinuteSent) _kickoffDone = true;
                return;
            }
            if (left > 61000) { _oneMinuteArmed = true; return; }
            if (!_oneMinuteArmed) return;
            _oneMinuteArmed = false;
            _oneMinuteSent = true;
            int bong;
            string text = MatchQueue.SplitBong(match.AnnounceOneMinute, out bong);
            _arena.sendArenaMessage(MatchQueue.SafeChat(text, MatchQueue.CHAT_MAX), bong);
        }

        private bool IsPlaced(Player player, Dictionary<string, MatchQueue.DesiredPlayer> desired)
        {
            MatchQueue.DesiredPlayer d;
            if (!desired.TryGetValue(player._alias, out d)) return player.IsSpectator;
            Team target = MatchQueue.FindTeam(_arena, d.Team);
            if (target == null) return true;   // nothing to aim for
            return d.Spec ? (player.IsSpectator && player._team == target)
                          : (!player.IsSpectator && player._team == target);
        }

        /// <summary>Same order as ScriptArena.handlePlayerJoin(bSpec): Player.LeaveGame event, then the spec.</summary>
        private void SpecViaScript(Player player, Team benchTeam)
        {
            if (!player.IsSpectator && _onLeaveGame != null) _onLeaveGame(player);
            if (benchTeam != null) player.spec(benchTeam); else player.spec();
        }

        /// <summary>Same order as handlePlayerJoin(!bSpec): Player.JoinGame event (it may veto), then the unspec.</summary>
        private void UnspecViaScript(Player player, Team team)
        {
            if (_onJoinGame != null && !_onJoinGame(player)) return;
            player.unspec(team);
            player._lastMovement = Environment.TickCount;
            player._maxTimeCalled = false;
        }

        public void OnGameStart()
        {
            _gameId = null;
            if (_matchId == null) return;
            _gameId = String.Format("{0}_{1}", _arena._name, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            MatchQueue.ReportGame(_matchId, _gameId, false, msg => { });
        }

        /// <param name="decided">False for a game with no winner (a ref's *endgame, a mix restart): not reported as played.</param>
        public void OnGameEnd(bool decided)
        {
            if (_matchId == null || _gameId == null) return;
            if (_kickoffEnd)
            {   // the kick-off countdown ended the warm-up: it never counts and needs no announcement
                _kickoffEnd = false;
                _gameId = null;
                return;
            }
            if (decided)
                MatchQueue.ReportGame(_matchId, _gameId, true, msg => _arena.sendArenaMessage(msg));
            else
                _arena.sendArenaMessage("Game ended with no winner - not reported to freeinf.org.");
            _gameId = null;
        }

        /// <summary>Any powered alias (above a normal player).</summary>
        private static bool CanRef(Player player)
        {
            return player.PermissionLevelLocal > Data.PlayerPermission.Normal;
        }

        /// <summary>
        /// *mvp alias (any powered alias): names the match MVP on freeinf.org after the game (/api/matches/{id}/mvp), then posts the
        /// site's congrats line in the arena. The site checks the name against who played. Until the result is
        /// recorded (about 15s after the game) it answers 409, so that is retried every 10s for a minute.
        /// </summary>
        public void Mvp(Player player, string payload)
        {
            if (!CanRef(player)) { player.sendMessage(-1, "*mvp needs a powered alias."); return; }
            string name = (payload ?? "").Trim();
            if (name.Length == 0) { player.sendMessage(-1, "Usage: *mvp <alias> - after the game, names the match MVP on freeinf.org."); return; }
            string id = _matchId ?? _lastMatchId;
            if (id == null) { player.sendMessage(-1, "This arena hasn't run a freeinf.org league match since the zone started."); return; }
            PostMvp(player, id, name, 0);
        }

        private void PostMvp(Player who, string matchId, string name, int attempt)
        {
            MatchQueue.Post("/api/matches/" + Uri.EscapeDataString(matchId) + "/mvp",
                "{\"player_name\":\"" + MiniJson.Escape(name) + "\"}",
                (code, reply) =>
                {
                    if (code >= 200 && code < 300)
                    {
                        string line = MiniJson.Str(reply, "announce");
                        if (!String.IsNullOrWhiteSpace(line))
                        {
                            int bong;
                            string text = MatchQueue.SplitBong(line, out bong);
                            _arena.sendArenaMessage(MatchQueue.SafeChat(text, MatchQueue.CHAT_MAX), bong);
                        }
                        who.sendMessage(0, "&" + MatchQueue.SafeChat("MVP saved on freeinf.org: " + (MiniJson.Str(reply, "mvp") ?? name) + ".", MatchQueue.CHAT_MAX - 1));
                        return;
                    }
                    string error = MiniJson.Str(reply, "error") ?? (code == 0 ? "could not reach freeinf.org" : "HTTP " + code);
                    if (code == 409 && error.IndexOf("no recorded result", StringComparison.OrdinalIgnoreCase) >= 0 && attempt < 6)
                    {
                        if (attempt == 0) who.sendMessage(0, "The result isn't recorded yet (that takes about 15s after the game). Trying again every 10s...");
                        Task.Delay(10000).ContinueWith(_ => PostMvp(who, matchId, name, attempt + 1));
                        return;
                    }
                    who.sendMessage(-1, MatchQueue.SafeChat("MVP not saved: " + error, MatchQueue.CHAT_MAX));
                });
        }

        /// <summary>
        /// ?side titan | collective: the home captain / co-captain picks the side in game, the same as on the match
        /// page (/api/matches/{id}/side). Picks for this arena's match first, else their next upcoming one.
        /// Returns false when automation is off, so the command falls through.
        /// </summary>
        public bool HandleSideCommand(Player player, string payload)
        {
            if (!MatchQueue.Enabled) return false;
            string w = (payload ?? "").Trim().ToLowerInvariant();
            string side = (w == "t" || w == "titan" || w == "titans") ? "titan"
                        : (w == "c" || w == "col" || w == "collective") ? "collective" : null;

            var match = MatchQueue.Matches
                .Where(m => MatchQueue.Upcoming(m) && m.HomeLeaders.Any(a => a.Equals(player._alias, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.Arena.Equals(_arena._name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(m => m.ScheduledAtUtc)
                .FirstOrDefault();
            if (match == null) { player.sendMessage(-1, "?side is for the home captain or co-captain of an upcoming league match."); return true; }
            if (side == null) { player.sendMessage(-1, "Usage: ?side titan  or  ?side collective   (" + match.Arena + ")"); return true; }

            Player who = player;
            MatchQueue.Post("/api/matches/" + Uri.EscapeDataString(match.Id) + "/side",
                "{\"alias\":\"" + MiniJson.Escape(player._alias) + "\",\"side\":\"" + side + "\"}",
                (code, reply) =>
                {
                    if (code >= 200 && code < 300)
                    {
                        who.sendMessage(0, "&" + MatchQueue.SafeChat(MiniJson.Str(reply, "message") ?? "Side saved on freeinf.org.", MatchQueue.CHAT_MAX - 1));
                        MatchQueue.RefreshSoon();
                        return;
                    }
                    who.sendMessage(-1, MatchQueue.SafeChat("Side not saved: " + (MiniJson.Str(reply, "error") ?? (code == 0 ? "could not reach freeinf.org" : "HTTP " + code)), MatchQueue.CHAT_MAX));
                });
            return true;
        }

        /// <summary>*matchauto [on|off|apply|reload|ask] - status, pause/resume, force a full re-apply, re-read config, re-ask plans.</summary>
        public void Command(Player player, string payload)
        {
            string arg = (payload ?? "").Trim().ToLower();
            if (arg == "off") { _paused = true; player.sendMessage(0, "Match automation paused in this arena (lineups won't be touched)."); return; }
            if (arg == "on") { _paused = false; _appliedUpdatedAt = null; player.sendMessage(0, "Match automation resumed; lineups re-apply within a second."); return; }
            if (arg == "apply") { _appliedUpdatedAt = null; player.sendMessage(0, "Lineups re-apply within a second."); return; }
            if (arg == "reload") { MatchQueue.LoadConfig(true); player.sendMessage(0, "Config reloaded: automation " + (MatchQueue.Enabled ? "on" : "off") + "."); return; }
            if (arg == "ask") { _planAsked.Clear(); player.sendMessage(0, "Players who haven't answered their captain's plan will be asked again."); return; }

            if (!MatchQueue.Enabled) { player.sendMessage(0, "Match automation is off (no MatchAutomationConfig.txt or no client_key)."); return; }
            if (MatchQueue.SiteAutomationOff) { player.sendMessage(0, "freeinf.org has zone match automation switched OFF - nothing to do."); return; }
            player.sendMessage(0, String.Format("Queue: {0} match(es), last good fetch {1}{2}.", MatchQueue.Matches.Count,
                MatchQueue.LastFetchOk == DateTime.MinValue ? "never" : ((int)(DateTime.UtcNow - MatchQueue.LastFetchOk).TotalSeconds) + "s ago",
                MatchQueue.LastError != null ? ", last error: " + MatchQueue.LastError : ""));
            foreach (var m in MatchQueue.Matches)
                player.sendMessage(0, MatchQueue.SafeChat(String.Format("~ {0} ({1}, starts in {2:F0} min{3})", m.Arena, m.Status, m.StartsInMin,
                    m.Held ? ", HELD for " + (m.HeldFor ?? "an earlier match") : ""), MatchQueue.CHAT_MAX));

            var match = MatchQueue.FindByArena(_arena._name);
            if (match == null) { player.sendMessage(0, "This arena is not a queued match."); return; }
            player.sendMessage(0, String.Format("This arena: side released {0}, ready {1}, {2} players listed{3}{4}{5}.",
                match.SideReleased ? "yes" : "no", match.ClientReady ? "yes" : "no", match.Players.Count,
                _paused ? ", PAUSED" : "", _gameId != null ? ", game " + _gameId : "",
                _held.Count > 0 ? ", holding " + String.Join(", ", _held.Keys.ToArray()) + " (flag)" : ""));
        }
    }

    /// <summary>
    /// Just enough JSON reading for the zone queue: objects → Dictionary, arrays → List, numbers → double.
    /// The CTF scripts carry no JSON library.
    /// </summary>
    static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            return v;
        }

        public static string Str(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v as string : null; }
        public static bool Bool(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v is bool && (bool)v; }
        public static double Num(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v is double ? (double)v : 0; }
        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }
        public static List<object> Arr(Dictionary<string, object> o, string k) { object v; return (o != null && o.TryGetValue(k, out v) ? v as List<object> : null) ?? new List<object>(); }

        public static string Escape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var o = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("expected : at " + i);
                    i++;
                    o[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("expected , or } at " + i);
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("expected , or ] at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (String.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (String.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (String.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }

            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("unexpected character at " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;   // \" \\ \/
                }
            }
            throw new FormatException("unterminated string");
        }
    }
}

/* PARSER GUARD - keep this block comment as the LAST thing in the file.
   ZoneServer's ScriptParser.findCommentsAndStrings uses Int16.MaxValue (32767) as its "no match" value when it
   chooses between the next // and the next /* comment. In a file longer than 32767 characters with no later
   block comment, every // comment past that offset is silently skipped, the quote pairing goes out of step
   and the zone fails to start with an ArgumentOutOfRangeException in ScriptParser.cs. A block comment AFTER
   all the // comments keeps the search honest. (Hit on 2026-10-02 when this file grew past 32 KB.) */
