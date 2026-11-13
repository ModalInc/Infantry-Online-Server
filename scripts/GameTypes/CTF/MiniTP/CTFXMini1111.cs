using System;
using System.Collections.Generic;
using System.Reflection;

using InfServer.Game;
using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// MiniTP bots, Travis's Pub list of 11-11:
    ///  1. PORTAL CONTACT 1:1 - "if they ever come in contact with a portal it needs to follow 1:1 what players do": a bot whose body touches a LIO portal of the MiniTP areas
    ///     goes through it the way ScriptArena.handlePlayerPortal + Logic_Lio.Warp send a player (team frequency, the destination warp group's fields by player count /
    ///     SpecificTeam, a random open spot in the field). The wave now stages at the chamber's switch instead of on the portal (CTFXMini Field case / StageTile).
    ///  2. QUIET BASE ROTATION - "defenders on one side, if they haven't encountered an enemy for 30+ seconds, then 75% of them at least go to enemy (get summoned or warp to
    ///     someone)": the anchor, the engineer and the medic keep the base; 75% of the other seat defenders Suit-Teleport to a teammate in the enemy base (energy, cooldown and
    ///     anti-warp as a player pays them), else go out through the ship like any attacker. When the base is hit again they are called back (those not already inside the
    ///     enemy base) and a defender asks for help; every new body follows its seat again.
    ///  3. FLAG GRAB - "bots don't prioritise capturing the flag when it's an available flag to pick up": a flag that is not its team's and nobody carries, lying in the bot's
    ///     own area, becomes its goal (raiders 900 px, defenders 1400 px, the anchor 400 px; at most two bots per flag) and is taken on touch (TouchFlag: what a pickup does to
    ///     the flag's state, without the carrying - MiniTP bots do not carry flags yet).
    /// </summary>
    internal sealed partial class CTFXMini
    {
        internal int PortalWalkIns, PortalTouches, PortalNoExit, QuietRotations, QuietSent, QuietRecalled, GrabGoals, GrabLooks, RebuildNoEnergy;

        // ---------------------------------------------------------------- 0. the engineer pays the turret maker's energy (11-11: "are engineer bots consuming the energy ... all bots please")
        readonly Dictionary<string, int> _makerOf = new Dictionary<string, int>();

        /// <summary>The VehicleMaker item a player uses to build this kind of turret (vehicle id of the MiniTP build, else the player-built twin: MG 400, Rocket 401, Plasma 700); 0 = none.</summary>
        int MakerItemOf(string kind)
        {
            int id;
            if (_makerOf.TryGetValue(kind, out id)) return id;
            id = 0;
            int[] vehs = kind == "MG" ? new int[] { 429, 400 } : kind == "Rocket" ? new int[] { 428, 401 } : kind == "Plasma" ? new int[] { 427, 700 } : kind == "Sentry" ? new int[] { 402 } : new int[0];
            try
            {
                foreach (int v in vehs)
                {
                    foreach (ItemInfo it in AssetManager.Manager.getItems)
                    {
                        ItemInfo.VehicleMaker vm = it as ItemInfo.VehicleMaker;
                        if (vm != null && vm.vehicleID == v) { id = vm.id; break; }
                    }
                    if (id > 0) break;
                }
            }
            catch (Exception) { id = 0; }
            _makerOf[kind] = id;
            _m.MiniLog("[MiniTP] turret maker for " + kind + ": item " + id + (id == 0 ? " (none found: the rebuild costs Oxide only)" : ""));
            return id;
        }

        // ---------------------------------------------------------------- 1. staging + portal contact
        /// <summary>Where a field walker waits for its wave: the entry chamber's switch (A7: swA7, D7: swD7), a few tiles off the two portals; the portal itself if the point is missing.</summary>
        void StageTile(MiniBot mb, out int tx, out int ty)
        {
            if (!Pt("FIELD", mb.Side == 0 ? "swA7" : "swD7", out tx, out ty)) { tx = mb.PortalX; ty = mb.PortalY; }
        }

        sealed class LioPortal { public int Id, X1, Y1, X2, Y2, Freq, Group; public string Name; }
        List<LioPortal> _lioPortals;
        int _touchLogged;

        void LoadPortals()
        {
            _lioPortals = new List<LioPortal>();
            int lox = 0, loy = 0;
            try { lox = _m.Arena._server._assets.Level.OffsetX * 16; loy = _m.Arena._server._assets.Level.OffsetY * 16; } catch (Exception) { }
            System.Collections.IDictionary lios = null;
            try
            {
                FieldInfo f = _m.Arena._server._assets.GetType().GetField("_idToLio", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) lios = f.GetValue(_m.Arena._server._assets) as System.Collections.IDictionary;
            }
            catch (Exception) { }
            if (lios == null) { _m.MiniLog("[MiniTP] portal touch: the LIO table is not readable, bots take no portals by touch"); return; }
            foreach (object o in lios.Values)
            {
                LioInfo.Portal p = o as LioInfo.Portal;
                if (p == null) continue;
                int cx = p.GeneralData.OffsetX - lox, cy = p.GeneralData.OffsetY - loy;          // a LIO object's offset is its centre (as Logic_Lio.Warp reads a warp field)
                int a = AreaOf(cx >> 4, cy >> 4);
                if (a < 0 || a > 2) continue;                                                    // the two bases and the field (the ship rooms keep their scripted hops)
                if (p.PortalData.DestinationWarpGroup == 200) continue;                          // the green-portal group: CTF.cs's own rule (TryGreen for bots)
                if (p.PortalData.SkillLogic != null && p.PortalData.SkillLogic.Trim().Length > 0) continue;   // a skill-gated portal: a bot has no skills to check
                int w2 = Math.Max(8, (int)p.GeneralData.Width / 2) + 4, h2 = Math.Max(8, (int)p.GeneralData.Height / 2) + 4;   // + the body's edge
                LioPortal lp = new LioPortal();
                lp.Id = p.GeneralData.Id; lp.Name = p.GeneralData.Name != null ? p.GeneralData.Name.Trim() : "";
                lp.X1 = cx - w2; lp.X2 = cx + w2; lp.Y1 = cy - h2; lp.Y2 = cy + h2;
                lp.Freq = p.PortalData.Frequency; lp.Group = p.PortalData.DestinationWarpGroup;
                _lioPortals.Add(lp);
            }
            _m.MiniLog("[MiniTP] portal touch: " + _lioPortals.Count + " LIO portals in the MiniTP areas");
        }

        void PortalTouchStep(int now)
        {
            if (_lioPortals == null) LoadPortals();
            if (_lioPortals.Count == 0) return;
            List<CTFXBot> go = null; List<LioPortal> via = null;
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || b.InDropship || mb.Phase == MiniBot.Ship || now - mb.PortalCool < 0 || mb.Dig > 0) continue;      // (a mining engineer steers clear of portals)
                int x = b._state.positionX, y = b._state.positionY;
                foreach (LioPortal lp in _lioPortals)
                {
                    if (x < lp.X1 || x > lp.X2 || y < lp.Y1 || y > lp.Y2) continue;
                    if (lp.Freq != -1 && (b._team == null || lp.Freq != b._team._id)) continue;     // another team's portal does nothing to it, as to a player
                    if (go == null) { go = new List<CTFXBot>(); via = new List<LioPortal>(); }
                    go.Add(b); via.Add(lp);
                    break;
                }
            }
            if (go == null) return;                                                               // (warping changes LiveBots' bodies, not the collection, but keep the loop clean)
            for (int i = 0; i < go.Count; i++) TakePortal(go[i], via[i], now);
        }

        void TakePortal(CTFXBot b, LioPortal lp, int now)
        {
            MiniBot mb = b.Mini;
            mb.PortalCool = now + 1500;
            int x, y;
            if (!WarpGroupSpot(b, lp.Group, out x, out y)) { PortalNoExit++; return; }
            int wasPhase = mb.Phase;
            b.Teleported(x, y, now);                                       // -> Warped: the phase follows the area it landed in
            b.GraceUntil = now + GraceMs;
            Warps++; PortalTouches++;
            if (mb.Phase == MiniBot.Raid && wasPhase != MiniBot.Raid)
            {
                mb.Hops++;
                Talk(b, 0.6, 24, "| Attacking Enemy [{c}] |", "At Enemy", "@Nme");
            }
            if (_touchLogged < 30)
            {
                _touchLogged++;
                _m.MiniLog("[MiniTP] portal touch #" + _touchLogged + ": " + (b.Slot != null ? b.Slot.Name : "?") + "/" + mb.Role + " took LIO " + lp.Id + " '" + lp.Name + "' (group " + lp.Group + ") -> " + (x >> 4) + "," + (y >> 4) + " area " + AreaOf(x >> 4, y >> 4));
            }
        }

        /// <summary>Logic_Lio.Warp for a bot: the group's fields that fit the arena's player count and the bot's team (SpecificTeam), a random one, a random open spot inside it;
        /// a relative-id field (hunts an object) is skipped. False = nothing fits (the player would get "Unable to satisfy warpgroup" and stay).</summary>
        bool WarpGroupSpot(CTFXBot b, int group, out int x, out int y)
        {
            x = 0; y = 0;
            List<LioInfo.WarpField> wg = _m.Arena._server._assets.Lios.getWarpGroupByID(group);
            if (wg == null || wg.Count == 0) return false;
            int pc = Math.Max(1, _m.Arena.PlayerCount);
            List<LioInfo.WarpField> ok = new List<LioInfo.WarpField>();
            LioInfo.WarpField fallback = null;
            foreach (LioInfo.WarpField w in wg)
            {
                if (w.GeneralData.RelativeId != 0) continue;
                if (w.WarpFieldData.MinPlayerCount > pc || w.WarpFieldData.MaxPlayerCount < pc) continue;
                if (w.WarpFieldData.WarpMode == LioInfo.WarpField.WarpMode.SpecificTeam && (b._team == null || b._team._id != w.WarpFieldData.WarpModeParameter)) continue;
                if (w.WarpFieldData.WarpMode == LioInfo.WarpField.WarpMode.Unassigned) { if (fallback == null) fallback = w; continue; }
                ok.Add(w);
            }
            if (ok.Count == 0 && fallback != null) ok.Add(fallback);
            if (ok.Count == 0) return false;
            LioInfo.WarpField f = ok[_m.Rng.Next(ok.Count)];
            int lox = _m.Arena._server._assets.Level.OffsetX * 16, loy = _m.Arena._server._assets.Level.OffsetY * 16;
            int cx = f.GeneralData.OffsetX - lox, cy = f.GeneralData.OffsetY - loy, w2 = Math.Max(1, f.GeneralData.Width / 2), h2 = Math.Max(1, f.GeneralData.Height / 2);
            x = cx; y = cy;
            for (int i = 0; i < 16; i++)
            {
                int tx = cx + _m.Rng.Next(-w2, w2 + 1), ty = cy + _m.Rng.Next(-h2, h2 + 1);
                if (_m.BodyFits(tx, ty)) { x = tx; y = ty; break; }
            }
            return true;
        }

        // ---------------------------------------------------------------- 2. quiet base rotation
        const int QuietMs = 30000;
        readonly int[] _quietSince = new int[2];
        readonly bool[] _hitNow = new bool[2];
        int _quietAt;

        void QuietStep(int now)
        {
            if (now - _quietAt < 1000) return;
            _quietAt = now;
            for (int side = 0; side < 2; side++)
            {
                int home = HomeAreaIdx(side);
                bool foe = false;
                foreach (Player p in _m.Arena.Players)
                {
                    if (p == null || p.IsDead || p._team == null || p._team.IsSpec || p.IsSpectator || SideOfTeam(p._team) == side) continue;
                    if (AreaOf(p._state.positionX >> 4, p._state.positionY >> 4) == home) { foe = true; break; }
                }
                if (!foe)
                    foreach (CTFXBot b in _m.LiveBots)
                    {
                        MiniBot mb = b.Mini;
                        if (mb == null || b.IsDead || b.Released) continue;
                        if ((mb.Side != side && AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) == home) || (mb.Side == side && mb.Phase == MiniBot.Defend && b.HasTarget)) { foe = true; break; }
                    }
                if (foe)
                {
                    _quietSince[side] = 0;
                    if (!_hitNow[side]) { _hitNow[side] = true; CallBack(side, now); }
                    continue;
                }
                _hitNow[side] = false;
                if (_quietSince[side] == 0) { _quietSince[side] = now | 1; continue; }
                if (now - _quietSince[side] < QuietMs || _allIn >= 0) continue;
                _quietSince[side] = now | 1;                                                      // the next look is another 30 quiet seconds away
                Rotate(side, now);
            }
        }

        void Rotate(int side, int now)
        {
            List<CTFXBot> el = new List<CTFXBot>();
            int free = 0, out_ = 0;                                                                  // the side's free seat defenders (not anchor / engineer / medic), and how many are already out
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || mb.Side != side) continue;
                if (mb.SeatAttack || IsAnchor(mb) || mb.Role == "ENG" || mb.Role == "MED") continue;
                free++;
                if (mb.Rotated) { out_++; continue; }
                if (b.InDropship || mb.Phase != MiniBot.Defend || mb.Attack || mb.Spot >= 0) continue;
                el.Add(b);
            }
            int n = Math.Min(el.Count, (free * 3 + 3) / 4 - out_);                                  // 75% of them out in all (one quarter stays): not 75% of whoever is left every 30 s
            if (n <= 0) return;
            // a teammate (human or bot) inside the enemy base to warp to, as a player would Suit-Teleport onto a friend
            int foeArea = HomeAreaIdx(side) == 0 ? 1 : 0;
            List<int[]> mates = new List<int[]>();
            foreach (Player p in _m.Arena.Players)
                if (p != null && !p.IsDead && p._team != null && !p._team.IsSpec && !p.IsSpectator && SideOfTeam(p._team) == side && AreaOf(p._state.positionX >> 4, p._state.positionY >> 4) == foeArea)
                    mates.Add(new int[] { p._state.positionX, p._state.positionY });
            foreach (CTFXBot o in _m.LiveBots)
                if (o.Mini != null && !o.IsDead && !o.Released && o.Mini.Side == side && o.Mini.Phase == MiniBot.Raid)
                    mates.Add(new int[] { o._state.positionX, o._state.positionY });
            CTFXBot talker = null;
            int warped = 0;
            for (int i = 0; i < n; i++)
            {
                int j = i + _m.Rng.Next(el.Count - i);
                CTFXBot b = el[j]; el[j] = el[i]; el[i] = b;
                MiniBot mb = b.Mini;
                mb.Attack = true; mb.Rotated = true; mb.Hold = false; mb.Hunting = false; mb.FootStage = -1;
                b.ForceGoal();
                if (talker == null) talker = b;
                bool done = false;
                if (mates.Count > 0)
                {
                    int[] m = mates[_m.Rng.Next(mates.Count)];
                    done = _m.Squads.TeleportToPx(b, m[0], m[1], now, "attach");                    // energy, cooldown, anti-warp: what the player pays
                    if (done) warped++;
                }
                if (!done)
                {   // out through the ship (the home portal), then the field walk / a green portal like every attacker
                    int sx = b.MarkX >> 4, sy = b.MarkY >> 4;
                    if (sx > 0 && sy > 0) Land(b, sx, sy, 2, now);
                }
            }
            QuietRotations++; QuietSent += n;
            _m.MiniLog("[MiniTP] QUIET BASE: " + (side == 0 ? "Titan" : "Collective") + " saw no enemy at home for " + (QuietMs / 1000) + " s - " + n + " more of " + free + " free defenders go attack, " + (out_ + n) + " out now (" + warped + " warped to a mate in the enemy base, the rest via the ship)");
            if (talker != null) Talk(talker, 0.8, 24, "base quiet, pushing nme", "| Attacking Enemy [{c}] |", "D clear - going O");
        }

        /// <summary>The base is hit: the rotated defenders that are not already inside the enemy base come back; one defender asks for help.</summary>
        void CallBack(int side, int now)
        {
            int back = 0;
            CTFXBot caller = null;
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || mb.Side != side) continue;
                if (mb.Phase == MiniBot.Defend && (caller == null || IsAnchor(mb))) caller = b;
                if (!mb.Rotated) continue;
                mb.Rotated = false; mb.Attack = mb.SeatAttack;
                if (mb.Phase == MiniBot.Field || mb.Phase == MiniBot.Ship)
                {
                    mb.WaveFree = false; mb.WaveAt = 0;
                    mb.Phase = MiniBot.Ship; mb.PhaseAt = now; mb.WaitUntil = now + 300 + _m.Rng.Next(900);   // the ship's yellow home portal
                    if (!b.InDropship) { int sx = b.MarkX >> 4, sy = b.MarkY >> 4; if (sx > 0 && sy > 0) Land(b, sx, sy, 2, now); }
                    back++;
                }
                b.ForceGoal();
            }
            if (back > 0) { QuietRecalled += back; _m.MiniLog("[MiniTP] base hit: " + (side == 0 ? "Titan" : "Collective") + " calls back " + back + " rotated defenders"); }
            if (caller != null && back > 0) Talk(caller, 0.9, 28, "Need help home [{c}]", "%28 Under attack", "nme in base");
        }

        // ---------------------------------------------------------------- 3. flag grab
        bool GrabGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (mb.Spot >= 0 || b.InDropship) return false;
            if (mb.Phase == MiniBot.Raid && (mb.Role == "ENG" || mb.Role == "MED")) return false;
            if (mb.Phase == MiniBot.Defend && mb.Role == "ENG") return false;
            GrabLooks++;
            double reach = IsAnchor(mb) ? 400.0 : mb.Phase == MiniBot.Defend ? 1400.0 : 1700.0;          // raiders: the whole enemy base (its landings are 820-900 px from the flag room)
            int px = b._state.positionX, py = b._state.positionY, area = AreaOf(px >> 4, py >> 4);
            Arena.FlagState best = null; double bd = reach * reach;
            foreach (Arena.FlagState fs in _m.Arena._flags.Values)
            {
                if (!fs.bActive || fs.carrier != null || fs.team == b._team) continue;
                if (AreaOf(fs.posX >> 4, fs.posY >> 4) != area) continue;
                double dx = fs.posX - px, dy = fs.posY - py, d = dx * dx + dy * dy;
                if (d >= bd) continue;
                int claims = 0;
                foreach (CTFXBot o in _m.LiveBots) if (o != b && o.Mini != null && o.Mini.GrabFlag == fs && !o.IsDead) claims++;
                if (claims >= 2) continue;
                if (mb.Phase == MiniBot.Raid && mb.Stage < 2 && FoesNear(b._team, fs.posX, fs.posY, 350.0) > 1) continue;      // a raider goes for it when it is an opening (<= 1 enemy on it) or the push is in the flag room
                bd = d; best = fs;
            }
            if (best == null) { mb.GrabFlag = null; return false; }
            if (mb.GrabFlag != best) GrabGoals++;
            mb.GrabFlag = best;
            mb.Hunting = true; mb.Hold = false;
            tx = best.posX >> 4; ty = best.posY >> 4;
            return true;
        }

        /// <summary>Census: the active flags by owner (T / C / none) and how many lie in each base.</summary>
        internal string FlagCensus()
        {
            int act = 0, t = 0, c = 0, none = 0, inA7 = 0, inD7 = 0, carried = 0;
            foreach (Arena.FlagState fs in _m.Arena._flags.Values)
            {
                if (!fs.bActive) continue;
                act++;
                if (fs.carrier != null) carried++;
                if (fs.team == null) none++; else if (SideOfTeam(fs.team) == 0) t++; else c++;
                int a = AreaOf(fs.posX >> 4, fs.posY >> 4);
                if (a == 0) inA7++; else if (a == 1) inD7++;
            }
            return "flags active " + act + " (T " + t + " C " + c + " none " + none + ", carried " + carried + ", A7 " + inA7 + " D7 " + inD7 + ") grab looks " + GrabLooks;
        }

        /// <summary>Enemies of team (humans in play and bots) within r px of (x, y).</summary>
        int FoesNear(Team team, int x, int y, double r)
        {
            int n = 0;
            foreach (Player p in _m.Arena.Players)
                if (p != null && !p.IsDead && !p.IsSpectator && p._team != null && p._team != team && !p._team.IsSpec && Dist2(p._state.positionX, p._state.positionY, x, y) <= r * r) n++;
            foreach (CTFXBot o in _m.LiveBots)
                if (!o.IsDead && !o.Released && o._team != team && Dist2(o._state.positionX, o._state.positionY, x, y) <= r * r) n++;
            return n;
        }

        void GrabTouchStep(int now)
        {
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || b.InDropship || b.Slot == null || _allIn == mb.Side) continue;     // an all-in side touches in AllInStep
                if (mb.Phase != MiniBot.Raid && mb.Phase != MiniBot.Defend && mb.Phase != MiniBot.Field) continue;
                foreach (Arena.FlagState fs in _m.Arena._flags.Values)
                {
                    if (!fs.bActive || fs.carrier != null || fs.team == b._team) continue;
                    double dx = b._state.positionX - fs.posX, dy = b._state.positionY - fs.posY;
                    if (dx * dx + dy * dy > 40.0 * 40.0) continue;
                    TouchFlag(b, fs, now);
                    mb.GrabFlag = null; b.ForceGoal();
                    return;                                                       // one flag change per poll (the script's flag rule runs on each)
                }
            }
        }
    }
}
