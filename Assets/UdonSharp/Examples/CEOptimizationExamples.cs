using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.Examples
{
#pragma warning disable CS0162, CS0219 // This sample intentionally demonstrates unreachable-code and constant-folding optimizations.
    /// <summary>
    /// The code shapes CE's syntactic optimizers were written for -- kept as a reference, not as a demo.
    /// 
    /// None of those optimizers runs in this release. All six (loop unrolling, tiny-method inlining,
    /// string interning, common subexpression elimination, loop-invariant code motion and extern call
    /// caching) are registered but disabled, because each was found able to change what a program does,
    /// and the assembly-level optimizer under Editor/Compiler/Optimization is not wired into the compiler.
    /// This behaviour therefore compiles to the same Udon stock UdonSharp produces for the same C#. The
    /// section comments below say, per technique, what actually happens today.
    /// 
    /// The optimization report (Udon CE/Optimization Report) lists each optimizer as disabled.
    /// </summary>
    // NoVariableSync, not None: this behaviour syncs no variables, but it does send network events
    // (see the string-interning examples below). BehaviourSyncMode.None disables SendCustomNetworkEvent
    // on the behaviour entirely, so those calls would silently never reach any client.
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class CEOptimizationExamples : UdonSharpBehaviour
    {
        // ════════════════════════════════════════════════════════════════════
        // OPTIMIZATION: CONSTANT FOLDING
        // ════════════════════════════════════════════════════════════════════
        // Evaluates constant expressions at compile time, eliminating runtime math.
        // 
        // Status: this one does happen, but it is UdonSharp's own binder that does it
        // (ConstantExpressionOptimizer), not a CE pass.
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
        // OPTIMIZATION: DEAD CODE ELIMINATION -- NOT PERFORMED
        // ════════════════════════════════════════════════════════════════════
        // What it was meant to do: remove code that can never execute, reducing program size.
        //
        // Status: no CE pass removes anything here. The PeepholeOptimizationPass and
        // DeadCodeEliminationPass this comment used to credit belong to the assembly-level
        // optimizer, which is not wired into the compiler. Code that must be absent from the
        // compiled program has to be excluded with #if, not with a const bool.
        // ════════════════════════════════════════════════════════════════════

        // Setting this to false stops the debug code running; it does not remove it from the program
        private const bool DEBUG_MODE = false;
        private const bool FEATURE_ENABLED = true;

        /// <summary>
        /// Code behind an always-false condition. Nothing in CE strips it; see the status note above.
        /// </summary>
        private void DeadCodeEliminationExamples()
        {
            // ─── Always-False Conditions ───
            // Never executes, but is not guaranteed to be absent from the compiled program
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
        // OPTIMIZATION 1: SMALL LOOP UNROLLING (CEOPT003) -- DISABLED
        // ════════════════════════════════════════════════════════════════════
        // Status: disabled in this release. It substituted the loop literal into member-access
        // names (producing 'v.0'), so every loop below compiles exactly as written.
        //
        // What it was meant to do: replace small fixed-iteration loops with straight-line code.
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
        /// Small fixed-count loops of the kind CEOPT003 targeted. They run as ordinary loops.
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
            // Note: Using flat array since UdonSharp doesn't support multidimensional arrays
            float[] matrix = new float[4]; // 2x2 = 4 elements
            for (int row = 0; row < 2; row++)
            {
                for (int col = 0; col < 2; col++)
                {
                    matrix[row * 2 + col] = row * 2 + col; // Flat index: row * width + col
                }
            }

            Debug.Log($"Processed {_corners.Length} corners, {channels.Length} channels");
        }

        private float CalculateCornerWeight(int corner) => 1.0f / (corner + 1);
        private float ProcessChannel(int channel) => channel * 0.5f;
        private float CalculateAxisValue(int axis) => axis * 10.0f;

        // ════════════════════════════════════════════════════════════════════
        // OPTIMIZATION 2: TINY METHOD INLINING (CEOPT004) -- DISABLED
        // ════════════════════════════════════════════════════════════════════
        // Status: disabled in this release. It dropped side effects and collapsed overloads by
        // name, so every call below is an ordinary method call.
        //
        // What it was meant to do: replace calls to very small methods with their body.
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
        /// Tiny helper calls of the kind CEOPT004 targeted. They are called, not inlined.
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
        // OPTIMIZATION 3: STRING INTERNING (CEOPT005) -- DISABLED
        // ════════════════════════════════════════════════════════════════════
        // Status: disabled in this release, so no duplicates are reported anywhere.
        //
        // What it was meant to do: identify duplicate string literals across your codebase.
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
        /// Duplicate literals of the kind CEOPT005 would have reported. Nothing reports them today.
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
        // COMBINED EXAMPLE: THE SHAPES ABOVE IN ONE UPDATE LOOP
        // ════════════════════════════════════════════════════════════════════

        private const bool ENABLE_PROFILING = false;
        private Vector3[] _quadPositions = new Vector3[4];

        /// <summary>
        /// The shapes above in one method. Only the constant folding below actually happens.
        /// </summary>
        private void Update()
        {
            // Constant folding: 1.0f / 60.0f → 0.0166667f
            float fixedDelta = 1.0f / 60.0f;

            // Never runs while ENABLE_PROFILING is false, but nothing removes it from the program
            if (ENABLE_PROFILING)
            {
                Debug.Log($"Frame time: {Time.deltaTime}");
                RecordFrameMetrics();
            }

            // Loop unrolling: 4 iterations become 4 statements
            // Note: Variables declared outside to avoid scope issues after unrolling
            float dist;
            float weight;
            for (int i = 0; i < 4; i++)
            {
                // Method inlining: Square() calls become (dist * dist)
                dist = GetDistanceToQuadCorner(i);
                weight = 1.0f / Square(dist + 0.01f);
                _quadPositions[i] = CalculateWeightedPosition(i, weight);
            }

            // All optimizations combine to produce minimal, efficient Udon code
        }

        private void RecordFrameMetrics() { }
        private float GetDistanceToQuadCorner(int corner) => corner + 1.0f;
        private Vector3 CalculateWeightedPosition(int corner, float weight) => Vector3.one * weight;

        // ════════════════════════════════════════════════════════════════════
        // CONTROL ATTRIBUTES: REMOVED
        // ════════════════════════════════════════════════════════════════════
        // 
        // [CENoOptimize], [CENoInline], [CEInline], [CENoUnroll], [CEUnroll], [CEConst] and
        // [CEDebugOnly] are [Obsolete(error: true)]: applying one is a compile error. The
        // optimizers they steered are disabled, and an attribute that silently does nothing is
        // worse than none -- [CEDebugOnly] in particular would leave "debug-only" code in a
        // release build. Use #if for code that must not ship. [CEPreserveAction] is unrelated
        // to these and still works.
        //
        // ════════════════════════════════════════════════════════════════════
    }
#pragma warning restore CS0162, CS0219
}
