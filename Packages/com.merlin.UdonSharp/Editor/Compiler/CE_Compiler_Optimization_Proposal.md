# UdonSharp CE Compiler Optimization Proposal

## Assembly-Level Optimization Pass Implementation

**Version:** 1.0  
**Date:** December 2025  
**Status:** Proposal  
**Author:** CE Development Team

---

## Executive Summary

This document proposes the implementation of an **Assembly-Level Optimization Pass** for the UdonSharp CE compiler. The current compiler pipeline generates Udon assembly without any post-emit optimization, leaving significant performance gains unrealized. By introducing a lightweight, pass-based optimizer operating on the instruction stream before UASM string generation, we can achieve measurable runtime performance improvements that benefit **all U# users automatically** with zero developer effort required.

### Key Benefits

| Metric | Expected Improvement |
|--------|---------------------|
| Instruction count reduction | 10-25% |
| Heap variable reduction | 5-15% |
| Network bandwidth (indirect) | Smaller programs = faster sync |
| Developer experience | Zero effort, automatic gains |

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Current Architecture Analysis](#2-current-architecture-analysis)
3. [Proposed Solution](#3-proposed-solution)
4. [Detailed Implementation Plan](#4-detailed-implementation-plan)
5. [Optimization Passes Specification](#5-optimization-passes-specification)
6. [Testing Strategy](#6-testing-strategy)
7. [Risk Assessment](#7-risk-assessment)
8. [Implementation Timeline](#8-implementation-timeline)
9. [Future Considerations](#9-future-considerations)
10. [Appendices](#10-appendices)

---

## 1. Problem Statement

### 1.1 The Gap in the Current Pipeline

The UdonSharp compiler translates C# source code to Udon assembly through several phases. However, **no optimization occurs at the instruction level**. The emit phase generates instructions that are immediately serialized to UASM strings without any analysis or transformation.

```
Current Flow:
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Roslyn    │ -> │    Bind     │ -> │    Emit     │ -> │  UASM Str   │
│   Parse     │    │   Phase     │    │   Phase     │    │  (direct)   │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
                         │
                         v
                   Constant Folding
                   (only optimization)
```

### 1.2 Observable Inefficiencies

Analysis of generated UASM reveals several patterns of inefficiency:

**Redundant Copy Operations:**
```uasm
# Generated for: x = y; z = x;
PUSH, __0_y
PUSH, __1_x
COPY
PUSH, __1_x        # x was just written, now immediately read
PUSH, __2_z
COPY
```

**Unnecessary Push-Pop Sequences:**
```uasm
# Generated for discarded expression results
PUSH, __temp_0
POP                 # Pushed then immediately popped
```

**Unoptimized Jump Chains:**
```uasm
JUMP, 0x00000100
# ... at 0x00000100:
JUMP, 0x00000200    # Could jump directly to 0x200
```

### 1.3 Why This Matters

1. **Instruction Count:** Every unnecessary instruction costs CPU cycles in the Udon VM
2. **Heap Pressure:** Redundant temporaries consume heap slots (limited resource in Udon)
3. **Program Size:** Larger programs take longer to load and sync across the network
4. **User Experience:** Accumulated inefficiencies impact frame times in complex worlds

---

## 2. Current Architecture Analysis

### 2.1 Compiler Pipeline Overview

The UdonSharp compiler (`UdonSharpCompilerV1.cs`) operates in distinct phases:

```csharp
// Phase 1: Setup - Load syntax trees, run CE optimizers
compilationContext.CurrentPhase = CompilationContext.CompilePhase.Setup;
ModuleBinding[] syntaxTrees = compilationContext.LoadSyntaxTreesAndCreateModules();
RunCompileTimeOptimizers(syntaxTrees);  // CE pre-compilation optimizers

// Phase 2: Roslyn Compile - Semantic analysis
compilationContext.CurrentPhase = CompilationContext.CompilePhase.RoslynCompile;
CSharpCompilation compilation = CSharpCompilation.Create(...);

// Phase 3: Bind - Symbol resolution, constant folding
compilationContext.CurrentPhase = CompilationContext.CompilePhase.Bind;
BindAllPrograms(rootTypes, compilationContext);

// Phase 4: Emit - Generate instructions (NO OPTIMIZATION)
compilationContext.CurrentPhase = CompilationContext.CompilePhase.Emit;
EmitAllPrograms(rootTypes, compilationContext, assembly);
```

### 2.2 Instruction Generation (`AssemblyModule.cs`)

The `AssemblyModule` class maintains an instruction list and generates UASM:

```csharp
internal class AssemblyModule
{
    private List<AssemblyInstruction> _instructions = new List<AssemblyInstruction>();
    
    // Instructions are added sequentially
    public void AddCopy(Value sourceValue, Value targetValue)
    {
        AddInstruction(new CopyInstruction(sourceValue, targetValue));
    }
    
    // Direct serialization - NO optimization
    public string BuildUasmStr()
    {
        StringBuilder uasmBuilder = new StringBuilder();
        BuildDataBlock(uasmBuilder);
        BuildInstructionUasm(uasmBuilder);
        return uasmBuilder.ToString();
    }
}
```

### 2.3 Instruction Types

The compiler generates these instruction types (from `AssemblyInstruction.cs`):

| Instruction | Size (bytes) | Description |
|-------------|--------------|-------------|
| `NOP` | 4 | No operation |
| `PUSH` | 8 | Push value to stack |
| `POP` | 4 | Pop value from stack |
| `COPY` | 20 | Copy value (PUSH src, PUSH dst, COPY) |
| `JUMP` | 8 | Unconditional jump |
| `JUMP_IF_FALSE` | 16 | Conditional jump |
| `JUMP_INDIRECT` | 8 | Indirect jump (for returns) |
| `EXTERN` | 8 | External method call |
| `RET` | 20 | Return from method |

### 2.4 Existing Optimization Infrastructure

The compiler already has optimization registries that CE uses:

```csharp
// CompileTimeOptimizerRegistry.cs - Syntax tree transforms
public static class CompileTimeOptimizerRegistry
{
    internal delegate (SyntaxTree tree, string filePath)[] OptimizerCallback(
        (SyntaxTree tree, string filePath)[] syntaxTrees);
    
    internal static (SyntaxTree, string)[] RunOptimizers((SyntaxTree, string)[] syntaxTrees);
}

// CompileTimeAnalyzerRegistry.cs - Post-bind analysis
public static class CompileTimeAnalyzerRegistry
{
    internal delegate void AnalyzerCallback(TypeSymbol type, BindContext context, CompilationContext ctx);
    
    internal static void RunAnalyzers(TypeSymbol type, BindContext context, CompilationContext ctx);
}
```

**Gap Identified:** No registry exists for post-emit instruction optimization.

### 2.5 Value System Analysis

The `Value` and `ValueTable` system manages heap allocations:

```csharp
internal class Value
{
    public string UniqueID { get; }      // Heap variable name
    public TypeSymbol UserType { get; }   // C# type
    public TypeSymbol UdonType { get; }   // Udon type
    public bool IsConstant { get; }
    public bool IsLocal { get; set; }     // Scope-limited
    public object DefaultValue { get; }
}
```

Values are created liberally during emit, with no reuse of dead values. This presents an optimization opportunity.

---

## 3. Proposed Solution

### 3.1 Solution Overview

Introduce an **Assembly Optimization Pass** that operates on the `_instructions` list in `AssemblyModule` before UASM string generation.

```
Proposed Flow:
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Roslyn    │ -> │    Bind     │ -> │    Emit     │ -> │  OPTIMIZE   │ -> │  UASM Str   │
│   Parse     │    │   Phase     │    │   Phase     │    │   PASS      │    │             │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
                                                                │
                                                                v
                                                    ┌───────────────────────┐
                                                    │ • Peephole Opts       │
                                                    │ • Copy Propagation    │
                                                    │ • Jump Threading      │
                                                    │ • Dead Code Elim      │
                                                    │ • Value Coalescence   │
                                                    └───────────────────────┘
```

### 3.2 Design Principles

1. **Non-Breaking:** All optimizations must preserve program semantics exactly
2. **Opt-Out Available:** Provide compiler flag to disable optimization for debugging
3. **Pass-Based:** Modular passes that can be individually enabled/disabled
4. **Conservative:** When in doubt, don't optimize
5. **Measurable:** Include metrics collection for optimization effectiveness

### 3.3 Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    AssemblyOptimizer                             │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                 IOptimizationPass                        │    │
│  │  + Name: string                                          │    │
│  │  + Priority: int                                         │    │
│  │  + CanRun(context): bool                                 │    │
│  │  + Run(context): bool                                    │    │
│  └─────────────────────────────────────────────────────────┘    │
│                              │                                   │
│         ┌────────────────────┼────────────────────┐             │
│         │                    │                    │             │
│         v                    v                    v             │
│  ┌─────────────┐     ┌─────────────┐     ┌─────────────┐       │
│  │  Peephole   │     │    Copy     │     │    Jump     │       │
│  │    Pass     │     │ Propagation │     │  Threading  │       │
│  └─────────────┘     └─────────────┘     └─────────────┘       │
│                                                                  │
├─────────────────────────────────────────────────────────────────┤
│                    OptimizationContext                           │
│  + Instructions: List<AssemblyInstruction>                       │
│  + Values: ValueTable                                            │
│  + CFG: ControlFlowGraph (lazy)                                  │
│  + Metrics: OptimizationMetrics                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 3.4 Integration Point

Modify `AssemblyModule.BuildUasmStr()`:

```csharp
public string BuildUasmStr()
{
    // NEW: Run optimization passes
    if (CompileContext.Options.EnableAssemblyOptimization)
    {
        var optimizer = new AssemblyOptimizer(this);
        optimizer.Optimize();
    }
    
    StringBuilder uasmBuilder = new StringBuilder();
    BuildDataBlock(uasmBuilder);
    BuildInstructionUasm(uasmBuilder);
    return uasmBuilder.ToString();
}
```

---

## 4. Detailed Implementation Plan

### 4.1 New Files to Create

```
Packages/com.merlin.UdonSharp/Editor/Compiler/
├── Optimization/
│   ├── AssemblyOptimizer.cs           # Main optimizer orchestrator
│   ├── OptimizationContext.cs         # Shared state for passes
│   ├── OptimizationMetrics.cs         # Performance tracking
│   ├── IOptimizationPass.cs           # Pass interface
│   ├── ControlFlowGraph.cs            # CFG for advanced opts
│   └── Passes/
│       ├── PeepholeOptimizationPass.cs
│       ├── RedundantCopyEliminationPass.cs
│       ├── PushPopEliminationPass.cs
│       ├── JumpThreadingPass.cs
│       ├── DeadCodeEliminationPass.cs
│       └── ValueCoalescencePass.cs
```

### 4.2 Core Classes

#### 4.2.1 IOptimizationPass Interface

```csharp
namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Interface for assembly optimization passes.
    /// Passes are executed in priority order (lower = earlier).
    /// </summary>
    public interface IOptimizationPass
    {
        /// <summary>
        /// Human-readable name for logging and debugging.
        /// </summary>
        string Name { get; }
        
        /// <summary>
        /// Execution priority. Lower values run first.
        /// Recommended ranges:
        ///   0-99: Analysis passes (CFG building, etc.)
        ///   100-199: Local optimizations (peephole)
        ///   200-299: Propagation passes (copy prop, etc.)
        ///   300-399: Elimination passes (dead code, etc.)
        ///   400-499: Cleanup passes (address recalculation)
        /// </summary>
        int Priority { get; }
        
        /// <summary>
        /// Whether this pass can run given current context.
        /// Use for dependency checks or feature flags.
        /// </summary>
        bool CanRun(OptimizationContext context);
        
        /// <summary>
        /// Execute the optimization pass.
        /// </summary>
        /// <param name="context">Shared optimization context</param>
        /// <returns>True if any changes were made</returns>
        bool Run(OptimizationContext context);
    }
}
```

#### 4.2.2 OptimizationContext

```csharp
namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Shared context for optimization passes.
    /// Provides access to instructions, values, and analysis results.
    /// </summary>
    public class OptimizationContext
    {
        public AssemblyModule Module { get; }
        public List<AssemblyInstruction> Instructions { get; }
        public ValueTable RootValueTable { get; }
        public CompilationContext CompileContext { get; }
        public OptimizationMetrics Metrics { get; }
        
        // Lazy-initialized CFG for passes that need it
        private ControlFlowGraph _cfg;
        public ControlFlowGraph CFG => _cfg ??= BuildCFG();
        
        // Analysis caches
        private Dictionary<Value, HashSet<int>> _valueUses;
        private Dictionary<Value, HashSet<int>> _valueDefs;
        
        public OptimizationContext(AssemblyModule module)
        {
            Module = module;
            Instructions = module.GetInstructionList(); // New internal method
            RootValueTable = module.RootTable;
            CompileContext = module.CompileContext;
            Metrics = new OptimizationMetrics();
        }
        
        /// <summary>
        /// Get all instruction indices where a value is used (read).
        /// </summary>
        public HashSet<int> GetValueUses(Value value)
        {
            EnsureUseDefAnalysis();
            return _valueUses.TryGetValue(value, out var uses) 
                ? uses 
                : new HashSet<int>();
        }
        
        /// <summary>
        /// Get all instruction indices where a value is defined (written).
        /// </summary>
        public HashSet<int> GetValueDefs(Value value)
        {
            EnsureUseDefAnalysis();
            return _valueDefs.TryGetValue(value, out var defs) 
                ? defs 
                : new HashSet<int>();
        }
        
        /// <summary>
        /// Mark a value's use/def analysis as dirty (after modifications).
        /// </summary>
        public void InvalidateAnalysis()
        {
            _valueUses = null;
            _valueDefs = null;
            _cfg = null;
        }
        
        /// <summary>
        /// Remove an instruction and mark for address recalculation.
        /// </summary>
        public void RemoveInstruction(int index)
        {
            Metrics.InstructionsRemoved++;
            Instructions.RemoveAt(index);
            InvalidateAnalysis();
        }
        
        /// <summary>
        /// Replace an instruction at the given index.
        /// </summary>
        public void ReplaceInstruction(int index, AssemblyInstruction newInstruction)
        {
            Metrics.InstructionsReplaced++;
            Instructions[index] = newInstruction;
            InvalidateAnalysis();
        }
        
        private void EnsureUseDefAnalysis()
        {
            if (_valueUses != null) return;
            
            _valueUses = new Dictionary<Value, HashSet<int>>();
            _valueDefs = new Dictionary<Value, HashSet<int>>();
            
            for (int i = 0; i < Instructions.Count; i++)
            {
                var instruction = Instructions[i];
                
                foreach (var usedValue in GetUsedValues(instruction))
                {
                    if (!_valueUses.ContainsKey(usedValue))
                        _valueUses[usedValue] = new HashSet<int>();
                    _valueUses[usedValue].Add(i);
                }
                
                foreach (var defValue in GetDefinedValues(instruction))
                {
                    if (!_valueDefs.ContainsKey(defValue))
                        _valueDefs[defValue] = new HashSet<int>();
                    _valueDefs[defValue].Add(i);
                }
            }
        }
        
        private IEnumerable<Value> GetUsedValues(AssemblyInstruction instruction)
        {
            switch (instruction)
            {
                case PushInstruction push:
                    yield return push.PushValue;
                    break;
                case CopyInstruction copy:
                    yield return copy.SourceValue;
                    break;
                case JumpIfFalseInstruction jumpIf:
                    yield return jumpIf.ConditionValue;
                    break;
                case JumpIndirectInstruction jumpInd:
                    yield return jumpInd.JumpTargetValue;
                    break;
                case RetInstruction ret:
                    yield return ret.RetValRef;
                    break;
            }
        }
        
        private IEnumerable<Value> GetDefinedValues(AssemblyInstruction instruction)
        {
            switch (instruction)
            {
                case CopyInstruction copy:
                    yield return copy.TargetValue;
                    break;
            }
        }
        
        private ControlFlowGraph BuildCFG()
        {
            return new ControlFlowGraph(Instructions);
        }
    }
}
```

#### 4.2.3 AssemblyOptimizer

```csharp
namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Main optimizer that orchestrates optimization passes.
    /// </summary>
    public class AssemblyOptimizer
    {
        private readonly AssemblyModule _module;
        private readonly List<IOptimizationPass> _passes;
        private const int MaxIterations = 10;
        
        public AssemblyOptimizer(AssemblyModule module)
        {
            _module = module;
            _passes = CreateDefaultPasses();
        }
        
        private List<IOptimizationPass> CreateDefaultPasses()
        {
            return new List<IOptimizationPass>
            {
                // Priority 100-199: Local optimizations
                new PushPopEliminationPass(),           // Priority 100
                new RedundantCopyEliminationPass(),     // Priority 110
                new PeepholeOptimizationPass(),         // Priority 120
                
                // Priority 200-299: Propagation
                new CopyPropagationPass(),              // Priority 200
                
                // Priority 300-399: Control flow
                new JumpThreadingPass(),                // Priority 300
                new DeadCodeEliminationPass(),          // Priority 310
                
                // Priority 400-499: Cleanup
                new ValueCoalescencePass(),             // Priority 400
            }.OrderBy(p => p.Priority).ToList();
        }
        
        public void Optimize()
        {
            var context = new OptimizationContext(_module);
            
            int iteration = 0;
            bool changed;
            
            do
            {
                changed = false;
                
                foreach (var pass in _passes)
                {
                    if (!pass.CanRun(context))
                        continue;
                    
                    try
                    {
                        bool passChanged = pass.Run(context);
                        changed |= passChanged;
                        
                        if (passChanged)
                        {
                            context.Metrics.PassesRun++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[UdonSharp] Optimization pass '{pass.Name}' failed: {ex.Message}");
                        // Continue with other passes
                    }
                }
                
                iteration++;
            } while (changed && iteration < MaxIterations);
            
            // Recalculate instruction addresses after all optimizations
            RecalculateAddresses(context);
            
            // Log metrics in debug builds
            #if UDONSHARP_DEBUG
            LogMetrics(context.Metrics);
            #endif
        }
        
        private void RecalculateAddresses(OptimizationContext context)
        {
            uint currentAddress = 0;
            
            foreach (var instruction in context.Instructions)
            {
                instruction.InstructionAddress = currentAddress;
                currentAddress += instruction.Size;
            }
            
            // Update jump labels
            // (Labels reference instructions, addresses are recalculated)
        }
        
        private void LogMetrics(OptimizationMetrics metrics)
        {
            Debug.Log($"[UdonSharp] Optimization: {metrics.InstructionsRemoved} instructions removed, " +
                      $"{metrics.InstructionsReplaced} replaced, {metrics.PassesRun} pass iterations");
        }
    }
}
```

#### 4.2.4 OptimizationMetrics

```csharp
namespace UdonSharp.Compiler.Optimization
{
    /// <summary>
    /// Tracks optimization statistics for analysis and debugging.
    /// </summary>
    public class OptimizationMetrics
    {
        public int InstructionsRemoved { get; set; }
        public int InstructionsReplaced { get; set; }
        public int PassesRun { get; set; }
        public int ValuesCoalesced { get; set; }
        public int JumpsThreaded { get; set; }
        public int DeadBlocksRemoved { get; set; }
        
        public Dictionary<string, int> PassSpecificMetrics { get; } = new Dictionary<string, int>();
        
        public void RecordPassMetric(string passName, string metric, int count)
        {
            var key = $"{passName}.{metric}";
            if (!PassSpecificMetrics.ContainsKey(key))
                PassSpecificMetrics[key] = 0;
            PassSpecificMetrics[key] += count;
        }
    }
}
```

---

## 5. Optimization Passes Specification

### 5.1 Push-Pop Elimination Pass

**Priority:** 100  
**Complexity:** Low  
**Risk:** Low  
**Expected Impact:** 2-5% instruction reduction

**Pattern:**
```uasm
PUSH, X
POP
→ (remove both)
```

**Implementation:**

```csharp
public class PushPopEliminationPass : IOptimizationPass
{
    public string Name => "Push-Pop Elimination";
    public int Priority => 100;
    
    public bool CanRun(OptimizationContext context) => true;
    
    public bool Run(OptimizationContext context)
    {
        bool changed = false;
        var instructions = context.Instructions;
        
        for (int i = instructions.Count - 2; i >= 0; i--)
        {
            if (instructions[i] is PushInstruction &&
                instructions[i + 1] is PopInstruction)
            {
                context.RemoveInstruction(i + 1);
                context.RemoveInstruction(i);
                changed = true;
                context.Metrics.RecordPassMetric(Name, "PairsRemoved", 1);
            }
        }
        
        return changed;
    }
}
```

### 5.2 Redundant Copy Elimination Pass

**Priority:** 110  
**Complexity:** Low  
**Risk:** Low  
**Expected Impact:** 3-8% instruction reduction

**Patterns:**

```uasm
# Pattern 1: Self-copy
COPY A → A
→ (remove)

# Pattern 2: Dead copy (target never used)
COPY A → B
# ... B never read before next write ...
COPY C → B
→ (remove first copy)

# Pattern 3: Copy chain with single use
COPY A → B
COPY B → C  (B has no other uses)
→ COPY A → C
```

**Implementation:**

```csharp
public class RedundantCopyEliminationPass : IOptimizationPass
{
    public string Name => "Redundant Copy Elimination";
    public int Priority => 110;
    
    public bool CanRun(OptimizationContext context) => true;
    
    public bool Run(OptimizationContext context)
    {
        bool changed = false;
        var instructions = context.Instructions;
        
        // Pattern 1: Self-copy elimination
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i] is CopyInstruction copy &&
                copy.SourceValue == copy.TargetValue)
            {
                context.RemoveInstruction(i);
                changed = true;
                context.Metrics.RecordPassMetric(Name, "SelfCopiesRemoved", 1);
            }
        }
        
        // Pattern 2: Dead copy elimination
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is CopyInstruction copy)
            {
                var targetUses = context.GetValueUses(copy.TargetValue);
                var targetDefs = context.GetValueDefs(copy.TargetValue);
                
                // Find next use and next def after this instruction
                int nextUse = targetUses.Where(u => u > i).DefaultIfEmpty(int.MaxValue).Min();
                int nextDef = targetDefs.Where(d => d > i).DefaultIfEmpty(int.MaxValue).Min();
                
                // If next def comes before next use, this copy is dead
                if (nextDef < nextUse && !IsValueExported(copy.TargetValue))
                {
                    context.RemoveInstruction(i);
                    changed = true;
                    i--; // Re-check this index
                    context.Metrics.RecordPassMetric(Name, "DeadCopiesRemoved", 1);
                }
            }
        }
        
        // Pattern 3: Copy chain optimization
        changed |= OptimizeCopyChains(context);
        
        return changed;
    }
    
    private bool OptimizeCopyChains(OptimizationContext context)
    {
        bool changed = false;
        var instructions = context.Instructions;
        
        for (int i = 0; i < instructions.Count - 1; i++)
        {
            if (instructions[i] is CopyInstruction copy1 &&
                instructions[i + 1] is CopyInstruction copy2 &&
                copy1.TargetValue == copy2.SourceValue)
            {
                // Check if intermediate value has only these two uses
                var uses = context.GetValueUses(copy1.TargetValue);
                if (uses.Count == 1 && uses.Contains(i + 1))
                {
                    // Replace with direct copy
                    var newCopy = new CopyInstruction(copy1.SourceValue, copy2.TargetValue);
                    context.ReplaceInstruction(i, newCopy);
                    context.RemoveInstruction(i + 1);
                    changed = true;
                    context.Metrics.RecordPassMetric(Name, "ChainsOptimized", 1);
                }
            }
        }
        
        return changed;
    }
    
    private bool IsValueExported(Value value)
    {
        // Check if value is associated with a serialized field or parameter
        return value.AssociatedSymbol is FieldSymbol field && 
               (field.IsSerialized || field.IsSynced);
    }
}
```

### 5.3 Peephole Optimization Pass

**Priority:** 120  
**Complexity:** Medium  
**Risk:** Low  
**Expected Impact:** 5-10% instruction reduction

**Patterns:**

```csharp
public class PeepholeOptimizationPass : IOptimizationPass
{
    public string Name => "Peephole Optimization";
    public int Priority => 120;
    
    private readonly List<PeepholePattern> _patterns = new List<PeepholePattern>
    {
        // Boolean constant conditions
        new BooleanConstantJumpPattern(),
        
        // Arithmetic identity operations (if exposed by Udon)
        // x + 0 → x, x * 1 → x, etc.
        
        // Consecutive identical pushes
        new ConsecutivePushPattern(),
        
        // NOP removal
        new NopRemovalPattern(),
    };
    
    public bool CanRun(OptimizationContext context) => true;
    
    public bool Run(OptimizationContext context)
    {
        bool changed = false;
        
        foreach (var pattern in _patterns)
        {
            changed |= pattern.Apply(context);
        }
        
        return changed;
    }
}

public abstract class PeepholePattern
{
    public abstract bool Apply(OptimizationContext context);
}

public class BooleanConstantJumpPattern : PeepholePattern
{
    public override bool Apply(OptimizationContext context)
    {
        bool changed = false;
        var instructions = context.Instructions;
        
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i] is JumpIfFalseInstruction jumpIf &&
                jumpIf.ConditionValue.IsConstant)
            {
                bool conditionValue = (bool)jumpIf.ConditionValue.DefaultValue;
                
                if (conditionValue)
                {
                    // Condition is always true, never jumps - remove instruction
                    context.RemoveInstruction(i);
                    changed = true;
                }
                else
                {
                    // Condition is always false, always jumps - convert to unconditional
                    var unconditionalJump = new JumpInstruction(jumpIf.JumpTarget);
                    context.ReplaceInstruction(i, unconditionalJump);
                    changed = true;
                }
            }
        }
        
        return changed;
    }
}

public class NopRemovalPattern : PeepholePattern
{
    public override bool Apply(OptimizationContext context)
    {
        bool changed = false;
        var instructions = context.Instructions;
        
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i] is NopInstruction)
            {
                // Only remove if not a jump target
                if (!IsJumpTarget(context, i))
                {
                    context.RemoveInstruction(i);
                    changed = true;
                }
            }
        }
        
        return changed;
    }
    
    private bool IsJumpTarget(OptimizationContext context, int index)
    {
        uint address = context.Instructions[index].InstructionAddress;
        
        foreach (var instruction in context.Instructions)
        {
            if (instruction is JumpInstruction jump && 
                jump.JumpTarget.Address == address)
                return true;
                
            if (instruction is JumpIfFalseInstruction jumpIf && 
                jumpIf.JumpTarget.Address == address)
                return true;
        }
        
        return false;
    }
}
```

### 5.4 Jump Threading Pass

**Priority:** 300  
**Complexity:** Medium  
**Risk:** Medium  
**Expected Impact:** 1-3% instruction reduction

**Pattern:**
```uasm
JUMP L1
...
L1: JUMP L2
→ JUMP L2
```

**Implementation:**

```csharp
public class JumpThreadingPass : IOptimizationPass
{
    public string Name => "Jump Threading";
    public int Priority => 300;
    
    public bool CanRun(OptimizationContext context) => true;
    
    public bool Run(OptimizationContext context)
    {
        bool changed = false;
        var instructions = context.Instructions;
        
        // Build address to instruction index mapping
        var addressToIndex = new Dictionary<uint, int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            addressToIndex[instructions[i].InstructionAddress] = i;
        }
        
        for (int i = 0; i < instructions.Count; i++)
        {
            JumpLabel targetLabel = null;
            
            if (instructions[i] is JumpInstruction jump)
                targetLabel = jump.JumpTarget;
            else if (instructions[i] is JumpIfFalseInstruction jumpIf)
                targetLabel = jumpIf.JumpTarget;
            else
                continue;
            
            // Find the instruction at the target
            if (!addressToIndex.TryGetValue(targetLabel.Address, out int targetIndex))
                continue;
            
            // If target is an unconditional jump, thread through it
            if (instructions[targetIndex] is JumpInstruction targetJump)
            {
                // Update our jump to point to the final destination
                JumpLabel finalTarget = ResolveJumpChain(instructions, addressToIndex, targetJump.JumpTarget, 10);
                
                if (finalTarget != targetLabel)
                {
                    if (instructions[i] is JumpInstruction)
                    {
                        context.ReplaceInstruction(i, new JumpInstruction(finalTarget));
                    }
                    else if (instructions[i] is JumpIfFalseInstruction oldJumpIf)
                    {
                        context.ReplaceInstruction(i, new JumpIfFalseInstruction(finalTarget, oldJumpIf.ConditionValue));
                    }
                    
                    changed = true;
                    context.Metrics.JumpsThreaded++;
                }
            }
        }
        
        return changed;
    }
    
    private JumpLabel ResolveJumpChain(
        List<AssemblyInstruction> instructions,
        Dictionary<uint, int> addressToIndex,
        JumpLabel label,
        int maxDepth)
    {
        if (maxDepth <= 0)
            return label;
        
        if (!addressToIndex.TryGetValue(label.Address, out int index))
            return label;
        
        if (instructions[index] is JumpInstruction jump)
        {
            return ResolveJumpChain(instructions, addressToIndex, jump.JumpTarget, maxDepth - 1);
        }
        
        return label;
    }
}
```

### 5.5 Dead Code Elimination Pass

**Priority:** 310  
**Complexity:** High  
**Risk:** Medium  
**Expected Impact:** 2-10% instruction reduction (varies by codebase)

**Approach:** Build CFG, identify unreachable blocks, remove them.

```csharp
public class DeadCodeEliminationPass : IOptimizationPass
{
    public string Name => "Dead Code Elimination";
    public int Priority => 310;
    
    public bool CanRun(OptimizationContext context) => true;
    
    public bool Run(OptimizationContext context)
    {
        var cfg = context.CFG;
        var reachable = new HashSet<BasicBlock>();
        var worklist = new Queue<BasicBlock>();
        
        // Start from entry block and all export entry points
        worklist.Enqueue(cfg.EntryBlock);
        foreach (var exportBlock in cfg.ExportBlocks)
        {
            worklist.Enqueue(exportBlock);
        }
        
        // Mark all reachable blocks
        while (worklist.Count > 0)
        {
            var block = worklist.Dequeue();
            
            if (reachable.Contains(block))
                continue;
            
            reachable.Add(block);
            
            foreach (var successor in block.Successors)
            {
                if (!reachable.Contains(successor))
                    worklist.Enqueue(successor);
            }
        }
        
        // Remove instructions in unreachable blocks
        bool changed = false;
        var unreachableIndices = new List<int>();
        
        foreach (var block in cfg.AllBlocks)
        {
            if (!reachable.Contains(block))
            {
                for (int i = block.StartIndex; i <= block.EndIndex; i++)
                {
                    unreachableIndices.Add(i);
                }
            }
        }
        
        // Remove in reverse order to preserve indices
        unreachableIndices.Sort();
        unreachableIndices.Reverse();
        
        foreach (int index in unreachableIndices)
        {
            context.RemoveInstruction(index);
            changed = true;
            context.Metrics.DeadBlocksRemoved++;
        }
        
        return changed;
    }
}
```

### 5.6 Control Flow Graph

```csharp
namespace UdonSharp.Compiler.Optimization
{
    public class BasicBlock
    {
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public List<BasicBlock> Successors { get; } = new List<BasicBlock>();
        public List<BasicBlock> Predecessors { get; } = new List<BasicBlock>();
        public bool IsExportEntry { get; set; }
    }
    
    public class ControlFlowGraph
    {
        public BasicBlock EntryBlock { get; private set; }
        public List<BasicBlock> ExportBlocks { get; } = new List<BasicBlock>();
        public List<BasicBlock> AllBlocks { get; } = new List<BasicBlock>();
        
        public ControlFlowGraph(List<AssemblyInstruction> instructions)
        {
            Build(instructions);
        }
        
        private void Build(List<AssemblyInstruction> instructions)
        {
            if (instructions.Count == 0)
                return;
            
            // Step 1: Identify block leaders
            var leaders = new HashSet<int> { 0 };
            
            for (int i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                
                // Target of a jump is a leader
                if (instruction is JumpInstruction jump)
                {
                    int targetIndex = FindInstructionIndex(instructions, jump.JumpTarget.Address);
                    if (targetIndex >= 0)
                        leaders.Add(targetIndex);
                    
                    // Instruction after jump is a leader
                    if (i + 1 < instructions.Count)
                        leaders.Add(i + 1);
                }
                else if (instruction is JumpIfFalseInstruction jumpIf)
                {
                    int targetIndex = FindInstructionIndex(instructions, jumpIf.JumpTarget.Address);
                    if (targetIndex >= 0)
                        leaders.Add(targetIndex);
                    
                    // Fall-through is a leader
                    if (i + 1 < instructions.Count)
                        leaders.Add(i + 1);
                }
                else if (instruction is ExportTag)
                {
                    // Export entry points are leaders
                    if (i + 1 < instructions.Count)
                        leaders.Add(i + 1);
                }
            }
            
            // Step 2: Create basic blocks
            var leaderList = leaders.OrderBy(l => l).ToList();
            var indexToBlock = new Dictionary<int, BasicBlock>();
            
            for (int i = 0; i < leaderList.Count; i++)
            {
                int start = leaderList[i];
                int end = (i + 1 < leaderList.Count) ? leaderList[i + 1] - 1 : instructions.Count - 1;
                
                var block = new BasicBlock
                {
                    StartIndex = start,
                    EndIndex = end
                };
                
                AllBlocks.Add(block);
                indexToBlock[start] = block;
                
                // Check if this is an export entry
                if (start > 0 && instructions[start - 1] is ExportTag)
                {
                    block.IsExportEntry = true;
                    ExportBlocks.Add(block);
                }
            }
            
            EntryBlock = AllBlocks.FirstOrDefault();
            
            // Step 3: Build edges
            foreach (var block in AllBlocks)
            {
                var lastInstruction = instructions[block.EndIndex];
                
                if (lastInstruction is JumpInstruction jump)
                {
                    int targetIndex = FindInstructionIndex(instructions, jump.JumpTarget.Address);
                    if (indexToBlock.TryGetValue(targetIndex, out var targetBlock))
                    {
                        block.Successors.Add(targetBlock);
                        targetBlock.Predecessors.Add(block);
                    }
                }
                else if (lastInstruction is JumpIfFalseInstruction jumpIf)
                {
                    // Jump target
                    int targetIndex = FindInstructionIndex(instructions, jumpIf.JumpTarget.Address);
                    if (indexToBlock.TryGetValue(targetIndex, out var targetBlock))
                    {
                        block.Successors.Add(targetBlock);
                        targetBlock.Predecessors.Add(block);
                    }
                    
                    // Fall-through
                    int fallThroughIndex = block.EndIndex + 1;
                    if (indexToBlock.TryGetValue(fallThroughIndex, out var fallThroughBlock))
                    {
                        block.Successors.Add(fallThroughBlock);
                        fallThroughBlock.Predecessors.Add(block);
                    }
                }
                else if (!(lastInstruction is RetInstruction))
                {
                    // Non-jump, non-return: fall through to next block
                    int nextIndex = block.EndIndex + 1;
                    if (indexToBlock.TryGetValue(nextIndex, out var nextBlock))
                    {
                        block.Successors.Add(nextBlock);
                        nextBlock.Predecessors.Add(block);
                    }
                }
            }
        }
        
        private int FindInstructionIndex(List<AssemblyInstruction> instructions, uint address)
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].InstructionAddress == address)
                    return i;
            }
            return -1;
        }
    }
}
```

### 5.7 Value Coalescence Pass

**Priority:** 400  
**Complexity:** High  
**Risk:** Medium-High  
**Expected Impact:** 5-15% heap reduction

**Purpose:** Reuse heap slots for values with non-overlapping lifetimes.

```csharp
public class ValueCoalescencePass : IOptimizationPass
{
    public string Name => "Value Coalescence";
    public int Priority => 400;
    
    public bool CanRun(OptimizationContext context) => true;
    
    public bool Run(OptimizationContext context)
    {
        // This pass is complex and requires liveness analysis
        // Only coalesce internal/temporary values, never user fields
        
        var internalValues = context.RootValueTable.GetAllValues()
            .Where(v => v.IsLocal && !v.IsConstant)
            .ToList();
        
        if (internalValues.Count < 2)
            return false;
        
        // Build liveness intervals
        var liveness = new Dictionary<Value, (int firstUse, int lastUse)>();
        
        foreach (var value in internalValues)
        {
            var uses = context.GetValueUses(value);
            var defs = context.GetValueDefs(value);
            var allRefs = uses.Union(defs).ToList();
            
            if (allRefs.Count == 0)
                continue;
            
            liveness[value] = (allRefs.Min(), allRefs.Max());
        }
        
        // Find non-overlapping values of compatible types
        var coalesceGroups = new List<List<Value>>();
        var assigned = new HashSet<Value>();
        
        foreach (var value in liveness.Keys.OrderByDescending(v => liveness[v].lastUse - liveness[v].firstUse))
        {
            if (assigned.Contains(value))
                continue;
            
            var group = new List<Value> { value };
            assigned.Add(value);
            
            foreach (var candidate in liveness.Keys.Where(v => !assigned.Contains(v)))
            {
                // Check type compatibility
                if (candidate.UdonType != value.UdonType)
                    continue;
                
                // Check for non-overlapping lifetimes with all group members
                bool canCoalesce = true;
                foreach (var member in group)
                {
                    var memberLife = liveness[member];
                    var candidateLife = liveness[candidate];
                    
                    // Overlapping if one starts before the other ends
                    bool overlaps = !(candidateLife.lastUse < memberLife.firstUse || 
                                     memberLife.lastUse < candidateLife.firstUse);
                    
                    if (overlaps)
                    {
                        canCoalesce = false;
                        break;
                    }
                }
                
                if (canCoalesce)
                {
                    group.Add(candidate);
                    assigned.Add(candidate);
                }
            }
            
            if (group.Count > 1)
                coalesceGroups.Add(group);
        }
        
        // Apply coalescence by rewriting value references
        bool changed = false;
        
        foreach (var group in coalesceGroups)
        {
            var primary = group[0]; // Use first value as the canonical one
            
            foreach (var secondary in group.Skip(1))
            {
                // Rewrite all instructions using secondary to use primary
                RewriteValueReferences(context, secondary, primary);
                context.Metrics.ValuesCoalesced++;
                changed = true;
            }
        }
        
        return changed;
    }
    
    private void RewriteValueReferences(OptimizationContext context, Value from, Value to)
    {
        for (int i = 0; i < context.Instructions.Count; i++)
        {
            var instruction = context.Instructions[i];
            
            switch (instruction)
            {
                case PushInstruction push when push.PushValue == from:
                    context.ReplaceInstruction(i, new PushInstruction(to));
                    break;
                    
                case CopyInstruction copy:
                    Value newSource = copy.SourceValue == from ? to : copy.SourceValue;
                    Value newTarget = copy.TargetValue == from ? to : copy.TargetValue;
                    
                    if (newSource != copy.SourceValue || newTarget != copy.TargetValue)
                        context.ReplaceInstruction(i, new CopyInstruction(newSource, newTarget));
                    break;
                    
                // Add other instruction types as needed
            }
        }
    }
}
```

---

## 6. Testing Strategy

### 6.1 Unit Tests

Create comprehensive unit tests for each optimization pass:

```csharp
[TestFixture]
public class PushPopEliminationTests
{
    [Test]
    public void ShouldRemoveConsecutivePushPop()
    {
        var module = CreateTestModule();
        var value = module.RootTable.CreateInternalValue(GetIntType());
        
        module.AddPush(value);
        module.AddPop();
        
        var optimizer = new AssemblyOptimizer(module);
        optimizer.Optimize();
        
        Assert.AreEqual(0, module.InstructionCount);
    }
    
    [Test]
    public void ShouldNotRemoveNonConsecutivePushPop()
    {
        var module = CreateTestModule();
        var value1 = module.RootTable.CreateInternalValue(GetIntType());
        var value2 = module.RootTable.CreateInternalValue(GetIntType());
        
        module.AddPush(value1);
        module.AddPush(value2);
        module.AddPop();
        module.AddPop();
        
        var optimizer = new AssemblyOptimizer(module);
        optimizer.Optimize();
        
        // Should remain unchanged (different semantics)
        Assert.AreEqual(4, module.InstructionCount);
    }
}

[TestFixture]
public class RedundantCopyEliminationTests
{
    [Test]
    public void ShouldRemoveSelfCopy()
    {
        var module = CreateTestModule();
        var value = module.RootTable.CreateInternalValue(GetIntType());
        
        module.AddCopy(value, value);
        
        var optimizer = new AssemblyOptimizer(module);
        optimizer.Optimize();
        
        Assert.AreEqual(0, module.InstructionCount);
    }
    
    [Test]
    public void ShouldOptimizeCopyChain()
    {
        var module = CreateTestModule();
        var a = module.RootTable.CreateInternalValue(GetIntType());
        var b = module.RootTable.CreateInternalValue(GetIntType());
        var c = module.RootTable.CreateInternalValue(GetIntType());
        
        module.AddCopy(a, b);
        module.AddCopy(b, c);
        
        var optimizer = new AssemblyOptimizer(module);
        optimizer.Optimize();
        
        // Should become: COPY a -> c
        Assert.AreEqual(1, module.InstructionCount);
        var copy = module[0] as CopyInstruction;
        Assert.AreEqual(a, copy.SourceValue);
        Assert.AreEqual(c, copy.TargetValue);
    }
}

[TestFixture]
public class JumpThreadingTests
{
    [Test]
    public void ShouldThreadJumpToJump()
    {
        var module = CreateTestModule();
        var label1 = module.CreateLabel();
        var label2 = module.CreateLabel();
        
        module.AddJump(label1);
        module.LabelJump(label1);
        module.AddJump(label2);
        module.LabelJump(label2);
        module.AddNop();
        
        var optimizer = new AssemblyOptimizer(module);
        optimizer.Optimize();
        
        // First jump should now point to label2
        var jump = module[0] as JumpInstruction;
        Assert.AreEqual(label2, jump.JumpTarget);
    }
}
```

### 6.2 Integration Tests

Test complete compilation with optimization enabled vs disabled:

```csharp
[TestFixture]
public class OptimizationIntegrationTests
{
    [Test]
    public void OptimizedCodeShouldBehaveSameAsUnoptimized()
    {
        string testCode = @"
            using UdonSharp;
            public class TestBehaviour : UdonSharpBehaviour
            {
                private int _value;
                
                public void TestMethod()
                {
                    _value = 1;
                    int temp = _value;
                    _value = temp + 1;
                    
                    if (true)
                    {
                        _value = 3;
                    }
                }
            }";
        
        // Compile without optimization
        var unoptimizedResult = CompileWithOptions(testCode, new UdonSharpCompileOptions 
        { 
            EnableAssemblyOptimization = false 
        });
        
        // Compile with optimization
        var optimizedResult = CompileWithOptions(testCode, new UdonSharpCompileOptions 
        { 
            EnableAssemblyOptimization = true 
        });
        
        // Verify both compile successfully
        Assert.IsTrue(unoptimizedResult.Success);
        Assert.IsTrue(optimizedResult.Success);
        
        // Verify optimized is smaller
        Assert.Less(optimizedResult.InstructionCount, unoptimizedResult.InstructionCount);
        
        // TODO: Add semantic equivalence verification via Udon simulation
    }
}
```

### 6.3 Regression Testing

Compile the entire VRChat examples repository and verify:

1. All scripts compile successfully
2. No behavioral changes (via simulation where possible)
3. Instruction count decreases or stays same (never increases)

### 6.4 Fuzz Testing

Generate random valid U# programs and verify optimization doesn't break them:

```csharp
[TestFixture]
public class FuzzTests
{
    [Test]
    [Repeat(1000)]
    public void RandomProgramShouldOptimizeSafely()
    {
        string randomCode = GenerateRandomUdonSharpCode();
        
        var unoptimizedResult = CompileWithOptions(randomCode, 
            new UdonSharpCompileOptions { EnableAssemblyOptimization = false });
        
        if (!unoptimizedResult.Success)
            return; // Skip invalid programs
        
        var optimizedResult = CompileWithOptions(randomCode, 
            new UdonSharpCompileOptions { EnableAssemblyOptimization = true });
        
        Assert.IsTrue(optimizedResult.Success, 
            $"Optimization broke compilation for: {randomCode}");
    }
}
```

---

## 7. Risk Assessment

### 7.1 Risk Matrix

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| Semantic change from optimization | Low | Critical | Extensive testing, conservative defaults |
| Performance regression (compile time) | Medium | Low | Benchmark, limit iterations |
| Breaks edge case code | Low | Medium | Opt-out flag, regression tests |
| Jump address corruption | Medium | High | Thorough address recalculation |
| Value coalescence breaks code | Medium | High | Conservative liveness analysis |

### 7.2 Mitigation Strategies

**Opt-Out Capability:**
```csharp
public class UdonSharpCompileOptions
{
    // Existing options...
    
    /// <summary>
    /// Enable assembly-level optimization. Default: true.
    /// Set to false for debugging or if issues occur.
    /// </summary>
    public bool EnableAssemblyOptimization { get; set; } = true;
    
    /// <summary>
    /// Specific passes to disable (by name).
    /// </summary>
    public HashSet<string> DisabledOptimizationPasses { get; set; } = new HashSet<string>();
}
```

**Validation Pass:**
Add a verification pass that runs after optimization to check invariants:

```csharp
public class OptimizationVerificationPass : IOptimizationPass
{
    public string Name => "Optimization Verification";
    public int Priority => 999; // Run last
    
    public bool Run(OptimizationContext context)
    {
        // Verify all jump targets are valid
        foreach (var instruction in context.Instructions)
        {
            if (instruction is JumpInstruction jump)
            {
                Assert(jump.JumpTarget.Address != uint.MaxValue, 
                    "Jump target not resolved");
            }
        }
        
        // Verify no orphaned values
        // Verify instruction addresses are sequential
        // etc.
        
        return false; // Never reports changes
    }
}
```

---

## 8. Implementation Timeline

### Phase 1: Foundation (Week 1-2)

| Task | Duration | Dependencies |
|------|----------|--------------|
| Create Optimization folder structure | 1 day | None |
| Implement `IOptimizationPass` interface | 1 day | Folder structure |
| Implement `OptimizationContext` | 2 days | Interface |
| Implement `AssemblyOptimizer` orchestrator | 2 days | Context |
| Add `EnableAssemblyOptimization` option | 1 day | None |
| Integrate into `AssemblyModule.BuildUasmStr()` | 1 day | Optimizer |
| Unit test framework setup | 2 days | All above |

### Phase 2: Core Passes (Week 3-4)

| Task | Duration | Dependencies |
|------|----------|--------------|
| `PushPopEliminationPass` | 2 days | Foundation |
| `RedundantCopyEliminationPass` | 3 days | Foundation |
| `PeepholeOptimizationPass` | 3 days | Foundation |
| Unit tests for core passes | 2 days | Core passes |

### Phase 3: Advanced Passes (Week 5-6)

| Task | Duration | Dependencies |
|------|----------|--------------|
| `ControlFlowGraph` implementation | 3 days | Foundation |
| `JumpThreadingPass` | 2 days | CFG |
| `DeadCodeEliminationPass` | 3 days | CFG |
| Unit tests for advanced passes | 2 days | Advanced passes |

### Phase 4: Value Optimization (Week 7-8)

| Task | Duration | Dependencies |
|------|----------|--------------|
| Liveness analysis | 3 days | CFG |
| `ValueCoalescencePass` | 4 days | Liveness |
| Unit tests for value optimization | 3 days | Value coalescence |

### Phase 5: Testing & Polish (Week 9-10)

| Task | Duration | Dependencies |
|------|----------|--------------|
| Integration tests | 3 days | All passes |
| Regression test suite | 2 days | Integration tests |
| Performance benchmarking | 2 days | All above |
| Documentation | 2 days | All above |
| Bug fixes and refinement | 1 day | Testing |

### Total Estimated Duration: 10 weeks

---

## 9. Future Considerations

### 9.1 Additional Optimization Opportunities

**Constant Propagation:**
Track constant values through copies and use constants directly in instructions.

**Common Subexpression Elimination:**
Detect repeated calculations and reuse results.

**Loop Invariant Code Motion:**
Move calculations that don't change inside loops to outside.

**Instruction Scheduling:**
Reorder instructions to minimize pipeline stalls (if Udon has any notion of pipelining).

### 9.2 Extended Constant Folding

Extend `ConstantExpressionOptimizer` to handle:

```csharp
// String concatenation
"Hello" + " " + "World" → "Hello World"

// Conditional expressions
true ? a : b → a

// Math operations on constants
Math.Min(5, 3) → 3
```

### 9.3 Profile-Guided Optimization

In the future, collect runtime profiles from VRChat worlds to guide optimization decisions:

- Hot path prioritization
- Inline expansion decisions
- Branch prediction hints

### 9.4 Debug Information Preservation

Ensure optimization doesn't break debug information:

```csharp
// Track source mappings through optimization
public class OptimizedDebugInfo
{
    // Maps optimized instruction addresses to original source locations
    public Dictionary<uint, SourceLocation> SourceMap { get; }
}
```

---

## 10. Appendices

### Appendix A: Instruction Size Reference

| Instruction | UASM Size | Component Breakdown |
|-------------|-----------|---------------------|
| NOP | 4 | `NOP` |
| PUSH | 8 | `PUSH, <address>` |
| POP | 4 | `POP` |
| COPY | 20 | `PUSH` + `PUSH` + `COPY` (4) |
| JUMP | 8 | `JUMP, <address>` |
| JUMP_IF_FALSE | 16 | `PUSH` + `JUMP_IF_FALSE, <address>` (8) |
| JUMP_INDIRECT | 8 | `JUMP_INDIRECT, <address>` |
| EXTERN | 8 | `EXTERN, "<signature>"` |
| RET | 20 | `PUSH` + `COPY` (4) + `JUMP_INDIRECT` |

### Appendix B: Sample Optimization Results

**Before Optimization:**
```uasm
.code_start
    _start:
        PUSH, __0_const_int_1
        PUSH, __1_temp
        COPY
        PUSH, __1_temp
        PUSH, __2_result
        COPY
        PUSH, __2_result
        POP
        PUSH, __0_const_int_1
        PUSH, __returnJump
        COPY
        JUMP_INDIRECT, __returnJump
.code_end
```

**After Optimization:**
```uasm
.code_start
    _start:
        PUSH, __0_const_int_1
        PUSH, __2_result
        COPY
        PUSH, __0_const_int_1
        PUSH, __returnJump
        COPY
        JUMP_INDIRECT, __returnJump
.code_end
```

**Reduction:** 12 instructions → 7 instructions (42% reduction)

### Appendix C: File Change Summary

**New Files:**
```
Editor/Compiler/Optimization/
├── AssemblyOptimizer.cs
├── OptimizationContext.cs
├── OptimizationMetrics.cs
├── IOptimizationPass.cs
├── ControlFlowGraph.cs
└── Passes/
    ├── PeepholeOptimizationPass.cs
    ├── RedundantCopyEliminationPass.cs
    ├── PushPopEliminationPass.cs
    ├── JumpThreadingPass.cs
    ├── DeadCodeEliminationPass.cs
    └── ValueCoalescencePass.cs
```

**Modified Files:**
```
Editor/Compiler/Assembly/AssemblyModule.cs
    - Add GetInstructionList() internal method
    - Modify BuildUasmStr() to call optimizer

Editor/Compiler/UdonSharpCompilerV1.cs
    - No changes required (optimization is internal to AssemblyModule)

UdonSharpCompileOptions.cs
    - Add EnableAssemblyOptimization property
    - Add DisabledOptimizationPasses property
```

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | December 2025 | CE Team | Initial proposal |

---

*End of Document*
