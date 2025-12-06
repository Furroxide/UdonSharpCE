using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.GraphBridge
{
    /// <summary>
    /// Marks a property to be exposed in the Udon Graph.
    ///
    /// Properties with this attribute appear as "Get" and/or "Set" nodes
    /// in the graph editor.
    /// </summary>
    /// <remarks>
    /// The property must be:
    /// - Public
    /// - Using an Udon-compatible type
    ///
    /// Read-only properties create only a "Get" node.
    /// Write-only properties create only a "Set" node.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class PlayerController : UdonSharpBehaviour
    /// {
    ///     [GraphProperty("Player/Health")]
    ///     public float Health { get; set; }
    ///
    ///     [GraphProperty("Player/Is Grounded", ReadOnly = true)]
    ///     public bool IsGrounded => _characterController.isGrounded;
    ///
    ///     [GraphProperty("Player/Speed")]
    ///     public float MoveSpeed { get; set; } = 5f;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class GraphPropertyAttribute : Attribute
    {
        /// <summary>
        /// Path in the node creation menu for Get/Set nodes.
        /// </summary>
        public string MenuPath { get; }

        /// <summary>
        /// Whether the property is read-only in the graph.
        /// If true, only a "Get" node is created.
        /// </summary>
        public bool ReadOnly { get; set; } = false;

        /// <summary>
        /// Unity icon name for the property nodes.
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// Tooltip for the property nodes.
        /// </summary>
        public string Tooltip { get; set; }

        public GraphPropertyAttribute()
        {
            MenuPath = null;
        }

        public GraphPropertyAttribute(string menuPath)
        {
            MenuPath = menuPath;
        }
    }

    /// <summary>
    /// Marks a method as an event that can be subscribed to in the graph.
    ///
    /// Events appear as entry points in the graph editor, similar to
    /// built-in events like Start, Update, etc.
    /// </summary>
    /// <remarks>
    /// The method should be:
    /// - Public
    /// - Void return type
    /// - Parameters become event data outputs
    /// </remarks>
    /// <example>
    /// <code>
    /// public class GameEvents : UdonSharpBehaviour
    /// {
    ///     [GraphEvent("Game/On Score Changed")]
    ///     public void OnScoreChanged(int newScore, int delta)
    ///     {
    ///         // Event handler implementation
    ///     }
    ///
    ///     [GraphEvent("Game/On Player Respawn")]
    ///     public void OnPlayerRespawn(VRCPlayerApi player, Vector3 position)
    ///     {
    ///         // Event handler implementation
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class GraphEventAttribute : Attribute
    {
        /// <summary>
        /// Path in the event menu.
        /// </summary>
        public string MenuPath { get; }

        /// <summary>
        /// Unity icon name for the event node.
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// Tooltip description.
        /// </summary>
        public string Tooltip { get; set; }

        /// <summary>
        /// Whether the event is networked.
        /// </summary>
        public bool Networked { get; set; } = false;

        public GraphEventAttribute(string menuPath)
        {
            MenuPath = menuPath;
        }
    }

    /// <summary>
    /// Groups multiple [GraphNode] methods into a collapsible category.
    /// Apply to a class containing [GraphNode] methods.
    /// </summary>
    /// <example>
    /// <code>
    /// [GraphCategory("CE/Math Utilities", Icon = "d_Profiler.CPU")]
    /// public static class CEMathNodes
    /// {
    ///     [GraphNode("Remap")]
    ///     public static float Remap(float value, float inMin, float inMax, float outMin, float outMax)
    ///     {
    ///         return Mathf.Lerp(outMin, outMax, Mathf.InverseLerp(inMin, inMax, value));
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class GraphCategoryAttribute : Attribute
    {
        /// <summary>
        /// Category path for all nodes in this class.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Icon for the category.
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// Priority for sorting (lower = higher in menu).
        /// </summary>
        public int Priority { get; set; } = 100;

        public GraphCategoryAttribute(string path)
        {
            Path = path;
        }
    }

    /// <summary>
    /// Specifies type constraints for a generic [GraphNode] parameter.
    /// </summary>
    /// <example>
    /// <code>
    /// [GraphNode("Array/Get Element")]
    /// public static T GetElement&lt;T&gt;(
    ///     [GraphInput][GraphTypeConstraint(typeof(int), typeof(float), typeof(string))] T[] array,
    ///     [GraphInput] int index)
    /// {
    ///     return array[index];
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class GraphTypeConstraintAttribute : Attribute
    {
        /// <summary>
        /// Allowed types for this parameter.
        /// </summary>
        public Type[] AllowedTypes { get; }

        public GraphTypeConstraintAttribute(params Type[] allowedTypes)
        {
            AllowedTypes = allowedTypes;
        }
    }
}
