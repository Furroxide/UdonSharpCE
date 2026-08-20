using System;

namespace UdonSharp.CE
{
    // ---------------------------------------------------------------------------------------------
    // Optimizer opt-outs -- all obsolete.
    //
    // These attributes exempted code from the syntactic optimizers (extern-call caching, common
    // subexpression elimination, loop-invariant code motion, small-loop unrolling, tiny-method
    // inlining). Every one of those passes is now IsEnabledByDefault => false, because each was found
    // to be able to change or delete program behaviour: they rewrote assignment targets so writes were
    // dropped, hoisted expressions past null guards, and substituted loop literals into member-access
    // names. There is therefore nothing left to opt out OF, and an attribute that silently does nothing
    // is exactly the class of failure this fork is removing.
    //
    // They error rather than warn so no one can believe an exemption is in force when none is. If the
    // optimizers are ever rewritten against the semantic model, these can be reinstated with real
    // readers behind them.
    // ---------------------------------------------------------------------------------------------

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
    [Obsolete("CENoOptimize does nothing. The optimizers it exempted from are all disabled by default because each could change program behaviour, so there is nothing to opt out of. Remove the attribute.", true)]
    public class CENoOptimizeAttribute : Attribute
    {
    }

    /// <summary>
    /// Prevents a method from being inlined by the tiny method inliner.
    /// Use when method call semantics are important (e.g., for debugging or profiling).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    [Obsolete("CENoInline does nothing. Tiny-method inlining is disabled by default (it dropped side effects and collapsed overloads by name), so no method is inlined. Remove the attribute.", true)]
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
    [Obsolete("CEInline does nothing. Nothing reads it, and the inliner it targeted is disabled by default because it was unsound. Remove the attribute.", true)]
    public class CEInlineAttribute : Attribute
    {
    }

    /// <summary>
    /// Prevents a loop from being unrolled by the small loop unrolling optimizer.
    /// Use when loop structure must be preserved.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    [Obsolete("CENoUnroll does nothing. Small-loop unrolling is disabled by default (it substituted the loop literal into member-access names, producing 'v.0'), so no loop is unrolled. Remove the attribute.", true)]
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
    ///     for (int i = 0; i < 8; i++) { /* ... */ }
    /// }
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    [Obsolete("CEUnroll does nothing. Nothing reads it, and the unroller it targeted is disabled by default because it was unsound. Remove the attribute.", true)]
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
    [Obsolete("CEConst does nothing. No constant-folding pass reads it. Use a C# 'const' field, which UdonSharp already folds.", true)]
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
    [Obsolete("CEDebugOnly does nothing. No pass strips the marked member, so code you believe is excluded from a release build still ships. Guard the call site with your own flag instead.", true)]
    public class CEDebugOnlyAttribute : Attribute
    {
    }

    /// <summary>
    /// Prevents the Action → CECallback transformation on the marked type or method.
    /// Use when implementing BCL interfaces that require Action parameters (e.g., INotifyCompletion).
    /// </summary>
    /// <remarks>
    /// UdonSharp's CE transformer normally converts all Action delegates to CECallback structs
    /// since Udon doesn't support delegates. However, some BCL interfaces require Action
    /// parameters in their signatures. This attribute prevents that transformation.
    /// 
    /// Note: Code marked with this attribute must not be executed at Udon runtime since
    /// Action delegates are not supported. It's intended for compile-time infrastructure
    /// like awaiters that are transformed away before runtime.
    /// </remarks>
    /// <example>
    /// [CEPreserveAction]
    /// public struct UdonTaskAwaiter : INotifyCompletion
    /// {
    ///     // OnCompleted(Action) signature is preserved for C# compiler compatibility
    ///     public void OnCompleted(Action continuation) { }
    /// }
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, AllowMultiple = false)]
    public class CEPreserveActionAttribute : Attribute
    {
    }
}

