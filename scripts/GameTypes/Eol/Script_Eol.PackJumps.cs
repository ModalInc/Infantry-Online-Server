using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "anyone on packs ... can fly when necessary, i.e when running away or if they need to jump
    /// over physics") pack bots (Flight / Jump / Drop / Bomb / Strafe Pack) jump - a rise-and-fall arc the engine flies
    /// (Bot.StartJump; while high enough they pass over lower physics):
    ///   - over a wall in their way that a jump can clear (physics up to 124 high - not the tall red walls);
    ///   - when running away hurt (under 35 % health with an enemy close).
    /// Only when needed: up against a wall it can clear, or now and then when fleeing hurt (1 in 4, 9 s apart); at
    /// most one jump every 4 s (their hydrogen); just high enough to clear the wall - never non-stop, never to the roof.
    /// </summary>
    public partial class Script_Eol
    {
        static readonly HashSet<int> PackVehIds = new HashSet<int> { 106, 107, 109, 114, 116 };
        const short PackJumpPeakZ = 135;      // just over the highest physics a jump clears - not to the roof
        const int PackJumpMs = 1300;
        const int PackJumpCooldownMs = 4000;     // (their hydrogen) at most one jump every 4 s
        const int PackJumpClearHigh = 124;     // physics this high or lower can be jumped
        const double PackFleeHealth = 0.35;
        const int PackFleeCooldownMs = 9000;     // a jump away when hurt: rarer, and only sometimes
        const double PackFleeChance = 0.25;
        const int PackFleeEnemyRange = 450;
        int _tickPackJumps;
        readonly Dictionary<Bot, int> _packJumpNext = new Dictionary<Bot, int>();

        static bool IsPackBot(Bot b)
        {
            return b != null && b._type != null && PackVehIds.Contains(b._type.Id);
        }

        void PollPackJumps(int now)
        {
            if (_arena == null || now - _tickPackJumps < 150)
                return;
            _tickPackJumps = now;
            LvlInfo lvl = _arena._server._assets.Level;
            foreach (Bot b in _arena.ActiveBots)
            {
                if (!IsPackBot(b) || b.IsDead || b.bCondemned || b.IsJumping || b._movement == null || IsDeploying(b))
                    continue;
                int due;
                if (_packJumpNext.TryGetValue(b, out due) && now - due < 0)
                    continue;
                bool jump = false;
                // a wall in the way that a jump clears
                double vx = b._state.velocityX, vy = b._state.velocityY;
                double len = Math.Sqrt(vx * vx + vy * vy);
                if (len < 1)
                {   // (stopped against it: look the way it faces)
                    double yaw = b._state.yaw * 1.5 * Math.PI / 180.0;
                    vx = Math.Sin(yaw); vy = -Math.Cos(yaw); len = 1;
                }
                for (int d = 16; d <= 40 && !jump; d += 12)
                {
                    int tx = (int)(b._state.positionX + vx / len * d) / 16, ty = (int)(b._state.positionY + vy / len * d) / 16;
                    if (tx < 0 || ty < 0 || tx >= lvl.Width || ty >= lvl.Height)
                        break;
                    LvlInfo.Tile t = lvl.Tiles[ty * lvl.Width + tx];
                    if (!t.Blocked)
                        continue;
                    if (BotCombat.IsWallFor(lvl, t, b._type))
                    {
                        // (only when it is actually up against it - not every time a wall is ahead)
                        if (lvl.PhysicsHigh[t.Physics] <= PackJumpClearHigh && b._movement.bCollision && d <= 28)
                            jump = true;
                        break;   // (a tall wall: no jump)
                    }
                }
                // running away hurt - now and then, not every time
                bool flee = false;
                if (!jump && _arena._rand.NextDouble() < PackFleeChance && b._type.Hitpoints > 0 && b._state.health < Math.Max(b._type.Hitpoints, HumanBotHealth) * PackFleeHealth)
                    foreach (InfServer.Game.Player p in _arena.PlayersIngame)
                        if (p != null && !p.IsDead && p._team != b._team && p._state != null
                            && Helpers.distanceTo(p._state, b._state) < PackFleeEnemyRange)
                        { jump = flee = true; break; }
                if (!jump)
                    continue;
                if (b.StartJump(PackJumpPeakZ, PackJumpMs))
                    _packJumpNext[b] = now + (flee ? PackFleeCooldownMs : PackJumpCooldownMs);
            }
        }
    }
}
