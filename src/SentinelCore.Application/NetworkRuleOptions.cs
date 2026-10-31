namespace SentinelCore.Application;

/// <summary>
/// Limiares das regras de rede. Todos exigem amostras e segundos mínimos: uma leitura isolada
/// nunca produz observação, e amostra insuficiente é "não avaliada", não "normal".
/// </summary>
public sealed record NetworkRuleOptions(
    int EndpointGrowthMinimumDelta = 200,
    double EndpointGrowthPercent = 50,
    int EndpointGrowthMinimumSamples = 10,
    double EndpointGrowthMinimumSeconds = 20,
    double ProcessConcentrationPercent = 60,
    int ProcessConcentrationMinimumEndpoints = 200,
    int ProcessConcentrationMinimumSamples = 8,
    double ProcessConcentrationMinimumSeconds = 15,
    double StateConcentrationPercent = 50,
    int StateConcentrationMinimumConnections = 500,
    int StateConcentrationMinimumSamples = 8,
    double StateConcentrationMinimumSeconds = 15,
    int DynamicPortMinimumEndpoints = 1_000,
    double DynamicPortMinimumSharePercent = 60,
    int DynamicPortMinimumSamples = 8,
    double DynamicPortMinimumSeconds = 15,
    uint AttemptFailureMinimumDelta = 50,
    int AttemptFailureMinimumSamples = 10,
    double AttemptFailureMinimumSeconds = 30,
    int EvidencePointLimit = 120,
    double MaxGapSeconds = 2.5)
{
    public static NetworkRuleOptions Default { get; } = new();
}
