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

        // (2026-11-26, owner: "make the base recall something where you have to stand still to use, not moving around. if
        // you move it should cancel") the HQ recall charges for 5 s; moving, dying, leaving or walking into an enemy
        // teleport disruptor cancels it (the recall isn't used up then)
        const int HqRecallChargeMs = 5000;
        const int HqRecallMoveTolerance = 12;   // px - a nudge isn't moving
        // (2026-11-26, owner: "the store recall, the pre equipped is still able to use while moving. please make sure you have to
        // stand still. similar to a HQ teleport ... all store recall, including the conscript purple version") the Store Recall
        // (every class's, Conscripts' too - itm 1552) and the Teleport Charge - Recall charge the same way; when the charge is
        // done the engine's own warp runs (StoreRecallBypass lets it through Player.WarpItem this once)
        class HqRecallCharge { public short X, Y; public int Due, NextTell; public ItemInfo.WarpItem Item; public ushort Target; public string Label = "HQ recall"; }
        readonly Dictionary<InfServer.Game.Player, HqRecallCharge> _hqRecalls = new Dictionary<InfServer.Game.Player, HqRecallCharge>();
        readonly HashSet<InfServer.Game.Player> _recallBypass = new HashSet<InfServer.Game.Player>();

        /// <summary>A recall that needs standing still (not the HQ Teleport - that one has its own charge): any warp item named
        /// "... Recall" - the Store Recall and the Teleport Charge - Recall.</summary>
        static bool IsStandStillRecall(ItemInfo.WarpItem item)
        {
            return item != null && item.id != HqTeleportItemId && (item.name ?? "").IndexOf("Recall", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Player.WarpItem: true = this recall goes through now (its charge just finished).</summary>
        bool StoreRecallBypass(InfServer.Game.Player player) { return player != null && _recallBypass.Contains(player); }

        void StartHqRecall(InfServer.Game.Player player) { StartRecallCharge(player, null, 0); }

        void StartRecallCharge(InfServer.Game.Player player, ItemInfo.WarpItem item, ushort target)
        {
            if (player == null || player.IsDead || player._state == null)
                return;
            string label = item == null ? "HQ recall" : (item.name ?? "Recall").Trim('"', ' ');
            HqRecallCharge cur;
            if (_hqRecalls.TryGetValue(player, out cur))
            {
                player.sendMessage(-1, "&" + cur.Label + " already charging - stand still.");
                return;
            }
            int now = Environment.TickCount;
            _hqRecalls[player] = new HqRecallCharge { X = player._state.positionX, Y = player._state.positionY, Due = now + HqRecallChargeMs, NextTell = now + 1000, Item = item, Target = target, Label = label };
            player.sendMessage(0, String.Format("&{0} in {1} s - stand still, moving cancels it.", label, HqRecallChargeMs / 1000));
        }

        /// <summary>The charge is done: the recall's own (engine) warp, let through the script hook this once.</summary>
        void FinishItemRecall(InfServer.Game.Player p, HqRecallCharge c)
        {
            if (p.ActiveVehicle == null || p.ActiveVehicle._type == null || !p.ActiveVehicle._type.IsWarpable)
                return;
            _recallBypass.Add(p);
            try { _arena.handlePlayerWarp(p, c.Item, c.Target, p._state.positionX, p._state.positionY); }
            finally { _recallBypass.Remove(p); }
        }

        void PollHqRecalls(int now)
        {
            if (_hqRecalls.Count == 0)
                return;
            foreach (InfServer.Game.Player p in new List<InfServer.Game.Player>(_hqRecalls.Keys))
            {
                HqRecallCharge c = _hqRecalls[p];
                if (p == null || p.IsDead || p.IsSpectator || p._arena != _arena || p._state == null)
                {
                    _hqRecalls.Remove(p);
                    continue;
                }
                if (Math.Abs(p._state.positionX - c.X) > HqRecallMoveTolerance || Math.Abs(p._state.positionY - c.Y) > HqRecallMoveTolerance)
                {
                    _hqRecalls.Remove(p);
                    p.sendMessage(-1, c.Label + " cancelled - you moved.");
                    continue;
                }
                if (UnderTeleportDisrupt(p))
                {
                    _hqRecalls.Remove(p);
                    p.sendMessage(-1, c.Label + " cancelled - you're inside a teleport disruptor.");
                    continue;
                }
                if (now - c.Due >= 0)
                {
                    _hqRecalls.Remove(p);
                    if (c.Item != null)
                        FinishItemRecall(p, c);
                    else
                        TryHqTeleport(p);
                    continue;
                }
                if (now - c.NextTell >= 0)
                {
                    c.NextTell += 1000;
                    p.sendMessage(0, String.Format("&{0} in {1}...", c.Label, (c.Due - now + 999) / 1000));
                }
            }
        }

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
                    // (2026-11-22, owner: a bought HQ Teleport was never used up - unlimited) the engine's warp code never takes
                    // the charge from the server's inventory, so the next inventory sync gave it back: one is taken per teleport
                    player.inventoryModify(HqTeleportItemId, -1);
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
