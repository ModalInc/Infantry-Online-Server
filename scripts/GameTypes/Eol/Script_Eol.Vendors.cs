using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

using Assets;
using InfServer;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Traders and vendors (2026-11-15).
    ///   - HELMET TRADER (vehicle 459) at Pioneer Station: "Sell all helmets" sells every helmet you carry for cash, at
    ///     the zone's helmet value ($20 - the old trader's rate: 500 helmets for $10,000), more for officers' helmets
    ///     (see HelmetPrices). "Helmet prices" lists them. Not destroyable; put back if it goes (sector rotation).
    ///   - SUPPLY VENDORS (vehicle 458): on the zone owner's outdoor spots (VendorTiles) in the open sectors. Rifle and
    ///     pistol ammo and recall tokens (the products are in the .veh). Destroyable; a new one comes VendorRespawnMs
    ///     after one is destroyed, at once after a sector change.
    /// Both stand on the arena's peaceful team (-1, shoots at nobody) so every player can use them.
    /// </summary>
    public partial class Script_Eol
    {
        public const int HelmetTraderVehId = 459;
        public const int SupplyVendorVehId = 458;
        const int VendorRespawnMs = 180000;          // 3 min after one is destroyed
        const int VendorPollMs = 5000;
        const int VendorSpotSearchRadius = 120;
        /// <summary>Supply vendors in each open sector (2026-11-21, owner: 5 each, at random).</summary>
        const int VendorsPerSector = 5;
        /// <summary>Vendors of one sector stay at least this far apart (raw), relaxed if the sector has no room for it.</summary>
        const int VendorMinSpacing = 1600;

        /// <summary>A random spot in the sector: open outdoor ground (terrain 0, no wall 2 tiles round), apart from the
        /// sector's other vendors.</summary>
        bool PickRandomVendorSpot(string sector, out short x, out short y)
        {
            x = y = 0;
            short minX, maxX, minY, maxY;
            if (_eol == null || !_eol.TryGetSectorBounds(sector, out minX, out maxX, out minY, out maxY))
                return false;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            for (int attempt = 0; attempt < 600; attempt++)
            {
                int spacing = attempt < 300 ? VendorMinSpacing : attempt < 450 ? VendorMinSpacing / 2 : 0;
                int px = _rand.Next(minX, maxX + 1), py = _rand.Next(minY, maxY + 1);
                if (!_eol.IsInSector(sector, px, py) || !VendorGroundClear(px, py))
                    continue;
                bool near = false;
                foreach (KeyValuePair<string, VendorSlot> kv in _vendorSlots)
                    if (kv.Key.StartsWith(sector + "#") && Math.Abs(kv.Value.X - px) < spacing && Math.Abs(kv.Value.Y - py) < spacing)
                    { near = true; break; }
                if (near)
                    continue;
                x = (short)px; y = (short)py;
                return true;
            }
            return false;
        }

        bool VendorGroundClear(int x, int y)
        {
            for (int dy = -32; dy <= 32; dy += 16)
                for (int dx = -32; dx <= 32; dx += 16)
                {
                    int ax = x + dx, ay = y + dy;
                    if (ax < 0 || ay < 0 || _arena.getTile(ax, ay).Blocked || _arena.getTerrainID(ax, ay) != 0)
                        return false;
                }
            return true;
        }

        /// <summary>
        /// Supply vendor spots, in TILES (the zone owner's outdoor spots, 2026-11-15 - next to flags they stood indoors).
        /// One vendor on each spot that lies in an open sector. Raw = tile x 16 + 8.
        /// Sector A: 88,299 309,80 · B: 159,684 660,800 · C: 1250,384 1231,65 812,93 1125,704 869,432 (three-sector map)
        /// </summary>
        static readonly KeyValuePair<short, short>[] VendorTiles =
        {
            new KeyValuePair<short, short>(88, 299), new KeyValuePair<short, short>(309, 80),
            new KeyValuePair<short, short>(159, 684),
            new KeyValuePair<short, short>(1250, 384), new KeyValuePair<short, short>(1231, 65), new KeyValuePair<short, short>(812, 93),
            new KeyValuePair<short, short>(660, 800), new KeyValuePair<short, short>(1125, 704), new KeyValuePair<short, short>(869, 432),
        };
        // Pioneer Station: tile 1667,165 (the zone owner's spot, 2026-11-15 - the first spot stuck him on physics), facing south.
        // Raw = tile x 16 + 8 (the tile's centre). Yaw: 240 steps a turn, 0 = north, 120 = south.
        // (2026-11-27, owner: "move helmet trader above where vehicle vendor is") open world: the alcove north of the VPPC
        static short HelmetTraderX { get { return EolMap.OpenWorld ? (short)(1687 * 16 + 8) : (short)(1667 * 16 + 8); } }
        static short HelmetTraderY { get { return EolMap.OpenWorld ? (short)(119 * 16 + 8) : (short)(165 * 16 + 8); } }
        const byte HelmetTraderYaw = 120;
        // (2026-11-15, owner: min $500 + 200 xp a helmet, a bit more for the better ones)
        const int HelmetBasePrice = 500, HelmetSpecialistPrice = 750, HelmetCommandPrice = 1000;
        /// <summary>Experience per helmet = its cash value x 2/5 (500 -> 200, 750 -> 300, 1000 -> 400).</summary>
        static int HelmetXpFor(int cash) { return cash * 2 / 5; }

        /// <summary>Officers' and specialists' helmets are worth more (everything else that is a helmet: base price).</summary>
        static readonly Dictionary<int, int> HelmetPrices = new Dictionary<int, int>
        {
            // command: Captain's, Lieutenant's, Bot Captain, Pirate Commander - x2
            { 85, HelmetCommandPrice }, { 1473, HelmetCommandPrice }, { 185, HelmetCommandPrice }, { 131, HelmetCommandPrice },
            { 1595, HelmetCommandPrice }, { 1673, HelmetCommandPrice },
            // leaders and specialists: Sergeant's, Squad Leader's, Field Surgeon's, Chief Engineer's, BlackOps',
            // Drop Trooper's, MPAC's, NeuroTAC's, Marine's, Bot Engineer - x1.5
            { 93, HelmetSpecialistPrice }, { 94, HelmetSpecialistPrice }, { 1472, HelmetSpecialistPrice }, { 1340, HelmetSpecialistPrice },
            { 170, HelmetSpecialistPrice }, { 1338, HelmetSpecialistPrice }, { 1339, HelmetSpecialistPrice }, { 1341, HelmetSpecialistPrice },
            { 1342, HelmetSpecialistPrice }, { 188, HelmetSpecialistPrice },
            // (2026-11-18) the INVENTORY helmets the pickups give (what is actually sold) - the ids above are the dropped
            // pickups, so every helmet used to sell at the base price. Command: Captain, Lieutenant, Pirate Commander;
            // specialists: Sergeant, Squad Leader, Field Surgeon, Chief Engineer (+ Bot Engineer), BlackOps, Drop Trooper,
            // MPAC, NeuroTAC, Marine
            { 1492, HelmetCommandPrice }, { 1505, HelmetCommandPrice }, { 2061, HelmetCommandPrice },
            { 1497, HelmetSpecialistPrice }, { 1499, HelmetSpecialistPrice }, { 1504, HelmetSpecialistPrice }, { 1503, HelmetSpecialistPrice },
            { 1501, HelmetSpecialistPrice }, { 1507, HelmetSpecialistPrice }, { 1502, HelmetSpecialistPrice }, { 1508, HelmetSpecialistPrice },
            { 1506, HelmetSpecialistPrice },
        };

        /// <summary>
        /// (2026-11-15, owner: spiders drop a reward the trader buys) "Spider Parts" (item 2060, an inventory item like
        /// the helmet tokens): one in each Spider Hive Drop (148) and Spider Adult Drop (147) - the hives and the
        /// web-shooting adults. (Their gems / husk are instant cash pickups, never in the inventory.) Min $500.
        /// </summary>
        public const int SpiderPartsItemId = 2060;
        static readonly Dictionary<int, int> SpiderPrices = new Dictionary<int, int>
        {
            { SpiderPartsItemId, 500 },
        };

        /// <summary>Anything the trader buys: helmets and spider parts.</summary>
        static bool IsTraderItem(ItemInfo it)
        {
            return it != null && (SpiderPrices.ContainsKey(it.id) || IsHelmetItem(it));
        }

        Vehicle _helmetTrader;
        int _tickVendorPoll;

        class VendorSlot
        {
            public Vehicle Vendor;
            public short X, Y;
            public bool Respawning;    // destroyed: a new one at RespawnDue (removed by a sector change: at once)
            public int RespawnDue;
            public string Name;        // its spot (for the log)
        }
        /// <summary>"tileX,tileY" -> its vendor.</summary>
        readonly Dictionary<string, VendorSlot> _vendorSlots = new Dictionary<string, VendorSlot>();

        static bool VehicleGone(Vehicle v)
        {
            return v == null || v.IsDead || v.bCondemned;
        }

        /// <summary>Every few seconds: the trader is at the station, each open sector has its vendors.</summary>
        void PollVendors(int now)
        {
            if (_arena == null || _eol == null || !_arena._bGameRunning || _bbetweengames || !_eol.MatchSectorPlayReady)
                return;
            if (_tickVendorPoll != 0 && now - _tickVendorPoll < VendorPollMs)
                return;
            _tickVendorPoll = now;

            Team peaceful = _arena.getTeamByID(-1);
            if (peaceful == null)
                return;

            // (one still waiting in the despawn queue is left to go first - never two traders at once)
            if (VehicleGone(_helmetTrader) && !IsVehicleDespawnQueued(_helmetTrader))
                _helmetTrader = PlaceTraderVehicle(HelmetTraderVehId, HelmetTraderX, HelmetTraderY, peaceful, exact: true, yaw: HelmetTraderYaw);

            // slots wanted: (2026-11-21, owner: "the ammo vendor spawns randomly around in each sector when active, lets do 5
            // in each") VendorsPerSector random open outdoor spots in every open sector, picked when the sector opens and kept
            // while it stays open (a sector change picks new ones). (VendorTiles, the old fixed spots, are no longer used.)
            HashSet<string> wanted = new HashSet<string>();
            if (_eol._activeSectors != null)
                foreach (string sector in _eol._activeSectors)
                {
                    if (string.IsNullOrEmpty(sector))
                        continue;
                    for (int i = 0; i < VendorsPerSector; i++)
                    {
                        string key = sector + "#" + i;
                        VendorSlot have;
                        if (_vendorSlots.TryGetValue(key, out have))
                        {
                            wanted.Add(key);
                            continue;
                        }
                        short x, y;
                        if (!PickRandomVendorSpot(sector, out x, out y))
                            continue;   // (no room found this poll - tried again next poll)
                        wanted.Add(key);
                        _vendorSlots[key] = new VendorSlot { X = x, Y = y, Name = string.Format("{0} random {1} (tile {2},{3})", sector, i + 1, x / 16, y / 16) };
                    }
                }

            // sectors that closed: their vendors go (sector rotation usually took them already)
            List<string> stale = null;
            foreach (KeyValuePair<string, VendorSlot> kv in _vendorSlots)
                if (!wanted.Contains(kv.Key))
                    (stale ?? (stale = new List<string>())).Add(kv.Key);
            if (stale != null)
                foreach (string key in stale)
                {
                    VendorSlot s = _vendorSlots[key];
                    if (!VehicleGone(s.Vendor))
                        EnqueueVehicleDespawn(s.Vendor);
                    _vendorSlots.Remove(key);
                }

            foreach (VendorSlot s in _vendorSlots.Values)
            {
                if (s.Vendor != null && VehicleGone(s.Vendor))
                {   // destroyed: another in a few minutes; removed (sector change, reset): straight back
                    // (IsDead is also true for a condemned = removed vehicle - only 0 health means it was destroyed)
                    s.Respawning = s.Vendor._state != null && s.Vendor._state.health <= 0;
                    s.RespawnDue = now + VendorRespawnMs;
                    s.Vendor = null;
                }
                if (s.Vendor == null && (!s.Respawning || now - s.RespawnDue >= 0))
                {
                    s.Vendor = PlaceTraderVehicle(SupplyVendorVehId, s.X, s.Y, peaceful, exact: true);
                    if (s.Vendor != null)
                    {
                        s.Respawning = false;
                        Log.write(TLog.Normal, "[Eol] Supply vendor placed at {0} ({1},{2}).", s.Name, s.Vendor._state.positionX, s.Vendor._state.positionY);
                    }
                }
            }
        }

        /// <param name="exact">Stand exactly there (a hand-picked spot) - no search for an open tile nearby.</param>
        /// <param name="yaw">Facing: 240 steps a turn, 0 = north.</param>
        Vehicle PlaceTraderVehicle(int vehId, short x, short y, Team team, bool exact = false, byte yaw = 0)
        {
            VehInfo vi = _arena._server._assets.getVehicleByID(vehId);
            if (vi == null)
                return null;
            // (inside the map: getTile indexes the tile array directly - a flag near the edge plus the offset would throw)
            int maxX = _arena._levelWidth * 16 - 17, maxY = _arena._levelHeight * 16 - 17;
            if (maxX < 16 || maxY < 16)
                return null;
            x = (short)Math.Max(16, Math.Min(maxX, (int)x));
            y = (short)Math.Max(16, Math.Min(maxY, (int)y));
            // the spot itself when it's open, else an open tile near it (none: tried again next poll)
            Helpers.ObjectState at = exact || !_arena.getTile(x, y).Blocked
                ? new Helpers.ObjectState { positionX = x, positionY = y }
                : findOpenWarpBot(_arena, x, y, VendorSpotSearchRadius);
            if (at == null)
                return null;
            at.yaw = yaw;
            return _arena.newVehicle(vi, team, null, at);
        }

        // ---------- the helmet trader ----------

        static bool IsHelmetItem(ItemInfo it)
        {
            if (it == null || string.IsNullOrEmpty(it.name))
                return false;
            if (!it.name.EndsWith("Helmet", StringComparison.OrdinalIgnoreCase))
                return false;   // ("Helmet Prize Class 1" and the "... Helms" bundles are not helmets to sell)
            string cat = it.category ?? "";
            return cat.Equals("Helmets", StringComparison.OrdinalIgnoreCase) || cat.Equals("NPC", StringComparison.OrdinalIgnoreCase);
        }

        static int HelmetValue(ItemInfo it)
        {
            int v;
            if (it != null && HelmetPrices.TryGetValue(it.id, out v))
                return v;
            if (it != null && SpiderPrices.TryGetValue(it.id, out v))
                return v;
            return IsHelmetItem(it) ? HelmetBasePrice : 0;
        }

        /// <summary>A Helmet Trader product was chosen (handled here - the engine produces nothing for it).</summary>
        void HandleHelmetTrader(InfServer.Game.Player player, VehInfo.Computer.ComputerProduct product)
        {
            if (player == null || product == null || string.IsNullOrEmpty(product.Title))
                return;
            if (product.Title.StartsWith("Helmet prices", StringComparison.OrdinalIgnoreCase))
            {
                player.sendMessage(0, String.Format("&Helmet Trader: ${0} + {1} xp for each helmet.", HelmetBasePrice, HelmetXpFor(HelmetBasePrice)));
                player.sendMessage(0, String.Format("&Specialists ${0} + {1} xp: Sergeant, Squad Leader, Surgeon, Chief Eng, BlackOps, Drop Trooper, MPAC, NeuroTAC, Marine, Bot Eng.",
                    HelmetSpecialistPrice, HelmetXpFor(HelmetSpecialistPrice)));
                player.sendMessage(0, String.Format("&Command ${0} + {1} xp: Captain, Lieutenant, Bot Captain, Pirate Commander.",
                    HelmetCommandPrice, HelmetXpFor(HelmetCommandPrice)));
                player.sendMessage(0, String.Format("&Spider Parts ${0} + {1} xp each (from spider hives and web spiders).",
                    SpiderPrices[SpiderPartsItemId], HelmetXpFor(SpiderPrices[SpiderPartsItemId])));
                return;
            }
            if (!product.Title.StartsWith("Sell all helmets", StringComparison.OrdinalIgnoreCase))
                return;

            int total = 0, count = 0, xp = 0;
            List<string> sold = new List<string>();
            foreach (InfServer.Game.Player.InventoryItem ii in new List<InfServer.Game.Player.InventoryItem>(player._inventory.Values))
            {
                if (ii == null || ii.quantity == 0 || !IsTraderItem(ii.item))
                    continue;
                int qty = ii.quantity, each = HelmetValue(ii.item);
                if (!player.inventoryModify(false, ii.item.id, -qty))
                    continue;
                total += each * qty;
                xp += HelmetXpFor(each) * qty;
                count += qty;
                sold.Add(qty > 1 ? String.Format("{0} x{1}", ii.item.name, qty) : ii.item.name);
            }
            if (count == 0)
            {
                player.sendMessage(-1, "You have no helmets to sell.");
                return;
            }
            player.Cash += total;
            player.Experience += xp;
            player.syncInventory();
            player.syncState();
            // (2026-11-15, "handing in a lot of helmets caused a crash") the message listed every helmet sold - a big
            // hand-in made one chat line far longer than the client takes. Now: the totals, plus the kinds only while
            // they fit in a short line.
            string head = String.Format("&Sold {0} helmet{1} for ${2} + {3} xp", count, count == 1 ? "" : "s", total, xp);
            string list = "";
            foreach (string s in sold)
            {
                string next = list.Length == 0 ? s : list + ", " + s;
                if (head.Length + 2 + next.Length > MaxChatLine)
                {
                    list = list.Length == 0 ? "" : list + ", ...";
                    break;
                }
                list = next;
            }
            player.sendMessage(0, list.Length == 0 ? head : head + ": " + list);
        }

        // ---------- the supply vendor ----------

        /// <summary>
        /// A Supply Vendor product (2026-11-16, "lag out on vendor purchase"). Sold here instead of by the engine,
        /// which took the cash without ever sending the new cash total to the client, and took the full price
        /// even when the stack was nearly at its cap. Now: only what fits is sold, at the per-round price, and the
        /// client gets ONE inventory and ONE state update. The products are the shop's own ammo (Ammo - Rifle 216,
        /// Ammo - Pistol 215 - what every shop rifle and pistol fires) and Recall Tokens.
        /// </summary>
        void HandleSupplyVendor(InfServer.Game.Player player, Computer computer, VehInfo.Computer.ComputerProduct product)
        {
            if (player == null || product == null || player.IsDead || player.IsSpectator || product.ProductToCreate <= 0 || product.Quantity <= 0)
                return;
            if (!InfServer.Logic.Logic_Assets.SkillCheck(player, product.SkillLogic))
                return;
            ItemInfo item = _arena._server._assets.getItemByID(product.ProductToCreate);
            if (item == null)
                return;

            int have = player.getInventoryAmount(item.id);
            int cap = item.maxAllowed < 0 ? -item.maxAllowed : (item.maxAllowed > 0 ? item.maxAllowed : int.MaxValue);
            int qty = Math.Min(product.Quantity, Math.Max(0, cap - have));
            if (qty <= 0)
            {
                player.sendMessage(-1, String.Format("You can't carry more {0} (max {1}).", item.name, cap));
                return;
            }
            // price per unit from the product (e.g. 200 rounds for $800 = $4 a round), rounded up
            int cost = qty == product.Quantity ? product.Cost : (int)Math.Ceiling(product.Cost * (double)qty / product.Quantity);
            if (player.Cash < cost)
            {
                player.sendMessage(-1, String.Format("You need ${0} for {1} x{2} (you have ${3}).", cost, item.name, qty, player.Cash));
                return;
            }
            if (!player.inventoryModify(false, item, qty))
            {
                player.sendMessage(-1, "Unable to add " + item.name + " to your inventory.");
                return;
            }
            int got = player.getInventoryAmount(item.id) - have;
            if (got <= 0)
                return;
            if (got < qty)   // (a category limit took some): charge only for what arrived
                cost = (int)Math.Ceiling(product.Cost * (double)got / product.Quantity);
            player.Cash -= cost;
            player.syncInventory();
            player.syncState();
            player.sendMessage(0, String.Format("&Bought {0} x{1} for ${2}.", item.name, got, cost));
        }

        /// <summary>Longest chat line sent to a client (theirs crash on very long lines).</summary>
        const int MaxChatLine = 180;
    }
}
