using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Repairs;

/// <summary>Leitura do perfil do sistema sem elevação, sem escrita e sem executar ferramentas.</summary>
public sealed class WindowsRepairSystemProfileReader : IRepairSystemProfileReader
{
    private readonly TimeProvider time;

    public WindowsRepairSystemProfileReader(TimeProvider? time = null) =>
        this.time = time ?? TimeProvider.System;

    public RepairSystemProfile Read()
    {
        var limitations = new List<string>();
        var version = Environment.OSVersion.Version;
        var windows = OperatingSystem.IsWindows();
        var windows11 = windows && version.Build >= 22000;
        var is64 = Environment.Is64BitOperatingSystem;
        var elevated = false;
        try
        {
            elevated = Environment.IsPrivilegedProcess;
        }
        catch (Exception error)
        {
            limitations.Add("Estado de elevação indisponível: " + error.Message);
        }

        limitations.Add(RepairGuidance.ElevationLimitation);
        if (!windows11)
            limitations.Add("Windows 11 é obrigatório para o Repair Center.");
        if (elevated)
            limitations.Add("O processo está elevado; nenhuma elevação adicional é solicitada.");

        return new RepairSystemProfile(
            windows11 ? $"Windows 11 build {version.Build}" : $"Windows build {version.Build}",
            version.Build, windows11, elevated, is64, "Windows API (somente leitura)",
            time.GetUtcNow(), limitations);
    }
}
