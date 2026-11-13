// CTF:X server-side bots - MILKING THE FLAG (Travis 10-15 night): "getting another bot from another team to truce and coordinate with
// them to 'milk' the flag by dropping a flag in their base, which no one picks up, then they exit the team or game and rejoin that
// team, but that flag still stays on the old team, thats called milking and it was a major part of ctfx meta if teams were so elite
// but didn't want the game to reset back to neutral locations, so its what really allowed people to establish stronger footholds
// consistently to bounty in the top brown base".
//  - The risen elite clan (CTFXClan.cs) owning every flag starts the 90 s victory countdown (CTF.cs OnFlagChange), whose end resets
//    every flag to neutral. Before that, the clan calls a truce with one bot of another base / hall team (an ace or veteran first, the
//    nearest to the clan's flag room): it walks in unharmed (neither side targets the other), picks up a clan flag there and drops it
//    on the spot - now its own team owns that flag, and the countdown is aborted.
//  - The partner then leaves its team (a quiet respawn) and joins the clan; the flag stays owned by its old team, in the clan's base.
//  - Nobody of the clan picks a milked flag up while it lies there (so the clan never owns them all again); every other team may, and
//    has to fight into top brown for it.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBotManager
    {
        CTFXBot _milkPartner;
        Arena.FlagState _milkFlag;
        int _milkAt, _milkUntil;
        readonly HashSet<Arena.FlagState> _milked = new HashSet<Arena.FlagState>();
        internal int MilkCalls, MilkDone, MilkAborted, MilkWinsSeen;
        bool _clanHeldAll;

        /// <summary>A truce between the clan and its milk partner: neither targets the other.</summary>
        internal bool Truce(CTFXBot a, CTFXBot b)
        {
            CTFXBot p = _milkPartner;
            if (p == null || Clan == null || a == null || b == null) return false;
            return (a == p && b._team == Clan.Team) || (b == p && a._team == Clan.Team);
        }

        /// <summary>A milked flag lying in the clan's base: no clan bot picks it up or runs for it.</summary>
        internal bool MilkedFor(Arena.FlagState fs, Team t)
        {
            return Clan != null && t == Clan.Team && _milked.Contains(fs);
        }

        internal void MilkStep(int now)
        {
            if (now - _milkAt < 1000) return;
            _milkAt = now;
            if (Clan == null || !ClanRisen || Clan.Team == null || Flags == null) { _milkPartner = null; _milkFlag = null; _milked.Clear(); return; }
            List<Arena.FlagState> flags = new List<Arena.FlagState>(Flags.All());
            if (flags.Count < 2) return;
            // milked flags stay milked while they lie where they were dropped, owned by someone else
            _milked.RemoveWhere(delegate(Arena.FlagState fs) { return fs.carrier != null || fs.team == null || fs.team == Clan.Team || !fs.bActive; });
            int owned = 0;
            foreach (Arena.FlagState fs in flags) if (fs.carrier == null && fs.team == Clan.Team) owned++;
            bool all = owned == flags.Count;
            if (all && !_clanHeldAll) MilkWinsSeen++;
            _clanHeldAll = all;
            if (_milkPartner != null)
            {
                CTFXBot p = _milkPartner;
                Arena.FlagState fs = _milkFlag;
                if (p.IsDead || p.Released || p.InDropship || now - _milkUntil >= 0 || fs == null || (!all && fs.carrier == null))
                {   // died, too slow, or the set broke on its own
                    if (fs == null || fs.carrier != p.Slot.Entry) { MilkAborted++; Out("[CTFXBots] milk: the truce with " + p.Slot.Name + " ends (" + (p.IsDead ? "died" : !all ? "the clan no longer holds every flag" : "too slow") + ")"); }
                    _milkPartner = null; _milkFlag = null;
                    if (Flags != null) Flags.EndChase(p.Slot);
                    return;
                }
                if (p.Slot != null && fs.carrier == p.Slot.Entry)
                {   // it has the clan's flag: down on the spot, its own team owns it now - the countdown is off
                    Flags.DropHere(fs, p, now);
                    _milked.Add(fs);
                    MilkDone++;
                    Out("[CTFXBots] milk: " + p.Slot.Name + " (" + p._team._name + ") dropped " + fs.flag.GeneralData.Name + " in " + Clan.Team._name + "'s base at "
                        + (fs.posX >> 4) + "," + (fs.posY >> 4) + " - the victory countdown is off; it joins " + Clan.Team._name);
                    if (Chat != null) Chat.Say(p, Chat.Pick("milked", "flag down, gg", "ok its yours"), 0.6, false);
                    _recruits[p.Slot] = Clan;                            // it leaves its team and comes back in the clan's colours
                    _recycle.Add(p._id);
                    _milkPartner = null; _milkFlag = null;
                }
                return;
            }
            if (!all) return;
            // the set is complete: find a partner and the clan flag it will drop
            Arena.FlagState pick = null;
            int hx = 0, hy = 0;
            bool home = Flags.EndHomePx(FocusEnd, out hx, out hy);
            double bd = double.MaxValue;
            foreach (Arena.FlagState fs in flags)
            {
                double d = home ? Math.Sqrt(Math.Pow(fs.posX - hx, 2) + Math.Pow(fs.posY - hy, 2)) : 0;
                if (d < bd) { bd = d; pick = fs; }
            }
            if (pick == null) return;
            CTFXBot best = null;
            double bs = double.MaxValue;
            foreach (CTFXBot o in LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o.Slot == null || o._team == Clan.Team || o.Group == null) continue;
                if (o.Group.HomeEnd == null && o.Group.CoreOf == null) continue;                  // a base / hall team's bot (it may carry)
                if (!ClanStyle(o.Style) || Flags.Carrying(o)) continue;
                double d = Math.Sqrt(Math.Pow(o._state.positionX - pick.posX, 2) + Math.Pow(o._state.positionY - pick.posY, 2));
                if (d > 3500) continue;
                if (o.Slot.Human != null && (o.Slot.Human.Tier == "ace" || o.Slot.Human.Tier == "veteran")) d *= 0.5;
                if (d < bs) { bs = d; best = o; }
            }
            if (best == null) return;
            _milkPartner = best; _milkFlag = pick; _milkUntil = now + 75000;
            MilkCalls++;
            Flags.ForceChase(best.Slot, pick, now);
            Out("[CTFXBots] milk: " + Clan.Team._name + " holds every flag - truce with " + best.Slot.Name + " (" + best._team._name + ", " + (int)bs + " px) to take and drop "
                + pick.flag.GeneralData.Name + " in its base");
            if (Chat != null)
            {
                CTFXBot c = null;
                foreach (CTFXSlot s in Clan.Slots) { CTFXBot b = GetBot(s.BotId); if (b != null && !b.IsDead && !b.InDropship) { c = b; break; } }
                if (c != null) Chat.Say(c, Chat.Pick("milk it", "need a milker", "truce for the milk"), 0.6, false);
            }
        }

        int _riseSeenAt;
        bool _milkTestDone;

        void MilkTestStep(int now)
        {
            if (_milkTestDone || !ClanRisen || Clan == null || Clan.Team == null || Flags == null) { if (!ClanRisen) _riseSeenAt = 0; return; }
            // the first test (c09) handed the flags over a minute after the rise, while Titan still held top brown: Titan's own bots took
            // them. Milking is what a team that owns its base does, so the test waits until the clan has held top brown for 20 s
            if (!Flags.HoldsFocus(Clan.Team)) { _riseSeenAt = 0; return; }
            if (_riseSeenAt == 0) { _riseSeenAt = now; return; }
            if (now - _riseSeenAt < 20000) return;
            int hx, hy;
            if (!Flags.EndHomePx(FocusEnd, out hx, out hy)) return;
            _milkTestDone = true;
            int i = 0;
            foreach (Arena.FlagState fs in new List<Arena.FlagState>(Flags.All()))
            {
                int ox, oy;
                NearestOpenPx(hx + (i % 2) * 24 - 12, hy + (i / 2) * 24 - 12, out ox, out oy);
                fs.carrier = null;
                fs.posX = (short)(ox * 16 + 8); fs.posY = (short)(oy * 16 + 8);
                fs.oldPosX = fs.posX; fs.oldPosY = fs.posY;
                fs.team = Clan.Team;                                                        // the victory check runs here (CTF.cs OnFlagChange)
                fs.oldTeam = fs.team;
                fs.lastOperation = now;
                Helpers.Object_Flags(Arena.Players, fs);
                i++;
            }
            Out("[CTFXBots] milk test: every flag handed to " + Clan.Team._name + " in top brown's flag room - the victory countdown runs; the milk has 90 s");
        }

        internal string MilkText()
        {
            return ", milk: truces " + MilkCalls + ", flags milked " + MilkDone + " (now " + _milked.Count + "), aborted " + MilkAborted + ", clan held every flag " + MilkWinsSeen + " times";
        }
    }

    internal sealed partial class CTFXFlags
    {
        /// <summary>The milk partner runs for this flag (CTFXMilk.cs).</summary>
        internal void ForceChase(CTFXSlot s, Arena.FlagState fs, int now)
        {
            if (s == null) return;
            _chase[s] = fs;
            _chaseUntil[s] = now + 75000;
            CTFXBot b = _m.GetBot(s.BotId);
            if (b != null) b.ForceGoal();
        }

        internal void EndChase(CTFXSlot s)
        {
            if (s == null) return;
            _chase.Remove(s); _chaseUntil.Remove(s);
        }

        /// <summary>The carrier puts the flag down where it stands (milking): its team owns it there.</summary>
        internal void DropHere(Arena.FlagState fs, CTFXBot b, int now) { Drop(fs, b, now, false); }
    }
}
/* PARSER GUARD - keep last */
