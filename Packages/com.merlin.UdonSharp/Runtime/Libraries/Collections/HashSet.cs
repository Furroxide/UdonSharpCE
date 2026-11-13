using System;
using UnityEngine;
// ReSharper disable GenericEnumeratorNotDisposed

namespace UdonSharp.Lib.Internal.Collections
{
    internal class HashSetIterator<T>
    {
        private HashSetEntry<T>[] _buckets;
        private HashSetEntry<T> _entry;
        private int _bucketIndex;
        private T _current;

        public HashSetIterator(HashSetEntry<T>[] buckets)
        {
            _buckets = buckets;
        }

        public bool MoveNext()
        {
            HashSetEntry<T> entry = _entry;
            if (entry != null && entry.next != null)
            {
                _entry = entry.next;
                _current = _entry.value;
                return true;
            }
            
            int bucketIndex = _bucketIndex;
            HashSetEntry<T>[] buckets = _buckets;
            int length = buckets.Length;

            while (bucketIndex < length)
            {
                _entry = buckets[bucketIndex++];

                if (_entry != null)
                {
                    _bucketIndex = bucketIndex;
                    _current = _entry.value;
                    return true;
                }
            }

            return false;
        }

        public T Current => _current;
    }
    
    internal class HashSetEntry<T>
    {
        public T value;
        public HashSetEntry<T> next;
        
        public HashSetEntry(T value)
        {
            this.value = value;
        }
    }
    
    internal class HashSet<T>
    {
        private const int InitialSize = 23; // Prime number for better distribution initially
        private const float LoadFactor = 0.75f;

        private HashSetEntry<T>[] _buckets = new HashSetEntry<T>[InitialSize];
        private int _size;
        private int _threshold = (int)(InitialSize * LoadFactor);
        
        private T[] _serializedItems;
        
        public int Count
        {
            get
            {
                if (_serializedItems != null) LoadSerializedData();
                return _size;
            }
        }
        
