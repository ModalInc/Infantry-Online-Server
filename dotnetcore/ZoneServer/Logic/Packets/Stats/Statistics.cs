using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Protocol;
using InfServer.Game;
using InfServer.Bots;

namespace InfServer.Logic
{	// Logic_Statistics Class
	/// Deals with handling statistics
	///////////////////////////////////////////////////////
	class Logic_Statistics
	{	/// <summary>
		/// Handles all player update packets received from clients
		/// </summary>
		static public void Handle_CS_ChartRequest(CS_ChartRequest pkt, Player player)
		{	//Make sure he has an arena
			if (player._arena == null)
			{
				Log.write(TLog.Error, "Player {0} requested chart with no arena.", player);
				return;
			}

			//What sort of chart request?
			SC_ScoreChart chart = new SC_ScoreChart();

			switch (pkt.type)
			{
				case Helpers.ChartType.ScoreOnlinePlayers:
					{
						chart.type = Helpers.ChartType.ScoreOnlinePlayers;
						chart.columns = "Online Player Scores,Name,Squad";
						chart.rows = BuildArenaChartRows(player._arena,
							p => p.StatsTotal,
							b => b.GetChartStats());
						player._client.sendReliable(chart, 1);
					}
					break;

				case Helpers.ChartType.ScoreCurrentGame:
					{
						chart.type = Helpers.ChartType.ScoreCurrentGame;
						chart.columns = "Current Game Scores,Name,Squad";
						chart.rows = BuildArenaChartRows(player._arena,
							p => p.StatsCurrentGame,
							b => b.GetChartStats());
						player._client.sendReliable(chart, 1);
					}
					break;

				case Helpers.ChartType.ScorePreviousGame:
					{
						chart.type = Helpers.ChartType.ScorePreviousGame;
						chart.columns = "Current Session Scores,Name,Squad";
						chart.rows = BuildArenaChartRows(player._arena,
							p => p.StatsLastGame,
							b => b._statsLastGame);
						player._client.sendReliable(chart, 1);
					}
					break;

				case Helpers.ChartType.ScoreCurrentSession:
					{
						chart.type = Helpers.ChartType.ScoreCurrentSession;
						chart.columns = "Current Session Scores,Name,Squad";
						chart.rows = BuildArenaChartRows(player._arena,
							p => p.StatsCurrentSession,
							b => b.GetChartStats());
						player._client.sendReliable(chart, 1);
					}
					break;

				case Helpers.ChartType.ScoreLifetime:
					{	//Send the request to the database!
						CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

						req.player = player.toInstance();
						req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreLifetime;
						req.options = pkt.options;

                        player._arena._server._db.send(req);
					}
					break;

                case Helpers.ChartType.ScoreDaily:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreDaily;
                        req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

                case Helpers.ChartType.ScoreWeekly:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreWeekly;
                        req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

                case Helpers.ChartType.ScoreMonthly:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreMonthly;
                        req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

                case Helpers.ChartType.ScoreYearly:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreYearly;
                        req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

                case Helpers.ChartType.ScoreHistoryDaily:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreHistoryDaily;

                        if (pkt.options == "")
                            req.options = player._alias.ToString();
                        else
                            req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

                case Helpers.ChartType.ScoreHistoryWeekly:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreHistoryWeekly;

                        if (pkt.options == "")
                            req.options = player._alias.ToString();
                        else
                            req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

                case Helpers.ChartType.ScoreHistoryMonthly:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreHistoryMonthly;

                        if (pkt.options == "")
                            req.options = player._alias.ToString();
                        else
                            req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

                case Helpers.ChartType.ScoreHistoryYearly:
                    {	//Send the request to the database!
                        CS_PlayerStatsRequest<Data.Database> req = new CS_PlayerStatsRequest<Data.Database>();

                        req.player = player.toInstance();
                        req.type = CS_PlayerStatsRequest<Data.Database>.ChartType.ScoreHistoryYearly;

                        if (pkt.options == "")
                            req.options = player._alias.ToString();
                        else
                            req.options = pkt.options;

                        player._arena._server._db.send(req);
                    }
                    break;

				default:
					{
						chart.type = Helpers.ChartType.ScoreCurrentGame;
						chart.columns = "Invalid Chart Request";
						chart.rows = new List<SC_ScoreChart.ChartRow>();
						player._client.sendReliable(chart, 1);
					}
					break;
			}
		}

		/// <summary>
		/// Merge non-stealth players + named bots into one score chart, sorted by points then kills.
		/// </summary>
		static List<SC_ScoreChart.ChartRow> BuildArenaChartRows(
			Arena arena,
			Func<Player, Data.PlayerStats> playerStats,
			Func<Bot, Data.PlayerStats> botStats)
		{
			List<SC_ScoreChart.ChartRow> rows = new List<SC_ScoreChart.ChartRow>();
			if (arena == null)
				return rows;

			foreach (Player p in arena.Players)
			{
				if (p == null || p.IsStealth)
					continue;
				Data.PlayerStats stats = playerStats(p);
				if (stats == null)
					continue;
				rows.Add(new SC_ScoreChart.ChartRow(p._alias, p._squad, stats));
			}

			foreach (Bot bot in arena.GetScoreChartBots())
			{
				if (bot == null)
					continue;
				Data.PlayerStats stats = botStats(bot);
				if (stats == null)
					continue;
				rows.Add(new SC_ScoreChart.ChartRow(bot._alias, "", stats));
			}

			rows.Sort((a, b) =>
			{
				long pa = a.stats != null ? a.stats.Points : 0;
				long pb = b.stats != null ? b.stats.Points : 0;
				int cmp = pb.CompareTo(pa);
				if (cmp != 0)
					return cmp;
				int ka = a.stats != null ? a.stats.kills : 0;
				int kb = b.stats != null ? b.stats.kills : 0;
				return kb.CompareTo(ka);
			});

			return rows;
		}

		/// <summary>
		/// Registers all handlers
		/// </summary>
		[Logic.RegistryFunc]
		static public void Register()
		{
			CS_ChartRequest.Handlers += Handle_CS_ChartRequest;
		}
	}
}
