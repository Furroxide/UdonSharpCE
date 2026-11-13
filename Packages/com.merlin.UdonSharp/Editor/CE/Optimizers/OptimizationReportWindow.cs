using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Editor window that displays optimization reports from the CE compilation pipeline.
    /// Shows what optimizations were applied, where, and their impact.
    /// </summary>
    internal class OptimizationReportWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private string _filterFile = "";
        private bool _showDetails = true;
        private Dictionary<string, bool> _fileFoldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> _optimizerFoldouts = new Dictionary<string, bool>();

        private enum GroupBy
        {
            File,
            Optimizer
        }

        private GroupBy _groupBy = GroupBy.File;

        public static void ShowWindow()
        {
            var window = GetWindow<OptimizationReportWindow>();
            window.titleContent = new GUIContent("CE Optimizations");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        // Copies of the optimization context taken on the main thread, and the only thing drawn from.
        //
        // The context is filled in by the U# compile, which runs on a worker task while the editor keeps
        // repainting. Enumerating its list and dictionary directly from OnGUI threw "Collection was
        // modified" whenever a repaint landed inside a compile, part-way through a layout group.
        private List<OptimizationEntry> _entries = new List<OptimizationEntry>();
        private List<KeyValuePair<string, StringLiteralInfo>> _internedStrings = new List<KeyValuePair<string, StringLiteralInfo>>();

        private void OnGUI()
        {
            // Only on Layout: the events that follow in the same pass have to see exactly the controls
            // Layout declared, so the data may not change between them.
            if (Event.current.type == EventType.Layout)
            {
                RefreshSnapshot(CEOptimizerRunner.GetContext());
            }

            DrawHeader();
            DrawFilters();
            DrawContent();
        }

        private void RefreshSnapshot(OptimizationContext context)
        {
            try
            {
                var entries = new List<OptimizationEntry>(context.GetEntries());
                var internedStrings = new List<KeyValuePair<string, StringLiteralInfo>>(context.InternedStrings);

                // A copy that raced an insert can hold a slot that was not written yet.
                entries.RemoveAll(entry => entry == null);
                internedStrings.RemoveAll(pair => pair.Key == null || pair.Value == null);

                _entries = entries;
                _internedStrings = internedStrings;
            }
            catch (System.Exception)
            {
                // The compile changed a collection while it was being copied. The previous snapshot is
                // still whole, so keep drawing that and take another one on the next pass.
                Repaint();
            }
        }

        private static Dictionary<string, int> CountByOptimizer(List<OptimizationEntry> entries)
        {
            var counts = new Dictionary<string, int>();

            foreach (var entry in entries)
            {
                string id = entry.OptimizerId ?? "";
                counts.TryGetValue(id, out int count);
                counts[id] = count + 1;
            }

            return counts;
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("CE Optimization Report", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Total Optimizations: {_entries.Count}");
            EditorGUILayout.LabelField($"Internable Strings: {_internedStrings.Count(s => s.Value.OccurrenceCount >= 2)}");
            EditorGUILayout.EndHorizontal();

            // Optimizer info
            var counts = CountByOptimizer(_entries);
            if (counts.Count > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("By Type:", EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                foreach (var (id, count) in counts)
                {
                    EditorGUILayout.LabelField($"{GetOptimizerShortName(id)}: {count}",
                        GUILayout.Width(100));
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField("Group By:", GUILayout.Width(60));
            _groupBy = (GroupBy)EditorGUILayout.EnumPopup(_groupBy, GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Filter:", GUILayout.Width(40));
            _filterFile = EditorGUILayout.TextField(_filterFile, GUILayout.Width(150));

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                _filterFile = "";
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawContent()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            var entries = _entries
                .Where(e => MatchesFilter(e))
                .ToList();

            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    _entries.Count == 0
                        ? "No optimizations were applied during the last compilation.\n\n" +
                          "This could mean:\n" +
                          "• No optimization opportunities were found\n" +
                          "• The code is already optimized\n" +
                          "• Optimizations are disabled"
                        : "No optimizations match the current filter.",
                    MessageType.Info);
            }
            else if (_groupBy == GroupBy.File)
            {
                DrawGroupedByFile(entries);
            }
            else
            {
                DrawGroupedByOptimizer(entries);
            }

            // Draw internable strings section
            DrawInternableStrings();

            EditorGUILayout.EndScrollView();
        }

        private void DrawGroupedByFile(List<OptimizationEntry> entries)
        {
            var byFile = entries.GroupBy(e => e.FilePath ?? "Unknown").OrderBy(g => g.Key);

            foreach (var group in byFile)
            {
                var fileName = Path.GetFileName(group.Key);
                var foldoutKey = group.Key;

                if (!_fileFoldouts.ContainsKey(foldoutKey))
                    _fileFoldouts[foldoutKey] = true;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                _fileFoldouts[foldoutKey] = EditorGUILayout.Foldout(
                    _fileFoldouts[foldoutKey],
                    $"{fileName} ({group.Count()} optimizations)",
                    true);

                if (_fileFoldouts[foldoutKey])
                {
                    EditorGUI.indentLevel++;

                    foreach (var entry in group.OrderBy(e => e.Line))
                    {
                        DrawEntry(entry);
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawGroupedByOptimizer(List<OptimizationEntry> entries)
        {
            var byOptimizer = entries.GroupBy(e => e.OptimizerId).OrderBy(g => g.Key);

            foreach (var group in byOptimizer)
            {
                var optimizerName = GetOptimizerFullName(group.Key);
                var foldoutKey = group.Key;

                if (!_optimizerFoldouts.ContainsKey(foldoutKey))
                    _optimizerFoldouts[foldoutKey] = true;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                _optimizerFoldouts[foldoutKey] = EditorGUILayout.Foldout(
                    _optimizerFoldouts[foldoutKey],
                    $"{optimizerName} ({group.Count()})",
                    true);

                if (_optimizerFoldouts[foldoutKey])
                {
                    EditorGUI.indentLevel++;

                    foreach (var entry in group.OrderBy(e => e.FilePath).ThenBy(e => e.Line))
                    {
                        DrawEntry(entry, showFile: true);
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawEntry(OptimizationEntry entry, bool showFile = false)
        {
            EditorGUILayout.BeginHorizontal();

            // Line number
            var lineStr = entry.Line > 0 ? $"L{entry.Line}" : "";
            EditorGUILayout.LabelField(lineStr, GUILayout.Width(40));

            // Description
            var desc = entry.Description;
            if (showFile && !string.IsNullOrEmpty(entry.FilePath))
            {
                desc = $"[{Path.GetFileName(entry.FilePath)}] {desc}";
            }

            EditorGUILayout.LabelField(desc, EditorStyles.wordWrappedLabel);

            // Jump to location button
            if (entry.Location != null && GUILayout.Button("→", GUILayout.Width(25)))
            {
                JumpToLocation(entry);
            }

            EditorGUILayout.EndHorizontal();

            // Show original/optimized code if available
            if (_showDetails && (!string.IsNullOrEmpty(entry.OriginalCode) || !string.IsNullOrEmpty(entry.OptimizedCode)))
            {
                EditorGUI.indentLevel++;
                if (!string.IsNullOrEmpty(entry.OriginalCode))
                {
                    EditorGUILayout.LabelField($"Before: {entry.OriginalCode}", EditorStyles.miniLabel);
                }
                if (!string.IsNullOrEmpty(entry.OptimizedCode))
                {
                    EditorGUILayout.LabelField($"After: {entry.OptimizedCode}", EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }
        }

        private void DrawInternableStrings()
        {
            var internableStrings = _internedStrings
                .Where(s => s.Value.OccurrenceCount >= 2)
                .OrderByDescending(s => s.Value.OccurrenceCount)
                .ToList();

            if (internableStrings.Count == 0)
                return;

            EditorGUILayout.Space(10);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField($"Internable Strings ({internableStrings.Count})", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "These string literals appear multiple times and could benefit from being declared as const fields.",
                MessageType.Info);

            foreach (var (content, info) in internableStrings.Take(20))
            {
                EditorGUILayout.BeginHorizontal();
                var displayContent = content.Length > 50 ? content.Substring(0, 47) + "..." : content;
                EditorGUILayout.LabelField($"\"{displayContent}\"", GUILayout.MinWidth(200));
                EditorGUILayout.LabelField($"{info.OccurrenceCount} occurrences", GUILayout.Width(100));
                EditorGUILayout.EndHorizontal();
            }

            if (internableStrings.Count > 20)
            {
                EditorGUILayout.LabelField($"... and {internableStrings.Count - 20} more", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private bool MatchesFilter(OptimizationEntry entry)
        {
            if (string.IsNullOrEmpty(_filterFile))
                return true;

            var filter = _filterFile.ToLowerInvariant();

            if (entry.FilePath?.ToLowerInvariant().Contains(filter) == true)
                return true;

            if (entry.Description?.ToLowerInvariant().Contains(filter) == true)
                return true;

            if (entry.OptimizerId?.ToLowerInvariant().Contains(filter) == true)
                return true;

            return false;
        }

        private void JumpToLocation(OptimizationEntry entry)
        {
            if (string.IsNullOrEmpty(entry.FilePath))
                return;

            var asset = AssetDatabase.LoadAssetAtPath<MonoScript>(entry.FilePath);
            if (asset != null)
            {
                AssetDatabase.OpenAsset(asset, entry.Line);
            }
        }

        private static string GetOptimizerShortName(string id)
        {
            switch (id)
            {
                case "CEOPT003": return "Unroll";
                case "CEOPT004": return "Inline";
                case "CEOPT005": return "Intern";
                case "CEOPT006": return "Logger";
                case "CEOPT007": return "Callback";
                case "CEOPT008": return "ExternCache";
                case "CEOPT009": return "CSE";
                case "CEOPT010": return "LICM";
                case "CEOPT011": return "LambdaInline";
                // Correctness-critical generators (CEGEN*), not optimizations.
                case "CEGEN001": return "SyncOnJoin";
                case "CEGEN002": return "Quantize";
                case "CEGEN004": return "Persistence";
                case "CEGEN008": return "SyncMode";
                case "CEGEN009": return "Rpc";
                case "CEASYNC001": return "Async";
                default: return id;
            }
        }

        private static string GetOptimizerFullName(string id)
        {
            switch (id)
            {
                case "CEOPT003": return "Small Loop Unrolling";
                case "CEOPT004": return "Tiny Method Inlining";
                case "CEOPT005": return "String Interning";
                case "CEOPT006": return "CELogger Transform";
                case "CEOPT007": return "Action to CECallback";
                case "CEOPT008": return "Extern Call Caching";
                case "CEOPT009": return "Common Subexpression Elimination";
                case "CEOPT010": return "Loop Invariant Code Motion";
                case "CEOPT011": return "Lambda Inlining";
                // Correctness-critical generators (CEGEN*), not optimizations.
                case "CEGEN001": return "SyncOnJoin Glue Generation";
                case "CEGEN002": return "Quantize Pre-Serialization";
                case "CEGEN004": return "Persistence Auto-Registration";
                case "CEGEN008": return "Behaviour Sync Mode Inference";
                case "CEGEN009": return "Rpc Network Event Emitter";
                case "CEASYNC001": return "Async Method Transform";
                default: return id;
            }
        }

        /// <summary>
        /// Generates a text report of all optimizations.
        /// </summary>
        public static string GenerateTextReport(OptimizationContext context)
        {
            var sb = new StringBuilder();

            sb.AppendLine("════════════════════════════════════════════════════════════════════");
            sb.AppendLine("                     CE Optimization Report                         ");
            sb.AppendLine("════════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            var counts = context.GetOptimizationCounts();
            if (counts.Count == 0)
            {
                sb.AppendLine("No optimizations were applied.");
                return sb.ToString();
            }

            sb.AppendLine("SUMMARY");
            sb.AppendLine("─────────────────────────────────────────────────────────────────────");
            sb.AppendLine($"Total Optimizations: {context.TotalOptimizations}");

            foreach (var (id, count) in counts.OrderBy(c => c.Key))
            {
                sb.AppendLine($"  ✓ {GetOptimizerFullName(id)}: {count}");
            }

            sb.AppendLine();
            sb.AppendLine("DETAILS BY FILE");
            sb.AppendLine("─────────────────────────────────────────────────────────────────────");

            var byFile = context.GetEntries()
                .GroupBy(e => e.FilePath ?? "Unknown")
                .OrderBy(g => g.Key);

            foreach (var group in byFile)
            {
                var fileName = Path.GetFileName(group.Key);
                sb.AppendLine();
                sb.AppendLine($"📄 {fileName}");

                foreach (var entry in group.OrderBy(e => e.Line))
                {
                    sb.AppendLine($"  Line {entry.Line}: {entry.Description}");
                    if (!string.IsNullOrEmpty(entry.OriginalCode))
                        sb.AppendLine($"    Before: {entry.OriginalCode}");
                    if (!string.IsNullOrEmpty(entry.OptimizedCode))
                        sb.AppendLine($"    After:  {entry.OptimizedCode}");
                }
            }

            // Internable strings
            var internableStrings = context.InternedStrings
                .Where(s => s.Value.OccurrenceCount >= 2)
                .OrderByDescending(s => s.Value.OccurrenceCount)
                .ToList();

            if (internableStrings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("INTERNABLE STRINGS");
                sb.AppendLine("─────────────────────────────────────────────────────────────────────");

                foreach (var (content, info) in internableStrings.Take(50))
                {
                    var displayContent = content.Length > 40 ? content.Substring(0, 37) + "..." : content;
                    sb.AppendLine($"  \"{displayContent}\" ({info.OccurrenceCount} occurrences)");
                }

                if (internableStrings.Count > 50)
                {
                    sb.AppendLine($"  ... and {internableStrings.Count - 50} more");
                }
            }

            sb.AppendLine();
            sb.AppendLine("════════════════════════════════════════════════════════════════════");

            return sb.ToString();
        }
    }
}

