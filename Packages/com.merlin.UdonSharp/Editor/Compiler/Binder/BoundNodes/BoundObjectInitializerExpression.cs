
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.Compiler.Binder
{
    /// <summary>
    /// Represents an object creation expression with an initializer list.
    /// For example: new Foo { Bar = 1, Baz = 2 }
    /// </summary>
    internal class BoundObjectInitializerExpression : BoundExpression
    {
        /// <summary>
        /// The constructor invocation that creates the object.
        /// </summary>
        private BoundExpression ConstructorInvocation { get; }
        
        /// <summary>
        /// The type being created.
        /// </summary>
        private TypeSymbol CreatedType { get; }
        
        /// <summary>
        /// The member symbols being assigned (fields or properties).
        /// </summary>
        private Symbol[] MemberSymbols { get; }
        
        /// <summary>
        /// The values to assign to each member.
        /// </summary>
        private BoundExpression[] MemberValues { get; }

        public override TypeSymbol ValueType => CreatedType;

        public BoundObjectInitializerExpression(
            SyntaxNode node,
            BoundExpression constructorInvocation,
            TypeSymbol createdType,
            Symbol[] memberSymbols,
            BoundExpression[] memberValues)
            : base(node, null)
        {
            ConstructorInvocation = constructorInvocation;
            CreatedType = createdType;
            MemberSymbols = memberSymbols;
            MemberValues = memberValues;
        }

        public override Value EmitValue(EmitContext context)
        {
            // First, emit the constructor to create the object
            Value createdObject = context.EmitValue(ConstructorInvocation);

            if (MemberSymbols == null || MemberSymbols.Length == 0)
                return createdObject;

            // Create access expression for the created object
            BoundAccessExpression objectAccess = BoundAccessExpression.BindAccess(createdObject);

            using (context.InterruptAssignmentScope())
            {
                // For each member assignment, emit the set operation
                for (int i = 0; i < MemberSymbols.Length; i++)
                {
                    Symbol memberSymbol = MemberSymbols[i];
                    BoundExpression valueExpression = MemberValues[i];

                    // Bind access to the member on the created object
                    BoundAccessExpression memberAccess = BoundAccessExpression.BindAccess(
                        context, 
                        SyntaxNode, 
                        memberSymbol, 
                        objectAccess);

                    // Emit the set operation
                    context.EmitSet(memberAccess, valueExpression);
                }
            }

            return createdObject;
        }

        protected override void ReleaseCowValuesImpl(EmitContext context)
        {
            ConstructorInvocation?.ReleaseCowReferences(context);
            
            if (MemberValues != null)
            {
                foreach (var value in MemberValues)
                {
                    value?.ReleaseCowReferences(context);
                }
            }
        }
    }
}






