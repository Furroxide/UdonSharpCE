using System;

namespace UdonSharp.CE
{
    /// <summary>
    /// Disables all CE compile-time optimizations for the marked member or class.
    /// Use when you need exact control over generated code.
    /// </summary>
    /// <example>
    /// [CENoOptimize]
    /// public class LegacyBehaviour : UdonSharpBehaviour
    /// {
    ///     // Code compiles exactly as written, no CE transformations
    /// }
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public class CENoOptimizeAttribute : Attribute
    {
    }

    /// <summary>
    /// Prevents a method from being inlined by the tiny method inliner.
    /// Use when method call semantics are important (e.g., for debugging or profiling).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CENoInlineAttribute : Attribute
    {
    }

    /// <summary>
    /// Forces a method to be inlined at all call sites.
    /// Use for performance-critical methods that the optimizer doesn't automatically inline.
    /// </summary>
    /// <example>
    /// [CEInline]
    /// private float CalculateWeight(float distance) => 1f / (distance * distance);
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CEInlineAttribute : Attribute
    {
    }

    /// <summary>
    /// Prevents a loop from being unrolled by the small loop unrolling optimizer.
    /// Use when loop structure must be preserved.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CENoUnrollAttribute : Attribute
    {
    }

    /// <summary>
    /// Forces loop unrolling even when the optimizer's automatic criteria aren't met.
    /// </summary>
    /// <example>
    /// [CEUnroll(maxIterations: 8)]
    /// void ProcessOctants()
    /// {
    ///     for (int i = 0; i &lt; 8; i++) { /* ... */ }
    /// }
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CEUnrollAttribute : Attribute
    {
        /// <summary>
        /// Maximum number of iterations to unroll.
        /// </summary>
        public int MaxIterations { get; }

        public CEUnrollAttribute(int maxIterations = 8)
        {
            MaxIterations = maxIterations;
        }
    }

    /// <summary>
    /// Marks a field as a compile-time constant, forcing evaluation at compile time.
    /// Use for computed constants that the optimizer doesn't automatically detect.
    /// </summary>
    /// <example>
    /// [CEConst]
    /// private float TWO_PI = 2f * Mathf.PI;
    /// </example>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class CEConstAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a method as debug-only, removing it and all call sites from release builds.
    /// Use for debug visualization, logging, or validation code that should not run in production.
    /// </summary>
    /// <example>
    /// [CEDebugOnly]
    /// private void DrawDebugGizmos()
    /// {
    ///     // Complex visualization code
    ///     // Completely removed in release builds
    /// }
    /// 
    /// void Update()
    /// {
    ///     MovePlayer();
    ///     DrawDebugGizmos();  // Call removed in release
    /// }
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CEDebugOnlyAttribute : Attribute
    {
    }
}

