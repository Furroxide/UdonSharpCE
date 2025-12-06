using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Async
{
    /// <summary>
    /// Propagates notification that async operations should be canceled.
    /// </summary>
    /// <remarks>
    /// Unlike .NET's CancellationToken, this is a simple struct that checks
    /// a shared cancellation state. It cannot truly interrupt Udon execution,
    /// but allows async methods to check for cancellation at await points.
    ///
    /// Usage:
    /// - Create a CancellationTokenSource
    /// - Pass its Token to async methods
    /// - Call source.Cancel() to request cancellation
    /// - Async methods should check token.IsCancellationRequested at key points
    /// </remarks>
    /// <example>
    /// <code>
    /// public class MyBehaviour : UdonSharpBehaviour
    /// {
    ///     private CancellationTokenSource _cts;
    ///
    ///     public async UdonTask LongOperation()
    ///     {
    ///         _cts = new CancellationTokenSource();
    ///
    ///         for (int i = 0; i &lt; 100; i++)
    ///         {
    ///             if (_cts.Token.IsCancellationRequested)
    ///             {
    ///                 Debug.Log("Operation canceled!");
    ///                 return;
    ///             }
    ///
    ///             await UdonTask.Delay(0.1f);
    ///             DoWork(i);
    ///         }
    ///     }
    ///
    ///     public void Cancel()
    ///     {
    ///         _cts?.Cancel();
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public struct CancellationToken
    {
        /// <summary>
        /// Internal reference to the source's cancellation state.
        /// This is set by CancellationTokenSource.
        /// </summary>
        internal CancellationTokenSource _source;

        /// <summary>
        /// Gets whether cancellation has been requested for this token.
        /// </summary>
        public bool IsCancellationRequested => _source != null && _source.IsCancellationRequested;

        /// <summary>
        /// Gets whether this token can be canceled (has a valid source).
        /// </summary>
        public bool CanBeCanceled => _source != null;

        /// <summary>
        /// Returns a CancellationToken that cannot be canceled.
        /// Use this as a default when no cancellation is needed.
        /// </summary>
        public static CancellationToken None => default;

        /// <summary>
        /// Throws if cancellation has been requested.
        /// In Udon, this logs a warning and returns - actual exceptions are not supported.
        /// </summary>
        public void ThrowIfCancellationRequested()
        {
            if (IsCancellationRequested)
            {
                Debug.LogWarning("[CE.Async] Operation was canceled");
            }
        }
    }

    /// <summary>
    /// Signals to a CancellationToken that it should be canceled.
    /// </summary>
    /// <remarks>
    /// Create a CancellationTokenSource, pass its Token to async methods,
    /// and call Cancel() when you want to request cancellation.
    ///
    /// Note: In Udon, cancellation is cooperative - async methods must
    /// check IsCancellationRequested and handle it appropriately.
    /// </remarks>
    /// <example>
    /// <code>
    /// private CancellationTokenSource _downloadCts;
    ///
    /// public void StartDownload()
    /// {
    ///     _downloadCts = new CancellationTokenSource();
    ///     DownloadAsync(_downloadCts.Token);
    /// }
    ///
    /// public void CancelDownload()
    /// {
    ///     _downloadCts?.Cancel();
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public class CancellationTokenSource
    {
        private bool _isCanceled;

        /// <summary>
        /// Gets whether cancellation has been requested.
        /// </summary>
        public bool IsCancellationRequested => _isCanceled;

        /// <summary>
        /// Gets a CancellationToken associated with this source.
        /// </summary>
        public CancellationToken Token => new CancellationToken { _source = this };

        /// <summary>
        /// Communicates a request for cancellation.
        /// </summary>
        public void Cancel()
        {
            _isCanceled = true;
        }

        /// <summary>
        /// Resets the cancellation state, allowing the token to be reused.
        /// </summary>
        /// <remarks>
        /// Use with caution - any code checking the old token will see the reset state.
        /// Generally, create a new CancellationTokenSource instead of resetting.
        /// </remarks>
        public void Reset()
        {
            _isCanceled = false;
        }
    }
}
