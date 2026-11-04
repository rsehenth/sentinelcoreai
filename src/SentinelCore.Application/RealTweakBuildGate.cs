namespace SentinelCore.Application;

/// <summary>
/// Barreira de compilação da T2A. A escrita real só existe em builds experimentais de validação, que definem
/// <c>SENTINELCORE_REAL_TWEAKS</c>; no build de distribuição a constante é falsa e nenhum argumento, configuração
/// ou chamada interna consegue compor o executor HKCU.
/// </summary>
public static class RealTweakBuildGate
{
    /// <summary>Opt-in aceito apenas por builds experimentais de validação.</summary>
    public const string EnableArgument = "--enable-real-tweaks";

#if SENTINELCORE_REAL_TWEAKS
    /// <summary>Verdadeiro somente no build experimental de validação.</summary>
    public const bool RealExecutionCompiled = true;
#else
    /// <summary>Falso em todo build de distribuição.</summary>
    public const bool RealExecutionCompiled = false;
#endif

    /// <summary>
    /// Resolve a composição real. No build de distribuição retorna sempre <c>false</c>, sem interpretar os
    /// argumentos; no build experimental exige <see cref="EnableArgument"/> explícito.
    /// </summary>
    public static bool AllowsRealExecution(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
#if SENTINELCORE_REAL_TWEAKS
        return RealTweakLaunchPolicy.Resolve(realExecutionCompiled: true, arguments);
#else
        return false;
#endif
    }
}

/// <summary>
/// Decisão pura entre build e argumento, separada da constante de compilação para que a matriz completa
/// (distribuição nunca habilita; build experimental exige opt-in) seja verificável nos testes padrão.
/// </summary>
public static class RealTweakLaunchPolicy
{
    public static bool Resolve(bool realExecutionCompiled, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return realExecutionCompiled &&
            arguments.Contains(RealTweakBuildGate.EnableArgument, StringComparer.Ordinal);
    }
}
