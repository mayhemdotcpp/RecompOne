using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace RecompOne.Runtime.Modding;

public static class ModCompiler
{
    private static List<MetadataReference>? _references;

    public static byte[]? Compile(string modId, IReadOnlyList<(string Path, string Text)> sources)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = sources.Select(s =>
            CSharpSyntaxTree.ParseText(SourceText.From(s.Text, Encoding.UTF8), parseOptions, s.Path)).ToList();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithAllowUnsafe(true)
            .WithOptimizationLevel(OptimizationLevel.Release);

        var compilation = CSharpCompilation.Create($"mod-{modId}", trees, References(), options);
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!result.Success)
        {
            foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                Console.Error.WriteLine($"[Mods] {modId}: {diag}");
            return null;
        }

        return ms.ToArray();
    }

    private static unsafe List<MetadataReference> References()
    {
        if (_references != null) return _references;

        var framework = Assembly.Load("netstandard");
        foreach (var name in framework.GetReferencedAssemblies())
        {
            try { Assembly.Load(name); }
            catch (Exception ex) { Console.Error.WriteLine($"[Mods] could not load {name.Name}: {ex.Message}"); }
        }

        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>();
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (!a.IsDynamic && a.FullName is { } fullName && seen.Add(fullName) &&
                a.TryGetRawMetadata(out var blob, out var length))
                refs.Add(AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((IntPtr)blob, length)).GetReference());

        _references = refs;
        return _references;
    }
}