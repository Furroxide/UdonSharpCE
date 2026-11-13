
using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.Compiler.Binder
{
    /// <summary>
    /// <c>source as T</c> where the answer depends on the runtime type of <c>source</c> and <c>T</c> is a type Udon
    /// knows, such as <c>component as BoxCollider</c> or <c>world.GetComponentArray(slot) as Vector3[]</c>.
    ///
    /// Lowering this to a plain cast copied the value through whatever its runtime type was, so the result was
    /// never null: the caller's null check passed and the next extern call on the wrong type halted the
    /// behaviour. The test here is <c>typeof(T).IsInstanceOfType(source)</c>, which is also false for null.
    /// </summary>
    internal sealed class BoundAsExpression : BoundExpression
    {
        private TypeSymbol TargetType { get; }

        private MethodSymbol IsInstanceOfTypeMethod { get; }

        public override TypeSymbol ValueType => TargetType;

        public BoundAsExpression(AbstractPhaseContext context, SyntaxNode node, BoundExpression sourceExpression, TypeSymbol targetType)
            : base(node, sourceExpression)
        {
            TargetType = targetType;
            IsInstanceOfTypeMethod = context.GetTypeSymbol(typeof(Type))
                .GetMembers<MethodSymbol>(nameof(Type.IsInstanceOfType), context)
                .First(e => e.Parameters.Length == 1);
        }

        public override Value EmitValue(EmitContext context)
        {
            Value sourceValue;
            Value isInstance;

            using (context.InterruptAssignmentScope())
            {
                sourceValue = context.EmitValue(SourceExpression);

                Value targetTypeValue = context.GetConstantValue(context.GetTypeSymbol(typeof(Type)), TargetType.UdonType.SystemType);

                isInstance = context.EmitValue(BoundInvocationExpression.CreateBoundInvocation(context, SyntaxNode,
                    IsInstanceOfTypeMethod, BoundAccessExpression.BindAccess(targetTypeValue),
                    new BoundExpression[] { BoundAccessExpression.BindAccess(sourceValue) }));
            }

            // A fresh value, never the assignment target: the target must not be written on only one of the two
            // paths below while copies of its old value may still be live.
            Value resultValue = context.CreateInternalValue(TargetType);

            JumpLabel notInstanceLabel = context.Module.CreateLabel();
            JumpLabel endLabel = context.Module.CreateLabel();

            context.Module.AddJumpIfFalse(notInstanceLabel, isInstance);
            context.Module.AddCopy(sourceValue, resultValue);
            context.Module.AddJump(endLabel);

            context.Module.LabelJump(notInstanceLabel);
            context.Module.AddCopy(context.GetConstantValue(TargetType, null), resultValue);

            context.Module.LabelJump(endLabel);

            return resultValue;
        }
    }
}
