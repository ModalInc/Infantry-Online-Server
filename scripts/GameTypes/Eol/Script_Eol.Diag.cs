using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using InfServer.Game;
using InfServer.Protocol;
using InfServer.Bots;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-21, owner: "monitor it now so bots do still shoot in the spawn zones of real players") every 5 s a line per
    /// real player to server-eol\eol-diag.log: where they are (square, tile, spawn zone, terrain safety) and the nearest enemy
    /// bots (team, distance, line of sight, targeting them or not). The server only logs to its window - this file can be read.
    /// Off: DiagEnabled = false.
    /// </summary>
    public partial class Script_Eol
    {
        const bool DiagEnabled = false;   // (test servers: true = server-eol/eol-diag.log every 5 s)
        int _tickDiag;
        string _diagFile;

        void PollSpawnZoneDiag(int now)
        {
            if (!DiagEnabled || _arena == null || now - _tickDiag < 5000)
                return;
            _tickDiag = now;
            try
            {
                if (_diagFile == null)
                    _diagFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "eol-diag.log");
                List<string> lines = new List<string>();
                string stamp = DateTime.Now.ToString("HH:mm:ss");
                foreach (InfServer.Game.Player p in _arena.PlayersIngame)
                {
                    if (p == null || p._state == null || p.IsDead)
                        continue;
                    short x = p._state.positionX, y = p._state.positionY;
                    var terr = _arena.getTerrain(x, y);
                    double edge = BufferEdgeDistance(x, y);
                    lines.Add(string.Format("{0} {1} team={2} {3} tile={4},{5} spawnZone={6} (edge {7:0}) safety={8} terrain={9}",
                        stamp, p._alias, p._team != null ? p._team._name : "-", Helpers.posToLetterCoord(x, y), x / 16, y / 16,
                        edge < 0 ? "IN" : "out", edge, terr != null && terr.safety, _arena.getTerrainID(x, y)));
                    var bots = new List<KeyValuePair<double, Bot>>();
                    foreach (Bot b in _arena.ActiveBots)
                    {
                        if (b == null || b.IsDead || b.bCondemned || b._state == null || b._team == null || b._team == p._team)
                            continue;
                        double d = Helpers.distanceTo(b._state, p._state);
                        if (d <= 2000)
                            bots.Add(new KeyValuePair<double, Bot>(d, b));
                    }
                    foreach (var kv in bots.OrderBy(k => k.Key).Take(5))
                    {
                        Bot b = kv.Value;
                        InfServer.Game.Player tgt = GetBotActivePlayerTarget(b);
                        lines.Add(string.Format("    bot {0} ({1}) dist={2:0} sees={3} targets={4}",
                            b._type != null ? b._type.Name : "?", b._team._name, kv.Key,
                            EolBotSenseUtil.BotCanSeePlayer(b._state, p), tgt == p ? "ME" : tgt != null ? tgt._alias : "-"));
                    }
                    if (bots.Count == 0)
                        lines.Add("    (no enemy bot within 2000)");
                }
                // the swarms: up or not, where, and when the next one is due
                foreach (SwarmRun r in SwarmRuns())
                    lines.Add(string.Format("{0} swarm {1}: team={2} active={3} nest={4} next in {5}s (game running={6}, sectors ready={7}, between games={8})",
                        stamp, r.Title, r.Team != null ? r.Team._name : "NONE", r.Active,
                        r.Nest != null && r.Nest._state != null && !r.Nest.IsDead ? Helpers.posToLetterCoord(r.Nest._state.positionX, r.Nest._state.positionY) : "-",
                        r.NextTick == 0 ? "?" : ((r.NextTick - now) / 1000).ToString(), _arena._bGameRunning, _eol != null && _eol.MatchSectorPlayReady, _bbetweengames));
                if (lines.Count > 0)
                    File.AppendAllLines(_diagFile, lines);
            }
            catch (Exception e)
            {
                Log.write(TLog.Warning, "[Eol] diag: {0}", e.Message);
            }
        }
    }
}
