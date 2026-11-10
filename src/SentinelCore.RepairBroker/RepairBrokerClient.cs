using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.RepairBroker;

/// <summary>Client transport for future trusted composition. The distributed WPF project has no reference to it.</summary>
public sealed class RepairBrokerClient : IRepairBrokerExecutor
{
    public static RepairManager CreateManager(IRepairRecordStore store, Func<string> profilePath)
    {
        var catalog = RepairCatalog.CreateDefault();
        var volumes = new NativeRepairVolumes();
        var preflight = NativeRepairPreflight.Create(catalog, volumes);
        var center = RepairCenter.CreateBrokered(preflight, new RepairBrokerClient(), store, profilePath);
        return new RepairManager(catalog, center, volumes, RepairExecutionMode.Real);
    }

    public Task<RepairExecutionOutcome> ExecuteAsync(RepairIntent persistedIntent, RepairAuthorizationToken consumedToken,
        IProgress<RepairProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteCoreAsync(persistedIntent, consumedToken, progress, cancellationToken));

    private static async Task<RepairExecutionOutcome> ExecuteCoreAsync(RepairIntent intent, RepairAuthorizationToken token,
        IProgress<RepairProgress>? progress, CancellationToken cancellationToken)
    {
        var elevationRequested = false;
        var committed = false;
        DateTimeOffset? processStarted = null;
        var elapsed = Stopwatch.StartNew();
        var elapsedAtProcessStart = TimeSpan.Zero;
        var output = new List<RepairOutputLine>();
        RepairExecutionOutcome Finish(RepairExecutionOutcome outcome) => outcome with
        {
            Native = outcome.Native! with { ElevationRequested = elevationRequested }
        };

        try
        {
            if (!RealRepairBuildGate.AllowsRealExecution(Environment.GetCommandLineArgs()) ||
                !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
                !Environment.Is64BitProcess)
                return Finish(NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Execução real bloqueada na distribuição."));
            var catalog = RepairCatalog.CreateDefault();
            if (!catalog.TryGet(intent.OperationId, out var operation) || operation is null ||
                !NativeRepairPreflight.IsBound(catalog, intent, token, operation, DateTimeOffset.UtcNow) ||
                !string.Equals(Environment.ProcessPath, BrokerWindows.InstalledImage(BrokerWindows.AppName),
                    StringComparison.OrdinalIgnoreCase))
                return Finish(NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Intenção, autorização ou cliente não reconhecido."));

            var imagePath = BrokerWindows.InstalledImage(BrokerWindows.BrokerName);
            using var pinnedImage = BrokerWindows.OpenPinnedBrokerImage(imagePath);
            var pipeId = Guid.NewGuid().ToString("N");
            var sid = BrokerWindows.CurrentSid();
            var session = BrokerWindows.CurrentSession();
            using var pipe = BrokerWindows.CreateLocalPipe(BrokerWindows.PipePrefix + pipeId, sid);
            cancellationToken.ThrowIfCancellationRequested();
            var elevate = operation.RequiresElevation && !Environment.IsPrivilegedProcess;
            progress?.Report(new(elevate ? "Aguardando consentimento UAC" : "Iniciando broker", elapsed.Elapsed, null));
            var startInfo = new ProcessStartInfo(imagePath)
            {
                UseShellExecute = elevate,
                Verb = elevate ? "runas" : string.Empty,
                WorkingDirectory = BrokerWindows.InstallDirectory,
                CreateNoWindow = !elevate,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (!elevate) startInfo.Environment.Clear();
            startInfo.ArgumentList.Add(pipeId);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            elevationRequested = elevate;
            using var broker = Process.Start(startInfo) ?? throw new InvalidOperationException("Broker sem processo associado.");
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(BrokerProtocol.HandshakeTimeout);
            await pipe.WaitForConnectionAsync(handshake.Token).ConfigureAwait(false);
            using var peer = BrokerWindows.Authenticate(pipe, peerIsServer: false, broker.Id,
                session, sid, BrokerWindows.BrokerName, out var brokerElevated);
            if (broker.HasExited || operation.RequiresElevation && !brokerElevated)
                return Finish(NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Broker não autenticado no nível esperado."));

            var protocol = new BrokerProtocol(pipe);
            var challenge = await protocol.ReadAsync(handshake.Token).ConfigureAwait(false);
            BrokerProtocol.Expect(challenge, BrokerMessageKind.Challenge, challenge.Nonce);
            await protocol.WriteAsync(new(BrokerMessageKind.Request, challenge.Nonce)
                { Intent = intent, Authorization = token }, handshake.Token).ConfigureAwait(false);
            var ready = await protocol.ReadAsync(handshake.Token).ConfigureAwait(false);
            if (ready.Kind == BrokerMessageKind.Result)
                return Finish(ReadOutcome(ready, challenge.Nonce));
            BrokerProtocol.Expect(ready, BrokerMessageKind.Ready, challenge.Nonce);

            cancellationToken.ThrowIfCancellationRequested();
            committed = true;
            await protocol.WriteAsync(new(BrokerMessageKind.Commit, challenge.Nonce)).ConfigureAwait(false);
            progress?.Report(new("Broker autorizado", elapsed.Elapsed, "Aguardando a ferramenta") { ExecutionCommitted = true });
            while (true)
            {
                var message = await protocol.ReadAsync().ConfigureAwait(false);
                if (message.Kind == BrokerMessageKind.Result)
                    return Finish(ReadOutcome(message, challenge.Nonce));
                BrokerProtocol.Expect(message, BrokerMessageKind.Progress, challenge.Nonce);
                var update = message.Progress ?? throw new InvalidDataException("Progresso do broker ausente.");
                if (processStarted is null && update.ProcessStartedUtc is { } started)
                {
                    processStarted = started;
                    elapsedAtProcessStart = elapsed.Elapsed;
                }
                if (update.Output is { } line && output.Count < RepairLimits.MaxOutputLines)
                    output.Add(line);
                progress?.Report(update);
            }
        }
        catch (Win32Exception error) when (elevationRequested && !committed && error.NativeErrorCode == 1223)
        {
            return Finish(NativeOutcome.BeforeStart(RepairBrokerStatus.ElevationDeclined, "Consentimento UAC recusado."));
        }
        catch (OperationCanceledException) when (!committed && cancellationToken.IsCancellationRequested)
        {
            return Finish(NativeOutcome.BeforeStart(RepairBrokerStatus.CancelledBeforeStart, "Cancelamento antes do compromisso de execução."));
        }
        catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or
            UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            var detail = error is OperationCanceledException
                ? "Prazo de comunicação com o broker excedido."
                : "Comunicação ou autenticação do broker indisponível.";
            if (!committed) return Finish(NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, detail));
            return Finish(NativeOutcome.BeforeStart(RepairBrokerStatus.Indeterminate, detail) with
            {
                Started = processStarted is not null,
                StartedAtUtc = processStarted ?? DateTimeOffset.UtcNow,
                Duration = processStarted is null ? TimeSpan.Zero : elapsed.Elapsed - elapsedAtProcessStart,
                Output = output,
                OutputTruncated = output.Count != 0,
                Native = new(RepairBrokerStatus.Indeterminate, false, processStarted, null)
            });
        }
    }

    private static RepairExecutionOutcome ReadOutcome(BrokerMessage message, string nonce)
    {
        BrokerProtocol.Expect(message, BrokerMessageKind.Result, nonce);
        if (message.Outcome is not { IsSynthetic: false, Native: { } native } outcome ||
            !Enum.IsDefined(native.Status) || outcome.Output is null ||
            outcome.Output.Count > RepairLimits.MaxOutputLines ||
            outcome.Output.Any(line => line?.Text is null || line.Text.Length > RepairLimits.MaxOutputLineChars ||
                line.Channel is null || !Enum.IsDefined(line.Channel.Value)) ||
            native.Status == RepairBrokerStatus.ProcessExited &&
                (!outcome.Started || !outcome.Completed || outcome.ExitCode is null ||
                 native.ProcessStartedUtc is null || native.ProcessExitedUtc is null))
            throw new InvalidDataException("Resultado do broker inválido.");
        return outcome;
    }
}
