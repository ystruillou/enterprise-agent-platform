using System.Diagnostics;
using System.Net;
using Eap.Api.Observability;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Eap.Tests;

public class CloudTraceExporterTests
{
    private sealed class FakeCredential : ICredential
    {
        public Task<string> GetAccessTokenForRequestAsync(string? authUri = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("jeton-de-test");
        public void Initialize(ConfigurableHttpClient httpClient) { }
    }

    /// <summary>Tient lieu de Google : enregistre ce qui est envoyé, répond 200.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri? Url, string? Authorization)> Requests { get; } = [];
        // L'exportateur OTLP appelle la version synchrone : les deux sont redéfinies, comme dans GoogleAuthHandler.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Send(request, ct));
    }

    private sealed class SomeTypedClient(HttpClient http) { public HttpClient Http { get; } = http; }

    // Le span passe par le vrai exportateur OTLP. true = conditions de production (IHttpClientFactory enregistré pour les
    // clés IAP). Ce test échouait dans les deux cas avant la redéfinition de Send : l'exportateur appelle la version
    // synchrone, qui contournait l'authentification (traces envoyées sans jeton au premier déploiement).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Spans_are_exported_to_Cloud_Trace_with_the_service_account_token(bool httpClientFactoryRegistered)
    {
        var google = new RecordingHandler();
        var source = new ActivitySource("test-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection().AddLogging();
        if (httpClientFactoryRegistered) services.AddHttpClient<SomeTypedClient>();
        services.AddOpenTelemetry().WithTracing(t => t
            .AddSource(source.Name)
            .AddCloudTraceExporter(services, new FakeCredential(), google));

        await using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        using (source.StartActivity("agent sav-produit")) { }
        Assert.True(tracer.ForceFlush(10_000));

        var export = Assert.Single(google.Requests);
        Assert.Equal(CloudTraceExporter.Endpoint, export.Url);
        Assert.Equal("Bearer jeton-de-test", export.Authorization);
    }
}
