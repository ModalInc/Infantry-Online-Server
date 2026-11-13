using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// Lag probe, one per CTF arena (mods: *lagprobe). It only measures; it changes nothing in the game.
    ///
    /// Two kinds of lag, measured separately:
    ///  - STALLS: the time between this arena's script polls. Every arena and every player packet (movement, deaths,
    ///    explosions, pickups) is handled on ONE thread that polls each arena about every 5 ms, so a long gap means
    ///    everyone's packets waited that long.
    ///  - SEND PRESSURE, sampled once a second per player: bytes sent last second, whether the client was over its
    ///    rate budget (the engine then holds ALL of its traffic, movement included), and how many reliable packets
    ///    are in flight / queued on stream 0 (a lost reliable is only resent after 1 s and holds up the rest).
    /// Plus GC pauses (process-wide) and hit-marker damage events skipped in this arena.
    ///
    /// Every minute with someone in the arena it appends one line to logs/LagProbe.txt (rotated into oldlogs/ with the
    /// other logs on restart).
    /// </summary>
    class LagProbe
    {
        private const string LOG_PATH = "logs/LagProbe.txt";
        private const int WINDOW_MS = 60000;

        private Arena _arena;
        private long _lastPollTs;          // Stopwatch timestamp of the previous poll (0 = none yet)
        private int _windowStart;          // TickCount the current minute started

        // Stalls, this minute
        private int _polls;
        private double _gapMaxMs;
        private string _gapMaxAt;
        private int _gaps50, _gaps100, _gaps250;

        // Send pressure, this minute (one sample = one player, one second)
        private int _samples, _overBudget;
        private int _sendMax, _inFlightMax, _queuedMax;
        private string _sendMaxWho, _inFlightMaxWho;
        private long _sendSum;
        private readonly Dictionary<string, int> _overBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _playersMax, _itemsMax;

        // Process-wide GC, at the start of this minute
        private int _gc0, _gc1, _gc2;
        private TimeSpan _gcPause;

        private int _hitMarkers;
        private string _lastLine;

        // GC.GetTotalPauseDuration is .NET 7+ (live); the local Windows dev server's runtime lacks it, so it is looked up.
        private static readonly MethodInfo _gcPauseFn = typeof(GC).GetMethod("GetTotalPauseDuration", Type.EmptyTypes);
        private static TimeSpan GcPauseTotal()
        {
            return _gcPauseFn != null ? (TimeSpan)_gcPauseFn.Invoke(null, null) : TimeSpan.Zero;
        }

        public void Attach(Arena arena)
        {
            _arena = arena;
            ResetWindow(Environment.TickCount);
        }

        /// <summary>Every script poll (~200/s). Two timestamps and a compare: nothing allocated.</summary>
        public void OnPoll(int now)
        {
            if (_arena == null) return;
            long ts = Stopwatch.GetTimestamp();
            if (_lastPollTs != 0)
            {
                double gap = (ts - _lastPollTs) * 1000.0 / Stopwatch.Frequency;
                _polls++;
                if (gap > _gapMaxMs) { _gapMaxMs = gap; _gapMaxAt = DateTime.UtcNow.ToString("HH:mm:ss"); }
                if (gap >= 50) _gaps50++;
                if (gap >= 100) _gaps100++;
                if (gap >= 250) _gaps250++;
            }
            _lastPollTs = ts;
        }

        public void HitMarkerSkipped() { _hitMarkers++; }

        /// <summary>Once a second, from the script's 1 Hz block.</summary>
        public void Sample(int now)
        {
            if (_arena == null) return;
            try
            {
                int players = 0;
                foreach (Player p in _arena.Players.ToList())
                {
                    Client c = p._client;
                    if (c == null) continue;
                    players++;
                    int speed = c._stats != null ? c._stats.SendSpeed : 0;
                    Client.StreamState s0 = c._streams != null && c._streams.Length > 0 ? c._streams[0] : null;
                    int inFlight = s0 != null ? (ushort)(s0.S2C_Reliable - s0.S2C_ReliableConfirmed) : 0;
                    int queued = s0 != null && s0.reliableQueue != null ? s0.reliableQueue.Count : 0;

                    _samples++;
                    _sendSum += speed;
                    if (speed > _sendMax) { _sendMax = speed; _sendMaxWho = p._alias; }
                    if (inFlight > _inFlightMax) { _inFlightMax = inFlight; _inFlightMaxWho = p._alias; }
                    if (queued > _queuedMax) _queuedMax = queued;
                    if (c._bytesWritten > c._rateThreshold)
                    {
                        _overBudget++;
                        int n;
                        _overBy.TryGetValue(p._alias, out n);
                        _overBy[p._alias] = n + 1;
                    }
                }
                if (players > _playersMax) _playersMax = players;
                if (_arena._items.Count > _itemsMax) _itemsMax = _arena._items.Count;
            }
            catch (Exception ex)
            {   // the network thread changes these as we read them; a missed sample is fine
                Console.WriteLine("[LagProbe] sample: " + ex.Message);
            }

            if (now - _windowStart >= WINDOW_MS)
            {
                try { if (_playersMax > 0) WriteLine(); }
                catch (Exception ex) { Console.WriteLine("[LagProbe] summary: " + ex.Message); }
                ResetWindow(now);
            }
        }

        private string Summary()
        {
            int gcPauseMs = (int)(GcPauseTotal() - _gcPause).TotalMilliseconds;
            string worstOver = _overBy.Count == 0 ? "-" : String.Join(",", _overBy.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key + ":" + kv.Value));
            return String.Format(
                "{0} [{1}] players={2} items={3} | polls={4} gapMax={5:F0}ms@{6} gaps>=50/100/250={7}/{8}/{9} | " +
                "send avg={10}B/s max={11}B/s({12}) overBudget={13}/{14} ({15}) relInFlightMax={16}({17}) relQueuedMax={18} | " +
                "gc0/1/2=+{19}/+{20}/+{21} gcPause=+{22}ms | hitMarkersSkipped={23}",
                DateTime.UtcNow.ToString("MM-dd HH:mm:ss"), _arena._name, _playersMax, _itemsMax,
                _polls, _gapMaxMs, _gapMaxAt ?? "-", _gaps50, _gaps100, _gaps250,
                _samples > 0 ? _sendSum / _samples : 0, _sendMax, _sendMaxWho ?? "-", _overBudget, _samples, worstOver,
                _inFlightMax, _inFlightMaxWho ?? "-", _queuedMax,
                GC.CollectionCount(0) - _gc0, GC.CollectionCount(1) - _gc1, GC.CollectionCount(2) - _gc2, gcPauseMs,
                _hitMarkers);
        }

        private void WriteLine()
        {
            _lastLine = Summary();
            try { File.AppendAllText(LOG_PATH, _lastLine + Environment.NewLine); }
            catch (Exception ex) { Console.WriteLine("[LagProbe] write: " + ex.Message); }
        }

        private void ResetWindow(int now)
        {
            _windowStart = now;
            _polls = 0; _gapMaxMs = 0; _gapMaxAt = null; _gaps50 = _gaps100 = _gaps250 = 0;
            _samples = _overBudget = 0; _sendMax = _inFlightMax = _queuedMax = 0; _sendSum = 0;
            _sendMaxWho = _inFlightMaxWho = null;
            _overBy.Clear();
            _playersMax = _itemsMax = 0;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _gcPause = GcPauseTotal();
            _hitMarkers = 0;
        }

        /// <summary>*lagprobe [last|pkt on|pkt off]</summary>
        public void Command(Player mod, string payload)
        {
            string arg = (payload ?? "").Trim().ToLowerInvariant();
            if (arg == "pkt on" || arg == "pkt off")
            {   // the engine's own timer: logs a warning for any packet that takes over 200 ms to handle (zone-wide).
                // A field of the live build's Server only, so it is set by name.
                FieldInfo f = _arena._server.GetType().GetField("_bLogPacketProcessDuration", BindingFlags.Public | BindingFlags.Instance);
                if (f == null) { mod.sendMessage(-1, "This server build has no slow-packet timer."); return; }
                f.SetValue(_arena._server, arg == "pkt on");
                mod.sendMessage(0, "Engine slow-packet warnings (over 200 ms, warnings.txt) are " + (arg == "pkt on" ? "ON" : "OFF") + " until restart.");
                return;
            }
            string text;
            try { text = arg == "last" ? (_lastLine ?? "No full minute logged yet.") : Summary(); }
            catch (Exception ex) { text = "lagprobe error: " + ex.Message; }
            // one chat line holds ~200 chars: send the summary in pieces at its " | " separators
            foreach (string part in text.Split(new[] { " | " }, StringSplitOptions.None))
                mod.sendMessage(0, part.Length > 200 ? part.Substring(0, 197) + "..." : part);
            if (arg != "last") mod.sendMessage(0, "(this minute so far; *lagprobe last = the last full minute; *lagprobe pkt on|off; log: " + LOG_PATH + ")");
        }
    }
}

/* PARSER GUARD: ZoneServer's ScriptParser stops matching // comments past char 32767 unless a block comment follows
   them. This file is well under that, but the guard costs nothing if it grows. */
