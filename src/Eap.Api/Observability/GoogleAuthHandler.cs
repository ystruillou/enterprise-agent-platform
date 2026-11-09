using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Eap.Api.Observability;

/// <summary>
/// Ajoute un jeton d'accès Google (identifiants par défaut : le compte de service de Cloud Run) aux requêtes d'export
/// OpenTelemetry vers Cloud Trace. Le jeton est renouvelé par la bibliothèque avant expiration : aucune clé à gérer.
/// </summary>
/// <remarks>
/// L'exportateur OTLP appelle la méthode SYNCHRONE <see cref="Send"/> : il faut la redéfinir aussi (documenté par
/// OpenTelemetry). Au premier déploiement, seule <see cref="SendAsync"/> l'était : l'appel synchrone contournait ce
/// gestionnaire et partait sans jeton (« unregistered callers »), sans aucune erreur visible.
/// On journalise le code HTTP et le type d'erreur seulement, jamais le corps de la réponse.
/// </remarks>
public sealed class GoogleAuthHandler : DelegatingHandler
{
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly ICredential _credential;

    /// <param name="inner">Gestionnaire suivant ; null quand c'est IHttpClientFactory qui le fournit.</param>
    public GoogleAuthHandler(ICredential credential, HttpMessageHandler? inner = null)
    {
        _credential = credential;
        if (inner is not null) InnerHandler = inner;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            await AuthorizeAsync(request, ct);
            return Report(request, await base.SendAsync(request, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw Failure(ex); }
    }

    // Chemin utilisé par l'exportateur OTLP (thread d'export en arrière-plan : l'attente bloquante y est sans effet
    // sur les requêtes des utilisateurs). Le jeton est mis en cache et renouvelé par la bibliothèque Google.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            AuthorizeAsync(request, ct).GetAwaiter().GetResult();
            return Report(request, base.Send(request, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw Failure(ex); }
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken ct) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await _credential.GetAccessTokenForRequestAsync(request.RequestUri?.ToString(), ct));

    private static HttpResponseMessage Report(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            Console.Error.WriteLine($"Export des traces refusé par {request.RequestUri?.Host} : HTTP {(int)response.StatusCode}");
        return response;
    }

    private static Exception Failure(Exception ex)
    {
        Console.Error.WriteLine($"Export des traces impossible ({ex.GetType().Name})");
        return ex is HttpRequestException ? ex : new HttpRequestException("Export des traces impossible.", ex);
    }
}

public static class CloudTraceExporter
{
    public static readonly Uri Endpoint = new("https://telemetry.googleapis.com/v1/traces");

    /// <summary>Nom du client HTTP qu'OpenTelemetry demande à IHttpClientFactory quand celui-ci est enregistré.</summary>
    public const string HttpClientName = "OtlpTraceExporter";

    /// <summary>
    /// Export OTLP vers Cloud Trace, authentifié. L'authentification est posée sur les deux chemins par lesquels
    /// l'exportateur obtient son client HTTP : sa propre fabrique, et le client nommé d'IHttpClientFactory (enregistré
    /// en production pour les clés IAP). Testé dans les deux configurations (CloudTraceExporterTests).
    /// </summary>
    /// <param name="primaryHandler">Pour les tests : remplace l'appel réseau.</param>
    public static TracerProviderBuilder AddCloudTraceExporter(this TracerProviderBuilder tracing, IServiceCollection services,
        ICredential credential, HttpMessageHandler? primaryHandler = null)
    {
        var client = services.AddHttpClient(HttpClientName).AddHttpMessageHandler(() => new GoogleAuthHandler(credential));
        if (primaryHandler is not null) client.ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        return tracing.AddOtlpExporter(o =>
        {
            o.Endpoint = Endpoint;
            o.Protocol = OtlpExportProtocol.HttpProtobuf;
            o.HttpClientFactory = () => new HttpClient(new GoogleAuthHandler(credential, primaryHandler ?? new HttpClientHandler()));
        });
    }
}
