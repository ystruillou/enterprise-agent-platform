using Eap.Core.Models;
using Microsoft.Extensions.AI;

namespace Eap.Tests;

/// <summary>Client de test qui mémorise les messages reçus et délègue au modèle simulé.</summary>
internal sealed class SpyChatClient : IChatClient
{
    private readonly FakeChatClient _inner = new("fake-model");

    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        return _inner.GetResponseAsync(LastMessages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        return _inner.GetStreamingResponseAsync(LastMessages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
