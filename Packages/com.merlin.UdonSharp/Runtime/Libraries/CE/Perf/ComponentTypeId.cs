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
        /// Maximum supported component types.
        /// </summary>
        public const int MaxTypes = 256;
    }
}
