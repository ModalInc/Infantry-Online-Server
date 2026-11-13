// CTF:X server-side bots - BOUNTY HUNTS (Travis 10-14 night: "bounty hunting was a thing, a coordinated storm where someone would
// attach with teleport disruptor (a beefier captain class with carapace, a sciops with cloak + zap glove + other melee, etc.) and
// people may even fly in with infiltrator or jump trooper on a fast pack to close the distance fast, and the elite bountier would be
// really good at marking those types of players hunting him on the minimap and react well in advance and his teammates would also
// attach and protect him and stun for him and try to kill the attackers or buy him space. That's how teams leech experience cash and
// points, by protecting their high bounty player too, as it gives them bounty").
//
// Every 2 s: the biggest enemy bounty on the map (a bot or a player, 400+) may draw a hunt from a team with 6+ free bots (one hunt
// per team, 90 s apart, a 30 % roll per look). The hunt: a TAGGER (a Captain first, then a SciOps / Infiltrator / Jump Trooper) gets
// to the victim (Suit Teleport onto the teammate nearest it when far), and once it is within 350 px the 3-5 HUNTERS warp onto the
// tagger (or the tagger, a summoner, pulls them in); then all go for the victim, which tops their target choice. It ends with the
// victim dead, 45 s, or the hunters gone. The VICTIM (a bot) reads its radar: hunters within 1000 px are noticed by skill (30-90 %
// per look): it backs off toward its team and calls "inc on me"; up to 4 free teammates within 1500 px come to protect it.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXHuntRun
    {
        public Team Team;
        public Player VictimP;
        public CTFXBot VictimB;
        public CTFXBot Tagger;
        public readonly List<CTFXBot> Members = new List<CTFXBot>();
        public int Phase, Since, Until, HopAt;     // Phase 0 = tagging, 1 = in
    }

    internal sealed partial class CTFXBotManager
    {
        readonly List<CTFXHuntRun> _hunts = new List<CTFXHuntRun>();
        readonly Dictionary<Team, int> _huntNext = new Dictionary<Team, int>();
        int _huntAt;
        internal int Hunts, HuntKills, HuntFails, HuntTags, HuntAttaches, HuntNoticed, HuntGuards;
        internal const int HuntMin = 2500;                 // cycle 22: at 400 hunts cut down rising elites (40 min, peak 1.7k); real hunts went after thousands

        static bool HuntDead(CTFXHuntRun h) { return h.VictimP != null ? h.VictimP.IsDead || h.VictimP._arena == null : h.VictimB == null || h.VictimB.IsDead || h.VictimB.InDropship; }
        static int HuntX(CTFXHuntRun h) { return h.VictimP != null ? h.VictimP._state.positionX : h.VictimB._state.positionX; }
        static int HuntY(CTFXHuntRun h) { return h.VictimP != null ? h.VictimP._state.positionY : h.VictimB._state.positionY; }

        bool HuntFree(CTFXBot b)
        {
            if (b == null || b.IsDead || b.InDropship || b.Released || b.Slot == null || b.HasTarget) return false;
            CTFXStyle st = b.Slot.Style;
            if (st == CTFXStyle.Post || st == CTFXStyle.Engineer || st == CTFXStyle.Maser || st == CTFXStyle.Bountier || st == CTFXStyle.Stormer) return false;
            if (b.Slot.Intent != null && (b.Slot.Intent.Act == CTFXAct.Anchor || b.Slot.Intent.Act == CTFXAct.Build || b.Slot.Intent.Act == CTFXAct.Defend)) return false;
            if (Flags.Carrying(b) || b.Pocketing) return false;              // a flag runner may be pulled onto a hunt (cycle 7: 0 hunts)
            foreach (CTFXHuntRun h in _hunts) if (h.Tagger == b || h.Members.Contains(b)) return false;
            return true;
        }

        void HuntStep(int now)
        {
            if (now - _huntAt < 2000) return;
            _huntAt = now;
            // the runs
            for (int i = _hunts.Count - 1; i >= 0; i--)
            {
                CTFXHuntRun h = _hunts[i];
                h.Members.RemoveAll(delegate(CTFXBot m) { return m.IsDead || m.InDropship; });
                bool dead = HuntDead(h);
                if (dead || now - h.Until >= 0 || (h.Members.Count == 0 && (h.Tagger == null || h.Tagger.IsDead)))
                {
                    if (dead) HuntKills++; else HuntFails++;
                    if (Hunts <= 12) Note("bounty hunt by " + h.Team._name + (dead ? ": the victim went down" : ": called off"));
                    foreach (CTFXBot m in h.Members) m.ForceGoal();
                    _hunts.RemoveAt(i);
                    continue;
                }
                int vx = HuntX(h), vy = HuntY(h);
                if (h.Phase == 0 && h.Tagger != null && !h.Tagger.IsDead)
                {
                    double d = Math.Sqrt(Math.Pow(h.Tagger._state.positionX - vx, 2) + Math.Pow(h.Tagger._state.positionY - vy, 2));
                    if (d > 1500 && now - h.HopAt >= 0)
                    {   // far: onto the teammate nearest the victim
                        h.HopAt = now + 6000;
                        CTFXBot to = null; double bd = d - 900;
                        foreach (CTFXBot o in LiveBots)
                        {
                            if (o == h.Tagger || o.IsDead || o.InDropship || o._team != h.Team) continue;
                            double od = Math.Sqrt(Math.Pow(o._state.positionX - vx, 2) + Math.Pow(o._state.positionY - vy, 2));
                            if (od < bd) { bd = od; to = o; }
                        }
                        if (to != null) Squads.TeleportTo(h.Tagger, to, now, "hunt tag");
                    }
                    if (d <= 350)
                    {   // tagged: the hunters come in
                        h.Phase = 1; HuntTags++;
                        Chat.Say(h.Tagger, "on the bounty, come", 0.5, false);
                        foreach (CTFXBot m in h.Members)
                        {
                            bool ok = Squads.CarriesSummoner(h.Tagger) && h.Tagger.ClassId != CTFXBot.SK_MEDIC && Squads.CanSummon(h.Tagger, now) && Squads.Summon(h.Tagger, m, now);
                            if (!ok) ok = Squads.TeleportTo(m, h.Tagger, now, "hunt attach");
                            if (ok) HuntAttaches++;
                            m.ForceGoal();
                        }
                    }
                }
                else if (h.Phase == 0) h.Phase = 1;
                // the victim's side (a bot): hunters on its radar
                if (h.VictimB != null) HuntDefend(h, now);
            }
            // a new hunt: the biggest enemy bounty, per team
            foreach (CTFXBotGroup g in Groups)
            {
                Team t = g.Team;
                if (t == null || (g.HomeEnd == null && g.CoreOf == null)) continue;
                bool busy = false;
                foreach (CTFXHuntRun h in _hunts) if (h.Team == t) { busy = true; break; }
                int next;
                if (busy || (_huntNext.TryGetValue(t, out next) && now - next < 0) || Rng.NextDouble() > 0.30) continue;
                Player vp = null; CTFXBot vb = null; int top = HuntMin - 1;
                foreach (Player p in Arena.PlayersIngame) if (!p.IsDead && p._team != t && p.Bounty > top) { top = p.Bounty; vp = p; vb = null; }
                foreach (CTFXBot o in LiveBots) if (!o.IsDead && !o.InDropship && o._team != t && BountyOf(o) > top) { top = BountyOf(o); vb = o; vp = null; }
                if (vp == null && vb == null) continue;
                List<CTFXBot> free = new List<CTFXBot>();
                foreach (CTFXBot o in LiveBots) if (o._team == t && HuntFree(o)) free.Add(o);
                if (free.Count < 5) continue;
                CTFXBot tagger = null; int tr = 99;
                foreach (CTFXBot o in free)
                {
                    int r = o.ClassId == CTFXBot.SK_CAPT ? 0 : o.ClassId == CTFXBot.SK_SCIOPS ? 1 : o.ClassId == CTFXBot.SK_INFIL ? 2 : o.ClassId == CTFXBot.SK_JT ? 3 : 9;
                    if (r < tr) { tr = r; tagger = o; }
                }
                if (tagger == null) continue;
                free.Remove(tagger);
                CTFXHuntRun run = new CTFXHuntRun();
                run.Team = t; run.VictimP = vp; run.VictimB = vb; run.Tagger = tagger; run.Since = now; run.Until = now + 45000;
                int want = 3 + Rng.Next(3);
                free.Sort(delegate(CTFXBot a, CTFXBot b2) { return Rng.Next(3) - 1; });
                for (int i = 0; i < free.Count && run.Members.Count < want; i++) run.Members.Add(free[i]);
                Flags.Unchase(tagger.Slot);
                foreach (CTFXBot m in run.Members) Flags.Unchase(m.Slot);
                _hunts.Add(run);
                _huntNext[t] = now + 90000;
                Hunts++;
                if (Hunts <= 12) Note("bounty hunt #" + Hunts + " by " + t._name + " on " + (vp != null ? vp._alias : vb.Slot.Name) + " (bounty " + top + "): tagger " + tagger.Slot.Name + " (class " + tagger.ClassId + "), " + run.Members.Count + " hunters");
                tagger.ForceGoal();
                foreach (CTFXBot m in run.Members) m.ForceGoal();
            }
        }

        void HuntDefend(CTFXHuntRun h, int now)
        {
            CTFXBot v = h.VictimB;
            int sx = 0, sy = 0, n = 0;
            List<CTFXBot> near = new List<CTFXBot>(h.Members);
            if (h.Tagger != null) near.Add(h.Tagger);
            foreach (CTFXBot m in near)
            {
                if (m.IsDead) continue;
                double d = Math.Sqrt(Math.Pow(m._state.positionX - v._state.positionX, 2) + Math.Pow(m._state.positionY - v._state.positionY, 2));
                if (d > 1000) continue;
                sx += m._state.positionX; sy += m._state.positionY; n++;
            }
            if (n == 0 || Rng.NextDouble() >= v.HuntSense()) return;
            HuntNoticed++;
            v.BreakAway(now, sx / n, sy / n, 450);
            Chat.Say(v, "inc on me " + Chat.PlaceOf(v), 0.5, false);
            int sent = 0;
            foreach (CTFXBot o in LiveBots)
            {
                if (sent >= 4) break;
                if (o == v || o._team != v._team || !HuntFree(o)) continue;
                double d2 = Math.Pow(o._state.positionX - v._state.positionX, 2) + Math.Pow(o._state.positionY - v._state.positionY, 2);
                if (d2 > 1500.0 * 1500.0) continue;
                o.GoTo(v._state.positionX >> 4, v._state.positionY >> 4);
                sent++; HuntGuards++;
            }
        }

        /// <summary>A hunter's goal: the tagger and (once tagged) the hunters go for the victim. False = not hunting.</summary>
        internal bool HuntGoal(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            foreach (CTFXHuntRun h in _hunts)
            {
                if (h.Tagger != b && !(h.Phase == 1 && h.Members.Contains(b))) continue;
                if (HuntDead(h)) return false;
                NearestOpenPx(HuntX(h), HuntY(h), out tx, out ty);
                return true;
            }
            return false;
        }

        /// <summary>Is this enemy the victim of a hunt b is in (it tops b's target choice)?</summary>
        internal bool HuntVictim(CTFXBot b, Player p, CTFXBot o)
        {
            foreach (CTFXHuntRun h in _hunts)
                if ((h.Tagger == b || h.Members.Contains(b)) && ((p != null && h.VictimP == p) || (o != null && h.VictimB == o))) return true;
            return false;
        }

        internal string HuntText()
        {
            return " | bounty hunts: " + Hunts + " (victim down " + HuntKills + ", called off " + HuntFails + "), tagged " + HuntTags + ", hunters attached " + HuntAttaches
                + ", victims noticed them " + HuntNoticed + ", guards sent " + HuntGuards;
        }
    }

    internal sealed partial class CTFXBot
    {
        /// <summary>How likely a look at the radar spots hunters coming (the elite bountier reads the minimap well).</summary>
        internal double HuntSense() { return TierPick(0.30, 0.90) * (Slot != null && Slot.Bounty >= 1000 ? 1.0 : 0.8); }
    }
}
/* PARSER GUARD - keep last */
