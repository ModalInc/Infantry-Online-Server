using System;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-27, owner: "when bot kills a real player have a funny kill macro that eolers used to do.. Playername is the
    /// player the bot killed") the bot says one of these in public chat, under its own name, when it kills a real player.
    /// At most one taunt every BotTauntGapMs arena-wide so a big fight doesn't flood the chat.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool BotKillTauntChatEnabled = true;
        const int BotTauntGapMs = 3000;
        int _tickLastBotTaunt;

        static readonly string[] BotKillTaunts =
        {
            "Eased. {0}, get good.",
            "2ez {0}, rofl.",
            "L0L get absolutely pwnd {0}",
            "Everyone saw that, you got smoked, {0}.",
            "Was it as good for you as it was for me, {0}?",
            "GG ez, {0}.",
        };

        void BotKillTauntChat(Bots.Bot bot, InfServer.Game.Player victim)
        {
            if (!BotKillTauntChatEnabled || _arena == null || bot == null || victim == null || string.IsNullOrEmpty(victim._alias))
                return;
            int now = Environment.TickCount;
            if (_tickLastBotTaunt != 0 && now - _tickLastBotTaunt < BotTauntGapMs)
                return;
            _tickLastBotTaunt = now;
            string from = string.IsNullOrEmpty(bot._alias) ? "Bot" : bot._alias;
            string line = string.Format(BotKillTaunts[_arena._rand.Next(BotKillTaunts.Length)], victim._alias);
            Helpers.Player_RouteChatRaw(_arena, from, line, 0, Helpers.Chat_Type.Normal);
        }
    }
}
