using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// The part of a live snapshot that differs between game types. Script_CTF and Script_USL each
    /// implement this; LiveSnapshotPublisher is otherwise identical in both script folders.
    /// </summary>
    public interface ILiveSnapshotHost
    {
        /// <summary>"usl" or "ctf" - the site keys arenas by game + zone + arena.</summary>
        string GameKey { get; }
        /// <summary>The map in play: USL sub-map key, CTF base name, or the level file without extension.</summary>
        string MapKey { get; }
        /// <summary>The class a player is playing (spectators keep their last class).</summary>
        string ClassOf(Player p);
        /// <summary>"T", "C", "spec", "np" or "other" - decided by each script's own team-name rule.</summary>
        string SideOf(Team t);
        /// <summary>Captain of a mix / OvD that is drafting or running.</summary>
        bool IsCaptain(Player p);
        /// <summary>Fill the headline game state (mode, clock, score).</summary>
        void FillState(LiveSnapshotState state, int now);
        /// <summary>Append zero or more extra top-level members, each as "key":value followed by a comma.</summary>
        void AppendExtras(StringBuilder sb, int now);
        /// <summary>The player whose point of view resolves per-player tickers (a spectator, ideally).</summary>
        Player TickerViewpoint();
    }

    public class LiveSnapshotState
    {
        public bool Running;
        public string Mode = "idle";      // idle | pub | mix | mix2 | draft | ovd | tt
        public string Phase;              // mix phase name when relevant
        public string Label;              // one-line human headline
        public int TimeLeftMs = -1;       // -1 = no clock
        public int ClockTotalMs = -1;
        public int ElapsedMs = -1;
        public string WinningTeam;        // CTF: the team holding the flags
        public int VictoryInMs = -1;      // CTF: countdown to a flag win
        public List<LiveScoreEntry> Score = new List<LiveScoreEntry>();
    }

    public class LiveScoreEntry
    {
        public string Team;
        public string Side;
        public int Kills;
    }

    /// <summary>What a mix / OvD session reports about itself. Null members are omitted from the JSON.</summary>
    public class LiveMixInfo
    {
        public string Label;          // "MIX", "MIX 2", "OvD"
        public string Phase;          // CaptainSignup | BaseSelect | Picking | Countdown | Running | Intermission
        public int TeamSize;
        public bool Rated;
        public string[] Captains;     // slot 0 / slot 1, null when unfilled
        public string Turn;           // captain whose pick it is
        public List<string> Pool;     // draftable aliases while drafting
        public string[] TeamNames;
        public int[] Kills;
        public int RemainingMs;
        public int ClockTotalMs;
        public int PhaseRemainingMs;
        public string Base;           // CTF OvD base
        public string Offense;        // CTF OvD
        public string Defense;
    }

    /// <summary>
    /// Live arena snapshot -> freeinf.org. Once a minute (config: live_interval_s, min 30) it POSTs the
    /// player list grouped by team, each player's class, who is spectating, the game clock/score and
    /// the ticker "bubbles" exactly as a spectator sees them. The site keeps only the latest snapshot
    /// per arena and drops it after a few minutes of silence, so a missed send costs nothing - there
    /// are no retries, the next tick supersedes.
    ///
    /// Config (key=value lines, '#' comments). USL reads the existing MixStatsConfig.txt so the shared
    /// api_key is reused; CTF has its own LiveSnapshotConfig.txt. Keys:
    ///   api_key=...             REQUIRED - the site's USL_MIX_INGEST_KEY (same secret as game ingest)
    ///   live_enabled=true       master switch (a shared file ignores the bare 'enabled' key)
    ///   live_interval_s=60      seconds between heartbeat snapshots (30..600)
    ///   live_change_send=true   also post as soon as something visible changes (ticker, timer, join/leave/team/class)
    ///   live_change_gap_s=10    never more than one change-triggered post per this many seconds (5..600)
    ///   live_endpoint=https://www.freeinf.org/api/live/ingest   explicit target
    ///   endpoint=https://.../api/usl-mix/ingest   (shared file) the base is reused for the live URL
    ///   zone_name=...           label override (defaults to server.xml zoneName)
    /// Runtime: *live status | on | off | now | reload | interval &lt;s&gt;
    /// </summary>
    public class LiveSnapshotPublisher
    {
        public const string SCRIPT_VERSION = "1.2.0";
        public const int SCHEMA_VERSION = 1;

        private const string DEFAULT_ENDPOINT = "https://www.freeinf.org/api/live/ingest";
        private const int DEFAULT_INTERVAL_S = 60;
        private const int MIN_INTERVAL_S = 30;
        private const int MAX_INTERVAL_S = 600;
        private const int FIRST_SEND_DELAY_MS = 8000;
        private const int HTTP_TIMEOUT_SECONDS = 15;
        private const int MAX_POOL = 48;
        private const int DEFAULT_CHANGE_GAP_S = 10;     // a visible change posts at once, but never more often than this
        private const int MIN_CHANGE_GAP_S = 5;
        private const int CHANGE_CHECK_MS = 1000;

        private static readonly HttpClient _http = new HttpClient() { Timeout = TimeSpan.FromSeconds(HTTP_TIMEOUT_SECONDS) };

        private readonly Arena _arena;
        private readonly CfgInfo _config;
        private readonly ILiveSnapshotHost _host;
        private readonly string _configPath;
        private readonly string _basePathFile;   // optional first-line base URL override (CTF's StatsEndpoint.txt)
        private readonly bool _sharedConfig;     // true = the file also configures something else (MixStats)

        private bool _enabled = true;
        private int _intervalMs = DEFAULT_INTERVAL_S * 1000;
        private string _endpoint = DEFAULT_ENDPOINT;
        private bool _endpointExplicit;
        private string _apiKey;
        private string _zoneName;

        private int _nextSendAt;                 // tick-wrap safe deadline; 0 = not armed yet
        private bool _changeSend = true;         // post early when something visible changes
        private int _changeGapMs = DEFAULT_CHANGE_GAP_S * 1000;
        private int _nextChangeCheckAt;
        private int _lastSendAt;
        private string _lastFingerprint;
        private volatile bool _sending;
        private bool _warnedNoKey;
        private readonly object _statusLock = new object();
        private string _lastStatus = "never sent";
        private DateTime _lastAt = DateTime.MinValue;
        private int _sent, _failed;

        public bool Enabled { get { return _enabled; } }

        public LiveSnapshotPublisher(Arena arena, CfgInfo config, ILiveSnapshotHost host, string configPath, string basePathFile, bool sharedConfig)
        {
            _arena = arena;
            _config = config;
            _host = host;
            _configPath = configPath;
            _basePathFile = basePathFile;
            _sharedConfig = sharedConfig;
            LoadConfig(false);
        }

        #region Config

        public void LoadConfig(bool announce)
        {
            _enabled = true;
            _intervalMs = DEFAULT_INTERVAL_S * 1000;
            _endpoint = DEFAULT_ENDPOINT;
            _endpointExplicit = false;
            _apiKey = null;
            _zoneName = null;
            string derivedBase = null;
            try
            {
                if (_basePathFile != null && File.Exists(_basePathFile))
                {
                    foreach (string raw in File.ReadAllLines(_basePathFile))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        derivedBase = line.TrimEnd('/');
                        break;
                    }
                }
                if (File.Exists(_configPath))
                {
                    foreach (string raw in File.ReadAllLines(_configPath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int hash = line.IndexOf('#');
                        if (hash > 0) line = line.Substring(0, hash).Trim();   // trailing comments
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();
                        switch (key)
                        {
                            case "api_key": _apiKey = val.Length > 0 ? val : null; break;
                            case "zone_name": _zoneName = val.Length > 0 ? val : null; break;
                            case "live_enabled": _enabled = ParseBool(val, true); break;
                            case "enabled": if (!_sharedConfig) _enabled = ParseBool(val, true); break;
                            case "live_interval_s": SetInterval(val); break;
                            case "live_change_send": _changeSend = ParseBool(val, true); break;
                            case "live_change_gap_s": SetChangeGap(val); break;
                            case "interval_s": if (!_sharedConfig) SetInterval(val); break;
                            case "live_endpoint": if (val.Length > 0) { _endpoint = val; _endpointExplicit = true; } break;
                            case "endpoint":
                                if (val.Length > 0)
                                {
                                    if (_sharedConfig)
                                    {   // MixStats' ingest URL: keep its host so a dev override carries over
                                        int cut = val.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
                                        if (cut > 0) derivedBase = val.Substring(0, cut);
                                    }
                                    else { _endpoint = val; _endpointExplicit = true; }
                                }
                                break;
                        }
                    }
                }
                else
                    Console.WriteLine("[LiveSnap] No " + _configPath + " - snapshots disabled (no api_key).");
                if (!_endpointExplicit && !String.IsNullOrEmpty(derivedBase))
                    _endpoint = derivedBase + "/api/live/ingest";
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LiveSnap] Config load error: " + ex.Message);
            }
            _nextSendAt = 0;
            _warnedNoKey = false;
            Console.WriteLine(String.Format("[LiveSnap] enabled={0} interval={1}s endpoint={2} key={3} zone={4}",
                _enabled, _intervalMs / 1000, _endpoint, _apiKey == null ? "MISSING" : "set", ZoneName));
            if (announce)
                _arena.sendArenaMessage("&Live snapshot config reloaded.");
        }

        private void SetInterval(string val)
        {
            int s;
            if (!int.TryParse(val, out s)) return;
            if (s < MIN_INTERVAL_S) s = MIN_INTERVAL_S;
            if (s > MAX_INTERVAL_S) s = MAX_INTERVAL_S;
            _intervalMs = s * 1000;
        }

        private void SetChangeGap(string val)
        {
            int s;
            if (!int.TryParse(val, out s)) return;
            if (s < MIN_CHANGE_GAP_S) s = MIN_CHANGE_GAP_S;
            if (s > MAX_INTERVAL_S) s = MAX_INTERVAL_S;
            _changeGapMs = s * 1000;
        }

        private static bool ParseBool(string v, bool dflt)
        {
            v = (v ?? "").Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "on" || v == "yes") return true;
            if (v == "0" || v == "false" || v == "off" || v == "no") return false;
            return dflt;
        }

        private string ZoneName
        {
            get
            {
                if (_zoneName != null) return _zoneName;
                try { return _arena._server.Name; } catch { return _host.GameKey.ToUpperInvariant(); }
            }
        }

        #endregion

        #region Poll / send

        /// <summary>
        /// Call from the script's poll. The heartbeat fires every live_interval_s; between heartbeats a
        /// once-a-second fingerprint of what the site would show (ticker texts, running timers, who is
        /// on which team, spectating, classes) posts early when it changes - a *timer, a new bubble, a
        /// join or leave - rate-limited to one early post per live_change_gap_s.
        /// </summary>
        public void Poll(int now)
        {
            if (!_enabled) return;
            if (IsPrivateArena()) return;   // "#name" arenas are private - never listed on the site
            if (_nextSendAt == 0) { _nextSendAt = now + FIRST_SEND_DELAY_MS; return; }
            bool due = now - _nextSendAt >= 0;
            if (!due && _changeSend && now - _nextChangeCheckAt >= 0)
            {
                _nextChangeCheckAt = now + CHANGE_CHECK_MS;
                if (_lastFingerprint != null && now - _lastSendAt >= _changeGapMs)
                {
                    string fp = null;
                    try { fp = Fingerprint(now); } catch { }
                    if (fp != null && fp != _lastFingerprint) due = true;
                }
            }
            if (!due) return;
            _nextSendAt = now + _intervalMs;
            SendNow(null);
        }

        /// <summary>
        /// Cheap summary of everything the site renders from a snapshot, minus the values that change
        /// every second on their own (countdowns, kills). Same input -> same string.
        /// </summary>
        private string Fingerprint(int now)
        {
            StringBuilder fb = new StringBuilder(512);
            Player view = null;
            try { view = _host.TickerViewpoint(); } catch { }
            if (_arena._tickers != null)
            {
                for (int idx = 0; idx <= 4; idx++)
                {
                    Arena.TickerInfo t;
                    if (!_arena._tickers.TryGetValue(idx, out t) || t == null) continue;
                    string text = t.message;
                    if (t.customTicker != null)
                    {
                        text = null;
                        if (view != null) { try { text = t.customTicker(view); } catch { text = null; } }
                    }
                    int remainingCs = t.timer == -1 ? 0 : (t.timer - now) / 10;
                    if (remainingCs < 0) remainingCs = 0;
                    NormalizeClockText(ref text, ref remainingCs);
                    fb.Append(idx).Append('=').Append(text ?? "").Append('|').Append(remainingCs > 0 ? "T" : "-").Append(';');
                }
            }
            List<string> rows = new List<string>();
            foreach (Player p in _arena.Players)
            {
                if (p == null || p._bIsStealth || p._alias == null) continue;
                rows.Add(p._alias + "|" + (p._team != null ? p._team._id.ToString() : "?") + "|" + (p.IsSpectator ? "s" : "p") + "|" + SafeClass(p));
            }
            rows.Sort(StringComparer.Ordinal);
            foreach (string r in rows) fb.Append(r).Append(';');
            return fb.ToString();
        }

        /// <summary>
        /// A script that writes the clock INTO the ticker text ("Time Left: 13:04", re-set every second) gives
        /// the site nothing to count down and a text that never stops changing. Peel the m:ss off the end into
        /// the countdown field so the site runs it locally, exactly like an engine-timed ticker.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex ClockTail = new System.Text.RegularExpressions.Regex(@"\s*(\d{1,3}):(\d{2})\s*$");
        private static void NormalizeClockText(ref string text, ref int remainingCs)
        {
            if (remainingCs > 0 || string.IsNullOrEmpty(text)) return;
            System.Text.RegularExpressions.Match m = ClockTail.Match(text);
            if (!m.Success) return;
            int mins, secs;
            if (!int.TryParse(m.Groups[1].Value, out mins) || !int.TryParse(m.Groups[2].Value, out secs)) return;
            remainingCs = (mins * 60 + secs) * 100;
            text = text.Substring(0, m.Index).TrimEnd() + " ";
        }

        /// <summary>
        /// A player is leaving this arena. Post right away so the site never shows them in two arenas at
        /// once: the next poll posts regardless of the change gap, and if they are the last one out the
        /// post goes NOW without them, because an emptied arena can be torn down before another poll runs.
        /// </summary>
        public void NotifyLeave(Player leaving)
        {
            if (!_enabled || _apiKey == null) return;
            _nextSendAt = Environment.TickCount;
            if (_arena.TotalPlayerCount <= 1)
                SendNow(null, leaving);
        }

        /// <summary>Build the snapshot on the game thread and POST it off-thread. Single flight.</summary>
        public void SendNow(Player requester)
        {
            SendNow(requester, null);
        }

        private bool IsPrivateArena()
        {
            return _arena != null && _arena._name != null && _arena._name.StartsWith("#");
        }

        private void SendNow(Player requester, Player exclude)
        {
            if (IsPrivateArena())
            {
                if (requester != null) requester.sendMessage(-1, "Private (#) arenas are not reported to the site.");
                return;
            }
            if (_apiKey == null)
            {
                if (!_warnedNoKey)
                {
                    Console.WriteLine("[LiveSnap] No api_key in " + _configPath + " - not sending.");
                    _warnedNoKey = true;
                }
                if (requester != null) requester.sendMessage(-1, "No api_key in " + _configPath + " - nothing sent.");
                return;
            }
            if (_sending)
            {
                if (requester != null) requester.sendMessage(-1, "A snapshot is still being sent - try again in a moment.");
                return;
            }
            string json;
            try
            {
                json = Build(Environment.TickCount, exclude);
            }
            catch (Exception ex)
            {
                SetStatus("build error: " + ex.Message);
                Console.WriteLine("[LiveSnap] Build error: " + ex);
                if (requester != null) requester.sendMessage(-1, "Snapshot build failed: " + ex.Message);
                return;
            }
            _sending = true;
            _lastSendAt = Environment.TickCount;
            try { _lastFingerprint = Fingerprint(_lastSendAt); } catch { _lastFingerprint = null; }
            Player who = requester;
            Task.Run(async () =>
            {
                try
                {
                    string result = await PostOnce(json);
                    _sent++;
                    SetStatus("OK " + result);
                    if (who != null) who.sendMessage(0, "Live snapshot sent: " + result);
                }
                catch (Exception ex)
                {
                    _failed++;
                    SetStatus("FAILED " + ex.Message);
                    Console.WriteLine("[LiveSnap] Send failed: " + ex.Message);
                    if (who != null) who.sendMessage(-1, "Live snapshot failed: " + ex.Message);
                }
                finally
                {
                    _sending = false;
                }
            });
        }

        private async Task<string> PostOnce(string json)
        {
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "LiveSnapshot/" + SCRIPT_VERSION + " (" + _host.GameKey + ")");
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
            HttpResponseMessage resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception(String.Format("HTTP {0} {1}", (int)resp.StatusCode, Truncate(body, 200)));
            return Truncate(body, 120);
        }

        private void SetStatus(string s)
        {
            lock (_statusLock) { _lastStatus = s; _lastAt = DateTime.UtcNow; }
        }

        private static string Truncate(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", "").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        #endregion

        #region Snapshot

        private static int SideRank(string side)
        {
            switch (side)
            {
                case "T": return 0;
                case "C": return 1;
                case "np": return 3;
                case "spec": return 4;
                default: return 2;
            }
        }

        /// <summary>The whole snapshot as JSON. Runs on the game thread; keep it allocation-light.</summary>
        public string Build(int now)
        {
            return Build(now, null);
        }

        /// <param name="exclude">A player on the way out of the arena, left out of the roster.</param>
        private string Build(int now, Player exclude)
        {
            StringBuilder sb = new StringBuilder(12 * 1024);
            sb.Append('{');
            Kv(sb, "auth_key", _apiKey);
            Kv(sb, "schema_version", SCHEMA_VERSION);
            Kv(sb, "script_version", SCRIPT_VERSION);
            Kv(sb, "game", _host.GameKey);
            Kv(sb, "zone", ZoneName);
            Kv(sb, "arena", _arena._name);
            Kv(sb, "level", _config.level.lvlFile);
            Kv(sb, "map", _host.MapKey);
            Kv(sb, "timestamp", DateTime.UtcNow.ToString("o"));
            Kv(sb, "interval_s", _intervalMs / 1000);

            // Stealthed mods are hidden from the in-game list; keep them off the site too.
            List<Player> players = _arena.Players.Where(p => p != null && p != exclude && !p._bIsStealth && p._alias != null).ToList();
            int playing = players.Count(p => !p.IsSpectator);
            Kv(sb, "players_total", players.Count);
            Kv(sb, "players_playing", playing);
            Kv(sb, "players_spectating", players.Count - playing);

            // Headline state
            LiveSnapshotState st = new LiveSnapshotState();
            _host.FillState(st, now);
            sb.Append("\"state\":{");
            Kv(sb, "running", st.Running);
            Kv(sb, "mode", st.Mode);
            if (st.Phase != null) Kv(sb, "phase", st.Phase);
            if (st.Label != null) Kv(sb, "label", st.Label);
            if (st.TimeLeftMs >= 0) Kv(sb, "time_left_ms", st.TimeLeftMs); else KvNull(sb, "time_left_ms");
            if (st.ClockTotalMs >= 0) Kv(sb, "clock_total_ms", st.ClockTotalMs); else KvNull(sb, "clock_total_ms");
            if (st.ElapsedMs >= 0) Kv(sb, "elapsed_ms", st.ElapsedMs); else KvNull(sb, "elapsed_ms");
            if (st.WinningTeam != null) Kv(sb, "winning_team", st.WinningTeam);
            if (st.VictoryInMs >= 0) Kv(sb, "victory_in_ms", st.VictoryInMs);
            sb.Append("\"score\":[");
            foreach (LiveScoreEntry e in st.Score)
            {
                sb.Append('{');
                Kv(sb, "team", e.Team);
                Kv(sb, "side", e.Side);
                Kv(sb, "kills", e.Kills);
                CloseObj(sb);
                sb.Append(',');
            }
            CloseArr(sb);
            sb.Append(',');
            CloseObj(sb);
            sb.Append(',');

            // Tickers as a spectator sees them (per-player tickers resolved for that viewpoint)
            Player view = null;
            try { view = _host.TickerViewpoint(); } catch { }
            sb.Append("\"tickers\":[");
            if (_arena._tickers != null)
            {
                for (int idx = 0; idx <= 4; idx++)
                {
                    Arena.TickerInfo t;
                    if (!_arena._tickers.TryGetValue(idx, out t) || t == null) continue;
                    string text = t.message;
                    if (t.customTicker != null)
                    {
                        text = null;
                        if (view != null)
                        {
                            try { text = t.customTicker(view); } catch { text = null; }
                        }
                    }
                    int remainingCs = t.timer == -1 ? 0 : (t.timer - now) / 10;
                    if (remainingCs < 0) remainingCs = 0;
                    NormalizeClockText(ref text, ref remainingCs);
                    if (String.IsNullOrEmpty(text) && remainingCs == 0) continue;
                    sb.Append('{');
                    Kv(sb, "idx", idx);
                    Kv(sb, "colour", (int)t.colour);
                    Kv(sb, "text", text ?? "");
                    Kv(sb, "remaining_cs", remainingCs);
                    CloseObj(sb);
                    sb.Append(',');
                }
            }
            CloseArr(sb);
            sb.Append(',');

            // Teams, in the order the in-game list reads: Titan side, Collective side, other playing
            // teams, then np, then spec. Only teams that have someone on them.
            var groups = players.GroupBy(p => p._team)
                .Select(g => new { Team = g.Key, Side = g.Key != null ? _host.SideOf(g.Key) : "spec", Members = g.ToList() })
                .OrderBy(g => SideRank(g.Side))
                .ThenBy(g => g.Team != null ? g.Team._id : (short)9999)
                .ToList();
            sb.Append("\"teams\":[");
            foreach (var g in groups)
            {
                sb.Append('{');
                Kv(sb, "name", g.Team != null ? g.Team._name : "spec");
                Kv(sb, "side", g.Side);
                Kv(sb, "kills", g.Team != null ? g.Team._currentGameKills : 0);
                Kv(sb, "deaths", g.Team != null ? g.Team._currentGameDeaths : 0);
                sb.Append("\"players\":[");
                foreach (Player p in g.Members
                    .OrderByDescending(p => SafeIsCaptain(p) ? 1 : 0)
                    .ThenBy(p => p._alias, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append('{');
                    Kv(sb, "alias", p._alias);
                    Kv(sb, "class", SafeClass(p));
                    Kv(sb, "spec", p.IsSpectator);
                    Kv(sb, "dead", !p.IsSpectator && p.IsDead);
                    if (SafeIsCaptain(p)) Kv(sb, "captain", true);
                    if (!String.IsNullOrEmpty(p._squad)) Kv(sb, "squad", p._squad);
                    CloseObj(sb);
                    sb.Append(',');
                }
                CloseArr(sb);
                CloseObj(sb);
                sb.Append(',');
            }
            CloseArr(sb);
            sb.Append(',');

            try { _host.AppendExtras(sb, now); } catch (Exception ex) { Console.WriteLine("[LiveSnap] extras: " + ex.Message); }

            CloseObj(sb);
            return sb.ToString();
        }

        private string SafeClass(Player p)
        {
            try { return _host.ClassOf(p) ?? "Unknown"; } catch { return "Unknown"; }
        }

        private bool SafeIsCaptain(Player p)
        {
            try { return _host.IsCaptain(p); } catch { return false; }
        }

        /// <summary>
        /// The class a player is actually playing: lowest SkillId among "Classes*" skills whose default
        /// vehicle matches the base vehicle, else any class skill, else any skill. Never depends on the
        /// dictionary's hash order (a ref or a ?swap recipient holds more than one skill).
        /// </summary>
        public static string ResolveClassName(Player player)
        {
            if (player == null || player._skills == null || player._skills.Count == 0) return "Unknown";
            int vehicleId = -1;
            if (player._baseVehicle != null && player._baseVehicle._type != null)
                vehicleId = player._baseVehicle._type.Id;
            SkillInfo byVehicle = null, anyClass = null, anySkill = null;
            foreach (Player.SkillItem item in player._skills.Values)
            {
                SkillInfo skill = item != null ? item.skill : null;
                if (skill == null) continue;
                if (anySkill == null || skill.SkillId < anySkill.SkillId) anySkill = skill;
                bool isClass = skill.Category != null && skill.Category.StartsWith("Classes", StringComparison.OrdinalIgnoreCase);
                if (!isClass) continue;
                if (anyClass == null || skill.SkillId < anyClass.SkillId) anyClass = skill;
                if (vehicleId >= 0 && skill.DefaultVehicleId == vehicleId && (byVehicle == null || skill.SkillId < byVehicle.SkillId))
                    byVehicle = skill;
            }
            SkillInfo pick = byVehicle ?? anyClass ?? anySkill;
            return pick != null && pick.Name != null ? pick.Name : "Unknown";
        }

        /// <summary>"mix":{...} member for a session (no trailing comma).</summary>
        public static void AppendMix(StringBuilder sb, LiveMixInfo m)
        {
            sb.Append('{');
            Kv(sb, "label", m.Label);
            Kv(sb, "phase", m.Phase);
            Kv(sb, "team_size", m.TeamSize);
            Kv(sb, "rated", m.Rated);
            sb.Append("\"captains\":[");
            if (m.Captains != null)
                for (int i = 0; i < m.Captains.Length; i++) { sb.Append(m.Captains[i] == null ? "null" : Q(m.Captains[i])); sb.Append(','); }
            CloseArr(sb);
            sb.Append(',');
            if (m.Turn != null) Kv(sb, "turn", m.Turn);
            if (m.Pool != null)
            {
                sb.Append("\"pool\":[");
                foreach (string a in m.Pool.Take(MAX_POOL)) { sb.Append(Q(a)); sb.Append(','); }
                CloseArr(sb);
                sb.Append(',');
            }
            sb.Append("\"teams\":[");
            if (m.TeamNames != null)
                for (int i = 0; i < m.TeamNames.Length; i++) { sb.Append(m.TeamNames[i] == null ? "null" : Q(m.TeamNames[i])); sb.Append(','); }
            CloseArr(sb);
            sb.Append(',');
            sb.Append("\"kills\":[");
            if (m.Kills != null)
                for (int i = 0; i < m.Kills.Length; i++) { sb.Append(m.Kills[i].ToString(CultureInfo.InvariantCulture)); sb.Append(','); }
            CloseArr(sb);
            sb.Append(',');
            Kv(sb, "remaining_ms", m.RemainingMs);
            Kv(sb, "clock_total_ms", m.ClockTotalMs);
            Kv(sb, "phase_remaining_ms", m.PhaseRemainingMs);
            if (m.Base != null) Kv(sb, "base", m.Base);
            if (m.Offense != null) Kv(sb, "offense", m.Offense);
            if (m.Defense != null) Kv(sb, "defense", m.Defense);
            CloseObj(sb);
        }

        /// <summary>"tt":{...} member for a Triple Threat side game (no trailing comma).</summary>
        public static void AppendTT(StringBuilder sb, int round, List<string> teamNames, List<int> wins, List<string> members)
        {
            sb.Append('{');
            Kv(sb, "round", round);
            sb.Append("\"teams\":[");
            if (teamNames != null) foreach (string n in teamNames) { sb.Append(n == null ? "null" : Q(n)); sb.Append(','); }
            CloseArr(sb);
            sb.Append(',');
            sb.Append("\"wins\":[");
            if (wins != null) foreach (int w in wins) { sb.Append(w.ToString(CultureInfo.InvariantCulture)); sb.Append(','); }
            CloseArr(sb);
            sb.Append(',');
            sb.Append("\"members\":[");
            if (members != null) foreach (string m in members) { sb.Append(Q(m)); sb.Append(','); }
            CloseArr(sb);
            CloseObj(sb);
        }

        /// <summary>"flags":[...] member (CTF): rows of { name, team, carrier } (no trailing comma).</summary>
        public static void AppendFlags(StringBuilder sb, List<string[]> rows)
        {
            sb.Append('[');
            if (rows != null)
                foreach (string[] r in rows)
                {
                    if (r == null || r.Length < 3) continue;
                    sb.Append('{');
                    Kv(sb, "name", r[0]);
                    Kv(sb, "team", r[1]);
                    Kv(sb, "carrier", r[2]);
                    CloseObj(sb);
                    sb.Append(',');
                }
            CloseArr(sb);
        }

        #endregion

        #region Mod command

        /// <summary>*live [status|on|off|now|reload|interval &lt;s&gt;]</summary>
        public bool HandleModCommand(Player player, string payload)
        {
            string p = (payload ?? "").Trim();
            string cmd = p.Split(' ')[0].ToLowerInvariant();
            string rest = p.Length > cmd.Length ? p.Substring(cmd.Length).Trim() : "";
            switch (cmd)
            {
                case "":
                case "status":
                {
                    string status; DateTime at;
                    lock (_statusLock) { status = _lastStatus; at = _lastAt; }
                    player.sendMessage(0, String.Format("LiveSnap v{0}: {1}, heartbeat {2}s, on change {3}, {4} sent / {5} failed", SCRIPT_VERSION,
                        _enabled ? "ENABLED" : "DISABLED", _intervalMs / 1000, _changeSend ? "every " + (_changeGapMs / 1000) + "s max" : "off", _sent, _failed));
                    player.sendMessage(0, String.Format("Endpoint: {0} | api_key: {1} | zone: {2}", _endpoint, _apiKey == null ? "MISSING" : "set", ZoneName));
                    player.sendMessage(0, String.Format("Last: {0}{1}", status, at == DateTime.MinValue ? "" : " (" + (int)(DateTime.UtcNow - at).TotalSeconds + "s ago)"));
                    player.sendMessage(0, "*live now | on | off | reload | interval <30-600> | preview");
                    return true;
                }
                case "on":
                    _enabled = true;
                    _nextSendAt = 0;
                    player.sendMessage(0, "Live snapshots ENABLED.");
                    return true;
                case "off":
                    _enabled = false;
                    player.sendMessage(0, "Live snapshots DISABLED (edit the config file to persist).");
                    return true;
                case "now":
                    player.sendMessage(0, "Sending a snapshot to " + _endpoint + " ...");
                    SendNow(player);
                    return true;
                case "reload":
                    LoadConfig(true);
                    return true;
                case "interval":
                    SetInterval(rest);
                    player.sendMessage(0, String.Format("Live snapshot interval: {0}s (in memory; live_interval_s in the config file persists it).", _intervalMs / 1000));
                    return true;
                case "preview":
                {   // the headline + team summary the site will receive, without sending
                    try
                    {
                        LiveSnapshotState st = new LiveSnapshotState();
                        int now = Environment.TickCount;
                        _host.FillState(st, now);
                        player.sendMessage(0, String.Format("state: mode={0} phase={1} label={2} left={3}s score={4}", st.Mode, st.Phase ?? "-", st.Label ?? "-",
                            st.TimeLeftMs >= 0 ? (st.TimeLeftMs / 1000).ToString() : "-",
                            String.Join(" | ", st.Score.Select(s => s.Team + "=" + s.Kills).ToArray())));
                        string json = Build(now);
                        player.sendMessage(0, String.Format("snapshot: {0} bytes, {1} players", json.Length, _arena.TotalPlayerCount));
                    }
                    catch (Exception ex) { player.sendMessage(-1, "Preview failed: " + ex.Message); }
                    return true;
                }
                default:
                    player.sendMessage(-1, "Unknown *live subcommand. *live status | now | on | off | reload | interval <s> | preview");
                    return true;
            }
        }

        #endregion

        #region JSON helpers (manual, culture-invariant)

        private static string Q(string s)
        {
            if (s == null) return "null";
            StringBuilder b = new StringBuilder(s.Length + 8);
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
            return b.ToString();
        }

        private static void Kv(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":").Append(Q(v)).Append(','); }
        private static void Kv(StringBuilder sb, string k, int v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)).Append(','); }
        private static void Kv(StringBuilder sb, string k, bool v) { sb.Append('"').Append(k).Append("\":").Append(v ? "true" : "false").Append(','); }
        private static void KvNull(StringBuilder sb, string k) { sb.Append('"').Append(k).Append("\":null,"); }

        /// <summary>Drop a trailing comma and close the object.</summary>
        private static void CloseObj(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] == ',') sb.Length--;
            sb.Append('}');
        }

        private static void CloseArr(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] == ',') sb.Length--;
            sb.Append(']');
        }

        #endregion
    }
}
