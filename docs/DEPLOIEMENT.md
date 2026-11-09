# Déploiement sur Google Cloud

L'API tourne sur **Cloud Run**, en région **europe-west9 (Paris)**, avec un **accès privé** (IAM). Pas de Docker en
local : l'image est construite par Cloud Build à partir du `Dockerfile`.

## Architecture

```
Utilisateur Google autorisé ──(jeton d'identité)──▶ Cloud Run « eap-api » (europe-west9, 0 à 1 instance)
                                                      │  identité : eap-api-runtime (compte de service dédié)
                                                      ├─▶ Secret Manager : gemini-api-key, anthropic-api-key (lecture seule)
                                                      ├─▶ API Gemini (chat + embeddings)
                                                      └─▶ API Anthropic (Claude)
Budget de facturation : 10 €/mois, alertes à 50, 90 et 100 %
```

## Choix et raisons

| Choix | Raison |
|---|---|
| Accès privé (`--no-allow-unauthenticated`) | L'identité `X-User-Id` est encore un en-tête libre : en public, n'importe qui pourrait consommer les crédits des modèles. Sans jeton : HTTP 403 |
| Compte de service dédié, un seul rôle | Moindre privilège : `secretmanager.secretAccessor` sur ses 2 secrets, rien d'autre (pas le compte Compute par défaut, trop large) |
| Clés dans Secret Manager, réplication à Paris | Jamais dans l'image, le dépôt ou les variables en clair ; injectées par Cloud Run au démarrage |
| 0 à 1 instance | Coût nul au repos ; une seule instance garde les plafonds FinOps en mémoire cohérents (registre de consommation encore en mémoire) |
| Image non root, fiches agents incluses | Les prompts et bases de connaissances sont versionnés avec le code : une révision Cloud Run = une version d'agent traçable |
| europe-west9 | Données traitées en France (le traitement chez les fournisseurs de modèles reste à couvrir contractuellement) |

## Commandes

Prérequis : projet avec facturation, `gcloud auth login`, `gcloud config set project eap-portfolio`.

```powershell
gcloud config set run/region europe-west9
gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com `
    secretmanager.googleapis.com billingbudgets.googleapis.com

# Budget (identifiant du compte de facturation : gcloud billing accounts list)
gcloud billing budgets create --billing-account=<COMPTE> --display-name="eap-portfolio 10 EUR" --budget-amount=10EUR `
    --filter-projects=projects/eap-portfolio --threshold-rule=percent=0.5 --threshold-rule=percent=0.9 --threshold-rule=percent=1.0

# Identité de l'API
gcloud iam service-accounts create eap-api-runtime --display-name="EAP API (Cloud Run)"

# Secrets : créés depuis un fichier temporaire supprimé aussitôt, la clé n'est jamais affichée
gcloud secrets create gemini-api-key --replication-policy=user-managed --locations=europe-west9 --data-file=<fichier>
gcloud secrets add-iam-policy-binding gemini-api-key `
    --member="serviceAccount:eap-api-runtime@eap-portfolio.iam.gserviceaccount.com" --role=roles/secretmanager.secretAccessor
# (idem pour anthropic-api-key)

# Construction (Cloud Build) et déploiement
gcloud run deploy eap-api --source . --service-account eap-api-runtime@eap-portfolio.iam.gserviceaccount.com `
    --no-allow-unauthenticated --min-instances 0 --max-instances 1 --memory 512Mi `
    --set-secrets "GEMINI_API_KEY=gemini-api-key:latest,ANTHROPIC_API_KEY=anthropic-api-key:latest" `
    --set-env-vars "ASPNETCORE_ENVIRONMENT=Production"
```

## Appeler l'API

```powershell
$url = "https://eap-api-920819552333.europe-west9.run.app"
$h = @{ Authorization = "Bearer $(gcloud auth print-identity-token)"; 'X-User-Id' = 'moi' }
Invoke-RestMethod "$url/health" -Headers $h
Invoke-RestMethod "$url/agents/sav-produit/chat" -Method Post -Headers $h -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes('{"message":"Combien de temps pour un retour ?"}'))

# Ou l'interface web, via un tunnel authentifié : http://localhost:8080
gcloud run services proxy eap-api --port 8080
```

