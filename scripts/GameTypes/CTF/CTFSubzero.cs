using System;
using System.Collections.Generic;
using System.Linq;

using Assets;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// Subzero (Tranq) ice effect (Travis, 2026-10-27: "think subzero from mortal kombat ... ice could form over the character equal to the
    /// stun duration ... second or 3rd restuns are highly common ... if it layered well that would also be a bonus"). Subzero (Tranq) is a
    /// Season 2 champion item (CTF.cs s2Conversions), so this is on only where champion items and item skins are both on (fxOn: off in
    /// league arenas, on Sundays, *skins off, *champItems off). Art in hwcaw.blo (_hwSubzero.mts), items in ctfpl.itm (build_subzero_itm.py).
    ///
    /// Every enemy a Subzero stuns (antiEffectsRadius + 8, the same test as CTFStunLock.cs) is frozen in ice that follows the two stun timers
    /// read from the item (antiEffectsRotate = can't turn, antiEffectsThrust = can't move; 1 tick = 10 ms):
    ///   0 .. rotate end       SzFreeze   the ice races up from the feet and seals over the head (white flash), then SzHold (sealed block)
    ///   rotate end ..         SzCrack    the top cracks off and falls away: head and shoulders free, legs locked; then SzHold (pillar rows)
    ///   last 0.5 s            SzThaw     cracks race up the pillar: everyone can read that the stun is about to end
    ///   thrust end            SzShatter  the pillar bursts (only when no newer stun extended the window)
    ///   death while frozen    SzBody     the block frosts over solid for 0.6 s, then bursts into chunks
    /// A 2nd / 3rd Subzero hit inside the window steps the tier up (2 = thick crust + icicle spikes, 3 = glacier + frost aura, the cap) and
    /// re-freezes; any other stun weapon (stock Tranq, Stunner...) on a frozen player extends the window, and re-freezes when it locks turning
    /// again. Tiers are the sprites' direction rows: the yaw sent picks the row (240 yaw units a turn, always a row centre).
    ///
    /// How it stays one clean block: a sent projectile can't be taken back, so every piece is short (0.5 s, 0.6 s with its join) and the
    /// zone sends the next one from the poll. Chained pieces overlap by JOIN_MS with pixel-identical, light-free frames (art rule), so there
    /// is never a gap or a doubled glow; a newer tier is drawn over the older one (it covers it pixel for pixel, and it is placed further
    /// south so it sorts in front); the death burst covers whatever piece is still up for its last 0.6 s. Each piece is placed LEAD px south
    /// of the victim (so it sorts in front of the soldier; the art is drawn as much higher), at the victim's current spot (re-anchored when
    /// they slid more than REANCHOR_PX).
    /// Network: one reliable SC_Projectile per piece to the players and spectators within FX_RANGE: a single 4 s Subzero stun = 9 sends
    /// (freeze, hold, crack, 4 pillar holds, thaw, shatter), each extra hit adds about 4; a kill sends 1 instead of what was left.
    /// Static, keyed by alias, so every arena shares it; each arena's poll serves its own players.
    /// </summary>
    public static class Subzero
    {
        public const string SKIN = "Subzero (Tranq)";
        public const string FREEZE = "SzFreeze", HOLD = "SzHold", CRACK = "SzCrack", THAW = "SzThaw", SHATTER = "SzShatter", BODY = "SzBody";
        private const int TICK_MS = 10;          // one anti-effect tick (item timings are centiseconds), as CTFStunLock.cs
        private const int MIN_TICKS = 50;        // anti-effects shorter than this are grazes (CTFStunLock.cs min_ticks)
        private const int STUN_MARGIN = 8;       // px on top of antiEffectsRadius, as CTFStunLock.cs
        private const int FX_RANGE = 1600;       // who is sent the pieces (the screen is ~1000 px wide)
        private const int PIECE_MS = 500;        // each chained piece covers 0.5 s (its item lives 0.6 s: the join)
        private const int JOIN_MS = 100;         // the next piece is sent this early; the two overlap with identical frames
        private const int WARN_MS = 500;         // the thaw (cracks racing up) plays in the last 0.5 s of the stun
        private const int QUARTER_MS = 250;      // a rotate lock ending within this of a piece boundary frees the head at that boundary
        private const int EPS_MS = 20;
        private const int MAX_TIER = 3;
        private const int REANCHOR_PX = 3;       // a victim who slid further than this gets the next piece at their new spot
        private const int YAW_TURN = 240;        // yaw units per turn (Protocol Helpers.Math, the bots)
        private const int DEATH_WAIT_MS = 1500;  // health 0 can reach the zone before the death packet: wait this long for Player.Death
        // px south of the victim per piece kind and tier (the art is drawn this much higher): newer pieces sort in front of older ones
        private static readonly int[] LeadPiece = { 3, 6, 9 }, LeadWarn = { 4, 7, 10 }, LeadFreeze = { 5, 8, 11 };
        private const int LEAD_SHATTER = 12;

        private class Ice
        {
            public string Arena;
            public int Tier, RotUntil, ThrustUntil, NextAt;   // NextAt: when the content of the piece now showing ends
            public bool Pillar, Warned;
            public int DeadSince;                             // != 0: the zone sees them dead (health 0), Player.Death not here yet
            public short X, Y;                                // anchor (the victim's spot)
            public ushort By;                                 // the player the pieces are sent as (the latest Subzero shooter)
        }
        private static readonly Dictionary<string, Ice> _ice = new Dictionary<string, Ice>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();
        public static int Sent;                               // pieces sent since the zone started (counted per piece, not per receiver)

        private static bool AtOrAfter(int a, int b) { return unchecked(a - b) >= 0; }
        private static int Later(int a, int b) { return unchecked(a - b) >= 0 ? a : b; }

        /// <summary>one piece over the victim: item name, the lead for its tier, its row (of rows) picked by the yaw</summary>
        private static void Send(Arena arena, Player victim, Ice st, string item, int lead, int row, int rows)
        {
            ItemInfo fx = AssetManager.Manager.getItemByName(item);
            if (fx == null || arena == null || victim == null)
                return;
            short vx = victim._state.positionX, vy = victim._state.positionY;
            if (Math.Abs(vx - st.X) > REANCHOR_PX || Math.Abs(vy - st.Y) > REANCHOR_PX) { st.X = vx; st.Y = vy; }
            var shot = new SC_Projectile { projectileID = (short)fx.id, playerID = st.By, posX = st.X, posY = (short)(st.Y + lead), posZ = victim._state.positionZ,
                yaw = (byte)(YAW_TURN * row / rows) };
            foreach (Player p in arena.getPlayersAndSpecInRange(st.X, st.Y, FX_RANGE))
                p._client.sendReliable(shot);
            Sent++;
        }
        private static void Freeze(Arena a, Player v, Ice s) { Send(a, v, s, FREEZE, LeadFreeze[s.Tier - 1], s.Tier - 1, 3); }

        /// <summary>Player.Explosion: a Subzero blast freezes (or tiers up) every enemy it stuns; another stun weapon extends a frozen
        /// enemy's window, re-freezing them when it locks their turning again.</summary>
        public static void OnExplosion(Arena arena, Player from, ItemInfo.Projectile wep, short x, short y, bool fxOn)
        {
            if (!fxOn || arena == null || from == null || from._team == null || wep == null)
                return;
            bool isSub = SKIN.Equals(wep.name, StringComparison.OrdinalIgnoreCase);
            int rotMs = wep.antiEffectsRotate >= MIN_TICKS ? wep.antiEffectsRotate * TICK_MS : 0, thrMs = wep.antiEffectsThrust >= MIN_TICKS ? wep.antiEffectsThrust * TICK_MS : 0;
            if (rotMs == 0 && thrMs == 0)
                return;
            lock (_lock) { if (!isSub && _ice.Count == 0) return; }
            List<Player> near = arena.getPlayersInRange(x, y, wep.antiEffectsRadius + STUN_MARGIN);
            if (near == null)
                return;
            int now = Environment.TickCount;
            foreach (Player p in near)
            {
                if (p == null || p == from || p.IsSpectator || p.IsDead || p._team == null || p._team == from._team)
                    continue;   // anti effects are for the other side
                lock (_lock)
                {
                    Ice st;
                    if (_ice.TryGetValue(p._alias, out st) && st.Arena != arena._name) { _ice.Remove(p._alias); st = null; }
                    if (st == null && !isSub)
                        continue;
                    if (st == null)
                        _ice[p._alias] = st = new Ice { Arena = arena._name, RotUntil = now, ThrustUntil = now, NextAt = now, X = p._state.positionX, Y = p._state.positionY };
                    int newRot = now + rotMs, newThr = now + Math.Max(thrMs, rotMs), oldThr = st.ThrustUntil;
                    bool freeze = false;
                    if (isSub) { st.Tier = Math.Min(MAX_TIER, st.Tier + 1); st.By = (ushort)from._id; freeze = true; }
                    else if (rotMs > 0 && !AtOrAfter(st.RotUntil, newRot) && (st.Pillar || st.Warned)) freeze = true;   // turning locked again
                    else if (st.Warned && !AtOrAfter(oldThr, newThr)) { st.Warned = false; st.Pillar = true; st.NextAt = oldThr; }   // the thaw stops; the pillar holds on
                    if (freeze)
                    {
                        st.NextAt = now + PIECE_MS; st.Pillar = false; st.Warned = false;
                        Freeze(arena, p, st);
                    }
                    st.RotUntil = Later(st.RotUntil, newRot);
                    st.ThrustUntil = Later(Later(st.ThrustUntil, newThr), st.RotUntil);
                }
            }
        }

        /// <summary>Player.Death: killed while frozen = the frozen-solid burst instead of whatever was left</summary>
        public static void OnDeath(Arena arena, Player victim, bool fxOn)
        {
            if (victim == null)
                return;
            lock (_lock)
            {
                Ice st;
                if (!_ice.TryGetValue(victim._alias, out st))
                    return;
                _ice.Remove(victim._alias);
                if (fxOn && arena != null && st.Arena == arena._name)
                    Send(arena, victim, st, BODY, LEAD_SHATTER, st.Tier - 1, 3);
            }
        }

        /// <summary>From the arena poll (every few ms): sends each frozen player's next piece just before the one showing ends</summary>
        public static void Poll(Arena arena, bool fxOn)
        {
            if (arena == null)
                return;
            lock (_lock)
            {
                if (_ice.Count == 0)
                    return;
                int now = Environment.TickCount;
                foreach (var kv in _ice.ToList())
                {
                    Ice st = kv.Value;
                    if (st.Arena != arena._name)
                        continue;
                    Player v = arena.Players.FirstOrDefault(pl => pl._alias.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (!fxOn || v == null || v.IsSpectator)
                    {   // gone, or the arena's skins went off: what is up fades on its own
                        _ice.Remove(kv.Key);
                        continue;
                    }
                    if (v.IsDead)
                    {   // health 0 often arrives before the death packet: send nothing more, and let Player.Death play the body burst
                        if (st.DeadSince == 0) st.DeadSince = now == 0 ? 1 : now;
                        else if (unchecked(now - st.DeadSince) > DEATH_WAIT_MS) _ice.Remove(kv.Key);
                        continue;
                    }
                    st.DeadSince = 0;
                    int t = st.Tier - 1;
                    for (int guard = 0; guard < 4 && !st.Warned && AtOrAfter(now, st.NextAt - JOIN_MS); guard++)
                    {
                        if (AtOrAfter(st.NextAt, st.ThrustUntil - WARN_MS - EPS_MS))
                            break;   // the thaw takes over from here
                        if (!AtOrAfter(st.NextAt, st.RotUntil - QUARTER_MS)) Send(arena, v, st, HOLD, LeadPiece[t], t, 6);              // still can't turn: sealed
                        else if (!st.Pillar) { Send(arena, v, st, CRACK, LeadPiece[t], t, 3); st.Pillar = true; }                       // the head breaks free
                        else Send(arena, v, st, HOLD, LeadPiece[t], 3 + t, 6);                                                          // legs still locked
                        st.NextAt += PIECE_MS;
                    }
                    if (!st.Warned && AtOrAfter(now, st.ThrustUntil - WARN_MS - JOIN_MS))
                    {
                        Send(arena, v, st, THAW, LeadWarn[t], t, 3);
                        st.Warned = true; st.NextAt = st.ThrustUntil;
                    }
                    if (st.Warned && AtOrAfter(now, st.ThrustUntil - JOIN_MS))
                    {
                        Send(arena, v, st, SHATTER, LEAD_SHATTER, t, 3);
                        _ice.Remove(kv.Key);
                    }
                }
            }
        }

        /// <summary>Player.Leave / spec: forget them (what is up fades on its own)</summary>
        public static void Forget(string alias)
        {
            if (alias == null)
                return;
            lock (_lock) { _ice.Remove(alias); }
        }

        /// <summary>for staff checks: how many players are frozen now and how many pieces went out since the zone started</summary>
        public static string Describe()
        {
            lock (_lock) { return String.Format("Subzero ice: {0} frozen now, {1} pieces sent", _ice.Count, Sent); }
        }
    }
}
