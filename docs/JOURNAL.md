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

**Coût observé :** 4 questions pour environ 0,0012 $ avec `gemini-3.5-flash-lite`, soit environ 0,0003 $ par réponse et
350 $ pour un million de réponses. Latence de 600 à 900 ms.
