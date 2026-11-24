using System;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-26, owner: "for vehicle bots please use another devs code instead") a bot rides a board / pack the way the
    /// other dev does it: a Bot IS its vehicle, so it has no rider slot like a player - its own body becomes the board under
    /// the SAME id, and swaps back. So the board going down no longer kills the bot and makes a new "rider" bot: the same bot
    /// (same id, name, bounty) just swaps back to its body. Order rule from the other dev: swap back to the body FIRST, then
    /// any death - a death sent while the body is still the board gets the kill credited to a hoverboard.
    /// </summary>
    public partial class RoamingAttacker : Bot
    {
        VehInfo.Car _footType;   // the body type while riding; null = on foot

        /// <summary>Riding a board / pack right now.</summary>
        public bool IsMounted { get { return _footType != null; } }

        public void Mount(VehInfo.Car board)
        {
            if (_footType != null || board == null)
                return;
            _footType = _type as VehInfo.Car;
            SwapType(board);
        }

        public void Dismount()
        {
            if (_footType == null)
                return;
            VehInfo.Car foot = _footType;
            _footType = null;
            SwapType(foot);
        }

        /// <summary>A bot spawned already on its board: remember the body it gets off as.</summary>
        public void SetFootBody(VehInfo.Car foot)
        {
            if (foot != null)
                _footType = foot;
        }

        void SwapType(VehInfo.Car info)
        {
            _type = info;
            if (_movement != null)
                _movement.SetCarType(info);   // moves as the new body too
            if (_arena != null)
                Helpers.Object_Vehicles(_arena.Players, this);   // re-send the body, same as the engine does on a class change
        }

        /// <summary>
        /// The board / pack is destroyed: the rider gets off and carries on (Script_Eol.OnBoardDestroyed) instead of dying.
        /// A death while still mounted (no script to hand it to) swaps back to the body BEFORE the death goes out.
        /// </summary>
        public override void kill(InfServer.Game.Player killer, int weaponID)
        {
            if (_footType != null)
            {
                if (_baseScript != null && _baseScript.OnBoardDestroyed(this, killer))
                    return;
                Dismount();
            }
            base.kill(killer, weaponID);
        }
    }
}
