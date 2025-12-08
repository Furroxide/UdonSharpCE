using System;
using System.Collections;
using UnityEngine;
using VRC.SDK3.Data;
using UdonSharp.CE.Data.Internal;

namespace UdonSharp.CE.Data
{
    /// <summary>
    /// A type-safe list wrapper with DataList bridge methods.
    /// Provides seamless conversion between UdonSharp List&lt;T&gt; and VRChat DataList.
    /// </summary>
    public class CEList<T> : IEnumerable
    #if COMPILER_UDONSHARP
        where T : IComparable
    #endif
    {
        // Internal to allow CEListIterator to access without bounds checking
        internal T[] _items;
        internal int _size;

        #region Constructors

        public CEList()
        {
            _items = new T[8];
            _size = 0;
        }

        public CEList(int capacity)
        {
            _items = new T[capacity > 0 ? capacity : 8];
            _size = 0;
        }

        public CEList(T[] source)
        {
            if (source == null)
            {
                _items = new T[8];
                _size = 0;
            }
            else
            {
                _items = new T[source.Length > 0 ? source.Length * 2 : 8];
                Array.Copy(source, _items, source.Length);
                _size = source.Length;
            }
        }

        public CEList(CEList<T> source)
        {
            if (source == null)
            {
                _items = new T[8];
                _size = 0;
            }
            else
            {
                _items = new T[source._items.Length];
                _size = source._size;
                Array.Copy(source._items, _items, _size);
            }
        }

        #endregion

        #region Core List Operations

        public int Count => _size;

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _size)
                {
                    Debug.LogError($"[CE.Data] CEList index out of range: {index}");
                #pragma warning disable CS0251
                    return _items[-1];
                #pragma warning restore CS0251
                }
                return _items[index];
            }
            set
            {
                if (index < 0 || index >= _size)
                {
                    Debug.LogError($"[CE.Data] CEList index out of range: {index}");
                #pragma warning disable CS0251
                    _items[-1] = value;
                #pragma warning restore CS0251
                    return;
                }
                _items[index] = value;
            }
        }

        public void Add(T item)
        {
            int size = _size;
            T[] itemArr = _items;

            if (size == itemArr.Length)
            {
                T[] newItems = new T[itemArr.Length * 2];
                Array.Copy(itemArr, newItems, itemArr.Length);
                _items = newItems;
                itemArr = newItems;
            }

            itemArr[size] = item;
            _size = size + 1;
        }

        public void AddRange(T[] items)
        {
            if (items == null) return;

            int count = items.Length;
            EnsureCapacity(_size + count);

            Array.Copy(items, 0, _items, _size, count);
            _size += count;
        }

        public void Insert(int index, T item)
        {
            int size = _size;
            T[] itemArr = _items;

            if (index < 0 || index > size)
            {
                Debug.LogError($"[CE.Data] CEList insert index out of range: {index}");
            #pragma warning disable CS0251
                itemArr[-1] = itemArr[0];
            #pragma warning restore CS0251
                return;
            }

            if (size == itemArr.Length)
            {
                T[] newItems = new T[itemArr.Length * 2];
                Array.Copy(itemArr, newItems, itemArr.Length);
                _items = newItems;
                itemArr = newItems;
            }

            Array.Copy(itemArr, index, itemArr, index + 1, size - index);
            itemArr[index] = item;
            _size = size + 1;
        }

        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }

        public void RemoveAt(int index)
        {
            int size = _size;
            T[] itemArr = _items;

            if (index < 0 || index >= size)
            {
                Debug.LogError($"[CE.Data] CEList remove index out of range: {index}");
            #pragma warning disable CS0251
                itemArr[-1] = itemArr[0];
            #pragma warning restore CS0251
                return;
            }

            Array.Copy(itemArr, index + 1, itemArr, index, size - index - 1);
            itemArr[size - 1] = default(T);
            _size = size - 1;
        }

        public bool Contains(T item)
        {
            return IndexOf(item) >= 0;
        }

        public int IndexOf(T item)
        {
            int size = _size;
            T[] itemArr = _items;

            if (item == null)
            {
                for (int i = 0; i < size; i++)
                {
                    if (itemArr[i] == null)
                        return i;
                }
                return -1;
            }

            for (int i = 0; i < size; i++)
            {
                if (item.Equals(itemArr[i]))
                    return i;
            }
            return -1;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _size);
            _size = 0;
        }

        public void Reverse()
        {
            Array.Reverse(_items, 0, _size);
        }

        public T[] ToArray()
        {
            int size = _size;
            T[] result = new T[size];
            Array.Copy(_items, result, size);
            return result;
        }

        #endregion

        #region DataList Bridge Methods

        /// <summary>
        /// Converts this CEList to a VRChat DataList.
        /// Only works for DataToken-compatible types (primitives, string, DataList, DataDictionary).
        /// </summary>
        public DataList ToDataList()
        {
            DataList result = new DataList();
            int size = _size;
            T[] itemArr = _items;

            for (int i = 0; i < size; i++)
            {
                result.Add(DataTokenConverter.ToToken(itemArr[i]));
            }

            return result;
        }

        /// <summary>
        /// Converts this CEList to a DataList using a custom converter function.
        /// </summary>
        public DataList ToDataList(System.Func<T, DataToken> converter)
        {
            if (converter == null)
            {
                Debug.LogError("[CE.Data] CEList.ToDataList: converter cannot be null");
                return new DataList();
            }

            DataList result = new DataList();
            int size = _size;
            T[] itemArr = _items;

            for (int i = 0; i < size; i++)
            {
                result.Add(converter(itemArr[i]));
            }

            return result;
        }

        /// <summary>
        /// Creates a CEList from a VRChat DataList.
        /// Only works for DataToken-compatible types.
        /// </summary>
        public static CEList<T> FromDataList(DataList dataList)
        {
            if (dataList == null)
            {
                return new CEList<T>();
            }

            int count = dataList.Count;
            CEList<T> result = new CEList<T>(count);

            for (int i = 0; i < count; i++)
            {
                if (dataList.TryGetValue(i, out DataToken token))
                {
                    result.Add(DataTokenConverter.FromToken<T>(token));
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a CEList from a DataList using a custom converter function.
        /// </summary>
        public static CEList<T> FromDataList(DataList dataList, System.Func<DataToken, T> converter)
        {
            if (dataList == null)
            {
                return new CEList<T>();
            }

            if (converter == null)
            {
                Debug.LogError("[CE.Data] CEList.FromDataList: converter cannot be null");
                return new CEList<T>();
            }

            int count = dataList.Count;
            CEList<T> result = new CEList<T>(count);

            for (int i = 0; i < count; i++)
            {
                if (dataList.TryGetValue(i, out DataToken token))
                {
                    result.Add(converter(token));
                }
            }

            return result;
        }

        #endregion

        #region JSON Serialization

        /// <summary>
        /// Serializes this CEList to a JSON string using VRCJson.
        /// Only works for DataToken-compatible element types.
        /// </summary>
        public string ToJson()
        {
            DataList dataList = ToDataList();
            if (VRCJson.TrySerializeToJson(dataList, JsonExportType.Minify, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError("[CE.Data] CEList.ToJson: Failed to serialize to JSON");
            return "[]";
        }

        /// <summary>
        /// Serializes this CEList to a JSON string with optional beautification.
        /// </summary>
        public string ToJson(bool beautify)
        {
            DataList dataList = ToDataList();
            JsonExportType exportType = beautify ? JsonExportType.Beautify : JsonExportType.Minify;
            if (VRCJson.TrySerializeToJson(dataList, exportType, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError("[CE.Data] CEList.ToJson: Failed to serialize to JSON");
            return "[]";
        }

        /// <summary>
        /// Deserializes a JSON string to a CEList.
        /// Note: JSON numbers become doubles, so use FromJson with converter for precise types.
        /// </summary>
        public static CEList<T> FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new CEList<T>();
            }

            if (VRCJson.TryDeserializeFromJson(json, out DataToken token))
            {
                if (token.TokenType == TokenType.DataList)
                {
                    return FromDataList(token.DataList);
                }
            }

            Debug.LogError("[CE.Data] CEList.FromJson: Failed to deserialize from JSON");
            return new CEList<T>();
        }

        /// <summary>
        /// Deserializes a JSON string to a CEList using a custom converter.
        /// </summary>
        public static CEList<T> FromJson(string json, System.Func<DataToken, T> converter)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new CEList<T>();
            }

            if (VRCJson.TryDeserializeFromJson(json, out DataToken token))
            {
                if (token.TokenType == TokenType.DataList)
                {
                    return FromDataList(token.DataList, converter);
                }
            }

            Debug.LogError("[CE.Data] CEList.FromJson: Failed to deserialize from JSON");
            return new CEList<T>();
        }

        #endregion

        #region IEnumerable Implementation

        public IEnumerator GetEnumerator()
        {
            return new CEListIterator<T>(this);
        }

        #endregion

        #region Private Helpers

        private void EnsureCapacity(int minCapacity)
        {
            T[] itemArr = _items;
            if (minCapacity > itemArr.Length)
            {
                int newCapacity = itemArr.Length * 2;
                if (newCapacity < minCapacity)
                    newCapacity = minCapacity;

                T[] newItems = new T[newCapacity];
                Array.Copy(itemArr, newItems, _size);
                _items = newItems;
            }
        }

        #endregion
    }

    /// <summary>
    /// Iterator for CEList.
    /// </summary>
    internal class CEListIterator<T> : IEnumerator
    #if COMPILER_UDONSHARP
        where T : IComparable
    #endif
    {
        private CEList<T> _list;
        private int _index;
        private T _current;

        public CEListIterator(CEList<T> list)
        {
            _list = list;
            _index = -1;
            _current = default;
        }

        public bool MoveNext()
        {
            CEList<T> list = _list;
            int count = list._size;
            int index = _index + 1;

            if (index < count)
            {
                _index = index;
                // Access _items directly to skip bounds check (already validated above)
                _current = list._items[index];
                return true;
            }

            _index = count;
            _current = default;
            return false;
        }

        public void Reset()
        {
            _index = -1;
            _current = default;
        }

        object IEnumerator.Current => Current;

        public T Current => _current;
    }
}
