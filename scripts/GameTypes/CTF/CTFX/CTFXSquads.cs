// CTF:X server-side bots - SQUADS: teleport grouping, blink storms and the defenders' answer (Travis 10-11). Route book only,
// never the engine pathfinder.
//
// A group with assault slots and a stormer (home = a base end) runs a storm cycle against the other end of its building:
//  1. GATHER  the squad (its assault slots, led by the first) stages at its home end's mouth. A member far from the leader
//             teleports to it, or the leader summons it (SL Summon, 3 charges) when the leader's class has one.
//  2. WAIT    the stormer (an Infiltrator, or a SciOps) walks the raid route into the enemy end and hides on a spawn spot there.
//  3. STORM   every gathered member teleports onto the stormer, a few hundred ms apart: the blink storm.
//  4. HOLD    the squad sweeps the enemy flag room; a member that respawned at home or straggled teleports back to the leader.
//  5. REST    after 60 s, or once one member is left, a 20-35 s pause; then the next cycle.
// Defenders of a stormed end answer in kind: their patrol guards and roamers teleport onto the defender nearest the landing,
// or run there when an enemy anti-warp source pins them. Idle roamers of any group teleport to a teammate in a fight inside
// their building now and then, so the action clusters.
//
// Warps follow the zone's warp items. Teleport to a teammate (WarpTeam: Suit Teleport 21, accuracy 64; Squad Leader Teleport
// 37 for the leader classes, 32) is refused when an enemy anti-warp source covers the warper: the server's own three checks
// (enemy bots, turrets, players), asked about a stand-in at the warper's spot. A summon (SummonTeam: Squad Leader Summon 35,
// accuracy 32) checks the summoner, then the target. The landing is random within +-accuracy/2 of the destination, on open
// tiles. Bots carry no energy, so each item has a per-bot cooldown instead (warpFx=1 also shows clients the item's use).

