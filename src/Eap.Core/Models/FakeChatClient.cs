using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Eap.Core.Models;

/// <summary>
/// Modèle simulé, déterministe et gratuit : sert aux tests, à la CI et aux démos sans clé API.
/// Il renvoie le premier extrait du contexte fourni, ce qui permet de tester la chaîne RAG de bout en bout.
/// </summary>
public sealed class FakeChatClient(string modelId) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var context = list.Where(m => m.Role == ChatRole.System).Select(m => m.Text).LastOrDefault() ?? "";
        var excerpt = ExtractFirstSource(context);

        var text = excerpt is null
            ? "[réponse simulée] Je n'ai pas trouvé l'information dans la base de connaissances. Je vous oriente vers un conseiller."
            : $"[réponse simulée] D'après nos informations : {excerpt}";

        var inputTokens = list.Sum(m => EstimateTokens(m.Text));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = modelId,
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = EstimateTokens(text) },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private static string? ExtractFirstSource(string context)
    {
        const string marker = "[source:";
        var start = context.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var bodyStart = context.IndexOf('\n', start);
        if (bodyStart < 0) return null;
        var end = context.IndexOf(marker, bodyStart, StringComparison.Ordinal);
        var body = (end < 0 ? context[bodyStart..] : context[bodyStart..end]).Trim();
        return body.Length > 400 ? body[..400] + "…" : body;
    }

    /// <summary>Approximation grossière (≈ 4 caractères par token), suffisante pour un modèle simulé.</summary>
    private static long EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);
}
