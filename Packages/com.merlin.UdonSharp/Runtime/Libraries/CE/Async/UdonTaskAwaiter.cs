using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace UdonSharp.CE.Async
{
    /// <summary>
    /// Awaiter for UdonTask that enables the async/await pattern.
    /// 
    /// Implements INotifyCompletion as required by C# compiler for await expressions.
    /// The OnCompleted method is written to work both before and after UdonSharp's
    /// Action → CECallback transformation (avoids ?. operator on the continuation).
    /// 
    /// The actual continuation mechanism at runtime is handled by AsyncStateMachineTransformer,
    /// which converts await expressions to SendCustomEventDelayedSeconds/Frames calls.
    /// </summary>
    /// <remarks>
    /// This struct is marked with [CEPreserveAction] to prevent the Action → CECallback
    /// transformation on the OnCompleted method, which would break INotifyCompletion compliance.
    /// </remarks>
    [PublicAPI]
    [CEPreserveAction]
    public struct UdonTaskAwaiter : INotifyCompletion
    {
        private readonly UdonTask _task;

        /// <summary>
        /// Creates an awaiter for the specified task.
        /// </summary>
        public UdonTaskAwaiter(UdonTask task)
        {
            _task = task;
        }

        /// <summary>
        /// Gets whether the task has completed.
        /// Required by the awaiter pattern.
        /// </summary>
        public bool IsCompleted => _task.IsCompleted;

        /// <summary>
        /// Gets the result of the task. For UdonTask (non-generic), this validates completion.
        /// Required by the awaiter pattern.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if the task faulted.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the task was canceled.</exception>
        public void GetResult()
        {
            if (_task.IsFaulted)
            {
                throw new InvalidOperationException(_task.Error ?? "UdonTask faulted");
            }
            if (_task.IsCanceled)
            {
                throw new OperationCanceledException("UdonTask was canceled");
            }
        }

        /// <summary>
        /// Schedules the continuation to run when the task completes.
        /// Required by INotifyCompletion interface.
        /// 
        /// Note: This implementation works both as standard C# (Action) and after
        /// UdonSharp transformation (CECallback). The AsyncStateMachineTransformer
        /// replaces await expressions with SendCustomEventDelayed* calls at compile time,
        /// so this method primarily serves editor/play mode testing.
        /// </summary>
        /// <param name="continuation">The continuation to invoke when complete.</param>
        public void OnCompleted(Action continuation)
        {
            // IMPORTANT: Do not use ?. operator here!
            // After UdonSharp transforms Action → CECallback, ?. won't work on structs.
            // Using explicit .Invoke() works for both Action and CECallback.
            continuation.Invoke();
        }
    }

    /// <summary>
    /// Awaiter for UdonTask{T} that returns a result value.
    /// 
    /// Like UdonTaskAwaiter, implements INotifyCompletion for C# compiler compatibility
    /// while being compatible with UdonSharp's Action → CECallback transformation.
    /// </summary>
    /// <remarks>
    /// This struct is marked with [CEPreserveAction] to prevent the Action → CECallback
    /// transformation on the OnCompleted method, which would break INotifyCompletion compliance.
    /// </remarks>
    [PublicAPI]
    [CEPreserveAction]
    public struct UdonTaskAwaiter<T> : INotifyCompletion
    {
        private readonly UdonTask<T> _task;

        /// <summary>
        /// Creates an awaiter for the specified task.
        /// </summary>
        public UdonTaskAwaiter(UdonTask<T> task)
        {
            _task = task;
        }

        /// <summary>
        /// Gets whether the task has completed.
        /// Required by the awaiter pattern.
        /// </summary>
        public bool IsCompleted => _task.IsCompleted;

        /// <summary>
        /// Gets the result of the task.
        /// Required by the awaiter pattern.
        /// </summary>
        /// <returns>The task's result value.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the task faulted.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the task was canceled.</exception>
        public T GetResult()
        {
            if (_task.IsFaulted)
            {
                throw new InvalidOperationException(_task.Error ?? "UdonTask faulted");
            }
            if (_task.IsCanceled)
            {
                throw new OperationCanceledException("UdonTask was canceled");
            }
            return _task.Result;
        }

        /// <summary>
        /// Schedules the continuation to run when the task completes.
        /// Required by INotifyCompletion interface.
        /// </summary>
        /// <param name="continuation">The continuation to invoke when complete.</param>
        public void OnCompleted(Action continuation)
        {
            // IMPORTANT: Do not use ?. operator here!
            // After UdonSharp transforms Action → CECallback, ?. won't work on structs.
            continuation.Invoke();
        }
    }
}

















