using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Eap.Api.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Eap.Tests;

public class IdentityTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Audience = "/projects/123/locations/europe-west9/services/eap-api";
    private static readonly ECDsaSecurityKey GoogleKey = NewKey("google-1");

    private static ECDsaSecurityKey NewKey(string kid) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = kid };

    private static string Token(ECDsaSecurityKey? key = null, string issuer = IapTokenValidator.Issuer, string audience = Audience,
        DateTime? expires = null, string sub = "accounts.google.com:42") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = sub, ["email"] = "alice@example.com" },
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            IssuedAt = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            SigningCredentials = new SigningCredentials(key ?? GoogleKey, SecurityAlgorithms.EcdsaSha256),
        });

    /// <summary>Clés « publiées par Google » ; la première lecture peut être périmée pour simuler une rotation.</summary>
    private sealed class FakeKeySource(params IReadOnlyList<SecurityKey>[] successiveKeySets) : IIapKeySource
    {
        public int Fetches { get; private set; }
        private IReadOnlyList<SecurityKey>? _cached;
        public Task<IReadOnlyList<SecurityKey>> GetKeysAsync(bool refresh, CancellationToken ct)
        {
            if (_cached is null || refresh) _cached = successiveKeySets[Math.Min(Fetches++, successiveKeySets.Length - 1)];
            return Task.FromResult(_cached);
        }
    }

    private static IapTokenValidator Validator(IIapKeySource? keys = null) => new(keys ?? new FakeKeySource([GoogleKey]), Audience);

    [Fact]
    public async Task A_genuine_IAP_token_gives_a_pseudonymous_user_id()
    {
        var caller = await Validator().ValidateAsync(Token());
        Assert.Equal("accounts.google.com:42", caller!.UserId);
        Assert.Equal("alice@example.com", caller.Email);
    }

    public static TheoryData<string, Func<string>> ForgedTokens => new()
    {
        { "signé par une autre clé", () => Token(key: NewKey("google-1")) },  // même identifiant de clé, autre clé privée
        { "audience d'un autre service", () => Token(audience: "/projects/123/locations/europe-west9/services/autre") },
        { "émetteur autre qu'IAP", () => Token(issuer: "https://accounts.google.com") },
        { "expiré", () => Token(expires: DateTime.UtcNow.AddMinutes(-5)) },
        { "pas un jeton", () => "abc.def.ghi" },
        { "absent", () => "" },
    };

    [Theory]
    [MemberData(nameof(ForgedTokens))]
    public async Task Forged_or_misdirected_tokens_are_refused(string why, Func<string> token)
    {
        Assert.True(await Validator().ValidateAsync(token()) is null, why);
    }

    [Fact]
    public async Task Keys_are_reloaded_once_when_Google_rotates_them()
    {
        var rotated = NewKey("google-2");
        var keys = new FakeKeySource([GoogleKey], [GoogleKey, rotated]); // le cache ne connaît pas encore google-2
        Assert.NotNull(await Validator(keys).ValidateAsync(Token(key: rotated)));
        Assert.Equal(2, keys.Fetches);
    }

    [Fact]
    public async Task A_bad_signature_does_not_trigger_a_reload_loop()
    {
        var keys = new FakeKeySource([GoogleKey]);
        Assert.Null(await Validator(keys).ValidateAsync(Token(audience: "/autre")));
        Assert.Equal(1, keys.Fetches);
    }

    private sealed class CountingHandler(string jwks) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks) });
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Forced_key_reloads_are_rate_limited()
    {
        var handler = new CountingHandler("""{"keys":[]}""");
        var clock = new ManualClock();
        var source = new GoogleIapKeySource(new HttpClient(handler), clock);

        await source.GetKeysAsync(refresh: false, default);       // 1er chargement
        for (var i = 0; i < 50; i++)
            await source.GetKeysAsync(refresh: true, default);    // rafale de jetons à clé inconnue
        Assert.Equal(1, handler.Calls);

        clock.Now += GoogleIapKeySource.MinRefreshInterval;       // une minute plus tard, une vraie rotation reste possible
        await source.GetKeysAsync(refresh: true, default);
        Assert.Equal(2, handler.Calls);
    }

    // --- Intégration : l'API en mode IAP --------------------------------------------------

    private HttpClient IapClient() => factory
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("Platform:AgentsPath", CoreTests.AgentsRoot);
            b.UseSetting("Platform:ProviderOverride", "fake");
            b.UseSetting("Platform:EmbeddingProviderOverride", "fake");
            b.UseSetting("Auth:Mode", "Iap");
            b.UseSetting("Auth:IapAudience", Audience);
            b.ConfigureTestServices(s => s.AddSingleton<IIapKeySource>(new FakeKeySource([GoogleKey])));
        })
        .CreateClient();

    [Fact]
    public async Task In_IAP_mode_data_routes_require_a_valid_token_and_the_free_header_is_ignored()
    {
        var client = IapClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "je-me-fais-passer-pour-un-autre");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/agents/sav-produit/chat", new { message = "Prix de la bouilloire ?" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode); // n'expose rien
    }

    [Fact]
    public async Task In_IAP_mode_a_signed_user_is_recognised()
    {
        var client = IapClient();
        client.DefaultRequestHeaders.Add(IapTokenValidator.HeaderName, Token());

        var me = await client.GetFromJsonAsync<Dictionary<string, string>>("/me");
        Assert.Equal("alice@example.com", me!["email"]);
        var chat = await client.PostAsJsonAsync("/agents/sav-produit/chat", new { message = "Prix de la bouilloire ?" });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
    }

    [Fact]
    public void IAP_mode_without_an_audience_refuses_to_start()
    {
        var ex = Assert.ThrowsAny<Exception>(() => factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Platform:AgentsPath", CoreTests.AgentsRoot);
            b.UseSetting("Auth:Mode", "Iap");
            b.UseSetting("Auth:IapAudience", "");
        }).CreateClient());
        Assert.Contains("IapAudience", ex.ToString());
    }
}
