using System;

// This attribute must be in System.Runtime.CompilerServices namespace
// for the C# compiler to recognize it as the async method builder attribute
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Indicates the type of the async method builder that should be used by a language compiler
    /// to build the attributed async method or to build the attributed type when used as 
    /// the return type of an async method.
    /// </summary>
    /// <remarks>
    /// This attribute is required for custom task-like types to work with async/await.
    /// It must be in the System.Runtime.CompilerServices namespace for the compiler to find it.
    /// 
    /// In newer .NET versions (4.7+), this is built-in. For Unity's older .NET,
    /// we define it here to enable UdonTask async support.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Delegate | AttributeTargets.Enum | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class AsyncMethodBuilderAttribute : Attribute
    {
        /// <summary>
        /// Initializes the <see cref="AsyncMethodBuilderAttribute"/> with the specified builder type.
        /// </summary>
        /// <param name="builderType">The Type of the associated builder.</param>
        public AsyncMethodBuilderAttribute(Type builderType)
        {
            BuilderType = builderType;
        }

        /// <summary>
        /// Gets the Type of the associated builder.
        /// </summary>
        public Type BuilderType { get; }
    }
}


























