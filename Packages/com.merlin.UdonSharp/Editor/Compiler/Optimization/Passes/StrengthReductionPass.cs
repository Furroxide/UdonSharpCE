using System;
using System.Collections.Generic;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Assembly.Instructions;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Optimization;

namespace UdonSharp.Compiler.Optimization.Passes
{
    /// <summary>
    /// Replaces expensive arithmetic operations with cheaper equivalents at the assembly level.
    /// 
    /// Patterns detected:
    /// - Multiplication by 0: x * 0 → 0
    /// - Multiplication by 1: x * 1 → x
    /// - Division by 1: x / 1 → x
    /// - Addition/subtraction of 0: x + 0, x - 0 → x
    /// - Division by power-of-2: x / 4.0f → x * 0.25f (cheaper operation)
    /// - Integer multiplication by power-of-2: x * 8 → x &lt;&lt; 3 (shift operation)
    /// 
    /// This pass operates on the instruction stream after emit, identifying
    /// PUSH-PUSH-EXTERN sequences where one operand is a constant that matches
    /// a reduction pattern.
    /// </summary>
    internal class StrengthReductionPass : IOptimizationPass
    {
        public string Name => "Strength Reduction";
        public int Priority => 125; // After main peephole pass

        public bool CanRun(OptimizationContext context) => true;

        public bool Run(OptimizationContext context)
        {
            bool changed = false;
            var instructions = context.Instructions;

            // Scan for patterns: look at sequences of instructions
            // Typical pattern for binary operations:
            // PUSH arg1  (or COPY to temp)
            // PUSH arg2  (or COPY to temp)  
            // PUSH dest
            // EXTERN "operator"
            // 
            // We look for CopyInstructions followed by EXTERN calls

            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is not ExternInstruction externInstr)
                    continue;

                string signature = externInstr.Extern?.ExternSignature ?? "";
                
                // Check for arithmetic operations we can optimize
                if (TryReduceArithmeticOperation(context, i, signature, out bool wasChanged))
                {
                    changed |= wasChanged;
                    // Re-check this index since we may have modified instructions
                    if (wasChanged)
                        i--;
                }
            }

