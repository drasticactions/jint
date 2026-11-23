#if PUBLIC_API_BASELINES
#nullable enable

using System.Xml.Linq;

namespace Jint.Tests.PublicInterface;

/// <summary>
/// Where the assemblies Jint ships, and the XML documentation files beside them, are on disk.
/// </summary>
/// <remarks>
/// <para>
/// Both the public API baselines and the documentation gate read the <em>built</em> assemblies rather than the
/// one this test process loaded, so that what they check is what ships. Keeping them present and current is the <c>BuildJintForEveryShippedTargetFramework</c> target in this project's
/// <c>.csproj</c>.
/// </para>
/// <para>
/// The target framework list comes from <c>Jint.csproj</c> itself, so adding one adds a row that wants a
/// baseline and a documentation pass rather than silently shipping an unsnapshotted, undocumented surface.
/// </para>
/// </remarks>
internal static class ShippedJintBuildOutput
{
    /// <summary>
    /// The repository root, found by walking up from the test binary — with <c>UseArtifactsOutput</c> the
    /// output always sits at <c>&lt;root&gt;/artifacts/bin/&lt;project&gt;/&lt;configuration&gt;_&lt;tfm&gt;/</c>.
    /// </summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>
    /// The artifacts pivot this test binary itself was built into, so that a Debug run reads Debug output.
    /// </summary>
    public static string Configuration { get; } = FindConfiguration();

    /// <summary>
    /// What <c>Jint.csproj</c> says it ships, in declaration order.
    /// </summary>
    public static string[] TargetFrameworks { get; } = ReadShippedTargetFrameworks();

    /// <summary>
    /// The last target framework <c>Jint.csproj</c> declares, which the documentation gate runs on.
    /// </summary>
    public static string NewestTargetFramework => TargetFrameworks[^1];

    public static string AssemblyPath(string targetFramework)
        => Path.Combine(RepositoryRoot, "artifacts", "bin", "Jint", $"{Configuration}_{targetFramework}", "Jint.dll");

    /// <summary>
    /// The XML documentation file the compiler writes beside the assembly, which is
    /// <c>GenerateDocumentationFile</c> in <c>Jint.csproj</c>.
    /// </summary>
    public static string DocumentationPath(string targetFramework)
        => Path.Combine(RepositoryRoot, "artifacts", "bin", "Jint", $"{Configuration}_{targetFramework}", "Jint.xml");

    /// <summary>
    /// Cecil and <c>MetadataLoadContext</c> both resolve the assembly's own references while they walk it, so
    /// the directory beside this test binary (which carries Acornima) and the running runtime's directory
    /// both have to be reachable.
    /// </summary>
    /// <remarks>
    /// The assembly's own output directory is deliberately <em>not</em> added wholesale: a resolver handed two
    /// paths for one assembly identity fails with "has already been loaded into this MetadataLoadContext".
    /// </remarks>
    public static List<string> ResolverPaths(string assemblyPath)
    {
        var paths = new List<string> { assemblyPath };
        paths.AddRange(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"));

        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrEmpty(runtimeDirectory))
        {
            paths.AddRange(Directory.GetFiles(runtimeDirectory, "*.dll"));
        }

        return paths;
    }

    /// <summary>
    /// The message a test fails with when the artifacts tree it reads is not there.
    /// </summary>
    public static string MissingBuildOutput(string targetFramework, string path) => $"""
        Jint has no {targetFramework} build output at '{path}'.

        The public API baselines and the documentation gate are both generated from the built assemblies,
        because three of the five target frameworks Jint ships cannot be loaded by any test project. Build
        them with:

            dotnet build -c Release Jint/Jint.csproj
        """;

    private static string[] ReadShippedTargetFrameworks()
    {
        var project = Path.Combine(RepositoryRoot, "Jint", "Jint.csproj");
        var declaration = XDocument.Load(project).Descendants("TargetFrameworks").FirstOrDefault()
            ?? throw new InvalidOperationException($"'{project}' declares no <TargetFrameworks>, so the public API baselines cannot know what Jint ships.");

        return declaration.Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(targetFramework => targetFramework.Trim())
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jint", "Jint.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"No directory containing 'Jint/Jint.csproj' above '{AppContext.BaseDirectory}'. The public API baselines are generated from the repository's own build output and cannot be produced from a detached copy of this assembly.");
    }

    private static string FindConfiguration()
    {
        // artifacts/bin/<project>/<configuration>_<tfm> - the layout Directory.Build.props opts into with
        // UseArtifactsOutput. Anything else (a plain bin/<configuration>/<tfm> layout, say) means the pivot
        // is not in the directory name, and Release is what this repository builds.
        var leaf = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var separator = leaf.IndexOf('_');
        return separator > 0 ? leaf.Substring(0, separator) : "release";
    }
}
#endif
