
using System;
using UnityEngine;

#if !COMPILER_UDONSHARP
[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("UdonSharp.Editor")]
#endif

namespace UdonSharp.Lib.Internal.Collections
{
    internal class QueueIterator<T>
    {
        private Queue<T> _queue;
        private int _index;
        private T _current;
        
        public QueueIterator(Queue<T> queue)
        {
            _queue = queue;
            _index = -1;
            _current = default;
        }

        public void Reset()
        {
            _index = -1;
            _current = default;
        }
        
        public bool MoveNext()
        {
            Queue<T> queue = _queue;
            int size = queue._size;
            int index = _index + 1;
            
            if (index < size)
            {
                T[] items = queue._items;
                
                _index = index;
                _current = items[(queue._head + index) % items.Length];
                return true;
            }
            
            _index = size;
            _current = default;
            return false;
        }

        public T Current => _current;
    }
    
    // Implemented as a basic ring buffer
    internal class Queue<T>
    {
        internal T[] _items;
        internal int _size;
        internal int _head;
        private QueueIterator<T> _cachedIterator;
        
        public int Count => _size;
        
        public Queue()
        {
            _items = new T[8];
        }
        
        public Queue(int capacity)
        {
            _items = new T[capacity];
        }
        
        public void Enqueue(T item)
        {
            int size = _size;
            T[] itemArr = _items;
            
            if (size == itemArr.Length)
            {
                T[] newItems = new T[itemArr.Length * 2];
                
                int head = _head;
                
                // We copy the array in a way that the head is at the start of the new array
                if (head != 0)
                {
                    Array.Copy(itemArr, head, newItems, 0, size - head);
                    Array.Copy(itemArr, 0, newItems, size - head, head);
                }
                else
                {
                    Array.Copy(itemArr, 0, newItems, 0, size);
                }
                
                _items = newItems;
                itemArr = newItems;
                _head = 0;
            }
            
            int tail = (_head + size) % itemArr.Length;
            itemArr[tail] = item;
            _size = size + 1;
        }
        
        public T Dequeue()
        {
            int size = _size;
            
            if (size == 0)
            {
                Debug.LogError("Queue is empty");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return default;
            }
            
            T[] itemArr = _items;
            int head = _head;
            T item = itemArr[head];
            itemArr[head] = default;
            _head = (head + 1) % itemArr.Length;
            _size = size - 1;
            
            return item;
        }
        
        public bool TryDequeue(out T item)
        {
            int size = _size;
            
            if (size == 0)
            {
                item = default;
                return false;
            }
            
            T[] itemArr = _items;
            int head = _head;
            item = itemArr[head];
            itemArr[head] = default;
            _head = (head + 1) % itemArr.Length;
            _size = size - 1;
            
            return true;
        }
        
        public bool TryPeek(out T item)
        {
            int size = _size;
            
            if (size == 0)
            {
                item = default;
                return false;
            }
            
            item = _items[_head];
            return true;
        }
        
        public T Peek()
        {
            int size = _size;
            
            if (size == 0)
            {
                Debug.LogError("Queue is empty");
            #pragma warning disable CS0251
                (new object[0])[-1] = 0; // Crash
            #pragma warning disable CS0251
                return default;
            }
            
            return _items[_head];
        }
        
        public T[] ToArray()
        {
            int size = _size;
            T[] itemArr = new T[size];
            
            if (size == 0)
                return itemArr;
            
            T[] items = _items;
            int itemsLength = items.Length;
            
            int head = _head;
            
            if (head + size <= itemsLength)
            {
                Array.Copy(items, head, itemArr, 0, size);
            }
            else
            {
                Array.Copy(items, head, itemArr, 0, itemsLength - head);
                Array.Copy(items, 0, itemArr, itemsLength - head, size - (itemsLength - head));
            }
            
            return itemArr;
        }
        
        public bool Contains(T item)
        {
            QueueIterator<T> iterator = GetEnumerator();
            
            while (iterator.MoveNext())
            {
                if (item.Equals(iterator.Current))
                    return true;
            }
            
            return false;
        }
        
        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _size = 0;
            _head = 0;
        }

        #region Performance Helpers

        /// <summary>
        /// Allocation-free iteration over items in queue order.
        /// </summary>
        public void ForEach(Action<T> action)
        {
            if (action == null) return;

            T[] items = _items;
            int size = _size;
            int head = _head;
            int length = items.Length;

            for (int i = 0; i < size; i++)
            {
                action(items[(head + i) % length]);
            }
        }

        /// <summary>
        /// Allocation-free iteration with queue index (0 = front).
        /// </summary>
        public void ForEachWithIndex(Action<T, int> action)
        {
            if (action == null) return;

            T[] items = _items;
            int size = _size;
            int head = _head;
            int length = items.Length;

            for (int i = 0; i < size; i++)
            {
                action(items[(head + i) % length], i);
            }
        }

        /// <summary>
        /// Allocation-free iteration that stops when predicate returns false.
        /// </summary>
        public void ForEachUntil(Func<T, bool> predicate)
        {
            if (predicate == null) return;

            T[] items = _items;
            int size = _size;
            int head = _head;
            int length = items.Length;

            for (int i = 0; i < size; i++)
            {
                if (!predicate(items[(head + i) % length])) return;
            }
        }

        /// <summary>
        /// Get item without bounds checking. Index is relative to queue front.
        /// </summary>
        public T GetUnchecked(int index) => _items[(_head + index) % _items.Length];

        /// <summary>
        /// Set item without bounds checking. Index is relative to queue front.
        /// </summary>
        public void SetUnchecked(int index, T value) => _items[(_head + index) % _items.Length] = value;

        /// <summary>
        /// Direct access to backing array (ring buffer). Use Count and HeadIndex to navigate.
        /// </summary>
        public T[] GetBackingArray() => _items;

        /// <summary>
        /// Current head index within backing array (for advanced scenarios).
        /// </summary>
        public int HeadIndex => _head;

        #endregion

        public QueueIterator<T> GetEnumerator()
        {
            if (_cachedIterator == null)
                _cachedIterator = new QueueIterator<T>(this);
            else
                _cachedIterator.Reset();

            return _cachedIterator;
        }
    }
}
