using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace UdonSharp.Compiler
{
    /// <summary>
    /// Marks an assembly as containing <see cref="UdonSharpSourceGeneratorProviderAttribute"/> types.
    /// This is a cheap gate so the compiler only pays for a full <c>GetTypes()</c> scan on assemblies
    /// that actually declare generator providers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class UdonSharpSourceGeneratorAssemblyAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a static class as a source-generator provider.
    ///
    /// The compiler force-runs the marked type's static constructor at the start of every compile via
    /// <see cref="System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor"/>, so registration
    /// no longer depends on <c>[InitializeOnLoad]</c> ordering. The CLR guarantees a static constructor
    /// runs at most once, so this is safe to invoke on every compile.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class UdonSharpSourceGeneratorProviderAttribute : Attribute
    {
        public UdonSharpSourceGeneratorProviderAttribute(string providerId, int order = 0)
        {
            ProviderId = providerId;
            Order = order;
        }

        /// <summary>Stable identifier used in diagnostics when the provider fails to register.</summary>
        public string ProviderId { get; }

        /// <summary>Providers run in ascending (Order, ProviderId) order, never in registration order.</summary>
        public int Order { get; }
    }

    /// <summary>
    /// Whether a generator actually executed. Used by the tripwire to distinguish
    /// "ran and found nothing to do" from "never ran", which are very different failures.
    /// </summary>
    internal enum SourceGeneratorRunStatus
    {
        NotRun,
        Ran,
        Faulted,
    }

    /// <summary>Per-generator execution record for one compile.</summary>
    internal sealed class SourceGeneratorRunRecord
    {
        public SourceGeneratorRunRecord(string generatorId, SourceGeneratorRunStatus status, int filesInspected, int filesTransformed)
        {
            GeneratorId = generatorId;
            Status = status;
            FilesInspected = filesInspected;
            FilesTransformed = filesTransformed;
        }

        public string GeneratorId { get; }
        public SourceGeneratorRunStatus Status { get; }
        public int FilesInspected { get; }
        public int FilesTransformed { get; }
    }

    /// <summary>
    /// Declares that a generator MUST have run if any input source contains one of the trigger tokens.
    ///
    /// This catches the failure shape where a generator is present and registered but silently produced
    /// nothing -- a presence check alone cannot see that. It trips in two cases: the generator has no
    /// <see cref="SourceGeneratorRunStatus.Ran"/> record at all, or it ran and handled no file in the
    /// whole compile while a trigger token is written somewhere in it.
    ///
    /// The second case is what makes the tripwire worth having, and it is also the one that can fire on a
    /// correct project, so nothing about it is free of false positives by construction. This comment
    /// used to claim exactly that, on the grounds that the condition was "did not run at all" -- which
    /// stopped being true when the zero-transform arm was added, and the claim outlived it. Three things
    /// keep it honest, and each has to stay true: a token is only declared for a hard obligation; a token
    /// spelled "[X" counts only where that bracket opens an attribute list whose first attribute is named
    /// X or XAttribute, so neither an array index that starts with the same letters nor a different
    /// attribute that does ([XLogger]) is a use of it; and a generator
    /// that recognises its construct and correctly emits nothing reports that through
    /// <c>OptimizationContext.RecordObligationDischarged</c>, which the runner counts as handling the file.
    /// </summary>
    internal sealed class SourceGeneratorTripwire
    {
        public SourceGeneratorTripwire(string generatorId, string diagnosticId, params string[] triggerTokens)
        {
            GeneratorId = generatorId;
            DiagnosticId = diagnosticId;
            TriggerTokens = triggerTokens ?? Array.Empty<string>();
        }

        public string GeneratorId { get; }
        public string DiagnosticId { get; }

        /// <summary>Literal substrings scanned against each module's raw source text.</summary>
        public IReadOnlyList<string> TriggerTokens { get; }
    }

    /// <summary>A diagnostic raised by a source generator.</summary>
    internal sealed class SourceGenerationDiagnostic
    {
        public SourceGenerationDiagnostic(DiagnosticSeverity severity, string id, Location location, string message, string filePath = null)
        {
            Severity = severity;
            Id = id;
            Location = location;
            Message = message;
            FilePath = filePath;
        }

        public DiagnosticSeverity Severity { get; }
        public string Id { get; }

        /// <summary>
        /// SHOULD belong to one of the generator's *input* trees.
        /// <c>CompilationContext.TranslateLocationToFileName</c> matches a location's tree against
        /// <c>ModuleBindings</c>, which still holds the original trees at generator-failure time, so a
        /// location taken from a rewritten output tree resolves to "Unknown File(1,1)".
        /// </summary>
        public Location Location { get; }

        /// <summary>
        /// The file the generator was working on, when it is known.
        ///
        /// Generators are chained, so only the first one in the chain sees trees that are still in
        /// <c>ModuleBindings</c>; every later generator's <see cref="Location"/> points at a rewritten tree
        /// the compilation context has never seen. Carrying the path lets the compiler re-anchor the
        /// location onto the matching input tree instead of printing "Unknown File(1,1)".
        /// </summary>
        public string FilePath { get; }

        public string Message { get; }
    }

    /// <summary>Result of running one provider's generators over the full tree set.</summary>
    internal sealed class SourceGenerationResult
    {
        public SourceGenerationResult(
            (SyntaxTree tree, string filePath)[] trees,
            IReadOnlyList<SourceGenerationDiagnostic> diagnostics = null,
            IReadOnlyList<SourceGeneratorRunRecord> runRecords = null,
            IReadOnlyList<SourceGeneratorTripwire> tripwires = null,
            bool failed = false)
        {
            Trees = trees;
            Diagnostics = diagnostics ?? Array.Empty<SourceGenerationDiagnostic>();
            RunRecords = runRecords ?? Array.Empty<SourceGeneratorRunRecord>();
            Tripwires = tripwires ?? Array.Empty<SourceGeneratorTripwire>();
            Failed = failed;
        }

        public (SyntaxTree tree, string filePath)[] Trees { get; }
        public IReadOnlyList<SourceGenerationDiagnostic> Diagnostics { get; }
        public IReadOnlyList<SourceGeneratorRunRecord> RunRecords { get; }
        public IReadOnlyList<SourceGeneratorTripwire> Tripwires { get; }

        /// <summary>When true the compiler aborts without applying any generated trees.</summary>
        public bool Failed { get; }

        /// <summary>
        /// Test helper: pretend the provider was present but produced nothing. Used to prove the tripwire
        /// catches a silently-suppressed generator.
        /// </summary>
        public static SourceGenerationResult NotRun((SyntaxTree tree, string filePath)[] trees, IReadOnlyList<SourceGeneratorTripwire> tripwires = null)
        {
            return new SourceGenerationResult(trees, tripwires: tripwires);
        }

        internal static SourceGenerationResult Merge(SourceGenerationResult first, SourceGenerationResult second)
        {
            if (first == null) return second;
            if (second == null) return first;

            return new SourceGenerationResult(
                second.Trees,
                first.Diagnostics.Concat(second.Diagnostics).ToArray(),
                first.RunRecords.Concat(second.RunRecords).ToArray(),
                first.Tripwires.Concat(second.Tripwires).ToArray(),
                first.Failed || second.Failed);
        }
    }

    /// <summary>
    /// Thrown by a source generator to abort the compile with a clickable source location.
    /// Prefer this over a bare exception: a bare exception still fails the compile, but reports
    /// "Unknown File(1,1)" instead of pointing at the offending declaration.
    /// </summary>
    internal sealed class CodeGenerationException : Exception
    {
        public CodeGenerationException(string generatorId, Location location, string message, Exception inner = null, string filePath = null)
            : base(message, inner)
        {
            GeneratorId = generatorId;
            Location = location;
            FilePath = filePath;
        }

        public string GeneratorId { get; }

        /// <summary>Should come from an input tree -- see <see cref="SourceGenerationDiagnostic.Location"/>.</summary>
        public Location Location { get; }

        /// <summary>
        /// The file being generated over, when known. Generators throw before they know which module they
        /// belong to, so the runner fills this in -- see <see cref="SourceGenerationDiagnostic.FilePath"/>
        /// for why the location alone is not enough once generators are chained.
        /// </summary>
        public string FilePath { get; }
    }
}
