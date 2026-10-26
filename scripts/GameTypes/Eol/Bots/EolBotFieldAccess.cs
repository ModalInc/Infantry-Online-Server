using InfServer.Bots;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Bot kill/death counters for score charts. ZoneServer <see cref="Bot"/> exposes
    /// <c>_kills</c>/<c>_deaths</c> and <see cref="Bot.RecordKill"/> / <see cref="Bot.RecordDeath"/>.
    /// </summary>
    static class EolBotFieldAccess
    {
        public static void IncrementKills(Bot bot, int killPoints = 0)
        {
            if (bot == null)
                return;

            if (killPoints <= 0 && bot._arena?._server?._zoneConfig?.bot != null)
                killPoints = (int)bot._arena._server._zoneConfig.bot.pointsKillReward;

            bot.RecordKill(killPoints);
        }

        public static void IncrementDeaths(Bot bot)
        {
            bot?.RecordDeath();
        }
    }
}
