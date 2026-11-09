using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;

namespace Eap.Api.Observability;

/// <summary>
/// Ajoute un jeton d'accès Google (identifiants par défaut : le compte de service de Cloud Run) aux requêtes d'export
/// OpenTelemetry vers Cloud Trace. Le jeton est renouvelé par la bibliothèque avant expiration : aucune clé à gérer.
/// </summary>
/// <remarks>
/// OpenTelemetry n'écrit pas ses échecs d'export dans les journaux de l'application (canal EventSource interne) :
/// sans ce signalement, des traces perdues passeraient inaperçues (constaté au premier déploiement). On journalise
/// le code HTTP et le type d'erreur seulement, jamais le corps de la réponse.
/// </remarks>
public sealed class GoogleAuthHandler(ICredential credential) : DelegatingHandler(new HttpClientHandler())
{
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var token = await credential.GetAccessTokenForRequestAsync(request.RequestUri?.ToString(), ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await base.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"Export des traces refusé par {request.RequestUri?.Host} : HTTP {(int)response.StatusCode}");
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Export des traces impossible ({ex.GetType().Name})");
            throw;
        }
    }
}
