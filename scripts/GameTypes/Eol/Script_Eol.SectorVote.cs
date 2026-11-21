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
