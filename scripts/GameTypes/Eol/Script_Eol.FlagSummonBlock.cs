using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-18, owner: "if you hold the citizen / elite / merc flags you are not able to be summoned. as they are
    /// valuable flags it will result in over powered tactics to keep them") while a player carries the Citizens, Elite
    /// Unit or Mercenaries Standard:
    ///   - summon items can't move them: "*" (ignore everyone's summons - what every engine summon / warp mode checks) is
    ///     put on their summon-ignore list, and taken off again when they no longer carry one (a "*" the player set
    ///     himself is left alone);
    ///   - the squad's S / X summon (teleport to a squad bot) is refused.
    /// </summary>
    public partial class Script_Eol
    {
        static readonly HashSet<string> NoSummonFlagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Citizens Standard", "Elite Unit Standard", "Mercenaries Standard" };

        readonly HashSet<InfServer.Game.Player> _noSummonAdded = new HashSet<InfServer.Game.Player>();
        int _tickNoSummonFlags;

        /// <summary>Carrying the Citizens / Elite Unit / Mercenaries Standard right now?</summary>
        public bool CarriesNoSummonFlag(InfServer.Game.Player p)
        {
            if (p == null || _arena == null || _arena._flags == null)
                return false;
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs == null || fs.carrier != p || fs.flag == null)
                    continue;
                string name = (fs.flag.GeneralData.Name ?? "").Trim().Trim('"').Trim();
                if (NoSummonFlagNames.Contains(name))
                    return true;
            }
            return false;
        }

        void PollNoSummonFlags(int now)
        {
            if (_arena == null || now - _tickNoSummonFlags < 400)
                return;
            _tickNoSummonFlags = now;
            HashSet<InfServer.Game.Player> present = new HashSet<InfServer.Game.Player>();
            foreach (InfServer.Game.Player p in _arena.Players)
            {
                if (p == null || p._summonIgnore == null)
                    continue;
                present.Add(p);
                bool carrying = !p.IsSpectator && CarriesNoSummonFlag(p);
                if (carrying && !_noSummonAdded.Contains(p) && !p._summonIgnore.Contains("*"))
                {
                    p._summonIgnore.Add("*");
                    _noSummonAdded.Add(p);
                    p.sendMessage(0, "&You carry a standard - nobody can summon you while you hold it.");
                }
                else if (!carrying && _noSummonAdded.Contains(p))
                {
                    p._summonIgnore.Remove("*");
                    _noSummonAdded.Remove(p);
                }
            }
            _noSummonAdded.IntersectWith(present);   // (players who left)
        }
    }
}
