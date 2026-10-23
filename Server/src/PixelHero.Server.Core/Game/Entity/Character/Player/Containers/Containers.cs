using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>player/containers/slot.ts.</summary>
    public sealed class Slot
    {
        public bool edible;
        public bool interactable;
        public bool equippable;
        public string name = "";
        public string description = "";
        public JsObject attackStats = Utils.GetEmptyStats();
        public JsObject defenseStats = Utils.GetEmptyStats();
        public JsObject bonuses = Utils.GetEmptyBonuses();

        // Max amount of an item we can put in a slot.
        public double maxStackSize = Constants.MAX_STACK;

        public double index;
        public string key;
        public double count;
        public JsObject enchantments;

        public Slot(double index, string key = "", double count = -1, JsObject enchantments = null)
        {
            this.index = index;
            this.key = key;
            this.count = count;
            this.enchantments = enchantments ?? new JsObject();
        }

        /// <summary>update(item, stackSize = item.maxStackSize).</summary>
        public void Update(Item item, double? stackSize = null)
        {
            var size = stackSize ?? item.maxStackSize;

            if (!item.exists)
            {
                Log.Info($"Item doesn't exist: {item.key}, deleting from existence.");
                return;
            }

            key = item.key;
            count = JsMath.Min(item.count, size);
            enchantments = item.enchantments;
            edible = item.edible;
            interactable = item.interactable;
            equippable = item.IsEquippable();
            name = item.name;
            description = item.description;
            attackStats = item.attackStats;
            defenseStats = item.defenseStats;
            bonuses = item.bonuses;
            maxStackSize = size;

            if (count < 1) Log.Error("Updating slot with count less than 1:", item.key);

            item.Despawn(true); // Despawn signal towards the item once we update the slot.
        }

        public double Add(double amount)
        {
            count = JsMath.Min((count > 0 ? count : 0) + amount, maxStackSize);

            return count;
        }

        public double Remove(double amount)
        {
            if (IsEmpty(count - amount)) return count;

            count -= amount;

            return amount;
        }

        public void Clear()
        {
            key = "";
            count = -1;
            enchantments = new JsObject();
            edible = false;
            interactable = false;
            equippable = false;
            maxStackSize = Constants.MAX_STACK;
            name = "";
            description = "";
            attackStats = Utils.GetEmptyStats();
            defenseStats = Utils.GetEmptyStats();
            bonuses = Utils.GetEmptyBonuses();
        }

        public bool IsFull(double? count = null) => (count ?? this.count) > maxStackSize;

        public bool IsEmpty(double? count = null) => (count ?? this.count) < 1;

        public bool IsShard() => key.Contains("shardt", StringComparison.Ordinal);

        public bool IsEnchanted() => enchantments.Count > 0;

        public JsObject Serialize(bool clientInfo = false)
        {
            // Base data that is always sent to the client.
            var data = new JsObject(
                "index", index,
                "key", key,
                "count", count,
                "enchantments", enchantments);

            if (clientInfo)
            {
                data.Set("name", name);
                data.Set("description", description);
                data.Set("edible", edible);
                data.Set("interactable", interactable);
                data.Set("equippable", equippable);
                data.Set("attackStats", attackStats);
                data.Set("defenseStats", defenseStats);
                data.Set("bonuses", bonuses);
            }

            return data;
        }

        /// <summary>
        /// JSON.stringify(slot) of the class instance: esbuild defines the class fields first, then assigns the
        /// constructor's parameter properties, so the fields come before index, key, count and enchantments.
        /// </summary>
        public JsObject ToJson() => new(
            "edible", edible,
            "interactable", interactable,
            "equippable", equippable,
            "name", name,
            "description", description,
            "attackStats", attackStats,
            "defenseStats", defenseStats,
            "bonuses", bonuses,
            "maxStackSize", maxStackSize,
            "index", index,
            "key", key,
            "count", count,
            "enchantments", enchantments);
    }

    /// <summary>player/containers/container.ts.</summary>
    public abstract class Container
    {
        protected List<Slot> slots = new();
        protected double? stackSize;

        public Action loadCallback;
        protected Action<Slot> addCallback;
        protected Action<Slot, string, double, JsObject, bool> removeCallback;
        protected Action<string> notifyCallback;

        public Modules.ContainerType type;
        public double size;

        protected Container(Modules.ContainerType type, double size)
        {
            this.type = type;
            this.size = size;

            // Create `size` amount of slots with empty data.
            for (var i = 0; i < size; i++) slots.Add(new Slot(i));
        }

        /// <summary>A slot by index, or null (undefined) for anything that is not an index of the array.</summary>
        protected Slot At(double index) =>
            index >= 0 && index < slots.Count && index % 1 == 0 ? slots[(int)index] : null;

        public void Load(List<object> items)
        {
            foreach (var entry in items)
            {
                var item = entry as JsObject;

                // Create a new item instance so that the item's data is created.
                if (!JsData.Truthy(item?["key"])) continue;

                if ((item.Num("count") ?? double.NaN) < 1) item.Set("count", 1.0);

                var slot = At(item.Num("index") ?? double.NaN) ??
                           throw new JsTypeError("Cannot read properties of undefined (reading 'update')");

                slot.Update(GetItem(item.Str("key"), item.Num("count") ?? double.NaN, item.Obj("enchantments")), stackSize);
            }

            loadCallback?.Invoke();
        }

        public void Empty() =>
            ForEachSlot(slot =>
            {
                if (JsData.Truthy(slot.key)) Remove(slot.index, slot.count);
            });

        public virtual double Add(Item item)
        {
            var itemCount = item.count;
            var slot = Find(item);
            double total = 0;

            // If the slot exists...
            if (slot != null)
            {
                var itemCopy = item.Copy();
                var slotCount = slot.count;

                // Add the items in the slot to the item to be added
                if (slot.count > 0) itemCopy.count += slot.count;

                // Update the slot with the new item count
                slot.Update(itemCopy, stackSize);

                // Slot update failed, we can't add the item
                if (slot.IsEmpty()) return -2;

                // Set the total to the new item count
                total += slot.count;

                if (slotCount > 0) total -= slotCount;

                // If the total count is less than the count of the item to be added...
                if (total < itemCount)
                {
                    // Subtract the total from the item count
                    itemCopy.count = itemCount - total;

                    // Add the item to the slot, and store the amount added
                    var amount = Add(itemCopy);

                    // Add the amount to the total
                    if (amount > 0) total += amount;
                }
            }

            // Call the add callback with the slot
            if (total > 0) addCallback?.Invoke(slot);

            // Return the total amount
            return total;
        }

        public virtual JsObject Remove(double index, double count = 1, bool drop = false)
        {
            var slot = At(index);

            if (!JsData.Truthy(slot?.key)) return null;

            count = JsMath.Min(count, slot.count);

            var serializedSlot = slot.Serialize();

            if (count < slot.count) slot.Remove(count);
            else slot.Clear();

            removeCallback?.Invoke(slot, serializedSlot.Str("key"), count, serializedSlot.Obj("enchantments"), drop);

            return serializedSlot;
        }

        public void RemoveItem(string key, double count = 1)
        {
            var index = GetIndex(key, count);

            if (index == -1)
            {
                double removed = 0;

                // Iterate through each slot and exhaust the count we are removing.
                ForEachSlot(slot =>
                {
                    // Skip slots if key doesn't match or we've exhausted the removal.
                    if (slot.key != key || removed >= count) return;

                    // Remove the maximum amount possible.
                    var removeCount = count - removed > slot.count ? slot.count : count - removed;

                    // Increment the removed amount.
                    removed += removeCount;

                    // Remove from the slot.
                    Remove(slot.index, removeCount);
                });
            }
            else Remove(index, count);
        }

        public void Move(double fromIndex, Container toContainer, double? toIndex = null)
        {
            if (Get(fromIndex).IsEmpty()) return;

            var item = GetItem(Get(fromIndex));
            var stackSize = this.stackSize is double s && s != 0 ? s : item.maxStackSize;

            // Prevent an item from being moved if it exceeds the max stack size.
            if (item.count > stackSize) item.count = stackSize;

            Swap(fromIndex, toContainer, toIndex);
        }

        public void Swap(double fromIndex, Container toContainer, double? toIndex = null)
        {
            // Return if the source and target containers are the same and the index is the same.
            if (type == toContainer.type && fromIndex == toIndex) return;

            var fromSlot = Get(fromIndex);

            if (fromSlot.IsEmpty()) return;

            var fromItem = GetItem(fromSlot);
            var fromStackSize = stackSize is double fs && fs != 0 ? fs : fromItem.maxStackSize;

            // If the target slot is undefined, move the item to the next available slot in the container.
            if (toIndex == null)
            {
                fromItem.count = 1;

                // Attempt to add the item to the container
                var amount = toContainer.Add(fromItem);

                // Remove the item from the source container if the item was added.
                if (amount > 0) Remove(fromIndex, 1);
            }
            else
            {
                // Get the target slot.
                var toSlot = toContainer.Get(toIndex.Value);

                // Check if the target slot is empty.
                var toEmpty = toSlot.IsEmpty();

                // If the target slot is not empty, get the item in the target slot.
                Item toItem = null;
                double toStackSize = double.NaN;
                var isSameItem = false;

                // If the target slot is not empty, check if the item in the target slot is addable to the item in the source slot.
                if (!toEmpty)
                {
                    toItem = toContainer.GetItem(toSlot);
                    toStackSize = toContainer.stackSize is double ts && ts != 0 ? ts : toItem.maxStackSize;
                    isSameItem = fromItem.key == toItem.key;

                    var fromFull = fromItem.count > (toContainer.stackSize is double a && a != 0 ? a : fromItem.maxStackSize);
                    var toFull = toItem.count > (stackSize is double b && b != 0 ? b : toItem.maxStackSize);

                    // If we cannot stack the items, return early.
                    if (((fromFull || toFull) && !isSameItem) || fromItem.IsEnchanted() || toItem.IsEnchanted()) return;
                }

                if (toEmpty || isSameItem)
                {
                    // Get the count of the item in the target slot.
                    var toCount = toEmpty ? 0 : toItem.count;

                    // Add the count of the item in the target slot to the count of the item in the source slot.
                    fromItem.count += toCount;

                    // Update the item in the target slot with the item in the source slot.
                    toSlot.Update(fromItem, toContainer.stackSize);

                    // Remove the item in the source slot from the container.
                    if (toSlot.count > 0) Remove(fromIndex, toSlot.count - toCount);
                }
                else if (fromItem.count <= fromStackSize && toItem.count <= toStackSize)
                {
                    // Update the item in the target slot with the item in the source slot.
                    toSlot.Update(fromItem, toContainer.stackSize);

                    Log.Assert(toSlot.count >= fromSlot.count, "source slot count is greater than target slot count");

                    fromSlot.Update(toItem, stackSize);

                    toItem.count -= fromSlot.count;

                    Log.Assert(toItem.count < 1, "target item count is greater than 0");
                }
            }

            // Call the load callback for this container
            loadCallback?.Invoke();

            // Call the load callback for the destination container
            toContainer.loadCallback?.Invoke();
        }

        /// <summary>slots[index]; an index outside the array gives undefined, and using it throws.</summary>
        public Slot Get(double index) =>
            At(index) ?? throw new JsTypeError("Cannot read properties of undefined (reading 'isEmpty')");

        public Item GetItem(Slot slot) => new(slot.key, -1, -1, true, slot.count, slot.enchantments);

        public Item GetItem(string key, double count, JsObject enchantments) => new(key, -1, -1, true, count, enchantments);

        public double GetIndex(string key, double count = 1) =>
            slots.FindIndex(slot => slot.key == key && slot.count >= count);

        public Slot Find(Item item)
        {
            var stackSize = this.stackSize is double s && s != 0 ? s : item.maxStackSize;

            // Find a slot with the same item.
            var sameItemSlot = slots.Find(slot =>
                // If the slot's key matches the item's key
                slot.key == item.key &&
                // And the slot's count is less than the item's max stack size or we're ignoring the max stack size.
                slot.count < stackSize &&
                // And the item is not enchanted.
                !item.IsEnchanted() &&
                // And the slot is not enchanted.
                !slot.IsEnchanted());

            // If there's no slot with the same item, find an empty slot.
            return sameItemSlot ?? GetEmptySlot();
        }

        public bool HasSpace(double count = 1) => count > 1 ? GetEmptySlots() >= count : GetEmptySlot() != null;

        public bool HasItem(string key, double count = 1)
        {
            var contains = false;

            // Check to see if the item is a stackable kind first.
            if (GetIndex(key, count) == -1)
            {
                // Search through the slots to see if we can find `count` amount of occurrences.
                double found = 0;

                ForEachSlot(slot =>
                {
                    if (slot.key != key) return;

                    found += slot.count;
                });

                // We found `count` or more occurrences of an item.
                if (found >= count) contains = true;
            }
            else contains = true;

            return contains;
        }

        public double Count(string key)
        {
            double found = 0;

            ForEachSlot(slot =>
            {
                if (slot.key != key) return;

                found += slot.count;
            });

            return found;
        }

        Slot GetEmptySlot() => slots.Find(slot => !JsData.Truthy(slot.key));

        public double GetEmptySlots() => slots.Count(slot => !JsData.Truthy(slot.key));

        public void ForEachSlot(Action<Slot> callback)
        {
            foreach (var slot in slots) callback(slot);
        }

        public JsObject Serialize(bool clientInfo = false)
        {
            var list = new List<object>();

            foreach (var slot in slots) list.Add(slot.Serialize(clientInfo));

            return new JsObject("slots", list);
        }

        public void OnLoaded(Action callback) => loadCallback = callback;

        public void OnAdd(Action<Slot> callback) => addCallback = callback;

        public void OnRemove(Action<Slot, string, double, JsObject, bool> callback) => removeCallback = callback;

        public void OnNotify(Action<string> callback) => notifyCallback = callback;
    }

    /// <summary>player/containers/impl/bank.ts.</summary>
    public sealed class Bank : Container
    {
        public Bank(double size) : base(Modules.ContainerType.Bank, size)
        {
            // protected override stackSize = MAX_STACK (a field initializer, after the base constructor).
            stackSize = Constants.MAX_STACK;
        }

        public override double Add(Item item)
        {
            var amount = base.Add(item);

            if (amount < 1)
            {
                notifyCallback?.Invoke("You do not have enough space in your bank.");
                return amount;
            }

            return amount;
        }
    }

    /// <summary>player/containers/impl/inventory.ts.</summary>
    public sealed class Inventory : Container
    {
        public Inventory(double size) : base(Modules.ContainerType.Inventory, size) { }

        public override double Add(Item item)
        {
            var amount = base.Add(item);

            // -2 represents an error when adding an item fails for not existing.
            if (amount == -2) return 0;

            if (amount < 1)
            {
                notifyCallback?.Invoke("You do not have enough space in your inventory.");
                return 0;
            }

            item.Despawn(true);

            return amount;
        }

        public override JsObject Remove(double index, double count = 1, bool drop = false)
        {
            // Ensure the index is within the bounds of the inventory.
            if (index < 0 || index >= size) return null;

            var item = GetItem(At(index) ?? throw new JsTypeError("Cannot read properties of undefined (reading 'key')"));

            if (item.undroppable && drop)
            {
                notifyCallback?.Invoke("You cannot drop this item.");
                return null;
            }

            return base.Remove(index, count, drop);
        }
    }
}
