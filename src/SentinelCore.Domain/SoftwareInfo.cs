using System.Reflection;

namespace SentinelCore.Domain;

public static class SoftwareInfo
{
    /// <summary>
    /// Versão do produto, lida da versão informacional do assembly. A fonte única é
    /// <c>Directory.Build.props</c>; não existe literal duplicado aqui. Metadados de build (<c>+...</c>) são
    /// descartados. A ausência do atributo é erro explícito, não "versão desconhecida" gravada em registros.
    /// </summary>
    public static readonly string Version = ReadProductVersion();

    public const string SnapshotsSchemaVersion = "1";
    public const string EventSchemaVersion = "1";
    public const string PerformanceSchemaVersion = "2";
    public const string NetworkSchemaVersion = "1";
    public const string GraphicsSchemaVersion = "1";
    public const string CorrelationSchemaVersion = "1";
    public const string MonitoringSchemaVersion = "1";
    public const string TransactionSchemaVersion = "1";
    public const string RepairSchemaVersion = "1";

    private static string ReadProductVersion()
    {
        var informational = typeof(SoftwareInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException(
                "Versão do produto ausente no assembly; defina <Version> em Directory.Build.props.")
            : version;
    }
}
