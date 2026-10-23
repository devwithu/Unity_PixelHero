using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Controllers
{
    /// <summary>controllers/stores.ts.</summary>
    public sealed class Stores
    {
        sealed class StoreInfo
        {
            public List<Item> items;
            public double refresh;
            public string currency;
            public List<string> allowedItems;
            public bool restricted;
            public double lastUpdate;
        }

        readonly JsObject<StoreInfo> stores = new(); // Key is a string representing the store's name in `stores.json`.
        readonly double updateFrequency = Constants.STORE_UPDATE_FREQUENCY;

        readonly World world;

        public Stores(World world)
        {
            this.world = world;

            // Load stores from the JSON.
            foreach (var pair in ServerData.Stores) Load((JsObject)pair.Value, pair.Key);

            // Set up an interval for refreshing the store data.
            Node.SetInterval(Update, updateFrequency);

            var size = stores.Count;
            Log.Info($"Loaded {size} shop{(size > 1 ? "s" : "")}.");
        }

        void Load(JsObject store, string key)
        {
            var items = new List<Item>();

            foreach (JsObject item in store.Arr("items"))
            {
                var itemKey = item.Str("key");
                var count = item.Num("count");
                var price = item["price"];
                var stockAmount = item["stockAmount"];

                // Skip if an item already exists with the same key.
                if (items.Any(existing => existing.key == itemKey))
                {
                    Log.Warning($"{I18n.T("store:WARNING_DUPLICATE")}'{itemKey}'.");
                    continue;
                }

                var storeItem = new Item(itemKey, -1, -1, false, count ?? -1);

                // Assign price if provided, otherwise use default item price.
                if (JsData.Truthy(price)) storeItem.price = (double)price;

                // Stocking amount and max amount of the item in store.
                storeItem.stockAmount = JsData.Truthy(stockAmount) ? (double)stockAmount : 1;
                storeItem.maxCount = count ?? double.NaN;

                items.Add(storeItem);
            }

            stores.Set(key, new StoreInfo
            {
                items = items,
                refresh = store.Num("refresh") ?? double.NaN,
                currency = store.Str("currency"),
                restricted = JsData.Truthy(store["restricted"]),
                lastUpdate = Node.DateNow()
            });
        }

        void Update()
        {
            foreach (var pair in stores)
            {
                var store = pair.Value;

                if (!CanRefresh(store)) continue;

                StockItems(store);

                UpdatePlayers(pair.Key);

                store.lastUpdate = Node.DateNow();
            }
        }

        static void StockItems(StoreInfo store)
        {
            foreach (var item in store.items)
            {
                if (item.count == -1 || item.count >= item.maxCount) continue;

                // If an alternate optional stock count is provided, increment by that amount.
                if (item.stockAmount is double amount && amount != 0 && !double.IsNaN(amount)) item.count += amount;
            }
        }

        void UpdatePlayers(string key) =>
            world.entities.ForEachPlayer(player =>
            {
                if (player.storeOpen != key) return;

                // Update packet to players with the store open.
                player.Send(new StorePacket(Opcodes.Store.Update, Serialize(key)));
            });

        public void Open(Player player, NPC npc)
        {
            var store = GetStore(npc);

            if (store == null)
            {
                Log.Debug($"[{player.username}] {I18n.T("store:INVALID_STORE")}.");
                return;
            }

            player.Send(new StorePacket(Opcodes.Store.Open, Serialize(npc.store)));

            player.storeOpen = npc.store;
        }

        public void Purchase(Player player, string storeKey, double index, double count = 1)
        {
            if (!VerifyStore(player, storeKey)) return;

            var store = stores[storeKey];
            var item = index >= 0 && index < store.items.Count && index % 1 == 0 ? store.items[(int)index] : null;

            // Prevent hollow admins from buying any of the items.
            if (player.IsHollowAdmin())
            {
                player.Notify("store:HOLLOW_ADMIN");
                return;
            }

            // Prevent cheaters from buying any of the items.
            if (player.IsCheater())
            {
                player.Notify("store:CHEATER");
                return;
            }

            // First and foremost check the user has enough space.
            if (!player.inventory.HasSpace() &&
                !player.inventory.HasItem((item ?? throw new JsTypeError("Cannot read properties of undefined (reading 'key')")).key))
            {
                player.Notify("store:NOT_ENOUGH_SPACE");
                return;
            }

            // Check if item exists
            if (item == null)
            {
                Log.Error($"{player.username} {I18n.T("store:PURCHASE_INVALID_STORE")}{storeKey}.");
                return;
            }

            if (item.count != -1)
            {
                if (item.count < 1)
                {
                    player.Notify("store:ITEM_OUT_OF_STOCK");
                    return;
                }

                // Prevent buying more than store has stock. Default to max stock.
                count = item.count < count ? item.count : count;
            }

            // Find total price of item by multiplying count against price.
            var currency = player.inventory.GetIndex(store.currency, item.price * count);

            // If no inventory slot index with currency is found, stop the purchase.
            if (currency < 0)
            {
                player.Notify("store:NOT_ENOUGH_CURRENCY");
                return;
            }

            // Clone the item we are adding
            var itemToAdd = item.Copy();
            itemToAdd.count = count;

            // Add the item to the player's inventory.
            var amount = player.inventory.Add(itemToAdd);

            if (amount < 1) return;

            if (item.count > 0)
            {
                // Decrement the item count by the amount we are buying.
                item.count -= amount;

                // Remove item from store if it is out of stock and not original to the store.
                if (item.count < 1 && IsOriginalItem(storeKey, item.key))
                    store.items = store.items.Where(storeItem => storeItem.key != item.key).ToList();
            }

            player.inventory.Remove(currency, item.price * amount);

            Log.Stores($"Player {player.username} purchased {JsString.Of(amount)} {item.key} for {JsString.Of(item.price * amount)} {store.currency}.");

            // Sync up new store data to all players.
            UpdatePlayers(storeKey);
        }

        public void Sell(Player player, string key, double index, double count = 1)
        {
            if (!VerifyStore(player, key)) return;

            // Ensure the count is correct.
            if (count < 1)
            {
                player.Notify("store:INVALID_ITEM_COUNT");
                return;
            }

            var slot = player.inventory.Get(index);

            // Ensure the item in the slot exists.
            if (slot.IsEmpty())
            {
                Log.Warning($"[{player.username}] {I18n.T("store:INVALID_ITEM_SELECTION")}");
                return;
            }

            var store = stores[key];

            // If the store has a list of allowed items, ensure the item is allowed.
            if (store.allowedItems != null && !store.allowedItems.Contains(slot.key))
            {
                player.Notify("store:RESTRICTED_ITEM");
                return;
            }

            // Disable selling in restricted stores.
            if (store.restricted)
            {
                player.Notify("store:RESTRICTED_STORE");
                return;
            }

            if (slot.key == store.currency)
            {
                player.Notify("store:CANNOT_SELL_ITEM");
                return;
            }

            // Temporary fix until we have a more suitable UI.
            count = slot.count;

            // Find the item in the store if it exists.
            var item = player.inventory.GetItem(slot);
            var storeItem = store.items.Find(i => i.key == slot.key);
            var price = storeItem != null && storeItem.price != 0 && !double.IsNaN(storeItem.price) ? storeItem.price : item.price; // Price of the item being sold.
            var totalCoins = GetTotalCost(count, price, storeItem?.count); // Amount of coins the player will be receiving.

            // Total amount of coins is invalid, this shouldn't technically happen.
            if (totalCoins < 0)
            {
                player.Notify("store:CANNOT_SELL_ITEM");
                return;
            }

            player.inventory.Remove(index, count);

            // Very weird if this somehow happened at this point in the code, I'd be curious to see how.
            if (player.inventory.Add(GetCurrency(store.currency, totalCoins)) < 1)
            {
                player.Notify("store:NOT_ENOUGH_CURRENCY");
                return;
            }

            // Increment the item count or add to store only if the player isn't a cheater :)
            if (!player.IsCheater() && !player.IsHollowAdmin())
            {
                if (storeItem == null || storeItem.count == 0 || double.IsNaN(storeItem.count)) store.items.Add(item);
                else if (storeItem.count != -1) storeItem.count += count;
            }

            // Sync up new store data to all players.
            UpdatePlayers(key);
        }

        public void Select(Player player, string key, double index, double count = 1)
        {
            if (!VerifyStore(player, key) || double.IsNaN(index)) return;

            // Grab the inventory slot at the specified index.
            var slot = player.inventory.Get(index);

            // This shouldn't get called unless there is a bug or client was messed with.
            if (slot.IsEmpty())
            {
                Log.Warning($"[{player.username}] {I18n.T("store:INVALID_ITEM_SELECTION")}");
                return;
            }

            var store = stores[key];

            // Check that the player isn't trying to sell the currency to the store.
            if (slot.key == store.currency)
            {
                player.Notify("store:CANNOT_SELL_ITEM");
                return;
            }

            // Temporary fix until we have a more suitable UI.
            count = slot.count;

            // Create an instance of an item and try to check if that item exists in the store.
            var item = player.inventory.GetItem(slot);
            var storeItem = store.items.Find(i => i.key == slot.key);
            var price = storeItem != null && storeItem.price != 0 && !double.IsNaN(storeItem.price) ? storeItem.price : item.price; // Price of the item being sold.
            var totalCoins = GetTotalCost(count, price, storeItem?.count); // Amount of coins the player will be receiving.

            // An invalid amount of coins was calculated, this shouldn't happen.
            if (totalCoins < 1)
            {
                player.Notify("store:CANNOT_SELL_ITEM");
                return;
            }

            // Invalid price, this shouldn't happen.
            if (double.IsNaN(totalCoins))
            {
                Log.Error("Malformed pricing for item selection.");
                return;
            }

            Log.Stores($"Player {player.username} sold {JsString.Of(count)} {item.key} for {JsString.Of(item.price * count)} {store.currency}.");

            // Create the select packet for the client to process and move the item into the slot.
            player.Send(new StorePacket(Opcodes.Store.Select, new JsObject(
                "key", key,
                "item", new JsObject(
                    "key", item.key,
                    "name", item.name,
                    "count", count,
                    "price", totalCoins,
                    "index", index))));
        }

        static bool CanRefresh(StoreInfo store) =>
            Node.DateNow() - (store.lastUpdate != 0 && !double.IsNaN(store.lastUpdate) ? store.lastUpdate : 0) > store.refresh;

        bool VerifyStore(Player player, string storeKey)
        {
            if (player.storeOpen != storeKey)
            {
                Log.Warning($"[{player.username}] {I18n.T("store:ACTION_STORE_NOT_OPEN")}");
                return false;
            }

            var store = storeKey == null ? null : stores[storeKey];

            // Check if store exists.
            if (store == null)
            {
                Log.Warning($"Player {player.username} tried to refresh a non-existent store with ID: {storeKey}.");
                return false;
            }

            return true;
        }

        public double GetPrice(string storeKey, string itemKey)
        {
            var store = stores[storeKey];

            // This should absolutely not happen.
            if (store == null) return -1;

            var item = store.items.Find(i => i.key == itemKey);

            if (item == null) return 1;

            return item.price;
        }

        StoreInfo GetStore(NPC npc) => npc.store != null && stores.Has(npc.store) ? stores[npc.store] : null;

        public Item GetCurrency(string key, double count) => new(key, -1, -1, false, count);

        static double GetTotalCost(double count, double price, double? storeCount = null, double limit = 10)
        {
            var store = storeCount ?? 0;
            double totalCost = 0;

            // If there is more than 10 items then the total cost is 20% of the price.
            if (store > limit) return 0.2 * price * count;

            // Used to store value limits.
            var remaining = count;

            // Iterate through the items and calculate the cost of each item.
            for (var i = 0; i < count; i++)
            {
                // Break after the tenth item since we'll reach the limit.
                if (i > limit) break;

                totalCost += (50 - 3 * JsMath.Min(store + i, limit)) / 100 * price;
                remaining--;
            }

            // Add the remaining items at 20% of the price.
            totalCost += 0.2 * price * remaining;

            return Math.Floor(totalCost);
        }

        public bool IsOriginalItem(string storeKey, string itemKey)
        {
            var store = ServerData.Stores.Obj(storeKey);

            if (store == null) return false;

            // Return if the itemKey exists in the store's original stock.
            return !store.Arr("items").Any(item => ((JsObject)item).Str("key") == itemKey);
        }

        public JsObject Serialize(string key)
        {
            var store = stores[key];
            var items = new List<object>();

            // Extract all the items from the store.
            foreach (var item in store.items)
                items.Add(new JsObject(
                    "key", item.key,
                    "name", item.name,
                    "count", item.count,
                    "price", item.price));

            return new JsObject("key", key, "items", items, "currency", store.currency);
        }
    }
}
