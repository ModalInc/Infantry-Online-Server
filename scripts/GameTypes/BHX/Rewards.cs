using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.RegularExpressions;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Scripting;
using InfServer.Bots;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_BHX
{


    public class Jackpot
    {
        public Player mvp;
        public Player lvp;
        public Dictionary<Player, Reward> _playerRewards;

        public int totalMVP;
        public int totalJackPot;

        public Jackpot()
        {
            _playerRewards = new Dictionary<Player, Reward>();
        }

        public class Reward
        {
            public Player player;
            public double MVP = 0;
            public int Score;
            public int experience;
            public int cash;
            public int points;
        }
    }

    class Rewards
    {


        //KillRewards
        public const int c_baseReward = 25;
        public const double c_pointMultiplier = 2;
        public const double c_cashMultiplier = 1;
        public const double c_expMultiplier = 0.5;
        public const int c_percentOfVictim = 50;
        public const int c_percentOfOwn = 3;
        public const int c_percentOfOwnIncrease = 5;

        static public Jackpot calculateJackpot(IEnumerable<GameType_BHX.PlayerStat> playerStats, Team winner, bool bTicker)
        {
            Jackpot result = new Jackpot();
            int fixedjackpot = 0;
            Jackpot.Reward rewards;
            int highest = 0;

            double pointsPerKill = 0;
            double pointsPerDeath = 0;
            double pointsPerRescue = 0;

            int totalkills = 0;
            int totalDeaths = 0;
            int gameLength = 0;
            if (winner != null)
            {
                totalkills = winner._currentGameKills;
                totalDeaths = winner._currentGameDeaths;
                gameLength = (winner._arena._tickGameEnded - winner._arena._tickGameStarted) / 1000;
            }


            fixedjackpot = 6500;
            pointsPerKill = 1.25;
            pointsPerDeath = 0.25;
            pointsPerRescue = 3.5;



            //Set our fixed
            result.totalJackPot += fixedjackpot;

            //Calculate MVP
            foreach (GameType_BHX.PlayerStat stats in playerStats)
            {
                rewards = new Jackpot.Reward();
                int score = 0;
                rewards.player = stats.player;

                score += Convert.ToInt32((stats.kills * pointsPerKill));
                score += Convert.ToInt32((stats.deaths * pointsPerDeath));
                score += Convert.ToInt32((stats.rescues * pointsPerRescue));


                //Tally up his overall score and check if it's our new highest
                rewards.Score = score;
                result.totalMVP += score;
                result._playerRewards.Add(stats.player, rewards);

                if (score >= highest)
                    highest = score;
            }

            //Calculate Total Jackpot
            result.totalJackPot += (int)(totalkills * pointsPerKill);
            result.totalJackPot += (int)(totalDeaths * pointsPerDeath);
            result.totalJackPot += (int)(gameLength * 2.25);


            //Calculate each players reward based on their MVP score and the overall jackpot
            foreach (Jackpot.Reward reward in result._playerRewards.Values)
            {

                reward.MVP = (double)reward.Score / (double)highest;
                if (!bTicker)
                {
                    reward.points = Convert.ToInt32(result.totalJackPot * reward.MVP);
                    reward.cash = Convert.ToInt32(((result.totalJackPot * reward.MVP) * 0.5));
                    reward.experience = Convert.ToInt32(10000 /* base reward for finishing */ + (result.totalJackPot * reward.MVP) * 0.5);
                }
            }
            return result;
        }

        /// <summary>
        /// Calculates and rewards a players for a bot kill
        /// </summary>
        static public void calculateBotKillRewards(Bots.Bot victim, Player killer)
        {
            CfgInfo cfg = killer._server._zoneConfig;
            int killerCash = 0;
            int killerExp = 0;
            int killerPoints = 0;
            int killerBounty = 0;
            int victimBounty = 0;
            int killerBountyIncrease = 0;

            BotSettings settings = victim.Settings();
            if (settings != null)
            {
                killerBounty = Convert.ToInt32(((double)killer.Bounty / 100) * c_percentOfOwn); //We are now using base reward of 25 + 3% of current bounty
                killerPoints = Convert.ToInt32((settings.Points) + (killerBounty * c_pointMultiplier));
                killerCash = Convert.ToInt32((settings.Cash) + (killerBounty * c_cashMultiplier));
                killerExp = Convert.ToInt32((settings.Experience) + (killerBounty * c_expMultiplier) +100);
                victimBounty = settings.Bounty;
                killerBountyIncrease = 0;

            }
            else
            {
                killerBounty = Convert.ToInt32(((double)killer.Bounty / 100) * c_percentOfOwn); //We are now using base reward of 25 + 3% of current bounty
                killerPoints = Convert.ToInt32(((int)cfg.bot.pointsKillReward) + (killerBounty * c_pointMultiplier));
                killerCash = Convert.ToInt32(((int)cfg.bot.cashKillReward) + (killerBounty * c_cashMultiplier));
                killerExp = Convert.ToInt32(((int)cfg.bot.expKillReward) + (killerBounty * c_expMultiplier) +50);
                victimBounty = (int)cfg.bot.fixedBountyToKiller;
                killerBountyIncrease = 0;
            }

            //Update his stats
            killer.Experience += killerExp;
            killer.KillPoints += killerPoints;
            //killer.Bounty += killerBounty;
            killer.Bounty += (killerBountyIncrease + victimBounty);

            //Inform the killer..
            killer.triggerMessage(1, 500,
                //String.Format("{0} killed by {1} (Cash={2} Exp={3} Points={4})",
                String.Format("{0} killed by {1} (Exp={3} Points={4})",
                victim._type.Name, killer._alias,
                killerCash, killerExp, killerPoints));

            //Sync his state
            killer.syncState();

            //Check for players in the share radius
            List<Player> sharedRewards = victim._arena.getPlayersInRange(victim._state.positionX, victim._state.positionY, 500);//cfg.bot.shareRadius);
            Dictionary<int, int> cashRewards = new Dictionary<int, int>();
            Dictionary<int, int> expRewards = new Dictionary<int, int>();
            Dictionary<int, int> pointRewards = new Dictionary<int, int>();

            foreach (Player p in sharedRewards)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                cashRewards[p._id] = 0;//(int)((((float)killerCash) / 1000) * cfg.bot.sharePercent);
                expRewards[p._id] = 10;//(int)((((float)killerExp) / 1000) * cfg.bot.sharePercent);
                pointRewards[p._id] = 1;//(int)((((float)killerPoints) / 1000) * cfg.bot.sharePercent);
            }

            //Send reward notices to our lucky witnesses
            List<int> sentTo = new List<int>();
            foreach (Player p in sharedRewards)
            {
                if (p == killer || p._team != killer._team)
                    continue;

                cashRewards[p._id] = addCash(p, cashRewards[p._id]);
                p.Experience += expRewards[p._id];
                p.AssistPoints += pointRewards[p._id];

                //Let em know
                p.triggerMessage(4, 500,
                    //String.Format("{0} killed by {1} (Cash={2} Exp={3} Points={4})",
                    String.Format("{0} killed by {1} (Exp={3} Points={4})",
                    victim._type.Name, killer._alias, cashRewards[p._id],
                    expRewards[p._id], pointRewards[p._id]));


                //Sync their state
                p.syncState();

                sentTo.Add(p._id);
            }

            //Route the kill to the rest of the arena
            foreach (Player p in victim._arena.Players)
            {	//As long as we haven't already declared it, send
                if (p == null)
                    continue;

                if (p == killer)
                    continue;

                if (sentTo.Contains(p._id))
                    continue;

                //Adjust our color accordingly..
                byte color = 0;
                if (victim._team == p._team)
                    color = 4;

                //Let them know
                p.triggerMessage(color, 500,
                    String.Format("{0} killed by {1}",
                    victim._type.Name, killer._alias));
            }
        }


        static public int addCash(Player player, int quantity)
        {
            //Any rare cash item?
            double multiplier = 1.0;
            int cash = 0;

            cash = Convert.ToInt32(quantity * multiplier);

            player.Cash += cash;
            player.syncState();

            return cash;
        }

    }
}
