# Feuille de route

Objectif : couvrir les livrables d'une mission de déploiement de plateforme agentique en entreprise,
et acquérir l'expérience Google Cloud et Gemini Enterprise.

## Semaine 1 : socle multi-modèles ✅
- [x] Solution .NET 10, API, tests
- [x] Fiches agents YAML et registre validé (règles de gouvernance minimales)
- [x] Fabrique de modèles : Gemini, Claude, OpenAI, Qwen (API compatibles OpenAI) et modèle simulé
- [x] RAG minimal par mots-clés
- [x] FinOps : coût par appel, plafond quotidien, quota par utilisateur
- [x] OpenTelemetry (`gen_ai.*`) avec export OTLP optionnel
- [x] Agent pilote 1 : SAV produit ; agent 2 : conformité promo (fiche et prompt)
- [x] Premier appel réel à Gemini (clé AI Studio) : voir [JOURNAL.md](JOURNAL.md)
- [x] Comparaison avec Claude (Haiku 5.5) : voir [JOURNAL.md](JOURNAL.md)
- [ ] Claude via le SDK officiel `Anthropic` (réglage de l'effort de raisonnement) au lieu de la couche compatible OpenAI

## Semaine 2 : traçabilité et premiers vrais tests
- [ ] Langfuse (cloud gratuit ou auto-hébergé) branché en OTLP
- [ ] Versionnage des prompts (version de la fiche dans chaque trace)
- [ ] Persistance du registre de consommation (PostgreSQL)
- [ ] Test des 4 fournisseurs sur l'agent SAV, avec un tableau qualité / coût / latence

## Semaine 3 : cadre d'évaluation (avancé au 2026-11-08)
- [x] Jeu de test `evals/sav-produit.jsonl` : 22 cas en 7 catégories
- [x] Contrôles déterministes : faits, interdits, escalade, sources réelles, longueur, réponse complète
- [x] Modèle juge (facultatif), rapport Markdown, code de sortie selon le seuil
- [x] Workflow `evals.yml` qui bloque une PR sous le seuil (actif dès que les secrets d'API sont configurés)
- [x] Première campagne Gemini / Claude : voir [JOURNAL.md](JOURNAL.md)
- [x] Recherche sémantique et hybride (embeddings Gemini, fusion RRF), mesurée avant et après sur le jeu de test
- [x] Évaluation de la recherche seule (`--retrieval-only`) : rappel, bruit, calibrage du seuil de similarité
- [x] Garde-fou de sortie : vérification des sources citées (Gemini 82 % → 100 %, Claude 98 %, 3 passages)
- [x] Juge arbitre des contrôles par mots-clés (`suspect` + `rubric`) ; juge payant `claude-sonnet-5-5` (0,43 $ pour 66 réponses)
- [ ] Base vectorielle persistante (pgvector ou Vertex AI Vector Search) quand la base dépassera quelques centaines de sections
- [x] Campagnes répétées (`--repeat 3`) : le verdict publié est celui des 3 passages
- [ ] Garde-fou de pertinence : une source réelle mais hors sujet passe le contrôle d'existence (vu par le juge sur esc-02)
- [ ] Juge moins cher pour la CI (Haiku ou Flash payant), calibré contre Sonnet sur les mêmes réponses
- [x] Jeu de test de l'agent conformité promo (20 cas, 8 catégories), mode « base entière », limite de longueur propre à chaque fiche
- [x] Garde-fou et contrôle des sources adaptés aux citations en ligne ; relecteur 0.3.0 à 97 % (3 passages)

## Semaine 4 : déploiement Google Cloud (commencé le 2026-11-09, voir [DEPLOIEMENT.md](DEPLOIEMENT.md))
- [x] Projet GCP, budget de 10 €/mois et alertes, compte de service à moindre privilège
- [x] Cloud Run (Paris, accès privé IAM, 0 à 1 instance) et Secret Manager ; image construite par Cloud Build
- [ ] Gemini via **Vertex AI** (authentification IAM, pas de clé) en plus d'AI Studio
- [ ] Claude sans clé statique : fédération d'identité (Cloud Run et GitHub Actions vers l'API Anthropic), comparée à Claude via Vertex AI
- [ ] Cloud Logging structuré et Cloud Trace
- [ ] Authentification (IAP ou OIDC) qui remplace l'en-tête `X-User-Id`
- [x] Déploiement continu depuis GitHub Actions (Workload Identity Federation, sans clé, actions épinglées)

## Semaine 5 : écosystème Gemini Enterprise
- [ ] Agent conformité réécrit en Python avec l'**ADK** (Agent Development Kit)
- [ ] Déploiement sur **Vertex AI Agent Engine**
- [ ] Essai de Gemini Enterprise : console d'administration, connecteurs, Agent Designer, publication de l'agent ADK
- [ ] Note comparative : Gemini Enterprise, Claude Enterprise, ChatGPT Enterprise

## Semaine 6 : kit de gouvernance
- [ ] Cycle de vie d'un agent, de l'idée au retrait, avec ses étapes de validation
- [ ] RACI (métier, Solutions, Tech, CDO, DPO, RSSI)
- [ ] Modèle de fiche agent et registre généré automatiquement depuis les YAML
- [ ] Grille de classification AI Act et check-list RGPD (AIPD)

## Semaines 7-8 : exploitation et valorisation
- [ ] Guides d'exploitation (incident, dépassement de budget, changement de modèle)
- [ ] Tutoriel « déployer son premier agent en 1 h » (critère de transfert de compétences)
- [ ] Démo publique, vidéo de 3 minutes, article LinkedIn
- [ ] Certification Google Cloud Generative AI Leader
