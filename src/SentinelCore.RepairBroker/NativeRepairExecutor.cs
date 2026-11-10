using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SentinelCore.Application;
using SentinelCore.Domain;
using SentinelCore.Infrastructure.Repairs;

namespace SentinelCore.RepairBroker;

internal sealed class NativeRepairExecutor(RepairCatalog catalog)
{
    internal async Task<RepairExecutionOutcome> ExecuteAsync(RepairOperationDescriptor approved, RepairVolume? volume,
        DateTimeOffset deadline, Action<RepairProgress> progress)
    {
        var operation = catalog.Get(approved.Id);
        var tool = new WindowsRepairToolLocator().Resolve(operation.Tool);
        if (!RepairCenter.BrokerExecutionEnabled || !Environment.Is64BitProcess || operation != approved ||
            operation.RequiresElevation && !Environment.IsPrivilegedProcess ||
            operation.IsDocumentedOnly || operation.Availability != RepairAvailability.Available ||
            operation.Risk is not (RiskLevel.Low or RiskLevel.Medium) || !tool.Available || tool.Path is null)
            return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Escopo, volume, privilégio ou executável divergiu.");

        var invocation = RepairInvocationBuilder.Build(operation, volume, tool);
        if (invocation.Arguments.Any(argument => !RepairArgumentPolicy.IsAllowedForExecutable(operation.Tool, argument)))
            return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Argumentos recusados pelo catálogo.");

        var startInfo = new ProcessStartInfo(tool.Path)
        {
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);
        var windows = Directory.GetParent(Environment.SystemDirectory)!.FullName;
        startInfo.Environment.Clear();
        startInfo.Environment["SystemRoot"] = windows;
        startInfo.Environment["windir"] = windows;
        startInfo.Environment["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd(Path.DirectorySeparatorChar);
        startInfo.Environment["PATH"] = Environment.SystemDirectory;
        startInfo.Environment["TEMP"] = Path.Combine(windows, "Temp");
        startInfo.Environment["TMP"] = startInfo.Environment["TEMP"];

        using var process = new Process { StartInfo = startInfo };
        var launched = false;
        try
        {
            using var toolImage = new FileStream(tool.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (volume is not null && (volume.VolumeIdentity is null ||
                    BrokerWindows.VolumeIdentity(volume.RootPath) != volume.VolumeIdentity) ||
                DateTimeOffset.UtcNow >= deadline)
                return NativeOutcome.BeforeStart(RepairBrokerStatus.Refused, "Volume divergente ou plano expirado no início.");
            if (!process.Start())
                return NativeOutcome.BeforeStart(RepairBrokerStatus.StartFailed, "O Windows não retornou um processo.");
            launched = true;
        }
        catch (Win32Exception error) when (!launched)
        {
            var status = error.NativeErrorCode is 5 or 740 ? RepairBrokerStatus.StartDenied : RepairBrokerStatus.StartFailed;
            return NativeOutcome.BeforeStart(status, $"Inicialização recusada pelo Windows (código {error.NativeErrorCode}).");
        }
        catch (UnauthorizedAccessException) when (!launched)
        {
            return NativeOutcome.BeforeStart(RepairBrokerStatus.StartDenied, "A imagem da ferramenta não pôde ser aberta.");
        }
        catch (IOException) when (!launched)
        {
            return NativeOutcome.BeforeStart(RepairBrokerStatus.StartFailed, "A imagem da ferramenta não pôde ser confirmada.");
        }

        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var output = new NativeOutputCapture(progress, timer);
        try
        {
            progress(new("Ferramenta iniciada", timer.Elapsed, operation.ExecutableName)
                { ExecutionCommitted = true, ProcessStartedUtc = started });
            using var stdout = process.StandardOutput.BaseStream;
            using var stderr = process.StandardError.BaseStream;
            var drains = Task.WhenAll(Task.Run(() => output.DrainAsync(stdout, RepairOutputChannel.Stdout)),
                Task.Run(() => output.DrainAsync(stderr, RepairOutputChannel.Stderr)));
            // Closing stdin prevents interactive replies to tool prompts.
            process.StandardInput.Close();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var exited = DateTimeOffset.UtcNow;
            if (exited < started) exited = started;
            var duration = timer.Elapsed;
            var code = process.ExitCode;
            var drained = await Task.WhenAny(drains, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == drains;
            if (drained) await drains.ConfigureAwait(false);
            var (lines, incomplete) = output.Finish();
            var abnormal = unchecked((uint)code) is >= 0xC0000000 and <= 0xCFFFFFFF;
            return new(true, !abnormal, false, abnormal, code, null, incomplete || !drained, lines,
                started, exited, duration, false, abnormal ? ["Término anormal do processo."] : [])
            {
                Native = new(abnormal ? RepairBrokerStatus.Indeterminate : RepairBrokerStatus.ProcessExited,
                    false, started, exited)
            };
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception)
        {
            var (lines, _) = output.Finish();
            return new(true, false, false, true, null, null, true, lines,
                started, DateTimeOffset.UtcNow, timer.Elapsed, false, ["Não foi possível confirmar o encerramento da ferramenta."])
            {
                Native = new(RepairBrokerStatus.Indeterminate, false, started, null)
            };
        }
    }
}

internal static class NativeOutcome
{
    internal static RepairExecutionOutcome BeforeStart(RepairBrokerStatus status, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        return new(false, false, status == RepairBrokerStatus.CancelledBeforeStart,
            status == RepairBrokerStatus.Indeterminate, null, detail, false, [], now, now, TimeSpan.Zero, false,
            [detail]) { Native = new(status, false, null, null) };
    }
}

internal sealed class NativeOutputCapture(Action<RepairProgress> progress, Stopwatch timer)
{
    private readonly List<RepairOutputLine> lines = [];
    private readonly string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private int bytes;
    private bool incomplete;
    private bool finished;

    internal async Task DrainAsync(Stream stream, RepairOutputChannel channel)
    {
        var line = new StringBuilder(RepairLimits.MaxOutputLineChars);
        var overlong = false;
        var chars = new char[1024];
        try
        {
            var header = new byte[4];
            var read = 0;
            while (read < header.Length)
            {
                var count = await stream.ReadAsync(header.AsMemory(read)).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }
            var encoding = DetectEncoding(header.AsSpan(0, read), out var bom);
            // The prefix is small and belongs to the same decoder as the rest of the stream.
            var decoder = encoding.GetDecoder();
            var buffer = new byte[1024];
            Consume(decoder.GetChars(header.AsSpan(bom, read - bom), chars, flush: false));
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                Consume(decoder.GetChars(buffer.AsSpan(0, read), chars, flush: false));
            Consume(decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true));
            AddLine(line, channel, overlong);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or DecoderFallbackException)
        {
            lock (lines) incomplete = true;
        }

        void Consume(int count)
        {
            for (var index = 0; index < count; index++)
            {
                var character = chars[index];
                if (character is '\r' or '\n')
                {
                    AddLine(line, channel, overlong);
                    line.Clear();
                    overlong = false;
                }
                else if (line.Length >= RepairLimits.MaxOutputLineChars || character is '\0' or '\uFFFD')
                {
                    overlong = true;
                }
                else if (!overlong)
                {
                    line.Append(character);
                }
            }
        }
    }

    private static Encoding DetectEncoding(ReadOnlySpan<byte> prefix, out int bom)
    {
        bom = 0;
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var marker = encoding.GetPreamble();
            if (!prefix.StartsWith(marker)) continue;
            bom = marker.Length;
            return encoding;
        }
        if (prefix.Length == 4 && prefix[0] != 0 && prefix[1] == 0 && prefix[2] != 0 && prefix[3] == 0)
            return Encoding.Unicode;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)BrokerWindows.GetOEMCP());
    }

    private void AddLine(StringBuilder line, RepairOutputChannel channel, bool overlong)
    {
        lock (lines)
        {
            if (finished) return;
            if (overlong)
            {
                incomplete = true;
                return;
            }
            if (line.Length == 0) return;
            if (lines.Count >= RepairLimits.MaxOutputLines)
            {
                incomplete = true;
                return;
            }
            var text = RepairOutputLimiter.Sanitize(line.ToString(), profile, out var redactedTruncated);
            if (redactedTruncated || bytes + Encoding.UTF8.GetByteCount(text) > RepairLimits.MaxOutputBytes)
            {
                incomplete = true;
                return;
            }
            if (text.Length == 0) return;
            var item = new RepairOutputLine(lines.Count + 1, text) { Channel = channel };
            lines.Add(item);
            bytes += Encoding.UTF8.GetByteCount(text);
            progress(new("Ferramenta em execução", timer.Elapsed, null) { Output = item, ExecutionCommitted = true });
        }
    }

    internal (RepairOutputLine[] Lines, bool Incomplete) Finish()
    {
        lock (lines)
        {
            finished = true;
            return (lines.ToArray(), incomplete);
        }
    }
}
