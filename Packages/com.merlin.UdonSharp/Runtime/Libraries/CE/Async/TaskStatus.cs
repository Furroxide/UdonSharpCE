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
        /// The task acknowledged cancellation by throwing an OperationCanceledException.
        /// In Udon, this is handled gracefully without actual exceptions.
        /// </summary>
        Canceled = 5,

        /// <summary>
        /// The task completed due to an unhandled exception.
        /// In Udon, faulted tasks log their error and stop execution.
        /// </summary>
        Faulted = 6
    }
}
