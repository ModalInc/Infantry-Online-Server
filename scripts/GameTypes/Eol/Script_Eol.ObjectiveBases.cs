using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-15, the zone owner: "the second team base did not go next to a fixed objective") Bot bases stand at the
    /// map's fixed objectives - the "Major Resource Point" flags in the .lio. One HQ slot per Major Resource Point in an
    /// open sector (away from the spawn zones): the HQ goes on open ground right next to the flag and gets a turret ring
    /// spread all round it (PlaceObjectiveBaseTurrets). A destroyed base comes back at another point (PollEngineerSpawning
    /// prefers a slot away from the one just destroyed), so the bases rotate over the objectives.
    /// No Major Resource Point in the open sectors: the old hand-made base spots as before.
    /// </summary>
    public partial class Script_Eol
    {
        const int ObjectiveHqDistanceRaw = 128;   // HQ this far from the flag (right next to it, not on it)
        const int ObjectiveHqClearRaw = 48;       // open ground this far around the HQ
        const string ObjectiveFlagName = "Major Resource Point";

        /// <summary>HQ slots at the Major Resource Points of the open sectors (empty: none found).</summary>
        List<pylonObject> BuildObjectiveHqSlots()
        {
            List<pylonObject> slots = new List<pylonObject>();
            if (_arena == null || _arena._flags == null || _eol == null)
                return slots;
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs == null || fs.flag == null)
                    continue;
                string name = fs.flag.GeneralData.Name ?? "";
                if (name.IndexOf(ObjectiveFlagName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                short fx = fs.flag.GeneralData.OffsetX, fy = fs.flag.GeneralData.OffsetY;
                if (!_eol.IsInAnyActiveSector(fx, fy) || NearNewPlayerBuffer(fx, fy))
                    continue;

                // right next to the flag (owner: the old hand-made spots were in the same building, not at the flag)
                Helpers.ObjectState at = FindHqSpotNextTo(fx, fy);
                // (2026-11-17, owner: "no bots can spawn in the building 361,348") a flag inside a no-spawn building
                // (Major Resource Point Sector A sits inside it): the base goes up on open ground just outside instead
                if (at != null && InNoBotSpawnBuilding(at.positionX, at.positionY))
                {
                    Helpers.ObjectState outside = HqSpotOutsideNoSpawn(fx, fy);
                    Log.write(TLog.Normal, "[Eol] Objective base: {0} is inside a no-spawn building - HQ slot {1}.", name,
                        outside != null ? (outside.positionX / 16) + "," + (outside.positionY / 16) + " (outside)" : "none");
                    at = outside;
                }
                if (at == null)
                {
                    Log.write(TLog.Warning, "[Eol] Objective base: no open ground next to {0} ({1},{2}).", name, fx, fy);
                    continue;
                }
                // (two points close together must not give two slots on the same spot)
                bool dup = false;
                foreach (pylonObject s in slots)
                {
                    long dx = s.getX() - at.positionX, dy = s.getY() - at.positionY;
                    if (dx * dx + dy * dy < (long)BotHqAnchorNearRaw * BotHqAnchorNearRaw * 4) { dup = true; break; }
                }
                if (!dup)
                    slots.Add(new pylonObject(at.positionX, at.positionY));
            }
            return slots;
        }

        /// <summary>Open ground ObjectiveHqDistanceRaw from the flag (8 directions, then a bit further out).</summary>
        Helpers.ObjectState FindHqSpotNextTo(short fx, short fy)
        {
            int[] dists = { ObjectiveHqDistanceRaw, ObjectiveHqDistanceRaw + 48, ObjectiveHqDistanceRaw + 112, ObjectiveHqDistanceRaw + 192 };
            foreach (int d in dists)
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    int x = fx + (int)Math.Round(Math.Cos(a) * d), y = fy + (int)Math.Round(Math.Sin(a) * d);
                    if (!AreaOpen(x, y, ObjectiveHqClearRaw) || NearNewPlayerBuffer((short)x, (short)y)
                        || !_eol.IsInAnyActiveSector((short)x, (short)y))
                        continue;
                    return new Helpers.ObjectState { positionX = (short)x, positionY = (short)y };
                }
            return null;
        }

        /// <summary>No blocked tile within r (raw) of (x,y), and inside the map.</summary>
        bool AreaOpen(int x, int y, int r)
        {
            int maxX = _arena._levelWidth * 16 - 17, maxY = _arena._levelHeight * 16 - 17;
            if (x - r < 16 || y - r < 16 || x + r > maxX || y + r > maxY)
                return false;
            for (int dx = -r; dx <= r; dx += 16)
                for (int dy = -r; dy <= r; dy += 16)
                    if (_arena.getTile((short)(x + dx), (short)(y + dy)).Blocked)
                        return false;
            return true;
        }

        /// <summary>
        /// Turret ring for an objective base: the six pieces of the hand-made layouts (Rocket, 2 MG, Plasma, Maser,
        /// Sentry) spread evenly all round the HQ (60 degrees apart, the gun types alternating so every side has a
        /// mix), each on the most open ground near its place in the ring - open ground = long sight lines, so together
        /// they cover every angle of approach. A piece with no open ground near its place is left out.
        /// </summary>
        void PlaceObjectiveBaseTurrets(Team team, Helpers.ObjectState hq)
        {
            int[] pieces = { 467, 457, 823, 460, 457, 466 };   // Rocket, MG, Maser, Plasma, MG, Sentry
            double start = new Random(hq.positionX * 31 + hq.positionY).NextDouble() * Math.PI / 3;
            List<KeyValuePair<int, int>> taken = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < pieces.Length; i++)
            {
                double baseA = start + i * Math.PI / 3;
                int bestX = 0, bestY = 0, bestScore = -1;
                foreach (int r in new[] { 192, 160, 224, 128 })
                    foreach (double jitter in new[] { 0.0, 0.2, -0.2, 0.4, -0.4 })
                    {
                        int x = hq.positionX + (int)Math.Round(Math.Cos(baseA + jitter) * r);
                        int y = hq.positionY + (int)Math.Round(Math.Sin(baseA + jitter) * r);
                        if (!AreaOpen(x, y, 24) || NearNewPlayerBuffer((short)x, (short)y))
                            continue;
                        bool close = false;
                        foreach (KeyValuePair<int, int> t in taken)
                            if ((long)(t.Key - x) * (t.Key - x) + (long)(t.Value - y) * (t.Value - y) < 96L * 96) { close = true; break; }
                        if (close)
                            continue;
                        int score = OpenScore(x, y);
                        if (score > bestScore) { bestScore = score; bestX = x; bestY = y; }
                    }
                if (bestScore < 0)
                    continue;
                taken.Add(new KeyValuePair<int, int>(bestX, bestY));
                createVehicleAtObjectState(pieces[i], bestX - hq.positionX, bestY - hq.positionY, team, hq);
            }
        }

        /// <summary>How open the ground is around a spot: open tiles along 8 sight lines out to ~20 tiles.</summary>
        int OpenScore(int x, int y)
        {
            int maxX = _arena._levelWidth * 16 - 17, maxY = _arena._levelHeight * 16 - 17, score = 0;
            for (int d = 0; d < 8; d++)
            {
                double a = d * Math.PI / 4;
                for (int step = 1; step <= 20; step++)
                {
                    int px = x + (int)Math.Round(Math.Cos(a) * step * 16), py = y + (int)Math.Round(Math.Sin(a) * step * 16);
                    if (px < 16 || py < 16 || px > maxX || py > maxY || _arena.getTile((short)px, (short)py).Blocked)
                        break;
                    score++;
                }
            }
            return score;
        }
    }
}
