using System.Xml.Linq;
using SentinelCore.Application;
using SentinelCore.Domain;
using Xunit;

namespace SentinelCore.Tests;

/// <summary>Contratos de apresentação do Application legado. As views Debloat do App foram removidas.</summary>
public sealed class DebloatPresentationTests
{
    [Fact]
    public void NativeLabelsDistinguishOriginsStatesAndCompleteness()
    {
        Assert.Equal(DebloatPresentation.NativeInventoryOrigin,
            DebloatPresentation.InventorySource(DebloatInventoryOrigin.NativeAppx));
        Assert.Equal(DebloatPresentation.InventoryOrigin,
            DebloatPresentation.InventorySource(DebloatInventoryOrigin.Synthetic));
        Assert.Equal("Consulta não executada — inventário não consultado.",
            DebloatPresentation.QueryState(DebloatInventoryQueryState.NotExecuted));
        Assert.Equal("Consulta concluída sem resultados — inventário vazio observado.",
            DebloatPresentation.QueryState(DebloatInventoryQueryState.CompletedNoResults));
        Assert.Contains("parcialmente", DebloatPresentation.QueryState(DebloatInventoryQueryState.Partial));
        Assert.Contains("negada", DebloatPresentation.QueryState(DebloatInventoryQueryState.AccessDenied));
        Assert.Contains("interrompida", DebloatPresentation.QueryState(DebloatInventoryQueryState.Interrupted));
        Assert.Contains("Falha de enumeração", DebloatPresentation.QueryState(DebloatInventoryQueryState.Failed));
        Assert.Contains("indisponível", DebloatPresentation.QueryState(DebloatInventoryQueryState.Unavailable));
        Assert.Equal(DebloatNativeMessages.OutOfCatalog,
            DebloatPresentation.CatalogMatch(DebloatCatalogMatchKind.NotInCatalog));
        Assert.Equal("Presente no inventário nativo (não homologado).",
            DebloatPresentation.Presence(DebloatPresence.PresentInNativeInventory));
        Assert.Equal(DebloatPresentation.NativeInventoryOrigin,
            DebloatPresentation.Origin(DebloatPresence.PresentInNativeInventory));
        Assert.Contains("parcial", DebloatPresentation.Completeness(DebloatObservationCompleteness.Partial));
    }
}
