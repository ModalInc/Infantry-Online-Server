using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Scripting;
using InfServer.Bots;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_BHX
{
    public class BotHandler
    {
        public List<Bot> _skrall;
        public List<Bot> _marines;
        private Arena _arena;
        public List<Bot> _condemnedBots;
        public bool bBotsEnabled;
        public int _maxBots = 30;
        public int _lastWave;
        public Random _rand = new Random();

        public BotHandler(Arena arena)
        {
            _skrall = new List<Bot>();
            _marines = new List<Bot>();
            _arena = arena;
        }

        /// <summary>
        /// Maintains our bots
        /// </summary>
        public void poll(int now)
        {
            //Are even running?
            if (!bBotsEnabled)
                return;
        }

        public Scientist newScientist(Helpers.ObjectState state, Team team, Team targetTeam, Player creator, Objective objective)
        {
            //Assume the worst
            bool bSuccess = false;

            //Grab our vehicle info
            VehInfo botVehicle = AssetManager.Manager.getVehicleByID((int)105);

            //Does this type exist in our vehicle file?
            if (botVehicle == null)
            {
                return null;
            }

            Scientist scientist = _arena.newBot(typeof(Scientist), (ushort)105, team, creator, state, null) as Scientist;
            if (scientist == null)
                return null;
            else
                bSuccess = true;

            scientist.init(objective);

            scientist.Destroyed += delegate (Vehicle bot)
            {
                _marines.Remove((Bot)bot);
            };

            _marines.Add(scientist);

            return scientist;
        }

        public Bot newQueen(Helpers.ObjectState state, Team team, Team targetTeam)
        {
            //Assume the worst
            bool bSuccess = false;

            //Grab our vehicle info
            VehInfo botVehicle = AssetManager.Manager.getVehicleByID((int)106);

            //Does this type exist in our vehicle file?
            if (botVehicle == null)
            {
                return null;
            }

            Queen queen = _arena.newBot(typeof(Queen), (ushort)106, team, null, state, null) as Queen;
            if (queen == null)
                return null;
            else
                bSuccess = true;

            queen.init(targetTeam);

            queen.Destroyed += delegate (Vehicle bot)
            {
                _skrall.Remove((Bot)bot);
            };

            _skrall.Add(queen);

            return queen;
        }


        /// <summary>
        /// Handles the creation of a new bot within the script
        /// </summary>
        public bool newBot(Configuration.BotType botType, Helpers.ObjectState state, Team team, Team targetTeam, Nest nest, Player creator)
        {
            //Assume the worst
            bool bSuccess = false;

            //Max bots?
            if (_skrall.Count >= _maxBots)
            {
                return false;
            }

            //Grab our vehicle info
            VehInfo botVehicle = AssetManager.Manager.getVehicleByID((int)botType);

            //Does this type exist in our vehicle file?
            if (botVehicle == null)
            {
                return false;
            }

            Skrall skrall = _arena.newBot(typeof(Skrall), (ushort)botType, team, creator, state, null) as Skrall;
            if (skrall == null)
                return false;
            else
                bSuccess = true;

            skrall.init(targetTeam);

            double result = (skrall._type.Hitpoints / 100) * 50;

            skrall._state.health = (short)result;

            skrall.Destroyed += delegate (Vehicle bot)
            {
                _skrall.Remove((Bot)bot);
                if (nest != null)
                nest.lostBot((Bot)bot);
            };

            _skrall.Add(skrall);

            if (nest != null)
                nest.newBot(skrall);

            return bSuccess;
        }
    }
}