## Rotation d'une clé

```powershell
gcloud secrets versions add gemini-api-key --data-file=<fichier>   # nouvelle version, puis :
gcloud run services update eap-api                                  # nouvelle révision qui relit « latest »
gcloud secrets versions disable 1 --secret gemini-api-key          # désactiver l'ancienne
```

## Déploiement continu (GitHub Actions, sans clé)

`.github/workflows/deploy.yml`, à chaque push sur `main` qui touche le code de l'API ou les fiches agents :
tests → image étiquetée par le commit → `gcloud run deploy --image` → test de santé (2 agents chargés, 403 sans jeton).

**Aucune clé de compte de service chez GitHub.** GitHub présente un jeton OIDC signé ; le fournisseur d'identité
`github-oidc` ne l'accepte que si `repository_id == 1449833259` (identifiant numérique : un dépôt renommé puis recréé
sous le même nom n'obtient rien) **et** `ref == refs/heads/main` (une branche ou une PR ne peut pas déployer).

Le compte `github-deployer` a quatre droits, chacun sur la ressource la plus étroite possible :

| Rôle | Portée | Pour |
|---|---|---|
| `artifactregistry.writer` | dépôt d'images `cloud-run-source-deploy` | pousser l'image |
| `run.developer` | service `eap-api` | déployer une révision |
| `run.invoker` | service `eap-api` | test de santé |
| `iam.serviceAccountUser` | compte `eap-api-runtime` | attacher l'identité de l'API à la révision |

Il ne peut ni lire les secrets, ni créer de service, ni modifier les droits. Les liaisons sont scriptées dans
`infra/grant-github-deployer.ps1` (idempotent, à exécuter par un propriétaire du projet).

```powershell
gcloud services enable iamcredentials.googleapis.com sts.googleapis.com
gcloud iam workload-identity-pools create github --location=global --display-name="GitHub Actions"
gcloud iam workload-identity-pools providers create-oidc github-oidc --location=global --workload-identity-pool=github `
  --issuer-uri="https://token.actions.githubusercontent.com" `
  --attribute-mapping="google.subject=assertion.sub,attribute.repository_id=assertion.repository_id,attribute.ref=assertion.ref" `
  --attribute-condition="assertion.repository_id == '1449833259' && assertion.ref == 'refs/heads/main'"
gcloud iam service-accounts create github-deployer --display-name="GitHub Actions deployer"

$d = "serviceAccount:github-deployer@eap-portfolio.iam.gserviceaccount.com"
gcloud iam service-accounts add-iam-policy-binding github-deployer@eap-portfolio.iam.gserviceaccount.com `
  --role=roles/iam.workloadIdentityUser `
  --member="principalSet://iam.googleapis.com/projects/920819552333/locations/global/workloadIdentityPools/github/attribute.repository_id/1449833259"
gcloud artifacts repositories add-iam-policy-binding cloud-run-source-deploy --location=europe-west9 --role=roles/artifactregistry.writer --member=$d
gcloud run services add-iam-policy-binding eap-api --region=europe-west9 --role=roles/run.developer --member=$d
gcloud run services add-iam-policy-binding eap-api --region=europe-west9 --role=roles/run.invoker --member=$d
gcloud iam service-accounts add-iam-policy-binding eap-api-runtime@eap-portfolio.iam.gserviceaccount.com --role=roles/iam.serviceAccountUser --member=$d
```

## Reste à faire (feuille de route)

- Authentification des utilisateurs finaux (IAP ou OIDC) à la place de l'en-tête `X-User-Id`
- Approbation manuelle avant production (règle de l'environnement GitHub `production`), et campagne d'évaluation avant déploiement
- Traces vers Cloud Trace, registre de consommation persistant (les plafonds survivront aux redémarrages)
- Gemini via Vertex AI (authentification IAM, sans clé d'API)
