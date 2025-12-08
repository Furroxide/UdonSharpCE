using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.GraphBridge
{
    /// <summary>
    /// Generates Markdown documentation for all CE Graph Nodes.
    /// Creates hierarchical documentation matching the node menu structure.
    /// </summary>
    public static class GraphNodeDocGenerator
    {
        #region Constants

        private const string OUTPUT_FOLDER = "Tools/Docusaurus/docs/api/graph-nodes";
        private const string INDEX_FILE = "_category_.json";

        #endregion

        #region Public Methods

        /// <summary>
        /// Generates documentation for all discovered graph nodes.
        /// </summary>
        [MenuItem("Tools/UdonSharpCE/Generate Node Documentation")]
        public static void GenerateAllDocs()
        {
            var nodes = GraphNodeGenerator.GetNodeDefinitions();

            if (nodes.Count == 0)
            {
                Debug.Log("[GraphNodeDocGenerator] No graph nodes found to document.");
                return;
            }

            EnsureOutputFolder();

            // Group nodes by category
            var nodesByCategory = GroupNodesByCategory(nodes);

            // Generate index
            GenerateIndexFile(nodesByCategory);

            int generatedCount = 0;

            // Generate category pages
            foreach (var kvp in nodesByCategory)
            {
                string category = kvp.Key;
                var categoryNodes = kvp.Value;

                // Create category folder
                string categoryFolder = Path.Combine(OUTPUT_FOLDER, SanitizeFileName(category));
                Directory.CreateDirectory(categoryFolder);

                // Generate category index
                GenerateCategoryIndex(categoryFolder, category, categoryNodes);

                // Generate individual node pages
                foreach (var node in categoryNodes)
                {
                    GenerateNodePage(categoryFolder, node);
                    generatedCount++;
                }
            }

            // Generate overview page
            GenerateOverviewPage(nodes);

            AssetDatabase.Refresh();
            Debug.Log($"[GraphNodeDocGenerator] Generated documentation for {generatedCount} nodes in {OUTPUT_FOLDER}");
        }

        /// <summary>
        /// Generates documentation for a single node.
        /// </summary>
        public static void GenerateSingleNodeDoc(NodeDefinition node)
        {
            if (node?.Method == null)
            {
                Debug.LogWarning("[GraphNodeDocGenerator] Cannot generate docs for null node.");
                return;
            }

            EnsureOutputFolder();

            string category = GetCategory(node.MenuPath);
            string categoryFolder = Path.Combine(OUTPUT_FOLDER, SanitizeFileName(category));
            Directory.CreateDirectory(categoryFolder);

            GenerateNodePage(categoryFolder, node);

            AssetDatabase.Refresh();
            Debug.Log($"[GraphNodeDocGenerator] Generated documentation for {node.MenuPath}");
        }

        #endregion

        #region Documentation Generation

        private static void GenerateIndexFile(Dictionary<string, List<NodeDefinition>> nodesByCategory)
        {
            string indexPath = Path.Combine(OUTPUT_FOLDER, INDEX_FILE);

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"label\": \"Graph Nodes\",");
            sb.AppendLine("  \"position\": 5,");
            sb.AppendLine("  \"link\": {");
            sb.AppendLine("    \"type\": \"generated-index\",");
            sb.AppendLine("    \"description\": \"Documentation for all CE Graph Nodes available in UdonSharpCE.\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(indexPath, sb.ToString());
        }

        private static void GenerateCategoryIndex(string categoryFolder, string category, List<NodeDefinition> nodes)
        {
            string indexPath = Path.Combine(categoryFolder, INDEX_FILE);

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"label\": \"{category}\",");
            sb.AppendLine($"  \"position\": {GetCategoryPosition(category)},");
            sb.AppendLine("  \"link\": {");
            sb.AppendLine("    \"type\": \"generated-index\",");
            sb.AppendLine($"    \"description\": \"{category} graph nodes ({nodes.Count} nodes).\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            File.WriteAllText(indexPath, sb.ToString());
        }

        private static void GenerateOverviewPage(List<NodeDefinition> allNodes)
        {
            string filePath = Path.Combine(OUTPUT_FOLDER, "overview.md");

            var sb = new StringBuilder();

            // Frontmatter
            sb.AppendLine("---");
            sb.AppendLine("sidebar_position: 1");
            sb.AppendLine("title: Graph Nodes Overview");
            sb.AppendLine("description: Overview of all CE Graph Nodes");
            sb.AppendLine("---");
            sb.AppendLine();

            // Title
            sb.AppendLine("# CE Graph Nodes Overview");
            sb.AppendLine();

            sb.AppendLine("This section documents all graph nodes available through UdonSharpCE's GraphBridge system.");
            sb.AppendLine();

            // Stats
            var categories = allNodes.GroupBy(n => GetCategory(n.MenuPath)).ToList();
            sb.AppendLine("## Statistics");
            sb.AppendLine();
            sb.AppendLine($"- **Total Nodes:** {allNodes.Count}");
            sb.AppendLine($"- **Categories:** {categories.Count}");
            sb.AppendLine($"- **Flow Nodes:** {allNodes.Count(n => n.IsFlowNode)}");
            sb.AppendLine($"- **Value Nodes:** {allNodes.Count(n => !n.IsFlowNode)}");
            sb.AppendLine();

            // Category list
            sb.AppendLine("## Categories");
            sb.AppendLine();

            foreach (var category in categories.OrderBy(g => g.Key))
            {
                string categoryName = category.Key;
                int count = category.Count();
                string folderName = SanitizeFileName(categoryName);

                sb.AppendLine($"### [{categoryName}](./{folderName}/)");
                sb.AppendLine();
                sb.AppendLine($"{count} nodes");
                sb.AppendLine();

                // List first few nodes
                var previewNodes = category.Take(5);
                foreach (var node in previewNodes)
                {
                    string nodeName = GetNodeName(node.MenuPath);
                    sb.AppendLine($"- `{nodeName}`");
                }

                if (count > 5)
                {
                    sb.AppendLine($"- *...and {count - 5} more*");
                }

                sb.AppendLine();
            }

            // Usage
            sb.AppendLine("## Usage");
            sb.AppendLine();
            sb.AppendLine("Graph nodes can be used in two ways:");
            sb.AppendLine();
            sb.AppendLine("1. **Direct Method Calls** - Call the underlying methods directly from your UdonSharpBehaviour code.");
            sb.AppendLine("2. **Generated Wrappers** - Use the code generator to create type-safe wrapper classes.");
            sb.AppendLine();
            sb.AppendLine("### Generating Wrappers");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("Tools > UdonSharpCE > Generate All Wrappers");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("This creates wrapper classes in `Assets/UdonSharpCE/GeneratedWrappers/`.");
            sb.AppendLine();

            File.WriteAllText(filePath, sb.ToString());
        }

        private static void GenerateNodePage(string categoryFolder, NodeDefinition node)
        {
            string nodeName = GetNodeName(node.MenuPath);
            string fileName = SanitizeFileName(nodeName) + ".md";
            string filePath = Path.Combine(categoryFolder, fileName);

            var sb = new StringBuilder();

            // Frontmatter
            sb.AppendLine("---");
            sb.AppendLine($"sidebar_label: \"{nodeName}\"");
            sb.AppendLine($"title: \"{nodeName}\"");
            sb.AppendLine($"description: \"{EscapeString(node.Tooltip ?? nodeName)}\"");
            sb.AppendLine("---");
            sb.AppendLine();

            // Title
            sb.AppendLine($"# {nodeName}");
            sb.AppendLine();

            // Description
            if (!string.IsNullOrEmpty(node.Tooltip))
            {
                sb.AppendLine(node.Tooltip);
                sb.AppendLine();
            }

            // Node info table
            sb.AppendLine("## Node Information");
            sb.AppendLine();
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");
            sb.AppendLine($"| **Menu Path** | `{node.MenuPath}` |");
            sb.AppendLine($"| **Node Type** | {(node.IsFlowNode ? "Flow Node" : "Value Node")} |");
            sb.AppendLine($"| **Containing Type** | `{node.ContainingType?.FullName ?? "Unknown"}` |");

            if (node.Method != null)
            {
                sb.AppendLine($"| **Method** | `{node.Method.Name}` |");
                sb.AppendLine($"| **Static** | {(node.Method.IsStatic ? "Yes" : "No")} |");
            }

            sb.AppendLine();

            // Signature
            if (node.Method != null)
            {
                sb.AppendLine("## Signature");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(GetMethodSignature(node));
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // Inputs
            if (node.Inputs.Count > 0)
            {
                sb.AppendLine("## Inputs");
                sb.AppendLine();
                sb.AppendLine("| Name | Type | Default | Description |");
                sb.AppendLine("|------|------|---------|-------------|");

                foreach (var input in node.Inputs)
                {
                    string defaultVal = input.DefaultValue != null ? $"`{input.DefaultValue}`" : "-";
                    string tooltip = !string.IsNullOrEmpty(input.Tooltip) ? input.Tooltip : "-";
                    string hidden = input.Hidden ? " (hidden)" : "";

                    sb.AppendLine($"| `{input.Name}`{hidden} | `{GetTypeName(input.Type)}` | {defaultVal} | {tooltip} |");
                }

                sb.AppendLine();
            }

            // Outputs
            if (node.Outputs.Count > 0)
            {
                sb.AppendLine("## Outputs");
                sb.AppendLine();
                sb.AppendLine("| Name | Type | Description |");
                sb.AppendLine("|------|------|-------------|");

                foreach (var output in node.Outputs)
                {
                    string tooltip = !string.IsNullOrEmpty(output.Tooltip) ? output.Tooltip : "-";
                    sb.AppendLine($"| `{output.Name}` | `{GetTypeName(output.Type)}` | {tooltip} |");
                }

                sb.AppendLine();
            }

            // Flow outputs
            if (node.FlowOutputs.Count > 0)
            {
                sb.AppendLine("## Flow Outputs");
                sb.AppendLine();
                sb.AppendLine("This node has multiple execution paths:");
                sb.AppendLine();

                foreach (var flowOut in node.FlowOutputs)
                {
                    sb.AppendLine($"- **{flowOut}**");
                }

                sb.AppendLine();
            }

            // Example usage
            sb.AppendLine("## Example Usage");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(GenerateExampleCode(node));
            sb.AppendLine("```");
            sb.AppendLine();

            // Validation
            var errors = UdonGraphIntegration.ValidateNode(node);
            if (errors.Count > 0)
            {
                sb.AppendLine("## Validation Notes");
                sb.AppendLine();
                sb.AppendLine(":::warning");
                sb.AppendLine("This node has the following validation warnings:");
                sb.AppendLine();
                foreach (var error in errors)
                {
                    sb.AppendLine($"- {error}");
                }
                sb.AppendLine(":::");
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        #endregion

        #region Helpers

        private static void EnsureOutputFolder()
        {
            if (!Directory.Exists(OUTPUT_FOLDER))
            {
                Directory.CreateDirectory(OUTPUT_FOLDER);
            }
        }

        private static Dictionary<string, List<NodeDefinition>> GroupNodesByCategory(List<NodeDefinition> nodes)
        {
            var result = new Dictionary<string, List<NodeDefinition>>();

            foreach (var node in nodes)
            {
                string category = GetCategory(node.MenuPath);

                if (!result.ContainsKey(category))
                {
                    result[category] = new List<NodeDefinition>();
                }

                result[category].Add(node);
            }

            return result;
        }

        private static string GetCategory(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
                return "Uncategorized";

            int slashIndex = menuPath.IndexOf('/');
            return slashIndex >= 0 ? menuPath.Substring(0, slashIndex) : menuPath;
        }

        private static string GetNodeName(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
                return "Unknown";

            int slashIndex = menuPath.LastIndexOf('/');
            return slashIndex >= 0 ? menuPath.Substring(slashIndex + 1) : menuPath;
        }

        private static int GetCategoryPosition(string category)
        {
            // Define category ordering
            var categoryOrder = new Dictionary<string, int>
            {
                { "Math", 1 },
                { "Logic", 2 },
                { "Flow", 3 },
                { "String", 4 },
                { "Array", 5 },
                { "Vector", 6 },
                { "Transform", 7 },
                { "Physics", 8 },
                { "Audio", 9 },
                { "Network", 10 },
                { "Player", 11 },
                { "CE", 20 },
            };

            return categoryOrder.TryGetValue(category, out int pos) ? pos : 100;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();

            foreach (char c in name.ToLowerInvariant())
            {
                if (invalid.Contains(c) || c == '/' || c == ' ')
                    sb.Append('-');
                else
                    sb.Append(c);
            }

            return sb.ToString();
        }

        private static string EscapeString(string str)
        {
            if (string.IsNullOrEmpty(str))
                return "";

            return str
                .Replace("\"", "\\\"")
                .Replace("\n", " ")
                .Replace("\r", "");
        }

        private static string GetMethodSignature(NodeDefinition node)
        {
            if (node.Method == null)
                return "// Unknown method";

            var sb = new StringBuilder();

            // Modifiers
            if (node.Method.IsStatic)
                sb.Append("static ");

            // Return type
            sb.Append(GetTypeName(node.Method.ReturnType));
            sb.Append(" ");

            // Method name
            sb.Append(node.Method.Name);
            sb.Append("(");

            // Parameters
            var parameters = node.Method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");

                var param = parameters[i];
                if (param.IsOut)
                    sb.Append("out ");

                Type paramType = param.ParameterType.IsByRef
                    ? param.ParameterType.GetElementType()
                    : param.ParameterType;

                sb.Append(GetTypeName(paramType));
                sb.Append(" ");
                sb.Append(param.Name);
            }

            sb.Append(")");

            return sb.ToString();
        }

        private static string GenerateExampleCode(NodeDefinition node)
        {
            if (node.Method == null)
                return "// No method available";

            var sb = new StringBuilder();

            sb.AppendLine("public class ExampleBehaviour : UdonSharpBehaviour");
            sb.AppendLine("{");
            sb.AppendLine("    void Start()");
            sb.AppendLine("    {");

            // Build the call
            sb.Append("        ");

            if (node.Method.ReturnType != typeof(void))
            {
                sb.Append($"{GetTypeName(node.Method.ReturnType)} result = ");
            }

            if (node.Method.IsStatic)
            {
                sb.Append($"{node.ContainingType?.Name ?? "Unknown"}.");
            }

            sb.Append($"{node.Method.Name}(");

            var parameters = node.Method.GetParameters()
                .Where(p => !p.IsOut)
                .ToList();

            for (int i = 0; i < parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(GetDefaultValueString(parameters[i].ParameterType));
            }

            sb.AppendLine(");");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string GetTypeName(Type type)
        {
            if (type == null) return "void";

            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(double)) return "double";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(object)) return "object";

            if (type.IsArray)
            {
                return GetTypeName(type.GetElementType()) + "[]";
            }

            if (type.IsByRef)
            {
                return GetTypeName(type.GetElementType());
            }

            return type.Name;
        }

        private static string GetDefaultValueString(Type type)
        {
            if (type == typeof(int)) return "0";
            if (type == typeof(float)) return "0f";
            if (type == typeof(bool)) return "false";
            if (type == typeof(string)) return "\"example\"";
            if (type == typeof(double)) return "0.0";
            if (type == typeof(byte)) return "0";
            if (type == typeof(Vector2)) return "Vector2.zero";
            if (type == typeof(Vector3)) return "Vector3.zero";
            if (type == typeof(Vector4)) return "Vector4.zero";
            if (type == typeof(Quaternion)) return "Quaternion.identity";
            if (type == typeof(Color)) return "Color.white";
            if (type == typeof(Transform)) return "transform";
            if (type == typeof(GameObject)) return "gameObject";

            if (type.IsArray)
            {
                return "null";
            }

            if (type.IsValueType)
            {
                return $"default({GetTypeName(type)})";
            }

            return "null";
        }

        #endregion
    }
}

