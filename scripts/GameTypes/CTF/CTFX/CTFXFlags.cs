// CTF:X server-side bots - FLAGS (Travis 10-13: "neutral start flags in the CENTER room of each base, like the original zone, so
// there is a mad dash to get flags, establish an anchor at a base, set up home and protect the flag, reinforce the engineer").
//
// The event flags (CTFXEvent.txt flagspawn=, the centre room of each building) are the arena own flags: the bots change their
// FlagState directly (carrier = the bot player-list row, so every client draws the flag on that bot) and send the flag update, the
// way the engine flagAction does for a player. Rules (Travis 10-13):
//   - a team may hold several flags; a team never picks up a flag it already owns (it stays set until an enemy takes it)
//   - carrying: the bot runs it to its own base flag room and drops it there; when the flag carry time (FlagData.DropDelay)
//     runs out first, it drops where the bot stands; the drop spot is the flag new last location
//   - the carrier is killed by a bot or a player: the killer takes the flag (its carry time starts again)
//   - the carrier is killed by a turret: the flag goes back to its last location
//   - a bot that kills a human carrier takes his flag (the engine bot-kill path has just put it back)
// Who goes for flags: at a game start (the opening, about 3 minutes) two dashers from every base team run for the nearest flag
// their team does not hold; later now and then one raider goes for an enemy-held flag. Any base-team bot passing a flag it may take
// picks it up. Carriers do not teleport and keep to their way (travelling), anchors guard the flags their team holds at home.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXFlags
    {
        readonly CTFXBotManager _m;
        internal bool On = true;
        readonly List<KeyValuePair<string, int[]>> _spawns = new List<KeyValuePair<string, int[]>>();   // flagspawn lines (tiles)
        readonly Dictionary<Arena.FlagState, int> _carrySince = new Dictionary<Arena.FlagState, int>();  // bot carriers
        readonly Dictionary<Arena.FlagState, Player> _lastCarrier = new Dictionary<Arena.FlagState, Player>();
        // the exact body carrying it: a seat keeps its player-list row for life, so a carrier that died (or was recycled) without the
        // death reaching BotDied would hand the flag to its next body in the dropship (10-13 soak: two flags ended up in the ship)
        readonly Dictionary<Arena.FlagState, CTFXBot> _body = new Dictionary<Arena.FlagState, CTFXBot>();
        readonly Dictionary<CTFXSlot, Arena.FlagState> _chase = new Dictionary<CTFXSlot, Arena.FlagState>();
        readonly Dictionary<CTFXSlot, int> _chaseUntil = new Dictionary<CTFXSlot, int>();
        readonly Dictionary<Player, ushort> _humanKilledBy = new Dictionary<Player, ushort>();          // victim -> bot id (network thread)
        int _lastStep, _lastAssign, _openUntil;
        // stats
        internal int Picks, Drops, Home, Timeouts, Taken, Resets, FromHumans;
        internal int Sent, ChaseDied, ChaseTimeout, ChaseGone, ChaseLogs;          // flag runners: sent, and how a run ended without a pickup

        internal CTFXFlags(CTFXBotManager m) { _m = m; }

        internal void Load()
        {
            _spawns.Clear();
            string path = Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("CTFX", "CTFXEvent.txt"))));
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw;
                    int cut = line.IndexOf("//", StringComparison.Ordinal);
                    if (cut >= 0) line = line.Substring(0, cut);
                    line = line.Trim();
                    if (line.StartsWith("flaghome=", StringComparison.OrdinalIgnoreCase))
                    {   // the flag room corner opposite its opening (Travis 10-15)
                        string[] hv = line.Substring(9).Split(',');
                        int hx0, hy0;
                        if (hv.Length == 3 && int.TryParse(hv[1].Trim(), out hx0) && int.TryParse(hv[2].Trim(), out hy0)) _flagHome[hv[0].Trim().ToLowerInvariant()] = new int[] { hx0, hy0 };
                        continue;
                    }
                    if (!line.StartsWith("flagspawn=", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] v = line.Substring(10).Split(',');
                    int x, y;
                    if (v.Length == 3 && int.TryParse(v[1].Trim(), out x) && int.TryParse(v[2].Trim(), out y))
                        _spawns.Add(new KeyValuePair<string, int[]>(v[0].Trim(), new int[] { x, y }));
                }
            }
            catch (Exception) { }
        }

        /// <summary>A game start (or the bots starting): every event flag back in its centre room, unowned; the opening dash begins.</summary>
        internal void Reset(int now)
        {
            _carrySince.Clear(); _lastCarrier.Clear(); _chase.Clear(); _chaseUntil.Clear(); _body.Clear(); _callAt.Clear(); _calledRun.Clear(); BotCalls = BotCallWarps = 0; _infil.Clear(); _infilNext.Clear(); _hopNext.Clear();
            InfilRuns = InfilAttaches = InfilSummons = InfilGaveUp = InfilMoved = ChaseHops = 0;
            lock (_humanKilledBy) _humanKilledBy.Clear();
            Picks = Drops = Home = Timeouts = Taken = Resets = FromHumans = 0; Sent = ChaseDied = ChaseTimeout = ChaseGone = ChaseLogs = 0;
            _openUntil = now + 200000;
            StartedAt = now;
            if (!On || _spawns.Count == 0) return;
            foreach (KeyValuePair<string, int[]> sp in _spawns)
            {
                Arena.FlagState fs = _m.Arena.getFlag(sp.Key);
                if (fs == null) continue;
                fs.posX = (short)(sp.Value[0] * 16 + 8); fs.posY = (short)(sp.Value[1] * 16 + 8);
                fs.oldPosX = fs.posX; fs.oldPosY = fs.posY;
                fs.bActive = true; fs.team = null; fs.oldTeam = null; fs.carrier = null;
                Helpers.Object_Flags(_m.Arena.Players, fs);
            }
            int ms = 0;
            foreach (Arena.FlagState fs in Flags()) { ms = CarryMs(fs); break; }
            _m.Log("[CTFXBots] flags: " + _spawns.Count + " event flags reset to their centre rooms, carry time " + (ms / 1000) + " s");
        }

        internal IEnumerable<Arena.FlagState> All() { return Flags(); }

        IEnumerable<Arena.FlagState> Flags()
        {
            foreach (KeyValuePair<string, int[]> sp in _spawns)
            {
                Arena.FlagState fs = _m.Arena.getFlag(sp.Key);
                if (fs != null && fs.bActive) yield return fs;
            }
        }

        static int CarryMs(Arena.FlagState fs)
        {
            int d = fs.flag.FlagData.DropDelay;
            return d > 0 ? Math.Max(15000, Math.Min(120000, d * 10)) : 45000;
        }

        CTFXBot BotOf(Player carrier)
        {
            if (carrier == null) return null;
            CTFXSlot s = _m.SlotOfRow(carrier._id);
            if (s == null || s.Entry != carrier) return null;
            return _m.GetBot(s.BotId);
        }

        internal bool Carrying(CTFXBot b)
        {
            if (b == null || b.Slot == null || b.Slot.Entry == null) return false;
            foreach (Arena.FlagState fs in Flags()) if (fs.carrier == b.Slot.Entry) return true;
            return false;
        }

        static bool BaseBot(CTFXBot b) { return b != null && b.Group != null && (b.Group.HomeEnd != null || b.Group.CoreOf != null) && b.Slot != null && b.Slot.Entry != null; }

        /// <summary>Any bot can take a flag off a carrier it killed (Travis: the killer takes the flag), base team or not.</summary>
        static bool CanCarry(CTFXBot b) { return b != null && !b.IsDead && b.Slot != null && b.Slot.Entry != null; }

        // ================================================================ the flag operations (as the engine flagAction)
        void Pickup(Arena.FlagState fs, CTFXBot b, int now)
        {
            if (fs.team != null && fs.team != b._team) CounterStorm(fs.team, fs, b, now);
            HomeRunner(b, now);
            fs.oldTeam = fs.team;
            fs.carrier = b.Slot.Entry;
            fs.team = fs.flag.FlagData.IsFlagOwnedWhenCarried ? b._team : null;
            fs.lastOperation = now;
            _carrySince[fs] = now;
            _body[fs] = b;
            Helpers.Object_Flags(_m.Arena.Players, fs);
            Picks++;
            int hx, hy;
            if (Picks <= 40 && HomePx(b, out hx, out hy))
                _m.Log("[CTFXBots] flag: " + b.Slot.Name + " (" + b.Style + "/" + (b.Slot.Intent != null ? b.Slot.Intent.Act.ToString() : "-") + ", " + b._team._name
                    + ") took " + Name(fs) + " at " + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + ", home "
                    + (int)Dist(b._state.positionX, b._state.positionY, hx, hy) + " px");
            _chase.Remove(b.Slot);
            b.ForceGoal();
            _m.Chat.Say(b, now - _openUntil < 0 ? "got " + Name(fs).ToLowerInvariant() : "flag, running it home", 0.35, false);
        }

        void Drop(Arena.FlagState fs, CTFXBot b, int now, bool home)
        {
            fs.posX = b._state.positionX; fs.posY = b._state.positionY;
            int cx, cy;
            if (home && HomePx(b, out cx, out cy)) { fs.posX = (short)cx; fs.posY = (short)cy; }   // the corner, exactly (10-15)
            fs.oldPosX = fs.posX; fs.oldPosY = fs.posY;                         // the new last location
            fs.carrier = null;
            fs.team = fs.flag.FlagData.IsFlagOwnedWhenDropped ? b._team : null;
            fs.oldTeam = fs.team;
            fs.lastOperation = now;
            _carrySince.Remove(fs);
            _body.Remove(fs);
            _courierWarped.Remove(b);
            Helpers.Object_Flags(_m.Arena.Players, fs);
            Drops++;
            if (home) Home++; else Timeouts++;
            int hx2, hy2;
            if (Drops <= 12 && HomePx(b, out hx2, out hy2))
                _m.Log("[CTFXBots] flag: " + b.Slot.Name + " dropped " + Name(fs) + (home ? " home" : " (carry time out)") + " at "
                    + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + ", " + (int)Dist(b._state.positionX, b._state.positionY, hx2, hy2)
                    + " px from home, doing " + b.Why);
            b.ForceGoal();
            if (home) _m.Chat.Say(b, "flag up " + (b.Group.HomeEnd ?? "home").Replace('-', ' '), 0.4, false);
        }

        void Transfer(Arena.FlagState fs, Player to, int now)
        {
            fs.carrier = to;
            fs.team = to._team;
            fs.lastOperation = now;
            CTFXBot tb = BotOf(to);
            if (tb != null) { _carrySince[fs] = now; _body[fs] = tb; } else { _carrySince.Remove(fs); _body.Remove(fs); }
            Helpers.Object_Flags(_m.Arena.Players, fs);
            Taken++;
        }

        void BackToLast(Arena.FlagState fs, int now)
        {   // the position never moved while it was carried: posX/Y is still where it was picked up from = its last location
            fs.posX = fs.oldPosX; fs.posY = fs.oldPosY;
            fs.carrier = null;
            fs.team = fs.oldTeam;
            fs.lastOperation = now;
            _carrySince.Remove(fs);
            _body.Remove(fs);
            Helpers.Object_Flags(_m.Arena.Players, fs);
            Resets++;
        }

        // ================================================================ deaths
        /// <summary>A bot died: the flags it carried go to its killer (a bot or a player), or back to their last location (a turret).</summary>
        internal void BotDied(CTFXBot b, Player killer, CTFXBot killerBot, bool byTurret)
        {
            if (!On || b == null || b.Slot == null || b.Slot.Entry == null) return;
            int now = Environment.TickCount;
            foreach (Arena.FlagState fs in new List<Arena.FlagState>(Flags()))
            {
                if (fs.carrier != b.Slot.Entry) continue;
                if (!byTurret && killer != null && !killer.IsDead && killer._team != b._team) Transfer(fs, killer, now);
                else if (!byTurret && CanCarry(killerBot) && killerBot._team != b._team) Transfer(fs, killerBot.Slot.Entry, now);
                else BackToLast(fs, now);
            }
        }

        /// <summary>Network thread (death report): a bot killed this human; remembered for HumanKilled.</summary>
        internal void NoteHumanKill(Player victim, ushort botId)
        {
            if (victim == null) return;
            lock (_humanKilledBy) _humanKilledBy[victim] = botId;
        }

        /// <summary>A bot killed a human (the engine has put his flags back): the bot takes the flags he was carrying.</summary>
        internal void HumanKilled(Player victim)
        {
            if (!On || victim == null) return;
            ushort id;
            lock (_humanKilledBy)
            {
                if (!_humanKilledBy.TryGetValue(victim, out id)) return;
                _humanKilledBy.Remove(victim);
            }
            CTFXSlot s = _m.SlotOfRow(id);
            CTFXBot k = s != null ? _m.GetBot(s.BotId) : null;
            if (!CanCarry(k)) return;
            int now = Environment.TickCount;
            foreach (Arena.FlagState fs in new List<Arena.FlagState>(Flags()))
            {
                Player last;
                if (fs.carrier == null && _lastCarrier.TryGetValue(fs, out last) && last == victim)
                {
                    Transfer(fs, k.Slot.Entry, now);
                    FromHumans++;
                }
            }
        }

        // ================================================================ step (manager poll)
        internal void Step(int now)
        {
            if (!On || now - _lastStep < 250) return;
            _lastStep = now;
            List<Arena.FlagState> flags = new List<Arena.FlagState>(Flags());
            if (flags.Count == 0) return;
            foreach (Arena.FlagState fs in flags)
            {
                CTFXBot c = BotOf(fs.carrier);
                CTFXBot body;
                if (c != null && (!_body.TryGetValue(fs, out body) || body != c)) { BackToLast(fs, now); _lastCarrier[fs] = null; continue; }   // a new body
                if (c != null)
                {
                    if (c.IsDead || c.Released || c.InDropship) { BackToLast(fs, now); _lastCarrier[fs] = null; continue; }
                    int since;
                    if (!_carrySince.TryGetValue(fs, out since)) { since = now; _carrySince[fs] = now; }
                    int hx, hy;
                    bool atHome = HomePx(c, out hx, out hy) && Dist(c._state.positionX, c._state.positionY, hx, hy) <= 56.0;
                    if (atHome) Drop(fs, c, now, true);
                    else if (now - since >= CarryMs(fs)) Drop(fs, c, now, false);
                    else { CourierWarp(c, now, hx, hy); HopHome(c, hx, hy, now); }
                }
                _lastCarrier[fs] = fs.carrier;
            }
            // pickups: a base-team bot within 40 px of a flag on the ground that its team does not hold
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b.InDropship || !BaseBot(b) || Carrying(b)) continue;
                foreach (Arena.FlagState fs in flags)
                {
                    if (fs.carrier != null || fs.team == b._team || _m.MilkedFor(fs, b._team)) continue;
                    if (Dist(b._state.positionX, b._state.positionY, fs.posX, fs.posY) > 40.0) continue;
                    if (now - fs.lastOperation < 1500) continue;                 // just dropped / taken
                    Pickup(fs, b, now);
                    break;
                }
            }
            if (now - _lastAssign >= 1000) { _lastAssign = now; Assign(now, flags); CourierAssign(now, flags); CallStep(now); InfilStep(now); HopStep(now); WaveStep(now); }
        }

        /// <summary>
        /// Flag business (Travis 10-14 eve: "shift most of the arena to flagging strong emphasis (like 70-75%) as a new default").
        /// Every base / hall team keeps Share of its free bots on flags: an enemy carrying a flag near (or from) home is intercepted
        /// first, then the flags on the ground it does not hold, nearest its home first, up to 6 on one flag before the next is taken.
        /// A chaser whose flag a teammate picks up escorts the carrier home; one whose flag an enemy picks up hunts the carrier.
        /// </summary>
        void Assign(int now, List<Arena.FlagState> flags)
        {
            List<CTFXSlot> drop = null;
            foreach (KeyValuePair<CTFXSlot, Arena.FlagState> kv in _chase)
            {
                CTFXBot b = _m.GetBot(kv.Key.BotId);
                int until;
                _chaseUntil.TryGetValue(kv.Key, out until);
                Arena.FlagState fs = kv.Value;
                bool mine = b != null && fs.carrier == null && fs.team == b._team;               // held by its team now (home)
                bool selfCarry = b != null && BotOf(fs.carrier) == b;
                if (b == null || b.IsDead || now - until >= 0 || mine || selfCarry)
                {
                    (drop ?? (drop = new List<CTFXSlot>())).Add(kv.Key);
                    string why = b == null || b.IsDead ? "died" : now - until >= 0 ? "timed out" : "its team holds it now";
                    if (why == "died") ChaseDied++; else if (why == "timed out") ChaseTimeout++; else ChaseGone++;
                    if (ChaseLogs < 40 && b != null && why != "its team holds it now" && !selfCarry)
                    {
                        ChaseLogs++;
                        _m.Note("flag runner " + kv.Key.Name + " (" + kv.Key.Group.Team._name + ") for " + Name(fs) + ": " + why + " at "
                            + b._state.positionX / 16 + "," + b._state.positionY / 16 + ", " + (int)Dist(b._state.positionX, b._state.positionY, FlagX(fs), FlagY(fs)) + " px from it"
                            + (fs.carrier != null ? " (carried)" : fs.team != null ? " (held by " + fs.team._name + ")" : " (neutral)"));
                    }
                }
            }
            if (drop != null) foreach (CTFXSlot s in drop) { _chase.Remove(s); _chaseUntil.Remove(s); CTFXBot b = _m.GetBot(s.BotId); if (b != null) b.ForceGoal(); }
            bool opening = now - _openUntil < 0;
            Dictionary<Team, int> chasing = new Dictionary<Team, int>();
            Dictionary<Arena.FlagState, int> onFlag = new Dictionary<Arena.FlagState, int>();
            foreach (KeyValuePair<CTFXSlot, Arena.FlagState> kv in _chase)
            {
                int n; chasing.TryGetValue(kv.Key.Group.Team, out n); chasing[kv.Key.Group.Team] = n + 1;
                int m2; onFlag.TryGetValue(kv.Value, out m2); onFlag[kv.Value] = m2 + 1;
            }
            // who is close to a win, and how hard each held flag is to take
            Dictionary<Team, int> owned = new Dictionary<Team, int>();
            foreach (Arena.FlagState fs in flags) if (fs.carrier == null && fs.team != null) { int o; owned.TryGetValue(fs.team, out o); owned[fs.team] = o + 1; }
            Team near = null;
            foreach (KeyValuePair<Team, int> kv in owned) if (flags.Count >= 3 && kv.Value >= flags.Count - 1) near = kv.Key;
            if (near != null && near != _nearWin) { _nearWin = near; NearWins++; _m.Log("[CTFXBots] flags: " + near._name + " holds " + owned[near] + " of " + flags.Count + " - every other team goes for its flags"); }
            else if (near == null) _nearWin = null;
            Dictionary<Arena.FlagState, int> guard = new Dictionary<Arena.FlagState, int>();
            foreach (Arena.FlagState fs in flags)
            {
                if (fs.carrier != null || fs.team == null) continue;
                int n = 0;
                foreach (CTFXBot o in _m.LiveBots)
                    if (!o.IsDead && !o.InDropship && o._team == fs.team && Dist(o._state.positionX, o._state.positionY, fs.posX, fs.posY) <= 600.0) n++;
                foreach (Computer c in _m.Turrets.Live)
                    if (!c.IsDead && c._team == fs.team && Dist(c._state.positionX, c._state.positionY, fs.posX, fs.posY) <= 700.0) n++;
                guard[fs] = n;
            }
            foreach (CTFXBotGroup g in _m.Groups)
            {
                if ((g.HomeEnd == null && g.CoreOf == null) || g.Team == null) continue;
                if (g.CoreOf != null && now - StartedAt < 360000) continue;                   // a hall team: the fields first
                int have;
                chasing.TryGetValue(g.Team, out have);
                int free = have;
                foreach (CTFXSlot s in g.Slots)
                {
                    if (_chase.ContainsKey(s)) continue;
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (b != null && !b.IsDead && !b.InDropship && !Carrying(b) && FreeForFlag(s, b)) free++;
                }
                int want = Math.Max(opening ? 2 : HoldsAny(g.Team, flags) ? 1 : 2, (int)Math.Round(Share * free));
                if (near != null && near != g.Team) want += 2;                                // break the win
                int hx, hy;
                if (!(g.HomeEnd != null ? _m.RouteNode(g.HomeEnd + ":patrol", 0, out hx, out hy) : _m.CoreHub(g.CoreOf, out hx, out hy))) continue;
                hx = hx * 16 + 8; hy = hy * 16 + 8;
                for (int add = 0; add < 2 && have < want; add++)
                {
                    Arena.FlagState best = null;
                    double bd = double.MaxValue;
                    foreach (Arena.FlagState fs in flags)
                    {
                        double d = Dist(hx, hy, FlagX(fs), FlagY(fs));
                        if (fs.carrier != null)
                        {
                            if (fs.carrier._team == g.Team) continue;                        // its own carrier: escorts come from its chasers
                            if (fs.oldTeam != g.Team && d > 3000.0) continue;                // an enemy carrier: ours, or near home
                            d *= 0.4;                                                         // intercepting comes first
                        }
                        else if (fs.team == g.Team || _m.MilkedFor(fs, g.Team)) continue;
                        // Travis 10-15: "dumb forever cluster fights" in the bottom hallways: a flag lying loose OUTSIDE the bases drew up
                        // to 6 runners from every team. Now a team only goes for a flag within 3200 px of its home (the opening dash
                        // excepted), and at most 2 of its runners go for a loose one out in the open; flags in a base keep the 6
                        bool loose = fs.carrier == null && _m.AreaAt(fs.posX, fs.posY).StartsWith("out", StringComparison.Ordinal);
                        bool hallFlag = fs.carrier == null && _m.AreaAt(fs.posX, fs.posY).EndsWith("-core", StringComparison.Ordinal);
                        if (!opening && fs.carrier == null && d > 3200.0 && (loose || hallFlag)) continue;
                        if (fs.carrier == null && fs.team != null && !loose && !hallFlag)
                        {
                            int gd; guard.TryGetValue(fs, out gd);
                            d += 350.0 * gd;                                                  // a fortress costs more
                            if (fs.team == near) d *= 0.35;                                   // one flag from a win: break it first
                        }
                        int on; onFlag.TryGetValue(fs, out on);
                        int mineOn = 0;
                        foreach (KeyValuePair<CTFXSlot, Arena.FlagState> kv in _chase) if (kv.Value == fs && kv.Key.Group.Team == g.Team) mineOn++;
                        if (loose && mineOn >= 2) continue;
                        if (on >= 6) d += 4000.0;                                             // 6 on one flag: the next flag
                        if (d < bd) { bd = d; best = fs; }
                    }
                    if (best == null) break;
                    CTFXSlot pick = null;
                    double pd = double.MaxValue;
                    foreach (CTFXSlot s in g.Slots)
                    {
                        if (_chase.ContainsKey(s)) continue;
                        CTFXBot b = _m.GetBot(s.BotId);
                        if (b == null || b.IsDead || b.InDropship || Carrying(b) || !FreeForFlag(s, b)) continue;
                        double d = Dist(b._state.positionX, b._state.positionY, FlagX(best), FlagY(best));
                        if (b.ClassId == CTFXBot.SK_INFIL) d *= 0.5;                 // a runner: fast and cloaked
                        if (d < pd) { pd = d; pick = s; }
                    }
                    if (pick == null) break;
                    _chase[pick] = best;
                    _chaseUntil[pick] = now + 90000;
                    Sent++;
                    have++;
                    int o2; onFlag.TryGetValue(best, out o2); onFlag[best] = o2 + 1;
                    CTFXBot pb = _m.GetBot(pick.BotId);
                    if (pb != null) pb.ForceGoal();
                }
                chasing[g.Team] = have;
            }
        }

        internal double Share = 0.72;                                                   // flagShare: the share of free bots on flags
        // 10-16 night: a bot carrier is its player-list row, a bare Player whose own state never leaves (0,0) - runners chasing or
        // escorting a bot-carried flag ran for the map corner (logged 27870-28569 px from it). The live body is the carrier's place.
        int FlagX(Arena.FlagState fs)
        {
            if (fs.carrier == null) return fs.posX;
            CTFXBot cb = BotOf(fs.carrier);
            if (cb != null) return cb._state.positionX;
            return fs.carrier._state != null ? fs.carrier._state.positionX : fs.posX;
        }
        int FlagY(Arena.FlagState fs)
        {
            if (fs.carrier == null) return fs.posY;
            CTFXBot cb = BotOf(fs.carrier);
            if (cb != null) return cb._state.positionY;
            return fs.carrier._state != null ? fs.carrier._state.positionY : fs.posY;
        }

        // ---- bots calling their own attacks (Travis 10-14 eve: "attacking enemy macros is something bots should understand how to read
        // and send themselves when they are prioritizing getting near flags"): a chaser closing on an enemy-held flag (or carrier) calls
        // "Attacking enemy" in team chat, and teammates far behind (its flag's other chasers first, then free ones) warp onto it, 2-5
        readonly Dictionary<Team, int> _callAt = new Dictionary<Team, int>();
        readonly Dictionary<CTFXSlot, int> _calledRun = new Dictionary<CTFXSlot, int>();
        internal int BotCalls, BotCallWarps;

        void CallStep(int now)
        {
            foreach (KeyValuePair<CTFXSlot, Arena.FlagState> kv in _chase)
            {
                Arena.FlagState fs = kv.Value;
                CTFXBot b = _m.GetBot(kv.Key.BotId);
                if (b == null || b.IsDead || b.InDropship) continue;
                bool enemyHeld = fs.carrier != null ? fs.carrier._team != b._team : fs.team != null && fs.team != b._team;
                if (!enemyHeld) continue;
                double d = Dist(b._state.positionX, b._state.positionY, FlagX(fs), FlagY(fs));
                if (d > 1100.0 || d < 150.0) continue;
                int at;
                if (_callAt.TryGetValue(b._team, out at) && now - at < 20000) continue;
                int ran;
                if (_calledRun.TryGetValue(kv.Key, out ran) && now - ran < 60000) continue;
                _callAt[b._team] = now; _calledRun[kv.Key] = now;
                CallFor(b, fs, now);
                return;                                                                       // one call per step (the dictionary changed)
            }
        }

        // ---- cycle 2 (10-14 night): flag runners died walking 5000-11000 px from random landings (362 of 562 runs in cycle 1). A
        // runner 2500+ px from its flag warps (Suit Teleport) onto the teammate nearest the flag when that one is 1500+ px nearer,
        // as a player warps to a teammate rather than walk the map; at most once every 12 s, not mid-fight
        readonly Dictionary<CTFXSlot, int> _hopNext = new Dictionary<CTFXSlot, int>();
        internal int ChaseHops;

        void HopStep(int now)
        {
            foreach (KeyValuePair<CTFXSlot, Arena.FlagState> kv in _chase)
            {
                CTFXBot b = _m.GetBot(kv.Key.BotId);
                if (b == null || b.IsDead || b.InDropship || b.HasTarget || Carrying(b)) continue;
                int next;
                if (_hopNext.TryGetValue(kv.Key, out next) && now - next < 0) continue;
                int fx = FlagX(kv.Value), fy = FlagY(kv.Value);
                double d0 = Dist(b._state.positionX, b._state.positionY, fx, fy);
                if (d0 < 2500) continue;
                _hopNext[kv.Key] = now + 12000;
                CTFXBot to = null;
                double best = d0 - 1500;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o == b || o.IsDead || o.InDropship || o.Released || o._team != b._team) continue;
                    double d = Dist(o._state.positionX, o._state.positionY, fx, fy);
                    if (d < best) { best = d; to = o; }
                }
                if (to != null && _m.Squads.TeleportTo(b, to, now, "flag hop")) ChaseHops++;
            }
        }

        /// <summary>Is b on its way to a flag an enemy holds or carries (assaulting another base)?</summary>
        internal bool ChasingEnemy(CTFXBot b)
        {
            Arena.FlagState fs;
            if (b == null || b.Slot == null || !_chase.TryGetValue(b.Slot, out fs)) return false;
            return fs.carrier != null ? fs.carrier._team != b._team : fs.team != null && fs.team != b._team;
        }

        /// <summary>b calls "Attacking enemy": teammates far behind warp onto it (its flag's chasers first, then free ones).</summary>
        void CallFor(CTFXBot b, Arena.FlagState fs, int now)
        {
            if (fs.carrier == null && fs.team != null && _m.Rng.NextDouble() < 0.25) Decoy(b, fs, now);
            BotCalls++;
            _m.Chat.Say(b, "Attacking enemy " + _m.Chat.PlaceOf(b), 1.0, true);
            List<KeyValuePair<double, CTFXBot>> c = new List<KeyValuePair<double, CTFXBot>>();
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == b || o.IsDead || o.Released || o.InDropship || o._team != b._team || o.Slot == null || Carrying(o) || o.HasTarget) continue;
                Arena.FlagState of;
                bool same = _chase.TryGetValue(o.Slot, out of) && of == fs;
                if (!same && (_chase.ContainsKey(o.Slot) || !FreeForFlag(o.Slot, o))) continue;
                double od = Dist(o._state.positionX, o._state.positionY, b._state.positionX, b._state.positionY);
                if (od < 700.0) continue;                                                  // already close: it walks
                CTFXHuman h = o.Slot.Human;
                if (_m.Rng.NextDouble() > 0.55 + 0.4 * (h != null ? h.Comply : 0.6)) continue;   // not everyone reads the chat
                c.Add(new KeyValuePair<double, CTFXBot>(same ? -od : od, o));
            }
            c.Sort(delegate(KeyValuePair<double, CTFXBot> x, KeyValuePair<double, CTFXBot> y) { return x.Key.CompareTo(y.Key); });
            int cap = 2 + _m.Rng.Next(4);
            List<CTFXBot> go = new List<CTFXBot>();
            for (int i = 0; i < c.Count && i < cap; i++) go.Add(c[i].Value);
            foreach (CTFXBot o in go)
            {
                if (!_chase.ContainsKey(o.Slot)) { _chase[o.Slot] = fs; _chaseUntil[o.Slot] = now + 90000; Sent++; }
                if (_m.Squads.TeleportTo(o, b, now, "attack call")) BotCallWarps++;
                o.ForceGoal();
            }
        }

        /// <summary>
        /// Travis 10-14 night: decoy runs, "highly advanced ... only on the most elite bots (like 1-3 players max out of 100)". When its team
        /// calls an attack on an enemy-held flag, one of the team's top-3 killers on the map (an ace or veteran, alive, not carrying) may
        /// run loudly at the enemy base's OTHER choke - the one farthest from where the attack comes in - so the defenders turn that way.
        /// </summary>
        void Decoy(CTFXBot caller, Arena.FlagState fs, int now)
        {
            if (now - _decoyNext < 0 || _m.Cmdr == null) return;
            List<CTFXSlot> top = new List<CTFXSlot>();
            foreach (CTFXBotGroup g in _m.Groups) foreach (CTFXSlot s in g.Slots) if (s.Kills + s.Deaths > 0) top.Add(s);
            top.Sort(delegate(CTFXSlot a, CTFXSlot c) { return (c.Kills / (double)Math.Max(1, c.Deaths)).CompareTo(a.Kills / (double)Math.Max(1, a.Deaths)); });
            CTFXBot d = null;
            for (int i = 0; i < top.Count && i < 3; i++)
            {
                CTFXBot o = _m.GetBot(top[i].BotId);
                if (o == null || o == caller || o.IsDead || o.InDropship || o._team != caller._team || Carrying(o) || o.Slot.Human == null) continue;
                if (o.Slot.Human.Tier != "ace" && o.Slot.Human.Tier != "veteran") continue;
                d = o; break;
            }
            if (d == null) return;
            CTFXChoke far = null; double fd = 0;
            foreach (CTFXTeamCmd t in _m.Cmdr.Teams)
            {
                if (t.Team != fs.team) continue;
                foreach (CTFXChoke c in t.Chokes)
                {
                    double dd = Dist(c.Tx * 16 + 8, c.Ty * 16 + 8, caller._state.positionX, caller._state.positionY);
                    if (dd > fd && Dist(c.Tx * 16 + 8, c.Ty * 16 + 8, fs.posX, fs.posY) < 1600) { fd = dd; far = c; }
                }
            }
            if (far == null) return;
            _decoyNext = now + 120000;
            Decoys++;
            Unchase(d.Slot);
            if (Dist(d._state.positionX, d._state.positionY, far.Tx * 16 + 8, far.Ty * 16 + 8) > 1500) _m.Squads.TeleportToPx(d, far.Tx * 16 + 8, far.Ty * 16 + 8, now, "decoy");
            d.GoTo(far.Tx, far.Ty);
            if (Decoys <= 6) _m.Note("decoy run: " + d.Slot.Name + " (" + d.Slot.Human.Tier + ", " + d.Slot.Kills + "/" + d.Slot.Deaths + ") at " + far.Name + " while " + caller.Slot.Name + " calls the attack on " + Name(fs));
        }
        int _decoyNext;
        internal int Decoys;

        /// <summary>Cycle 3 (10-14 night): an outnumbered runner at an enemy flag does not scatter; it stages ~600 px back from the flag
        /// and calls the team in (at most every 8 s per team), then pushes again (the order: regroup, call, stack, push).</summary>
        internal bool Regroup(CTFXBot b, int now, out int fx, out int fy)
        {
            fx = 0; fy = 0;
            Arena.FlagState fs;
            if (b == null || b.Slot == null || !_chase.TryGetValue(b.Slot, out fs)) return false;
            fx = FlagX(fs); fy = FlagY(fs);
            int at;
            if (!_callAt.TryGetValue(b._team, out at) || now - at >= 8000) { _callAt[b._team] = now; CallFor(b, fs, now); Regroups++; }
            return true;
        }
        internal int Regroups;

        /// <summary>
        /// Travis 10-14 night: defenders counter-storm right after losing the flag room, while the attackers are hurt and low. The team
        /// that held the flag: 3-6 free bots (and its flag runners) warp onto the teammate nearest the thief and hunt the carrier.
        /// </summary>
        void CounterStorm(Team t, Arena.FlagState fs, CTFXBot thief, int now)
        {
            CounterStorms++;
            int want = 3 + _m.Rng.Next(4), got = 0;
            CTFXBot near = null; double nd = 1200.0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team != t) continue;
                double d = Dist(o._state.positionX, o._state.positionY, thief._state.positionX, thief._state.positionY);
                if (d < nd) { nd = d; near = o; }
            }
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (got >= want) break;
                if (o == near || o.IsDead || o.InDropship || o.Released || o._team != t || o.Slot == null || Carrying(o) || o.HasTarget) continue;
                if (!FreeForFlag(o.Slot, o) && !_chase.ContainsKey(o.Slot)) continue;
                CTFXHuman h = o.Slot.Human;
                if (_m.Rng.NextDouble() > 0.5 + 0.45 * (h != null ? h.Comply : 0.6)) continue;
                _chase[o.Slot] = fs; _chaseUntil[o.Slot] = now + 60000;
                if (near != null && Dist(o._state.positionX, o._state.positionY, near._state.positionX, near._state.positionY) > 700) _m.Squads.TeleportTo(o, near, now, "counter-storm");
                o.ForceGoal();
                got++; CounterStormers++;
            }
            if (near != null) _m.Chat.Say(near, "flag taken, on them " + _m.Chat.PlaceOf(near), 0.6, false);
        }
        internal int CounterStorms, CounterStormers;

        /// <summary>
        /// Travis 10-15: "teams should be coordinating 1 person running to a base that is a defensible flag area, and the flag runner, so the
        /// flag runner can attach to the home bee-liner (engineer ideally since they need to set up starting turrets immediately)". When a
        /// bot picks a flag up and no teammate stands within 900 px of its home, the team's nearest engineer (else its nearest free bot)
        /// warps or runs straight home; the carrier's HopHome then lands on it.
        /// </summary>
        void HomeRunner(CTFXBot carrier, int now)
        {
            int hx, hy;
            if (!HomePx(carrier, out hx, out hy)) return;
            foreach (CTFXBot o in _m.LiveBots)
                if (o != carrier && !o.IsDead && !o.InDropship && o._team == carrier._team && Dist(o._state.positionX, o._state.positionY, hx, hy) <= 900) return;
            CTFXBot best = null; double bs = double.MaxValue;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == carrier || o.IsDead || o.InDropship || o.Released || o._team != carrier._team || o.Slot == null || Carrying(o)) continue;
                if (!FreeForFlag(o.Slot, o) && o.ClassId != CTFXBot.SK_ENGY) continue;
                double d = Dist(o._state.positionX, o._state.positionY, hx, hy) * (o.ClassId == CTFXBot.SK_ENGY ? 0.4 : 1.0);
                if (d < bs) { bs = d; best = o; }
            }
            if (best == null) return;
            Unchase(best.Slot);
            if (Dist(best._state.positionX, best._state.positionY, hx, hy) > 1500)
            {   // onto a teammate near home when there is one, else the home runner just goes (the carrier keeps hopping toward home)
                foreach (CTFXBot o in _m.LiveBots)
                    if (o != best && !o.IsDead && !o.InDropship && o._team == best._team && Dist(o._state.positionX, o._state.positionY, hx, hy) < 1200)
                    { _m.Squads.TeleportTo(best, o, now, "home runner"); break; }
            }
            best.GoTo(hx >> 4, hy >> 4);
            HomeRunners++;
            _m.Chat.Say(best, "omw home, attach to me " + carrier.Slot.Name, 0.5, false);
        }
        internal int HomeRunners;

        /// <summary>A human's attack call near a flag: an answering bot joins the chase for that flag.</summary>
        internal void JoinNearestFlag(CTFXBot b, Player near)
        {
            if (b == null || b.Slot == null || _chase.ContainsKey(b.Slot)) return;
            foreach (Arena.FlagState fs in Flags())
            {
                if (fs.carrier != null && fs.carrier._team == b._team) continue;
                if (fs.carrier == null && fs.team == b._team) continue;
                if (Dist(near._state.positionX, near._state.positionY, FlagX(fs), FlagY(fs)) > 1200.0) continue;
                _chase[b.Slot] = fs; _chaseUntil[b.Slot] = Environment.TickCount + 90000; Sent++;
                return;
            }
        }

        /// <summary>Is a human within 1200 px of a flag his team does not hold?</summary>
        internal bool NearFlag(Player p)
        {
            foreach (Arena.FlagState fs in Flags())
            {
                if (fs.carrier == null && fs.team == p._team) continue;
                if (Dist(p._state.positionX, p._state.positionY, FlagX(fs), FlagY(fs)) <= 1200.0) return true;
            }
            return false;
        }

        /// <summary>A carrier far from home warps onto a teammate standing in its home end (an anchor, a defender, a human), the way a
        /// flagger attaches to the man at home; Suit Teleport may be used with a flag. Its own chance and the warp rules (cooldown,
        /// energy, anti-warp) decide.</summary>
        void HopHome(CTFXBot c, int hx, int hy, int now)
        {
            if (now - c.WarpReadyAt < 0 || Dist(c._state.positionX, c._state.positionY, hx, hy) < 1200.0) return;
            if (_m.Rng.NextDouble() > 0.5) return;                              // 10-15: twice as keen (carry time outs left flags loose)
            // Travis 10-14 night: "those with flags are meant to warp to someone who is safest with better coordination (one person always
            // going home and anchoring or summoning)": the teammate within 900 px of home with the fewest enemies near, the Captain
            // anchor first
            CTFXBot best = null;
            double bd = double.MaxValue;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == c || o.IsDead || o.Released || o.InDropship || o._team != c._team) continue;
                double d = Dist(o._state.positionX, o._state.positionY, hx, hy);
                if (d > 900.0) continue;
                double sc = d + EnemiesNear(c._team, o._state.positionX, o._state.positionY, 450.0) * 700.0 - (o.ClassId == CTFXBot.SK_CAPT ? 400.0 : 0.0);
                if (sc < bd) { bd = sc; best = o; }
            }
            if (best == null)
            {   // nobody safe at home yet: a hop part of the way, onto the teammate 1500+ px nearer home (10-15)
                double cd = Dist(c._state.positionX, c._state.positionY, hx, hy), bd2 = cd - 1500.0;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o == c || o.IsDead || o.Released || o.InDropship || o._team != c._team) continue;
                    double d2 = Dist(o._state.positionX, o._state.positionY, hx, hy);
                    if (d2 < bd2) { bd2 = d2; best = o; }
                }
            }
            if (best != null) { if (_m.Squads.TeleportTo(c, best, now, "flag home")) HopsHome++; return; }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team != c._team) continue;
                if (Dist(p._state.positionX, p._state.positionY, hx, hy) <= 900.0 && _m.Squads.TeleportToPx(c, p._state.positionX, p._state.positionY, now, "flag home"))
                { HopsHome++; return; }
            }
        }
        internal int HopsHome;

        /// <summary>On its way to a flag it was sent for (it still lies there).</summary>
        internal bool Chasing(CTFXBot b)
        {
            Arena.FlagState fs;
            return b != null && b.Slot != null && _chase.TryGetValue(b.Slot, out fs) && fs.carrier == null;
        }

        static bool HoldsAny(Team t, List<Arena.FlagState> flags)
        {
            foreach (Arena.FlagState fs in flags) if (fs.team == t) return true;
            return false;
        }

        static bool FreeForFlag(CTFXSlot s, CTFXBot b)
        {
            if (s.Style == CTFXStyle.Post || s.Style == CTFXStyle.Engineer || s.Style == CTFXStyle.Bountier || s.Style == CTFXStyle.Maser) return false;
            if (s.Style == CTFXStyle.Stormer) return false;
            CTFXStorm st = s.Group != null ? s.Group.Storm : null;                 // a storm member is free between storms (Travis 10-14)
            if (s.Style == CTFXStyle.Assault && st != null && st.Joined.Contains(b._id) && st.Phase != CTFXStorm.Wait) return false;
            if (s.Intent != null && (s.Intent.Act == CTFXAct.Anchor || s.Intent.Act == CTFXAct.Defend || s.Intent.Act == CTFXAct.Build)) return false;
            return true;
        }

        // ================================================================ goals (CTFXBot.ChooseGoal asks first)
        /// <summary>A carrier heads for its home flag room; a chaser for its flag. False = no flag business.</summary>
        internal bool Goal(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!On || b.Slot == null) return false;
            if (Carrying(b))
            {
                int hx, hy;
                if (!HomePx(b, out hx, out hy)) return false;
                _m.NearestOpenPx(hx, hy, out tx, out ty);
                return true;
            }
            if (b.Slot.Courier && CourierSweep(b, out tx, out ty)) return true;       // an enemy infiltrator near its base comes first
            Arena.FlagState fs;
            if (_chase.TryGetValue(b.Slot, out fs))
            {
                if (InfilGoal(b, fs, out tx, out ty)) return true;                  // an infiltrator sneaks to a safe attach spot first (CTFXInfil.cs)
                if (WaveGoal(b, fs, out tx, out ty)) return true;                   // a flag in an enemy base: gather, then go in together (CTFXWave.cs)
                if (fs.carrier == null && fs.team != null && fs.team != b._team)
                {   // a flag in a base: the attack takes it door by door (CTFXBase.cs)
                    CTFXBaseDef bd = _m.BaseAt(fs.posX, fs.posY);
                    if (bd != null && _m.AttackGoal(b, bd, fs.posX, fs.posY, out tx, out ty)) return true;
                }
                if (fs.carrier == null) { _m.NearestOpenPx(fs.posX, fs.posY, out tx, out ty); return true; }
                // a teammate carries it: escort (60-200 px around him); an enemy carries it: run him down
                double a = _m.Rng.NextDouble() * Math.PI * 2, r = fs.carrier._team == b._team ? 60 + _m.Rng.Next(141) : 0;
                _m.NearestOpenPx(FlagX(fs) + (int)(Math.Cos(a) * r), FlagY(fs) + (int)(Math.Sin(a) * r), out tx, out ty);
                return true;
            }
            return false;
        }

        readonly Dictionary<string, int[]> _flagHome = new Dictionary<string, int[]>();   // end -> flag room corner tile (flaghome=)

        /// <summary>Where a flag goes in end e: its flag room corner opposite the opening, else the end's patrol node 0.</summary>
        internal bool EndHomePx(string e, out int x, out int y)
        {
            x = 0; y = 0;
            int[] c;
            if (e != null && _flagHome.TryGetValue(e, out c)) { x = c[0] * 16 + 8; y = c[1] * 16 + 8; return true; }
            int tx, ty;
            if (e != null && _m.RouteNode(e + ":patrol", 0, out tx, out ty)) { x = tx * 16 + 8; y = ty * 16 + 8; return true; }
            return false;
        }

        bool HomePx(CTFXBot b, out int x, out int y)
        {
            x = 0; y = 0;
            int tx, ty;
            if (b.Group == null) return false;
            if (_m.FocusEnd != null && b.Group.HomeEnd != null && HoldsFocus(b._team) && EndHomePx(_m.FocusEnd, out x, out y)) return true;
            if (b.Group.HomeEnd != null && EndHomePx(b.Group.HomeEnd, out x, out y)) return true;
            // the focus base (top brown) when its team holds it: 3+ of its bots inside and not outnumbered there; else home
            if (_m.FocusEnd != null && b.Group.HomeEnd != null && HoldsFocus(b._team) && _m.RouteNode(_m.FocusEnd + ":patrol", 0, out tx, out ty))
            { x = tx * 16 + 8; y = ty * 16 + 8; return true; }
            if (b.Group.HomeEnd != null && _m.RouteNode(b.Group.HomeEnd + ":patrol", 0, out tx, out ty)) { x = tx * 16 + 8; y = ty * 16 + 8; return true; }
            if (b.Group.CoreOf != null)
            {   // Travis 10-15 night: the halls are transitional, the ends are the flagging bases - the end with fewer enemies inside
                string top = _m.TopEnd(b.Group.CoreOf), low = _m.OtherEnd(top);
                string e = low == null ? top : top == null ? low : EnemiesInside(top, b._team) < EnemiesInside(low, b._team) ? top : low;
                if (e != null && EndHomePx(e, out x, out y)) { HallCarries++; return true; }
            }
            if (b.Group.CoreOf != null && _m.CoreHub(b.Group.CoreOf, out tx, out ty)) { x = tx * 16 + 8; y = ty * 16 + 8; return true; }   // a hall group holds the middle
            return false;
        }

        internal int HallCarries, NearWins;
        internal int StartedAt;                                   // the game's start (Reset): the opening belongs to the base teams
        Team _nearWin;

        int EnemiesInside(string end, Team mine)
        {
            int n = 0;
            foreach (CTFXBot o in _m.LiveBots) if (!o.IsDead && !o.InDropship && o._team != mine && _m.InsideEnd(end, o)) n++;
            return n;
        }

        int _focusAt;
        Team _focusHolder;

        /// <summary>Does team t hold the focus base (the most of its bots inside, at least 3)? Re-counted once a second.</summary>
        internal bool HoldsFocus(Team t)
        {
            int now = Environment.TickCount;
            if (now - _focusAt >= 1000)
            {
                _focusAt = now;
                Dictionary<Team, int> n = new Dictionary<Team, int>();
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o.IsDead || o.InDropship || o._team == null || !_m.InsideEnd(_m.FocusEnd, o)) continue;
                    int c; n.TryGetValue(o._team, out c); n[o._team] = c + 1;
                }
                _focusHolder = null;
                int best = 2, second = 0;
                foreach (KeyValuePair<Team, int> kv in n)
                {
                    if (kv.Value > best) { second = best; best = kv.Value; _focusHolder = kv.Key; }
                    else if (kv.Value > second) second = kv.Value;
                }
                if (_focusHolder != null && second >= best) _focusHolder = null;
            }
            return t != null && t == _focusHolder;
        }

        /// <summary>A flag team holds on the ground within r px of (x, y) (anchors guard it).</summary>
        internal bool HeldNear(Team team, int x, int y, double r, out int fx, out int fy)
        {
            fx = 0; fy = 0;
            if (!On) return false;
            foreach (Arena.FlagState fs in Flags())
                if (fs.carrier == null && fs.team == team && Dist(x, y, fs.posX, fs.posY) <= r) { fx = fs.posX; fy = fs.posY; return true; }
            return false;
        }

        static string Name(Arena.FlagState fs) { return (fs.flag.GeneralData.Name ?? "flag").Trim().Trim((char)34); }      // LIO names come quoted

        static double Dist(int x0, int y0, int x1, int y1) { double dx = x1 - x0, dy = y1 - y0; return Math.Sqrt(dx * dx + dy * dy); }

        internal string Summary()
        {
            if (!On || _spawns.Count == 0) return "";
            StringBuilder sb = new StringBuilder(" | flags:");
            foreach (Arena.FlagState fs in Flags())
            {
                CTFXBot c = BotOf(fs.carrier);
                sb.Append(' ').Append(Name(fs)).Append(' ');
                if (fs.carrier != null) sb.Append("carried by ").Append(c != null ? c.Slot.Name : fs.carrier._alias).Append(" (").Append(fs.carrier._team != null ? fs.carrier._team._name : "-").Append(')');
                else sb.Append(fs.team != null ? fs.team._name : "neutral").Append(" at ").Append(fs.posX / 16).Append(',').Append(fs.posY / 16);
                sb.Append(';');
            }
            sb.Append(" picks ").Append(Picks).Append(", home ").Append(Home).Append(", carry time out ").Append(Timeouts).Append(", taken by killers ")
              .Append(Taken).Append(" (from humans ").Append(FromHumans).Append("), back to last ").Append(Resets).Append(", chasers ").Append(_chase.Count).Append(" (sent ").Append(Sent).Append(", died ").Append(ChaseDied).Append(", timed out ").Append(ChaseTimeout).Append(", flag moved on ").Append(ChaseGone).Append("), bot attack calls ").Append(BotCalls).Append(" warps ").Append(BotCallWarps).Append(", runner hops ").Append(ChaseHops).Append(", regroups ").Append(Regroups).Append(", decoy runs ").Append(Decoys).Append(", home runners ").Append(HomeRunners).Append(", counter-storms ").Append(CounterStorms).Append(" (").Append(CounterStormers).Append(" in)").Append(InfilText()).Append(", warped home ").Append(HopsHome);
            return sb.ToString();
        }
    }
}
/* PARSER GUARD - keep last */
