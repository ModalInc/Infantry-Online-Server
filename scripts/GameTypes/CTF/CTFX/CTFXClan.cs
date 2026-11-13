// CTF:X server-side bots - THE ELITE CLAN (Travis 10-15 night: "large elite private teams, which were the most dominant ctfx teams in
// history, so maybe the elite bots over time form teams amongst eachother and take over the game for their elite focus coordination
// and skill in taking top base, establishing a very strong defensive line and constantly defending well while attacking flags at
// other bases and dominating the competition").
//  - From minute 5, every 2 min, up to 2 of the best killers on the map (ace or veteran, 6+ kills, K/D 2+, ranked by K/D x kills) who
//    play a free style (roam, guard, sniper, assault, stormer, ADHD; never a post, an engineer or an outside pack) are asked to the
//    clan. The clan starts on the brown hall team of the best of them (else the hall team with the best K/D). A recruit switches at
//    its next respawn, as a player leaves his team and joins another: its seat moves group, its player-list row leaves and comes
//    back in the clan's colours. 12 members at most.
//  - At 5 members the clan rises: its home becomes the focus base (top brown). The commander gives it anchors on top brown's chokes,
//    a defensive line and raids on the other bases' flags, and its carriers bring flags there: it takes top brown from whoever
//    holds it and keeps it.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBotManager
    {
        internal CTFXBotGroup Clan;
        internal bool ClanRisen;
        int _clanT0, _clanNext;
        readonly Dictionary<CTFXSlot, CTFXBotGroup> _recruits = new Dictionary<CTFXSlot, CTFXBotGroup>();
        readonly Dictionary<Team, int> _given = new Dictionary<Team, int>();
        internal int ClanAsks, ClanJoins;
        const int ClanMax = 12, ClanRise = 5;

        static double ClanKd(CTFXSlot s) { return s.Kills / (double)Math.Max(1, s.Deaths); }

        static bool ClanStyle(CTFXStyle st)
        {
            return st == CTFXStyle.Roam || st == CTFXStyle.Guard || st == CTFXStyle.Sniper || st == CTFXStyle.Assault || st == CTFXStyle.Stormer || st == CTFXStyle.Adhd;
        }

        int ClanSize()
        {
            int n = 0;
            if (Clan != null) foreach (CTFXSlot s in Clan.Slots) if (!s.Retired) n++;
            return n;
        }

        internal void ClanStep(int now)
        {
            if (Clan != null && !_groups.Contains(Clan)) { Clan = null; ClanRisen = false; _recruits.Clear(); _given.Clear(); _clanT0 = 0; _siegeHave = false; _siegeLaunch = 0; _siegeFirst = 0; }   // a new start rebuilt the groups
            ClanSiegeStep(now);
            if (_groups.Count == 0 || FocusEnd == null) return;
            if (_clanT0 == 0) { _clanT0 = now; _clanNext = now + 300000; return; }
            if (now - _clanNext < 0) return;
            _clanNext = now + 120000;
            if (ClanSize() + _recruits.Count >= ClanMax) return;
            string fq = FocusEnd.Substring(0, FocusEnd.IndexOf('-'));
            List<CTFXSlot> cand = new List<CTFXSlot>();
            foreach (CTFXBotGroup g in _groups)
            {
                if (g == Clan || g.Team == null || g.Outside || g.Ambush || g.DropPack) continue;
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Retired || s.Human == null || _recruits.ContainsKey(s) || !ClanStyle(s.Style) || s.Benched || s.RoomHome != null) continue;
                    if (s.Human.Tier != "ace" && s.Human.Tier != "veteran") continue;
                    if (s.Kills < 6 || ClanKd(s) < 2.0) continue;
                    cand.Add(s);
                }
            }
            if (cand.Count == 0) return;
            cand.Sort(delegate(CTFXSlot a, CTFXSlot b) { return (ClanKd(b) * b.Kills).CompareTo(ClanKd(a) * a.Kills); });
            if (Clan == null)
            {   // the clan forms around the best of them on a brown hall team, else on the hall team with the best K/D
                foreach (CTFXSlot s in cand) if (s.Group.CoreOf == fq) { Clan = s.Group; break; }
                if (Clan == null)
                {
                    double best = -1;
                    foreach (CTFXBotGroup g in _groups)
                    {
                        if (g.CoreOf != fq || g.Team == null) continue;
                        int k = 0, d = 0;
                        foreach (CTFXSlot s in g.Slots) { k += s.Kills; d += s.Deaths; }
                        double kd = k / (double)Math.Max(1, d);
                        if (kd > best) { best = kd; Clan = g; }
                    }
                }
                if (Clan == null) return;
                Out("[CTFXBots] clan: the elite gather on " + Clan.Team._name + " (" + ClanSize() + " there now)");
                cand.RemoveAll(delegate(CTFXSlot s) { return s.Group == Clan; });
            }
            int asked = 0;
            foreach (CTFXSlot s in cand)
            {
                if (asked >= 2 || ClanSize() + _recruits.Count >= ClanMax) break;
                int given;
                _given.TryGetValue(s.Group.Team, out given);
                if (given >= 2) continue;                                                     // no team is gutted for it
                _given[s.Group.Team] = given + 1;
                _recruits[s] = Clan;
                ClanAsks++; asked++;
                Out("[CTFXBots] clan: " + s.Name + " (" + s.Human.Tier + " " + s.Style + ", " + s.Kills + "/" + s.Deaths + ", " + s.Group.Team._name + ") will join " + Clan.Team._name + " at its next respawn");
                CTFXBot b = GetBot(s.BotId);
                if (b != null && !b.IsDead && Chat != null) Chat.Say(b, Chat.Pick("brb switching", "joining " + Clan.Team._name, "gonna go " + Clan.Team._name), 0.4, false);
            }
        }

        /// <summary>SpawnSlot, between lives: a recruit leaves its team for the clan (seat, commander seat, player-list row).</summary>
        void ClanSwitch(CTFXSlot slot, int now)
        {
            CTFXBotGroup to;
            if (!_recruits.TryGetValue(slot, out to)) return;
            _recruits.Remove(slot);
            if (to != Clan || to == slot.Group || to.Team == null) return;
            CTFXBotGroup from = slot.Group;
            from.Slots.Remove(slot);
            to.Slots.Add(slot);
            slot.Group = to;
            slot.HomeTeam = to.Team;                                   // 10-22: CTFXRoom counts a team's seats by it
            slot.Index = to.Slots.Count - 1;
            if (Cmdr != null) Cmdr.MoveSeat(slot, to);
            // keep the row and change its team the way the engine does for a player (SC_ChangeTeam). A leave + re-enter under the
            // same id left clients holding the OLD team: its old teammates' shots passed through it (Travis 10-20: "Arlo 92 is invincible")
            SetRowTeam(slot, to.Team);
            ClanJoins++;
            Out("[CTFXBots] clan: " + slot.Name + " left " + (from.Team != null ? from.Team._name : "?") + " for " + to.Team._name + " (" + ClanSize() + " members)");
            if (!ClanRisen && ClanSize() >= ClanRise) Rise(now);
        }

        void Rise(int now)
        {
            ClanRisen = true;
            Clan.HomeEnd = FocusEnd;
            Clan.CoreOf = null;
            Clan.Quad = FocusEnd.Substring(0, FocusEnd.IndexOf('-'));
            if (Cmdr != null) Cmdr.AddBaseTeam(Clan, now);
            foreach (CTFXSlot s in Clan.Slots) { CTFXBot b = GetBot(s.BotId); if (b != null && !b.IsDead) b.ForceGoal(); }
            Out("[CTFXBots] clan: " + Clan.Team._name + " rises with " + ClanSize() + " members - top brown is its home now");
        }

        int _siegeFirst, _siegeLaunch, _siegeX, _siegeY, _siegeAt;
        bool _siegeHave;
        internal int SiegeLaunches, SiegeMembers, SiegeTakes;
        bool _clanHeld;
        int _heldLogAt;

        /// <summary>A risen clan's member while the clan does not hold top brown: the staging spot, or the flag room during a push.</summary>
        internal bool ClanSiegeGoal(CTFXBot b, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!ClanRisen || Clan == null || b.Group != Clan || Flags == null || Flags.Carrying(b)) return false;
            if (Flags.HoldsFocus(Clan.Team)) return false;                                  // it holds the base: a base team's goals
            int fx, fy;
            if (!Flags.EndHomePx(FocusEnd, out fx, out fy)) return false;
            if (_siegeLaunch != 0 && now - _siegeLaunch < 60000)
            {   // the push: into the flag room
                NearestOpenPx(fx + Rng.Next(-96, 97), fy + Rng.Next(-96, 97), out tx, out ty);
                return true;
            }
            if (!_siegeHave)
            {
                int hx, hy;
                string q = FocusEnd.Substring(0, FocusEnd.IndexOf('-'));
                if (!CoreHub(q, out hx, out hy)) return false;
                double dx = hx * 16 + 8 - fx, dy = hy * 16 + 8 - fy, dd = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
                int sx, sy;
                NearestOpenPx(fx + (int)(dx / dd * 750), fy + (int)(dy / dd * 750), out sx, out sy);
                _siegeX = sx * 16 + 8; _siegeY = sy * 16 + 8; _siegeHave = true;
            }
            NearestOpenPx(_siegeX + Rng.Next(-64, 65), _siegeY + Rng.Next(-64, 65), out tx, out ty);
            return true;
        }

        void ClanSiegeStep(int now)
        {
            if (now - _siegeAt < 1000) return;
            _siegeAt = now;
            if (!ClanRisen || Clan == null || Flags == null) return;
            bool held = Flags.HoldsFocus(Clan.Team);
            if (held && !_clanHeld) { SiegeTakes++; if (now - _heldLogAt >= 60000) { _heldLogAt = now; Out("[CTFXBots] clan: " + Clan.Team._name + " holds top brown"); } }
            _clanHeld = held;
            if (held) { _siegeLaunch = 0; _siegeFirst = 0; return; }
            if (_siegeLaunch != 0)
            {
                if (now - _siegeLaunch < 60000) return;
                _siegeLaunch = 0; _siegeFirst = 0;                                          // the push is over: regroup
            }
            if (!_siegeHave) return;
            int there = 0;
            foreach (CTFXSlot s in Clan.Slots)
            {
                CTFXBot b = GetBot(s.BotId);
                if (b == null || b.IsDead || b.InDropship) continue;
                double dx = b._state.positionX - _siegeX, dy = b._state.positionY - _siegeY;
                if (dx * dx + dy * dy <= 240.0 * 240.0) there++;
            }
            if (there == 0) return;
            if (_siegeFirst == 0) _siegeFirst = now;
            int waited = now - _siegeFirst;
            if (!(there >= 5 || (there >= 3 && waited >= 30000) || waited >= 50000)) return;
            _siegeLaunch = now; SiegeLaunches++; SiegeMembers += there;
            Out("[CTFXBots] clan: " + Clan.Team._name + " storms top brown with " + there + " (waited " + (waited / 1000) + " s)");
            foreach (CTFXSlot s in Clan.Slots) { CTFXBot b = GetBot(s.BotId); if (b != null && !b.IsDead) b.ForceGoal(); }
        }

        internal string ClanText()
        {
            if (Clan == null) return " | elite clan: not formed (asks " + ClanAsks + ")";
            int k = 0, d = 0, inTop = 0, alive = 0;
            foreach (CTFXSlot s in Clan.Slots)
            {
                k += s.Kills; d += s.Deaths;
                CTFXBot b = GetBot(s.BotId);
                if (b == null || b.IsDead || b.InDropship) continue;
                alive++;
                if (InsideEnd(FocusEnd, b)) inTop++;
            }
            return " | elite clan: " + Clan.Team._name + (ClanRisen ? " (risen, home " + FocusEnd + ")" : " (gathering)") + ", members " + ClanSize() + ", recruits waiting " + _recruits.Count
                + ", joins " + ClanJoins + " of " + ClanAsks + " asked, K/D " + k + "/" + d + ", in top brown " + inTop + " of " + alive + " alive"
                + (Flags != null ? ", holds focus " + (Flags.HoldsFocus(Clan.Team) ? "yes" : "no") : "") + ", sieges " + SiegeLaunches + " (avg " + (SiegeLaunches > 0 ? (SiegeMembers / (double)SiegeLaunches).ToString("0.0") : "-") + "), took top brown " + SiegeTakes + " times";
        }
    }

    internal sealed partial class CTFXCommand
    {
        /// <summary>A group that became a base team mid-game (the elite clan rising): its command, chokes, line and seats.</summary>
        internal void AddBaseTeam(CTFXBotGroup g, int now)
        {
            if (!On || g.HomeEnd == null || g.Team == null) return;
            string enemy = _m.OtherEnd(g.HomeEnd);
            if (enemy == null) return;
            CTFXTeamCmd t = null;
            foreach (CTFXTeamCmd o in Teams) if (o.Team == g.Team && o.End == g.HomeEnd) t = o;
            if (t == null)
            {
                t = new CTFXTeamCmd();
                t.Team = g.Team; t.End = g.HomeEnd; t.Enemy = enemy; t.Quad = g.Quad;
                BuildChokes(t);
                BuildLines(t);
                Teams.Add(t);
            }
            foreach (CTFXSlot s in g.Slots) if (!t.Seats.Contains(s)) { s.Intent = new CTFXIntent(); t.Seats.Add(s); }
        }

        /// <summary>A seat changed group (the clan): off its old command, onto the new group's command if it has one.</summary>
        internal void MoveSeat(CTFXSlot s, CTFXBotGroup to)
        {
            foreach (CTFXTeamCmd t in Teams) t.Seats.Remove(s);
            s.Intent = null;
            foreach (CTFXTeamCmd t in Teams)
                if (t.Team == to.Team && t.End == to.HomeEnd) { s.Intent = new CTFXIntent(); t.Seats.Add(s); break; }
        }
    }
}
/* PARSER GUARD - keep last */
