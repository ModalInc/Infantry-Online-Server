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
    public class Objective
    {   ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        private Random _rand;
        private Arena _arena;
        private BotHandler _handler;
        private GameType_BHX _mainScript;
        public Scientist _botScientist;
        public Vehicle _npc;
        public int hideID;
        public string _name;
        public bool bObjectiveFollowing;
        public bool bObjectiveRescued;
        public short posX;
        public short posY;

        private int _tickLastWave;

        public void poll(int now)
        {
            if (now - _tickLastWave >= 1000 && !bObjectiveFollowing)
            {
                Helpers.Player_RouteExplosion(_arena.getTeamByName("Marines").AllPlayers, 3060, _npc._state.positionX, _npc._state.positionY, 0, 0, 0);
                _tickLastWave = now;
            }


        }

        public Objective(Arena arena, GameType_BHX mainScript, Scientist scientist, Vehicle computer, string name)
        {
            _mainScript = mainScript;
            _arena = arena;
            _botScientist = scientist;
            _npc = computer;
            _name = name;
        }

        public void objectiveDeath()
        {
            _arena.setTicker(1, 4, 0, "");
            bObjectiveFollowing = false;
            int levelX = _arena._server._assets.Level.OffsetX * 16;
            int levelY = _arena._server._assets.Level.OffsetY * 16;


            LioInfo.Hide hide = _arena._server._assets.Lios.Hides.FirstOrDefault(hi => hi.GeneralData.Name == _name);

            Helpers.ObjectState newObj = new Helpers.ObjectState();
            newObj.positionX = (short)(hide.GeneralData.OffsetX - levelX);
            newObj.positionY = (short)(hide.GeneralData.OffsetY - levelY);

            _npc = _arena.newVehicle(AssetManager.Manager.getVehicleByID(403), _arena.getTeamByName("Marines"), null, newObj);

            _arena.sendArenaMessage(String.Format("The Scientist {0} has died. (Respawning at {1})", _name, Helpers.posToLetterCoord(_npc._state.positionX, _npc._state.positionY)));

        }

        public void arrivedAtDZ(int now)
        {
            _arena.setTicker(1, 4, 20 * 100, String.Format("Transport for {0} arriving in.. ", _name));
            _arena.sendArenaMessage(String.Format("Scientist {0} has arrived at the DZ, Awaiting transport..", _name));

            posX = _botScientist._state.positionX;
            posY = _botScientist._state.positionY;
        }

        public void objectiveRescued()
        {
            bObjectiveRescued = true;
            _arena.sendArenaMessage(String.Format("Scientist {0} has been rescued!", _name));
            _mainScript.spawnSupplyDrop(posX, posY);
        }
    }
}