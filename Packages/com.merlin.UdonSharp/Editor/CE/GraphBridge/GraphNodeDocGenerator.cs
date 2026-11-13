using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.GraphBridge
{
    /// <summary>
    /// Generates Markdown documentation for all CE Graph Nodes.
    /// Creates hierarchical documentation matching the node menu structure.
    /// </summary>
    /// <remarks>
    /// The pages are written for a Docusaurus site, which reads .md files as MDX. In MDX a brace opens a
    /// JavaScript expression and an angle bracket opens a JSX element, so any tooltip or name containing
    /// one -- a generic type, an HTML tag, a sentence with "{x}" in it -- used to fail the whole site
    /// build. Every piece of user text below therefore goes through an escape for the place it lands in:
    /// Markdown text, an inline code span, a YAML front-matter string, or a JSON string.
    /// </remarks>
    public static class GraphNodeDocGenerator
    {
        #region Constants

        private const string INDEX_FILE = "_category_.json";
        private const string OVERVIEW_FILE = "overview.md";

        // The folder the last run wrote to. EditorUserSettings is stored per project, under UserSettings/,
        // so two projects open on one machine do not overwrite each other's choice the way a single
        // EditorPrefs key would.
        private const string OUTPUT_FOLDER_SETTING = "UdonSharpCE.GraphBridge.DocumentationFolder";

        #endregion

        #region Public Methods

        /// <summary>
        /// Generates documentation for all discovered graph nodes into a folder the user picks.
        /// </summary>
        [MenuItem("Udon CE/Graph Bridge/Generate Documentation", false, 1505)]
        public static void GenerateAllDocs()
        {
            var nodes = GraphNodeGenerator.GetNodeDefinitions();

            if (nodes.Count == 0)
            {
                Debug.Log("[GraphNodeDocGenerator] No graph nodes found to document.");
                return;
            }

            string outputFolder = PromptForOutputFolder();
            if (outputFolder == null)
                return;

            int generatedCount = WriteDocumentation(nodes, outputFolder);

            RefreshIfInsideAssets(outputFolder);
            Debug.Log($"[GraphNodeDocGenerator] Generated documentation for {generatedCount} nodes in {outputFolder}");
        }

        /// <summary>
        /// Generates documentation for a single node, into a folder the user picks.
        /// </summary>
        public static void GenerateSingleNodeDoc(NodeDefinition node)
        {
            if (node?.Method == null)
            {
                Debug.LogWarning("[GraphNodeDocGenerator] Cannot generate docs for null node.");
                return;
            }

            string outputFolder = PromptForOutputFolder();
            if (outputFolder == null)
                return;

            // Planned against every node, so the page gets the same file name a full run would give it.
            var allNodes = new List<NodeDefinition>(GraphNodeGenerator.GetNodeDefinitions());
            if (!allNodes.Contains(node))
                allNodes.Add(node);

            PlannedPage page = PlanPages(allNodes).First(planned => planned.Node == node);

            string categoryFolder = Path.Combine(outputFolder, page.CategoryFolder);
            Directory.CreateDirectory(categoryFolder);
            File.WriteAllText(Path.Combine(categoryFolder, page.FileName), BuildNodePage(page));

            RefreshIfInsideAssets(outputFolder);
            Debug.Log($"[GraphNodeDocGenerator] Generated documentation for {node.MenuPath}");
        }

        #endregion

        #region Output Folder

        /// <summary>
        /// Asks where to write, starting from the folder used last time in this project. Null if cancelled.
        ///
        /// This used to write to Tools/Docusaurus/docs/api/graph-nodes under the project root: the layout
        /// of the UdonSharp CE repository's own documentation site, which in any other project is a stray
        /// folder nothing reads.
        /// </summary>
        private static string PromptForOutputFolder()
        {
            string remembered = EditorUserSettings.GetConfigValue(OUTPUT_FOLDER_SETTING);
            string start = !string.IsNullOrEmpty(remembered) && Directory.Exists(remembered)
                ? remembered
                : Path.GetDirectoryName(Application.dataPath);

            string chosen = EditorUtility.SaveFolderPanel("Generate Graph Node Documentation", start, "");
            if (string.IsNullOrEmpty(chosen))
                return null;

            EditorUserSettings.SetConfigValue(OUTPUT_FOLDER_SETTING, chosen);
            return chosen;
        }

        private static void RefreshIfInsideAssets(string folder)
        {
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/') + "/";
            string target = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/') + "/";

            if (target.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
            {
                AssetDatabase.Refresh();
            }
        }

        #endregion

        #region Documentation Generation

        /// <summary>
        /// One page to be written: the node, and where its page goes.
        /// </summary>
        internal sealed class PlannedPage
        {
            public NodeDefinition Node;

            /// <summary>The first segment of the menu path, as written.</summary>
            public string Category;

            /// <summary>The folder name for the category, unique among the categories.</summary>
            public string CategoryFolder;

            /// <summary>The page title: the menu path below the category.</summary>
            public string Title;

            /// <summary>The page file name, unique within its category folder.</summary>
            public string FileName;
        }

        /// <summary>
        /// Writes the whole documentation tree for <paramref name="nodes"/> into
        /// <paramref name="outputFolder"/>. Returns the number of node pages written.
        /// </summary>
        internal static int WriteDocumentation(IEnumerable<NodeDefinition> nodes, string outputFolder)
        {
            List<PlannedPage> pages = PlanPages(nodes);

            Directory.CreateDirectory(outputFolder);

            File.WriteAllText(Path.Combine(outputFolder, INDEX_FILE), BuildIndexFile());

            foreach (var category in pages.GroupBy(page => page.CategoryFolder))
            {
                string categoryFolder = Path.Combine(outputFolder, category.Key);
                Directory.CreateDirectory(categoryFolder);

                File.WriteAllText(
                    Path.Combine(categoryFolder, INDEX_FILE),
                    BuildCategoryIndex(category.First().Category, category.Count()));

                foreach (var page in category)
                {
                    File.WriteAllText(Path.Combine(categoryFolder, page.FileName), BuildNodePage(page));
                }
            }

            File.WriteAllText(Path.Combine(outputFolder, OVERVIEW_FILE), BuildOverviewPage(pages));

            return pages.Count;
        }

        /// <summary>
        /// Decides every page's folder and file name.
        ///
        /// A page used to be named after the last segment of its menu path alone. Every [GraphProperty]
        /// produces ".../Get" and ".../Set" nodes, so in a category with two properties the second
        /// property's pages silently overwrote the first's -- and any two nodes ending in the same word,
        /// or differing only in case, did the same. The name is now the whole path below the category,
        /// made unique within its folder.
        /// </summary>
        internal static List<PlannedPage> PlanPages(IEnumerable<NodeDefinition> nodes)
        {
            var pages = new List<PlannedPage>();
            var categoryFolders = new Dictionary<string, string>(StringComparer.Ordinal);
            var usedCategoryFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedFileNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                if (node == null)
                    continue;

                string category = GetCategory(node.MenuPath);

                if (!categoryFolders.TryGetValue(category, out string categoryFolder))
                {
                    categoryFolder = MakeUnique(SanitizeFileName(category), usedCategoryFolders);
                    categoryFolders.Add(category, categoryFolder);
                    usedFileNames.Add(categoryFolder, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                string title = GetNodeName(node.MenuPath);
                string fileName = MakeUnique(SanitizeFileName(title), usedFileNames[categoryFolder]) + ".md";

                pages.Add(new PlannedPage
                {
                    Node = node,
                    Category = category,
                    CategoryFolder = categoryFolder,
                    Title = title,
                    FileName = fileName
                });
            }

            return pages;
        }

        private static string MakeUnique(string name, HashSet<string> used)
        {
            string candidate = name;

            for (int suffix = 2; !used.Add(candidate); suffix++)
            {
                candidate = name + "-" + suffix;
            }

            return candidate;
        }

        private static string BuildIndexFile()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"label\": \"Graph Nodes\",");
            sb.AppendLine("  \"position\": 5,");
            sb.AppendLine("  \"link\": {");
            sb.AppendLine("    \"type\": \"generated-index\",");
            sb.AppendLine("    \"description\": \"Documentation for all CE Graph Nodes available in UdonSharpCE.\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string BuildCategoryIndex(string category, int nodeCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"label\": {JsonString(category)},");
            sb.AppendLine($"  \"position\": {GetCategoryPosition(category)},");
            sb.AppendLine("  \"link\": {");
            sb.AppendLine("    \"type\": \"generated-index\",");
            sb.AppendLine($"    \"description\": {JsonString($"{category} graph nodes ({nodeCount} nodes).")}");
            sb.AppendLine("  }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string BuildOverviewPage(List<PlannedPage> pages)
        {
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
            var categories = pages.GroupBy(page => page.CategoryFolder).ToList();
            sb.AppendLine("## Statistics");
            sb.AppendLine();
            sb.AppendLine($"- **Total Nodes:** {pages.Count}");
            sb.AppendLine($"- **Categories:** {categories.Count}");
            sb.AppendLine($"- **Flow Nodes:** {pages.Count(page => page.Node.IsFlowNode)}");
            sb.AppendLine($"- **Value Nodes:** {pages.Count(page => !page.Node.IsFlowNode)}");
            sb.AppendLine();

            // Category list. Each node links to its own page file rather than to the category folder: a
            // folder link only resolves on a site whose sidebar is generated from the folders, and a
            // broken link fails a Docusaurus build.
            sb.AppendLine("## Categories");
            sb.AppendLine();

            foreach (var category in categories.OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"### {EscapeMarkdown(category.First().Category)}");
                sb.AppendLine();
                sb.AppendLine($"{category.Count()} nodes");
                sb.AppendLine();

                foreach (var page in category)
                {
                    sb.AppendLine($"- [{EscapeMarkdown(page.Title)}](./{page.CategoryFolder}/{page.FileName})");
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
            sb.AppendLine("Udon CE > Graph Bridge > Generate Wrappers");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("This creates wrapper classes in `Assets/UdonSharpCE/GeneratedWrappers/`.");
            sb.AppendLine();

            return sb.ToString();
        }

        private static string BuildNodePage(PlannedPage page)
        {
            NodeDefinition node = page.Node;
            var sb = new StringBuilder();

            // Frontmatter
            sb.AppendLine("---");
            sb.AppendLine($"sidebar_label: {YamlString(page.Title)}");
            sb.AppendLine($"title: {YamlString(page.Title)}");
            sb.AppendLine($"description: {YamlString(node.Tooltip ?? page.Title)}");
            sb.AppendLine("---");
            sb.AppendLine();

            // Title
            sb.AppendLine($"# {EscapeMarkdown(page.Title)}");
            sb.AppendLine();

            // Description
            if (!string.IsNullOrEmpty(node.Tooltip))
            {
                sb.AppendLine(EscapeMarkdown(node.Tooltip));
                sb.AppendLine();
            }

            // Node info table
            sb.AppendLine("## Node Information");
            sb.AppendLine();
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");
            sb.AppendLine($"| **Menu Path** | {CodeCell(node.MenuPath)} |");
            sb.AppendLine($"| **Node Type** | {(node.IsFlowNode ? "Flow Node" : "Value Node")} |");
            sb.AppendLine($"| **Containing Type** | {CodeCell(node.ContainingType != null ? GetTypeName(node.ContainingType, true) : "Unknown")} |");

            if (node.Method != null)
            {
                sb.AppendLine($"| **Method** | {CodeCell(node.Method.Name)} |");
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
                    string defaultVal = input.DefaultValue != null ? CodeCell(input.DefaultValue.ToString()) : "-";
                    string tooltip = !string.IsNullOrEmpty(input.Tooltip) ? EscapeMarkdown(input.Tooltip) : "-";
                    string hidden = input.Hidden ? " (hidden)" : "";

                    sb.AppendLine($"| {CodeCell(input.Name)}{hidden} | {CodeCell(GetTypeName(input.Type))} | {defaultVal} | {tooltip} |");
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
                    string tooltip = !string.IsNullOrEmpty(output.Tooltip) ? EscapeMarkdown(output.Tooltip) : "-";
                    sb.AppendLine($"| {CodeCell(output.Name)} | {CodeCell(GetTypeName(output.Type))} | {tooltip} |");
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
                    sb.AppendLine($"- **{EscapeMarkdown(flowOut)}**");
                }

                sb.AppendLine();
            }

            // Example usage
            if (node.Method != null)
            {
                sb.AppendLine("## Example Usage");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(GenerateExampleCode(node));
                sb.AppendLine("```");
                sb.AppendLine();
            }

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
                    sb.AppendLine($"- {EscapeMarkdown(error)}");
                }
                sb.AppendLine(":::");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        #endregion

        #region Escaping

        private static string SingleLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var sb = new StringBuilder(text.Length);

            foreach (char c in text)
            {
                // Line breaks would end a table row, a heading or a front-matter value part-way through.
                if (c == '\r' || c == '\n')
                    sb.Append(' ');
                else if (c == '\t' || c >= ' ')
                    sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Text placed in the Markdown body: a heading, a paragraph, a list item or a table cell.
        /// Backslash escapes are CommonMark's and MDX honours them, including for braces and angle brackets.
        /// </summary>
        internal static string EscapeMarkdown(string text)
        {
            text = SingleLine(text);

            var sb = new StringBuilder(text.Length + 8);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // MDX expressions and JSX, then Markdown's inline syntax, then the table cell separator.
                // A leading -, + or = would start a list or turn the line above into a heading, and a
                // leading colon can open a ":::" admonition.
                bool escape = "{}<>\\`*_[]#|~&".IndexOf(c) >= 0 ||
                              (i == 0 && (c == '-' || c == '+' || c == '=' || c == ':'));

                if (escape)
                    sb.Append('\\');

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// An inline code span inside a table row. Code spans are literal, so braces and angle brackets are
        /// safe in one, but a backtick ends it early -- a generic type's reflection name such as List`1
        /// used to -- and a pipe ends the table cell.
        /// </summary>
        internal static string CodeCell(string text)
        {
            text = SingleLine(text).Replace("|", "\\|");

            if (text.Length == 0)
                return "-";

            int longestRun = 0;
            int run = 0;

            foreach (char c in text)
            {
                run = c == '`' ? run + 1 : 0;
                longestRun = Math.Max(longestRun, run);
            }

            string fence = new string('`', longestRun + 1);
            bool pad = text[0] == '`' || text[text.Length - 1] == '`';

            return pad ? $"{fence} {text} {fence}" : $"{fence}{text}{fence}";
        }

        /// <summary>
        /// A double-quoted YAML scalar for the front matter. The title and label were written between
        /// quotes without escaping, so a name with a quote or backslash in it broke the page's metadata.
        /// </summary>
        internal static string YamlString(string text)
        {
            return "\"" + SingleLine(text).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// A JSON string literal, quotes included.
        /// </summary>
        internal static string JsonString(string text)
        {
            var sb = new StringBuilder("\"");

            foreach (char c in text ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        #endregion

        #region Helpers

        private static string GetCategory(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
                return "Uncategorized";

            int slashIndex = menuPath.IndexOf('/');
            return slashIndex >= 0 ? menuPath.Substring(0, slashIndex) : menuPath;
        }

        /// <summary>
        /// The menu path below its category, which is what tells two nodes of one category apart.
        /// </summary>
        private static string GetNodeName(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
                return "Unknown";

            int slashIndex = menuPath.IndexOf('/');
            string name = slashIndex >= 0 ? menuPath.Substring(slashIndex + 1) : menuPath;

            return string.IsNullOrEmpty(name) ? menuPath : name;
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

        /// <summary>
        /// A file or folder name that is also safe in a URL: lower case letters, digits, '.', '_' and
        /// '-', since Docusaurus turns the file name into the page's address.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var sb = new StringBuilder();

            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.';

                if (keep)
                    sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }

            string sanitized = sb.ToString().Trim('-', '.');
            return sanitized.Length > 0 ? sanitized : "node";
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

            if (node.Method.IsGenericMethodDefinition)
            {
                sb.Append('<').Append(string.Join(", ", node.Method.GetGenericArguments().Select(argument => argument.Name))).Append('>');
            }

            sb.Append("(");

            // Parameters
            var parameters = node.Method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");

                var param = parameters[i];
                if (param.IsOut)
                    sb.Append("out ");
                else if (param.ParameterType.IsByRef)
                    sb.Append("ref ");

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
                sb.Append($"{(node.ContainingType != null ? GetTypeName(node.ContainingType) : "Unknown")}.");
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

        /// <summary>
        /// A type as a reader writes it in C#. Reflection's own Name spells a generic type List`1, which is
        /// neither C# nor safe in a Markdown code span.
        /// </summary>
        private static string GetTypeName(Type type, bool qualified = false)
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

            if (type.IsByRef)
            {
                return GetTypeName(type.GetElementType(), qualified);
            }

            if (type.IsArray)
            {
                return GetTypeName(type.GetElementType(), qualified) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            }

            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            string name = type.Name;
            int arityMarker = name.IndexOf('`');
            if (arityMarker >= 0)
                name = name.Substring(0, arityMarker);

            if (type.IsNested && type.DeclaringType != null && !type.IsGenericParameter)
            {
                name = GetTypeName(type.DeclaringType, qualified) + "." + name;
            }
            else if (qualified && !string.IsNullOrEmpty(type.Namespace))
            {
                name = type.Namespace + "." + name;
            }

            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                int inherited = type.IsNested && type.DeclaringType != null && type.DeclaringType.IsGenericType
                    ? type.DeclaringType.GetGenericArguments().Length
                    : 0;

                if (arguments.Length > inherited)
                {
                    name += "<" + string.Join(", ", arguments.Skip(inherited).Select(argument => GetTypeName(argument, qualified))) + ">";
                }
            }

            return name;
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
