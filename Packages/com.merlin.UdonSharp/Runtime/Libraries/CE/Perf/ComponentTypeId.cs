using JetBrains.Annotations;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Component type identifiers for the ECS-Lite system.
    /// These are used internally to identify component array types.
    /// </summary>
    /// <remarks>
    /// Since Udon doesn't support true generics at runtime in the same way .NET does,
    /// we use integer type IDs to identify components. The CEWorld maps these IDs
    /// to the corresponding arrays.
    /// </remarks>
    [PublicAPI]
    public static class ComponentTypeId
    {
        // Primitive types
        public const int Bool = 1;
        public const int Byte = 2;
        public const int SByte = 3;
        public const int Short = 4;
        public const int UShort = 5;
        public const int Int = 6;
        public const int UInt = 7;
        public const int Long = 8;
        public const int ULong = 9;
        public const int Float = 10;
        public const int Double = 11;

        // Unity types
        public const int Vector2 = 20;
        public const int Vector3 = 21;
        public const int Vector4 = 22;
        public const int Vector2Int = 23;
        public const int Vector3Int = 24;
        public const int Quaternion = 25;
        public const int Color = 26;
        public const int Color32 = 27;
        public const int Matrix4x4 = 28;

        // Reference types
        public const int String = 40;
        public const int GameObject = 41;
        public const int Transform = 42;
        public const int Object = 43;

        // Custom component base (user-defined components start here)
        public const int CustomBase = 100;

        /// <summary>
        /// Upper bound of the type id space, i.e. of the numbers in this class.
        /// </summary>
        /// <remarks>
        /// This is not how many components a world can hold. A CEWorld tracks components in one 32-bit
        /// mask per entity, so it accepts 32 RegisterComponent calls and returns -1 for the 33rd. A type
        /// id is only a label passed to RegisterComponent; the slot it returns (0-31) is what
        /// AddComponent, HasComponent and CEQuery take, and passing a type id there instead is silently
        /// ignored once it is 32 or more.
        /// </remarks>
        public const int MaxTypes = 256;
    }
}
