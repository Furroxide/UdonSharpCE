using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.Compiler;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Data;

namespace UdonSharp.CE.Tests
{
    internal static class EditorTestUtilities
    {
        public static string RunOptimizer(string optimizerTypeName, string source)
        {
            Assembly ceEditorAssembly = GetAssemblyByName("UdonSharp.CE.Editor");
            Type optimizerType = ceEditorAssembly.GetType(optimizerTypeName, throwOnError: true);
            object optimizer = Activator.CreateInstance(optimizerType, nonPublic: true);

            Type contextType = ceEditorAssembly.GetType("UdonSharp.CE.Editor.Optimizers.OptimizationContext", throwOnError: true);
            object context = Activator.CreateInstance(contextType, nonPublic: true);

            SyntaxTree inputTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
            MethodInfo optimizeMethod = optimizerType.GetMethod("Optimize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            SyntaxTree outputTree = (SyntaxTree)optimizeMethod.Invoke(optimizer, new object[] { inputTree, context });
            return outputTree.GetText().ToString();
        }

        public static Assembly CompileAssembly(string source, string assemblyNamePrefix)
        {
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                .Select(assembly => assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(MetadataReference.CreateFromFile)
                .Cast<MetadataReference>()
                .ToArray();

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyNamePrefix + "." + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var stream = new MemoryStream())
            {
                EmitResult emitResult = compilation.Emit(stream);
                if (!emitResult.Success)
                {
                    string errors = string.Join(
                        Environment.NewLine,
                        emitResult.Diagnostics
                            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                            .Select(diagnostic => diagnostic.ToString()));

                    Assert.Fail(errors);
                }

                stream.Seek(0, SeekOrigin.Begin);
                return Assembly.Load(stream.ToArray());
            }
        }

        public static UdonSharpProgramAsset CreateProgramAssetForBehaviourType(Type behaviourType, IList<string> createdAssetPaths)
        {
            GameObject go = new GameObject("CE.ProgramAssetTest");
            try
            {
                UdonSharpBehaviour behaviour = (UdonSharpBehaviour)go.AddComponent(behaviourType);
                UdonSharpProgramAsset asset = UdonSharpEditor.UdonSharpEditorUtility.GetUdonSharpProgramAsset(behaviour);
                string assetPath = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(assetPath) && !createdAssetPaths.Contains(assetPath))
                {
                    createdAssetPaths.Add(assetPath);
                }

                return asset;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        public static void CompileAllPrograms()
        {
            UdonSharpProgramAsset.CompileAllCsPrograms(true);
        }

        public static string GetAssemblyError(UdonSharpProgramAsset asset)
        {
            FieldInfo assemblyErrorField = typeof(VRC.Udon.Editor.ProgramSources.UdonAssemblyProgramAsset)
                .GetField("assemblyError", BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)assemblyErrorField.GetValue(asset);
        }

        public static string GetUasm(UdonSharpProgramAsset asset)
        {
            return UdonSharpEditor.UdonSharpEditorCache.Instance.GetUASMStr(asset);
        }

        public static void DeleteAssets(IList<string> assetPaths)
        {
            for (int i = assetPaths.Count - 1; i >= 0; i--)
            {
                string assetPath = assetPaths[i];
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
            }

            assetPaths.Clear();
            AssetDatabase.Refresh();
        }

        public static Assembly GetAssemblyByName(string assemblyName)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => candidate.GetName().Name == assemblyName);

            if (assembly == null)
            {
                Assert.Fail($"Assembly '{assemblyName}' is not loaded.");
            }

            return assembly;
        }
    }
}
