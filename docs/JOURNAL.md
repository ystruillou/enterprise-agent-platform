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

## 2026-11-08 : première campagne du cadre d'évaluation

22 cas (faits, escalade, pièges, hors périmètre, sécurité, transparence), contrôles déterministes, seuil de 85 %.

| | `gemini-3.5-flash-lite` | `claude-haiku-5-5` |
|---|---|---|
| Score | **77 %** (17/22), ❌ refusé | **86 %** (19/22), ✅ validé* |
| Échecs communs | liv-03, ret-01 | liv-03, ret-01 |
| Sources inventées | 3 (« Consignes générales », « Instructions générales ») | 0 |
| Latence médiane | 590 ms | 1 710 ms |
| Coût des 22 cas | 0,0068 $ | 0,0048 $ |

\* 20/22 (91 %) après correction d'un faux positif du contrôle (« fiches produits » pris pour une source inventée).
La relance n'a pas pu se faire : crédits Anthropic épuisés (voir plus bas).

**Ce que l'évaluation a appris, et qu'aucun test manuel n'aurait montré :**

1. **Les deux échecs communs viennent de la recherche documentaire, pas du modèle.** « Je veux être livré demain »
   ne trouve pas la section « Livraison » (« livré » n'est pas « livraison »), et « renvoyer un article » ne trouve pas
   « Retours ». Le modèle répond honnêtement qu'il n'a pas l'information, faute de l'avoir reçue.
   → Prochaine étape : une recherche sémantique (embeddings), mesurée avant et après sur ce même jeu de test.
2. **Gemini invente une source dès qu'il n'en a pas** (refus, transparence, sécurité). Claude écrit « aucun document ».
3. **Réponse tronquée chez Claude** : le plafond de 600 tokens de sortie inclut le raisonnement interne, et une
   réponse a été coupée en pleine phrase. → Plafond relevé à 1 500 tokens ; les réponses tronquées sont désormais
   détectées (`truncated` dans l'API, contrôle « réponse complète » en évaluation).
4. **Bug de production trouvé par l'évaluation** : Gemini renvoie parfois ses erreurs dans un tableau JSON, ce qui
   faisait planter l'extraction du code d'erreur (et donc la 502 prévue).
5. **Il faut évaluer l'évaluateur.** Le premier contrôle des sources découpait les citations sur « et » et sur les
   virgules, alors que les titres en contiennent (« Retours et remboursement ») : trois faux positifs pour Claude.
   Chaque échec doit être relu avant de conclure. Un contrôle ne se corrige que s'il est faux, jamais pour faire passer un modèle.
6. **Quotas et crédits.** Le juge `gemini-3.6-flash` a épuisé l'offre gratuite en une dizaine d'appels. Puis le solde
   Anthropic est tombé à zéro, avec un message `invalid_request_error`, le même code qu'une requête mal formée.
   → *FinOps* : surveiller les soldes et les quotas, et alerter avant l'épuisement.

## 2026-11-08 : recherche sémantique et hybride, mesurée avant et après

Même agent, même modèle (`gemini-3.5-flash-lite`), même prompt, mêmes 22 cas : seule la recherche documentaire change.

**Recherche seule** (`--retrieval-only`, sans appel au modèle de chat) : sections attendues transmises au modèle.

| Mots-clés | Sémantique (`gemini-embedding-2`) | Hybride (fusion RRF) |
|---|---|---|
| 13/16 | 15/16 | **16/16** |

**Campagne complète :**

| | Mots-clés (avant) | Hybride, 1 passage | Hybride, 3 passages |
|---|---|---|---|
| Score | 77 % ❌ | 86 % ✅ | **82 % ❌** (54/66) |
| Cas factuels et multi-questions | 14/16 | 16/16 | **48/48** |
| Échecs | 2 recherche + 3 sources inventées | 3 sources inventées | 12 sources inventées |

**Enseignements :**

1. **La recherche hybride a résolu tous les échecs factuels.** « Livré demain » trouve maintenant « Livraison », « renvoyer »
   trouve « Retours ». Sur 3 passages, 48 réponses factuelles sur 48 sont justes.
2. **Le sémantique seul rate les questions à deux sujets.** L'embedding de la question fait une moyenne de ses sens :
   « batterie de l'aspirateur et garantie de la cafetière » ne remonte pas la fiche Cafetière. Les mots-clés la trouvent ;
   la fusion des deux classements (Reciprocal Rank Fusion) cumule les avantages.
3. **Un passage unique ment.** 86 % sur un passage, 82 % sur trois : le défaut restant (Gemini invente une source quand il n'en
   a pas) est aléatoire. Le verdict publié est celui des 3 passages : **refusé**.
4. **Le défaut restant n'est plus un problème de recherche** : les 12 échecs sont des sources inventées sur des questions sans
   réponse documentaire (refus, transparence, sécurité). → Prochaine étape : un **garde-fou de sortie** qui vérifie la ligne
   « Source : » contre la base avant de répondre, plutôt que d'espérer que le modèle obéisse au prompt.
5. **Le seuil de similarité dépend du modèle d'embeddings.** Calibré à 0,50 pour `gemini-embedding-2` (questions documentées
   ≥ 0,55, « recette de crêpes » 0,48) ; il est ignoré quand on force un autre générateur (simulé en dev), dont l'échelle diffère.
   Changer de modèle d'embeddings impose de réindexer **et** de recalibrer.
6. **J'ai corrigé une étiquette trop permissive.** mul-01 acceptait « Cafetière **ou** Garantie » comme contexte suffisant ; la
   réponse a montré que sans la fiche Cafetière, le modèle ne relie pas « cafetière pro » à la gamme Pro. La correction a
   *baissé* le rappel mesuré du sémantique (16 → 15) : c'est ce qui la rend crédible.
7. **Quotas** : l'offre gratuite des embeddings plafonne à 100 requêtes par minute. Un cache des vecteurs de question évite
   de revectoriser une question déjà vue (fréquent sur une FAQ). La latence de Gemini a aussi varié de 0,7 s à 5,4 s de médiane
   selon l'heure : une comparaison de latences ne vaut qu'entre mesures faites au même moment.

## Coût observé lors du premier branchement

**Coût observé :** 4 questions pour environ 0,0012 $ avec `gemini-3.5-flash-lite`, soit environ 0,0003 $ par réponse et
350 $ pour un million de réponses. Latence de 600 à 900 ms.
