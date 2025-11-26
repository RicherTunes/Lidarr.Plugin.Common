using System;
using System.Collections.Generic;

namespace Lidarr.Plugin.Common.Services.Resilience
{
    /// <summary>
    /// A thread-safe circular buffer (ring buffer) for tracking sliding window data.
    /// Used by circuit breakers for failure tracking within a time window.
    /// </summary>
    /// <typeparam name="T">The type of elements in the buffer.</typeparam>
    /// <remarks>
    /// This implementation provides:
    /// - O(1) add operations
    /// - Thread-safe access via locking
    /// - Automatic overwriting of oldest elements when capacity is reached
    /// - Efficient memory usage with fixed-size backing array
    ///
    /// Common use cases:
    /// - Tracking recent failures for circuit breaker decisions
    /// - Collecting samples for rate calculation
    /// - Maintaining a rolling history of events
    /// </remarks>
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private readonly object _lock = new object();
        private int _head;
        private int _count;

        /// <summary>
        /// Gets the maximum capacity of the buffer.
        /// </summary>
        public int Capacity { get; }

        /// <summary>
        /// Gets the current number of elements in the buffer.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _count;
                }
            }
        }

        /// <summary>
        /// Gets whether the buffer is empty.
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                {
                    return _count == 0;
                }
            }
        }

        /// <summary>
        /// Gets whether the buffer is full.
        /// </summary>
        public bool IsFull
        {
            get
            {
                lock (_lock)
                {
                    return _count == Capacity;
                }
            }
        }

        /// <summary>
        /// Creates a new circular buffer with the specified capacity.
        /// </summary>
        /// <param name="capacity">Maximum number of elements the buffer can hold.</param>
        /// <exception cref="ArgumentOutOfRangeException">When capacity is less than 1.</exception>
        public CircularBuffer(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");

            Capacity = capacity;
            _buffer = new T[capacity];
            _head = 0;
            _count = 0;
        }

        /// <summary>
        /// Adds an element to the buffer, overwriting the oldest element if full.
        /// </summary>
        /// <param name="item">The element to add.</param>
        public void Add(T item)
        {
            lock (_lock)
            {
                _buffer[_head] = item;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity)
                    _count++;
            }
        }

        /// <summary>
        /// Clears all elements from the buffer.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                _head = 0;
                _count = 0;
            }
        }

        /// <summary>
        /// Returns all elements in the buffer as a list, from oldest to newest.
        /// </summary>
        /// <returns>A list containing all elements in chronological order.</returns>
        public List<T> ToList()
        {
            lock (_lock)
            {
                var result = new List<T>(_count);
                if (_count == 0)
                    return result;

                // Calculate starting position (oldest element)
                int start = _count < Capacity ? 0 : _head;

                for (int i = 0; i < _count; i++)
                {
                    int index = (start + i) % Capacity;
                    result.Add(_buffer[index]);
                }

                return result;
            }
        }

        /// <summary>
        /// Returns all elements as an array, from oldest to newest.
        /// </summary>
        /// <returns>An array containing all elements in chronological order.</returns>
        public T[] ToArray()
        {
            return ToList().ToArray();
        }

        /// <summary>
        /// Counts elements matching a predicate.
        /// </summary>
        /// <param name="predicate">The condition to match.</param>
        /// <returns>Number of elements matching the predicate.</returns>
        public int CountWhere(Func<T, bool> predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            lock (_lock)
            {
                if (_count == 0)
                    return 0;

                int matchCount = 0;
                int start = _count < Capacity ? 0 : _head;

                for (int i = 0; i < _count; i++)
                {
                    int index = (start + i) % Capacity;
                    if (predicate(_buffer[index]))
                        matchCount++;
                }

                return matchCount;
            }
        }

        /// <summary>
        /// Gets the most recently added element without removing it.
        /// </summary>
        /// <returns>The most recent element.</returns>
        /// <exception cref="InvalidOperationException">When buffer is empty.</exception>
        public T PeekNewest()
        {
            lock (_lock)
            {
                if (_count == 0)
                    throw new InvalidOperationException("Buffer is empty.");

                int index = (_head - 1 + Capacity) % Capacity;
                return _buffer[index];
            }
        }

        /// <summary>
        /// Tries to get the most recently added element.
        /// </summary>
        /// <param name="item">The most recent element if available.</param>
        /// <returns>True if an element was retrieved; false if buffer is empty.</returns>
        public bool TryPeekNewest(out T item)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }

                int index = (_head - 1 + Capacity) % Capacity;
                item = _buffer[index];
                return true;
            }
        }

        /// <summary>
        /// Gets the oldest element in the buffer without removing it.
        /// </summary>
        /// <returns>The oldest element.</returns>
        /// <exception cref="InvalidOperationException">When buffer is empty.</exception>
        public T PeekOldest()
        {
            lock (_lock)
            {
                if (_count == 0)
                    throw new InvalidOperationException("Buffer is empty.");

                int start = _count < Capacity ? 0 : _head;
                return _buffer[start];
            }
        }

        /// <summary>
        /// Tries to get the oldest element in the buffer.
        /// </summary>
        /// <param name="item">The oldest element if available.</param>
        /// <returns>True if an element was retrieved; false if buffer is empty.</returns>
        public bool TryPeekOldest(out T item)
        {
            lock (_lock)
            {
                if (_count == 0)
                {
                    item = default;
                    return false;
                }

                int start = _count < Capacity ? 0 : _head;
                item = _buffer[start];
                return true;
            }
        }
    }
}
