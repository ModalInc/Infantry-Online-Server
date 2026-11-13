// CTF:X server-side bots - STAGGERED CLIPS (Travis 10-15): "some high damaging weapons, such as the AR weapons, have sometimes '1-2 or 2-1
// or 1-1-1' stuttered shots, where most of the time they do the efficient triple ... duelers who are fighting a very evasive target that
// is LOW on energy chip shots from a high powered weapon like an AR (but not an smg or pistol) ... stub cannon for heavies also ...
// shots are slightly staggered at different timings to mix up timings of shot and aim and dodge against skilled duel opponents ... a
// feature of elite bots SOME OF THE TIME, not all of the time ... not for a lot of weapons".
//  - Weapons: the assault rifles (a name with " AR", "AR249" or "AKS") and the Stub Cannon. Nothing else.
//  - Who: veterans (20 % of the eligible clips) and aces (35 %); rookies and regulars always fire the plain triple.
//  - When: a duel (no other enemy within 600 px of it), the target low on energy (under 40 %: the shield is down, so chip hits land)
//    and moving sideways to its line of fire at 80+ px/s (dodging). Otherwise the efficient full-rate clip, as before.
//  - How: at the clip's first shot one pattern is picked - 1-2, 2-1 or 1-1-1 for a 3-round clip (one or two breaks anywhere in a longer
//    one) - and each break holds the next shot 110-260 ms longer. Only that clip; the next is plain again unless rolled anew.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        static HashSet<int> _staggerIds;
        int[] _stagGap;                 // extra ms after shot k of the current clip (0 = none)
        int _stagShot;                  // shots fired so far in that clip

        static bool StaggerWeapon(int id)
        {
            if (_staggerIds == null)
            {
                HashSet<int> s = new HashSet<int>();
                try
                {
                    // the real item list: getItemByID over 1..4999 logs "Unable to get item by ID" per gap on the live engine (4,000+ lines)
                    List<ItemInfo> all = AssetManager.Manager.getItems ?? new List<ItemInfo>();
                    foreach (ItemInfo it in all)
                    {
                        if (it == null || it.name == null) continue;
                        string n = it.name.Trim('"', ' ');
                        if (n.Contains(" AR") || n.Contains("AR249") || n.Contains("AKS") || n.StartsWith("Stub", StringComparison.OrdinalIgnoreCase)) s.Add(it.id);
                    }
                }
                catch (Exception) { }
                _staggerIds = s;
            }
            return _staggerIds.Contains(id);
        }

        /// <summary>From ShotFired, after the clip count went down: roll a pattern at a clip's first shot, then hold the next shot when the
        /// pattern breaks after this one.</summary>
        void StaggerShot(int now, int i, bool firstOfClip)
        {
            CTFXFireProfile p = _prof[i];
            if (firstOfClip)
            {
                _stagGap = null; _stagShot = 0;
                if (p.Clip >= 3 && StaggerWeapon(p.Id) && StaggerNow()) _stagGap = StaggerPattern(p.Clip);
            }
            _stagShot++;
            if (_stagGap == null || _clipLeft[i] <= 0 || _stagShot - 1 >= _stagGap.Length) return;
            int g = _stagGap[_stagShot - 1];
            if (g > 0) { _readyAt[i] += g; Mgr.StaggerBreaks++; }
        }

        bool StaggerNow()
        {
            if (Human == null) return false;
            double share = Human.Tier == "ace" ? 0.35 : Human.Tier == "veteran" ? 0.20 : 0.0;
            if (share <= 0 || _rng.NextDouble() >= share) return false;
            double ef = TargetEnergyFrac();
            if (ef < 0 || ef >= 0.40) return false;                                           // its shield must be down
            Helpers.ObjectState ts = _tgtB != null ? _tgtB._state : _tgtP != null ? _tgtP._state : null;
            if (ts == null) return false;
            double lx = ts.positionX - _state.positionX, ly = ts.positionY - _state.positionY, ld = Math.Sqrt(lx * lx + ly * ly);
            if (ld < 1) return false;
            double side = Math.Abs(ts.velocityX * ly - ts.velocityY * lx) / ld;                // sideways to the line of fire (1000 = 100 px/s)
            if (side < 800) return false;
            Team tt = _tgtB != null ? _tgtB._team : _tgtP._team;
            foreach (CTFXBot o in Mgr.LiveBots)
            {   // a duel: no other enemy within 600 px
                if (o == _tgtB || o.IsDead || o.InDropship || o._team == _team) continue;
                double dx = o._state.positionX - _state.positionX, dy = o._state.positionY - _state.positionY;
                if (dx * dx + dy * dy < 600.0 * 600.0) return false;
            }
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p == _tgtP || p.IsDead || p._team == _team || p._state == null) continue;
                double dx = p._state.positionX - _state.positionX, dy = p._state.positionY - _state.positionY;
                if (dx * dx + dy * dy < 600.0 * 600.0) return false;
            }
            Mgr.StaggerClips++;
            return true;
        }

        int StagGap() { return 110 + _rng.Next(151); }

        int[] StaggerPattern(int clip)
        {
            int[] g = new int[clip];
            if (clip == 3)
            {
                int k = _rng.Next(3);                                                           // 0: 1-2, 1: 2-1, 2: 1-1-1
                if (k == 0 || k == 2) g[0] = StagGap();
                if (k == 1 || k == 2) g[1] = StagGap();
            }
            else
            {
                int breaks = 1 + _rng.Next(2);
                for (int b = 0; b < breaks; b++) g[_rng.Next(clip - 1)] = StagGap();
            }
            return g;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int StaggerClips, StaggerBreaks;
        internal int HumanHitLog;                  // human hits logged in full ([CTFXHit], first 400 per run)
        internal int HumanHits, HumanHitsLagged;   // human bursts scored on a bot / of them landing only thanks to the 400 ms window (CTFXBot.TakeHit)
    }
}
