using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;

namespace Eap.Api.Observability;

/// <summary>
/// Ajoute un jeton d'accès Google (identifiants par défaut : le compte de service de Cloud Run) aux requêtes d'export
/// OpenTelemetry vers Cloud Trace. Le jeton est renouvelé par la bibliothèque avant expiration : aucune clé à gérer.
/// </summary>
public sealed class GoogleAuthHandler(ICredential credential) : DelegatingHandler(new HttpClientHandler())
{
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await credential.GetAccessTokenForRequestAsync(request.RequestUri?.ToString(), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}
