using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Async
{
    /// <summary>
    /// Represents an asynchronous operation that can be awaited in Udon.
    ///
    /// At compile time, each method declared <c>async UdonTask</c> on an UdonSharpBehaviour is lowered
    /// into a state machine that resumes itself with SendCustomEventDelayedSeconds/Frames.
    /// </summary>
    /// <remarks>
    /// UdonTask is fire-and-forget, not a handle. It is a value type with nowhere to register a
    /// continuation, so the struct a caller receives is a snapshot of the moment the call returned and
    /// never changes afterwards. Only an <c>await</c> inside another <c>async UdonTask</c> method observes
    /// completion, because CE wires that one up at compile time; awaiting a UdonTask from anywhere else is
    /// a compile error rather than an await that resumes immediately.
    ///
    /// Unlike .NET Tasks, UdonTasks:
    /// - Cannot run in parallel (Udon is single-threaded)
    /// - Use frame-based scheduling via SendCustomEventDelayed*
    /// - Don't support true exception handling
    /// - Cannot have two calls to the same async method in flight at once; the state machine's locals live
    ///   in shared fields, so CE refuses and logs the second call
    ///
    /// The async transformation generates:
    /// - State fields for the state machine
    /// - Hoisted local variables as instance fields
    /// - A MoveNext continuation method
    /// </remarks>
    /// <example>
    /// <code>
    /// public class CutsceneController : UdonSharpBehaviour
    /// {
    ///     public async UdonTask PlayCutscene()
    ///     {
    ///         // Fade out
    ///         await FadeScreen(1f, Color.black);
    ///
    ///         // Wait 2 seconds
    ///         await UdonTask.Delay(2f);
    ///
    ///         // Show title
    ///         ShowTitle();
    ///         await UdonTask.Delay(3f);
    ///
    ///         // Fade in
    ///         await FadeScreen(1f, Color.clear);
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AsyncMethodBuilder(typeof(AsyncUdonTaskMethodBuilder))]
    public struct UdonTask
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
        /// The UdonSharpBehaviour that owns this task (for delayed event calls).
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
        /// Gets the error message if the task faulted.
        /// </summary>
        public string Error => _error;

        /// <summary>
        /// Returns a completed task.
        /// Use this when you need to return immediately from an async method.
        /// </summary>
        public static UdonTask CompletedTask => new UdonTask { _status = TaskStatus.RanToCompletion };

        /// <summary>
        /// A task that has started and has not finished yet.
        /// </summary>
        /// <remarks>
        /// Returned by the entry point CE generates for an <c>async UdonTask</c> method that suspended on
        /// its first await. UdonTask is a value type with nowhere to register a continuation, so this is a
        /// snapshot of the moment the call returned and never updates -- it says "started", not "watch me".
        /// It exists so a caller inspecting <see cref="Status"/> is not told a running sequence already
        /// finished; to react to completion, have the async method set a field or call a method at its end.
        /// </remarks>
        public static UdonTask Started => new UdonTask { _status = TaskStatus.Running };

        /// <summary>
        /// Creates a task that completes after the specified time delay.
        /// </summary>
        /// <param name="seconds">The number of seconds to delay.</param>
        /// <returns>A marker task. Only an await of it waits.</returns>
        /// <remarks>
        /// <c>await UdonTask.Delay(seconds)</c> in an <c>async UdonTask</c> method is lowered to
        /// <c>SendCustomEventDelayedSeconds("__Name_MoveNext", seconds, EventTiming.PostLateUpdate)</c>.
        /// </remarks>
        public static UdonTask Delay(float seconds)
        {
            // A lowered await never calls this: CE replaces the whole await with the delayed event. This
            // body only runs for a Delay that is not awaited, and then nothing waits for it.
            return new UdonTask
            {
                _status = TaskStatus.WaitingForActivation
            };
        }

        /// <summary>
        /// Creates a task that yields execution until the next frame.
        /// </summary>
        /// <returns>A task that completes on the next frame.</returns>
        /// <remarks>
        /// Equivalent to DelayFrames(1): an awaited Yield is lowered to
        /// <c>SendCustomEventDelayedFrames("__Name_MoveNext", 1, EventTiming.PostLateUpdate)</c>.
        /// Use this to spread long work across frames.
        /// </remarks>
        public static UdonTask Yield()
        {
            return DelayFrames(1);
        }

        /// <summary>
        /// Creates a task that completes after the specified number of frames.
        /// </summary>
        /// <param name="frames">The number of frames to delay. Pass 1 or more: an awaited DelayFrames hands
        /// the value to SendCustomEventDelayedFrames as written, and the clamp below never runs for it.</param>
        /// <returns>A marker task. Only an await of it waits.</returns>
        /// <remarks>
        /// <c>await UdonTask.DelayFrames(frames)</c> in an <c>async UdonTask</c> method is lowered to
        /// <c>SendCustomEventDelayedFrames("__Name_MoveNext", frames, EventTiming.PostLateUpdate)</c>.
        /// </remarks>
        public static UdonTask DelayFrames(int frames)
        {
            if (frames < 1) frames = 1;
            return new UdonTask
            {
                _status = TaskStatus.WaitingForActivation
            };
        }

        /// <summary>
        /// Combines the statuses of tasks that have already finished.
        /// </summary>
        /// <param name="tasks">The tasks to combine.</param>
        /// <returns>Faulted, canceled or completed when every input already is; otherwise a pending task.</returns>
        /// <remarks>
        /// Not awaitable: <c>await UdonTask.WhenAll(...)</c> is CE0025, because nothing would ever resume
        /// it. A UdonTask is a snapshot that never updates, so an input that is still pending here stays
        /// pending, and so does the result. Use this to inspect stored task values, not to wait for them.
        /// </remarks>
        public static UdonTask WhenAll(params UdonTask[] tasks)
        {
            if (tasks == null || tasks.Length == 0)
                return CompletedTask;

            // Check if all tasks are already complete
            bool allComplete = true;
            bool anyFaulted = false;
            bool anyCanceled = false;

            for (int i = 0; i < tasks.Length; i++)
            {
                if (!tasks[i].IsCompleted)
                {
                    allComplete = false;
                }
                else if (tasks[i].IsFaulted)
                {
                    anyFaulted = true;
                }
                else if (tasks[i].IsCanceled)
                {
                    anyCanceled = true;
                }
            }

            if (allComplete)
            {
                if (anyFaulted)
                {
                    return new UdonTask { _status = TaskStatus.Faulted, _error = "One or more tasks faulted" };
                }
                if (anyCanceled)
                {
                    return new UdonTask { _status = TaskStatus.Canceled };
                }
                return CompletedTask;
            }

            // Some input has not finished. No UdonTask ever updates, so this result never will either.
            return new UdonTask { _status = TaskStatus.WaitingForActivation };
        }

        /// <summary>
        /// Returns the first of the given tasks that has already finished.
        /// </summary>
        /// <param name="tasks">The tasks to look through.</param>
        /// <returns>The first input whose status is final, or a pending task when none is.</returns>
        /// <remarks>
        /// Not awaitable: <c>await UdonTask.WhenAny(...)</c> is CE0025, because nothing would ever resume
        /// it. A UdonTask is a snapshot that never updates, so a pending result here stays pending.
        /// </remarks>
        public static UdonTask WhenAny(params UdonTask[] tasks)
        {
            if (tasks == null || tasks.Length == 0)
                return CompletedTask;

            // Check if any task is already complete
            for (int i = 0; i < tasks.Length; i++)
            {
                if (tasks[i].IsCompleted)
                {
                    return tasks[i];
                }
            }

            // None has finished. No UdonTask ever updates, so this result never will either.
            return new UdonTask { _status = TaskStatus.WaitingForActivation };
        }

        /// <summary>
        /// Creates a canceled task.
        /// </summary>
        /// <returns>A task in the Canceled state.</returns>
        public static UdonTask FromCanceled()
        {
            return new UdonTask { _status = TaskStatus.Canceled };
        }

        /// <summary>
        /// Creates a faulted task with the specified error.
        /// </summary>
        /// <param name="error">The error message.</param>
        /// <returns>A task in the Faulted state.</returns>
        public static UdonTask FromError(string error)
        {
            return new UdonTask
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
        public static UdonTask FromException(Exception exception)
        {
            return new UdonTask
            {
                _status = TaskStatus.Faulted,
                _error = exception?.Message ?? "Unknown error"
            };
        }

        /// <summary>
        /// Gets an awaiter for this task.
        /// This enables the async/await pattern for UdonTask.
        /// </summary>
        /// <returns>An awaiter that can be used to await this task.</returns>
        public UdonTaskAwaiter GetAwaiter()
        {
            return new UdonTaskAwaiter(this);
        }

        /// <summary>
        /// Marks this task as completed.
        /// </summary>
        internal void SetCompleted()
        {
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
    }
}
