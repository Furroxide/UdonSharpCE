using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.Examples
{
    /// <summary>
    /// This example script demonstrates all automatic CE compiler optimizations.
    /// 
    /// These optimizations happen automatically during compilation - no attributes
    /// or configuration needed. Users benefit simply by using UdonSharpCE.
    /// 
    /// View the optimization report: Tools > UdonSharpCE > Show Optimization Report
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CEOptimizationExamples : UdonSharpBehaviour
    {
        // ════════════════════════════════════════════════════════════════════
        // OPTIMIZATION: CONSTANT FOLDING
        // ════════════════════════════════════════════════════════════════════
        // Evaluates constant expressions at compile time, eliminating runtime math.
        // 
        // Note: Now handled by the Binder's ConstantExpressionOptimizer and
        // StrengthReductionPass at the binary level for optimal results.
        // 
        // Benefits:
        // - Fewer Udon instructions
        // - Zero runtime cost for constant math
        // - Works with arithmetic, bitwise, and comparison operators
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// These constants are computed at compile time, not runtime.
        /// </summary>
        private void ConstantFoldingExamples()
        {
            // ─── Arithmetic Operations ───
            // Before: Runtime multiplies 2.0f * 3.14159f every call
            // After:  Just loads the constant 6.28318f
            float twoPi = 2.0f * 3.14159f;

            // Before: Runtime divides 9.81f / 2.0f
            // After:  Just loads 4.905f
            float halfGravity = 9.81f / 2.0f;

            // Before: 100.0f * 0.01f computed at runtime
            // After:  Just 1.0f
            float percentage = 100.0f * 0.01f;

            // ─── Bitwise Operations ───
            // Before: Three OR operations at runtime
            // After:  Just the value 7
            int flags = 1 | 2 | 4;

            // Before: Left shift computed at runtime  
            // After:  Just 16
            int shifted = 1 << 4;

            // Before: Bitwise AND at runtime
            // After:  Just 0x00FF0000
            int masked = 0x00FFFFFF & 0x00FF0000;

            // ─── Compound Expressions ───
            // Before: Multiple operations at runtime
            // After:  Single constant 628.318f
            float circumference = 2.0f * 3.14159f * 100.0f;

            // Before: Complex expression evaluated at runtime
            // After:  Just 16
            int complexBitwise = (1 << 3) | (1 << 4);

            // Use the values to prevent "unused variable" warnings
            Debug.Log($"Constants: {twoPi}, {halfGravity}, {percentage}");
            Debug.Log($"Bitwise: {flags}, {shifted}, {masked}");
            Debug.Log($"Compound: {circumference}, {complexBitwise}");
        }

        // ════════════════════════════════════════════════════════════════════
        // OPTIMIZATION: DEAD CODE ELIMINATION
        // ════════════════════════════════════════════════════════════════════
        // Removes code that can never execute, reducing program size.
        //
        // Note: Now handled by binary-level optimization passes including
        // PeepholeOptimizationPass and DeadCodeEliminationPass for thorough
        // CFG-based analysis.
        //
        // Benefits:
        // - Smaller compiled programs
        // - Debug code automatically removed when condition is false
        // - Cleaner generated Udon assembly
        // ════════════════════════════════════════════════════════════════════

        // Set to false to completely remove debug code from builds
        private const bool DEBUG_MODE = false;
        private const bool FEATURE_ENABLED = true;

        /// <summary>
        /// Dead code is completely removed from the compiled output.
        /// </summary>
        private void DeadCodeEliminationExamples()
        {
            // ─── Always-False Conditions ───
            // This entire block is removed from the compiled program
            if (false)
            {
                Debug.Log("This code doesn't exist in the compiled output");
                ExpensiveDebugOperation();
            }

            // ─── Const-Based Feature Flags ───
            // When DEBUG_MODE is false, this entire block is removed
            if (DEBUG_MODE)
            {
                Debug.Log("[DEBUG] Starting update cycle");
                DrawDebugVisualization();
                ValidateInternalState();
            }

            // ─── Always-True Simplification ───
            // The else branch is removed, and the if is simplified to just the body
            if (true)
            {
                DoImportantWork();
            }
            else
            {
                DoAlternativeWork(); // This is removed
            }

            // ─── Const Feature Toggle ───
            // When FEATURE_ENABLED is true, the else is removed
            if (FEATURE_ENABLED)
            {
                ProcessWithNewFeature();
            }
            else
            {
                ProcessWithLegacyMethod(); // Removed
            }

            // ─── Unreachable Code After Return ───
            if (ShouldEarlyExit())
            {
                DoCleanup();
                return;

                // Everything below this return is removed
                Debug.Log("This never runs");
                DoMoreWork();
            }

            // ─── Ternary Simplification ───
            // false ? "A" : "B" becomes just "B"
            string result = false ? "Never selected" : "Always selected";
            Debug.Log(result);
        }

        // These methods exist to make the examples compile
        private void ExpensiveDebugOperation() { }
        private void DrawDebugVisualization() { }
        private void ValidateInternalState() { }
        private void DoImportantWork() { }
        private void DoAlternativeWork() { }
        private void ProcessWithNewFeature() { }
        private void ProcessWithLegacyMethod() { }
        private bool ShouldEarlyExit() => false;
        private void DoCleanup() { }
        private void DoMoreWork() { }

        // ════════════════════════════════════════════════════════════════════
        // OPTIMIZATION 1: SMALL LOOP UNROLLING (CEOPT003)
        // ════════════════════════════════════════════════════════════════════
        // Replaces small fixed-iteration loops with straight-line code.
        //
        // Criteria for automatic unrolling:
        // - Iteration count is constant and known at compile time
        // - Iteration count ≤ 4
        // - Loop body is simple (≤ 5 statements)
        // - No break/continue/return inside the loop
        //
        // Benefits:
        // - Eliminates loop overhead (increment, compare, jump)
        // - Better instruction cache utilization
        // - Enables further optimizations on the unrolled code
        // ════════════════════════════════════════════════════════════════════

        private Vector3[] _corners = new Vector3[4];
        private Vector3[] _localCorners = new Vector3[4];
        private float[] _weights = new float[4];
        private Transform[] _waypoints = new Transform[4];

        /// <summary>
        /// Small loops are automatically unrolled into straight-line code.
        /// </summary>
        private void SmallLoopUnrollingExamples()
        {
            // ─── Simple Array Processing ───
            // Before: Loop with increment, compare, and jump each iteration
            // After:  Four direct assignments with no loop overhead
            for (int i = 0; i < 4; i++)
            {
                _corners[i] = transform.TransformPoint(_localCorners[i]);
            }
            // Becomes:
            // _corners[0] = transform.TransformPoint(_localCorners[0]);
            // _corners[1] = transform.TransformPoint(_localCorners[1]);
            // _corners[2] = transform.TransformPoint(_localCorners[2]);
            // _corners[3] = transform.TransformPoint(_localCorners[3]);

            // ─── Quad Corners (Common Pattern) ───
            // UI and mesh operations often work with 4 corners
            for (int i = 0; i < 4; i++)
            {
                _weights[i] = CalculateCornerWeight(i);
            }

            // ─── RGB Channel Processing ───
            // Processing 3 color channels
            float[] channels = new float[3];
            for (int c = 0; c < 3; c++)
            {
                channels[c] = ProcessChannel(c);
            }

            // ─── XYZ Axis Processing ───
            // Common pattern for 3D math
            Vector3 result = Vector3.zero;
            for (int axis = 0; axis < 3; axis++)
            {
                result[axis] = CalculateAxisValue(axis);
            }

            // ─── 2x2 Matrix Operations ───
            // Small nested loops are also unrolled
            float[,] matrix = new float[2, 2];
            for (int row = 0; row < 2; row++)
            {
                for (int col = 0; col < 2; col++)
                {
                    matrix[row, col] = row * 2 + col;
                }
            }

            Debug.Log($"Processed {_corners.Length} corners, {channels.Length} channels");
        }

        private float CalculateCornerWeight(int corner) => 1.0f / (corner + 1);
        private float ProcessChannel(int channel) => channel * 0.5f;
        private float CalculateAxisValue(int axis) => axis * 10.0f;

        // ════════════════════════════════════════════════════════════════════
        // OPTIMIZATION 2: TINY METHOD INLINING (CEOPT004)
        // ════════════════════════════════════════════════════════════════════
        // Replaces calls to very small methods with their body.
        //
        // Criteria for automatic inlining:
        // - Method is expression-bodied or has single return statement
        // - Method is private and non-virtual
        // - Method is called 2+ times
        // - No ref/out parameters
        //
        // Benefits:
        // - Eliminates method call overhead (push args, call, return)
        // - Reduces stack manipulation in Udon
        // - Enables constant folding on the inlined expression
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tiny helper methods are inlined at their call sites.
        /// </summary>
        private void TinyMethodInliningExamples()
        {
            float distance = 5.0f;
            float x = 3.0f;
            float y = 4.0f;
            float value = 1.5f;

            // ─── Expression-Bodied Methods ───
            // Before: Method call with push/call/return overhead
            // After:  Inline expression (distance * distance)
            float distSquared = Square(distance);

            // Multiple calls benefit even more
            float sumOfSquares = Square(x) + Square(y);

            // ─── Math Helper Functions ───
            // These common patterns inline nicely
            float clamped = Clamp01(value);
            float smoothed = SmoothStep01(value);

            // ─── Property-Like Accessors ───
            float half = HalfOf(distance);
            float doubled = DoubleOf(distance);

            // ─── Chained Inlining ───
            // Length calls Square, both get inlined
            float length = Length(x, y);
            // Becomes: Mathf.Sqrt((x * x) + (y * y))

            Debug.Log($"Results: {distSquared}, {sumOfSquares}, {clamped}, {smoothed}");
            Debug.Log($"More: {half}, {doubled}, {length}");
        }

        // These methods will be inlined at call sites:

        private float Square(float x) => x * x;

        private float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

        private float SmoothStep01(float x) => x * x * (3f - 2f * x);

        private float HalfOf(float x) => x * 0.5f;

        private float DoubleOf(float x) => x * 2f;

        private float Length(float x, float y) => Mathf.Sqrt(Square(x) + Square(y));

        // ════════════════════════════════════════════════════════════════════
        // OPTIMIZATION 3: STRING INTERNING (CEOPT005)
        // ════════════════════════════════════════════════════════════════════
        // Identifies duplicate string literals across your codebase.
        //
        // The optimizer tracks string usage and reports duplicates in the
        // Optimization Report window. Strings appearing 2+ times could be
        // declared as const fields to reduce memory usage.
        //
        // Benefits:
        // - Reduced memory allocation for duplicate strings
        // - Awareness of string usage patterns
        // - Guidance for manual optimization
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Duplicate strings are detected for potential interning.
        /// Check Tools > UdonSharpCE > Show Optimization Report for details.
        /// </summary>
        private void StringInterningExamples()
        {
            // ─── Event Names (Common Duplicates) ───
            // These strings appear multiple times across methods
            SendCustomEvent("OnGameStarted");
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "OnGameStarted");

            SendCustomEvent("OnPlayerJoined");
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, "OnPlayerJoined");

            // ─── Log Messages ───
            // Duplicate log strings are common
            Debug.Log("[MyWorld] Player joined");
            Debug.Log("[MyWorld] Player joined"); // Duplicate!
            Debug.Log("[MyWorld] Game started");
            Debug.Log("[MyWorld] Game started"); // Duplicate!

            // ─── Better Pattern: Use Constants ───
            // Declare frequently-used strings as const fields
            Debug.Log(LOG_PREFIX + "Initialized");
            Debug.Log(LOG_PREFIX + "Ready");
        }

        // Recommended: Declare repeated strings as constants
        private const string LOG_PREFIX = "[MyWorld] ";
        private const string EVENT_GAME_STARTED = "OnGameStarted";
        private const string EVENT_PLAYER_JOINED = "OnPlayerJoined";

        /// <summary>
        /// Example using the const pattern for strings.
        /// </summary>
        private void StringConstantsExample()
        {
            // Using constants ensures only one string instance
            SendCustomEvent(EVENT_GAME_STARTED);
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, EVENT_GAME_STARTED);

            SendCustomEvent(EVENT_PLAYER_JOINED);
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, EVENT_PLAYER_JOINED);
        }

        // ════════════════════════════════════════════════════════════════════
        // COMBINED EXAMPLE: ALL OPTIMIZATIONS WORKING TOGETHER
        // ════════════════════════════════════════════════════════════════════

        private const bool ENABLE_PROFILING = false;
        private Vector3[] _quadPositions = new Vector3[4];

        /// <summary>
        /// Real-world example showing multiple optimizations in action.
        /// </summary>
        private void Update()
        {
            // Constant folding: 1.0f / 60.0f → 0.0166667f
            float fixedDelta = 1.0f / 60.0f;

            // Dead code elimination: Entire profiling block removed
            if (ENABLE_PROFILING)
            {
                Debug.Log($"Frame time: {Time.deltaTime}");
                RecordFrameMetrics();
            }

            // Loop unrolling: 4 iterations become 4 statements
            for (int i = 0; i < 4; i++)
            {
                // Method inlining: Square() calls become (dist * dist)
                float dist = GetDistanceToQuadCorner(i);
                float weight = 1.0f / Square(dist + 0.01f);
                _quadPositions[i] = CalculateWeightedPosition(i, weight);
            }

            // All optimizations combine to produce minimal, efficient Udon code
        }

        private void RecordFrameMetrics() { }
        private float GetDistanceToQuadCorner(int corner) => corner + 1.0f;
        private Vector3 CalculateWeightedPosition(int corner, float weight) => Vector3.one * weight;

        // ════════════════════════════════════════════════════════════════════
        // CONTROL ATTRIBUTES: WHEN YOU NEED TO DISABLE OPTIMIZATIONS
        // ════════════════════════════════════════════════════════════════════
        // 
        // In rare cases, you may need to prevent specific optimizations.
        // Use these attributes from UdonSharp.CE namespace:
        //
        // [CENoOptimize]     - Disable ALL optimizations on class/method/field
        // [CENoInline]       - Prevent method from being inlined
        // [CEInline]         - Force method to be inlined
        // [CENoUnroll]       - Prevent loop unrolling in method
        // [CEUnroll(n)]      - Force unrolling up to n iterations
        // [CEConst]          - Mark field as compile-time constant
        // [CEDebugOnly]      - Remove method entirely in release builds
        //
        // Example:
        // [CENoInline]
        // private float CriticalMethod(float x) => x * x;
        //
        // ════════════════════════════════════════════════════════════════════
    }
}

