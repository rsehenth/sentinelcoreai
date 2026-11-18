namespace SentinelCore.Application;

// Dois gates independentes dos de Tweaks, Reparos e Debloat. Fechados por padrão e SEM argumento de ativação:
// executores implementados não autorizam execução; mesmo um build experimental permanece fechado.
// A barreira é verificada na composição (App) e na fronteira do gerenciador, não apenas no botão.
// RealExecutionCompiled é um indicador legado de habilitação; não prova ausência de código nativo no binário.

public static class StartupModificationBuildGate
{
#if SENTINELCORE_REAL_STARTUP
    public const bool RealExecutionCompiled = true;
#else
    public const bool RealExecutionCompiled = false;
#endif
    public const bool RealExecutionImplemented = true;

    public static bool PermitsOperationalExecution => false;
}

public static class ApplicationUninstallBuildGate
{
#if SENTINELCORE_REAL_UNINSTALL
    public const bool RealExecutionCompiled = true;
#else
    public const bool RealExecutionCompiled = false;
#endif
    public const bool RealExecutionImplemented = true;

    public static bool PermitsOperationalExecution => false;
}
