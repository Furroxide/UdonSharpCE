using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace UdonSharp.Compiler
{
    // 'Assembly' alone binds to the UdonSharp.Compiler.Assembly namespace inside this namespace.
    using ReflectionAssembly = System.Reflection.Assembly;

    /// <summary>
    /// Registry for correctness-critical source generators.
    ///
    /// This is deliberately NOT <see cref="CompileTimeOptimizerRegistry"/>. The two concepts are not
    /// interchangeable:
    ///
    /// <list type="bullet">
    /// <item>A <b>generator</b> emits code the program is incorrect without. Failure is a compile
    /// <see cref="DiagnosticSeverity.Error"/> that aborts, so the previously-serialized program is left
    /// intact rather than being overwritten by a program missing its generated glue. Generators cannot be
    /// disabled.</item>
    /// <item>An <b>optimizer</b> makes a correct program faster. Failure is a
    /// <see cref="DiagnosticSeverity.Warning"/> and the compile continues with the untransformed tree.</item>
    /// </list>
    ///
    /// Conflating them is what let a single codegen exception silently strip every CE transform
    /// project-wide behind one console warning.
    /// </summary>
    internal static class SourceGeneratorRegistry
    {
        /// <summary>Runs a provider's generators over the whole tree set.</summary>
        internal delegate SourceGenerationResult GeneratorCallback((SyntaxTree tree, string filePath)[] syntaxTrees);

        private sealed class ProviderEntry
        {
            public string ProviderId;
            public int Order;
            public GeneratorCallback Callback;
        }

        private static readonly object Lock = new object();
        private static readonly List<ProviderEntry> Providers = new List<ProviderEntry>();

        /// <summary>Provider ids discovered by the domain scan, whether or not they managed to register.</summary>
        private static readonly List<string> ExpectedProviderIds = new List<string>();

        private static bool _domainScanned;

        internal static int ProviderCount
        {
            get { lock (Lock) return Providers.Count; }
        }

        internal static IReadOnlyList<string> RegisteredProviderIds
        {
            get { lock (Lock) return Providers.Select(p => p.ProviderId).ToArray(); }
        }

        internal static void RegisterProvider(string providerId, int order, GeneratorCallback callback)
        {
            if (string.IsNullOrEmpty(providerId) || callback == null)
                return;

            lock (Lock)
            {
                ProviderEntry existing = Providers.FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.Ordinal));
                if (existing != null)
                {
                    // Idempotent: a domain reload can re-run the static constructor.
                    existing.Order = order;
                    existing.Callback = callback;
                    return;
                }

                Providers.Add(new ProviderEntry { ProviderId = providerId, Order = order, Callback = callback });
            }
        }

        internal static void UnregisterProvider(string providerId)
        {
            lock (Lock)
            {
                Providers.RemoveAll(p => string.Equals(p.ProviderId, providerId, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Forces every declared provider to register, instead of hoping <c>[InitializeOnLoad]</c> ran in a
        /// helpful order. Reports USCE0002 when a provider's static constructor throws and USCE0003 when a
        /// declared provider is present in the domain but never registered.
        /// </summary>
        internal static void EnsureProvidersRegistered(CompilationContext context)
        {
            RunProviderClassConstructors(context);

            string[] expected;
            string[] registered;

            lock (Lock)
            {
                expected = ExpectedProviderIds.ToArray();
                registered = Providers.Select(p => p.ProviderId).ToArray();
            }

            foreach (string providerId in expected)
            {
                if (registered.Contains(providerId, StringComparer.Ordinal))
                    continue;

                context.AddDiagnostic(DiagnosticSeverity.Error, (Location)null,
                    $"USCE0003: source generator provider '{providerId}' is present in the domain but did not register. " +
                    "Correctness-critical code generation would be skipped, producing a program that compiles but does not work.");
            }
        }

        private static void RunProviderClassConstructors(CompilationContext context)
        {
            List<Type> providerTypes;

            lock (Lock)
            {
                if (_domainScanned)
                {
                    // Static constructors have already run once for this domain; nothing further to force.
                    return;
                }

                _domainScanned = true;
                ExpectedProviderIds.Clear();
                providerTypes = DiscoverProviderTypes();
            }

            foreach (Type providerType in providerTypes)
            {
                UdonSharpSourceGeneratorProviderAttribute attribute =
                    providerType.GetCustomAttribute<UdonSharpSourceGeneratorProviderAttribute>();

                if (attribute == null)
                    continue;

                lock (Lock)
                {
                    if (!ExpectedProviderIds.Contains(attribute.ProviderId, StringComparer.Ordinal))
                        ExpectedProviderIds.Add(attribute.ProviderId);
                }

                try
                {
                    RuntimeHelpers.RunClassConstructor(providerType.TypeHandle);
                }
                catch (Exception e)
                {
                    context.AddDiagnostic(DiagnosticSeverity.Error, (Location)null,
                        $"USCE0002: source generator provider '{attribute.ProviderId}' ({providerType.FullName}) " +
                        $"failed to initialize, so its code generation will not run: {e}");
                }
            }
        }

        private static List<Type> DiscoverProviderTypes()
        {
            var results = new List<Type>();

            foreach (ReflectionAssembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                // Cheap gate: one attribute lookup per assembly, full type scan only for marked assemblies.
                try
                {
                    if (assembly.GetCustomAttribute<UdonSharpSourceGeneratorAssemblyAttribute>() == null)
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type != null && type.GetCustomAttribute<UdonSharpSourceGeneratorProviderAttribute>() != null)
                        results.Add(type);
                }
            }

            return results;
        }

        /// <summary>
        /// Runs every registered provider in ascending (Order, ProviderId) order.
        ///
        /// There is deliberately no catch here. A generator exception must reach
        /// <c>UdonSharpCompilerV1.RunSourceGenerators</c>, which turns it into a compile error.
        /// </summary>
        internal static SourceGenerationResult RunGenerators((SyntaxTree tree, string filePath)[] syntaxTrees)
        {
            ProviderEntry[] providers;

            lock (Lock)
            {
                providers = Providers
                    .OrderBy(p => p.Order)
                    .ThenBy(p => p.ProviderId, StringComparer.Ordinal)
                    .ToArray();
            }

            SourceGenerationResult accumulated = new SourceGenerationResult(syntaxTrees);

            foreach (ProviderEntry provider in providers)
            {
                SourceGenerationResult result = provider.Callback(accumulated.Trees) ?? new SourceGenerationResult(accumulated.Trees);
                accumulated = SourceGenerationResult.Merge(accumulated, result);

                if (accumulated.Failed)
                    break;
            }

            return accumulated;
        }

        /// <summary>
        /// Test seam: registers or replaces a provider for the scope's lifetime and restores the previous
        /// state on dispose. Used to prove that a throwing generator fails the compile and that a
        /// suppressed generator trips the tripwire.
        /// </summary>
        internal static IDisposable OverrideForTests(string providerId, int order, GeneratorCallback callback)
        {
            ProviderEntry previous;

            lock (Lock)
            {
                previous = Providers.FirstOrDefault(p => string.Equals(p.ProviderId, providerId, StringComparison.Ordinal));
                Providers.RemoveAll(p => string.Equals(p.ProviderId, providerId, StringComparison.Ordinal));
                Providers.Add(new ProviderEntry { ProviderId = providerId, Order = order, Callback = callback });
            }

            return new ProviderOverrideScope(providerId, previous);
        }

        private sealed class ProviderOverrideScope : IDisposable
        {
            private readonly string _providerId;
            private readonly ProviderEntry _previous;
            private bool _disposed;

            public ProviderOverrideScope(string providerId, ProviderEntry previous)
            {
                _providerId = providerId;
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;

                lock (Lock)
                {
                    Providers.RemoveAll(p => string.Equals(p.ProviderId, _providerId, StringComparison.Ordinal));

                    if (_previous != null)
                        Providers.Add(_previous);
                }
            }
        }
    }
}
