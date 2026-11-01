namespace SentinelCore.Domain;

/// <summary>
/// Anotação de qualidade histórica derivada em leitura. Nunca é gravada no envelope durável e nunca contém um
/// valor "corrigido": o dado nativo original não foi preservado, então só é possível declarar o que não é
/// verificável.
/// </summary>
public sealed record NetworkQualityAnnotation(
    string DefectId, string Summary, string AffectedCollectorVersion, string FixedCollectorVersion,
    int AffectedEndpoints, IReadOnlyList<string> AffectedFields, bool ValuesProvenWrong);

/// <summary>
/// Defeito D1 da Fase 3.3A: o coletor de endpoints 1.0.0 lia <c>dwRemoteScopeId</c> e <c>dwRemotePort</c> de
/// <c>MIB_TCP6ROW_OWNER_PID</c> nos offsets 36 e 40, quando o layout oficial os coloca em 40 e 44. A versão 1.0.1
/// usa os offsets oficiais. O discriminador é a versão do coletor gravada em cada sessão, não a versão do
/// produto: ela identifica exatamente o parser que produziu o registro.
/// </summary>
public static class NetworkQualityCatalog
{
    public const string EndpointCollectorId = "network-endpoints";
    public const string DefectiveCollectorVersion = "1.0.0";
    public const string FixedCollectorVersion = "1.0.1";
    public const string Tcp6RemoteOffsetDefectId = "NET-TCP6-REMOTE-OFFSET";

    public static NetworkQualityAnnotation? Evaluate(NetworkSession session)
    {
        var usedDefectiveParser = session.Sources.Any(source =>
            string.Equals(source.CollectorId, EndpointCollectorId, StringComparison.Ordinal)
            && string.Equals(source.CollectorVersion, DefectiveCollectorVersion, StringComparison.Ordinal));
        if (!usedDefectiveParser || !session.IncludesEndpointDetails) return null;

        var affected = session.EndpointDetails.Count(endpoint =>
            endpoint.Protocol == NetworkProtocol.Tcp && endpoint.Family == NetworkAddressFamily.IPv6);
        if (affected == 0) return null;

        return new(Tcp6RemoteOffsetDefectId,
            $"Qualidade histórica: {affected} linha(s) TCP IPv6 foram lidas pelo coletor {DefectiveCollectorVersion}, "
            + "que obtinha porta remota e escopo do endereço remoto de offsets incorretos (36/40 em vez de 40/44). "
            + "Esses dois campos são não verificáveis nesta sessão: o dado nativo original não foi preservado. "
            + "Nenhum valor corrigido é inferido. Porta local, estado e PID não dependem desses offsets; as contagens "
            + "só poderiam ter sido reduzidas se duas conexões IPv6 distintas diferissem apenas pela porta remota, "
            + "pois a porta remota compõe a chave de deduplicação.",
            DefectiveCollectorVersion, FixedCollectorVersion, affected,
            ["remotePort", "remoteScopeId"], ValuesProvenWrong: false);
    }
}
