using System.Collections.Concurrent;
using Eap.Core.Agents;
using Eap.Core.FinOps;
using Eap.Core.Knowledge;
using Eap.Core.Models;
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
builder.Services.AddSingleton<IUsageLedger, InMemoryUsageLedger>();
builder.Services.AddSingleton(TimeProvider.System);

var knowledgeCache = new ConcurrentDictionary<string, KnowledgeBase>();
ModelOverride? modelOverride = builder.Configuration["Platform:ProviderOverride"] is { Length: > 0 } forced
    ? new ModelOverride(forced, builder.Configuration["Platform:ModelOverride"] is { Length: > 0 } m ? m : "fake-model")
    : null;

builder.Services.AddSingleton(sp => new AgentRunner(
    sp.GetRequiredService<ChatClientFactory>(),
    sp.GetRequiredService<IUsageLedger>(),
    agent => knowledgeCache.GetOrAdd(agent.Id, _ => agent.KnowledgeDir is { Length: > 0 } dir
        ? KnowledgeBase.LoadFrom(Path.Combine(agent.BaseDirectory, dir))
        : KnowledgeBase.Empty),
    sp.GetRequiredService<TimeProvider>(),
    modelOverride));

// --- Traçabilité : OpenTelemetry, export OTLP (Langfuse, Cloud Trace, Jaeger…) ---------
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("enterprise-agent-platform"))
    .WithTracing(t =>
    {
        t.AddSource(AgentRunner.Activity.Name, ChatClientFactory.TelemetrySource)
         .AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation();
        // Activé seulement si OTEL_EXPORTER_OTLP_ENDPOINT est défini.
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            t.AddOtlpExporter();
    });

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", agents = registry.All.Count }));

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
    AgentRegistry reg, AgentRunner runner, CancellationToken ct) =>
{
    if (reg.Find(id) is not { } agent) return Results.NotFound(new { error = $"Agent inconnu : {id}" });
    if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4000)
        return Results.BadRequest(new { error = "Le message doit contenir entre 1 et 4000 caractères." });

    // Identité provisoire : en production, elle viendra de l'IdP (IAP / OIDC), pas d'un en-tête libre.
    var userId = http.Request.Headers["X-User-Id"].FirstOrDefault() is { Length: > 0 } u ? u : "anonymous";
    try
    {
        var reply = await runner.AskAsync(agent, userId, request.Message, ct);
        return Results.Ok(new
        {
            reply.Answer, reply.TraceId, reply.Sources,
            Usage = new
            {
                reply.Usage.Provider, reply.Usage.Model, reply.Usage.InputTokens, reply.Usage.OutputTokens,
                reply.Usage.CostUsd, reply.Usage.CostEstimated, LatencyMs = Math.Round(reply.Usage.LatencyMs),
            },
        });
    }
    catch (QuotaExceededException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
});

// Tableau de bord FinOps (agrégats du jour).
app.MapGet("/finops/usage", (IUsageLedger ledger, TimeProvider time, DateOnly? day) =>
{
    var d = day ?? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
    var lines = ledger.Summarize(d);
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
