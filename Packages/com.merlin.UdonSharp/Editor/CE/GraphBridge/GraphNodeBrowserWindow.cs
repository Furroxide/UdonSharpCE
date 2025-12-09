using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace UdonSharp.CE.Editor.GraphBridge
{
    /// <summary>
    /// Editor window for browsing, searching, and interacting with CE Graph Nodes.
    /// Displays all methods decorated with [GraphNode] in a hierarchical tree view.
    /// </summary>
    public class GraphNodeBrowserWindow : EditorWindow
    {
        #region Menu Item

        // Menu Guideline: Graph Bridge has 5 items → keep as submenu
        [MenuItem("Udon CE/Graph Bridge/Node Browser", false, 1501)]
        public static void ShowWindow()
        {
            var window = GetWindow<GraphNodeBrowserWindow>();
            window.titleContent = new GUIContent("CE Graph Node Browser");
            window.minSize = new Vector2(800, 500);
            window.Show();
        }

        #endregion

        #region State

        private NodeTreeView _treeView;
        private TreeViewState _treeViewState;
        private SearchField _searchField;
        private string _searchString = "";

        private NodeDefinition _selectedNode;
        private Vector2 _detailsScrollPos;

        // Layout
        private float _treeViewWidth = 350f;
        private bool _isResizing;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _subHeaderStyle;
        private GUIStyle _codeStyle;
        private bool _stylesInitialized;

        #endregion

        #region Unity Callbacks

        private void OnEnable()
        {
            _treeViewState = new TreeViewState();
            _searchField = new SearchField();
            _stylesInitialized = false;

            RefreshNodes();
        }

        private void OnGUI()
        {
            InitializeStyles();

            DrawToolbar();

            EditorGUILayout.BeginHorizontal();

            // Left panel: Tree view
            DrawTreeViewPanel();

            // Resizer
            DrawResizer();

            // Right panel: Details
            DrawDetailsPanel();

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Initialization

        private void InitializeStyles()
        {
            if (_stylesInitialized)
                return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14
            };

            _subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12
            };

            _codeStyle = new GUIStyle(EditorStyles.textArea)
            {
                font = Font.CreateDynamicFontFromOSFont("Consolas", 12),
                wordWrap = true
            };

            _stylesInitialized = true;
        }

        private void RefreshNodes()
        {
            var nodes = GraphNodeGenerator.GetNodeDefinitions();
            _treeView = new NodeTreeView(_treeViewState, nodes);
            _treeView.OnNodeSelected += OnNodeSelected;

            if (!string.IsNullOrEmpty(_searchString))
            {
                _treeView.searchString = _searchString;
            }
        }

        #endregion

        #region Drawing

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Refresh button
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                GraphNodeGenerator.InvalidateCache();
                RefreshNodes();
            }

            GUILayout.Space(10);

            // Search field
            EditorGUI.BeginChangeCheck();
            _searchString = _searchField.OnToolbarGUI(_searchString, GUILayout.Width(200));
            if (EditorGUI.EndChangeCheck())
            {
                _treeView.searchString = _searchString;
            }

            GUILayout.FlexibleSpace();

            // Stats
            var nodes = GraphNodeGenerator.GetNodeDefinitions();
            EditorGUILayout.LabelField($"{nodes.Count} nodes", EditorStyles.toolbarButton, GUILayout.Width(80));

            // Generate all button
            if (GUILayout.Button("Generate All", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                GraphNodeCodeGenerator.GenerateAllWrappers();
            }

            // Generate docs button
            if (GUILayout.Button("Generate Docs", EditorStyles.toolbarButton, GUILayout.Width(90)))
            {
                GraphNodeDocGenerator.GenerateAllDocs();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawTreeViewPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(_treeViewWidth));

            // Tree view
            Rect treeRect = GUILayoutUtility.GetRect(0, 10000, 0, 10000);
            _treeView?.OnGUI(treeRect);

            EditorGUILayout.EndVertical();
        }

        private void DrawResizer()
        {
            Rect resizerRect = GUILayoutUtility.GetRect(5f, 10000f, GUILayout.Width(5f));
            EditorGUIUtility.AddCursorRect(resizerRect, MouseCursor.ResizeHorizontal);

            if (Event.current.type == EventType.MouseDown && resizerRect.Contains(Event.current.mousePosition))
            {
                _isResizing = true;
            }

            if (_isResizing)
            {
                _treeViewWidth = Event.current.mousePosition.x;
                _treeViewWidth = Mathf.Clamp(_treeViewWidth, 200f, position.width - 300f);
                Repaint();
            }

            if (Event.current.type == EventType.MouseUp)
            {
                _isResizing = false;
            }

            // Draw resizer line
            EditorGUI.DrawRect(new Rect(resizerRect.x + 2, resizerRect.y, 1, resizerRect.height),
                new Color(0.5f, 0.5f, 0.5f));
        }

        private void DrawDetailsPanel()
        {
            EditorGUILayout.BeginVertical();

            if (_selectedNode == null)
            {
                DrawEmptyDetails();
            }
            else
            {
                DrawNodeDetails();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawEmptyDetails()
        {
            EditorGUILayout.Space(50);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.HelpBox(
                "Select a node from the tree to view its details.\n\n" +
                "Use the search bar to filter nodes by name or category.",
                MessageType.Info,
                true);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawNodeDetails()
        {
            _detailsScrollPos = EditorGUILayout.BeginScrollView(_detailsScrollPos);

            // Header
            EditorGUILayout.LabelField(_selectedNode.MenuPath, _headerStyle);
            EditorGUILayout.Space(5);

            // Method info
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string methodSignature = GetMethodSignature(_selectedNode);
            EditorGUILayout.LabelField("Method", _subHeaderStyle);
            EditorGUILayout.SelectableLabel(methodSignature, _codeStyle, GUILayout.Height(40));

            EditorGUILayout.Space(5);

            // Type info
            EditorGUILayout.LabelField("Containing Type", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_selectedNode.ContainingType?.FullName ?? "Unknown");

            EditorGUILayout.Space(5);

            // Node type
            EditorGUILayout.LabelField("Node Type", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_selectedNode.IsFlowNode ? "Flow Node (has execution ports)" : "Value Node (pure function)");

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Inputs
            if (_selectedNode.Inputs.Count > 0)
            {
                EditorGUILayout.LabelField("Inputs", _subHeaderStyle);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                foreach (var input in _selectedNode.Inputs)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(input.Name, GUILayout.Width(120));
                    EditorGUILayout.LabelField(GetTypeName(input.Type), EditorStyles.miniLabel);

                    if (input.DefaultValue != null)
                    {
                        EditorGUILayout.LabelField($"= {input.DefaultValue}", EditorStyles.miniLabel, GUILayout.Width(100));
                    }

                    if (input.Hidden)
                    {
                        EditorGUILayout.LabelField("[Hidden]", EditorStyles.miniLabel, GUILayout.Width(60));
                    }

                    EditorGUILayout.EndHorizontal();

                    if (!string.IsNullOrEmpty(input.Tooltip))
                    {
                        EditorGUILayout.LabelField($"  {input.Tooltip}", EditorStyles.wordWrappedMiniLabel);
                    }
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(5);

            // Outputs
            if (_selectedNode.Outputs.Count > 0)
            {
                EditorGUILayout.LabelField("Outputs", _subHeaderStyle);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                foreach (var output in _selectedNode.Outputs)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(output.Name, GUILayout.Width(120));
                    EditorGUILayout.LabelField(GetTypeName(output.Type), EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();

                    if (!string.IsNullOrEmpty(output.Tooltip))
                    {
                        EditorGUILayout.LabelField($"  {output.Tooltip}", EditorStyles.wordWrappedMiniLabel);
                    }
                }

                EditorGUILayout.EndVertical();
            }

            // Flow outputs
            if (_selectedNode.FlowOutputs.Count > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Flow Outputs", _subHeaderStyle);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                foreach (var flowOut in _selectedNode.FlowOutputs)
                {
                    EditorGUILayout.LabelField($"→ {flowOut}");
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(10);

            // Validation
            DrawValidation();

            EditorGUILayout.Space(10);

            // Actions
            DrawActions();

            EditorGUILayout.EndScrollView();
        }

        private void DrawValidation()
        {
            var errors = UdonGraphIntegration.ValidateNode(_selectedNode);

            if (errors.Count == 0)
            {
                EditorGUILayout.HelpBox("Node is valid for Udon compilation.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField("Validation Errors", _subHeaderStyle);
                foreach (var error in errors)
                {
                    EditorGUILayout.HelpBox(error, MessageType.Error);
                }
            }
        }

        private void DrawActions()
        {
            EditorGUILayout.LabelField("Actions", _subHeaderStyle);

            EditorGUILayout.BeginHorizontal();

            // Copy signature
            if (GUILayout.Button("Copy Signature", GUILayout.Width(120)))
            {
                string signature = GetMethodSignature(_selectedNode);
                EditorGUIUtility.systemCopyBuffer = signature;
                Debug.Log($"[GraphNodeBrowser] Copied: {signature}");
            }

            // Copy call
            if (GUILayout.Button("Copy Call", GUILayout.Width(100)))
            {
                string call = GetMethodCall(_selectedNode);
                EditorGUIUtility.systemCopyBuffer = call;
                Debug.Log($"[GraphNodeBrowser] Copied: {call}");
            }

            // Generate wrapper
            if (GUILayout.Button("Generate Wrapper", GUILayout.Width(120)))
            {
                GraphNodeCodeGenerator.GenerateWrapper(_selectedNode);
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Helpers

        private void OnNodeSelected(NodeDefinition node)
        {
            _selectedNode = node;
            Repaint();
        }

        private string GetMethodSignature(NodeDefinition node)
        {
            if (node.Method == null)
                return "Unknown";

            var sb = new StringBuilder();

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

                sb.Append(GetTypeName(param.ParameterType));
                sb.Append(" ");
                sb.Append(param.Name);
            }

            sb.Append(")");

            return sb.ToString();
        }

        private string GetMethodCall(NodeDefinition node)
        {
            if (node.Method == null)
                return "Unknown";

            var sb = new StringBuilder();

            // Type name for static methods
            if (node.Method.IsStatic)
            {
                sb.Append(node.ContainingType?.Name ?? "Unknown");
                sb.Append(".");
            }

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

                sb.Append(param.Name);
            }

            sb.Append(")");

            return sb.ToString();
        }

        private string GetTypeName(Type type)
        {
            if (type == null) return "void";

            // Handle common types
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(double)) return "double";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(object)) return "object";

            // Handle arrays
            if (type.IsArray)
            {
                return GetTypeName(type.GetElementType()) + "[]";
            }

            // Handle by-ref types (out/ref parameters)
            if (type.IsByRef)
            {
                return GetTypeName(type.GetElementType());
            }

            return type.Name;
        }

        #endregion
    }

    #region Tree View Implementation

    /// <summary>
    /// Tree view for displaying graph nodes hierarchically.
    /// </summary>
    internal class NodeTreeView : TreeView
    {
        private readonly List<NodeDefinition> _allNodes;
        private readonly Dictionary<int, NodeDefinition> _nodeMap = new Dictionary<int, NodeDefinition>();

        public event Action<NodeDefinition> OnNodeSelected;

        public NodeTreeView(TreeViewState state, List<NodeDefinition> nodes)
            : base(state)
        {
            _allNodes = nodes ?? new List<NodeDefinition>();
            showAlternatingRowBackgrounds = true;
            showBorder = true;
            Reload();
        }

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
            _nodeMap.Clear();

            if (_allNodes.Count == 0)
            {
                root.AddChild(new TreeViewItem { id = 1, depth = 0, displayName = "No nodes found" });
                return root;
            }

            // Build tree from menu paths
            var pathToItem = new Dictionary<string, TreeViewItem>();
            int nextId = 1;

            foreach (var node in _allNodes)
            {
                string path = node.MenuPath ?? "Uncategorized";
                string[] parts = path.Split('/');

                TreeViewItem parent = root;
                string currentPath = "";

                // Create folder items
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    currentPath += (i > 0 ? "/" : "") + parts[i];

                    if (!pathToItem.TryGetValue(currentPath, out TreeViewItem folderItem))
                    {
                        folderItem = new TreeViewItem
                        {
                            id = nextId++,
                            depth = i,
                            displayName = parts[i]
                        };
                        pathToItem[currentPath] = folderItem;
                        parent.AddChild(folderItem);
                    }

                    parent = folderItem;
                }

                // Create node item
                var nodeItem = new TreeViewItem
                {
                    id = nextId,
                    depth = parts.Length - 1,
                    displayName = parts[parts.Length - 1]
                };
                _nodeMap[nextId] = node;
                nextId++;

                parent.AddChild(nodeItem);
            }

            SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            base.SelectionChanged(selectedIds);

            if (selectedIds.Count > 0 && _nodeMap.TryGetValue(selectedIds[0], out NodeDefinition node))
            {
                OnNodeSelected?.Invoke(node);
            }
        }

        protected override bool DoesItemMatchSearch(TreeViewItem item, string search)
        {
            if (_nodeMap.TryGetValue(item.id, out NodeDefinition node))
            {
                // Search in menu path
                if (node.MenuPath?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Search in method name
                if (node.Method?.Name?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Search in type name
                if (node.ContainingType?.Name?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                return false;
            }

            // For folder items, check if display name matches
            return item.displayName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            base.RowGUI(args);

            // Add icon for nodes vs folders
            if (_nodeMap.ContainsKey(args.item.id))
            {
                var iconRect = new Rect(args.rowRect.x + GetContentIndent(args.item), args.rowRect.y, 16, 16);
                var node = _nodeMap[args.item.id];
                var icon = UdonGraphIntegration.GetNodeIcon(node);
                if (icon?.image != null)
                {
                    GUI.DrawTexture(iconRect, icon.image);
                }
            }
        }
    }

    #endregion
}

