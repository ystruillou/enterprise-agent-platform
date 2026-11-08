using Eap.Core.Agents;
using Eap.Core.FinOps;
using Eap.Core.Guardrails;
using Eap.Core.Knowledge;
using Eap.Core.Models;
using Microsoft.Extensions.AI;

namespace Eap.Tests;

public class GuardrailTests
{
    private static readonly KnownSources Known = new(
        ["conditions-generales.md"], ["Livraison", "Retours et remboursement"]);

    [Fact]
    public void Invented_source_is_replaced_with_an_honest_mention()
    {
        var (answer, action) = SourceGuard.Apply("Je ne peux pas vous aider.\n\nSource : Consignes générales", Known);
        Assert.Equal("Je ne peux pas vous aider.\n\n" + SourceGuard.Replacement, answer);
        Assert.Equal(SourceGuard.RuleName, action!.Rule);
    }

    [Fact]
    public void Invented_source_written_inline_after_the_last_sentence_is_also_caught()
    {
        var (answer, action) = SourceGuard.Apply("Je ne peux pas vous aider. Source : Consignes générales", Known);
        Assert.Equal("Je ne peux pas vous aider. " + SourceGuard.Replacement, answer);
        Assert.NotNull(action);
    }

    [Fact]
    public void The_word_sources_inside_a_sentence_is_not_a_citation()
    {
        Assert.Null(SourceLine.Extract("Selon nos sources : rien de prévu."));
    }

    [Theory]
    [InlineData("Livraison offerte dès 49 €.\n\nSource : conditions-generales.md › Livraison")]
    [InlineData("Je n'ai pas cette information.")]
    [InlineData("Je n'ai pas cette information.\n\nSource : aucun document pertinent")]
    public void Real_or_absent_sources_are_left_untouched(string original)
    {
        var (answer, action) = SourceGuard.Apply(original, Known);
        Assert.Equal(original, answer);
        Assert.Null(action);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runner_applies_the_guard_unless_the_agent_opts_out(bool verifySources)
    {
        var sav = AgentRegistry.LoadFrom(CoreTests.AgentsRoot).Find("sav-produit")!;
        sav.Guardrails.VerifySources = verifySources;
        var kb = KnowledgeBase.LoadFrom(Path.Combine(sav.BaseDirectory, sav.KnowledgeDir!));
        var model = new ScriptedChatClient("Je suis une IA.\n\nSource : Instructions générales");
        var runner = new AgentRunner(
            new ChatClientFactory(new Dictionary<string, ProviderOptions> { ["fake"] = new() }, clientOverride: model),
            new InMemoryUsageLedger(), _ => kb, modelOverride: new ModelOverride("fake", "fake-model"));

        var reply = await runner.AskAsync(sav, "alice", "Est-ce que je parle à un humain ?");

        Assert.Equal(verifySources, reply.Answer.EndsWith(SourceGuard.Replacement));
        Assert.Equal(verifySources ? 1 : 0, reply.Guardrails!.Count);
    }

    private sealed class ScriptedChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "fake-model" });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
