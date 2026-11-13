using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UdonSharp.CE.Editor.Analyzers;
using UdonSharp.CE.Persistence;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Udon;
using UnityEditor;
using UnityEditor.Compilation;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Estimates attributed persistence models, including plain classes absent from the scene.
    /// Shares the compiler analyzer's size rules and constant-bound array lengths. Unknown lengths
    /// are skipped and named; a schema estimate must never become an upload-blocking error.
    /// </summary>
    public class PersistenceSizeValidator : IValidator
    {
        public string Name => "Persistence Size";
        public string Category => "Persistence";
        public string Description => "Estimates bounded PlayerData fields against the shared storage quota.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var candidates = new HashSet<Type>(context.BehaviourTypes);
            var sourceAssemblies = new HashSet<System.Reflection.Assembly>(CompilerUdonInterface.UdonSharpAssemblies);
            foreach (Type type in TypeCache.GetTypesWithAttribute<PlayerDataAttribute>())
                if (sourceAssemblies.Contains(type.Assembly)) candidates.Add(type);

            Type[] models = candidates.Where(type => type.GetCustomAttribute<PlayerDataAttribute>(true) != null).ToArray();
            CompilationContext compilationContext = CreateSourceContext(models);
            var issues = new List<ValidationIssue>();
            foreach (Type model in models)
            {
                var symbol = compilationContext.RoslynCompilation.GetTypeByMetadataName(model.FullName);
                var unknown = new List<string>();
                long fieldBytes = 0;
                int fieldCount = 0;
                foreach (FieldInfo field in model.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var key = field.GetCustomAttribute<PersistKeyAttribute>(true);
                    if (key == null || field.GetCustomAttribute<PersistIgnoreAttribute>(true) != null) continue;
                    IFieldSymbol sourceField = symbol?.GetMembers(field.Name).OfType<IFieldSymbol>().FirstOrDefault();
                    long valueBytes = sourceField != null
                        ? PlayerDataSizeAnalyzer.EstimateValueBytes(sourceField, compilationContext)
                        : EstimateReflectedValue(field);
                    if (valueBytes < 0) { unknown.Add(field.Name); continue; }
                    fieldBytes += PlayerDataSizeEstimate.GetFieldBytes(key.Key, valueBytes);
                    fieldCount++;
                }
                long total = PlayerDataSizeEstimate.GetModelBytes(fieldBytes);
                bool large = fieldCount > 0 && PlayerDataSizeEstimate.IsNearQuota(total);
                if (!large && unknown.Count == 0) continue;
                issues.Add(new ValidationIssue
                {
                    Severity = large ? ValidationSeverity.Warning : ValidationSeverity.Info,
                    ValidatorName = Name,
                    Message = PlayerDataSizeEstimate.DescribeVerdict(model.Name, total, fieldCount, unknown),
                    Details = "The quota is shared by every model. Unknown strings and arrays are excluded, not assigned guessed lengths. " +
                              "Use [MaxLength] for a bounded estimate; actual saved data and SDK-reported usage remain authoritative.",
                    RelatedType = model,
                    FilePath = context.GetSourceFile(model)
                });
            }
            return issues;
        }

        private static long EstimateReflectedValue(FieldInfo field)
        {
            Type type = field.FieldType;
            int? maxLength = field.GetCustomAttribute<MaxLengthAttribute>(true)?.Length;
            if (maxLength < 0) return -1;
            if (type == typeof(string))
                return maxLength.HasValue ? PlayerDataSizeEstimate.GetStringBytes(maxLength.Value) : -1;
            if (type.IsArray)
            {
                if (type.GetArrayRank() != 1 || !maxLength.HasValue) return -1;
                Type element = type.GetElementType();
                if (element.IsEnum) element = Enum.GetUnderlyingType(element);
                int bytes = PlayerDataSizeEstimate.GetWorstCaseValueBytes(element.FullName);
                return bytes >= 0 ? PlayerDataSizeEstimate.GetArrayBytes(bytes, maxLength.Value) : -1;
            }
            if (type.IsEnum) type = Enum.GetUnderlyingType(type);
            return PlayerDataSizeEstimate.GetWorstCaseValueBytes(type.FullName);
        }

        private static CompilationContext CreateSourceContext(Type[] models)
        {
            var names = new HashSet<string>(models.Select(type => type.Assembly.GetName().Name));
            var trees = new List<SyntaxTree>();
            // Model constructors can allocate, call APIs or mutate state. Read their declarations
            // instead; assembly defines preserve the active initializer under conditional compilation.
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                if (!names.Contains(assembly.name)) continue;
                var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9)
                    .WithPreprocessorSymbols(assembly.defines);
                foreach (string path in assembly.sourceFiles)
                {
                    try { trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path)); }
                    catch (IOException) { /* A source being moved remains an unknown-length field. */ }
                }
            }
            var references = new List<MetadataReference>(CompilationContext.GetMetadataReferences());
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                if (assembly.GetName().Name != "UdonSharp.Runtime" && assembly.GetName().Name != "UdonSharp.CE.Runtime") continue;
                if (!references.OfType<PortableExecutableReference>().Any(reference => reference.FilePath == assembly.Location))
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
            return new CompilationContext(new UdonSharpCompileOptions { IsEditorBuild = true })
            {
                RoslynCompilation = CSharpCompilation.Create("CE.Persistence.SchemaValidation", trees, references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            };
        }
    }
}
