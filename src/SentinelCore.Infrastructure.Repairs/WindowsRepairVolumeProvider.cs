using SentinelCore.Domain;

namespace SentinelCore.Infrastructure.Repairs;

/// <summary>
/// Enumera somente volumes locais fixos por meio de <see cref="DriveInfo"/>, em modo de leitura. Não desmonta,
/// não formata, não executa chkdsk e não aceita volume digitado pelo chamador.
/// </summary>
public sealed class WindowsRepairVolumeProvider : IRepairVolumeProvider
{
    public Task<IReadOnlyList<RepairVolume>> GetVolumesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<RepairVolume>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) return [];
            var volumes = new List<RepairVolume>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (drive.DriveType != DriveType.Fixed || drive.Name.Length < 2 ||
                        !RepairVolumeIdPolicy.TryNormalize(drive.Name[..2], out var id))
                        continue;
                    var limitations = new List<string>
                    {
                        "Somente volumes locais fixos com letra de unidade são listados; mount points e volumes dinâmicos não são representados.",
                        "O sistema de arquivos é reportado pelo Windows e não certifica integridade."
                    };
                    var ready = drive.IsReady;
                    string fileSystem;
                    try
                    {
                        fileSystem = ready ? drive.DriveFormat : "indisponível";
                    }
                    catch (Exception error)
                    {
                        fileSystem = "indisponível";
                        limitations.Add("Formato do volume indisponível: " + error.Message);
                    }
                    if (!ready) limitations.Add("Volume não está pronto; operações de verificação serão recusadas.");
                    long? total = null;
                    long? free = null;
                    if (ready)
                    {
                        try
                        {
                            total = drive.TotalSize;
                            free = drive.AvailableFreeSpace;
                        }
                        catch (Exception error)
                        {
                            limitations.Add("Capacidade do volume indisponível: " + error.Message);
                        }
                    }
                    var supportsScan = string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
                    if (!supportsScan)
                        limitations.Add("A verificação online /scan exige NTFS; este volume não é compatível.");
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? id
                        : $"{id} · {drive.VolumeLabel}";
                    volumes.Add(new RepairVolume(id, drive.RootDirectory.FullName, fileSystem,
                        drive.DriveType.ToString(), ready, total, free, true, supportsScan,
                        $"{label} · {fileSystem}", limitations));
                }
                catch (Exception error)
                {
                    volumes.Add(new RepairVolume("?", "", "indisponível", drive.DriveType.ToString(), false,
                        null, null, drive.DriveType == DriveType.Fixed, false,
                        $"Volume indisponível ({drive.Name})",
                        ["Volume não pôde ser lido: " + error.Message]));
                }
            }
            return volumes
                .Where(volume => RepairVolumeIdPolicy.IsWellFormed(volume.Id))
                .OrderBy(volume => volume.Id, StringComparer.Ordinal)
                .ToList();
        }, cancellationToken);
}
