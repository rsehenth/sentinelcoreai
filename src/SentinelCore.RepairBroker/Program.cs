using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using SentinelCore.Application;
using SentinelCore.Domain;

namespace SentinelCore.RepairBroker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!RepairCenter.BrokerExecutionEnabled || RuntimeFeature.IsDynamicCodeSupported ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitProcess)
            return 2;
        if (args.Length != 2 || !Guid.TryParseExact(args[0], "N", out var pipeId) || pipeId == Guid.Empty ||
            !int.TryParse(args[1], out var callerPid) || callerPid <= 0)
            return 2;

        try
        {
            if (!string.Equals(Environment.ProcessPath, BrokerWindows.InstalledImage(BrokerWindows.BrokerName),
                    StringComparison.OrdinalIgnoreCase))
                return 2;

            using var pipe = new NamedPipeClientStream(".", BrokerWindows.PipePrefix + pipeId.ToString("N"),
                PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            using var handshake = new CancellationTokenSource(BrokerProtocol.HandshakeTimeout);
            await pipe.ConnectAsync(handshake.Token).ConfigureAwait(false);
            using var peer = BrokerWindows.Authenticate(pipe, peerIsServer: true, callerPid,
                BrokerWindows.CurrentSession(), BrokerWindows.CurrentSid(), BrokerWindows.AppName, out var callerElevated);
            var protocol = new BrokerProtocol(pipe);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await protocol.WriteAsync(new(BrokerMessageKind.Challenge, nonce), handshake.Token).ConfigureAwait(false);
            var message = await protocol.ReadAsync(handshake.Token).ConfigureAwait(false);
            BrokerProtocol.Expect(message, BrokerMessageKind.Request, nonce);
            var outcome = await ProcessRequestAsync(protocol, nonce, message, callerElevated,
                () => BrokerWindows.IsAlive(peer), handshake.Token).ConfigureAwait(false);
            await protocol.WriteAsync(new(BrokerMessageKind.Result, nonce) { Outcome = outcome }).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or Win32Exception or OperationCanceledException or
            UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return 2;
        }
    }

    private static async Task<RepairExecutionOutcome> ProcessRequestAsync(BrokerProtocol protocol, string nonce,
        BrokerMessage message, bool callerElevated, Func<bool> callerAlive, CancellationToken handshake)
    {
        var catalog = RepairCatalog.CreateDefault();
        if (message.Intent is not { Plan: not null } intent || message.Authorization is not { } token ||
            !catalog.TryGet(intent.OperationId, out var operation) || operation is null ||
            !NativeRepairPreflight.IsBound(catalog, intent, token, operation, DateTimeOffset.UtcNow))
            return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Plano, autorização ou catálogo divergente.");

        BrokerLease lease;
        try { lease = new BrokerLease(); }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 or 80 or 183)
        {
            return NativeOutcome.BeforeStart(RepairBrokerStatus.Conflict, "O broker já está atendendo outro reparo.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Estado protegido do broker indisponível.");
        }

        using (lease)
        {
            if (lease.HasUnresolvedExecution)
                return NativeOutcome.BeforeStart(RepairBrokerStatus.Conflict, "Execução anterior sem encerramento comprovado.");

            var preflight = NativeRepairPreflight.Create(catalog, new NativeRepairVolumes());
            var observed = await preflight.EvaluateAsync(operation, intent.Plan.Volume?.Id, handshake).ConfigureAwait(false);
            if (!NativeRepairPreflight.MatchesObservation(catalog, operation, intent.Plan, observed, callerElevated))
                return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Preflight privilegiado divergiu do plano.");

            await protocol.WriteAsync(new(BrokerMessageKind.Ready, nonce), handshake).ConfigureAwait(false);
            BrokerProtocol.Expect(await protocol.ReadAsync(handshake).ConfigureAwait(false), BrokerMessageKind.Commit, nonce);
            if (!callerAlive() || !NativeRepairPreflight.IsBound(catalog, intent, token, operation, DateTimeOffset.UtcNow))
                return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Solicitante encerrado ou autorização expirada.");
            try
            {
                if (!lease.Reserve(intent.Id, token.AuthorizationId, token.ExpiresAtUtc))
                    return NativeOutcome.BeforeStart(RepairBrokerStatus.Conflict, "Repetição ou bloqueio de reparo detectado.");
            }
            catch (IOException)
            {
                return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "A reserva durável não pôde ser gravada.");
            }

            if (!callerAlive() || !NativeRepairPreflight.IsBound(catalog, intent, token, operation, DateTimeOffset.UtcNow))
            {
                lease.ClearActive();
                return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Solicitante encerrou ou autorização expirou antes do início.");
            }

            var executor = new NativeRepairExecutor(catalog);
            var updates = Channel.CreateBounded<RepairProgress>(RepairLimits.MaxOutputLines + 1);
            var planExpires = intent.Plan.CreatedAtUtc + RepairLimits.AuthorizationLifetime;
            var deadline = planExpires < token.ExpiresAtUtc ? planExpires : token.ExpiresAtUtc;
            var execution = executor.ExecuteAsync(operation, observed.Volume, deadline,
                update => updates.Writer.TryWrite(update));
            var outcome = await ObserveAsync(protocol, nonce, execution, updates.Reader).ConfigureAwait(false);

            if (outcome.Native?.Status != RepairBrokerStatus.Indeterminate)
            {
                try { lease.ClearActive(); }
                catch (IOException)
                {
                    return outcome with
                    {
                        Limitations = [.. outcome.Limitations, "A reserva do broker pode continuar bloqueada. Requer revisão."]
                    };
                }
            }
            return outcome;
        }
    }

    private static async Task<RepairExecutionOutcome> ObserveAsync(BrokerProtocol protocol, string nonce,
        Task<RepairExecutionOutcome> execution, ChannelReader<RepairProgress> updates)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            do
            {
                var update = new RepairProgress("Broker acompanhando a ferramenta", timer.Elapsed, null)
                    { ExecutionCommitted = true };
                while (updates.TryRead(out var queued))
                {
                    update = queued;
                    await protocol.WriteAsync(new(BrokerMessageKind.Progress, nonce) { Progress = update }).ConfigureAwait(false);
                }
                if (execution.IsCompleted) break;
                await protocol.WriteAsync(new(BrokerMessageKind.Progress, nonce) { Progress = update with { Output = null } })
                    .ConfigureAwait(false);
                await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            } while (true);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            // Once committed, the tool runs to exit even if the requesting process disappears.
        }
        return await execution.ConfigureAwait(false);
    }
}
