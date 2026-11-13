// CTF:X server-side bots - MOVEMENT PACKS (Travis 10-15: "I haven't really seen any bots on flight packs, hoverboards, drop packs (most of
// them use them for temporary transportation and then 90% of the time are not using them, with niche purposes)").
//
// packs=1 (CTFXBots.txt; OFF by default until Travis has seen how his client draws it). A player's pack is a separate vehicle he rides;
// a bot has no rider slot, so the bot's own body swaps to the pack's vehicle type (Hoverboard 105, Flight Pack 106, Drop Pack 109) under
// the same id and is re-sent to every client (one SC_Vehicles, as the engine does for a class change), then swaps back on the ground.
// Its movement model is retuned to the pack's terrain-0 speeds. When: out of buildings, on its way somewhere 1800+ px off and not
// fighting (a long trip from a landing); off again within 450 px of the goal, at a building, when it takes a target, or when hit
// (the hit then lands on its own body). A Jump Trooper rides a Drop Pack, an Infiltrator a Flight Pack, the rest a Hoverboard where
// their class may use one.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        VehInfo.Car _footType;
        int _packAt, _packCheckAt, _packCheckX, _packCheckY;
        internal bool OnPack { get { return _footType != null; } }

        int PackFor()
        {
            // 10-17 (Travis: "many classes able to get on vehicles they don't qualify for"): each ride is gated by its item's skill logic
            // (Hoverboard 1, Flight Pack 13, Drop Pack 30); the Jump Trooper prefers the Drop Pack, the Infiltrator the Flight Pack
            bool hover = CTFXSkillLogic.Allowed(1, _class), flight = CTFXSkillLogic.Allowed(13, _class), drop = CTFXSkillLogic.Allowed(30, _class);
            if (_class == SK_JT) return drop ? 109 : flight ? 106 : hover ? 105 : 0;
            if (_class == SK_INFIL) return flight ? 106 : hover ? 105 : 0;
            return hover ? 105 : flight ? 106 : 0;
        }

        void PackStep(int now, int px, int py)
        {
            if (!Mgr.Packs || now - _packAt < 0) return;
            _packAt = now + 500;
            if (IsDead || InDropship) { if (OnPack) Dismount(now); return; }
            if (Group != null && Group.DropPack && _class == SK_JT && CTFXSkillLogic.Allowed(30, _class))
            {   // Travis 10-15: "always leave the dropship with a droppack occupied, and never exit it" (a hit knocks it off; it remounts)
                if (!OnPack) Mount(109, now);
                return;
            }
            bool outside = Mgr.AreaAt(px, py).StartsWith("out", StringComparison.Ordinal);
            double gx = _gtx * 16 + 8 - px, gy = _gty * 16 + 8 - py, gd = Math.Sqrt(gx * gx + gy * gy);
            if (OnPack)
            {
                if (HasTarget || !outside || !_hasGoal || gd < 450.0) { Dismount(now); return; }
                // 10-17 (Travis: Umber 79 bounced back and forth on a hoverboard for minutes): a ride that has not gone 150 px in 6 s is
                // stuck against something the pack's wider turning cannot clear; it gets off, walks, and does not ride again for 30 s
                if (now - _packCheckAt >= 0)
                {
                    double mx = px - _packCheckX, my = py - _packCheckY;
                    if (_packCheckAt != 0 && mx * mx + my * my < 150.0 * 150.0) { Dismount(now); _packAt = now + 30000; Mgr.PackStuck++; }
                    _packCheckAt = now + 6000; _packCheckX = px; _packCheckY = py;
                }
                return;
            }
            _packCheckAt = 0;
            // 10-17 (Travis: engineers must beeline to their turret spots): an engineer with a job rides for any trip over 600 px
            if (HasTarget || !outside || !_hasGoal || gd < (Style == CTFXStyle.Engineer && _job != null ? 600.0 : 1800.0) || _carrying) return;
            int veh = PackFor();
            if (veh != 0) Mount(veh, now);
        }

        void Mount(int vehId, int now)
        {
            VehInfo.Car info = AssetManager.Manager.getVehicleByID(vehId) as VehInfo.Car;
            if (info == null || _cm == null) return;
            _footType = _type;
            SwapType(info);
            Mgr.PackMounts++;
        }

        /// <summary>11-11 (MiniTP mining): get on a vehicle the bot has just built (the caller paid its maker); true = riding.</summary>
        internal bool RideVehicle(int vehId, int now) { if (OnPack) return true; Mount(vehId, now); if (OnPack) BoardHp = Math.Max(1, (int)_type.Hitpoints); return OnPack; }
        internal int BoardHp = -1;                  // > 0: a vehicle the bot BUILT (MiniTP mining): never knocked off, hits work out on the rider's body; -1 = a CTF:X pack
        internal VehInfo.Car FootType { get { return _footType ?? _type; } }

        internal void Dismount(int now)
        {
            BoardHp = -1;
            if (_footType == null) return;
            VehInfo.Car foot = _footType;
            _footType = null;
            SwapType(foot);
            _packAt = now + 4000;                                                     // a moment on foot before the next ride
        }

        void SwapType(VehInfo.Car info)
        {
            ((Vehicle)this)._type = info;
            _type = info;
            _cm.Retune(info);
            try { Helpers.Object_Vehicles(_arena.Players, this); } catch (Exception) { }
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal bool Packs;
        internal int PackMounts, PackStuck, BoardsBroken;
        internal string PacksText() { return Packs ? " | packs: mounts " + PackMounts + ", stuck rides ended " + PackStuck : ""; }
    }
}
/* PARSER GUARD - keep last */
