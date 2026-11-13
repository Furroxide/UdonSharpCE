using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace UdonSharp.CE.Editor.Net
{
    internal enum SyncOnJoinValueKind
    {
        Primitive,
        Enum,
        String,
        UnityStruct,
        DataDictionary,
        DataList,
        Array,
        List,
        CustomObject,
    }

    /// <summary>
    /// How a primitive rides inside the JSON payload.
    ///
    /// JSON numbers are IEEE doubles, so <c>BigInteger</c> (long/ulong) is written as a string --
    /// anything above 2^53 would otherwise come back rounded with no error anywhere. <c>Floating</c>
    /// needs its own handling because <c>VRCJson.TrySerializeToJson</c> fails outright on NaN/Infinity,
    /// which would blank the whole payload over a single bad component.
    /// </summary>
    internal enum SyncOnJoinPrimitiveEncoding
    {
        None,
        Boolean,
        Number,
        BigInteger,
        Floating,
    }

    internal sealed class SyncOnJoinTypeSpec
    {
        public SyncOnJoinValueKind Kind { get; set; }
        public ITypeSymbol TypeSymbol { get; set; }
        public string TypeName { get; set; }
        public string PrimitiveReadExpression { get; set; }
        public SyncOnJoinPrimitiveEncoding PrimitiveEncoding { get; set; }

        /// <summary>
        /// Inclusive bounds, as C# double literals, a decoded JSON number has to sit inside before it is
        /// converted to this integer or enum type. Null for everything else.
        ///
        /// Udon has no unchecked conversions: UdonSharp lowers <c>(byte)number</c> to
        /// <c>System.Convert.ToByte</c>, which throws on a value that does not fit, and lowers a number
        /// to an enum it does not compile itself into an array lookup. Either one, hit by a payload
        /// written by another world version or a hostile owner, halts the behaviour inside
        /// OnDeserialization for the rest of the session.
        /// </summary>
        public string NumberMinLiteral { get; set; }
        public string NumberMaxLiteral { get; set; }

        /// <summary>
        /// <c>long</c> or <c>ulong</c> for an enum backed by one, null for every other enum. Those ride
        /// as strings for the same reason a long field does: a JSON number is a double, so a flags value
        /// with a bit above 2^53 set came back with its low bits rounded away.
        /// </summary>
        public string EnumWideUnderlyingTypeName { get; set; }

        public string[] ComponentNames { get; set; }
        public SyncOnJoinTypeSpec[] ComponentTypes { get; set; }
        public SyncOnJoinTypeSpec ElementType { get; set; }
        public IReadOnlyList<SyncOnJoinMemberSpec> Members { get; set; }
        public bool IsReferenceType { get; set; }
    }

    internal sealed class SyncOnJoinMemberSpec
    {
        public string FieldName { get; set; }
        public string Key { get; set; }
        public SyncOnJoinTypeSpec Type { get; set; }
    }

    internal static class SyncOnJoinTypeSupport
    {
        private static readonly SymbolDisplayFormat FullyQualifiedFormat =
            SymbolDisplayFormat.FullyQualifiedFormat;

        public static bool TryCreateSpec(ITypeSymbol typeSymbol, out SyncOnJoinTypeSpec spec, out string reason)
        {
            var stack = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            return TryCreateSpec(typeSymbol, stack, out spec, out reason);
        }

        public static bool HasAttribute(ISymbol symbol, string attributeBaseName)
        {
            if (symbol == null)
                return false;

            string shortName = attributeBaseName;
            string fullName = attributeBaseName.EndsWith("Attribute", StringComparison.Ordinal)
                ? attributeBaseName
                : attributeBaseName + "Attribute";

            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                string name = attribute.AttributeClass?.Name;
                string qualifiedName = attribute.AttributeClass?.ToDisplayString();
                if (name == shortName || name == fullName ||
                    qualifiedName == shortName || qualifiedName == fullName ||
                    qualifiedName?.EndsWith("." + shortName, StringComparison.Ordinal) == true ||
                    qualifiedName?.EndsWith("." + fullName, StringComparison.Ordinal) == true)
                {
                    return true;
                }
            }

            return false;
        }

        public static string GetAttributeStringArgument(ISymbol symbol, string attributeBaseName)
        {
            if (symbol == null)
                return null;

            string shortName = attributeBaseName;
            string fullName = attributeBaseName.EndsWith("Attribute", StringComparison.Ordinal)
                ? attributeBaseName
                : attributeBaseName + "Attribute";

            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                string name = attribute.AttributeClass?.Name;
                if (name != shortName && name != fullName)
                    continue;

                ImmutableArray<TypedConstant> args = attribute.ConstructorArguments;
                if (args.Length > 0 && args[0].Value is string value && !string.IsNullOrEmpty(value))
                    return value;
            }

            return null;
        }

        /// <summary>
        /// Namespace assumed for unqualified names passed to <see cref="FindAttribute"/>. Every attribute this
        /// class looks up by identity is one of CE's own networking attributes.
        /// </summary>
        private const string CENetAttributeNamespace = "UdonSharp.CE.Net";

        /// <summary>
        /// Finds an attribute through the semantic model, matching subclassed attribute types as well.
        ///
        /// Callers must never compare <c>AttributeSyntax.Name.ToString()</c>: a <c>using</c> alias makes the
        /// written text differ from the attribute's identity, which is exactly how the analyzer and the
        /// emitter end up disagreeing about which fields are synced.
        /// </summary>
        /// <param name="symbol">Symbol whose attribute list is searched.</param>
        /// <param name="attributeTypeName">
        /// Metadata name of the attribute. Unqualified names are resolved against
        /// <see cref="CENetAttributeNamespace"/>; pass a namespace-qualified name for anything else.
        /// </param>
        public static AttributeData FindAttribute(ISymbol symbol, string attributeTypeName)
        {
            if (symbol == null || string.IsNullOrEmpty(attributeTypeName))
                return null;

            // Match on the fully qualified identity, not the simple name: any user type in the project that
            // happens to be called SyncOnJoin/SyncOnJoinAttribute/SyncSerializer would otherwise be taken for
            // CE's, and the emitter would generate sync glue for fields the author never opted in to syncing.
            string qualifiedName = attributeTypeName.IndexOf('.') >= 0
                ? attributeTypeName
                : CENetAttributeNamespace + "." + attributeTypeName;

            string simpleName = qualifiedName.Substring(qualifiedName.LastIndexOf('.') + 1);
            string writtenName = simpleName.EndsWith("Attribute", StringComparison.Ordinal)
                ? simpleName.Substring(0, simpleName.Length - "Attribute".Length)
                : simpleName;

            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                INamedTypeSymbol attributeClass = attribute.AttributeClass;

                // An attribute the model could not bind produces an error symbol that carries only the name as
                // written, with no namespace to compare against. Name matching stays scoped to error symbols
                // so that a missing metadata reference degrades into the previous behaviour instead of
                // dropping glue for every field in the project, while resolved symbols stay strictly matched.
                if (attributeClass == null || attributeClass.TypeKind == TypeKind.Error)
                {
                    string errorName = attributeClass?.Name;
                    if (errorName == simpleName || errorName == writtenName)
                        return attribute;

                    continue;
                }

                for (INamedTypeSymbol current = attributeClass; current != null; current = current.BaseType)
                {
                    if (current.ToDisplayString() == qualifiedName)
                        return attribute;
                }
            }

            return null;
        }

        public static bool IsListType(ITypeSymbol typeSymbol, out ITypeSymbol elementType)
        {
            elementType = null;
            if (!(typeSymbol is INamedTypeSymbol namedType) || !namedType.IsGenericType || namedType.TypeArguments.Length != 1)
                return false;

            string original = namedType.OriginalDefinition.ToDisplayString();
            if (original != "System.Collections.Generic.List<T>")
                return false;

            elementType = namedType.TypeArguments[0];
            return true;
        }

        public static bool IsManualOnlyRoot(ITypeSymbol typeSymbol, out string reason)
        {
            return !TryCreateSpec(typeSymbol, out _, out reason);
        }

        public static string GetTypeName(ITypeSymbol typeSymbol)
        {
            if (typeSymbol == null)
                return "object";

            switch (typeSymbol.SpecialType)
            {
                case SpecialType.System_Boolean:
                    return "bool";
                case SpecialType.System_SByte:
                    return "sbyte";
                case SpecialType.System_Byte:
                    return "byte";
                case SpecialType.System_Int16:
                    return "short";
                case SpecialType.System_UInt16:
                    return "ushort";
                case SpecialType.System_Int32:
                    return "int";
                case SpecialType.System_UInt32:
                    return "uint";
                case SpecialType.System_Int64:
                    return "long";
                case SpecialType.System_UInt64:
                    return "ulong";
                case SpecialType.System_Single:
                    return "float";
                case SpecialType.System_Double:
                    return "double";
                case SpecialType.System_String:
                    return "string";
            }

            if (typeSymbol is IArrayTypeSymbol arrayType)
                return GetTypeName(arrayType.ElementType) + "[]";

            return typeSymbol.ToDisplayString(FullyQualifiedFormat);
        }

        private static bool TryCreateSpec(
            ITypeSymbol typeSymbol,
            HashSet<ITypeSymbol> stack,
            out SyncOnJoinTypeSpec spec,
            out string reason)
        {
            spec = null;
            reason = null;

            if (typeSymbol == null)
            {
                reason = "the field type could not be resolved";
                return false;
            }

            bool isAbstract = typeSymbol is INamedTypeSymbol namedType && namedType.IsAbstract;
            bool containsTypeParameters = typeSymbol is INamedTypeSymbol genericType &&
                genericType.TypeArguments.Any(argument => argument.TypeKind == TypeKind.TypeParameter);
            if (typeSymbol.TypeKind == TypeKind.TypeParameter ||
                typeSymbol.TypeKind == TypeKind.Interface ||
                isAbstract ||
                containsTypeParameters)
            {
                reason = $"'{GetTypeName(typeSymbol)}' is polymorphic or generic";
                return false;
            }

            if (IsUnityObject(typeSymbol))
            {
                reason = $"'{GetTypeName(typeSymbol)}' is a Unity object reference";
                return false;
            }

            if (IsDelegate(typeSymbol))
            {
                reason = $"'{GetTypeName(typeSymbol)}' is a delegate";
                return false;
            }

            if (TryCreatePrimitiveSpec(typeSymbol, out spec))
                return true;

            if (typeSymbol.TypeKind == TypeKind.Enum)
                return TryCreateEnumSpec(typeSymbol, out spec, out reason);

            if (TryCreateUnityStructSpec(typeSymbol, out spec))
                return true;

            if (IsDataDictionary(typeSymbol))
            {
                spec = new SyncOnJoinTypeSpec
                {
                    Kind = SyncOnJoinValueKind.DataDictionary,
                    TypeSymbol = typeSymbol,
                    TypeName = GetTypeName(typeSymbol),
                    IsReferenceType = true,
                };
                return true;
            }

            if (IsDataList(typeSymbol))
            {
                spec = new SyncOnJoinTypeSpec
                {
                    Kind = SyncOnJoinValueKind.DataList,
                    TypeSymbol = typeSymbol,
                    TypeName = GetTypeName(typeSymbol),
                    IsReferenceType = true,
                };
                return true;
            }

            if (typeSymbol is IArrayTypeSymbol arrayType)
            {
                if (arrayType.Rank != 1 || arrayType.ElementType.TypeKind == TypeKind.Array)
                {
                    reason = $"'{GetTypeName(typeSymbol)}' is a multidimensional or jagged array";
                    return false;
                }

                if (IsListType(arrayType.ElementType, out _))
                {
                    reason = $"'{GetTypeName(typeSymbol)}' nests collection types";
                    return false;
                }

                if (!TryCreateSpec(arrayType.ElementType, stack, out SyncOnJoinTypeSpec elementSpec, out reason))
                    return false;

                spec = new SyncOnJoinTypeSpec
                {
                    Kind = SyncOnJoinValueKind.Array,
                    TypeSymbol = typeSymbol,
                    TypeName = GetTypeName(typeSymbol),
                    ElementType = elementSpec,
                    IsReferenceType = true,
                };
                return true;
            }

            if (IsListType(typeSymbol, out ITypeSymbol listElementType))
            {
                if (listElementType.TypeKind == TypeKind.Array || IsListType(listElementType, out _))
                {
                    reason = $"'{GetTypeName(typeSymbol)}' nests collection types";
                    return false;
                }

                // Deliberately no IComparable gate on the element type. UdonSharp's replacement List
                // (Runtime/Libraries/Collections/List.cs) declares 'where T : IComparable' only under
                // COMPILER_UDONSHARP, and that constraint is never checked against emitted glue: the emitter
                // writes 'new System.Collections.Generic.List<T>()', which Roslyn binds to the unconstrained
                // BCL List<T>, and the swap to UdonSharp's List happens after Roslyn through
                // UdonSharpUtils.GetForwardedType against the runtime assembly, which Unity compiles without
                // COMPILER_UDONSHARP and therefore without the constraint. Only List<T>.Sort() actually needs
                // IComparable and the glue never emits a Sort. Gating here rejected List<Vector3>,
                // List<Color>, List<Quaternion> and every plain [Serializable] model, and because a member
                // failure fails its whole containing type, a single such field made an entire data model
                // manual-only and cascaded to every behaviour field referencing it.
                if (!TryCreateSpec(listElementType, stack, out SyncOnJoinTypeSpec elementSpec, out reason))
                    return false;

                spec = new SyncOnJoinTypeSpec
                {
                    Kind = SyncOnJoinValueKind.List,
                    TypeSymbol = typeSymbol,
                    TypeName = GetTypeName(typeSymbol),
                    ElementType = elementSpec,
                    IsReferenceType = true,
                };
                return true;
            }

            return TryCreateCustomObjectSpec(typeSymbol, stack, out spec, out reason);
        }

        private static bool TryCreatePrimitiveSpec(ITypeSymbol typeSymbol, out SyncOnJoinTypeSpec spec)
        {
            spec = null;
            string readExpression = null;
            SyncOnJoinPrimitiveEncoding encoding = SyncOnJoinPrimitiveEncoding.None;

            switch (typeSymbol.SpecialType)
            {
                case SpecialType.System_Boolean:
                    readExpression = "{0}.Boolean";
                    encoding = SyncOnJoinPrimitiveEncoding.Boolean;
                    break;
                case SpecialType.System_SByte:
                    readExpression = "(sbyte){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Number;
                    break;
                case SpecialType.System_Byte:
                    readExpression = "(byte){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Number;
                    break;
                case SpecialType.System_Int16:
                    readExpression = "(short){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Number;
                    break;
                case SpecialType.System_UInt16:
                    readExpression = "(ushort){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Number;
                    break;
                case SpecialType.System_Int32:
                    readExpression = "(int){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Number;
                    break;
                case SpecialType.System_UInt32:
                    readExpression = "(uint){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Number;
                    break;
                case SpecialType.System_Int64:
                    readExpression = "(long){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.BigInteger;
                    break;
                case SpecialType.System_UInt64:
                    readExpression = "(ulong){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.BigInteger;
                    break;
                case SpecialType.System_Single:
                    readExpression = "(float){0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Floating;
                    break;
                case SpecialType.System_Double:
                    readExpression = "{0}.Number";
                    encoding = SyncOnJoinPrimitiveEncoding.Floating;
                    break;
                case SpecialType.System_String:
                    spec = new SyncOnJoinTypeSpec
                    {
                        Kind = SyncOnJoinValueKind.String,
                        TypeSymbol = typeSymbol,
                        TypeName = "string",
                        IsReferenceType = true,
                    };
                    return true;
            }

            if (readExpression == null)
                return false;

            // Null for bool, float and double, which is right: none of them is narrowed from the JSON
            // number by a conversion that can throw.
            TryGetIntegerRange(typeSymbol.SpecialType, out string minLiteral, out string maxLiteral);

            spec = new SyncOnJoinTypeSpec
            {
                Kind = SyncOnJoinValueKind.Primitive,
                TypeSymbol = typeSymbol,
                TypeName = GetTypeName(typeSymbol),
                PrimitiveReadExpression = readExpression,
                PrimitiveEncoding = encoding,
                NumberMinLiteral = minLiteral,
                NumberMaxLiteral = maxLiteral,
            };
            return true;
        }

        /// <summary>
        /// The values a JSON number may hold and still convert to an integer type without
        /// <c>System.Convert</c> throwing, written as double literals for the generated range check.
        /// </summary>
        /// <remarks>
        /// long and ulong stop at 2^53 rather than at their own limits. They normally ride as strings,
        /// so a number only reaches them from a payload written before that encoding or by hand, and
        /// past 2^53 a double no longer holds every integer -- the value would already be rounded, so
        /// accepting it would apply a number the sender never had.
        /// </remarks>
        private static bool TryGetIntegerRange(SpecialType type, out string minLiteral, out string maxLiteral)
        {
            switch (type)
            {
                case SpecialType.System_SByte:
                    minLiteral = "-128d";
                    maxLiteral = "127d";
                    return true;
                case SpecialType.System_Byte:
                    minLiteral = "0d";
                    maxLiteral = "255d";
                    return true;
                case SpecialType.System_Int16:
                    minLiteral = "-32768d";
                    maxLiteral = "32767d";
                    return true;
                case SpecialType.System_UInt16:
                    minLiteral = "0d";
                    maxLiteral = "65535d";
                    return true;
                case SpecialType.System_Int32:
                    minLiteral = "-2147483648d";
                    maxLiteral = "2147483647d";
                    return true;
                case SpecialType.System_UInt32:
                    minLiteral = "0d";
                    maxLiteral = "4294967295d";
                    return true;
                case SpecialType.System_Int64:
                    minLiteral = "-9007199254740992d";
                    maxLiteral = "9007199254740992d";
                    return true;
                case SpecialType.System_UInt64:
                    minLiteral = "0d";
                    maxLiteral = "9007199254740992d";
                    return true;
                default:
                    minLiteral = null;
                    maxLiteral = null;
                    return false;
            }
        }

        /// <summary>
        /// Entries in the lookup table UdonSharp refuses to build past. Mirrors the limit in
        /// <c>EmitContext.GetEnumArrayForType</c>.
        /// </summary>
        private const int ExternEnumTableLimit = 2048;

        private static bool TryCreateEnumSpec(ITypeSymbol typeSymbol, out SyncOnJoinTypeSpec spec, out string reason)
        {
            spec = null;
            reason = null;

            SpecialType underlying = (typeSymbol as INamedTypeSymbol)?.EnumUnderlyingType?.SpecialType
                                     ?? SpecialType.System_Int32;

            string minLiteral;
            string maxLiteral;

            // An enum with no declaration in the compiled source is one UdonSharp does not compile itself
            // (UnityEngine, VRChat, System), and it converts a number back into one of those very
            // differently from an enum it does compile.
            if (typeSymbol.DeclaringSyntaxReferences.Length == 0)
            {
                if (!TryGetExternEnumRange(typeSymbol, underlying, out minLiteral, out maxLiteral, out reason))
                    return false;
            }
            else if (!TryGetIntegerRange(underlying, out minLiteral, out maxLiteral))
            {
                reason = $"'{GetTypeName(typeSymbol)}' is an enum with no integer underlying type";
                return false;
            }

            spec = new SyncOnJoinTypeSpec
            {
                Kind = SyncOnJoinValueKind.Enum,
                TypeSymbol = typeSymbol,
                TypeName = GetTypeName(typeSymbol),
                NumberMinLiteral = minLiteral,
                NumberMaxLiteral = maxLiteral,
                EnumWideUnderlyingTypeName = underlying == SpecialType.System_Int64 ? "long"
                    : underlying == SpecialType.System_UInt64 ? "ulong"
                    : null,
            };
            return true;
        }

        /// <summary>
        /// The numbers that can be converted back into an enum UdonSharp does not compile itself.
        /// </summary>
        /// <remarks>
        /// UdonSharp has no extern for that conversion, so <c>EmitContext.GetEnumArrayForType</c> bakes a
        /// constant array of the enum's values and the cast becomes an index into it. That only works
        /// for an int-backed enum (it unboxes every value as int, and anything else is an
        /// InvalidCastException out of the compiler), only up to <see cref="ExternEnumTableLimit"/>
        /// (past it the compiler throws NotSupportedException), and only for non-negative values (the
        /// table starts at zero). Each of those surfaced as a raw compiler exception or as a behaviour
        /// that halted the first time the field held the value, naming neither [SyncOnJoin] nor the
        /// field. Rejecting the type here turns all three into CE0032 on the field.
        ///
        /// The table is not sized to the largest declared value: it is rounded up to cover combined
        /// flags, so the bound is recomputed here the same way rather than assumed. An index past it
        /// is an out-of-range array read, which halts the behaviour just as surely as a throwing
        /// conversion.
        /// </remarks>
        private static bool TryGetExternEnumRange(
            ITypeSymbol enumType,
            SpecialType underlying,
            out string minLiteral,
            out string maxLiteral,
            out string reason)
        {
            minLiteral = null;
            maxLiteral = null;
            reason = null;

            string typeName = GetTypeName(enumType);

            if (underlying != SpecialType.System_Int32)
            {
                reason = $"'{typeName}' is an enum UdonSharp does not compile itself and it is not backed by int, " +
                         "so a number cannot be converted back into it";
                return false;
            }

            int maxValue = 0;

            foreach (IFieldSymbol member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (!member.HasConstantValue || !(member.ConstantValue is int value))
                    continue;

                if (value < 0)
                {
                    reason = $"'{typeName}' declares the negative value '{member.Name}', which the lookup table " +
                             "UdonSharp converts numbers to such enums through cannot hold";
                    return false;
                }

                if (value > maxValue)
                    maxValue = value;
            }

            if (maxValue >= ExternEnumTableLimit)
            {
                reason = $"'{typeName}' has values up to {maxValue.ToString(CultureInfo.InvariantCulture)}, past the " +
                         $"{ExternEnumTableLimit.ToString(CultureInfo.InvariantCulture)}-entry lookup table UdonSharp " +
                         "converts numbers to such enums through";
                return false;
            }

            int mostSignificantBit = 0;

            for (int remaining = maxValue + 1; remaining > 0; remaining >>= 1)
                mostSignificantBit++;

            int tableLength = (1 << mostSignificantBit) - 1;

            minLiteral = "0d";
            maxLiteral = (tableLength - 1).ToString(CultureInfo.InvariantCulture) + "d";
            return true;
        }

        private static bool TryCreateUnityStructSpec(ITypeSymbol typeSymbol, out SyncOnJoinTypeSpec spec)
        {
            spec = null;
            string typeName = typeSymbol.ToDisplayString(FullyQualifiedFormat).Replace("global::", string.Empty);

            string[] components = null;
            string[] componentTypes = null;
            switch (typeName)
            {
                case "UnityEngine.Vector2":
                    components = new[] { "x", "y" };
                    componentTypes = new[] { "float", "float" };
                    break;
                case "UnityEngine.Vector3":
                    components = new[] { "x", "y", "z" };
                    componentTypes = new[] { "float", "float", "float" };
                    break;
                case "UnityEngine.Vector4":
                case "UnityEngine.Quaternion":
                    components = new[] { "x", "y", "z", "w" };
                    componentTypes = new[] { "float", "float", "float", "float" };
                    break;
                case "UnityEngine.Color":
                    components = new[] { "r", "g", "b", "a" };
                    componentTypes = new[] { "float", "float", "float", "float" };
                    break;
                case "UnityEngine.Vector2Int":
                    components = new[] { "x", "y" };
                    componentTypes = new[] { "int", "int" };
                    break;
                case "UnityEngine.Vector3Int":
                    components = new[] { "x", "y", "z" };
                    componentTypes = new[] { "int", "int", "int" };
                    break;
                case "UnityEngine.Color32":
                    components = new[] { "r", "g", "b", "a" };
                    componentTypes = new[] { "byte", "byte", "byte", "byte" };
                    break;
                case "UnityEngine.Rect":
                    components = new[] { "x", "y", "width", "height" };
                    componentTypes = new[] { "float", "float", "float", "float" };
                    break;
                case "UnityEngine.Bounds":
                    components = new[] { "center", "size" };
                    componentTypes = new[] { "UnityEngine.Vector3", "UnityEngine.Vector3" };
                    break;
                default:
                    return false;
            }

            var componentSpecs = new SyncOnJoinTypeSpec[components.Length];
            for (int i = 0; i < components.Length; i++)
            {
                componentSpecs[i] = CreateKnownComponentSpec(componentTypes[i]);
            }

            spec = new SyncOnJoinTypeSpec
            {
                Kind = SyncOnJoinValueKind.UnityStruct,
                TypeSymbol = typeSymbol,
                TypeName = GetTypeName(typeSymbol),
                ComponentNames = components,
                ComponentTypes = componentSpecs,
            };
            return true;
        }

        private static SyncOnJoinTypeSpec CreateKnownComponentSpec(string typeName)
        {
            switch (typeName)
            {
                case "int":
                    TryGetIntegerRange(SpecialType.System_Int32, out string intMin, out string intMax);
                    return new SyncOnJoinTypeSpec
                    {
                        Kind = SyncOnJoinValueKind.Primitive,
                        TypeName = "int",
                        PrimitiveReadExpression = "(int){0}.Number",
                        PrimitiveEncoding = SyncOnJoinPrimitiveEncoding.Number,
                        NumberMinLiteral = intMin,
                        NumberMaxLiteral = intMax
                    };
                case "byte":
                    TryGetIntegerRange(SpecialType.System_Byte, out string byteMin, out string byteMax);
                    return new SyncOnJoinTypeSpec
                    {
                        Kind = SyncOnJoinValueKind.Primitive,
                        TypeName = "byte",
                        PrimitiveReadExpression = "(byte){0}.Number",
                        PrimitiveEncoding = SyncOnJoinPrimitiveEncoding.Number,
                        NumberMinLiteral = byteMin,
                        NumberMaxLiteral = byteMax
                    };
                case "UnityEngine.Vector3":
                    return new SyncOnJoinTypeSpec
                    {
                        Kind = SyncOnJoinValueKind.UnityStruct,
                        TypeName = "global::UnityEngine.Vector3",
                        ComponentNames = new[] { "x", "y", "z" },
                        ComponentTypes = new[]
                        {
                            CreateKnownComponentSpec("float"),
                            CreateKnownComponentSpec("float"),
                            CreateKnownComponentSpec("float"),
                        }
                    };
                default:
                    return new SyncOnJoinTypeSpec
                    {
                        Kind = SyncOnJoinValueKind.Primitive,
                        TypeName = "float",
                        PrimitiveReadExpression = "(float){0}.Number",
                        PrimitiveEncoding = SyncOnJoinPrimitiveEncoding.Floating
                    };
            }
        }

        private static bool TryCreateCustomObjectSpec(
            ITypeSymbol typeSymbol,
            HashSet<ITypeSymbol> stack,
            out SyncOnJoinTypeSpec spec,
            out string reason)
        {
            spec = null;
            reason = null;

            if (typeSymbol.TypeKind != TypeKind.Class && typeSymbol.TypeKind != TypeKind.Struct)
            {
                reason = $"'{GetTypeName(typeSymbol)}' is not a serializable data type";
                return false;
            }

            if (typeSymbol.DeclaringSyntaxReferences.Length == 0)
            {
                reason = $"'{GetTypeName(typeSymbol)}' is defined outside the compiled source";
                return false;
            }

            if (!HasAttribute(typeSymbol, "Serializable") &&
                !HasAttribute(typeSymbol, "DataModel") &&
                !HasAttribute(typeSymbol, "PlayerData"))
            {
                reason = $"'{GetTypeName(typeSymbol)}' is not marked [Serializable], [DataModel], or [PlayerData]";
                return false;
            }

            if (typeSymbol.TypeKind == TypeKind.Class && !HasUsableParameterlessConstructor(typeSymbol))
            {
                reason = $"'{GetTypeName(typeSymbol)}' has no accessible parameterless constructor";
                return false;
            }

            if (!stack.Add(typeSymbol))
            {
                reason = $"'{GetTypeName(typeSymbol)}' is recursive";
                return false;
            }

            try
            {
                var members = new List<SyncOnJoinMemberSpec>();
                foreach (IFieldSymbol field in GetSerializableFields(typeSymbol))
                {
                    if (HasAttribute(field, "SerializeReference"))
                    {
                        reason = $"field '{field.Name}' uses [SerializeReference]";
                        return false;
                    }

                    // The helpers are emitted onto this type, and a derived type cannot read a base's
                    // private field. Emitting the read anyway is CS0122 in generated code, and because
                    // helpers go onto every qualifying [Serializable] type whether or not anything syncs
                    // it, that one pair of classes stopped the whole project compiling. Dropping the
                    // field instead would publish a snapshot that silently lacks part of the model.
                    if (field.DeclaredAccessibility == Accessibility.Private &&
                        !SymbolEqualityComparer.Default.Equals(field.ContainingType, typeSymbol))
                    {
                        reason = $"field '{field.ContainingType?.Name}.{field.Name}' is private to a base class, so " +
                                 $"generated code on '{GetTypeName(typeSymbol)}' cannot read it";
                        return false;
                    }

                    if (!TryCreateSpec(field.Type, stack, out SyncOnJoinTypeSpec fieldSpec, out string fieldReason))
                    {
                        reason = $"field '{field.Name}' cannot be generated: {fieldReason}";
                        return false;
                    }

                    members.Add(new SyncOnJoinMemberSpec
                    {
                        FieldName = field.Name,
                        Key = GetFieldKey(field),
                        Type = fieldSpec,
                    });
                }

                spec = new SyncOnJoinTypeSpec
                {
                    Kind = SyncOnJoinValueKind.CustomObject,
                    TypeSymbol = typeSymbol,
                    TypeName = GetTypeName(typeSymbol),
                    Members = members,
                    IsReferenceType = typeSymbol.TypeKind == TypeKind.Class,
                };
                return true;
            }
            finally
            {
                stack.Remove(typeSymbol);
            }
        }

        private static IEnumerable<IFieldSymbol> GetSerializableFields(ITypeSymbol typeSymbol)
        {
            var stack = new Stack<INamedTypeSymbol>();
            for (INamedTypeSymbol current = typeSymbol as INamedTypeSymbol;
                 current != null && current.SpecialType != SpecialType.System_Object;
                 current = current.BaseType)
            {
                stack.Push(current);
            }

            while (stack.Count > 0)
            {
                INamedTypeSymbol current = stack.Pop();
                foreach (IFieldSymbol field in current.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsImplicitlyDeclared ||
                        field.IsStatic ||
                        field.IsConst ||
                        field.IsReadOnly ||
                        HasAttribute(field, "NonSerialized") ||
                        HasAttribute(field, "DataIgnore") ||
                        HasAttribute(field, "PersistIgnore"))
                    {
                        continue;
                    }

                    bool isSerialized =
                        field.DeclaredAccessibility == Accessibility.Public ||
                        HasAttribute(field, "SerializeField");

                    if (isSerialized)
                        yield return field;
                }
            }
        }

        private static string GetFieldKey(IFieldSymbol field)
        {
            return GetAttributeStringArgument(field, "DataField") ??
                   GetAttributeStringArgument(field, "PersistKey") ??
                   field.Name;
        }

        private static bool HasUsableParameterlessConstructor(ITypeSymbol typeSymbol)
        {
            if (!(typeSymbol is INamedTypeSymbol namedType))
                return false;

            foreach (IMethodSymbol constructor in namedType.InstanceConstructors)
            {
                if (constructor.Parameters.Length != 0)
                    continue;

                if (constructor.DeclaredAccessibility == Accessibility.Public ||
                    constructor.DeclaredAccessibility == Accessibility.Internal ||
                    constructor.DeclaredAccessibility == Accessibility.ProtectedOrInternal ||
                    constructor.DeclaredAccessibility == Accessibility.NotApplicable)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnityObject(ITypeSymbol typeSymbol)
        {
            for (INamedTypeSymbol current = typeSymbol as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                if (current.ToDisplayString() == "UnityEngine.Object")
                    return true;
            }

            return false;
        }

        private static bool IsDelegate(ITypeSymbol typeSymbol)
        {
            for (INamedTypeSymbol current = typeSymbol as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                string name = current.ToDisplayString();
                if (name == "System.Delegate" || name == "System.MulticastDelegate")
                    return true;
            }

            return false;
        }

        private static bool IsDataDictionary(ITypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString() == "VRC.SDK3.Data.DataDictionary";
        }

        private static bool IsDataList(ITypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString() == "VRC.SDK3.Data.DataList";
        }
    }
}
