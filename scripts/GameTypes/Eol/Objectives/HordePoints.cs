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
    public class HordePoints : Computer
    {
        public short posX;
        public short posY;
        public int height;
        public int width;
        public bool active;
        public bool _isBeingCaptured;
        public Team team;
        private Arena _arena;
        private int tickLastWave;
        private int tickStartCapture;
        public event Action<Vehicle> Captured;
        IEnumerable<Vehicle> hps;
        public Vehicle _hPoint;
        private int tickLastReward;
        private int _tickLastHordeWaveExplosionLog;
        const int HordeWaveExplosionLogMinIntervalMs = 5000;
        List<InfServer.Game.Player> playersInArea;
        List<Vehicle> HordePs;
        List<Vehicle> botsInArea;
        List<Team> playerteamsinarea;
        Dictionary<Team, int> teamcounts;
        public Script_Eol eol;

        //Settings
        private int _CaptureRadius = 400;
        private int _CaptureTime = 5;


        public HordePoints(VehInfo.Computer type, Arena arena)
           : base(type, arena)
        {
            tickLastWave = 0;
            hps = _arena.Vehicles.Where((Vehicle v) => v._type.Id == 468);
            playersInArea = new List<InfServer.Game.Player>();
            HordePs = new List<Vehicle>();
            botsInArea = new List<Vehicle>();

            posX = this._state.positionX;
            posY = this._state.positionY;
            team = this._team;
        }


        public void poll(int now)
        {
            
            int attackerplayers = 0;
            int attackerbots = 0;
            int biggestattacker = 0;
            int defenderplayers = 0;
            int defenderbots = 0;
            int[] botids = new int[] { 142, 144, 145, 146, 147, 157 };
            playerteamsinarea = new List<Team>();
            teamcounts = new Dictionary<Team, int>();

            playersInArea.Clear();
            HordePs.Clear();
            botsInArea.Clear();
            playerteamsinarea.Clear();
            teamcounts.Clear();

            playersInArea = _arena.getPlayersInRange(posX, posY, 400).Where((InfServer.Game.Player p) => !p.IsDead).ToList();
            botsInArea = _arena.getVehiclesInRange(posX, posY, 400).Where((Vehicle v) => v._id == 142 || v._id == 144 || v._id == 145 || v._id == 146 || v._id == 147 || v._id == 157).ToList();

            if (playersInArea.Count() > 0)
            {
                foreach (InfServer.Game.Player pt in playersInArea)
                    playerteamsinarea.Add(pt._team);
            }

            if (playerteamsinarea.Count() > 0)
            {
                foreach (Team t in playerteamsinarea)
                {
                    if (!teamcounts.ContainsKey(t))
                    {
                        teamcounts.Add(t, 1);
                    }
                    else
                    {
                        int count = 0;
                        teamcounts.TryGetValue(t, out count);
                        teamcounts.Remove(t);
                        teamcounts.Add(t, count + 1);
                    }
                }
            }

            Team attacker = teamcounts.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;

            attackerplayers = playersInArea.Count((InfServer.Game.Player p) => p._team != team);
            attackerbots = botsInArea.Count((Vehicle v) => v._team != team);
            defenderplayers = playersInArea.Count((InfServer.Game.Player p) => p._team == team);

            if (attackerplayers > attackerbots)
            {
                biggestattacker = attackerplayers;
            }
            else if (attackerbots > attackerplayers)
            {
                biggestattacker = attackerbots;
            }


            if (now - tickLastReward > 2000)
            {
                if (this._team != null)
                {
                    if (this._team != eol.botTeam4)
                    {

                        int cashReward = 1250;
                        int expReward = 950;
                        int pointReward = 1500;

                        foreach (InfServer.Game.Player player in playersInArea.Where((InfServer.Game.Player p) => p._team == team))
                        {
                            player.sendMessage(0, String.Format("&SCU ownership reward: (Cash={0} Experience={1} Points={2})", cashReward, expReward, pointReward));
                            player.Cash += cashReward;
                            player.Experience += expReward;
                            player.BonusPoints += pointReward;

                            player.syncState();
                        }
                    }
                }

                tickLastReward = now;
            }

            if (biggestattacker == 0 && defenderplayers == 0)
            {
                tickStartCapture = 0;
            }

            if (biggestattacker > defenderplayers)
            {

                if (now - tickLastWave >= 1500)
                {
                    if (_tickLastHordeWaveExplosionLog == 0 || now - _tickLastHordeWaveExplosionLog >= HordeWaveExplosionLogMinIntervalMs)
                    {
                        _tickLastHordeWaveExplosionLog = now;
                        Log.write(TLog.Normal,
                            "[Eol] HordePoints wave explosion proj=3095 recipientsAll={0} playersInArea={1} attackerPlayers={2} attackerBots={3} defenderPlayers={4} biggest={5}",
                            _arena.PlayerCount, playersInArea.Count, attackerplayers, attackerbots, defenderplayers, biggestattacker);
                    }
                    if (eol != null)
                        eol.EnqueueExplosionRoute(3095, posX, posY, 0, 0, 0);
                    else
                        Helpers.Player_RouteExplosion(_arena.Players, 3095, posX, posY, 0, 0, 0);
                    tickLastWave = now;
                }

                if (tickStartCapture != 0 && biggestattacker > 0)
                {
                    int quickCaptureMod = ((biggestattacker - 1) * 1000);
                    tickStartCapture -= quickCaptureMod;
                }

                if (tickStartCapture != 0 && now - tickStartCapture >= 10000)
                {
                    _arena.triggerMessage(0, 500, String.Format("{0} has taken control of the SCU.", attacker));
                    this._team = attacker;
                    this._type.Name = "SCU [" + attacker + "]";

                    tickStartCapture = 0;

                    int cashReward = 1250;
                    int expReward = 950;
                    int pointReward = 1500;

                    foreach (InfServer.Game.Player player in playersInArea.Where((InfServer.Game.Player p) => p._team == team))
                    {
                        player.sendMessage(0, String.Format("&SCU Capture reward: (Cash={0} Experience={1} Points={2})", cashReward, expReward, pointReward));
                        player.Cash += cashReward;
                        player.Experience += expReward;
                        player.BonusPoints += pointReward;

                        player.syncState();
                    }
                }

            }

            if (defenderplayers > biggestattacker)
            {
                if (now - tickLastWave >= 1500)
                {
                    tickStartCapture = 0;
                }
            }
            else
            {
                if (biggestattacker == defenderplayers && biggestattacker > 0 && defenderplayers > 0)
                {
                    if (now - tickLastWave >= 1500)
                    {
                        if (_tickLastHordeWaveExplosionLog == 0 || now - _tickLastHordeWaveExplosionLog >= HordeWaveExplosionLogMinIntervalMs)
                        {
                            _tickLastHordeWaveExplosionLog = now;
                            Log.write(TLog.Normal,
                                "[Eol] HordePoints wave explosion proj=1095 recipientsAll={0} playersInArea={1} attackerPlayers={2} attackerBots={3} defenderPlayers={4} biggest={5}",
                                _arena.PlayerCount, playersInArea.Count, attackerplayers, attackerbots, defenderplayers, biggestattacker);
                        }
                        if (eol != null)
                            eol.EnqueueExplosionRoute(1095, posX, posY, 0, 0, 0);
                        else
                            Helpers.Player_RouteExplosion(_arena.Players, 1095, posX, posY, 0, 0, 0);
                        tickLastWave = now;
                    }
                    tickStartCapture = 0;
                }
            }

            return;
        }
    }
}
