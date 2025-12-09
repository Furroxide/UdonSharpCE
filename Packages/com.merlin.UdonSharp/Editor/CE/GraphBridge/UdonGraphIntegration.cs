using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.GraphBridge
{
    /// <summary>
    /// Handles integration between CE graph nodes and the Udon Graph editor.
    ///
    /// Provides type marshalling, node registration, and UI customization
    /// for [GraphNode] decorated methods.
    /// </summary>
    public static class UdonGraphIntegration
    {
        #region Type Marshalling

        /// <summary>
        /// Maps C# types to Udon type names.
        /// </summary>
        private static readonly Dictionary<Type, string> TypeToUdonName = new Dictionary<Type, string>
        {
            { typeof(int), "SystemInt32" },
            { typeof(float), "SystemSingle" },
            { typeof(double), "SystemDouble" },
            { typeof(bool), "SystemBoolean" },
            { typeof(string), "SystemString" },
            { typeof(byte), "SystemByte" },
            { typeof(sbyte), "SystemSByte" },
            { typeof(short), "SystemInt16" },
            { typeof(ushort), "SystemUInt16" },
            { typeof(uint), "SystemUInt32" },
            { typeof(long), "SystemInt64" },
            { typeof(ulong), "SystemUInt64" },
            { typeof(char), "SystemChar" },
            { typeof(object), "SystemObject" },
            { typeof(Vector2), "UnityEngineVector2" },
            { typeof(Vector3), "UnityEngineVector3" },
            { typeof(Vector4), "UnityEngineVector4" },
            { typeof(Quaternion), "UnityEngineQuaternion" },
            { typeof(Color), "UnityEngineColor" },
            { typeof(Color32), "UnityEngineColor32" },
            { typeof(Transform), "UnityEngineTransform" },
            { typeof(GameObject), "UnityEngineGameObject" },
            { typeof(Animator), "UnityEngineAnimator" },
            { typeof(AudioSource), "UnityEngineAudioSource" },
            { typeof(Rigidbody), "UnityEngineRigidbody" },
            { typeof(Collider), "UnityEngineCollider" },
            { typeof(Renderer), "UnityEngineRenderer" },
            { typeof(Material), "UnityEngineMaterial" },
            { typeof(Texture), "UnityEngineTexture" },
            { typeof(Texture2D), "UnityEngineTexture2D" },
            { typeof(Sprite), "UnityEngineSprite" },
            { typeof(Rect), "UnityEngineRect" },
            { typeof(Bounds), "UnityEngineBounds" },
            { typeof(Ray), "UnityEngineRay" },
            { typeof(RaycastHit), "UnityEngineRaycastHit" },
            { typeof(LayerMask), "UnityEngineLayerMask" },
        };

        /// <summary>
        /// Gets the Udon type name for a C# type.
        /// </summary>
        public static string GetUdonTypeName(Type type)
        {
            if (type == null)
                return "SystemObject";

            // Handle arrays
            if (type.IsArray)
            {
                Type elementType = type.GetElementType();
                return GetUdonTypeName(elementType) + "Array";
            }

            // Check direct mapping
            if (TypeToUdonName.TryGetValue(type, out string udonName))
            {
                return udonName;
            }

            // Handle enums
            if (type.IsEnum)
            {
                return "SystemInt32"; // Enums are represented as int in Udon
            }

            // Handle generic types
            if (type.IsGenericType)
            {
                return "SystemObject"; // Generic types fall back to object
            }

            // Handle Unity types by reflection
            if (type.Namespace?.StartsWith("UnityEngine") == true)
            {
                return type.Namespace.Replace(".", "") + type.Name;
            }

            // Handle VRC types
            if (type.Namespace?.StartsWith("VRC") == true)
            {
                return type.FullName?.Replace(".", "") ?? "SystemObject";
            }

            return "SystemObject";
        }

        /// <summary>
        /// Gets the C# type from an Udon type name.
        /// </summary>
        public static Type GetTypeFromUdonName(string udonName)
        {
            foreach (var kvp in TypeToUdonName)
            {
                if (kvp.Value == udonName)
                    return kvp.Key;
            }

            // Handle array types
            if (udonName.EndsWith("Array"))
            {
                string elementTypeName = udonName.Substring(0, udonName.Length - 5);
                Type elementType = GetTypeFromUdonName(elementTypeName);
                return elementType?.MakeArrayType();
            }

            return typeof(object);
        }

        /// <summary>
        /// Checks if a type is supported in Udon.
        /// </summary>
        public static bool IsUdonSupported(Type type)
        {
            if (type == null)
                return false;

            // Primitives and known types
            if (TypeToUdonName.ContainsKey(type))
                return true;

            // Arrays of supported types
            if (type.IsArray)
                return IsUdonSupported(type.GetElementType());

            // Enums
            if (type.IsEnum)
                return true;

            // Unity types
            if (type.Namespace?.StartsWith("UnityEngine") == true)
                return true;

            // VRC types
            if (type.Namespace?.StartsWith("VRC") == true)
                return true;

            return false;
        }

        #endregion

        #region Node Registration

        /// <summary>
        /// Registered custom nodes by menu path.
        /// </summary>
        private static Dictionary<string, NodeDefinition> _registeredNodes;

        /// <summary>
        /// Initializes the integration system.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            _registeredNodes = new Dictionary<string, NodeDefinition>();

            EditorApplication.delayCall += () =>
            {
                RefreshNodes();
            };
        }

        /// <summary>
        /// Refreshes the registered node list.
        /// </summary>
        public static void RefreshNodes()
        {
            _registeredNodes.Clear();

            var nodes = GraphNodeGenerator.GetNodeDefinitions();
            foreach (var node in nodes)
            {
                if (!_registeredNodes.ContainsKey(node.MenuPath))
                {
                    _registeredNodes.Add(node.MenuPath, node);
                }
            }

            if (_registeredNodes.Count != 0)
            {
                Debug.Log($"[UdonGraphIntegration] Registered {_registeredNodes.Count} custom nodes");
            }

        
        }

        /// <summary>
        /// Gets all registered nodes.
        /// </summary>
        public static IEnumerable<NodeDefinition> GetRegisteredNodes()
        {
            if (_registeredNodes == null)
                RefreshNodes();

            return _registeredNodes.Values;
        }

        /// <summary>
        /// Gets a node by menu path.
        /// </summary>
        public static NodeDefinition GetNode(string menuPath)
        {
            if (_registeredNodes == null)
                RefreshNodes();

            _registeredNodes.TryGetValue(menuPath, out NodeDefinition node);
            return node;
        }

        #endregion

        #region Value Conversion

        /// <summary>
        /// Converts a value to the target type for Udon.
        /// </summary>
        public static object ConvertValue(object value, Type targetType)
        {
            if (value == null)
                return GetDefaultValue(targetType);

            if (targetType.IsInstanceOfType(value))
                return value;

            try
            {
                // Handle common conversions
                if (targetType == typeof(float) && value is double d)
                    return (float)d;

                if (targetType == typeof(int) && value is float f)
                    return (int)f;

                if (targetType == typeof(float) && value is int i)
                    return (float)i;

                if (targetType == typeof(string))
                    return value.ToString();

                if (targetType == typeof(bool) && value is int bi)
                    return bi != 0;

                // Use Convert for other cases
                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return GetDefaultValue(targetType);
            }
        }

        /// <summary>
        /// Gets the default value for a type.
        /// </summary>
        public static object GetDefaultValue(Type type)
        {
            if (type == null)
                return null;

            if (type.IsValueType)
                return Activator.CreateInstance(type);

            return null;
        }

        #endregion

        #region UI Helpers

        /// <summary>
        /// Color mappings for node categories.
        /// </summary>
        private static readonly Dictionary<string, Color> CategoryColors = new Dictionary<string, Color>
        {
            { "Math", new Color(0.3f, 0.6f, 0.9f) },
            { "Logic", new Color(0.9f, 0.6f, 0.3f) },
            { "Flow", new Color(0.5f, 0.8f, 0.5f) },
            { "String", new Color(0.9f, 0.4f, 0.6f) },
            { "Array", new Color(0.6f, 0.5f, 0.9f) },
            { "Vector", new Color(0.8f, 0.8f, 0.3f) },
            { "Transform", new Color(0.4f, 0.7f, 0.7f) },
            { "Physics", new Color(0.7f, 0.4f, 0.4f) },
            { "Audio", new Color(0.6f, 0.8f, 0.4f) },
            { "Network", new Color(0.4f, 0.6f, 0.8f) },
            { "Player", new Color(0.8f, 0.5f, 0.8f) },
        };

        /// <summary>
        /// Gets the color for a node based on its category.
        /// </summary>
        public static Color GetNodeColor(NodeDefinition node)
        {
            if (!string.IsNullOrEmpty(node.Color))
            {
                if (ColorUtility.TryParseHtmlString(node.Color, out Color parsed))
                    return parsed;
            }

            // Try to match category from menu path
            string menuPath = node.MenuPath ?? "";
            foreach (var kvp in CategoryColors)
            {
                if (menuPath.Contains(kvp.Key))
                    return kvp.Value;
            }

            return new Color(0.5f, 0.5f, 0.5f); // Default gray
        }

        /// <summary>
        /// Gets the icon for a node.
        /// </summary>
        public static GUIContent GetNodeIcon(NodeDefinition node)
        {
            if (!string.IsNullOrEmpty(node.Icon))
            {
                var icon = EditorGUIUtility.IconContent(node.Icon);
                if (icon != null && icon.image != null)
                    return icon;
            }

            // Default icons based on category
            string menuPath = node.MenuPath ?? "";
            if (menuPath.Contains("Math"))
                return EditorGUIUtility.IconContent("d_Profiler.CPU");
            if (menuPath.Contains("Logic"))
                return EditorGUIUtility.IconContent("d_FilterByType");
            if (menuPath.Contains("Flow"))
                return EditorGUIUtility.IconContent("d_Animation Icon");
            if (menuPath.Contains("String"))
                return EditorGUIUtility.IconContent("d_Font Icon");
            if (menuPath.Contains("Array"))
                return EditorGUIUtility.IconContent("d_PreMatCube");
            if (menuPath.Contains("Vector"))
                return EditorGUIUtility.IconContent("d_Transform Icon");
            if (menuPath.Contains("Physics"))
                return EditorGUIUtility.IconContent("d_Rigidbody Icon");
            if (menuPath.Contains("Audio"))
                return EditorGUIUtility.IconContent("d_AudioSource Icon");

            return EditorGUIUtility.IconContent("d_cs Script Icon");
        }

        /// <summary>
        /// Builds the menu tree for the node browser.
        /// </summary>
        public static Dictionary<string, List<NodeDefinition>> BuildMenuTree()
        {
            var tree = new Dictionary<string, List<NodeDefinition>>();

            foreach (var node in GetRegisteredNodes())
            {
                string path = node.MenuPath;
                int lastSlash = path.LastIndexOf('/');
                string category = lastSlash >= 0 ? path.Substring(0, lastSlash) : "Uncategorized";

                if (!tree.ContainsKey(category))
                    tree[category] = new List<NodeDefinition>();

                tree[category].Add(node);
            }

            return tree;
        }

        #endregion

        #region Validation

        /// <summary>
        /// Validates a node definition.
        /// </summary>
        public static List<string> ValidateNode(NodeDefinition node)
        {
            var errors = new List<string>();

            if (node.Method == null)
            {
                errors.Add("Node has no method");
                return errors;
            }

            // Check return type
            if (node.Method.ReturnType != typeof(void) && !IsUdonSupported(node.Method.ReturnType))
            {
                errors.Add($"Return type {node.Method.ReturnType.Name} is not supported in Udon");
            }

            // Check parameter types
            foreach (var param in node.Method.GetParameters())
            {
                Type paramType = param.ParameterType;
                if (param.IsOut)
                    paramType = paramType.GetElementType();

                if (!IsUdonSupported(paramType))
                {
                    errors.Add($"Parameter {param.Name} has unsupported type {paramType.Name}");
                }
            }

            return errors;
        }

        /// <summary>
        /// Validates all registered nodes.
        /// </summary>
        [MenuItem("Udon CE/Graph Bridge/Validate Nodes", false, 1502)]
        public static void ValidateAllNodes()
        {
            var nodes = GetRegisteredNodes();
            int errorCount = 0;

            foreach (var node in nodes)
            {
                var errors = ValidateNode(node);
                if (errors.Count > 0)
                {
                    Debug.LogWarning($"[GraphBridge] Validation errors for {node.MenuPath}:");
                    foreach (var error in errors)
                    {
                        Debug.LogWarning($"  - {error}");
                        errorCount++;
                    }
                }
            }

            if (errorCount == 0)
            {
                Debug.Log($"[GraphBridge] All {nodes.Count()} nodes validated successfully");
            }
            else
            {
                Debug.LogWarning($"[GraphBridge] Found {errorCount} validation errors");
            }
        }

        #endregion
    }
}
