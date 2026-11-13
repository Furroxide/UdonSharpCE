using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UdonSharp.CE.GraphBridge;

namespace UdonSharp.CE.Editor.GraphBridge
{
    /// <summary>
    /// Represents a discovered graph node definition.
    /// </summary>
    public class NodeDefinition
    {
        /// <summary>
        /// Menu path for the node.
        /// </summary>
        public string MenuPath;

        /// <summary>
        /// The method info for the node.
        /// </summary>
        public MethodInfo Method;

        /// <summary>
        /// Containing type.
        /// </summary>
        public Type ContainingType;

        /// <summary>
        /// Whether this is a flow node.
        /// </summary>
        public bool IsFlowNode;

        /// <summary>
        /// Icon name.
        /// </summary>
        public string Icon;

        /// <summary>
        /// Node color.
        /// </summary>
        public string Color;

        /// <summary>
        /// Tooltip description.
        /// </summary>
        public string Tooltip;

        /// <summary>
        /// Input port definitions.
        /// </summary>
        public List<PortDefinition> Inputs = new List<PortDefinition>();

        /// <summary>
        /// Output port definitions.
        /// </summary>
        public List<PortDefinition> Outputs = new List<PortDefinition>();

        /// <summary>
        /// Flow output names (for branching nodes).
        /// </summary>
        public List<string> FlowOutputs = new List<string>();

        /// <summary>
        /// Category from parent class.
        /// </summary>
        public string Category;
    }

    /// <summary>
    /// Represents an input or output port.
    /// </summary>
    public class PortDefinition
    {
        /// <summary>
        /// Display name.
        /// </summary>
        public string Name;

        /// <summary>
        /// Port type.
        /// </summary>
        public Type Type;

        /// <summary>
        /// Default value for inputs.
        /// </summary>
        public object DefaultValue;

        /// <summary>
        /// Tooltip.
        /// </summary>
        public string Tooltip;

        /// <summary>
        /// Whether hidden from UI.
        /// </summary>
        public bool Hidden;
    }

    /// <summary>
    /// Scans assemblies for [GraphNode] methods and generates Udon Graph node definitions.
    ///
    /// This editor tool finds all methods marked with [GraphNode] and creates
    /// the necessary metadata for the Udon Graph editor to display them as nodes.
    /// </summary>
    public static class GraphNodeGenerator
    {
        /// <summary>
        /// Cache of discovered node definitions.
        /// </summary>
        private static List<NodeDefinition> _nodeCache;

        /// <summary>
        /// Whether the cache is valid.
        /// </summary>
        private static bool _cacheValid;

        /// <summary>
        /// Menu item to regenerate graph nodes.
        /// </summary>
        [MenuItem("Udon CE/Graph Bridge/Generate Nodes", false, 1503)]
        public static void GenerateNodes()
        {
            var nodes = ScanForGraphNodes();

            Debug.Log($"[GraphNodeGenerator] Found {nodes.Count} graph nodes");

            foreach (var node in nodes)
            {
                Debug.Log($"  - {node.MenuPath}: {node.Method.Name}");
            }

            GenerateNodeAssets(nodes);

            _nodeCache = nodes;
            _cacheValid = true;
        }

        /// <summary>
        /// Gets all discovered node definitions (cached).
        /// </summary>
        public static List<NodeDefinition> GetNodeDefinitions()
        {
            if (!_cacheValid)
            {
                _nodeCache = ScanForGraphNodes();
                _cacheValid = true;
            }
            return _nodeCache;
        }

        /// <summary>
        /// Invalidates the node cache.
        /// </summary>
        public static void InvalidateCache()
        {
            _cacheValid = false;
        }

        /// <summary>
        /// Finds every [GraphNode] method and [GraphProperty] property.
        ///
        /// This used to walk every type of nearly every loaded assembly -- the prefix filter that was
        /// meant to skip the framework let System, UnityEngine, UnityEditor, Roslyn and the VRChat SDK
        /// through -- asking each public method, inherited ones included, for the attribute. That was
        /// hundreds of thousands of lookups, it ran after every domain reload, and because inherited
        /// methods were not excluded it returned an instance node once for its own class and once more
        /// for every subclass.
        /// </summary>
        private static List<NodeDefinition> ScanForGraphNodes()
        {
            var nodes = new List<NodeDefinition>();

            // Unity's own index of attributed methods: each method once, on the type that declares it.
            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<GraphNodeAttribute>())
            {
                // Private methods are not exported.
                if (!method.IsPublic || method.DeclaringType == null)
                    continue;

                try
                {
                    var nodeAttr = method.GetCustomAttribute<GraphNodeAttribute>();
                    if (nodeAttr == null)
                        continue;

                    var node = CreateNodeDefinition(method, nodeAttr, GetCategoryPath(method.DeclaringType));
                    if (node != null)
                    {
                        nodes.Add(node);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GraphNodeGenerator] Error reading node {method.DeclaringType.FullName}.{method.Name}: {ex.Message}");
                }
            }

