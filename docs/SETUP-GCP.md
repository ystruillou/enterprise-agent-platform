# Mise en place de Google Cloud (à faire une fois)

## 1. Clé Gemini pour démarrer tout de suite (gratuit, 5 minutes)
1. Ouvrir https://aistudio.google.com/apikey avec un compte Google.
2. Créer une clé API.
3. La définir pour la session : `$env:GEMINI_API_KEY = "..."`.
   L'offre gratuite d'AI Studio suffit pour développer. Attention : sur l'offre gratuite,
   Google peut utiliser les données pour améliorer ses produits. N'y mettez aucune donnée réelle.

## 2. Compte Google Cloud (pour Vertex AI, Cloud Run et Gemini Enterprise)
1. https://cloud.google.com/free : créer le compte. Une carte bancaire est demandée,
   des crédits d'essai sont offerts aux nouveaux comptes (vérifier le montant et la durée sur la page).
2. Créer un projet, par exemple `eap-portfolio`.
3. **Avant tout le reste**, dans Facturation › Budgets et alertes : un budget de 20 € avec des alertes à 50 %, 90 % et 100 %.
4. Installer la CLI : https://cloud.google.com/sdk/docs/install, puis :
   ```bash
   gcloud auth login
   gcloud config set project eap-portfolio
   gcloud services enable aiplatform.googleapis.com run.googleapis.com secretmanager.googleapis.com \
       cloudtrace.googleapis.com logging.googleapis.com artifactregistry.googleapis.com
   ```

## 3. Gemini Enterprise
Gemini Enterprise est un SaaS sous licence. Vérifier sur la page produit s'il existe un essai
pour votre type de compte. C'est l'étape de la semaine 5 ; d'ici là, Vertex AI et l'ADK couvrent l'essentiel.

## 4. Formation recommandée en parallèle
- Google Cloud Skills Boost : parcours *Generative AI Leader* et *Introduction to Vertex AI*
- Documentation ADK : https://google.github.io/adk-docs/
