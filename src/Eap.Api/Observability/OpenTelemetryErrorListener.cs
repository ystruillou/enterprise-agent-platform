using System.Diagnostics.Tracing;

namespace Eap.Api.Observability;

/// <summary>
/// Signale dans les journaux (stderr, donc Cloud Logging) les erreurs de l'exportateur OTLP, émises sur EventSource et
/// invisibles sinon : sans lui, un export refusé fait simplement disparaître les traces (constaté au premier déploiement).
/// </summary>
/// <remarks>
/// Seulement le nom et l'identifiant de l'événement, jamais son contenu : il peut reprendre la réponse du service distant,
/// une URL ou des en-têtes. Le code HTTP d'un refus est journalisé par <see cref="GoogleAuthHandler"/>.
/// </remarks>
public sealed class OpenTelemetryErrorListener : EventListener
{
    public const string ExporterSource = "OpenTelemetry-Exporter-OpenTelemetryProtocol";

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == ExporterSource) EnableEvents(source, EventLevel.Warning);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e) =>
        Console.Error.WriteLine($"OpenTelemetry {e.Level} : {e.EventName} (événement {e.EventId}) — export des traces en échec");
}
