using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Marks a method as an ECS-Lite system that operates on component data.
    ///
    /// Systems are the core update logic in ECS-Lite. They operate on component arrays
    /// directly to achieve optimal performance in Udon's constrained environment.
    /// </summary>
    /// <remarks>
    /// Systems receive component arrays as parameters and iterate over them in batches.
    /// This pattern eliminates per-entity method call overhead and enables cache-friendly access.
    ///
    /// The system signature determines which components it operates on:
    /// - Read-only: Pass arrays without ref (e.g., Vector3[] positions)
    /// - Read-write: Pass arrays with ref (e.g., ref Vector3[] positions)
    ///
    /// Systems are called with:
    /// - int count: Number of active entities to process
    /// - Component arrays: Parallel arrays for each required component type
    /// </remarks>
    /// <example>
    /// <code>
    /// public class BulletManager : UdonSharpBehaviour
    /// {
    ///     private CEWorld world;
    ///
    ///     [CESystem]
    ///     private void MovementSystem(int count, Vector3[] positions, Vector3[] velocities)
    ///     {
    ///         float dt = Time.deltaTime;
    ///         for (int i = 0; i &lt; count; i++)
    ///         {
    ///             positions[i] += velocities[i] * dt;
    ///         }
    ///     }
    ///
    ///     [CESystem(Order = 100)]
    ///     private void BoundsCheckSystem(int count, Vector3[] positions, bool[] alive)
    ///     {
    ///         for (int i = 0; i &lt; count; i++)
    ///         {
    ///             if (positions[i].magnitude > 1000f)
    ///             {
    ///                 alive[i] = false;
    ///             }
    ///         }
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Method)]
    public class CESystemAttribute : Attribute
    {
        /// <summary>
        /// Execution order for this system. Lower values execute first.
        /// Default is 0.
        /// </summary>
        /// <remarks>
        /// Use order to ensure proper system sequencing:
        /// - Input systems: -100 to -1
        /// - Core logic: 0 to 99
        /// - Rendering/output: 100+
        /// </remarks>
        public int Order { get; set; } = 0;

        /// <summary>
        /// Optional group name for organizing systems.
        /// Systems in the same group can be enabled/disabled together.
        /// </summary>
        public string Group { get; set; } = "";

        /// <summary>
        /// Whether this system should run in FixedUpdate instead of Update.
        /// </summary>
        public bool FixedUpdate { get; set; } = false;

        /// <summary>
        /// Whether this system is enabled by default.
        /// Disabled systems can be enabled at runtime via CEWorld.EnableSystem.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Optional batch size for processing. 0 means process all at once.
        /// Non-zero values enable time-sliced execution across multiple frames.
        /// </summary>
        /// <remarks>
        /// Use batching for expensive systems to maintain frame rate:
        /// <code>
        /// [CESystem(BatchSize = 100)]
        /// private void ExpensiveAISystem(int count, ...) { }
        /// </code>
        /// </remarks>
        public int BatchSize { get; set; } = 0;

        /// <summary>
        /// Creates a new CESystem attribute with default settings.
        /// </summary>
        public CESystemAttribute() { }

        /// <summary>
        /// Creates a new CESystem attribute with a specified order.
        /// </summary>
        /// <param name="order">Execution order (lower runs first).</param>
        public CESystemAttribute(int order)
        {
            Order = order;
        }
    }
}
