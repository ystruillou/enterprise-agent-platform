using System.Diagnostics.Tracing;

namespace Eap.Api.Observability;

/// <summary>
/// Recopie dans les journaux (stderr, donc Cloud Logging) les avertissements et erreurs internes d'OpenTelemetry,
/// émis sur EventSource et invisibles sinon. Sans lui, un export refusé (droits, quota, réponse partielle) fait
/// simplement disparaître les traces. Niveau Warning et plus : pas de bruit en fonctionnement normal.
/// </summary>
public sealed class OpenTelemetryErrorListener : EventListener
{
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
            EnableEvents(source, EventLevel.Warning);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        var message = e.Message is { } format && e.Payload is { Count: > 0 } payload
            ? SafeFormat(format, payload)
            : string.Join(" | ", e.Payload ?? []);
        Console.Error.WriteLine($"OpenTelemetry {e.Level} [{e.EventSource.Name}] {message}");
    }

    private static string SafeFormat(string format, IReadOnlyCollection<object?> payload)
    {
        try { return string.Format(format, [.. payload]); }
        catch (FormatException) { return format + " | " + string.Join(" | ", payload); }
    }
}
