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
| **RAG** | Recherche par mots-clés, sémantique, hybride (fusion RRF) ou base entière (référentiel court à appliquer en totalité), choisie dans la fiche ; seuil de similarité calibré ; repli automatique sur les mots-clés si les embeddings sont indisponibles |
| **Garde-fous** | Vérification des sources citées avant de répondre : une source absente de la base est remplacée, l'intervention est tracée |
| **Qualité** | [Cadre d'évaluation](evals/README.md) : jeux de test, contrôles déterministes (dont la détection des **sources inventées**), modèle juge, seuil de passage en production inscrit dans la fiche et appliqué en CI |
| **Qualité** (suite) | Modèle juge en **arbitre** des contrôles par mots-clés ambigus, et en second regard sur toutes les réponses |
| **Sécurité** | Aucune clé dans le code : Secret Manager en production, variables d'environnement en local ; validation des entrées |
| **Déploiement** | [Cloud Run à Paris](docs/DEPLOIEMENT.md), connexion Google par IAP (jeton vérifié par l'application), compte de service à moindre privilège, budget avec alertes |

## Résultats d'évaluation de l'agent SAV

22 cas de test, seuil de passage en production à 85 % (détails et enseignements dans [docs/JOURNAL.md](docs/JOURNAL.md)).

**Comparaison de modèles** (recherche par mots-clés, 1 passage) :

| | `gemini-3.5-flash-lite` | `claude-haiku-5-5` |
|---|---|---|
| Score | 77 %, ❌ refusé | 86 %, ✅ validé |
| Sources inventées | 3 | 0 |
| Latence médiane | 0,6 s | 1,7 s |
| Coût des 22 cas | 0,0068 $ | 0,0048 $ |

**Amélioration mesurée étape par étape** (Gemini, même prompt, mêmes cas, 3 passages sauf mention) :

| Étape | Réponses factuelles justes | Sources inventées visibles | Score |
|---|---|---|---|
| Recherche par mots-clés (1 passage) | 14/16 | 3 | 77 % ❌ |
| + recherche hybride (embeddings + mots-clés) | 48/48 | 12 | 82 % ❌ |
| + garde-fou de sortie sur les sources | 48/48 | 0 (9 corrigées) | **100 %** ✅ |

**Comparaison finale** (recherche hybride + garde-fou, 3 passages) : Gemini 100 %, 0,6 s ; Claude Haiku 98 %, 2,0 s,
et seulement 2 interventions du garde-fou, inoffensives, contre 9 vraies inventions corrigées chez Gemini.

## Architecture

```
            ┌──────────────── Eap.Api (ASP.NET Core, .NET 10) ────────────────┐
 Client ──▶ │ /agents (registre)   /agents/{id}/chat   /finops/usage   /health │
            └──────────┬───────────────────────────────────────────────────────┘
                       ▼
            AgentRunner : garde-fous FinOps → recherche hybride (RAG) → appel modèle → garde-fous → registre de consommation
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
dotnet test                          # 113 tests, aucun appel payant
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
- [docs/DEPLOIEMENT.md](docs/DEPLOIEMENT.md) : déploiement Google Cloud, choix de sécurité et commandes
- [docs/JOURNAL.md](docs/JOURNAL.md) : constats terrain (modèles retirés, alias qui dérivent, différences entre couches compatibles OpenAI…)

## Licence

MIT
