using System.Collections.Generic;
using System.Linq;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Optimization;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Detects repeated EXTERN calls with identical signatures and arguments within
    /// a basic block and replaces subsequent calls with copies from the first result.
    /// 
    /// This provides binary-level common subexpression elimination for extern calls,
    /// complementing the C# level ExternCallCachingOptimizer.
    /// 
    /// Pattern detected:
    /// PUSH a, PUSH b, PUSH result1, EXTERN op
    /// ...
    /// PUSH a, PUSH b, PUSH result2, EXTERN op  // same op, same args
    /// →
    /// PUSH a, PUSH b, PUSH result1, EXTERN op
    /// ...
    /// COPY result1 → result2
    /// </summary>
    internal class ExternDeduplicationPass : IOptimizationPass
    {
        public string Name => "Extern Deduplication";
        public int Priority => 130; // After peephole, before copy propagation

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            ControlFlowGraph cfg = context.CFG;
            if (cfg.EntryBlock == null)
                return false;

            bool changed = false;

            // Process each basic block independently
            foreach (BasicBlock block in cfg.AllBlocks)
            {
                changed |= ProcessBlock(context, block);
            }

            return changed;
        }

        private bool ProcessBlock(OptimizationContext context, BasicBlock block)
        {
            var instructions = context.Instructions;
            bool changed = false;

            // Track extern calls we've seen in this block: signature + args fingerprint -> (result value, extern index)
            var seenExterns = new Dictionary<string, (Value resultValue, int externIndex)>();

            for (int i = block.StartIndex; i <= block.EndIndex && i < instructions.Count; i++)
            {
                if (instructions[i] is not ExternInstruction externInstr)
                    continue;

                string signature = externInstr.Extern?.ExternSignature;
                if (string.IsNullOrEmpty(signature))
                    continue;

                // Skip extern calls that have side effects (setters, etc.)
                if (!IsPureExtern(signature))
                    continue;

                // Find the operand setup for this extern call
                var operandInfo = FindOperandPattern(context, i, block.StartIndex);
                if (operandInfo == null)
                    continue;

                // Create a fingerprint for this call: signature + argument values
                string fingerprint = CreateFingerprint(signature, operandInfo.Value.argValues);

                if (seenExterns.TryGetValue(fingerprint, out var previous))
                {
                    // We've seen this exact call before in this block
                    // Replace this call with a COPY from the previous result
                    Value destValue = operandInfo.Value.destValue;
                    
                    if (destValue != null && previous.resultValue != null)
                    {
                        // Remove the argument pushes and extern, replace with copy
                        int removeCount = operandInfo.Value.pushCount + 1; // +1 for the extern itself
                        int removeStart = i - operandInfo.Value.pushCount;

                        // Ensure we're still within the block
                        if (removeStart >= block.StartIndex)
                        {
                            // Remove instructions in reverse order
                            for (int j = 0; j < removeCount; j++)
                            {
                                context.RemoveInstruction(removeStart);
                            }

                            // Insert copy instruction
                            var copyInstr = new CopyInstruction(previous.resultValue, destValue);
                            context.Instructions.Insert(removeStart, copyInstr);

                            context.Metrics.RecordPassMetric(Name, "ExternsDeduped", 1);
                            changed = true;

                            // Adjust block end index since we removed instructions
                            // The block structure is now invalid, but we'll rebuild CFG next iteration
                            // For safety, break out of this block
                            break;
                        }
                    }
                }
                else
                {
                    // First occurrence - record it
                    if (operandInfo.Value.destValue != null)
                    {
                        seenExterns[fingerprint] = (operandInfo.Value.destValue, i);
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// Determines if an extern call is pure (no side effects, result depends only on arguments).
        /// </summary>
        private bool IsPureExtern(string signature)
        {
            // Exclude setters, methods that modify state
            if (signature.Contains("__Set") || signature.Contains("__set_"))
                return false;

            // Include getters and operators (usually pure)
            if (signature.Contains("__Get") || signature.Contains("__get_") ||
                signature.Contains("__op_"))
                return true;

            // Include common pure method patterns
            if (signature.Contains("__Equals__") ||
                signature.Contains("__GetHashCode__") ||
                signature.Contains("__ToString__"))
                return true;

            // Math operations are pure
            if (signature.Contains("Mathf") || signature.Contains("Math"))
                return true;

            // Vector/Quaternion operations are usually pure
            if (signature.Contains("Vector") || signature.Contains("Quaternion") ||
                signature.Contains("Matrix") || signature.Contains("Color"))
            {
                // But not setters
                if (!signature.Contains("__Set"))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Finds the operand pattern for an extern call.
        /// Returns the argument values, destination value, and count of push instructions.
        /// </summary>
        private (List<Value> argValues, Value destValue, int pushCount)? FindOperandPattern(
            OptimizationContext context, int externIndex, int blockStart)
        {
            var instructions = context.Instructions;
            var argValues = new List<Value>();
            Value destValue = null;
            int pushCount = 0;

            // Look backwards from the extern to find PUSH instructions
            // The typical pattern is: PUSH arg1, PUSH arg2, ..., PUSH dest, EXTERN
            for (int i = externIndex - 1; i >= blockStart && i >= externIndex - 10; i--)
            {
                if (instructions[i] is PushInstruction push)
                {
                    if (destValue == null)
                    {
                        // Last push before extern is typically the destination
                        destValue = push.PushValue;
                    }
                    else
                    {
                        // Earlier pushes are arguments
                        argValues.Insert(0, push.PushValue);
                    }
                    pushCount++;
                }
                else if (instructions[i] is CopyInstruction ||
                         instructions[i] is ExternInstruction ||
                         instructions[i] is JumpInstruction ||
                         instructions[i] is JumpIfFalseInstruction ||
                         instructions[i] is RetInstruction)
                {
                    // Hit a different instruction type, stop looking
                    break;
                }
            }

            if (pushCount == 0)
                return null;

            return (argValues, destValue, pushCount);
        }

        /// <summary>
        /// Creates a unique fingerprint for an extern call based on signature and arguments.
        /// </summary>
        private string CreateFingerprint(string signature, List<Value> argValues)
        {
            // Use signature + argument value unique IDs
            var parts = new List<string> { signature };
            foreach (var arg in argValues)
            {
                // Use the unique ID of constant values, or the value reference for variables
                if (arg.IsConstant)
                {
                    parts.Add($"const:{arg.DefaultValue}");
                }
                else
                {
                    parts.Add($"val:{arg.UniqueID}");
                }
            }
            return string.Join("|", parts);
        }
    }
}

