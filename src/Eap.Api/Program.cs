using Eap.Api.FinOps;
using Eap.Api.Identity;
using Eap.Api.Observability;
using Eap.Core.Agents;
using Eap.Core.FinOps;
using Eap.Core.Knowledge;
using Eap.Core.Models;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Registre des agents -------------------------------------------------------------
var agentsPath = builder.Configuration["Platform:AgentsPath"] is { Length: > 0 } configured
    ? configured
    : FindAgentsDirectory(builder.Environment.ContentRootPath);
var registry = AgentRegistry.LoadFrom(agentsPath);
builder.Services.AddSingleton(registry);

// --- Fournisseurs de modèles, FinOps, exécution ----------------------------------------
var providers = builder.Configuration.GetSection("Providers").Get<Dictionary<string, ProviderOptions>>()
    ?? throw new InvalidOperationException("Section 'Providers' absente de la configuration.");
builder.Services.AddSingleton(new ChatClientFactory(
    new Dictionary<string, ProviderOptions>(providers, StringComparer.OrdinalIgnoreCase)));
// Registre de consommation : en mémoire par défaut (développement, tests) ; Firestore en production, pour que les
// plafonds de dépense survivent aux redémarrages et soient partagés entre instances.
var gcpProject = builder.Configuration["Gcp:ProjectId"];
if (string.Equals(builder.Configuration["FinOps:Ledger"], "Firestore", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(gcpProject))
        throw new InvalidOperationException("Gcp:ProjectId est obligatoire avec FinOps:Ledger = Firestore.");
    var firestore = new FirestoreDbBuilder
    {
        ProjectId = gcpProject,
        DatabaseId = builder.Configuration["FinOps:FirestoreDatabase"] is { Length: > 0 } dbId ? dbId : "(default)",
        EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction, // émulateur si FIRESTORE_EMULATOR_HOST
    }.Build();
    builder.Services.AddSingleton<IUsageLedger>(new FirestoreUsageLedger(firestore));
}
else
{
    builder.Services.AddSingleton<IUsageLedger, InMemoryUsageLedger>();
}
builder.Services.AddSingleton(TimeProvider.System);

ModelOverride? modelOverride = builder.Configuration["Platform:ProviderOverride"] is { Length: > 0 } forced
    ? new ModelOverride(forced, builder.Configuration["Platform:ModelOverride"] is { Length: > 0 } m ? m : "fake-model")
    : null;

// Embeddings : "fake" en développement (sans clé), sinon le fournisseur déclaré dans chaque fiche.
builder.Services.AddSingleton(sp => new KnowledgeRetrieverFactory(
    sp.GetRequiredService<ChatClientFactory>(),
    builder.Configuration["Platform:EmbeddingProviderOverride"] is { Length: > 0 } e ? e : null));

builder.Services.AddSingleton(sp => new AgentRunner(
    sp.GetRequiredService<ChatClientFactory>(),
    sp.GetRequiredService<IUsageLedger>(),
    sp.GetRequiredService<KnowledgeRetrieverFactory>().For,
    sp.GetRequiredService<TimeProvider>(),
    modelOverride));

// --- Identité de l'appelant ----------------------------------------------------------
// Production : jeton IAP vérifié. Développement et tests : en-tête X-User-Id (voir appsettings.Development.json).
var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
if (auth.Mode == AuthMode.Iap)
{
    // Échec au démarrage plutôt qu'un service qui accepterait n'importe quel jeton.
    if (string.IsNullOrWhiteSpace(auth.IapAudience))
        throw new InvalidOperationException("Auth:IapAudience est obligatoire en mode IAP (/projects/NUMERO/locations/REGION/services/SERVICE).");
    builder.Services.AddHttpClient<GoogleIapKeySource>();
    builder.Services.AddSingleton<IIapKeySource>(sp => sp.GetRequiredService<GoogleIapKeySource>());
    builder.Services.AddSingleton(sp => new IapTokenValidator(sp.GetRequiredService<IIapKeySource>(), auth.IapAudience));
}