using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>The storm cycle of one group (CTFXSquads).</summary>
    internal sealed class CTFXStorm
    {
        public const int Gather = 0, Wait = 1, Storm = 2, Hold = 3, Rest = 4;
        public string Home, Enemy;           // base ends
        public int Phase, Since, Until;
        public int StormX, StormY;           // px of the landing
        public int InsideSince;              // tick the stormer entered the enemy end (0 = not inside)
        public int HideSince;                // tick the stormer took its current spot (0 = not placed)
        public int Cycles;
        public readonly List<KeyValuePair<ushort, int>> Queue = new List<KeyValuePair<ushort, int>>();     // member id, due tick
        public readonly HashSet<ushort> Joined = new HashSet<ushort>();       // bot ids that blinked in on this storm (the rest stay on their jobs)
        public readonly HashSet<CTFXSlot> SitOut = new HashSet<CTFXSlot>();   // members doing their own thing this cycle (CTFXCommand)
        public static readonly string[] Names = new string[] { "gather", "wait", "storm", "hold", "rest" };
    }

    internal sealed class CTFXSquads
    {
        // warp items (ctfpl.itm)
        internal const int SuitTeleport = 21, SlTeleport = 37, SlSummon = 35;
        const int TeleportCooldown = 20000, SlTeleportCooldown = 15000, SummonRecharge = 25000;

        readonly CTFXBotManager _m;
        readonly List<CTFXBot> _tmp = new List<CTFXBot>(16);
        readonly List<KeyValuePair<ushort, int>> _recalls = new List<KeyValuePair<ushort, int>>();       // defender id, due tick
        readonly Dictionary<ushort, ushort> _recallTo = new Dictionary<ushort, ushort>();               // defender id -> anchor id
        int _lastStep, _lastJoin;
        internal int Warps, Pinned, Summons, Storms, Landed, Recalls, Joins, Walks, SatOut, Rehides, NoOpening;
        const double Loiter = 480.0;   // px: while the squad waits for its stormer, its members roam this far from the mouth on their own

        internal CTFXSquads(CTFXBotManager m) { _m = m; }

        internal void Clear()
        {
            _recalls.Clear(); _recallTo.Clear();
            Warps = 0; Pinned = 0; Summons = 0; Storms = 0; Landed = 0; Recalls = 0; Joins = 0; Walks = 0; SatOut = 0; Rehides = 0; NoOpening = 0;
        }

        /// <summary>Give every group that can storm (assault slots + a stormer + a home end with an enemy end) its cycle.</summary>
        internal void Setup(List<CTFXBotGroup> groups, int now)
        {
            foreach (CTFXBotGroup g in groups)
            {
                g.Storm = null;
                if (g.HomeEnd == null) continue;
                bool assault = false, stormer = false;
                foreach (CTFXSlot s in g.Slots) { if (s.Style == CTFXStyle.Assault) assault = true; if (s.Style == CTFXStyle.Stormer) stormer = true; }
                string enemy = _m.OtherEnd(g.HomeEnd);
                // every storm squad not already in the brown building storms the focus base (top brown, Travis 10-13: the biggest
                // hotspot at all times, other teams launching at it)
                if (_m.FocusEnd != null && g.HomeEnd != _m.FocusEnd && enemy != _m.FocusEnd) enemy = _m.FocusEnd;
                if (!assault || !stormer || enemy == null) continue;
                CTFXStorm st = new CTFXStorm();
                st.Home = g.HomeEnd; st.Enemy = enemy; st.Phase = CTFXStorm.Gather; st.Since = now;
                g.Storm = st;
            }
        }

        // ================================================================ per poll
        internal void Step(List<CTFXBotGroup> groups, int now)
        {
            if (now - _lastStep < 100) return;
            _lastStep = now;
            foreach (CTFXBotGroup g in groups) if (g.Storm != null) Cycle(g, g.Storm, now);
            RunRecalls(now);
            if (now - _lastJoin >= 1500) { _lastJoin = now; JoinFights(groups, now); }
        }

        void Cycle(CTFXBotGroup g, CTFXStorm st, int now)
        {
            CTFXBot leader = Leader(g);
            List<CTFXBot> mem = Members(g);
            CTFXBot stormer = Stormer(g);
            switch (st.Phase)
            {
                case CTFXStorm.Gather:
                case CTFXStorm.Wait:
                    // Travis 10-14: no staging. The members do their normal activities (the commander's picks) while the stormer sneaks
                    // in; the stormer presents the opportunity, and the members free right then blink onto it.
                    if (st.Phase == CTFXStorm.Gather) { Enter(g, st, CTFXStorm.Wait, now); st.HideSince = 0; break; }
                    if (stormer == null || stormer.InDropship) { st.HideSince = 0; break; }
                    bool inside = _m.InsideEnd(st.Enemy, stormer);
                    if (!inside) st.InsideSince = 0;
                    else if (st.InsideSince == 0) st.InsideSince = now | 1;
                    bool placed = stormer.AtHide || (inside && now - st.InsideSince >= 3000);
                    if (!placed) { st.HideSince = 0; if (now - st.Since > 150000) Restart(g, st, stormer, now); break; }
                    if (st.HideSince == 0) st.HideSince = now | 1;
                    int free = 0;
                    foreach (CTFXBot b in mem) if (b != stormer && FreeToStorm(b, now)) free++;
                    int foes = EnemiesNear(stormer, 450.0);
                    if (free >= 2 && foes <= free + 1)
                    {   // the opening: enough of the squad free, few enough defenders around the stormer
                        st.StormX = stormer._state.positionX; st.StormY = stormer._state.positionY;
                        st.Queue.Clear(); st.Joined.Clear();
                        int due = now;
                        foreach (CTFXBot b in mem)
                        {
                            if (b != stormer && FreeToStorm(b, now)) st.Joined.Add(b._id);
                            if (b == stormer || !FreeToStorm(b, now)) continue;        // busy in a fight / carrying / in the ship: it stays on its own job
                            due += 150 + _m.Rng.Next(200);
                            st.Queue.Add(new KeyValuePair<ushort, int>(b._id, due));
                        }
                        Storms++;
                        Enter(g, st, CTFXStorm.Storm, now);
                        _m.Chat.Storming(stormer, st.Enemy);
                        _m.Note("storm #" + Storms + ": " + Team(g) + " " + st.Queue.Count + " of " + mem.Count + " blink onto its stormer at " + (st.StormX >> 4) + "," + (st.StormY >> 4)
                            + " (" + st.Enemy + ", " + foes + " defenders near)");
                    }
                    else if (now - st.HideSince > 40000)
                    {   // no opening from this spot: the stormer moves to another one (it does not hide forever)
                        Rehides++;
                        st.HideSince = 0;
                        stormer.NewHide();
                    }
                    else if (now - st.Since > 150000) Restart(g, st, stormer, now);
                    break;
                case CTFXStorm.Storm:
                    for (int i = 0; i < st.Queue.Count; )
                    {
                        if (now - st.Queue[i].Value < 0) { i++; continue; }
                        CTFXBot b = _m.GetBot(st.Queue[i].Key);
                        if (b != null && !b.IsDead && now - b.WarpReadyAt < 0 && b.WarpReadyAt - st.Since <= 8000)
                        {   // its teleport is still recharging from the gather: it goes as soon as it can (within 8 s of the storm)
                            st.Queue[i] = new KeyValuePair<ushort, int>(b._id, b.WarpReadyAt + _m.Rng.Next(200));
                            i++;
                            continue;
                        }
                        st.Queue.RemoveAt(i);
                        CTFXBot to = stormer != null ? stormer : leader;
                        if (b == null || b.IsDead || b.Released || to == null || b == to) continue;
                        if (TeleportTo(b, to, now, "storm")) Landed++;
                    }
                    if (st.Queue.Count == 0)
                    {
                        Enter(g, st, CTFXStorm.Hold, now);
                        Respond(st.Enemy, st.StormX, st.StormY, g.Team, now);
                    }
                    break;
                case CTFXStorm.Hold:
                    {
                        CTFXBot anchor = leader != null && _m.InsideEnd(st.Enemy, leader) ? leader : null;
                        if (anchor == null && stormer != null && _m.InsideEnd(st.Enemy, stormer)) anchor = stormer;
                        if (anchor == null) foreach (CTFXBot b in mem) if (_m.InsideEnd(st.Enemy, b)) { anchor = b; break; }
                        if (anchor != null) Regroup(anchor, mem.FindAll(x => st.Joined.Contains(x._id)), now, false);   // only the ones that came in
                    }
                    if (now - st.Since > 60000 || Alive(mem) <= 1)
                    {
                        Enter(g, st, CTFXStorm.Rest, now);
                        st.Until = now + 20000 + _m.Rng.Next(15001);
                    }
                    break;
                default:
                    if (now - st.Until >= 0)
                    {
                        st.Cycles++;
                        Enter(g, st, CTFXStorm.Gather, now);
                        if (stormer != null) stormer.NewHide();
                    }
                    break;
            }
        }

        void Enter(CTFXBotGroup g, CTFXStorm st, int phase, int now)
        {
            st.Phase = phase; st.Since = now;
            if (phase == CTFXStorm.Storm)
                foreach (CTFXBot m in _m.LiveBots) if (m.Group == g && !m.IsDead && !m.InDropship) m.PreStorm(now);   // Travis 10-14: energizers before the storm
            CTFXBot leader = Leader(g);
            if (leader != null) leader.ForceGoal();                    // the leader's goal follows the phase (LeaderGoal)
            if (phase == CTFXStorm.Gather) SitOuts(g, st, leader);
        }

        /// <summary>
        /// A new cycle: each member but the leader decides whether it comes along this time (Travis 10-12: "they work way too well
        /// together"). It sits the cycle out with chance (1 - compliance) x 0.6, about 5-45 %, and does its own thing meanwhile.
        /// </summary>
        void SitOuts(CTFXBotGroup g, CTFXStorm st, CTFXBot leader)
        {
            st.SitOut.Clear();
            if (_m.Cmdr == null || !_m.Cmdr.On) return;
            foreach (CTFXSlot s in g.Slots)
            {
                if (s.Style != CTFXStyle.Assault || (leader != null && leader.Slot == s)) continue;
                double comply = s.Human != null ? s.Human.Comply : 0.6;
                if (_m.Rng.NextDouble() < (1.0 - comply) * 0.6) st.SitOut.Add(s);
            }
            if (st.SitOut.Count > 0) SatOut += st.SitOut.Count;
        }

        /// <summary>Members away from the leader (and out of a fight) come to it: summoned by a leader class that has SL Summon,
        /// else by their own teleport. At home (gather) any distance over 260 px counts; in the enemy end only real stragglers.</summary>
        void Regroup(CTFXBot leader, List<CTFXBot> mem, int now, bool home)
        {
            double far = home ? Loiter + 160.0 : 420.0;
            foreach (CTFXBot b in mem)
            {
                if (b == leader || b.HasTarget || Dist(b, leader) <= far) continue;
                if (CanSummon(leader, now) && Summon(leader, b, now)) continue;
                TeleportTo(b, leader, now, home ? "gather" : "regroup");
            }
        }

        // ================================================================ the defenders' answer
        /// <summary>A storm landed at (x, y) in end <paramref name="end"/>: the end's patrol defenders teleport onto the defender
        /// nearest the landing (1/2 to 2 1/2 s later, staggered), or run there when no defender is near it.</summary>
        void Respond(string end, int x, int y, Team attacker, int now)
        {
            foreach (CTFXBotGroup g in _m.Groups)
            {
                if (g.HomeEnd != end || g.Team == attacker) continue;
                foreach (CTFXSlot s in g.Slots)
                {
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (b == null || b.IsDead || b.Released || b.HasTarget) continue;
                    if (s.Style != CTFXStyle.Guard && s.Style != CTFXStyle.Roam && s.Style != CTFXStyle.Adhd) continue;
                    if (_m.Cmdr.Manages(b)) continue;                       // its team's alarm calls it (CTFXCommand), with a human delay
                    if (DistPx(b, x, y) <= 320.0) continue;
                    CTFXBot anchor = NearestFriend(b, x, y, 700.0);
                    if (anchor == null) { b.GoTo(x >> 4, y >> 4); continue; }
                    _recallTo[b._id] = anchor._id;
                    _recalls.Add(new KeyValuePair<ushort, int>(b._id, now + 500 + _m.Rng.Next(2001)));
                }
            }
        }

        void RunRecalls(int now)
        {
            for (int i = 0; i < _recalls.Count; )
            {
                if (now - _recalls[i].Value < 0) { i++; continue; }
                ushort id = _recalls[i].Key;
                _recalls.RemoveAt(i);
                CTFXBot b = _m.GetBot(id), to = null;
                ushort aid;
                if (_recallTo.TryGetValue(id, out aid)) { to = _m.GetBot(aid); _recallTo.Remove(id); }
                if (b == null || b.IsDead || b.Released || b.HasTarget) continue;
                if (to == null || to.IsDead || to.Released) continue;
                if (TeleportTo(b, to, now, "defend")) Recalls++;
                else b.GoTo(to._state.positionX >> 4, to._state.positionY >> 4);   // pinned: run there
            }
        }

        /// <summary>Now and then an idle roamer teleports to a teammate fighting inside its building (the action clusters).</summary>
        void JoinFights(List<CTFXBotGroup> groups, int now)
        {
            foreach (CTFXBotGroup g in groups)
            {
                if (g.Quad == null) continue;
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Style != CTFXStyle.Roam && s.Style != CTFXStyle.Adhd) continue;
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (b == null || b.IsDead || b.Released || b.HasTarget || now - b.WarpReadyAt < 0) continue;
                    if (_m.Cmdr.Manages(b)) continue;                       // it hunts fights by its own choice (CTFXCommand)
                    if (_m.Rng.NextDouble() >= 0.06) continue;                      // about once every 25 s per idle roamer
                    CTFXBot best = null;
                    double bd = 1500.0;
                    foreach (CTFXBot o in _m.LiveBots)
                    {
                        if (o == b || o.IsDead || o.Released || o._team != b._team || !o.HasTarget) continue;
                        if (!_m.InQuad(g.Quad, o)) continue;
                        double d = Dist(o, b);
                        if (d > 400.0 && d < bd) { bd = d; best = o; }
                    }
                    if (best != null && TeleportTo(b, best, now, "join")) Joins++;
                }
            }
        }

        // ================================================================ warps
        internal int WarpCapped;
        /// <summary>WarpTeam: b goes to teammate <paramref name="to"/>. Refused when an enemy anti-warp source covers b's spot.</summary>
        internal bool TeleportTo(CTFXBot b, CTFXBot to, int now, string why)
        {
            if (b == null || to == null || b.IsDead || to.IsDead || b._team != to._team || b.InDropship || to.InDropship) return false;
            if (why != "flag home" && now - b.WarpCapUntil < 0) { WarpCapped++; return false; }   // Travis 10-16: warps capped per bot
            if (_m.Flags.Carrying(b) && why != "flag home") return false;       // a carrier warps only to get the flag home
            if (now - b.WarpReadyAt < 0 || b.Stunned(now)) return false;
            bool lead = IsLeaderClass(b.ClassId);
            int item = lead ? SlTeleport : SuitTeleport;
            string pin = _m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b);
            b.WarpReadyAt = now + (lead ? SlTeleportCooldown : TeleportCooldown) / 4;       // a refused try waits a bit too
            if (pin != null)
            {
                Pinned++;
                if (Pinned <= 3) _m.Note("warp refused (" + why + "): a " + b.Style + " bot is anti-warped by " + pin);
                return false;
            }
            if (!b.SpendEnergy(item)) { _m.WarpEnergyRefusals++; return false; }   // the teleport's energy, as a player pays it
            b.WarpReadyAt = now + (lead ? SlTeleportCooldown : TeleportCooldown);
            int span = (int)Dist(b, to);
            Move(b, to._state.positionX, to._state.positionY, item, ItemAccuracy(item, lead ? 32 : 64), now);
            b.WarpCapUntil = now + 45000 + (b._id % 4) * 15000;
            if (Warps <= 3) _m.Note("warp #" + Warps + " (" + why + "): a " + b.Style + " bot teleported " + span + " px onto a teammate (item " + item + ")");
            return true;
        }

        /// <summary>WarpTeam onto a spot (a human teammate): b goes to (x, y) px. As TeleportTo, without a bot at the other end.</summary>
        internal bool TeleportToPx(CTFXBot b, int x, int y, int now, string why)
        {
            if (b == null || b.IsDead || b.InDropship || (_m.Flags.Carrying(b) && why != "flag home") || (_m.Mini != null && _m.Mini.Carrying(b))) return false;      // 11-11: a MiniTP carrier only goes home by the medic's summon
            if (why != "flag home" && now - b.WarpCapUntil < 0) { WarpCapped++; return false; }   // Travis 10-16: warps capped per bot
            if (now - b.WarpReadyAt < 0 || b.Stunned(now)) return false;
            bool lead = IsLeaderClass(b.ClassId);
            int item = lead ? SlTeleport : SuitTeleport;
            b.WarpReadyAt = now + (lead ? SlTeleportCooldown : TeleportCooldown) / 4;
            if (_m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b) != null) { Pinned++; return false; }
            if (!b.SpendEnergy(item)) { _m.WarpEnergyRefusals++; return false; }
            b.WarpReadyAt = now + (lead ? SlTeleportCooldown : TeleportCooldown);
            Move(b, x, y, item, ItemAccuracy(item, lead ? 32 : 64), now);
            b.WarpCapUntil = now + 45000 + (b._id % 4) * 15000;
            return true;
        }

        internal const int TeleportSummoner = 17, MedicSummonCooldown = 20000;

        /// <summary>Does b carry a summoner (the leader classes: SL Summon; medics: Teleport Summoner)?</summary>
        internal bool CarriesSummoner(CTFXBot b) { return b != null && (IsLeaderClass(b.ClassId) || b.ClassId == CTFXBot.SK_MEDIC); }

        /// <summary>SL Summon on a human teammate who asked for it: charges, energy, anti-warp at both ends, then he lands at the bot.</summary>
        internal bool SummonHuman(CTFXBot leader, Player p, int now)
        {
            string why = SummonRefusal(leader, p, now);
            LastSummonRefusal = why;
            if (why != null)
            {
                SummonRefusals++;
                if (SummonRefusals <= 6) _m.Note("summon for " + p._alias + " refused (" + leader.Slot.Name + ", class " + leader.ClassId + "): " + why);
                return false;
            }
            bool medic = !IsLeaderClass(leader.ClassId);
            int item = medic ? TeleportSummoner : SlSummon;
            if (medic) leader.SummonNextAt = now + MedicSummonCooldown;
            else
            {
                leader.SummonCharges--;
                if (leader.SummonCharges <= 0) leader.SummonRechargeAt = now + SummonRecharge;
                leader.SummonNextAt = now + 600;
            }
            _m.AutoDropOnBotSummon(p, leader);   // before the warp, as the engine's Player.WarpItem
            p.warp(Helpers.ResetFlags.ResetNone, leader._state, (short)ItemAccuracy(item, 32), -1, 0);
            Summons++;
            return true;
        }

        internal int SummonRefusals;
        internal string LastSummonRefusal;          // why the last SummonHuman was refused (null = it went)

        /// <summary>Why the summoner cannot pull human p in now (null = it can; the energy is spent then).</summary>
        string SummonRefusal(CTFXBot leader, Player p, int now)
        {
            if (p == null || p.IsDead || p.IsSpectator) return "he is dead or spectating";
            if (p.ActiveVehicle == null || !p.ActiveVehicle._type.IsWarpable) return "his vehicle cannot be warped";
            bool medic = !IsLeaderClass(leader.ClassId);
            if (medic ? (leader.ClassId != CTFXBot.SK_MEDIC || now - leader.SummonNextAt < 0) : !CanSummon(leader, now)) return "summoner recharging";
            if (leader.Stunned(now) || leader.InDropship) return "summoner stunned or in the dropship";
            string a = _m.AntiWarpedAt(leader._state.positionX, leader._state.positionY, leader._team, leader);
            if (a != null) return "summoner anti-warped by " + a;
            if (_m.Arena.checkBotAntiwarp(p) != null || _m.Arena.checkVehAntiWarp(p) != null || p.checkAntiWarp() != null) { Pinned++; return "he is anti-warped"; }
            if (!leader.SpendEnergy(medic ? TeleportSummoner : SlSummon)) { _m.WarpEnergyRefusals++; return "summoner energy"; }
            return null;
        }

        // ---- the AC heavies of a turret room (posts, Travis 10-13): they kill from the back of the room and live long, so they build
        // bounty; when one is hurt or the room is breaking (3+ enemies within 300 px), an anchor summoner deeper in the base (a leader
        // class or medic of its team, nearer the flag) pulls it back, and it holds there 15-25 s (the medics heal it) before walking
        // back to its post. No summoner: it walks back to the flag room itself.
        int _lastPostCheck;
        internal int PostRescues, PostFallbacks;

        internal void PostStep(int now)
        {
            if (now - _lastPostCheck < 1000) return;
            _lastPostCheck = now;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.Style != CTFXStyle.Post || b.IsDead || b.InDropship || now - b.PostHoldUntil < 0 || b.Group == null || b.Group.HomeEnd == null) continue;
                bool hurt = b._state.health < 0.45 * b._type.Hitpoints;
                int near = 0;
                foreach (CTFXBot o in _m.LiveBots)
                    if (!o.IsDead && o._team != b._team && Dist(o, b) <= 300.0) near++;
                foreach (Player p in _m.Arena.PlayersIngame)
                    if (!p.IsDead && p._team != b._team)
                    { double dx = p._state.positionX - b._state.positionX, dy = p._state.positionY - b._state.positionY; if (dx * dx + dy * dy <= 300.0 * 300.0) near++; }
                if (!hurt && near < 3) continue;
                int fx, fy;
                if (!_m.RouteNode(b.Group.HomeEnd + ":patrol", 0, out fx, out fy)) continue;
                fx = fx * 16 + 8; fy = fy * 16 + 8;
                // a summoner of its team nearer the flag room than the post, within 900 px of it
                CTFXBot best = null;
                double bd = double.MaxValue;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o == b || o.IsDead || o.InDropship || o._team != b._team || !CarriesSummoner(o)) continue;
                    double dFlag = Math.Sqrt(Math.Pow(o._state.positionX - fx, 2) + Math.Pow(o._state.positionY - fy, 2));
                    if (dFlag > 900.0 || Dist(o, b) < 200.0) continue;
                    if (dFlag < bd && (IsLeaderClass(o.ClassId) ? CanSummon(o, now) : now - o.SummonNextAt >= 0)) { bd = dFlag; best = o; }
                }
                b.PostHoldUntil = now + 15000 + _m.Rng.Next(10001);
                if (best != null && (IsLeaderClass(best.ClassId) ? Summon(best, b, now) : MedicSummon(best, b, now)))
                {
                    PostRescues++;
                    _m.Chat.Say(best, "pulled the heavy back", 0.3, true);
                }
                else { b.GoTo(fx >> 4, fy >> 4); PostFallbacks++; }
                b.ForceGoal();
            }
        }

        internal bool MedicSummonBot(CTFXBot medic, CTFXBot b, int now)
        {
            if (medic.ClassId != CTFXBot.SK_MEDIC || now - medic.SummonNextAt < 0) return false;
            return MedicSummon(medic, b, now);
        }

        bool MedicSummon(CTFXBot medic, CTFXBot b, int now)
        {
            if (b.IsDead || medic.IsDead || b.Stunned(now)) return false;
            if (_m.AntiWarpedAt(medic._state.positionX, medic._state.positionY, medic._team, medic) != null) return false;
            if (_m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b) != null) { Pinned++; return false; }
            if (!medic.SpendEnergy(TeleportSummoner)) { _m.WarpEnergyRefusals++; return false; }
            medic.SummonNextAt = now + MedicSummonCooldown;
            Move(b, medic._state.positionX, medic._state.positionY, TeleportSummoner, ItemAccuracy(TeleportSummoner, 48), now, medic);
            Summons++;
            return true;
        }

        /// <summary>SummonTeam: the leader pulls teammate b to itself. Checks the summoner's spot, then b's.</summary>
        internal bool Summon(CTFXBot leader, CTFXBot b, int now)
        {
            if (b.IsDead || leader.IsDead || b.Stunned(now) || _m.Flags.Carrying(b)) return false;
            if (_m.AntiWarpedAt(leader._state.positionX, leader._state.positionY, leader._team, leader) != null) return false;
            if (_m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b) != null) { Pinned++; return false; }
            if (!leader.SpendEnergy(SlSummon)) { _m.WarpEnergyRefusals++; return false; }     // the summoner pays, as a player does
            leader.SummonCharges--;
            if (leader.SummonCharges <= 0) leader.SummonRechargeAt = now + SummonRecharge;
            leader.SummonNextAt = now + 600;                                // the item's prefire + fire delay
            Move(b, leader._state.positionX, leader._state.positionY, SlSummon, ItemAccuracy(SlSummon, 32), now, leader);
            Summons++;
            return true;
        }

        internal bool CanSummon(CTFXBot leader, int now)
        {
            if (!IsLeaderClass(leader.ClassId) || leader.IsDead || now - leader.SummonNextAt < 0) return false;
            if (leader.SummonCharges <= 0)
            {
                if (now - leader.SummonRechargeAt < 0) return false;
                leader.SummonCharges = 3;
            }
            return true;
        }

        void Move(CTFXBot b, int dx, int dy, int item, int acc, int now)
        {
            Move(b, dx, dy, item, acc, now, b);
        }

        /// <summary>The landing: random within +-acc/2 of (dx, dy) on a spot the body fits (8 tries, else the destination).</summary>
        void Move(CTFXBot b, int dx, int dy, int item, int acc, int now, CTFXBot user)
        {
            int lx = dx, ly = dy, h = Math.Max(1, acc / 2);
            for (int i = 0; i < 8; i++)
            {
                int ox = dx + _m.Rng.Next(-h, h + 1), oy = dy + _m.Rng.Next(-h, h + 1);
                if (_m.BodyFits(ox, oy)) { lx = ox; ly = oy; break; }
            }
            _m.WarpFx(user, b, item, b._state.positionX, b._state.positionY);
            b.Teleported(lx, ly, now);
            Warps++;
        }

        static readonly Dictionary<int, int> _acc = new Dictionary<int, int>();
        static int ItemAccuracy(int id, int fallback)
        {
            int a;
            if (_acc.TryGetValue(id, out a)) return a;
            ItemInfo.WarpItem w = AssetManager.Manager.getItemByID(id) as ItemInfo.WarpItem;
            a = w != null && w.accuracyRadius > 0 ? w.accuracyRadius : fallback;
            _acc[id] = a;
            return a;
        }

        // ================================================================ goals (CTFXBot.RouteGoal asks)
        /// <summary>The squad leader's goal for its group's phase: the home mouth while gathering, the enemy flag room after the storm.</summary>
        internal bool LeaderGoal(CTFXBotGroup g, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXStorm st = g.Storm;
            if (st == null) return false;
            CTFXBot leader = Leader(g);
            bool there = leader != null && _m.InsideEnd(st.Enemy, leader);
            if (st.Phase == CTFXStorm.Hold || (st.Phase == CTFXStorm.Rest && there))     // sweep the enemy base (a resting squad that is in it stays)
                return _m.Rng.NextDouble() < 0.35 ? _m.RouteNode(st.Enemy + ":patrol", 0, out tx, out ty)
                    : _m.Rng.NextDouble() < 0.5 ? _m.RandomSpawnNode(st.Enemy, out tx, out ty) || _m.RandomRouteNode(st.Enemy + ":patrol", out tx, out ty)
                    : _m.RandomRouteNode(st.Enemy + ":patrol", out tx, out ty);
            int px, py;
            if (!StagePx(g, out px, out py)) return false;
            tx = px >> 4; ty = py >> 4;
            return true;
        }

        /// <summary>
        /// The squad is in the enemy base (hold, or resting while still in it): every member but the leader works its own
        /// objective instead of trailing the leader (Travis 10-11: the storm squads clustered and stalled after the first fight).
        /// </summary>
        internal bool Spread(CTFXBotGroup g, CTFXBot b)
        {
            CTFXStorm st = g.Storm;
            if (st == null || b.Slot == null) return false;
            CTFXBot leader = Leader(g);
            if (leader == b || (leader == null && b.Slot.StyleIndex == 0)) return false;
            return st.Phase != CTFXStorm.Storm;                        // gather / wait / rest: loiter on its own (MemberGoal); hold: fire team
        }

        /// <summary>
        /// A fire team member's next objective in the enemy end, by its place in the squad: 1, 4 hunt the enemy turrets (the
        /// nearest standing one, else the flag room); 2, 5 flank through the end (its spawn spots); 3, 6 hold its mouth (the last
        /// nodes of the raid route in); the rest sweep the flag room. Each picks a new one when it gets there.
        /// </summary>
        internal bool MemberGoal(CTFXBotGroup g, CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXStorm st = g.Storm;
            if (st == null) return false;
            int k = b.Slot != null ? b.Slot.StyleIndex : 0;
            if (st.Phase != CTFXStorm.Hold && !(st.Phase == CTFXStorm.Rest && _m.InsideEnd(st.Enemy, b)))
                return LoiterGoal(g, b, out tx, out ty);
            switch (k % 3)
            {
                case 1:
                    {
                        CTFXTurretSpot best = null;
                        double bd = double.MaxValue;
                        foreach (CTFXTurretSpot s in _m.Turrets.Spots)
                        {
                            if (s.End != st.Enemy || s.TurretId == 0) continue;
                            double dx = s.Tx * 16 + 8 - b._state.positionX, dy = s.Ty * 16 + 8 - b._state.positionY, d = dx * dx + dy * dy;
                            if (d < bd) { bd = d; best = s; }
                        }
                        if (best != null) { tx = best.Tx + _m.Rng.Next(-4, 5); ty = best.Ty + _m.Rng.Next(-4, 5); return true; }
                        return _m.RouteNode(st.Enemy + ":patrol", 0, out tx, out ty);
                    }
                case 2:
                    return _m.RandomSpawnNode(st.Enemy, out tx, out ty) || _m.RandomRouteNode(st.Enemy + ":patrol", out tx, out ty);
                default:
                    if (k % 2 == 1) return _m.RandomRouteNode(st.Enemy + ":patrol", out tx, out ty);
                    int n = _m.Rng.Next(3);                                  // one of the last 3 nodes of the raid route in: its mouth
                    return _m.RouteNode(st.Home + ">" + st.Enemy + ":raid", -1 - n, out tx, out ty) || _m.RandomRouteNode(st.Enemy + ":patrol", out tx, out ty);
            }
        }

        /// <summary>Waiting for the storm: any route node within Loiter px of the staging spot, away from the squad's crowd.</summary>
        bool LoiterGoal(CTFXBotGroup g, CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int sx, sy;
            CTFXRouteBook rb = _m.Routes;
            if (rb == null || rb.N == 0 || !StagePx(g, out sx, out sy)) return false;
            int best = -1;
            double bs = double.MinValue;
            List<CTFXBot> mem = new List<CTFXBot>(Members(g));
            for (int i = 0; i < 30; i++)
            {
                int n = _m.Rng.Next(rb.N);
                double dx = rb.X[n] - sx, dy = rb.Y[n] - sy;
                if (dx * dx + dy * dy > Loiter * Loiter) continue;
                int crowd = 0;
                foreach (CTFXBot o in mem)
                    if (o != b && Math.Abs(o._state.positionX - rb.X[n]) + Math.Abs(o._state.positionY - rb.Y[n]) < 160) crowd++;
                double sc = _m.Rng.NextDouble() / (1.0 + crowd);
                if (sc > bs) { bs = sc; best = n; }
            }
            if (best < 0) return false;
            tx = rb.X[best] >> 4; ty = rb.Y[best] >> 4;
            return true;
        }

        /// <summary>The staging spot: out in the hall, the third node of the raid route from the home end (10-12: staging at the
        /// home mouth kept the squad standing in its own base; out here it waits where the traffic is).</summary>
        bool StagePx(CTFXBotGroup g, out int px, out int py)
        {
            px = 0; py = 0;
            int tx, ty;
            if (g.Storm == null || !_m.RouteNode(g.Storm.Home + ">" + g.Storm.Enemy + ":raid", 2, out tx, out ty)) return false;
            px = tx * 16 + 8; py = ty * 16 + 8;
            return true;
        }

        // ================================================================ helpers
        CTFXBot Leader(CTFXBotGroup g)
        {
            CTFXBot l = _m.GetBot(g.Leader[(int)CTFXStyle.Assault]);
            bool sat = g.Storm != null && l != null && l.Slot != null && g.Storm.SitOut.Contains(l.Slot);
            if (l != null && !l.IsDead && !l.Released && !sat && !l.InDropship) return l;
            foreach (CTFXSlot s in g.Slots)
            {
                if (s.Style != CTFXStyle.Assault || (g.Storm != null && g.Storm.SitOut.Contains(s))) continue;
                CTFXBot b = _m.GetBot(s.BotId);
                if (b != null && !b.IsDead && !b.Released && !b.InDropship) { g.Leader[(int)CTFXStyle.Assault] = b._id; return b; }
            }
            return null;
        }

        List<CTFXBot> Members(CTFXBotGroup g)
        {
            _tmp.Clear();
            foreach (CTFXSlot s in g.Slots)
            {
                if (s.Style != CTFXStyle.Assault || (g.Storm != null && g.Storm.SitOut.Contains(s))) continue;
                CTFXBot b = _m.GetBot(s.BotId);
                if (b != null && !b.IsDead && !b.Released && !b.InDropship) _tmp.Add(b);       // still in the dropship: not with the squad yet
            }
            return _tmp;
        }

        /// <summary>No opening in 2.5 min: the stormer comes out and fights with its team, the cycle starts over after a rest.</summary>
        void Restart(CTFXBotGroup g, CTFXStorm st, CTFXBot stormer, int now)
        {
            NoOpening++;
            Enter(g, st, CTFXStorm.Rest, now);
            st.Until = now + 20000 + _m.Rng.Next(15001);
            if (stormer != null) stormer.NewHide();
        }

        /// <summary>A member that can drop what it is doing and blink in now.</summary>
        bool FreeToStorm(CTFXBot b, int now)
        {
            return !b.IsDead && !b.Released && !b.InDropship && !b.HasTarget && !b.Stunned(now) && !_m.Flags.Carrying(b) && now - b.WarpReadyAt >= -3000;
        }

        /// <summary>Enemies of b (bots and players) within r px.</summary>
        int EnemiesNear(CTFXBot b, double r)
        {
            int n = 0, x = b._state.positionX, y = b._state.positionY;
            foreach (CTFXBot q in _m.LiveBots)
            {
                if (q.IsDead || q.Released || q.InDropship || q._team == b._team) continue;
                double dx = q._state.positionX - x, dy = q._state.positionY - y;
                if (dx * dx + dy * dy <= r * r) n++;
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team == b._team) continue;
                double dx = p._state.positionX - x, dy = p._state.positionY - y;
                if (dx * dx + dy * dy <= r * r) n++;
            }
            return n;
        }

        CTFXBot Stormer(CTFXBotGroup g)
        {
            foreach (CTFXSlot s in g.Slots)
            {
                if (s.Style != CTFXStyle.Stormer) continue;
                CTFXBot b = _m.GetBot(s.BotId);
                if (b != null && !b.IsDead && !b.Released) return b;
            }
            return null;
        }

        CTFXBot NearestFriend(CTFXBot b, int x, int y, double within)
        {
            CTFXBot best = null;
            double bd = within;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == b || o.IsDead || o.Released || o._team != b._team || o.InDropship) continue;
                double d = DistPx(o, x, y);
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        static int Alive(List<CTFXBot> l)
        {
            int n = 0;
            foreach (CTFXBot b in l) if (!b.IsDead) n++;
            return n;
        }

        static bool IsLeaderClass(int cls) { return cls == 7 || cls == 11 || cls == 12; }   // Squad Leader, Sergeant, Captain

        static double Dist(CTFXBot a, CTFXBot b) { return DistPx(a, b._state.positionX, b._state.positionY); }

        static double DistPx(CTFXBot a, int x, int y)
        {
            double dx = a._state.positionX - x, dy = a._state.positionY - y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static string Team(CTFXBotGroup g) { return g.Team != null ? g.Team._name : "?"; }

        /// <summary>One line per storming group for the stats: team, phase and cycles.</summary>
        internal string Summary(List<CTFXBotGroup> groups)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (CTFXBotGroup g in groups)
            {
                if (g.Storm == null) continue;
                sb.Append(sb.Length == 0 ? "" : ", ").Append(Team(g)).Append(' ').Append(CTFXStorm.Names[g.Storm.Phase]).Append(" #").Append(g.Storm.Cycles + 1);
                List<CTFXBot> mem = Members(g);
                if (mem.Count >= 2)
                {   // how spread out the squad is (mean distance from its centre) and how many of it are on the move
                    double cx = 0, cy = 0;
                    foreach (CTFXBot b in mem) { cx += b._state.positionX; cy += b._state.positionY; }
                    cx /= mem.Count; cy /= mem.Count;
                    double sum = 0;
                    int moving = 0;
                    foreach (CTFXBot b in mem)
                    {
                        sum += Math.Sqrt((b._state.positionX - cx) * (b._state.positionX - cx) + (b._state.positionY - cy) * (b._state.positionY - cy));
                        if (b.Moving) moving++;
                    }
                    sb.Append(" spread ").Append((int)(sum / mem.Count)).Append(" px, moving ").Append(moving).Append('/').Append(mem.Count);
                    int nearT = 0, onT = 0;
                    System.Text.StringBuilder where = new System.Text.StringBuilder();
                    foreach (CTFXBot b in mem)
                    {
                        if (b.AtTurret) onT++;
                        foreach (Computer c in _m.Turrets.Live)
                        {
                            if (c._team == b._team || c._primaryGun == null) continue;
                            double dx = c._state.positionX - b._state.positionX, dy = c._state.positionY - b._state.positionY;
                            if (dx * dx + dy * dy <= 450.0 * 450.0) { nearT++; break; }
                        }
                        where.Append(' ').Append(b._state.positionX >> 4).Append(',').Append(b._state.positionY >> 4).Append("->").Append(b.GoalText);
                    }
                    sb.Append(", near an enemy turret ").Append(nearT).Append(", shooting one ").Append(onT).Append(" (").Append(where.ToString().Trim()).Append(')');
                }
                CTFXBot s = Stormer(g);
                sb.Append(" stormer ").Append(s != null ? s.StormerState() : "dead");
            }
            return " | storms " + Storms + " (landed " + Landed + ", walked raids " + Walks + ", sat out " + SatOut + ", stormer moved spots " + Rehides + ", no opening " + NoOpening + ") recalls " + Recalls + " joins " + Joins + " summons " + Summons
                + " warps " + Warps + " (capped per bot " + WarpCapped + ") pinned " + Pinned + (sb.Length > 0 ? " [" + sb.ToString() + "]" : "");
        }
    }
}
/* PARSER GUARD - keep last */
