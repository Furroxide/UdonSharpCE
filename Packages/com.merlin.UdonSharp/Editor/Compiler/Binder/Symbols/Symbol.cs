
using System;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using UdonSharp.Compiler.Binder;

namespace UdonSharp.Compiler.Symbols
{
    /// <summary>
    /// The base type for all U# symbols
    /// </summary>
    internal abstract class Symbol
    {
        /// <summary>
        /// The symbol this is declared in, for instance if this is a field symbol, will point to the declaring class symbol, or if a local variable symbol, will point to a method symbol
        /// </summary>
        public TypeSymbol ContainingType { get; protected set; }

        /// <summary>
        /// Used to retrieve the non-generic-typed version of this symbol, for instance if you're getting a symbol for DoThing<int>(), will return DoThing<>()
        /// </summary>
        public Symbol OriginalSymbol { get; protected set; }

        /// <summary>
        /// The source Roslyn-generated symbol for this U# symbol
        /// </summary>
        public virtual ISymbol RoslynSymbol { get; }

        /// <summary>
        /// Name of the symbol in code
        /// </summary>
        public virtual string Name => RoslynSymbol.Name;

        /// <summary>
        /// If this symbol has had its body visited and types linked
        /// </summary>
        public virtual bool IsBound => true;

        /// <summary>
        /// If this is a symbol pointing to an Udon extern
        /// </summary>
        public virtual bool IsExtern => false;

        /// <summary>
        /// If this is a static symbol. This may return true on fields, properties, and methods. Classes will only return true on this if they are marked as a static class.
        /// </summary>
        public virtual bool IsStatic => RoslynSymbol.IsStatic;
        
        public ImmutableArray<Attribute> SymbolAttributes { get; private set; }
        
        protected Symbol(ISymbol sourceSymbol, AbstractPhaseContext context)
        {
            RoslynSymbol = sourceSymbol;

            if (sourceSymbol?.ContainingType != null && !(sourceSymbol is ITypeParameterSymbol))
                ContainingType = context.GetTypeSymbolWithoutRedirect(sourceSymbol.ContainingType);
        }

        internal bool HasAttribute<T>() where T : Attribute
        {
            return GetAttribute<T>() != null;
        }

        internal T GetAttribute<T>() where T : Attribute
        {
            if (!SymbolAttributes.IsDefaultOrEmpty)
            {
                foreach (Attribute symbolAttribute in SymbolAttributes)
                {
                    if (symbolAttribute is T symbolT)
                        return symbolT;
                }
            }

            if (RoslynSymbol == null)
                return null;

            foreach (AttributeData attributeData in RoslynSymbol.GetAttributes())
            {
                if (!(TryCreateAttributeInstance(attributeData) is T symbolAttribute))
                    continue;

                return symbolAttribute;
            }

            return null;
        }

        /// <summary>
        /// Gets direct dependencies of this symbol, this symbol must be bound for the dependencies to be valid
        /// Will throw exception if this symbol has not been resolved
        /// </summary>
        /// <value></value>
        public ImmutableArray<Symbol> DirectDependencies { get; private set; }

        public void SetDependencies(IEnumerable<Symbol> dependencies)
        {
            if (DirectDependencies != null)
                throw new InvalidOperationException($"Cannot set dependencies on symbol {this} since dependencies have already been set");

            DirectDependencies = ImmutableArray.CreateRange(dependencies);
        }

        private void SetAttributes(ImmutableArray<Attribute> attributes)
        {
            if (SymbolAttributes != null)
                throw new InvalidOperationException("Cannot set attributes multiple times on the same symbol");
            
            SymbolAttributes = attributes;
        }

        /// <summary>
        /// Gets direct dependencies of this symbol, this symbol must be resolved for the dependencies to be valid
        /// Will throw exception if this symbol has not been resolved
        /// </summary>
        /// <returns></returns>
        public ImmutableArray<Symbol> GetDirectDependencies<T>() where T : Symbol
        {
            return ImmutableArray.CreateRange<Symbol>(DirectDependencies.OfType<T>()); // Todo: cache
        }

        /// <summary>
        /// Returns all symbols that are an implementation of this symbol.
        /// This means interfaces will collect all the used implementations of the interface, methods will collect all overrides of the method, properties will collect all overrides, etc.
        /// </summary>
        /// <returns></returns>
        public virtual ImmutableArray<Symbol> GetImplementations() => ImmutableArray<Symbol>.Empty;

