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

            CSharpCompilation compilation = _semanticModelCompilation;

            // A caller that never went through SetSemanticModelTrees, or that asks about a tree the current
            // pass set does not contain, still gets a working model -- just one that cannot see other files.
            if (compilation == null || !compilation.ContainsSyntaxTree(tree))
                compilation = CreateSemanticCompilation(new[] { tree });

            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            _semanticModelLookup[tree] = model;
            return model;
        }

        /// <summary>
        /// Sets the syntax trees visible to semantic models created during an optimizer pass.
        /// This lets cross-file user types resolve while individual optimizers still receive one tree at a time.
        ///
        /// Every generator and optimizer pass calls this, and it used to build a fresh whole-project
        /// CSharpCompilation each time -- around twenty extra project-wide compilations per U# compile, each
        /// one re-resolving every metadata reference and rebuilding every declaration table. A pass changes
        /// only the trees it rewrote, so the compilation is now built once and patched with
        /// ReplaceSyntaxTree.
        /// </summary>
        public void SetSemanticModelTrees(IEnumerable<SyntaxTree> trees)
        {
            // Cached models are bound to the previous compilation object, so they go stale the moment any
            // tree in it is replaced.
            _semanticModelLookup.Clear();

            SyntaxTree[] newTrees = trees?.Where(tree => tree != null).ToArray();

            if (newTrees == null || newTrees.Length == 0)
            {
                _semanticModelTrees = newTrees;
                _semanticModelCompilation = null;
                return;
            }

            if (_semanticModelCompilation == null ||
                _semanticModelTrees == null ||
                _semanticModelTrees.Length != newTrees.Length)
            {
                _semanticModelTrees = newTrees;
                _semanticModelCompilation = CreateSemanticCompilation(newTrees);
                return;
            }

            CSharpCompilation compilation = _semanticModelCompilation;

            for (int i = 0; i < newTrees.Length; i++)
            {
                if (ReferenceEquals(_semanticModelTrees[i], newTrees[i]))
                    continue;

                // ReplaceSyntaxTree throws when the incoming tree is already in the compilation, which a
                // pass that reorders or duplicates trees would produce. Rebuilding is correct there, and
                // rare enough that the per-pass saving still holds.
                if (compilation.ContainsSyntaxTree(newTrees[i]))
                {
                    compilation = CreateSemanticCompilation(newTrees);
                    break;
                }

                compilation = compilation.ReplaceSyntaxTree(_semanticModelTrees[i], newTrees[i]);
            }

            _semanticModelTrees = newTrees;
            _semanticModelCompilation = compilation;
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

        /// <summary>
        /// Builds a compilation over the given trees using the same reference set UdonSharp itself compiles
        /// these files with.
        ///
        /// Referencing every AppDomain assembly, as this used to, pulled in Assembly-CSharp -- which already
        /// holds a compiled copy of every behaviour in <paramref name="trees"/>. Lookups on those user types
        /// then came back ambiguous, so a generator resolving a type through the semantic model found
        /// nothing and emitted nothing, silently. Sharing UdonSharp's reference set removes the duplicates
        /// and reuses its cached list instead of re-reading several hundred assemblies off disk per pass.
        /// </summary>
        private static CSharpCompilation CreateSemanticCompilation(IEnumerable<SyntaxTree> trees)
        {
            return CSharpCompilation.Create(
                $"UdonSharpCEOptimizerSemantic_{Guid.NewGuid():N}",
                trees,
                UdonSharp.Compiler.CompilationContext.GetMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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

