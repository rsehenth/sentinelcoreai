using System.ComponentModel;

namespace SentinelCore.App.Localization;

internal interface ILocalizedChoice
{
    void RefreshLanguage();
}

/// <summary>The key and object identity stay fixed when only the display label changes.</summary>
internal sealed class LocalizedChoice<T>(T value, Func<string> label) : INotifyPropertyChanged, ILocalizedChoice
{
    public LocalizedChoice(T value, string portugueseLabel) : this(value, () => portugueseLabel) { }
    public T Value { get; } = value;
    public string Label => Loc.T(label());
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}
