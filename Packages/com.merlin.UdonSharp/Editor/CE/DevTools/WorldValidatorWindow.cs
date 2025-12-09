using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UdonSharp.CE.Editor.DevTools.Validation;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools
{
    /// <summary>
    /// Editor window for validating UdonSharpBehaviour scenes for common issues.
    /// Provides visualization of validation results and auto-fix capabilities.
    /// </summary>
    public class WorldValidatorWindow : EditorWindow
    {
        #region Menu Item

        [MenuItem("Udon CE/Dev Tools/World Validator", false, 1402)]
        public static void ShowWindow()
        {
            var window = GetWindow<WorldValidatorWindow>();
            window.titleContent = new GUIContent("CE World Validator");
            window.minSize = new Vector2(650, 450);
            window.Show();
        }

        #endregion

        #region State

        private ValidationReport _report;
        private Vector2 _scrollPos;
        private string _filterCategory = "All";
        private ValidationSeverity _minSeverity = ValidationSeverity.Info;
        private string _searchFilter = "";

        // Cached data
        private List<string> _categories;
        private ValidationRunner _runner;

        // Styles
        private GUIStyle _headerStyle;
        private GUIStyle _errorBoxStyle;
        private GUIStyle _warningBoxStyle;
        private GUIStyle _infoBoxStyle;
        private bool _stylesInitialized;

        #endregion

        #region Unity Callbacks

        private void OnEnable()
        {
            _runner = new ValidationRunner();
            _categories = new List<string> { "All" };
            _categories.AddRange(_runner.GetCategories());
            _stylesInitialized = false;
        }

        private void OnGUI()
        {
            InitializeStyles();

            DrawToolbar();

            if (_report == null)
            {
                DrawEmptyState();
                return;
            }

            DrawSummary();

            EditorGUILayout.Space(5);

            DrawFilters();

            EditorGUILayout.Space(5);

            DrawIssuesList();

            EditorGUILayout.Space(5);

            DrawActions();
        }

        #endregion

        #region Drawing Methods

        private void InitializeStyles()
        {
            if (_stylesInitialized)
                return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14
            };

            _errorBoxStyle = new GUIStyle(EditorStyles.helpBox);
            _warningBoxStyle = new GUIStyle(EditorStyles.helpBox);
            _infoBoxStyle = new GUIStyle(EditorStyles.helpBox);

            _stylesInitialized = true;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Run All Checks", EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                RunValidation();
            }

            GUILayout.FlexibleSpace();

            if (_report != null)
            {
                // Status indicator
                Color statusColor = _report.AllPassed ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.9f, 0.2f, 0.2f);
                string statusText = _report.AllPassed ? "✓ All Passed" : $"✗ {_report.TotalErrors} Errors";

                GUI.color = statusColor;
                EditorGUILayout.LabelField(statusText, EditorStyles.toolbarButton, GUILayout.Width(100));
                GUI.color = Color.white;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEmptyState()
        {
            EditorGUILayout.Space(50);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(450));
            EditorGUILayout.HelpBox(
                "Click 'Run All Checks' to validate your world for common issues.\n\n" +
                "This tool checks for:\n" +
                "• GetComponent calls in Update methods\n" +
                "• Uninitialized synced arrays\n" +
                "• Invalid VRCPlayerApi usage\n" +
                "• LocalOnly network call violations\n" +
                "• Inefficient sync mode usage\n" +
                "• Bandwidth limit violations\n" +
                "• Persistence size limit issues",
                MessageType.Info);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSummary()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Error count
            GUI.color = _report.TotalErrors > 0 ? new Color(0.9f, 0.3f, 0.3f) : new Color(0.3f, 0.8f, 0.3f);
            EditorGUILayout.LabelField($"Errors: {_report.TotalErrors}", GUILayout.Width(80));

            // Warning count
            GUI.color = _report.TotalWarnings > 0 ? new Color(0.9f, 0.7f, 0.1f) : new Color(0.3f, 0.8f, 0.3f);
            EditorGUILayout.LabelField($"Warnings: {_report.TotalWarnings}", GUILayout.Width(100));

            GUI.color = Color.white;

            // Info count
            EditorGUILayout.LabelField($"Info: {_report.TotalInfo}", GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            // Validators run
            EditorGUILayout.LabelField($"Validators: {_report.ValidatorResults.Count}", GUILayout.Width(100));

            // Timestamp
            EditorGUILayout.LabelField($"Last run: {_report.Timestamp:HH:mm:ss}", GUILayout.Width(120));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));

            // Category dropdown
            int categoryIndex = _categories.IndexOf(_filterCategory);
            if (categoryIndex < 0) categoryIndex = 0;
            int newCategoryIndex = EditorGUILayout.Popup(categoryIndex, _categories.ToArray(), GUILayout.Width(120));
            _filterCategory = _categories[newCategoryIndex];

            GUILayout.Space(10);

            // Severity dropdown
            _minSeverity = (ValidationSeverity)EditorGUILayout.EnumPopup(_minSeverity, GUILayout.Width(80));

            GUILayout.Space(10);

            // Search
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(150));

            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawIssuesList()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            var filtered = _report.Issues
                .Where(i => _filterCategory == "All" || GetValidatorCategory(i.ValidatorName) == _filterCategory)
                .Where(i => i.Severity <= _minSeverity)
                .Where(i => string.IsNullOrEmpty(_searchFilter) ||
                           i.Message.ToLowerInvariant().Contains(_searchFilter.ToLowerInvariant()) ||
                           i.ValidatorName.ToLowerInvariant().Contains(_searchFilter.ToLowerInvariant()))
                .OrderBy(i => i.Severity)
                .ThenBy(i => i.ValidatorName);

            foreach (var issue in filtered)
            {
                DrawIssue(issue);
            }

            if (!filtered.Any())
            {
                EditorGUILayout.HelpBox(
                    _report.Issues.Count == 0 ? "No issues found! Your world looks good." : "No issues match the current filter.",
                    MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawIssue(ValidationIssue issue)
        {
            Color bgColor = issue.Severity switch
            {
                ValidationSeverity.Error => new Color(0.5f, 0.2f, 0.2f, 0.3f),
                ValidationSeverity.Warning => new Color(0.5f, 0.4f, 0.2f, 0.3f),
                _ => new Color(0.3f, 0.3f, 0.3f, 0.3f)
            };

            string icon = issue.Severity switch
            {
                ValidationSeverity.Error => "✗",
                ValidationSeverity.Warning => "⚠",
                _ => "ℹ"
            };

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header row
            EditorGUILayout.BeginHorizontal();

            // Icon and validator name
            Color severityColor = issue.Severity switch
            {
                ValidationSeverity.Error => new Color(0.9f, 0.3f, 0.3f),
                ValidationSeverity.Warning => new Color(0.9f, 0.7f, 0.1f),
                _ => new Color(0.6f, 0.6f, 0.6f)
            };

            GUI.color = severityColor;
            EditorGUILayout.LabelField($"{icon} [{issue.ValidatorName}]", EditorStyles.boldLabel, GUILayout.Width(200));
            GUI.color = Color.white;

            // Message
            EditorGUILayout.LabelField(issue.Message, EditorStyles.wordWrappedLabel);

            // Context button
            if (issue.Context != null)
            {
                if (GUILayout.Button("→", GUILayout.Width(25)))
                {
                    Selection.activeObject = issue.Context;
                    EditorGUIUtility.PingObject(issue.Context);
                }
            }

            // Auto-fix button
            if (issue.CanAutoFix)
            {
                if (GUILayout.Button("Fix", GUILayout.Width(40)))
                {
                    if (issue.AutoFix())
                    {
                        AssetDatabase.Refresh();
                        RunValidation(); // Re-run to update results
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            // Details
            if (!string.IsNullOrEmpty(issue.Details))
            {
                EditorGUILayout.LabelField(issue.Details, EditorStyles.wordWrappedMiniLabel);
            }

            // File/line link
            if (!string.IsNullOrEmpty(issue.FilePath))
            {
                EditorGUILayout.BeginHorizontal();

                string location = issue.LineNumber > 0
                    ? $"{Path.GetFileName(issue.FilePath)}:{issue.LineNumber}"
                    : Path.GetFileName(issue.FilePath);

                if (GUILayout.Button(location, EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                {
                    // Open file at line
                    string fullPath = issue.FilePath;
                    if (!Path.IsPathRooted(fullPath))
                    {
                        fullPath = Path.GetFullPath(fullPath);
                    }
                    UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(fullPath, issue.LineNumber);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawActions()
        {
            EditorGUILayout.BeginHorizontal();

            // Auto-fix all
            int fixableCount = _report?.AutoFixableCount ?? 0;
            GUI.enabled = fixableCount > 0;
            if (GUILayout.Button($"Fix All Auto-Fixable ({fixableCount})"))
            {
                int fixed_count = 0;
                foreach (var issue in _report.Issues.Where(i => i.CanAutoFix))
                {
                    try
                    {
                        if (issue.AutoFix())
                            fixed_count++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CE.Validator] Auto-fix failed: {ex.Message}");
                    }
                }

                if (fixed_count > 0)
                {
                    AssetDatabase.Refresh();
                    RunValidation();
                    Debug.Log($"[CE.Validator] Fixed {fixed_count} issues");
                }
            }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            // Export report
            if (GUILayout.Button("Export Report", GUILayout.Width(100)))
            {
                ExportReport();
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Validation

        private void RunValidation()
        {
            try
            {
                _report = _runner.RunAll();
                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CE.Validator] Validation failed: {ex.Message}\n{ex.StackTrace}");
                _report = null;
            }
        }

        private string GetValidatorCategory(string validatorName)
        {
            var info = _runner.GetValidatorInfo();
            var match = info.FirstOrDefault(v => v.Name == validatorName);
            return match.Category ?? "Unknown";
        }

        #endregion

        #region Export

        private void ExportReport()
        {
            if (_report == null)
                return;

            string path = EditorUtility.SaveFilePanel(
                "Save Validation Report",
                "",
                $"validation_report_{DateTime.Now:yyyyMMdd_HHmmss}.md",
                "md");

            if (string.IsNullOrEmpty(path))
                return;

            var sb = new StringBuilder();

            sb.AppendLine("# CE World Validation Report");
            sb.AppendLine();
            sb.AppendLine($"**Generated:** {_report.Timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"- **Status:** {(_report.AllPassed ? "PASSED ✓" : "FAILED ✗")}");
            sb.AppendLine($"- **Errors:** {_report.TotalErrors}");
            sb.AppendLine($"- **Warnings:** {_report.TotalWarnings}");
            sb.AppendLine($"- **Info:** {_report.TotalInfo}");
            sb.AppendLine($"- **Validators Run:** {_report.ValidatorResults.Count}");
            sb.AppendLine();

            // Group by severity
            var severityGroups = new[]
            {
                (ValidationSeverity.Error, "Errors"),
                (ValidationSeverity.Warning, "Warnings"),
                (ValidationSeverity.Info, "Info")
            };

            foreach (var (severity, title) in severityGroups)
            {
                var issues = _report.GetIssuesBySeverity(severity);
                if (issues.Count == 0)
                    continue;

                sb.AppendLine($"## {title}");
                sb.AppendLine();

                foreach (var issue in issues)
                {
                    string icon = severity switch
                    {
                        ValidationSeverity.Error => "❌",
                        ValidationSeverity.Warning => "⚠️",
                        _ => "ℹ️"
                    };

                    sb.AppendLine($"### {icon} [{issue.ValidatorName}] {issue.Message}");
                    sb.AppendLine();

                    if (!string.IsNullOrEmpty(issue.Details))
                    {
                        sb.AppendLine($"> {issue.Details}");
                        sb.AppendLine();
                    }

                    if (!string.IsNullOrEmpty(issue.FilePath))
                    {
                        string location = issue.LineNumber > 0
                            ? $"`{issue.FilePath}:{issue.LineNumber}`"
                            : $"`{issue.FilePath}`";
                        sb.AppendLine($"**Location:** {location}");
                        sb.AppendLine();
                    }

                    if (issue.RelatedType != null)
                    {
                        sb.AppendLine($"**Type:** `{issue.RelatedType.Name}`");
                        sb.AppendLine();
                    }
                }
            }

            // Validator summary
            sb.AppendLine("## Validators");
            sb.AppendLine();
            sb.AppendLine("| Validator | Status | Errors | Warnings |");
            sb.AppendLine("|-----------|--------|--------|----------|");

            foreach (var (name, result) in _report.ValidatorResults)
            {
                string status = result.Passed ? "✓ Pass" : "✗ Fail";
                sb.AppendLine($"| {name} | {status} | {result.ErrorCount} | {result.WarningCount} |");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("*Generated by CE World Validator*");

            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[CE.Validator] Report saved to {path}");

            // Open the file
            EditorUtility.RevealInFinder(path);
        }

        #endregion
    }
}

