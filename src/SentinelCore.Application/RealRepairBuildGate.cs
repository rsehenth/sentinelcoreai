namespace SentinelCore.Application;

/// <summary>
/// Barreira de compilação independente do Windows Repair Center. Não reutiliza o gate dos Tweaks.
/// Nesta fase nenhum executor real é composto, mesmo em build experimental: a constante
/// <see cref="RealExecutionImplemented"/> permanece falsa até uma etapa futura com validação real.
/// </summary>
public static class RealRepairBuildGate
{
    public const string EnableArgument = "--enable-real-repairs";

#if SENTINELCORE_REAL_REPAIRS
    public const bool RealExecutionCompiled = true;
#else
    public const bool RealExecutionCompiled = false;
#endif

    /// <summary>Falso em toda a R1: não existe executor real composto nesta fase.</summary>
    public const bool RealExecutionImplemented = false;

    public static bool AllowsRealExecution(IEnumerable<string> arguments) =>
        RealRepairLaunchPolicy.Resolve(RealExecutionCompiled, RealExecutionImplemented, arguments);
}

public static class RealRepairLaunchPolicy
{
    public static bool Resolve(bool compiled, bool implemented, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return compiled && implemented &&
            arguments.Contains(RealRepairBuildGate.EnableArgument, StringComparer.Ordinal);
    }
}
