using System;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Time-sliced batch processor for distributing expensive work across frames.
    ///
    /// Enables processing large data sets without causing frame drops by
    /// limiting work per frame based on a configurable batch size.
    /// </summary>
    /// <remarks>
    /// Use BatchProcessor for:
    /// - Initial world setup (spawning many objects)
    /// - AI updates for large numbers of entities
    /// - Expensive calculations that don't need every-frame updates
    /// - Progressive loading/generation
    ///
    /// The processor maintains its position between frames, and keeps it when
    /// the item count changes: a pass carries on from where it was against the
    /// new count instead of starting over.
    ///
    /// Every Process method takes a delegate. Udon has no way to represent one, and CE inlines a lambda
    /// only where it is passed to List&lt;T&gt;.ForEach or one of CEList's ForEach helpers, so this class
    /// is for editor and plain C# code. An UdonSharpBehaviour that wants time-slicing keeps the index itself
    /// (a field, advanced by a fixed step each Update) and calls its per-item method directly.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Editor / plain C# usage
    /// public class AIManager
    /// {
    ///     // Process 50 enemies per call
    ///     private readonly BatchProcessor aiProcessor = new BatchProcessor(50);
    ///     private int enemyCount;
    ///
    ///     public void Step()
    ///     {
    ///         // enemyCount may differ from the last call; the pass continues regardless
    ///         aiProcessor.ProcessContinuous(enemyCount, UpdateEnemy);
    ///     }
    ///
    ///     void UpdateEnemy(int index)
    ///     {
    ///         // Expensive AI logic for enemy at index
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public class BatchProcessor
    {
        #region Fields

        /// <summary>
        /// Number of items to process per batch (per frame).
        /// </summary>
        private int _batchSize;

        /// <summary>
        /// Current position in the processing sequence.
        /// </summary>
        private int _currentIndex;

        /// <summary>
        /// Total number of items to process.
        /// </summary>
        private int _totalItems;

        /// <summary>
        /// Whether a processing pass is currently active.
        /// </summary>
        private bool _isProcessing;

        /// <summary>
        /// Number of completed passes.
        /// </summary>
        private int _completedPasses;

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the batch size (items per frame).
        /// </summary>
        public int BatchSize
        {
            get => _batchSize;
            set => _batchSize = Mathf.Max(1, value);
        }

        /// <summary>
        /// Gets the current processing index.
        /// </summary>
        public int CurrentIndex => _currentIndex;

        /// <summary>
        /// Gets whether a processing pass is active.
        /// </summary>
        public bool IsProcessing => _isProcessing;

        /// <summary>
        /// Gets the completion percentage (0-1) of the current pass.
        /// </summary>
        public float Progress => _totalItems > 0 ? (float)_currentIndex / _totalItems : 0f;

        /// <summary>
        /// Gets the number of completed processing passes.
        /// </summary>
        public int CompletedPasses => _completedPasses;

        /// <summary>
        /// Gets the remaining items in the current pass.
        /// </summary>
        public int Remaining => _totalItems - _currentIndex;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new batch processor with the specified batch size.
        /// </summary>
        /// <param name="batchSize">Number of items to process per frame.</param>
        public BatchProcessor(int batchSize = 100)
        {
            _batchSize = Mathf.Max(1, batchSize);
            _currentIndex = 0;
            _totalItems = 0;
            _isProcessing = false;
            _completedPasses = 0;
        }

        #endregion

        #region Processing

        /// <summary>
        /// Processes one batch of items using the provided action.
        /// </summary>
        /// <param name="totalItems">Total number of items to process.</param>
        /// <param name="processItem">Action called for each item with its index.</param>
        /// <returns>True if processing is complete, false if more batches remain.</returns>
        public bool Process(int totalItems, Action<int> processItem)
        {
            if (processItem == null) return true;

            // Start new pass if needed
            if (!_isProcessing)
            {
                _currentIndex = 0;
                _isProcessing = true;
            }

            SyncTotalItems(totalItems);

            if (_currentIndex >= _totalItems)
            {
                _isProcessing = false;
                _completedPasses++;
                return true;
            }

            // Process one batch
            int endIndex = Mathf.Min(_currentIndex + _batchSize, _totalItems);

            for (int i = _currentIndex; i < endIndex; i++)
            {
                processItem(i);
            }

            _currentIndex = endIndex;

            // Check if complete
            if (_currentIndex >= _totalItems)
            {
                _isProcessing = false;
                _completedPasses++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Processes one batch of items using the provided action with early exit.
        /// </summary>
        /// <param name="totalItems">Total number of items to process.</param>
        /// <param name="processItem">Function called for each item. Return false to stop.</param>
        /// <returns>True if processing is complete, false if more batches remain.</returns>
        public bool ProcessWhile(int totalItems, Func<int, bool> processItem)
        {
            if (processItem == null) return true;

            if (!_isProcessing)
            {
                _currentIndex = 0;
                _isProcessing = true;
            }

            SyncTotalItems(totalItems);

            if (_currentIndex >= _totalItems)
            {
                _isProcessing = false;
                _completedPasses++;
                return true;
            }

            int endIndex = Mathf.Min(_currentIndex + _batchSize, _totalItems);

            for (int i = _currentIndex; i < endIndex; i++)
            {
                if (!processItem(i))
                {
                    // Early exit requested
                    _isProcessing = false;
                    _completedPasses++;
                    _currentIndex = _totalItems;
                    return true;
                }
            }

            _currentIndex = endIndex;

            if (_currentIndex >= _totalItems)
            {
                _isProcessing = false;
                _completedPasses++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Adopts the caller's current item count for the pass in progress, without restarting it.
        /// </summary>
        /// <remarks>
        /// The count is expected to move between frames -- that is what spreading work over frames
        /// means when the items are entities being spawned and destroyed. A changed count used to send
        /// the pass back to index 0, so while the count changed every frame only the first batch was
        /// ever processed: everything past it starved, the pass never completed, and CompletedPasses
        /// stayed at 0. The position is kept and only pulled back when the collection shrank beneath it,
        /// in which case the pass is simply over.
        /// </remarks>
        private void SyncTotalItems(int totalItems)
        {
            _totalItems = totalItems;

            if (_currentIndex > _totalItems)
            {
                _currentIndex = Mathf.Max(0, _totalItems);
            }
        }

        /// <summary>
        /// Resets the processor to start from the beginning.
        /// </summary>
        public void Reset()
        {
            _currentIndex = 0;
            _isProcessing = false;
        }

        /// <summary>
        /// Resets the processor and pass counter.
        /// </summary>
        public void ResetAll()
        {
            _currentIndex = 0;
            _isProcessing = false;
            _completedPasses = 0;
        }

        #endregion

        #region Continuous Processing

        /// <summary>
        /// Processes continuously, automatically restarting when complete.
        /// Useful for cyclic updates (e.g., AI that processes all entities repeatedly).
        /// </summary>
        /// <param name="totalItems">Total number of items to process.</param>
        /// <param name="processItem">Action called for each item with its index.</param>
        /// <returns>True if a complete pass just finished.</returns>
        public bool ProcessContinuous(int totalItems, Action<int> processItem)
        {
            bool completed = Process(totalItems, processItem);

            if (completed)
            {
                // Immediately start next pass
                _currentIndex = 0;
                _isProcessing = true;
            }

            return completed;
        }

        #endregion
    }
}
