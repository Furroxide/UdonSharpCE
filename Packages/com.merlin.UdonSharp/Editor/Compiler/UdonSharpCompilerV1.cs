
// #define SINGLE_THREAD_BUILD

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using UdonSharp.Compiler.Assembly;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Emit;
using UdonSharp.Compiler.Symbols;
using UdonSharp.Compiler.Udon;
using UdonSharp.Core;
using UdonSharp.Internal;
using UdonSharp.Lib.Internal;
using UdonSharp.Serialization;
using UdonSharpEditor;
using UnityEditor;
using VRC.SDK3.Network;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using Debug = UnityEngine.Debug;

namespace UdonSharp.Compiler
{
    using SyntaxTree = Microsoft.CodeAnalysis.SyntaxTree;

    public class UdonSharpCompileOptions
    {
        public bool IsEditorBuild { get; set; } = true;
        // public bool BuildDebugInfo { get; set; } = true;
        public bool DisableLogging { get; set; } = false;
        public bool ConcurrentBuild { get; set; } = true;
        
        /// <summary>
        /// Reserved for the assembly-level optimizer (<c>UdonSharp.Compiler.Optimization.AssemblyOptimizer</c>).
        /// Nothing reads this yet: the optimizer has no call site, so the setting has no effect either way.
        ///
        /// It used to be documented as "enabled by default" and defaulted to <c>true</c>, which was wrong
        /// twice over -- it claimed an optimization that never ran, and it meant that adding the two-line
        /// hook would have switched the passes on for every project at once. Those passes are not sound
        /// (see the type comment on <c>AssemblyOptimizer</c>), so the default is off and must stay off
        /// until they are.
        /// </summary>
        public bool EnableAssemblyOptimization { get; set; } = false;

        /// <summary>
        /// Optional set of pass names to disable if the assembly-level optimizer is ever run. Unused today,
        /// for the same reason as <see cref="EnableAssemblyOptimization"/>.
        /// </summary>
        public HashSet<string> DisabledOptimizationPasses { get; set; } = new HashSet<string>();
    }
    
    [InitializeOnLoad]
    public class UdonSharpCompilerV1
    {
        private static int _assemblyCounter;
        private const int MAX_PARALLELISM = 6;
        public const string UdonSharpAssemblyNamePrefix = "UdonSharpRoslynCompileAssembly";

        private class CompileJob
        {
            public Task Task { get; set; }
            public CompilationContext Context { get; set; }
            public UdonSharpCompileOptions CompileOptions { get; set; }
            public Stopwatch CompileTimer { get; set; }
        }
        
        private static CompileJob CurrentJob { get; set; }
        private static bool _compileQueued;
        private static UdonSharpCompileOptions _queuedOptions;

        static UdonSharpCompilerV1()
        {
            EditorApplication.update += EditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayStateChanged;
        }

        private static void OnPlayStateChanged(PlayModeStateChange stateChange)
        {
            if (stateChange == PlayModeStateChange.ExitingEditMode || 
                stateChange == PlayModeStateChange.ExitingPlayMode)
                WaitForCompile();
        }

        private static void EditorUpdate()
        {
            TickCompile();
        }

        private static void TickCompile()
        {
            if (CurrentJob == null) return;
            
            if (!CurrentJob.Task.IsCompleted)
            {
                CompilationContext.CompilePhase currentPhase = CurrentJob.Context.CurrentPhase;
                float phaseProgress = CurrentJob.Context.PhaseProgress;

                float totalProgress = (phaseProgress / (int) CompilationContext.CompilePhase.Count) +
                                      ((int) currentPhase / (float)(int)CompilationContext.CompilePhase.Count);
                
                UdonSharpUtils.ShowAsyncProgressBar($"U#: {currentPhase}", totalProgress);
                return;
            }

            if (!CurrentJob.CompileOptions.DisableLogging)
            {
                foreach (var diagnostic in CurrentJob.Context.Diagnostics)
                {
                    string filePath = "";
                    if (diagnostic.Location != null)
                        filePath = CurrentJob.Context.TranslateLocationToFileName(diagnostic.Location);
                    LinePosition? linePosition = diagnostic.Location?.GetLineSpan().StartLinePosition;

                    int line = (linePosition?.Line ?? 0) + 1;
                    int character = (linePosition?.Character ?? 0) + 1;

                    string fileStr = $"{filePath ?? "Unknown File"}({line},{character})";

                    string logStr = $"{fileStr}: {diagnostic.Message}";

                    // CE-owned diagnostics carry a CE diagnostic code, so tag them [UdonSharpCE] to keep
                    // fork-specific findings distinguishable from upstream UdonSharp ones in the console.
                    bool isCEDiagnostic = UdonSharpCEUtils.IsCEDiagnostic(diagnostic.Message);

                    switch (diagnostic.Severity)
                    {
                        case DiagnosticSeverity.Error:
                            if (isCEDiagnostic)
                                UdonSharpCEUtils.LogBuildError(diagnostic.Message, filePath, line, character);
                            else
                                UdonSharpUtils.LogBuildError(diagnostic.Message, filePath, line, character);
                            break;
                        case DiagnosticSeverity.Warning:
                            if (isCEDiagnostic)
                                UdonSharpCEUtils.LogWarning(logStr);
                            else
                                UdonSharpUtils.LogWarning(logStr);
                            break;
                        case DiagnosticSeverity.Log:
                            if (isCEDiagnostic)
                                UdonSharpCEUtils.Log(logStr);
                            else
                                UdonSharpUtils.Log(logStr);
                            break;
                    }
                }
            }
            
            // Translate the diagnostic types and apply them to the cache, todo: consider merging the two structures
            UdonSharpEditorCache.CompileDiagnostic[] diagnostics = new UdonSharpEditorCache.CompileDiagnostic[CurrentJob.Context.Diagnostics.Count];
            CompilationContext.CompileDiagnostic[] compileDiagnostics = CurrentJob.Context.Diagnostics.ToArray();

            for (int i = 0; i < diagnostics.Length; ++i)
            {
                LinePosition diagLine = compileDiagnostics[i]?.Location?.GetLineSpan().StartLinePosition ?? LinePosition.Zero;
                
                diagnostics[i] = new UdonSharpEditorCache.CompileDiagnostic()
                {
                    severity = compileDiagnostics[i].Severity,
                    message = compileDiagnostics[i].Message,
                    file = CurrentJob.Context.TranslateLocationToFileName(compileDiagnostics[i].Location) ?? "",
                    line = diagLine.Line,
                    character = diagLine.Character,
                };
            }

            UdonSharpEditorCache.Instance.LastCompileDiagnostics = diagnostics;
            
            if (CurrentJob.Task.IsFaulted)
            {
                UdonSharpUtils.LogError("internal compiler error, dumping exceptions. Please report to Merlin");

                if (CurrentJob.Task.Exception != null)
                {
                    foreach (Exception innerException in CurrentJob.Task.Exception.InnerExceptions)
                    {
                        UdonSharpUtils.LogError(innerException);
                    }
                }

                CleanupCompile();
                return;
            }

            if (CurrentJob.Context.ErrorCount > 0)
            {
                CleanupCompile();
                return;
            }
                
            foreach (ModuleBinding rootBinding in CurrentJob.Context.ModuleBindings)
            {
                if (rootBinding.programAsset == null) 
                    continue;
                
                rootBinding.programAsset.ApplyProgram();
                
                UdonSharpEditorCache.Instance.SetUASMStr(rootBinding.programAsset, rootBinding.assembly);
                
                rootBinding.programAsset.CompiledVersion = UdonSharpProgramVersion.CurrentVersion;
                EditorUtility.SetDirty(rootBinding.programAsset);
            }
            
            try
            {
                UdonSharpEditorCache.Instance.RehashAllScripts();
                UdonSharpEditorManager.RunPostBuildSceneFixup();
            }
            catch (Exception e)
            {
                UdonSharpUtils.LogError($"Exception while running post build fixup:\n{e}");
                CleanupCompile();
                return;
            }

            UdonSharpEditorCache.Instance.LastBuildType = CurrentJob.CompileOptions.IsEditorBuild
                ? UdonSharpEditorCache.DebugInfoType.Editor
                : UdonSharpEditorCache.DebugInfoType.Client;
            
            int scriptCount = CurrentJob.Context.ModuleBindings.Count(e => e.programAsset != null);
            UdonSharpUtils.Log($"Compile of {scriptCount} script{(scriptCount != 1 ? "s" : "")} finished in {CurrentJob.CompileTimer.Elapsed:mm\\:ss\\.fff}");
            
            CleanupCompile();

            if (_compileQueued)
            {
                Compile(_queuedOptions);
                _compileQueued = false;
                _queuedOptions = null;
            }
        }

