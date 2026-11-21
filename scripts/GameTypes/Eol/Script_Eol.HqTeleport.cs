using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-21, owner: "the HQ recall does not work on the new HQs we did, make sure it finds that new one we did so HQ
    /// recalls work") the HQ Teleport (itm 208) warps to lio warp group 70 ("HQWarp", relative id 69 = veh 469's). The engine
    /// lands the player on the HQ's exact centre point (the warp field is 0 x 0, and its free-spot search is never used), which
    /// is inside the HQ building now that 469 is drawn as one. Done here instead: the team's own live HQ - 469 Primary HQ or
    /// 473 Player headquarters - and an open spot in a ring round it.
    /// </summary>
    public partial class Script_Eol
    {
        const int HqTeleportItemId = 208;
        const int HqWarpGroup = 70;
        const int PlayerHqVehId = 473;

        bool IsAnyHqVeh(Vehicle v)
        {
            return v != null && v._type != null && (IsHqVehId(v._type.Id) || v._type.Id == PlayerHqVehId);
        }

        void TryHqTeleport(InfServer.Game.Player player)
        {
            if (player.IsDead || player._team == null || player.ActiveVehicle == null || !player.ActiveVehicle._type.IsWarpable)
                return;
            Vehicle hq = null;
            double best = double.MaxValue;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (!IsAnyHqVeh(v) || v.IsDead || v.bCondemned || v._state == null || v._team != player._team)
                    continue;
                double d = Helpers.distanceTo(v._state, player._state);
                if (d < best) { best = d; hq = v; }
            }
            if (hq == null)
            {
                player.sendMessage(-1, "&Your team has no HQ to teleport to.");
                return;
            }
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            short hx = hq._state.positionX, hy = hq._state.positionY;
            // a ring round the HQ (outside its building), nearer spots first
            for (int ring = 80; ring <= 320; ring += 40)
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    double a = _rand.NextDouble() * Math.PI * 2;
                    int x = hx + (int)(Math.Cos(a) * ring), y = hy + (int)(Math.Sin(a) * ring);
                    if (x < 32 || y < 32 || !HqLandingClear(x, y))
                        continue;
                    warp(player, new Helpers.ObjectState { positionX = (short)x, positionY = (short)y });
                    return;
                }
            player.sendMessage(-1, "&No open ground round your HQ to teleport to.");
        }

        /// <summary>No wall here or one tile round it.</summary>
        bool HqLandingClear(int x, int y)
        {
            for (int dy = -16; dy <= 16; dy += 16)
                for (int dx = -16; dx <= 16; dx += 16)
                    if (_arena.getTile(x + dx, y + dy).Blocked)
                        return false;
            return true;
        }
    }
}
