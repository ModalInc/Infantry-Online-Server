using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "make it so that bots heal each other too. so the bounty will be more interesting") every human
    /// and pirate bot carries 2 medikits a life: a teammate bot within 72 px under 50 % health gets patched up for 40 % of
    /// its max (8 s between uses per bot). Squad medics heal as before, now on every human / pirate bot team.
    /// </summary>
    public partial class Script_Eol
    {
        const int BotMedikitsPerLife = 2, BotMedikitRange = 72, BotMedikitCooldownMs = 8000;
        const double BotMedikitBelow = 0.5, BotMedikitHealShare = 0.4;
        readonly Dictionary<Bots.Bot, int> _botMedikitsUsed = new Dictionary<Bots.Bot, int>();
        readonly Dictionary<Bots.Bot, int> _botMedikitNext = new Dictionary<Bots.Bot, int>();
        int _tickBotMedikits;

        bool CarriesMedikits(Bots.Bot b)
        {
            return b != null && !(b is AlienHorde) && !IsAlienBotTeam(b._team) && (b.PlayerEnergy || IsRoamingSquadTeam(b._team) || IsRoamBotTeam(b._team));
        }

        void PollBotMedikits(int now)
        {
            if (_arena == null || now - _tickBotMedikits < 500)
                return;
            _tickBotMedikits = now;
            List<Bots.Bot> bots = new List<Bots.Bot>();
            foreach (Bots.Bot b in _arena.ActiveBots)
                if (b != null && !b.IsDead && !b.bCondemned && b._state != null && CarriesMedikits(b))
                    bots.Add(b);
            foreach (Bots.Bot healer in bots)
            {
                int used, next;
                _botMedikitsUsed.TryGetValue(healer, out used);
                if (used >= BotMedikitsPerLife || (_botMedikitNext.TryGetValue(healer, out next) && now - next < 0) || IsDeploying(healer))
                    continue;
                Bots.Bot patient = null;
                double worst = BotMedikitBelow;
                foreach (Bots.Bot b in bots)
                {
                    if (b == healer || b._team != healer._team || b.IsDead)
                        continue;
                    int max = MaxHealthOf(b);
                    double share = (double)b._state.health / Math.Max(1, max);
                    if (share >= worst || Helpers.distanceTo(b._state, healer._state) > BotMedikitRange)
                        continue;
                    worst = share; patient = b;
                }
                if (patient == null)
                    continue;
                int pmax = MaxHealthOf(patient);
                patient._state.health = (short)Math.Min(pmax, patient._state.health + Math.Max(10, (int)Math.Round(pmax * BotMedikitHealShare)));
                _botMedikitsUsed[healer] = used + 1;
                _botMedikitNext[healer] = now + BotMedikitCooldownMs;
                if (_arena._rand.Next(3) == 0)
                    SquadSay(healer, Pick3("patching you up", "medikit, hold still", "got you covered"));
            }
            if (_botMedikitsUsed.Count > 200)
            {
                List<Bots.Bot> gone = new List<Bots.Bot>();
                foreach (Bots.Bot b in _botMedikitsUsed.Keys) if (b == null || b.bCondemned || b.IsDead) gone.Add(b);
                foreach (Bots.Bot b in gone) { _botMedikitsUsed.Remove(b); _botMedikitNext.Remove(b); }
            }
        }

        /// <summary>A bot died: it respawns with fresh medikits.</summary>
        void ResetBotMedikits(Bots.Bot b)
        {
            if (b == null) return;
            _botMedikitsUsed.Remove(b);
            _botMedikitNext.Remove(b);
        }
    }
}