        private static void BuildClassSerializationInfo()
        {
            // Build serialization info
            UdonSharpEditorCache.Instance.ClearSerializationInfos();

            foreach (TypeSymbol referencedUserType in CurrentJob.Context.GetReferencedUserClasses())
            {
                System.Collections.Generic.List<UdonSharpEditorCache.UdonFieldInfo> fieldInfos = new System.Collections.Generic.List<UdonSharpEditorCache.UdonFieldInfo>();

                foreach (FieldSymbol fieldSymbol in referencedUserType.FieldSymbols)
                {
                    if (!TypeSymbol.TryGetSystemType(fieldSymbol.Type.RoslynSymbol, out Type fieldType))
                    {
                        UdonSharpUtils.LogWarning($"Could not resolve field type {fieldSymbol.Type.RoslynSymbol} on field {fieldSymbol}");
                        continue;
                    }
                    
                    if (fieldSymbol.IsStatic || fieldSymbol.IsConst)
                        continue;
                    
                    fieldInfos.Add(new UdonSharpEditorCache.UdonFieldInfo()
                    {
                        fieldName = fieldSymbol.Name,
                        fieldType = fieldType,
                        fieldIndex = referencedUserType.GetUserFieldIndex(fieldSymbol),
                        isStrongBoxed = UdonSharpUtils.IsStrongBoxedType(fieldSymbol.Type.UdonType.SystemType),
                    });
                }
                
                if (TypeSymbol.TryGetSystemType(referencedUserType.RoslynSymbol, out Type userType))
                {
                    UdonSharpEditorCache.Instance.SetSerializationInfo(userType, new UdonSharpEditorCache.UdonClassInfo() { classType = userType, fields = fieldInfos.ToArray() });
                }
            }
        }

        internal static void WaitForCompile()
        {
            if (CurrentJob == null) return;
            
            if (!CurrentJob.Task.IsCompleted)
                CurrentJob.Task.Wait();
            
            TickCompile();
        }

        private static void CleanupCompile()
        {
            UdonSharpUtils.ClearAsyncProgressBar();
            
            EditorApplication.UnlockReloadAssemblies();

            CurrentJob = null;
            
            // Clear serialization caches to ensure field layouts match the newly compiled programs
            UdonVariableStorageInterface.ClearCache();
            UdonSharpBehaviourFormatterEmitter.ClearCache();
        }

        private static void PrintStageTime(string stageName, Stopwatch stopwatch)
        {
            // Debug.Log($"{stageName}: {stopwatch.Elapsed.TotalSeconds * 1000.0}ms");
        }

        [PublicAPI]
        public static void CompileSync(UdonSharpCompileOptions options = null)
        {
            WaitForCompile();
            Compile(options);
            WaitForCompile();
        }

        /// <summary>
        /// Info that we need to pass from the main thread because the Unity APIs to access the info don't work off the main thread.
        /// </summary>
        private class ProgramAssetInfo
        {
            public UdonSharpProgramAsset programAsset;
            public Type scriptClass;
        }

        [PublicAPI]
        public static void Compile(UdonSharpCompileOptions options = null)
        {
            if (options == null)
                options = new UdonSharpCompileOptions();

            if (UdonSharpUtils.DoesUnityProjectHaveCompileErrors())
            {
                UdonSharpUtils.LogError("All Unity C# compiler errors must be resolved before running an UdonSharp compile.");
                return;
            }
            
            if (CurrentJob != null)
            {
                _compileQueued = true;
                _queuedOptions = options;
                return;
            }
            
            Localization.Loc.InitLocalization();

            UdonSharpProgramAsset[] allPrograms = UdonSharpProgramAsset.GetAllUdonSharpPrograms();

            bool hasError = !ValidateProgramAssetCollisions(allPrograms);
            
            Dictionary<string, ProgramAssetInfo> rootProgramLookup = new Dictionary<string, ProgramAssetInfo>();
            foreach (UdonSharpProgramAsset udonSharpProgram in allPrograms)
            {
                if (udonSharpProgram.sourceCsScript == null)
                {
                    UdonSharpUtils.LogError($"Source C# script on {udonSharpProgram} is null", udonSharpProgram);
                    hasError = true;
                    continue;
                }

                string assetPath = AssetDatabase.GetAssetPath(udonSharpProgram.sourceCsScript);
                
                if (string.IsNullOrEmpty(assetPath))
                {
                    UdonSharpUtils.LogError($"Source C# script on {udonSharpProgram} is null", udonSharpProgram);
                    hasError = true;
                    continue;
                }
                
                if (hasError)
                    continue;
                
                rootProgramLookup.Add(assetPath.Replace('\\', '/'), new ProgramAssetInfo() { programAsset = udonSharpProgram ? udonSharpProgram : null, scriptClass = udonSharpProgram != null ? udonSharpProgram.GetClass() : null });
            }

            if (hasError)
            {
                UdonSharpEditorCache.Instance.LastCompileDiagnostics = new []
                    {
                        new UdonSharpEditorCache.CompileDiagnostic()
                        {
                            file = "",
                            message = "Compile validation failed, check console output for details.",
                            severity = DiagnosticSeverity.Error,
                        } 
                    };
                
                return;
            }
            
            // var allSourcePaths = new HashSet<string>(UdonSharpProgramAsset.GetAllUdonSharpPrograms().Where(e => e.isV1Root).Select(e => AssetDatabase.GetAssetPath(e.sourceCsScript).Replace('\\', '/')));
            HashSet<string> allSourcePaths = new HashSet<string>(CompilationContext.GetAllFilteredSourcePaths(options.IsEditorBuild));

            if (!ValidateUdonSharpBehaviours(allPrograms, allSourcePaths))
                return;

            CompilationContext compilationContext = new CompilationContext(options);

            EditorApplication.LockReloadAssemblies();
            
            CompilerUdonInterface.AssemblyCacheInit();

            IEnumerable<CompilationContext.ScriptAssembly> scriptAssemblies = CompilationContext.GetBuildAssemblies(options.IsEditorBuild);
            
            Task compileTask = new Task(() => Compile(compilationContext, rootProgramLookup, scriptAssemblies));
            CurrentJob = new CompileJob() { Context = compilationContext, Task = compileTask, CompileTimer = Stopwatch.StartNew(), CompileOptions = options };
            
            compileTask.Start();
        }

