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
    public static class Extensions
    {
        public static void averagePosition(this Team team, out short _posX, out short _posY)
        {       //Average the coordinates!
            int count = 0;
            int posX = 0;
            int posY = 0;

            foreach (Player p in team.ActivePlayers)
                if (!p.IsDead)
                {
                    posX += p._state.positionX;
                    posY += p._state.positionY;
                    count++;
                }

            if (count == 0)
            {
                _posX = 0;
                _posY = 0;
                return;
            }

            posX /= count;
            posY /= count;

            _posX = (short)posX;
            _posY = (short)posY;
        }
    }
}
