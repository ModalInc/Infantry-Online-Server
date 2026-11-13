// CTF:X server-side bots - BRAIN (one CTFXBot per body). The manager is CTFXBots.cs.
//
// Engine facts this code relies on (verified against the source):
//  a. Movement is CLIENT PHYSICS (CTFXMove.cs, CTFXClientMove): the brain never steers. Once per poll Think ends in
//     Drive(move, moveYaw, faceYaw), which turns the wanted move direction and the facing into the keys a player would hold
//     (KeyThrust / KeyStrafe / KeyRotate); the controller integrates them in 10 ms steps and bounces 90% off walls. So combat
//     movement probes the tiles ahead first (ProbeBlocked: strafe flip, approach / back-off sidestep) and a bot that is pinned
//     against geometry backs out toward the most open direction (PinnedStep). The thrust / rotate / stop calls the engine makes
//     on the base movement controller (also from ArenaBot.manageBotTarget) are ignored by it.
//  b. Key conventions: yaw is 240 units per turn and rises clockwise (0 = N, 60 = E), KeyRotate +1 raises the yaw, KeyStrafe +1
//     is right (clockwise of the nose), KeyThrust -1 is back. A dead, idle or faulted bot presses nothing (poll).
//  c. Bot.poll(): the dead branch spawns the drop item on every poll, so a dead bot is never polled through base.poll().
//     poll() is true only when _itemUseID != 0 or every 200 ms; the engine then routes the state and clears _itemUseID.
//     The updateMs knob (CTFXBots.txt, default 200 = nothing added) makes poll() return true earlier when set below 200.
//     A bot-vs-bot shot is routed by us right after base.poll() (local engine erases bot shots unless an enemy human sees it).
//  d. Yaw is 240 per revolution: yaw = atan2(dx, -dy / 0.7) / 1.5 deg (0 = north, 60 = east, 120 = south, 180 = west);
//     the world direction of a forward run at yaw Y is (sin(Y * 1.5 deg), -0.7 * cos(Y * 1.5 deg)) (y is squished 7/10).
//  e. Pathfinder nodes are tile corners: pixel = node * 100 + 8. The callback runs on the pathing thread (lock!) and
//     arrives immediately with null when more than 25 requests are queued.
//  f. Damage to a bot: target.applyExplosionDamage(true, null, x, y, projectile). Never hit a dead bot (its death event
//     would fire twice). attacker == null makes Bot.Death arrive with killer == null, so LastHitBy tracks bot-vs-bot kills.
//  g. Fire modes (CTFXFireProfile, built once per weapon id from the item data; item delays are in 10 ms units): STAND_STREAM
//     and SHOOT_SCOOT weapons hold still (no move keys, facing kept) until |vx|+|vy| < 300 (1000 = 100 px/s), wait the
//     item prefire, then shoot from a stand; STRAFE_FIRE weapons shoot on the move. Shots are gated by our own readyAt /
//     clipLeft / reload bookkeeping per kit entry instead of the engine ableToFire. Item lockout: after a kit item fires, a
//     DIFFERENT kit item may only fire once the first one's fireDelayOther (profile OtherMs) is over (CanShoot / ShotFired).
//  h. Doors (CTFXDoors.cs): the pathfinder is built from the static level where door tiles are solid and never returns a path
//     through a door. PathMove asks the CTFXDoorPilot first: a goal in another region becomes a plan of door steps (path to the
//     stand tile in front of the door, switchRequest, run straight through: Drive then faces the way it runs, also in combat).
//     Unlabelled tiles and same-region goals are untouched.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;
using Vec3 = Axiom.Math.Vector3;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>One weapon of a kit with its distance band (px): best inside Lo..Hi, nicest near Pref, falloff to Max.</summary>
    internal sealed class CTFXWep
    {
        public int Id, Lo, Hi, Pref, Max;
        public bool Shotgun;
        public CTFXWep(int id, int lo, int hi, int pref, int max, bool shotgun)
        {
            Id = id; Lo = lo; Hi = hi; Pref = pref; Max = max; Shotgun = shotgun;
        }
    }

    /// <summary>How one kit weapon is fired, derived once from its item data (item delays are in 10 ms units).</summary>
    internal sealed class CTFXFireProfile
    {
        public const int StandStream = 0, ShootScoot = 1, StrafeFire = 2;
        public int Id, Mode, IntervalMs, Clip, ReloadMs, PrefireMs;
        public int OtherMs;                // fireDelayOther: after this item fires, NO OTHER item may fire for this long
        public bool Stand, Resolved;       // Resolved = the item was found (an unresolved profile is never cached)
        public string ModeName
        {
            get { return Mode == StandStream ? "STAND" : (Mode == ShootScoot ? "SCOOT" : "STRAFE"); }
        }
    }

    internal sealed partial class CTFXBot : Bot
    {
        // ================================================================ static data
        const int K_POST = 0, K_GUARD_INF = 1, K_BR = 2, K_AR = 3, K_ROAM_CAW = 4, K_ROAM_HVY = 5, K_SNIPER = 6, K_DRAINER = 7, K_GUARD_HVY = 8;
        const int K_MEDIC = 9, K_JT = 10, K_INF = 11, K_INFIL = 12, K_HEAVY = 13, K_ENGY = 14, K_SL = 15, K_SCIOPS = 16, K_SGT = 17, K_CAPT = 18, K_BIO = 19;
        // Bot classes are the zone's own class skills (ctfpl.rpg): "Classes" 1 Field Medic, 2 Jump Trooper, 3 Infantry,
        // 4 Infiltrator, 5 Heavy Weapons, 6 Combat Engineer, 7 Squad Leader; "Classes CTFX" 10 SciOps, 11 Sergeant, 12 Captain,
        // 13 BioChem Trooper (Travis 10-11: the full spread, each with the weapons its builds use).
        internal const int SK_MEDIC = 1, SK_JT = 2, SK_INF = 3, SK_INFIL = 4, SK_HEAVY = 5, SK_ENGY = 6, SK_SL = 7,
            SK_SCIOPS = 10, SK_SGT = 11, SK_CAPT = 12, SK_BIO = 13;
        // stand / scoot cycle phases and what FireModeStep tells the movement + fire code
        const int FM_FREE = 0, FM_STOP = 1, FM_BURST = 2, FM_REPOS = 3;
        const int FMR_FREE = 0, FMR_MOVE = 1, FMR_HOLD = 2, FMR_FIRE = 3;
        // DuelBot bodies (ctfpl.veh 126-131, 301): unlike the player class bodies they have weapon-collision
        // physics, so players' shots actually hit them (Travis 10-11). 129 Infantry, 301 Heavy Weapons, 127 Heavy.
        static readonly int[] RoamBodies = new int[] { 129, 301, 127 };

        static CTFXWep W(int id, int lo, int hi, int pref, int max) { return new CTFXWep(id, lo, hi, pref, max, false); }
        static CTFXWep SG(int id, int hi, int pref, int max) { return new CTFXWep(id, 0, hi, pref, max, true); }

        // Every kit is legal for the class it is rolled with (RollLoadout); CTFXSkillLogic re-checks each item against the item's
        // skill logic at spawn and drops anything the class could not buy, so a data change can never arm a bot illegally.
        static readonly CTFXWep[][] Kits = new CTFXWep[][]
        {
            new CTFXWep[] { W(3031, 300, 9999, 400, 700), W(3009, 120, 300, 200, 360), W(1109, 0, 150, 90, 260), W(1062, 0, 0, 0, 0) },    // post (Heavy): AC / MML / SMG / Demo Pack
            new CTFXWep[] { W(1096, 100, 9999, 280, 520), SG(3023, 140, 80, 200), W(1106, 150, 300, 220, 360) },     // guard (Infantry): AR / SG / rifle grenade
            new CTFXWep[] { W(3038, 120, 9999, 280, 560), SG(3023, 140, 80, 200) },                                  // (Infantry) battle rifle + SG
            new CTFXWep[] { W(1096, 120, 9999, 260, 520), SG(3023, 140, 80, 200) },                                  // (Infantry) AR + SG
            new CTFXWep[] { W(1004, 100, 9999, 250, 480), SG(3001, 120, 70, 200) },                                  // roam (Infantry): CAW + ACW
            new CTFXWep[] { W(3028, 100, 9999, 260, 500), W(1109, 0, 150, 90, 260) },                               // roam (Heavy): LMG + SMG
            new CTFXWep[] { W(1056, 250, 9999, 480, 800), W(1109, 0, 250, 130, 320) },                               // sniper (Infantry): g9 / SMG
            new CTFXWep[] { W(1163, 60, 9999, 150, 260), W(1113, 100, 400, 250, 500), W(1109, 0, 90, 60, 200) },     // drainer (Medic): beamer / Tranq / SMG
            new CTFXWep[] { W(3028, 100, 9999, 260, 500), W(3009, 150, 300, 220, 360), W(1109, 0, 150, 90, 260) },   // guard (Heavy): LMG / MML / SMG
            // ---- the full class spread (class research, builds + starting kits); the class gate re-checks every item
            new CTFXWep[] { W(1109, 0, 250, 130, 320), W(1038, 200, 9999, 400, 700), W(1113, 100, 400, 250, 500), W(1163, 60, 9999, 150, 260) },   // Field Medic: SMG / Needler / Tranq / beamer
            new CTFXWep[] { W(1096, 100, 9999, 260, 520), W(1106, 150, 300, 220, 360), W(1017, 150, 400, 260, 500), W(1011, 80, 300, 180, 400) },  // Jump Trooper: AR / RG / GL / frag
            new CTFXWep[] { W(1096, 100, 9999, 260, 520), W(1106, 150, 300, 220, 360), SG(3001, 120, 70, 200), W(1176, 80, 300, 180, 400) },      // Infantry: AR / RG / ACW / WP
            new CTFXWep[] { W(1044, 100, 9999, 300, 600), W(1073, 80, 400, 200, 500), W(1166, 60, 9999, 150, 260) },                              // Infiltrator: PR / Disruptor / beamer
            new CTFXWep[] { W(3031, 300, 9999, 400, 700), W(3009, 120, 300, 200, 360), W(1083, 150, 450, 300, 600), W(3028, 100, 9999, 260, 500), W(1062, 0, 0, 0, 0) }, // Heavy Weapons: AC / MML / RPG / LMG / Demo Pack
            new CTFXWep[] { W(1030, 60, 350, 180, 450), SG(306, 140, 80, 180), W(1164, 60, 9999, 150, 260), W(1064, 80, 300, 180, 400) },          // Combat Engineer: flechette / incinerator / beamer / haywire
            new CTFXWep[] { W(3038, 120, 9999, 280, 560), W(3016, 60, 400, 200, 500), W(1165, 60, 9999, 150, 260), W(1064, 80, 300, 180, 400) },   // Squad Leader: BR2000 / CR 102 / beamer / haywire
            new CTFXWep[] { W(1031, 250, 9999, 480, 900), W(1185, 0, 50, 25, 70), W(1195, 0, 40, 20, 60) },                                       // SciOps: GR790 / PowerFist / Zap Glove
            new CTFXWep[] { W(3038, 120, 9999, 280, 560), SG(3001, 120, 70, 200), W(1064, 80, 300, 180, 400), W(1185, 0, 50, 25, 70) },          // Sergeant: BR2000 / ACW / haywire / PowerFist (legal for 11 since the itm fix)
            new CTFXWep[] { W(1096, 100, 9999, 260, 520), W(1185, 0, 50, 25, 70), W(1064, 80, 300, 180, 400), W(1012, 80, 300, 180, 400) },       // Captain: AR / PowerFist / haywire / EMP
            new CTFXWep[] { W(1202, 100, 400, 250, 500), W(1197, 0, 180, 100, 220), W(1205, 100, 400, 250, 500), W(1038, 200, 9999, 400, 700) },   // BioChem: CL5 / GP6 / Viral / Needler
            // ---- bountiers (CTFXBounty): the trap engineer, the SciOps picker, the Infiltrator picker
            new CTFXWep[] { W(1164, 0, 9999, 150, 260), W(1030, 60, 350, 180, 450), W(1014, 0, 0, 0, 0), W(1029, 0, 0, 0, 0), W(1020, 0, 0, 0, 0) },  // Engineer: Engy Beamer / flechette / AP, bullet, plasma mines
            new CTFXWep[] { W(1195, 0, 45, 20, 60), W(1185, 0, 55, 25, 70), W(1031, 150, 9999, 400, 900) },                                          // SciOps: Zap Glove / PowerFist / GR790
            new CTFXWep[] { W(1073, 40, 300, 120, 400), W(1044, 60, 9999, 200, 500), W(1166, 0, 9999, 120, 260) }                                     // Infiltrator: Disruptor / PR / beamer
            ,
            // ---- the SciOps maser squad (Travis 10-13, maser enabled in the local itm): the burst passes walls (bounceCount -1)
            new CTFXWep[] { W(1187, 0, 9999, 150, 280), W(1185, 0, 55, 25, 70), W(1195, 0, 45, 20, 60) },                                       // SciOps: Maser / PowerFist / Zap Glove
            // ---- the Zap Glove ambusher (Travis 10-14): cloaked, waits on a straggler's path, zap then fist; the GR790 only answers fire
            new CTFXWep[] { W(1195, 0, 45, 15, 40), W(1185, 0, 55, 20, 50), W(1196, 0, 140, 50, 200), W(1012, 0, 120, 60, 160), W(1031, 150, 9999, 400, 900) },   // SciOps: Zap / PowerFist / PB g111 / EMP / GR790
            // ---- the meme Heavy (Travis 10-15): Plasma Cannon, slow, bounces off walls twice, usually a one-shot; AC as the real gun
            new CTFXWep[] { W(1008, 120, 700, 320, 800), W(3031, 300, 9999, 400, 700), W(1109, 0, 150, 90, 260), W(1062, 0, 0, 0, 0) },       // Heavy: PC2000 / AC / SMG / Demo Pack
            // ---- the drop pack Jump Trooper bountier (Travis 10-15): g9 at range from the pack, swoops with rifle grenade / AR / GL
            new CTFXWep[] { W(1056, 250, 9999, 520, 850), W(1106, 150, 300, 220, 360), W(1096, 100, 9999, 260, 520), W(1017, 150, 400, 260, 500) }   // JT: g9 / RG / AR / GL
        };
        const int K_BTY_ENG = 20, K_BTY_SCI = 21, K_BTY_INFIL = 22, K_MASER = 23, K_ZAP = 24, K_PC = 25, K_DROPJT = 26;

        /// <summary>A kit by id: the hand kits, or a zone build the shop rolled (CTFXShop, ids from 100).</summary>
        internal static CTFXWep[] KitAt(int k)
        {
            if (k >= CTFXShop.Base) { CTFXShop.Build b = CTFXShop.Of(k); return b != null ? b.Kit : Kits[K_INF]; }
            return Kits[k];
        }

        /// <summary>The hand-tuned range profile of a weapon some kit already carries (null = none).</summary>
        internal static CTFXWep KnownW(int id)
        {
            foreach (CTFXWep[] k in Kits) foreach (CTFXWep w in k) if (w.Id == id) return new CTFXWep(w.Id, w.Lo, w.Hi, w.Pref, w.Max, w.Shotgun);
            return null;
        }

        internal static double ShopShare;   // shop=<share>: lives of classes with zone builds that roll one (CTFXShop)
        int _kitId;
        double _wallRange;                  // a kit weapon that passes walls (the maser): its reach, targets behind walls inside it count
        internal string KitName;
        internal int LastHitItem, LifeStart;

        bool SeesOrWall(int px, int py, int x, int y)
        {
            if (Mgr.Los(px, py, x, y)) return true;
            if (_wallRange <= 0) return false;
            double dx = x - px, dy = y - py;
            return dx * dx + dy * dy <= _wallRange * _wallRange;
        }

        // ---- fire profiles: computed once per weapon id from the item data and shared by every bot
        static readonly int[] ScootIds = new int[] { 3009, 1083, 1056, 1106, 1082 };    // one-volley launchers always shoot and scoot
        static readonly Dictionary<int, CTFXFireProfile> Profiles = new Dictionary<int, CTFXFireProfile>();
        static bool _modesLogged;

        static CTFXFireProfile GetProfile(int id)
        {
            lock (Profiles)
            {
                CTFXFireProfile p;
                if (!Profiles.TryGetValue(id, out p))
                {
                    p = BuildProfile(id);
                    if (p.Resolved) Profiles[id] = p;
                }
                return p;
            }
        }

        /// <summary>The fire numbers of an item (MultiUse or Projectile); zeros for anything else.</summary>
        static void ReadFire(ItemInfo it, out int fireDelay, out int prefire, out int clip, out int reload, out bool cancel, out int other)
        {
            fireDelay = 0; prefire = 0; clip = 0; reload = 0; cancel = false; other = 0;
            ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
            if (mu != null)
            {
                fireDelay = mu.fireDelay; prefire = mu.prefireDelay; clip = mu.ammoCapacity;
                reload = mu.reloadDelayNormal; cancel = mu.movementCancelsPrefire; other = mu.fireDelayOther;
                return;
            }
            ItemInfo.Projectile pr = it as ItemInfo.Projectile;
            if (pr != null)
            {
                fireDelay = pr.fireDelay; prefire = pr.prefireDelay; clip = pr.ammoCapacity;
                reload = pr.reloadDelayNormal; cancel = pr.movementCancelsPrefire; other = pr.fireDelayOther;
            }
        }

        static CTFXFireProfile BuildProfile(int id)
        {
            CTFXFireProfile p = new CTFXFireProfile();
            p.Id = id;
            ItemInfo top = null, child = null;
            try
            {
                top = AssetManager.Manager.getItemByID(id);
                child = top;
                ItemInfo.MultiUse mu = top as ItemInfo.MultiUse;
                if (mu != null && mu.childItems != null && mu.childItems.Count > 0)
                {
                    ItemInfo c = AssetManager.Manager.getItemByID(mu.childItems[0].id);
                    if (c != null) child = c;
                }
            }
            catch (Exception) { }
            p.Resolved = top != null;
            int tf, tp, tc, tr, cf, cp, cc, cr, tOth, cOth;
            bool tcancel, ccancel;
            ReadFire(top, out tf, out tp, out tc, out tr, out tcancel, out tOth);
            ReadFire(child, out cf, out cp, out cc, out cr, out ccancel, out cOth);
            p.OtherMs = Math.Max(0, Math.Max(tOth, cOth) * 10);
            p.IntervalMs = Math.Max(100, Math.Max(tf, cf) * 10);
            p.Clip = tc > 0 ? tc : (cc > 0 ? cc : 1);
            p.ReloadMs = Math.Max(300, (tr > 0 ? tr : cr) * 10);
            p.PrefireMs = Math.Max(tp, cp) * 10;
            p.Stand = tcancel || ccancel;
            bool scootId = false;
            for (int i = 0; i < ScootIds.Length; i++)
                if (ScootIds[i] == id) scootId = true;
            if (p.Stand && p.Clip >= 3) p.Mode = CTFXFireProfile.StandStream;
            else if ((p.Stand && p.Clip < 3) || scootId) p.Mode = CTFXFireProfile.ShootScoot;
            else p.Mode = CTFXFireProfile.StrafeFire;
            return p;
        }

        /// <summary>One console + log line the first time any bot builds its kit (through the manager's warn-once log helper).</summary>
        static void LogFireModes(CTFXBotManager mgr)
        {
            if (_modesLogged) return;
            _modesLogged = true;
            System.Text.StringBuilder sb = new System.Text.StringBuilder("fire modes:");
            HashSet<int> seen = new HashSet<int>();
            for (int k = 0; k < Kits.Length; k++)
                for (int i = 0; i < Kits[k].Length; i++)
                {
                    int id = Kits[k][i].Id;
                    if (!seen.Add(id)) continue;
                    CTFXFireProfile p = GetProfile(id);
                    if (!p.Resolved) { sb.Append(' ').Append(id).Append("=?"); continue; }
                    sb.Append(' ').Append(id).Append('=').Append(p.ModeName)
                      .Append("(pre ").Append(p.PrefireMs).Append(", clip ").Append(p.Clip)
                      .Append(", int ").Append(p.IntervalMs).Append(", rel ").Append(p.ReloadMs).Append(", oth ").Append(p.OtherMs).Append(')');
                }
            mgr.WarnOnce(sb.ToString());
        }

        // ---- hot-spot age: the manager keeps its newest spot for 90 s but exposes no age, so the first sighting
        // (every bot checks each poll) or one of our own hits stamps it here and ChooseGoal enforces 60 s
        static bool HotHave;
        static int HotX, HotY, HotSeen;

        // 8 compass directions (world tiles) for the pinned escape: N, NE, E, SE, S, SW, W, NW
        static readonly int[] Dx8 = new int[] { 0, 1, 1, 1, 0, -1, -1, -1 };
        static readonly int[] Dy8 = new int[] { -1, -1, 0, 1, 1, 1, 0, -1 };

        sealed class LosCache { public int Key = -1; public int Tick; public bool Val; }

        // ================================================================ loadout roll (called by the manager)
        static bool PackHasKiller(CTFXSlot slot)
        {
            if (slot.Group == null) return false;
            foreach (CTFXSlot s in slot.Group.Slots) if (s.Style == CTFXStyle.Bountier) return true;
            return false;
        }

        internal static bool RollLoadout(CTFXSlot slot, Random rng, HashSet<int> bad, out int body, out int kit, out int cls)
        {
            int k = slot.StyleIndex;
            if (slot.Style == CTFXStyle.Bountier)
            {   // a solo bountier plays the class it was given (CTFXBots mustHave pass); a pack killer is the BR finisher
                cls = slot.ForceClass != 0 ? slot.ForceClass : SK_INF;
                kit = cls == SK_ENGY ? K_BTY_ENG : cls == SK_SCIOPS ? (slot.Group != null && slot.Group.Ambush ? K_ZAP : K_BTY_SCI) : cls == SK_INFIL ? K_BTY_INFIL
                    : slot.ForceClass == SK_INF ? K_SNIPER : cls == SK_JT && slot.Group != null && slot.Group.DropPack ? K_DROPJT : K_BR;   // ForceClass Inf = the solo sniper
            }
            else if (slot.ForceClass != 0) { cls = slot.ForceClass; kit = KitOf(cls); }      // mustHave seat
            else switch (slot.Style)
            {
                case CTFXStyle.Post: cls = SK_HEAVY; kit = K_POST; break;                                     // turret-room AC anchor
                case CTFXStyle.Guard:
                    switch (k % 4)
                    {
                        case 0: cls = SK_INF; kit = K_INF; break;
                        case 1: cls = SK_HEAVY; kit = K_GUARD_HVY; break;
                        case 2: cls = SK_ENGY; kit = K_ENGY; break;
                        default: cls = SK_SGT; kit = K_SGT; break;
                    }
                    break;
                case CTFXStyle.Assault:                                                                     // the squad leader is a Squad Leader
                    switch (k % 6)
                    {
                        case 0: cls = SK_SL; kit = K_SL; break;
                        case 1: cls = SK_JT; kit = K_JT; break;
                        case 2: cls = SK_CAPT; kit = K_CAPT; break;
                        case 3: cls = SK_BIO; kit = K_BIO; break;
                        case 4: cls = SK_INF; kit = K_BR; break;
                        default: cls = SK_INF; kit = K_INF; break;
                    }
                    break;
                case CTFXStyle.Roam:
                    switch (rng.Next(6))
                    {
                        case 0: cls = SK_INF; kit = K_INF; break;
                        case 1: cls = SK_JT; kit = K_JT; break;
                        case 2: cls = SK_HEAVY; kit = K_HEAVY; break;
                        case 3: cls = SK_SCIOPS; kit = K_SCIOPS; break;
                        case 4: cls = SK_INFIL; kit = K_INFIL; break;
                        default: cls = SK_INF; kit = K_ROAM_CAW; break;
                    }
                    break;
                case CTFXStyle.Sniper:
                    if (k % 2 == 0) { cls = SK_INF; kit = K_SNIPER; } else { cls = SK_SCIOPS; kit = K_SCIOPS; }
                    break;
                case CTFXStyle.Drain:
                    if (PackHasKiller(slot)) { cls = SK_MEDIC; kit = K_MEDIC; }                             // a bounty pack: drainers only
                    else if (k % 3 == 2) { cls = SK_INF; kit = K_BR; }                                      // 1 finisher (the bountier) in every 3
                    else { cls = SK_MEDIC; kit = K_MEDIC; }
                    break;
                case CTFXStyle.Maser: cls = SK_SCIOPS; kit = K_MASER; break;                                  // the SciOps maser squad
                case CTFXStyle.Engineer: cls = SK_ENGY; kit = K_ENGY; break;                                 // builds its end's turret sets (CTFXTurrets)
                case CTFXStyle.Stormer:                                                                     // the anchor stormer sneaks in: Infiltrator, or SciOps
                    if (k % 2 == 0) { cls = SK_INFIL; kit = K_INFIL; } else { cls = SK_SCIOPS; kit = K_SCIOPS; }
                    break;
                default:                                                                                    // adhd: anything goes
                    {
                        int[] cl = new int[] { SK_INF, SK_JT, SK_HEAVY, SK_INFIL, SK_ENGY, SK_SL, SK_SCIOPS, SK_SGT, SK_CAPT, SK_BIO, SK_MEDIC };
                        int[] kt = new int[] { K_INF, K_JT, K_HEAVY, K_INFIL, K_ENGY, K_SL, K_SCIOPS, K_SGT, K_CAPT, K_BIO, K_MEDIC };
                        int r = rng.Next(cl.Length);
                        cls = cl[r]; kit = kt[r];
                        break;
                    }
            }
            // shopping (overnight runs): a share of lives buy one of their class's zone builds instead of the hand kit
            if (ShopShare > 0 && slot.Style != CTFXStyle.Post && slot.Style != CTFXStyle.Bountier && slot.Style != CTFXStyle.Engineer
                && slot.Style != CTFXStyle.Stormer && slot.Style != CTFXStyle.Maser && slot.Style != CTFXStyle.Drain && slot.ForceClass == 0
                && rng.NextDouble() < ShopShare)
            {
                CTFXShop.Build sb = CTFXShop.Roll(cls, rng);
                if (sb != null) kit = CTFXShop.KitIdOf(sb);
            }
            if (slot.BuildName != null)
            {   // MiniTP: the seat's zone build (CTFXBuilds.txt) by name, when its class is the seat's
                CTFXShop.Build nb = CTFXShop.ByName(slot.BuildName);
                if (nb != null && nb.Cls == cls && nb.Kit != null && nb.Kit.Length > 0) kit = CTFXShop.KitIdOf(nb);
            }
            else if (cls == SK_HEAVY && slot.Style != CTFXStyle.Post && rng.NextDouble() < 0.12) kit = K_PC;   // the meme Plasma Cannon Heavy
            body = BodyFor(cls);                                                                        // the class's own bot body
            if (body > 0 && !bad.Contains(body)) return true;
            body = DuelBodyFor(cls);                                                                    // no class bodies here: closest DuelBot
            if (!bad.Contains(body)) return true;
            foreach (int alt in new int[] { 129, 131, 130, 301, 127 })                                   // any hittable DuelBot body will do
                if (!bad.Contains(alt)) { body = alt; return true; }
            return false;
        }

        // The class bot bodies (local ctfpl.veh 240-250, scratchpad bodies/make_bodies.py): copies of the class vehicles 110-120 so
        // every class looks like itself, with shots hitting them (WeaponPhysicsUnowned 0) and the class's build armour baked in.
        // Team uniforms are drawn by the client from the team's CFG Colors, not by the vehicle.
        internal static readonly int[] ClassBodyIds = new int[] { 240, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250 };
        static readonly Dictionary<int, int> ClassBody = new Dictionary<int, int>
        {
            { SK_ENGY, 240 }, { SK_HEAVY, 241 }, { SK_INF, 242 }, { SK_INFIL, 243 }, { SK_JT, 244 }, { SK_MEDIC, 245 },
            { SK_SL, 246 }, { SK_SCIOPS, 247 }, { SK_BIO, 248 }, { SK_SGT, 249 }, { SK_CAPT, 250 }
        };

        internal static int BodyFor(int cls)
        {
            int b;
            return ClassBody.TryGetValue(cls, out b) ? b : 0;
        }

        /// <summary>
        /// The DuelBot body that stands in for a class when this ctfpl.veh has no class bot bodies (class research: the class
        /// vehicles have WeaponPhysicsUnowned 1, so shots pass through a server bot wearing one).
        /// </summary>
        internal static int DuelBodyFor(int cls)
        {
            switch (cls)
            {
                case SK_HEAVY: return 301;                      // DuelBot Heavy Weapons
                case SK_MEDIC: case SK_SL: case SK_SCIOPS: case SK_BIO: return 131;     // DuelBot Medic
                case SK_INFIL: case SK_JT: return 130;          // DuelBot Infil
                default: return 129;                            // DuelBot Infantry: Infantry, Engineer, Sergeant, Captain
            }
        }

        static readonly Dictionary<int, string> ClassShort = new Dictionary<int, string>
        {
            { SK_MEDIC, "Med" }, { SK_JT, "JT" }, { SK_INF, "Inf" }, { SK_INFIL, "Infil" }, { SK_HEAVY, "Hvy" }, { SK_ENGY, "Eng" },
            { SK_SL, "SL" }, { SK_SCIOPS, "Sci" }, { SK_SGT, "Sgt" }, { SK_CAPT, "Cpt" }, { SK_BIO, "Bio" }
        };
        internal static string ShortClass(int cls) { string n; return ClassShort.TryGetValue(cls, out n) ? n : "c" + cls; }

        internal static int ClassByShort(string s)
        {
            foreach (KeyValuePair<int, string> kv in ClassShort) if (kv.Value.Equals(s, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return 0;
        }

        static int KitOf(int cls)
        {
            switch (cls)
            {
                case SK_MEDIC: return K_MEDIC; case SK_JT: return K_JT; case SK_INF: return K_INF; case SK_INFIL: return K_INFIL;
                case SK_HEAVY: return K_HEAVY; case SK_ENGY: return K_ENGY; case SK_SL: return K_SL; case SK_SCIOPS: return K_SCIOPS;
                case SK_SGT: return K_SGT; case SK_CAPT: return K_CAPT; case SK_BIO: return K_BIO;
            }
            return K_INF;
        }

        /// <summary>The class a seat always rolls by its style and place (0 = random each life: roamers, ADHD).</summary>
        static int FixedClass(CTFXSlot s)
        {
            if (s.ForceClass != 0) return s.ForceClass;
            int k = s.StyleIndex;
            switch (s.Style)
            {
                case CTFXStyle.Post: return SK_HEAVY;
                case CTFXStyle.Guard: return k % 4 == 0 ? SK_INF : k % 4 == 1 ? SK_HEAVY : k % 4 == 2 ? SK_ENGY : SK_SGT;
                case CTFXStyle.Assault: { int[] c = new int[] { SK_SL, SK_JT, SK_CAPT, SK_BIO, SK_INF, SK_INF }; return c[k % 6]; }
                case CTFXStyle.Sniper: return k % 2 == 0 ? SK_INF : SK_SCIOPS;
                case CTFXStyle.Drain: return k % 3 == 2 ? SK_INF : SK_MEDIC;
                case CTFXStyle.Engineer: return SK_ENGY;
                case CTFXStyle.Stormer: return k % 2 == 0 ? SK_INFIL : SK_SCIOPS;
            }
            return 0;
        }

        /// <summary>mustHave: every class in <paramref name="need"/> that no seat of the team plays for sure goes to one seat whose class is
        /// free to change (a roamer or ADHD first, then a plain infantry guard / assault member). Returns how many seats were set.</summary>
        internal static int EnsureClasses(List<CTFXSlot> team, List<int> need, Random rng)
        {
            int set = 0;
            foreach (int cls in need)
            {
                bool have = false;
                foreach (CTFXSlot s in team) if (FixedClass(s) == cls) { have = true; break; }
                if (have) continue;
                CTFXSlot pick = null;
                foreach (CTFXSlot s in team)
                    if (s.ForceClass == 0 && (s.Style == CTFXStyle.Roam || s.Style == CTFXStyle.Adhd)) { pick = s; break; }
                if (pick == null)
                    foreach (CTFXSlot s in team)
                        if (s.ForceClass == 0 && FixedClass(s) == SK_INF && s.Style != CTFXStyle.Sniper && s.Style != CTFXStyle.Drain) { pick = s; break; }
                if (pick == null) continue;
                pick.ForceClass = cls;
                set++;
            }
            return set;
        }

        /// <summary>The class (skill id) a DuelBot body plays when nothing else says so.</summary>
        internal static int BodyClass(int body)
        {
            switch (body)
            {
                case 301: case 127: return SK_HEAVY;
                case 130: return SK_INFIL;
                case 131: return SK_MEDIC;
                default: return SK_INF;
            }
        }


        // ---- class gate: each kit item must pass its skill logic for the class (the rule ?buy uses); worked out once per kit and class
        static readonly Dictionary<int, CTFXWep[]> LegalKits = new Dictionary<int, CTFXWep[]>();

        static CTFXWep[] LegalKit(int kitId, int cls, CTFXBotManager mgr)
        {
            lock (LegalKits)
            {
                CTFXWep[] k;
                int key = kitId * 64 + cls;
                if (LegalKits.TryGetValue(key, out k)) return k;
                List<CTFXWep> ok = new List<CTFXWep>();
                foreach (CTFXWep w in KitAt(kitId))
                {
                    if (CTFXSkillLogic.Allowed(w.Id, cls)) ok.Add(w);
                    else if (mgr != null)
                        mgr.WarnOnce("class gate: a " + ClassName(cls) + " may not buy item " + w.Id + " (" + ItemName(w.Id) + ", logic " + ItemLogic(w.Id) + "): dropped from kit " + kitId);
                }
                if (ok.Count == 0 && mgr != null) mgr.WarnOnce("class gate: kit " + kitId + " has no item a " + ClassName(cls) + " may use: those bots are unarmed");
                // Travis 10-13: every class buys the grenades it qualifies for (turrets need them): the first legal one, if none yet
                bool nade = false;
                foreach (CTFXWep w in ok) if (IsGrenade(w.Id)) nade = true;
                if (!nade)
                    foreach (int g in GrenadePref)
                        if (CTFXSkillLogic.Allowed(g, cls)) { ok.Add(W(g, 80, 300, 180, 400)); break; }
                bool rep = false;
                foreach (CTFXWep w in ok) if (w.Id == Repulsor) rep = true;
                if (!rep && CTFXSkillLogic.Allowed(Repulsor, cls)) ok.Add(W(Repulsor, 0, 120, 60, 160));
                int rfg = RfgFor(cls);                                                       // 10-17: the repulsor field / Enhanced RFG as a defensive tool (CTFXRfg.cs)
                if (rfg != 0) { bool have = false; foreach (CTFXWep w in ok) if (w.Id == rfg) have = true; if (!have) ok.Add(W(rfg, 0, 120, 60, 160)); }
                k = ok.ToArray();
                LegalKits[key] = k;
                return k;
            }
        }

        internal const int Repulsor = 1049, Disruptor = 1073;
        internal readonly bool[] _inbDrain = new bool[8];      // CTFXBotManager.Inbound: entry j of the inbound buffer is an energy-only shell (haywire, EMP)
        internal static bool IsDrainOnly(int id) { return DmgKind(id) == 2; }

        // grenades by preference: Frag (all but Infiltrators), WP, Haywire, EMP, Repulsor, Plasma (Infiltrators)
        static readonly int[] GrenadePref = new int[] { 1011, 1176, 1064, 1012, 1049, 1224 };
        static readonly HashSet<int> GrenadeIds = new HashSet<int>(new int[] { 1011, 1012, 1049, 1064, 1176, 1193, 1204, 1224, 1017, 1082, 1106, 1107 });
        internal static bool IsGrenade(int id) { return GrenadeIds.Contains(id); }

        // every kit / class pair RollLoadout can hand out
        static readonly int[][] KitClassPairs = new int[][]
        {
            new int[] { K_POST, SK_HEAVY }, new int[] { K_GUARD_HVY, SK_HEAVY }, new int[] { K_BR, SK_INF }, new int[] { K_ROAM_CAW, SK_INF },
            new int[] { K_SNIPER, SK_INF }, new int[] { K_MEDIC, SK_MEDIC }, new int[] { K_JT, SK_JT }, new int[] { K_INF, SK_INF },
            new int[] { K_INFIL, SK_INFIL }, new int[] { K_HEAVY, SK_HEAVY }, new int[] { K_ENGY, SK_ENGY }, new int[] { K_SL, SK_SL },
            new int[] { K_SCIOPS, SK_SCIOPS }, new int[] { K_SGT, SK_SGT }, new int[] { K_CAPT, SK_CAPT }, new int[] { K_BIO, SK_BIO },
            new int[] { K_BTY_ENG, SK_ENGY }, new int[] { K_BTY_SCI, SK_SCIOPS }, new int[] { K_BTY_INFIL, SK_INFIL }
        };

        /// <summary>Log line at attach: every kit the roll can hand out, checked against its class.</summary>
        /// <summary>Attach log: the cloak each stormer class switches on (CloakFor).</summary>
        internal static string CloakReport()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("stormer cloak:");
            foreach (int cls in new int[] { SK_INFIL, SK_SCIOPS })
            {
                ItemInfo.UtilityItem u = CloakFor(cls);
                sb.Append(' ').Append(ClassName(cls)).Append('=');
                if (u == null) sb.Append("none");
                else sb.Append(u.id).Append(' ').Append(u.name.Trim((char)34, ' ')).Append(" (cloakDistance ").Append(u.cloakDistance)
                    .Append(", stealthDistance ").Append(u.stealthDistance).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>Every weapon some kit carries, once.</summary>
        internal static List<int> KitWeaponIds()
        {
            List<int> ids = new List<int>();
            foreach (CTFXWep[] kit in Kits)
                foreach (CTFXWep w in kit)
                    if (!ids.Contains(w.Id)) ids.Add(w.Id);
            return ids;
        }

        internal static string GateReport()
        {
            int items = 0, bad = 0;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (int[] p in KitClassPairs)
                foreach (CTFXWep w in Kits[p[0]])
                {
                    items++;
                    if (CTFXSkillLogic.Allowed(w.Id, p[1])) continue;
                    bad++;
                    sb.Append(' ').Append(ClassName(p[1])).Append(':').Append(w.Id);
                }
            return "class gate: " + KitClassPairs.Length + " kits, " + items + " items checked against their class skill logic: "
                + (bad == 0 ? "all legal" : bad + " ILLEGAL, dropped at spawn:" + sb.ToString());
        }

        /// <summary>Log line at attach: the ammo each kit weapon draws and the starting stock per class.</summary>
        internal static string AmmoReport(double scale, bool on)
        {
            if (!on) return "ammo: off (ammo=0), unlimited";
            System.Text.StringBuilder sb = new System.Text.StringBuilder("ammo (stock x" + scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "):");
            HashSet<long> seen = new HashSet<long>();
            foreach (int[] p in KitClassPairs)
                foreach (CTFXWep w in Kits[p[0]])
                {
                    if (!seen.Add(((long)w.Id << 8) | (uint)p[1])) continue;
                    ItemInfo it = AssetManager.Manager.getItemByID(w.Id);
                    int aid = 0, per = 0, cap;
                    if (it == null || !it.getAmmoType(out aid, out per, out cap) || aid <= 0 || per <= 0) { sb.Append(' ').Append(w.Id).Append("=free"); continue; }
                    sb.Append(' ').Append(w.Id).Append('=').Append(ItemName(aid)).Append(" x").Append(per)
                      .Append(" of ").Append((int)Math.Round(StartAmmo(p[1], aid) * scale)).Append('(').Append(ClassName(p[1])).Append(')');
                }
            return sb.ToString();
        }

        // Starting ammo per item, from the zone's own builds (CTF.cs buildInputs): dhvy = AC + MML with ammo mg 125 and rocket 26,
        // ohvy2 = ammo pistol 70 for a heavy SMG, oinf / dinf = ammo rifle 60-140 and ammo shotgun 60-155, dinfcaw = ammo pistol 300,
        // the medic builds = ammo pistol 120, oinf = 4 rifle grenades.
        static int StartAmmo(int cls, int ammoId)
        {
            switch (ItemName(ammoId))
            {
                case "Ammo MG": return 125;                                                     // dhvy
                case "Rocket": return 26;                                                       // dhvy (MML 2 a volley, RPG 1)
                case "Ammo Rifle": return cls == SK_HEAVY ? 100 : (cls == SK_JT ? 200 : 120);   // oinf/dinf, jt 200
                case "Ammo Shotgun": return cls == SK_ENGY ? 100 : 120;                         // engstandard 100 (flechette)
                case "Ammo Pistol": return cls == SK_HEAVY ? 70 : (cls == SK_MEDIC || cls == SK_BIO ? 120 : 200);
                case "Light HE": return 50;                                                     // jt
                case "Fuel Canister": return 50;                                                // engstandard (incinerator)
                case "Gas Canister": return 30;                                                 // BioChem kits carry none: given so the class can fight
                case "Tranq": return 10;                                                        // med
                case "Viral": return 20;                                                        // BioChem starting kit
                default: return 4;                                                              // grenades, rifle grenades: an item that is its own ammo
            }
        }

        internal static string ClassName(int cls)
        {
            SkillInfo sk = null;
            try { sk = AssetManager.Manager.getSkillByID(cls); } catch (Exception) { }
            return sk != null ? sk.Name : "class " + cls;
        }

        static string ItemName(int id) { ItemInfo it = AssetManager.Manager.getItemByID(id); return it != null ? it.name : "?"; }
        static string ItemLogic(int id) { ItemInfo it = AssetManager.Manager.getItemByID(id); return it != null ? it.skillLogic : "?"; }

        // ================================================================ identity / hooks used by the manager
        internal CTFXBotManager Mgr;
        internal CTFXSlot Slot;
        internal CTFXBotGroup Group;
        internal CTFXStyle Style;
        internal CTFXBot LastHitBy;     // last bot that damaged this one (bot-vs-bot kill stats); cleared on death
        internal bool LastHitByTurret;
        bool _carrying;                 // carrying an event flag this poll (CTFXFlags)  // the last hit came from a turret (a carrier killed by one: the flag goes back, CTFXFlags)
        internal bool DeathHandled;
        internal bool Released;
        internal int LastPollTick;

        // ================================================================ brain state
        readonly Random _rng;
        bool _ready;
        CTFXClientMove _cm;                 // client-faithful movement (CTFXMove.cs): the brain only sets its Key* fields, in Drive
        readonly Predicate<Vehicle> _botPred;
        int _faults;

        // movement keys / cadence
        bool _driven;                       // Drive ran during this poll (poll releases every key when it did not)
        bool _doorSteer;                   // PathMove is steering through a door this poll: face the way we run
        int _lastRoute;                     // tick of the last poll that returned true (updateMs knob)
        bool _routeNow;                     // route this poll (a stun began: the stop shows at once, not up to 200 ms later)
        // where the body was over the last ~0.7 s: a human's explosion is reported where the shooter's client drew the bot (5 Hz
        // updates + ping behind the server's position), so a small anti-effect radius (Viral 8 px) is tested against those positions
        readonly short[] _histX = new short[16], _histY = new short[16];
        readonly int[] _histT = new int[16];
        int _histI;

        // pinned in combat (PinnedStep): a 400 ms measuring window, the 300 ms escape
        int _pinT, _pinX, _pinY, _pinDir, _pinUntil;
        bool _pinArmed;                     // a full window with keys down has passed (the first one is warm-up from a standstill)
        float _pinYaw;

        // style
        int _engage, _minR, _maxR, _jit;
        double EngR { get { return _engage * Mgr.EngageScale; } }          // the engage radius x engageScale (CTFXBots.txt)
        object _farShot;                    // 10-21: the shooter it answered from beyond its engage radius (ScanTargets): kept while in reach and sight
        int _farReach;                      // that answer's reach: its longest usable weapon, at most 1000 px
        double _hyst = 0.7;
        CTFXWep[] _kit;
        int _curWep = -1, _lastWepSwitch, _badMask;

        // target (only the current target is referenced)
        Player _tgtP;
        CTFXBot _tgtB;
        Computer _tgtC;                     // an enemy turret: only when no player or bot is in range (CTFXTurrets)
        int _tgtLastLos, _nextScan;
        readonly LosCache _losT = new LosCache(), _losL = new LosCache();
        int _jitter, _jitTick;
        bool _moving;

        // ---- the human layer (CTFXHuman.cs): what the seat has noticed but not yet answered, the first-shot settle, its aim habits
        internal CTFXHuman Human;
        Player _pendP; CTFXBot _pendB; Computer _pendC;     // a newly seen enemy, answered at _pendAt (reaction time)
        int _pendAt, _firstShotAt;
        double _aimBias, _leadBias, _shotNoise;             // yaw units / fraction of the lead / yaw units
        int _aimBiasAt, _trackAt;
        Player _alertP;                                     // the player whose explosion last hit it
        int _hitAt;                                         // when it was last hit (alerts: an off-screen shooter becomes visible)
        int _laneIdx = -1, _laneN = -1, _laneX, _laneY;     // its own line through the current route waypoint

        internal object TargetObj { get { return _tgtP != null ? (object)_tgtP : _tgtB != null ? (object)_tgtB : (object)_tgtC; } }
        double _wdist = 200.0;              // world distance to the target (y gap x 10/7): where a lobbed weapon has to land
        double _shotRange;                  // distance to the target at the last trigger pull (the shot logs)
        CTFXTurretSpot _job;                // engineer: the turret it is going to build
        // personality (Travis 10-11: not enough difference between the bots' own movements): rolled per life in Setup
        int _pStrafeLo, _pStrafeHi, _pScanLo, _pScanSpan, _pFollowR, _pWanderMs, _wanderX, _wanderY, _wanderAt;
        double _pHold;                      // chance a strafe period is a hold (no strafe); x0.35 since 10-12, and the stop is short
        double _pPedal;                     // chance a strafe period mixes a step in or out into the strafe (Travis 10-12: not stiff)
        int _pedal;                         // this period's pedal: 1 forward, -1 back, 0 none
        // why it stands still when it does (CTFXMonitor): pause, post, perch, anchor, hide, formation, build, door, squad ...
        internal string Why = "-";
        string _why;
        float _lookYaw;                     // idle at a goal: where it looks, re-picked every 1.5-3.5 s (a player glances around)
        int _lookAt;
        int _energyPubAt;                   // energy reaches clients once a second (Travis 10-12: a player's teammates see it tick ~1/s)
        // travelling: on its way to an objective 900+ px off (its choke, base, hall, raid end, a fight at home) it fights what is within
        // 300 px or shooting it and does not chase (10-12 census: 22 of 32 bots outside were bots on their way somewhere, caught
        // brawling with every new body out of the dropship)
        bool _traveling;
        int _travelAt;
        // dropship (CTFXDropship): this body started in the dropship and has not taken a MapEnter portal yet
        internal bool InDropship;
        int _shipSince, _shipReadyAt;
        float _pFollowAng, _pFace;          // formation slot off the leader's facing; a personal facing offset when idle (yaw units)
        // a turret that shot at this bot lately: it answers it even past its usual engage range (CTFXTurrets.Fight)
        internal Computer TurretThreat;
        internal int TurretThreatAt;
        int _buildAt, _nextBuildAt;         // engineer: the build lands at _buildAt (0 = not started); the maker's 2.5 s between builds
        int _mvX, _mvY, _mvTick;
        int _strafeDir = 1, _nextStrafe;
        bool _strafeHold;                   // this strafe period is a no-strafe period (hold / range axis only)

        // retreat / relocate
        bool _retreating, _relocating;
        int _retreatUntil, _retreatCool, _relocUntil, _relocCool, _rtx, _rty;

        // goal / path
        bool _hasGoal, _arrived;
        int _gtx, _gty, _pauseUntil, _adhdNext;
        int _scanT0;
        float _scanBase;
        readonly object _pathLock = new object();
        int _pathSeq;
        List<Vec3> _pendList;
        bool _pendReady, _inFlight, _forceRepath;
        int _inFlightTick, _reqGoalTx, _reqGoalTy, _pathGoalTx, _pathGoalTy, _lastReq, _repathGap, _noPathUntil, _pathFails;
        int[] _pxs, _pys;
        int _pn, _pi;

        // stuck detection
        bool _navActive;
        int _stuckT, _stuckX, _stuckY, _stuckCount;
        int _unstickUntil;
        float _unstickYaw;

        // door navigation (CTFXDoors.cs): door crossings planned between the region of the bot and the region of its goal
        CTFXDoorPilot _door;

        // route following (CTFXRoutes.cs): replaces the engine pathfinder whenever the route book is loaded
        readonly List<int> _rp = new List<int>(64);
        // off the network: the tile path onto it first (CTFXBotManager.PathToNetwork), then the route from that waypoint
        readonly List<int> _preX = new List<int>(16), _preY = new List<int>(16);
        int _prei, _preNode = -1, _netFailAt;
        int _rpi, _rpGoalX, _rpGoalY, _rpLastPlan, _rpDoorAttempt, _rpDoorNext;
        bool _rpHave;

        // shot gating per kit entry (our own clip / interval / reload bookkeeping instead of the engine's ableToFire)
        CTFXFireProfile[] _prof;
        int[] _readyAt, _clipLeft, _reloadUntil;
        bool[] _reloading;
        // item lockout: the kit entry that fired last and when; a DIFFERENT entry may only fire once that item's fireDelayOther is over
        int _lastShotIdx = -1, _lastShotTick;

        // class (skill id) the body plays: gates the kit, equip and fire (CTFXSkillLogic)
        int _class;
        internal bool HasTd;                // carries the Teleport Disruptor (anti-warp source)
        internal static readonly HashSet<string> TdTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "veteran", "ace" };   // tdTiers

        static ItemInfo.UtilityItem _td;
        static bool _tdLooked;
        internal static ItemInfo.UtilityItem TdItem()
        {
            if (!_tdLooked)
            {
                _tdLooked = true;
                try { _td = AssetManager.Manager.getItemByName("Teleport Disruptor") as ItemInfo.UtilityItem; } catch (Exception) { }
            }
            return _td;
        }
        // ammo (ammo=1): per kit entry the stock slot it draws from (-1 = no ammo) and the ammo one shot uses; per bot the stock of
        // each ammo item its kit uses. A bot whose whole kit is dry backs off and "rebuys" (refill) when _rebuyUntil passes.
        int[] _ammoSlot, _ammoPer, _ammo, _ammoStart;
        int _rebuyUntil;
        bool _rebuying;
        // stun: anti-effect deadlines (client rule: a projectile's antiEffects* x 10 ms inside its antiEffectsRadius)
        int _antiThrustUntil, _antiRotateUntil, _antiFireUntil, _antiRechargeUntil;
        int _stunProbeN, _stunProbeT, _stunProbeX, _stunProbeY, _stunProbeV;     // first 3 stuns: logged movement check

        // stand / scoot cycle of the equipped weapon (FireModeStep)
        int _fmPhase, _fmT0, _fmStillT, _fmUntil, _fmShots;
        bool _fmStill;

        // pursuit of a target that dropped out of sight (PursueMove)
        bool _lkValid, _pursuing, _pursueHadGoal;
        int _lkX, _lkY, _lkTick, _pursueCool;

        // ================================================================ construction
        public CTFXBot(VehInfo.Car type, Helpers.ObjectState state, Arena arena, CTFXBotManager mgr)
            : base(type, state, arena, new CTFXClientMove(type, state, arena))
        {
            Mgr = mgr;
            _rng = mgr.Rng;
            _cm = _movement as CTFXClientMove;
            UseSharedSkillLayer = false;            // CTFXHuman already models reaction, settle and aim error
            _botPred = delegate(Vehicle v)
            {
                CTFXBot o = v as CTFXBot;
                return o != null && o != this && o._team != _team && !o.IsDead && !o.Released && !o.InDropship;
            };
        }

        internal int ClassId { get { return _class; } }
        internal int CourierPauseUntil;     // a courier hunting an enemy infiltrator: no new flag run until then (CTFXFlags.CourierSweep)

        // ---- squads (CTFXSquads): warp cooldowns (bots carry no energy), summon charges, the stormer's hiding spot
        internal int WarpReadyAt, SummonCharges = 3, SummonRechargeAt, SummonNextAt;
        internal int WarpCapUntil;          // Travis 10-16: "cap each bot" - its own warps (rejoins, hops, attack calls) at most one per 45-90 s
        internal bool AtHide;               // a stormer standing on its hiding spot in the enemy end: its squad can storm
        int _hideTx, _hideTy;
        bool _hidePicked;

        internal bool HasTarget { get { return _tgtP != null || _tgtB != null || _tgtC != null; } }
        internal bool Moving { get { return _moving; } }
        internal bool AtTurret { get { return _tgtC != null; } }
        internal string GoalText { get { return _hasGoal ? _gtx + "," + _gty : "-"; } }

        /// <summary>A player's heal item on this bot (CTFXBots.HumanRepair): repairType 2 = energy, else health. Returns what it gave.</summary>
        internal int TakeHeal(ItemInfo.RepairItem item)
        {
            if (IsDead || item == null) return 0;
            float pct = item.repairPercentage > 100 ? item.repairPercentage / 1000f : item.repairPercentage / 100f;
            if (item.repairType == 2)
            {
                double before = _energy;
                _energy = Math.Min(_energyMax, _energy + item.repairAmount + _energyMax * pct);
                return (int)(_energy - before);
            }
            int max = _type.Hitpoints, h0 = _state.health;
            _state.health = (short)Math.Min(max, _state.health + item.repairAmount + (int)(max * pct));
            if (Slot != null) Slot.TkHealed += _state.health - h0;
            return _state.health - h0;
        }

        // Travis 10-20: no Teleport Disruptor on in the dropship ("it's toxic"). The server treats a bot's active gear as an anti-warp
        // source (checkBotAntiwarp), so a bot gearing up in the ship blocked players' warps there. Anti-warp gear stays off while the
        // body is in the ship (whatever put it back on - Setup, the utilities) and the TD goes back on as it leaves.
        bool _awOff;

        void ShipAntiWarp()
        {
            if (_activeEquip == null) return;
            if (InDropship && !IsDead)
            {
                for (int k = _activeEquip.Count - 1; k >= 0; k--)
                {
                    ItemInfo.UtilityItem u = _activeEquip[k];
                    if (u != null && u.antiWarpDistance > 0) _activeEquip.RemoveAt(k);
                }
                _awOff = true;
            }
            else if (_awOff)
            {
                _awOff = false;
                ItemInfo.UtilityItem td = TdItem();
                if (HasTd && td != null && !IsDead && !_activeEquip.Contains(td)) _activeEquip.Add(td);
            }
        }

        /// <summary>This body starts in the dropship: a moment to gear up (as a player at the store), then into the nearest portal.</summary>
        internal void EnterDropship(int now)
        {
            InDropship = true;
            _shipSince = now;
            MarkX = _state.positionX; MarkY = _state.positionY;                 // the beacon / blink mark, set in the safety of the ship
            if (now - Mgr.OpeningUntil < 0)
            {   // a game start (Travis 10-13: originally not everyone left the dropship at once; it could take up to 3 minutes,
                // staggered, while the first ones dash for the flags): a quarter go at once, most within 1.5 min, some up to 3
                // (Travis 10-13 after watching: most should be moving after the first 30-60 s; only an occasional careful one waits long)
                double u = _rng.NextDouble(), late = 0.05 + 0.10 * Math.Max(0.0, Math.Min(1.0, ((Human != null ? Human.Restless : 1.0) - 0.6) / 0.9));
                // (Travis 10-13 again: still way too many in the ship) 70 % out within 8 s, most of the rest within 25 s, a few up to 60 s
                int ms = u < 0.70 ? 1000 + _rng.Next(7001) : u < 1.0 - late * 0.5 ? 8000 + _rng.Next(17001) : 25000 + _rng.Next(35001);
                // Travis 10-14: the brown fortress (and the secondary base) is fortified at once - its engineers go first
                // Travis 10-17: the opening order is engineers (every base that is set up), then infiltrators (flags, deep anchors for the
                // engineers' foothold), then everyone else
                if (Style == CTFXStyle.Engineer && Group != null && Group.HomeEnd != null)
                    ms = 300 + _rng.Next(900);
                else if (_class == SK_INFIL || Style == CTFXStyle.Stormer)
                    ms = 1800 + _rng.Next(2200);
                else ms += 3500;
                _shipReadyAt = now + ms;
                Mgr.OpeningWaits++;
            }
            else _shipReadyAt = now + (int)((1000 + _rng.Next(4001)) * (Human != null ? Human.Restless : 1.0));   // buys, reads chat (1-5 s)
            if (now - Mgr.OpeningUntil >= 0 && Style != CTFXStyle.Maser && _rng.NextDouble() < 0.03)
            { _shipReadyAt = now + 30000 + _rng.Next(60001); Mgr.AfkSpells++; }   // Travis 10-15: AFK in the ship for a while (population ebb)
        }

        /// <summary>Its goal tile, if it has one (CTFXCommand: teammates heading somewhere count as being there).</summary>
        internal bool GoalTile(out int tx, out int ty) { tx = _gtx; ty = _gty; return _hasGoal; }

        // The stormer's cloak: its class's own (Improved Cloak 43 for an Infiltrator, Cloaking Unit 5 for a SciOps) stays on in its
        // active gear, which every bot update carries, so clients draw it cloaked. Enemy bots see it only inside the cloak distance.
        ItemInfo.UtilityItem _cloak;
        internal bool Cloaked { get { return _cloak != null && !IsDead && (Mgr.Mini == null || _cloakOn); } }       // MiniTP: only while it is switched on
        internal int CloakRange { get { return _cloak != null ? Math.Max(48, _cloak.cloakDistance) : 0; } }

        internal static ItemInfo.UtilityItem CloakFor(int cls)
        {
            foreach (int id in new int[] { 43, 5 })
            {
                ItemInfo.UtilityItem u = AssetManager.Manager.getItemByID(id) as ItemInfo.UtilityItem;
                if (u != null && CTFXSkillLogic.Allowed(id, cls)) return u;
            }
            return null;
        }

        /// <summary>Fire-locked by an anti-effect: no item use, warps included.</summary>
        internal bool Stunned(int now) { return now - _antiFireUntil < 0; }

        internal void ForceGoal() { _hasGoal = false; }

        /// <summary>Where the bot is headed (px), false = no goal.</summary>
        internal bool GoalPx(out int x, out int y)
        {
            x = _gtx * 16 + 8; y = _gty * 16 + 8;
            return _hasGoal;
        }

        internal void NewHide() { _hidePicked = false; AtHide = false; _hasGoal = false; }

        static int now0() { return Environment.TickCount; }

        /// <summary>Stats: where a stormer is, where it hides, what it is doing.</summary>
        internal string StormerState()
        {
            return "at " + (_state.positionX >> 4) + "," + (_state.positionY >> 4) + (_hidePicked ? " hide " + _hideTx + "," + _hideTy : " no hide")
                + (_hasGoal ? " goal " + _gtx + "," + _gty : " no goal") + (AtHide ? " HIDDEN" : "") + (HasTarget ? " fighting" : "")
                + (_retreating ? " retreating" : "") + " route " + _rp.Count + "/" + _rpi
                + (_rpi < _rp.Count && Mgr.Routes != null ? " next " + (Mgr.Routes.X[_rp[_rpi]] >> 4) + "," + (Mgr.Routes.Y[_rp[_rpi]] >> 4) : "")
                + (now0() - _antiThrustUntil < 0 ? " STUNNED" : "");
        }

        /// <summary>Walk to tile (tx, ty) next (a defender pinned by anti-warp runs to the fight instead).</summary>
        internal void GoTo(int tx, int ty)
        {
            _gtx = tx; _gty = ty; _hasGoal = true; _arrived = false;
            CancelPath();
        }

        /// <summary>A warp moved this bot to (x, y) px: CTFXClientMove takes outside writes over; the old plan goes; clients see it now.</summary>
        internal int MarkX, MarkY;          // where it stood in the dropship as it left (a bountier beacon / blink mark)

        /// <summary>Back in the dropship after a beacon / blink return: it waits there, then walks out through a portal again.</summary>
        internal void BackToShip(int now, int waitMs)
        {
            InDropship = true;
            _shipSince = now;
            _shipReadyAt = now + waitMs;
            _state.health = (short)_type.Hitpoints;                        // the ship store heals and refills, as it does a player
            _stims = -1;                                                    // stims, energizers, medikits bought again (HealStep counts them)
            EnergyFull(now);
            if (_ammo != null) RefillAmmo();
            DropTarget();
            ForceGoal();
        }

        /// <summary>Hit by anything in the last ms milliseconds (a bot in a fight).</summary>
        internal bool RecentlyHit(int now, int ms) { return _hitAt != 0 && now - _hitAt < ms; }

        internal static float YawToPublic(double dx, double dy) { return YawTo(dx, dy); }

        /// <summary>Drop a mine of kit item id where its barrel points (a mine leaves at the barrel and stays there).</summary>
        internal bool LayMine(int now, int id, float yaw)
        {
            int i = KitIndex(id);
            return i >= 0 && Mgr.Shots.MayLay(this, id) && FireNow(now, i, yaw, 30.0);   // the item's live caps (CTFXShots.MayLay)
        }

        internal void Teleported(int x, int y, int now)
        {
            int ox = _state.positionX, oy = _state.positionY;   // where the screens that saw it last had it (CTFXBotManager.WarpExit)
            _state.positionX = (short)x; _state.positionY = (short)y;
            _state.velocityX = 0; _state.velocityY = 0;
            ClearRoute();
            CancelPath();
            _navActive = false; _stuckCount = 0; _stuckT = now; _stuckX = x; _stuckY = y;
            _mvX = x; _mvY = y; _mvTick = now;
            _pinT = now; _pinX = x; _pinY = y; _pinArmed = false; _pinUntil = now;
            _lkValid = false;
            if (_pursuing) EndPursuit();
            _hasGoal = false; AtHide = false;
            _itemUseID = 0;
            _state.updateNumber++;
            Helpers.Update_RouteBot(this);
            Mgr.BotRoutes++;
            _lastRoute = now;
            Mgr.WarpExit(this, ox, oy, now);
            if (Mini != null) Mgr.Mini.Warped(this, now);        // MiniTP: any warp (a summon, a teleport) re-syncs the phase to the area it landed in
        }

        /// <summary>The stormer's hiding spot in the enemy end, picked once per hide (CTFXBotManager.HideTile).</summary>
        bool HideSpot(string enemy, out int tx, out int ty)
        {
            if (!_hidePicked && Mgr.HideTile(enemy, _team, out _hideTx, out _hideTy)) _hidePicked = true;
            tx = _hideTx; ty = _hideTy;
            return _hidePicked;
        }

        // The player-list row (name + team uniform) belongs to the slot and outlives each body (CTFXBotManager.AnnounceSlot).

        /// <summary>Every removal ends here. While the bots run, a body whose slot lives on (the engine's vehicle reset at every game
        /// start, the engine timeouts) is taken out quietly instead and respawned under the same id, so its row never leaves the
        /// player list. Otherwise (StopAll, a retired slot) clients are told the vehicle is gone, and the slot's row goes first.</summary>
        public override void destroy(bool bRestoreBase, bool bRemove)
        {
            if (!bCondemned)
            {
                try { if (Mgr.TakeOverRemoval(this)) return; } catch (Exception) { }
                try { Mgr.VehicleGone(this); } catch (Exception) { }
            }
            base.destroy(bRestoreBase, bRemove);
        }

        /// <summary>A corpse lies in the world until its slot respawns, and ArenaBot.checkBotAntiwarp does not skip dead bots: the
        /// gear goes with the life, so a dead Teleport Disruptor carrier blocks no warps.</summary>
        public override void kill(Player killer, int weaponID)
        {
            if (_activeEquip != null) _activeEquip.Clear();
            base.kill(killer, weaponID);
        }

        // ---------------------------------------------------------------- energy
        // The zone runs EnergyShieldMode=1, which makes energy the shield: a hit's HP damage is scaled by 1 - energy/max after
        // Energy-type damage has drained energy, firing and warps cost the item's per-terrain energy, and energy comes back at the
        // body's rate plus the active utilities' (a cloak -180, a TD -60 ...), never while an anti-recharge hit lasts. The client's
        // rules: cfs-studio damageCalculation (calculateDamage, tickEnergyRegen), headlessBot (resolveEnergyMax), assets
        // (resolveItemTerrainEnergy). Teammates are sent the bot's energy (SC_PlayerTeamUpdate), as for a player.
        double _energy, _energyMax;
        int _energyAt;
        double _hpRem;                                  // sub-HP damage carried to the next hit (the client works in milli-HP)

        internal double Energy { get { return _energy; } }
        internal double EnergyMax { get { return _energyMax; } }

        double EnergyMaxNow()
        {
            int vm = _type.EnergyMax;
            double m = vm > 0 ? vm : vm < 0 ? Mgr.EnergyDefaultMax : 0;
            if (_activeEquip != null) foreach (ItemInfo.UtilityItem u in _activeEquip) if (u != null) m += u.extraCapacity;
            return m > 0 ? m : 0;
        }

        int EnergyRateNow()
        {
            int r = _type.EnergyRate < 0 ? Mgr.EnergyDefaultRate : _type.EnergyRate;   // thousandths of energy per 10 ms
            if (_activeEquip != null) foreach (ItemInfo.UtilityItem u in _activeEquip) if (u != null) r += u.rechargeRate;
            return r;
        }

        /// <summary>A new body starts full.</summary>
        void EnergyFull(int now)
        {
            _energyMax = EnergyMaxNow();
            _energy = _energyMax;
            _energyAt = now;
            _hpRem = 0;
            _state.energy = (short)Math.Round(_energy);
            _energyPubAt = now + 900 + _rng.Next(201);
        }

        void EnergyTick(int now)
        {
            int dt = now - _energyAt;
            _energyAt = now;
            if (dt <= 0) return;
            if (dt > 1000) dt = 1000;
            _energyMax = EnergyMaxNow();
            int rate = EnergyRateNow();
            if (rate > 0 && now - _antiRechargeUntil < 0) rate = 0;
            _energy += rate / 1000.0 * (dt / 10.0);
            if (_energy > _energyMax) _energy = _energyMax;
            if (_energy < 0) _energy = 0;
            _state.energy = (short)Math.Round(_energy);           // live on every update (10-15 night: the 1 s publish flickered)
        }

        /// <summary>The energy <paramref name="itemId"/> costs where the bot stands; -1 = it can't be used here.</summary>
        double EnergyCost(int itemId)
        {
            int t;
            try { t = _arena.getTerrainID(_state.positionX, _state.positionY); } catch (Exception) { t = 0; }
            return CTFXBotManager.TerrainEnergy(itemId, t, _energyMax, _energy);
        }

        internal bool CanPay(int itemId) { return CanAfford(itemId); }     // 11-11: the MiniTP engineer asks before a rebuild

        bool CanAfford(int itemId)
        {
            double c = EnergyCost(itemId);
            return c >= 0 && _energy >= c;
        }

        /// <summary>Pays for <paramref name="itemId"/> if the bot can; false = not here or not enough energy.</summary>
        internal bool SpendEnergy(int itemId)
        {
            double c = EnergyCost(itemId);
            if (c < 0 || _energy < c) return false;
            _energy -= c;
            return true;
        }

        internal void Setup(CTFXSlot slot, int kitId, int cls)
        {
            Slot = slot;
            if (slot.Human == null) slot.Human = CTFXHuman.Roll(Mgr.Rng, Mgr.NameFor(slot));      // Travis 10-22: seeded by the callsign, the same player every boot
            if (slot.Style == CTFXStyle.Bountier) slot.Human.MakeElite(new Random(slot.Human.Seed ^ 0x5bd1e995));      // bountiers are the zone sharpest
            Human = slot.Human;
            Group = slot.Group;
            Style = slot.Style;
            _class = cls > 0 ? cls : BodyClass(_type.Id);
            _kitId = kitId;
            _kit = LegalKit(kitId, _class, Mgr);
            CTFXShop.Build shopB = CTFXShop.Of(kitId);
            KitName = shopB != null ? shopB.Name : ClassName(_class) + " kit " + kitId;
            _wallRange = 0;
            foreach (CTFXWep w in _kit) _wallRange = Math.Max(_wallRange, CTFXShop.WallReach(w.Id));
            LifeStart = Environment.TickCount; LastHitItem = 0;
            EsSpawn();
            if (Mgr.Usage != null) Mgr.Usage.Life(this, KitName, _kit);
            // Teleport Disruptor: a class whose skill logic may buy it carries it, switched on (Travis 10-11) - only its veterans and aces
            // since 10-25 ("too many bots with td, we need to considerably shrink them": a third of the bots blocked warps, ~32 of 100;
            // tdTiers in CTFXBots.txt). The server treats a bot's active gear as an anti-warp source for enemy players near it
            // (ArenaBot.checkBotAntiwarp; TD = 512 px).
            ItemInfo.UtilityItem td = TdItem();
            CTFXHuman tdh = slot.Human;
            // MiniTP (Travis 11-07: "do not make it perfect, sometimes yes depending on skill tiers"): whether a life owns a TD is a tier chance, and it starts OFF -
            // the bot switches it on when it thinks of it and pays the energy for as long as it stays on (TdStep)
            HasTd = td != null && CTFXSkillLogic.Allowed(td.id, _class) && tdh != null && tdh.Tier != null && (Mgr.Mini != null ? Mgr.Mini.Owns(Mgr.Mini.TdOwn(tdh.Tier)) : TdTiers.Contains(tdh.Tier));
            _tdOn = false; _tdFight = false; _tdForgot = false; _tdQuietSince = 0;
            if (HasTd && Mgr.Mini == null && _activeEquip != null && !_activeEquip.Contains(td)) _activeEquip.Add(td);
            _cloakOn = false;
            if (Mgr.Mini != null)
            {   // MiniTP (Travis 11-08): the cloak is the one its build buys (the Infiltrator's); it starts OFF and CloakStep switches it by the situation
                _cloak = shopB != null && shopB.CloakId != 0 ? Util(shopB.CloakId) : null;
            }
            else
            {
                _cloak = Style == CTFXStyle.Stormer || (Style == CTFXStyle.Bountier && (_class == SK_SCIOPS || _class == SK_INFIL)) ? CloakFor(_class) : null;
                if (_cloak != null && _activeEquip != null && !_activeEquip.Contains(_cloak)) _activeEquip.Add(_cloak);
            }
            UtilSpawn(Environment.TickCount);                                  // armour, generator, energy item (CTFXUtilities.cs)
            EnergyFull(Environment.TickCount);
            switch (Style)
            {
                // _jit = the aim error in yaw units (1.5 deg each), rolled again after every shot: with the flight sim it IS the accuracy
                case CTFXStyle.Post: _engage = 650; _jit = 1; break;
                case CTFXStyle.Guard: _engage = 450; _jit = 2; _minR = 90; _maxR = 260; break;
                case CTFXStyle.Assault: _engage = 450; _jit = 2; _minR = 100; _maxR = 280; break;
                case CTFXStyle.Roam: _engage = 500; _jit = 2; _minR = 100; _maxR = 300; break;
                case CTFXStyle.Sniper: _engage = 700; _jit = 1; break;
                case CTFXStyle.Drain: _engage = 450; _jit = 2; _minR = 60; _maxR = 170; break;
                case CTFXStyle.Stormer: _engage = 0; _jit = 2; _minR = 80; _maxR = 220; break;         // sneaking in: fights nothing until hidden (Think)
                case CTFXStyle.Engineer: _engage = 420; _jit = 2; _minR = 90; _maxR = 240; break;
                default: _engage = 450; _jit = 3; _minR = 100; _maxR = 280; _hyst = 0.9; break;   // adhd
            }
            int now = Environment.TickCount;
            LastPollTick = now;
            _nextScan = now + _rng.Next(500);
            RollPersonality();
            _nextStrafe = now + _pStrafeLo + _rng.Next(_pStrafeHi - _pStrafeLo + 1);
            _strafeHold = false;
            _adhdNext = now + 20000 + _rng.Next(20001);
            _scanT0 = now;
            _scanBase = _state.yaw;
            _lastWepSwitch = now - 1000;
            _retreatCool = now - 1; _relocCool = now - 1; _noPathUntil = now - 1; _lastReq = now - 5000;
            _mvTick = now; _mvX = _state.positionX; _mvY = _state.positionY;
            _jitTick = now - 1000;
            _lastRoute = now;
            _pinT = now; _pinX = _state.positionX; _pinY = _state.positionY; _pinUntil = now; _pinArmed = false; _pinDir = 0;
            int kn = _kit.Length;
            _prof = new CTFXFireProfile[kn]; _readyAt = new int[kn]; _clipLeft = new int[kn];
            _reloadUntil = new int[kn]; _reloading = new bool[kn];
            for (int i = 0; i < kn; i++)
            {
                _prof[i] = GetProfile(_kit[i].Id);
                _clipLeft[i] = _prof[i].Clip;
                _readyAt[i] = now - 1;
            }
            _lastShotIdx = -1; _lastShotTick = now;
            SetupAmmo();
            _antiThrustUntil = now; _antiRotateUntil = now; _antiFireUntil = now; _antiRechargeUntil = now;
            _fmPhase = FM_FREE; _lkValid = false; _pursuing = false; _pursueCool = now - 1;
            _door =(Mgr.Doors != null && Mgr.Doors.Ready) ? new CTFXDoorPilot() : null;
            LogFireModes(Mgr);
            _ready = true;
        }

        /// <summary>
        /// This life's personality (Travis 10-11: the bots moved too much alike): its own combat range (75-130% of the style's band),
        /// strafe rhythm, how often it holds instead of strafing, how fast it reacts to a new target, and for squad members its own
        /// formation slot (distance and angle off the leader's facing) with a personal wander and facing.
        /// </summary>
        void RollPersonality()
        {
            Random r = Human != null ? new Random(Human.Seed + 7) : _rng;   // the seat's habits, the same every life
            double range = 0.75 + r.NextDouble() * 0.55;
            if (_minR > 0) _minR = (int)(_minR * range);
            if (_maxR > 0) _maxR = (int)(_maxR * range);
            _pStrafeLo = 500 + r.Next(701);                         // 500-1200 ms
            _pStrafeHi = _pStrafeLo + 700 + r.Next(1601);           // +700-2300 ms
            _pHold = 0.12 + r.NextDouble() * 0.38;                  // 12-50 % of the periods hold still
            _pPedal = 0.45 + r.NextDouble() * 0.35;                 // 45-80 % of the in-range periods step in or out while strafing
            _pScanLo = 220 + r.Next(231);                           // re-looks for targets every 220-450 ms
            _pScanSpan = 80 + r.Next(221);                          // + 80-300 ms
            _pFollowR = 70 + r.Next(151);                           // 70-220 px from its leader
            int k = Slot != null ? Slot.StyleIndex : 0;             // flanks first, then the rear: 60 / -60 / 95 / -95 / 125 / -125 deg ...
            float[] slots = new float[] { 40f, -40f, 63f, -63f, 83f, -83f, 120f };
            _pFollowAng = WrapYaw(slots[k % slots.Length] + (float)(r.NextDouble() * 16.0 - 8.0));
            _pWanderMs = 1200 + r.Next(2801);
            _pFace = (float)(r.NextDouble() * 24.0 - 12.0);
            if (Style == CTFXStyle.Bountier)
            {   // evasive: quick strafe flips, never standing in the open, always pedalling the range
                _pStrafeLo = 250 + r.Next(251); _pStrafeHi = _pStrafeLo + 300 + r.Next(501);
                _pHold = 0.0; _pPedal = 0.85;
            }
        }

        /// <summary>
        /// An engineer within 40 px of its job's tile builds: it holds the spot through the maker's prefire (1.5 s; it may shoot
        /// meanwhile, as a player can), then the turret appears (the limits are checked at that moment). Pushed or walked off the
        /// spot, the build starts over. The maker's fire delay (2.5 s) separates two builds.
        /// </summary>
        void BuildStep(int now, int px, int py)
        {
            if (_job == null) return;
            double jx = _job.Tx * 16 + 8 - px, jy = _job.Ty * 16 + 8 - py;
            // 56 px: beyond Nav's 48 px arrival, so an engineer that stops at the edge of it still builds (10-12: one stood 2 min
            // 41-48 px from its spot, arrived but never in reach)
            if (jx * jx + jy * jy > 56.0 * 56.0) { _buildAt = 0; return; }
            if (_buildAt == 0) { _buildAt = Math.Max(now, _nextBuildAt) + 1500; return; }
            if (now - _buildAt < 0) return;
            Mgr.Turrets.TryBuild(this, _job, now);
            _job = null; _buildAt = 0; _nextBuildAt = now + 2500;
            _hasGoal = false;
        }

        /// <summary>Called by the manager when the bot leaves the world: drop every reference.</summary>
        internal void Release()
        {
            lock (_pathLock) { Released = true; _pendList = null; _pendReady = false; _pathSeq++; }
            if (_inFlight) { _inFlight = false; Mgr.EndPath(false); }
            _tgtP = null; _tgtB = null; LastHitBy = null; Slot = null; _pxs = null; _pys = null;
        }

        // ================================================================ poll
        public override bool poll()
        {
            if (IsDead || Released)
            {
                ReleaseKeys();                      // a corpse presses nothing
                return false;                       // never base.poll() a corpse (fact c)
            }
            long t0 = Stopwatch.GetTimestamp();
            int now = Environment.TickCount;
            LastPollTick = now;
            EnergyTick(now);
            ShipAntiWarp();
            if (now - _healTick >= 250) { _healTick = now; try { HealStep(now); EsStep(now); UtilStep(now); if (Mini != null) { TdStep(now); CloakStep(now); } } catch (Exception ex) { Fault(ex); } }
            _driven = false;
            _doorSteer = false;
            if (_ready && _cm != null && _weapon != null)
            {
                try { Think(now); }
                catch (Exception ex) { Fault(ex); }
            }
            if (!_driven) ReleaseKeys();            // idle (not set up yet) or a brain fault before Drive: no key stays down
            bool r = base.poll();
            if (_itemUseID != 0)
            {
                // The shot leaves from the state the clients are sent right now, and flies on the server as they draw it: its damage
                // lands when and where it touches a body (CTFXShots), never at the moment of firing.
                try { Mgr.Shots.Fire(this, _itemUseID, _shotRange); }
                catch (Exception ex) { Fault(ex); }
                _itemUseID = Mgr.HalloweenItem(_itemUseID);     // *halloween: clients draw the skin; the server flies the stock shot (same stats)
                if (Mgr.SelfRoute)
                {   // routed here: the local engine erases a bot shot unless its own chosen player target is in sight, and a shot no
                    // client sees must not land either; it goes to the players it can reach (CTFXBots per-player routing)
                    if (Slot == null || Slot.Entry == null || !Mgr.RouteSelf(this, (short)_itemUseID))
                    {
                        _state.updateNumber++;
                        Helpers.Update_RouteBot(this);
                    }
                    Mgr.BotRoutes++;
                    _itemUseID = 0;
                    r = false;
                    _lastRoute = now;
                }
            }
            // updateMs knob: route the state more often than the engine does (its own cadence is 200 ms = 5 Hz). At 200 or more
            // nothing is added, so the default sends no extra packets.
            if (now - _histT[(_histI + 15) & 15] >= 40)
            {
                _histX[_histI] = _state.positionX; _histY[_histI] = _state.positionY; _histT[_histI] = now; _histI = (_histI + 1) & 15;
            }
            // updateNearMs: a bot on a human's screen (player or spectator, Mgr.NearHuman) routes faster; the rest stay at updateMs
            int um = Mgr.UpdateMs;
            if (Mgr.UpdateNearMs < um && Mgr.Fast(_id)) um = Mgr.UpdateNearMs;
            if (_routeNow) { _routeNow = false; if (!r) { r = true; Mgr.StunRoutes++; } }
            else if (r && Mgr.UpdateFarMs > 200)
            {   // nobody can see it: route at the slower far cadence (2.5 Hz, beyond 2500 px 1.25 Hz)
                int fm = Mgr.FarMs(_state.positionX, _state.positionY);
                if (fm > 0 && now - _lastRoute < fm) { r = false; Mgr.FarSkips++; }
            }
            if (!r && um < 200 && now - _lastRoute >= um) r = true;
            // a shot fired this poll is always routed now: the live engine clears _itemUseID only when it routes the bot, so a far
            // skip (or a >600 ms hitch) with the shot still pending re-fired it every arena tick (80-160 server shots per trigger)
            if (!r && _itemUseID != 0) r = true;
            if (r) { _lastRoute = now; Mgr.BotRoutes++; }      // true = the arena routes this bot's state now
            if (r && _itemUseID == 0 && Slot != null && Slot.Entry != null && Mgr.RouteSelf(this))
                r = false;                                      // routed here with its row: the packets carry its bounty (CTFXBots.RouteSelf)
            Mgr.PollTicks += Stopwatch.GetTimestamp() - t0;
            Mgr.PollCount++;
            return r;
        }

        void Fault(Exception ex)
        {
            _faults++;
            string st = ex.StackTrace;
            int nl = st == null ? -1 : st.IndexOf('\n');
            Mgr.WarnOnce("bot brain error: " + ex.GetType().Name + " " + ex.Message + " @ " + (st == null ? "" : (nl > 0 ? st.Substring(0, nl) : st).Trim()));
            if (_faults == 25) Mgr.RequestRecycle(this);
        }

        // ================================================================ the brain
        void Think(int now)
        {
            int px = _state.positionX, py = _state.positionY;
            _why = null;
            TrackHotSpot(now);
            if (Style == CTFXStyle.Stormer)
            {   // on its hiding spot it holds and fights back; on the way in it fights nothing and keeps walking (cloaked)
                int hx = _hideTx * 16 + 8 - px, hy = _hideTy * 16 + 8 - py;
                AtHide = _hidePicked && hx * hx + hy * hy <= 56 * 56;
                bool stormed = Group != null && Group.Storm != null && Group.Storm.Phase == CTFXStorm.Hold;
                _engage = stormed ? 450 : AtHide ? 320 : 0;                 // after the storm it fights with its squad (Travis 10-14)
            }
            if (_rebuying && now - _rebuyUntil >= 0)
            {   // Travis 10-14: spent consumables stay spent until bought again (the dropship), so with the commander on there is no
                // on-the-spot refill: the bot is out of that ammo until a pile, a teammate's drop, a recall or its next life
                if (Mgr.Cmdr != null && Mgr.Cmdr.On) { _rebuyUntil = now + 3600000; Mgr.Cmdr.DryOn++; }   // stays dry (AddAmmo / BackToShip end it)
                else RefillAmmo();
            }
            if (_stunProbeT != 0 && now - _stunProbeT >= 0)
            {
                int mx = px - _stunProbeX, my = py - _stunProbeY;
                Mgr.WarnOnce("stun #" + _stunProbeN + " check: moving " + _stunProbeV + " px/s when hit, moved " + (int)Math.Sqrt(mx * mx + my * my)
                    + " px in the 2 s after (thrust lock " + Math.Max(0, _antiThrustUntil - now) + " ms left)");
                _stunProbeT = 0;
            }

            if (now - _travelAt >= 0) { _travelAt = now + 500; _traveling = Traveling(px, py); }
            if (now - _mvTick >= 250)
            {
                _moving = Math.Abs(px - _mvX) + Math.Abs(py - _mvY) > 6;
                _mvX = px; _mvY = py; _mvTick = now;
            }
            if (_curWep < 0) SelectWeapon(now, 200.0, true);
            if (Mgr.Ammo && !_rebuying && _curWep >= 0 && Dry(_curWep))
            {
                if (Mgr.Usage != null) Mgr.Usage.Refuse(_kit[_curWep].Id, 2);
                if (AllDry()) StartRebuy(now);
                else SelectWeapon(now, 200.0, true);                         // out of this ammo: take a weapon that still has some
            }

            _carrying = (Mgr.Flags != null && Mgr.Flags.Carrying(this)) || (Mini != null && Mgr.Mini != null && Mgr.Mini.Carrying(this)) || (Mini != null && Mini.Dig == 4 && OnPack);      // 11-11: a mining engineer on its board runs past fights      // 11-11: MiniTP carriers play as carriers
            if ((Mgr.Cmdr != null && Mgr.Cmdr.On) || Mini != null) OddsStep(now, px, py);       // MiniTP has no commander but the same odds: outnumbered -> back off + call for help (Travis 11-08)
            DemoStep(now, px, py);
            PackStep(now, px, py);                                                // movement packs on long trips (CTFXPacks.cs, packs=1)                                                // the elite Heavy's Demo Pack (CTFXCombos.cs)       // outnumbered: back off, call for help (CTFXOdds.cs)
            // ---- target
            bool has = TargetValid(now, px, py);
            if (InDropship)
            {   // nobody fights in the dropship
                _tgtP = null; _tgtB = null; _tgtC = null; _pendAt = 0; has = false;
                _nextScan = now + 300;
            }
            if (now - _nextScan >= 0)
            {
                ScanTargets(now, px, py);
                has = _tgtP != null || _tgtB != null || _tgtC != null;
            }
            if (_pendAt != 0 && now - _pendAt >= 0)
            {
                CommitPending(now, px, py);
                has = _tgtP != null || _tgtB != null || _tgtC != null;
            }
            BuildStep(now, px, py);                                 // an engineer on its spot builds, fighting or not
            double dx = 0, dy = 0, dist = 0;
            float aimYaw = _state.yaw;
            bool los = false;
            Helpers.ObjectState ts = null;
            if (has)
            {
                ts = _tgtP != null ? _tgtP._state : (_tgtB != null ? _tgtB._state : _tgtC._state);
                int tid = _tgtP != null ? (int)_tgtP._id : (_tgtB != null ? 100000 + (int)_tgtB._id : 300000 + (int)_tgtC._id);
                dx = ts.positionX - px; dy = ts.positionY - py;
                dist = Math.Sqrt(dx * dx + dy * dy);
                _wdist = Math.Sqrt(dx * dx + dy * dy / 0.49);
                los = CachedLos(_losT, now, tid, px, py, ts.positionX, ts.positionY);
                if (!los && _wallRange > 0 && dist <= _wallRange) los = true;   // the maser burst goes through the wall
                if (los)
                {
                    _tgtLastLos = now;
                    if (_pursuing) EndPursuit();                    // found it again: back to normal combat
                    _lkValid = true; _lkX = ts.positionX; _lkY = ts.positionY; _lkTick = now;
                }
                if (_comboPick) { _comboPick = false; SelectWeapon(now, dist, true); }   // a combo step is due: past the switch locks
                else SelectWeapon(now, dist, false);
                if (_weapon.bEquipped)
                {
                    if (now - _jitTick > 500) RollJitter(now);
                    aimYaw = HumanAim(now, ts, dx, dy);
                }
                else aimYaw = YawTo(dx, dy);
            }

            // ---- retreat (not post): low health with an enemy close
            if (_retreating)
            {
                if (now - _retreatUntil >= 0) { _retreating = false; _retreatCool = now + 1500; _hasGoal = false; CancelPath(); }
            }
            else if (has && Style != CTFXStyle.Post && dist <= 250.0 && now - _retreatCool >= 0 && (_state.health < RetreatAt() * _type.Hitpoints || Outnumbered(now)))
            {
                _retreating = true;
                _retreatUntil = now + 3000 + _rng.Next(2001);
                if (_pursuing) EndPursuit();
                if (Mgr.Cmdr != null && Mgr.Cmdr.FootRetreat(this, out _rtx, out _rty)) { }   // a foothold backs off a short way only
                else if (_preCover) { _rtx = _pcx; _rty = _pcy; _preCover = false; }
                else if (!MedicGoal(out _rtx, out _rty) && !NearbyCover(px, py, out _rtx, out _rty)) Mgr.RetreatGoal(Group, out _rtx, out _rty, this);
                CancelPath();
            }
            // ---- sniper: an enemy inside 200 px means find a new overwatch tile
            if (Style == CTFXStyle.Sniper)
            {
                if (_relocating && now - _relocUntil >= 0) _relocating = false;
                if (has && dist < 200.0 && !_relocating && now - _relocCool >= 0)
                {
                    _relocating = true; _relocUntil = now + 6000; _relocCool = now + 9000;
                    _hasGoal = false;
                }
            }

            // ---- fire mode: stand / scoot weapons hold still for their prefire and only shoot from a stand
            // (retreat and relocation bypass it, and so does a target out of sight)
            bool losHold = los || (has && _lkValid && now - _tgtLastLos < 400);        // 11-08 jitter: a sight line that flickers for a beat round a corner keeps the mode (and the stand / prefire progress)
            int fm = FireModeStep(now, px, py, has && losHold && !_retreating && !_relocating && _weapon.bEquipped);

            // ---- movement
            bool move, combat = false;
            float moveYaw = 0f, faceYaw = _state.yaw, hold;
            if (has)
            {
                faceYaw = aimYaw;
                if (_carrying || (_traveling && !_retreating && RunLatched(now, px, py, dist)))
                {   // a flag carrier keeps running home and shoots on the move (Travis: flaggers run, they do not stand and trade); so does
                    // anyone on its way to an objective, unless the enemy is right on it (Travis: favour getting away over spawn fights)
                    float hold2;
                    move = Nav(now, out moveYaw, out hold2);
                }
                else if (_retreating) move = PathMove(now, px, py, _rtx, _rty, out moveYaw);
                else if (_relocating)
                {
                    move = Nav(now, out moveYaw, out hold);
                    if (!move) _relocating = false;
                }
                else if (!losHold && PursueMove(now, px, py, out moveYaw))
                {
                    move = true;                    // lost sight of the target: walk to where it was last seen
                    faceYaw = moveYaw;
                }
                else
                {
                    move = _job != null && _buildAt != 0 ? false : CombatMove(now, px, py, dx, dy, dist, aimYaw, out moveYaw);
                    _navActive = false;
                    combat = true;
                    if (fm == FMR_HOLD || fm == FMR_FIRE) move = false;           // standing for the prefire / the burst (facing kept)
                    else if (fm == FMR_MOVE && !move)                             // between volleys even a holding sniper strafes
                        move = StrafeYaw(now, px, py, aimYaw, out moveYaw);
                }
            }
            else
            {
                if (_retreating) { move = PathMove(now, px, py, _rtx, _rty, out moveYaw); hold = _state.yaw; }
                else if (PursueMove(now, px, py, out moveYaw)) { move = true; hold = _state.yaw; }
                else move = Nav(now, out moveYaw, out hold);
                faceYaw = move ? moveYaw : hold;
            }
            if (move && !has && now - _healHoldUntil < 0) move = false;           // the medic stands still for the medikit's prefire (movement cancels it)
            if (!move) _navActive = false;
            Why = move ? "move" : has ? "fight" : _retreating ? "retreat" : _rebuying ? "rebuy" : now - _antiThrustUntil < 0 ? "stunned" : _why ?? "other";

            // pinned against geometry in combat: back out toward the most open direction (outside combat the window just restarts)
            if (combat) PinnedStep(now, px, py, ref move, ref moveYaw);
            else { _pinT = now; _pinX = px; _pinY = py; _pinArmed = false; _pinUntil = now; }

            if (move && now - _unstickUntil < 0)
            {   // backing out of a pin toward open space (NavMoving)
                moveYaw = _unstickYaw;
                if (!has) faceYaw = moveYaw;
            }
            if (move && _doorSteer) faceYaw = moveYaw;          // crossing a door: run straight through, facing the way we go
            if (!_doorSteer && !(_job != null && _buildAt != 0) && !_rebuying)
                DodgeAdjust(now, px, py, _carrying || _retreating || (_traveling && !combat), has && (fm == FMR_HOLD || fm == FMR_FIRE), ref move, ref moveYaw);
            if (!has && now - _turnUntil < 0) faceYaw = _turnTo;               // the turn FireNow asked for (rotate keys, never a snap)
            Drive(move, moveYaw, faceYaw);
            if (!has && !move && !InDropship && !_rebuying && now - _suppNext >= 0 && (Mgr.Cmdr != null || Mini != null))
            {   // a line holder with nothing in sight: bullets down the choke the enemy has to come through (CTFXLines.cs; MiniTP: CTFXMini.Suppress, the ladder level beside it)
                _suppNext = now + 500 + _rng.Next(600);
                int sx, sy;
                if (Mini != null) { if (Mgr.Mini.Suppress(this, now, out sx, out sy)) { Mgr.Mini.SuppressTried++; if (SuppressFire(now, sx, sy)) Mgr.Mini.Suppressions++; } }
                else if (Mgr.Cmdr.Suppress(this, now, out sx, out sy) && SuppressFire(now, sx, sy)) Mgr.Cmdr.Suppressions++;
            }

            // ---- fire (our own clip / interval / reload bookkeeping; stand and scoot weapons only shoot from a stand). At a bot the
            // nose settles inside the turn dead zone (3 units, Drive) and lines up on the aim for the shot itself: the flight decides
            // the hit (CTFXShots), so the style's jitter is the whole aim error. At a player the engine's 5-unit window stays.
            if (!InDropship && RfgStep(now, px, py)) { }                       // the repulsor field, when truly under threat (CTFXRfg.cs)
            bool atBot = _tgtB != null || _tgtC != null;
            if (has) Mgr.HoldSample(!los ? 0 : _curWep < 0 || !_weapon.bEquipped ? 1 : !(fm == FMR_FREE || fm == FMR_FIRE) ? 2
                : !CanShoot(now, _curWep) ? 3 : !LobOk(_curWep) ? 4
                : !AimOn(atBot, aimYaw, dist) ? 5
                : !(_firstShotAt == 0 || now - _firstShotAt >= 0) ? 6 : !EnergyForShotPeek() ? 7 : 8);
            // out of energy for every weapon with an enemy in sight: back off a moment to recharge, as a player does, instead of standing
            // there facing it (Travis 10-13 "indecision churn": 21 % of the time a bot held a target it could not afford to shoot)
            if (has && !AnyUsableHere())
            {
                DropTarget();                                                   // nothing it carries fires here: it carries on
                _nextScan = now + 1500;
                Mgr.NoFireHere++;
                if (Mgr.Usage != null && _curWep >= 0) Mgr.Usage.Refuse(_kit[_curWep].Id, 1);
            }
            else if (has && PanicStep(now, px, py, dist, aimYaw)) { }                // repulsor shove / disruptor trap fired this poll
            else if (has && _curWep >= 0 && !EnergyForShotPeek() && AnyAffordable())
                SelectWeapon(now, dist, true);                                  // this one costs more than it has: another one now
            else if (has && los && !_retreating && Style != CTFXStyle.Post && _curWep >= 0 && !EnergyForShotPeek()
                && now - _retreatCool >= 0)
            {
                _retreating = true;
                _retreatUntil = now + 1200 + _rng.Next(1801);
                Mgr.RetreatGoal(Group, out _rtx, out _rty, this);
                CancelPath();
                Mgr.RechargeBacks++;
            }
            if (has) FireBlock = !los ? "no LOS" : _curWep < 0 ? "no weapon" : !_weapon.bEquipped ? "not equipped" : !(fm == FMR_FREE || fm == FMR_FIRE) ? "fire mode " + fm
                : !CanShoot(now, _curWep) ? "interval/reload" : !LobOk(_curWep) ? "lob range" : !MeleeReach(_curWep, dist) ? "out of reach " + (int)dist
                : !AimOn(atBot, aimYaw, dist) ? "aiming" : (_firstShotAt != 0 && now - _firstShotAt < 0) ? "settle" : !PrefireDone(now) ? "prefire" : !EnergyForShotPeek() ? "energy" : "fired";
            else FireBlock = "no target";
            if (has && los && _curWep >= 0 && _weapon.bEquipped && (fm == FMR_FIRE || (fm == FMR_FREE && !StandWeapon())) && CanShoot(now, _curWep)
                && LobOk(_curWep) && MeleeReach(_curWep, dist) && AimOn(atBot, aimYaw, dist)
                && _rfgAt != now && (_firstShotAt == 0 || now - _firstShotAt >= 0) && !Mgr.Bounty.HoldFire(this, now) && PrefireDone(now) && EnergyForShotPeek() && EnergyForShot(now))
            {
                _pfStart = 0;
                // (Travis 11-08: "they should never be able to bypass the rotation ... they should be pressing their rotate key to face someone always, never cheating": the old line here set the
                // yaw to the exact aim at the moment of the shot; now the shot leaves along the nose the rotate keys have actually brought round, which AimOn has already held to a few units)
                _itemUseID = (short)_kit[_curWep].Id;    // the configured top-level item (MultiUse) so clients draw its real pattern
                _shotRange = dist;
                if (_tgtC != null) Mgr.Turrets.ShotsAtTurrets++;
                ShotFired(now, _curWep);
                ComboShot(now, _curWep);
                if (fm == FMR_FIRE) _fmShots--;
                RollJitter(now);
            }
        }

        /// <summary>The shot's energy (the top-level item's, per terrain), paid now; refused while it can't be afforded.</summary>
        bool EnergyForShot(int now)
        {
            if (SpendEnergy(_kit[_curWep].Id)) return true;
            if (now - _lastEnergyNote >= 1000) { _lastEnergyNote = now; Mgr.EnergyRefusals++; if (Mgr.Usage != null) Mgr.Usage.Refuse(_kit[_curWep].Id, 0); }
            return false;
        }
        int _lastEnergyNote;

        // ---------------------------------------------------------------- defensive tools (Travis 10-13: favour getting away; repulsor
        // fields used defensively; an infiltrator disrupts next to a wall so the splash stuns whoever chases it)
        int _panicNext;

        int KitIndex(int id)
        {
            if (_kit == null) return -1;
            for (int i = 0; i < _kit.Length; i++) if (_kit[i].Id == id && (_badMask & (1 << i)) == 0 && !Dry(i)) return i;
            return -1;
        }

        /// <summary>Fire kit item i now toward yaw (yaw units), the shot landing at range px; false = it could not (cooldown, energy).</summary>
        float _turnTo; int _turnUntil;                                   // a turn the bot has asked for (FireNow): Drive faces it while nothing else holds its eyes
        bool FireNow(int now, int i, float yaw, double range)
        {
            if (i < 0 || !CanShoot(now, i)) return false;
            // Travis 11-08: a bot whose target died whipped round to the next thing it wanted to shoot (suppression fire, a repulsor throw, a trap) because this set the facing to the aim
            // at once. A human turns with the rotate keys: the bot now asks Drive to turn toward the aim (a pending turn, 0.6 s) and fires on a later poll, once the nose is within 5 units.
            if (Math.Abs(YawDelta(yaw, _state.yaw)) > 5f)
            {
                _turnTo = yaw; _turnUntil = now + 600;
                return false;
            }
            if (!SpendEnergy(_kit[i].Id)) return false;
            if (i != _curWep) { if (!EquipIdx(i)) return false; _curWep = i; _fmPhase = FM_FREE; _lastWepSwitch = now; }
            _itemUseID = (short)_kit[i].Id;
            _shotRange = range;
            ShotFired(now, i);
            return true;
        }

        /// <summary>
        /// Repulsor: an enemy within 110 px while it is hurt, carrying a flag or on its way somewhere: thrown at the enemy, the blast
        /// shoves it off. Disruptor trap (Infiltrator): an enemy 60-260 px behind it while it runs: the disruptor into a wall beside
        /// the chaser, the splash stuns it. One tool every 4-7 s.
        /// </summary>
        internal bool CanRepulse(int now)
        {
            if (now - _panicNext < 0 || now - _antiFireUntil < 0 || IsDead || InDropship) return false;
            int i = KitIndex(Repulsor);
            return i >= 0 && CanShoot(now, i) && EnergyCost(Repulsor) <= _energy;
        }

        /// <summary>A turret guard: the repulsor thrown at the turret (x, y) so its burst shoves the incoming shots off it.</summary>
        internal bool GuardRepulse(int now, int x, int y)
        {
            int i = KitIndex(Repulsor);
            double ex = x - _state.positionX, ey = y - _state.positionY;
            if (!FireNow(now, i, YawTo(ex, ey), Math.Max(40.0, Math.Sqrt(ex * ex + ey * ey)))) return false;
            _panicNext = now + 5000 + _rng.Next(3001);
            return true;
        }

        /// <summary>A hunt pack runner: its Disruptor straight at the victim (x, y), 60-260 px off, so the splash stuns it for the killer.</summary>
        internal bool RunnerDisrupt(int now, int x, int y)
        {
            if (now - _panicNext < 0 || now - _antiFireUntil < 0 || IsDead || InDropship) return false;
            int di = KitIndex(Disruptor);
            if (di < 0 || !CanShoot(now, di) || EnergyCost(Disruptor) > _energy) return false;
            double ex = x - _state.positionX, ey = y - _state.positionY, d = Math.Sqrt(ex * ex + ey * ey);
            if (d < 60.0 || d > 260.0 || !Mgr.Los(_state.positionX, _state.positionY, x, y)) return false;
            if (!WallBeside(x, y)) return false;                       // 10-17: it passes through the victim; no wall beside them, no stun
            if (!FireNow(now, di, YawTo(ex, ey), d)) return false;
            _panicNext = now + 3000 + _rng.Next(2001);
            return true;
        }

        bool PanicStep(int now, int px, int py, double dist, float aimYaw)
        {
            if (now - _panicNext < 0 || now - _antiFireUntil < 0 || Style == CTFXStyle.Post) return false;
            bool hurt = _state.health < 0.5 * _type.Hitpoints, running = _carrying || _traveling || _retreating;
            bool desperate = hurt || _carrying;                              // the repulsor is for getting out alive, not every brush
            Helpers.ObjectState ts = _tgtP != null ? _tgtP._state : _tgtB != null ? _tgtB._state : null;
            if (ts == null) return false;                                   // turrets: no
            if (dist <= 110.0 && desperate)
            {
                int ri = KitIndex(Repulsor);
                if (ri >= 0 && FireNow(now, ri, aimYaw, Math.Max(40.0, dist)))
                {
                    _panicNext = now + 8000 + _rng.Next(4001);
                    Mgr.Repulses++;
                    return true;
                }
            }
            if (_class == SK_INFIL && running && dist >= 60.0 && dist <= 260.0)
            {
                int di = KitIndex(Disruptor);
                if (di < 0) return false;
                // a wall within 40 px of the chaser (8 directions): the shot goes into it, the splash (40 px) catches the chaser
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4.0;
                    int wx = ts.positionX + (int)(Math.Cos(a) * 28), wy = ts.positionY + (int)(Math.Sin(a) * 20);
                    if (!SolidPx(wx, wy) || !Mgr.Los(px, py, ts.positionX, ts.positionY)) continue;
                    double ex = wx - px, ey = wy - py;
                    if (FireNow(now, di, YawTo(ex, ey), Math.Sqrt(ex * ex + ey * ey)))
                    {
                        _panicNext = now + 4000 + _rng.Next(3001);
                        Mgr.DisruptTraps++;
                        return true;
                    }
                    break;
                }
            }
            return false;
        }

        /// <summary>
        /// Is the nose on the target? Within the target's own angular width at that distance (a 16 px body at 60 px spans about 15 deg
        /// each way), never tighter than 3 yaw units. Close fights were a dance: both strafing, the aim moved faster than the turn, the
        /// 3-unit window was never met and nobody fired (Travis 10-13). At a player the engine's own window counts too.
        /// </summary>
        bool AimOn(bool atBot, float aimYaw, double dist)
        {
            double tol = 3.0;
            if (dist > 1.0) tol = Math.Max(3.0, Math.Atan2(16.0, dist) * 180.0 / Math.PI / 1.5 * 0.8);
            if (Math.Abs(YawDelta(aimYaw, _state.yaw)) <= tol) return true;
            return !atBot && _weapon.isAimed((int)aimYaw);
        }

        // the prefire of a weapon that may be fired on the move (PowerFist 500 ms, Zap Glove 300 ms ...): the trigger is held that long
        // with the aim on the target before the shot leaves (Travis 10-13: those fired at once). Stand / scoot weapons wait theirs
        // out standing in FireModeStep.
        int _pfStart, _pfWep = -1;

        bool PrefireDone(int now)
        {
            CTFXFireProfile fp = (_prof != null && _curWep >= 0 && _curWep < _prof.Length) ? _prof[_curWep] : null;
            if (fp == null || fp.PrefireMs <= 0 || fp.Mode != CTFXFireProfile.StrafeFire) return true;
            if (_pfWep != _curWep || _pfStart == 0 || now - _pfStart > fp.PrefireMs + 1500) { _pfWep = _curWep; _pfStart = now; }
            return now - _pfStart >= fp.PrefireMs;
        }

        int _onCheck, _aloneCool, _pcx, _pcy; bool _onLast, _preCover; internal int FallBacks;

        /// <summary>
        /// Soak cycle 1 (Travis 10-17: bots die too often, he wants a war of attrition and careful play once areas are stacked): a seat that
        /// is not carrying a flag, with 3+ enemies inside 320 px and at least 3 more of them than mates, falls back at once instead of
        /// trading into the crossfire, as a player does. Rookies and ADHD seats keep their nerve; checked at most twice a second.
        /// </summary>
        bool Outnumbered(int now)
        {
            if (now - _onCheck < 500) return _onLast;
            _onCheck = now; _onLast = false;
            if (_carrying || Style == CTFXStyle.Adhd || Style == CTFXStyle.Stormer || Style == CTFXStyle.Bountier || Human == null || Human.Tier == "rookie") return false;
            int px = _state.positionX, py = _state.positionY, foes = 0;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team == _team) continue;
                double dx = o._state.positionX - px, dy = o._state.positionY - py;
                if (dx * dx + dy * dy < 320.0 * 320.0) foes++;
            }
            if (Mini != null) foes += HumanFoesNear(320.0);        // MiniTP: the enemy is mostly HUMANS (the mates already count humans: a bot beside a player is not alone), so they count as foes too
            int mates = Mgr.MatesNear(this, 350.0);
            _preCover = false;
            if (foes >= 2 && mates == 0 && now - _aloneCool >= 0)
            {   // soak cycle 5: 70 % of deaths came with NO mate within 350 px (c04) - a lone bot that meets two falls back on the nearest group
                int ctx, cty;
                if (NearbyCover(px, py, out ctx, out cty)) { _preCover = true; _pcx = ctx; _pcy = cty; _aloneCool = now + 6000; _onLast = true; FallBacks++; Mgr.FallBacks++; Mgr.AloneFalls++; return true; }
            }
            if (foes < 3 || foes < Mgr.MatesNear(this, 320.0) + 3 + 1) return false;      // mates exclude itself: even with itself it is 3 down
            _onLast = true; FallBacks++; Mgr.FallBacks++;
            return true;
        }

        /// <summary>The health share at which it backs off: 35 %, up to 55 % as its bounty grows (a player with a big bounty plays safe).</summary>
        double RetreatAt()
        {
            int b = Slot != null ? Slot.Bounty : 0;
            if (Style == CTFXStyle.Bountier) return 0.6;                       // careful: out before it is in danger
            if (Slot != null && Slot.Intent != null && Slot.Intent.Foothold) return 0.6;   // a team foothold plays safe (CTFXLines.cs)
            // Travis 10-17: "bots are a little too reckless and die too often; there used to be a longer war of attrition once areas were
            // stacked up and players were more careful." Experienced seats back off sooner, a drained shield (the HP armour) or being
            // outnumbered on the spot makes everyone more careful, and a rookie keeps the old nerve.
            double r = 0.35 + Math.Min(0.20, b / 2500.0);
            string tier = Human != null ? Human.Tier : "regular";
            r += tier == "ace" ? 0.12 : tier == "veteran" ? 0.10 : tier == "regular" ? 0.06 : 0.0;
            if (_energyMax > 0 && _energy < 0.25 * _energyMax) r += 0.08;
            int foes = 0, friends = Mgr.MatesNear(this, 300.0);
            double px = _state.positionX, py = _state.positionY;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team == _team) continue;
                double dx = o._state.positionX - px, dy = o._state.positionY - py;
                if (dx * dx + dy * dy < 300.0 * 300.0) foes++;
            }
            if (Mini != null) foes += HumanFoesNear(300.0);        // MiniTP: human enemies make it careful too
            if (foes >= friends + 2) r += 0.10; else if (foes > friends + 0) r += 0.04;
            return Math.Min(0.75, r);
        }

        // ---------------------------------------------------------------- healing (Travis 10-13: medics heal so bots survive and
        // build bounty; bountiers use stim packs / energizers when hurt or drained). The zone repair items, their own numbers:
        //   Deluxe Medikit 49 / Medikit 432 (medic): +50 / +20 health to every teammate within 500 px, itself included, 1.25 s
        //   Stim Pack 47: +10 health, itself; Energizer 15: +200 energy, itself (not Infiltrators); Engineer Repair Kit 33: +75 to a
        //   turret of its team within 120 px. A human teammate is healed the engine way (Player.heal: his client applies the item).
        int _meleeReactNext, _eliteRecallNext;
        int _healNext, _selfNext, _stims = -1, _energizers = -1, _medCharges = -1, _kitCharges = -1;
        int _medicSeekAt, _healTick, _deluxeNext, _healStillAt, _healHoldUntil;

        static ItemInfo.RepairItem Repair(int id) { return AssetManager.Manager.getItemByID(id) as ItemInfo.RepairItem; }

        void HealStep(int now)
        {
            if (IsDead || InDropship || OnPack) return;
            bool elite = Style == CTFXStyle.Bountier;
            if (_stims < 0)
            {   // what this life carries (a player buys them at the store; these are counts of uses)
                _stims = CTFXSkillLogic.Allowed(47, _class) ? (elite ? 5 : 2) : 0;
                _energizers = CTFXSkillLogic.Allowed(15, _class) ? (elite ? 4 : 1) : 0;
                _medCharges = _class == SK_MEDIC ? 150 : 0;      // Tsolvy units (the base stack is 150): Medikit uses 1 a time, Deluxe 5 (the items' ammoUsedPerShot)
                _kitCharges = _class == SK_ENGY ? 100 : 0;       // Titanium Oxide units an engineer carries (cap 100): the Repair Kit uses 5 a time
            }
            // the medic: an area heal when someone on its team within 500 px is hurt. Pub 11-08 (Travis: "med bots overheal in a way humans wouldn't be capable of"): the bot now follows
            // the medikits' own data. Medikit 432 / Deluxe 49: prefire 1 s with movementCancelsPrefire (the medic stands still that long, a pending hold asks Think to stop it), a clip of
            // ONE that reloads for 5 s (so one use per 5 s, not per 1.25 s), 100 energy a use, Tsolvy 1 / 5 a use, and the heal falls off with distance (repairDistance is negative).
            // The regular medikit is the working heal; the deluxe only when three or more mates in reach are under half health or one is under 28 %, at most every 7 s.
            if (_medCharges > 0 && now - _healNext >= 0)
            {
                int deep; double worst;
                Mgr.MedicNeed(this, 500, out deep, out worst);
                if (worst >= 0.85) _healStillAt = 0;                                          // nobody hurt in reach: nothing to prepare
                else
                {
                    ItemInfo.RepairItem kit = Repair(432), dk = Repair(49);
                    bool deluxe = false;
                    if (dk != null && CTFXSkillLogic.Allowed(49, _class) && _medCharges >= dk.ammoUsedPerShot && now - _deluxeNext >= 0 && (deep >= 3 || worst < 0.28)) { kit = dk; deluxe = true; }
                    if (kit == null) kit = dk;
                    if (kit != null && _medCharges >= Math.Max(1, kit.ammoUsedPerShot))
                    {
                        int pre = kit.movementCancelsPrefire ? kit.prefireDelay * 10 : 0;
                        bool ready = true;
                        if (pre > 0)
                        {
                            if (!IsStill()) _healStillAt = 0;                                  // moving cancels the prefire
                            else if (_healStillAt == 0) _healStillAt = now;
                            ready = _healStillAt != 0 && now - _healStillAt >= pre;
                            if (!ready) _healHoldUntil = now + 400;                            // stand still for it (Think holds a medic with nothing in sight)
                        }
                        if (ready)
                        {
                            if (Mgr.HealArea(this, kit, now))
                            {
                                _medCharges -= Math.Max(1, kit.ammoUsedPerShot);
                                if (deluxe) _deluxeNext = now + 7000;
                                _healStillAt = 0;
                                _healNext = now + Math.Max(kit.fireDelay, kit.ammoCapacity <= 1 ? kit.reloadDelayNormal : 0) * 10; Mgr.MedicHeals++;
                            }
                            else _healNext = now + 400;
                        }
                    }
                }
            }
            // the engineer: its team turrets within 120 px
            if (_kitCharges > 0 && now - _healNext >= 0 && (Mini == null || (Still && !HasTarget)))     // 11-11 (Travis: engineers too strong): in MiniTP the kit is used standing still with nothing to shoot, as a player has to
            {
                ItemInfo.RepairItem kit = Repair(33);
                if (kit != null && _kitCharges >= Math.Max(1, kit.ammoUsedPerShot) && Mgr.Turrets.RepairNear(this, kit))
                { _kitCharges -= Math.Max(1, kit.ammoUsedPerShot); _healNext = now + Math.Max(kit.fireDelay, kit.reloadDelayNormal) * 10; Mgr.TurretRepairs++; }      // 5 Titanium Oxide a use, 1.5 s between uses (the kit's own data)
            }
            if (now - _selfNext < 0) return;
            double hp = (double)_state.health / Math.Max(1, _type.Hitpoints), en = _energyMax > 0 ? _energy / _energyMax : 1.0;
            if (_stims > 0 && hp < (elite ? 0.6 : 0.5))
            {
                ItemInfo.RepairItem st = Repair(47);
                if (st != null) { int h0 = _state.health; _state.health = (short)Math.Min(_type.Hitpoints, _state.health + st.repairAmount); _stims--; _selfNext = now + 600; Mgr.Stims++; if (Slot != null) { Slot.TkStims++; Slot.TkHealed += _state.health - h0; } return; }
            }
            // cycle 23: a big bounty keeps it - hurt under 40 % with no medic close and no enemy on top of it, it recalls to the dropship
            // (healed and restocked there, bounty kept) and is summoned back, as a player holding thousands does; skill decides how
            // often it thinks of it in time
            if (Slot != null && Slot.Bounty >= 800 && Style != CTFXStyle.Bountier && _state.health < 0.4 * _type.Hitpoints && now - _eliteRecallNext >= 0
                && Mgr.NearestMedic(this, 400.0) == null)
            {
                _eliteRecallNext = now + 3000;
                bool close = false;
                foreach (CTFXBot o in Mgr.LiveBots)
                {
                    if (o.IsDead || o._team == _team) continue;
                    double ex = o._state.positionX - _state.positionX, ey = o._state.positionY - _state.positionY;
                    if (ex * ex + ey * ey < 120.0 * 120.0) { close = true; break; }
                }
                if (!close && _rng.NextDouble() < TierPick(0.35, 0.9) && Recall(now)) { Mgr.EliteRecalls++; return; }
            }
            if ((LastHitItem == 1195 || LastHitItem == 1185) && now - _hitAt < 600 && now - _meleeReactNext >= 0)
            {   // Travis 10-14 night: "Victims should know to energize or repulsor charge to try to survive, but not succeed hopefully half
                // of the time at least, maybe more, depends on the skill": it thinks of it (rookie 25 %, ace 65 %), then repulsor or energizer
                _meleeReactNext = now + 4000;
                Mgr.MeleeReactTries++;
                if (_rng.NextDouble() < TierPick(0.25, 0.65))
                {
                    CTFXBot by = LastHitBy;
                    if (by != null && !by.IsDead && TryRepulseAt(now, by._state.positionX, by._state.positionY)) { Mgr.MeleeReacts++; return; }
                    if (_energizers > 0)
                    {
                        ItemInfo.RepairItem ez0 = Repair(15);
                        if (ez0 != null) { _energy = Math.Min(_energyMax, _energy + ez0.repairAmount); _energizers--; _selfNext = now + 600; Mgr.Energizes++; Mgr.MeleeReacts++; if (Slot != null) Slot.TkEnergizers++; return; }
                    }
                }
            }
            if (_energizers > 0 && en < (elite ? 0.35 : 0.25))
            {
                ItemInfo.RepairItem ez = Repair(15);
                if (ez != null) { _energy = Math.Min(_energyMax, _energy + ez.repairAmount); _energizers--; _selfNext = now + 600; Mgr.Energizes++; if (Slot != null) Slot.TkEnergizers++; return; }
            }
        }

        /// <summary>A hurt bot backs off toward a medic of its team within 900 px when there is one (else home, as before).</summary>
        internal int CoverRetreats;

        /// <summary>
        /// Soak cycle 3 (Travis 10-17: attrition, "not getting cleared"): a hurt or outnumbered bot used to run all the way to its flag room,
        /// often thousands of px through the same enemies. A player falls back to the nearest strength: a mate group of 2+ or a turret of its
        /// team within 700 px, and only if that is farther from the enemy than it stands now.
        /// </summary>
        bool NearbyCover(int px, int py, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            Helpers.ObjectState ts = _tgtP != null ? _tgtP._state : _tgtB != null ? _tgtB._state : _tgtC != null ? _tgtC._state : null;
            double ex = ts != null ? ts.positionX : px, ey = ts != null ? ts.positionY : py;
            double now2 = (px - ex) * (px - ex) + (py - ey) * (py - ey);
            double best = double.MaxValue; int bx = 0, by = 0;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o == this || o.IsDead || o.InDropship || o._team != _team) continue;
                double dx = o._state.positionX - px, dy = o._state.positionY - py, d2 = dx * dx + dy * dy;
                if (d2 > 700.0 * 700.0 || d2 < 120.0 * 120.0) continue;
                double fx = o._state.positionX - ex, fy = o._state.positionY - ey;
                if (fx * fx + fy * fy < now2 + 150.0 * 150.0) continue;                      // not past the enemy
                if (Mgr.MatesNear(o, 200.0) < 1) continue;                                   // a group, not one bot
                if (d2 < best) { best = d2; bx = o._state.positionX; by = o._state.positionY; }
            }
            foreach (Computer c in Mgr.Turrets.Live)
            {
                if (c.IsDead || c._team != _team || c._primaryGun == null) continue;
                double dx = c._state.positionX - px, dy = c._state.positionY - py, d2 = dx * dx + dy * dy;
                if (d2 > 700.0 * 700.0 || d2 < 80.0 * 80.0) continue;
                double fx = c._state.positionX - ex, fy = c._state.positionY - ey;
                if (fx * fx + fy * fy < now2 + 150.0 * 150.0) continue;
                if (d2 * 0.8 < best) { best = d2 * 0.8; bx = c._state.positionX; by = c._state.positionY; }
            }
            if (best == double.MaxValue) return false;
            Mgr.NearestOpenPx(bx, by, out tx, out ty);
            CoverRetreats++; Mgr.CoverRetreats++;
            return true;
        }

        bool MedicGoal(out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXBot m = Mgr.NearestMedic(this, 900.0);
            if (m == null) { m = Mgr.PitMedicFor(this, 1500.0); if (m != null) Mgr.PitStops++; }   // rotate back to the pit (CTFXElite.cs)
            if (m == null) return false;
            tx = m._state.positionX >> 4; ty = m._state.positionY >> 4;
            return true;
        }

        /// <summary>A pack weapon that softens without killing: the beamers (energy drain) and the Tranq (stun).</summary>
        static bool Supportive(int id) { return id == 1113 || (id >= 1163 && id <= 1166); }

        /// <summary>Softened: stunned, drained under 35 % energy, or under 55 % health (a pack killer strikes then).</summary>
        internal bool Softened(int now)
        {
            return now - _antiThrustUntil < 0 || (_energyMax > 0 && _energy < 0.35 * _energyMax) || _state.health < 0.55 * _type.Hitpoints;
        }

        internal int PostHoldUntil;       // a post summoned back by an anchor stays deep in the base until then

        bool AnyAffordable()
        {
            for (int i = 0; i < _kit.Length; i++)
            {
                if ((_badMask & (1 << i)) != 0 || Dry(i)) continue;
                double c = EnergyCost(_kit[i].Id);
                if (c >= 0 && c <= _energy) return true;                       // -1 = not usable on this terrain (was counted as free)
            }
            return false;
        }

        /// <summary>Any kit weapon usable on the terrain it stands on (energy aside)? None = a no-fire zone for this kit.</summary>
        bool AnyUsableHere()
        {
            for (int i = 0; i < _kit.Length; i++)
                if ((_badMask & (1 << i)) == 0 && !Dry(i) && !IsDefTool(_kit[i].Id) && EnergyCost(_kit[i].Id) >= 0) return true;
            return false;
        }

        bool EnergyForShotPeek()
        {
            double c = EnergyCost(_kit[_curWep].Id);
            return c >= 0 && _energy >= c;
        }

        // ---------------------------------------------------------------- targeting
        bool TargetValid(int now, int px, int py)
        {
            Helpers.ObjectState ts;
            if (_tgtP != null)
            {
                Player p = _tgtP;
                if (p.IsDead || p.IsSpectator || p._team == _team || p._state == null || p._arena != _arena) { DropTarget(); _lkValid = false; return false; }
                ts = p._state;
            }
            else if (_tgtB != null)
            {
                CTFXBot b = _tgtB;
                if (b.IsDead || b.Released || b._team == _team) { DropTarget(); _lkValid = false; return false; }
                if (b.Cloaked)
                {
                    double cx = b._state.positionX - px, cy = b._state.positionY - py;
                    if (cx * cx + cy * cy > (double)b.CloakRange * b.CloakRange && !KeepsCloaked(cx * cx + cy * cy)) { DropTarget(); _lkValid = false; return false; }
                }
                ts = b._state;
            }
            else if (_tgtC != null)
            {
                Computer c = _tgtC;
                if (c.IsDead || c._team == _team) { DropTarget(); _lkValid = false; return false; }
                if (c._primaryGun == null && !CanHurtStructure(c)) { DropTarget(); _lkValid = false; return false; }      // Travis 11-08: it does no damage to it: change of plan
                ts = c._state;
            }
            else return false;
            bool far = _farShot != null && _tgtC == null && (_farShot == (object)_tgtP || _farShot == (object)_tgtB);   // a shooter answered past its engage radius
            double dx = ts.positionX - px, dy = ts.positionY - py, lim = far ? _farReach + 60.0 : _tgtC != null ? Math.Max(EngR + 60.0, 950.0) : EngR + 60.0;
            if (!far && _traveling && _tgtC == null && !(_hitAt != 0 && now - _hitAt < 3000)) lim = Math.Min(lim, 360.0 * Mgr.EngageScale);   // on its way: lets it go
            if (dx * dx + dy * dy > lim * lim || now - _tgtLastLos > 2000) { DropTarget(); return false; }
            if (!far && _tgtC == null && Human != null && !InView(dx / 1.15, dy / 1.15)) { DropTarget(); return false; }   // ran off its screen
            return true;
        }

        /// <summary>On its way to an objective 900+ px off (CTFXCommand activities with one place, or a hall group): see _traveling.</summary>
        bool _hallPass;                 // passing through the brown hall (Traveling)

        /// <summary>
        /// 10-17 (Travis, Kai 36): a bot on its way somewhere used to run from anything past 140 px, so a Heavy crossing a landing area
        /// turned its back on a player who was in range and died running. It runs only when running works: a flag in hand or a flag run
        /// (the old rule), the goal about to be reached, or an exit from the enemy's reach that is quicker than the enemy's kill
        /// (estimated from its own health and shield). Otherwise it turns and fights, with the mates near it.
        /// </summary>
        int _runUntil; bool _runVal;
        /// <summary>11-08 jitter: the run-or-fight verdict held for 0.6 s (it flipped every poll at the 140 px edge: Nav and CombatMove took the keys in turn); an enemy on top of it is re-judged at once.</summary>
        bool RunLatched(int now, int px, int py, double dist)
        {
            if (dist <= 140.0 || now - _runUntil >= 0) { _runVal = RunBeatsFight(now, px, py, dist); _runUntil = now + 600; }
            return _runVal;
        }

        bool RunBeatsFight(int now, int px, int py, double dist)
        {
            if (Mgr.Flags != null && (Mgr.Flags.Carrying(this) || Mgr.Flags.Chasing(this))) return dist > 140.0;
            if (_hallPass) return dist > 140.0;
            if (dist <= 140.0) return false;
            double gx = _gtx * 16 + 8 - px, gy = _gty * 16 + 8 - py;
            if (_hasGoal && gx * gx + gy * gy < 350.0 * 350.0) return true;        // nearly there: finish the errand
            double reach = _tgtP != null ? 430.0 : 330.0;                          // how far a player (a bot) can hurt it
            if (dist > reach) return true;                                         // out of reach already: keep going
            double hpFrac = Math.Max(0.05, (double)_state.health / Math.Max(1, _type.Hitpoints));
            double enFrac = _energyMax > 0 ? _energy / _energyMax : 0.0;
            double killMs = (900.0 + 1900.0 * hpFrac + 1200.0 * enFrac) * (_tgtP != null ? 1.0 : 1.4);   // what the enemy needs to kill it
            double exitMs = (reach - dist) / 240.0 * 1000.0;                       // time under fire while it runs out of reach
            int mates = Mgr.MatesNear(this, 300.0);
            bool run = exitMs < 0.45 * killMs && mates < 2;
            if (!run) Mgr.StandAndFight++;
            return run;
        }

        bool Traveling(int px, int py)
        {
            if (!_hasGoal || InDropship || Mgr.Cmdr == null) return false;
            if (Mgr.Flags != null && Mgr.Flags.Carrying(this)) return true;   // a flag carrier keeps running: only what is close or shooting it
            if (Mgr.Flags != null && Mgr.Flags.Chasing(this)) return true;    // so does a flag runner on its way to a flag (10-14: 4 picks in 12 min)
            if (Slot != null && Slot.AttachTo != null) return false;         // with a human: it fights what he fights
            CTFXAct a = Mgr.Cmdr.ActOf(this);
            bool objective = a == CTFXAct.Anchor || a == CTFXAct.Defend || a == CTFXAct.Raid || a == CTFXAct.Skirmish || a == CTFXAct.Build
                || (a == CTFXAct.None && Group != null && Group.CoreOf != null);
            // crossing a landing area (A-H) it is passing through too, wanderers included: the brawl there is the brawlers' business
            if (a != CTFXAct.Outside && a != CTFXAct.Hunt && Mgr.Dropship != null && Mgr.Dropship.OutsideArea(px, py) != ' ') return true;
            // 10-15 J21: in the brown hall on its way out of it (goal 300+ px off), it walks through instead of joining the brawl
            if (a != CTFXAct.Hunt && Mgr.AreaAt(px, py) == "brown-core")
            {
                double hx = _gtx * 16 + 8 - px, hy = _gty * 16 + 8 - py;
                if (hx * hx + hy * hy > 300.0 * 300.0) { _hallPass = true; return true; }
            }
            _hallPass = false;
            if (!objective) return false;
            double gx = _gtx * 16 + 8 - px, gy = _gty * 16 + 8 - py;
            return gx * gx + gy * gy > 900.0 * 900.0;
        }

        void DropTarget()
        {
            if (_tgtP != null || _tgtB != null || _tgtC != null) Mgr.TargetDrops++;
            _tgtP = null; _tgtB = null; _tgtC = null; _losT.Key = -1;
        }

        int _chipUntil;                     // a doomed bot settles on the turret that is killing it until then (ScanTargets)
        internal int Chips;

        void ScanTargets(int now, int px, int py)
        {
            _nextScan = now + _pScanLo + _rng.Next(_pScanSpan + 1);
            if (_tgtC != null && now - _chipUntil < 0 && !_tgtC.IsDead) { _nextScan = now + 400; return; }
            {   // 10-17 (Travis): a bot that fought a player into turret fire kept duelling him; losing anyway, it gets chip damage on the turret
                Computer dt = TurretThreat;
                if (dt != null && now - TurretThreatAt <= 1500 && !dt.IsDead && dt._team != _team && Style != CTFXStyle.Stormer && Style != CTFXStyle.Bountier && CanHurtTurret()
                    && _state.health < 0.5 * _type.Hitpoints && !_carrying && !(_tgtC == dt))
                {
                    double ex = dt._state.positionX - px, ey = dt._state.positionY - py;
                    if (ex * ex + ey * ey <= 900.0 * 900.0 && Mgr.Los(px, py, dt._state.positionX, dt._state.positionY))
                    {
                        _pendAt = 0; _pendP = null; _pendB = null; _pendC = null;
                        Commit(now, null, null, dt);
                        _chipUntil = now + 4000; _nextScan = now + 400; Chips++; Mgr.ChipTurrets++;
                        return;
                    }
                }
            }
            double er = EngR, es2 = Mgr.EngageScale * Mgr.EngageScale, rr = er * er, bd = double.MaxValue;
            double tr = _traveling ? 300.0 * 300.0 * es2 : rr;              // on its way somewhere: only what is close, or shooting it
            // Travis 10-14 eve: "an unrealistically large amount of people fighting in the outdoors": passing a landing area it only
            // turns on what is point-blank (or shooting it); the brawlers (Outside / Hunt) keep their full range
            if (_traveling && Mgr.Dropship != null && Mgr.Dropship.OutsideArea(px, py) != ' ') tr = Math.Min(tr, 180.0 * 180.0 * es2);
            if (_traveling && _hallPass) { tr = Math.Min(tr, 200.0 * 200.0 * es2); Mgr.HallPassThrough++; }
            if (_job != null && _buildAt == 0 && _traveling) tr = Math.Min(tr, 150.0 * 150.0 * es2);        // 10-17: an engineer on its way to a turret spot only turns on what is point-blank
            Player bp = null;
            CTFXBot bb = null;
            bool alerted = _hitAt != 0 && now - _hitAt < 3000;
            // a player sees one screen around him (InView) and turns on whoever is shooting him; among what he sees he picks the way
            // a player does (Pick): what is in front of him, who is shooting him, (veterans) the weak, fewer of his team already on it
            List<Player> ps = _arena.getPlayersInRange(px, py, (int)er, true);
            if (ps != null)
                for (int i = 0; i < ps.Count; i++)
                {
                    Player p = ps[i];
                    if (p == null || p.IsSpectator || p.IsDead || p._team == null || p._team == _team || p._state == null) continue;
                    double ex = p._state.positionX - px, ey = p._state.positionY - py, d2 = ex * ex + ey * ey;
                    if (d2 > rr) continue;
                    bool shotMe = alerted && p == _alertP;
                    int pcr = PlayerCloak(p);
                    if (pcr > 0 && !shotMe && p != _tgtP && d2 > (double)pcr * pcr && !NoticesCloaked(null, d2)) continue;   // a cloaked player: up close or on ES
                    double cf = CarrierF(p);                                           // MiniTP carrier-first (Travis 11-08): an enemy carrying a flag is seen on the run too, and rated far nearer
                    if (!shotMe && d2 > tr && cf >= 1.0) continue;
                    if (Style == CTFXStyle.Bountier && !shotMe && !Mgr.Bounty.MayEngage(this, p, null)) continue;
                    if (p != _tgtP && !shotMe && !InView(ex, ey)) continue;
                    int maxHp = p._baseVehicle != null ? p._baseVehicle._type.Hitpoints : 60;
                    double sc = d2 * Pick(ex, ey, p, null, shotMe, p._state.health, maxHp) * ShooterF(p, null) * cf;
                    if (sc >= bd || !SeesOrWall(px, py, p._state.positionX, p._state.positionY)) continue;
                    bd = sc; bp = p; bb = null;
                }
            List<Vehicle> vs = _arena.getVehiclesInRange(px, py, (int)er, _botPred);
            if (vs != null)
                for (int i = 0; i < vs.Count; i++)
                {
                    CTFXBot o = vs[i] as CTFXBot;
                    if (o == null) continue;
                    double ex = o._state.positionX - px, ey = o._state.positionY - py, d2 = ex * ex + ey * ey;
                    if (o.Cloaked && d2 > (double)o.CloakRange * o.CloakRange && !NoticesCloaked(o, d2)) continue;     // a cloaked stormer: up close, or on Enhanced Sensors
                    if (d2 > rr) continue;
                    bool shotMe = alerted && o == LastHitBy;
                    if (!shotMe && d2 > tr) continue;
                    if (Style == CTFXStyle.Bountier && !shotMe && !Mgr.Bounty.MayEngage(this, null, o)) continue;
                    if (Mgr.Truce(this, o)) continue;                                  // the milk truce (CTFXMilk.cs)
                    if (o != _tgtB && !shotMe && !InView(ex, ey)) continue;
                    double sc = d2 * Pick(ex, ey, null, o, shotMe, o._state.health, o._type.Hitpoints) * ShooterF(null, o);
                    if (sc >= bd || !SeesOrWall(px, py, o._state.positionX, o._state.positionY)) continue;
                    bd = sc; bb = o; bp = null;
                }
            bool forced = false;
            if (Style == CTFXStyle.Drain)
            {
                // a drain pack fires at the pack victim (CTFXBounty), else at its leader's target, when in range and in sight
                Player vp = null; CTFXBot vb = null;
                bool packVictim = Mgr.Bounty != null && Mgr.Bounty.PackVictim(this, out vp, out vb);
                CTFXBot lead = packVictim ? null : FollowTarget();
                if (packVictim || lead != null)
                {
                    Player lp = packVictim ? vp : lead._tgtP;
                    CTFXBot lb = packVictim ? vb : lead._tgtB;
                    Helpers.ObjectState ls = lp != null ? lp._state : (lb != null ? lb._state : null);
                    bool alive = lp != null ? (!lp.IsDead && !lp.IsSpectator && lp._team != _team) : (lb != null && !lb.IsDead && !lb.Released && lb._team != _team);
                    if (ls != null && alive)
                    {
                        double ex = ls.positionX - px, ey = ls.positionY - py, d2 = ex * ex + ey * ey;
                        if (d2 <= rr && Mgr.Los(px, py, ls.positionX, ls.positionY)) { bp = lp; bb = lb; bd = d2; forced = true; }
                    }
                }
            }
            // Travis 10-21 ("could the bots' vision range be too small?"): the engage-radius cap above comes before the shot-me exemption,
            // so a player or bot hitting it from past 450 px (rifles reach 1200) was never answered. With nothing else in range, an alerted
            // bot turns on its shooter up to its longest usable weapon's reach (at most 1000 px), in sight; kept while in reach and sight.
            bool farPick = false;
            if (alerted && bp == null && bb == null && Style != CTFXStyle.Stormer)
            {
                int reach = Math.Min(1000, UsableReach());
                double far2 = (double)reach * reach, fd = double.MaxValue;
                Player sp = _alertP;
                if (sp != null && sp._arena == _arena && !sp.IsDead && !sp.IsSpectator && sp._team != null && sp._team != _team && sp._state != null)
                {
                    double ex = sp._state.positionX - px, ey = sp._state.positionY - py, d2 = ex * ex + ey * ey;
                    if (d2 > rr && d2 <= far2 && SeesOrWall(px, py, sp._state.positionX, sp._state.positionY)) { fd = d2; bp = sp; }
                }
                CTFXBot so = LastHitBy;
                if (so != null && !so.IsDead && !so.Released && so._team != _team && !Mgr.Truce(this, so))
                {
                    double ex = so._state.positionX - px, ey = so._state.positionY - py, d2 = ex * ex + ey * ey;
                    bool seen = !so.Cloaked || d2 <= (double)so.CloakRange * so.CloakRange || NoticesCloaked(so, d2);
                    if (seen && d2 > rr && d2 < fd && d2 <= far2 && SeesOrWall(px, py, so._state.positionX, so._state.positionY)) { fd = d2; bb = so; bp = null; }
                }
                if (bp != null || bb != null)
                {
                    bd = fd; farPick = true; _farReach = reach;
                    _farShot = bp != null ? (object)bp : bb;
                    Mgr.FarAnswers++;
                }
            }
            Computer bc = null;
            // MiniTP (Travis 11-08: "the attacking bots should prioritize damaging turrets"): a raider's turret competes with its other targets as if nearer (the offensive heavy: much nearer) and is
            // looked for out to its weapon's reach (at most 900 px), not only the engage radius; the standoff spot (CTFXMini.TurretStandoff) is what puts it where it can shoot safely
            bool miniHunt = Mini != null && Mgr.Mini.TurretHunter(this);
            if (miniHunt && (bp != null || bb != null) && bd <= 260.0 * 260.0) miniHunt = false;      // an enemy inside 260 px is the threat to answer, not a turret (a raider does not ignore what is on top of it)
            double trr = miniHunt ? Math.Max(rr, Math.Pow(Math.Min(900.0, (double)UsableReach()), 2)) : rr;
            double tf = miniHunt ? Mgr.Mini.TurretFactor(this) : 0.49;
            Computer th = TurretThreat;
            if (th != null && now - TurretThreatAt <= 4000 && !th.IsDead && th._team != _team && Style != CTFXStyle.Stormer && CanHurtTurret())
            {   // a turret is shooting at us: answer it (as if 40% nearer than it is), or another target if one is far nearer
                double ex = th._state.positionX - px, ey = th._state.positionY - py, d2 = ex * ex + ey * ey;
                if (d2 <= 950.0 * 950.0 && d2 * 0.36 < bd && Mgr.Los(px, py, th._state.positionX, th._state.positionY))
                {
                    Acquire(now, px, py, null, null, th);
                    return;
                }
            }
            if ((Style == CTFXStyle.Assault || Style == CTFXStyle.Stormer || miniHunt) && (bp != null || bb != null) && CanHurtTurret())
            {   // a storm squad clears the base it took: an enemy turret competes with its other targets as if 30% nearer; a
                // turret hunter of a fire team takes any turret in sight unless a target is within 120 px
                bool hunter = Group != null && Group.Storm != null && Slot != null && Slot.StyleIndex % 3 == 1 && Mgr.Squads.Spread(Group, this);
                double best = hunter ? (bd <= 120.0 * 120.0 ? bd : double.MaxValue) : bd;
                foreach (Computer c in Mgr.Turrets.Live)
                {
                    if (c._team == null || c._team == _team || c._team._id == -1 || c._primaryGun == null) continue;
                    double ex = c._state.positionX - px, ey = c._state.positionY - py, d2 = ex * ex + ey * ey;
                    if (d2 * tf >= best || d2 > trr || (c != _tgtC && !InView(ex, ey)) || !Mgr.Los(px, py, c._state.positionX, c._state.positionY)) continue;
                    best = d2 * tf; bc = c;
                }
                if (bc != null)
                {
                    Acquire(now, px, py, null, null, bc);
                    return;
                }
            }
            if (bp == null && bb == null)
            {   // no player or bot in range: an enemy turret will do (only with a weapon that hurts it)
                if (!CanHurtTurret()) return;
                foreach (Computer c in Mgr.Turrets.Live)
                {
                    if (c._primaryGun == null && !CanHurtStructure(c)) continue;      // Travis 11-08: nothing in this kit hurts that structure (the SMG against a green portal): leave it
                    if (c._team == null || c._team == _team || c._team._id == -1 || (c._type.Destroyable == 0 && c._primaryGun == null)) continue;
                    double ex = c._state.positionX - px, ey = c._state.positionY - py, d2 = ex * ex + ey * ey;
                    if (d2 >= bd || d2 > trr || (c != _tgtC && !InView(ex, ey)) || !Mgr.Los(px, py, c._state.positionX, c._state.positionY)) continue;
                    bd = d2; bc = c;
                }
                if (bc == null) return;
                Acquire(now, px, py, null, null, bc);
                return;
            }
            if (bp == _tgtP && bb == _tgtB) { if (!farPick) _farShot = null; _tgtLastLos = now; return; }
            if (!forced && _tgtC == null && (_tgtP != null || _tgtB != null))
            {   // a player sticks with his target: the new one must rate clearly better (Stick; ADHD switches at a whim)
                Helpers.ObjectState cs = _tgtP != null ? _tgtP._state : _tgtB._state;
                double cx = cs.positionX - px, cy = cs.positionY - py, cd2 = cx * cx + cy * cy;
                double stick = Human != null ? Human.Stick : _hyst;
                if (Style == CTFXStyle.Adhd) stick = Math.Max(stick, 0.9);
                if (bd >= stick * stick * cd2) return;
            }
            if (!farPick) _farShot = null;
            Acquire(now, px, py, bp, bb, null);
        }

        /// <summary>Its longest usable weapon reach (px): kit weapons not dry and not marked bad.</summary>
        int UsableReach()
        {
            int r = 0;
            if (_kit == null) return 0;
            for (int i = 0; i < _kit.Length; i++)
                if ((_badMask & (1 << i)) == 0 && !Dry(i) && _kit[i].Max > r) r = _kit[i].Max;
            return r;
        }

        /// <summary>Inside the seat's screen around the bot: 420 x 320 px each way at View 1 (a sniper's is 30 % wider), x viewScale.</summary>
        bool InView(double ex, double ey)
        {
            if (Human == null) return true;
            double v = Human.View * (Style == CTFXStyle.Sniper ? 1.3 : 1.0) * Mgr.ViewScale;
            return Math.Abs(ex) <= 420.0 * v && Math.Abs(ey) <= 320.0 * v;
        }

        /// <summary>The seat's rating of a candidate, as a factor on its squared distance (lower = preferred): in front of it 0.85,
        /// behind it 1.25, shooting it 0.6, (veterans and aces) under 40 % health 0.85, +25 % for each teammate past the first
        /// already on it, and a 12 % judgement noise.</summary>
        double Pick(double ex, double ey, Player p, CTFXBot b, bool shotMe, int hp, int maxHp)
        {
            if (Human == null) return 1.0;
            double m = 1.0;
            double turn = Math.Abs(YawDelta(YawTo(ex, ey), _state.yaw)) * 1.5;
            if (turn <= 45.0) m *= 0.85; else if (turn >= 120.0) m *= 1.25;
            if (shotMe) m *= 0.6;
            if ((Human.Tier == "veteran" || Human.Tier == "ace") && maxHp > 0 && hp < 0.4 * maxHp) m *= 0.85;
            if (maxHp > 0 && hp < 0.3 * maxHp) m *= Style == CTFXStyle.Bountier ? 0.45 : 0.7;   // Travis 10-15: kill-steal awareness (bounty farming)
            int n = Chasers(p, b);
            if (n >= 2) m *= 1.0 + 0.25 * (n - 1);
            if (Mgr.HuntVictim(this, p, b)) m *= 0.2;                       // the hunt's victim tops the choice (CTFXHunt.cs)
            if (b != null && Mgr.Flags != null && Mgr.Flags.Carrying(b)) m *= 0.5;   // a flag carrier: kill it first
            if (Mgr.Flags != null && TdOnOurCarrier(p, b)) { m *= 0.4; Mgr.TdAndOut++; }   // TD and out: whoever pins our carrier
            if (Style == CTFXStyle.Drain)
            {   // a bounty hunter: the bigger the bounty, the better the target (lower score wins)
                int bty = p != null ? p.Bounty : CTFXBotManager.BountyOf(b);
                m /= 1.0 + bty / 150.0;
            }
            m *= Math.Max(0.75, Math.Min(1.3, 1.0 + CTFXHuman.Gauss(_rng, 0.0, 0.12)));
            return m * m;
        }

        /// <summary>Teammates already on (or turning to) this enemy.</summary>
        int Chasers(Player p, CTFXBot b)
        {
            int n = 0;
            foreach (CTFXBot o in Mgr.LiveBots)
                if (o != this && o._team == _team
                    && ((p != null && (o._tgtP == p || o._pendP == p)) || (b != null && (o._tgtB == b || o._pendB == b)))) n++;
            return n;
        }

        /// <summary>A target the scan picked. The current one is kept; a new one is answered after the seat's reaction time (more
        /// for a bigger turn) while the bot carries on with what it was doing, and Think commits it then (CommitPending).</summary>
        void Acquire(int now, int px, int py, Player p, CTFXBot b, Computer c)
        {
            if (p == _tgtP && b == _tgtB && c == _tgtC) { _tgtLastLos = now; return; }
            if (Human == null) { Commit(now, p, b, c); return; }
            if (_pendAt != 0 && PendingAlive()) return;     // still turning to the first one it noticed: that one is answered first
            Helpers.ObjectState s = p != null ? p._state : b != null ? b._state : c._state;
            double turn = Math.Abs(YawDelta(YawTo(s.positionX - px, s.positionY - py), _state.yaw)) * 1.5;
            int ms = Human.ReactionMs(_rng, turn);
            _pendP = p; _pendB = b; _pendC = c; _pendAt = (now + ms) | 1;
            Mgr.ReactMsSum += ms; Mgr.Reacts++;
        }

        bool PendingAlive()
        {
            if (_pendP != null) return !_pendP.IsDead && !_pendP.IsSpectator && _pendP._arena == _arena;
            if (_pendB != null) return !_pendB.IsDead && !_pendB.Released;
            return _pendC != null && !_pendC.IsDead;
        }

        void CommitPending(int now, int px, int py)
        {
            Player p = _pendP; CTFXBot b = _pendB; Computer c = _pendC;
            _pendP = null; _pendB = null; _pendC = null; _pendAt = 0;
            Helpers.ObjectState s;
            if (p != null) { if (p.IsDead || p.IsSpectator || p._team == _team || p._state == null || p._arena != _arena) return; s = p._state; }
            else if (b != null) { if (b.IsDead || b.Released || b._team == _team) return; s = b._state; }
            else if (c != null) { if (c.IsDead || c._team == _team) return; s = c._state; }
            else return;
            if (!Mgr.Los(px, py, s.positionX, s.positionY)) return;                     // gone from sight while it reacted
            Commit(now, p, b, c);
        }

        /// <summary>Takes the target. A new one starts an engagement: the first shot waits for the settle, and the aim starts off its
        /// worst (a bigger held bias and a fresh lead misjudgement, both shrinking while it tracks).</summary>
        void Commit(int now, Player p, CTFXBot b, Computer c)
        {
            bool fresh = !(p == _tgtP && b == _tgtB && c == _tgtC);
            _tgtP = p; _tgtB = b; _tgtC = c; _tgtLastLos = now; _losT.Key = -1;
            Mgr.TargetCommits++;
            if (!fresh || Human == null) return;
            _firstShotAt = (now + Human.SettleMs(_rng)) | 1;
            _aimBias = CTFXHuman.Gauss(_rng, 0.0, Human.AimSd * 1.4 * AimMult());
            _aimBiasAt = now + 700 + _rng.Next(600);
            _leadBias = CTFXHuman.Gauss(_rng, 0.0, Human.LeadSd);
            _trackAt = now;
        }

        /// <summary>The style's aim: posts and snipers 25 % steadier, ADHD 30 % shakier.</summary>
        double AimMult()
        {
            return _jit <= 1 ? 0.75 : _jit >= 3 ? 1.3 : 1.0;
        }

        /// <summary>
        /// Where the seat aims: the straight line to the target plus the lead the engine computes, misjudged by the engagement's lead
        /// error, plus a held bias (re-drawn every 0.6-1.6 s) and the shot's own noise. While it keeps tracking the same target the
        /// bias and the lead error shrink (x0.88 a second), as a player settles on someone.
        /// </summary>
        float HumanAim(int now, Helpers.ObjectState ts, double dx, double dy)
        {
            float lead = _weapon.getAimAngle(ts);
            if (Human == null) return WrapYaw(lead + _jitter);
            int dt = now - _trackAt;
            if (dt > 0)
            {
                double f = Math.Pow(0.88, dt / 1000.0);
                _aimBias *= f; _leadBias *= f; _trackAt = now;
            }
            if (now - _aimBiasAt >= 0)
            {
                _aimBias = CTFXHuman.Gauss(_rng, 0.0, Human.AimSd * AimMult());
                _aimBiasAt = now + 600 + _rng.Next(1001);
            }
            float straight = YawTo(dx, dy);
            float leadPart = YawDelta(lead, straight);
            return WrapYaw(straight + leadPart * (float)(1.0 + _leadBias) + (float)(_aimBias + _shotNoise));
        }

        // ---------------------------------------------------------------- weapons
        /// <summary>
        /// How well weapon w suits a target d map px away (wd world px: the y gap x 10/7). A lobbed weapon lands at one fixed world
        /// distance (CTFXShots.LobLand), so it scores by how close the target is to that spot; everything else by its kit range.
        /// </summary>
        static double Score(CTFXWep w, double d, double wd)
        {
            int land = CTFXShots.LobLand(w.Id);
            if (land > 0)
            {
                double e = Math.Abs(wd - land);
                return e <= 35.0 ? 100.0 - e * 0.1 : 70.0 - (e - 35.0) * 0.4;
            }
            if (d >= w.Lo && d <= w.Hi) return 100.0 - Math.Abs(d - w.Pref) * 0.05;
            double outside = d < w.Lo ? w.Lo - d : d - w.Hi;
            return 70.0 - outside * 0.4;
        }

        /// <summary>A lobbed weapon only fires while the target stands near where it lands.</summary>
        bool LobOk(int i)
        {
            int land = CTFXShots.LobLand(_kit[i].Id);
            return land == 0 || Math.Abs(_wdist - land) <= 60.0;
        }

        void SelectWeapon(int now, double dist, bool force)
        {
            if (_kit == null || (!force && now - _lastWepSwitch < 700)) return;
            // mid prefire / burst the weapon stays: only a target inside 120 px breaks the cycle
            if (!force && (_fmPhase == FM_STOP || _fmPhase == FM_BURST) && dist >= 120.0) return;
            double wd = force ? dist : _wdist;
            bool mateDrains = MateDraining();                                          // combos (CTFXCombos.cs, Travis 10-14)
            double ef = TargetEnergyFrac();
            int best = -1;
            double bs = -1e9;
            bool turretOnly = _tgtC != null && CanHurtTurret();
            for (int i = 0; i < _kit.Length; i++)
            {
                if ((_badMask & (1 << i)) != 0 || Dry(i)) continue;
                if (IsMine(_kit[i].Id)) continue;                                               // MiniTP mines ride in the kit: laid by the mine rule (CTFXMini.MineStep), never shot
                if (_tgtC != null && turretOnly && TurretUseless(_kit[i].Id)) continue;           // 10-17: never shoot a turret with a gun that cannot hurt it
                if (_tgtC != null && _tgtC._primaryGun == null && _tgtC._type != null && !CTFXEffect.Works(_kit[i].Id, _tgtC._type.Id)) continue;   // Travis 11-08: a structure (green portal ...) only takes what the shared weapon-vs-vehicle table says hurts it (CTFXEffect.cs)
                if (_tgtC == null && _kit[i].Id == Disruptor && !TargetAtWall()) continue;          // 10-17: its shot passes through bodies, only a wall beside the target makes it stun
                double s = Score(_kit[i], dist, wd) + (i == _curWep ? 12.0 : 0.0);    // hysteresis for the current weapon
                // MiniTP (Travis 11-07: "dumb at close range, they do not use their upclose shot like an RG or an MML"): inside ~340 px the up-close
                // finisher outranks the primary gun while it is ready to fire (its own range band starts at 120-150 px, so point blank was the AR)
                if (Mgr.Mini != null && Mgr.Mini.IsFinisher(_kit[i].Id) && wd <= 340.0 && CanShoot(now, i) && !RgWaits(_kit[i].Id))
                    s = 118.0 - Math.Abs(wd - 120.0) * 0.05 + (i == _curWep ? 12.0 : 0.0);
                if (_tgtC != null && IsGrenade(_kit[i].Id)) s += 45.0;                 // turrets: grenades and launchers do the work
                double ec = EnergyCost(_kit[i].Id);
                if (ec < 0) s -= 300.0;                                                // not usable on this terrain at all (cost -1)
                else if (ec > _energy) continue;                                       // Travis 10-14: no energy for it = not picked at all
                if (IsDefTool(_kit[i].Id)) s -= 200.0;                                // a defensive tool (PanicStep), not a weapon
                if (RgWaits(_kit[i].Id)) s -= 250.0;                                  // Travis 11-08: a rifle grenade does nothing to a man whose shield is up: it waits for the drain
                if (ec > 0 && _energyMax > 0 && !IsDefTool(_kit[i].Id))
                {   // Travis 11-08: "weapons that cost energy ... they're making themselves more vulnerable to damage. If they're already low on energy, and taking damage,
                    // you wouldn't expend further energy to make yourself even more vulnerable": energy IS the shield. What a shot leaves must stay above a floor, and
                    // the floor is high while the bot is being hit (a third of its energy), lower when it is not (a seventh). The price grows the deeper under the floor the shot
                    // would take it, so a zero-cost gun (SMG, flechette, grenades) wins whenever the kit has one; a kit of only energy guns still picks the cheapest.
                    bool underFire = _hitAt != 0 && now - _hitAt < 4000;
                    double left = (_energy - ec) / _energyMax, floorF = underFire ? 0.33 : 0.14;
                    if (left < floorF) s -= (underFire ? 260.0 : 110.0) * Math.Min(1.0, 0.35 + (floorF - left) / floorF);
                }
                if (Style == CTFXStyle.Drain && Mgr.Bounty.PackFinisher(Group) != null && !Supportive(_kit[i].Id)) s -= 150.0;   // drain and stun, the killer kills
                if (_kit[i].Id == CTFXBounty.ApMine || _kit[i].Id == CTFXBounty.BulletMine || _kit[i].Id == CTFXBounty.PlasmaMine || _kit[i].Id == DemoPack) s -= 500.0;   // laid, not shot
                if (_tgtC != null) s += TurretBias(_kit[i].Id);                        // what works on turrets (scratchpad turretdps/REPORT.md)
                if (Mini != null && _tgtC == null && Mgr.Mini.IsShooterTarget(this, _tgtP, _tgtB) && StunsOrKnocks(_kit[i].Id)) s += 40.0;      // MiniTP: whoever is shooting our turrets is knocked off balance first
                s += ComboBonus(i, mateDrains, ef);
                if (s > bs) { bs = s; best = i; }
            }
            if (best < 0 || best == _curWep) return;
            if (mateDrains && _curWep >= 0 && IsBeamer(_kit[_curWep].Id) && !IsBeamer(_kit[best].Id)) Mgr.Finishers++;
            _lastWepSwitch = now;
            if (EquipIdx(best)) { _curWep = best; _fmPhase = FM_FREE; }
        }

        bool EquipIdx(int i)
        {
            CTFXWep w = _kit[i];
            if (!CTFXSkillLogic.Allowed(w.Id, _class))
            {
                _badMask |= 1 << i;
                Mgr.WarnOnce("class gate: refused to equip item " + w.Id + " on a " + ClassName(_class));
                return false;
            }
            ItemInfo it = AssetManager.Manager.getItemByID(w.Id);
            if (it == null) { _badMask |= 1 << i; Mgr.WarnOnce("weapon " + w.Id + " does not resolve"); return false; }
            bool ok = false;
            try { ok = _weapon.equip(it); } catch (Exception) { }
            if (!ok) { _badMask |= 1 << i; Mgr.WarnOnce("body " + _type.Id + " cannot equip weapon " + w.Id); }
            return ok;
        }

        // ---------------------------------------------------------------- shot gating + fire modes
        /// <summary>
        /// Clip / interval / reload bookkeeping of kit entry i (a finished reload refills the clip here). Item lockout: right
        /// after another kit item fired, entry i must also wait out THAT item's fireDelayOther (the same item only waits its
        /// own interval); a weapon switch itself is free, but the first shot after it waits here.
        /// </summary>
        bool CanShoot(int now, int i)
        {
            if (_prof == null || i < 0 || i >= _prof.Length) return false;
            if (now - _antiFireUntil < 0 || Dry(i)) return false;            // stunned (no fire) or out of ammo
            // the Repulsor Charge has ONE cooldown for every path that throws it (combat pick, panic, turret guard, a mate's bail-out):
            // Travis 10-20 saw bots throw two back to back (the combat pick never set the panic cooldown; reloadDelayNormal is 0)
            if (_kit[i].Id == Repulsor && now - _panicNext < 0) return false;
            if (_reloading[i])
            {
                if (now - _reloadUntil[i] < 0) return false;
                _reloading[i] = false;
                _clipLeft[i] = _prof[i].Clip;
            }
            if (_lastShotIdx >= 0 && _lastShotIdx < _prof.Length && _lastShotIdx != i
                && now - (_lastShotTick + _prof[_lastShotIdx].OtherMs) < 0) return false;
            return _clipLeft[i] > 0 && now - _readyAt[i] >= 0;
        }

        void ShotFired(int now, int i)
        {
            CTFXFireProfile p = _prof[i];
            _lastShotIdx = i; _lastShotTick = now;
            if (_kit[i].Id == Repulsor && now - _panicNext >= 0) _panicNext = now + 5000 + _rng.Next(3001);   // see CanShoot
            if (Mgr.Usage != null) Mgr.Usage.Shot(_kit[i].Id);
            _readyAt[i] = now + p.IntervalMs;
            if (Mgr.Ammo && _ammoSlot != null && _ammoSlot[i] >= 0) _ammo[_ammoSlot[i]] -= _ammoPer[i];
            bool firstOfClip = _clipLeft[i] >= p.Clip;
            --_clipLeft[i];
            StaggerShot(now, i, firstOfClip);                               // an elite's 1-2 / 2-1 / 1-1-1 now and then (CTFXStagger.cs)
            if (_clipLeft[i] <= 0)
            {
                _clipLeft[i] = 0;
                _reloading[i] = true;
                _reloadUntil[i] = now + p.ReloadMs;
            }
        }

        /// <summary>Still = under 30 px/s (velocity 1000 = 100 px/s); friction needs about a second after the thrust stops.</summary>
        bool IsStill()
        {
            return Math.Abs((int)_state.velocityX) + Math.Abs((int)_state.velocityY) < 160;     // 10-17: a fuller stop (it was 300 and bots fired while still gliding)
        }

        /// <summary>The equipped weapon is a stand / scoot one: movement cancels its prefire, so it only ever fires from a stand (10-17: ACs were shot on the run).</summary>
        bool StandWeapon()
        {
            CTFXFireProfile fp = (_prof != null && _curWep >= 0 && _curWep < _prof.Length) ? _prof[_curWep] : null;
            return fp != null && fp.Mode != CTFXFireProfile.StrafeFire;
        }

        bool OffPost(int px, int py)
        {
            if (Style != CTFXStyle.Post) return false;
            int tx, ty;
            PostAnchor(out tx, out ty);
            double ex = tx * 16 + 8 - px, ey = ty * 16 + 8 - py;
            return ex * ex + ey * ey > 48.0 * 48.0;
        }

        /// <summary>Where a post's leash is tied: its current stand goal (fanned spot or the outdoor line), else its post tile.</summary>
        void PostAnchor(out int tx, out int ty)
        {
            if (_hasGoal) { tx = _gtx; ty = _gty; return; }
            Mgr.PostTile(Slot != null ? Slot.PostIndex : 0, out tx, out ty);
        }

        /// <summary>Burst length: a whole clip for a post, 50-100% of it for everyone else; a scooter shoots one volley.</summary>
        int BurstShots(CTFXFireProfile fp)
        {
            if (fp.Mode == CTFXFireProfile.ShootScoot) return 1;
            if (Style == CTFXStyle.Post) return fp.Clip;
            return Math.Max(1, (int)Math.Round(fp.Clip * (0.5 + 0.5 * _rng.NextDouble())));
        }

        int _fmLinger;
        void EndBurst(int now, CTFXFireProfile fp)
        {
            // shoot and scoot: on the move through the reload, stopping for the next prefire only when the next shot is nearly ready
            // (Travis 10-13: the scoot was far too quick - 0.8-1.4 s against a 2.5 s MML reload, then standing out the rest)
            if (fp.Mode == CTFXFireProfile.ShootScoot) { _fmPhase = FM_REPOS; _fmUntil = now + Math.Max(800, fp.ReloadMs - fp.PrefireMs) + _rng.Next(601); }
            else if (Style == CTFXStyle.Post) { _fmPhase = FM_STOP; _fmT0 = now; _fmStill = false; }                     // a post never moves off: straight into the next prefire
            else { _fmPhase = FM_REPOS; _fmLinger = (_hitAt != 0 && now - _hitAt < 1500) ? now : now + 350 + _rng.Next(500); _fmUntil = now + 600 + _rng.Next(901) + 600; }   // linger (not while being hit: 11-08), then 600-1500 ms of moving
        }

        /// <summary>
        /// Advance the stand / scoot cycle of the equipped weapon: stop and wait until still, wait the prefire while still
        /// (moving restarts it; forced done after prefire + 2.5 s), shoot the burst from a stand, then move 600-1500 ms
        /// (scoot: one volley, 800-1400 ms) before the next prefire. STRAFE_FIRE weapons and engaged == false (no target in
        /// sight, retreat, relocation) bypass the cycle. Returns FMR_FREE (shoot on the move as before), FMR_MOVE (between
        /// volleys: move, no shot), FMR_HOLD (standing for the prefire: no move, no shot) or FMR_FIRE (standing and shooting).
        /// </summary>
        int FireModeStep(int now, int px, int py, bool engaged)
        {
            CTFXFireProfile fp = (_prof != null && _curWep >= 0 && _curWep < _prof.Length) ? _prof[_curWep] : null;
            if (fp == null || fp.Mode == CTFXFireProfile.StrafeFire || !engaged) { _fmPhase = FM_FREE; return FMR_FREE; }

            if (_fmPhase == FM_REPOS && now - _fmUntil >= 0) _fmPhase = FM_FREE;
            if (_fmPhase == FM_FREE)
            {
                if (OffPost(px, py)) return FMR_FREE;          // a post that was pushed off walks back first, shooting as it goes
                _fmPhase = FM_STOP; _fmT0 = now; _fmStill = false;
            }
            if (_fmPhase == FM_REPOS) return now - _fmLinger < 0 ? FMR_HOLD : FMR_MOVE;     // 10-17: stand a beat after the burst, then glide
            if (_fmPhase == FM_STOP)
            {
                if (!IsStill()) _fmStill = false;
                else if (!_fmStill) { _fmStill = true; _fmStillT = now; }
                bool prefired = (_fmStill && now - _fmStillT >= fp.PrefireMs) || (now - _fmT0 >= fp.PrefireMs + 5000 && Math.Abs((int)_state.velocityX) + Math.Abs((int)_state.velocityY) < 500);   // from a stand; 10-17: never forced out of a run
                if (!prefired) return FMR_HOLD;
                _fmPhase = FM_BURST;
                _fmShots = BurstShots(fp);
                double ec = EnergyCost(_kit[_curWep].Id);
                if (ec > 0) _fmShots = Math.Min(_fmShots, (int)(_energy / ec));    // a burst only as long as the energy it has
                _fmUntil = now + fp.ReloadMs + _fmShots * fp.IntervalMs + 2500;      // never stand there forever
            }
            if (_fmShots <= 0 || now - _fmUntil >= 0)
            {
                EndBurst(now, fp);
                return _fmPhase == FM_REPOS ? FMR_MOVE : FMR_HOLD;
            }
            return FMR_FIRE;
        }

        void RollJitter(int now)
        {
            _jitTick = now;
            _jitter = _jit == 0 ? 0 : _rng.Next(-_jit, _jit + 1);
            if (Human != null) _shotNoise = CTFXHuman.Gauss(_rng, 0.0, Human.ShotSd * AimMult());
        }

        // A human explosion near this bot: the engine damage, then the knockback a client would apply to its own vehicle (no
        // client simulates a bot, so the server does). The server calls this for every vehicle within damage radius + 500 px.
        public override void applyExplosion(Player attacker, int dmgX, int dmgY, ItemInfo.Projectile wep)
        {
            TakeHit(attacker, dmgX, dmgY, wep);
            if (wep == null || IsDead) return;
            int fx = attacker != null ? attacker._state.positionX : dmgX, fy = attacker != null ? attacker._state.positionY : dmgY;
            Knockback(wep.id, dmgX, dmgY, dmgX - fx, dmgY - fy);
            // the anti-effect radius is tiny (Viral 8 px): measured from where the shooter saw the body (recent positions), and a hit
            // that lands on the body counts as a hit on it (distance 0), as a projectile touching a player stuns him
            double d = RecentDist(dmgX, dmgY, 700);
            if (d <= _type.TriggerRadius + 4) d = 0;
            int st = Mgr.Stuns;
            ApplyAnti(wep, d);
            if (Mgr.Stuns != st) Mgr.HumanStuns++;
        }

        /// <summary>A human's explosion (dmgX, dmgY): the item's damage mode against the human's team, then TakeBlast at the client's
        /// distance from the bot's current position (the server uses its private recent-position history).</summary>
        internal void TakeHit(Player attacker, int dmgX, int dmgY, ItemInfo.Projectile wep)
        {
            if (wep == null || IsDead) return;
            if (attacker != null)
            {
                if (wep.damageMode == 2 && attacker._team == _team) return;                       // enemy only
                if ((wep.damageMode == 3 || wep.damageMode == 4) && attacker._team != _team) return;   // friendly only
            }
            // the client scored the burst against where IT saw the body (its view lags the server): measured against the body's last 400 ms
            // of positions, as stock InfServer's getImpliedRadius does, and a burst on the body is a direct hit (radius-0 types land).
            // Before 10-15 it used the current position only, so a small-radius hit on a moving bot often did nothing (cfs-studio parity RE)
            double dx = _state.positionX - dmgX, dy = (_state.positionY - dmgY) / 0.7, now0 = Math.Sqrt(dx * dx + dy * dy);
            double d = RecentDistIso(dmgX, dmgY, 400);
            bool direct = d <= _type.TriggerRadius + 4;
            Mgr.HumanHits++;
            if (now0 - d > 2.0) Mgr.HumanHitsLagged++;
            TakeBlast(direct ? 0.0 : d - _type.TriggerRadius, wep, attacker, direct);
        }

        /// <summary>
        /// Damage at <paramref name="edge"/> px from the body's edge, by the client's rule (cfs-studio damageCalculation, retail
        /// 0x473050): distance = max(0, |dx, dy x 10/7| - TriggerRadius); each type falls off linearly from inner to outer over its
        /// own radius and adds nothing past it; a type without a radius hits only the body the shot touched (direct). Armour as the
        /// server's Vehicle.applyExplosionDamage, but each type is clamped at 0: the server lets a type under the armour's ignore value
        /// subtract from the others, which the class armour baked into the bot bodies would trigger on mixed-damage weapons. Energy
        /// damage is not applied, as on the server. 1/1000 HP units and the kill path as the server.
        /// </summary>
        internal void TakeBlast(double edge, ItemInfo.Projectile wep, Player attacker, bool direct)
        {
            if (wep == null || IsDead) return;
            if (GraceUntil != 0 && Environment.TickCount - GraceUntil < 0) return;       // MiniTP: warp grace after a scripted warp (CTFXMini.Land)
            bool watched = Mgr.Watched(this);
            double e0 = _energy; int h0 = _state.health;
            // 11-11 (Travis: "the hoverboard is never meant to take damage ... hopping on a board shouldn't affect their damage taken"): a board the bot BUILT (MiniTP mining)
            // is never knocked off or destroyed; the hit is worked out on the rider's own body (its armour, self-ignore), exactly as on foot
            bool onBoard = OnPack && BoardHp > 0;
            VehInfo.Car boardType = null;
            if (onBoard) { boardType = _type; _type = FootType; }
            else if (OnPack) Dismount(Environment.TickCount);                      // a CTF:X pack: knocked off, the hit lands on its own body
            try {
            if (edge < 0) edge = 0;
            _hitAt = Environment.TickCount | 1;                 // the shooter (attacker, or LastHitBy for a bot) is noticed for 3 s
            if (attacker != null) _alertP = attacker;
            // pass 1: Energy-type damage, and any type whose mode routes it to energy (2), drains energy through the armour (mode 1
            // bypasses the armour); a negative amount restores energy
            double[] g = new double[6];
            int[] mode = new int[] { wep.kineticDamageMode, wep.explosiveDamageMode, wep.electronicDamageMode, wep.psionicDamageMode,
                wep.bypassDamageMode, wep.energyDamageMode };
            g[0] = Gross(edge, direct, wep.kineticDamageRadius, wep.kineticDamageInner, wep.kineticDamageOuter);
            g[1] = Gross(edge, direct, wep.explosiveDamageRadius, wep.explosiveDamageInner, wep.explosiveDamageOuter);
            g[2] = Gross(edge, direct, wep.electronicDamageRadius, wep.electronicDamageInner, wep.electronicDamageOuter);
            g[3] = Gross(edge, direct, wep.psionicDamageRadius, wep.psionicDamageInner, wep.psionicDamageOuter);
            g[4] = Gross(edge, direct, wep.bypassDamageRadius, wep.bypassDamageInner, wep.bypassDamageOuter);
            g[5] = Gross(edge, direct, wep.energyDamageRadius, wep.energyDamageInner, wep.energyDamageOuter);
            for (int t = 0; t < 6; t++)
            {
                if (g[t] == 0 || !(t == 5 || mode[t] == 2)) continue;
                if (g[t] > 0)
                {
                    double d = (mode[t] == 1 ? g[t] : Armoured(g[t], t)) / 1000.0;
                    _energy -= Math.Min(d, _energy);
                }
                else _energy = Math.Min(_energyMax, _energy - g[t] / 1000.0);
            }
            // pass 2: every other type, scaled by the shield (1 - energy/max) BEFORE the armour; under 2 HP after armour does nothing
            double shield = Mgr.EnergyShield && _energyMax > 0 ? 1.0 - _energy / _energyMax : 1.0;
            double hp = 0;
            CTFXSlot ls = Slot;
            if (ls != null) { ls.TkHits++; ls.TkEnergyAtHit += 1.0 - shield; }
            for (int t = 0; t < 6; t++)
            {
                if (g[t] <= 0 || t == 5 || mode[t] == 2) continue;
                double raw = g[t] / 1000.0, after = raw * shield;
                double part = mode[t] == 1 ? after : Armoured(after * 1000.0, t) / 1000.0;
                Mgr.ShieldMilliHp += (long)Math.Max(0, (mode[t] == 1 ? raw : Armoured(g[t], t) / 1000.0) * 1000.0 - part * 1000.0);
                if (ls != null) { ls.TkRaw += raw; ls.TkShield += raw - after; ls.TkArmour += after - part; if (part < 2) ls.TkFloor += part; }
                if (part >= 2) hp += part;
            }
            if (hp > 0)
            {
                double acc = _hpRem + hp;
                int whole = (int)Math.Floor(acc);
                _hpRem = acc - whole;
                _state.health -= (short)whole;
                if (ls != null) ls.TkHp += whole;
                if (attacker != null && !_attackers.Contains(attacker)) _attackers.Add(attacker);
            }
            if (watched || (attacker != null && Mgr.HumanHitLog++ < 400))
            {   // Travis 10-15: "bots energy not even getting damage when clearly hit" - every hit on a spectated bot, to match his screen
                CTFXBot by = HitBy; HitBy = null;
                Mgr.Log("[CTFXHit] " + (Slot != null ? Slot.Name : "?") + " (" + ClassName(_class) + (OnPack ? ", pack" : "") + ") <- " + wep.name.Trim('"', ' ')
                    + " by " + (attacker != null ? attacker._alias : by != null && by.Slot != null ? by.Slot.Name : "?") + (direct ? " direct" : " splash " + edge.ToString("0") + " px")
                    + ": energy " + e0.ToString("0") + " > " + _energy.ToString("0") + " of " + _energyMax.ToString("0") + ", hp " + h0 + " > " + Math.Max(0, (int)_state.health)
                    + " (raw kin " + (g[0] / 1000).ToString("0") + " energy " + (g[5] / 1000).ToString("0") + ", shield let " + (shield * 100).ToString("0") + "% through)");
                Mgr.WatchedHits++;
            }
            else HitBy = null;
            } finally { if (boardType != null) _type = boardType; }
            if (_state.health <= 0)
            {
                kill(attacker, wep.id);
                _state.health = 0;
            }
        }
        /// <summary>*ctfxbots inv: the bot's body and kit as a player's profile would show them (class, hp, energy shield, weapons, ammo, consumables).</summary>
        internal System.Collections.Generic.List<string> InvReport()
        {
            var r = new System.Collections.Generic.List<string>();
            r.Add((Slot != null ? Slot.Name : "?") + " | " + ClassName(_class) + " | " + Style + (OnPack ? " (on a pack)" : "") + " | bounty " + (Slot != null ? Slot.Bounty : 0)
                + " | hp " + Math.Max(0, (int)_state.health) + "/" + (int)_type.Hitpoints + " | energy " + _energy.ToString("0") + "/" + _energyMax.ToString("0")
                + " (shield lets " + (Mgr.EnergyShield && _energyMax > 0 ? ((1.0 - _energy / _energyMax) * 100).ToString("0") : "100") + "% through)");
            var sb = new System.Text.StringBuilder("kit:");
            if (_kit != null) foreach (CTFXWep w in _kit) sb.Append(' ').Append(ItemName(w.Id)).Append(',');
            r.Add(sb.ToString().TrimEnd(','));
            sb = new System.Text.StringBuilder("ammo:");
            if (_ammo != null && _ammoIds != null) for (int k = 0; k < _ammoIds.Length; k++) sb.Append(' ').Append(ItemName(_ammoIds[k])).Append(' ').Append(_ammo[k]).Append('/').Append(_ammoStart[k]).Append(',');
            r.Add(sb.ToString().TrimEnd(','));
            r.Add("utility: stims " + _stims + ", energizers " + _energizers + ", medikit charges " + _medCharges + ", repair-kit charges " + _kitCharges + "  (-1 = not stocked yet)");
            return r;
        }

        internal CTFXBot HitBy;                 // the bot whose shot is landing now (CTFXShots.Burst), for the spectated-bot hit log

        /// <summary>One damage type's amount at <paramref name="edge"/> (thousandths of HP): inner to outer over its radius, nothing past
        /// it; a type without a radius hits only the body the shot touched.</summary>
        static double Gross(double edge, bool direct, int range, int inner, int outer)
        {
            if (range <= 0) return direct ? inner : 0.0;
            if (edge > range) return 0.0;
            return inner + (outer - inner) * (edge / range);
        }

        /// <summary>The body's armour for one type (ignore, then the per-mille reduction), never below 0. Thousandths in and out.</summary>
        double Armoured(double amount, int type)
        {
            // 10-16: the class bodies 240-250 have the class's build armour baked in (ctfpl.veh, ~2.4 HP ignore + 37 % kinetic / explosive;
            // the stock class vehicles 110-120 carry none: a player's armour is only his worn utilities). With the utilities worn the armour
            // was counted twice, so the worn layers alone count (a body without utilities keeps its own)
            bool baked = !TestOldArmour && Mgr.Utilities && _activeEquip != null && _type.Id >= 240 && _type.Id <= 250;
            double v = baked ? amount : (amount - _type.Armors[type].SelfIgnore) * (1.0 - _type.Armors[type].SelfReduction / 1000.0);
            return v > 0.0 ? UtilArmour(v, type) : 0.0;                      // then each worn utility's layer (CTFXUtilities.cs)
        }

        // Client knockback rule (cfs-studio damageCalculation.computeKnockback, vehicle mode). Radius > 0 is a hard range with full
        // force inside, < 0 fades linearly over |radius|. The push blends the blast->bot radial with the shot travel direction by
        // col 128 percent (0 = radial). Force is in the client float velocity, x VP/1500 into our integer velocity.
        internal void Knockback(int projId, int ex, int ey, double travelX, double travelY)
        {
            CTFXBotManager.KnockInfo k;
            if (_cm == null || !Mgr.Knock.TryGetValue(projId, out k)) return;
            double dx = _state.positionX - ex, dy = _state.positionY - ey;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (k.Radius > 0 && dist > k.Radius) return;
            double falloff = k.Radius > 0 ? 1.0 : Math.Max(0.0, 1.0 - dist / -k.Radius);
            if (falloff <= 0) return;
            double tl = Math.Sqrt(travelX * travelX + travelY * travelY);
            double px = tl > 0.01 ? travelX / tl : 0, py = tl > 0.01 ? travelY / tl : 0;
            double rx = px, ry = py;
            if (dist > 0.01) { rx = dx / dist; ry = dy / dist; }
            double pct = k.DirPct / 100.0;
            double bx = rx * (1 - pct) + px * pct, by = ry * (1 - pct) + py * pct;
            double bl = Math.Sqrt(bx * bx + by * by);
            if (bl > 0.001) { bx /= bl; by /= bl; } else { bx = px; by = py; }
            if (bx == 0 && by == 0) return;
            double f = k.Force * falloff * (10000.0 / 1500.0) * Mgr.KnockScale;
            _cm.Knock((int)(bx * f), (int)(by * f), k.Absolute, k.IgnoreMs);
            Mgr.Knocks++;
            if (Mgr.Knocks <= 5)
                Mgr.WarnOnce("kb #" + Mgr.Knocks + ": " + Style + " bot hit by item " + projId + " at " + (int)dist + " px, push "
                    + (int)(f / 100) + " px/s" + (k.Absolute ? " (set)" : "") + ", top speed off " + k.IgnoreMs + " ms");
        }

        // Client anti-effect rule (cfs-studio combat.ts applyAnti): inside antiEffectsRadius each non-zero duration (x 10 ms) extends
        // its lock: thrust + strafe, turning, firing, energy recharge (energy is not modelled for bots yet). Independent of damage.
        internal void ApplyAnti(ItemInfo.Projectile p, double dist)
        {
            if (p == null || !Mgr.Stun || p.antiEffectsRadius <= 0 || dist > p.antiEffectsRadius) return;
            int now = Environment.TickCount;
            Extend(ref _antiRechargeUntil, now, p.antiEffectsRecharge);         // blocks energy regen (EnergyTick)
            bool any = Extend(ref _antiThrustUntil, now, p.antiEffectsThrust);
            any |= Extend(ref _antiRotateUntil, now, p.antiEffectsRotate);
            any |= Extend(ref _antiFireUntil, now, p.antiEffectsFire);
            if (!any) return;                                                   // recharge only (shotgun pellets ...): not a stun
            Mgr.Stuns++;
            _routeNow = true;
            if (Mgr.Stuns <= 3)
            {
                Mgr.WarnOnce("stun #" + Mgr.Stuns + ": " + Style + " bot hit by item " + p.id + " (" + p.name + ") at " + (int)dist + " px: no move "
                    + p.antiEffectsThrust * 10 + " ms, no turn " + p.antiEffectsRotate * 10 + " ms, no fire " + p.antiEffectsFire * 10 + " ms");
                // check it took: how far the bot gets in the next 2 s (Think logs it)
                _stunProbeN = Mgr.Stuns; _stunProbeT = now + 2000; _stunProbeX = _state.positionX; _stunProbeY = _state.positionY;
                _stunProbeV = (int)Math.Sqrt((double)_state.velocityX * _state.velocityX + (double)_state.velocityY * _state.velocityY) / 10;
            }
        }

        /// <summary>Closest distance from (x, y) to where the body is now or was within the last <paramref name="ms"/> ms.</summary>
        /// <summary>RecentDist with the client's y squish (dy x 10/7), as the damage rule measures.</summary>
        double RecentDistIso(int x, int y, int ms)
        {
            int now = Environment.TickCount;
            double dx = _state.positionX - x, dy = (_state.positionY - y) / 0.7, best = dx * dx + dy * dy;
            for (int i = 0; i < 16; i++)
            {
                if (_histT[i] == 0 || now - _histT[i] > ms) continue;
                dx = _histX[i] - x; dy = (_histY[i] - y) / 0.7;
                double d = dx * dx + dy * dy;
                if (d < best) best = d;
            }
            return Math.Sqrt(best);
        }

        internal double RecentDist(int x, int y, int ms)
        {
            int now = Environment.TickCount;
            double dx = _state.positionX - x, dy = _state.positionY - y, best = dx * dx + dy * dy;
            for (int i = 0; i < 16; i++)
            {
                if (_histT[i] == 0 || now - _histT[i] > ms) continue;
                dx = _histX[i] - x; dy = _histY[i] - y;
                double d = dx * dx + dy * dy;
                if (d < best) best = d;
            }
            return Math.Sqrt(best);
        }

        static bool Extend(ref int until, int now, int ticks)
        {
            if (ticks <= 0) return false;
            int t = now + ticks * 10;
            if (t - until > 0) until = t;
            return true;
        }

        // ---------------------------------------------------------------- ammo
        void SetupAmmo()
        {
            int kn = _kit.Length;
            _ammoSlot = new int[kn]; _ammoPer = new int[kn];
            List<int> ids = new List<int>(4);
            for (int i = 0; i < kn; i++)
            {
                _ammoSlot[i] = -1;
                ItemInfo it = AssetManager.Manager.getItemByID(_kit[i].Id);
                int aid = 0, per = 0, cap;
                if (it == null || !it.getAmmoType(out aid, out per, out cap) || aid <= 0 || per <= 0) continue;
                int slot = ids.IndexOf(aid);
                if (slot < 0) { slot = ids.Count; ids.Add(aid); }
                _ammoSlot[i] = slot; _ammoPer[i] = per;
            }
            _ammo = new int[ids.Count]; _ammoStart = new int[ids.Count];
            _ammoIds = ids.ToArray();
            SpareDrops = _class == SK_HEAVY ? 0 : 2;                            // ammo it carries for teammates this life (CTFXLines.cs)
            for (int k = 0; k < ids.Count; k++)
            {
                _ammo[k] = _ammoStart[k] = Math.Max(1, (int)Math.Round(StartAmmo(_class, ids[k]) * Mgr.AmmoScale));
                CTFXShop.Build sb = CTFXShop.Of(_kitId);                         // a zone build carries the ammo it buys
                int bought;
                if (sb != null && sb.Ammo.TryGetValue(ids[k], out bought)) _ammo[k] = _ammoStart[k] = Math.Max(1, (int)Math.Round(bought * Mgr.AmmoScale));
                // never above the item's own limit in the zone (Repulsor is 1; the grenade default of 4 above is for items without one)
                ItemInfo ai = AssetManager.Manager.getItemByID(ids[k]);
                if (ai != null && ai.maxAllowed > 0 && _ammoStart[k] > ai.maxAllowed) _ammo[k] = _ammoStart[k] = ai.maxAllowed;
            }
            _rebuying = false;
        }

        /// <summary>Kit entry i cannot fire for lack of ammo (always false with ammo=0 or for an item that uses none).</summary>
        bool Dry(int i)
        {
            return Mgr.Ammo && _ammoSlot != null && i >= 0 && i < _ammoSlot.Length && _ammoSlot[i] >= 0 && _ammo[_ammoSlot[i]] < _ammoPer[i];
        }

        bool AllDry()
        {
            if (_kit == null || _kit.Length == 0) return false;
            for (int i = 0; i < _kit.Length; i++)
                if (!Dry(i) && (_badMask & (1 << i)) == 0) return false;
            return true;
        }

        /// <summary>Whole kit dry: back off (a post holds its post) and refill after 4-6 s, as a player would ?buy.</summary>
        void StartRebuy(int now)
        {
            _rebuying = true;
            // the real ways come first (a pile, a teammate drop, DropShip Recall: CTFXLines.cs); this on-the-spot refill is the fallback
            _rebuyUntil = now + (Mgr.Cmdr != null && Mgr.Cmdr.On ? 25000 : 4000) + _rng.Next(2001);
            Mgr.DryEvents++;
            if (Style != CTFXStyle.Post && !_retreating)
            {
                _retreating = true;
                _retreatUntil = Math.Min(_rebuyUntil, now + 6000);
                if (_pursuing) EndPursuit();
                Mgr.RetreatGoal(Group, out _rtx, out _rty, this);
                CancelPath();
            }
            if (Mgr.DryEvents <= 3)
                Mgr.WarnOnce("ammo #" + Mgr.DryEvents + ": " + Style + " " + ClassName(_class) + " bot ran its whole kit dry, rebuying in " + (_rebuyUntil - now) + " ms");
        }

        // ---------------------------------------------------------------- resupply (Travis 10-13: a real loop, CTFXLines.cs)
        int[] _ammoIds;
        int _suppNext;
        internal int SpareDrops;            // ammo piles it can still drop for a teammate this life
        internal int RecallAt;              // when it last used DropShip Recall
        internal bool Rebuying { get { return _rebuying; } }

        /// <summary>The ammo it is shortest of: item id, share left (1 = full) and its full stock. False = no ammo use.</summary>
        internal bool AmmoNeed(out int ammoId, out double frac, out int start)
        {
            ammoId = 0; frac = 1.0; start = 0;
            if (!Mgr.Ammo || _ammo == null || _ammoIds == null) return false;
            for (int k = 0; k < _ammo.Length; k++)
            {
                double f = _ammoStart[k] > 0 ? _ammo[k] / (double)_ammoStart[k] : 1.0;
                if (f < frac) { frac = f; ammoId = _ammoIds[k]; start = _ammoStart[k]; }
            }
            return ammoId != 0;
        }

        /// <summary>A picked up pile: into its stock (at most 1.5 x a full one); a rebuy in progress is over.</summary>
        internal void AddAmmo(int ammoId, int qty)
        {
            if (_ammoIds == null) return;
            int k = Array.IndexOf(_ammoIds, ammoId);
            if (k < 0) return;
            _ammo[k] = Math.Min(_ammoStart[k] * 3 / 2, Math.Max(0, _ammo[k]) + qty);
            if (_rebuying && !AllDry()) { _rebuying = false; _retreating = false; _curWep = -1; }
        }

        /// <summary>Run straight away from (fx, fy) to dist px from it (out of an anti-warp field before warping out, as a player does).</summary>
        internal bool Retreating { get { return _retreating; } }

        /// <summary>Retreat to tile (tx, ty) for up to ms (a route node the planner can reach).</summary>
        internal void RetreatTo(int now, int tx, int ty, int ms)
        {
            _rtx = tx; _rty = ty;
            _retreating = true; _retreatUntil = now + ms;
            if (_pursuing) EndPursuit();
            CancelPath();
        }

        internal void BreakAway(int now, int fx, int fy, int dist)
        {
            double dx = _state.positionX - fx, dy = _state.positionY - fy, d = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
            Mgr.NearestOpenPx(fx + (int)(dx / d * dist), fy + (int)(dy / d * dist), out _rtx, out _rty);
            _retreating = true; _retreatUntil = now + 4000;
            if (_pursuing) EndPursuit();
            CancelPath();
        }

        /// <summary>Plays alone (a bountier, an ADHD loner, a one-bot team): recalls and walks back out, never asks for a summon.</summary>
        internal bool SoloPlayer { get { return Style == CTFXStyle.Adhd || Group == null || Group.Slots.Count <= 1; } }   // a pack killer is summoned by its pack (CTFXBounty)

        /// <summary>Nobody summoned it: out of the ship through a portal in 0.5-2 s.</summary>
        internal void LeaveShipSoon(int now) { if (InDropship) _shipReadyAt = now + 500 + _rng.Next(1501); }

        /// <summary>DropShip Recall (zone item 46, as a player uses it): back to its spot in the dropship, where the store refills it.</summary>
        internal bool Recall(int now)
        {
            if (MarkX == 0 || InDropship || IsDead || _carrying || !CTFXSkillLogic.Allowed(46, _class)) return false;
            if (Mgr.AntiWarpedAt(_state.positionX, _state.positionY, _team, this) != null) return false;
            Teleported(MarkX, MarkY, now);
            _retreating = false;
            BackToShip(now, SoloPlayer ? 2000 + _rng.Next(3001) : 27000);       // a team player waits in the ship for its summon (CTFXLines)
            RecallAt = now;
            return true;
        }

        /// <summary>Suppressive fire: its first loaded gun that reaches (x, y), a few units of spread. Bullets only.</summary>
        bool SuppressFire(int now, int x, int y)
        {
            double ex = x - _state.positionX, ey = y - _state.positionY, d = Math.Sqrt(ex * ex + ey * ey);
            for (int i = 0; i < _kit.Length; i++)
            {
                if ((_badMask & (1 << i)) != 0 || _ammoSlot == null || _ammoSlot[i] < 0 || Dry(i) || _kit[i].Max < d * 0.8 || !CanShoot(now, i)) continue;
                if (EnergyCost(_kit[i].Id) > _energy) continue;
                if (FireNow(now, i, WrapYaw(YawTo(ex, ey) + _rng.Next(-4, 5)), d)) return true;
            }
            return false;
        }

        void RefillAmmo()
        {
            _rebuying = false;
            if (_ammo != null) for (int k = 0; k < _ammo.Length; k++) _ammo[k] = _ammoStart[k];
            _curWep = -1;                                                       // pick a weapon again on this think
        }

        // ---------------------------------------------------------------- combat movement
        /// <summary>
        /// Calmer strafe (stopgap until a proper movement model): the strafe direction flips every 900-2500 ms, and on each
        /// flip there is a 35% chance that the whole period is a no-strafe period (hold still, or move along the range axis
        /// only). Never strafes while the target is beyond the preferred range of the equipped weapon: it closes in instead
        /// (a post only holds: it stays on its leash). The stand / scoot fire modes (FireModeStep) are not touched.
        /// </summary>
        bool CombatMove(int now, int px, int py, double dx, double dy, double dist, float aimYaw, out float moveYaw)
        {
            moveYaw = 0f;
            if (now - _nextStrafe >= 0)
            {
                _strafeDir = -_strafeDir;
                _nextStrafe = now + _pStrafeLo + _rng.Next(_pStrafeHi - _pStrafeLo + 1);   // its own rhythm (personality)
                // a stop is rare and short (to steady the aim); most periods mix a step in or out into the strafe
                _strafeHold = _rng.NextDouble() < _pHold * 0.35;
                if (_strafeHold) _nextStrafe = now + 250 + _rng.Next(451);
                _pedal = _rng.NextDouble() < _pPedal ? (_rng.Next(2) == 0 ? 1 : -1) : 0;
            }
            // preferred range of the equipped weapon; a close-range weapon (shotgun) is kept 20 px outside the back-off range
            // so that closing in and backing off never fight each other
            double prefR = double.MaxValue;
            if (_kit != null && _curWep >= 0 && _curWep < _kit.Length)
            {
                prefR = _kit[_curWep].Pref;
                if (prefR < _minR + 20) prefR = _minR + 20;
            }
            if (Style == CTFXStyle.Post)
            {
                int tx, ty;
                PostAnchor(out tx, out ty);
                double ex = tx * 16 + 8 - px, ey = ty * 16 + 8 - py;
                if (ex * ex + ey * ey > 48.0 * 48.0) { moveYaw = YawTo(ex, ey); return LineMove(now, px, py, aimYaw, ref moveYaw); }   // 3-tile leash, never retreats
                if (_strafeHold || dist > prefR) return false;                                  // no strafing: stand on the post
                return StrafeYaw(now, px, py, aimYaw, out moveYaw);
            }
            if (Style == CTFXStyle.Sniper)
                return false;                                                               // holds still and shoots
            // The range it wants: the weapon's preferred range inside its band. How far off it is decides the pedal: out of reach
            // it closes in on a zig-zag (strafe + forward), too close it backs off strafing, in the band each strafe period has
            // its own rolled step in, step out or none. Moves are relative to the aim: 0 forward, 30 forward + strafe, 60
            // strafe, 90 back + strafe, 120 back (yaw units, Drive turns them into the keys a player holds).
            double want = prefR == double.MaxValue ? (_minR + _maxR) / 2.0 : Math.Min(prefR, _maxR) * 0.85;
            if (want < _minR + 20) want = _minR + 20;
            double err = (dist - want) / Math.Max(50.0, want * 0.3);
            int pedal = _pedal;
            if (err > 1.0 || dist > _maxR || dist > prefR) pedal = 1;
            else if (err < -1.0 || dist < _minR) pedal = -1;
            else if (err > 0.35 && pedal < 0) pedal = 0;                                    // a bit far: no backing away
            else if (err < -0.35 && pedal > 0) pedal = 0;                                   // a bit close: no stepping in
            float side = _strafeDir > 0 ? 1f : -1f, off;
            if (_strafeHold)
            {
                if (pedal == 0) return false;                                               // a short stop to steady the aim
                off = pedal > 0 ? 0f : 120f;                                                // or a straight step in / out
            }
            else if (pedal > 0) off = dist > want * 1.8 ? 22f : 30f;
            else if (pedal < 0) off = 90f;
            else off = 60f;
            moveYaw = WrapYaw(aimYaw + side * off);
            if (ProbeOpen(px, py, moveYaw, 40) >= 32) return true;
            float mirror = WrapYaw(aimYaw - side * off);
            if (off > 0f && off < 120f && ProbeOpen(px, py, mirror, 40) >= 32)
            {   // walled on this side: the same move to the other side
                _strafeDir = -_strafeDir;
                moveYaw = mirror;
                return true;
            }
            if (pedal != 0)
            {
                moveYaw = WrapYaw(aimYaw + (pedal > 0 ? 0f : 120f));
                if (ProbeOpen(px, py, moveYaw, 32) >= 32) return true;
            }
            return StrafeYaw(now, px, py, aimYaw, out moveYaw);
        }

        // ---------------------------------------------------------------- client keys and wall handling
        /// <summary>
        /// THE movement call, once per poll (every path of Think ends here): turns the wanted move direction and the facing into
        /// the keys a player would hold. Rotation: KeyRotate toward faceYaw, none inside 3 units. Movement (move == false lifts
        /// thrust and strafe): rel = moveYaw minus the CURRENT yaw, 8-way like the client bots - up to 15 units off the nose is
        /// thrust, 15-45 thrust + strafe, 45-75 strafe, 75-105 back + strafe, beyond that back; the strafe side follows the sign of
        /// rel (positive = clockwise of the nose = right). Yaw units: 240 per turn, 0 = N, 60 = E.
        /// </summary>
        void Drive(bool move, float moveYaw, float faceYaw)
        {
            _driven = true;
            CTFXClientMove cm = _cm;
            if (cm == null) return;
            float yaw = _state.yaw;
            float d = YawDelta(faceYaw, yaw);                   // > 0: the target is clockwise of the nose, so the yaw must rise
            int now = Environment.TickCount;
            cm.KeyRotate = now - _antiRotateUntil < 0 ? 0 : (d > 3f ? 1 : (d < -3f ? -1 : 0));   // stunned: cannot turn
            int kt = 0, ks = 0;
            if (move && now - _antiThrustUntil >= 0)                          // stunned: no thrust or strafe (it still slides)
            {
                float rel = YawDelta(moveYaw, yaw), a = rel < 0f ? -rel : rel;
                int side = rel < 0f ? -1 : 1;
                int band = a <= 15f ? 0 : a <= 45f ? 1 : a <= 75f ? 2 : a <= 105f ? 3 : 4;
                if (_band >= 0 && band != _band)
                {   // 11-08 jitter: stays in the band it was in until the heading is 6 yaw units (9 deg) past the edge, so a heading on an edge does not flip the keys poll by poll
                    float lo = _band == 0 ? -99f : _band == 1 ? 15f : _band == 2 ? 45f : _band == 3 ? 75f : 105f;
                    float hi = _band == 0 ? 15f : _band == 1 ? 45f : _band == 2 ? 75f : _band == 3 ? 105f : 999f;
                    if (a > lo - 6f && a <= hi + 6f) band = _band;
                }
                _band = band;
                switch (band)
                {
                    case 0: kt = 1; break;
                    case 1: kt = 1; ks = side; break;
                    case 2: ks = side; break;
                    case 3: kt = -1; ks = side; break;
                    default: kt = -1; break;
                }
            }
            else _band = -1;
            if (move && (kt != 0 || ks != 0) && !KeysClear(yaw, kt, ks))
            {   // wall sliding (Travis 10-13: bots bumped into physics without knowing): the clear key pair nearest the wanted heading
                int bkt = 0, bks = 0;
                float bd = 999f;
                for (int c = 0; c < 8; c++)
                {
                    int t = KeyT[c], s2 = KeyS[c];
                    float dir = WrapYaw(yaw + KeyRel(t, s2));
                    float off = Math.Abs(YawDelta(dir, moveYaw));
                    if (off >= bd || off > 90f || !KeysClear(yaw, t, s2)) continue;       // never more than 135 deg off the wanted way
                    bd = off; bkt = t; bks = s2;
                }
                if (bd < 999f) { kt = bkt; ks = bks; Mgr.Slides++; }      // nothing clear (already brushing a wall): keep the keys, the physics slides it
            }
            cm.KeyThrust = kt;
            cm.KeyStrafe = ks;
        }

        int _band = -1;                                                   // the 8-way key band the heading is in (Drive's hysteresis)
        static readonly int[] KeyT = new int[] { 1, 1, 0, -1, -1, -1, 0, 1 }, KeyS = new int[] { 0, 1, 1, 1, 0, -1, -1, -1 };

        /// <summary>Yaw units from the nose that a key pair moves the body (30 units = 45 deg).</summary>
        static float KeyRel(int kt, int ks)
        {
            if (kt > 0) return ks * 30f;
            if (kt < 0) return ks == 0 ? 120f : ks * 90f;
            return ks * 60f;
        }

        /// <summary>Does the whole body (radius 8) fit 10 and 20 px ahead along the way this key pair moves it? (world y squished 7/10)</summary>
        bool KeysClear(float yaw, int kt, int ks)
        {
            double r = WrapYaw(yaw + KeyRel(kt, ks)) * 1.5 * Math.PI / 180.0;
            double ux = Math.Sin(r), uy = -0.7 * Math.Cos(r), n = Math.Sqrt(ux * ux + uy * uy);
            ux /= n; uy /= n;
            int px = _state.positionX, py = _state.positionY;
            return Mgr.BodyFits(px + (int)Math.Round(ux * 10), py + (int)Math.Round(uy * 10))
                && Mgr.BodyFits(px + (int)Math.Round(ux * 20), py + (int)Math.Round(uy * 20));
        }

        /// <summary>The way out of a pin: of 16 headings, the one with the longest body-wide free run (up to 96 px).</summary>
        float OpenestYaw(int px, int py)
        {
            float best = _state.yaw;
            int bestRun = -1;
            for (int k = 0; k < 16; k++)
            {
                float y = k * 15f;
                double r = y * 1.5 * Math.PI / 180.0;
                double ux = Math.Sin(r), uy = -0.7 * Math.Cos(r), n = Math.Sqrt(ux * ux + uy * uy);
                ux /= n; uy /= n;
                int run = 0;
                for (int s = 12; s <= 96; s += 12)
                {
                    if (!Mgr.BodyFits(px + (int)Math.Round(ux * s), py + (int)Math.Round(uy * s))) break;
                    run = s;
                }
                if (run > bestRun) { bestRun = run; best = y; }
            }
            return best;
        }

        void ReleaseKeys()
        {
            CTFXClientMove cm = _cm;
            if (cm != null) { cm.KeyThrust = 0; cm.KeyStrafe = 0; cm.KeyRotate = 0; }
        }

        /// <summary>Signed shortest turn from yaw from to yaw to, in yaw units (-120..120]; positive = clockwise = the yaw rises.</summary>
        static float YawDelta(float to, float from)
        {
            float d = (to - from) % 240f;
            if (d > 120f) d -= 240f;
            else if (d <= -120f) d += 240f;
            return d;
        }

        /// <summary>A solid tile within 28 px of (x, y), 8 directions: the Disruptor goes off on it and the splash reaches a body standing there.</summary>
        bool WallBeside(int x, int y)
        {
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4.0;
                if (SolidPx(x + (int)(Math.Cos(a) * 28), y + (int)(Math.Sin(a) * 28))) return true;
            }
            return false;
        }

        bool TargetAtWall()
        {
            Helpers.ObjectState ts = _tgtP != null ? _tgtP._state : _tgtB != null ? _tgtB._state : null;
            return ts != null && WallBeside(ts.positionX, ts.positionY);
        }

        /// <summary>Wall test on a pixel (level edge = wall), the same rule the movement controller collides with.</summary>
        bool SolidPx(int x, int y)
        {
            if (x < 0 || y < 0) return true;
            int tx = x >> 4, ty = y >> 4;
            if (tx >= _arena._levelWidth || ty >= _arena._levelHeight) return true;
            return _arena._tiles[ty * _arena._levelWidth + tx].Blocked;
        }

        /// <summary>
        /// Free run along the yaw in px, up to dist: samples one tile apart (16, 32, ...) starting at the bot centre; the result is
        /// the distance of the last open sample before the first wall (dist = nothing in the way). World y is squished 7/10.
        /// </summary>
        int ProbeOpen(int px, int py, float yaw, int dist)
        {
            double r = yaw * 1.5 * Math.PI / 180.0;
            double ux = Math.Sin(r), uy = -0.7 * Math.Cos(r), n = Math.Sqrt(ux * ux + uy * uy);
            ux /= n; uy /= n;
            for (int s = 16; s <= dist; s += 16)
                if (SolidPx(px + (int)Math.Round(ux * s), py + (int)Math.Round(uy * s))) return s - 16;
            return dist;
        }

        /// <summary>
        /// The strafe of combat: sideways to the target (aim yaw +- 60 units). A wall within 48 px on the strafe side flips the
        /// strafe now (the next flip is re-rolled). Walls on both sides: the side with more room, but only with 32 px or more
        /// of it, else a hold (false) - so a narrow corridor still gets a short strafe while a wall-hugging bot never runs into one.
        /// </summary>
        bool StrafeYaw(int now, int px, int py, float aimYaw, out float moveYaw)
        {
            moveYaw = WrapYaw(aimYaw + (_strafeDir > 0 ? 60f : -60f));
            int room = ProbeOpen(px, py, moveYaw, 48);
            if (room >= 48) return true;
            float other = WrapYaw(aimYaw + (_strafeDir > 0 ? -60f : 60f));
            int otherRoom = ProbeOpen(px, py, other, 48);
            if (otherRoom > room)
            {
                _strafeDir = -_strafeDir;
                _nextStrafe = now + _pStrafeLo + _rng.Next(_pStrafeHi - _pStrafeLo + 1);
                _strafeHold = false;
                moveYaw = other;
                room = otherRoom;
            }
            return room >= 32;
        }

        /// <summary>Approach / back off along moveYaw: a wall within 32 px strafes instead (a hold when the sides are walled too).</summary>
        bool LineMove(int now, int px, int py, float aimYaw, ref float moveYaw)
        {
            if (ProbeOpen(px, py, moveYaw, 32) >= 32) return true;
            return StrafeYaw(now, px, py, aimYaw, out moveYaw);
        }

        /// <summary>
        /// Pinned = in combat, keys down, yet under 16 px moved in 400 ms (the first window after the keys go down is warm-up:
        /// the thrust ramp alone can cost that much from a standstill). Then 300 ms toward the most open compass direction.
        /// </summary>
        void PinnedStep(int now, int px, int py, ref bool move, ref float moveYaw)
        {
            if (!move) { _pinT = now; _pinX = px; _pinY = py; _pinArmed = false; _pinUntil = now; return; }       // keys up: no window
            if (now - _pinUntil < 0)                                                                              // backing out
            {
                move = true; moveYaw = _pinYaw;
                _pinT = now; _pinX = px; _pinY = py; _pinArmed = false;
                return;
            }
            if (_strafeDir != _pinDir) { _pinDir = _strafeDir; _pinT = now; _pinX = px; _pinY = py; _pinArmed = false; return; }   // the strafe turned round: it stops on purpose
            if (now - _pinT < 400) return;
            int mx = px - _pinX, my = py - _pinY;
            bool still = mx * mx + my * my < 16 * 16;
            _pinT = now; _pinX = px; _pinY = py;
            if (!still || !_pinArmed) { _pinArmed = true; return; }
            _pinYaw = OpenYaw(px, py, moveYaw);
            _pinUntil = now + 300;
            _pinArmed = false;
            move = true; moveYaw = _pinYaw;
        }

        /// <summary>Yaw of the most open of the 8 compass directions: most open tiles within 3 tiles (a diagonal also needs its two
        /// shoulder tiles open, the level edge is a wall); ties go to the direction closest to wantYaw.</summary>
        float OpenYaw(int px, int py, float wantYaw)
        {
            float best = wantYaw;
            int bestScore = int.MinValue;
            for (int k = 0; k < 8; k++)
            {
                int ddx = Dx8[k] * 16, ddy = Dy8[k] * 16, open = 0;
                for (int s = 1; s <= 3; s++)
                {
                    if (SolidPx(px + ddx * s, py + ddy * s)) break;
                    if (ddx != 0 && ddy != 0 && (SolidPx(px + ddx * s, py + ddy * (s - 1)) || SolidPx(px + ddx * (s - 1), py + ddy * s))) break;
                    open++;
                }
                float y = YawTo(Dx8[k], Dy8[k]);
                int score = open * 1000 - (int)Math.Abs(YawDelta(y, wantYaw));
                if (score > bestScore) { bestScore = score; best = y; }
            }
            return best;
        }

        // ---------------------------------------------------------------- pursuit
        /// <summary>
        /// Sight of the target is gone: walk (path) to where it was last seen, for up to 2.5 s after the last sighting, then
        /// resume the previous goal. Posts and snipers hold their ground. Normal target scanning keeps running meanwhile.
        /// </summary>
        bool PursueMove(int now, int px, int py, out float moveYaw)
        {
            moveYaw = 0f;
            if (_traveling) { if (_pursuing) EndPursuit(); return false; }      // on its way somewhere: no chasing
            if (!_lkValid || Style == CTFXStyle.Post || Style == CTFXStyle.Sniper) { if (_pursuing) EndPursuit(); return false; }
            if (now - _lkTick >= 2500) { EndPursuit(); return false; }
            if (!_pursuing)
            {
                if (now - _pursueCool < 0) return false;        // a pursuit just ended: no flip-flopping on a corner edge
                _pursuing = true; _pursueHadGoal = _hasGoal; CancelPath();
            }
            if (PathMove(now, px, py, _lkX >> 4, _lkY >> 4, out moveYaw)) return true;
            EndPursuit();                                       // arrived at the last known spot
            return false;
        }

        void EndPursuit()
        {
            if (_pursuing)
            {
                _pursuing = false;
                _pursueCool = Environment.TickCount + 800;
                _hasGoal = _pursueHadGoal;                      // the walk must not cost the bot its previous goal
                CancelPath();
                _navActive = false;
            }
            _lkValid = false;
        }

        // ---------------------------------------------------------------- hot spots (a hair of ADHD)
        void TrackHotSpot(int now)
        {
            int x, y;
            if (!Mgr.GetHotSpot(now, out x, out y)) { HotHave = false; return; }
            if (!HotHave || x != HotX || y != HotY) { HotHave = true; HotX = x; HotY = y; HotSeen = now; }
        }

        static void NoteHotSpot(int now, int x, int y)
        {
            HotHave = true; HotX = x; HotY = y; HotSeen = now;
        }

        static bool InZone(CTFXZone z, int tx, int ty, int extraTiles)
        {
            if (z == null || z.Cx == null || z.Cy == null) return false;
            long r = z.R + extraTiles, r2 = r * r;
            for (int i = 0; i < z.Cx.Length && i < z.Cy.Length; i++)
            {
                long ex = tx - z.Cx[i], ey = ty - z.Cy[i];
                if (ex * ex + ey * ey <= r2) return true;
            }
            return false;
        }

        /// <summary>
        /// 12% of the time head for the newest hot spot instead of the usual goal: it must be under 60 s old and within
        /// 160 tiles of the bot (a guard also needs it inside its home radius + 40 tiles). False = use the usual goal.
        /// </summary>
        bool HotGoal(int now, bool homeBound, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (_rng.NextDouble() >= 0.12 || !HotHave || now - HotSeen >= 60000) return false;
            double ex = HotX - _state.positionX, ey = HotY - _state.positionY;
            if (ex * ex + ey * ey > 2560.0 * 2560.0) return false;                          // 160 tiles
            if (homeBound && !InZone(Group.Home, HotX >> 4, HotY >> 4, 40)) return false;
            Mgr.NearestOpenPx(HotX, HotY, out tx, out ty);
            return true;
        }

        // ---------------------------------------------------------------- navigation
        CTFXBot FollowTarget()
        {
            if (_carrying) return null;                                     // a flag carrier runs it home, not with the squad
            if (Style != CTFXStyle.Assault && Style != CTFXStyle.Drain) return null;
            if (Mgr.Cmdr != null && Mgr.Cmdr.Manages(this)) return null;                    // its own activity, nobody's tail
            if (Style == CTFXStyle.Assault && Group.Storm != null && Mgr.Squads.Spread(Group, this)) return null;   // fire teams: own objectives
            if (Style == CTFXStyle.Drain && Mgr.Bounty != null)
            {   // a bounty pack works for its killer: it rides with the bountier, and runs down the pack victim when there is one
                Player vp; CTFXBot vb;
                if (Mgr.Bounty.PackVictim(this, out vp, out vb)) return null;
                CTFXBot fin = Mgr.Bounty.PackFinisher(Group);
                if (fin != null) return fin;
            }
            int si = (int)Style;
            ushort lid = Group.Leader[si];
            if (lid == 0 || lid == _id) { Group.Leader[si] = _id; return null; }
            CTFXBot l = Mgr.GetBot(lid);
            if (l == null || l.IsDead || l.Released) { Group.Leader[si] = _id; return null; }
            if (l.InDropship) return null;                                  // its leader is still in the dropship: its own goal for now
            return l;
        }

        CTFXZone RoamZone()
        {
            double u = _rng.NextDouble();
            if (u < 0.5) return Group.Home;
            if (u < 0.7) return Mgr.ZoneBrown;
            if (u < 0.8) return Mgr.ZoneCenter;
            return Mgr.OtherZones[_rng.Next(Mgr.OtherZones.Length)];
        }

        int PauseMs()
        {
            if (Mgr.Mini != null) return Mgr.Mini.PauseMs(this);                                   // MiniTP: short while sweeping, long only at a held firing position
            int cp = Mgr.Cmdr != null ? Mgr.Cmdr.PauseMs(this) : -1;
            if (cp >= 0) return cp;                                                             // by activity (CTFXCommand)
            switch (Style)
            {
                case CTFXStyle.Assault:
                    // a squad holding the enemy end keeps sweeping it (10-12 soak: 5 stood bunched in the flag room)
                    if (Group != null && Group.Storm != null && Group.Storm.Phase == CTFXStorm.Hold) return 150 + _rng.Next(451);
                    return 300 + _rng.Next(1200);
                case CTFXStyle.Post: return 2500 + _rng.Next(3001);                             // a shuffle every 2.5-5.5 s (10-15)
                case CTFXStyle.Sniper: return 0;
                default: return 2000 + _rng.Next(4001);
            }
        }

        void ChooseGoal(int now)
        {
            int cx, cy;
            if (Slot != null && Slot.AttachSummoned && !_carrying && Mgr.Chat != null && Mgr.Chat.AttachGoal(this, out cx, out cy))
            {   // Travis 10-21 (Waylander): a player summoned it with his own summoner - it stays around him, whatever its job (CTFXChat)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;          // NearestOpenPx gives tiles
                CancelPath();
                return;
            }
            if (Mgr.Mini != null && Mgr.Mini.Goal(this, now, out cx, out cy))
            {   // MiniTP (MiniTP\CTFXMini.cs): the ship, the field, the enemy base or the home anchors
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (Style == CTFXStyle.Drain && Mgr.Bounty != null && Mgr.Bounty.DrainerGoal(this, out cx, out cy))
            {   // a hunt pack drainer runs the victim down
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (Style == CTFXStyle.Bountier && Mgr.Bounty != null && Mgr.Bounty.Goal(this, out cx, out cy))
            {   // the trap spot, the watch spot, or the victim (CTFXBounty)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;          // NearestOpenPx gives tiles
                CancelPath();
                return;
            }
            if (Style == CTFXStyle.Maser && Mgr.Cmdr != null && Mgr.Cmdr.MaserGoal(this, out cx, out cy))
            {   // a siege: its spot outside the building, by the flag (CTFXSiege.cs)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (PocketGoal(now, out cx, out cy))
            {   // a medic glued to the team's best killer (CTFXElite.cs)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (Mgr.HuntGoal(this, out cx, out cy))
            {   // a bounty hunt: the tagger, then the hunters, on the victim (CTFXHunt.cs)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (CorridorGoal(now, out cx, out cy))
            {   // an Infiltrator perched at an end of top brown's hall corridor (CTFXInfil.cs)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (Mgr.ClanSiegeGoal(this, now, out cx, out cy))
            {   // the risen elite clan takes top brown as one (CTFXClan.cs)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (Mgr.Flags != null && Mgr.Flags.Goal(this, out cx, out cy))
            {   // carrying a flag home, or running for one (CTFXFlags)
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;          // NearestOpenPx gives tiles
                CancelPath();
                return;
            }
            if (Mgr.Chat != null && Mgr.Chat.AttachGoal(this, out cx, out cy))
            {   // with a human it answered (CTFXChat): around him
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;          // NearestOpenPx gives tiles
                CancelPath();
                return;
            }
            if (Mgr.Cmdr != null && Mgr.Cmdr.Goal(this, now, out cx, out cy))
            {   // the bot's own activity (CTFXCommand): anchor, defend, skirmish, raid, wander, hunt
                _job = null;
                _gtx = cx; _gty = cy; _hasGoal = true; _arrived = false;
                CancelPath();
                return;
            }
            if (Mgr.Routes != null && Group != null && (Group.HomeEnd != null || Group.CoreOf != null || Group.Home == Mgr.ZonePockets) && RouteGoal(now)) return;
            int tx = 0, ty = 0;
            switch (Style)
            {
                // a hair of ADHD: every goal-picking style but the post has a 12% chance of heading for the newest hot spot
                // (Assault / Drain only pick goals as squad leaders: followers follow)
                case CTFXStyle.Post:
                    if (now - PostHoldUntil < 0) { tx = _state.positionX >> 4; ty = _state.positionY >> 4; break; }   // pulled back: hold here
                    if (Mgr.Cmdr == null || !Mgr.Cmdr.PostGoal(this, now, out tx, out ty)) Mgr.PostTile(Slot != null ? Slot.PostIndex : 0, out tx, out ty);   // fanned / outdoors (CTFXSweep)
                    break;
                case CTFXStyle.Guard:
                    if (!HotGoal(now, true, out tx, out ty))
                    {
                        // brown guards stay inside the base: a random open tile near a random inner point
                        if (Group.Home == Mgr.ZoneBrown) Mgr.InnerGoal(-1, out tx, out ty);
                        else Mgr.RandomGoal(Group.Home, out tx, out ty);
                    }
                    break;
                case CTFXStyle.Assault:
                    // objective = the brown zone: AssaultGoal turns that into the base interior (BL 40% / BR 40% / BM 20%)
                    if (!HotGoal(now, false, out tx, out ty)) Mgr.AssaultGoal(Mgr.ZoneBrown, out tx, out ty);
                    break;
                case CTFXStyle.Sniper:
                    if (!HotGoal(now, false, out tx, out ty)) Mgr.OverwatchTile(Group.Home, out tx, out ty);
                    break;
                case CTFXStyle.Drain:
                    if (!HotGoal(now, false, out tx, out ty)) Mgr.RandomGoal(Mgr.ZonePockets, out tx, out ty);
                    break;
                case CTFXStyle.Adhd:
                    {
                        int hx, hy;
                        if (Mgr.GetHotSpot(now, out hx, out hy)) Mgr.NearestOpenPx(hx, hy, out tx, out ty);
                        else Mgr.RandomGoal(RoamZone(), out tx, out ty);
                        _adhdNext = now + 20000 + _rng.Next(20001);
                        break;
                    }
                default:
                    // roamers with home=brown: 60% of the picks are an inner point of the base, else the usual roam logic
                    if (Group.Home == Mgr.ZoneBrown && _rng.NextDouble() < 0.60) Mgr.InnerGoal(-1, out tx, out ty);
                    else if (!HotGoal(now, false, out tx, out ty)) Mgr.RandomGoal(RoamZone(), out tx, out ty);
                    break;
            }
            _gtx = tx; _gty = ty; _hasGoal = true; _arrived = false;
            CancelPath();
        }

        /// <summary>No-target navigation. Returns true when the bot should move (moveYaw set); hold = facing when it does not.</summary>
        bool Nav(int now, out float moveYaw, out float hold)
        {
            moveYaw = 0f;
            int px = _state.positionX, py = _state.positionY;
            hold = (Style == CTFXStyle.Post || Style == CTFXStyle.Sniper) ? WrapYaw(_scanBase + (now - _scanT0) * 0.008f) : (float)_state.yaw;
            float ay;
            if (Mini != null && Mgr.Mini.WatchYaw(this, now, out ay)) hold = ay;                // MiniTP (Travis 11-08): a holder faces where the enemy will come from, with the anchors' slow sweep
            else if (Mgr.Cmdr != null && Mgr.Cmdr.HoldYaw(this, now, out ay)) hold = ay;        // an anchor watches its choke
            else if (Style == CTFXStyle.Post && Mgr.Cmdr != null && Mgr.Cmdr.PostYaw(this, now, out ay)) hold = ay;   // the threat, else out of its room
            else if (Style != CTFXStyle.Post && Style != CTFXStyle.Sniper && Style != CTFXStyle.Stormer)
            {   // standing at a goal a player glances about: a new look 50 units either side every 1.5-3.5 s
                if (now - _lookAt >= 0) { _lookAt = now + 1500 + _rng.Next(2001); _lookYaw = WrapYaw(_state.yaw + _rng.Next(-50, 51)); }
                hold = _lookYaw;
            }

            if (InDropship && Mgr.Dropship != null && Mgr.Dropship.Ready)
            {   // gear up, walk into the nearest MapEnter portal; the portal's warp sends it out (CTFXDropship.Warp)
                if (Slot != null && Slot.Benched) { _shipReadyAt = now + 2000; _navActive = false; _why = "benched"; return false; }      // Travis 10-22: a human took its seat (CTFXRoom.cs): it waits here
                if (now - _shipReadyAt < 0) { _navActive = false; _why = "gearup"; return false; }
                if (Mgr.Cmdr != null && Mgr.Cmdr.MaserIdle(this)) { _shipReadyAt = now + 2000 + _rng.Next(2001); _navActive = false; _why = "ship idle"; return false; }
                if (Mgr.Dropship.InPortal(px, py) || now - _shipReadyAt > 30000)
                {   // 30 s walking without reaching a portal (counted from the end of its gear-up, not its spawn): the warp takes it anyway
                    if (now - _shipReadyAt > 30000) Mgr.Dropship.Stuck++;
                    long dwell = now - _shipSince;
                    if (Mgr.Dropship.Warp(this, now)) { InDropship = false; Mgr.Dropship.DwellMs += dwell; Mgr.Cmdr.Landed(this, now); if (Style == CTFXStyle.Maser) Mgr.Cmdr.MaserOuts++; }
                    return false;
                }
                int sx, sy;
                Mgr.Dropship.NearestPortal(px, py, out sx, out sy);
                moveYaw = YawTo(sx - px, sy - py);
                NavMoving(now, px, py);
                return true;
            }
            CTFXBot leader = FollowTarget();
            if (leader != null)
            {
                int lx = leader._state.positionX, ly = leader._state.positionY;
                // its own slot in a loose formation: _pFollowR px out at _pFollowAng off the leader's facing, plus a small personal
                // wander re-rolled every _pWanderMs; no room there (wall, no sight of the leader) = straight at the leader
                if (now - _wanderAt >= 0) { _wanderAt = now + _pWanderMs; _wanderX = _rng.Next(-40, 41); _wanderY = _rng.Next(-28, 29); }
                double fa = (leader._state.yaw + _pFollowAng) * 1.5 * Math.PI / 180.0;
                int sx = lx + (int)(Math.Sin(fa) * _pFollowR) + _wanderX, sy = ly - (int)(Math.Cos(fa) * _pFollowR * 0.7) + _wanderY;
                if (!Mgr.BodyFits(sx, sy) || !Mgr.Los(lx, ly, sx, sy)) { sx = lx; sy = ly; }
                double dx = sx - px, dy = sy - py, d = Math.Sqrt(dx * dx + dy * dy);
                double lxd = lx - px, lyd = ly - py, ld = Math.Sqrt(lxd * lxd + lyd * lyd);
                hold = WrapYaw(leader._state.yaw + _pFace);                // roughly the way its leader faces, each a little off
                // in its slot (not when a door region border lies between: then the door plan walks it)
                if ((d <= 30.0 || (sx == lx && sy == ly && ld <= 90.0)) && !(_door != null && Mgr.Doors != null && Mgr.Doors.Separated(px >> 4, py >> 4, lx >> 4, ly >> 4)))
                { _navActive = false; _why = "formation"; return false; }
                if (d <= 300.0 && CachedLos(_losL, now, 200000 + (int)leader._id, px, py, sx, sy))
                {
                    moveYaw = YawTo(dx, dy);
                    NavMoving(now, px, py);
                    return true;
                }
                return PathMove(now, px, py, sx >> 4, sy >> 4, out moveYaw);
            }

            if (!_hasGoal || (Style == CTFXStyle.Adhd && now - _adhdNext >= 0)) ChooseGoal(now);
            if (_job != null && _buildAt != 0) { _navActive = false; hold = _state.yaw; _why = "build"; return false; }   // building (BuildStep)
            double gx = _gtx * 16 + 8 - px, gy = _gty * 16 + 8 - py;
            if (gx * gx + gy * gy <= 48.0 * 48.0)                           // arrival: within 3 tiles
            {
                _navActive = false;
                if (!_arrived) { _arrived = true; _stuckCount = 0; _pauseUntil = Perching(now) ? _perchUntil : Pocketing ? now + 250 : now + PauseMs(); }
                // a sniper the commander moves about (not anchoring) picks its next perch like everyone else
                bool managed = Mgr.Cmdr != null && Mgr.Cmdr.Manages(this);
                bool holds = Style == CTFXStyle.Stormer || (Style == CTFXStyle.Sniper && !managed);
                _why = Style == CTFXStyle.Post ? "post" : Style == CTFXStyle.Stormer ? "hide" : holds || Perching(now) ? "perch"
                    : managed && Mgr.Cmdr.ActOf(this) == CTFXAct.Anchor ? "anchor"
                    : Style == CTFXStyle.Assault && Group != null && Group.Storm != null && Group.Leader[(int)CTFXStyle.Assault] == _id ? "squad" : "pause";
                if (!holds && now - _pauseUntil >= 0) ChooseGoal(now);
                return false;
            }
            return PathMove(now, px, py, _gtx, _gty, out moveYaw);
        }

        /// <summary>Move toward goal tile (gtx, gty) along the pathfinder path (straight when there is none).</summary>
        bool PathMove(int now, int px, int py, int gtx, int gty, out float moveYaw)
        {
            moveYaw = 0f;
            if (Mgr.Routes != null) return RouteMove(now, px, py, gtx, gty, out moveYaw);
            // doors: the native pathfinder never routes through one, so a goal in another region goes via the door plan.
            // Approach = path to the stand tile in front of the next door (goal replaced), Steer = straight through the
            // open door, Hold = waiting for it to open, Failed = the door did not work (blacklisted, pick a new goal)
            bool doorApproach = false;
            if (_door != null)
            {
                int dc = _door.Update(Mgr.Doors, now, px, py, gtx, gty);
                if (_door.Changed) { _door.Changed = false; CancelPath(); }    // the path was made for another goal
                if (dc == CTFXDoorPilot.Steer)
                {
                    moveYaw = YawTo(_door.TargetPx - px, _door.TargetPy - py);
                    _navActive = false;                                        // own timeouts: no stuck counting here
                    _doorSteer = true;                                         // Drive faces the way it runs (also in combat)
                    return true;
                }
                if (dc == CTFXDoorPilot.Hold) { _navActive = false; _why = "door"; return false; }
                if (dc == CTFXDoorPilot.Failed) { _navActive = false; _hasGoal = false; return false; }
                if (dc == CTFXDoorPilot.Approach) { gtx = _door.TargetTx; gty = _door.TargetTy; doorApproach = true; }
            }
            double tx = gtx * 16 + 8, ty = gty * 16 + 8;
            double gx = tx - px, gy = ty - py;
            if (!doorApproach && gx * gx + gy * gy <= 40.0 * 40.0) { _navActive = false; return false; }
            ConsumePath(now);
            if (WantPath(now, gtx, gty)) RequestPath(now, px, py, gtx, gty);
            if (_pn > 0)
            {
                while (_pi < _pn)
                {
                    double ex = _pxs[_pi] - px, ey = _pys[_pi] - py;
                    if (ex * ex + ey * ey <= 24.0 * 24.0) _pi++; else break;
                }
                if (_pi < _pn) { tx = _pxs[_pi]; ty = _pys[_pi]; }
            }
            moveYaw = YawTo(tx - px, ty - py);
            NavMoving(now, px, py);
            return true;
        }

        // ---------------------------------------------------------------- routes (CTFXRoutes.cs): no engine pathfinder
        /// <summary>
        /// Walk to goal tile (gtx, gty) on the route book: plan from the nearest waypoint the bot can walk straight to, to the
        /// waypoint nearest the goal, then steer waypoint to waypoint. A segment through a door: wait at the waypoint in front
        /// of it while the door is shut, opening it once a second (open / closed / open, CTFXDoors fact e); 7 tries, then the
        /// goal is dropped.
        /// </summary>
        bool RouteMove(int now, int px, int py, int gtx, int gty, out float moveYaw)
        {
            moveYaw = 0f;
            CTFXRouteBook rb = Mgr.Routes;
            int gx = gtx * 16 + 8, gy = gty * 16 + 8;
            double ex = gx - px, ey = gy - py;
            if (ex * ex + ey * ey <= 40.0 * 40.0) { _navActive = false; return false; }
            bool goalMoved = !_rpHave || Math.Abs(gx - _rpGoalX) + Math.Abs(gy - _rpGoalY) > 64;
            bool retry = _rpHave && _rp.Count == 0 && now - _rpLastPlan >= 3000;
            if ((goalMoved && now - _rpLastPlan >= 400) || retry) PlanRoute(now, px, py, gx, gy);
            if (_prei < _preX.Count)
            {   // off the network: the tile path onto it first
                double qx = _preX[_prei] - px, qy = _preY[_prei] - py;
                if (qx * qx + qy * qy <= 20.0 * 20.0) _prei++;
                if (_prei < _preX.Count) { moveYaw = YawTo(_preX[_prei] - px, _preY[_prei] - py); NavMoving(now, px, py); return true; }
            }
            int tx = gx, ty = gy;                               // past the last waypoint: straight at the goal
            int arrive = (Human != null ? Human.Arrive : 20) + Math.Min(20, (Math.Abs((int)_state.velocityX) + Math.Abs((int)_state.velocityY)) / 60);     // how early this seat cuts on to the next waypoint (11-08: +0-20 px with speed: the turn starts before the corner)
            while (_rpi < _rp.Count)
            {
                int lx, ly;
                LanePoint(rb, _rpi, px, py, out lx, out ly);
                double dx = lx - px, dy = ly - py;
                if (dx * dx + dy * dy > (double)arrive * arrive) break;
                _rpi++; _rpDoorAttempt = 0; _rpDoorNext = now;
            }
            if (_rpi < _rp.Count)
            {
                int n = _rp[_rpi];
                int door = _rpi > 0 ? rb.DoorBetween(_rp[_rpi - 1], n) : 0;
                if (door != 0 && Mgr.Doors != null && !Mgr.Doors.IsOpen(door))
                {
                    if (now - _rpDoorNext >= 0)
                    {
                        if (_rpDoorAttempt >= 7) { Mgr.Doors.Fails++; ClearRoute(); _hasGoal = false; _navActive = false; return false; }
                        Mgr.Doors.OpenDoor(door, _rpDoorAttempt % 3);
                        _rpDoorAttempt++; _rpDoorNext = now + 1000;
                    }
                    int pn = _rp[_rpi - 1];
                    double hx = rb.X[pn] - px, hy = rb.Y[pn] - py;
                    if (hx * hx + hy * hy > 24.0 * 24.0) { moveYaw = YawTo(hx, hy); NavMoving(now, px, py); return true; }
                    _navActive = false;
                    _why = "door";
                    return false;                                   // waiting in front of the shut door
                }
                if (door != 0) { tx = rb.X[n]; ty = rb.Y[n]; _doorSteer = true; }   // run straight through, facing the way we go
                else
                {
                    LanePoint(rb, _rpi, px, py, out tx, out ty);
                    if (Mini != null) _rpi += Mgr.Mini.Steer(this, now, px, py, gx, gy, ref tx, ref ty);        // MiniTP: a look-ahead point (skips waypoints it is already past), swung wide of a contact
                }
            }
            moveYaw = YawTo(tx - px, ty - py);
            NavMoving(now, px, py);
            return true;
        }

        /// <summary>The seat's own line: route waypoint _rp[idx] moved Human.Lane px to the side of the way it is walked (half that,
        /// or the waypoint itself, where the body does not fit or the step there is not walkable). Door waypoints stay exact.</summary>
        void LanePoint(CTFXRouteBook rb, int idx, int px, int py, out int lx, out int ly)
        {
            int n = _rp[idx];
            if (idx == _laneIdx && n == _laneN) { lx = _laneX; ly = _laneY; return; }
            lx = rb.X[n]; ly = rb.Y[n];
            bool doorNear = (idx > 0 && rb.DoorBetween(_rp[idx - 1], n) != 0) || (idx + 1 < _rp.Count && rb.DoorBetween(n, _rp[idx + 1]) != 0);
            if (Human != null && Human.Lane != 0 && !doorNear)
            {
                double fx = idx > 0 ? rb.X[_rp[idx - 1]] : px, fy = idx > 0 ? rb.Y[_rp[idx - 1]] : py;
                double ux = rb.X[n] - fx, uy = rb.Y[n] - fy, ul = Math.Sqrt(ux * ux + uy * uy);
                if (ul > 1.0)
                {
                    ux /= ul; uy /= ul;
                    for (int k = 2; k >= 1; k--)
                    {
                        int cx = (int)(rb.X[n] - uy * Human.Lane * k / 2.0), cy = (int)(rb.Y[n] + ux * Human.Lane * k / 2.0);
                        if (Mgr.BodyFits(cx, cy) && Mgr.WalkLine(rb.X[n], rb.Y[n], cx, cy)) { lx = cx; ly = cy; break; }
                    }
                }
            }
            _laneIdx = idx; _laneN = n; _laneX = lx; _laneY = ly;
        }

        void PlanRoute(int now, int px, int py, int gx, int gy)
        {
            CTFXRouteBook rb = Mgr.Routes;
            // MiniTP: the areas (ship rooms, bases, field) are sealed boxes; a goal in another one (a teammate rescue, a retreat, a hot spot) cannot be
            // walked to. Drop it so ChooseGoal gives the bot one of its own area (CTFXMini.SameArea)
            if (Mini != null && !Mgr.Mini.SameArea(px, py, gx, gy)) { _rp.Clear(); _rpHave = false; _hasGoal = false; Mgr.Mini.CrossDropped(this, px, py, gx, gy, _why); return; }
            _rpHave = true; _rpLastPlan = now; _rpGoalX = gx; _rpGoalY = gy; _rpi = 0; _rpDoorAttempt = 0; _rpDoorNext = now;
            Mgr.RoutePlans++;
            int from = rb.Nearest(px, py, 480, Mgr.WalkLine), to = rb.Nearest(gx, gy, 480, Mgr.WalkLine);
            if (from < 0 && _prei < _preX.Count && _preNode >= 0) from = _preNode;    // still on its way onto the network
            else if (from < 0 && now - _netFailAt >= 0)
            {   // nothing in a straight walk: the tile grid to the nearest waypoint
                if (Mgr.PathToNetwork(px, py, _preX, _preY, out _preNode)) { _prei = 0; from = _preNode; }
                else { _netFailAt = now + 5000; _preX.Clear(); _preY.Clear(); _prei = 0; }
            }
            else if (from >= 0) { _preX.Clear(); _preY.Clear(); _prei = 0; }
            if (from < 0) from = rb.Nearest(px, py, 640, null);              // no way found: head for the nearest one
            if (to < 0) to = rb.Nearest(gx, gy, 640, null);
            if (from < 0 || to < 0 || !rb.Plan(from, to, _rp, DoorCostFn, EdgeCostFn)) { _rp.Clear(); Mgr.RouteFails++; if (Mini != null) Mgr.Mini.PlanFailed(this, px, py, gx, gy, from, to); return; }
            Mgr.NoteDoors(_rp);
            // the first waypoint can be behind us: start at the second one when we can walk straight to it
            if (_rp.Count >= 2 && rb.DoorBetween(_rp[0], _rp[1]) == 0 && Mgr.WalkLine(px, py, rb.X[_rp[1]], rb.Y[_rp[1]])) _rpi = 1;
        }

        void ClearRoute()
        {
            _rp.Clear(); _rpi = 0; _rpHave = false;
            _preX.Clear(); _preY.Clear(); _prei = 0; _preNode = -1;
        }

        /// <summary>
        /// Route-book goals for a group that lives in a base end (quadrant lock): guards walk their end's patrol loop, an assault
        /// leader heads for the enemy end's flag room down the raid route, a sniper watches the core from its own main entrance,
        /// roamers and ADHD stay inside their building, the bounty pack (home=pockets) roams the caves and plaza links.
        /// False = no route goal for this bot (the old goal logic runs).
        /// </summary>
        bool RouteGoal(int now)
        {
            CTFXBotManager m = Mgr;
            string home = Group.HomeEnd, enemy = m.OtherEnd(home), quad = Group.Quad;
            int tx = 0, ty = 0;
            bool got;
            if (Group.CoreOf != null) return CoreGoal(now);
            if (home != null && Style == CTFXStyle.Stormer && Group.Storm != null && Group.Storm.Phase == CTFXStorm.Hold) got = m.Squads.MemberGoal(Group, this, out tx, out ty);
            else if (home != null && Style == CTFXStyle.Stormer) got = enemy != null && HideSpot(enemy, out tx, out ty);
            else if (home != null && Style == CTFXStyle.Assault && Group.Storm != null)
                got = m.Squads.Spread(Group, this) ? m.Squads.MemberGoal(Group, this, out tx, out ty) : m.Squads.LeaderGoal(Group, out tx, out ty);
            else if (home != null && Style == CTFXStyle.Engineer && m.Turrets.HasSets(this))
            {   // the next missing turret of its sets, or a spot beside its first set when they all stand
                _job = m.Turrets.NextJob(this, now, out tx, out ty);
                got = tx > 0;
            }
            else if (home == null && Group.Outside)
            {   // home=outside: the neutral landing areas, nearer likelier (CTFXDropship.OutsideGoal)
                got = m.Dropship.OutsideGoal(this, m.Cmdr.HerdK, out tx, out ty) || m.RandomNeutralNode(out tx, out ty);
                if (Style == CTFXStyle.Adhd) _adhdNext = now + 20000 + _rng.Next(20001);
            }
            else if (home == null)
            {
                if (Style != CTFXStyle.Drain && Style != CTFXStyle.Adhd) return false;
                got = m.RandomNeutralNode(out tx, out ty);
                if (Style == CTFXStyle.Adhd) _adhdNext = now + 20000 + _rng.Next(20001);
            }
            else switch (Style)
            {
                case CTFXStyle.Post:
                    if (home == "brown-top") return false;                                   // the configured turret-room posts
                    got = m.RouteNode(home + ":patrol", 0, out tx, out ty);                   // the flag room
                    break;
                case CTFXStyle.Guard: got = m.RandomRouteNode(home + ":patrol", out tx, out ty); break;
                case CTFXStyle.Assault: got = enemy != null && m.RouteNode(home + ">" + enemy + ":raid", -1, out tx, out ty); break;
                case CTFXStyle.Sniper: got = m.RouteNode(quad + "-core>" + home, -1, out tx, out ty); break;
                case CTFXStyle.Drain: got = m.RandomNeutralNode(out tx, out ty); break;
                case CTFXStyle.Adhd:
                    got = m.RandomRouteNode(_rng.Next(2) == 0 || enemy == null ? home + ":patrol" : home + ">" + enemy + ":raid", out tx, out ty);
                    _adhdNext = now + 20000 + _rng.Next(20001);
                    break;
                default:
                    got = m.RandomRouteNode(_rng.NextDouble() < 0.65 || enemy == null ? home + ":patrol" : home + ">" + enemy + ":raid", out tx, out ty);
                    break;
            }
            if (!got) return false;
            _gtx = tx; _gty = ty; _hasGoal = true; _arrived = false;
            CancelPath();
            return true;
        }

        /// <summary>
        /// Goals of a group that lives in a building's middle hall (home = brown-core): guards hold the hall, roamers mostly stay in
        /// it and sometimes push into an end, an assault squad raids the top end's flag room, a sniper watches the hall from one of
        /// its mouths, a post holds the hub.
        /// </summary>
        bool CoreGoal(int now)
        {
            CTFXBotManager m = Mgr;
            string q = Group.CoreOf, top = m.TopEnd(q), low = m.OtherEnd(top);
            int tx = 0, ty = 0;
            bool got;
            double u = _rng.NextDouble();
            // 10-15 night: a game's first 3-6 minutes (its own) the hall teams fight out in the landing fields, the elite set up the flags
            if (m.Flags != null && m.Dropship != null && m.Dropship.HasOutside && Style != CTFXStyle.Post
                && now - m.Flags.StartedAt < 180000 + (int)(Slot != null ? Slot.FixedId % 7 : 0) * 30000
                && m.Dropship.OutsideGoal(this, m.Cmdr != null ? m.Cmdr.HerdK : 1.2, out tx, out ty))
            {
                m.FieldOpenings++;
                _gtx = tx; _gty = ty; _hasGoal = true; _arrived = false;
                CancelPath();
                return true;
            }
            switch (Style)
            {
                case CTFXStyle.Post: got = m.CoreHub(q, out tx, out ty); break;
                case CTFXStyle.Guard:
                    got = u < 0.08 || top == null ? CoreNodeUncrowded(q, out tx, out ty) : u < 0.50 ? m.RandomRouteNode(top + ":patrol", out tx, out ty)
                        : u < 0.85 || low == null ? m.StagingGoal(top, out tx, out ty) : m.StagingGoal(low, out tx, out ty);
                    break;
                case CTFXStyle.Sniper: got = m.RouteNode(q + "-core>" + (u < 0.5 || low == null ? top : low), -1, out tx, out ty); break;
                case CTFXStyle.Assault:
                    got = top != null && (u < 0.7 ? m.RouteNode(top + ":patrol", 0, out tx, out ty) : m.RandomRouteNode(top + ":patrol", out tx, out ty));
                    break;
                case CTFXStyle.Adhd:
                    got = u < 0.15 || top == null ? m.RandomCoreNode(q, out tx, out ty)
                        : m.RandomRouteNode((u < 0.7 || low == null ? top : low) + ":patrol", out tx, out ty);
                    _adhdNext = now + 20000 + _rng.Next(20001);
                    break;
                default:                                                                     // roam, drain, stormer
                    // Travis 10-15: the hall was a forever brawl (15.8 bots on average there, 4.9 in top brown); now 30 % hall, 30 %
                    // top brown from the hall, 25 % top brown from its outdoor north side (the cave route), 15 % the bottom end
                    // 10-15 J21: still a brawl in the lower hall; now 10 % hall (never a 5+ crowd), 35 % into top brown, 45 % its attack
                    // positions (outdoor approach, the hall mouth under it), 10 % the bottom end
                    // 10-15 night: the ends are the game; 5 % hall, 45 % into top brown, 30 % its attack positions (now inside it), 20 %
                    // the bottom end
                    got = u < 0.05 || top == null ? CoreNodeUncrowded(q, out tx, out ty)
                        : u < 0.50 ? m.RandomRouteNode(top + ":patrol", out tx, out ty)
                        : u < 0.80 ? m.StagingGoal(top, out tx, out ty)
                        : m.StagingGoal(low ?? top, out tx, out ty);
                    break;
            }
            if (!got) return false;
            _gtx = tx; _gty = ty; _hasGoal = true; _arrived = false;
            CancelPath();
            return true;
        }

        bool CoreNodeUncrowded(string q, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!Mgr.RandomCoreNode(q, out tx, out ty)) return false;
                if (Mgr.CrowdAt(tx * 16 + 8, ty * 16 + 8, 200.0) < 5) return true;
            }
            return true;
        }

        bool WantPath(int now, int gtx, int gty)
        {
            if (_arena._pathfinder == null || _inFlight || now - _noPathUntil < 0) return false;
            if (_pn == 0 || _pi >= _pn) return now - _lastReq >= 600;      // no usable path
            if (now - _lastReq < _repathGap) return false;                 // re-path every 2-4 s at most
            int dx = gtx - _pathGoalTx, dy = gty - _pathGoalTy;
            return _forceRepath || dx * dx + dy * dy > 16;                 // ...and only if the goal moved > 4 tiles or we are stuck
        }

        void RequestPath(int now, int px, int py, int gtx, int gty)
        {
            if (!Mgr.TryBeginPath()) { _noPathUntil = now + 150 + _rng.Next(250); return; }
            int seq;
            lock (_pathLock) { seq = ++_pathSeq; _pendReady = false; _pendList = null; }
            _inFlight = true; _inFlightTick = now; _reqGoalTx = gtx; _reqGoalTy = gty;
            _lastReq = now; _repathGap = 2000 + _rng.Next(2001); _forceRepath = false;
            try
            {
                _arena._pathfinder.queueRequest((short)(px >> 4), (short)(py >> 4), (short)gtx, (short)gty,
                    delegate(List<Vec3> nodes, int status) { OnPathResult(seq, nodes); });
            }
            catch (Exception) { _inFlight = false; Mgr.EndPath(true); _noPathUntil = now + 1000; }
        }

        /// <summary>Runs on the pathing thread: only hands the list over.</summary>
        void OnPathResult(int seq, List<Vec3> nodes)
        {
            lock (_pathLock)
            {
                if (Released || seq != _pathSeq) return;
                _pendList = nodes;
                _pendReady = true;
            }
        }

        void ConsumePath(int now)
        {
            if (!_inFlight) return;
            List<Vec3> nodes = null;
            bool ready = false;
            lock (_pathLock)
            {
                if (_pendReady) { nodes = _pendList; _pendList = null; _pendReady = false; ready = true; }
            }
            if (ready)
            {
                _inFlight = false;
                bool ok = nodes != null && nodes.Count > 0;
                Mgr.EndPath(!ok);
                if (ok)
                {
                    int n = nodes.Count;
                    if (_pxs == null || _pxs.Length < n) { _pxs = new int[n + 16]; _pys = new int[n + 16]; }
                    for (int i = 0; i < n; i++) { _pxs[i] = (int)(nodes[i].x * 100f + 8f); _pys[i] = (int)(nodes[i].y * 100f + 8f); }
                    _pn = n; _pi = 0; _pathGoalTx = _reqGoalTx; _pathGoalTy = _reqGoalTy; _pathFails = 0;
                }
                else
                {
                    _pn = 0; _pathFails++;
                    _noPathUntil = now + 800 + _rng.Next(700);
                    if (_pathFails >= 2) { _hasGoal = false; _pathFails = 0; }    // unreachable goal: pick another one
                }
            }
            else if (now - _inFlightTick > 5000)
            {
                lock (_pathLock) { _pathSeq++; }
                _inFlight = false;
                Mgr.EndPath(true);
                _pathFails++;
            }
        }

        /// <summary>Forget the current path and any request in flight (new goal).</summary>
        void CancelPath()
        {
            lock (_pathLock) { _pathSeq++; _pendReady = false; _pendList = null; }
            if (_inFlight) { _inFlight = false; Mgr.EndPath(false); }
            _pn = 0; _pi = 0; _forceRepath = false; _pathFails = 0; _lastReq = Environment.TickCount - 5000;
            ClearRoute();
        }

        /// <summary>Stuck = moved less than 20 px in 1.5 s while navigating. Within 64 px of the goal it counts as there; else it backs
        /// out toward the most open heading for half a second and re-plans; 3 stucks new goal; 6 stucks recycle.</summary>
        void NavMoving(int now, int px, int py)
        {
            if (!_navActive) { _navActive = true; _stuckT = now; _stuckX = px; _stuckY = py; return; }
            if (now - _stuckT < 1500) return;
            int dx = px - _stuckX, dy = py - _stuckY;
            _stuckT = now; _stuckX = px; _stuckY = py;
            if (dx * dx + dy * dy >= 20 * 20) return;
            double gx = _gtx * 16 + 8 - px, gy = _gty * 16 + 8 - py;
            if (_hasGoal && gx * gx + gy * gy <= 64.0 * 64.0)
            {   // pressed against something right by the goal: that is close enough
                if (!_arrived) { _arrived = true; _stuckCount = 0; _pauseUntil = Perching(now) ? _perchUntil : Pocketing ? now + 250 : now + PauseMs(); }
                Mgr.NearGoalArrivals++;
                return;
            }
            _unstickYaw = OpenestYaw(px, py);
            _unstickUntil = now + 400 + _rng.Next(301);
            _stuckCount++;
            Mgr.StuckEvents++;
            _forceRepath = true; _pn = 0;
            ClearRoute();
            if (_stuckCount % 3 == 0) _hasGoal = false;
            if (_stuckCount >= 6) Mgr.RequestRecycle(this);
        }

        bool CachedLos(LosCache c, int now, int key, int x0, int y0, int x1, int y1)
        {
            if (c.Key == key && now - c.Tick < 250) return c.Val;
            c.Key = key; c.Tick = now; c.Val = Mgr.Los(x0, y0, x1, y1);
            return c.Val;
        }

        // ---------------------------------------------------------------- yaw helpers (fact d)
        internal static float WrapYaw(float y)
        {
            y %= 240f;
            return y < 0f ? y + 240f : y;
        }

        internal static float YawTo(double dx, double dy)
        {
            double a = Math.Atan2(dx, -dy / 0.7) * (180.0 / Math.PI) / 1.5;
            if (a < 0.0) a += 240.0;
            return (float)a;
        }

        internal static void YawVec(float yaw, out float x, out float y)
        {
            double r = yaw * 1.5 * Math.PI / 180.0;
            x = (float)Math.Sin(r);
            y = (float)-Math.Cos(r);
        }
    }

    /// <summary>
    /// Item skill logic for bots: a port of the server's Logic_Assets.SkillCheck (InfServer/Logic/General/Assets.Skills.cs), the
    /// rule ?buy and pickups use. %N = class id of the class vehicle, N = has class skill N; experience (@), points (#) and
    /// attributes (-) are false for a bot. Same grammar and quirks as the server. Results are cached per item and class.
    /// </summary>
    internal static class CTFXSkillLogic
    {
        static readonly Regex Param = new Regex(@"\!?([%@#\-]?)([0-9]+)", RegexOptions.Compiled);
        static readonly Regex Illegal = new Regex(@"[^-!@#%&()|0-9]", RegexOptions.Compiled);
        static readonly Dictionary<long, bool> Cache = new Dictionary<long, bool>();

        /// <summary>May a bot of class skill <paramref name="skill"/> buy, equip and fire item <paramref name="itemId"/>?</summary>
        public static bool Allowed(int itemId, int skill)
        {
            long key = ((long)itemId << 16) | (uint)skill;
            lock (Cache)
            {
                bool ok;
                if (Cache.TryGetValue(key, out ok)) return ok;
                ItemInfo it = AssetManager.Manager.getItemByID(itemId);
                ok = it != null && Test(it.skillLogic, skill, ClassIdOf(skill));
                Cache[key] = ok;
                return ok;
            }
        }

        static int ClassIdOf(int skill)
        {
            SkillInfo sk = AssetManager.Manager.getSkillByID(skill);
            VehInfo v = sk != null ? AssetManager.Manager.getVehicleByID(sk.DefaultVehicleId) : null;
            return v != null ? v.ClassId : 0;
        }

        internal static bool Test(string logic, int skill, int classId)
        {
            if (String.IsNullOrWhiteSpace(logic)) return true;
            string clean = Illegal.Replace(logic, "");        // also strips quotes, so a quoted empty string passes like on the server
            if (clean.Length == 0) return true;
            string b = Param.Replace(clean.TrimStart('&'), delegate(Match m)
            {
                string pre = m.Groups[1].Value;
                int n = int.Parse(m.Groups[2].Value);
                bool val = pre == "%" ? classId == n : (pre.Length == 0 && n == skill);
                return (val ^ m.Groups[0].Value.StartsWith("!")) ? "1" : "0";
            });
            int pos = 0;
            try { return Expr(b, ref pos); }
            catch (Exception) { return false; }
        }

        // the server's recursive descent parser: expr = and ('|' and)*, and = not ('&' not)*, not = '!' not | simple
        static bool Expr(string e, ref int pos)
        {
            bool x = And(e, ref pos);
            while (pos + 1 < e.Length && e[pos] == '|') { pos++; x |= And(e, ref pos); }
            return x;
        }

        static bool And(string e, ref int pos)
        {
            bool x = Not(e, ref pos);
            while (pos + 1 < e.Length && e[pos] == '&') { pos++; x &= Not(e, ref pos); }
            return x;
        }

        static bool Not(string e, ref int pos)
        {
            if (e[pos] == '!') { pos++; return !Not(e, ref pos); }
            return Simple(e, ref pos);
        }

        static bool Simple(string e, ref int pos)
        {
            char c = e[pos];
            if (c == '(')
            {
                pos++;
                bool x = Expr(e, ref pos);
                if (e.IndexOf(')') < 0) throw new FormatException("Lacking closed parens");
                return x;
            }
            if (c == '0' || c == '1') { pos++; return c == '1'; }
            throw new FormatException("Unexpected character: " + c);
        }
    }
}
/* PARSER GUARD - keep last */
