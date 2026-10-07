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

namespace InfServer.Script.GameType_Eol
{
    public partial class Teams
    {
        ///////////////////////////////////////////////////
        // Member Variables
        ///////////////////////////////////////////////////
        private Arena _arena;					//Pointer to our arena class
        private CfgInfo _config;                //The zone config
        public Script_Eol _baseScript;

        public Teams(Arena arena, Script_Eol baseScript)
        {
            _arena = arena;
            _config = arena._server._zoneConfig;
            _baseScript = baseScript;

        }

        public bool Poll(int now)
        {
            return true;
        }

        /// <summary>
        /// Determines which team is appropriate for the player to be playing on
        /// </summary>
        public virtual Team pickTeam(InfServer.Game.Player player)
        {
            List<Team> publicTeams = _arena.PublicTeams.ToList();
            Team pick = null;
            int playing = _arena.PlayersIngame.Count();
            if (_config.arena.forceEvenTeams)
            {	//We just want one for each team
                int playerCount = 0;

                for (int i = 0; i < _config.arena.desiredFrequencies; ++i)
                {	//Do we have more active players than the last?
                    Team team = publicTeams[i];
                    int maxPlayers = team._info.maxPlayers;
                    int activePlayers = team.ActivePlayerCount;
                    if (playing < 30) //30
                    { maxPlayers = 5; }
                    if (playing >= 30 && playing < 60) //30
                    { maxPlayers = 6; }
                    if (playing > 60)
                    { maxPlayers = 8; }

                    if ((pick == null && maxPlayers != -1) ||
                        (playerCount > activePlayers &&
                            (maxPlayers == 0 || playerCount <= maxPlayers)))
                    {
                        pick = team;
                        playerCount = activePlayers;
                    }
                }

                if (pick == null)
                    return null;
            }
            else
            {	//Spread them out until we hit our desired number of frequencies
                int playerCount = int.MaxValue;
                int desiredFreqs = _config.arena.desiredFrequencies;
                int idx = 0;

                while (desiredFreqs > 0 && publicTeams.Count > idx)
                {	//Valid team?
                    Team team = publicTeams[idx++];
                    int maxPlayers = team._info.maxPlayers;
                    if (playing < 30) //30
                    { maxPlayers = 5; }
                    if (playing >= 30 && playing < 60) //30
                    { maxPlayers = 6; }
                    if (playing > 60)
                    { maxPlayers = 8; }

                    if (maxPlayers == -1)
                        continue;

                    //Do we have less active players than the last?
                    int activePlayers = team.ActivePlayerCount;

                    if (activePlayers < playerCount &&
                        (maxPlayers == 0 || activePlayers + 1 <= maxPlayers))
                    {
                        pick = team;
                        playerCount = activePlayers;

                        if (activePlayers == 0)
                            break;
                    }

                    if (activePlayers > 0)
                        desiredFreqs--;
                }

                if (pick == null)
                {	//Desired frequencies are all full, go to our extra teams!
                    playerCount = -1;
                    desiredFreqs = _config.arena.frequencyMax;
                    idx = 0;

                    while (desiredFreqs > 0 && publicTeams.Count > idx)
                    {	//Valid team?
                        Team team = publicTeams[idx++];
                        int maxPlayers = team._info.maxPlayers;
                        if (playing < 30) //30
                        { maxPlayers = 1; }
                        if (playing >= 30 && playing < 60) //30
                        { maxPlayers = 8; }
                        if (playing > 60)
                        { maxPlayers = 10; }

                        if (maxPlayers == -1)
                            continue;

                        //Do we have more active players than the last?
                        int activePlayers = team.ActivePlayerCount;

                        if (activePlayers > playerCount &&
                            (maxPlayers == 0 || activePlayers + 1 <= maxPlayers))
                        {
                            pick = team;
                            playerCount = activePlayers;
                        }

                        if (activePlayers > 0)
                            desiredFreqs--;
                    }
                }
            }

            return pick;
        }

        public void gameStart()
        {

        }

        public bool gamesEnd()
        {
            return true;
        }

        public bool gameReset()
        {
            return true;
        }
    }
}