            // TypeCache has no index for properties, so those are still found by reflection -- but only
            // in assemblies that reference the one declaring the attribute, since no other assembly can
            // have used it.
            Assembly attributeAssembly = typeof(GraphPropertyAttribute).Assembly;
            string attributeAssemblyName = attributeAssembly.GetName().Name;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                if (assembly != attributeAssembly &&
                    !assembly.GetReferencedAssemblies().Any(reference => reference.Name == attributeAssemblyName))
                {
                    continue;
                }

                try
                {
                    ScanAssemblyProperties(assembly, nodes);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GraphNodeGenerator] Error scanning assembly {assembly.FullName}: {ex.Message}");
                }
            }

            // Sorted on more than the menu path. Generated files are only rewritten when their text
            // changes, and neither source above promises an order, so two nodes sharing a path must not
            // be free to swap places between runs.
            nodes.Sort(CompareNodes);

            return nodes;
        }

        private static int CompareNodes(NodeDefinition a, NodeDefinition b)
        {
            int byPath = string.Compare(a.MenuPath, b.MenuPath, StringComparison.Ordinal);
            if (byPath != 0)
                return byPath;

            int byType = string.Compare(a.ContainingType?.FullName, b.ContainingType?.FullName, StringComparison.Ordinal);
            if (byType != 0)
                return byType;

            return string.Compare(a.Method?.ToString(), b.Method?.ToString(), StringComparison.Ordinal);
        }

        private static string GetCategoryPath(Type type)
        {
            return type.GetCustomAttribute<GraphCategoryAttribute>()?.Path;
        }

        /// <summary>
        /// Scans a single assembly for [GraphProperty] properties.
        /// </summary>
        private static void ScanAssemblyProperties(Assembly assembly, List<NodeDefinition> nodes)
        {
            // DeclaredOnly: a property is reported by the type that declares it and not again by each
            // type that inherits it.
            const BindingFlags declaredPublic =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // One type that fails to load must not hide every property in the assembly.
                types = ex.Types.Where(type => type != null).ToArray();
            }

            foreach (var type in types)
            {
                PropertyInfo[] properties = type.GetProperties(declaredPublic);
                if (properties.Length == 0)
                    continue;

                string categoryPath = null;
                bool categoryResolved = false;

                foreach (var property in properties)
                {
                    // Not inherited: an override that does not repeat the attribute is already covered by
                    // the node for the property it overrides.
                    var propAttr = property.GetCustomAttribute<GraphPropertyAttribute>(false);
                    if (propAttr == null)
                        continue;

                    if (!categoryResolved)
                    {
                        categoryPath = GetCategoryPath(type);
                        categoryResolved = true;
                    }

                    var propertyNodes = CreatePropertyNodes(property, propAttr, categoryPath);
                    nodes.AddRange(propertyNodes);
                }
            }
        }

        /// <summary>
        /// Creates a node definition from a method.
        /// </summary>
        internal static NodeDefinition CreateNodeDefinition(
            MethodInfo method,
            GraphNodeAttribute nodeAttr,
            string categoryPath)
        {
            var node = new NodeDefinition
            {
                Method = method,
                ContainingType = method.DeclaringType,
                MenuPath = nodeAttr.MenuPath,
                IsFlowNode = nodeAttr.IsFlowNode,
                Icon = nodeAttr.Icon,
                Color = nodeAttr.Color,
                Tooltip = nodeAttr.Tooltip ?? method.Name,
                Category = categoryPath
            };

            // Prepend category path if present
            if (!string.IsNullOrEmpty(categoryPath) && !node.MenuPath.StartsWith(categoryPath))
            {
                node.MenuPath = $"{categoryPath}/{node.MenuPath}";
            }

            // Parse parameters for inputs
            foreach (var param in method.GetParameters())
            {
                var inputAttr = param.GetCustomAttribute<GraphInputAttribute>();
                var outputAttr = param.GetCustomAttribute<GraphOutputAttribute>();

                if (param.IsOut || outputAttr != null)
                {
                    // Output parameter
                    node.Outputs.Add(new PortDefinition
                    {
                        Name = outputAttr?.DisplayName ?? param.Name,
                        Type = param.ParameterType.GetElementType() ?? param.ParameterType,
                        Tooltip = outputAttr?.Tooltip
                    });
                }
                else
                {
                    // Input parameter
                    node.Inputs.Add(new PortDefinition
                    {
                        Name = inputAttr?.DisplayName ?? param.Name,
                        Type = param.ParameterType,
                        DefaultValue = inputAttr?.DefaultValue ?? (param.HasDefaultValue ? param.DefaultValue : null),
                        Tooltip = inputAttr?.Tooltip,
                        Hidden = inputAttr?.Hidden ?? false
                    });
                }
            }

            // Add return type as output
            if (method.ReturnType != typeof(void))
            {
                node.Outputs.Insert(0, new PortDefinition
                {
                    Name = "Result",
                    Type = method.ReturnType
                });
            }

            // Parse flow outputs
            var flowOutputAttrs = method.GetCustomAttributes<GraphFlowOutputAttribute>();
            foreach (var flowAttr in flowOutputAttrs)
            {
                node.FlowOutputs.Add(flowAttr.Name);
            }

            return node;
        }

        /// <summary>
        /// Creates Get/Set nodes for a property.
        /// </summary>
        internal static List<NodeDefinition> CreatePropertyNodes(
            PropertyInfo property,
            GraphPropertyAttribute propAttr,
            string categoryPath)
        {
            var nodes = new List<NodeDefinition>();
            string basePath = propAttr.MenuPath ?? property.Name;

            if (!string.IsNullOrEmpty(categoryPath))
            {
                basePath = $"{categoryPath}/{basePath}";
            }

            // Create Get node
            if (property.CanRead && property.GetMethod != null)
            {
                nodes.Add(new NodeDefinition
                {
                    Method = property.GetMethod,
                    ContainingType = property.DeclaringType,
                    MenuPath = $"{basePath}/Get",
                    IsFlowNode = false,
                    Icon = propAttr.Icon,
                    Tooltip = propAttr.Tooltip ?? $"Gets {property.Name}",
                    Outputs = new List<PortDefinition>
                    {
                        new PortDefinition
                        {
                            Name = property.Name,
                            Type = property.PropertyType
                        }
                    }
                });
            }

            // Create Set node
            if (property.CanWrite && property.SetMethod != null && !propAttr.ReadOnly)
            {
                nodes.Add(new NodeDefinition
                {
                    Method = property.SetMethod,
                    ContainingType = property.DeclaringType,
                    MenuPath = $"{basePath}/Set",
                    IsFlowNode = true,
                    Icon = propAttr.Icon,
                    Tooltip = propAttr.Tooltip ?? $"Sets {property.Name}",
                    Inputs = new List<PortDefinition>
                    {
                        new PortDefinition
                        {
                            Name = property.Name,
                            Type = property.PropertyType
                        }
                    }
                });
            }

            return nodes;
        }

        /// <summary>
        /// Generates node asset files for the Udon Graph.
        /// </summary>
        private static void GenerateNodeAssets(List<NodeDefinition> nodes)
        {
            // Create output directory
            string outputDir = "Assets/UdonSharpCE/GeneratedNodes";
            if (!AssetDatabase.IsValidFolder("Assets/UdonSharpCE"))
            {
                AssetDatabase.CreateFolder("Assets", "UdonSharpCE");
            }
            if (!AssetDatabase.IsValidFolder(outputDir))
            {
                AssetDatabase.CreateFolder("Assets/UdonSharpCE", "GeneratedNodes");
            }

            // Generate manifest
            var manifest = new NodeManifest
            {
                GeneratedAt = DateTime.Now.ToString("O"),
                NodeCount = nodes.Count,
                Nodes = nodes.Select(n => new NodeManifestEntry
                {
                    MenuPath = n.MenuPath,
                    MethodName = n.Method?.Name,
                    TypeName = n.ContainingType?.FullName,
                    IsFlowNode = n.IsFlowNode,
                    InputCount = n.Inputs.Count,
                    OutputCount = n.Outputs.Count
                }).ToList()
            };

            string manifestJson = JsonUtility.ToJson(manifest, true);
            string manifestPath = $"{outputDir}/NodeManifest.json";

            System.IO.File.WriteAllText(manifestPath, manifestJson);
            AssetDatabase.ImportAsset(manifestPath);

            Debug.Log($"[GraphNodeGenerator] Generated manifest at {manifestPath}");
        }

        /// <summary>
        /// Manifest of generated nodes.
        /// </summary>
        [Serializable]
        private class NodeManifest
        {
            public string GeneratedAt;
            public int NodeCount;
            public List<NodeManifestEntry> Nodes;
        }

        /// <summary>
        /// Entry in the node manifest.
        /// </summary>
        [Serializable]
        private class NodeManifestEntry
        {
            public string MenuPath;
            public string MethodName;
            public string TypeName;
            public bool IsFlowNode;
            public int InputCount;
            public int OutputCount;
        }
    }

    /// <summary>
    /// Asset postprocessor to regenerate nodes on script changes.
    /// </summary>
    public class GraphNodePostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            // Check if any C# files changed
            bool scriptsChanged = importedAssets.Any(a => a.EndsWith(".cs")) ||
                                  deletedAssets.Any(a => a.EndsWith(".cs"));

            if (scriptsChanged)
            {
                GraphNodeGenerator.InvalidateCache();
            }
        }
    }
}
