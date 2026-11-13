// CTF:X server-side bots - ENHANCED SENSORS (Travis 10-14: "some smart or support class bots should carry enhanced sensors and turn it
// on at times (not all times) as it drains their energy passively, but when their energy is full or if they suspect a cloaker might be
// in the area, that's when it would trigger it on, but we should encode flaws in their ability to detect cloaking, just like humans,
// they don't always turn ES on").
//
// Carried by about half the lives of the support / thinking classes that may buy it (Medic, Engineer, Squad Leader, SciOps, Sergeant,
// Captain, BioChem). Switched on = Enhanced Sensors (item 12) in the bot's active gear, so its real energy drain applies (EnergyRateNow)
// and clients see it on. When:
//   - energy near full: now and then (a check every 4-7 s, 12-30 % by skill tier)
//   - a cloaker suspected (a hit from an unseen attacker, or a teammate within 500 px killed or hit by one in the last 8 s): likely
//     but not certain (45-85 % by tier); a rookie often forgets
// It goes off after 8-20 s, or when energy falls under 35 %. While on, an enemy cloaked bot inside the sensor range is noticed on a
// scan with 70-90 % (ace highest) - a look can miss it; once targeted it stays seen while inside the range.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        internal const int EnhancedSensors = 12;
        static ItemInfo.UtilityItem _esItem;
        static bool _esLooked;
        bool _esCarry, _esOn;
        int _esUntil, _esCheckAt, _suspectAt;

        static ItemInfo.UtilityItem EsItem()
        {
            if (!_esLooked) { _esLooked = true; _esItem = AssetManager.Manager.getItemByID(EnhancedSensors) as ItemInfo.UtilityItem; }
            return _esItem;
        }

        /// <summary>Sensor reach (px): the item's anti-cloak distance, 400 when the item leaves it unset.</summary>
        static int EsRange() { ItemInfo.UtilityItem u = EsItem(); return u != null && u.antiCloakDistance > 0 ? u.antiCloakDistance : 400; }

        internal bool EsOn { get { return _esOn; } }

        /// <summary>A new body: does this life carry ES (about half the support / thinking classes allowed it)?</summary>
        void EsSpawn()
        {
            _esOn = false; _suspectAt = 0;
            int c = _class;
            bool smart = c == SK_MEDIC || c == SK_ENGY || c == SK_SL || c == SK_SCIOPS || c == SK_SGT || c == SK_CAPT || c == SK_BIO;
            _esCarry = smart && EsItem() != null && CTFXSkillLogic.Allowed(EnhancedSensors, c) && _rng.NextDouble() < (Mgr.Mini != null ? Mgr.Mini.EsOwn(Human != null ? Human.Tier : null) : 0.5);   // MiniTP: a tier chance
            _esCheckAt = Environment.TickCount + 3000 + _rng.Next(4000);
            if (_esCarry) Mgr.EsCarriers++;
        }

        /// <summary>Something unseen hit it, or hit a teammate near it: a cloaker may be about.</summary>
        internal void Suspect(int now) { _suspectAt = now; }

        double TierPick(double rookie, double ace)
        {
            string t = Human != null ? Human.Tier : "regular";
            double k = t == "ace" ? 1.0 : t == "veteran" ? 0.7 : t == "regular" ? 0.4 : 0.0;
            return rookie + (ace - rookie) * k;
        }

        void EsStep(int now)
        {
            if (!_esCarry || IsDead || InDropship) { if (_esOn) EsSet(false); return; }
            double en = _energyMax > 0 ? _energy / _energyMax : 1.0;
            if (_esOn)
            {
                if (now - _esUntil >= 0 || en < 0.35) EsSet(false);
                return;
            }
            if (now - _esCheckAt < 0) return;
            _esCheckAt = now + 4000 + _rng.Next(3001);
            bool suspect = _suspectAt != 0 && now - _suspectAt < 8000;
            double p = suspect ? TierPick(0.45, 0.85) : en >= 0.95 ? TierPick(0.12, 0.30) : 0.0;
            // Travis 10-15: defenders sweep side rooms with ES on, hunting the infiltrators that set up safe attaches
            bool homeGuard = !suspect && Mgr.Cmdr != null && Group != null && Group.HomeEnd != null && en >= 0.6
                && (Mgr.Cmdr.ActOf(this) == CTFXAct.Anchor || Mgr.Cmdr.ActOf(this) == CTFXAct.Defend) && Mgr.InsideEnd(Group.HomeEnd, this);
            if (homeGuard) { p = Math.Max(p, TierPick(0.15, 0.40)); }
            if (p <= 0 || _rng.NextDouble() >= p) return;                   // the flaw: it does not always think of it
            _esUntil = now + 8000 + _rng.Next(12001);
            EsSet(true);
            if (suspect) Mgr.EsOnSuspect++; else if (homeGuard) Mgr.EsSweeps++; else Mgr.EsOnIdle++;
        }

        void EsSet(bool on)
        {
            ItemInfo.UtilityItem u = EsItem();
            if (u == null || _activeEquip == null) return;
            _esOn = on;
            if (on && !_activeEquip.Contains(u)) _activeEquip.Add(u);
            else if (!on) _activeEquip.Remove(u);
        }

        /// <summary>A scan: does it notice cloaked enemy o at squared distance d2 (inside the cloak range it is seen anyway)?</summary>
        bool NoticesCloaked(CTFXBot o, double d2)
        {
            if (!_esOn) return false;
            double r = EsRange();
            if (d2 > r * r) return false;
            if (_rng.NextDouble() >= TierPick(0.70, 0.90)) return false;   // a look can miss it
            Mgr.EsSpots++;
            return true;
        }

        /// <summary>A human's cloak (an active utility with a cloak distance): that distance in px, 0 = not cloaked.</summary>
        static int PlayerCloak(Player p)
        {
            if (p == null || p.activeUtilities == null) return 0;
            int r = 0;
            foreach (ItemInfo.UtilityItem u in p.activeUtilities) if (u != null && u.cloakDistance > 0) r = Math.Max(r, Math.Max(48, u.cloakDistance));
            return r;
        }

        /// <summary>Keeps a cloaked target it already has while the sensors are on and it is in range.</summary>
        bool KeepsCloaked(double d2)
        {
            double r = EsRange();
            return _esOn && d2 <= r * r;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int EsCarriers, EsOnIdle, EsOnSuspect, EsSpots, Suspicions;

        /// <summary>A cloaked bot hit victim v: v and its teammates within 500 px suspect a cloaker.</summary>
        internal void CloakedHit(CTFXBot v, int now)
        {
            if (v == null) return;
            Suspicions++;
            foreach (CTFXBot o in LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team != v._team) continue;
                double dx = o._state.positionX - v._state.positionX, dy = o._state.positionY - v._state.positionY;
                if (dx * dx + dy * dy <= 500.0 * 500.0) o.Suspect(now);
            }
        }

        internal string SensorsText()
        {
            return " | sensors: carried by " + EsCarriers + " lives, switched on at full energy " + EsOnIdle + " / on a suspicion " + EsOnSuspect
                + ", cloakers spotted " + EsSpots + ", cloaked hits " + Suspicions;
        }
    }
}
/* PARSER GUARD - keep last */
