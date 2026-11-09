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

## Connexion des utilisateurs (Identity-Aware Proxy)

```
Navigateur ──▶ IAP (connexion Google, liste d'accès) ──jeton signé──▶ Cloud Run eap-api ──▶ vérifie le jeton ──▶ agents
```

- **IAP filtre en amont** : sans connexion, redirection vers Google ; sans le rôle `iap.httpsResourceAccessor`, refus.
- **L'application vérifie le jeton qu'IAP ajoute** (`x-goog-iap-jwt-assertion`) : signature ES256 par une clé publiée par
  Google, émetteur `https://cloud.google.com/iap`, audience `/projects/920819552333/locations/europe-west9/services/eap-api`,
  expiration. Défense en profondeur : si IAP était désactivé par erreur, l'application refuserait quand même (401).
- **Identifiant de l'utilisateur** : le `sub` Google, stable et pseudonyme, pour les quotas et les traces. L'adresse
  e-mail n'est renvoyée qu'à l'utilisateur lui-même (`/me`), jamais journalisée.
- **Fermé par défaut** : `Auth:Mode` vaut `Iap` sauf en développement (`Header`, en-tête `X-User-Id`) ; en mode IAP sans
  audience configurée, l'application refuse de démarrer.

Mise en place dans un projet **sans organisation** (compte Gmail) :

1. Écran de consentement OAuth, audience **External**, application en mode test, utilisateurs autorisés listés.
2. Activation d'IAP sur le service depuis la console (onglet Sécurité) : la première activation ne peut pas se faire en
   ligne de commande dans ce cas.
3. Client OAuth « Application Web », URI de redirection `https://iap.googleapis.com/v1/oauth/clientIds/ID_CLIENT:handleRedirect`,
   appliqué par `infra/set-iap-oauth-client.ps1` (secret saisi masqué, jamais affiché ni conservé). Sans lui, IAP répond
   « Empty Google Account OAuth client ID(s)/secret(s) » (HTTP 502).
4. Droits : `infra/grant-iap-access.ps1` (agent IAP → `run.invoker` ; utilisateurs et compte de déploiement →
   `iap.httpsResourceAccessor`). Ajouter quelqu'un : `-Users alice@example.com`.

Le test de santé du déploiement continu passe par IAP avec un jeton d'accès du compte de déploiement, puis appelle `/me` :
la réponse prouve que le jeton IAP a été émis **et** vérifié par l'application.

## Rôles, registre persistant, traces, barrière de qualité

Mis en place par `infra/setup-finops-trace-evals.ps1` (idempotent), avant le déploiement du code qui s'en sert.

| Fonction | Mise en œuvre | Droit accordé |
|---|---|---|
| **Administrateurs** | `Auth__Admins__N` (variables Cloud Run, hors dépôt) ; l'adresse vient du jeton IAP, donc non usurpable. Le tableau FinOps renvoie 403 aux autres | aucun |
| **Registre de consommation** | Firestore `eap-finops` (Paris, protégé contre la suppression) : agrégats par jour × agent × modèle, compteur par jour × agent × utilisateur haché. Rétention par TTL : 7 jours (quotas), ~13 mois (FinOps) | `eap-api-runtime` : `datastore.user` |
| **Traces** | OTLP vers `telemetry.googleapis.com`, jeton du compte de service (aucune clé). Ni prompt ni réponse, ni message d'erreur du fournisseur | `eap-api-runtime` : `telemetry.tracesWriter` |
| **Barrière de qualité** | `evaluate.yml` : campagne des 2 agents avec juge Claude Sonnet, appelée par le déploiement quand un agent, un jeu de test ou le cœur change. Sous le seuil d'une fiche : pas de mise en production | `github-evaluator` (fédération d'identité, main) : lecture des 2 secrets, rien d'autre |

Le compte de déploiement ne lit toujours pas les clés : l'évaluation a son propre compte, limité aux secrets.
Registre indisponible au moment du contrôle des plafonds : la requête est refusée (503). Après l'appel au modèle :
la réponse est rendue et l'incident tracé (`eap.ledger.error`).

## Reste à faire (feuille de route)

- Rôles plus fins (responsable métier d'un agent, lecteur FinOps), et groupes plutôt qu'une liste d'adresses
- Approbation manuelle avant production (règle de l'environnement GitHub `production`), et campagne d'évaluation avant déploiement
- Traces vers Cloud Trace, registre de consommation persistant (les plafonds survivront aux redémarrages)
- Gemini via Vertex AI (authentification IAM, sans clé d'API)
