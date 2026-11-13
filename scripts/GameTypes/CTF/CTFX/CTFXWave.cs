// CTF:X server-side bots - FLAG WAVES (10-15 night, cycle 3: 1365 flag runners sent, 1071 died, no pickup in 38 minutes - they went
// one by one into flag rooms held by turrets, posts and defenders; Travis: "there just needs to be a lot more focus on the flagging
// objective across the board"). A runner for an enemy-held flag lying in a base no longer runs in alone:
//  - it gathers at its wave's staging spot, 650 px short of the flag on the side the wave comes from (the first runner picks it),
//    until 4 of its team are there - or 3 after 25 s, or anyone after 45 s - then the whole wave goes in together, and the first
//    one there calls the attack (teammates far behind warp onto it, CallFor);
//  - a wave pushes for 40 s, then whoever is left regroups at the staging spot for the next push;
//  - a flag in the open or in a hall, or a carried one, is still run at straight away; so is one a runner is already close to.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXFlags
    {
        sealed class Wave { public int Sx, Sy, First, LaunchAt; public bool Have; }
        readonly Dictionary<string, Wave> _waves = new Dictionary<string, Wave>(StringComparer.Ordinal);
        internal int WavesLaunched, WaveMembers, WaveGathers;
        int _waveAt;

        static string WaveKey(Team t, Arena.FlagState fs) { return t._name + "|" + fs.GetHashCode(); }

        Wave WaveOf(Team t, Arena.FlagState fs)
        {
            Wave w;
            string k = WaveKey(t, fs);
            if (!_waves.TryGetValue(k, out w)) { w = new Wave(); _waves[k] = w; }
            return w;
        }

        /// <summary>Is fs a flag the enemy holds inside a base end (a wave's business)?</summary>
        bool WaveFlag(Arena.FlagState fs, Team mine)
        {
            if (fs.carrier != null || fs.team == null || fs.team == mine) return false;
            string a = _m.AreaAt(fs.posX, fs.posY);
            return a != "out" && !a.EndsWith("-core", StringComparison.Ordinal);
        }

        /// <summary>Goal hook for a runner: its wave's staging spot while the wave gathers; false = straight at the flag.</summary>
        bool WaveGoal(CTFXBot b, Arena.FlagState fs, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!WaveFlag(fs, b._team)) return false;
            int now = Environment.TickCount;
            Wave w = WaveOf(b._team, fs);
            if (w.LaunchAt != 0 && now - w.LaunchAt < 55000) return false;                // the push is on
            if (w.LaunchAt != 0) { w.LaunchAt = 0; w.First = 0; }                           // the push is over: regroup
            double dx = b._state.positionX - fs.posX, dy = b._state.positionY - fs.posY, d = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
            if (!w.Have)
            {
                if (d < 700) return false;                                                  // already on top of it
                int sx, sy;
                _m.NearestOpenPx(fs.posX + (int)(dx / d * 650), fs.posY + (int)(dy / d * 650), out sx, out sy);
                w.Sx = sx * 16 + 8; w.Sy = sy * 16 + 8; w.Have = true;
            }
            _m.NearestOpenPx(w.Sx + _m.Rng.Next(-48, 49), w.Sy + _m.Rng.Next(-48, 49), out tx, out ty);
            return true;
        }

        /// <summary>Once a second: count who is at each gathering wave's staging spot and launch it when it is ready.</summary>
        void WaveStep(int now)
        {
            if (now - _waveAt < 1000) return;
            _waveAt = now;
            Dictionary<string, List<CTFXSlot>> by = new Dictionary<string, List<CTFXSlot>>(StringComparer.Ordinal);
            Dictionary<string, Arena.FlagState> flagOf = new Dictionary<string, Arena.FlagState>(StringComparer.Ordinal);
            foreach (KeyValuePair<CTFXSlot, Arena.FlagState> kv in _chase)
            {
                if (kv.Key.Group == null || kv.Key.Group.Team == null || !WaveFlag(kv.Value, kv.Key.Group.Team)) continue;
                string k = WaveKey(kv.Key.Group.Team, kv.Value);
                List<CTFXSlot> l;
                if (!by.TryGetValue(k, out l)) { l = new List<CTFXSlot>(); by[k] = l; flagOf[k] = kv.Value; }
                l.Add(kv.Key);
            }
            foreach (KeyValuePair<string, List<CTFXSlot>> kv in by)
            {
                Wave w;
                if (!_waves.TryGetValue(kv.Key, out w) || !w.Have) continue;
                if (w.LaunchAt != 0 && now - w.LaunchAt < 55000) continue;
                List<CTFXBot> there = new List<CTFXBot>();
                foreach (CTFXSlot s in kv.Value)
                {
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (b == null || b.IsDead || b.InDropship) continue;
                    if (Dist(b._state.positionX, b._state.positionY, w.Sx, w.Sy) <= 200.0) { there.Add(b); _chaseUntil[s] = now + 90000; }   // waiting is not a stall
                }
                if (there.Count == 0) continue;
                if (w.First == 0) { w.First = now; WaveGathers++; }
                int waited = now - w.First;
                // 10-17 (Travis: attacks arrive piecemeal and die 2000 px out): a bigger wave - 5 of its team, or 4 after 30 s, or 3 after 60 s
                if (!(there.Count >= 5 || (there.Count >= 4 && waited >= 30000) || (there.Count >= 3 && waited >= 60000) || waited >= 90000)) continue;
                w.LaunchAt = now; WavesLaunched++; WaveMembers += there.Count;
                foreach (CTFXSlot s in kv.Value) { CTFXBot b = _m.GetBot(s.BotId); if (b != null && !b.IsDead) b.ForceGoal(); }
                Arena.FlagState fs = flagOf[kv.Key];
                if (WavesLaunched <= 20)
                    _m.Log("[CTFXBots] flag wave: " + there.Count + " of " + kv.Value.Count + " " + there[0]._team._name + " runners go for " + Name(fs) + " after " + (waited / 1000) + " s at "
                        + (w.Sx >> 4) + "," + (w.Sy >> 4));
                CallFor(there[0], fs, now);
            }
            // forget waves nobody runs for any more
            List<string> gone = null;
            foreach (string k in _waves.Keys) if (!by.ContainsKey(k)) (gone ?? (gone = new List<string>())).Add(k);
            if (gone != null) foreach (string k in gone) _waves.Remove(k);
        }

        internal string WaveText()
        {
            return ", flag waves launched " + WavesLaunched + " (avg " + (WavesLaunched > 0 ? (WaveMembers / (double)WavesLaunched).ToString("0.0") : "-") + " at launch, gathers " + WaveGathers + ")";
        }
    }
}
/* PARSER GUARD - keep last */
