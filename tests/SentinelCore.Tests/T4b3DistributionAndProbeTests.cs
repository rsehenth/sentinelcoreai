using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// T4B.3: static/reflective guards for the boundary between test mechanisms and the distributed application.
/// They read source and project files only; nothing here touches Windows, the Registry or installed packages.
/// </summary>
public sealed class T4b3DistributionAndProbeTests
{
    private static readonly string[] ProductionProjects =
    [
        "SentinelCore.App", "SentinelCore.Application", "SentinelCore.Domain", "SentinelCore.Persistence",
        "SentinelCore.Infrastructure.Windows", "SentinelCore.Infrastructure.Modifications",
        "SentinelCore.Infrastructure.Repairs", "SentinelCore.Infrastructure.Debloat"
    ];

    private static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(TestRepository.Combine("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void ProbeSeamIsInternalAsyncLocalAndEmptyByDefault()
    {
        var assembly = typeof(MemoryStore).Assembly;
        var envelope = assembly.GetType("SentinelCore.Persistence.RecordEnvelope", throwOnError: true)!;
        Assert.False(envelope.IsPublic);
        var probe = envelope.GetField("Probe", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.NotNull(probe);
        Assert.True(probe.IsAssembly, "Probe deve permanecer internal.");
        Assert.True(probe.IsInitOnly);
        Assert.True(probe.FieldType.IsGenericType && probe.FieldType.GetGenericTypeDefinition() == typeof(AsyncLocal<>));
        Assert.Null(envelope.GetField("Probe", BindingFlags.Static | BindingFlags.Public));
        Assert.Null(RecordEnvelope.Probe.Value);
    }

    [Fact]
    public void OnlyTheTestAssemblyAndSyntheticWorkerSeeInternalsOfPersistence()
    {
        var visible = typeof(MemoryStore).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["SentinelCore.Tests", "SyntheticJournalWorker"], visible);
    }

    [Fact]
    public async Task ProbeIsScopedToItsAsyncFlowAndDoesNotLeakToOtherFlows()
    {
        var gate = new TaskCompletionSource();
        var beforeSetting = Task.Run(async () => { await gate.Task; return RecordEnvelope.Probe.Value; });
        RecordEnvelope.Probe.Value = _ => { };
        try
        {
            gate.SetResult();
            Assert.Null(await beforeSetting); // an unrelated flow started earlier never observes the delegate
        }
        finally { RecordEnvelope.Probe.Value = null; }
        await Task.Run(() => RecordEnvelope.Probe.Value = _ => { });
        Assert.Null(RecordEnvelope.Probe.Value); // assignment inside a child flow does not reach the caller
    }

    [Fact]
    public void NothingOutsideRecordEnvelopeReferencesTheProbeOrItsStages()
    {
        var envelope = Path.GetFullPath(TestRepository.Combine("src", "SentinelCore.Persistence", "RecordEnvelope.cs"));
        var offenders = ProductionSources().Where(file => Path.GetFullPath(file) != envelope)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\bAtomicWriteStage\b|RecordEnvelope\s*\.\s*Probe\b|using\s+static\s+SentinelCore\.Persistence\.RecordEnvelope|using\s+\w+\s*=\s*(SentinelCore\.Persistence\.)?RecordEnvelope\b"))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void ProductionHasNoEnvironmentArgumentConfigurationOrDynamicLoadingSwitchForTheProbe()
    {
        var forbidden = new Regex(
            @"GetEnvironmentVariable|GetEnvironmentVariables|GetCommandLineArgs|Environment\.CommandLine|ConfigurationManager|IConfiguration|" +
            @"Assembly\.Load|LoadFrom|LoadFile|UnsafeLoadFrom|ReflectionOnlyLoad|AssemblyLoadContext|AppContext\.(GetData|SetSwitch|TryGetSwitch)|" +
            @"AppDomain\.CurrentDomain\.GetData|ExpandEnvironmentVariables|NativeLibrary\.Load|Activator\.CreateInstance|BindingFlags\.NonPublic",
            RegexOptions.CultureInvariant);
        var offenders = ProductionSources().Where(file => forbidden.IsMatch(File.ReadAllText(file))).ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void DistributedProjectGraphExcludesTestHarnessWorkerAndNativeDebloatAssemblies()
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(TestRepository.Combine("src", "SentinelCore.App", "SentinelCore.App.csproj"));
        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!reachable.Add(Path.GetFileNameWithoutExtension(project))) continue;
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!,
                    reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar))));
        }
        Assert.Equal(ProductionProjects.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), reachable.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(reachable, name => name.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Worker", StringComparison.OrdinalIgnoreCase) || name.Contains(".Native", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PublishScriptKeepsEveryRealExecutionPropertyDisabled()
    {
        var script = File.ReadAllText(TestRepository.Combine("scripts", "publish.ps1"));
        foreach (var flag in new[] { "SentinelCoreRealTweaks=false", "SentinelCoreRealRepairs=false", "SentinelCoreRealDebloat=false" })
            Assert.Contains("-p:" + flag, script);
        Assert.DoesNotMatch(new Regex(@"SentinelCoreReal\w*\s*[=:]\s*true", RegexOptions.IgnoreCase), script);
    }

    [Fact]
    public void InternalsVisibleToDeclarationsInProductionAreExactlyTheReviewedOnes()
    {
        var expected = new[]
        {
            "SentinelCore.Application/AssemblyInfo.cs=SentinelCore.Tests", "SentinelCore.Domain/AssemblyInfo.cs=SentinelCore.Tests",
            "SentinelCore.Infrastructure.Windows/AssemblyInfo.cs=SentinelCore.Tests", "SentinelCore.Persistence/AssemblyInfo.cs=SentinelCore.Tests",
            "SentinelCore.Persistence/AssemblyInfo.cs=SyntheticJournalWorker"
        };
        var src = TestRepository.Combine("src");
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\[\s*assembly\s*:\s*InternalsVisibleTo\s*\(\s*""([^""]+)""|<InternalsVisibleTo\b[^>]*Include\s*=\s*""([^""]+)""")
                .Select(m => Path.GetRelativePath(src, file).Replace('\\', '/') + "=" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), found);
    }

    [Fact]
    public void ProductionProjectsReferenceNoTestPackagesCompileLinksOrExternalReferences()
    {
        var offenders = Directory.EnumerateFiles(TestRepository.Combine("src"), "*.csproj", SearchOption.AllDirectories)
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"xunit|Microsoft\.NET\.Test|\.Tests\b|SyntheticJournalWorker|<Compile\s+Include\s*=\s*""\.\.|<Reference\s+Include", RegexOptions.IgnoreCase))
            .ToArray();
        Assert.Empty(offenders);
    }
}
