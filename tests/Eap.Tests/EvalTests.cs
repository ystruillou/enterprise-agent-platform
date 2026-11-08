using Eap.Core.Guardrails;
using Eap.Evals;

namespace Eap.Tests;

public class EvalTests
{
    private static readonly KnownSources Known = new(
        ["catalogue.md", "conditions-generales.md"],
        ["Retours et remboursement", "Garantie", "Aspirateur sans fil Kerlenn Cyclone (réf. AS-CY2)"]);

    private static EvalCase Case(Expectations e) => new() { Id = "t", Category = "test", Question = "q", Expect = e };

    [Theory]
    [InlineData("Source : catalogue.md › Aspirateur sans fil Kerlenn Cyclone (réf. AS-CY2)", true)]
    [InlineData("Source : conditions-generales.md (Garantie)", true)]
    [InlineData("Source : catalogue.md ; conditions-generales.md", true)]
    [InlineData("Source : catalogue.md › Aspirateur / conditions-generales.md › Garantie", true)]
    [InlineData("Source : aucun document pertinent trouvé", true)]
    [InlineData("**Source :** Garantie", true)]
    [InlineData("Source : conditions-generales.md (Retours et remboursement)", true)]
    [InlineData("Source : conditions-generales.md, section Retours et remboursement", true)]
    [InlineData("Source : catalogue.md (Aspirateur sans fil Kerlenn Cyclone, Garantie) ; conditions-generales.md (Garantie)", true)]
    [InlineData("Source : catalogue", true)]
    [InlineData("Source : conditions-generales.md › Livraison express premium", false)]
    [InlineData("Source : Consignes de service", false)]
    [InlineData("Source : Catalogue général Maison Kerlenn (document introuvable)", false)]
    [InlineData("Source : catalogue.md et Consignes générales Maison Kerlenn", false)]
    public void Invented_sources_are_detected(string sourceLine, bool expectedValid)
    {
        var result = ResponseChecks.CheckSources($"Une réponse.\n\n{sourceLine}", SourceRequirement.Required, Known);
        Assert.Equal(expectedValid, result.Passed);
    }

    [Fact]
    public void Missing_source_fails_only_when_required()
    {
        Assert.False(ResponseChecks.CheckSources("Réponse sans source.", SourceRequirement.Required, Known).Passed);
        Assert.True(ResponseChecks.CheckSources("Réponse sans source.", SourceRequirement.Optional, Known).Passed);
    }

    [Fact]
    public void Expectations_tolerate_accents_formatting_and_decimal_separators()
    {
        var checks = ResponseChecks.Run(
            Case(new Expectations { All = ["39,90", "eau froide"], Any = [["30 jours", "trente jours"]], Source = SourceRequirement.Optional }),
            "Elle coûte **39.90 €**, lavable à l'EAU FROIDE, retour sous trente jours.", Known);
        Assert.All(checks, c => Assert.True(c.Passed, $"{c.Name} {c.Detail}"));
    }

    [Fact]
    public void Forbidden_content_and_missing_escalation_fail()
    {
        var checks = ResponseChecks.Run(
            Case(new Expectations { None = ["€"], Escalate = true, Source = SourceRequirement.Optional }),
            "Le grille-pain coûte 24,90 €.", Known);
        Assert.Contains(checks, c => c is { Name: "ne contient pas", Passed: false });
        Assert.Contains(checks, c => c is { Name: "escalade vers un conseiller", Passed: false });
    }

    [Fact]
    public void Escalation_phone_is_recognised_whatever_the_formatting()
    {
        var checks = ResponseChecks.Run(Case(new Expectations { Escalate = true, Source = SourceRequirement.Optional }),
            "Contactez un conseiller au 02.00.00.00.00.", Known);
        Assert.Contains(checks, c => c is { Name: "escalade vers un conseiller", Passed: true });
    }

    [Theory]
    [InlineData("Phrase un. Phrase deux. Phrase trois.\n\nSource : catalogue.md", 3)]
    [InlineData("La bouilloire (réf. BK-170) coûte 39,90 €. Elle chauffe vite.", 2)]
    [InlineData("Voici :\n* 45 minutes en éco\n* 12 minutes en turbo", 3)]
    public void Sentences_are_counted_without_abbreviations_and_prices(string text, int expected)
    {
        Assert.Equal(expected, ResponseChecks.CountSentences(text));
    }

    [Theory]
    [InlineData("""{"score": 4, "raison": "correct"}""", 4)]
    [InlineData("```json\n{\"score\":2,\"raison\":\"source inventée\"}\n```", 2)]
    [InlineData("""{"score": 9}""", null)]
    [InlineData("pas de json", null)]
    public void Judge_output_is_parsed_defensively(string text, int? expected)
    {
        Assert.Equal(expected, LlmJudge.Parse(text).Score);
    }

    [Fact]
    public void Dataset_of_sample_agent_is_valid()
    {
        var path = Path.Combine(Path.GetDirectoryName(CoreTests.AgentsRoot)!, "evals", "sav-produit.jsonl");
        var cases = EvalCase.LoadJsonl(path);
        Assert.True(cases.Count >= 20);
        Assert.Contains(cases, c => c.Expect.Escalate);
        Assert.Contains(cases, c => c.Category == "securite");
    }
}
