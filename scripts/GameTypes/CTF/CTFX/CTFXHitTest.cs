// CTF:X server-side bots - DAMAGE SELF-TEST (Travis 10-16: "bots tank way more than I deal"). Drop logs\ctfxhittest.txt (item ids on one line, e.g.
// "3001 3023 3016") and the server runs it once, ~45 s after the bots are up: for one living bot of each class it applies direct hits of each
// weapon at 100 / 60 / 30 % energy and writes the HP a hit does and the hits a kill would take to ctfxbots.log ([CTFXHitTest]). The bot's
// health, energy and carry are put back afterwards. Nothing here runs unless the file exists.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        /// <summary>HP per direct hit of weapon <paramref name="wepId"/> at <paramref name="energyPct"/> of max energy (NaN = not a projectile).</summary>
        internal double HitTestHp(int wepId, double energyPct, int n)
        {
            ItemInfo it = AssetManager.Manager.getItemByID(wepId);
            ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
            if (mu != null && mu.childItems != null && mu.childItems.Count > 0) { ItemInfo c = AssetManager.Manager.getItemByID(mu.childItems[0].id); if (c != null) it = c; }
            ItemInfo.Projectile wep = it as ItemInfo.Projectile;
            if (wep == null || IsDead) return double.NaN;
            short h0 = _state.health; double e0 = _energy, rem0 = _hpRem;
            _state.health = 30000; _hpRem = 0;
            for (int i = 0; i < n; i++) { _energy = _energyMax * energyPct; TakeBlast(0.0, wep, null, true); }
            double lost = 30000 - _state.health + _hpRem;
            _state.health = h0; _energy = e0; _hpRem = rem0;
            return lost / n;
        }

        internal static bool TestOldArmour;     // self-test only: count the baked body armour again, as before the 10-16 fix

        internal string HitTestLine(int[] weps)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Slot != null ? Slot.Name : "?").Append(' ').Append(ClassName(_class)).Append(" body ").Append(_type.Id).Append(" hp ").Append(_type.Hitpoints)
                .Append(" (").Append(_armorU != null ? _armorU.name : "no armour").Append(", ").Append(_genU != null ? _genU.name : "-").Append(")");
            foreach (int w in weps)
                foreach (double p in new double[] { 1.0, 0.6, 0.3 })
                {
                    double hp = HitTestHp(w, p, 40);
                    sb.Append(" | ").Append(ItemName(w)).Append(' ').Append((int)(p * 100)).Append("%: ");
                    if (double.IsNaN(hp)) sb.Append("n/a");
                    else sb.Append(hp.ToString("0.00")).Append(" HP/hit, ").Append(hp > 0 ? Math.Ceiling(_type.Hitpoints / hp).ToString("0") : "never").Append(" to kill");
                }
            return sb.ToString();
        }
    }

    internal sealed partial class CTFXBotManager
    {
        int _hitTestAt = -1;

        void HitTestStep(int now)
        {
            string path = System.IO.Path.Combine("logs", "ctfxhittest.txt");   // Linux zones too
            if (_hitTestAt == -1) { if (!File.Exists(path)) return; _hitTestAt = now + 45000; return; }
            if (_hitTestAt == 0 || now - _hitTestAt < 0) return;
            _hitTestAt = 0;
            try
            {
                List<int> weps = new List<int>();
                foreach (string t in File.ReadAllText(path).Split(new char[] { ' ', ',', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)) { int id; if (int.TryParse(t, out id)) weps.Add(id); }
                File.Delete(path);
                HashSet<string> seen = new HashSet<string>();
                int done = 0;
                foreach (CTFXBot b in _bots.Values)
                {
                    if (b.IsDead || b.Slot == null || b.OnPack) continue;
                    string cls = CTFXBot.ClassName(b.ClassId);
                    if (!seen.Add(cls)) continue;
                    Log("[CTFXHitTest] FIXED  " + b.HitTestLine(weps.ToArray()));
                    CTFXBot.TestOldArmour = true;
                    try { Log("[CTFXHitTest] BEFORE " + b.HitTestLine(weps.ToArray())); } finally { CTFXBot.TestOldArmour = false; }
                    done++;
                }
                Log("[CTFXHitTest] done: " + done + " bots, " + weps.Count + " weapons (hits are direct, energy held at the stated share)");
            }
            catch (Exception ex) { Log("[CTFXHitTest] failed: " + ex.Message); }
        }
    }
}
/* PARSER GUARD - keep last */
