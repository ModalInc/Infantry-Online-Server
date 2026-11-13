// CTF:X server-side bots - THE SKILL SPREAD (Travis 10-14 night: mirror the 2001 public CTF:X, "a wide variety of skills, so some
// bots should dominate others"). Per seat: kills, deaths, best streak, bounty peak, time held at 10k+ and 30k+. The stats line
// shows the top five seats against the median, so a run can be judged by how far the elite stand above the rest.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Text;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBotManager
    {
        int _spreadAt;

        /// <summary>A seat's bot killed someone (a bot or a player).</summary>
        static void SeatKill(CTFXSlot s)
        {
            if (s == null) return;
            s.Kills++; s.Streak++;
            if (s.Streak > s.BestStreak) s.BestStreak = s.Streak;
        }

        /// <summary>A seat's bot died.</summary>
        static void SeatDeath(CTFXSlot s)
        {
            if (s == null) return;
            s.Deaths++; s.Streak = 0;
        }

        /// <summary>Time held at 10k+ / 30k+ and the bounty peak, per seat (called with each stats line and on demand).</summary>
        void SpreadTick(int now)
        {
            int dt = _spreadAt == 0 ? 0 : now - _spreadAt;
            _spreadAt = now;
            if (dt < 0 || dt > 120000) dt = 0;
            foreach (CTFXBotGroup g in Groups)
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Bounty > s.BountyPeak) s.BountyPeak = s.Bounty;
                    if (s.Bounty >= 10000) s.Ms10k += dt;
                    if (s.Bounty >= 30000) s.Ms30k += dt;
                }
        }

        internal string SpreadText()
        {
            SpreadTick(Environment.TickCount);
            List<CTFXSlot> all = new List<CTFXSlot>();
            foreach (CTFXBotGroup g in Groups) foreach (CTFXSlot s in g.Slots) if (s.Kills + s.Deaths > 0) all.Add(s);
            if (all.Count == 0) return "";
            Comparison<CTFXSlot> byKd = delegate(CTFXSlot a, CTFXSlot b) { return Kd(b).CompareTo(Kd(a)); };
            all.Sort(byKd);
            double med = Kd(all[all.Count / 2]), low = Kd(all[all.Count * 3 / 4]);
            int k1 = 0, k10 = 0, k30 = 0;
            long ms10 = 0, ms30 = 0;
            foreach (CTFXSlot s in all) { if (s.BountyPeak >= 1000) k1++; if (s.BountyPeak >= 10000) k10++; if (s.BountyPeak >= 30000) k30++; ms10 += s.Ms10k; ms30 += s.Ms30k; }
            StringBuilder sb = new StringBuilder(" | spread: K/D median ").Append(med.ToString("0.00")).Append(", bottom quarter ").Append(low.ToString("0.00"))
                .Append(", top five ");
            for (int i = 0; i < all.Count && i < 5; i++)
            {
                CTFXSlot s = all[i];
                sb.Append(i == 0 ? "" : "; ").Append(s.Name).Append(' ').Append(s.Kills).Append('/').Append(s.Deaths)
                  .Append(" (").Append(s.Human != null ? s.Human.Tier : "-").Append(' ').Append(s.Style).Append(", ").Append(s.Group != null && s.Group.Team != null ? s.Group.Team._name : "-")
                  .Append(", streak ").Append(s.BestStreak).Append(", peak ").Append(s.BountyPeak).Append(')');
            }
            // the damage ledger of the three biggest bounty peaks: per hit, where its HP damage went
            List<CTFXSlot> rich = new List<CTFXSlot>(all);
            rich.Sort(delegate(CTFXSlot a, CTFXSlot b) { return b.BountyPeak.CompareTo(a.BountyPeak); });
            sb.Append(" | tank ledger (peak bounty: hits, raw HP, kept off by shield / armour / under-2 floor, landed, healed, stims, energizers, recalls, avg energy at a hit)");
            for (int i = 0; i < rich.Count && i < 3; i++)
            {
                CTFXSlot s = rich[i];
                double r = Math.Max(1.0, s.TkRaw);
                CTFXBot lb = GetBot(s.BotId);
                sb.Append(i == 0 ? " " : "; ").Append(s.Name).Append(' ').Append(s.BountyPeak).Append(" (").Append(lb != null ? CTFXBot.ClassName(lb.ClassId) : "-").Append("): ")
                  .Append(s.TkHits).Append(" hits, ").Append((int)s.TkRaw).Append(" raw, shield ").Append((s.TkShield / r * 100).ToString("0")).Append("% armour ")
                  .Append((s.TkArmour / r * 100).ToString("0")).Append("% floor ").Append((s.TkFloor / r * 100).ToString("0")).Append("%, landed ").Append((int)s.TkHp)
                  .Append(" (").Append((s.TkHp / r * 100).ToString("0")).Append("%), healed ").Append((int)s.TkHealed).Append(", stims ").Append(s.TkStims)
                  .Append(", energizers ").Append(s.TkEnergizers).Append(", recalls ").Append(s.TkRecalls).Append(", energy ")
                  .Append(s.TkHits > 0 ? (s.TkEnergyAtHit / s.TkHits * 100).ToString("0") : "-").Append('%');
            }
            double top = Kd(all[0]);
            sb.Append(" | top/median ").Append(med > 0 ? (top / med).ToString("0.0") : "-").Append("x, peaks 1k+ ").Append(k1).Append(" 10k+ ").Append(k10).Append(" 30k+ ").Append(k30)
              .Append(", held 10k+ ").Append(ms10 / 1000).Append(" s, 30k+ ").Append(ms30 / 1000).Append(" s");
            return sb.ToString();
        }

        static double Kd(CTFXSlot s) { return s.Kills / (double)Math.Max(1, s.Deaths); }
    }
}
/* PARSER GUARD - keep last */
