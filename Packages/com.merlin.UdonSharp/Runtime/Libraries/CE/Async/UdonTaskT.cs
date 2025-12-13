using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace UdonSharp.CE.Async
{
    /// <summary>
    /// Represents an asynchronous operation that returns a value.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <remarks>
    /// UdonTask{T} extends UdonTask to support returning values from async methods.
    /// The result is stored in a generated field and available after completion.
    ///
    /// Note: Due to Udon limitations with generics, some complex generic scenarios
    /// may require using UdonTask with out parameters instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class DataLoader : UdonSharpBehaviour
    /// {
    ///     public async UdonTask<int> CalculateAsync(int input)
    ///     {
    ///         await UdonTask.Delay(1f);  // Simulate async work
    ///         return input * 2;
    ///     }
    ///
    ///     public async UdonTask UseResult()
    ///     {
    ///         var task = CalculateAsync(5);
    ///         await task;
    ///
    ///         int result = task.Result;  // 10
    ///         Debug.Log($"Result: {result}");
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AsyncMethodBuilder(typeof(AsyncUdonTaskMethodBuilder<>))]
    public struct UdonTask<T>
    {
        /// <summary>
        /// Internal task identifier for tracking and debugging.
        /// </summary>
        internal int _taskId;

        /// <summary>
        /// Current status of the task.
        /// </summary>
        internal TaskStatus _status;

        /// <summary>
        /// The result value of the task.
        /// Only valid when Status is RanToCompletion.
        /// </summary>
        internal T _result;

        /// <summary>
        /// The UdonSharpBehaviour that owns this task.
        /// </summary>
        internal UdonSharpBehaviour _owner;

        /// <summary>
        /// Optional error message if the task faulted.
        /// </summary>
        internal string _error;

        /// <summary>
        /// Gets the current status of the task.
        /// </summary>
        public TaskStatus Status => _status;

        /// <summary>
        /// Gets whether the task has completed (successfully, canceled, or faulted).
        /// </summary>
        public bool IsCompleted => _status >= TaskStatus.RanToCompletion;

        /// <summary>
        /// Gets whether the task completed successfully.
        /// </summary>
        public bool IsCompletedSuccessfully => _status == TaskStatus.RanToCompletion;

        /// <summary>
        /// Gets whether the task was canceled.
        /// </summary>
        public bool IsCanceled => _status == TaskStatus.Canceled;

        /// <summary>
        /// Gets whether the task faulted (encountered an error).
        /// </summary>
        public bool IsFaulted => _status == TaskStatus.Faulted;

        /// <summary>
        /// Gets the result of the task.
        /// Only valid when IsCompletedSuccessfully is true.
        /// </summary>
        /// <remarks>
        /// Accessing Result on an incomplete, canceled, or faulted task
        /// returns the default value for T and logs a warning.
        /// </remarks>
        public T Result
        {
            get
            {
                if (!IsCompletedSuccessfully)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[CE.Async] Accessing Result on task with status {_status}. " +
                        "Returning default value.");
                }
                return _result;
            }
        }

        /// <summary>
        /// Gets the error message if the task faulted.
        /// </summary>
        public string Error => _error;

        /// <summary>
        /// Creates a completed task with the specified result.
        /// </summary>
        /// <param name="result">The result value.</param>
        /// <returns>A completed task containing the result.</returns>
        public static UdonTask<T> FromResult(T result)
        {
            return new UdonTask<T>
            {
                _status = TaskStatus.RanToCompletion,
                _result = result
            };
        }

        /// <summary>
        /// Creates a canceled task.
        /// </summary>
        /// <returns>A task in the Canceled state.</returns>
        public static UdonTask<T> FromCanceled()
        {
            return new UdonTask<T> { _status = TaskStatus.Canceled };
        }

        /// <summary>
        /// Creates a faulted task with the specified error.
        /// </summary>
        /// <param name="error">The error message.</param>
        /// <returns>A task in the Faulted state.</returns>
        public static UdonTask<T> FromError(string error)
        {
            return new UdonTask<T>
            {
                _status = TaskStatus.Faulted,
                _error = error
            };
        }

        /// <summary>
        /// Creates a faulted task from an exception.
        /// </summary>
        /// <param name="exception">The exception that caused the fault.</param>
        /// <returns>A task in the Faulted state.</returns>
        public static UdonTask<T> FromException(Exception exception)
        {
            return new UdonTask<T>
            {
                _status = TaskStatus.Faulted,
                _error = exception?.Message ?? "Unknown error"
            };
        }

        /// <summary>
        /// Gets an awaiter for this task.
        /// This enables the async/await pattern for UdonTask{T}.
        /// </summary>
        /// <returns>An awaiter that can be used to await this task.</returns>
        public UdonTaskAwaiter<T> GetAwaiter()
        {
            return new UdonTaskAwaiter<T>(this);
        }

        /// <summary>
        /// Marks this task as completed with the specified result.
        /// </summary>
        internal void SetResult(T result)
        {
            _result = result;
            _status = TaskStatus.RanToCompletion;
        }

        /// <summary>
        /// Marks this task as canceled.
        /// </summary>
        internal void SetCanceled()
        {
            _status = TaskStatus.Canceled;
        }

        /// <summary>
        /// Marks this task as faulted with the specified error.
        /// </summary>
        internal void SetFaulted(string error)
        {
            _status = TaskStatus.Faulted;
            _error = error;
        }

        /// <summary>
        /// Implicit conversion to non-generic UdonTask.
        /// Allows using UdonTask{T} where UdonTask is expected.
        /// </summary>
        public static implicit operator UdonTask(UdonTask<T> task)
        {
            return new UdonTask
            {
                _taskId = task._taskId,
                _status = task._status,
                _owner = task._owner,
                _error = task._error
            };
        }
    }
}
