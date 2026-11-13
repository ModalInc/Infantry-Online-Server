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
    /// CTF mix (pickup game) manager: captain signup, snake-order drafting, a restart countdown,
    /// and a mercy-rule watch on the live game. Ported from the USL MixManager
    /// (scripts/GameTypes/USL/USLMixes.cs), which has been running in the USL zone since 2026-08.
    ///
    /// Attached to Script_CTF with minimal touchpoints:
    ///   init():             _mixManager = new CTFMixManager(arena, CFG, this)
    ///   poll():             _mixManager.Poll(now)
    ///   playerChatCommand:  routed FIRST via HandleChatCommand (consumes only while active)
    ///   playerModCommand:   *mix routed via HandleModCommand
    ///   playerEnterArena:   _mixManager.OnPlayerEnterArena(player)
    ///   playerLeave:        _mixManager.OnPlayerLeave(player)   <-- REQUIRED, see below
    ///
    /// Flow:
    ///   *mix [size] [capt1,capt2] -> captain signup: ?c to volunteer, ?p / ?np to opt in and out.
    ///   Picking                   -> captains are unspecced onto opposing teams. Snake order: the
    ///                                first captain picks 1, then 2 each until both teams are full.
    ///                                ?l lists the numbered pool, ?pick <#|name> (or bare ?<#>) picks.
    ///   Countdown                 -> a 2 minute restart countdown in the bubble ticker (*mix go skips).
    ///   Running                   -> mercy rule (default +30 kills) ends a blowout early.
    ///   Intermission              -> 30s, then captain signup reopens by itself.
    ///
    /// ---------------------------------------------------------------------------------------
    /// DELIBERATE DIVERGENCES FROM THE USL VERSION - each of these is a CTF-specific hazard:
    ///
    /// 1. THE POOL IS AN EXPLICIT OPT-IN SET, CROSS-CHECKED AGAINST TEAM "np".
    ///    CTF does have an np team - TeamInfo38 in ctfpl/ctfdl, and the end-of-game spec-all uses
    ///    `p.spec("np")` (CTF.cs). USL derives its whole pool from "spectators not on np"; this
    ///    manager keeps an explicit set INSTEAD, because the goal here is a queue players can opt
    ///    into from a running pub game rather than a snapshot of who happens to be spectating.
    ///    Team np is still honoured: `?np` moves you there and `EligiblePool` excludes anyone on it,
    ///    so `?team np` and the end-of-game spec-all both do the right thing.
    ///
    /// 2. NO CUSTOM ?teamname COMMAND.
    ///    USL guards custom names against the substrings "- t"/"- c"/"titan"/"collective" because
    ///    side detection is name-substring based. CTF's suffix is a bare SPACE plus T/C - CTF.cs
    ///    tests `_team._name.Contains(" T")` in at least eight places (CTF.cs:855, 877, 896, 2855,
    ///    3614, ...). That matches far too easily: "Da Team", "The Titans" and any name whose second
    ///    word starts with T or C would trip it, making isTitan and isCollective true at once and
    ///    corrupting spawn side. Mixes instead draw a random matched pair from the cfg's own
    ///    "<name> T" / "<name> C" teams (MOON, Mir, Juicy Fruit, ... - TeamInfo2..33), which gives the
    ///    varied-uniform effect with zero risk. Revisit only if side detection is made explicit.
    ///
    /// 3. NO IsTitanSideLeft() FIRST-PICK RULE.
    ///    That is a USL megamap concept. First pick here is decided by coin flip and announced.
    ///
    /// 4. OnPlayerLeave MUST BE WIRED UP.
    ///    Every collection here is keyed by ALIAS, not by Player, precisely so a disconnect cannot
    ///    pin a Player object graph - the bug that made CTF's server RAM climb until it died. The one
    ///    unavoidable per-player state (pool membership) is cleaned on leave.
    /// </summary>
    class CTFMixManager
    {
        private enum MixState { Idle, CaptainSignup, BaseSelect, Picking, Countdown, Running }

        private const int CAPTAIN_SIGNUP_TIMEOUT = 60000;  // ?c window before random captains (automation only)
        private const int CAPTAIN_SIGNUP_EXTEND = 30000;   // extension when the pool is too small
        private const int PICK_TURN_TIMEOUT = 30000;       // per turn; refreshed after each pick
        private const int PICK_REMINDER_REPEAT = 60000;    // automation off: public nudge cadence
        private const int RESTART_COUNTDOWN = 120000;      // teams picked -> game restarts (*mix go skips)

        // CTF mixes are 10v10 (5 offense + 5 defense a side) - that is the format the zone runs.
        private const int DEFAULT_TEAM_SIZE = 10;
        private const int OVD_TEAM_SIZE = 5;        // OvD is 5v5 and runs far more often than a 10v10 mix
        // OvD is 5v5 or it is not OvD. The comp does not work short-handed, so a game is never
        // launched below this - the draft holds and asks for more players instead.
        private const int OVD_MIN_TEAM_SIZE = 5;
        private const int MAX_TEAM_SIZE = 15;
        private const int BASE_SELECT_TIMEOUT = 45000;
        private const int OVD_MATCH_MINUTES = 18;   // OvD match length

        // OvD pacing (Travis, 2026-09-12: the automation was "really slow and rough"). A pub OvD
        // has to keep moving, so timer automation is ON by default for OvD - captains drawn, idle
        // picks made and the base auto-chosen on shorter clocks than a mix. Mix keeps the
        // community's no-timer default. "*ovd auto off" turns it off for OvD alone.
        private const int OVD_CAPTAIN_SIGNUP_TIMEOUT = 45000;
        private const int OVD_PICK_TURN_TIMEOUT = 20000;
        private const int OVD_BASE_SELECT_TIMEOUT = 30000;
        private bool _ovdAutoPick = true;
        private bool TimerAutomation { get { return _ovdMode ? _ovdAutoPick : _autoPickEnabled; } }
        private int SignupTimeout { get { return _ovdMode ? OVD_CAPTAIN_SIGNUP_TIMEOUT : CAPTAIN_SIGNUP_TIMEOUT; } }
        private int PickTimeout { get { return _ovdMode ? OVD_PICK_TURN_TIMEOUT : PICK_TURN_TIMEOUT; } }
        private int BaseTimeout { get { return _ovdAutoPick ? OVD_BASE_SELECT_TIMEOUT : BASE_SELECT_TIMEOUT; } }

        /// <summary>Longest alias the bubble ticker and the ?l grid print in full.</summary>
        private const int TICKER_ALIAS_MAX = 10;

        /// <summary>The alias as the bubble / grid shows it: whole, unless longer than TICKER_ALIAS_MAX.</summary>
        private static string TickerAlias(string alias)
        {
            if (String.IsNullOrEmpty(alias))
                return "";
            return alias.Length <= TICKER_ALIAS_MAX ? alias : alias.Substring(0, TICKER_ALIAS_MAX);
        }

        // Which side OWNS each base is fixed by the map, and the owning side is the one that
        // DEFENDS it. Mirrors the flag-ownership branch in Script_CTF.SetupBase.
        private static readonly HashSet<string> TITAN_BASES =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "D7", "F6", "F5" };
        // H4 is deliberately absent - the base is walled off (see EXPERIMENTAL_BASES). SetupBase
        // still knows it as a Collective base for any manual *setup H4.
        private static readonly HashSet<string> COLLECTIVE_BASES =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A7", "A5", "B6", "B8" };

        // Defined in the bases table but NOT playable OvD ground. Hidden from ?base and refused if
        // typed, so a captain cannot strand a match somewhere nobody can reach. A mod can still
        // force one by hand with *setup <base> - this only governs the OvD flow.
        //   F8/A8/A10 - experimental, not easily reachable on the main map
        //   H4        - walled off, no longer usable at all
        private static readonly HashSet<string> EXPERIMENTAL_BASES =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "F8", "A8", "A10", "H4" };

        /// <summary>Bases a captain may actually choose to defend.</summary>
        private List<string> PlayableBases()
        {
            return _script.GetBaseNames().Where(b => !EXPERIMENTAL_BASES.Contains(b)).ToList();
        }

        // OvD only: offense vs defense rather than two symmetric teams.
        private bool _ovdMode;
        private int _defenseSlot;        // which captain slot is DEFENSE
        private string _selectedBase;

        // Randomized public teasing for captains who take too long
        private static readonly string[] SLOWPOKE_LINES = new string[]
        {
            "Hurry up Captain {0}, it's your turn! (?pick <# or name>)",
            "Captain {0} is still deciding... any day now.",
            "{0} is carefully weighing their options. Allegedly.",
            "The suspense is killing us, {0} - ?pick someone!",
            "{0} went to get a snack. Someone poke them.",
            "Paging Captain {0}... your pick is on line 1.",
        };

        private Arena _arena;
        private CfgInfo _config;
        private Script_CTF _script;

        private MixState _state = MixState.Idle;
        private int _teamSize = DEFAULT_TEAM_SIZE;
        private int _phaseDeadline;
        private int _restartDeadline;
        private bool _signupExtendAnnounced;

        // Community default matches USL: NO timer automation. Captains pick at their own pace.
        private bool _autoPickEnabled = false;
        private bool _priorityAutoEnabled = false;

        private string[] _captainAliases = new string[2];
        private Team[] _teams = new Team[2];

        // Seat claims (ported from USL, 2026-09-13): once a player is on a mix / OvD team they keep
        // that seat for the session. A disconnect or arena hop returns them to it 5s after they
        // re-enter, as long as the seat has not been filled meanwhile; a spec while still IN the
        // arena (their own, or a mod's *spec) is a bench, not a drop, and never auto-restores.
        // Alias strings and ints only, never a Player reference, cleared with the session.
        private class SeatClaim
        {
            public string Alias;
            public int TeamSlot;        // index into _teams
            public int RestoreAtTick;   // 0 = nothing pending
            public bool Benched;
        }
        private List<SeatClaim> _seatClaims = new List<SeatClaim>();
        private int[] _seatCount = new int[2];   // roster size per slot at launch; 0 before launch (use _teamSize)
        // Deliberately NOT immediate: a player is still settling when the arena-enter event fires,
        // and a scripted unspec at that instant is exactly what desyncs or drops a client.
        private const int RECONNECT_RESTORE_DELAY = 5000;
        private int _turnCaptain;
        private int _turnPicksLeft;

        // Explicit opt-in pool (see divergence 1). Alias-keyed so a leaver pins nothing.
        private HashSet<string> _pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Everyone who has been drafted this session, so a picked player cannot be picked twice
        // even if something specs them back out mid-draft.
        private HashSet<string> _drafted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Stable pick numbering: an alias keeps its number for the whole session, so "?pick 5"
        // never lands on a different player than the one the captain read off ?l.
        private Dictionary<string, int> _poolNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _nextPoolNumber = 1;

        // Wait ledger: identity is alias OR machine UID triple OR IP, all already in memory on
        // Player - no DB calls (those can block the poll). A fresh alias neither hides nor resets
        // a sit-out streak. Values are copied out; no Player reference is retained.
        private class WaitRecord
        {
            public string Alias;
            public uint Uid1, Uid2, Uid3;
            public string Ip;
            public int Streak;
        }
        private List<WaitRecord> _waitLedger = new List<WaitRecord>();

        // Sit-out block styling for ?l. CTF already speaks &, *, ~, !, # and $; the priority block
        // claims '@'. If it reads wrong in game this is a one-character change.
        private const string PRIORITY_MARK = "@";
        private const string PRIORITY_RULE = "@==============================";
        private const string PRIORITY_GAP = "@ ";
        private const int PRIORITY_URGENT_STREAK = 2;
        private const int PRIORITY_AUTO_STREAK = 3;
        private int[] _priorityTaken = new int[2];

        private const int VOTEOUT_THRESHOLD = 8;
        private HashSet<string> _voteoutBanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<string>> _voteoutVotes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Your-pick chime, per player, persisted (the client cannot store it).
        private const int TURN_BONG_DEFAULT = 2;
        private static readonly string PREFS_PATH = Path.Combine("scripts", "GameTypes", "CTF", "MixPrefs.txt");
        private Dictionary<string, int> _bongPrefs;
        private HashSet<string> _hintedCaptains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Ordered role/build preferences (CTFRoles.cs). Captains draft off these.
        private CTFRolePrefs _roles;
        public CTFRolePrefs Roles { get { return _roles; } }

        public CTFMixManager(Arena arena, CfgInfo config, Script_CTF script)
        {
            _arena = arena;
            _config = config;
            _script = script;
            _roles = new CTFRolePrefs(arena, script);
        }

        /// <summary>True while a mix session owns the arena - the caller should hold off any
        /// automatic game start while this is set.</summary>
        public bool SuppressAutoStart
        {
            get
            {
                if (_state == MixState.Idle)
                    return false;
                // An OvD match runs WITHOUT a gameStart of its own, so the arena's game loop has to
                // stay held down for the whole match - otherwise it would start a game underneath
                // the match and flagReset the base setup away. A mix does restart properly, so once
                // it is Running the normal loop can resume.
                if (_state == MixState.Running)
                    return _ovdMode;
                return true;
            }
        }

        /// <summary>True while teams are being organized. Excludes Running deliberately: during the
        /// live game normal in-game restrictions must still apply.</summary>
        public bool IsOrganizing
        {
            get
            {
                return _state == MixState.CaptainSignup || _state == MixState.BaseSelect
                    || _state == MixState.Picking || _state == MixState.Countdown;
            }
        }

        public bool IsActive { get { return _state != MixState.Idle; } }

        /// <summary>Phase name for the site's live feed (Idle | CaptainSignup | BaseSelect | Picking | Countdown | Running).</summary>
        public string PhaseName { get { return _state.ToString(); } }
        /// <summary>Current captains (slot 0 / slot 1, null when unfilled) - the site marks them in the live player list.</summary>
        public string[] CaptainAliases { get { return _captainAliases; } }

        /// <summary>
        /// What this session tells the site's live snapshot (CTFLiveSnapshot.cs): phase, captains, whose
        /// pick it is, the draftable pool while organizing, team names, the OvD base and sides. Null when idle.
        /// </summary>
        public LiveMixInfo LiveInfo(int now)
        {
            if (_state == MixState.Idle) return null;
            LiveMixInfo i = new LiveMixInfo();
            i.Label = _ovdMode ? "OvD" : "Mix";
            i.Phase = _state.ToString();
            i.TeamSize = _teamSize;
            i.Rated = false;
            i.Captains = new string[] { _captainAliases[0], _captainAliases[1] };
            i.Turn = (_state == MixState.Picking && _turnCaptain >= 0 && _turnCaptain < 2) ? _captainAliases[_turnCaptain] : null;
            i.TeamNames = new string[] { _teams[0] != null ? _teams[0]._name : null, _teams[1] != null ? _teams[1]._name : null };
            i.Kills = new int[] { _teams[0] != null ? _teams[0]._currentGameKills : 0, _teams[1] != null ? _teams[1]._currentGameKills : 0 };
            i.RemainingMs = 0;
            i.ClockTotalMs = 0;
            i.PhaseRemainingMs = (IsOrganizing && _phaseDeadline != 0 && _phaseDeadline - now > 0) ? _phaseDeadline - now : 0;
            if (_ovdMode)
            {
                i.Base = _selectedBase;
                if (_teams[0] != null && _teams[1] != null && _defenseSlot >= 0 && _defenseSlot < 2)
                {
                    i.Defense = _teams[_defenseSlot]._name;
                    i.Offense = _teams[1 - _defenseSlot]._name;
                }
            }
            if (IsOrganizing)
            {
                try
                {
                    i.Pool = EligiblePool().Where(p => p != null && p._alias != null).Select(p => p._alias)
                        .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                }
                catch { i.Pool = null; }
            }
            return i;
        }

        /// <summary>
        /// The mode of the session that most recently RAN ("OvD" or "Mix"), or null if the manager
        /// has not run one. Deliberately survives the reset back to Idle: Game.End fires after the
        /// session has ended, so asking IsActive there always says "no" and the game gets recorded
        /// as a pub game with no offense/defense at all.
        /// </summary>
        public string LastGameMode { get; private set; }

        /// <summary>
        /// True only while LaunchMix() is performing its restart. That restart calls gameEnd() purely
        /// to reset team scores, which fires Game.End and made the stats writer submit a whole junk
        /// row-set for the game that happened BEFORE the mix - and consume the session record, so the
        /// real match then recorded as "Pub". Game.End skips recording while this is set.
        /// </summary>
        public bool SuppressGameEndStats { get; private set; }

        /// <summary>
        /// Name of the team that DEFENDED in that session, or null. This is ground truth - the
        /// manager chose it - so the stats writer can stop guessing sides from summon counts.
        /// </summary>
        public string LastDefenseTeam { get; private set; }

        /// <summary>Name of the team that ATTACKED, so the stats writer can tell a player who was in
        /// the match from one who never got drafted and stayed on a pub team.</summary>
        public string LastOffenseTeam { get; private set; }

        /// <summary>Aliases of the two captains for that session (null entries when *mix fill built the
        /// teams with nobody drafting). Ground truth for the is_captain stat, so the site never has to
        /// infer it from chat.</summary>
        public string[] LastCaptains { get; private set; }

        /// <summary>
        /// Forgets the recorded session. Game.End calls this once it has read the values, so the
        /// record is consumed exactly once - otherwise the NEXT ordinary pub game would inherit
        /// "OvD" and the previous match's defending team, which is worse than guessing.
        /// </summary>
        public void ClearLastSession()
        {
            LastGameMode = null;
            LastDefenseTeam = null;
            LastOffenseTeam = null;
            LastCaptains = null;
            // A hand-hosted game's *ovd captains list has now been read too. Only when nothing is
            // organizing: mid-draft the array belongs to the session that is about to launch.
            if (_state == MixState.Idle)
            {
                _captainAliases[0] = null;
                _captainAliases[1] = null;
            }
        }

        /// <summary>The captains typed with *ovd captains for a game that is NOT being run by the
        /// manager (no *ovd go). Game.End reads this when there is no session record, so a hand-hosted
        /// OvD still gets its is_captain flags. A copy, so the caller cannot alias the working array.</summary>
        public string[] PendingCaptains
        {
            get
            {
                if (_captainAliases[0] == null && _captainAliases[1] == null) return null;
                return new[] { _captainAliases[0], _captainAliases[1] };
            }
        }

        /// <summary>Records what this session is, for Game.End to read once it is over.</summary>
        private void RememberSession()
        {
            LastGameMode = _ovdMode ? "OvD" : "Mix";
            LastDefenseTeam = (_ovdMode && _teams != null && _teams[_defenseSlot] != null)
                ? _teams[_defenseSlot]._name
                : null;
            LastOffenseTeam = (_ovdMode && _teams != null && _teams[1 - _defenseSlot] != null)
                ? _teams[1 - _defenseSlot]._name
                : null;
            // Copy, not alias - StartSignup nulls _captainAliases for the next mix and Game.End
            // may read this after that.
            LastCaptains = new[] { _captainAliases[0], _captainAliases[1] };
        }

        /// <summary>
        /// True from the moment an OvD has a base/teams through the end of the match.
        /// (No longer used to suppress the flag-victory rule - SetupBase now parks the other flags
        /// unowned, which makes that rule unsatisfiable structurally. Kept as a general "is an OvD
        /// live" test.)
        /// </summary>
        public bool IsOvdActive
        {
            get
            {
                return _ovdMode && (_state == MixState.BaseSelect || _state == MixState.Picking
                                 || _state == MixState.Countdown || _state == MixState.Running);
            }
        }

        /// <summary>"MIX" or "OvD" - used in every announcement so players know which they are in.</summary>
        private string ModeName { get { return _ovdMode ? "OvD" : "MIX"; } }

        /// <summary>Role of a captain slot: OvD names them, a mix just has two teams.</summary>
        private string SlotName(int slot)
        {
            if (!_ovdMode)
                return _teams[slot] != null ? _teams[slot]._name : ("Team " + (slot + 1));
            return slot == _defenseSlot ? "DEFENSE" : "OFFENSE";
        }

        #region Player lifecycle

        /// <summary>
        /// A player who walks in while a mix/OvD is already under way (any state past Idle,
        /// Running included) is defaulted INTO the pool rather than left to type ?p themselves -
        /// they can always ?np straight back out, since EligiblePool() and the draft both honor
        /// "sitting on team np" over pool membership in every state. JoinPool already no-ops
        /// harmlessly (with its own message) for a captain, an already-drafted player, or someone
        /// already pooled, so this is safe to call unconditionally.
        /// </summary>
        public void OnPlayerEnterArena(Player player)
        {
            if (player == null)
                return;

            // A claimed player who dropped and came back goes straight back to their seat - checked
            // first, since a mid-game disconnect is the main case the claims exist for. A benched
            // player (specced while still in the arena) is left where they are.
            SeatClaim claim = _state != MixState.Idle ? FindSeatClaim(player._alias) : null;
            if (claim != null && !claim.Benched)
            {
                claim.RestoreAtTick = Environment.TickCount + RECONNECT_RESTORE_DELAY;
                Team seat = claim.TeamSlot >= 0 && claim.TeamSlot <= 1 ? _teams[claim.TeamSlot] : null;
                player.sendMessage(0, String.Format("@Welcome back - returning you to {0} in {1} seconds.",
                    seat != null ? seat._name : "your team", RECONNECT_RESTORE_DELAY / 1000));
                return;
            }

            // Only while teams are being organized. During the match itself there is nothing to
            // join, and "You are IN the pool" mid-OvD only confused people (2026-09-12).
            if (!IsOrganizing)
                return;

            if (player._team != null && player._team._name.Equals("np", StringComparison.OrdinalIgnoreCase))
                return;   // already opted out - respect it, don't pull them back in

            JoinPool(player, true);
        }

        /// <summary>
        /// MUST be called from Script_CTF's Player.LeaveArena handler, NOT from Player.Leave.
        ///
        /// Player.Leave fires on every spec: `Player.spec()` calls `_arena.playerLeave()`, which
        /// callsyncs "Player.Leave" (ScriptArena.cs:145). Hooking it here dropped players out of the
        /// pool the moment ?p specced them into it - the draft then came up one player short every
        /// time. Player.LeaveArena (ArenaState.cs:410, `lostPlayer`) is the real "gone" event.
        /// </summary>
        public void OnPlayerLeaveArena(Player player)
        {
            if (player == null)
                return;
            _pool.Remove(player._alias);

            // A captain leaving mid-draft cannot be recovered from - end the session cleanly rather
            // than leaving a draft that can never advance.
            if ((_state == MixState.CaptainSignup || _state == MixState.Picking) && IsCaptain(player._alias))
                Cancel(String.Format("captain {0} left the arena", player._alias));
        }

        #endregion

        #region Command handling

        /// <summary>
        /// Handles ? commands. Returns true ONLY when consumed, so every unrelated CTF command
        /// falls through untouched when no mix is running.
        /// </summary>
        public bool HandleChatCommand(Player player, string command, string payload)
        {
            if (command == "mix")
            {
                SendStatus(player);
                return true;
            }

            // Personal chime preference - usable any time, persisted
            if (command == "bong")
            {
                EnsurePrefsLoaded();
                string arg = (payload ?? "").Trim().ToLower();
                int bong;
                if (arg == "off")
                    bong = 0;
                else if (arg == "on")
                    bong = TURN_BONG_DEFAULT;
                else if (!int.TryParse(arg, out bong) || bong < 1 || bong > 4)
                {
                    int current = GetTurnBong(player._alias);
                    player.sendMessage(0, String.Format("Your-pick chime is {0}. Usage: ?bong off / on / 1-4",
                        current == 0 ? "OFF" : "#" + current));
                    return true;
                }
                _bongPrefs[player._alias] = bong;
                SavePrefs();
                player.sendMessage(bong, bong == 0 ? "Your-pick chime disabled." : String.Format("Your-pick chime set to #{0}.", bong));
                return true;
            }

            // Role preferences are settable whenever a player thinks of it, not only mid-draft.
            if (_roles.HandleChatCommand(player, command, payload))
                return true;

            // Draft commands mean nothing with no draft up, and during the live game they must fall
            // through so nothing here shadows normal in-game commands.
            if (_state == MixState.Idle || _state == MixState.Running)
                return false;

            if (command == "p" || command == "playing")
            {
                JoinPool(player);
                return true;
            }

            if (command == "np")
            {
                LeavePool(player);
                return true;
            }

            if (command == "c")
            {
                if (_state != MixState.CaptainSignup)
                {
                    player.sendMessage(-1, "Captain signup is not open.");
                    return true;
                }
                HandleCaptainSignup(player);
                return true;
            }

            if (command == "l" || command == "list")
            {
                SendPoolList(player);
                return true;
            }

            // The comp-planning view: who plays what, in preference order. Separate from ?l on
            // purpose - ?l is for picking fast by number, this is for deciding WHO to pick.
            if (command == "lr" || command == "pool" || command == "poolroles")
            {
                List<Player> pool = EligiblePool();
                AssignPoolNumbers(pool);
                _roles.SendPoolRoles(player, pool, _poolNumbers);
                return true;
            }

            if (command == "base")
            {
                HandleBaseCommand(player, payload);
                return true;
            }

            if (command == "voteout")
            {
                HandleVoteOut(player, (payload ?? "").Trim());
                return true;
            }

            if (_state == MixState.Picking && (command == "pick" || IsPickShorthand(command)))
            {
                string target = command == "pick" ? (payload ?? "").Trim() : command;
                HandlePick(player, target);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handles *ovd - a 5v5 offense-vs-defense game. Everything downstream is the mix draft
        /// engine; OvD only adds the base-selection phase and the offense/defense naming, because
        /// the base decides which side defends and therefore which team each captain drafts onto.
        /// </summary>
        public bool HandleOvdCommand(Player player, string payload)
        {
            payload = (payload ?? "").Trim().ToLower();

            // "*ovd auto on/off" is OvD's own switch - OvD defaults to timer automation ON and a
            // mix to OFF, so the two must not share one flag.
            if (payload == "auto" || payload.StartsWith("auto "))
            {
                string arg = payload == "auto" ? "" : payload.Substring(5).Trim();
                if (arg == "on") _ovdAutoPick = true;
                else if (arg == "off") _ovdAutoPick = false;
                else
                {
                    player.sendMessage(0, String.Format("OvD timer automation is {0}. Usage: *ovd auto on/off", _ovdAutoPick ? "ON" : "OFF"));
                    return true;
                }
                player.sendMessage(0, String.Format("OvD timer automation {0} - {1}", _ovdAutoPick ? "ON" : "OFF",
                    _ovdAutoPick
                        ? String.Format("captains drawn after {0}s, idle picks after {1}s, base auto-chosen after {2}s.",
                            OVD_CAPTAIN_SIGNUP_TIMEOUT / 1000, OVD_PICK_TURN_TIMEOUT / 1000, OVD_BASE_SELECT_TIMEOUT / 1000)
                        : "captains and picks wait until made."));
                return true;
            }

            // Everything except a fresh start is shared with *mix - no reason to duplicate it.
            // (That includes "fill", "go", "off", "status", "priority", "unban".)
            if (payload.Length > 0 && !payload.StartsWith("start") && !Char.IsDigit(payload[0]))
                return HandleModCommand(player, payload);

            if (_state == MixState.CaptainSignup || _state == MixState.BaseSelect
                || _state == MixState.Picking || _state == MixState.Countdown)
            {
                SendStatus(player);
                player.sendMessage(0, "Use '*mix off' to cancel or '*mix go' to force the current phase.");
                return true;
            }

            string[] parts = payload.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int idx = 0;
            if (idx < parts.Length && parts[idx] == "start")
                idx++;

            int size = OVD_TEAM_SIZE;
            if (idx < parts.Length)
            {
                int parsed;
                if (int.TryParse(parts[idx], out parsed))
                {
                    if (parsed < OVD_MIN_TEAM_SIZE || parsed > MAX_TEAM_SIZE)
                    {
                        player.sendMessage(-1, String.Format("OvD team size must be {0}-{1} - {0}v{0} is the hard minimum.",
                            OVD_MIN_TEAM_SIZE, MAX_TEAM_SIZE));
                        return true;
                    }
                    size = parsed;
                    idx++;
                }
            }

            StartSignup(size, true);

            // Optional captain designation, same token forms as *mix
            string captains = idx < parts.Length ? String.Join(" ", parts.Skip(idx).ToArray()) : "";
            foreach (string rawTok in captains.Split(','))
            {
                string tok = rawTok.Trim();
                if (tok.Length == 0)
                    continue;
                if (_state != MixState.CaptainSignup)
                    break;
                Player target = ResolvePickTarget(player, tok);
                if (target == null)
                    continue;
                if (_voteoutBanned.Contains(target._alias))
                {
                    player.sendMessage(-1, String.Format("{0} has been voted out and cannot captain.", target._alias));
                    continue;
                }
                if (!IsCaptain(target._alias))
                    DesignateCaptain(target);
            }
            return true;
        }

        /// <summary>Handles *mix: start / size / captains / auto / mercy / priority / go / off / status / unban.</summary>
        public bool HandleModCommand(Player player, string payload)
        {
            payload = (payload ?? "").Trim().ToLower();

            if (payload.Length == 0)
            {
                SendStatus(player);
                player.sendMessage(0, "Usage:");
                player.sendMessage(0, "~  *mix <size> [capt1,capt2]  - start a mix, e.g. *mix 10 axi,mtn or *mix 10 1,2");
                player.sendMessage(0, "  *ovd [size] [capt1,capt2]   - start a 5v5 OvD; defending captain picks the base with ?base");
                player.sendMessage(0, String.Format("  *mix auto on/off  - MIX timer automation (random captains at 60s, auto-picks at 30s). Currently {0}.",
                    _autoPickEnabled ? "ON" : "OFF"));
                player.sendMessage(0, String.Format("  *ovd auto on/off  - OvD timer automation (captains at {0}s, picks at {1}s, base at {2}s). Currently {3}.",
                    OVD_CAPTAIN_SIGNUP_TIMEOUT / 1000, OVD_PICK_TURN_TIMEOUT / 1000, OVD_BASE_SELECT_TIMEOUT / 1000, _ovdAutoPick ? "ON" : "OFF"));
                player.sendMessage(0, String.Format("  *mix priority on/off  - reserve each team's last slot for the longest-waiting player. Currently {0}.",
                    _priorityAutoEnabled ? "ON" : "OFF"));
                player.sendMessage(0, "  *mix fill  - put everyone in the arena into the pool (skips np)");
                player.sendMessage(0, "~  *mix go / off / status  - skip current phase / cancel session / show phase");
                player.sendMessage(0, "  *mix unban <name>  - lift a captain vote-out ban");
                player.sendMessage(0, "~Hand-drafted OvD (you build the teams, it does the typing):");
                player.sendMessage(0, "  *ovd base D7  - flag, drops, minerals to your medic, defence warped in. Defence builds.");
                player.sendMessage(0, "  *ovd go       - stage offence, start the clock. (*ovd go D7 does both at once)");
                player.sendMessage(0, "  *ovd captains <a>,<b>  - who captained, for the stats row");
                player.sendMessage(0, "~Players use: ?p play, ?np sit out, ?c captain, ?l pool, ?pick <#|name>, ?voteout <captain>, ?bong off");
                return true;
            }

            if (payload == "unban" || payload.StartsWith("unban "))
            {
                string who = payload == "unban" ? "" : payload.Substring(6).Trim();
                string hit = _voteoutBanned.FirstOrDefault(a => a.Equals(who, StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                {
                    _voteoutBanned.Remove(hit);
                    player.sendMessage(0, String.Format("{0} can captain again.", hit));
                }
                else
                {
                    player.sendMessage(0, _voteoutBanned.Count == 0
                        ? "No captain vote-out bans."
                        : String.Format("Not on the ban list. Currently banned: {0}", String.Join(", ", _voteoutBanned.ToArray())));
                }
                return true;
            }

            if (payload == "auto" || payload.StartsWith("auto "))
            {
                string arg = payload == "auto" ? "" : payload.Substring(5).Trim();
                if (arg == "on")
                    _autoPickEnabled = true;
                else if (arg == "off")
                    _autoPickEnabled = false;
                else
                {
                    player.sendMessage(0, String.Format("Timer automation is {0}. Usage: *mix auto on/off", _autoPickEnabled ? "ON" : "OFF"));
                    return true;
                }
                player.sendMessage(0, String.Format("Timer automation {0} - {1}", _autoPickEnabled ? "ON" : "OFF",
                    _autoPickEnabled ? "captains drawn after 60s, idle picks auto-assigned after 30s."
                                     : "captains and picks wait until made (restart countdown still applies)."));
                return true;
            }

            if (payload == "priority" || payload.StartsWith("priority "))
            {
                string arg = payload == "priority" ? "" : payload.Substring(9).Trim();
                if (arg == "on")
                    _priorityAutoEnabled = true;
                else if (arg == "off")
                    _priorityAutoEnabled = false;
                else
                {
                    player.sendMessage(0, String.Format("Priority auto-assign is {0}. Usage: *mix priority on/off",
                        _priorityAutoEnabled ? "ON" : "OFF"));
                    return true;
                }
                player.sendMessage(0, _priorityAutoEnabled
                    ? String.Format("Priority auto-assign ON - BOTH teams' last slot goes to players who sat out {0}+ mixes running. Fires in pairs only, so one captain is never saddled alone.", PRIORITY_AUTO_STREAK)
                    : "Priority auto-assign OFF - the sit-out block in ?l is advisory only.");
                return true;
            }

            if (payload == "off")
            {
                if (_state == MixState.Idle)
                {
                    player.sendMessage(0, "No mix session is active.");
                    return true;
                }
                Cancel(_state == MixState.Running
                    ? "mercy watch stopped by " + player._alias
                    : "cancelled by " + player._alias);
                return true;
            }

            if (payload == "go")
            {
                if (_state == MixState.CaptainSignup)
                    ResolveCaptains();
                else if (_state == MixState.BaseSelect)
                {
                    List<string> opts = PlayableBases();
                    if (opts.Count > 0)
                        ChooseBase(opts[_arena._rand.Next(0, opts.Count)]);
                }
                else if (_state == MixState.Picking)
                    StartRestartCountdown();
                else if (_state == MixState.Countdown)
                    Launch();
                else if (_manualBase != null)
                    ManualGo(player);
                else
                    player.sendMessage(0, "No mix session is active. Hand-drafted OvD: *ovd base D7, then *ovd go (or *ovd go D7 for both).");
                return true;
            }

            // "*ovd go D7" - base setup and launch in one, for a host who does not want the setup window.
            if (payload.StartsWith("go "))
            {
                if (IsOrganizing)
                {
                    player.sendMessage(-1, "A draft is in progress - plain *mix go forces the current phase; *mix off abandons it.");
                    return true;
                }
                string[] goParts = payload.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (ManualBase(player, goParts[1].ToUpper(), goParts.Length > 2 ? goParts[2] : null))
                    ManualGo(player);
                return true;
            }

            if (payload == "base" || payload.StartsWith("base "))
            {
                if (IsOrganizing)
                {
                    player.sendMessage(-1, "A draft is in progress - the defending captain picks the base with ?base. *mix off to abandon the draft first.");
                    return true;
                }
                string[] baseParts = payload.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (baseParts.Length < 2)
                {
                    player.sendMessage(0, "Usage: *ovd base <name> [t|c]   e.g. *ovd base D7.  Bases: "
                        + String.Join(", ", _script.GetBaseNames().ToArray()));
                    return true;
                }
                ManualBase(player, baseParts[1].ToUpper(), baseParts.Length > 2 ? baseParts[2] : null);
                return true;
            }

            if (payload == "captains" || payload.StartsWith("captains "))
            {
                if (IsOrganizing)
                {
                    player.sendMessage(-1, "Captains are set by ?c / the draft while a session is running.");
                    return true;
                }
                string list = payload == "captains" ? "" : payload.Substring(9);
                string[] toks = list.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length == 0)
                {
                    player.sendMessage(0, String.Format("Captains on record: {0}. Usage: *ovd captains <name>,<name>",
                        String.Join(", ", _captainAliases.Where(a => a != null).DefaultIfEmpty("none").ToArray())));
                    return true;
                }
                _captainAliases[0] = null;
                _captainAliases[1] = null;
                int slot = 0;
                foreach (string tok in toks.Take(2))
                {
                    Player who = FindArenaPlayer(tok);
                    if (who == null)
                        player.sendMessage(-1, String.Format("No player in the arena matches '{0}'.", tok));
                    else
                        _captainAliases[slot++] = who._alias;
                }
                player.sendMessage(0, String.Format("Captains for the stats row: {0}.",
                    String.Join(", ", _captainAliases.Where(a => a != null).DefaultIfEmpty("none").ToArray())));
                return true;
            }

            if (payload == "status")
            {
                SendStatus(player);
                return true;
            }

            if (payload == "fill")
            {
                if (_state != MixState.CaptainSignup)
                {
                    player.sendMessage(-1, "Fill only works while captain signup is open.");
                    return true;
                }
                int added = FillPoolFromArena();
                player.sendMessage(0, added == 0
                    ? "Nobody to add - everyone present is already in the pool or on np."
                    : String.Format("Added {0} player{1} to the pool ({2} total).", added, added == 1 ? "" : "s", _pool.Count));
                return true;
            }

            // A draft in progress blocks a new start. A live game or the intermission does NOT -
            // that is exactly when a ref wants to take over and run their own draft.
            if (_state == MixState.CaptainSignup || _state == MixState.BaseSelect
                || _state == MixState.Picking || _state == MixState.Countdown)
            {
                SendStatus(player);
                player.sendMessage(0, "Use '*mix off' to cancel or '*mix go' to force the current phase to complete.");
                return true;
            }

            // "*mix start [size] [capt1,capt2]" or "*mix <size> [capt1,capt2]"
            string[] startParts = payload.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int tokenIdx = 0;
            if (tokenIdx < startParts.Length && startParts[tokenIdx] == "start")
                tokenIdx++;

            int size = DEFAULT_TEAM_SIZE;
            if (tokenIdx < startParts.Length)
            {
                int parsedSize;
                if (int.TryParse(startParts[tokenIdx], out parsedSize))
                {
                    if (parsedSize < 2 || parsedSize > MAX_TEAM_SIZE)
                    {
                        player.sendMessage(-1, String.Format("Team size must be 2-{0}.", MAX_TEAM_SIZE));
                        return true;
                    }
                    size = parsedSize;
                    tokenIdx++;
                }
                else if (startParts[0] != "start")
                {
                    player.sendMessage(-1, "Usage: *mix start [size] [capt1,capt2] - or *mix with no arguments for help.");
                    return true;
                }
            }
            string captainList = tokenIdx < startParts.Length ? String.Join(" ", startParts.Skip(tokenIdx).ToArray()) : "";

            StartSignup(size, false);   // explicit: *mix is never OvD, whatever ran before

            foreach (string rawTok in captainList.Split(','))
            {
                string tok = rawTok.Trim();
                if (tok.Length == 0)
                    continue;
                if (_state != MixState.CaptainSignup)
                    break; // both slots filled - picking already started
                Player target = ResolvePickTarget(player, tok);
                if (target == null)
                    continue; // slot stays open for ?c
                if (_voteoutBanned.Contains(target._alias))
                {
                    player.sendMessage(-1, String.Format("{0} has been voted out and cannot captain.", target._alias));
                    continue;
                }
                if (!IsCaptain(target._alias))
                    DesignateCaptain(target);
            }
            return true;
        }

        #endregion

        #region Hand-drafted OvD (setup + launch without the draft)

        // Zmn-style hosting: the ref builds the teams by hand (*unspec onto Titan Militia /
        // Collective or any T/C pair) and only wants the mechanical part - base setup, defence
        // placement, offence staging, the clock, and a stats row that says OvD with the right sides.
        // These reuse exactly the steps the draft path runs, on the teams as they stand, so a
        // hand-drafted OvD and a drafted one are indistinguishable from launch onwards.
        // _manualBase is the base *ovd base set up, consumed by *ovd go.
        private string _manualBase = null;

        /// <summary>
        /// Makes sure a game is live under the OvD. Both OvD paths need one: Running + _ovdMode
        /// holds the arena's loop down only while _bGameRunning is true, and the 18:00 clock ends
        /// the match through gameEnd(). Must run BEFORE SetupBase, since gameStart resets the flags.
        /// </summary>
        private void EnsureLiveGame()
        {
            if (_arena._bGameRunning)
                return;
            // Bookkeeping, not news: no "Game has started!", no "healed" line, no note from here.
            _script.StartGameQuietly();
            if (!_arena._bGameRunning)
                _arena.sendArenaMessage("!Could not start a game under the OvD - the arena may restart it. A ref can *startgame and *ovd go.");
        }

        /// <summary>
        /// *ovd base X [t|c]: resolve the two populated teams, decide who defends from the base's
        /// side (or the override), then do what ChooseBase does - setup, defence placement, hold the
        /// arena's game loop so nothing flagResets the base away.
        /// </summary>
        private bool ManualBase(Player mod, string baseName, string sideTok)
        {
            List<string> known = _script.GetBaseNames();
            if (!known.Contains(baseName))
            {
                mod.sendMessage(-1, String.Format("Unknown base '{0}'. Bases: {1}", baseName, String.Join(", ", known.ToArray())));
                return false;
            }

            bool defenseIsTitan;
            if (sideTok == "t" || sideTok == "titan")
                defenseIsTitan = true;
            else if (sideTok == "c" || sideTok == "collective")
                defenseIsTitan = false;
            else if (TITAN_BASES.Contains(baseName))
                defenseIsTitan = true;
            else if (COLLECTIVE_BASES.Contains(baseName))
                defenseIsTitan = false;
            else
            {
                mod.sendMessage(-1, String.Format("{0} has no fixed side - say who defends: *ovd base {0} t   (or c)", baseName));
                return false;
            }

            // The rosters live on whatever pair the host used; we only need one team per side.
            List<Team> populated = _arena.Teams
                .Where(t => t != null && !t.IsSpec && t.ActivePlayerCount > 0
                    && !t._name.Equals("np", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Team titan = populated.FirstOrDefault(t => Script_CTF.IsTitanSideTeam(t));
            Team coll = populated.FirstOrDefault(t => Script_CTF.IsCollectiveSideTeam(t));
            if (titan == null || coll == null)
            {
                mod.sendMessage(-1, String.Format("Need one Titan-side and one Collective-side team with players. Have: {0}",
                    String.Join(", ", populated.Select(t => t._name + " (" + t.ActivePlayerCount + ")").DefaultIfEmpty("nobody in game").ToArray())));
                return false;
            }
            if (populated.Count > 2)
                mod.sendMessage(0, String.Format("Note: {0} teams have players; using {1} and {2}.",
                    populated.Count, titan._name, coll._name));

            Team defense = defenseIsTitan ? titan : coll;
            Team offense = defenseIsTitan ? coll : titan;

            _ovdMode = true;
            _teams[0] = titan;
            _teams[1] = coll;
            _defenseSlot = defenseIsTitan ? 0 : 1;
            _selectedBase = baseName;
            _manualBase = baseName;
            ClearPhaseTicker();
            ClearSeats();

            // Running + _ovdMode holds the arena's game loop down (SuppressAutoStart) for the whole
            // match, exactly as after a drafted launch. That needs a live game underneath it - the
            // Running poll returns to Idle the moment none is - so start one first if needed.
            EnsureLiveGame();
            _state = MixState.Running;
            _script.HoldAutomaticGameStart();

            if (!_script.SetupBase(baseName, mod, false))
            {
                _arena.sendArenaMessage("!Base setup failed - see the message above.");
                _manualBase = null;
                return false;
            }
            _script.PositionOvdDefense(baseName, defense, false);
            AnnounceSides(true, "*ovd go when ready");
            return true;
        }

        /// <summary>*ovd go after *ovd base: record the session for the stats writer and launch.</summary>
        private void ManualGo(Player mod)
        {
            Team defense = _teams[_defenseSlot];
            Team offense = _teams[1 - _defenseSlot];
            if (defense == null || offense == null || defense.ActivePlayerCount == 0 || offense.ActivePlayerCount == 0)
            {
                mod.sendMessage(-1, String.Format("One side is empty ({0}: {1}, {2}: {3}) - fill it, or *ovd base again.",
                    defense != null ? defense._name : "?", defense != null ? defense.ActivePlayerCount : 0,
                    offense != null ? offense._name : "?", offense != null ? offense.ActivePlayerCount : 0));
                return;
            }
            if (!_arena._bGameRunning)
                _arena.gameStart();
            _state = MixState.Running;
            RememberSession();   // OvD + sides + captains for Game.End, same as a drafted launch
            RecordSeats();
            LaunchOvd();
            _manualBase = null;
        }

        private Player FindArenaPlayer(string token)
        {
            Player exact = _arena.Players.FirstOrDefault(p => p._alias.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
            List<Player> prefix = _arena.Players
                .Where(p => p._alias.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
            return prefix.Count == 1 ? prefix[0] : null;
        }

        #endregion

        #region Poll

        public void Poll(int now)
        {
if (_state == MixState.Idle)
                return;

            ProcessPendingRestores(now);

            switch (_state)
            {
                case MixState.CaptainSignup:
                    if (TimerAutomation && now >= _phaseDeadline)
                        ResolveCaptains();
                    break;

                case MixState.BaseSelect:
                    // Defending captain sat on it - pick for them rather than stalling the game.
                    if (now >= _phaseDeadline)
                    {
                        List<string> options = PlayableBases();
                        if (options.Count == 0)
                        {
                            Cancel("this map has no bases configured for OvD");
                            break;
                        }
                        string auto = options[_arena._rand.Next(0, options.Count)];
                        _arena.sendArenaMessage(String.Format("!No base picked in time - {0} it is.", auto));
                        ChooseBase(auto);
                    }
                    break;

                case MixState.Picking:
                    Player capt = CurrentCaptain();
                    if (capt == null)
                    {
                        Cancel(String.Format("captain {0} is no longer in the arena", _captainAliases[_turnCaptain]));
                        break;
                    }
                    if (now >= _phaseDeadline)
                    {
                        if (TimerAutomation)
                            AutoPick();
                        else
                        {
                            string tease = SLOWPOKE_LINES[_arena._rand.Next(0, SLOWPOKE_LINES.Length)];
                            _arena.sendArenaMessage(SideMark(_teams[_turnCaptain]) + String.Format(tease, _captainAliases[_turnCaptain]));
                            _phaseDeadline = now + PICK_REMINDER_REPEAT;
                        }
                    }
                    break;

                case MixState.Countdown:
                    if (now >= _restartDeadline)
                        Launch();
                    break;

                case MixState.Running:
                    // Nothing to police mid-match any more - just notice when it ends.
                    // (A kill-differential mercy rule lived here until 2026-08-29; removed
                    //  because CTF is far less KDA-driven than USL.)
                    if (!_arena._bGameRunning)
                    {
                        _state = MixState.Idle;
                        ClearSeats();
                    }
                    break;
            }
        }

        #endregion

        #region Pool

        /// <param name="automatic">True when the manager pooled them (walked in mid-draft) rather
        /// than the player typing ?p - the wording tells them how to get OUT, not that they are in.</param>
        private void JoinPool(Player player, bool automatic = false)
        {
            if (IsCaptain(player._alias))
            {
                if (!automatic) player.sendMessage(0, "You are a captain - you are already in.");
                return;
            }
            if (_drafted.Contains(player._alias))
            {
                if (!automatic) player.sendMessage(0, "You have already been drafted onto a team.");
                return;
            }
            if (_pool.Contains(player._alias))
            {
                if (!automatic) player.sendMessage(0, String.Format("You are IN the pool ({0} players) - already counted. ?np to sit out.", _pool.Count));
                return;
            }

            // Opening a session npd everyone, so ?p means "put me back on the spec team and into
            // the pool" - the same move ?p has always meant in this zone. An in-game player who
            // joined after the decks were cleared is left in their game; being drafted moves them.
            bool onNp = player._team != null && player._team._name.Equals("np", StringComparison.OrdinalIgnoreCase);
            if (onNp)
                player.spec("spec");   // raises Player.Leave; harmless - pool state lives on LeaveArena

            _pool.Add(player._alias);

            player.sendMessage(0, automatic
                ? String.Format("&{0} draft on - you're IN the pool ({1}). ?np to sit out.", ModeName, _pool.Count)
                : String.Format("You are IN the pool ({0} players). ?l shows it, ?lr shows roles, ?np to sit out.", _pool.Count));

            // A held OvD draft (see AdvanceTurn) resumes as soon as a body appears - on a fresh pick
            // clock, not the minute-long reminder deadline the hold left behind.
            if (_state == MixState.Picking && _ovdMode && _turnPicksLeft > 0)
            {
                _phaseDeadline = Environment.TickCount + PickTimeout;
                _arena.sendArenaMessage(String.Format("&{0} joined - draft resumes.", player._alias));
                AnnounceTurn();
            }
        }

        /// <summary>
        /// Puts everyone currently in the arena into the pool, skipping anyone sitting on np (opting
        /// out has to keep meaning something) and anyone already in. Announces itself, because
        /// players who did not type ?p deserve to know they are now in a draft.
        /// </summary>
        private int FillPoolFromArena(bool announce = true)
        {
            int added = 0;
            foreach (Player p in _arena.Players.ToList())
            {
                if (p == null)
                    continue;
                if (IsCaptain(p._alias) || _drafted.Contains(p._alias) || _pool.Contains(p._alias))
                    continue;
                if (p._team != null && p._team._name.Equals("np", StringComparison.OrdinalIgnoreCase))
                    continue;   // they said no

                _pool.Add(p._alias);
                added++;
                if (announce)
                    p.sendMessage(0, "You have been added to the mix pool by a ref. ?np if you can't play.");
            }

            if (announce && added > 0)
                _arena.sendArenaMessage(String.Format("&{0} player{1} added to the pool - ?np if you can't play.",
                    added, added == 1 ? "" : "s"));
            return added;
        }

        private void LeavePool(Player player)
        {
            if (IsCaptain(player._alias))
            {
                player.sendMessage(-1, "Captains cannot sit out - a ref can *mix off to cancel.");
                return;
            }
            bool was = _pool.Remove(player._alias);

            // Only park a SPECTATOR on np. Yanking an in-game player out of the pub game they are
            // playing would make ?np punish them for un-queueing, which is the opposite of the
            // intent - queueing is meant to cost nothing either way.
            if (player.IsSpectator && _arena.getTeamByName("np") != null)
                player.spec("np");

            player.sendMessage(0, was
                ? "You are OUT of the pool - you will not be picked. ?p to rejoin."
                : "You are NOT in the mix pool. ?p to join.");
        }

        /// <summary>
        /// The draftable pool: opted-in players still present in the arena, minus captains and
        /// anyone already drafted. Resolved live against the arena so a disconnect drops out even if
        /// OnPlayerLeave was somehow missed.
        /// </summary>
        private List<Player> EligiblePool()
        {
            List<Player> result = new List<Player>();
            foreach (Player p in _arena.Players.ToList())
            {
                if (p == null || !_pool.Contains(p._alias))
                    continue;
                if (IsCaptain(p._alias) || _drafted.Contains(p._alias))
                    continue;
                // Sitting on np beats being in the set - covers ?team np and the end-of-game
                // spec-all, neither of which goes through this manager.
                if (p._team != null && p._team._name.Equals("np", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(p);
            }
            return result;
        }

        private void AssignPoolNumbers(List<Player> pool)
        {
            foreach (Player p in pool)
            {
                if (!_poolNumbers.ContainsKey(p._alias))
                    _poolNumbers[p._alias] = _nextPoolNumber++;
            }
        }

        #endregion

        #region Captain signup

        /// <summary>Restart in whatever mode is already running (mercy intermission, *mix go).</summary>
        private void StartSignup(int teamSize)
        {
            StartSignup(teamSize, _ovdMode);
        }

        private void StartSignup(int teamSize, bool ovd)
        {
            _ovdMode = ovd;
            _selectedBase = null;
            _state = MixState.CaptainSignup;
            EnsurePrefsLoaded();
            _teamSize = teamSize;
            _captainAliases[0] = null;
            _captainAliases[1] = null;
            _teams[0] = null;
            _teams[1] = null;
            _manualBase = null;
            ClearSeats();
_roles.Reload(); // re-read MixRolePrefs.txt so seeded entries edited live take effect
            _pool.Clear();   // fresh opt-in every mix - players ?p in for the one they want
            _drafted.Clear();
            _poolNumbers.Clear();
            _nextPoolNumber = 1;
            _hintedCaptains.Clear();
            _voteoutVotes.Clear(); // votes reset per session; the ban list does NOT
            _priorityTaken[0] = 0;
            _priorityTaken[1] = 0;
            _phaseDeadline = Environment.TickCount + SignupTimeout;
            _signupExtendAnnounced = false;

            // Stop the arena's own game loop from starting a game underneath the draft.
            _script.HoldAutomaticGameStart();

            // Clear the decks - everyone to the spectator team so the pub game ends and the draft
            // starts clean - and then put EVERYONE present into the pool. This is the opt-OUT model
            // (Travis, 2026-09-12): being in the arena means you are playing unless you say ?np.
            // The earlier opt-in (np everyone, wait for ?p) left half the room out of every draft
            // because people who had just walked in never saw the prompt. Anyone already on np
            // stays out; they said no.
            _script.SpecAllToSpectators();
            int pooled = FillPoolFromArena(false);

            // Two lines, no more (Travis, 2026-09-12: "too many words"). What it is, that you are in,
            // how to captain or sit out - then how long until the draft and where the pool is.
            _arena.sendArenaMessage(String.Format("&{0} ({1}v{1}) - everyone here is IN ({2} in the pool). ?c to captain, ?np to sit out.",
                ModeName, _teamSize, pooled), 30);

            if (TimerAutomation)
            {
                _arena.sendArenaMessage(String.Format("#Captains in {0}s, then the draft.  ?l pool | ?lr roles | ?role sets yours", SignupTimeout / 1000));
                SetPhaseTicker(SignupTimeout, "Captain signup: ");
            }
            else
            {
                _arena.sendArenaMessage("#Waiting for two ?c.  ?l pool | ?lr roles | ?role sets yours");
                SetPhaseTickerText("Captain signup: ?c");
            }
        }

        private void HandleCaptainSignup(Player player)
        {
            if (_voteoutBanned.Contains(player._alias))
            {
                player.sendMessage(-1, "You have been voted out and cannot captain.");
                return;
            }
            if (IsCaptain(player._alias))
            {
                player.sendMessage(0, "You are already signed up as captain.");
                return;
            }

            // Volunteering implies playing
            _pool.Add(player._alias);

            if (_captainAliases[0] == null)
            {
                _captainAliases[0] = player._alias;
                _pool.Remove(player._alias);
                _arena.sendArenaMessage(String.Format("&{0} is captain! One ?c slot left.", player._alias));
            }
            else if (_captainAliases[1] == null)
            {
                _captainAliases[1] = player._alias;
                _pool.Remove(player._alias);
                _arena.sendArenaMessage(String.Format("&{0} is captain!", player._alias));
                OnCaptainsReady();
            }
        }

        /// <summary>Ref-designated captain - same slot flow as ?c.</summary>
        private void DesignateCaptain(Player target)
        {
            _pool.Remove(target._alias);
            if (_captainAliases[0] == null)
            {
                _captainAliases[0] = target._alias;
                _arena.sendArenaMessage(String.Format("&{0} designated captain! One slot left - type ?c.", target._alias));
            }
            else if (_captainAliases[1] == null)
            {
                _captainAliases[1] = target._alias;
                _arena.sendArenaMessage(String.Format("&{0} designated captain!", target._alias));
                OnCaptainsReady();
            }
        }

        /// <summary>Fills open captain slots randomly from the pool, then starts picking.</summary>
        private void ResolveCaptains()
        {
            List<Player> pool = EligiblePool().Where(p => !_voteoutBanned.Contains(p._alias)).ToList();
            int needed = (_captainAliases[0] == null ? 1 : 0) + (_captainAliases[1] == null ? 1 : 0);

            if (pool.Count < needed)
            {
                if (!_signupExtendAnnounced)
                {
                    _signupExtendAnnounced = true;
                    _arena.sendArenaMessage(String.Format("!Not enough players to pick captains ({0} available, {1} needed) - signup stays open. Type ?p!",
                        pool.Count, needed));
                }
                _phaseDeadline = Environment.TickCount + CAPTAIN_SIGNUP_EXTEND;
                SetPhaseTicker(CAPTAIN_SIGNUP_EXTEND, "Captain signup: ");
                return;
            }

            List<Player> chosen = pool.OrderBy(p => _arena._rand.Next(0, 10000)).Take(needed).ToList();
            int idx = 0;
            for (int slot = 0; slot < 2; slot++)
            {
                if (_captainAliases[slot] == null)
                {
                    _captainAliases[slot] = chosen[idx]._alias;
                    _pool.Remove(chosen[idx]._alias);
                    // No arena line - the sides announcement that follows names both captains, and
                    // "X was randomly selected as captain!" twice was two of the six lines Travis
                    // found unreadable (2026-09-12). The captain themselves is told privately.
                    chosen[idx].sendMessage(GetTurnBong(chosen[idx]._alias), "You were drawn as captain - nobody else volunteered.");
                    idx++;
                }
            }
            OnCaptainsReady();
        }

        #endregion

        #region Picking

        /// <summary>
        /// Both captains are in. A mix goes straight to drafting; OvD first has to know WHICH BASE
        /// is being played, because the base decides which side defends it - and therefore which
        /// arena team each captain drafts onto. Choosing the base after the draft would mean
        /// reshuffling both rosters, so it happens first.
        /// </summary>
        private void OnCaptainsReady()
        {
            if (!_ovdMode)
            {
                StartPicking();
                return;
            }

            // Coin flip decides who defends. That has to be known BEFORE the draft so each captain
            // drafts the right kind of comp - but the BASE itself is chosen after the draft, once
            // they can see the roster they actually have.
            _defenseSlot = _arena._rand.Next(0, 2);

            // Announced once the teams exist (BeginDraft): two coloured lines, DEFENSE = / OFFENSE =.
            // The old "OvD: X DEFENDS, Y ATTACKS" + "Draft first..." pair here, plus the "X (team)
            // vs Y (team)!" line at draft start, were three of the six lines Travis could not parse.
            StartPicking();
        }

        /// <summary>
        /// Teams are drafted; the defending captain now chooses the ground. Runs between the draft
        /// and the restart countdown.
        /// </summary>
        private void StartBaseSelect()
        {
            _state = MixState.BaseSelect;
            _phaseDeadline = Environment.TickCount + BaseTimeout;
            SetPhaseTicker(BaseTimeout, "Base pick: ");

            _arena.sendArenaMessage(String.Format("&Teams are set. {0}, pick the base you will DEFEND:  ?base <name>",
                _captainAliases[_defenseSlot]), 30);

            Player d = _arena.getPlayerByName(_captainAliases[_defenseSlot]);
            if (d != null)
                SendBaseList(d);
        }

        /// <summary>?base during OvD base selection - the defending captain chooses the ground.</summary>
        private void HandleBaseCommand(Player player, string payload)
        {
            if (_state != MixState.BaseSelect)
            {
                player.sendMessage(-1, "There is no base to pick right now.");
                return;
            }
            if (!player._alias.Equals(_captainAliases[_defenseSlot], StringComparison.OrdinalIgnoreCase))
            {
                player.sendMessage(-1, String.Format("Only the defending captain ({0}) picks the base.",
                    _captainAliases[_defenseSlot]));
                return;
            }

            string arg = (payload ?? "").Trim();
            if (arg.Length == 0)
            {
                SendBaseList(player);
                return;
            }

            if (EXPERIMENTAL_BASES.Contains(arg))
            {
                player.sendMessage(-1, String.Format("{0} is not available for OvD ({1}) - pick another.",
                    arg.ToUpper(),
                    arg.Equals("H4", StringComparison.OrdinalIgnoreCase) ? "walled off" : "experimental / not easily reachable"));
                SendBaseList(player);
                return;
            }

            List<string> valid = PlayableBases();
            string hit = valid.FirstOrDefault(b => b.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                player.sendMessage(-1, String.Format("There is no base called {0} on this map.", arg));
                SendBaseList(player);
                return;
            }

            ChooseBase(hit);
        }

        private void SendBaseList(Player player)
        {
            List<string> all = PlayableBases();
            List<string> titan = all.Where(b => TITAN_BASES.Contains(b)).ToList();
            List<string> coll = all.Where(b => COLLECTIVE_BASES.Contains(b)).ToList();
            List<string> other = all.Where(b => !TITAN_BASES.Contains(b) && !COLLECTIVE_BASES.Contains(b)).ToList();

            player.sendMessage(0, "Bases - whichever you pick, YOU defend it:");
            if (titan.Count > 0)
                player.sendMessage(0, "~Titan side: " + String.Join(", ", titan.ToArray()));
            if (coll.Count > 0)
                player.sendMessage(0, "~Collective side: " + String.Join(", ", coll.ToArray()));
            if (other.Count > 0)
                player.sendMessage(0, "~Other: " + String.Join(", ", other.ToArray()));
            player.sendMessage(0, "&Example:  ?base D7");
        }

        /// <summary>Locks the base in, configures the arena for it, and opens the draft.</summary>
        private void ChooseBase(string baseName)
        {
            _selectedBase = baseName;
            ClearPhaseTicker();

            bool defenseIsTitan = TITAN_BASES.Contains(baseName);
            if (!defenseIsTitan && !COLLECTIVE_BASES.Contains(baseName))
            {
                // Base not in either ownership list - coin flip rather than refuse to play on it.
                defenseIsTitan = _arena._rand.Next(0, 2) == 0;
            }

            // The teams were drafted onto a provisional T/C pair before the base was known. The
            // DEFENDING side must own the base, or SetupBase hands the base flag to the attackers -
            // so if the provisional sides are backwards, move both rosters onto correct teams. This
            // happens pre-game, so it is invisible beyond a team name change.
            if (!SidesMatch(defenseIsTitan))
                SwapTeamSides(defenseIsTitan);

            if (_teams[0] == null || _teams[1] == null)
            {
                Cancel("could not resolve a matched pair of T/C teams from the zone config");
                return;
            }

            // THE RESTART BUG (live, 2026-09-12): an OvD opened while no game was running - the
            // usual case, right after the previous one ended or with everyone specced - launched
            // into a dead arena. Running + !_bGameRunning drops the manager to Idle on the next
            // poll, the hold lifts, and the arena's own loop runs its 15 second "Next game:"
            // countdown into gameStart(): flagReset() wipes the base and StartGame warps everyone
            // who has just been placed back to the dropships. The hand-hosted *ovd base path
            // already guarded this; the drafted path did not. A game must be live BEFORE the base
            // is set up, because gameStart itself resets the flags.
            EnsureLiveGame();

            // Exactly the setup a host would otherwise have typed: flag moved to the base and given
            // to the owning side, other flags parked, resources and fixed drops spawned.
            if (!_script.SetupBase(baseName, null, false))
                _arena.sendArenaMessage("!Base setup failed - a mod may need to run *setup manually.");

            // Defence support goes in NOW, not at launch - the engineer needs the setup window to
            // build the base, which is the entire point of going on 15 rather than 18.
            _script.PositionOvdDefense(baseName, _teams[_defenseSlot], false);

            // The whole result in two coloured lines; the setup / placement / side-swap chatter
            // that used to surround them is silent now.
            AnnounceSides(true, "staged at go");

            StartRestartCountdown();
        }

        /// <summary>True when the defending slot already sits on the base-owning side.</summary>
        private bool SidesMatch(bool defenseIsTitan)
        {
            Team d = _teams[_defenseSlot];
            if (d == null)
                return false;
            bool dIsTitan = d._name.EndsWith(" T") || d._name.IndexOf("Titan", StringComparison.OrdinalIgnoreCase) >= 0;
            return dIsTitan == defenseIsTitan;
        }

        /// <summary>
        /// Moves both drafted rosters onto correctly-sided teams. Called only when the base the
        /// defence chose belongs to the side they were NOT provisionally drafted onto.
        /// </summary>
        private void SwapTeamSides(bool defenseIsTitan)
        {
            int off = 1 - _defenseSlot;
            List<Player> defRoster = _teams[_defenseSlot] != null ? _teams[_defenseSlot].ActivePlayers.ToList() : new List<Player>();
            List<Player> offRoster = _teams[off] != null ? _teams[off].ActivePlayers.ToList() : new List<Player>();

            Team newDef = PickRandomSideTeam(defenseIsTitan, null);
            Team newOff = PickRandomSideTeam(!defenseIsTitan, newDef);
            if (newDef == null || newOff == null)
                return;   // caller reports the failure

            foreach (Player p in defRoster)
                MoveToTeam(p, newDef);
            foreach (Player p in offRoster)
                MoveToTeam(p, newOff);

            _teams[_defenseSlot] = newDef;
            _teams[off] = newOff;
            // Silent: the DEFENSE = / OFFENSE = lines that follow name the final teams.
        }

        private void StartPicking()
        {
            // Random matched pair from the cfg's own "<name> T" / "<name> C" teams (see divergence 2).
            _teams[0] = PickRandomSideTeam(true, null);
            _teams[1] = PickRandomSideTeam(false, _teams[0]);
            if (_teams[0] == null || _teams[1] == null)
            {
                Cancel("could not resolve a matched pair of T/C teams from the zone config");
                return;
            }

            BeginDraft();
        }

        /// <summary>Teams are resolved - seat the captains and open the snake draft.</summary>
        private void BeginDraft()
        {

            for (int i = 0; i < 2; i++)
            {
                Player capt = _arena.getPlayerByName(_captainAliases[i]);
                if (capt == null)
                {
                    Cancel(String.Format("captain {0} left before picking started", _captainAliases[i]));
                    return;
                }
                MoveToTeam(capt, _teams[i]);
            }

            _state = MixState.Picking;
            _turnCaptain = _arena._rand.Next(0, 2); // coin flip (see divergence 3)
            _turnPicksLeft = 1;                     // snake: 1, then 2-2-2... until both are full
            _phaseDeadline = Environment.TickCount + PickTimeout;

            if (_ovdMode)
            {
                // The whole story in two lines, coloured by side ('!' = Collective red, plain =
                // Titan green, the zone's own convention): who defends, who attacks, which team.
                int off = 1 - _defenseSlot;
                _arena.sendArenaMessage(String.Format("{0}DEFENSE = {1}   ({2})",
                    SideMark(_teams[_defenseSlot]), _captainAliases[_defenseSlot], _teams[_defenseSlot]._name), 30);
                _arena.sendArenaMessage(String.Format("{0}OFFENSE = {1}   ({2})",
                    SideMark(_teams[off]), _captainAliases[off], _teams[off]._name));
            }
            else
            {
                _arena.sendArenaMessage(String.Format("&{0} ({1}) vs {2} ({3})!",
                    _captainAliases[0], _teams[0]._name, _captainAliases[1], _teams[1]._name), 30);
            }
            // '#' so the coin flip does not read as one more white status line.
            _arena.sendArenaMessage(String.Format("#Coin flip: {0} picks first. Captains are PM'd on their turn.",
                _captainAliases[_turnCaptain]));
            AnnounceSatOut();
            AnnounceTurn();
        }

        /// <summary>Calls out pool players carrying a sit-out streak so captains can prioritize them.</summary>
        private void AnnounceSatOut()
        {
            List<Player> waiting = EligiblePool().Where(p => WaitStreak(p) > 0)
                .OrderByDescending(p => WaitStreak(p)).ToList();
            if (waiting.Count == 0)
                return;

            _arena.sendArenaMessage(String.Format("{0}{1} player{2} still waiting to get in - listed at the BOTTOM of ?l.",
                PRIORITY_MARK, waiting.Count, waiting.Count == 1 ? "" : "s"));

            List<string> urgent = waiting.Where(p => WaitStreak(p) >= PRIORITY_URGENT_STREAK)
                .Select(p => String.Format("{0} ({1} mixes)", p._alias, WaitStreak(p))).ToList();
            if (urgent.Count > 0)
                _arena.sendArenaMessage(PRIORITY_MARK + "Waiting longest: " + String.Join(", ", urgent.ToArray()));
        }

        private WaitRecord FindWaitRecord(Player p)
        {
            string ip = p._ipAddress != null ? p._ipAddress.ToString() : null;
            foreach (WaitRecord r in _waitLedger)
            {
                if (r.Alias.Equals(p._alias, StringComparison.OrdinalIgnoreCase))
                    return r;
                if (r.Uid1 != 0 && r.Uid1 == p._UID1 && r.Uid2 == p._UID2 && r.Uid3 == p._UID3)
                    return r;
                if (r.Ip != null && ip != null && r.Ip == ip)
                    return r;
            }
            return null;
        }

        private WaitRecord EnsureWaitRecord(Player p)
        {
            WaitRecord r = FindWaitRecord(p);
            if (r == null)
            {
                r = new WaitRecord();
                _waitLedger.Add(r);
            }
            r.Alias = p._alias;
            r.Uid1 = p._UID1;
            r.Uid2 = p._UID2;
            r.Uid3 = p._UID3;
            r.Ip = p._ipAddress != null ? p._ipAddress.ToString() : null;
            return r;
        }

        private int WaitStreak(Player p)
        {
            WaitRecord r = FindWaitRecord(p);
            return r == null ? 0 : r.Streak;
        }

        /// <summary>
        /// Settles the ledger at launch: everyone who made a team resets to 0, everyone still in the
        /// pool takes another strike. This is the point of the ledger - a boolean could not tell one
        /// sit-out from four in a row.
        /// </summary>
        private void UpdateWaitLedger()
        {
            foreach (Player p in _arena.PlayersIngame.ToList())
            {
                if (p == null || (p._team != _teams[0] && p._team != _teams[1]))
                    continue;
                EnsureWaitRecord(p).Streak = 0;
            }

            foreach (Player p in EligiblePool())
                EnsureWaitRecord(p).Streak++;
        }

        private void HandlePick(Player player, string target)
        {
            if (!player._alias.Equals(_captainAliases[_turnCaptain], StringComparison.OrdinalIgnoreCase))
            {
                if (IsCaptain(player._alias))
                    player.sendMessage(-1, "It is not your pick.");
                else
                    player.sendMessage(-1, "Only the picking captain can do that.");
                return;
            }

            if (String.IsNullOrEmpty(target))
            {
                player.sendMessage(0, "Usage: ?pick <number or name>, or ?pick 1,2 to take two at once (?l lists the pool). Bare ?<number> works too.");
                return;
            }

            string[] tokens = target.Split(',');
            int allowed = _turnPicksLeft;
            int used = 0;
            foreach (string rawToken in tokens)
            {
                string token = rawToken.Trim();
                if (token.Length == 0)
                    continue;
                if (used >= allowed)
                {
                    player.sendMessage(-1, String.Format("Only {0} pick{1} this turn - extra names ignored.", allowed, allowed == 1 ? "" : "s"));
                    break;
                }

                Player picked = ResolvePickTarget(player, token);
                if (picked == null)
                    continue; // error already sent; let remaining tokens try

                used++;
                DraftPlayer(picked, _turnCaptain);
                _turnPicksLeft--;
                _phaseDeadline = Environment.TickCount + PickTimeout;
                if (TimerAutomation)
                    SetPhaseTicker(PickTimeout, "Pick timer: ");

                _arena.sendArenaMessage(String.Format("{0}{1} picks {2} ({3}/{4})",
                    SideMark(_teams[_turnCaptain]), player._alias, picked._alias,
                    _teams[_turnCaptain].ActivePlayerCount, _teamSize));

                if (_turnPicksLeft <= 0)
                {
                    AdvanceTurn();
                    break;
                }
            }
        }

        /// <summary>
        /// Moves a drafted player onto their mix team and takes them out of the pool for good.
        /// Handles both cases, because the pool can now contain players who are still in the pub
        /// game: a spectator is unspecced, an in-game player is moved team-to-team directly (the
        /// same split Script_CTF.AssignPlayerToTeam uses).
        /// </summary>
        private void DraftPlayer(Player picked, int slot)
        {
            _drafted.Add(picked._alias);
            _pool.Remove(picked._alias);
            MoveToTeam(picked, _teams[slot]);
        }

        /// <summary>Puts a player on a team whether they are currently spectating or in the game.</summary>
        private void MoveToTeam(Player player, Team team)
        {
            if (player.IsSpectator)
                player.unspec(team);
            else
                team.addPlayer(player, true);
            ClaimSeat(player, team);
        }

        #region Seat claims

        private void ClaimSeat(Player player, Team team)
        {
            int slot = team != null && team == _teams[0] ? 0 : team != null && team == _teams[1] ? 1 : -1;
            if (slot < 0 || player == null || player._alias == null)
                return;
            SeatClaim c = FindSeatClaim(player._alias);
            if (c == null)
            {
                c = new SeatClaim();
                _seatClaims.Add(c);
            }
            c.Alias = player._alias;
            c.TeamSlot = slot;
            c.RestoreAtTick = 0;
            c.Benched = false;   // placed again on purpose - a later drop may auto-restore once more
        }

        private SeatClaim FindSeatClaim(string alias)
        {
            if (alias == null)
                return null;
            foreach (SeatClaim c in _seatClaims)
                if (c.Alias != null && c.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        /// <summary>
        /// Snapshot both rosters at launch: captains, hand-placed players and *ovd go games are all
        /// covered this way, and the roster sizes become the "seat is still open" test - a
        /// hand-hosted 4v6 must not be judged against _teamSize.
        /// </summary>
        private void RecordSeats()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_teams[i] == null)
                {
                    _seatCount[i] = 0;
                    continue;
                }
                foreach (Player p in _teams[i].ActivePlayers.ToList())
                    ClaimSeat(p, _teams[i]);
                _seatCount[i] = _teams[i].ActivePlayerCount;
            }
        }

        private void ClearSeats()
        {
            _seatClaims.Clear();
            _seatCount[0] = 0;
            _seatCount[1] = 0;
        }

        /// <summary>
        /// A claimed player was specced while still IN the arena - by themselves or a mod's *spec.
        /// That is a bench, not a drop: any pending restore is cancelled and this claim never
        /// auto-restores again. Called from Script_CTF's Player.Leave handler.
        /// </summary>
        public void OnPlayerBenched(Player p)
        {
            if (p == null || _seatClaims.Count == 0)
                return;
            SeatClaim c = FindSeatClaim(p._alias);
            if (c == null)
                return;
            c.RestoreAtTick = 0;
            c.Benched = true;
        }

        /// <summary>
        /// Returns re-entered players to the team that had them, once their restore delay is up.
        /// Resolves the Player fresh by alias rather than holding one across the disconnect, and
        /// re-checks the seat: they may already be back (joined by hand, or a ref placed them), or
        /// the seat may have been filled while they were gone.
        /// </summary>
        private void ProcessPendingRestores(int now)
        {
            if (_seatClaims.Count == 0)
                return;
            List<SeatClaim> gone = null;
            foreach (SeatClaim c in _seatClaims.ToList())
            {
                if (c.RestoreAtTick == 0 || now < c.RestoreAtTick)
                    continue;
                c.RestoreAtTick = 0;   // one attempt per re-entry; re-entering again re-arms it

                Team team = c.TeamSlot >= 0 && c.TeamSlot <= 1 ? _teams[c.TeamSlot] : null;
                Player p = _arena.getPlayerByName(c.Alias);
                if (team == null || p == null)
                    continue;
                if (!p.IsSpectator && p._team == team)
                    continue;

                int seats = _seatCount[c.TeamSlot] > 0 ? _seatCount[c.TeamSlot] : _teamSize;
                if (team.ActivePlayerCount >= seats)
                {
                    p.sendMessage(0, String.Format("@Your seat on {0} was filled while you were gone.", team._name));
                    if (gone == null) gone = new List<SeatClaim>();
                    gone.Add(c);
                    continue;
                }

                MoveToTeam(p, team);
                _arena.sendArenaMessage(String.Format("{0}{1} reconnected and returns to {2}.",
                    SideMark(team), p._alias, team._name));
            }
            if (gone != null)
                foreach (SeatClaim c in gone)
                    _seatClaims.Remove(c);
        }

        #endregion

        private Player ResolvePickTarget(Player captain, string target)
        {
            List<Player> pool = EligiblePool();
            AssignPoolNumbers(pool);

            int number;
            if (int.TryParse(target, out number))
            {
                foreach (Player p in pool)
                {
                    int n;
                    if (_poolNumbers.TryGetValue(p._alias, out n) && n == number)
                        return p;
                }
                captain.sendMessage(-1, String.Format("No eligible player is #{0}. Use ?l for the current list.", number));
                return null;
            }

            Player exact = pool.FirstOrDefault(p => p._alias.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            // Exact short-code match. Unique by construction, and resolves cases plain prefix
            // matching cannot - e.g. code "Axi" (a full 3-char alias) must not be treated as an
            // ambiguous prefix of "Axidus" (whose code is longer).
            Dictionary<string, string> shortCodes = ComputeShortCodes(pool);
            Player codeMatch = pool.FirstOrDefault(p => shortCodes[p._alias].Equals(target, StringComparison.OrdinalIgnoreCase));
            if (codeMatch != null)
                return codeMatch;

            List<Player> partial = pool.Where(p => p._alias.StartsWith(target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (partial.Count == 0)
                partial = pool.Where(p => p._alias.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (partial.Count == 1)
                return partial[0];

            captain.sendMessage(-1, partial.Count == 0
                ? String.Format("No eligible player matches '{0}'. Use ?l for the list.", target)
                : String.Format("'{0}' matches multiple players: {1}", target,
                    String.Join(", ", partial.Select(p => p._alias).ToArray())));
            return null;
        }

        /// <summary>Captain idled through their turn - fill their remaining picks randomly.</summary>
        private void AutoPick()
        {
            List<Player> pool = EligiblePool();
            int autoCount = Math.Min(_turnPicksLeft, pool.Count);

            if (autoCount > 0)
            {
                List<Player> chosen = pool.OrderBy(p => _arena._rand.Next(0, 10000)).Take(autoCount).ToList();
                foreach (Player p in chosen)
                {
                    DraftPlayer(p, _turnCaptain);
                    _arena.sendArenaMessage(String.Format("{0}AUTO-PICK {1} ({2}/{3}) - {4} idled",
                        SideMark(_teams[_turnCaptain]), p._alias,
                        _teams[_turnCaptain].ActivePlayerCount, _teamSize, _captainAliases[_turnCaptain]));
                }
            }
            AdvanceTurn();
        }

        private void AdvanceTurn()
        {
            int needed0 = _teamSize - _teams[0].ActivePlayerCount;
            int needed1 = _teamSize - _teams[1].ActivePlayerCount;

            if (needed0 <= 0 && needed1 <= 0)
            {
                // OvD picks its ground after the roster is known.
                if (_ovdMode && _selectedBase == null)
                    StartBaseSelect();
                else
                    StartRestartCountdown();
                return;
            }

            if (EligiblePool().Count == 0)
            {
                // OvD has a hard floor: 5v5 or nothing. Short-handed OvD is not a worse game, it is
                // a different (broken) one - the defence comp cannot cover the base. So the draft
                // HOLDS here and asks for bodies rather than launching something unplayable.
                if (_ovdMode && (_teams[0].ActivePlayerCount < OVD_MIN_TEAM_SIZE
                              || _teams[1].ActivePlayerCount < OVD_MIN_TEAM_SIZE))
                {
                    int need = Math.Max(OVD_MIN_TEAM_SIZE - _teams[0].ActivePlayerCount, 0)
                             + Math.Max(OVD_MIN_TEAM_SIZE - _teams[1].ActivePlayerCount, 0);
                    // Say it once, then once a minute. With OvD timer automation the Picking poll
                    // re-fired AutoPick every second while the pool was empty, and every fire came
                    // back through here - the red/white pair every second in Travis's screenshot.
                    // Pushing the deadline out is what makes the poll wait.
                    _phaseDeadline = Environment.TickCount + PICK_REMINDER_REPEAT;
                    _arena.sendArenaMessage(String.Format("!OvD needs {0}v{0} - {1}v{2} so far, {3} more player{4} required. Type ?p - the draft resumes the moment someone joins.",
                        OVD_MIN_TEAM_SIZE, _teams[0].ActivePlayerCount, _teams[1].ActivePlayerCount,
                        need, need == 1 ? "" : "s"));
                    SetPhaseTickerText("Need players: ?p");
                    return;   // stay in Picking; the captain can pick as soon as anyone ?p's
                }

                _arena.sendArenaMessage(String.Format("!The pool is empty - teams final at {0}v{1} (wanted {2}v{2}). More players must ?p for bigger teams.",
                    _teams[0].ActivePlayerCount, _teams[1].ActivePlayerCount, _teamSize));
                StartRestartCountdown();
                return;
            }

            // Snake: pass to the other captain unless their team is already full
            int next = 1 - _turnCaptain;
            if ((next == 0 ? needed0 : needed1) <= 0)
                next = _turnCaptain;

            _turnCaptain = next;
            _turnPicksLeft = Math.Min(2, next == 0 ? needed0 : needed1);
            _phaseDeadline = Environment.TickCount + PickTimeout;

            if (_priorityAutoEnabled && _turnPicksLeft == 1 && TryAutoAssignLongestWaiting())
                return; // that call consumed the turn and re-entered AdvanceTurn

            AnnounceTurn();
        }

        /// <summary>
        /// The wait ledger's teeth (*mix priority on, DEFAULT OFF): a team's LAST pick goes to the
        /// longest-waiting player rather than being the captain's free choice - the last slot, not
        /// the first, so captains still draft their core.
        ///
        /// Fires ONLY in pairs, one per team. With an odd number of priority players, whichever
        /// captain's last slot came up first would otherwise eat the cost while the other drafted
        /// freely - a coin flip dressed as a fairness rule. So either both teams absorb one or
        /// neither does; odd leftovers stay advisory. Returns true when it consumed the turn.
        /// </summary>
        private bool TryAutoAssignLongestWaiting()
        {
            int me = _turnCaptain;
            int other = 1 - me;

            if (_priorityTaken[me] > 0)
                return false; // one forced pick per team, ever

            // The other team's last slot is already gone and they never absorbed one - firing now
            // would recreate exactly the asymmetry this rule exists to prevent.
            bool otherSlotGone = _teams[other] != null && _teams[other].ActivePlayerCount >= _teamSize;
            if (_priorityTaken[other] == 0 && otherSlotGone)
                return false;

            List<Player> eligible = EligiblePool().Where(p => WaitStreak(p) >= PRIORITY_AUTO_STREAK)
                .OrderByDescending(p => WaitStreak(p)).ToList();

            int needed = _priorityTaken[other] > 0 ? 1 : 2; // hold one back while their slot is ahead
            if (eligible.Count < needed)
                return false;

            Player pick = eligible[0];
            int streak = WaitStreak(pick);
            DraftPlayer(pick, me);
            _priorityTaken[me]++;
            _turnPicksLeft--;
            _arena.sendArenaMessage(String.Format("{0}PRIORITY PICK: {1} joins {2} - sat out {3} mixes running (last slot on BOTH teams is reserved).",
                PRIORITY_MARK, pick._alias, _teams[me]._name, streak));
            AdvanceTurn();
            return true;
        }

        private void AnnounceTurn()
        {
            // No arena line - the bubble names whose turn it is and the captain gets a private
            // bonged PM. Pick announcements remain the draft's public record.
            // The whole alias, not its first three letters (Travis, 2026-09-12): "Waiting on Ste"
            // told nobody who was up when two captains shared a prefix. Capped at TICKER_ALIAS_MAX
            // so a long alias plus the countdown still fits the bubble.
            string captName = TickerAlias(_captainAliases[_turnCaptain]);
            if (TimerAutomation)
                SetPhaseTicker(PickTimeout, captName + " pick: ");
            else
                SetPhaseTickerText("Waiting on " + captName);

            Player capt = CurrentCaptain();
            if (capt != null)
            {
                string hint = _hintedCaptains.Add(capt._alias)
                    ? " ?pick <# or short name>; ?pick 1,2 takes two. ?lr shows what everyone plays. (?bong off mutes this chime)" : "";
                capt.sendMessage(GetTurnBong(capt._alias), String.Format("Your pick - {0} pick{1}!{2}",
                    _turnPicksLeft, _turnPicksLeft == 1 ? "" : "s", hint));

                // What they have built so far, so the hole they still need is obvious
                Team mine = _teams[_turnCaptain];
                if (mine != null)
                    capt.sendMessage(0, String.Format("~Your team so far: {0}",
                        _roles.SummariseTeam(mine.ActivePlayers.ToList())));

                SendPoolList(capt);
            }
        }

        #endregion

        #region Vote-out

        /// <summary>
        /// ?voteout &lt;captain&gt;: 8 votes remove a captain and bar them until zone restart. During
        /// signup the slot reopens; mid-draft the session is cancelled for a ref restart (the ban
        /// carries over, so they cannot simply ?c again).
        /// </summary>
        private void HandleVoteOut(Player voter, string target)
        {
            if (_state != MixState.CaptainSignup && _state != MixState.Picking)
            {
                voter.sendMessage(-1, "Captain vote-outs only run during signup or picking.");
                return;
            }

            List<string> current = _captainAliases.Where(a => a != null).ToList();
            if (current.Count == 0)
            {
                voter.sendMessage(-1, "There is no captain to vote out yet.");
                return;
            }

            string resolved = (target.Length == 0 && current.Count == 1) ? current[0]
                : current.FirstOrDefault(a => a.Equals(target, StringComparison.OrdinalIgnoreCase))
                  ?? (target.Length > 0 ? current.FirstOrDefault(a => a.StartsWith(target, StringComparison.OrdinalIgnoreCase)) : null);
            if (resolved == null)
            {
                voter.sendMessage(-1, String.Format("Usage: ?voteout <captain>. Current captain{0}: {1}",
                    current.Count == 1 ? "" : "s", String.Join(", ", current.ToArray())));
                return;
            }
            if (voter._alias.Equals(resolved, StringComparison.OrdinalIgnoreCase))
            {
                voter.sendMessage(-1, "You cannot vote yourself out.");
                return;
            }
            if (!_pool.Contains(voter._alias) && !_drafted.Contains(voter._alias) && !IsCaptain(voter._alias))
            {
                voter.sendMessage(-1, "Only players in the mix can vote.");
                return;
            }

            HashSet<string> votes;
            if (!_voteoutVotes.TryGetValue(resolved, out votes))
                _voteoutVotes[resolved] = votes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!votes.Add(voter._alias))
            {
                voter.sendMessage(0, String.Format("You already voted against {0} ({1}/{2}).", resolved, votes.Count, VOTEOUT_THRESHOLD));
                return;
            }

            // First vote announces so others can pile on; later votes ack privately (no spam)
            if (votes.Count == 1)
                _arena.sendArenaMessage(String.Format("!A vote to remove captain {0} has started ({1}/{2}) - type ?voteout {0}",
                    resolved, votes.Count, VOTEOUT_THRESHOLD));
            else
                voter.sendMessage(0, String.Format("Vote recorded against {0} ({1}/{2}).", resolved, votes.Count, VOTEOUT_THRESHOLD));

            if (votes.Count < VOTEOUT_THRESHOLD)
                return;

            _voteoutBanned.Add(resolved);
            _arena.sendArenaMessage(String.Format("!{0} has been VOTED OUT as captain and is now ineligible to captain.", resolved), 30);

            if (_state == MixState.CaptainSignup)
            {
                for (int i = 0; i < 2; i++)
                    if (_captainAliases[i] != null && _captainAliases[i].Equals(resolved, StringComparison.OrdinalIgnoreCase))
                        _captainAliases[i] = null;
                _arena.sendArenaMessage("&A captain slot is open again - type ?c!");
            }
            else
            {
                Cancel(String.Format("captain {0} was voted out mid-draft - a ref can restart with *mix start", resolved));
            }
        }

        #endregion

        #region Launch and mercy

        private void StartRestartCountdown()
        {
            // OvD's own 18 minute clock already opens with a 3 minute setup window (starts at
            // 18:00, "GO" at 15:00 - see LaunchOvd), so a SEPARATE 2 minute pre-countdown here is
            // redundant dead time on top of it. Skip straight to Launch(); MIX still gets the real
            // 2 minute countdown below, since it has no equivalent built-in setup window.
            if (_ovdMode)
            {
                Launch();
                return;
            }

            _state = MixState.Countdown;
            _restartDeadline = Environment.TickCount + RESTART_COUNTDOWN;
            SetPhaseTicker(RESTART_COUNTDOWN, "Game starts: ");
            _arena.sendArenaMessage(String.Format("&Teams picked! {0} ({1}) vs {2} ({3})",
                _teams[0]._name, _teams[0].ActivePlayerCount, _teams[1]._name, _teams[1].ActivePlayerCount), 30);
            _arena.sendArenaMessage("*Game restarts in 2 minutes - set your class and build now!");
        }

        private void Launch()
        {
            // Capture what this session was BEFORE anything resets - Game.End needs it afterwards.
            RememberSession();

            RecordSeats();      // everyone is placed now - these are the seats reconnects return to
            UpdateWaitLedger(); // resets pickers to 0, strikes everyone left in the pool

            // Stay resident through the game (Running, not Idle) so Poll can notice when it ends.
            _state = MixState.Running;
            ClearPhaseTicker();

            if (_ovdMode)
                LaunchOvd();
            else
                LaunchMix();
        }

        /// <summary>
        /// MIX: a proper restart. Neutral start, everyone in at once - deliberately a bit hectic.
        /// </summary>
        private void LaunchMix()
        {
            // This gameEnd is bookkeeping, not a finished match - do not let it record stats.
            SuppressGameEndStats = true;
            if (_arena._bGameRunning)
                _arena.gameEnd();
            _arena.gameStart(); // resets every team's _currentGameKills
            SuppressGameEndStats = false;

            // Re-record AFTER the restart: the restart's Game.End consumed the record written in
            // Launch(), which is why mix matches were coming out labelled "Pub".
            RememberSession();

            _arena.sendArenaMessage("&GO GO GO! Good luck!", 4);
        }

        /// <summary>
        /// OvD: NO game restart. The base, drops and flag are already placed, and a restart would
        /// only undo them (StartGame calls flagReset + SpawnMapFlags). Instead the match is framed
        /// by a long clock: the timer starts at 18:00 and everyone GOES ON 15:00, which gives both
        /// sides three minutes to buy, build and get into position.
        /// </summary>
        private void LaunchOvd()
        {
            // The base was configured when it was chosen; nothing since then has reset the flags,
            // because we deliberately never call gameStart here. If the game died in between all
            // the same (a ref *endgame, everyone specced), start one and redo the base rather than
            // hang a clock on nothing.
            if (!_arena._bGameRunning)
            {
                EnsureLiveGame();
                if (_selectedBase != null && _arena._bGameRunning)
                {
                    _script.SetupBase(_selectedBase, null);
                    _script.PositionOvdDefense(_selectedBase, _teams[_defenseSlot]);
                }
            }
            _script.ApplyOvdGameTimer(OVD_MATCH_MINUTES);

            // A run against real (run.ts) bots once showed the clock start fine but EVERYTHING
            // below it silently never fire - no announcement, no offence warp, no exception in any
            // log file (exceptions.txt, errors.txt, ZoneServerHandler1.txt, the per-arena log, or
            // warnings.txt). Whatever throws here was invisible before; wrapping it at least turns
            // "nothing happened and nobody knows why" into a logged line to chase next time.
            try
            {
                // Defence was already placed when the base was chosen and has been building since;
                // re-warping them here would yank them off whatever they are mid-way through. Only
                // the offence is staged at go time.
                int staged = 0;
                if (_selectedBase != null)
                    staged = _script.PositionOvdOffense(_selectedBase, _teams[1 - _defenseSlot], false);

                // One line. The sides were announced when the base was picked; the comp warning, if
                // any, goes to the team it concerns.
                _arena.sendArenaMessage(String.Format("#GO ON 15:00 - the clock runs from {0}:00. {1}",
                    OVD_MATCH_MINUTES,
                    staged > 0 ? String.Format("Offence staged at the {0} entrance.", _selectedBase)
                               : "Offence spawns normally."), 4);
                AnnounceCompAdvisory();
            }
            catch (Exception ex)
            {
                Console.WriteLine(String.Format("Error in LaunchOvd (base={0}): {1}", _selectedBase ?? "?", ex));
                _arena.sendArenaMessage("!OvD launch hit an error positioning/announcing the match - the clock is running regardless. Check the server console.");
            }
        }



        /// <summary>
        /// Says what each side actually fielded and flags the classic holes. ADVISORY ONLY: a game
        /// that refuses to start because nobody wants to play medic is worse than a game with no
        /// medic, and the same choice was made for mix role preferences.
        /// </summary>
        private void AnnounceCompAdvisory()
        {
            // Only the holes, and only to the team that has them. The per-side class summaries that
            // used to print here were two more arena lines nobody could act on (captains already saw
            // their roster privately during the draft).
            for (int i = 0; i < 2; i++)
            {
                if (_teams[i] == null)
                    continue;
                List<Player> members = _teams[i].ActivePlayers.ToList();

                List<string> missing = new List<string>();
                bool isDefense = _ovdMode && i == _defenseSlot;
                if (isDefense)
                {
                    if (!TeamHasClass(members, "Field Medic")) missing.Add("Medic");
                    if (!TeamHasClass(members, "Combat Engineer")) missing.Add("Engineer");
                }
                else if (_ovdMode)
                {
                    if (!TeamHasClass(members, "Squad Leader")) missing.Add("Squad Leader");
                }

                if (missing.Count == 0)
                    continue;
                string line = String.Format("!{0} has no {1} - someone may want to swap.",
                    SlotName(i), String.Join(" and no ", missing.ToArray()));
                foreach (Player p in members)
                    p.sendMessage(0, line);
            }
        }

        /// <summary>
        /// The match at a glance, two lines, coloured by side ('!' = Collective red, none = Titan
        /// green): DEFENSE = who (team) base X, OFFENSE = who (team). "Who" is the captain, or the
        /// team itself for a hand-hosted game. Everything else the launch used to say - setup ran,
        /// minerals spawned, defence placed, sides swapped, X vs Y - is implied by these or private.
        /// </summary>
        private void AnnounceSides(bool setup, string offenseTail)
        {
            Team def = _teams[_defenseSlot], off = _teams[1 - _defenseSlot];
            if (def == null || off == null)
                return;
            string defWho = _captainAliases[_defenseSlot] ?? def._name;
            string offWho = _captainAliases[1 - _defenseSlot] ?? off._name;
            _arena.sendArenaMessage(String.Format("{0}DEFENSE = {1}   ({2})   base {3}{4}",
                SideMark(def), defWho, def._name, _selectedBase ?? "?", setup ? " - build now" : ""), 30);
            _arena.sendArenaMessage(String.Format("{0}OFFENSE = {1}   ({2}){3}",
                SideMark(off), offWho, off._name, offenseTail != null ? "   " + offenseTail : ""));
        }

        /// <summary>True if anyone on the team lists that class as their MAIN.</summary>
        private bool TeamHasClass(List<Player> members, string className)
        {
            foreach (Player p in members)
            {
                string main = _roles.Primary(p._alias);
                if (main == null)
                    continue;
                string cls = _roles.ClassOf(main);
                if (cls != null && cls.Equals(className, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        #endregion

        #region Display

        private void Cancel(string reason)
        {
            _state = MixState.Idle;
            _manualBase = null;
            _pool.Clear();
            _drafted.Clear();
            ClearSeats();
            ClearPhaseTicker();
            // The hold parked the arena's state machine; with a game still live that would restart
            // it on the next poll. Put the machine back on the running game.
            _script.ReleaseAutomaticGameStart();
_arena.sendArenaMessage(String.Format("!Mix ended - {0}.", reason));
        }

        private void SendStatus(Player player)
        {
            switch (_state)
            {
                case MixState.Idle:
                    player.sendMessage(0, String.Format("No mix session is active. A ref can start one with *mix <size> or *ovd. Timer automation: mix {0}, OvD {1}.",
                        _autoPickEnabled ? "ON" : "OFF", _ovdAutoPick ? "ON" : "OFF"));
                    break;
                case MixState.CaptainSignup:
                    player.sendMessage(0, String.Format("MIX ({0}v{0}): captain signup open - ?c to sign up, ?p to play. Pool: {1}. Captains so far: {2}",
                        _teamSize, _pool.Count,
                        String.Join(", ", _captainAliases.Where(a => a != null).DefaultIfEmpty("none").ToArray())));
                    break;
                case MixState.BaseSelect:
                    player.sendMessage(0, String.Format("OvD ({0}v{0}): {1} is picking the base to defend (?base <name>).",
                        _teamSize, _captainAliases[_defenseSlot]));
                    break;
                case MixState.Picking:
                    player.sendMessage(0, String.Format("MIX ({0}v{0}): picking - {1} ({2}/{0}) vs {3} ({4}/{0}). {5} is on the clock.",
                        _teamSize, _captainAliases[0], _teams[0].ActivePlayerCount,
                        _captainAliases[1], _teams[1].ActivePlayerCount, _captainAliases[_turnCaptain]));
                    break;
                case MixState.Countdown:
                    player.sendMessage(0, String.Format("Teams picked - the game restarts in {0}s (*mix go to skip).",
                        Math.Max(0, (_restartDeadline - Environment.TickCount) / 1000)));
                    break;
                case MixState.Running:
                    // Nothing to police mid-match any more - just notice when it ends.
                    // (A kill-differential mercy rule lived here until 2026-08-29; removed
                    //  because CTF is far less KDA-driven than USL.)
                    if (!_arena._bGameRunning)
                    {
                        _state = MixState.Idle;
                        ClearSeats();
                    }
                    break;
            }
            player.sendMessage(0, "~Commands: ?p play, ?np sit out, ?c captain, ?l pool, ?lr roles, ?pick <#|name|1,2>, ?voteout <captain>, ?bong off");
            player.sendMessage(0, "~Set what you play:  ?role dinfcaw, oinf, dhvy   (main first) | ?role list");
        }

        /// <summary>
        /// Pick-codes for the ?l grid, accepted by ?pick as an exact match. The WHOLE alias unless
        /// it is longer than TICKER_ALIAS_MAX characters, in which case the first TICKER_ALIAS_MAX.
        /// The old three-letter codes (Travis, 2026-09-12: "overly restrictive") made captains
        /// decode "Ste"/"Sha" against the roster; the pool NUMBER is what stays exact when two
        /// long aliases share a prefix.
        /// </summary>
        private Dictionary<string, string> ComputeShortCodes(List<Player> pool)
        {
            var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Player p in pool)
                codes[p._alias] = TickerAlias(p._alias);
            return codes;
        }

        private void SendPoolList(Player player)
        {
            List<Player> pool = EligiblePool();
            if (pool.Count == 0)
            {
                player.sendMessage(0, "Pool: empty (players type ?p to join).");
                return;
            }
            AssignPoolNumbers(pool);
            Dictionary<string, string> codes = ComputeShortCodes(pool);

            // Anyone carrying a sit-out streak is pulled OUT of the compact grid and given their own
            // block at the BOTTOM - one per line, rules above and below, so it cannot be skimmed past
            // the way a comma list can. Pool numbers are stable either way, so ?pick works the same
            // from either section.
            List<Player> waiting = pool.Where(p => WaitStreak(p) > 0)
                .OrderByDescending(p => WaitStreak(p)).ThenBy(p => _poolNumbers[p._alias]).ToList();
            List<Player> fresh = pool.Where(p => WaitStreak(p) == 0)
                .OrderBy(p => _poolNumbers[p._alias]).ToList();

            player.sendMessage(0, String.Format("Pool ({0}) - ?pick by number or short name   (?lr = who plays what):", pool.Count));

            if (fresh.Count > 0)
            {
                List<string> cells = fresh
                    .Select(p => String.Format("{0}.{1}", _poolNumbers[p._alias], codes[p._alias])).ToList();
                int cellWidth = cells.Max(c => c.Length) + 2;
                const int PER_ROW = 4;
                int row = 0;
                for (int i = 0; i < cells.Count; i += PER_ROW)
                {
                    StringBuilder line = new StringBuilder();
                    for (int j = i; j < Math.Min(i + PER_ROW, cells.Count); j++)
                        line.Append(cells[j].PadRight(cellWidth));
                    string prefix = (row++ % 2 == 1) ? "~" : "";
                    player.sendMessage(0, prefix + line.ToString().TrimEnd());
                }
            }

            if (waiting.Count == 0)
                return;

            player.sendMessage(0, PRIORITY_GAP);
            player.sendMessage(0, PRIORITY_RULE);
            player.sendMessage(0, PRIORITY_MARK + "   HAVEN'T PLAYED - PICK THESE FIRST");
            player.sendMessage(0, PRIORITY_RULE);
            int labelWidth = waiting.Max(p => (_poolNumbers[p._alias] + "." + p._alias).Length);
            foreach (Player p in waiting)
            {
                int streak = WaitStreak(p);
                string label = (_poolNumbers[p._alias] + "." + p._alias).PadRight(labelWidth);
                string main = _roles.Primary(p._alias);
                player.sendMessage(0, String.Format("{0}   {1}  {2}  -  sat out {3} mix{4}{5}",
                    PRIORITY_MARK, label,
                    main == null ? "(no prefs)" : main,
                    streak, streak == 1 ? "" : "es",
                    streak >= PRIORITY_URGENT_STREAK ? "   <<<" : ""));
            }
            player.sendMessage(0, PRIORITY_RULE);
            player.sendMessage(0, PRIORITY_GAP);
        }

        #endregion

        #region Tickers and teams

        // Top-right bubble countdown, the way both USL mixes and the CTF OvD script do it: colour 1,
        // ticker index 1 (CTF's game clock owns index 3), timer in CENTISECONDS, label carrying its
        // trailing space, and an explicit clear on every transition - a stale timed ticker keeps
        // counting down for late joiners otherwise.
        private const byte PHASE_TICKER_COLOUR = 1;
        private const int PHASE_TICKER_IDX = 1;

        private void SetPhaseTicker(int durationMs, string label)
        {
            _arena.setTicker(PHASE_TICKER_COLOUR, PHASE_TICKER_IDX, durationMs / 10, label);
        }

        private void SetPhaseTickerText(string label)
        {
            _arena.setTicker(PHASE_TICKER_COLOUR, PHASE_TICKER_IDX, 0, label);
        }

        private void ClearPhaseTicker()
        {
            _arena.setTicker(PHASE_TICKER_COLOUR, PHASE_TICKER_IDX, 0, "");
        }

        /// <summary>'!' colors Collective-side lines; Titan-side lines stay unprefixed, so the two
        /// drafts read apart at a glance.</summary>
        private string SideMark(Team team)
        {
            if (team != null && (team._name.EndsWith(" C") || team._name.IndexOf("Collective", StringComparison.OrdinalIgnoreCase) >= 0))
                return "!";
            return "";
        }

        /// <summary>Team name minus its trailing " T"/" C" side suffix, for pairing comparisons.</summary>
        private static string BaseTeamName(string teamName)
        {
            if (String.IsNullOrEmpty(teamName))
                return teamName;
            if (teamName.EndsWith(" T") || teamName.EndsWith(" C"))
                return teamName.Substring(0, teamName.Length - 2);
            return teamName;
        }

        /// <summary>
        /// A random cfg team for the given side. CTF's paired teams are "&lt;base&gt; T" / "&lt;base&gt; C"
        /// (MOON, Mir, Juicy Fruit, ... - TeamInfo2..33 in the zone cfg), so the suffix is matched at
        /// the END of the name rather than anywhere in it: CTF.cs tests Contains(" T") in a dozen
        /// places, and any name whose second word starts with T would otherwise be mistaken for a
        /// Titan team. Excluding the other pick's base name gives varied matchups instead of the same
        /// uniforms every mix.
        /// </summary>
        // cfg teams reserved for league squads (freeinf.org match automation); a random mix must never wear them
        private static readonly HashSet<string> LEAGUE_RESERVED_TEAMS =
            new HashSet<string>(new string[] { "187", "BO", "DF", "PTI" }, StringComparer.OrdinalIgnoreCase);

        private Team PickRandomSideTeam(bool titanSide, Team exclude)
        {
            string suffix = titanSide ? " T" : " C";
            List<Team> candidates = new List<Team>();

            foreach (var teamInfo in _config.teams)
            {
                string n = teamInfo == null ? null : teamInfo.name;
                if (String.IsNullOrEmpty(n) || !n.EndsWith(suffix))
                    continue;
                if (LEAGUE_RESERVED_TEAMS.Contains(BaseTeamName(n)))
                    continue;

                Team team = _arena.getTeamByName(n);
                if (team == null || team == exclude)
                    continue;
                if (exclude != null && BaseTeamName(n).Equals(BaseTeamName(exclude._name), StringComparison.OrdinalIgnoreCase))
                    continue;
                // Don't hijack a team that already has players on it (another event, a private team)
                if (team.ActivePlayerCount > 0)
                    continue;
                candidates.Add(team);
            }

            if (candidates.Count == 0)
            {
                // Fall back to the zone's two default teams rather than failing the whole mix
                string fallback = titanSide ? "Titan Militia" : "Collective";
                Team def = _arena.getTeamByName(fallback);
                if (def != null && def != exclude)
                    return def;
                return null;
            }
            return candidates[_arena._rand.Next(0, candidates.Count)];
        }

        #endregion

        #region Prefs

        /// <summary>
        /// Loads MixPrefs.txt: tab delimited, alias first, then key=value fields. A bare numeric
        /// field is the older "alias&lt;tab&gt;2" bong format and still loads.
        /// </summary>
        private void EnsurePrefsLoaded()
        {
            if (_bongPrefs != null)
                return;
            _bongPrefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(PREFS_PATH))
                {
                    foreach (string line in File.ReadAllLines(PREFS_PATH))
                    {
                        if (String.IsNullOrEmpty(line))
                            continue;
                        string[] parts = line.Split('\t');
                        string alias = parts[0];
                        if (alias.Length == 0)
                            continue;

                        for (int i = 1; i < parts.Length; i++)
                        {
                            string field = parts[i];
                            int eq = field.IndexOf('=');
                            if (eq < 0)
                            {
                                int legacy;
                                if (int.TryParse(field, out legacy))
                                    _bongPrefs[alias] = legacy;
                                continue;
                            }
                            string key = field.Substring(0, eq);
                            string val = field.Substring(eq + 1);
                            if (key == "bong")
                            {
                                int b;
                                if (int.TryParse(val, out b))
                                    _bongPrefs[alias] = b;
                            }
                        }
                    }
                }
            }
            catch { /* unreadable prefs = defaults */ }
        }

        private void SavePrefs()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (string a in _bongPrefs.Keys)
                {
                    StringBuilder sb = new StringBuilder(a);
                    sb.Append("\tbong=").Append(_bongPrefs[a]);
                    lines.Add(sb.ToString());
                }
                File.WriteAllLines(PREFS_PATH, lines.ToArray());
            }
            catch { /* a read-only disk must not break the mix */ }
        }

        private int GetTurnBong(string alias)
        {
            EnsurePrefsLoaded();
            int v;
            return _bongPrefs.TryGetValue(alias, out v) ? v : TURN_BONG_DEFAULT;
        }

        #endregion

        #region Small helpers

        private Player CurrentCaptain()
        {
            return _arena.getPlayerByName(_captainAliases[_turnCaptain]);
        }

        private bool IsCaptain(string alias)
        {
            return (_captainAliases[0] != null && _captainAliases[0].Equals(alias, StringComparison.OrdinalIgnoreCase))
                || (_captainAliases[1] != null && _captainAliases[1].Equals(alias, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Bare ?&lt;number&gt; picks, including comma-separated multi-picks like ?1,2</summary>
        private static bool IsPickShorthand(string s)
        {
            if (String.IsNullOrEmpty(s))
                return false;
            bool anyDigit = false;
            foreach (char c in s)
            {
                if (Char.IsDigit(c))
                    anyDigit = true;
                else if (c != ',')
                    return false;
            }
            return anyDigit;
        }

        #endregion
    }
}
