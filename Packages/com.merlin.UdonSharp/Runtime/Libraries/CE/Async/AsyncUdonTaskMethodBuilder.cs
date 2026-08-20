using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace UdonSharp.CE.Async
{
    /// <summary>
    /// Async method builder for UdonTask.
    /// This enables 'async UdonTask' method declarations.
    ///
    /// Note: nothing in a VRChat world ever runs this builder. CE's async transform replaces every
    /// 'async UdonTask' method with a hand-built state machine before Roslyn sees the method, so Roslyn
    /// never emits a builder call for it. The type exists so the same source still compiles as ordinary
    /// C# in Assembly-CSharp, where Unity and editor tooling do compile it.
    /// </summary>
    [PublicAPI]
    public struct AsyncUdonTaskMethodBuilder
    {
        private UdonTask _task;

        /// <summary>
        /// Creates a new builder instance.
        /// </summary>
        public static AsyncUdonTaskMethodBuilder Create()
        {
            return new AsyncUdonTaskMethodBuilder
            {
                _task = new UdonTask()
            };
        }

        /// <summary>
        /// Begins running the async state machine.
        /// </summary>
        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        /// <summary>
        /// Associates the builder with the state machine.
        /// </summary>
        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            // No-op for struct builders
        }

        /// <summary>
        /// Marks the task as completed successfully.
        /// </summary>
        public void SetResult()
        {
            _task = UdonTask.CompletedTask;
        }

        /// <summary>
        /// Marks the task as faulted with an exception.
        /// </summary>
        public void SetException(Exception exception)
        {
            _task = UdonTask.FromException(exception);
        }

        /// <summary>
        /// Gets the task for this builder.
        /// </summary>
        public UdonTask Task => _task;

        /// <summary>
        /// Schedules the state machine to await the given awaiter.
        ///
        /// Note: only the Assembly-CSharp compilation reaches this. CE's transform has already removed
        /// every await from the Udon compilation, so continuing the state machine straight away here
        /// cannot affect world behaviour.
        /// </summary>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        /// <summary>
        /// Schedules the state machine to await the given awaiter (unsafe version).
        /// </summary>
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // Same as AwaitOnCompleted - transformed at compile time for Udon
            stateMachine.MoveNext();
        }

        /// <summary>
        /// Schedules the state machine to await a UdonTaskAwaiter specifically.
        /// This overload avoids the INotifyCompletion constraint.
        /// </summary>
        public void AwaitOnCompleted<TStateMachine>(
            ref UdonTaskAwaiter awaiter, ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            // For Udon-specific awaiters, just continue the state machine
            stateMachine.MoveNext();
        }
    }

    /// <summary>
    /// Async method builder for UdonTask{T}.
    /// This enables 'async UdonTask{T}' method declarations with return values.
    /// </summary>
    [PublicAPI]
    public struct AsyncUdonTaskMethodBuilder<T>
    {
        private UdonTask<T> _task;

        /// <summary>
        /// Creates a new builder instance.
        /// </summary>
        public static AsyncUdonTaskMethodBuilder<T> Create()
        {
            return new AsyncUdonTaskMethodBuilder<T>
            {
                _task = new UdonTask<T>()
            };
        }

        /// <summary>
        /// Begins running the async state machine.
        /// </summary>
        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        /// <summary>
        /// Associates the builder with the state machine.
        /// </summary>
        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            // No-op for struct builders
        }

        /// <summary>
        /// Marks the task as completed successfully with the given result.
        /// </summary>
        public void SetResult(T result)
        {
            _task = UdonTask<T>.FromResult(result);
        }

        /// <summary>
        /// Marks the task as faulted with an exception.
        /// </summary>
        public void SetException(Exception exception)
        {
            _task = UdonTask<T>.FromException(exception);
        }

        /// <summary>
        /// Gets the task for this builder.
        /// </summary>
        public UdonTask<T> Task => _task;

        /// <summary>
        /// Schedules the state machine to await the given awaiter.
        /// </summary>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // Fallback for editor testing - transformed at compile time for Udon
            stateMachine.MoveNext();
        }

        /// <summary>
        /// Schedules the state machine to await the given awaiter (unsafe version).
        /// </summary>
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // Fallback for editor testing - transformed at compile time for Udon
            stateMachine.MoveNext();
        }

        /// <summary>
        /// Schedules the state machine to await a UdonTaskAwaiter{T} specifically.
        /// This overload avoids the INotifyCompletion constraint.
        /// </summary>
        public void AwaitOnCompleted<TStateMachine>(
            ref UdonTaskAwaiter<T> awaiter, ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }
    }
}





























