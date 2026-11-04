using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

public sealed class RepairResultInterpreterTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static RepairOperationDescriptor Operation(string id) =>
        RepairCatalog.CreateDefault().Get(id);

    private static RepairExecutionOutcome Outcome(int? exitCode, params string[] lines) =>
        new(true, true, false, false, exitCode, null, false,
            lines.Select((line, index) => new RepairOutputLine(index + 1, line)).ToList(),
            At, At.AddSeconds(1), TimeSpan.FromSeconds(1), true, []);

    private static RepairExecutionOutcome Truncated(int? exitCode, params string[] lines) =>
        Outcome(exitCode, lines) with { OutputTruncated = true };

    private static RepairExecutionOutcome Cancelled() =>
        new(true, false, true, false, null, null, false, [], At, At, TimeSpan.Zero, true, []);

    private static RepairExecutionOutcome Interrupted() =>
        new(true, false, false, true, null, null, false, [], At, At, TimeSpan.Zero, true, []);

    [Fact]
    public void SfcVerifySuccessAndCorruptionAreDistinguishedByExitCode()
    {
        var operation = Operation(RepairCatalog.SfcVerifyId);

        var success = RepairResultInterpreter.Interpret(operation, Outcome(0,
            "Windows Resource Protection did not find any integrity violations."));
        var corruption = RepairResultInterpreter.Interpret(operation, Outcome(1,
            "Windows Resource Protection found corrupt files."));

        Assert.Equal(RepairResultClassification.NoIssuesReported, success.Classification);
        Assert.Equal(RepairConfidence.High, success.Confidence);
        Assert.Equal(RepairResultClassification.CorruptionDetected, corruption.Classification);
        Assert.True(corruption.Evidence.CorruptionDetected);
        Assert.True(corruption.RequiresIntervention);
    }

    [Fact]
    public void SfcRepairReportsCompletedPartialAndFailureWithoutAssumingSuccess()
    {
        var operation = Operation(RepairCatalog.SfcRepairId);

        Assert.Equal(RepairResultClassification.RepairCompleted,
            RepairResultInterpreter.Interpret(operation, Outcome(1,
                "Windows Resource Protection found corrupt files and successfully repaired them.")).Classification);
        Assert.Equal(RepairResultClassification.RepairPartial,
            RepairResultInterpreter.Interpret(operation, Outcome(2,
                "Windows Resource Protection found corrupt files but was unable to fix some of them.")).Classification);
        Assert.Equal(RepairResultClassification.RepairFailed,
            RepairResultInterpreter.Interpret(operation, Outcome(3,
                "Windows Resource Protection could not perform the requested operation.")).Classification);
    }

    [Fact]
    public void DismDistinguishesNoCorruptionRepairableSourceMissingAndGenericFailure()
    {
        var check = Operation(RepairCatalog.DismCheckHealthId);
        var restore = Operation(RepairCatalog.DismRestoreHealthId);

        Assert.Equal(RepairResultClassification.NoCorruptionDetected,
            RepairResultInterpreter.Interpret(check, Outcome(0,
                "No component store corruption detected.")).Classification);
        Assert.Equal(RepairResultClassification.CorruptionDetected,
            RepairResultInterpreter.Interpret(check, Outcome(0,
                "The component store is repairable.")).Classification);
        var source = RepairResultInterpreter.Interpret(restore, Outcome(RepairExitCodes.DismSourceNotFound,
            "The source files could not be found."));
        Assert.Equal(RepairResultClassification.RepairSourceUnavailable, source.Classification);
        Assert.True(source.Evidence.RepairSourceUnavailable);
        Assert.Equal(RepairResultClassification.RepairFailed,
            RepairResultInterpreter.Interpret(restore, Outcome(RepairExitCodes.DismGenericFailure,
                "The operation failed.")).Classification);
    }

    [Fact]
    public void ExitCodeZeroAloneDoesNotDeclareRepairSuccess()
    {
        var restore = Operation(RepairCatalog.DismRestoreHealthId);
        var result = RepairResultInterpreter.Interpret(restore, Outcome(0, "Output without a recognized marker."));

        Assert.Equal(RepairResultClassification.RepairCompleted, result.Classification);
        Assert.Equal(RepairConfidence.Medium, result.Confidence);
        Assert.Contains(result.Limitations, item => item.Contains("não prova", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalizedPhraseCannotOverrideANonZeroExitCode()
    {
        var check = Operation(RepairCatalog.DismCheckHealthId);
        var result = RepairResultInterpreter.Interpret(check, Outcome(1,
            "No component store corruption detected."));

        Assert.Equal(RepairResultClassification.UnknownExitCode, result.Classification);
        Assert.Equal(RepairConfidence.Low, result.Confidence);
    }

    [Fact]
    public void ChkdskReportsProblemsRestartAndVolumeConditions()
    {
        var scan = Operation(RepairCatalog.ChkdskScanId);

        Assert.Equal(RepairResultClassification.CorruptionDetected,
            RepairResultInterpreter.Interpret(scan, Outcome(1,
                "Windows found problems with the file system.")).Classification);
        var restart = RepairResultInterpreter.Interpret(scan, Outcome(2,
            "A restart is required to complete the repair."));
        Assert.Equal(RepairResultClassification.RestartRequired, restart.Classification);
        Assert.True(restart.RequiresRestart);
        Assert.False(restart.Evidence.RepairCompleted);
        Assert.Equal(RepairResultClassification.VolumeUnavailable,
            RepairResultInterpreter.Interpret(scan, Outcome(3,
                "Cannot open volume for direct access.")).Classification);
        Assert.Equal(RepairResultClassification.IncompatibleFileSystem,
            RepairResultInterpreter.Interpret(scan, Outcome(3,
                "The type of the file system is not NTFS.")).Classification);
        Assert.Equal(RepairResultClassification.RepairPartial,
            RepairResultInterpreter.Interpret(scan, Outcome(3,
                "Some errors could not be fixed.")).Classification);
    }

    [Fact]
    public void ReagentcReadsEnabledAndDisabledStateFromStructuredMarkers()
    {
        var operation = Operation(RepairCatalog.WinReInfoId);

        var enabled = RepairResultInterpreter.Interpret(operation, Outcome(0,
            "Windows Recovery Environment (Windows RE) information.", "Windows RE status: Enabled"));
        var disabled = RepairResultInterpreter.Interpret(operation, Outcome(0,
            "Windows Recovery Environment (Windows RE) information.", "Windows RE status: Disabled"));
        var failure = RepairResultInterpreter.Interpret(operation, Outcome(1, "The operation failed."));

        Assert.Equal(RepairResultClassification.InformationRetrieved, enabled.Classification);
        Assert.True(enabled.Evidence.WinReEnabled);
        Assert.False(disabled.Evidence.WinReEnabled);
        Assert.Equal(RepairResultClassification.RepairFailed, failure.Classification);
    }

    [Fact]
    public void AccessDeniedUnsupportedCancelledInterruptedAndTruncatedAreExplicit()
    {
        var sfc = Operation(RepairCatalog.SfcVerifyId);
        var dism = Operation(RepairCatalog.DismCheckHealthId);

        Assert.Equal(RepairResultClassification.AccessDenied,
            RepairResultInterpreter.Interpret(sfc, Outcome(5, "Access is denied.")).Classification);
        Assert.Equal(RepairResultClassification.UnsupportedSystem,
            RepairResultInterpreter.Interpret(dism, Outcome(RepairExitCodes.DismNotSupported,
                "The operation is not supported.")).Classification);
        Assert.Equal(RepairResultClassification.Cancelled,
            RepairResultInterpreter.Interpret(sfc, Cancelled()).Classification);
        Assert.Equal(RepairResultClassification.Interrupted,
            RepairResultInterpreter.Interpret(sfc, Interrupted()).Classification);
        var truncated = RepairResultInterpreter.Interpret(sfc, Truncated(0,
            "Windows Resource Protection did not find any integrity violations."));
        Assert.Equal(RepairResultClassification.OutputIncomplete, truncated.Classification);
        Assert.Equal(RepairConfidence.Low, truncated.Confidence);
        Assert.True(truncated.RequiresIntervention);
    }

    [Fact]
    public void OutputLimiterCapsLinesBytesAndLineLengthAndTruncates()
    {
        var many = RepairOutputLimiter.Limit(Enumerable.Range(0, 250).Select(index => "line " + index));
        var longLine = RepairOutputLimiter.Limit(["x" + new string('y', 1000)]);
        var bytes = RepairOutputLimiter.Limit(Enumerable.Range(0, 200)
            .Select(index => new string((char)('a' + index % 26), 500)));

        Assert.Equal(RepairLimits.MaxOutputLines, many.Lines.Count);
        Assert.True(many.Truncated);
        Assert.Equal(250, many.OriginalLineCount);
        Assert.Equal(RepairLimits.MaxOutputLineChars, longLine.Lines.Single().Text.Length);
        Assert.True(longLine.Truncated);
        Assert.True(bytes.Bytes <= RepairLimits.MaxOutputBytes);
        Assert.True(bytes.Truncated);
    }

    [Fact]
    public void OutputSanitizerRemovesControlsAndRedactsUserProfile()
    {
        var sanitized = RepairOutputLimiter.Sanitize(
            "a\u0001b\tc C:\\Users\\alice\\Documents\\secret.txt", "C:\\Users\\alice");

        Assert.Equal("ab\tc %USERPROFILE%\\Documents\\secret.txt", sanitized);
        Assert.DoesNotContain("alice", sanitized);
    }
}
