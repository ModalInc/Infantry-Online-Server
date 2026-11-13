using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// MiniTP flag CARRYING (Travis 11-11: "the bot that killed me holding a flag didn't ACQUIRE the flag as the carrier. If it grabs a flag it should go to its home base ...
    /// the area with its turrets and flags ... get summoned home by the medic, and make sure they can't cheat a teleport disruptor (a sentry or someone carrying a TD)").
    ///  - The carrier is the bot's player-list row (Slot.Entry), the way CTF:X bots carry (CTFXFlags): clients draw the flag on the bot.
    ///  - Pickup: a bot touching (40 px) a flag its team does not own that nobody carries takes it and every free enemy flag stacked with it.
    ///  - A bot that kills a human carrier takes his flags (the engine has already put them on the ground: the last carrier is remembered every poll).
    ///  - Home run: in the enemy base / the field it gets out of any anti-warp (TD carrier, enemy sentry / turret) and asks; the home medic summons it with the medic's own
    ///    rules (CTFXSquads.MedicSummon: cooldown, energy, anti-warp at BOTH ends). At home it walks to its team's flag spot (flag1 / flag2) and drops the flags there.
    ///  - Carry time = the flag's DropDelay (as CTFXFlags.CarryMs): then it drops where it stands. A carrier that dies: the flags go to the killer (human or bot), else
    ///    they drop where it fell. Every change runs the script's flag rule (victory countdown).
    /// </summary>
    internal sealed partial class CTFXMini
    {
        internal int CarryPicks, CarryFromHumans, CarryHome, CarryTimeouts, CarryLost, CarrySummons, CarryAsks, CarryPinned;
        readonly Dictionary<Arena.FlagState, Player> _lastCarrierOf = new Dictionary<Arena.FlagState, Player>();
        readonly Dictionary<Arena.FlagState, int> _carryAt = new Dictionary<Arena.FlagState, int>();
        readonly Dictionary<Arena.FlagState, CTFXBot> _carryBody = new Dictionary<Arena.FlagState, CTFXBot>();

        /// <summary>The bot carrying fs (its row is the carrier), or null.</summary>
        CTFXBot CarrierBot(Arena.FlagState fs)
        {
            if (fs == null || fs.carrier == null) return null;
            foreach (CTFXBot b in _m.LiveBots) if (b.Slot != null && b.Slot.Entry == fs.carrier) return b;      // (SlotOfRow by row id does not match in MiniTP's low-id mode)
            return null;
        }

        internal bool Carrying(CTFXBot b)
        {
            if (b == null || b.Slot == null || b.Slot.Entry == null) return false;
            foreach (Arena.FlagState fs in _m.Arena._flags.Values) if (fs.bActive && fs.carrier == b.Slot.Entry) return true;
            return false;
        }

        static int CarryLimitMs(Arena.FlagState fs)
        {
            int d = fs.flag.FlagData.DropDelay;
            return d > 0 ? Math.Max(15000, Math.Min(180000, d * 10)) : 60000;
        }

        void Changed(Arena.FlagState fs)
        {
            Helpers.Object_Flags(_m.Arena.Players, fs);
            try { _m.FlagChanged(fs); } catch (Exception ex) { _m.WarnOnce("[MiniTP] flag rule: " + ex.Message); }
        }

        void CarryPickup(Arena.FlagState fs, CTFXBot b, int now)
        {
            fs.oldTeam = fs.team;
            fs.oldPosX = fs.posX; fs.oldPosY = fs.posY;
            fs.carrier = b.Slot.Entry;
            fs.team = fs.flag.FlagData.IsFlagOwnedWhenCarried ? b._team : null;
            fs.lastOperation = now;
            _carryAt[fs] = now; _carryBody[fs] = b;
            Changed(fs);
            CarryPicks++;
            b.Mini.GrabFlag = null;
            b.ForceGoal();
        }

        void CarryDrop(Arena.FlagState fs, CTFXBot b, int x, int y, int now, string why)
        {
            fs.posX = (short)x; fs.posY = (short)y;
            fs.oldPosX = fs.posX; fs.oldPosY = fs.posY;
            fs.carrier = null;
            fs.team = fs.flag.FlagData.IsFlagOwnedWhenDropped && b != null ? b._team : null;
            fs.oldTeam = fs.team;
            fs.lastOperation = now;
            _carryAt.Remove(fs); _carryBody.Remove(fs);
            Changed(fs);
            if (b != null && !b.IsDead) b.ForceGoal();
            if (CarryPicks + CarryFromHumans <= 60)
                _m.MiniLog("[MiniTP] FLAG " + why + ": " + (b != null && b.Slot != null ? _m.RowName(b.Slot) : "?") + " dropped " + FlagName(fs) + " at " + (x >> 4) + "," + (y >> 4));
        }

        void CarryTransfer(Arena.FlagState fs, Player to, CTFXBot toBot, int now)
        {
            fs.carrier = to;
            fs.team = fs.flag.FlagData.IsFlagOwnedWhenCarried ? to._team : null;
            fs.lastOperation = now;
            if (toBot != null) { _carryAt[fs] = now; _carryBody[fs] = toBot; toBot.ForceGoal(); } else { _carryAt.Remove(fs); _carryBody.Remove(fs); }
            Changed(fs);
        }

        static string FlagName(Arena.FlagState fs)
        {
            try { return fs.flag.GeneralData.Name.Trim((char)34, ' '); } catch (Exception) { return "a flag"; }
        }

        /// <summary>Every poll: carriers (home / time / bodies), last carriers, and pickups.</summary>
        int _rigCarryAt;
        /// <summary>RIG ONLY (the file MiniTP/RigCarryTest.txt exists only on the local test rig): every 45 s a raider is handed the nearest enemy flag in its area, so the
        /// home run (anti-warp, medic summon, the drop at the flag spot) can be watched in a bot-only game where the flag rooms are always defended.</summary>
        void RigCarry(int now)
        {
            if (now - _rigCarryAt < 0) return;
            _rigCarryAt = now + 45000;
            if (!_m.Arena._bGameRunning || !System.IO.File.Exists(System.IO.Path.Combine("scripts", "GameTypes", "CTF", "MiniTP", "RigCarryTest.txt"))) return;
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || b.InDropship || mb.Phase != MiniBot.Raid || b.Slot == null || b.Slot.Entry == null || Carrying(b)) continue;
                int area = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
                foreach (Arena.FlagState fs in _m.Arena._flags.Values)
                {
                    if (!fs.bActive || fs.carrier != null || fs.team == b._team || AreaOf(fs.posX >> 4, fs.posY >> 4) != area) continue;
                    CarryPickup(fs, b, now);
                    _m.MiniLog("[MiniTP] RIG CARRY TEST: " + _m.RowName(b.Slot) + " (" + mb.Role + ") handed " + FlagName(fs) + " at " + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4));
                    mb.CarryAskAt = now + 1500;
                    return;
                }
            }
        }

        void CarryStep(int now)
        {
            RigCarry(now);
            foreach (Arena.FlagState fs in new List<Arena.FlagState>(_m.Arena._flags.Values))
            {
                if (!fs.bActive) continue;
                CTFXBot c = CarrierBot(fs);
                if (c != null)
                {
                    CTFXBot body;
                    if (c.IsDead || c.Released || c.InDropship || c.Mini == null || (_carryBody.TryGetValue(fs, out body) && body != c))
                    { CarryDrop(fs, c, c._state.positionX, c._state.positionY, now, "LOST"); CarryLost++; _lastCarrierOf[fs] = null; continue; }
                    int since;
                    if (!_carryAt.TryGetValue(fs, out since)) { since = now; _carryAt[fs] = now; _carryBody[fs] = c; }
                    int hx, hy;
                    if (HomeFlagPx(c.Mini.Side, c._state.positionX, c._state.positionY, out hx, out hy)
                        && AreaOf(c._state.positionX >> 4, c._state.positionY >> 4) == HomeAreaIdx(c.Mini.Side)
                        && Dist2(c._state.positionX, c._state.positionY, hx, hy) <= 80.0 * 80.0)     // a bot stops ~45-64 px short of a goal
                    {
                        int ox = hx + _m.Rng.Next(-10, 11), oy = hy + _m.Rng.Next(-10, 11);
                        if (!_m.BodyFits(ox, oy)) { ox = hx; oy = hy; }
                        CarryDrop(fs, c, ox, oy, now, "HOME"); CarryHome++;
                        Talk(c, 0.8, 25, "Flag home [{c}]", "flag up home", "| Flag secured |");
                    }
                    else if (now - since >= CarryLimitMs(fs)) { CarryDrop(fs, c, c._state.positionX, c._state.positionY, now, "TIMEOUT"); CarryTimeouts++; }
                }
                _lastCarrierOf[fs] = fs.carrier;
            }
            // pickups
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || b.InDropship || b.Slot == null || b.Slot.Entry == null || _allIn == mb.Side) continue;   // an all-in side touches (AllInStep)
                if (mb.Phase != MiniBot.Raid && mb.Phase != MiniBot.Defend && mb.Phase != MiniBot.Field) continue;
                bool took = false;
                foreach (Arena.FlagState fs in _m.Arena._flags.Values)
                {
                    if (!fs.bActive || fs.carrier != null || fs.team == b._team || now - fs.lastOperation < 1500) continue;
                    if (Dist2(b._state.positionX, b._state.positionY, fs.posX, fs.posY) > 40.0 * 40.0) continue;
                    CarryPickup(fs, b, now);                     // the whole stack lying there comes with it
                    took = true;
                }
                if (took)
                {
                    _m.MiniLog("[MiniTP] FLAG PICKUP: " + _m.RowName(b.Slot) + " (" + mb.Role + ") at " + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4));
                    Talk(b, 0.8, 26, "Got their flag [{c}] - S me home", "SSSS flag", "flag! sum me home");
                    mb.CarryAskAt = now + 1500;
                }
            }
        }

        /// <summary>Census: every bot carrier - who, phase, area, px from its home flag spot, seconds carried.</summary>
        internal string CarryCensus()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int now = Environment.TickCount;
            foreach (Arena.FlagState fs in _m.Arena._flags.Values)
            {
                CTFXBot c = fs.bActive ? CarrierBot(fs) : null;
                if (c == null || c.Mini == null) continue;
                int hx, hy, since;
                HomeFlagPx(c.Mini.Side, c._state.positionX, c._state.positionY, out hx, out hy);
                _carryAt.TryGetValue(fs, out since);
                sb.Append(" [" + (c.Slot != null ? c.Slot.Name : "?") + " " + FlagName(fs) + " ph" + c.Mini.Phase + " a" + AreaOf(c._state.positionX >> 4, c._state.positionY >> 4)
                    + " d" + (int)Math.Sqrt(Dist2(c._state.positionX, c._state.positionY, hx, hy)) + " " + ((now - since) / 1000) + "s " + c.Why + "]");
            }
            return sb.ToString();
        }

        /// <summary>CTFXBots.OnPlayerKilled: bot k killed human victim - the flags he carried (the engine dropped them) go to k.</summary>
        internal void HumanKilledBy(Player victim, CTFXBot k)
        {
            if (victim == null || k == null || k.IsDead || k.Slot == null || k.Slot.Entry == null || k.Mini == null) return;
            int now = Environment.TickCount;
            int n = 0;
            foreach (Arena.FlagState fs in new List<Arena.FlagState>(_m.Arena._flags.Values))
            {
                if (!fs.bActive) continue;
                Player last;
                bool his = fs.carrier == victim || (fs.carrier == null && _lastCarrierOf.TryGetValue(fs, out last) && last == victim);
                if (!his) continue;
                CarryTransfer(fs, k.Slot.Entry, k, now);
                n++;
            }
            if (n > 0)
            {
                CarryFromHumans += n;
                _m.MiniLog("[MiniTP] FLAG TAKEN: " + _m.RowName(k.Slot) + " killed carrier " + victim._alias + " and took " + n + " flag(s)");
                Talk(k, 0.9, 26, "Took his flag [{c}] - S me home", "SSSS got flag", "flag! sum me");
                k.Mini.CarryAskAt = now + 1500;
            }
        }

        /// <summary>CTFXBots.OnBotDeath: a carrier bot died - its flags go to the human / bot that killed it, else they lie where it fell.</summary>
        internal void CarrierDied(CTFXBot b, Player killer, CTFXBot killerBot)
        {
            if (b == null || b.Slot == null || b.Slot.Entry == null) return;
            int now = Environment.TickCount;
            foreach (Arena.FlagState fs in new List<Arena.FlagState>(_m.Arena._flags.Values))
            {
                if (!fs.bActive || fs.carrier != b.Slot.Entry) continue;
                if (killer != null && !killer.IsDead && killer._team != b._team) CarryTransfer(fs, killer, null, now);
                else if (killerBot != null && !killerBot.IsDead && killerBot._team != b._team && killerBot.Slot != null && killerBot.Slot.Entry != null && killerBot.Mini != null)
                    CarryTransfer(fs, killerBot.Slot.Entry, killerBot, now);
                else CarryDrop(fs, b, b._state.positionX, b._state.positionY, now, "DIED");
                CarryLost++;
                int hx, hy; HomeFlagPx(b.Mini != null ? b.Mini.Side : 0, b._state.positionX, b._state.positionY, out hx, out hy);
                _m.MiniLog("[MiniTP] FLAG CARRIER KILLED: " + (b.Slot != null ? b.Slot.Name : "?") + " in area " + AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) + " " + (int)Math.Sqrt(Dist2(b._state.positionX, b._state.positionY, hx, hy))
                    + " px from its flag spot, by " + (killer != null ? killer._alias : killerBot != null && killerBot.Slot != null ? killerBot.Slot.Name : "turret/other") + (fs.carrier != null ? " who now carries it" : ""));
            }
        }

        /// <summary>The team's flag spot at home (A7 / D7 flag1 / flag2): the one nearer (x, y).</summary>
        bool HomeFlagPx(int side, int x, int y, out int hx, out int hy)
        {
            hx = 0; hy = 0;
            string home = HomeArea[side];
            int ax, ay, bx, by;
            bool a = Pt(home, "flag1", out ax, out ay), bb = Pt(home, "flag2", out bx, out by);
            if (!a && !bb) return false;
            if (a && (!bb || Dist2(x, y, ax * 16 + 8, ay * 16 + 8) <= Dist2(x, y, bx * 16 + 8, by * 16 + 8))) { hx = ax * 16 + 8; hy = ay * 16 + 8; }
            else { hx = bx * 16 + 8; hy = by * 16 + 8; }
            return true;
        }

        /// <summary>The goal of a carrier: at home, its flag spot; away, out of any anti-warp and back at a safe spot while it asks the home medic for the summon.</summary>
        bool CarryGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!Carrying(b)) return false;
            mb.Hunting = false; mb.Hold = false;
            int a = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
            int hx, hy;
            if (a == HomeAreaIdx(mb.Side) && HomeFlagPx(mb.Side, b._state.positionX, b._state.positionY, out hx, out hy)) { tx = hx >> 4; ty = hy >> 4; return true; }
            if (now - mb.RelocUntil < 0) { tx = mb.RelocX; ty = mb.RelocY; return true; }
            if (mb.Phase == MiniBot.Raid && Retreat(b, out tx, out ty)) { mb.Hold = true; return true; }       // the far back of the gauntlet: hold there for the summon
            tx = b._state.positionX >> 4; ty = b._state.positionY >> 4; mb.Hold = true;
            return true;
        }

        /// <summary>Every poll for each carrier away from home: out of anti-warp first (a TD carrier or an enemy sentry blocks the summon), then the home medic summons it.</summary>
        void CarryHomeStep(int now)
        {
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || b.InDropship || now - mb.CarryAskAt < 0 || !Carrying(b)) continue;
                int a = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
                if (a == HomeAreaIdx(mb.Side)) continue;
                mb.CarryAskAt = now + 1200;
                if (_m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b) != null)
                {   // pinned: walk out of the disruptor / sentry range (the same reloc the squad leader uses), ask again after
                    CarryPinned++;
                    if (now - mb.RelocUntil >= 0) { RelocateFromTd(b, now); b.ForceGoal(); Talk(b, 0.5, 0, "td'd with flag, moving", "anti on me w/ flag"); }
                    continue;
                }
                CTFXBot medic = Summoner(mb.Side, true, now);
                if (medic == null) { if (now - mb.CarrySaidAt >= 0) { mb.CarrySaidAt = now + 15000; CarryAsks++; Talk(b, 0.7, 27, "SSSS flag [{c}]", "need sum home, flag", "S me home w/ flag"); } continue; }
                if (_m.Squads.MedicSummonBot(medic, b, now))
                {
                    CarrySummons++;
                    _m.Chat.Say(medic, "Acknowledged --[" + _m.RowName(b.Slot) + "]--", 0.85, true, 22);
                    int hx2, hy2; HomeFlagPx(mb.Side, b._state.positionX, b._state.positionY, out hx2, out hy2);
                    _m.MiniLog("[MiniTP] FLAG SUMMON: medic " + _m.RowName(medic.Slot) + " pulled carrier " + _m.RowName(b.Slot) + " home - landed in area " + AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) + " " + (int)Math.Sqrt(Dist2(b._state.positionX, b._state.positionY, hx2, hy2)) + " px from the flag spot (medic area " + AreaOf(medic._state.positionX >> 4, medic._state.positionY >> 4) + ")");
                }
            }
        }
    }
}
