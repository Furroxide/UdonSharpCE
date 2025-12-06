using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.GraphBridge
{
    /// <summary>
    /// Marks a method to be exposed as an Udon Graph node.
    ///
    /// Methods with this attribute are compiled to Udon and can be
    /// called from the Udon Graph editor as custom nodes.
    /// </summary>
    /// <remarks>
    /// The method must be:
    /// - Public
    /// - Static or instance (instance methods become behaviour-bound nodes)
    /// - Using only Udon-compatible parameter and return types
    ///
    /// Parameters become input ports, return value becomes output port.
    /// Use [GraphInput] and [GraphOutput] for additional port customization.
    /// </remarks>
    /// <example>
    /// <code>
    /// public static class MathNodes
    /// {
    ///     [GraphNode("Math/Lerp Vector3")]
    ///     public static Vector3 LerpVector3(
    ///         [GraphInput("Start")] Vector3 a,
    ///         [GraphInput("End")] Vector3 b,
    ///         [GraphInput("T")] float t)
    ///     {
    ///         return Vector3.Lerp(a, b, t);
    ///     }
    ///
    ///     [GraphNode("Math/Clamp Float", Icon = "d_Animation Icon")]
    ///     public static float ClampFloat(
    ///         [GraphInput("Value")] float value,
    ///         [GraphInput("Min", DefaultValue = 0f)] float min,
    ///         [GraphInput("Max", DefaultValue = 1f)] float max)
    ///     {
    ///         return Mathf.Clamp(value, min, max);
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class GraphNodeAttribute : Attribute
    {
        /// <summary>
        /// Path in the node creation menu.
        /// Use "/" to create submenus.
        /// </summary>
        public string MenuPath { get; }

        /// <summary>
        /// Unity icon name for the node.
        /// Use built-in icon names like "d_Animation Icon".
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// Whether this is a flow node (has execution ports).
        /// Default is true. Set to false for pure value nodes.
        /// </summary>
        public bool IsFlowNode { get; set; } = true;

        /// <summary>
        /// Node color as hex string (e.g., "#FF5500").
        /// </summary>
        public string Color { get; set; }

        /// <summary>
        /// Tooltip description shown in the graph editor.
        /// </summary>
        public string Tooltip { get; set; }

        /// <summary>
        /// Category for node organization.
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Whether the node can be searched by name.
        /// Default is true.
        /// </summary>
        public bool Searchable { get; set; } = true;

        /// <summary>
        /// Keywords for search (comma-separated).
        /// </summary>
        public string SearchKeywords { get; set; }

        public GraphNodeAttribute(string menuPath)
        {
            MenuPath = menuPath;
        }
    }

    /// <summary>
    /// Customizes an input port on a [GraphNode] method.
    /// </summary>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class GraphInputAttribute : Attribute
    {
        /// <summary>
        /// Display name for the input port.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Default value when no connection is made.
        /// </summary>
        public object DefaultValue { get; set; }

        /// <summary>
        /// Whether this input is hidden from the graph UI.
        /// Hidden inputs use their default value.
        /// </summary>
        public bool Hidden { get; set; } = false;

        /// <summary>
        /// Tooltip for the input port.
        /// </summary>
        public string Tooltip { get; set; }

        /// <summary>
        /// Minimum value for numeric inputs (for UI slider).
        /// </summary>
        public float Min { get; set; } = float.MinValue;

        /// <summary>
        /// Maximum value for numeric inputs (for UI slider).
        /// </summary>
        public float Max { get; set; } = float.MaxValue;

        public GraphInputAttribute()
        {
            DisplayName = null;
        }

        public GraphInputAttribute(string displayName)
        {
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Defines additional output ports on a [GraphNode] method.
    ///
    /// By default, the return value creates a single output.
    /// Use this attribute for multiple outputs via out parameters.
    /// </summary>
    /// <example>
    /// <code>
    /// [GraphNode("Math/SinCos")]
    /// public static void SinCos(
    ///     [GraphInput] float angle,
    ///     [GraphOutput("Sin")] out float sin,
    ///     [GraphOutput("Cos")] out float cos)
    /// {
    ///     sin = Mathf.Sin(angle);
    ///     cos = Mathf.Cos(angle);
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class GraphOutputAttribute : Attribute
    {
        /// <summary>
        /// Display name for the output port.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Tooltip for the output port.
        /// </summary>
        public string Tooltip { get; set; }

        public GraphOutputAttribute()
        {
            DisplayName = null;
        }

        public GraphOutputAttribute(string displayName)
        {
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Marks a method as a flow control node with multiple execution outputs.
    /// </summary>
    /// <example>
    /// <code>
    /// [GraphNode("Flow/Branch On Value")]
    /// [GraphFlowOutput("OnZero")]
    /// [GraphFlowOutput("OnPositive")]
    /// [GraphFlowOutput("OnNegative")]
    /// public static int BranchOnValue([GraphInput] int value)
    /// {
    ///     if (value == 0) return 0;
    ///     return value > 0 ? 1 : 2;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class GraphFlowOutputAttribute : Attribute
    {
        /// <summary>
        /// Name of the flow output port.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Tooltip for the flow output.
        /// </summary>
        public string Tooltip { get; set; }

        public GraphFlowOutputAttribute(string name)
        {
            Name = name;
        }
    }
}
