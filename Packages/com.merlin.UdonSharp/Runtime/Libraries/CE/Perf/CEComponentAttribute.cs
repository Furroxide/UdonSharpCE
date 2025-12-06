using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Marks a struct as an ECS-Lite component for use with CEWorld.
    ///
    /// Components marked with this attribute are conceptually transformed to
    /// Structure-of-Arrays (SoA) storage for optimal cache performance in Udon.
    /// </summary>
    /// <remarks>
    /// At runtime in Udon, structs cannot be used efficiently. The CE.Perf system
    /// stores component data as parallel arrays (one per field) to enable:
    /// - Cache-friendly memory access patterns
    /// - Batched operations without per-entity overhead
    /// - Efficient iteration over large entity counts
    ///
    /// The user writes:
    /// <code>
    /// [CEComponent]
    /// public struct Position { public Vector3 value; }
    /// </code>
    ///
    /// At runtime this becomes parallel arrays:
    /// <code>
    /// Vector3[] position_value;
    /// </code>
    /// </remarks>
    /// <example>
    /// <code>
    /// [CEComponent]
    /// public struct Enemy
    /// {
    ///     public Vector3 position;
    ///     public float health;
    ///     public int state;
    /// }
    ///
    /// // Use with CEWorld:
    /// var world = new CEWorld(1000);
    /// int entity = world.CreateEntity();
    /// world.Set(entity, new Position { value = Vector3.zero });
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Struct)]
    public class CEComponentAttribute : Attribute
    {
        /// <summary>
        /// Optional tag for grouping related components.
        /// Components with the same tag are stored in the same archetype chunk.
        /// </summary>
        public string Tag { get; set; } = "";

        /// <summary>
        /// Maximum instances expected for this component.
        /// Used for pre-allocation hints.
        /// </summary>
        public int MaxInstances { get; set; } = 1000;

        /// <summary>
        /// Creates a new CEComponent attribute with default settings.
        /// </summary>
        public CEComponentAttribute() { }

        /// <summary>
        /// Creates a new CEComponent attribute with a specified tag.
        /// </summary>
        /// <param name="tag">Tag for grouping related components.</param>
        public CEComponentAttribute(string tag)
        {
            Tag = tag;
        }
    }
}
