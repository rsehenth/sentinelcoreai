using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class EventCatalogTests
{
    private static RawEventRecord Event(string provider, int id, EventLevel level = EventLevel.Error) =>
        new("System", provider, id, 10, DateTimeOffset.Parse("2026-10-30T12:00:00Z"), level, 2, null, null,
            null, false, []);

    private static RawWerReport Report(string eventType, string? code = null,
        EvidenceConfidence confidence = EvidenceConfidence.High) =>
        new("id", true, "Sistema", "ReportArchive", "folder", eventType, code, null, null, null,
            null, null, null, null, null, [], [], confidence);

    [Fact]
    public void NvidiaDriverMapsToGraphics() =>
        Assert.Equal(EventCategory.Graphics, EventCatalog.ClassifyEvent(Event("nvlddmkm", 153)).Category);

    [Fact]
    public void NvidiaProviderIsCaseInsensitive() =>
        Assert.Equal("graphics:nvidia", EventCatalog.ClassifyEvent(Event("NVLDDMKM", 13)).GroupId);

    [Fact]
    public void DisplayTdrMapsToGraphics() =>
        Assert.Equal(EventCategory.Graphics, EventCatalog.ClassifyEvent(Event("Display", 4101)).Category);

    [Fact]
    public void LiveKernel141MapsToGraphics() =>
        Assert.Equal(EventCategory.Graphics, EventCatalog.ClassifyReport(Report("LiveKernelEvent", "141")).Category);

    [Fact]
    public void KernelPower41MapsToPower() =>
        Assert.Equal(EventCategory.Power,
            EventCatalog.ClassifyEvent(Event("Microsoft-Windows-Kernel-Power", 41)).Category);

    [Fact]
    public void Disk51MapsToStorage() =>
        Assert.Equal(EventCategory.Storage, EventCatalog.ClassifyEvent(Event("disk", 51)).Category);

    [Fact]
    public void Volsnap36MapsToStorage() =>
        Assert.Equal(EventCategory.Storage, EventCatalog.ClassifyEvent(Event("volsnap", 36)).Category);

    [Fact]
    public void TcpipResourceExhaustionMapsToNetwork() =>
        Assert.Equal(EventCategory.Network, EventCatalog.ClassifyEvent(Event("Tcpip", 4231)).Category);

    [Fact]
    public void ApplicationErrorMapsToApplications() =>
        Assert.Equal(EventCategory.Applications, EventCatalog.ClassifyEvent(Event("Application Error", 1000)).Category);

    [Fact]
    public void ServiceControlManagerMapsToServices() =>
        Assert.Equal(EventCategory.Services,
            EventCatalog.ClassifyEvent(Event("Service Control Manager", 7031)).Category);

    [Fact]
    public void Dcom10016IsInformationalAndNotProofOfFailure()
    {
        var classification = EventCatalog.ClassifyEvent(Event("Microsoft-Windows-DCOM", 10016));
        Assert.Equal(EvidenceSeverity.Informational, classification.Severity);
        Assert.Equal("other:dcom-10016", classification.GroupId);
    }

    [Fact]
    public void UnknownErrorIsNotAutomaticallySevere()
    {
        var classification = EventCatalog.ClassifyEvent(Event("Provedor Desconhecido", 9999));
        Assert.Equal(EvidenceSeverity.Unknown, classification.Severity);
        Assert.Equal(EventCategory.Other, classification.Category);
    }

    [Fact]
    public void EventIdAloneIsNeverEnoughToClassify()
    {
        var classification = EventCatalog.ClassifyEvent(Event("Provedor Qualquer", 1000));
        Assert.Equal("other:unknown", classification.GroupId);
        Assert.Equal(EvidenceSeverity.Unknown, classification.Severity);
    }

    [Fact]
    public void ApplicationCrashReportIsObservedImpact()
    {
        var classification = EventCatalog.ClassifyReport(Report("AppCrash"));
        Assert.Equal(EventCategory.Applications, classification.Category);
        Assert.Equal(EvidenceSeverity.ObservedImpact, classification.Severity);
        Assert.Equal("applications:crash", classification.GroupId);
    }

    [Fact]
    public void KernelReportWithCodeIsGraphicsOnlyForKnownCodes()
    {
        Assert.Equal("graphics:live-kernel", EventCatalog.ClassifyReport(Report("Kernel", "141")).GroupId);
        Assert.Equal("other:device", EventCatalog.ClassifyReport(Report("Kernel", "999")).GroupId);
    }

    [Fact]
    public void CriticalWerReportIsInformational()
    {
        Assert.Equal(EvidenceSeverity.Informational,
            EventCatalog.ClassifyReport(Report("Critical_Update")).Severity);
    }
}
