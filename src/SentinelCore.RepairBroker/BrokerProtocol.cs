using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using SentinelCore.Domain;

namespace SentinelCore.RepairBroker;

internal enum BrokerMessageKind { Unknown, Challenge, Request, Ready, Commit, Progress, Result }

internal sealed record BrokerMessage(BrokerMessageKind Kind, string Nonce, int Version = 1)
{
    public RepairIntent? Intent { get; init; }
    public RepairAuthorizationToken? Authorization { get; init; }
    public RepairProgress? Progress { get; init; }
    public RepairExecutionOutcome? Outcome { get; init; }
}

[JsonSourceGenerationOptions(MaxDepth = 24, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BrokerMessage))]
[JsonSerializable(typeof(BrokerLeaseState))]
internal sealed partial class BrokerJson : JsonSerializerContext;

internal sealed class BrokerProtocol(PipeStream pipe)
{
    internal const int MaxFrameBytes = 256 * 1024;
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(20);

    internal async Task<BrokerMessage> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FrameTimeout);
        var prefix = new byte[4];
        await pipe.ReadExactlyAsync(prefix, deadline.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Quadro IPC fora do limite.");
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize(payload, BrokerJson.Default.BrokerMessage);
        if (message is null || message.Version != 1 || !Enum.IsDefined(message.Kind) ||
            message.Kind == BrokerMessageKind.Unknown || !ValidNonce(message.Nonce))
            throw new InvalidDataException("Quadro IPC inválido.");
        return message;
    }

    internal async Task WriteAsync(BrokerMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, BrokerJson.Default.BrokerMessage);
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Quadro IPC fora do limite.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FrameTimeout);
        await pipe.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
    }

    internal static bool ValidNonce(string? nonce) =>
        nonce is { Length: 64 } && nonce.All(char.IsAsciiHexDigitUpper);

    internal static void Expect(BrokerMessage message, BrokerMessageKind kind, string nonce)
    {
        if (message.Kind != kind || !string.Equals(message.Nonce, nonce, StringComparison.Ordinal))
            throw new InvalidDataException("Sequência ou desafio IPC inválido.");
    }
}
