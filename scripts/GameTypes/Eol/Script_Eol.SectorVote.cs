using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-21, owner: "the sectors A/B/C this also can be changed by command or player vote"; "if votes are 50/50 then
    /// have it up to chance")
    ///
    /// Mods:    *sector a | b | c       make that the open sector
    /// (2026-11-21, owner: "lets remove voting and have just mods do it") no player vote.
    /// A change = 30-second countdown, then bots, bases, turrets and the swarm clear, the sector layout and flags reset, and
    /// everyone is sent to the dropship (the daily sector change, newSectorDSRecall, does the same).
    /// </summary>
    public partial class Script_Eol
    {
        const int SectorChangeCountdownSec = 30;

        bool _sectorChangePending;
        string _pendingSector;
        int _sectorChangeDueAt;

        /// <summary>Runs the pending change once - from the countdown ticker, or from the poll if the ticker was lost.</summary>
        void RunPendingSectorChange()
        {
            if (!_sectorChangePending || _pendingSector == null)
                return;
            string sector = _pendingSector;
            _pendingSector = null;
            try { ApplySectorChange(sector); }
            catch (Exception e) { Log.write(TLog.Error, "[Eol] Sector change failed: {0}", e); }
            _sectorChangePending = false;
            _sectorChangeCountdownActive = false;
            _arena.setTicker(1, KothLobbyTickerIdx, 0, "");
            _arena.sendArenaMessage("&" + sector + " is open!", _config.flag.victoryWarningBong);
        }


        static string SectorArg(string payload)
        {
            string arg = (payload ?? "").Trim().ToLowerInvariant();
            if (arg.StartsWith("sector "))
                arg = arg.Substring(7).Trim();
            return arg;
        }

        /// <summary>*sector; true = handled.</summary>
        bool TrySectorModCommand(InfServer.Game.Player player, string cmd, string payload)
        {
            if (cmd != "sector")
                return false;
            if (!IsEolModerator(player))
            {
                player.sendMessage(-1, "Moderators only.");
                return true;
            }
            string err = StartSectorChange(SectorArg(payload), player._alias);
            if (err != null)
                player.sendMessage(-1, "&" + err);
            return true;
        }

        string OpenSectorsText()
        {
            if (_eol == null || _eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return "none";
            return string.Join(" + ", _eol._activeSectors);
        }

        /// <summary>From the main poll: runs a sector change whose countdown ticker was lost.</summary>
        void PollSectorVote(int now)
        {
            if (_sectorChangePending && _pendingSector != null && now - _sectorChangeDueAt >= 2000)
                RunPendingSectorChange();
            PollHiddenSectorVote(now);
        }

        // ---------- (2026-11-21, owner: "can we also get a vote back for sectors please. ?vote sector ... i.e ?vote sector A";
        // "have it hidden though so no one knows about it unless told") a HIDDEN player vote: no help text, no arena
        // announcement, a bare or wrong ?vote is ignored silently. Voters only hear back privately. 2 minutes from the
        // first vote; most votes wins, a tie is up to chance; the change itself is the normal *sector countdown. One vote an hour.
        const int SectorVoteMs = 120000;
        const int SectorVoteCooldownMs = 60 * 60 * 1000;   // (2026-11-21, owner: "make it so you can only vote every hour") from the end of the last vote, whatever it picked
        bool _sectorVoteRunning;
        int _sectorVoteEndsAt, _lastSectorVoteChange;
        bool _hadSectorVote;   // (2026-11-21 fix: the old int.MinValue / 2 start overflowed against a large server TickCount - every vote was blocked with a huge wait)
        readonly Dictionary<string, string> _sectorVotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // alias -> a/b/c
        static readonly string[] SectorVoteOptions = { "a", "b", "c" };

        /// <summary>?vote sector a|b|c; true = handled (anything else after ?vote is swallowed silently).</summary>
        bool TrySectorVoteCommand(InfServer.Game.Player player, string cmd, string payload)
        {
            if (cmd != "vote")
                return false;
            string[] parts = (payload ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] != "sector" || !SectorVoteOptions.Contains(parts[1]))
                return true;   // (hidden: no hint)
            string arg = parts[1];
            int now = Environment.TickCount;
            if (!_sectorVoteRunning)
            {
                if (_sectorChangePending)
                {
                    player.sendMessage(-1, "&A sector change is already on its way.");
                    return true;
                }
                if (_hadSectorVote && unchecked(now - _lastSectorVoteChange) < SectorVoteCooldownMs)
                {
                    int min = (SectorVoteCooldownMs - (now - _lastSectorVoteChange)) / 60000 + 1;
                    player.sendMessage(-1, "&One vote an hour - the next in " + min + " min.");
                    return true;
                }
                _sectorVoteRunning = true;
                _sectorVoteEndsAt = now + SectorVoteMs;
                _sectorVotes.Clear();
            }
            _sectorVotes[player._alias] = arg;
            player.sendMessage(0, "&Your vote: Sector " + arg.ToUpperInvariant() + " (" + SectorVoteTally() + ", "
                + Math.Max(1, (_sectorVoteEndsAt - now) / 1000) + "s left).");
            return true;
        }

        string SectorVoteTally()
        {
            return string.Join(" ", SectorVoteOptions.Select(o => o.ToUpperInvariant() + ":" + _sectorVotes.Values.Count(v => v == o)));
        }

        void PollHiddenSectorVote(int now)
        {
            if (!_sectorVoteRunning || now - _sectorVoteEndsAt < 0)
                return;
            _sectorVoteRunning = false;
            int best = SectorVoteOptions.Max(o => _sectorVotes.Values.Count(v => v == o));
            List<string> top = SectorVoteOptions.Where(o => _sectorVotes.Values.Count(v => v == o) == best).ToList();
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            string win = top[_rand.Next(top.Count)];   // (a tie: up to chance)
            string tally = SectorVoteTally();
            List<string> voters = new List<string>(_sectorVotes.Keys);
            _sectorVotes.Clear();
            string err = StartSectorChange(win, "the vote");   // (the normal countdown message - no mention of a vote)
            _lastSectorVoteChange = now;   // (the next vote in an hour, whatever this one picked)
            _hadSectorVote = true;
            string note = "&Vote result (" + tally + "): Sector " + win.ToUpperInvariant() + (top.Count > 1 ? " (tie - chance picked)" : "")
                + (err == null ? "." : " - " + err);
            foreach (InfServer.Game.Player p in _arena.Players)
                if (p != null && voters.Contains(p._alias))
                    p.sendMessage(0, note);   // (only the voters hear it)
        }

        /// <summary>null = the change is on its way; else why not.</summary>
        string StartSectorChange(string arg, string by)
        {
            if (arg != "a" && arg != "b" && arg != "c")
                return "Use: a, b or c.";
            string sector = "Sector " + arg.ToUpperInvariant();
            if (string.Equals(EolBoundaries.PinnedFirstSector, sector, StringComparison.OrdinalIgnoreCase)
                && _eol != null && _eol._activeSectors != null && _eol._activeSectors.Count > 0 && _eol._activeSectors[0] == sector)
                return sector + " is already open.";
            if (_sectorChangePending)
                return "A sector change is already on its way.";
            _sectorChangePending = true;
            _pendingSector = sector;
            _sectorChangeDueAt = Environment.TickCount + SectorChangeCountdownSec * 1000;
            _sectorChangeCountdownActive = true;   // (keeps the KOTH lobby off this ticker slot - it was overwriting the countdown, so the change never ran)
            _arena.sendArenaMessage(string.Format("&{0} opens in {1}s - everyone back to the dropship.", sector, SectorChangeCountdownSec), _config.flag.victoryWarningBong);   // (short: long lines wrap badly in the client)
            _arena.setTicker(1, KothLobbyTickerIdx, SectorChangeCountdownSec * 100, "Sector change in: ", delegate () { RunPendingSectorChange(); });
            return null;
        }

        /// <summary>The change itself - the daily sector change's steps (newSectorDSRecall) with the open sector pinned first.</summary>
        void ApplySectorChange(string sector)
        {
            Log.write(TLog.Normal, "[Eol] Sector change to {0}: clearing the field.", sector);
            foreach (InfServer.Game.Player p in _arena.Players)
                p.clearProjectiles();
            _bbetweengames = true;
            if (_eol != null)
                _eol.bbetweengames = true;
            foreach (Vehicle v in _arena.Vehicles.ToList())
                if (v._type.Type == VehInfo.Types.Computer)
                    EnqueueVehicleDespawn(v);
            _usedpylons.Clear();
            EndAllSwarms(Environment.TickCount);   // (the Queen or the King, whichever is up)
            DestroyAllTrackedScriptBots();
            ClearAllDefenseBaseVehicleLists();
            _minX = 0; _maxX = 0; _minY = 0; _maxY = 0;

            EolBoundaries.PinnedFirstSector = sector;   // (the open sector; with more players more open after it)
            if (_eol != null)
            {
                _eol.RotateMatchSectors();
                _eol.RebuildBorderWallVehicles();
            }

            // flags back to their spawns; those in closed sectors are taken off the map
            _arena.flagReset();
            _arena.flagSpawn();
            HideFlagsInClosedSectors();
            Helpers.Object_FlagsReset(_arena.Players);
            Helpers.Object_Flags(_arena.Players, _arena._flags.Values.Where(f => f.bActive));

            if (_eol != null)
                foreach (InfServer.Game.Player p in _arena.PlayersIngame.ToList())
                    if (!_eol.TryWarpPlayerToDropship(p, announce: false))
                        p.sendMessage(-1, "There was an issue sending you to the dropship, please contact the dev team about this issue.");

            _bbetweengames = false;
            if (_eol != null)
                _eol.bbetweengames = false;
            if (EolBaseSetupEnabled)
                RebuildBotHqSlotTemplates();
            Log.write(TLog.Normal, "[Eol] Sector change done: open = {0}.", OpenSectorsText());
        }
    }
}