        public abstract void Bind(BindContext context);

        private static object ConvertAttributeValue(TypedConstant constant)
        {
            if (constant.IsNull)
                return null;

            if (constant.Kind == TypedConstantKind.Array)
            {
                if (!(constant.Type is IArrayTypeSymbol arrayTypeSymbol) ||
                    !TypeSymbol.TryGetSystemType(arrayTypeSymbol.ElementType, out Type elementType))
                {
                    return constant.Values.Select(ConvertAttributeValue).ToArray();
                }

                Array values = Array.CreateInstance(elementType, constant.Values.Length);
                for (int i = 0; i < constant.Values.Length; ++i)
                {
                    values.SetValue(ConvertAttributeValue(constant.Values[i]), i);
                }

                return values;
            }

            if (constant.Type != null &&
                constant.Type.TypeKind == TypeKind.Enum &&
                TypeSymbol.TryGetSystemType(constant.Type, out Type enumType))
            {
                return Enum.ToObject(enumType, constant.Value);
            }

            if (constant.Value is ITypeSymbol typeSymbol &&
                TypeSymbol.TryGetSystemType(typeSymbol, out Type systemType))
            {
                return systemType;
            }

            return constant.Value;
        }

        private static Attribute TryCreateAttributeInstance(AttributeData attribute)
        {
            if (attribute.AttributeClass == null ||
                !TypeSymbol.TryGetSystemType(attribute.AttributeClass, out Type attributeType) ||
                !typeof(Attribute).IsAssignableFrom(attributeType))
            {
                return null;
            }

            object[] attributeArgs = new object[attribute.ConstructorArguments.Length];
            for (int i = 0; i < attributeArgs.Length; ++i)
            {
                attributeArgs[i] = ConvertAttributeValue(attribute.ConstructorArguments[i]);
            }

            try
            {
                if (!(Activator.CreateInstance(attributeType, attributeArgs) is Attribute attributeInstance))
                    return null;

                const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (KeyValuePair<string, TypedConstant> namedArgument in attribute.NamedArguments)
                {
                    object namedValue = ConvertAttributeValue(namedArgument.Value);

                    PropertyInfo property = attributeType.GetProperty(namedArgument.Key, bindingFlags);
                    if (property?.CanWrite == true)
                    {
                        property.SetValue(attributeInstance, namedValue);
                        continue;
                    }

                    FieldInfo field = attributeType.GetField(namedArgument.Key, bindingFlags);
                    field?.SetValue(attributeInstance, namedValue);
                }

                return attributeInstance;
            }
            catch (Exception)
            {
                return null;
            }
        }

        protected void SetupAttributes(BindContext context)
        {
            var attribData = RoslynSymbol.GetAttributes();
            List<Attribute> attributes = new List<Attribute>();

            for (int i = 0; i < attribData.Length; ++i)
            {
                AttributeData attribute = attribData[i];

                Attribute attributeInstance = TryCreateAttributeInstance(attribute);
                if (attributeInstance != null)
                    attributes.Add(attributeInstance);
            }

            SetAttributes(attributes.ToImmutableArray());
        }

        // UdonSharp symbols will always have a Roslyn analogue symbol that refers to exactly the same data, the only difference is that UdonSharp symbols will annotate more information and track dependencies more explicitly
        // So we just use the root symbol hash code and equals here
        // This is not needed most of the time since you must retrieve the symbols from the Roslyn symbols in most contexts that matter for comparison. The retrieval mechanisms already guarantee that there is one UdonSharp symbol for a given Roslyn symbol
        public override int GetHashCode()
        {
            return RoslynSymbol.GetHashCode();
        }

        protected bool Equals(Symbol other)
        {
            return RoslynSymbol.Equals(other.RoslynSymbol);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Symbol) obj);
        }

        // This may get more descriptive info in the future
        public override string ToString()
        {
            return RoslynSymbol.ToString();
        }

        /// <summary>
        /// Stops complaints about reference comparison. In this context we know that there will only ever be one Symbol for a given Roslyn symbol. So it is safe to do reference comparisons.
        /// </summary>
        /// <param name="lhs"></param>
        /// <param name="rhs"></param>
        /// <returns></returns>
        public static bool operator ==(Symbol lhs, Symbol rhs)
        {
            return ReferenceEquals(lhs, rhs);
        }

        public static bool operator !=(Symbol lhs, Symbol rhs)
        {
            return !(lhs == rhs);
        }
    }
}
