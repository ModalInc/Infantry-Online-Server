// CTF:X server-side bots - STALE BRAWLS + DOOR CHOICE (Travis 10-15 night, west brown: "its possible to go UP and use the higher up
// elevator door switch, but looks like ppl funnel at the bottom one, and its a never ending loop of bots and anchors always attaching
// and killing and no one ever leaves the area which is problematic gameplay loop").
//  - Every 2 s: crowds of 6+ fighting bots (any team) within 200 px of one another, outside every base end and 400+ px from every flag.
//    One seen at about the same spot (250 px) for 75 s straight turns STALE, until it has been gone 30 s.
//  - In or beside a stale brawl (250 px): no rescuers answer a help call from there (a summon out still does), no rejoin teleport lands
//    on a teammate there, no attack position is taken there, and every 10 s each free bot in it (not a post, anchor or defender, not a
//    flag carrier, bountier or maser) breaks off toward its objective, 40 % of them (70 % of the hurt), as players drift back to flags.
//  - Doors: a route plan through a door pays 90 px per bot (any team) beyond 2 within 200 px of its near waypoint, plus a taste of
//    0-500 px per bot and door, re-rolled every 2 min. A crowded door sends traffic to another one: west brown's upper door A (D240,
//    the brown-core>plaza2 link) when the bottom door B (D253) is a brawl.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        Func<int, int, float> _doorCostFn;

        /// <summary>The door cost hook for this bot's route plans (CTFXRouteBook.Plan).</summary>
        Func<int, int, float> DoorCostFn
        {
            get
            {
                if (_doorCostFn == null) { CTFXBot me = this; _doorCostFn = delegate(int door, int node) { return me.Mgr.DoorCost(me, door, node, Environment.TickCount); }; }
                return _doorCostFn;
            }
        }
    }

    internal sealed partial class CTFXBotManager
    {
        sealed class Brawl { public int X, Y, Since, Seen, N; public bool Stale; }
        readonly List<Brawl> _brawls = new List<Brawl>();
        readonly List<CTFXBot> _fighters = new List<CTFXBot>(64);
        int _brawlAt, _brawlLeaveAt;
        internal int FieldOpenings;
        internal int StaleBrawls, StaleRescueSkips, StaleRejoinSkips, StaleLeavers, StaleStageSkips, DoorAPlans, DoorBPlans;
        readonly Dictionary<int, int> _doorCrowdAt = new Dictionary<int, int>(), _doorCrowd = new Dictionary<int, int>();

        static double D2(CTFXBot a, CTFXBot b)
        {
            double dx = a._state.positionX - b._state.positionX, dy = a._state.positionY - b._state.positionY;
            return dx * dx + dy * dy;
        }

        /// <summary>Inside a base end, or 400 px or closer to an event flag: a fight there is the game, not a brawl.</summary>
        bool AtObjective(int x, int y)
        {
            foreach (string e in EndIds) if (InsideEndPx(e, x, y)) return true;
            if (Flags != null)
                foreach (Arena.FlagState fs in Flags.All())
                {
                    double dx = fs.posX - x, dy = fs.posY - y;
                    if (dx * dx + dy * dy <= 400.0 * 400.0) return true;
                }
            return false;
        }

        internal void BrawlStep(int now)
        {
            if (Mini != null) return;                      // MiniTP: no stale-brawl detection (it keys on the CTF:X objective and would flag every fight)
            if (now - _brawlAt < 2000) return;
            _brawlAt = now;
            _fighters.Clear();
            foreach (CTFXBot b in LiveBots) if (!b.IsDead && !b.Released && !b.InDropship && b.HasTarget) _fighters.Add(b);
            int n = _fighters.Count;
            bool[] used = new bool[n];
            const double R2 = 200.0 * 200.0;
            for (;;)
            {
                int best = 0, bi = -1;
                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    int c = 0;
                    for (int j = 0; j < n; j++) if (!used[j] && D2(_fighters[i], _fighters[j]) <= R2) c++;
                    if (c > best) { best = c; bi = i; }
                }
                if (best < 6) break;
                long sx = 0, sy = 0;
                int m = 0;
                CTFXBot ctr = _fighters[bi];
                for (int j = 0; j < n; j++)
                    if (!used[j] && D2(ctr, _fighters[j]) <= R2) { used[j] = true; sx += _fighters[j]._state.positionX; sy += _fighters[j]._state.positionY; m++; }
                int cx = (int)(sx / m), cy = (int)(sy / m);
                if (AtObjective(cx, cy)) continue;
                Brawl br = null;
                foreach (Brawl o in _brawls)
                {
                    double dx = o.X - cx, dy = o.Y - cy;
                    if (dx * dx + dy * dy <= 250.0 * 250.0) { br = o; break; }
                }
                if (br == null) { br = new Brawl(); br.Since = now; _brawls.Add(br); }
                br.X = cx; br.Y = cy; br.Seen = now; br.N = m;
                if (!br.Stale && now - br.Since >= 75000)
                {
                    br.Stale = true; StaleBrawls++;
                    Out("[CTFXBots] stale brawl at " + (cx >> 4) + "," + (cy >> 4) + " (" + AreaAt(cx, cy) + "): " + m + " fighting there for " + ((now - br.Since) / 1000) + " s, away from every flag");
                }
            }
            _brawls.RemoveAll(delegate(Brawl o) { return now - o.Seen >= (o.Stale ? 30000 : 6000); });
            if (now - _brawlLeaveAt >= 10000) { _brawlLeaveAt = now; LeaveStale(now); }
        }

        /// <summary>A stale brawl within 250 px of (x, y).</summary>
        internal bool InStaleBrawl(int x, int y)
        {
            foreach (Brawl o in _brawls)
            {
                if (!o.Stale) continue;
                double dx = o.X - x, dy = o.Y - y;
                if (dx * dx + dy * dy <= 250.0 * 250.0) return true;
            }
            return false;
        }

        void LeaveStale(int now)
        {
            foreach (Brawl br in _brawls)
            {
                if (!br.Stale) continue;
                foreach (CTFXBot b in LiveBots)
                {
                    if (b.IsDead || b.Released || b.InDropship || b.Slot == null || b.Retreating) continue;
                    if (b.Style == CTFXStyle.Post || b.Style == CTFXStyle.Bountier || b.Style == CTFXStyle.Maser) continue;
                    double dx = b._state.positionX - br.X, dy = b._state.positionY - br.Y;
                    if (dx * dx + dy * dy > 250.0 * 250.0) continue;
                    CTFXAct a = Cmdr != null ? Cmdr.ActOf(b) : CTFXAct.None;
                    if (a == CTFXAct.Anchor || a == CTFXAct.Defend || (Flags != null && Flags.Carrying(b))) continue;
                    bool hurt = b._state.health < 0.5 * b.MaxHp;
                    if (Rng.NextDouble() >= (hurt ? 0.7 : 0.4)) continue;
                    int ox = 0, oy = 0, tx, ty;
                    bool got = Cmdr != null && Cmdr.ObjectiveOf(b, out ox, out oy);
                    if (got)
                    {
                        double ex = ox - br.X, ey = oy - br.Y;
                        if (ex * ex + ey * ey < 500.0 * 500.0) got = false;                 // its objective is the brawl itself (the hall hub)
                    }
                    if (got) { tx = ox >> 4; ty = oy >> 4; }
                    else if (!StagingGoal(FocusEnd, out tx, out ty)) continue;
                    b.RetreatTo(now, tx, ty, 9000 + Rng.Next(4001));
                    StaleLeavers++;
                }
            }
        }

        /// <summary>The extra cost (px) of a route plan crossing a door from waypoint node: its crowd, and this bot's taste for it.</summary>
        internal float DoorCost(CTFXBot b, int door, int node, int now)
        {
            int at, c;
            if (!_doorCrowdAt.TryGetValue(node, out at) || now - at >= 1000 || now - at < 0)
            {
                c = CrowdAt(Routes.X[node], Routes.Y[node], 200.0);
                _doorCrowd[node] = c; _doorCrowdAt[node] = now;
            }
            else c = _doorCrowd[node];
            unchecked
            {
                uint h = (uint)((b.Slot != null ? b.Slot.FixedId : 0) * 7919 + door * 104729 + (now / 120000) * 31337);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return Math.Max(0, c - 2) * 90f + (h % 501);
            }
        }

        /// <summary>West brown's two side doors in a fresh plan: counts which one it crosses (the stats line).</summary>
        internal void NoteDoors(List<int> path)
        {
            if (Routes == null) return;
            for (int i = 1; i < path.Count; i++)
            {
                int d = Routes.DoorBetween(path[i - 1], path[i]);
                if (d == 240) DoorAPlans++;
                else if (d == 253) DoorBPlans++;
            }
        }

        internal string BrawlText()
        {
            int now = 0;
            foreach (Brawl o in _brawls) if (o.Stale) now++;
            return ", stale brawls " + StaleBrawls + " (now " + now + ", rescues held back " + StaleRescueSkips + ", rejoins diverted " + StaleRejoinSkips
                + ", attack positions skipped " + StaleStageSkips + ", bots pulled out " + StaleLeavers + "), west brown doors upper/lower planned " + DoorAPlans + "/" + DoorBPlans
                + ", hall-team flags carried into an end " + (Flags != null ? Flags.HallCarries : 0) + ", skirmish pushes after a sweep " + (Cmdr != null ? Cmdr.PushOuts : 0)
                + ", near wins " + (Flags != null ? Flags.NearWins : 0) + ", hall-team opening goals in the fields " + FieldOpenings + (Flags != null ? Flags.WaveText() : "");
        }
    }
}
/* PARSER GUARD - keep last */
