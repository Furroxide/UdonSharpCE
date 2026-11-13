using JetBrains.Annotations;

namespace UdonSharp.CE.Async
{
    /// <summary>
    /// Represents the current stage in the lifecycle of an UdonTask.
    /// </summary>
    [PublicAPI]
    public enum TaskStatus
    {
        /// <summary>
        /// The task has been initialized but has not yet been scheduled.
        /// </summary>
        Created = 0,

        /// <summary>
        /// The task is waiting to be activated and scheduled internally.
        /// </summary>
        WaitingForActivation = 1,

        /// <summary>
        /// The task has been scheduled for execution but has not yet begun executing.
        /// </summary>
        WaitingToRun = 2,

        /// <summary>
        /// The task is running but has not yet completed.
        /// </summary>
        Running = 3,

        /// <summary>
        /// The task completed execution successfully.
        /// </summary>
        RanToCompletion = 4,

        /// <summary>
        /// The task was canceled. Only <c>UdonTask.FromCanceled</c> produces this: cancellation in CE is
        /// cooperative, so a canceled async method simply returns and its machine completes normally.
        /// </summary>
        Canceled = 5,

        /// <summary>
        /// The task faulted. Only <c>FromError</c> and <c>FromException</c> produce this, and it is a value
        /// to read: nothing logs it or stops because of it, since Udon has no exception handling.
        /// </summary>
        Faulted = 6
    }
}