        public bool Add(T value)
        {
            if (value == null)
            {
                Debug.LogError("Value cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
            }
            
            if (_serializedItems != null)
                LoadSerializedData();
            
            return AddEntry(value);
        }

        /// <summary>
        /// The insertion half of <see cref="Add"/>, split out so that <see cref="LoadSerializedData"/> has
        /// something to call that is not <see cref="Add"/> itself.
        ///
        /// Udon gives a method that is not marked [RecursiveMethod] ONE set of parameter and local slots.
        /// LoadSerializedData used to rebuild the set by calling Add, so the first Add on a set that still
        /// carried serialized items re-entered Add from inside Add: the inner calls overwrote the outer
        /// call's value, and when control came back the outer Add found the LAST serialized item already
        /// present and returned false without ever adding what the caller passed. In C# each call has its
        /// own frame, so this never showed in the editor. Nothing reachable from this method may call Add.
        /// </summary>
        private bool AddEntry(T value)
        {
            int size = _size;
            if (size >= _threshold)
            {
                Resize();
            }

            // ReSharper disable once PossibleNullReferenceException
            int hashCode = value.GetHashCode();
            HashSetEntry<T>[] buckets = _buckets;
            int bucketIndex = (hashCode & 0x7FFFFFFF) % buckets.Length;

            HashSetEntry<T> entry = buckets[bucketIndex];

            if (entry == null)
            {
                buckets[bucketIndex] = new HashSetEntry<T>(value);
            }
            else
            {
                while (true)
                {
                    if (entry.value.Equals(value))
                    {
                        return false;
                    }

                    if (entry.next == null)
                    {
                        entry.next = new HashSetEntry<T>(value);
                        break;
                    }

                    entry = entry.next;
                }
            }

            _size = size + 1;
            
            return true;
        }
        
        public bool Contains(T value)
        {
            if (value == null)
            {
                Debug.LogError("Value cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
            }
            
            if (_serializedItems != null)
                LoadSerializedData();
            
            // ReSharper disable once PossibleNullReferenceException
            int hashCode = value.GetHashCode();
            HashSetEntry<T>[] buckets = _buckets;
            int bucketIndex = (hashCode & 0x7FFFFFFF) % buckets.Length;

            HashSetEntry<T> entry = buckets[bucketIndex];

            while (entry != null)
            {
                if (entry.value.Equals(value))
                {
                    return true;
                }

                entry = entry.next;
            }

            return false;
        }
        
        public bool Remove(T value)
        {
            if (value == null)
            {
                Debug.LogError("Value cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
            }
            
            if (_serializedItems != null)
                LoadSerializedData();
            
            // ReSharper disable once PossibleNullReferenceException
            int hashCode = value.GetHashCode();
            HashSetEntry<T>[] buckets = _buckets;
            int bucketIndex = (hashCode & 0x7FFFFFFF) % buckets.Length;

            HashSetEntry<T> entry = buckets[bucketIndex];
            HashSetEntry<T> previousEntry = null;

            while (entry != null)
            {
                if (entry.value.Equals(value))
                {
                    if (previousEntry == null)
                    {
                        buckets[bucketIndex] = entry.next;
                    }
                    else
                    {
                        previousEntry.next = entry.next;
                    }

                    _size--;
                    return true;
                }

                previousEntry = entry;
                entry = entry.next;
            }

            return false;
        }
        
        public void Clear()
        {
            Array.Clear(_buckets, 0, _buckets.Length);
            _size = 0;
            _serializedItems = null;
        }
        
        public void UnionWith(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = other.GetEnumerator();

            while (iterator.MoveNext())
            {
                Add(iterator.Current);
            }
        }
        
        public void IntersectWith(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = GetEnumerator();

            while (iterator.MoveNext())
            {
                if (!other.Contains(iterator.Current))
                {
                    Remove(iterator.Current);
                }
            }
        }
        
        public void ExceptWith(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = other.GetEnumerator();

            while (iterator.MoveNext())
            {
                Remove(iterator.Current);
            }
        }
        
        public void SymmetricExceptWith(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = other.GetEnumerator();

            while (iterator.MoveNext())
            {
                if (Contains(iterator.Current))
                {
                    Remove(iterator.Current);
                }
                else
                {
                    Add(iterator.Current);
                }
            }
        }
        
        public bool IsSubsetOf(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return false;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = GetEnumerator();

            while (iterator.MoveNext())
            {
                if (!other.Contains(iterator.Current))
                {
                    return false;
                }
            }

            return true;
        }
        
        public bool IsSupersetOf(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return false;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = other.GetEnumerator();

            while (iterator.MoveNext())
            {
                if (!Contains(iterator.Current))
                {
                    return false;
                }
            }

            return true;
        }
        
        public bool IsProperSubsetOf(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return false;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = GetEnumerator();
            int count = 0;

            while (iterator.MoveNext())
            {
                count++;

                if (!other.Contains(iterator.Current))
                {
                    return false;
                }
            }

            return count < other.Count;
        }
        
        public bool IsProperSupersetOf(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return false;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = other.GetEnumerator();
            int count = 0;

            while (iterator.MoveNext())
            {
                count++;

                if (!Contains(iterator.Current))
                {
                    return false;
                }
            }

            return count < _size;
        }
        
        public bool Overlaps(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return false;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            HashSetIterator<T> iterator = GetEnumerator();

            while (iterator.MoveNext())
            {
                if (other.Contains(iterator.Current))
                {
                    return true;
                }
            }

            return false;
        }
        
        public bool SetEquals(HashSet<T> other)
        {
            if (other == null)
            {
                Debug.LogError("Argument cannot be null.");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return false;
            }
            
            if (_serializedItems != null)
                LoadSerializedData();

            if (_size != other.Count)
            {
                return false;
            }

            HashSetIterator<T> iterator = GetEnumerator();

            while (iterator.MoveNext())
            {
                if (!other.Contains(iterator.Current))
                {
                    return false;
                }
            }

            return true;
        }
        
        private void Resize()
        {
            int newSize = _buckets.Length * 2 + 1;
            HashSetEntry<T>[] newBuckets = new HashSetEntry<T>[newSize];
            HashSetEntry<T>[] oldBuckets = _buckets;
            int bucketCount = oldBuckets.Length;

            for (int i = 0; i < bucketCount; i++)
            {
                HashSetEntry<T> entry = oldBuckets[i];

                while (entry != null)
                {
                    HashSetEntry<T> nextEntry = entry.next;

                    int newBucketIndex = (entry.value.GetHashCode() & 0x7FFFFFFF) % newSize;
                    entry.next = newBuckets[newBucketIndex];
                    newBuckets[newBucketIndex] = entry;

                    entry = nextEntry;
                }
            }

            _buckets = newBuckets;
            _threshold = (int)(newSize * LoadFactor);
        }
        
        public void StoreSerializedData()
        {
            if (_size == 0)
                return;
            
            T[] serializedItems = ToArray();
            
            Clear();
            
            _serializedItems = serializedItems;
        }

        private void LoadSerializedData()
        {
            T[] serializedItems = _serializedItems;
            
            if (serializedItems == null)
            {
                return;
            }
            
            Clear();

            // AddEntry, never Add: Add is one of this method's callers. See AddEntry.
            foreach (T item in serializedItems)
            {
                AddEntry(item);
            }
        }
        
        public T[] ToArray()
        {
            if (_serializedItems != null)
                LoadSerializedData();
            
            T[] itemArr = new T[_size];
            int index = 0;

            HashSetIterator<T> iterator = GetEnumerator();

            while (iterator.MoveNext())
            {
                itemArr[index++] = iterator.Current;
            }

            return itemArr;
        }
        
        public HashSetIterator<T> GetEnumerator()
        {
            if (_serializedItems != null)
                LoadSerializedData();
            
            return new HashSetIterator<T>(_buckets);
        }
        
        public static HashSet<T> CreateFromArray(T[] items)
        {
            HashSet<T> hashSet = new HashSet<T>();

            foreach (T item in items)
            {
                hashSet.Add(item);
            }

            return hashSet;
        }
        
        public static HashSet<T> CreateFromList(System.Collections.Generic.List<T> items)
        {
            HashSet<T> hashSet = new HashSet<T>();

            foreach (T item in items)
            {
                hashSet.Add(item);
            }

            return hashSet;
        }
    }
}