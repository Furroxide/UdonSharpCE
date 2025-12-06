using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Async
{
    /// <summary>
    /// Represents an asynchronous operation that can be awaited in Udon.
    ///
    /// At compile time, methods returning UdonTask are transformed into state machines
    /// that use SendCustomEventDelayedSeconds/Frames for continuation.
    /// </summary>
    /// <remarks>
    /// UdonTask provides a promise-like API for async operations in VRChat worlds.
    /// Unlike .NET Tasks, UdonTasks:
    /// - Cannot run in parallel (Udon is single-threaded)
    /// - Use frame-based scheduling via SendCustomEventDelayed*
    /// - Don't support true exception handling
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
        /// Creates a task that completes after the specified time delay.
        /// </summary>
        /// <param name="seconds">The number of seconds to delay.</param>
        /// <returns>A task that completes after the delay.</returns>
        /// <remarks>
        /// At compile time, await Delay() is transformed to:
        /// SendCustomEventDelayedSeconds(nameof(__MoveNext), seconds)
        /// </remarks>
        public static UdonTask Delay(float seconds)
        {
            // This is a marker method - the actual delay is implemented by the compiler
            // generating SendCustomEventDelayedSeconds calls.
            // At runtime, this just returns a task that the compiler uses for tracking.
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
        /// Equivalent to Delay(0) or DelayFrames(1).
        /// Use this to prevent blocking the main thread in long operations.
        /// </remarks>
        public static UdonTask Yield()
        {
            return DelayFrames(1);
        }

        /// <summary>
        /// Creates a task that completes after the specified number of frames.
        /// </summary>
        /// <param name="frames">The number of frames to delay (minimum 1).</param>
        /// <returns>A task that completes after the delay.</returns>
        /// <remarks>
        /// At compile time, await DelayFrames() is transformed to:
        /// SendCustomEventDelayedFrames(nameof(__MoveNext), frames)
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
        /// Creates a task that completes when all provided tasks have completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait for.</param>
        /// <returns>A task that completes when all tasks are complete.</returns>
        /// <remarks>
        /// WhenAll checks task completion each frame until all are done.
        /// Limited to 8 tasks due to VRChat parameter limits.
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

            // Return a pending task - compiler generates polling code
            return new UdonTask { _status = TaskStatus.WaitingForActivation };
        }

        /// <summary>
        /// Creates a task that completes when any of the provided tasks has completed.
        /// </summary>
        /// <param name="tasks">The tasks to wait for.</param>
        /// <returns>A task that completes when any task is complete.</returns>
        /// <remarks>
        /// WhenAny returns as soon as the first task completes.
        /// Other tasks continue running (cannot be canceled automatically).
        /// Limited to 8 tasks due to VRChat parameter limits.
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

            // Return a pending task - compiler generates polling code
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
