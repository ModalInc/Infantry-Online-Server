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
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public class Nest
    {   ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        //private Bot _bot;							//Pointer to our bot class
        private Team _team;
        private Team _targetTeam;
        private Arena _arena;
        private BotHandler _handler;
        public Vehicle _vehicle;
        private GameType_BHX _script;
        private List<Bot> _bots = new List<Bot>();

        public int maxActive = 1;
        private bool bSpawning;
        private bool _bQueen;
        private Bot _queen;
        private bool _bQueenSpawned;

        private int _proximityMinTrigger = 1000;
        private int _proximityMaxTrigger = 2500;
        private int _proximityTrigger;

        //100 tick ~ 30 sec
        private int tickLastHatch = Environment.TickCount;
        private int tickMinHatch = 1000;
        private int tickMaxHatch = 90000;
        private int tickNextHatch;
        
        //private int tickWaveHatch = 1000;
        
        //public int BotCount
        //{
        //    get
        //    {
        //        return _bots.Count;
        //    }
        //}

        public Nest(Team team, Team targetTeam, Arena arena, BotHandler handler, Vehicle vehicle, GameType_BHX script, bool queenNest)
        {
            _team = team;
            _targetTeam = targetTeam;
            _arena = arena;
            _handler = handler;
            _vehicle = vehicle;
            _script = script;
            _bQueen = queenNest;

            _proximityTrigger = _script._botHandler._rand.Next(_proximityMinTrigger, _proximityMaxTrigger);
            tickNextHatch = _script._botHandler._rand.Next(tickMinHatch, tickMaxHatch);
    }

        public void lostBot(Bot bot)
        {
            _bots.Remove(bot);
        }

        public void newBot(Bot bot)
        {
            _bots.Add(bot);
        }

        public void poll(int now)
        {
            List<Player> proximity = _arena.getPlayersInRange(_vehicle._state.positionX, _vehicle._state.positionY,
                _proximityTrigger).Where(player => player._team == _targetTeam && !player.IsDead).ToList();
            int proximityCount = proximity.Count;

            if (proximityCount > 0)
            {
                bSpawning = true;
                int elapsedMod = (int)((proximityCount + timeElapsedMultiplier()) * _script._marinesMultiplier);
                //int marineMod = (int)(proximityCount * _script._marinesMultiplier);
                int sciMod = (int)(_script._scientistsRescued * _script._scientistMultiplier);
                int SkrallMod = (int)(_team.ActivePlayerCount * _script._skrallMultiplier);
                maxActive = elapsedMod + sciMod - SkrallMod + 1;
                //_arena.sendArenaMessage(String.Format("maxActive {0}, el {1}, mar {2}, sci {3}, skr {4}", maxActive, elapsedMod, marineMod, sciMod, SkrallMod));
            }
            else
                bSpawning = false;

            //(now - _script._botHandler._lastWave >= tickWaveHatch)
            if (bSpawning && _script._botHandler._skrall.Count < _script._botHandler._maxBots 
                && (now - tickLastHatch >= tickNextHatch) && !_bQueen)
            {
                int hatchlingCount = _script._botHandler._rand.Next(0, maxActive);
                //_arena.sendArenaMessage(String.Format("hatchling {0}, max {1}", hatchlingCount, maxActive));
                if (hatchlingCount > 0)
                {
                    foreach (Player player in proximity)
                        player.triggerMessage(0, 500, "Did you hear that??! Keep your head on a swivel!");

                    InfServer.Protocol.Helpers.ObjectState _newState = new InfServer.Protocol.Helpers.ObjectState();
                    for (int i = 1; i <= hatchlingCount; i++)
                    {
                        _newState.positionX = (short)(_vehicle._state.positionX + _script._botHandler._rand.Next(-25, 25));
                        _newState.positionY = (short)(_vehicle._state.positionY + _script._botHandler._rand.Next(-25, 25));
                        //_vehicle._state.positionX = (short)(_vehicle._state.positionX + _script._botHandler._rand.Next(-100, 100));
                        //_vehicle._state.positionY = (short)(_vehicle._state.positionY + _script._botHandler._rand.Next(-100, 100));
                        _handler.newBot((Configuration.BotType)_script._botHandler._rand.Next(200, 205), _newState, _team, _targetTeam, this, null);
                        //_handler.newBot((Configuration.BotType)_script._botHandler._rand.Next(200, 205), _vehicle._state, _team, _targetTeam, this, null);
                    }
                }

                _proximityTrigger = _script._botHandler._rand.Next(_proximityMinTrigger, _proximityMaxTrigger);
                tickNextHatch = _script._botHandler._rand.Next(tickMinHatch, tickMaxHatch);
                tickLastHatch = now;
                //tickWaveHatch = now
                //_script._botHandler._lastWave = now;
            }
            else if (bSpawning && _bQueen)
            {
                if (_queen != null && _bQueenSpawned == true)
                {
                    return;
                }

                foreach (Player player in proximity)
                    player.triggerMessage(0, 500, "Did you hear that??! WTF is that? OH GOD!");


                _queen = _handler.newQueen(_vehicle._state, _team, _targetTeam);

                if (_queen != null)
                    _bQueenSpawned = true;
            }
        }

        public double timeElapsedMultiplier()
        {
            double result = 0;

            int tickStart = _arena._tickGameStarted;
            int elapsed = Environment.TickCount - tickStart;

            //5 Minutes
            if (elapsed >= 300000)
                result = 1;
            //10 Minutes
            else if (elapsed >= 600000)
                result = 2;
            //15 Minutes
            else if (elapsed >= 900000)
                result = 3;
            //20 Minutes
            else if (elapsed >= 1200000)
                result = 4;
            //25 minutes 
            else if (elapsed >= 1500000)
                result = 5;
            return result;
        }
    }
}