using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>player/trade.ts.</summary>
    public sealed class Trade
    {
        sealed class OfferedItem
        {
            public double inventoryIndex;
            public double maxCount; // The amount of the item in the inventory slot.
            public Item item;
        }

        // The items that the player is offering to the other player (use the other player's `itemOffered` to get the items exchanged.)
        readonly JsObject<OfferedItem> itemsOffered = new();

        public string lastRequest = ""; // The last person who requested to trade with the player.
        public Player activeTrade; // The player who we are currently trading with.
        public bool accepted;
        public bool inProgress; // Extra safety to prevent duplicate trades.

        public Action<string> openCallback;
        public Action<string, double, double, string> addCallback;
        public Action<string, double> removeCallback;
        public Action<string> acceptCallback; // Send the instance of who is accepting the trade.

        readonly Player player;

        public Trade(Player player)
        {
            this.player = player;

            // Create an empty items offered container mock.
            for (var i = 0; i < player.inventory.size; i++) itemsOffered.Set(i.ToString(), null);
        }

        public void Add(double index, double count = 1)
        {
            // An item is deemed added when all of its counts have been offered.
            if (IsAdded(index)) return;

            // In case of client-side packet manipulation.
            if (double.IsNaN(count) || count < 1) return;

            var offerIndex = GetEmptySlot();

            if (offerIndex == -1)
            {
                player.Notify("misc:CANNOT_ADD_ITEMS_TRADE", "", "TRADE");
                return;
            }

            // Grab the slot from the inventory.
            var slot = player.inventory.Get(index);

            // Ensure the slot exists and is not empty.
            if (slot.IsEmpty()) return;

            // Create an item instance and make necessary changes to it.
            var item = player.inventory.GetItem(slot);

            // Undroppable items are special items that cannot be traded.
            if (item.undroppable)
            {
                player.Notify("misc:CANNOT_TRADE_ITEM", "", "TRADE");
                return;
            }

            // Handle existing items in the trade.
            var existingIndex = HasItem(slot.key);

            if (existingIndex == -1) item.count = count > slot.count ? slot.count : count;
            else
            {
                var offer = itemsOffered[JsString.Of(existingIndex)];

                if (offer.inventoryIndex == index && item.stackable)
                    item.count = offer.item.count + count > offer.maxCount ? offer.maxCount : offer.item.count + count;
                else existingIndex = -1;
            }

            // The index at which we are adding the item.
            var trueIndex = existingIndex == -1 ? offerIndex : existingIndex;

            // Add the item to the offered array or update existing item.
            itemsOffered.Set(JsString.Of(trueIndex), new OfferedItem { inventoryIndex = index, maxCount = slot.count, item = item });

            // Any addition or removal of an item resets the trade acceptance.
            accepted = false;
            (GetActiveTrade() ?? throw new JsTypeError("Cannot set properties of undefined (setting 'accepted')")).accepted = false;

            // Callbacks for both instances of the trade.
            SignalAdd(player.instance, trueIndex, item.count, item.key);
        }

        void SignalAdd(string instance, double offerIndex, double count, string key)
        {
            addCallback?.Invoke(instance, offerIndex, count, key);
            GetActiveTrade()?.addCallback?.Invoke(instance, offerIndex, count, key);
        }

        public void Remove(double index)
        {
            var key = JsString.Of(index);

            if (itemsOffered[key] == null) return;

            itemsOffered.Set(key, null);

            // Any addition or removal of an item resets the trade acceptance.
            accepted = false;
            (GetActiveTrade() ?? throw new JsTypeError("Cannot set properties of undefined (setting 'accepted')")).accepted = false;

            // Send the callback to both trade instances.
            removeCallback?.Invoke(player.instance, index);
            GetActiveTrade()?.removeCallback?.Invoke(player.instance, index);
        }

        public void Accept()
        {
            // If the other player has already accepted the trade, then we can do the exchange.
            if (GetActiveTrade()?.accepted ?? false)
            {
                // A trade is marked in progress as soon as it passes majority of checks and the exchange begins.
                if (inProgress) return;

                var emptySlots = player.inventory.GetEmptySlots();
                var otherPlayerEmptySlots = activeTrade?.inventory.GetEmptySlots();
                var offeredCount = GetTotalOfferedCount();
                var otherOfferedCount = GetActiveTrade()?.GetTotalOfferedCount();

                // Ensure we have some number we're working with for empty slots.
                if (otherPlayerEmptySlots == null || otherOfferedCount == null)
                {
                    accepted = false;
                    return;
                }

                // Positive diff is we're offering more than other player, negative diff is we're being offered more.
                var diff = offeredCount - otherOfferedCount.Value;

                // The difference indicates who is geting more items than they have available spaces
                if (diff != emptySlots)
                {
                    if (diff < 0 && emptySlots < Math.Abs(diff))
                    {
                        NotEnoughSpace();
                        return;
                    }

                    if (diff > 0 && otherPlayerEmptySlots < diff)
                    {
                        GetActiveTrade()?.NotEnoughSpace();
                        return;
                    }
                }

                Exchange();
                return;
            }

            // Set the trade to accepted.
            accepted = true;

            // Relay to the client that one of the parties accepted the trade.
            acceptCallback?.Invoke("misc:ACCEPTED_TRADE");
            GetActiveTrade()?.acceptCallback?.Invoke("misc:ACCEPTED_TRADE_OTHER");
        }

        public void Request(Player target)
        {
            // Player is too far away to start trading.
            if (player.GetDistance(target) > 1) return;

            // Prevent hollow admins from trading.
            if (player.IsHollowAdmin())
            {
                player.Notify("misc:CANNOT_TRADE_ADMIN");
                return;
            }

            if (target.IsHollowAdmin())
            {
                player.Notify("misc:CANNOT_TRADE_ADMIN_OTHER");
                return;
            }

            // Prevent cheaters from trading.
            if (player.IsCheater())
            {
                player.Notify("misc:CANNOT_TRADE_CHEATER");
                return;
            }

            if (target.IsCheater())
            {
                player.Notify("misc:CANNOT_TRADE_CHEATER_OTHER");
                return;
            }

            if (target.trade.lastRequest == player.instance)
            {
                Open(target);
                return;
            }

            lastRequest = target.instance;

            target.Notify($"misc:TRADE_REQUEST_OTHER;username={Utils.FormatName(player.username)}", "rgb(84, 224, 255)", "TRADE");

            player.Notify($"misc:TRADE_REQUEST;username={Utils.FormatName(target.username)}", "", "TRADE");
        }

        public void Open(Player target)
        {
            Log.Debug($"Opening trade between {player.username} and {target.username}.");

            openCallback?.Invoke(target.instance);
            target.trade.openCallback?.Invoke(player.instance);

            // Set the active trade for both players.
            player.trade.activeTrade = target;
            target.trade.activeTrade = player;

            // Remove the last request for both players.
            lastRequest = "";
            target.trade.lastRequest = "";
        }

        public void Close()
        {
            if (activeTrade == null) return;

            Log.Debug($"Closing trade between {player.username} and {activeTrade?.username}.");

            // Close the trade for the player.
            player.Send(new TradePacket(Opcodes.Trade.Close, new JsObject()));

            // Close the trade for the other party if they are still trading.
            activeTrade?.Send(new TradePacket(Opcodes.Trade.Close, new JsObject()));

            // Clear the active trade for both players.
            GetActiveTrade()?.Clear(); // Clear first so it's not undefined.
            Clear();
        }

        void NotEnoughSpace()
        {
            // Reset the accepted status.
            accepted = false;
            (GetActiveTrade() ?? throw new JsTypeError("Cannot set properties of undefined (setting 'accepted')")).accepted = false;

            // Clear the trade for both players.
            acceptCallback?.Invoke(null);
            GetActiveTrade()?.acceptCallback?.Invoke(null);

            activeTrade?.Notify("misc:NO_SPACE_OTHER", "", "TRADE");

            player.Notify("misc:NO_SPACE", "", "TRADE");
        }

        void Exchange()
        {
            // Set the trade to in progress.
            inProgress = true;

            // Remove the items from both players' inventories.
            var flagged = RemoveItemsBeforeTrade();

            if (flagged)
            {
                player.Notify("misc:PLEASE_REPORT_BUG", "", "TRADE");
                Log.Warning($"Trade exchange failed for {player.username} and {activeTrade?.username}.");
                Close();
                return;
            }

            // Add the items to both players' inventories (we also store total items added for memes).
            var totalItems = AddItemsAfterRemoving();

            // This is just a mini easter egg I like to include with basically everything at this point.
            if (totalItems == 0)
            {
                player.Notify("misc:TRADE_EMPTY", "", "TRADE");
                activeTrade?.Notify("misc:TRADE_EMPTY", "", "TRADE");
            }
            else
            {
                // Notify both that the trade is complete.
                player.Notify("misc:TRADE_COMPLETE", "", "TRADE");
                activeTrade?.Notify("misc:TRADE_COMPLETE", "", "TRADE");

                Log.Trade(ProduceTradeLog());
            }

            Close();
        }

        string ProduceTradeLog()
        {
            var offered = new JsObject<double?>();
            var received = new JsObject<double?>();

            ForEachOfferedItem((item, _) =>
                offered.Set(item.key, (offered[item.key] ?? 0) + (item.stackable ? item.count : 1)));

            GetActiveTrade()?.ForEachOfferedItem((item, _) =>
                received.Set(item.key, (received[item.key] ?? 0) + (item.stackable ? item.count : 1)));

            var offeredItems = string.Join(",", offered.Entries.Select(pair => $" {JsString.Of(pair.Value ?? double.NaN)} {pair.Key}(s)"));
            var receivedItems = string.Join(",", received.Entries.Select(pair => $" {JsString.Of(pair.Value ?? double.NaN)} {pair.Key}(s)"));

            return $"Player {player.username} traded{offeredItems} for{receivedItems} with player {activeTrade?.username}";
        }

        bool RemoveItemsBeforeTrade()
        {
            var flagged = false;

            // Remove the offered items from our player's inventory
            ForEachOfferedItem((item, inventoryIndex) =>
            {
                // Stop everything if flagged.
                if (flagged) return;

                // Ensure that we are dealing with items that exist.
                if (!player.inventory.HasItem(item.key, item.count))
                {
                    flagged = true;
                    return;
                }

                player.inventory.Remove(inventoryIndex, item.count);
            });

            // Remove the offered items from the other player's inventory.
            GetActiveTrade()?.ForEachOfferedItem((item, inventoryIndex) =>
            {
                // Stop everything if flagged.
                if (flagged) return;

                // Ensure we are dealing with items that exist.
                if (!(activeTrade?.inventory.HasItem(item.key, item.count) ?? false))
                {
                    flagged = true;
                    return;
                }

                activeTrade?.inventory.Remove(inventoryIndex, item.count);
            });

            return flagged;
        }

        double AddItemsAfterRemoving()
        {
            double totalItems = 0;

            // Adds the items from the other player's offers into our own inventory.
            GetActiveTrade()?.ForEachOfferedItem((item, _) =>
            {
                player.inventory.Add(item);
                totalItems++;
            });

            // Adds the items from our offers into the other player's inventory.
            ForEachOfferedItem((item, _) =>
            {
                activeTrade.inventory.Add(item);
                totalItems++;
            });

            return totalItems;
        }

        public void Clear()
        {
            activeTrade = null;
            accepted = false;
            inProgress = false;

            // Clear the items offered array and set them back to undefined.
            foreach (var pair in itemsOffered) itemsOffered.Set(pair.Key, null);
        }

        double GetEmptySlot()
        {
            // Try to find an index that is empty.
            foreach (var pair in itemsOffered)
                if (pair.Value == null)
                    return JsString.ParseInt(pair.Key);

            return -1;
        }

        public double GetTotalOfferedCount()
        {
            double count = 0;

            foreach (var pair in itemsOffered)
                if (pair.Value != null)
                    count++;

            return count;
        }

        public Trade GetActiveTrade() => activeTrade?.trade;

        double HasItem(string key)
        {
            foreach (var pair in itemsOffered)
                if (pair.Value != null && pair.Value.item.key == key)
                    return JsString.ParseInt(pair.Key);

            return -1;
        }

        bool IsAdded(double index) =>
            itemsOffered.Values.Any(offer => offer != null && offer.inventoryIndex == index && offer.item.count == offer.maxCount);

        void ForEachOfferedItem(Action<Item, double> callback)
        {
            foreach (var offeredItem in itemsOffered.Values)
                if (offeredItem?.item != null)
                    callback(offeredItem.item, offeredItem.inventoryIndex);
        }

        public void OnOpen(Action<string> callback) => openCallback = callback;

        public void OnAdd(Action<string, double, double, string> callback) => addCallback = callback;

        public void OnRemove(Action<string, double> callback) => removeCallback = callback;

        public void OnAccept(Action<string> callback) => acceptCallback = callback;
    }
}
