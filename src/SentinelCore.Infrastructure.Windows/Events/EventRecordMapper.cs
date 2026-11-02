using System.Xml.Linq;
using SentinelCore.Domain;
using DomainEventProperty = SentinelCore.Domain.EventProperty;

namespace SentinelCore.Infrastructure.Windows;

/// <summary>
/// Conversão de um <see cref="EventRecord"/> nativo para o contrato comum de evento. É a única implementação do
/// mapeamento de XML, horário e dados; a investigação de eventos e a observação incremental do monitoramento
/// reutilizam este código em vez de manter cópias divergentes.
/// </summary>
internal static class EventRecordMapper
{
    public static RawEventRecord? Map(System.Diagnostics.Eventing.Reader.EventRecord record, string channel,
        int maxMessageChars, out bool truncated, out bool unavailable)
    {
        truncated = false;
        unavailable = false;
        string xml;
        try
        {
            xml = record.ToXml();
        }
        catch (Exception error) when (error is System.Diagnostics.Eventing.Reader.EventLogException
            or InvalidOperationException)
        {
            xml = string.Empty;
        }

        var occurred = ParseSystemTime(xml) ?? LegacyTime(record.TimeCreated);
        if (occurred is null) return null;

        string? message = null;
        try
        {
            message = record.FormatDescription();
        }
        catch (Exception error) when (error is System.Diagnostics.Eventing.Reader.EventLogException
            or InvalidOperationException or ArgumentException)
        {
            unavailable = true;
        }

        if (message is null) unavailable = true;
        var messageText = EventQuery.Truncate(message, maxMessageChars, out truncated);

        return new(channel, string.IsNullOrWhiteSpace(record.ProviderName) ? "Desconhecido" : record.ProviderName,
            record.Id, record.RecordId, occurred.Value, EventQuery.Level(record.Level), record.Level, record.Task,
            record.ActivityId, messageText, truncated, ParseData(xml));
    }

    public static IReadOnlyList<DomainEventProperty> ParseData(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        try
        {
            var document = XDocument.Parse(xml);
            var values = document.Descendants()
                .Where(element => element.Name.LocalName == "Data")
                .Take(64)
                .Select((element, index) =>
                {
                    var text = EventQuery.Truncate(element.Value, 512, out var valueTruncated);
                    return new DomainEventProperty(element.Attribute("Name")?.Value ?? $"Data[{index}]",
                        (text ?? string.Empty) + (valueTruncated ? "…" : string.Empty));
                })
                .ToArray();
            if (values.Length > 0) return values;

            var userData = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "UserData");
            if (userData is null) return [];
            return userData.Descendants()
                .Where(element => !element.HasElements)
                .Take(64)
                .Select(element =>
                {
                    var text = EventQuery.Truncate(element.Value, 512, out var valueTruncated);
                    return new DomainEventProperty(element.Name.LocalName,
                        (text ?? string.Empty) + (valueTruncated ? "…" : string.Empty));
                })
                .ToArray();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    public static DateTimeOffset? ParseSystemTime(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var attribute = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "TimeCreated")
                ?.Attribute("SystemTime");
            if (attribute is not null && DateTimeOffset.TryParse(attribute.Value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var value))
                return value.ToUniversalTime();
        }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException)
        {
        }
        return null;
    }

    private static DateTimeOffset? LegacyTime(DateTime? value)
    {
        if (value is not { } date) return null;
        var normalized = date.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(date, DateTimeKind.Local)
            : date;
        return new DateTimeOffset(normalized).ToUniversalTime();
    }
}
