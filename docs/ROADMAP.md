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
- [ ] Comparaison avec Claude

## Semaine 2 : traçabilité et premiers vrais tests
- [ ] Langfuse (cloud gratuit ou auto-hébergé) branché en OTLP
- [ ] Versionnage des prompts (version de la fiche dans chaque trace)
- [ ] Persistance du registre de consommation (PostgreSQL)
- [ ] Test des 4 fournisseurs sur l'agent SAV, avec un tableau qualité / coût / latence

## Semaine 3 : cadre d'évaluation
- [ ] Jeux de test `evals/*.jsonl` (question, faits attendus, refus attendu)
- [ ] Métriques : exactitude, ancrage dans les sources, refus hors périmètre, format
- [ ] LLM juge et vérifications déterministes
- [ ] Projet `Eap.Evals` et CI GitHub Actions qui **bloque** si le score passe sous `evaluation.min_score`

## Semaine 4 : déploiement Google Cloud
- [ ] Projet GCP, budget et alertes, comptes de service à moindre privilège
- [ ] Gemini via **Vertex AI** (authentification IAM, pas de clé) en plus d'AI Studio
- [ ] Claude sans clé statique : fédération d'identité (Cloud Run et GitHub Actions vers l'API Anthropic), comparée à Claude via Vertex AI
- [ ] Cloud Run, Secret Manager, Cloud Logging et Cloud Trace
- [ ] Authentification (IAP ou OIDC) qui remplace l'en-tête `X-User-Id`
- [ ] Déploiement continu depuis GitHub Actions (Workload Identity Federation)

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