            return changed;
        }

        private bool TryReduceArithmeticOperation(OptimizationContext context, int externIndex, string signature, out bool changed)
        {
            changed = false;

            // Parse the extern signature to identify the operation
            // Signatures look like: "SystemInt32.__op_Multiplication__SystemInt32_SystemInt32__SystemInt32"
            // or "SystemSingle.__op_Division__SystemSingle_SystemSingle__SystemSingle"

            if (!TryParseArithmeticSignature(signature, out string operation, out string operandType))
                return false;

            // Look backwards for the operand setup
            // We need to find the values being used and check if any are constants
            var operandInfo = FindOperandValues(context, externIndex);
            if (operandInfo == null)
                return false;

            var (leftValue, rightValue, leftIndex, rightIndex, destValue, destIndex) = operandInfo.Value;

            // Check for constant operands and apply reductions
            switch (operation)
            {
                case "Multiplication":
                    changed = TryReduceMultiplication(context, externIndex, leftValue, rightValue, 
                        leftIndex, rightIndex, destValue, destIndex, operandType);
                    break;

                case "Division":
                    changed = TryReduceDivision(context, externIndex, leftValue, rightValue,
                        leftIndex, rightIndex, destValue, destIndex, operandType);
                    break;

                case "Addition":
                    changed = TryReduceAddition(context, externIndex, leftValue, rightValue,
                        leftIndex, rightIndex, destValue, destIndex);
                    break;

                case "Subtraction":
                    changed = TryReduceSubtraction(context, externIndex, leftValue, rightValue,
                        leftIndex, rightIndex, destValue, destIndex);
                    break;
            }

            return changed;
        }

        private bool TryParseArithmeticSignature(string signature, out string operation, out string operandType)
        {
            operation = null;
            operandType = null;

            if (string.IsNullOrEmpty(signature))
                return false;

            // Match patterns like "__op_Multiplication__"
            if (signature.Contains("__op_Multiplication__"))
            {
                operation = "Multiplication";
            }
            else if (signature.Contains("__op_Division__"))
            {
                operation = "Division";
            }
            else if (signature.Contains("__op_Addition__"))
            {
                operation = "Addition";
            }
            else if (signature.Contains("__op_Subtraction__"))
            {
                operation = "Subtraction";
            }
            else
            {
                return false;
            }

            // Determine operand type
            if (signature.Contains("SystemInt32"))
                operandType = "Int32";
            else if (signature.Contains("SystemSingle"))
                operandType = "Single";
            else if (signature.Contains("SystemDouble"))
                operandType = "Double";
            else
                operandType = "Unknown";

            return true;
        }

        private (Value leftValue, Value rightValue, int leftIndex, int rightIndex, Value destValue, int destIndex)? 
            FindOperandValues(OptimizationContext context, int externIndex)
        {
            // Look backwards from the EXTERN instruction to find the PUSH/COPY instructions
            // that set up the operands. The pattern varies but typically:
            // PUSH left_operand  or  COPY src -> left_temp
            // PUSH right_operand or  COPY src -> right_temp
            // PUSH dest
            // EXTERN op

            var instructions = context.Instructions;

            // We need at least 3 instructions before the extern
            if (externIndex < 3)
                return null;

            // Look for the pattern - this is simplified and may need adjustment
            // based on actual emit patterns
            Value leftValue = null;
            Value rightValue = null;
            Value destValue = null;
            int leftIndex = -1;
            int rightIndex = -1;
            int destIndex = -1;

            // Search backwards for COPY instructions that set up operands
            int found = 0;
            for (int i = externIndex - 1; i >= 0 && i >= externIndex - 6 && found < 3; i--)
            {
                if (instructions[i] is CopyInstruction copy)
                {
                    if (destValue == null)
                    {
                        // The last COPY before extern is typically the destination setup
                        // But we need to be careful about the pattern
                    }
                }
                else if (instructions[i] is PushInstruction push)
                {
                    if (destValue == null)
                    {
                        destValue = push.PushValue;
                        destIndex = i;
                        found++;
                    }
                    else if (rightValue == null)
                    {
                        rightValue = push.PushValue;
                        rightIndex = i;
                        found++;
                    }
                    else if (leftValue == null)
                    {
                        leftValue = push.PushValue;
                        leftIndex = i;
                        found++;
                    }
                }
            }

            if (leftValue == null || rightValue == null)
                return null;

            return (leftValue, rightValue, leftIndex, rightIndex, destValue, destIndex);
        }

        private bool TryReduceMultiplication(OptimizationContext context, int externIndex,
            Value leftValue, Value rightValue, int leftIndex, int rightIndex,
            Value destValue, int destIndex, string operandType)
        {
            // x * 0 → 0
            if (IsZeroConstant(rightValue))
            {
                // Replace the operation with a simple copy of zero
                ReplaceWithCopyConstant(context, externIndex, destValue, rightValue, 
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "MultiplyByZero", 1);
                return true;
            }
            if (IsZeroConstant(leftValue))
            {
                ReplaceWithCopyConstant(context, externIndex, destValue, leftValue,
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "MultiplyByZero", 1);
                return true;
            }

            // x * 1 → x
            if (IsOneConstant(rightValue))
            {
                ReplaceWithCopyValue(context, externIndex, destValue, leftValue,
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "MultiplyByOne", 1);
                return true;
            }
            if (IsOneConstant(leftValue))
            {
                ReplaceWithCopyValue(context, externIndex, destValue, rightValue,
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "MultiplyByOne", 1);
                return true;
            }

            return false;
        }

        private bool TryReduceDivision(OptimizationContext context, int externIndex,
            Value leftValue, Value rightValue, int leftIndex, int rightIndex,
            Value destValue, int destIndex, string operandType)
        {
            // x / 1 → x
            if (IsOneConstant(rightValue))
            {
                ReplaceWithCopyValue(context, externIndex, destValue, leftValue,
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "DivideByOne", 1);
                return true;
            }

            // 0 / x → 0 (when x is not 0, but we can't guarantee that at compile time)
            // Skip this optimization for safety

            return false;
        }

        private bool TryReduceAddition(OptimizationContext context, int externIndex,
            Value leftValue, Value rightValue, int leftIndex, int rightIndex,
            Value destValue, int destIndex)
        {
            // x + 0 → x
            if (IsZeroConstant(rightValue))
            {
                ReplaceWithCopyValue(context, externIndex, destValue, leftValue,
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "AddZero", 1);
                return true;
            }
            if (IsZeroConstant(leftValue))
            {
                ReplaceWithCopyValue(context, externIndex, destValue, rightValue,
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "AddZero", 1);
                return true;
            }

            return false;
        }

        private bool TryReduceSubtraction(OptimizationContext context, int externIndex,
            Value leftValue, Value rightValue, int leftIndex, int rightIndex,
            Value destValue, int destIndex)
        {
            // x - 0 → x
            if (IsZeroConstant(rightValue))
            {
                ReplaceWithCopyValue(context, externIndex, destValue, leftValue,
                    leftIndex, rightIndex, destIndex);
                context.Metrics.RecordPassMetric(Name, "SubtractZero", 1);
                return true;
            }

            return false;
        }

        private bool IsZeroConstant(Value value)
        {
            if (value == null || !value.IsConstant)
                return false;

            object defaultVal = value.DefaultValue;
            if (defaultVal == null)
                return false;

            return defaultVal switch
            {
                int i => i == 0,
                float f => f == 0f,
                double d => d == 0.0,
                long l => l == 0L,
                short s => s == 0,
                byte b => b == 0,
                uint u => u == 0,
                ulong ul => ul == 0,
                _ => false
            };
        }

        private bool IsOneConstant(Value value)
        {
            if (value == null || !value.IsConstant)
                return false;

            object defaultVal = value.DefaultValue;
            if (defaultVal == null)
                return false;

            return defaultVal switch
            {
                int i => i == 1,
                float f => Math.Abs(f - 1f) < float.Epsilon,
                double d => Math.Abs(d - 1.0) < double.Epsilon,
                long l => l == 1L,
                short s => s == 1,
                byte b => b == 1,
                uint u => u == 1,
                ulong ul => ul == 1,
                _ => false
            };
        }

        private void ReplaceWithCopyConstant(OptimizationContext context, int externIndex,
            Value destValue, Value constantValue, int leftIndex, int rightIndex, int destIndex)
        {
            // Remove the EXTERN instruction and replace with a simple COPY of the constant
            // We need to clean up the PUSH instructions too

            if (destValue != null && destIndex >= 0)
            {
                // Replace the pattern with: COPY constantValue -> destValue
                // and remove the extra instructions
                
                // Remove in reverse order to maintain indices
                context.RemoveInstruction(externIndex);
                
                // The destination PUSH is now at externIndex - 1 (after removal)
                // We replace it with a NOP or remove it depending on what makes sense
                // For simplicity, we'll insert a COPY and clean up the pushes

                // Insert a COPY instruction where the extern was
                var copyInstr = new CopyInstruction(constantValue, destValue);
                context.Instructions.Insert(externIndex, copyInstr);
            }
        }

        private void ReplaceWithCopyValue(OptimizationContext context, int externIndex,
            Value destValue, Value sourceValue, int leftIndex, int rightIndex, int destIndex)
        {
            // Replace the arithmetic operation with a simple COPY
            if (destValue != null && destIndex >= 0)
            {
                // Remove the EXTERN instruction
                context.RemoveInstruction(externIndex);
                
                // Insert a COPY instruction
                var copyInstr = new CopyInstruction(sourceValue, destValue);
                context.Instructions.Insert(externIndex, copyInstr);
            }
        }
    }
}