// --- Traçabilité : OpenTelemetry, export OTLP (Cloud Trace, Langfuse, Jaeger…) ---------
var cloudTrace = builder.Configuration.GetValue<bool>("Observability:CloudTrace");
// Les échecs internes d'OpenTelemetry (export refusé…) sont recopiés dans les journaux : sans cela, ils sont muets.
// Gardé par le conteneur de services : un écouteur collecté par le GC cesserait d'écouter.
builder.Services.AddSingleton(new OpenTelemetryErrorListener());
if (cloudTrace && string.IsNullOrWhiteSpace(gcpProject))
    throw new InvalidOperationException("Gcp:ProjectId est obligatoire avec Observability:CloudTrace.");
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r =>
    {
        r.AddService("enterprise-agent-platform");
        if (cloudTrace) r.AddAttributes([new("gcp.project_id", gcpProject!)]); // projet de destination des traces
    })
    .WithTracing(t =>
    {
        t.AddSource(AgentRunner.Activity.Name, ChatClientFactory.TelemetrySource)
         .AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation();
        // Cloud Trace : point d'entrée OTLP de Google, authentifié par le compte de service de Cloud Run (aucune clé).
        // Le contenu des prompts et des réponses n'est pas tracé (EnableSensitiveData = false, voir ChatClientFactory).
        if (cloudTrace)
            t.AddOtlpExporter("cloudtrace", o =>
            {
                o.Endpoint = new Uri("https://telemetry.googleapis.com/v1/traces");
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                var credential = GoogleCredential.GetApplicationDefault().CreateScoped(GoogleAuthHandler.Scope);
                o.HttpClientFactory = () => new HttpClient(new GoogleAuthHandler(credential));
            });
        // Autre destination (Langfuse, Jaeger…) : activée seulement si OTEL_EXPORTER_OTLP_ENDPOINT est défini.
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            t.AddOtlpExporter();
    });

var app = builder.Build();
if (cloudTrace) app.Logger.LogInformation("Export des traces actif vers Cloud Trace (projet {Project})", gcpProject);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", agents = registry.All.Count }));

// Toutes les routes de données exigent une identité ; /health et la page statique n'exposent rien.
app.Use(async (http, next) =>
{
    var path = http.Request.Path;
    if (!(path.StartsWithSegments("/agents") || path.StartsWithSegments("/finops") || path.StartsWithSegments("/me")))
    {
        await next();
        return;
    }
    // En développement (mode Header), tout appelant est administrateur : il n'y a de toute façon aucune authentification.
    CallerIdentity? caller = auth.Mode == AuthMode.Header
        ? new(http.Request.Headers["X-User-Id"].FirstOrDefault() is { Length: > 0 } u ? u : "anonymous", null, IsAdmin: true)
        : await http.RequestServices.GetRequiredService<IapTokenValidator>()
            .ValidateAsync(http.Request.Headers[IapTokenValidator.HeaderName].FirstOrDefault(), http.RequestAborted);
    if (caller is { Email: { } email } && auth.Mode == AuthMode.Iap) caller = caller with { IsAdmin = auth.IsAdmin(email) };
    if (caller is null)
    {
        // Message neutre : on ne dit pas pourquoi le jeton est refusé.
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await http.Response.WriteAsJsonAsync(new { error = "Authentification requise." });
        return;
    }
    http.Items[nameof(CallerIdentity)] = caller;
    await next();
});

// L'utilisateur connecté voit sa propre adresse ; elle n'est ni journalisée ni tracée.
app.MapGet("/me", (HttpContext http) =>
{
    var caller = (CallerIdentity)http.Items[nameof(CallerIdentity)]!;
    return Results.Ok(new { caller.Email, AuthMode = auth.Mode.ToString(), caller.IsAdmin });
});

