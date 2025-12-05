using System;
using System.Collections;
using UnityEngine;
using VRC.SDK3.Data;
using UdonSharp.CE.Data.Internal;

namespace UdonSharp.CE.Data
{
    /// <summary>
    /// Key-value pair for CEDictionary iteration.
    /// </summary>
    public class CEKeyValuePair<TKey, TValue>
    {
        public TKey Key;
        public TValue Value;

        public CEKeyValuePair() { }

        public CEKeyValuePair(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// A type-safe dictionary wrapper with DataDictionary bridge methods.
    /// Provides seamless conversion between UdonSharp Dictionary and VRChat DataDictionary.
    /// </summary>
    /// <remarks>
    /// Note: VRChat DataDictionary only supports string keys for JSON compatibility.
    /// For non-string keys, use the converter overloads.
    /// </remarks>
    public class CEDictionary<TKey, TValue> : IEnumerable
    {
        private const int InitialSize = 23;
        private const float LoadFactor = 0.75f;

        private TKey[] _keys;
        private TValue[] _values;
        private bool[] _occupied;
        private int _size;
        private int _capacity;

        #region Constructors

        public CEDictionary()
        {
            _capacity = InitialSize;
            _keys = new TKey[_capacity];
            _values = new TValue[_capacity];
            _occupied = new bool[_capacity];
            _size = 0;
        }

        public CEDictionary(int capacity)
        {
            _capacity = capacity > 0 ? capacity : InitialSize;
            _keys = new TKey[_capacity];
            _values = new TValue[_capacity];
            _occupied = new bool[_capacity];
            _size = 0;
        }

        #endregion

        #region Core Dictionary Operations

        public int Count => _size;

        public TValue this[TKey key]
        {
            get
            {
                if (TryGetValue(key, out TValue value))
                {
                    return value;
                }
                Debug.LogError($"[CE.Data] CEDictionary key not found: {key}");
            #pragma warning disable CS0251
                return _values[-1];
            #pragma warning restore CS0251
            }
            set
            {
                int index = FindKeyIndex(key);
                if (index >= 0)
                {
                    _values[index] = value;
                }
                else
                {
                    Add(key, value);
                }
            }
        }

        public void Add(TKey key, TValue value)
        {
            if (key == null)
            {
                Debug.LogError("[CE.Data] CEDictionary key cannot be null");
            #pragma warning disable CS0251
                _keys[-1] = key;
            #pragma warning restore CS0251
                return;
            }

            int index = FindKeyIndex(key);
            if (index >= 0)
            {
                Debug.LogError($"[CE.Data] CEDictionary key already exists: {key}");
                return;
            }

            if (_size >= (int)(_capacity * LoadFactor))
            {
                Resize();
            }

            // Find empty slot
            int hashCode = key.GetHashCode();
            int startIndex = Mathf.Abs(hashCode % _capacity);

            for (int i = 0; i < _capacity; i++)
            {
                int idx = (startIndex + i) % _capacity;
                if (!_occupied[idx])
                {
                    _keys[idx] = key;
                    _values[idx] = value;
                    _occupied[idx] = true;
                    _size++;
                    return;
                }
            }
        }

        public bool Remove(TKey key)
        {
            int index = FindKeyIndex(key);
            if (index < 0)
            {
                return false;
            }

            _keys[index] = default;
            _values[index] = default;
            _occupied[index] = false;
            _size--;

            // Rehash following entries to maintain probe sequence
            RehashAfterRemoval(index);
            return true;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            int index = FindKeyIndex(key);
            if (index >= 0)
            {
                value = _values[index];
                return true;
            }
            value = default;
            return false;
        }

        public bool ContainsKey(TKey key)
        {
            return FindKeyIndex(key) >= 0;
        }

        public bool ContainsValue(TValue value)
        {
            for (int i = 0; i < _capacity; i++)
            {
                if (_occupied[i])
                {
                    if (value == null)
                    {
                        if (_values[i] == null) return true;
                    }
                    else if (value.Equals(_values[i]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public void Clear()
        {
            Array.Clear(_keys, 0, _capacity);
            Array.Clear(_values, 0, _capacity);
            Array.Clear(_occupied, 0, _capacity);
            _size = 0;
        }

        public TKey[] GetKeys()
        {
            TKey[] result = new TKey[_size];
            int idx = 0;
            for (int i = 0; i < _capacity && idx < _size; i++)
            {
                if (_occupied[i])
                {
                    result[idx++] = _keys[i];
                }
            }
            return result;
        }

        public TValue[] GetValues()
        {
            TValue[] result = new TValue[_size];
            int idx = 0;
            for (int i = 0; i < _capacity && idx < _size; i++)
            {
                if (_occupied[i])
                {
                    result[idx++] = _values[i];
                }
            }
            return result;
        }

        #endregion

        #region DataDictionary Bridge Methods

        /// <summary>
        /// Converts this CEDictionary to a VRChat DataDictionary.
        /// Requires TKey to be string for JSON compatibility.
        /// </summary>
        public DataDictionary ToDataDictionary()
        {
            DataDictionary result = new DataDictionary();

            for (int i = 0; i < _capacity; i++)
            {
                if (_occupied[i])
                {
                    DataToken keyToken = DataTokenConverter.ToToken(_keys[i]);
                    DataToken valueToken = DataTokenConverter.ToToken(_values[i]);
                    result.SetValue(keyToken, valueToken);
                }
            }

            return result;
        }

        /// <summary>
        /// Converts this CEDictionary to a DataDictionary using custom converters.
        /// </summary>
        public DataDictionary ToDataDictionary(
            System.Func<TKey, DataToken> keyConverter,
            System.Func<TValue, DataToken> valueConverter)
        {
            if (keyConverter == null || valueConverter == null)
            {
                Debug.LogError("[CE.Data] CEDictionary.ToDataDictionary: converters cannot be null");
                return new DataDictionary();
            }

            DataDictionary result = new DataDictionary();

            for (int i = 0; i < _capacity; i++)
            {
                if (_occupied[i])
                {
                    DataToken keyToken = keyConverter(_keys[i]);
                    DataToken valueToken = valueConverter(_values[i]);
                    result.SetValue(keyToken, valueToken);
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a CEDictionary from a VRChat DataDictionary.
        /// </summary>
        public static CEDictionary<TKey, TValue> FromDataDictionary(DataDictionary dataDict)
        {
            if (dataDict == null)
            {
                return new CEDictionary<TKey, TValue>();
            }

            DataList keys = dataDict.GetKeys();
            int count = keys.Count;
            CEDictionary<TKey, TValue> result = new CEDictionary<TKey, TValue>(count * 2);

            for (int i = 0; i < count; i++)
            {
                if (keys.TryGetValue(i, out DataToken keyToken))
                {
                    if (dataDict.TryGetValue(keyToken, out DataToken valueToken))
                    {
                        TKey key = DataTokenConverter.FromToken<TKey>(keyToken);
                        TValue value = DataTokenConverter.FromToken<TValue>(valueToken);
                        result.Add(key, value);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a CEDictionary from a DataDictionary using custom converters.
        /// </summary>
        public static CEDictionary<TKey, TValue> FromDataDictionary(
            DataDictionary dataDict,
            System.Func<DataToken, TKey> keyConverter,
            System.Func<DataToken, TValue> valueConverter)
        {
            if (dataDict == null)
            {
                return new CEDictionary<TKey, TValue>();
            }

            if (keyConverter == null || valueConverter == null)
            {
                Debug.LogError("[CE.Data] CEDictionary.FromDataDictionary: converters cannot be null");
                return new CEDictionary<TKey, TValue>();
            }

            DataList keys = dataDict.GetKeys();
            int count = keys.Count;
            CEDictionary<TKey, TValue> result = new CEDictionary<TKey, TValue>(count * 2);

            for (int i = 0; i < count; i++)
            {
                if (keys.TryGetValue(i, out DataToken keyToken))
                {
                    if (dataDict.TryGetValue(keyToken, out DataToken valueToken))
                    {
                        TKey key = keyConverter(keyToken);
                        TValue value = valueConverter(valueToken);
                        result.Add(key, value);
                    }
                }
            }

            return result;
        }

        #endregion

        #region JSON Serialization

        /// <summary>
        /// Serializes this CEDictionary to a JSON string.
        /// Keys must be strings for JSON compatibility.
        /// </summary>
        public string ToJson()
        {
            DataDictionary dataDict = ToDataDictionary();
            if (VRCJson.TrySerializeToJson(dataDict, JsonExportType.Minify, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError("[CE.Data] CEDictionary.ToJson: Failed to serialize to JSON");
            return "{}";
        }

        /// <summary>
        /// Serializes this CEDictionary to a JSON string with optional beautification.
        /// </summary>
        public string ToJson(bool beautify)
        {
            DataDictionary dataDict = ToDataDictionary();
            JsonExportType exportType = beautify ? JsonExportType.Beautify : JsonExportType.Minify;
            if (VRCJson.TrySerializeToJson(dataDict, exportType, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError("[CE.Data] CEDictionary.ToJson: Failed to serialize to JSON");
            return "{}";
        }

        /// <summary>
        /// Deserializes a JSON string to a CEDictionary.
        /// Note: JSON numbers become doubles, use converter for precise types.
        /// </summary>
        public static CEDictionary<TKey, TValue> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new CEDictionary<TKey, TValue>();
            }

            if (VRCJson.TryDeserializeFromJson(json, out DataToken token))
            {
                if (token.TokenType == TokenType.DataDictionary)
                {
                    return FromDataDictionary(token.DataDictionary);
                }
            }

            Debug.LogError("[CE.Data] CEDictionary.FromJson: Failed to deserialize from JSON");
            return new CEDictionary<TKey, TValue>();
        }

        /// <summary>
        /// Deserializes a JSON string to a CEDictionary using custom converters.
        /// </summary>
        public static CEDictionary<TKey, TValue> FromJson(
            string json,
            System.Func<DataToken, TKey> keyConverter,
            System.Func<DataToken, TValue> valueConverter)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new CEDictionary<TKey, TValue>();
            }

            if (VRCJson.TryDeserializeFromJson(json, out DataToken token))
            {
                if (token.TokenType == TokenType.DataDictionary)
                {
                    return FromDataDictionary(token.DataDictionary, keyConverter, valueConverter);
                }
            }

            Debug.LogError("[CE.Data] CEDictionary.FromJson: Failed to deserialize from JSON");
            return new CEDictionary<TKey, TValue>();
        }

        #endregion

        #region IEnumerable Implementation

        public IEnumerator GetEnumerator()
        {
            return new CEDictionaryIterator<TKey, TValue>(this);
        }

        internal bool GetEntryAt(int index, out TKey key, out TValue value)
        {
            if (index >= 0 && index < _capacity && _occupied[index])
            {
                key = _keys[index];
                value = _values[index];
                return true;
            }
            key = default;
            value = default;
            return false;
        }

        internal int Capacity => _capacity;
        internal bool IsOccupied(int index) => index >= 0 && index < _capacity && _occupied[index];

        #endregion

        #region Private Helpers

        private int FindKeyIndex(TKey key)
        {
            if (key == null) return -1;

            int hashCode = key.GetHashCode();
            int startIndex = Mathf.Abs(hashCode % _capacity);

            for (int i = 0; i < _capacity; i++)
            {
                int idx = (startIndex + i) % _capacity;
                if (!_occupied[idx])
                {
                    return -1; // Empty slot means key not found
                }
                if (key.Equals(_keys[idx]))
                {
                    return idx;
                }
            }
            return -1;
        }

        private void Resize()
        {
            int newCapacity = _capacity * 2 + 1;
            TKey[] oldKeys = _keys;
            TValue[] oldValues = _values;
            bool[] oldOccupied = _occupied;
            int oldCapacity = _capacity;

            _capacity = newCapacity;
            _keys = new TKey[newCapacity];
            _values = new TValue[newCapacity];
            _occupied = new bool[newCapacity];
            _size = 0;

            for (int i = 0; i < oldCapacity; i++)
            {
                if (oldOccupied[i])
                {
                    Add(oldKeys[i], oldValues[i]);
                }
            }
        }

        private void RehashAfterRemoval(int removedIndex)
        {
            int idx = (removedIndex + 1) % _capacity;
            while (_occupied[idx])
            {
                TKey key = _keys[idx];
                TValue value = _values[idx];

                _keys[idx] = default;
                _values[idx] = default;
                _occupied[idx] = false;
                _size--;

                Add(key, value);

                idx = (idx + 1) % _capacity;
            }
        }

        #endregion
    }

    /// <summary>
    /// Iterator for CEDictionary.
    /// </summary>
    internal class CEDictionaryIterator<TKey, TValue> : IEnumerator
    {
        private CEDictionary<TKey, TValue> _dict;
        private int _index;
        private CEKeyValuePair<TKey, TValue> _current;

        public CEDictionaryIterator(CEDictionary<TKey, TValue> dict)
        {
            _dict = dict;
            _index = -1;
            _current = new CEKeyValuePair<TKey, TValue>();
        }

        public bool MoveNext()
        {
            CEDictionary<TKey, TValue> dict = _dict;
            int capacity = dict.Capacity;

            int index = _index + 1;
            while (index < capacity)
            {
                if (dict.IsOccupied(index))
                {
                    if (dict.GetEntryAt(index, out TKey key, out TValue value))
                    {
                        _index = index;
                        _current.Key = key;
                        _current.Value = value;
                        return true;
                    }
                }
                index++;
            }

            _index = capacity;
            return false;
        }

        public void Reset()
        {
            _index = -1;
        }

        object IEnumerator.Current => Current;

        public CEKeyValuePair<TKey, TValue> Current => _current;
    }
}
