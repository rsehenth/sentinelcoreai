using System.Text.RegularExpressions;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Persistence;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>
/// 7B.7: hardening of critical persistence and broker contracts. Synthetic only: isolated temporary roots,
/// fake stores and static source inspection; no Windows APIs, no broker process, no real data roots.
/// </summary>
public sealed class Stage7b7ContractHardeningTests
{
    public enum ReconciliationModule { Audit, Hardware, Event, Performance, Network, Graphics, Correlation }

    private static string NewRoot(string label) =>
        Path.Combine(Path.GetTempPath(), "SentinelCore7b7Tests", label, Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ReconciliationModule.Audit)]
    [InlineData(ReconciliationModule.Hardware)]
    [InlineData(ReconciliationModule.Event)]
    [InlineData(ReconciliationModule.Performance)]
    [InlineData(ReconciliationModule.Network)]
    [InlineData(ReconciliationModule.Graphics)]
    [InlineData(ReconciliationModule.Correlation)]
    public async Task ReconciliationKeepsValidRecordWhenDerivedReportWriteFails(ReconciliationModule module)
    {
        var root = NewRoot(module.ToString());
        try
        {
            var store = new MemoryStore(root);
            var (id, reportPath) = await SaveRecord(store, module);

            // Only the derived projection becomes unwritable; the authoritative Markdown record stays intact.
            File.Delete(reportPath);
            Directory.CreateDirectory(reportPath);

            var (recordIds, warnings) = await Load(store, module);

            Assert.Contains(id, recordIds);
            Assert.Contains(warnings, warning => warning.Contains(id.ToString("D")) &&
                warning.Contains("relatório derivado não pôde"));
            Assert.DoesNotContain(warnings, warning => warning.Contains("não recuperad"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(Guid Id, string ReportPath)> SaveRecord(MemoryStore store, ReconciliationModule module)
    {
        switch (module)
        {
            case ReconciliationModule.Audit:
                var audit = Samples.Audit(DateTimeOffset.UtcNow);
                await store.SaveAsync(audit);
                return (audit.Id, store.GetReportPath(audit.Id));
            case ReconciliationModule.Hardware:
                var snapshot = HardwareSnapshotSample();
                await store.SaveSnapshotAsync(snapshot);
                return (snapshot.Id, store.GetHardwareReportPath(snapshot.Id));
            case ReconciliationModule.Event:
                var investigation = Samples.Investigation();
                await store.SaveInvestigationAsync(investigation);
                return (investigation.Id, store.GetEventReportPath(investigation.Id));
            case ReconciliationModule.Performance:
                var session = PerformanceSamples.Session();
                await store.SaveSessionAsync(session);
                return (session.Id, store.GetPerformanceReportPath(session.Id));
            case ReconciliationModule.Network:
                var network = NetworkPersistenceTests.CreateValidSession(5, includeObservation: true);
                await store.SaveNetworkSessionAsync(network);
                return (network.Id, store.GetNetworkReportPath(network.Id));
            case ReconciliationModule.Graphics:
                var graphics = await GraphicsInvestigationSample();
                await store.SaveGraphicsInvestigationAsync(graphics);
                return (graphics.Id, store.GetGraphicsReportPath(graphics.Id));
            default:
                var correlation = await CorrelationInvestigationSample();
                await store.SaveCorrelationInvestigationAsync(correlation);
                return (correlation.Id, store.GetCorrelationReportPath(correlation.Id));
        }
    }

    private static async Task<(IReadOnlyList<Guid> RecordIds, IReadOnlyList<string> Warnings)> Load(
        MemoryStore store, ReconciliationModule module)
    {
        switch (module)
        {
            case ReconciliationModule.Audit:
                var audits = await store.LoadAsync();
                return ([.. audits.Audits.Select(audit => audit.Id)], audits.Warnings);
            case ReconciliationModule.Hardware:
                var hardware = await store.LoadSnapshotsAsync();
                return ([.. hardware.Snapshots.Select(snapshot => snapshot.Id)], hardware.Warnings);
            case ReconciliationModule.Event:
                var events = await store.LoadInvestigationsAsync();
                return ([.. events.Investigations.Select(investigation => investigation.Id)], events.Warnings);
            case ReconciliationModule.Performance:
                var performance = await store.LoadSessionsAsync();
                return ([.. performance.Sessions.Select(session => session.Id)], performance.Warnings);
            case ReconciliationModule.Network:
                var network = await store.LoadNetworkSessionsAsync();
                return ([.. network.Sessions.Select(session => session.Id)], network.Warnings);
            case ReconciliationModule.Graphics:
                var graphics = await store.LoadGraphicsInvestigationsAsync();
                return ([.. graphics.Investigations.Select(investigation => investigation.Id)], graphics.Warnings);
            default:
                var correlation = await store.LoadCorrelationInvestigationsAsync();
                return ([.. correlation.Investigations.Select(investigation => investigation.Id)], correlation.Warnings);
        }
    }

    private static HardwareSnapshot HardwareSnapshotSample()
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var item = HardwareSamples.Item("Processor", HardwareCategory.Cpu, "cpu",
            stableId: "sha256:cpu0000000000000000000000",
            properties: [new("architecture", "x64"), new("capacity_bytes", "1073741824")]);
        return new(1, Guid.NewGuid(), "0.2.0", "1", at, at.AddSeconds(2), TimeSpan.FromSeconds(2),
            HardwareCollectionStatus.Completed,
            [
                new("cpu", "1.0.0", HardwareCategory.Cpu, "Fake: tests only", at, at.AddSeconds(1),
                    TimeSpan.FromSeconds(1), HardwareCollectionStatus.Completed, [item], [])
            ]);
    }

    private static async Task<GraphicsInvestigation> GraphicsInvestigationSample()
    {
        var runner = GraphicsSamples.Runner(events: GraphicsSamples.RealisticEvents(
            events: [GraphicsSamples.DisplayTdr()],
            reports:
            [
                GraphicsSamples.LiveKernel("wer-117", "117"),
                GraphicsSamples.LiveKernel("wer-141", "141"),
                GraphicsSamples.LiveKernel("wer-142", "142")
            ]));
        return await runner.RunAsync(new GraphicsInvestigationRequest(EventWindow.Last7Days));
    }

    private static async Task<CorrelationInvestigation> CorrelationInvestigationSample()
    {
        var audits = new CorrelationSamples.FakeAuditStore();
        var events = new CorrelationSamples.FakeEventStore();
        var graphics = new CorrelationSamples.FakeGraphicsStore();
        var performance = new CorrelationSamples.FakePerformanceStore();
        var network = new CorrelationSamples.FakeNetworkStore();
        audits.Records.Add(CorrelationSamples.AuditWithBoot(CorrelationSamples.Now.AddHours(-5)));
        var tdr = GraphicsSamples.DisplayTdr(recordId: 12345, at: CorrelationSamples.Now.AddHours(-2));
        events.Records.Add(await CorrelationSamples.EventsAsync(events: [tdr]));
        var reader = new CorrelationEvidenceReader(audits, null, events, performance, network, graphics);
        var runner = new EvidenceCorrelationRunner(reader, CorrelationSamples.Clock());
        return await runner.RunAsync(new CorrelationRequest(EventWindow.Last24Hours,
            Sources: CorrelationSources.Audits | CorrelationSources.Events));
    }

    [Fact]
    public void BrokerClientClassifiesInvalidFramesAndResultsInsteadOfThrowingRaw()
    {
        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.RepairBroker.Client",
            "RepairBrokerClient.cs"));
        var filter = Regex.Match(source, @"catch \(Exception error\) when \(error is[^)]*InvalidDataException[^)]*\)",
            RegexOptions.Singleline);
        Assert.True(filter.Success, "O filtro final do cliente deve classificar InvalidDataException.");
        Assert.Contains("\"Quadro ou resultado do broker inválido.\"", source);
    }

    [Fact]
    public void BrokerMainExitsCleanlyOnInvalidProtocolFrames()
    {
        var source = File.ReadAllText(TestRepository.Combine("src", "SentinelCore.RepairBroker", "Program.cs"));
        var main = source[..source.IndexOf("private static async Task<RepairExecutionOutcome> ProcessRequestAsync",
            StringComparison.Ordinal)];
        Assert.Matches(@"catch \(Exception error\) when \(error is[^)]*InvalidDataException[^)]*\)", main);
    }
}