        private static bool ValidateProgramAssetCollisions(UdonSharpProgramAsset[] allProgramAssets)
        {
            Dictionary<MonoScript, System.Collections.Generic.List<UdonSharpProgramAsset>> scriptToAssetMap = new Dictionary<MonoScript, System.Collections.Generic.List<UdonSharpProgramAsset>>();

            foreach (UdonSharpProgramAsset programAsset in allProgramAssets)
            {
                if (programAsset == null || programAsset.sourceCsScript == null)
                    continue;

                // Add program asset to map to check if there are any duplicate program assets that point to the same script
                if (!scriptToAssetMap.TryGetValue(programAsset.sourceCsScript, out var programAssetList))
                {
                    programAssetList = new System.Collections.Generic.List<UdonSharpProgramAsset>();
                    scriptToAssetMap.Add(programAsset.sourceCsScript, programAssetList);
                }

                programAssetList.Add(programAsset);
            }

            int errorCount = 0;
            
            foreach (var scriptAssetMapping in scriptToAssetMap)
            {
                if (scriptAssetMapping.Value.Count > 1)
                {
                    UdonSharpUtils.LogError($"Script {Path.GetFileName(AssetDatabase.GetAssetPath(scriptAssetMapping.Key))} is referenced by {scriptAssetMapping.Value.Count} UdonSharpProgramAssets, scripts can only be referenced by 1 program asset.\n" +
                                            "Referenced program assets:\n" +
                                            string.Join(",\n", scriptAssetMapping.Value.Select(AssetDatabase.GetAssetPath)));

                    errorCount++;
                }
            }

            return errorCount == 0;
        }

        private static bool ValidateUdonSharpBehaviours(UdonSharpProgramAsset[] allProgramAssets, HashSet<string> allSourcePaths)
        {
            bool succeeded = true;
            
            foreach (var programAsset in allProgramAssets)
            {
                if (programAsset.sourceCsScript == null)
                    continue;

                string sourcePath = AssetDatabase.GetAssetPath(programAsset.sourceCsScript);
                
                if (string.IsNullOrEmpty(sourcePath))
                    continue;

                if (!allSourcePaths.Contains(sourcePath))
                {
                    succeeded = false;
                    UdonSharpUtils.LogError($"Script '{sourcePath}' does not belong to a U# assembly, have you made a U# assembly definition for the assembly the script is a part of?", programAsset.sourceCsScript);
                }
            }

            return succeeded;
        }

        private static void Compile(CompilationContext compilationContext, IReadOnlyDictionary<string, ProgramAssetInfo> rootProgramLookup, IEnumerable<CompilationContext.ScriptAssembly> allScriptAssemblies)
        {
            Stopwatch setupTimer = Stopwatch.StartNew();
            
            CompilerUdonInterface.CacheInit();
            
            compilationContext.CurrentPhase = CompilationContext.CompilePhase.Setup;
            ModuleBinding[] syntaxTrees = compilationContext.LoadSyntaxTreesAndCreateModules(allScriptAssemblies);

            // Phase A: correctness-critical source generation. Any failure here is a compile Error, which
            // aborts before ApplyProgram so the previously-serialized program is left intact rather than
            // being overwritten by one that is missing its generated glue.
            Stopwatch generatorTimer = Stopwatch.StartNew();
            syntaxTrees = RunSourceGenerators(compilationContext, syntaxTrees);
            PrintStageTime("CE Source Generators", generatorTimer);

            if (compilationContext.ErrorCount > 0)
                return;

            // Phase B: optional optimizations. Failure is a Warning; the generated glue is already safely
            // in the tree at this point, so continuing with an untransformed tree is correct.
            Stopwatch optimizerTimer = Stopwatch.StartNew();
            syntaxTrees = RunCompileTimeOptimizers(compilationContext, syntaxTrees);
            PrintStageTime("CE Optimizers", optimizerTimer);

            if (compilationContext.ErrorCount > 0)
                return;

            foreach (ModuleBinding binding in syntaxTrees)
            {
                foreach (Diagnostic diag in binding.tree.GetDiagnostics())
                {
                    if (diag.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Error) continue;
                    
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, diag.Location, $"{diag.Severity.ToString().ToLower()} {diag.Id}: {diag.GetMessage()}");
                }
            }

            if (compilationContext.ErrorCount > 0)
                return;

            System.Collections.Generic.List<ModuleBinding> rootTrees = new System.Collections.Generic.List<ModuleBinding>();
            
            foreach (ModuleBinding treeBinding in syntaxTrees)
            {
                if (rootProgramLookup.ContainsKey(treeBinding.filePath))
                {
                    ProgramAssetInfo info = rootProgramLookup[treeBinding.filePath];
                    treeBinding.programAsset = info.programAsset;
                    treeBinding.programClass = info.scriptClass;
                    // ReSharper disable once Unity.NoNullPropagation
                    treeBinding.programScript = treeBinding?.programAsset?.sourceCsScript;
                    
                    rootTrees.Add(treeBinding);
                }
            }
            
            PrintStageTime("U# Setup", setupTimer);
            
            Stopwatch roslynCompileTimer = Stopwatch.StartNew();

            compilationContext.CurrentPhase = CompilationContext.CompilePhase.RoslynCompile;
            
            // Run compilation for the semantic views
            CSharpCompilation compilation = CSharpCompilation.Create(
                $"{UdonSharpAssemblyNamePrefix}{_assemblyCounter++}",
                syntaxTrees.Select(e => e.tree),
                CompilationContext.GetMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: compilationContext.Options.ConcurrentBuild));

            PrintStageTime("Roslyn Compile", roslynCompileTimer);

            compilationContext.RoslynCompilation = compilation;

            byte[] builtAssembly = null;
            
            Stopwatch roslynEmitTimer = Stopwatch.StartNew();
            
            using (MemoryStream memoryStream = new MemoryStream())
            {
                EmitResult emitResult = compilation.Emit(memoryStream);
                if (emitResult.Success)
                {
                    builtAssembly = memoryStream.ToArray();
                }
                else
                {
                    foreach (Diagnostic diag in emitResult.Diagnostics)
                    {
                        if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                        {
                            compilationContext.AddDiagnostic(DiagnosticSeverity.Error, diag.Location, $"{diag.Severity.ToString().ToLower()} {diag.Id}: {diag.GetMessage()}");
                        }
                    }
                }
            }
            
            PrintStageTime("Roslyn Emit", roslynEmitTimer);

            if (compilationContext.ErrorCount > 0)
                return;

            foreach (ModuleBinding tree in syntaxTrees)
                tree.semanticModel = compilation.GetSemanticModel(tree.tree);

            ConcurrentBag<(INamedTypeSymbol, ModuleBinding)> rootUdonSharpTypes = new ConcurrentBag<(INamedTypeSymbol, ModuleBinding)>();
            HashSet<INamedTypeSymbol> typesWithAssociatedProgramAssets = new HashSet<INamedTypeSymbol>();
            object programTypesLock = new object();