// Registre public des agents (vue "fiche agent", sans le prompt).
app.MapGet("/agents", (AgentRegistry reg) => reg.All
    .OrderBy(a => a.Id)
    .Select(a => new
    {
        a.Id, a.Name, a.Version, Status = a.Status.ToString(), a.Purpose,
        a.Owners, Risk = new { AiAct = a.Risk.AiAct.ToString(), a.Risk.PersonalData },
        Model = new { a.Model.Provider, a.Model.Name }, a.Budget,
    }));

app.MapPost("/agents/{id}/chat", async (string id, ChatRequest request, HttpContext http,
    AgentRegistry reg, AgentRunner runner, ILogger<Program> logger, CancellationToken ct) =>
{
    if (reg.Find(id) is not { } agent) return Results.NotFound(new { error = $"Agent inconnu : {id}" });
    if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4000)
        return Results.BadRequest(new { error = "Le message doit contenir entre 1 et 4000 caractères." });

    // Identité établie par le middleware (jeton IAP vérifié en production) : sert aux quotas par utilisateur.
    var userId = ((CallerIdentity)http.Items[nameof(CallerIdentity)]!).UserId;
    try
    {
        var reply = await runner.AskAsync(agent, userId, request.Message, ct);
        return Results.Ok(new
        {
            reply.Answer, reply.TraceId, reply.Sources, reply.Truncated,
            Usage = new
            {
                reply.Usage.Provider, reply.Usage.Model, reply.Usage.ServedModel,
                reply.Usage.InputTokens, reply.Usage.OutputTokens,
                reply.Usage.CostUsd, reply.Usage.CostEstimated, LatencyMs = Math.Round(reply.Usage.LatencyMs),
            },
        });
    }
    catch (QuotaExceededException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (ModelProviderException ex)
    {
        return ProviderUnavailable(ex);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Registre de consommation (Firestore) ou autre dépendance indisponible : refus propre, sans détail au client.
        logger.LogError("Dépendance indisponible pour l'agent {AgentId} ({ExceptionType})", id, ex.GetType().Name);
        return Results.Json(new { error = "Service momentanément indisponible. Réessayez plus tard." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    IResult ProviderUnavailable(ModelProviderException ex)
    {
        // Pas de pile d'exception ni de contenu externe dans les journaux : seulement des champs maîtrisés.
        logger.LogError("Échec du fournisseur {Provider} / {Model} pour l'agent {AgentId} : HTTP {HttpStatus} {ProviderErrorCode} ({ExceptionType})",
            ex.Provider, ex.Model, id, ex.HttpStatus, ex.ProviderErrorCode, ex.InnerException?.GetType().Name);
        // Le détail technique reste dans les journaux ; le client reçoit un message neutre.
        return Results.Json(new { error = "Le service de modèle est momentanément indisponible. Réessayez plus tard." },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

// Tableau de bord FinOps (agrégats du jour).
app.MapGet("/finops/usage", async (HttpContext http, IUsageLedger ledger, TimeProvider time, DateOnly? day, CancellationToken ct) =>
{
    // Consommation de tous les utilisateurs et coûts : réservé aux administrateurs.
    if (!((CallerIdentity)http.Items[nameof(CallerIdentity)]!).IsAdmin)
        return Results.Json(new { error = "Réservé aux administrateurs." }, statusCode: StatusCodes.Status403Forbidden);
    var d = day ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
    var lines = await ledger.SummarizeAsync(d, ct);
    return Results.Ok(new { Day = d, TotalUsd = lines.Sum(l => l.CostUsd), Lines = lines });
});

app.Run();

static string FindAgentsDirectory(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "agents");
        if (Directory.Exists(candidate)) return candidate;
    }
    throw new DirectoryNotFoundException("Dossier 'agents' introuvable ; renseignez Platform:AgentsPath.");
}

public sealed record ChatRequest(string Message);

public partial class Program;
