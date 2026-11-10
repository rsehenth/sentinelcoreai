using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

namespace SentinelCore.App.Localization;

/// <summary>Idioma da interface. Somente apresentação: valores persistidos, códigos e identificadores não mudam.</summary>
public enum AppLanguage
{
    English,
    Portuguese
}

/// <summary>Idioma corrente da interface, com inglês como padrão. Texto sem tradução permanece como gravado.</summary>
public static class Localizer
{
    private const string FileName = "ui-language.txt";

    private static string? file;
    private static ResourceDictionary? texts;

    public static AppLanguage Current { get; private set; } = AppLanguage.English;

    public static void Initialize(string dataRoot)
    {
        file = Path.Combine(dataRoot, FileName);
        try
        {
            Current = File.ReadAllText(file).Trim() == "pt" ? AppLanguage.Portuguese : AppLanguage.English;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Apply();
    }

    public static void Change(AppLanguage language)
    {
        if (language == Current) return;
        Current = language;
        Apply();
        try
        {
            if (file is not null) File.WriteAllText(file, language == AppLanguage.Portuguese ? "pt" : "en");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Instala os textos no aplicativo atual sem persistir a escolha (hosts de validação).</summary>
    public static void Use(AppLanguage language)
    {
        Current = language;
        Apply();
    }

    public static string T(string portuguese) =>
        Current == AppLanguage.Portuguese ? portuguese : Strings.English(portuguese);

    /// <summary>Troca o dicionário de recursos; os elementos ligados por DynamicResource são atualizados.</summary>
    private static void Apply()
    {
        if (System.Windows.Application.Current is not { } application) return;
        if (texts is not null) application.Resources.MergedDictionaries.Remove(texts);
        texts = Strings.CreateDictionary(Current);
        application.Resources.MergedDictionaries.Add(texts);
    }
}

/// <summary>Atalhos de tradução usados pela apresentação.</summary>
public static class Loc
{
    public static string T(string portuguese) => Localizer.T(portuguese);

    public static string F(LocString handler) => handler.ToString();
}

/// <summary>Interpolação que traduz cada fragmento literal e mantém os valores formatados.</summary>
[InterpolatedStringHandler]
public struct LocString
{
    private readonly StringBuilder text = new();

    public LocString(int literalLength, int formattedCount) { }

    public readonly void AppendLiteral(string value) => text.Append(Localizer.T(value));

    public readonly void AppendFormatted<T>(T value) => text.Append(value?.ToString());

    public readonly void AppendFormatted<T>(T value, string? format) =>
        text.Append(value is IFormattable formattable ? formattable.ToString(format, CultureInfo.CurrentCulture) : value?.ToString());

    public readonly void AppendFormatted<T>(T value, int alignment) => text.Append(Align(value?.ToString(), alignment));

    public readonly void AppendFormatted<T>(T value, int alignment, string? format) =>
        text.Append(Align(value is IFormattable formattable ? formattable.ToString(format, CultureInfo.CurrentCulture) : value?.ToString(), alignment));

    public readonly void AppendFormatted(string? value) => text.Append(value);

    public readonly void AppendFormatted(string? value, int alignment = 0, string? format = null) => text.Append(Align(value, alignment));

    public readonly override string ToString() => text.ToString();

    private static string Align(string? value, int alignment) => alignment switch
    {
        > 0 => (value ?? string.Empty).PadLeft(alignment),
        < 0 => (value ?? string.Empty).PadRight(-alignment),
        _ => value ?? string.Empty
    };
}
