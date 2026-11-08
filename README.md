# Enterprise Agent Platform

Plateforme de référence pour **déployer des agents IA métier en entreprise de façon gouvernée** :
multi-modèles (Gemini, Claude, GPT, Qwen), traçable, maîtrisée en coûts et évaluée avant la production.

> Projet portfolio construit sur un cas fictif de grande distribution (l'enseigne « Maison Kerlenn »).
> Il reproduit les livrables d'un déploiement de plateforme agentique : plateforme documentée,
> kit de gouvernance, cadre d'évaluation, guides d'exploitation et deux cas d'usage pilotes.

## Ce que la plateforme montre

| Exigence d'entreprise | Mise en œuvre |
|---|---|
| **Indépendance vis-à-vis du fournisseur** | Abstraction `Microsoft.Extensions.AI` ; le modèle se choisit dans la fiche agent (`gemini`, `anthropic`, `openai`, `qwen`) |
| **Gouvernance** | Chaque agent a une **fiche** versionnée (`agents/*/agent.yaml`) : finalité, responsables, statut de cycle de vie, risque AI Act, données personnelles, budget, seuil d'évaluation. Elle est validée au démarrage |
| **Traçabilité** | OpenTelemetry (conventions `gen_ai.*`), export OTLP vers Langfuse, Cloud Trace ou Jaeger. Le contenu des prompts n'est **pas** journalisé par défaut (RGPD) |
| **FinOps** | Coût calculé à chaque appel, agrégé par agent et par modèle, plafond quotidien par agent et quota par utilisateur |
| **Qualité** | Jeux de test et seuils de passage en production, contrôlés en CI *(semaine 3)* |
| **Sécurité** | Aucune clé dans le code (variables d'environnement, puis Secret Manager), validation des entrées *(IAM et IAP en semaine 4)* |

## Premier résultat : Gemini et Claude sur le même agent

Agent SAV, mêmes 5 questions, seul le modèle change (détails dans [docs/JOURNAL.md](docs/JOURNAL.md)) :

| | `gemini-3.5-flash-lite` | `claude-haiku-5-5` |
|---|---|---|
| Faits exacts | 5/5 | 5/5 |
| Sources inventées | 2/5 | 0/5 |
| Latence | ~0,8 s | ~2 s |
| Coût des 5 réponses | 0,00168 $ | 0,00130 $ |

## Architecture

```
            ┌──────────────── Eap.Api (ASP.NET Core, .NET 10) ────────────────┐
 Client ──▶ │ /agents (registre)   /agents/{id}/chat   /finops/usage   /health │
            └──────────┬───────────────────────────────────────────────────────┘
                       ▼
            AgentRunner : garde-fous FinOps → recherche de contexte (RAG) → appel modèle → registre de consommation
                       │                                         │
                       ▼                                         ▼
            ChatClientFactory ── IChatClient instrumenté ──▶ Gemini · Claude · GPT · Qwen · fake
                       │
                       └──▶ OpenTelemetry ──▶ OTLP (Langfuse / Cloud Trace)
```

- `agents/` : les fiches agents, prompts et bases de connaissances (le métier peut les relire dans Git).
- `src/Eap.Core` : registre, exécution, RAG, FinOps, fabrique de modèles.
- `src/Eap.Api` : API HTTP et page de démo.
- `tests/` : tests unitaires et d'intégration, sans clé API grâce au modèle simulé `fake`.

## Démarrer en local

Prérequis : SDK .NET 10.

```bash
dotnet test                          # 22 tests, aucun appel payant
dotnet run --project src/Eap.Api     # http://localhost:5xxx
```

En environnement `Development`, tous les agents utilisent le modèle simulé (`Platform:ProviderOverride = fake`).
Pour appeler les vrais modèles, videz ce paramètre et définissez les clés voulues :

```powershell
$env:GEMINI_API_KEY = "..."          # https://aistudio.google.com/apikey
$env:ANTHROPIC_API_KEY = "..."
$env:ASPNETCORE_ENVIRONMENT = "Production"   # désactive le modèle simulé de appsettings.Development.json
dotnet run --project src/Eap.Api --no-launch-profile
```

Pour comparer les fournisseurs sur un même agent, forcez-en un :
`$env:Platform__ProviderOverride = "qwen"; $env:Platform__ModelOverride = "qwen-plus"`.

## API

| Méthode | Route | Rôle |
|---|---|---|
| GET | `/agents` | Registre des agents (sans les prompts) |
| POST | `/agents/{id}/chat` | `{ "message": "..." }` → réponse, sources, trace, tokens, coût. En-tête `X-User-Id` provisoire |
| GET | `/finops/usage?day=AAAA-MM-JJ` | Consommation agrégée |
| GET | `/health` | Sonde de disponibilité |

## Feuille de route et retours d'expérience

- [docs/ROADMAP.md](docs/ROADMAP.md) : avancement semaine par semaine
- [docs/JOURNAL.md](docs/JOURNAL.md) : constats terrain (modèles retirés, alias qui dérivent, différences entre couches compatibles OpenAI…)

## Licence

MIT
