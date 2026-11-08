# Journal de bord : constats terrain

Ce qu'on apprend en branchant de vrais modèles, et qu'aucune documentation ne dit clairement.

## 2026-11-08 : premier branchement de Gemini

**1. Un modèle « disponible » peut ne plus répondre.**
`gemini-2.5-flash` apparaissait encore dans la liste des modèles de l'API, mais renvoyait 404 à l'appel.
La page tarifs le signale comme obsolète, avec une redirection prévue vers `gemini-3.5-flash-lite`.
→ *Gouvernance* : surveiller les calendriers de retrait des modèles et intégrer le changement de modèle
au cycle de vie de l'agent (nouvelle version de la fiche, puis réévaluation complète).

**2. Les alias de modèle dérivent sans prévenir.**
Un appel à `gemini-3.5-flash` a été servi par `gemini-3.6-flash`, et `gemini-flash-latest` par `gemini-3.8-flash`.
→ *Traçabilité* : la fiche agent fige une version précise, et chaque appel enregistre la version **réellement
servie** (`ServedModel`, attribut de trace `eap.model.served`). Sans cela, une évaluation n'est pas reproductible.

**3. Les couches « compatibles OpenAI » ne sont pas équivalentes.**
Avec deux messages système (règles de l'agent, puis contexte documentaire), la couche compatible OpenAI de
Gemini n'a conservé que le dernier. Les règles étaient ignorées sans aucune erreur. Le signal qui a
trahi le problème : 40 tokens en entrée au lieu de 300 environ.
→ Correctif : un seul message système. *Leçon* : l'abstraction multi-fournisseurs ne dispense pas de tester
chaque fournisseur. C'est le rôle du cadre d'évaluation (semaine 3), et le suivi des tokens est un excellent détecteur.

**4. Hallucination de source.**
Sur une demande hors périmètre, l'agent a cité une source inexistante (« Consignes générales Maison Kerlenn »).
→ Cas à ajouter au jeu d'évaluation : toute source citée doit appartenir à la base de connaissances.

**5. Erreurs transitoires.**
Un 403 ponctuel est apparu sur l'offre gratuite après de nombreux appels rapprochés.
→ Les erreurs fournisseur renvoient maintenant une 502 neutre au client, avec le détail dans les journaux.
À faire : nouvelles tentatives avec temporisation, puis bascule vers un modèle de secours.

## 2026-11-08 : comparaison Gemini et Claude sur l'agent SAV

Mêmes 5 questions, même prompt, même base de connaissances. Seul le modèle change, via la configuration.

| Critère | `gemini-3.5-flash-lite` | `claude-haiku-5-5` |
|---|---|---|
| Exactitude des faits (5 questions) | 5/5 | 5/5 |
| Refus hors périmètre et escalade | 2/2 | 2/2 |
| **Sources inventées** | **2/5** (« Consignes de service », « Catalogue général… ») | **0/5** |
| Latence | 650 à 1 200 ms | 1 700 à 2 300 ms |
| Tokens de sortie par réponse | 50 à 100 | 230 à 550 (dont raisonnement interne facturé) |
| Coût des 5 questions | 0,00168 $ | 0,00130 $ (−23 %) |

**Constats :**
- **Le paramètre `temperature` est refusé par Claude Haiku 5.5 (HTTP 400).** Chaque fournisseur déclare maintenant
  `SupportsTemperature`, et la plateforme n'envoie le paramètre qu'à ceux qui l'acceptent. Une même fiche agent reste ainsi portable.
- **Claude raisonne avant de répondre** (raisonnement adaptatif activé par défaut). Ce raisonnement est invisible mais facturé : il explique
  les 230 à 550 tokens de sortie pour des réponses de 60 à 100 mots, et la latence plus élevée. Grâce à son prix au token,
  Claude reste moins cher ici. Le niveau d'effort se règle avec le SDK officiel (piste : effort `low` pour un agent de FAQ).
- **Claude respecte mieux la consigne de citer ses sources**, ce qui compte pour la traçabilité et la confiance.
  Gemini répond plus vite.
- La couche compatible OpenAI convient pour ce banc d'essai, mais Anthropic recommande son SDK officiel en production :
  c'est une étape de la feuille de route.

**Choix argumenté** (le genre de recommandation attendue d'un expert) : pour un SAV client, le coût et la qualité
des sources font pencher vers Claude, à valider sur un jeu d'évaluation plus large (semaine 3).
Si la latence est critique (chat en direct), Gemini garde l'avantage.

## Coût observé lors du premier branchement

**Coût observé :** 4 questions pour environ 0,0012 $ avec `gemini-3.5-flash-lite`, soit environ 0,0003 $ par réponse et
350 $ pour un million de réponses. Latence de 600 à 900 ms.
