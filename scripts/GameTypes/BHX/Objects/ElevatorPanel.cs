using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;


using InfServer.Game;
using InfServer.Protocol;
using InfServer.Scripting;
using InfServer.Bots;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_BHX
{ 
    public class ElevatorPanel
    {   ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        private Random _rand;
        private Arena _arena;
        private GameType_BHX _mainScript;
        public Vehicle _switch;
        public int _id;

        public ElevatorPanel(Arena arena, GameType_BHX mainScript, Vehicle computer, int id)
        {
            _mainScript = mainScript;
            _arena = arena;
            _id = id;
            _switch = computer;
        }


        public bool tryPanel(Player player, Computer computer, VehInfo.Computer.ComputerProduct product)
        {
            int idx = Convert.ToInt32(product.Title.Substring(0, 2)) - 1;
            IEnumerable<Player> playersInElevator = _arena.getPlayersInRange(_switch._state.positionX, _switch._state.positionY, 180);
            IEnumerable<Vehicle> botsInElevator = _arena.getVehiclesInRange(_switch._state.positionX, _switch._state.positionY, 180).Where(b => b._bBotVehicle);

            if (idx == _id)
            {
                player.sendMessage(-1, "You're already on this floor");
                return false;
            }

            /*
            switch (idx)
            {
                //1- Lab
                case 1:
                    {
                        if (_mainScript._scientistsRescued < 2)
                        {
                            player.sendMessage(-1, "You haven't rescued all of the scientists from this floor yet!");
                            return false;
                        }
                    }
                    break;
                //2 - Basement
                case 2:
                    {
                        if (_mainScript._scientistsRescued < 4)
                        {
                            player.sendMessage(-1, "You haven't rescued all of the scientists from this floor yet!");
                            return false;
                        }
                    }
                    break;
            }
            */

            Helpers.ObjectState warpTo = _mainScript._elevatorPanels[idx]._switch._state;

            foreach (Player p in playersInElevator)
                p.warp(Helpers.ResetFlags.ResetNone, warpTo, 150, -1, 0);

            foreach (Vehicle p in botsInElevator)
            {
                Bot bot;
                if (p._type.Id == 105)
                    bot = _mainScript._botHandler._marines.FirstOrDefault(b => b._id == p._id);
                else
                    bot = _mainScript._botHandler._skrall.FirstOrDefault(b => b._id == p._id);

                if (bot != null)
                {
                    bot._movement.warp(warpTo);
                    bot.Navigation.Clear();
                }
            }

            return true;
        }
    }
}