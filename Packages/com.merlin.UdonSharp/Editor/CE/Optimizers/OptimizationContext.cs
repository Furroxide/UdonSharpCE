using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Context passed to optimizers during the optimization phase.
    /// Tracks what optimizations were applied for reporting.
    /// </summary>
    internal class OptimizationContext
    {
        /// <summary>
        /// Whether optimizations are enabled globally.
        /// </summary>
        public bool OptimizationsEnabled { get; set; } = true;

        /// <summary>
        /// The path of the file currently being optimized.
        /// </summary>
        public string CurrentFilePath { get; set; }

        /// <summary>
        /// All recorded optimization entries.
        /// </summary>
        private readonly List<OptimizationEntry> _entries = new List<OptimizationEntry>();

        /// <summary>
        /// String literals encountered across all files for interning.
        /// Maps string content to first occurrence location.
        /// </summary>
        internal Dictionary<string, StringLiteralInfo> InternedStrings { get; } = new Dictionary<string, StringLiteralInfo>();

        private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelLookup = new Dictionary<SyntaxTree, SemanticModel>();
        private SyntaxTree[] _semanticModelTrees;
        private CSharpCompilation _semanticModelCompilation;
        private static readonly Lazy<MetadataReference[]> _optimizerMetadataReferences =
            new Lazy<MetadataReference[]>(BuildOptimizerMetadataReferences);

        /// <summary>
        /// Records an optimization that was applied.
        /// </summary>
        /// <param name="optimizerId">The ID of the optimizer that made the change.</param>
        /// <param name="description">Human-readable description of what was optimized.</param>
        /// <param name="location">Source location where optimization was applied.</param>
        /// <param name="originalCode">Original code before optimization.</param>
        /// <param name="optimizedCode">Code after optimization (if applicable).</param>
        public void RecordOptimization(
            string optimizerId,
            string description,
            Location location,
            string originalCode = null,
            string optimizedCode = null)
        {
            _entries.Add(new OptimizationEntry
            {
                OptimizerId = optimizerId,
                Description = description,
                FilePath = CurrentFilePath,
                Location = location,
                OriginalCode = originalCode,
                OptimizedCode = optimizedCode
            });
        }

        /// <summary>
        /// Gets or creates a semantic model for an optimizer input tree.
        /// This is used by syntax rewriters that need alias-resolved type information.
        /// </summary>
        public SemanticModel GetSemanticModel(SyntaxTree tree)
        {
            if (tree == null)
                return null;

            if (_semanticModelLookup.TryGetValue(tree, out SemanticModel cachedModel))
                return cachedModel;

            CSharpCompilation compilation = _semanticModelCompilation ?? CSharpCompilation.Create(
                $"UdonSharpCEOptimizerSemantic_{_semanticModelLookup.Count}",
                _semanticModelTrees ?? new[] { tree },
                _optimizerMetadataReferences.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            _semanticModelLookup[tree] = model;
            return model;
        }

        /// <summary>
        /// Sets the syntax trees visible to semantic models created during an optimizer pass.
        /// This lets cross-file user types resolve while individual optimizers still receive one tree at a time.
        /// </summary>
        public void SetSemanticModelTrees(IEnumerable<SyntaxTree> trees)
        {
            _semanticModelLookup.Clear();
            _semanticModelTrees = trees?.Where(tree => tree != null).ToArray();
            _semanticModelCompilation = _semanticModelTrees == null || _semanticModelTrees.Length == 0
                ? null
                : CSharpCompilation.Create(
                    $"UdonSharpCEOptimizerSemantic_{Guid.NewGuid():N}",
                    _semanticModelTrees,
                    _optimizerMetadataReferences.Value,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        /// <summary>
        /// Gets all recorded optimizations.
        /// </summary>
        public IReadOnlyList<OptimizationEntry> GetEntries() => _entries;

        /// <summary>
        /// Gets all recorded optimizations for a specific file.
        /// </summary>
        public IEnumerable<OptimizationEntry> GetEntriesForFile(string filePath)
        {
            foreach (var entry in _entries)
            {
                if (entry.FilePath == filePath)
                    yield return entry;
            }
        }

        /// <summary>
        /// Gets the total count of optimizations applied.
        /// </summary>
        public int TotalOptimizations => _entries.Count;

        /// <summary>
        /// Gets optimization count by optimizer ID.
        /// </summary>
        public Dictionary<string, int> GetOptimizationCounts()
        {
            var counts = new Dictionary<string, int>();
            foreach (var entry in _entries)
            {
                if (counts.TryGetValue(entry.OptimizerId, out int count))
                    counts[entry.OptimizerId] = count + 1;
                else
                    counts[entry.OptimizerId] = 1;
            }
            return counts;
        }

        /// <summary>
        /// Clears all recorded entries. Used between compilations.
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
            InternedStrings.Clear();
            _semanticModelLookup.Clear();
            _semanticModelTrees = null;
            _semanticModelCompilation = null;
        }

        private static MetadataReference[] BuildOptimizerMetadataReferences()
        {
            var references = new List<MetadataReference>();
            foreach (string assemblyPath in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                         .Select(assembly => assembly.Location)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(assemblyPath));
                }
                catch
                {
                    // Ignore transient or inaccessible editor assemblies; alias resolution only
                    // needs the references that can be loaded successfully.
                }
            }

            return references.ToArray();
        }
    }

    /// <summary>
    /// A single optimization entry recording what was changed.
    /// </summary>
    internal class OptimizationEntry
    {
        /// <summary>
        /// ID of the optimizer that made this change.
        /// </summary>
        public string OptimizerId { get; set; }

        /// <summary>
        /// Human-readable description of the optimization.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// File path where the optimization was applied.
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Source location of the optimization.
        /// </summary>
        public Location Location { get; set; }

        /// <summary>
        /// Original code before optimization (optional).
        /// </summary>
        public string OriginalCode { get; set; }

        /// <summary>
        /// Optimized code after transformation (optional).
        /// </summary>
        public string OptimizedCode { get; set; }

        /// <summary>
        /// Gets the line number from the location.
        /// </summary>
        public int Line => (Location?.GetLineSpan().StartLinePosition.Line ?? 0) + 1;
    }

    /// <summary>
    /// Information about an interned string literal.
    /// </summary>
    internal class StringLiteralInfo
    {
        /// <summary>
        /// The string content.
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Generated field name for the interned string.
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>
        /// Number of occurrences across all files.
        /// </summary>
        public int OccurrenceCount { get; set; }

        /// <summary>
        /// File path of the first occurrence.
        /// </summary>
        public string FirstOccurrenceFile { get; set; }
    }
}

