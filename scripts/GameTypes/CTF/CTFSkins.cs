using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Assets;
using InfServer.Game;
using InfServer.Logic;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// Item skins (Travis, 2026-10-23): one system for every cosmetic copy of a stock item - purchased skin sets, the old
    /// per-player ?cosmetic swaps, champion skins and (since 10-27) the champion item conversions alike. A skin is an item in
    /// ctfpl.itm that copies a stock item (any item, any weapon) with only its graphics changed; the catalogue below says who
    /// owns which skins of which stock items.
    ///
    ///   catalogue  Sets of skins: owners (aliases) and/or a site product (PurchaseCache.txt); per stock item, its skin options.
    ///              A champion set only shows while champion items are on in the arena (isChampEnabled: off on Sundays, in league
    ///              arenas and by *champItems). New skin = its item in the itm + a line in Catalogue. CTF.cs adds its champion
    ///              groups (AddChampionGroup) as champion sets.
    ///   OFF BY DEFAULT (Travis 10-27: players complained their skins switched on by themselves): an owner holds the stock item until
    ///              they turn a skin on. Greet tells them they have some.
    ///   ?skin / ?cosmetic (the same command): lists the skins for the items your class uses (numbered; ?skin all = every class);
    ///              ?skin on / off = all of them; ?skin <#|item> [option|on|off]; ?skin <option> picks it wherever you own it.
    ///              Picks are kept per alias in SkinPrefs.txt (written by the zone - never push it from local). ?champ = the champion
    ///              ones only.
    ///   followers  an option that only fires beside another skin (CB RG needs the Care Bearsault Rifle, Subzero (Tranq) the Needler
    ///              (SubZero), from the itm's requiredItem) is never listed: it follows whatever the player holds for that item.
    ///   shared     one skin item for several stock items (the Care Bearmer serves all four class beamers) is swapped only by the
    ///              stock item the player's class can use.
    ///   sweep      1 Hz from poll and straight after ?b / ?bw: an owner's stock item, or another skin of it, becomes their pick.
    ///   pickup     an owner picking up the stock item or a skin gets their pick; anyone else picking up a skin gets the stock
    ///              item, so a dropped skin can't be passed around. Skins a mod *prizes to someone are left alone.
    ///   stats      StatId maps a skin - and each child projectile of a multi-item skin - to the stock id, so weapon stats and
    ///              beamer hits count it as the stock weapon (champion items keep their own ids, as before); StockOf does the same
    ///              for ?bsave / ?short.
    /// Static, so every arena shares the picks. An option whose item is missing from the item file is skipped.
    ///   off        *skins off (refs; OFF from the start in league arenas - CTF.cs skinsEnabled, passed in as skinsOk): every cosmetic in anyone's
    ///              inventory, the catalogue's and OtherCosmetics, becomes its stock item; pickups give the stock item; ?skin picks are kept.
    /// </summary>
    public static class Skins
    {
        private class Option { public string Key, ItemName; public int ItemId; public ItemInfo Item; }
        private class Family { public string Stock; public string[] Short; public ItemInfo StockItem; public List<Option> Options = new List<Option>(); }
        private class Set
        {
            public string Label, Product; public bool Champion, Stats = true; public string[] Owners;
            public List<Family> Items = new List<Family>();
            public Set(string label, string[] owners, string product = null, bool champion = false) { Label = label; Owners = owners; Product = product; Champion = champion; }
            /// <summary>stock item, its short names for ?skin, then option key / skin item name pairs</summary>
            public Set Item(string stock, string[] shortNames, params string[] keyItem)
            {
                var f = new Family { Stock = stock, Short = shortNames };
                for (int i = 0; i + 1 < keyItem.Length; i += 2) f.Options.Add(new Option { Key = keyItem[i], ItemName = keyItem[i + 1] });
                Items.Add(f);
                return this;
            }
            /// <summary>one option given by item id (the champion groups' tables are id-keyed)</summary>
            public Set ItemId(string stock, string key, int id)
            {
                var f = new Family { Stock = stock, Short = new string[0] };
                f.Options.Add(new Option { Key = key, ItemId = id });
                Items.Add(f);
                return this;
            }
        }

        // ------------------------------------------------------------------ the catalogue
        private static readonly List<Set> Catalogue = new List<Set>
        {
            // Black Widow / Crab CAW (10-22), bought by JACKIE; Axidus = staff test owner
            new Set("CAW skin set", new[] { "JACKIE", "Axidus" }, product: "bw_caw")
                .Item("Kuchler A6 CAW", new[] { "caw" }, "widow", "Black Widow CAW", "crab", "Crab CAW"),
            // the site's Rainbow CAW perk (10-27: was CTF.cs PRODUCT_CONVERSIONS on ?b only, which fought the sweep over the same CAW)
            // 10-27: the Rainbow Caw itself fires one round with the live look (no more 13-item relay); Stream / Prism are the remade looks
            new Set("Rainbow CAW", new string[0], product: "rainbow_caw")
                .Item("Kuchler A6 CAW", new[] { "caw" }, "rainbow", "Rainbow Caw", "stream", "Rainbow Caw Stream", "prism", "Rainbow Caw Prism"),
            // Freedom (10-27, Zmn's ask; Axidus = staff test): the Maklov AR as a thin red-white-blue tracer (star glint, navy canton, ribbon) with
            // a star muzzle flash, sparkler trail and star pop; the RG and the RG holder become firework rockets and follow the AR (their
            // requiredItem is the Freedom AR, so they are never listed); a Freedom kill gets an eagle that plants a flag in the body
            // (CTFFreedom.cs). Not champion-flagged. Art in its own freedom.blo.
            new Set("Freedom", new[] { "Zmn", "Axidus" })
                .Item("Maklov AR mk 606", new[] { "ar", "maklov" }, "freedom", "Freedom AR")
                .Item("Maklov RG 2", new[] { "rg" }, "freedom", "Freedom RG")
                .Item("Maklov RG 2 Holder", new[] { "rgh", "holder" }, "freedom", "Freedom RG Holder"),
            // the ?cosmetic swaps that were hard-coded in CTF.cs before 10-23
            new Set("SIG ARMS JACKIE", new[] { "JACKIE" })
                .Item("SiG Arms m2 AS", new[] { "sg", "sig" }, "jackie", "SIG ARMS JACKIE"),
            new Set("Maklov DC mk2", new[] { "Phlow" })
                .Item("Maklov AC mk2", new[] { "ac" }, "dc", "Maklov DC mk2"),
            // Shadeaux's plasma mine (10-25): its blast spells LOL out of mist. His alone. Not champion-flagged on purpose, so it still plays
            // on Sundays when champion skins are off (a rare effect people can enjoy)
            new Set("LOL Plasma Mine (Shadeaux)", new[] { "Shadeaux" })
                .Item("Plasma Mine", new[] { "pm", "plasma", "mine" }, "lol", "LOL Plasma Mine"),
            // Sorriddo's CTFDL4 champion weapon (10-23): a pink beamer (Bug Hunt's ElectroBolt look) from any store beamer
            new Set("Pink beamer (CTFDL4 champion)", new[] { "Soriddo", "Axidus" }, champion: true)
                .Item("Medic Beamer", new[] { "eb", "beamer" }, "pink", "Pink Medic Beamer")
                .Item("Engy Beamer", new[] { "eb", "beamer" }, "pink", "Pink Engy Beamer")
                .Item("SL Beamer", new[] { "eb", "beamer" }, "pink", "Pink SL Beamer")
                .Item("Infil Beamer", new[] { "eb", "beamer" }, "pink", "Pink Infil Beamer"),
            // Duel champion ARs (Round 4, 10-25): the ONE standard Dueler AR that ?duel hands out to everybody (CTF.cs: item 1162 "AssaultRifle") in each champion's
            // own look; both sets are keyed to it, so Axidus (owner of both) switches with ?skin a3 / ?skin b3 / ?skin off. Not champion-flagged on purpose (dueling never
            // happens in a league match), so it plays on Sundays. Art in hwcaw.blo.
            new Set("Dueling champion AR (season/duel 1)", new[] { "Dro", "Dropped", "Axidus" })
                .Item("AssaultRifle", new[] { "ar", "duel", "dar" }, "a3", "Duel Champion AR 1"),
            new Set("Dueling champion AR (season/duel 2-3)", new[] { "thegreatchompy", "tgc", "Axidus" })
                .Item("AssaultRifle", new[] { "ar", "duel", "dar" }, "b3", "Duel Champion AR 2"),
            // Thor's Pumpkin Beamer (10-25): a jack-o'-lantern riding an orange arc, from any store beamer (art in hwcaw.blo)
            new Set("Pumpkin beamer", new[] { "Thor", "Axidus" })
                .Item("Medic Beamer", new[] { "eb", "beamer" }, "pumpkin", "Pumpkin Medic Beamer")
                .Item("Engy Beamer", new[] { "eb", "beamer" }, "pumpkin", "Pumpkin Engy Beamer")
                .Item("SL Beamer", new[] { "eb", "beamer" }, "pumpkin", "Pumpkin SL Beamer")
                .Item("Infil Beamer", new[] { "eb", "beamer" }, "pumpkin", "Pumpkin Infil Beamer"),
            // posty's Golden Tranq (10-26, a championship reward): a gold syringe dart; its stuns, kills and Teleport Beacon arrivals have their own
            // effects (CTFGoldenTranq.cs, same owners). Champion-flagged: off on Sundays, in league arenas and with *champItems off. Art in hwcaw.blo.
            new Set("Golden Tranq (champion)", new[] { "posty", "Axidus" }, champion: true)
                .Item("Tranq", new[] { "tranq", "tq" }, "gold", "Golden Tranq"),
        };

        private static readonly object _catLock = new object();
        private static readonly HashSet<string> _groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>a CTF.cs champion group joins the catalogue as a champion set (once per zone run, whichever arena opens first): its
        /// aliases, the option key players see (s1, s2...) and its stock item name -> champion item id table. A group added before another
        /// with the same key wins that stock item (anjro's own SiG before the rest of OVDL1).</summary>
        public static void AddChampionGroup(string label, string key, IEnumerable<string> aliases, IEnumerable<KeyValuePair<string, int>> conversions)
        {
            lock (_catLock)
            {
                if (!_groups.Add(label))
                    return;
                var set = new Set(label + " champion items", aliases.ToArray(), champion: true) { Stats = false };
                foreach (var kv in conversions)
                    set.ItemId(kv.Key, key, kv.Value);
                Catalogue.Add(set);
                _resolved = false;
            }
        }

        /// <summary>more looks of champion items for a champion group (the Rainbow CAW's Stream / Prism, the Care Bearmer's Braid): the same option
        /// pairs on every stock item named; added after the groups, so the group's own key stays the first option</summary>
        public static void AddChampionLooks(string label, IEnumerable<string> aliases, string[] stocks, params string[] keyItem)
        {
            lock (_catLock)
            {
                if (!_groups.Add(label))
                    return;
                var set = new Set(label, aliases.ToArray(), champion: true) { Stats = false };
                foreach (string stock in stocks)
                    set.Item(stock, new string[0], keyItem);
                Catalogue.Add(set);
                _resolved = false;
            }
        }

        // ------------------------------------------------------------------ item tables (built from the item file, again after a group joins)
        private static bool _resolved;
        private static readonly Dictionary<int, ItemInfo> _stockOfSkin = new Dictionary<int, ItemInfo>();         // skin id -> stock item (the first family's)
        private static readonly Dictionary<int, int> _statId = new Dictionary<int, int>();                         // skin / skin child id -> stock id
        private static readonly Dictionary<string, HashSet<int>> _family = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);   // stock name -> stock + skin ids
        private static readonly Dictionary<int, string> _familyOf = new Dictionary<int, string>();                 // any of those ids -> stock name (the first family's)
        private static readonly Dictionary<int, List<string>> _shared = new Dictionary<int, List<string>>();       // a skin id several stock items share -> those stock names
        private static readonly Dictionary<string, ItemInfo> _stockItem = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
        // Cosmetic items outside the catalogue that *skins off also turns back into their stock item
        private static readonly string[][] OtherCosmetics = { new[] { "Rainbow CAW", "Kuchler A6 CAW" } };
        private static readonly Dictionary<int, ItemInfo> _offStock = new Dictionary<int, ItemInfo>();               // any cosmetic id -> stock item (skins off)
        private static readonly HashSet<string> _toldOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);    // alias|arena told skins are off

        private static void Resolve()
        {
            if (_resolved) return;
            lock (_catLock)
            {
                if (_resolved) return;
                _stockOfSkin.Clear(); _statId.Clear(); _family.Clear(); _familyOf.Clear(); _shared.Clear(); _stockItem.Clear(); _offStock.Clear();
                foreach (Set set in Catalogue)
                    foreach (Family f in set.Items)
                    {
                        f.StockItem = AssetManager.Manager.getItemByName(f.Stock);
                        if (f.StockItem == null) { Console.WriteLine("[skins] stock item missing: " + f.Stock); continue; }
                        _stockItem[f.Stock] = f.StockItem;
                        HashSet<int> ids;
                        if (!_family.TryGetValue(f.Stock, out ids)) _family[f.Stock] = ids = new HashSet<int> { f.StockItem.id };
                        if (!_familyOf.ContainsKey(f.StockItem.id)) _familyOf[f.StockItem.id] = f.Stock;
                        foreach (Option o in f.Options)
                        {
                            o.Item = o.ItemName != null ? AssetManager.Manager.getItemByName(o.ItemName) : AssetManager.Manager.getItemByID(o.ItemId);
                            if (o.Item == null) { Console.WriteLine("[skins] skin item missing: " + (o.ItemName ?? o.ItemId.ToString())); continue; }
                            ids.Add(o.Item.id);
                            string first;
                            if (!_familyOf.TryGetValue(o.Item.id, out first)) _familyOf[o.Item.id] = f.Stock;
                            else if (!first.Equals(f.Stock, StringComparison.OrdinalIgnoreCase))
                            {
                                List<string> sh;
                                if (!_shared.TryGetValue(o.Item.id, out sh)) _shared[o.Item.id] = sh = new List<string> { first };
                                if (!sh.Contains(f.Stock, StringComparer.OrdinalIgnoreCase)) sh.Add(f.Stock);
                            }
                            if (!_stockOfSkin.ContainsKey(o.Item.id)) _stockOfSkin[o.Item.id] = f.StockItem;
                            if (!set.Stats) continue;
                            _statId[o.Item.id] = f.StockItem.id;
                            // a multi-item skin fires its own children: pair them with the stock item's, slot by slot
                            var sm = o.Item as ItemInfo.MultiUse; var km = f.StockItem as ItemInfo.MultiUse;
                            if (sm != null && km != null && sm.childItems.Count == km.childItems.Count)
                                for (int i = 0; i < sm.childItems.Count; i++)
                                    if (sm.childItems[i].id != km.childItems[i].id && sm.childItems[i].id > 0 && km.childItems[i].id > 0)
                                        _statId[sm.childItems[i].id] = km.childItems[i].id;
                        }
                    }
                foreach (var kv in _stockOfSkin)
                    _offStock[kv.Key] = kv.Value;
                foreach (string[] c in OtherCosmetics)
                {
                    ItemInfo cos = AssetManager.Manager.getItemByName(c[0]), stock = AssetManager.Manager.getItemByName(c[1]);
                    if (cos != null && stock != null && cos.id != stock.id && !_offStock.ContainsKey(cos.id))
                        _offStock[cos.id] = stock;
                }
                _resolved = true;
            }
        }

        /// <summary>may this player's item family swap item id? Always, unless several stock items share the skin: then only the family
        /// of the stock item the player's class can use (the first one when none can)</summary>
        private static bool SwapsHere(Player p, int id, string stock)
        {
            List<string> sh;
            if (!_shared.TryGetValue(id, out sh)) return true;
            string mine = sh.FirstOrDefault(s => ClassUses(p, _stockItem[s])) ?? sh[0];
            return mine.Equals(stock, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>the stock item a cosmetic goes back to for this player (a shared skin: their class's one)</summary>
        private static ItemInfo StockFor(Player p, int id)
        {
            List<string> sh;
            if (_shared.TryGetValue(id, out sh))
            {
                string s = sh.FirstOrDefault(x => ClassUses(p, _stockItem[x])) ?? sh[0];
                return _stockItem[s];
            }
            ItemInfo stock;
            return _offStock.TryGetValue(id, out stock) ? stock : null;
        }

        private static bool ClassUses(Player p, ItemInfo item)
        {
            try { return item != null && Logic_Assets.SkillCheck(p, item.skillLogic); }
            catch { return true; }   // no class yet (spectating): treat every item as theirs
        }

        /// <summary>skins off: every cosmetic the player holds becomes its stock item (add first: if the add fails nothing is taken); true when it swapped something</summary>
        private static bool Strip(Player p)
        {
            if (p._inventory == null)
                return false;
            bool changed = false;
            foreach (var inv in p._inventory.Values.ToList())
            {
                ItemInfo stock;
                if (inv.quantity <= 0 || !_offStock.ContainsKey(inv.item.id) || (stock = StockFor(p, inv.item.id)) == null)
                    continue;
                int n = inv.quantity;
                if (p.inventoryModify(false, stock, n))
                {
                    p.inventoryModify(false, inv.item, -n);
                    changed = true;
                }
            }
            if (!changed)
                return false;
            p.syncInventory();
            if (_toldOff.Add(p._alias + "|" + (p._arena != null ? p._arena._name : "")))
                p.sendMessage(0, "&Item skins are off in this arena: your items are the standard ones here (your ?skin picks are kept).");
            return true;
        }

        // ------------------------------------------------------------------ ownership and picks
        // Staff own every set - bought, personal and champion - so they can test any skin in game (Travis 10-27: "Axidus ... need to be able to test anything")
        private static readonly string[] Staff = { "Axidus" };

        private static bool OwnsSet(string alias, Set set)
        {
            if (Staff.Any(s => s.Equals(alias, StringComparison.OrdinalIgnoreCase))) return true;
            if (set.Owners.Any(o => o.Equals(alias, StringComparison.OrdinalIgnoreCase))) return true;
            if (set.Product == null) return false;
            var products = ProductPurchaseManager.GetPlayerProducts(alias).Result;   // a cache read: completes in place
            return products != null && products.Any(p => p.Equals(set.Product, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>the alias's options per stock item, from every set they own, in catalogue order (champion ones flagged)</summary>
        private static Dictionary<string, List<KeyValuePair<Option, Set>>> Owned(string alias)
        {
            var owned = new Dictionary<string, List<KeyValuePair<Option, Set>>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(alias)) return owned;
            foreach (Set set in Catalogue)
            {
                if (!OwnsSet(alias, set)) continue;
                foreach (Family f in set.Items)
                {
                    if (f.StockItem == null) continue;
                    foreach (Option o in f.Options)
                    {
                        if (o.Item == null) continue;
                        List<KeyValuePair<Option, Set>> list;
                        if (!owned.TryGetValue(f.Stock, out list)) owned[f.Stock] = list = new List<KeyValuePair<Option, Set>>();
                        if (!list.Any(kv => kv.Key.Key.Equals(o.Key, StringComparison.OrdinalIgnoreCase) || kv.Key.Item.id == o.Item.id))   // one option per key and per item
                            list.Add(new KeyValuePair<Option, Set>(o, set));
                    }
                }
            }
            return owned;
        }

        private static int RequiredOf(ItemInfo item)
        {
            var pr = item as ItemInfo.Projectile;
            return pr != null ? pr.requiredItem : 0;
        }

        /// <summary>an option that only fires beside another skin (CB RG needs the Care Bearsault Rifle): never listed or picked, it follows</summary>
        private static bool IsFollower(Option o)
        {
            int r = RequiredOf(o.Item);
            return r > 0 && _stockOfSkin.ContainsKey(r);
        }

        /// <summary>items whose every option follows another item are not listed (the RGs and RG holders follow the AR)</summary>
        private static bool Listed(List<KeyValuePair<Option, Set>> opts) { return opts.Any(kv => !IsFollower(kv.Key)); }

        private static readonly string PREFS_PATH = Path.Combine("scripts", "GameTypes", "CTF", "SkinPrefs.txt");
        private static readonly object _lock = new object();
        private static Dictionary<string, Dictionary<string, string>> _prefs;   // alias -> stock name -> option key | off
        private static readonly HashSet<string> _told = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string Pick(string alias, string stock)
        {
            lock (_lock)
            {
                LoadPrefs();
                Dictionary<string, string> mine; string pick;
                return _prefs.TryGetValue(alias, out mine) && mine.TryGetValue(stock, out pick) ? pick : null;   // null = never picked = off
            }
        }

        /// <summary>what the alias should hold for this stock item: their pick (off when they never picked one; a champion pick waits for
        /// champion items), then fitted to what it needs to fire - an RG takes the version its AR wants, the Tranq the one its Needler wants</summary>
        private static ItemInfo Wanted(string stock, List<KeyValuePair<Option, Set>> opts, string alias, bool champOk,
            Dictionary<string, List<KeyValuePair<Option, Set>>> owned, int depth = 0)
        {
            ItemInfo stockItem = _stockItem[stock], want = stockItem;
            var live = opts.Where(kv => champOk || !kv.Value.Champion).ToList();
            string pick = Pick(alias, stock);
            if (pick != null && pick != "off")
            {
                var chosen = live.FirstOrDefault(kv => !IsFollower(kv.Key) && kv.Key.Key.Equals(pick, StringComparison.OrdinalIgnoreCase));
                if (chosen.Key != null) want = chosen.Key.Item;
            }
            int req = RequiredOf(want);
            string leader;
            if (req > 0 && depth < 2 && _familyOf.TryGetValue(req, out leader) && !leader.Equals(stock, StringComparison.OrdinalIgnoreCase))
            {
                List<KeyValuePair<Option, Set>> lopts;
                ItemInfo lw = owned.TryGetValue(leader, out lopts) ? Wanted(leader, lopts, alias, champOk, owned, depth + 1) : _stockItem[leader];
                if (lw.id != req)
                {
                    var fit = live.FirstOrDefault(kv => RequiredOf(kv.Key.Item) == lw.id);
                    if (fit.Key != null) return fit.Key.Item;
                    if (RequiredOf(stockItem) == lw.id) return stockItem;
                }
            }
            return want;
        }

        private static string Current(string stock, List<KeyValuePair<Option, Set>> opts, string alias, bool champOk, Dictionary<string, List<KeyValuePair<Option, Set>>> owned)
        {
            ItemInfo w = Wanted(stock, opts, alias, champOk, owned);
            return w.id == _stockItem[stock].id ? "normal" : w.name;
        }

        // ------------------------------------------------------------------ the swaps
        // ("Freedom Flag" 10-27: the Freedom kill reward's display item - nobody should hold one; its id 1262 was the old Freedom AR)
        // items taken out of the catalogue: anyone still holding one gets the stock item back (10-27: the Needler (SubZero) only existed as the
        // Subzero Tranq's gate; the Subzero Tranq now needs the standard Needler)
        private static readonly string[][] Retired = { new[] { "Freedom Flag", "Maklov AR mk 606" }, new[] { "Needler (SubZero)", "Needler" } };

        private static bool RetireOld(Player p)
        {
            bool changed = false;
            foreach (string[] r in Retired)
            {
                ItemInfo old = AssetManager.Manager.getItemByName(r[0]), stock = AssetManager.Manager.getItemByName(r[1]);
                if (old == null || stock == null || old.id == stock.id) continue;
                int n = p.getInventoryAmount(old.id);
                if (n > 0 && p.inventoryModify(false, stock, n)) { p.inventoryModify(false, old, -n); changed = true; }
            }
            if (changed) p.syncInventory();
            return changed;
        }

        /// <summary>puts every item the player owns skins for into its pick; true when it swapped something</summary>
        public static bool Sweep(Player p, bool champOk, bool skinsOk = true)
        {
            if (p == null) return false;
            Resolve();
            bool retired = RetireOld(p);
            if (!skinsOk) return Strip(p) || retired;
            var owned = Owned(p._alias);
            if (owned.Count == 0) return retired;
            bool changed = false;
            foreach (var kv in owned)
            {
                ItemInfo want = Wanted(kv.Key, kv.Value, p._alias, champOk, owned);
                foreach (int id in _family[kv.Key])
                {
                    if (id == want.id || !SwapsHere(p, id, kv.Key)) continue;
                    int n = p.getInventoryAmount(id);
                    if (n <= 0) continue;
                    // add first: if the add fails nothing is taken away
                    if (p.inventoryModify(false, want, n))
                    {
                        p.inventoryModify(false, AssetManager.Manager.getItemByID(id), -n);
                        changed = true;
                    }
                }
            }
            if (!changed) return false;
            p.syncInventory();
            if (_told.Add(p._alias))
                p.sendMessage(0, "&Item skins: your items now match your ?skin picks (?skin to see or change them, saved).");
            return true;
        }

        /// <summary>the item a pickup should give: the owner's pick, or the stock item for anyone else picking up a skin</summary>
        public static ItemInfo PickupItem(Player p, ItemInfo item, bool champOk, bool skinsOk = true)
        {
            if (p == null || item == null) return item;
            Resolve();
            if (!skinsOk)
            {
                ItemInfo plainOff = _offStock.ContainsKey(item.id) ? StockFor(p, item.id) : null;
                return plainOff ?? item;
            }
            string stock;
            if (!_familyOf.TryGetValue(item.id, out stock)) return item;
            var owned = Owned(p._alias);
            List<KeyValuePair<Option, Set>> opts;
            if (owned.TryGetValue(stock, out opts)) return Wanted(stock, opts, p._alias, champOk, owned);
            ItemInfo plain;
            return _stockOfSkin.TryGetValue(item.id, out plain) ? (StockFor(p, item.id) ?? plain) : item;
        }

        /// <summary>should the player hold this item even with champion items off - a skin they own outside the champion groups (the Rainbow CAW
        /// perk's Rainbow Caw, which is also an OVDL1 champion item)? CTF.cs RevertChampItems leaves those alone.</summary>
        public static bool KeepsWithoutChampion(Player p, int itemId, bool skinsOk)
        {
            if (p == null || !skinsOk) return false;
            Resolve();
            string stock;
            if (!_familyOf.TryGetValue(itemId, out stock)) return false;
            var owned = Owned(p._alias);
            List<KeyValuePair<Option, Set>> opts;
            return owned.TryGetValue(stock, out opts) && Wanted(stock, opts, p._alias, false, owned).id == itemId;
        }

        public static bool HasSkins(string alias)
        {
            Resolve();
            return Owned(alias).Count > 0;
        }

        /// <summary>is the alias's skin of this stock item on right now (Chuckie's ChUcKdaPaCk vehicle follows his Drop Pack pick)</summary>
        public static bool IsOn(string alias, string stock, bool champOk, bool skinsOk = true)
        {
            Resolve();
            if (!skinsOk) return false;
            var owned = Owned(alias);
            List<KeyValuePair<Option, Set>> opts;
            return owned.TryGetValue(stock, out opts) && Wanted(stock, opts, alias, champOk, owned).id != _stockItem[stock].id;
        }

        /// <summary>how many of an item the player holds counting its stock item and every skin of it (autopickup targets are stock amounts)</summary>
        public static int HeldWithSkins(Player p, int itemId)
        {
            Resolve();
            string stock;
            if (p == null || !_familyOf.TryGetValue(itemId, out stock))
                return p == null ? 0 : p.getInventoryAmount(itemId);
            return _family[stock].Sum(id => p.getInventoryAmount(id));
        }

        /// <summary>?drop / ?d by a base name (batch 5, 10-28): the stock item first, then every skin and champion item of it. Just the item itself
        /// when it is not the stock item of a family (a skin named on its own stands for itself only).</summary>
        public static List<ItemInfo> StockAndSkins(ItemInfo item)
        {
            var list = new List<ItemInfo>();
            if (item == null) return list;
            list.Add(item);
            Resolve();
            string stock;
            ItemInfo stockItem;
            HashSet<int> ids;
            if (!_familyOf.TryGetValue(item.id, out stock) || !_stockItem.TryGetValue(stock, out stockItem) || stockItem.id != item.id
                || !_family.TryGetValue(stock, out ids))
                return list;
            foreach (int id in ids)
            {
                if (id == item.id) continue;
                ItemInfo s = AssetManager.Manager.getItemByID(id);
                if (s != null) list.Add(s);
            }
            return list;
        }

        /// <summary>the stock item for a skin, anything else unchanged (?bsave / ?short)</summary>
        public static ItemInfo StockOf(ItemInfo item)
        {
            if (item == null) return item;
            Resolve();
            ItemInfo plain;
            return _stockOfSkin.TryGetValue(item.id, out plain) ? plain : item;
        }

        /// <summary>the stock id a skin (or a skin's child projectile) is counted as in weapon stats</summary>
        public static int StatId(int id)
        {
            Resolve();
            int stock;
            return _statId.TryGetValue(id, out stock) ? stock : id;
        }

        /// <summary>on entering an arena: owners hear what they have and that skins are off until they turn them on</summary>
        public static void Greet(Player p, bool champOk, bool skinsOk = true)
        {
            if (p == null) return;
            Resolve();
            var owned = Owned(p._alias);
            var listed = owned.Where(kv => Listed(kv.Value)).ToList();
            if (listed.Count == 0) return;
            if (!skinsOk)
            {
                p.sendMessage(0, "*Item skins are off in this arena (a league match, or a ref's *skins off): standard items here. Your ?skin picks are kept.");
                return;
            }
            var on = listed.Select(kv => new { kv.Key, Cur = Current(kv.Key, kv.Value, p._alias, champOk, owned) }).Where(x => x.Cur != "normal").ToList();
            bool champ = listed.Any(kv => kv.Value.Any(o => o.Value.Champion));
            if (on.Count == 0)
                p.sendMessage(0, string.Format("*You have {0} skin{1}{2}, all off. ?skin shows them, ?skin on turns them all on.",
                    listed.Count, listed.Count == 1 ? "" : "s", champ ? " (champion items too)" : ""));
            else
                p.sendMessage(0, Clip("*Skins on: " + string.Join(", ", on.Select(x => Nick(x.Key) + " " + KeyOf(x.Key, listed.First(kv => kv.Key == x.Key).Value, p._alias, champOk, owned))) + ". ?skin to change."));
        }

        // ------------------------------------------------------------------ ?skin / ?cosmetic / ?champ
        public static void HandleCommand(Player p, string payload, bool champOk, bool skinsOk = true)
        {
            Resolve();
            var owned = Owned(p._alias);
            var listed = owned.Where(kv => Listed(kv.Value)).Select(kv => kv.Key).ToList();   // ?skin numbers = places in this list
            if (listed.Count == 0)
            {
                p.sendMessage(-1, "You don't own any item skins.");
                return;
            }
            string[] words = (payload ?? "").Trim().ToLower().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { List(p, owned, listed, listed, champOk, skinsOk, true); return; }
            if (words.Length == 1 && words[0] == "all") { List(p, owned, listed, listed, champOk, skinsOk, false); return; }

            string last = words[words.Length - 1];
            int num;
            var changed = new List<string>(); var notes = new List<string>();   // the reply: only what this command changed (Travis 10-27)
            Action<string, string> pick = (stock, key) =>
            {
                SetPick(p._alias, stock, key); if (!changed.Contains(stock)) changed.Add(stock);
                string leader = PullLeader(p._alias, owned, stock, key, notes);
                if (leader != null && listed.Contains(leader) && !changed.Contains(leader)) changed.Add(leader);
            };
            if (words.Length == 2 && (last == "on" || last == "off") && !int.TryParse(words[0], out num) && !listed.Any(s => Matches(s, words[0]))
                && listed.Any(s => HasOption(owned[s], words[0])))
            {   // ?skin s1 on / ?skin carebearmer off: an option with on / off (the option alone turns it on too)
                foreach (string s in listed.Where(s => HasOption(owned[s], words[0])))
                {
                    string key = owned[s].First(o => !IsFollower(o.Key) && IsOption(o.Key, words[0])).Key.Key;
                    if (last == "on") pick(s, key);
                    else if ((Pick(p._alias, s) ?? "off").Equals(key, StringComparison.OrdinalIgnoreCase)) pick(s, "off");
                }
                if (changed.Count == 0) { p.sendMessage(0, "&Nothing was on '" + words[0] + "'."); return; }
            }
            else if (words.Length == 1 && (last == "off" || last == "on"))
            {
                foreach (string stock in listed) SetPick(p._alias, stock, last == "off" ? "off" : FirstKey(owned[stock]));
                Saved(p, words, champOk, skinsOk);
                p.sendMessage(0, last == "off" ? "&All your item skins are OFF (saved): standard items. ?skin on turns them all back on."
                    : string.Format("&All {0} of your item skins are ON (saved). ?skin lists them, ?skin <#> off turns one off.", listed.Count));
                if (!skinsOk) p.sendMessage(0, "*Item skins are OFF in this arena right now (a league match, or a ref's *skins off): your picks are saved for other arenas.");
                return;
            }
            else if (int.TryParse(words[0], out num))
            {   // ?skin <#> [option|on|off]
                if (num < 1 || num > listed.Count) { p.sendMessage(-1, "No item skin #" + num + ". ?skin lists yours with their numbers."); return; }
                string stock = listed[num - 1]; var opts = owned[stock];
                string word = words.Length > 1 ? last : null;
                if (word == null)
                {   // alone: the next look, after the last one off (one look: on / off) - the shortest way through several skins
                    var keys = opts.Where(kv => !IsFollower(kv.Key)).Select(kv => kv.Key.Key).ToList(); keys.Add("off");
                    ItemInfo cur = Wanted(stock, opts, p._alias, champOk, owned);
                    string now = cur.id == _stockItem[stock].id ? "off" : opts.Where(kv => kv.Key.Item.id == cur.id).Select(kv => kv.Key.Key).FirstOrDefault() ?? "off";
                    word = keys[(keys.IndexOf(now) + 1) % keys.Count];
                }
                if (word == "off") pick(stock, "off");
                else if (word == "on") pick(stock, FirstKey(opts));
                else if (HasOption(opts, word)) pick(stock, opts.First(o => !IsFollower(o.Key) && IsOption(o.Key, word)).Key.Key);
                else
                {   // the option belongs to another item: say which one
                    var has = listed.Where(s => HasOption(owned[s], word)).ToList();
                    p.sendMessage(-1, string.Format("#{0} is {1}: it has no '{2}'.{3}", num, Nick(stock), word, has.Count == 0 ? " ?skin lists your options."
                        : string.Format(" '{0}' is on {1}: ?skin {2} {0}, or just ?skin {0}.", word, string.Join(", ", has.Select(s => (listed.IndexOf(s) + 1) + ") " + Nick(s))), listed.IndexOf(has[0]) + 1)));
                    return;
                }
            }
            else
            {
                string item = string.Join(" ", words.Take(words.Length - 1));
                // the items this applies to: the named ones, or every item that has the option
                var targets = listed.Where(s => item.Length == 0 ? HasOption(owned[s], last) : Matches(s, item)).ToList();
                if (item.Length == 0 && targets.Count == 0)
                {   // a lone item name: show its options
                    var named = listed.Where(s => Matches(s, last)).ToList();
                    if (named.Count == 1) { ListOne(p, owned, listed, named[0], champOk); return; }
                    if (named.Count > 1) { List(p, owned, listed, named, champOk, skinsOk, false); return; }
                }
                if (targets.Count == 0 || (last != "off" && last != "on" && !targets.Any(s => HasOption(owned[s], last))))
                {
                    p.sendMessage(-1, "Usage: ?skin = your list, ?skin on / ?skin off = all, ?skin <#> <option|on|off>, ?skin <option>.");
                    return;
                }
                foreach (string s in targets)
                {
                    if (last == "off") pick(s, "off");
                    else if (last == "on") pick(s, FirstKey(owned[s]));
                    else if (HasOption(owned[s], last)) pick(s, owned[s].First(o => !IsFollower(o.Key) && IsOption(o.Key, last)).Key.Key);
                }
            }
            Saved(p, words, champOk, skinsOk);
            // the reply: the changed item(s) only, not the whole list
            if (changed.Count <= 4)
                foreach (string s in changed) p.sendMessage(0, Clip(Line(p, owned, listed, s, champOk) + " (saved)"));
            else
                p.sendMessage(0, string.Format("&{0} item skins changed (saved). ?skin lists them.", changed.Count));
            foreach (string n in notes) p.sendMessage(0, Clip(n));
            if (!skinsOk) p.sendMessage(0, "*Item skins are OFF in this arena right now (a league match, or a ref's *skins off): your picks are saved for other arenas.");
        }

        /// <summary>A pick whose item needs another item to fire (the Golden Tranq needs the standard Needler, Subzero (Tranq) the Needler
        /// (SubZero)) sets that other item too, so the newest pick wins instead of the follower rule quietly swapping it back. Adds a note and
        /// returns the other item's stock name when its pick changed, else null.</summary>
        private static string PullLeader(string alias, Dictionary<string, List<KeyValuePair<Option, Set>>> owned, string stock, string key, List<string> notes)
        {
            List<KeyValuePair<Option, Set>> opts, lopts;
            if (!owned.TryGetValue(stock, out opts)) return null;
            ItemInfo item = key == "off" ? _stockItem[stock] : opts.Where(kv => kv.Key.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key.Item).FirstOrDefault();
            if (item == null) return null;
            int req = RequiredOf(item); string leader;
            if (req <= 0 || !_familyOf.TryGetValue(req, out leader) || leader.Equals(stock, StringComparison.OrdinalIgnoreCase) || !owned.TryGetValue(leader, out lopts)) return null;
            string want = req == _stockItem[leader].id ? "off" : lopts.Where(kv => kv.Key.Item.id == req && !IsFollower(kv.Key)).Select(kv => kv.Key.Key).FirstOrDefault();
            if (want == null) return null;
            if ((Pick(alias, leader) ?? "off").Equals(want, StringComparison.OrdinalIgnoreCase)) return null;
            SetPick(alias, leader, want);
            ItemInfo need = AssetManager.Manager.getItemByID(req);
            notes.Add(string.Format("~{0} switched to {1} too: {2} needs the {3}.", leader, want == "off" ? "normal" : want, item.name, need != null ? need.name : leader));
            return leader;
        }

        /// <summary>?champ [on|off]: only the champion options (alone: off when any is on, else on). False when the player owns none.</summary>
        public static bool HandleChampCommand(Player p, string payload, bool champOk, bool skinsOk = true)
        {
            Resolve();
            var owned = Owned(p._alias);
            var champ = owned.Where(kv => kv.Value.Any(o => o.Value.Champion && !IsFollower(o.Key))).Select(kv => kv.Key).ToList();
            if (champ.Count == 0) return false;
            string w = (payload ?? "").Trim().ToLower();
            Func<string, bool> champOn = s => { string pk = Pick(p._alias, s); return pk != null && owned[s].Any(o => o.Value.Champion && o.Key.Key.Equals(pk, StringComparison.OrdinalIgnoreCase)); };
            bool turnOn = w == "on" ? true : w == "off" ? false : !champ.Any(champOn);
            foreach (string s in champ)
            {
                if (turnOn) SetPick(p._alias, s, owned[s].First(o => o.Value.Champion && !IsFollower(o.Key)).Key.Key);
                else if (champOn(s)) SetPick(p._alias, s, "off");   // a personal skin picked on the same item stays
            }
            Saved(p, new[] { "champ", turnOn ? "on" : "off" }, champOk, skinsOk);
            p.sendMessage(0, turnOn ? string.Format("&Champion items ON (saved): {0} item{1}. ?skin lists them.", champ.Count, champ.Count == 1 ? "" : "s")
                                      + (champOk ? "" : " Champion items are off in this arena right now, so they show where they are allowed.")
                                    : "&Champion items OFF (saved): standard items. ?champ on to turn them back on.");
            return true;
        }

        private static void Saved(Player p, string[] words, bool champOk, bool skinsOk)
        {
            lock (_lock) SavePrefs();
            _told.Add(p._alias);
            Sweep(p, champOk, skinsOk);
            Console.WriteLine(string.Format("[skins] {0}: ?skin {1}", p._alias, string.Join(" ", words)));
        }

        /// <summary>the first option a player can pick for an item (on)</summary>
        private static string FirstKey(List<KeyValuePair<Option, Set>> opts)
        {
            var o = opts.FirstOrDefault(kv => !IsFollower(kv.Key));
            return o.Key != null ? o.Key.Key : "off";
        }

        /// <summary>the list (numbers = places in listed, so ?skin <#> always means the same item): the items in show - only those the player's
        /// class uses when byClass, then a line says how many other classes' items there are (Travis 10-27: not four beamers a class can't use)</summary>
        private static void List(Player p, Dictionary<string, List<KeyValuePair<Option, Set>>> owned, List<string> listed, List<string> show, bool champOk, bool skinsOk, bool byClass)
        {
            var mine = byClass ? show.Where(s => ClassUses(p, _stockItem[s])).ToList() : show;
            if (mine.Count == 0) mine = show;   // nothing for this class (or no class yet): show everything
            p.sendMessage(0, "&Your skins (saved). ?skin <#> = next look, ?skin <look>, ?skin <#> off, ?skin off = all off:");
            if (!skinsOk)
                p.sendMessage(0, "*Item skins are OFF in this arena right now (a league match, or a ref's *skins off): your picks are saved for other arenas.");
            foreach (string s in mine)
                p.sendMessage(0, Clip(Line(p, owned, listed, s, champOk)));
            int others = show.Count - mine.Count;
            if (others > 0)
                p.sendMessage(0, string.Format("~+{0} more for other classes: ?skin all", others));
        }

        private static void ListOne(Player p, Dictionary<string, List<KeyValuePair<Option, Set>>> owned, List<string> listed, string stock, bool champOk)
        {
            p.sendMessage(0, Clip(Line(p, owned, listed, stock, champOk)));
            p.sendMessage(0, string.Format("~?skin {0} = next look, ?skin {0} <look>, ?skin {0} off.", listed.IndexOf(stock) + 1));
        }

        /// <summary>"1) CAW: widow crab [rainbow] stream prism off": the item's number and short name, its looks, the one in use in brackets</summary>
        private static string Line(Player p, Dictionary<string, List<KeyValuePair<Option, Set>>> owned, List<string> listed, string stock, bool champOk)
        {
            var opts = owned[stock];
            string now = KeyOf(stock, opts, p._alias, champOk, owned);
            var keys = opts.Where(o => !IsFollower(o.Key)).Select(o => o.Key.Key).ToList(); keys.Add("off");
            bool champOff = !champOk && opts.Any(o => o.Value.Champion);
            return string.Format("*{0}) {1}: {2}{3}", listed.IndexOf(stock) + 1, Nick(stock), string.Join(" ", keys.Select(k => k == now ? "[" + k + "]" : k)),
                champOff ? "  (champion looks are off in this arena)" : "");
        }

        /// <summary>the look in use on an item: its option key, or off</summary>
        private static string KeyOf(string stock, List<KeyValuePair<Option, Set>> opts, string alias, bool champOk, Dictionary<string, List<KeyValuePair<Option, Set>>> owned)
        {
            ItemInfo cur = Wanted(stock, opts, alias, champOk, owned);
            if (cur.id == _stockItem[stock].id) return "off";
            return opts.Where(kv => kv.Key.Item.id == cur.id).Select(kv => kv.Key.Key).FirstOrDefault() ?? "off";
        }

        // short weapon names for the list (Travis 10-27: less clutter); anything not here shows its own name. ?skin <name> matches either.
        private static readonly Dictionary<string, string> ShortName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Kuchler A6 CAW", "CAW" }, { "Maklov AR mk 606", "Maklov AR" }, { "AssaultRifle", "Duel AR" }, { "SiG Arms m2 AS", "SiG" },
            { "Maklov AC mk2", "AC mk2" }, { "Unittech SC 99r", "SC 99r" }, { "Kuchler PC v2", "PC v2" }, { "Micro Missile Launcher", "Micro ML" },
            { "Mini Missile Launcher", "Mini ML" }, { "Maklov g9 Sniper", "g9 Sniper" },
        };
        private static string Nick(string stock) { string n; return ShortName.TryGetValue(stock, out n) ? n : stock; }

        private static bool IsOption(Option o, string word)
        {
            return o.Key.Equals(word, StringComparison.OrdinalIgnoreCase) || o.Item.name.Equals(word, StringComparison.OrdinalIgnoreCase);
        }
        private static bool HasOption(List<KeyValuePair<Option, Set>> opts, string word) { return opts.Any(o => !IsFollower(o.Key) && IsOption(o.Key, word)); }

        private static bool Matches(string stock, string word)
        {
            if (stock.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 || Nick(stock).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Catalogue.SelectMany(s => s.Items).Any(f => f.Stock.Equals(stock, StringComparison.OrdinalIgnoreCase) && f.Short.Any(n => n.Equals(word, StringComparison.OrdinalIgnoreCase)));
        }

        private static string Clip(string s) { return s.Length <= 230 ? s : s.Substring(0, 227) + "..."; }

        private static void SetPick(string alias, string stock, string pick)
        {
            lock (_lock)
            {
                LoadPrefs();
                Dictionary<string, string> mine;
                if (!_prefs.TryGetValue(alias, out mine)) _prefs[alias] = mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (pick == null || pick == "off") mine[stock] = "off"; else mine[stock] = pick;
            }
        }

        private static void LoadPrefs()
        {
            if (_prefs != null) return;
            _prefs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(PREFS_PATH)) return;
                foreach (string raw in File.ReadAllLines(PREFS_PATH))
                {
                    if (raw.StartsWith("#")) continue;
                    string[] f = raw.Split('\t');
                    if (f.Length < 3) continue;
                    Dictionary<string, string> mine;
                    if (!_prefs.TryGetValue(f[0].Trim(), out mine)) _prefs[f[0].Trim()] = mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    mine[f[1].Trim()] = f[2].Trim().ToLower();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("SkinPrefs.txt not read: " + ex.Message);
            }
        }

        private static void SavePrefs()
        {
            try
            {
                var lines = new List<string> { "# alias<TAB>stock item<TAB>option|off - written by the zone (?skin / ?cosmetic / ?champ). Do not push from local." };
                foreach (var a in _prefs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    foreach (var s in a.Value.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                        lines.Add(a.Key + "\t" + s.Key + "\t" + s.Value);
                File.WriteAllLines(PREFS_PATH, lines);
            }
            catch (Exception ex)
            {
                Console.WriteLine("SkinPrefs.txt not written: " + ex.Message);
            }
        }
    }
}
/* PARSER GUARD: keep a block comment last in this file (> 32 KB). The zone's ScriptParser stops seeing // comments past char 32767 when no
   block comment follows them, then mis-pairs quotes and the zone fails to boot (memory ctf-script-parser-32k-trap). */
