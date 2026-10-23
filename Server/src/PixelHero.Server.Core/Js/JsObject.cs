using System.Collections;
using System.Globalization;
using PixelHero.Js;

namespace PixelHero.Server.Js
{
    /// <summary>
    /// A JavaScript object used as a dictionary (<c>{ [key: string]: T }</c>), with JavaScript's property order:
    /// array-index keys ("0", "17", ...) ascending, then the other keys in insertion order. Re-assigning a key keeps its
    /// place; deleting and re-adding moves it to the end. Enumeration works like <c>for...in</c>: it walks the keys
    /// present when it starts and skips keys deleted meanwhile (keys added meanwhile are not visited).
    /// <see cref="Values"/>/<see cref="Keys"/> are snapshots like Object.values/Object.keys.
    /// Implements the non-generic IDictionary so <c>JsJson.Stringify</c> can write it.
    /// </summary>
    public class JsObject<TValue> : IDictionary, IEnumerable<KeyValuePair<string, TValue>>
    {
        sealed class Entry
        {
            public string Key;
            public TValue Value;
            public Entry Previous, Next;
            public bool Index;
            public bool Removed;
        }

        readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        readonly SortedDictionary<uint, Entry> _indexEntries = new();
        Entry _first, _last;

        public int Count => _entries.Count;

        public TValue this[string key]
        {
            get => _entries.TryGetValue(key, out var entry) ? entry.Value : default;
            set => Set(key, value);
        }

        /// <summary><c>key in object</c>.</summary>
        public bool Has(string key) => key != null && _entries.ContainsKey(key);

        public bool TryGet(string key, out TValue value)
        {
            if (key != null && _entries.TryGetValue(key, out var entry))
            {
                value = entry.Value;
                return true;
            }
            value = default;
            return false;
        }

        public void Set(string key, TValue value)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Value = value;
                return;
            }
            entry = new Entry { Key = key, Value = value };
            _entries[key] = entry;
            if (IsArrayIndex(key, out var index))
            {
                entry.Index = true;
                _indexEntries[index] = entry;
            }
            else
            {
                entry.Previous = _last;
                if (_last != null) _last.Next = entry;
                else _first = entry;
                _last = entry;
            }
        }

        /// <summary><c>delete object[key]</c>; returns whether the key was present.</summary>
        public bool Delete(string key)
        {
            if (key == null || !_entries.Remove(key, out var entry)) return false;
            entry.Removed = true;
            if (entry.Index)
            {
                IsArrayIndex(key, out var index);
                _indexEntries.Remove(index);
            }
            else
            {
                if (entry.Previous != null) entry.Previous.Next = entry.Next;
                else _first = entry.Next;
                if (entry.Next != null) entry.Next.Previous = entry.Previous;
                else _last = entry.Previous;
            }
            return true;
        }

        public void Clear()
        {
            foreach (var entry in _entries.Values) entry.Removed = true;
            _entries.Clear();
            _indexEntries.Clear();
            _first = _last = null;
        }

        List<Entry> Snapshot()
        {
            var list = new List<Entry>(_entries.Count);
            list.AddRange(_indexEntries.Values);
            for (var entry = _first; entry != null; entry = entry.Next) list.Add(entry);
            return list;
        }

        /// <summary>Object.keys.</summary>
        public List<string> Keys => Snapshot().ConvertAll(entry => entry.Key);

        /// <summary>Object.values.</summary>
        public List<TValue> Values => Snapshot().ConvertAll(entry => entry.Value);

        /// <summary>Object.entries.</summary>
        public List<KeyValuePair<string, TValue>> Entries =>
            Snapshot().ConvertAll(entry => new KeyValuePair<string, TValue>(entry.Key, entry.Value));

        /// <summary><c>for (let key in object)</c>: skips keys deleted during the walk.</summary>
        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        {
            foreach (var entry in Snapshot())
                if (!entry.Removed)
                    yield return new KeyValuePair<string, TValue>(entry.Key, entry.Value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>The ECMAScript array index test: a canonical uint32 below 2^32 - 1.</summary>
        public static bool IsArrayIndex(string key, out uint index)
        {
            index = 0;
            if (string.IsNullOrEmpty(key) || key.Length > 10) return false;
            if (key.Length > 1 && key[0] == '0') return false;
            foreach (var c in key)
                if (c < '0' || c > '9')
                    return false;
            if (!ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            if (value >= uint.MaxValue) return false;
            index = (uint)value;
            return true;
        }

        // ---- non-generic IDictionary (for JsJson.Stringify) ----
        bool IDictionary.IsFixedSize => false;
        bool IDictionary.IsReadOnly => false;
        ICollection IDictionary.Keys => Keys;
        ICollection IDictionary.Values => Values;
        int ICollection.Count => Count;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        object IDictionary.this[object key]
        {
            get => this[(string)key];
            set => Set((string)key, (TValue)value);
        }

        void IDictionary.Add(object key, object value) => Set((string)key, (TValue)value);
        bool IDictionary.Contains(object key) => key is string s && Has(s);
        void IDictionary.Remove(object key) => Delete((string)key);

        void ICollection.CopyTo(Array array, int index)
        {
            foreach (var pair in this) array.SetValue(new DictionaryEntry(pair.Key, pair.Value), index++);
        }

        // A C# null value is the port's undefined: JsJson.Stringify leaves the key out, like JSON.stringify.
        IDictionaryEnumerator IDictionary.GetEnumerator() =>
            new DictionaryEnumerator(Entries.ConvertAll(pair =>
                new KeyValuePair<string, TValue>(pair.Key, pair.Value)));

        sealed class DictionaryEnumerator : IDictionaryEnumerator
        {
            readonly List<KeyValuePair<string, TValue>> _items;
            int _position = -1;

            public DictionaryEnumerator(List<KeyValuePair<string, TValue>> items) => _items = items;

            public DictionaryEntry Entry => new(_items[_position].Key, (object)_items[_position].Value ?? Undefined.Value);
            public object Key => _items[_position].Key;
            public object Value => (object)_items[_position].Value ?? Undefined.Value;
            public object Current => Entry;
            public bool MoveNext() => ++_position < _items.Count;
            public void Reset() => _position = -1;
        }
    }

    /// <summary>A plain JavaScript object holding any values (JSON data, packet payloads).</summary>
    public class JsObject : JsObject<object>
    {
        public JsObject() { }

        /// <summary>Builds an object literal from key/value pairs, in order: <c>new JsObject("x", 1, "y", 2)</c>.</summary>
        public JsObject(params object[] pairs)
        {
            for (var i = 0; i < pairs.Length; i += 2) Set((string)pairs[i], pairs[i + 1]);
        }
    }
}

namespace PixelHero.Server.Js
{
    /// <summary>
    /// A class instance handed to JSON.stringify later (a packet holds the object until the network flush): the
    /// fields are read when it is written, so changes made before the flush show, as in the original.
    /// </summary>
    public sealed class LiveJson : System.Collections.IDictionary
    {
        readonly Func<JsObject> _read;

        public LiveJson(Func<JsObject> read) => _read = read;

        System.Collections.IDictionary Current => _read();

        public System.Collections.IDictionaryEnumerator GetEnumerator() => Current.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Current.GetEnumerator();
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public System.Collections.ICollection Keys => Current.Keys;
        public System.Collections.ICollection Values => Current.Values;
        public int Count => Current.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public object this[object key] { get => Current[key]; set => throw new NotSupportedException(); }
        public void Add(object key, object value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object key) => Current.Contains(key);
        public void Remove(object key) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => Current.CopyTo(array, index);
    }
}