            Parallel.ForEach(rootTrees, new ParallelOptions { MaxDegreeOfParallelism = MAX_PARALLELISM}, module =>
            {
                SemanticModel model = module.semanticModel;
                SyntaxTree tree = model.SyntaxTree;

                INamedTypeSymbol udonSharpBehaviourDeclaration = null;

                foreach (ClassDeclarationSyntax classDecl in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(classDecl) is INamedTypeSymbol classType && classType.IsUdonSharpBehaviour())
                    {
                        if (!classType.IsAbstract && classType.Name != Path.GetFileNameWithoutExtension(module.filePath))
                        {
                            compilationContext.AddDiagnostic(DiagnosticSeverity.Error, classType.DeclaringSyntaxReferences.First().GetSyntax(), "UdonSharpBehaviour classes must have the same name as their containing .cs file");
                            return;
                        }

                        if (!ReferenceEquals(module.programScript, null) &&
                            ReferenceEquals(module.programClass, null))
                        {
                            compilationContext.AddDiagnostic(DiagnosticSeverity.Error, classType.DeclaringSyntaxReferences.First().GetSyntax(), "Could not retrieve C# class, make sure the MonoBehaviour class name matches the name of the .cs file and Unity has had a chance to compile the C# script.");
                            return;
                        }
                        
                        if (!ReferenceEquals(module.programAsset, null) && classType.Name == Path.GetFileNameWithoutExtension(module.filePath))
                        {
                            if (classType.IsAbstract)
                            {
                                compilationContext.AddDiagnostic(DiagnosticSeverity.Error, classType.DeclaringSyntaxReferences.First().GetSyntax(), "Abstract U# behaviours cannot have an associated U# program asset");
                                return;
                            }
                            
                            if (classType.IsGenericType)
                            {
                                compilationContext.AddDiagnostic(DiagnosticSeverity.Error, classType.DeclaringSyntaxReferences.First().GetSyntax(), "Generic U# behaviours cannot have an associated U# program asset");
                                return;
                            }
                        }
                        
                        if (classType.IsAbstract || classType.IsGenericType)
                            continue;

                        // If there are multiple UdonSharpBehaviours declared in the same behaviour, they need to be partial classes of the same class
                        // We'll skip adding them as roots in that case
                        if (udonSharpBehaviourDeclaration == null)
                        {
                            udonSharpBehaviourDeclaration = classType;
                            rootUdonSharpTypes.Add((classType, module));
                        }
                    }
                }

                if (!ReferenceEquals(module.programAsset, null) && udonSharpBehaviourDeclaration == null)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, tree.GetRoot(), "Script with U# program asset referencing it must have an UdonSharpBehaviour definition; scripts without UdonSharpBehaviour definitions should not have an associated UdonSharpProgramAsset");
                    return;
                }

                // Allow multiple partial classes of the same UdonSharpBehaviour type in one file, but still check if people have multiple of the partial class with program assets associated
                if (udonSharpBehaviourDeclaration?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ClassDeclarationSyntax declarationSyntax && 
                    declarationSyntax.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    lock (programTypesLock)
                    {
                        if (typesWithAssociatedProgramAssets.Contains(udonSharpBehaviourDeclaration))
                        {
                            compilationContext.AddDiagnostic(DiagnosticSeverity.Error, udonSharpBehaviourDeclaration.DeclaringSyntaxReferences.First().GetSyntax(), "Partial U# behaviours cannot have multiple program assets, choose 1 primary program asset for partial U# behaviours");
                            return;
                        }

                        typesWithAssociatedProgramAssets.Add(udonSharpBehaviourDeclaration);
                    }
                }
            });

            (INamedTypeSymbol, ModuleBinding)[] rootTypes = rootUdonSharpTypes.ToArray();

            compilationContext.BuildUdonBehaviourInheritanceLookup(rootTypes.Select(e => e.Item1));
            
            compilationContext.CurrentPhase = CompilationContext.CompilePhase.Bind;

            BindAllPrograms(rootTypes, compilationContext);

            // Run registered compile-time analyzers after binding
            foreach (var binding in rootTypes)
            {
                if (binding.Item2.binding != null)
                {
                    TypeSymbol typeSymbol = binding.Item2.binding.GetTypeSymbol(binding.Item1);
                    CompileTimeAnalyzerRegistry.RunAnalyzers(typeSymbol, binding.Item2.binding, compilationContext);
                }
            }

            if (compilationContext.ErrorCount > 0) return;

            System.Reflection.Assembly assembly = null;
            try
            {
                using (new UdonSharpUtils.UdonSharpAssemblyLoadStripScope())
                    assembly = System.Reflection.Assembly.Load(builtAssembly);
            }
            catch (Exception e)
            {
                compilationContext.AddDiagnostic(DiagnosticSeverity.Error, (Location)null, e.ToString());
            }
            
            if (compilationContext.ErrorCount > 0) return;
            
            compilationContext.CurrentPhase = CompilationContext.CompilePhase.Emit;
            
            BuildClassSerializationInfo();
            EmitAllPrograms(rootTypes, compilationContext, assembly);
        }

        private static void BindAllPrograms((INamedTypeSymbol, ModuleBinding)[] bindings, CompilationContext compilationContext)
        {
            Stopwatch bindTimer = Stopwatch.StartNew();

            Dictionary<TypeSymbol, HashSet<Symbol>> symbolsToBind = new Dictionary<TypeSymbol, HashSet<Symbol>>();
            object referencedSymbolsLock = new object();

            int currentIterationDivisor = 2;
            compilationContext.PhaseProgress = 0f;

            Dictionary<TypeSymbol, HashSet<Symbol>> bindSet = symbolsToBind;

        #if SINGLE_THREAD_BUILD
            foreach (var rootTypeSymbol in bindings)
        #else
            Parallel.ForEach(bindings, new ParallelOptions { MaxDegreeOfParallelism = MAX_PARALLELISM},rootTypeSymbol => 
        #endif
            {
                if (compilationContext.ErrorCount > 0)
                    return;
                
                BindContext bindContext = new BindContext(compilationContext, rootTypeSymbol.Item1, Array.Empty<Symbol>());
                Dictionary<TypeSymbol, HashSet<Symbol>> referencedTypes;
                
                try
                {
                    bindContext.Bind();

                    rootTypeSymbol.Item2.binding = bindContext;
                    referencedTypes = bindContext.GetTypeSymbol(rootTypeSymbol.Item1).CollectReferencedUnboundSymbols(bindContext, Array.Empty<Symbol>());
                }
                catch (CompilerException e)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, e.Location ?? bindContext.CurrentNode?.GetLocation(), e.Message);
                    return;
                }
                catch (Exception e)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, bindContext.CurrentNode, e.ToString());
                    return;
                }

                lock (referencedSymbolsLock)
                {
                    UnionSymbols(referencedTypes, bindSet);
                    compilationContext.PhaseProgress += (1f / bindings.Length) / currentIterationDivisor;
                }
            }
        #if !SINGLE_THREAD_BUILD
            );
        #endif
            
            while (symbolsToBind.Count > 0)
            {
                currentIterationDivisor *= 2;
                
                Dictionary<TypeSymbol, HashSet<Symbol>> newSymbols = new Dictionary<TypeSymbol, HashSet<Symbol>>();
                
            #if SINGLE_THREAD_BUILD
                foreach (var typeSymbol in symbolsToBind)
            #else
                Parallel.ForEach(symbolsToBind, new ParallelOptions { MaxDegreeOfParallelism = MAX_PARALLELISM}, typeSymbol =>
            #endif
                {
                    if (compilationContext.ErrorCount > 0)
                        return;
                    
                    BindContext bindContext = new BindContext(compilationContext, typeSymbol.Key.RoslynSymbol, typeSymbol.Value);
                    Dictionary<TypeSymbol, HashSet<Symbol>> referencedSymbols;
                    
                    try
                    {
                        bindContext.Bind();
                        referencedSymbols = typeSymbol.Key.CollectReferencedUnboundSymbols(bindContext, typeSymbol.Value);
                    }
                    catch (CompilerException e)
                    {
                        compilationContext.AddDiagnostic(DiagnosticSeverity.Error, e.Location ?? bindContext.CurrentNode?.GetLocation(), e.Message);
                        return;
                    }
                    catch (Exception e)
                    {
                        compilationContext.AddDiagnostic(DiagnosticSeverity.Error, bindContext.CurrentNode, e.ToString());
                        return;
                    }

                    lock (referencedSymbolsLock)
                    {
                        UnionSymbols(referencedSymbols, newSymbols);
                        compilationContext.PhaseProgress += (1f / symbolsToBind.Count) / currentIterationDivisor;
                    }
                }
            #if !SINGLE_THREAD_BUILD
                );
            #endif

                symbolsToBind = newSymbols;
            }
            
            PrintStageTime("U# Bind", bindTimer);
        }

        private static void UnionSymbols(Dictionary<TypeSymbol, HashSet<Symbol>> mergeSet, Dictionary<TypeSymbol, HashSet<Symbol>> bindSet)
        {
            foreach (var referencedTypeSymbols in mergeSet)
            {
                if (bindSet.TryGetValue(referencedTypeSymbols.Key, out var typeSymbols))
                    typeSymbols.UnionWith(referencedTypeSymbols.Value);
                else
                    bindSet.Add(referencedTypeSymbols.Key, referencedTypeSymbols.Value);
            }
        }

        private static void EmitAllPrograms((INamedTypeSymbol, ModuleBinding)[] bindings, CompilationContext compilationContext, System.Reflection.Assembly assembly)
        {
            Stopwatch emitTimer = Stopwatch.StartNew();

            int progressCounter = 0;
            int bindingCount = bindings.Length;
            
        #if SINGLE_THREAD_BUILD
            foreach (var binding in bindings)
        #else
            Parallel.ForEach(bindings, new ParallelOptions { MaxDegreeOfParallelism = MAX_PARALLELISM * 2 }, binding => 
        #endif
            {
                if (compilationContext.ErrorCount > 0)
                    return;
                
                INamedTypeSymbol rootTypeSymbol = binding.Item1;
                ModuleBinding moduleBinding = binding.Item2;
                AssemblyModule assemblyModule = new AssemblyModule(compilationContext, moduleBinding.filePath);
                moduleBinding.assemblyModule = assemblyModule;
                
                EmitContext moduleEmitContext = new EmitContext(assemblyModule, rootTypeSymbol);

                string typeName = TypeSymbol.GetFullTypeName(rootTypeSymbol);

                long typeID = UdonSharpInternalUtility.GetTypeID(typeName);
                
                moduleEmitContext.RootTable.CreateReflectionValue(CompilerConstants.UsbTypeIDHeapKey, moduleEmitContext.GetTypeSymbol(SpecialType.System_Int64), typeID);
                moduleEmitContext.RootTable.CreateReflectionValue(CompilerConstants.UsbTypeNameHeapKey, moduleEmitContext.GetTypeSymbol(SpecialType.System_String), typeName);

                TypeSymbol udonSharpBehaviourType = moduleEmitContext.GetTypeSymbol(typeof(UdonSharpBehaviour));
                
                if (moduleEmitContext.EmitType.BaseType != udonSharpBehaviourType ||
                    compilationContext.HasInheritedUdonSharpBehaviours(moduleEmitContext.EmitType))
                {
                    System.Collections.Generic.List<long> baseTypeArr = new System.Collections.Generic.List<long>();

                    TypeSymbol currentType = moduleEmitContext.EmitType;

                    while (currentType != udonSharpBehaviourType)
                    {
                        baseTypeArr.Add(UdonSharpInternalUtility.GetTypeID(TypeSymbol.GetFullTypeName(currentType.RoslynSymbol)));
                        currentType = currentType.BaseType;
                    }

                    // Array of base types inclusive of the root type
                    moduleEmitContext.RootTable.CreateReflectionValue(CompilerConstants.UsbTypeIDArrayHeapKey,
                        moduleEmitContext.GetTypeSymbol(typeof(long).MakeArrayType()), baseTypeArr.ToArray());
                }

                try
                {
                    moduleEmitContext.Emit();
                }
                catch (CompilerException e)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, e.Location ?? moduleEmitContext.CurrentNode?.GetLocation(), e.Message);
                }
                catch (Exception e)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, moduleEmitContext.CurrentNode, e.ToString());
                    return;
                }

                BehaviourSyncMode syncMode = BehaviourSyncMode.Any;
                UdonBehaviourSyncModeAttribute syncModeAttribute = null;

                TypeSymbol currentTypeSymbol = moduleEmitContext.EmitType;

                while (currentTypeSymbol != udonSharpBehaviourType && syncModeAttribute == null)
                {
                    syncModeAttribute = currentTypeSymbol.GetAttribute<UdonBehaviourSyncModeAttribute>();
                    currentTypeSymbol = currentTypeSymbol.BaseType;
                }

                if (syncModeAttribute != null)
                    syncMode = syncModeAttribute.behaviourSyncMode;

                moduleBinding.programAsset.behaviourSyncMode = syncMode;
                    
                Dictionary<string, FieldDefinition> fieldDefinitions = new Dictionary<string, FieldDefinition>();

                foreach (FieldSymbol symbol in moduleEmitContext.DeclaredFields)
                {
                    if (!symbol.Type.TryGetSystemType(out Type symbolSystemType))
                        UdonSharpUtils.LogError($"Could not get type for field {symbol.Name}");
                    
                    CheckSyncCompatibility(symbol, compilationContext, moduleEmitContext);

                    fieldDefinitions.Add(symbol.Name, new FieldDefinition(symbol.Name, symbolSystemType, symbol.Type.UdonType.SystemType, symbol.SyncMode, symbol.IsSerialized, symbol.SymbolAttributes.ToList()));
                }

                if (compilationContext.ErrorCount > 0)
                {
                    return;
                }

                moduleBinding.programAsset.fieldDefinitions = fieldDefinitions;
                
                // Validate network calls and setup metadata for them
                List<NetworkCallingEntrypointMetadata> methodMetadata = BuildNetworkMetadata(compilationContext, moduleEmitContext);
                
                if (compilationContext.ErrorCount > 0)
                {
                    return;
                }

                Interlocked.Increment(ref progressCounter);
                compilationContext.PhaseProgress = progressCounter / (float) bindingCount;
                
                if (moduleEmitContext.DebugInfo != null)
                    UdonSharpEditorCache.Instance.SetDebugInfo(moduleBinding.programAsset, CurrentJob.CompileOptions.IsEditorBuild ? UdonSharpEditorCache.DebugInfoType.Editor : UdonSharpEditorCache.DebugInfoType.Client, moduleEmitContext.DebugInfo);

                moduleBinding.programAsset.scriptID = typeID;
                moduleBinding.networkMethodMetadata = methodMetadata.ToArray();

                try
                {
                    AssembleProgram(binding, assembly);
                }
                catch (Exception e)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, moduleEmitContext.CurrentNode, e.ToString());
                }
            }
        #if !SINGLE_THREAD_BUILD
            );
        #endif
            
            PrintStageTime("U# Emit + Udon Assembly", emitTimer);
        }

        private static void CheckSyncCompatibility(FieldSymbol field, CompilationContext context, EmitContext emitContext)
        {
            UdonSyncedAttribute fieldSyncAttribute = field.GetAttribute<UdonSyncedAttribute>();

            if (fieldSyncAttribute == null)
            {
                return;
            }

            TypeSymbol fieldType = field.Type;

            if (!fieldType.IsExtern)
            {
                if (fieldType.IsEnum || (fieldType.IsArray && fieldType.ElementType.IsEnum))
                {
                    fieldType = fieldType.UdonType;
                }
                else
                {
                    context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"Cannot sync type '{fieldType}'");
                    return;
                }
            }
            
            if (!UdonNetworkTypes.CanSync(fieldType.UdonType.SystemType))
            {
                context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"'{field.Name}' type '{fieldType}' is not supported by Udon sync");
            }
            
            UdonBehaviourSyncModeAttribute syncModeAttribute = emitContext.EmitType.GetAttribute<UdonBehaviourSyncModeAttribute>();
            
            switch (syncModeAttribute?.behaviourSyncMode ?? BehaviourSyncMode.Any)
            {
                case BehaviourSyncMode.None:
                case BehaviourSyncMode.NoVariableSync:
                    context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"'{field.Name}' cannot be synced on an UdonBehaviour with sync mode None");
                    break;
                case BehaviourSyncMode.Continuous:
                    if (fieldType.IsArray)
                    {
                        context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"'{field.Name}' is an array which is not supported by Continuous sync, switch the behaviour to use Manual sync if you want to sync array fields");
                    }

                    goto default;
                case BehaviourSyncMode.Manual:
                    if (fieldSyncAttribute.NetworkSyncType == UdonSyncMode.Linear)
                    {
                        context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"'{field.Name}' has linear interpolation sync mode which cannot be used with Manual sync, use Continuous sync if you want linear interpolation on synced values");
                    }
                    
                    if (fieldSyncAttribute.NetworkSyncType == UdonSyncMode.Smooth)
                    {
                        context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"'{field.Name}' has smooth interpolation sync mode which cannot be used with Manual sync, use Continuous sync if you want smooth interpolation on synced values");
                    }
                    
                    goto default;
                case BehaviourSyncMode.Any:
                    // UdonSharp CE.
                    //
                    // Reaching here now means the author wrote [UdonBehaviourSyncMode(BehaviourSyncMode.Any)]
                    // *explicitly*: a behaviour that declares nothing gets a concrete mode injected before
                    // Roslyn runs (BehaviourSyncModeEmitter, CEGEN008), so it arrives as Manual or Continuous.
                    //
                    // Any is deliberately not validated as Continuous. It means "nothing is enforced; the
                    // mode is chosen per UdonBehaviour in the inspector" -- UdonSharpEditorManager.UpdateSyncModes
                    // explicitly SKIPS Any rather than reconciling it, so Manual is a legitimate choice for
                    // this behaviour and erroring on a synced array here would reject a working world.
                    //
                    // A synced array is still worth flagging, because if the component is left on Continuous
                    // that array can never sync and nothing else would say so.
                    if (fieldType.IsArray)
                    {
                        context.AddDiagnostic(DiagnosticSeverity.Warning, field.RoslynSymbol.Locations.First(),
                            $"'{field.Name}' is a synced array on a behaviour declared BehaviourSyncMode.Any, so its " +
                            "sync mode is whatever each UdonBehaviour is set to in the inspector. Continuous sync " +
                            "cannot carry arrays at all -- the field would silently never sync. Declare " +
                            "[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)] to make the requirement explicit.");
                    }

                    goto default;
                default:
                    
                    if (fieldSyncAttribute.NetworkSyncType == UdonSyncMode.Linear && !UdonNetworkTypes.CanSyncLinear(fieldType.UdonType.SystemType))
                    {
                        context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"'{field.Name}' type '{fieldType}' is not supported for linear sync");
                    }
                    else if (fieldSyncAttribute.NetworkSyncType == UdonSyncMode.Smooth && !UdonNetworkTypes.CanSyncSmooth(fieldType.UdonType.SystemType))
                    {
                        context.AddDiagnostic(DiagnosticSeverity.Error, field.RoslynSymbol.Locations.First(), $"'{field.Name}' type '{fieldType}' is not supported for smooth sync");
                    }
                    break;
            }
        }

        private static List<NetworkCallingEntrypointMetadata> BuildNetworkMetadata(CompilationContext compilationContext, EmitContext moduleEmitContext)
        {
            HashSet<string> methodUniqueIds = new();
            List<NetworkCallingEntrypointMetadata> methodMetadata = new();
            
            foreach (MethodSymbol method in moduleEmitContext.RootMethods)
            {
                CompilationContext.MethodExportLayout layout = compilationContext.GetUsbMethodLayout(method, moduleEmitContext);
                    
                if (!methodUniqueIds.Add(layout.ExportMethodName))
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, method.RoslynSymbol.Locations.First(), $"Network '{method}' overload overlaps with another method. Network callable methods cannot be overloaded");
                }
                    
                if (layout.NetworkCallableAttribute == null)
                    continue;
                    
                // Network callable method validation
                
                if (method.RoslynSymbol.IsVirtual ||
                    method.RoslynSymbol.IsAbstract ||
                    method.RoslynSymbol.IsOverride)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, method.RoslynSymbol.Locations.First(), $"Network callable method '{method}' cannot be virtual or abstract");
                }
                    
                if (method.RoslynSymbol.IsStatic)
                {
                    // This case shouldn't be hit technically since root methods are filtered to not include static methods
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, method.RoslynSymbol.Locations.First(), $"Network callable method '{method}' cannot be static");
                }
                    
                // We'll omit this since U# allowed return parameters on network callable methods prior, and it's fine if they return something since we can just discard it
                // if (method.ReturnType != null)
                // {
                //     compilationContext.AddDiagnostic(DiagnosticSeverity.Error, method.RoslynSymbol.Locations.First(), $"Network callable method '{method}' cannot have a return type");
                // }

                foreach (ParameterSymbol parameter in method.Parameters)
                {
                    if (parameter.IsByRef)
                    {
                        compilationContext.AddDiagnostic(DiagnosticSeverity.Error, parameter.RoslynSymbol.Locations.First(), $"Network callable method '{method}' cannot have ref or out parameters");
                    }
                        
                    Type parameterType = parameter.Type.UdonType.SystemType;

                    VRCUdonSyncType udonType = VRCUdonSyncTypeConverter.TypeToUdonType(parameterType);
                        
                    if (udonType == VRCUdonSyncType.NONE)
                    {
                        compilationContext.AddDiagnostic(DiagnosticSeverity.Error, parameter.RoslynSymbol.Locations.First(), $"Network callable method '{method}' parameter '{parameter.Name}' has unsupported type for network methods '{parameterType}'");
                    }
                }
                
                // Early out to avoid double errors in some cases
                if (compilationContext.ErrorCount > 0)
                {
                    return new List<NetworkCallingEntrypointMetadata>();
                }
                    
                List<NetworkCallingParameterMetadata> parameterMetadata = new();
                    
                for (int i = 0; i < method.Parameters.Length; ++i)
                {
                    ParameterSymbol parameter = method.Parameters[i];
                    Type parameterType = parameter.Type.UdonType.SystemType;
                        
                    VRCUdonSyncType udonType = VRCUdonSyncTypeConverter.TypeToUdonType(parameterType);

                    parameterMetadata.Add(new NetworkCallingParameterMetadata(layout.ParameterExportNames[i], udonType));
                }

                try
                {
                    NetworkCallingEntrypointMetadata metadata = new(layout.ExportMethodName, layout.NetworkCallableAttribute, parameterMetadata.ToArray());

                    methodMetadata.Add(metadata);
                }
                catch (Exception e)
                {
                    compilationContext.AddDiagnostic(DiagnosticSeverity.Error, method.RoslynSymbol.Locations.First(), $"{method}: {e.Message}");
                }
            }
            
            return methodMetadata;
        }

        private static readonly object _assembleLock = new object();

        private static void AssembleProgram((INamedTypeSymbol, ModuleBinding) binding, System.Reflection.Assembly assembly)
        {
            INamedTypeSymbol rootTypeSymbol = binding.Item1;
            ModuleBinding rootBinding = binding.Item2;
            System.Collections.Generic.List<Value> assemblyValues = rootBinding.assemblyModule.RootTable.GetAllUniqueChildValues();
            string generatedUasm = rootBinding.assemblyModule.BuildUasmStr();

            rootBinding.programAsset.AssembleCsProgram(generatedUasm, rootBinding.assemblyModule.GetHeapSize());
            rootBinding.programAsset.SetEntrypointMetadata(rootBinding.networkMethodMetadata);
            rootBinding.programAsset.SetUdonAssembly("");

            IUdonProgram program = rootBinding.programAsset.GetRealProgram();

            foreach (Value val in assemblyValues)
            {
                if (val.DefaultValue == null) continue;
                uint valAddress = program.SymbolTable.GetAddressFromSymbol(val.UniqueID);
                program.Heap.SetHeapVariable(valAddress, val.DefaultValue, val.UdonType.SystemType);
            }

            string typeName = TypeSymbol.GetFullTypeName(rootTypeSymbol);

            Type asmType = assembly.GetType(typeName);
            
            lock (_assembleLock)
            {
                UdonSharpEditorManager.ConstructorWarningsDisabled = true;
                object component;
                
                try
                {
                    component = Activator.CreateInstance(asmType);
                }
                finally
                {
                    UdonSharpEditorManager.ConstructorWarningsDisabled = false;
                }

                while (asmType != typeof(UdonSharpBehaviour))
                {
                    foreach (FieldInfo field in asmType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        uint valAddress = program.SymbolTable.GetAddressFromSymbol(UdonSharpUtils.UnmanglePropertyFieldName(field.Name));

                        object fieldValue = field.GetValue(component);

                        if (fieldValue == null)
                            continue;

                        if (UdonSharpUtils.IsUserJaggedArray(fieldValue.GetType()))
                        {
                            Serializer serializer = Serializer.CreatePooled(fieldValue.GetType());

                            SimpleValueStorage<object[]> arrayStorage = new SimpleValueStorage<object[]>();
                            serializer.WriteWeak(arrayStorage, fieldValue);

                            program.Heap.SetHeapVariable<object[]>(valAddress, arrayStorage.Value);
                        }
                        else if (UdonSharpUtils.IsUserDefinedType(fieldValue.GetType()))
                        {
                            Serializer serializer = Serializer.CreatePooled(fieldValue.GetType());

                            IValueStorage typeStorage = (IValueStorage)Activator.CreateInstance(typeof(SimpleValueStorage<>).MakeGenericType(serializer.GetUdonStorageType()), null);
                            serializer.WriteWeak(typeStorage, fieldValue);

                            program.Heap.SetHeapVariable(valAddress, typeStorage.Value, serializer.GetUdonStorageType());
                        }
                        // We set synced strings to an empty string by default
                        else if (field.FieldType == typeof(string) &&
                                 field.GetValue(component) == null &&
                                 field.GetCustomAttribute<UdonSyncedAttribute>() != null)
                        {
                            program.Heap.SetHeapVariable(valAddress, "");
                        }
                        else
                        {
                            program.Heap.SetHeapVariable(valAddress, fieldValue, field.FieldType);
                        }
                    }
                    
                    asmType = asmType.BaseType;
                }

                rootBinding.assembly = generatedUasm;
            }
        }

        /// <summary>
        /// Phase A of the CE transform stage: correctness-critical source generation.
        ///
        /// Unlike <see cref="RunCompileTimeOptimizers"/>, every failure here becomes a compile
        /// <see cref="DiagnosticSeverity.Error"/>. That matters because the alternative -- warn and continue
        /// with the untransformed trees -- produces a program that compiles cleanly while missing its
        /// generated sync glue, async state machines and quantization, and then marks itself up to date.
        /// </summary>
        private static ModuleBinding[] RunSourceGenerators(CompilationContext compilationContext, ModuleBinding[] syntaxTrees)
        {
            if (syntaxTrees == null || syntaxTrees.Length == 0)
                return syntaxTrees;

            SourceGenerationResult result;

            try
            {
                SourceGeneratorRegistry.EnsureProvidersRegistered(compilationContext);

                if (compilationContext.ErrorCount > 0)
                    return syntaxTrees;

                result = SourceGeneratorRegistry.RunGenerators(ToTreeTuples(syntaxTrees));
            }
            catch (CodeGenerationException e)
            {
                // Generator-authored failure: re-anchored onto the matching input tree, so the diagnostic is
                // clickable even when the generator was several rewrites deep in the chain.
                compilationContext.AddDiagnostic(DiagnosticSeverity.Error,
                    ResolveGeneratorLocation(syntaxTrees, e.Location, e.FilePath),
                    $"{e.GeneratorId}: {e.Message}");
                return syntaxTrees;
            }
            catch (Exception e)
            {
                compilationContext.AddDiagnostic(DiagnosticSeverity.Error, (Location)null,
                    $"USCE0004: UdonSharp CE source generation failed and the compile cannot continue: {e}");
                return syntaxTrees;
            }

            foreach (SourceGenerationDiagnostic diagnostic in result.Diagnostics)
            {
                compilationContext.AddDiagnostic(diagnostic.Severity,
                    ResolveGeneratorLocation(syntaxTrees, diagnostic.Location, diagnostic.FilePath),
                    $"{diagnostic.Id}: {diagnostic.Message}");
            }

            EvaluateGeneratorTripwires(compilationContext, syntaxTrees, result);

            if (result.Failed || compilationContext.ErrorCount > 0)
                return syntaxTrees;

            return ApplyTransformedTrees(compilationContext, syntaxTrees, result.Trees);
        }

        /// <summary>
        /// Re-anchors a generator's location onto the matching *input* tree.
        ///
        /// <see cref="CompilationContext.TranslateLocationToFileName"/> resolves a file by reference-comparing
        /// <c>binding.tree == location.SourceTree</c>. That only holds for the first generator in the chain:
        /// every later generator sees trees its predecessors rewrote, so its location points at a tree that is
        /// not in <c>ModuleBindings</c> and the error prints "Unknown File(1,1)". The generator does know the
        /// file it was working on, so we rebuild the location against that file's input tree.
        ///
        /// The offset is clamped because the rewritten tree is longer or shorter than the input; the line may
        /// be approximate, but an approximate line in the right file beats an unknown file.
        /// </summary>
        private static Location ResolveGeneratorLocation(ModuleBinding[] syntaxTrees, Location location, string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return location;

            // Already anchored to an input tree -- leave the exact span alone.
            if (location?.SourceTree != null && syntaxTrees.Any(binding => binding.tree == location.SourceTree))
                return location;

            ModuleBinding target = syntaxTrees.FirstOrDefault(
                binding => string.Equals(binding.filePath, filePath, StringComparison.Ordinal));

            if (target?.tree == null)
                return location;

            TextSpan span = location?.SourceSpan ?? default(TextSpan);

            // Clamp against the tree the location is built on, not against ModuleBinding.sourceText.
            // Location.Create validates the span against its own tree, so a span past the tree's end throws
            // out of the diagnostic path and loses the generator error entirely. The two lengths match today
            // only because the tree was parsed from that exact string; nothing enforces it.
            int limit = target.tree.GetRoot().FullSpan.End;

            return Location.Create(target.tree, TextSpan.FromBounds(Math.Min(span.Start, limit), Math.Min(span.End, limit)));
        }

        /// <summary>
        /// Catches the failure shape a presence check cannot see: a generator that was registered, ran, and
        /// silently produced nothing.
        ///
        /// Two conditions trip the diagnostic, and the second is the one that matters in production:
        /// the generator has no <see cref="SourceGeneratorRunStatus.Ran"/> record at all (its provider never
        /// registered, or it faulted), OR it ran and reported <c>FilesTransformed == 0</c>. Checking only
        /// the first made every tripwire self-satisfying -- the same loop in <c>CEGeneratorRunner</c> that
        /// declares a tripwire also writes the Ran record that clears it -- so the diagnostic was reachable
        /// only through the <c>OverrideForTests</c> seam, and the regression it exists to catch (a rewriter
        /// that stops emitting, leaving a program that compiles but never syncs) shipped silently.
        ///
        /// The zero-transform arm is only sound because of two things, and both must stay true:
        ///
        /// 1. Only a token that is a HARD OBLIGATION carries a tripwire. A generator with a legitimate
        ///    no-op path -- an opt-out attribute, an already-explicit declaration -- declares no tripwire
        ///    and says why at its entry in <c>CEGeneratorRunner</c>.
        /// 2. The scan looks at code only. <c>DemoManager</c> prints "[Sync], [Rpc], RateLimiter" in a UI
        ///    string and <c>CEPersistence</c> spells "[PlayerData(\"key\")]" inside its runtime error text;
        ///    a raw substring scan fails the compile of projects that declare neither attribute and where
        ///    every generator behaved correctly.
        ///
        /// FilesTransformed is a whole-compile count, so this catches a generator that stopped emitting
        /// entirely, not one that skipped a single file. That is the deliberate trade: per-file granularity
        /// would need each generator to report which files it considered, and guessing at it from the
        /// outside is exactly how a tripwire starts failing correct code.
        /// </summary>
        private static void EvaluateGeneratorTripwires(CompilationContext compilationContext, ModuleBinding[] syntaxTrees, SourceGenerationResult result)
        {
            if (result.Tripwires.Count == 0)
                return;

            Dictionary<string, SourceGeneratorRunRecord> runRecords =
                new Dictionary<string, SourceGeneratorRunRecord>(StringComparer.Ordinal);

            foreach (SourceGeneratorRunRecord record in result.RunRecords)
                runRecords[record.GeneratorId] = record;

            foreach (SourceGeneratorTripwire tripwire in result.Tripwires)
            {
                runRecords.TryGetValue(tripwire.GeneratorId, out SourceGeneratorRunRecord runRecord);

                bool ran = runRecord != null && runRecord.Status == SourceGeneratorRunStatus.Ran;

                if (ran && runRecord.FilesTransformed > 0)
                    continue;

                string failure = ran
                    ? "ran but emitted nothing for"
                    : "did not run on";

                foreach (ModuleBinding binding in syntaxTrees)
                {
                    foreach (string token in tripwire.TriggerTokens)
                    {
                        if (!SourceDeclaresTrigger(binding, token))
                            continue;

                        compilationContext.AddDiagnostic(DiagnosticSeverity.Error, binding.tree.GetRoot().GetLocation(),
                            $"{tripwire.DiagnosticId}: source generator '{tripwire.GeneratorId}' {failure} " +
                            $"'{binding.filePath}', which uses '{token}'. Correctness-critical code generation was " +
                            "skipped, so the resulting program would compile but not behave correctly.");

                        goto nextTripwire;
                    }
                }

                nextTripwire: ;
            }
        }

        /// <summary>
        /// True when <paramref name="token"/> appears in this module's real code -- not in a comment, not
        /// inside a string or character literal, and not in a <c>#if</c> region the parser dropped.
        ///
        /// Offsets into <see cref="ModuleBinding.sourceText"/> are valid against
        /// <see cref="ModuleBinding.tree"/> because the tree was parsed from that exact string
        /// (<c>CompilationContext.LoadSyntaxTreesAndCreateModules</c>), so a substring hit can be classified
        /// by asking the tree what lives at that offset.
        /// </summary>
        private static bool SourceDeclaresTrigger(ModuleBinding binding, string token)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(binding.sourceText) || binding.tree == null)
                return false;

            SyntaxNode root = binding.tree.GetRoot();
            int treeEnd = root.FullSpan.End;

            for (int index = binding.sourceText.IndexOf(token, StringComparison.Ordinal);
                 index >= 0;
                 index = binding.sourceText.IndexOf(token, index + 1, StringComparison.Ordinal))
            {
                // Guards FindToken's range check in the event sourceText and the tree ever drift apart.
                if (index >= treeEnd)
                    break;

                if (IsCodePosition(root, index, token))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Classifies one source offset as a real use of <paramref name="trigger"/>, or as text the
        /// generator was right to ignore.
        /// </summary>
        private static bool IsCodePosition(SyntaxNode root, int position, string trigger)
        {
            SyntaxToken token = root.FindToken(position);

            // FindToken maps a trivia offset onto the token that owns the trivia, so an offset the token's
            // own span does not cover is a comment, a disabled preprocessor region or whitespace.
            if (!token.Span.Contains(position))
                return false;

            // A trigger spelled with a leading '[' names an attribute, so the bracket it matched has to be
            // one that opens an attribute list. "Not a comment and not a string" is not the same thing:
            // 'counts[RpcKind.Chat]++' and 'new int[PlayerDataSlots]' are code and contain "[Rpc" and
            // "[PlayerData", so a project that declared no [Rpc] and no [PlayerData] at all -- where the
            // generator correctly transformed nothing -- failed its compile with USCE0001 over an array
            // index.
            //
            // And the attribute that bracket opens has to be the one the trigger names, whole: "[X" stands
            // for [X] or [XAttribute], never for an attribute whose name merely starts with X. A project's
            // own [RpcLogger], [PlayerDataCache] or [SyncOnJoinHelper] is a different attribute, and with no
            // real use of the generator's attribute anywhere the generator correctly emits nothing -- which
            // the prefix match reported as USCE0001. An attribute that shares a prefix and IS one of the
            // generator's spellings, like [RpcOwnerOnly], is declared as a trigger of its own.
            if (!string.IsNullOrEmpty(trigger) && trigger[0] == '[')
            {
                if (!token.IsKind(SyntaxKind.OpenBracketToken) || !(token.Parent is AttributeListSyntax attributeList))
                    return false;

                string attributeName = trigger.Substring(1);

                return attributeList.Attributes.Count > 0 &&
                       attributeList.Attributes[0].Name is IdentifierNameSyntax written &&
                       (written.Identifier.ValueText == attributeName ||
                        written.Identifier.ValueText == attributeName + "Attribute");
            }

            switch (token.Kind())
            {
                case SyntaxKind.StringLiteralToken:
                case SyntaxKind.CharacterLiteralToken:
                case SyntaxKind.InterpolatedStringTextToken:
                case SyntaxKind.XmlTextLiteralToken:
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Phase B of the CE transform stage: optional optimizations.
        ///
        /// A failure here is a <see cref="DiagnosticSeverity.Warning"/> rather than a bare console line, so
        /// it lands in <c>UdonSharpEditorCache.LastCompileDiagnostics</c> and is assertable by tests. The
        /// compile continues with the untransformed trees, which is safe because Phase A already put the
        /// correctness-critical glue in place.
        /// </summary>
        private static ModuleBinding[] RunCompileTimeOptimizers(CompilationContext compilationContext, ModuleBinding[] syntaxTrees)
        {
            if (syntaxTrees == null || syntaxTrees.Length == 0)
                return syntaxTrees;

            try
            {
                var optimizedTuples = CompileTimeOptimizerRegistry.RunOptimizers(ToTreeTuples(syntaxTrees));

                if (optimizedTuples == null || optimizedTuples.Length == 0)
                    return syntaxTrees;

                return ApplyTransformedTrees(compilationContext, syntaxTrees, optimizedTuples);
            }
            catch (Exception ex)
            {
                compilationContext.AddDiagnostic(DiagnosticSeverity.Warning, (Location)null,
                    $"USCE0005: a CE optimizer failed and was skipped; the program is correct but unoptimized: {ex}");
            }

            return syntaxTrees;
        }

        private static (SyntaxTree tree, string filePath)[] ToTreeTuples(ModuleBinding[] syntaxTrees)
        {
            var treeTuples = new (SyntaxTree tree, string filePath)[syntaxTrees.Length];
            for (int i = 0; i < syntaxTrees.Length; i++)
                treeTuples[i] = (syntaxTrees[i].tree, syntaxTrees[i].filePath);

            return treeTuples;
        }

        /// <summary>
        /// Writes transformed trees back into the module bindings and republishes them on the context, so
        /// binding, emit and diagnostic file resolution all see the transformed source.
        /// </summary>
        private static ModuleBinding[] ApplyTransformedTrees(CompilationContext compilationContext, ModuleBinding[] syntaxTrees, (SyntaxTree tree, string filePath)[] transformedTuples)
        {
            var bindingByPath = new Dictionary<string, ModuleBinding>(StringComparer.Ordinal);
            foreach (var binding in syntaxTrees)
            {
                if (!string.IsNullOrEmpty(binding.filePath))
                    bindingByPath[binding.filePath] = binding;
            }

            var updatedBindings = new ModuleBinding[transformedTuples.Length];
            for (int i = 0; i < transformedTuples.Length; i++)
            {
                var (tree, filePath) = transformedTuples[i];
                if (bindingByPath.TryGetValue(filePath, out var binding))
                {
                    // A tree no generator touched comes back as the same instance. Re-serializing it anyway
                    // cost a full GetText().ToString() per file per compile for the overwhelming majority of
                    // files, which is pure compile-time overhead on projects that use no CE features at all.
                    if (!ReferenceEquals(binding.tree, tree))
                    {
                        binding.tree = tree;
                        binding.sourceText = tree.GetText().ToString();
                    }

                    binding.filePath = filePath;
                    updatedBindings[i] = binding;
                }
                else
                {
                    updatedBindings[i] = new ModuleBinding
                    {
                        tree = tree,
                        filePath = filePath,
                        sourceText = tree.GetText().ToString()
                    };
                }
            }

            compilationContext.ReplaceModuleBindings(updatedBindings);
            return updatedBindings;
        }
    }
}
