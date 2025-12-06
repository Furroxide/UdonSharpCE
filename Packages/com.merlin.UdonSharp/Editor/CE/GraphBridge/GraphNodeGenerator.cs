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
        [MenuItem("Tools/UdonSharpCE/Generate Graph Nodes")]
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
        /// Scans all assemblies for [GraphNode] methods.
        /// </summary>
        private static List<NodeDefinition> ScanForGraphNodes()
        {
            var nodes = new List<NodeDefinition>();

            // Get all loaded assemblies
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                // Skip system assemblies
                if (assembly.FullName.StartsWith("System.") ||
                    assembly.FullName.StartsWith("mscorlib") ||
                    assembly.FullName.StartsWith("Unity.") ||
                    assembly.FullName.StartsWith("UnityEngine.") ||
                    assembly.FullName.StartsWith("UnityEditor."))
                {
                    continue;
                }

                try
                {
                    ScanAssembly(assembly, nodes);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[GraphNodeGenerator] Error scanning assembly {assembly.FullName}: {ex.Message}");
                }
            }

            // Sort by menu path
            nodes.Sort((a, b) => string.Compare(a.MenuPath, b.MenuPath, StringComparison.Ordinal));

            return nodes;
        }

        /// <summary>
        /// Scans a single assembly for graph nodes.
        /// </summary>
        private static void ScanAssembly(Assembly assembly, List<NodeDefinition> nodes)
        {
            Type graphNodeAttrType = typeof(GraphNodeAttribute);

            foreach (var type in assembly.GetTypes())
            {
                // Get category attribute if present
                string categoryPath = null;
                var categoryAttr = type.GetCustomAttribute<GraphCategoryAttribute>();
                if (categoryAttr != null)
                {
                    categoryPath = categoryAttr.Path;
                }

                // Scan methods
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                {
                    var nodeAttr = method.GetCustomAttribute<GraphNodeAttribute>();
                    if (nodeAttr == null)
                        continue;

                    var node = CreateNodeDefinition(method, nodeAttr, categoryPath);
                    if (node != null)
                    {
                        nodes.Add(node);
                    }
                }

                // Scan properties
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                {
                    var propAttr = property.GetCustomAttribute<GraphPropertyAttribute>();
                    if (propAttr == null)
                        continue;

                    var propertyNodes = CreatePropertyNodes(property, propAttr, categoryPath);
                    nodes.AddRange(propertyNodes);
                }
            }
        }

        /// <summary>
        /// Creates a node definition from a method.
        /// </summary>
        private static NodeDefinition CreateNodeDefinition(
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
        private static List<NodeDefinition> CreatePropertyNodes(
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
