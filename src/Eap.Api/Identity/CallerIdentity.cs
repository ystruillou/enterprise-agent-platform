using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Eap.Api.Identity;

/// <summary>Origine de l'identité de l'appelant.</summary>
public enum AuthMode
{
    /// <summary>En-tête libre <c>X-User-Id</c> : développement et tests uniquement, n'importe qui peut l'écrire.</summary>
    Header,
    /// <summary>Jeton signé par Identity-Aware Proxy (Google), vérifié par l'application : production.</summary>
    Iap,
}

public sealed class AuthOptions
{
    /// <summary>Par défaut le mode sûr : un oubli de configuration ferme l'accès au lieu de l'ouvrir.</summary>
    public AuthMode Mode { get; set; } = AuthMode.Iap;
    /// <summary>Audience attendue pour Cloud Run : <c>/projects/NUMERO/locations/REGION/services/SERVICE</c>.</summary>
    public string? IapAudience { get; set; }
}

/// <param name="UserId">Identifiant stable et pseudonyme (sert aux quotas et aux traces) : jamais l'adresse e-mail.</param>
/// <param name="Email">Adresse de l'utilisateur, renvoyée à lui seul (<c>/me</c>), ni journalisée ni tracée.</param>
public sealed record CallerIdentity(string UserId, string? Email);

/// <summary>Clés publiques qui signent les jetons IAP.</summary>
public interface IIapKeySource
{
    /// <param name="refresh">Recharger même si le cache est valide (jeton signé par une clé inconnue : rotation chez Google).</param>
    Task<IReadOnlyList<SecurityKey>> GetKeysAsync(bool refresh, CancellationToken ct);
}

/// <summary>Clés publiées par Google, gardées en cache une heure.</summary>
public sealed class GoogleIapKeySource(HttpClient http, TimeProvider time) : IIapKeySource
{
    public const string JwksUrl = "https://www.gstatic.com/iap/verify/public_key-jwk";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && time.GetUtcNow() < _expires) return _keys;
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh && time.GetUtcNow() < _expires) return _keys;
            var json = await http.GetStringAsync(JwksUrl, ct);
            _keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
            _expires = time.GetUtcNow() + CacheDuration;
            return _keys;
        }
        finally { _lock.Release(); }
    }
}

/// <summary>
/// Vérifie le jeton qu'IAP ajoute à chaque requête (<c>x-goog-iap-jwt-assertion</c>) : signature ES256 par une clé Google,
/// émetteur IAP, audience de CE service, validité dans le temps. IAP filtre déjà en amont ; cette vérification protège
/// contre une mauvaise configuration (IAP désactivé par erreur, accès qui contournerait le proxy) : défense en profondeur.
/// </summary>
public sealed class IapTokenValidator(IIapKeySource keys, string audience)
{
    public const string HeaderName = "x-goog-iap-jwt-assertion";
    public const string Issuer = "https://cloud.google.com/iap";

    private readonly JsonWebTokenHandler _handler = new();

    public async Task<CallerIdentity?> ValidateAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        foreach (var refresh in new[] { false, true })
        {
            var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = audience,
                IssuerSigningKeys = await keys.GetKeysAsync(refresh, ct),
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            });
            if (result.IsValid)
            {
                var sub = result.Claims.TryGetValue("sub", out var s) ? s?.ToString() : null;
                var email = result.Claims.TryGetValue("email", out var e) ? e?.ToString() : null;
                return string.IsNullOrEmpty(sub) ? null : new CallerIdentity(sub, email);
            }
            // Une seule cause justifie de recharger les clés : une clé inconnue (rotation). Le reste est un refus définitif.
            if (result.Exception is not SecurityTokenSignatureKeyNotFoundException) return null;
        }
        return null;
    }
}
