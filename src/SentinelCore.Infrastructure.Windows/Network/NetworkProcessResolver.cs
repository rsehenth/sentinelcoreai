using System.IO;
using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Windows.Network;

/// <summary>
/// Associa PIDs das tabelas de rede a processos reais usando o menor privilégio possível:
/// <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c>, nunca <c>PROCESS_ALL_ACCESS</c>.
/// A identidade é <c>PID + hora de criação</c>; o nome exposto é apenas o nome do arquivo,
/// nunca o caminho completo.
/// </summary>
internal sealed class NetworkProcessResolver
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int NameBuffer = 32_768;

    internal sealed record Entry(string? Identity, string? Name, NetworkProcessStatus Status);

    private static readonly Entry SystemIdle = new("0:system", "System Idle Process", NetworkProcessStatus.SystemProcess);
    private static readonly Entry SystemProcess = new("4:system", "System", NetworkProcessStatus.SystemProcess);
    private static readonly Entry Exited = new(null, null, NetworkProcessStatus.Exited);

    private readonly Dictionary<int, Entry> current = new();
    private readonly Dictionary<int, Entry> previous = new();
    private readonly List<string> notes = [];

    /// <summary>Limitações da amostra corrente: PID reutilizado e processos não identificados.</summary>
    public IReadOnlyList<string> Notes => notes;

    public void BeginTick()
    {
        current.Clear();
        notes.Clear();
    }

    public void EndTick()
    {
        previous.Clear();
        foreach (var pair in current) previous[pair.Key] = pair.Value;
    }

    public Entry Resolve(int pid)
    {
        if (cache(pid) is { } cached) return cached;
        var entry = Cache(pid, Read(pid));
        return entry;

        Entry? cache(int id) => current.TryGetValue(id, out var value) ? value : null;
        Entry Cache(int id, Entry value)
        {
            current[id] = value;
            if (previous.TryGetValue(id, out var old) && old.Identity is not null &&
                value.Identity is not null && old.Identity != value.Identity)
                notes.Add($"PID {id} reutilizado durante a coleta: a identidade mudou e conexões anteriores " +
                          "não são atribuídas ao novo processo.");
            return value;
        }
    }

    private static Entry Read(int pid)
    {
        if (pid == 0) return SystemIdle;
        if (pid == 4) return SystemProcess;

        var handle = NativeNetwork.OpenProcess(NativeNetwork.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return error switch
            {
                ErrorAccessDenied => new Entry(null, null, NetworkProcessStatus.AccessDenied),
                ErrorInvalidParameter => Exited,
                _ => new Entry(null, null, NetworkProcessStatus.Unavailable)
            };
        }

        try
        {
            if (!NativeNetwork.GetProcessTimes(handle, out var creation, out _, out _, out _) || creation <= 0)
                return new Entry(null, null, NetworkProcessStatus.Unavailable);

            DateTimeOffset start;
            try { start = new DateTimeOffset(DateTime.FromFileTimeUtc(creation)); }
            catch (ArgumentOutOfRangeException) { return new Entry(null, null, NetworkProcessStatus.Unavailable); }

            var name = ReadName(handle);
            var identity = Performance.PerformanceNormalizers.ProcessIdentity(pid, start);
            return new Entry(identity, name, NetworkProcessStatus.Resolved);
        }
        finally
        {
            NativeNetwork.CloseHandle(handle);
        }
    }

    private static string? ReadName(IntPtr handle)
    {
        var buffer = new char[NameBuffer];
        var size = buffer.Length;
        if (!NativeNetwork.QueryFullProcessImageNameW(handle, 0, buffer, ref size) || size <= 0) return null;
        var path = new string(buffer, 0, Math.Min(size, buffer.Length));
        try { return Path.GetFileName(path); }
        catch (ArgumentException) { return null; }
    }

    internal static string StatusName(NetworkProcessStatus status) => status switch
    {
        NetworkProcessStatus.Resolved => "resolvido",
        NetworkProcessStatus.AccessDenied => "acesso negado",
        NetworkProcessStatus.Exited => "processo encerrado",
        NetworkProcessStatus.SystemProcess => "processo do sistema",
        _ => "indisponível"
    };
}
