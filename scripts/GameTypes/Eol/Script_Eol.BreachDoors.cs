using System;
using System.Collections.Generic;

using Assets;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-26, owner: "destructable wall ... when its broken you can go through it, it can be repaired by a engy/chief
    /// engy ... saying 'repair wall' when you are close to it, and they have to stand there for 10 seconds") breachable doors:
    /// a lio door (its lock / switch raises and lowers it) with hit points kept here. Explosions next to a closed door take
    /// their explosive damage off; at 0 the door is blown open and its lock is dead until a Combat Engineer or Chief Engineer
    /// types "repair door" next to it and stands still for 10 s - then the door is shut again at full strength.
    /// Test zone only: A1 south entrance + east building exit (lio doors 1200-1205, locks 920 / 921).
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>(2026-11-26, owner: "do 1 hp now so i can easily do it") any explosion breaks a door - raise when tuned.</summary>
        public const int BreachDoorMaxHp = 1;          // hit points (weapon explosive damage / 1000)
        const int BreachDoorRepairMs = 10000;          // stand still this long
        const int BreachDoorRepairRange = 160;         // px from the middle of the door
        const int BreachDoorBlastSlack = 48;           // px added to the weapon's own blast radius

        class BreachDoor
        {
            public string Name; public int SwitchId; public short X, Y;   // X, Y = middle of the door (px)
            public int Hp; public bool Broken;
        }

        class BreachRepair { public InfServer.Game.Player P; public BreachDoor Door; public short X, Y; public int Due; }

        readonly List<BreachDoor> _breachDoors = new List<BreachDoor>
        {
            new BreachDoor { Name = "south door", SwitchId = 920, X = 512, Y = 944, Hp = BreachDoorMaxHp },   // tiles 29-34 x 58-59
            new BreachDoor { Name = "east door", SwitchId = 921, X = 1216, Y = 720, Hp = BreachDoorMaxHp },   // tiles 73-78 x 44-45
        };
        readonly List<BreachRepair> _breachRepairs = new List<BreachRepair>();

        static Script_Eol _breachChatScript;
        static bool _breachChatHooked;

        Arena.SwitchState BreachSwitch(BreachDoor d)
        {
            Arena.SwitchState ss;
            return _arena != null && _arena._switches != null && _arena._switches.TryGetValue(d.SwitchId, out ss) ? ss : null;
        }

        /// <summary>Closed = the lock is off (the door is down and blocking).</summary>
        bool BreachDoorClosed(BreachDoor d)
        {
            Arena.SwitchState ss = BreachSwitch(d);
            return ss != null && !ss.bOpen;
        }

        void BreachDoorTell(BreachDoor d, string msg)
        {
            foreach (InfServer.Game.Player p in _arena.PlayersIngame)
                if (p != null && p._state != null && Math.Abs(p._state.positionX - d.X) < 1200 && Math.Abs(p._state.positionY - d.Y) < 1200)
                    p.sendMessage(-1, msg);
        }

        /// <summary>Player.Explosion: explosive damage next to a closed door wears it down; at 0 it is blown open.</summary>
        void BreachDoorExplosion(InfServer.Game.Player player, ItemInfo.Projectile weapon, short x, short y)
        {
            if (player == null || weapon == null || weapon.explosiveDamageInner <= 0)
                return;
            foreach (BreachDoor d in _breachDoors)
            {
                if (d.Broken || !BreachDoorClosed(d))
                    continue;
                int reach = Math.Max(weapon.explosiveDamageRadius, 0) + BreachDoorBlastSlack;
                int dx = x - d.X, dy = y - d.Y;
                if (dx * dx + dy * dy > reach * reach)
                    continue;
                d.Hp -= Math.Max(1, weapon.explosiveDamageInner / 1000);
                if (d.Hp > 0)
                    continue;
                d.Hp = 0;
                d.Broken = true;
                Arena.SwitchState ss = BreachSwitch(d);
                if (ss != null)
                    _arena.switchRequest(true, true, player, ss.Switch);   // forced: the door opens for everyone
                BreachDoorTell(d, "The " + d.Name + " has been breached! An engineer can 'repair door' to seal it.");
            }
        }

        /// <summary>Player.Switch: a breached door's lock does nothing until it is repaired.</summary>
        bool BreachDoorSwitchAllowed(InfServer.Game.Player player, LioInfo.Switch swi)
        {
            if (swi == null)
                return true;
            foreach (BreachDoor d in _breachDoors)
                if (d.SwitchId == swi.GeneralData.Id && d.Broken)
                {
                    if (player != null)
                        player.sendMessage(-1, "The " + d.Name + " is wrecked - an engineer has to 'repair door' first.");
                    return false;
                }
            return true;
        }

        static bool IsEngineerClass(InfServer.Game.Player p)
        {
            Vehicle v = p != null ? p._baseVehicle : null;
            int id = v != null && v._type != null ? v._type.Id : -1;
            return id == ClsEngineer || id == ClsChiefEngineer;
        }

        /// <summary>"repair door" said next to a breached / damaged door by a Combat Engineer or Chief Engineer.</summary>
        void BreachDoorStartRepair(InfServer.Game.Player p)
        {
            if (p == null || p.IsDead || p._state == null)
                return;
            BreachDoor best = null;
            int bestD = int.MaxValue;
            foreach (BreachDoor d in _breachDoors)
            {
                int dx = p._state.positionX - d.X, dy = p._state.positionY - d.Y, dd = dx * dx + dy * dy;
                if (dd <= BreachDoorRepairRange * BreachDoorRepairRange && dd < bestD) { best = d; bestD = dd; }
            }
            if (best == null)
            {
                p.sendMessage(-1, "Get closer to the door to repair it.");
                return;
            }
            if (!IsEngineerClass(p))
            {
                p.sendMessage(-1, "Only a Combat Engineer or Chief Engineer can repair the door.");
                return;
            }
            if (!best.Broken && best.Hp >= BreachDoorMaxHp)
            {
                p.sendMessage(-1, "The " + best.Name + " isn't damaged.");
                return;
            }
            foreach (BreachRepair r in _breachRepairs)
                if (r.P == p || r.Door == best)
                    return;
            _breachRepairs.Add(new BreachRepair { P = p, Door = best, X = p._state.positionX, Y = p._state.positionY, Due = Environment.TickCount + BreachDoorRepairMs });
            p.sendMessage(-1, "Repairing the " + best.Name + " - stand still for 10 seconds.");
        }

        void PollBreachDoors(int now)
        {
            _breachChatScript = this;
            if (!_breachChatHooked)
            {
                _breachChatHooked = true;
                CS_Chat.Handlers += OnBreachChat;
            }
            for (int i = _breachRepairs.Count - 1; i >= 0; i--)
            {
                BreachRepair r = _breachRepairs[i];
                InfServer.Game.Player p = r.P;
                if (p == null || p.IsDead || p.IsSpectator || p._arena != _arena || p._state == null)
                {
                    _breachRepairs.RemoveAt(i);
                    continue;
                }
                if (Math.Abs(p._state.positionX - r.X) > HqRecallMoveTolerance || Math.Abs(p._state.positionY - r.Y) > HqRecallMoveTolerance)
                {
                    _breachRepairs.RemoveAt(i);
                    p.sendMessage(-1, "Door repair cancelled - you moved.");
                    continue;
                }
                if (now - r.Due < 0)
                    continue;
                _breachRepairs.RemoveAt(i);
                BreachDoor d = r.Door;
                d.Hp = BreachDoorMaxHp;
                d.Broken = false;
                Arena.SwitchState ss = BreachSwitch(d);
                if (ss != null && ss.bOpen)
                    _arena.switchRequest(true, false, p, ss.Switch);   // shut it again
                BreachDoorTell(d, "The " + d.Name + " has been repaired and sealed.");
            }
        }

        /// <summary>Network thread: "repair door" (any chat channel) is handed to the arena thread.</summary>
        static void OnBreachChat(CS_Chat pkt, InfServer.Game.Player player)
        {
            try
            {
                Script_Eol s = _breachChatScript;
                if (s == null || pkt == null || player == null || s._arena == null || player._arena != s._arena)
                    return;
                string low = pkt.message != null ? pkt.message.Trim().ToLowerInvariant() : "";
                if (low != "repair door" && low != "repair wall")
                    return;
                player._arena.handleEvent(delegate (Arena a)
                {
                    try { if (_breachChatScript == s) s.BreachDoorStartRepair(player); }
                    catch (Exception ex) { Log.write(TLog.Warning, "[Eol] repair door: " + ex.Message); }
                });
            }
            catch (Exception) { }
        }
    }
}
