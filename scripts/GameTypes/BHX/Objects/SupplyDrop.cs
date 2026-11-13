using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Scripting;
using InfServer.Bots;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_BHX
{
    public class SupplyDrop
    {
        public Team _team;      //The team we belong to\
        public Arena _arena;
        public short posX;
        public short posY;
        public string _name;
        public Computer _computer;
        private Random _rand;
        private int tickLastPing;
        private bool bOpened;

        public SupplyDrop(Team team, Computer computer, short pX, short pY)
        {
            _arena = team._arena;
            _team = team;
            posX = pX;
            posY = pY;
            _computer = computer;
            _name = Helpers.posToLetterCoord(posX, posY);
            _rand = new Random();
        }


        public void poll(int now)
        {
        }


        public void open(Player from)
        {
            _computer.destroy(true);
            bOpened = true;


            //Generic stuff..
            for (int i = 0; i < _team.ActivePlayerCount; ++i)
            {
                _arena.spawnItemInArea(AssetManager.Manager.getItemByName("Bullets"), (ushort)_rand.Next(150, 275), posX, posY, 150);
                _arena.spawnItemInArea(AssetManager.Manager.getItemByName("AC Rounds"), (ushort)_rand.Next(150, 275), posX, posY, 150);
                _arena.spawnItemInArea(AssetManager.Manager.getItemByName("Hand Grenade"), (ushort)_rand.Next(0, 3), posX, posY, 150);
                _arena.spawnItemInArea(AssetManager.Manager.getItemByName("Incinerator Charge"), (ushort)_rand.Next(50, 100), posX, posY, 150);
                _arena.spawnItemInArea(AssetManager.Manager.getItemByName("Shotgun Shells"), (ushort)_rand.Next(55, 100), posX, posY, 150);
            }

            //How many rocketeers we got?
            int rocketeers = _arena.PlayersIngame.Count(p => p.findSkill(-7) != null);

            for (int i = 0; i < rocketeers; ++i)
            {
                _arena.spawnItemInArea(AssetManager.Manager.getItemByName("Rocket Launcher"), 8, posX, posY, 150);
            }

                //Alert our lucky players
                _team.sendArenaMessage(String.Format("&The supply crate at {0} has been opened", _name), 4);
        }
    }

    public static class ArenaExtensions
    {
        /// <summary>
        /// Spawns the given item randomly in the specified area
        /// </summary>
        public static void spawnItemInArea(this Arena arena, ItemInfo item, ushort quantity, short x, short y, short radius)
        {       //Sanity
            if (quantity <= 0)
                return;

            int blockedAttempts = 30;

            short pX;
            short pY;
            while (true)
            {
                pX = x;
                pY = y;
                Helpers.randomPositionInArea(arena, radius, ref pX, ref pY);
                if (arena.getTile(pX, pY).Blocked)
                {
                    blockedAttempts--;
                    if (blockedAttempts <= 0)
                        //Consider the spawn to be blocked
                        return;
                    continue;
                }
                arena.itemSpawn(item, quantity, pX, pY, null);
                break;
            }
        }
    }
}
