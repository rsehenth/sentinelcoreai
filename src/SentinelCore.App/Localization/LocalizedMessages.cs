namespace SentinelCore.App.Localization;

/// <summary>Retains presentation renderers so a language change updates text without rerunning its operation.</summary>
internal sealed class LocalizedMessages
{
    private readonly Dictionary<string, Action> renderers = new(StringComparer.Ordinal);
    public void Set(string key, Action render)
    {
        renderers[key] = render;
        render();
    }
    public void Refresh()
    {
        foreach (var render in renderers.Values.ToArray()) render();
    }
}
