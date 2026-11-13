using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// MiniTP ENGINEER MINING TRIP (Travis 11-11: "the whole mining subroutine for engineers: dropship recall, build a hoverboard, fly to an area of minerals, collect minerals,
    /// then get a summon from the medic at home. They should physically drop the Tsolvy crystals on the ground, then a delayed reaction for the medic to pick them up - aces and
    /// veterans faster, worse players slower - no more than the carrying capacity of 150, and the engineer re-picks the excess if the medic had topped off").
    ///   Dig 1 RECALL   - DropShip Recall (item 46, as a player: no anti-warp on it), waits a moment in the ship (the store refills its Light PowerCells).
    ///   Dig 2 OUT      - out of the ship by a field exit near the mineral spot.
    ///   Dig 3 BUILD    - the Hoverboard (VehicleMaker item 1 -> vehicle 105): its skill logic, its required item (Light PowerCells x2) taken, standing still for its prefire,
    ///                    its energy paid, then a short wait before it gets on (no cells / no skill: it walks).
    ///   Dig 4 RIDE     - to the mineral spot (toxS1 / toxS2), off the board there.
    ///   Dig 5 COLLECT  - the field's mineral hides fire for it the way they fire for a player standing in their range (the hide's own attempt delay, player-count window,
    ///                    MaxTypeInlevel / MaxTypeInArea, clumps, quantity); it walks onto the drops: Titanium Oxide up to 100, Tsolvy up to 150 (EngTso).
    ///   Dig 6 SUMMON   - asks the home medic ("S me home"); the medic's summon with its own rules (cooldown, energy, anti-warp both ends); no medic for 40 s: Recall + ship hop.
    ///   Dig 7 DELIVER  - at home it walks to the medic and DROPS the Tsolvy on the ground (a visible pile), says so.
    ///   Dig 8 LINGER   - 12 s later it takes back whatever the medic left (a medic tops off at 150).
    /// The medic's pickup has a reaction delay by tier (MedPileReady).
    /// </summary>
    internal sealed partial class CTFXMini
    {
        internal int DigDeaths, DigTrips, DigBoards, DigWalks, DigSpawns, DigTox, DigTso, DigSummoned, DigRecalled, DigDelivered, DigRepicked;
        const int HoverMaker = 1, HoverVeh = 105, MineralTox = 2009, MineralTso = 2005;

        sealed class MineralHide { public Arena.HideState Hs; public int X, Y, Item, Next; }
        List<MineralHide> _minHides;
        readonly Dictionary<CTFXSlot, int> _digNextOf = new Dictionary<CTFXSlot, int>();     // the next trip per seat (a new body must not start one at once)

        void LoadMineralHides()
        {
            _minHides = new List<MineralHide>();
            int lx = _m.Arena._server._assets.Level.OffsetX * 16, ly = _m.Arena._server._assets.Level.OffsetY * 16;
            foreach (Arena.HideState hs in _m.Arena._hides)
            {
                int id = hs.Hide.HideData.HideId;
                if (id != MineralTox && id != MineralTso) continue;
                if (hs.Hide.GeneralData.RelativeId != 0) continue;
                int cx = hs.Hide.GeneralData.OffsetX - lx, cy = hs.Hide.GeneralData.OffsetY - ly;
                if (AreaOf(cx >> 4, cy >> 4) != 2) continue;                       // the MiniTP field only
                MineralHide mh = new MineralHide(); mh.Hs = hs; mh.X = cx; mh.Y = cy; mh.Item = id;
                _minHides.Add(mh);
            }
            _m.MiniLog("[MiniTP] mining: " + _minHides.Count + " mineral hides (Titanium Oxide / Tsolvy) in the field");
        }

        /// <summary>The mineral spot a trip goes to: toxS1 / toxS2, the one with hides nearer the bot's side's field exits.</summary>
        bool DigSpot(MiniBot mb, out int tx, out int ty)
        {   // the mineral spot near its OWN field exits and away from the ENEMY's (where their ships drop players into the field): toxS1 for Titan, toxS2 for Collective
            tx = ty = 0;
            double best = double.MaxValue;
            foreach (string sp in new string[] { "toxS1", "toxS2" })
            {
                int sx, sy;
                if (!Pt("FIELD", sp, out sx, out sy)) continue;
                double own = NearestExitTiles(mb.Side, sx, sy), foe = NearestExitTiles(1 - mb.Side, sx, sy);
                if (own >= double.MaxValue) continue;
                double score = own - 0.7 * Math.Min(foe, 200.0);
                if (score < best) { best = score; tx = sx; ty = sy; }
            }
            return best < double.MaxValue;
        }

        double NearestExitTiles(int side, int sx, int sy)
        {
            double best = double.MaxValue;
            foreach (int e in Exits[side])
            {
                int ex, ey;
                if (!Pt("FIELD", "exit" + e, out ex, out ey)) continue;
                double d = Math.Sqrt((double)(ex - sx) * (ex - sx) + (double)(ey - sy) * (ey - sy));
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>Per poll: the trigger and every step of a running trip; the medic's pickup delay.</summary>
        void DigStep(int now)
        {
            if (_minHides == null) LoadMineralHides();
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || mb.Role != "ENG") continue;
                if (b.IsDead && mb.Dig > 0) { DigDeaths++; _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " DIED on its trip at step " + mb.Dig + " (" + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + ", " + ((now - mb.DigStart) / 1000) + " s in)"); mb.Dig = 0; continue; }
                if (b.IsDead || b.Released) continue;
                if (mb.Dig == 0) { DigTrigger(b, mb, now); continue; }
                switch (mb.Dig)
                {
                    case 1:     // in the ship after the recall
                        if (!b.InDropship && AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) != 3 && AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) != 4) { DigEnd(b, mb, now, (AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) == HomeAreaIdx(mb.Side) ? "called home (the base is about to be hit) - " : "") + "not in the ship after the recall (area " + AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) + ", mark " + (b.MarkX >> 4) + "," + (b.MarkY >> 4) + ")"); break; }
                        if (now - mb.DigAt < 2500) break;
                        mb.Cells = 4;                                                   // the ship store: Light PowerCells for two boards
                        mb.Dig = 3; mb.DigAt = now; mb.BuildStart = 0;                   // Travis 11-11: the board is made and boarded IN THE DROPSHIP, safe from the fight
                        break;
                    case 3:
                        if (AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) != 3 && AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) != 4) { DigEnd(b, mb, now, "left the ship before the board was built (called home?)"); break; }
                        DigBuild(b, mb, now); break;
                    case 4:
                        {
                            int sx, sy; DigSpot(mb, out sx, out sy);
                            if (Dist2(b._state.positionX, b._state.positionY, sx * 16 + 8, sy * 16 + 8) <= 90.0 * 90.0 || now - mb.DigAt > 60000)
                            {
                                if (b.OnPack) b.Dismount(now);
                                mb.Dig = 5; mb.DigAt = now; b.ForceGoal();
                                _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " at the mineral spot " + (sx) + "," + (sy) + " after " + ((now - mb.DigStart) / 1000) + " s");
                            }
                            break;
                        }
                    case 5: DigCollect(b, mb, now); break;
                    case 6: DigSummon(b, mb, now); break;
                    case 7: DigDeliver(b, mb, now); break;
                    case 8:
                        if (now - mb.DigAt < 12000) break;
                        {   // what the medic left: taken back (the medic tops off at 150)
                            int px, py;
                            if (mb.EngTso < 150 && FindPile(b, HomeAreaIdx(mb.Side), MineralTso, out px, out py) && Dist2(px, py, mb.DropX, mb.DropY) <= 120.0 * 120.0)
                            {
                                mb.Dig = 9; mb.DigAt = now; b.ForceGoal();
                                EngSay(b, mb, now, "repick", "Medic is topped off, taking the rest back", "@ Tsolvy left over, picking it up", "| Engineer | Tsolvy left over |");
                            }
                            else DigEnd(b, mb, now, null);
                        }
                        break;
                    case 9:
                        {
                            int px, py;
                            if (!FindPile(b, HomeAreaIdx(mb.Side), MineralTso, out px, out py) || now - mb.DigAt > 15000) { DigEnd(b, mb, now, null); break; }
                            if (Dist2(b._state.positionX, b._state.positionY, px, py) <= 28.0 * 28.0) { int got = TakeDrop(b, px, py, MineralTso, 150 - mb.EngTso); mb.EngTso += got; DigRepicked += got; if (got > 0) DigEnd(b, mb, now, null); }
                        }
                        break;
                }
            }
            MedPileStep(now);
        }

        internal static bool RigMine { get { return System.IO.File.Exists(System.IO.Path.Combine("scripts", "GameTypes", "CTF", "MiniTP", "RigMineTest.txt")); } }

        void DigTrigger(CTFXBot b, MiniBot mb, int now)
        {
            int seatNext;
            if (b.Slot != null && _digNextOf.TryGetValue(b.Slot, out seatNext) && now - seatNext < 0) return;
            if (!_m.Arena._bGameRunning || mb.Phase != MiniBot.Defend || b.InDropship || now - mb.DigNext < 0 || mb.BuildIdx >= 0 || _minHides.Count == 0) return;      // (a game start wipes every bot)
            int side = mb.Side;
            bool rig = System.IO.File.Exists(System.IO.Path.Combine("scripts", "GameTypes", "CTF", "MiniTP", "RigMineTest.txt"));      // RIG ONLY: trips without waiting for low minerals / a quiet base
            bool quiet = _quietSince[side] != 0 && now - _quietSince[side] >= 15000;
            if (!quiet && !rig) return;
            CTFXBot med = null;
            foreach (CTFXBot o in _m.LiveBots) if (o.Mini != null && o.Mini.Side == side && o.Mini.Role == "MED" && !o.IsDead) { med = o; break; }
            bool toxLow = b.KitCharges >= 0 && b.KitCharges < 60, tsoLow = med != null && med.TsoUnits >= 0 && med.TsoUnits < 70;
            if (!toxLow && !tsoLow && !rig) return;
            if (!CTFXSkillLogic.Allowed(46, b.ClassId)) return;
            mb.DigNext = now + 180000;
            if (b.Slot != null) _digNextOf[b.Slot] = now + 180000;
            if (!b.Recall(now)) { mb.DigNext = now + 20000; return; }
            b.BackToShip(now, 600000);                                                            // it leaves the ship by its own step (DigExit), not the ship's gear-up timer
            mb.Dig = 1; mb.DigAt = now; mb.DigStart = now; mb.Phase = MiniBot.Ship; mb.PhaseAt = now; mb.WaitUntil = now + 600000;
            DigTrips++;
            _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " goes mining (Oxide " + b.KitCharges + ", medic Tsolvy " + (med != null ? med.TsoUnits.ToString() : "-") + ")");
            TalkNow(b, 1.0, 25, "Base quiet - going mining [{c}]", "@ Off to mine Oxide + Tsolvy", "| Engineer | Mining run |");
        }

        void DigBuild(CTFXBot b, MiniBot mb, int now)
        {
            ItemInfo.VehicleMaker vm = AssetManager.Manager.getItemByID(HoverMaker) as ItemInfo.VehicleMaker;
            bool can = vm != null && CTFXSkillLogic.Allowed(HoverMaker, b.ClassId) && (vm.requiredItemID <= 0 || mb.Cells >= Math.Max(1, vm.requiredItemAmount));
            if (!can) { DigWalks++; DigExit(b, mb, now); return; }                              // no board: out on foot
            if (b.HasTarget || !b.Still) { mb.BuildStart = 0; return; }
            if (mb.BuildStart == 0) { mb.BuildStart = now | 1; return; }
            if (mb.BoardAt == 0)
            {
                if (now - mb.BuildStart < Math.Max(0, vm.prefireDelay) * 10) return;           // the maker's prefire, standing still
                if (!b.CanPay(HoverMaker)) { if (now - mb.BuildStart > 15000) { DigWalks++; DigExit(b, mb, now); } return; }
                b.SpendEnergy(HoverMaker);
                if (vm.requiredItemID > 0) mb.Cells -= Math.Max(1, vm.requiredItemAmount);
                mb.BoardAt = now + 1000;                                                         // the board stands there a moment before it can get on
                return;
            }
            if (now - mb.BoardAt < 0) return;
            mb.BoardAt = 0;
            if (b.RideVehicle(HoverVeh, now)) { DigBoards++; _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " built a Hoverboard at " + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + " (" + mb.Cells + " cells left, energy " + (int)b.Energy + ")"); } else DigWalks++;
            DigExit(b, mb, now);
        }

        /// <summary>Out of the dropship through the field exit portal nearest the mineral spot, already at full speed toward it (Travis 11-11: "maintain their top speed
        /// velocity when entering the portal and coming out the other side and immediately fly to a mineral field, that way they bypass most action").</summary>
        void DigExit(CTFXBot b, MiniBot mb, int now)
        {
            int sx, sy, best = -1; double bd = double.MaxValue;
            if (!DigSpot(mb, out sx, out sy)) { DigEnd(b, mb, now, "no mineral spot"); return; }
            int[] ex = Exits[mb.Side];
            for (int i = 0; i < ex.Length; i++)
            {
                int px, py;
                if (!Pt("FIELD", "exit" + ex[i], out px, out py)) continue;
                double d = Dist2(px * 16, py * 16, sx * 16, sy * 16);
                if (d < bd) { bd = d; best = ex[i]; }
            }
            int lxp, lyp;
            if (best < 0 || !Pt("FIELD", "exit" + best, out lxp, out lyp)) { DigEnd(b, mb, now, "no field exit"); return; }
            mb.Dig = 4; mb.DigAt = now;
            _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " leaves the ship by exit" + best + (b.OnPack ? " on its board at full speed" : " on foot") + " for the mineral spot " + sx + "," + sy);
            Land(b, lxp, lyp, 2, now);                                                      // -> Warped: the field (Dig keeps it off the ship-hop rule)
            mb.Phase = MiniBot.Field; mb.PhaseAt = now;
            double dx = sx * 16 + 8 - b._state.positionX, dy = sy * 16 + 8 - b._state.positionY, d0 = Math.Sqrt(dx * dx + dy * dy);
            if (b.OnPack && d0 > 1.0)
            {   // the movement model takes the state's velocity over and caps it at the board's top speed: it comes out of the portal at full speed
                b._state.velocityX = (short)(dx / d0 * 3000.0); b._state.velocityY = (short)(dy / d0 * 3000.0);
                b._state.yaw = (byte)CTFXBot.YawToPublic(dx, dy);
            }
            b.ForceGoal();
        }

        void DigCollect(CTFXBot b, MiniBot mb, int now)
        {
            // the hides fire for it as for a player in range (their own rules)
            foreach (MineralHide mh in _minHides)
            {
                int r = mh.Hs.Hide.HideData.MaxPlayerDistance > 0 && mh.Hs.Hide.HideData.MaxPlayerDistance < 99999 ? mh.Hs.Hide.HideData.MaxPlayerDistance : 400;
                if (Dist2(b._state.positionX, b._state.positionY, mh.X, mh.Y) > (double)r * r || now - mh.Next < 0) continue;
                mh.Next = now + Math.Max(1000, mh.Hs.Hide.HideData.AttemptDelay * 100);
                if (SpawnMineral(mh)) DigSpawns++;
            }
            bool fullTox = b.KitCharges >= 90, fullTso = mb.EngTso >= 120;
            if ((fullTox && fullTso) || now - mb.DigAt > 50000)
            {
                mb.Dig = 6; mb.DigAt = now; mb.CarryAskAt = now;
                _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " is done: Oxide " + b.KitCharges + ", Tsolvy " + mb.EngTso + " - asking for the summon home");
                TalkNow(b, 1.0, 27, "SSSS minerals [{c}]", "S me home - got minerals", "sum home pls, minerals");
                return;
            }
            // picks up a drop it stands on
            foreach (int item in new int[] { MineralTox, MineralTso })
            {
                if (item == MineralTox && fullTox) continue;
                if (item == MineralTso && mb.EngTso >= 150) continue;
                int px, py;
                if (!FindPileNear(b, item, 28.0, out px, out py)) continue;
                int got = item == MineralTox ? TakeDrop(b, px, py, item, 100 - Math.Max(0, b.KitCharges)) : TakeDrop(b, px, py, item, 150 - mb.EngTso);
                if (item == MineralTox) { int added = b.AddTox(got); DigTox += added; if (added < got) _m.Arena.itemSpawn(AssetManager.Manager.getItemByID(item), (ushort)(got - added), (short)px, (short)py, (short)16, null); }
                else { mb.EngTso += got; DigTso += got; }
                if (got > 0) b.ForceGoal();
            }
        }

        bool SpawnMineral(MineralHide mh)
        {
            Arena.HideState hs = mh.Hs;
            int pc = _m.Arena.PlayerCount;
            if (pc > hs.Hide.HideData.MaxPlayers || pc < hs.Hide.HideData.MinPlayers) return false;
            ItemInfo it = AssetManager.Manager.getItemByID(mh.Item);
            if (it == null) return false;
            int inLevel = 0, inArea = 0, w2 = hs.Hide.GeneralData.Width / 2, h2 = hs.Hide.GeneralData.Height / 2;
            foreach (Arena.ItemDrop d in _m.Arena._items.Values)
            {
                if (d.item != it) continue;
                inLevel += d.quantity;
                if (Math.Abs(d.positionX - mh.X) <= w2 && Math.Abs(d.positionY - mh.Y) <= h2) inArea += d.quantity;
            }
            if (hs.Hide.HideData.MaxTypeInlevel != -1 && inLevel >= hs.Hide.HideData.MaxTypeInlevel) return false;
            if (hs.Hide.HideData.MaxTypeInArea != -1 && inArea >= hs.Hide.HideData.MaxTypeInArea) return false;
            int clumps = hs.Hide.HideData.ClumpQuantity == 0 ? 1 : hs.Hide.HideData.ClumpQuantity;
            bool any = false;
            for (int i = 0; i < 10 && clumps > 0; i++)
            {
                short x = (short)mh.X, y = (short)mh.Y;
                if (hs.Hide.HideData.ClumpRadius > 0) Helpers.randomPositionInArea(_m.Arena, ref x, ref y, (short)hs.Hide.HideData.ClumpRadius, (short)hs.Hide.HideData.ClumpRadius);
                else Helpers.randomPositionInArea(_m.Arena, ref x, ref y, hs.Hide.GeneralData.Width, hs.Hide.GeneralData.Height);
                if (_m.Arena.getTile(x, y).Blocked) continue;
                _m.Arena.itemSpawn(it, (ushort)Math.Max(1, hs.Hide.HideData.HideQuantity), x, y, hs.Hide.HideData.RelativeId, hs.Hide.HideData.AssignFrequency, null);
                any = true; clumps--;
            }
            return any;
        }

        void DigSummon(CTFXBot b, MiniBot mb, int now)
        {
            if (AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) == HomeAreaIdx(mb.Side)) { DigHome(b, mb, now); return; }
            if (now - mb.CarryAskAt < 0) return;
            mb.CarryAskAt = now + 1500;
            if (b.OnPack) b.Dismount(now);
            CTFXBot medic = Summoner(mb.Side, true, now);
            if (medic != null && _m.Squads.MedicSummonBot(medic, b, now))
            {
                DigSummoned++;
                _m.Chat.Say(medic, "Acknowledged --[" + _m.RowName(b.Slot) + "]--", 0.85, true, 22);
                return;                                             // -> Warped: home
            }
            if (now - mb.DigAt > 40000 && b.Recall(now))
            {   // nobody summoned it: the dropship recall, then the yellow home portal
                DigRecalled++;
                mb.Phase = MiniBot.Ship; mb.PhaseAt = now; mb.WaitUntil = now + 1500;
                mb.Dig = 7; mb.DigAt = now;
            }
        }

        void DigHome(CTFXBot b, MiniBot mb, int now)
        {
            mb.Dig = 7; mb.DigAt = now; b.ForceGoal();
        }

        void DigDeliver(CTFXBot b, MiniBot mb, int now)
        {
            if (AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) != HomeAreaIdx(mb.Side)) { if (now - mb.DigAt > 30000) DigEnd(b, mb, now, "never got home"); return; }
            if (mb.EngTso <= 0) { DigEnd(b, mb, now, null); return; }
            CTFXBot med = null;
            foreach (CTFXBot o in _m.LiveBots) if (o.Mini != null && o.Mini.Side == mb.Side && o.Mini.Role == "MED" && !o.IsDead && AreaOf(o._state.positionX >> 4, o._state.positionY >> 4) == HomeAreaIdx(mb.Side)) { med = o; break; }
            bool near = med != null ? Dist2(b._state.positionX, b._state.positionY, med._state.positionX, med._state.positionY) <= 70.0 * 70.0 : true;
            if (!near && now - mb.DigAt < 25000) return;
            ItemInfo tso = AssetManager.Manager.getItemByID(MineralTso);
            if (tso == null) { DigEnd(b, mb, now, "no Tsolvy item"); return; }
            int q = mb.EngTso;
            _m.Arena.itemSpawn(tso, (ushort)q, b._state.positionX, b._state.positionY, (short)12, null);       // a visible pile on the ground, as a player's drop
            mb.EngTso = 0; mb.DropX = b._state.positionX; mb.DropY = b._state.positionY;
            DigDelivered += q;
            mb.Dig = 8; mb.DigAt = now;
            _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " dropped " + q + " Tsolvy for the medic at " + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + " (trip " + ((now - mb.DigStart) / 1000) + " s)");
            TalkNow(b, 1.0, 25, "Dropped " + q + " Tsolvy for you [{c}]", "@ Tsolvy on the ground, medic", "| Engineer | " + q + " Tsolvy dropped |");
        }

        void DigEnd(CTFXBot b, MiniBot mb, int now, string why)
        {
            if (b.OnPack) b.Dismount(now);
            _m.MiniLog("[MiniTP] MINING: engineer " + _m.RowName(b.Slot) + " trip ended at step " + mb.Dig + ": " + (why ?? "done") + " (at " + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + ")");
            mb.Dig = 0; mb.BoardAt = 0; mb.BuildStart = 0;
            b.ForceGoal();
        }

        /// <summary>Census: each engineer's trip step and spot.</summary>
        internal string DigCensus()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (CTFXBot b in _m.LiveBots)
                if (b.Mini != null && b.Mini.Role == "ENG")
                    sb.Append(" [" + (b.Slot != null ? b.Slot.Name : "?") + (b.IsDead ? " dead" : "") + " dig " + b.Mini.Dig + " @" + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + (b.OnPack ? " board" : "") + (b.Mini.Dig >= 3 && b.Mini.Dig <= 5 ? SpotText(b.Mini) : "") + " ox " + b.KitCharges + " tso " + b.Mini.EngTso + " " + b.Why + "]");
            return sb.ToString();
        }

        string SpotText(MiniBot mb) { int sx, sy; return DigSpot(mb, out sx, out sy) ? " ->" + sx + "," + sy : ""; }

        /// <summary>The goal while mining (Dig 3+): the board build spot (where it stands), the mineral spot, the nearest mineral drop, the medic at home, the leftover pile.</summary>
        bool DigGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = b._state.positionX >> 4; ty = b._state.positionY >> 4;
            switch (mb.Dig)
            {
                case 1: case 2: case 3: case 6: case 8: return true;                          // stands (in the ship / building the board / waiting for the summon / letting the medic pick up)
                case 4: { int sx, sy; if (DigSpot(mb, out sx, out sy)) { tx = sx; ty = sy; } return true; }
                case 5:
                    {
                        int px, py, best = -1; double bd = 350.0 * 350.0; int bx = 0, by = 0;
                        foreach (Arena.ItemDrop d in _m.Arena._items.Values)
                        {
                            if (d.item == null || d.quantity <= 0 || (d.item.id != MineralTox && d.item.id != MineralTso)) continue;
                            if (d.item.id == MineralTox && b.KitCharges >= 90) continue;
                            if (d.item.id == MineralTso && mb.EngTso >= 150) continue;
                            double dd = Dist2(b._state.positionX, b._state.positionY, d.positionX, d.positionY);
                            if (dd < bd) { bd = dd; best = 1; bx = d.positionX; by = d.positionY; }
                        }
                        if (best > 0) { tx = bx >> 4; ty = by >> 4; return true; }
                        int sx, sy; if (DigSpot(mb, out sx, out sy)) { Around(sx, sy, 3, out tx, out ty); }
                        px = py = 0;
                        return true;
                    }
                case 7:
                    foreach (CTFXBot o in _m.LiveBots)
                        if (o.Mini != null && o.Mini.Side == mb.Side && o.Mini.Role == "MED" && !o.IsDead && AreaOf(o._state.positionX >> 4, o._state.positionY >> 4) == HomeAreaIdx(mb.Side))
                        { tx = o._state.positionX >> 4; ty = o._state.positionY >> 4; return true; }
                    return Anchor(HomeArea[mb.Side], "MED", 2, out tx, out ty);
                case 9: { int px, py; if (FindPile(b, HomeAreaIdx(mb.Side), MineralTso, out px, out py)) { tx = px >> 4; ty = py >> 4; } return true; }
            }
            return false;
        }

        /// <summary>A mineral drop of item within r px of the bot.</summary>
        bool FindPileNear(CTFXBot b, int item, double r, out int px, out int py)
        {
            px = py = 0;
            foreach (Arena.ItemDrop d in _m.Arena._items.Values)
            {
                if (d.item == null || d.item.id != item || d.quantity <= 0) continue;
                if (Dist2(b._state.positionX, b._state.positionY, d.positionX, d.positionY) <= r * r) { px = d.positionX; py = d.positionY; return true; }
            }
            return false;
        }

        /// <summary>Takes up to max of the drop of item lying at (px, py): the drop goes, what it could not carry is put back where it lay. Returns what it took.</summary>
        int TakeDrop(CTFXBot b, int px, int py, int item, int max)
        {
            if (max <= 0) return 0;
            Arena.ItemDrop got = null;
            foreach (Arena.ItemDrop d in _m.Arena._items.Values)
                if (d.item != null && d.item.id == item && d.quantity > 0 && d.positionX == px && d.positionY == py) { got = d; break; }
            if (got == null) return 0;
            int qty = got.quantity, took = Math.Min(qty, max);
            ItemInfo it = got.item;
            _m.Arena._items.Remove(got.id);
            Helpers.Object_ItemDropUpdate(_m.Arena.Players, got.id, 0);
            if (took < qty) _m.Arena.itemSpawn(it, (ushort)(qty - took), (short)px, (short)py, (short)16, null);
            return took;
        }

        // ---------------------------------------------------------------- the medic's pickup delay (Travis: aces and veterans pick it up faster, worse players slower)
        static int PileReactMs(CTFXBot b, Random rng)
        {
            string tier = b.Slot != null && b.Slot.Human != null ? b.Slot.Human.Tier : "regular";
            switch (tier)
            {
                case "elite": case "ace": return 400 + rng.Next(600);
                case "veteran": return 900 + rng.Next(1000);
                case "rookie": return 3500 + rng.Next(3500);
                default: return 1800 + rng.Next(1800);
            }
        }

        void MedPileStep(int now)
        {
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || mb.Role != "MED" || mb.Phase != MiniBot.Defend) continue;
                int px, py;
                bool pile = b.TsoUnits >= 0 && b.TsoUnits < 150 && FindPile(b, HomeAreaIdx(mb.Side), MineralTso, out px, out py);
                if (!pile) { mb.PileSeenAt = 0; mb.PileGo = false; continue; }
                if (mb.PileSeenAt == 0) { mb.PileSeenAt = now + PileReactMs(b, _m.Rng) | 1; continue; }
                if (!mb.PileGo && now - mb.PileSeenAt >= 0) { mb.PileGo = true; b.ForceGoal(); }
            }
        }

        /// <summary>DefendGoal for the medic: the Tsolvy pile once its reaction delay has run (MedPileStep).</summary>
        bool MedPileReady(CTFXBot b, MiniBot mb) { return mb.PileGo && b.TsoUnits >= 0 && b.TsoUnits < 150; }
    }
}
