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

## 2026-11-08 : garde-fou de sortie, puis Gemini et Claude à armes égales

Le défaut restant (sources inventées) ne relève plus de la recherche : il est traité par le code, pas par le prompt.
Avant de répondre, la plateforme vérifie la ligne « Source : » contre la base ; une citation invérifiable est remplacée
par « aucune source vérifiable dans la documentation ». Chaque intervention est tracée (`eap.guardrail.actions`).

**Campagne finale : 22 cas × 3 passages, recherche hybride, garde-fou actif, mêmes conditions pour les deux modèles.**

| | `gemini-3.5-flash-lite` | `claude-haiku-5-5` |
|---|---|---|
| Score | **100 %** (66/66) ✅ | **98 %** (65/66) ✅ |
| Réponses corrigées par le garde-fou | 9 (sources inventées) | 2 (faux positifs inoffensifs, voir plus bas) |
| Latence médiane | 0,6 s | 2,0 s |
| Coût des 66 réponses | 0,021 $ | 0,017 $ |

**Enseignements :**

1. **Un garde-fou déterministe vaut mieux qu'une consigne répétée dans le prompt.** Gemini invente une source dans
   14 % des réponses ; le prompt l'interdisait déjà. Le code le garantit, et le compteur d'interventions garde le
   défaut visible : le score mesure ce que voit l'utilisateur, le compteur mesure le modèle.
2. **Le garde-fou avait une faille, trouvée en relisant un échec de Claude.** Claude écrivait parfois « Source : … »
   à la fin de sa dernière phrase, pas sur une ligne à part : une source inventée écrite ainsi serait passée.
   → Détection étendue, et tests dédiés. Un second bug avait été trouvé par les tests unitaires : l'expression régulière
   avalait la ligne vide avant la source.
3. **Il faut pouvoir relire ce que le garde-fou a remplacé.** Le rapport d'évaluation montre maintenant la citation
   d'origine (l'API, elle, ne l'expose pas). Résultat : les 9 interventions sur Gemini sont de vraies inventions
   (« Instructions générales », « Consignes service client ») ; les 2 sur Claude sont des **faux positifs** :
   « catalogue.md (produit non trouvé) », une citation honnête annotée. Conséquence inoffensive (la mention de
   remplacement est vraie aussi), donc pas de règle ad hoc ajoutée pour l'instant.
4. **L'échec restant de Claude est une limite du contrôle, pas du modèle.** Sur la lampe, Claude répond « 2 ans […] la
   garantie de 5 ans au total ne s'applique pas à ce modèle » : réponse juste, mais le contrôle « ne contient pas 5 ans »
   la refuse. Je ne modifie pas le cas pour faire passer le modèle : c'est précisément le rôle du modèle juge, à activer
   sur un quota adapté.
5. **Avec les mêmes outils, le choix de modèle change de nature.** Les deux passent le seuil ; Gemini est 3 fois plus
   rapide, Claude un peu moins cher et beaucoup plus fiable sur les sources (2 corrections inoffensives contre 9 réelles).
   Recommandation pour un SAV en chat : Gemini avec garde-fou si la latence prime, Claude si l'on veut un modèle qui
   respecte les consignes sans filet.

## 2026-11-08 : le modèle juge, en arbitre et en second regard

Deux rôles pour le juge :

- **Arbitre.** Un contrôle par mots-clés ne distingue pas « c'est 5 ans » de « ce n'est pas 5 ans ». Les fragments
  ambigus passent de `none` à `suspect` : interdits sans juge (par prudence), **levés si le juge valide la réponse**
  (≥ 4/5), au vu d'un critère propre au cas (`rubric`). Les fuites (prompt, numéro de carte) restent strictement
  interdites, aucun juge ne peut les lever. Un juge sans verdict (quota, réponse illisible) ne lève rien.
- **Second regard.** Il peut refuser une réponse que tous les contrôles acceptent.

**Campagne : agent `gemini-3.5-flash-lite`, juge `claude-sonnet-5-5` (autre fournisseur), 22 cas × 3.**

| Score | Note moyenne | Contrôles levés | Refus non vus par les contrôles | Coût agent | Coût juge |
|---|---|---|---|---|---|
| **98 %** (65/66) ✅ | 4,7/5 | 0 | 1 | 0,021 $ | **0,43 $** |

**Enseignements :**

1. **Le juge a trouvé un défaut invisible aux contrôles.** À « Où en est ma commande ? », Gemini oriente bien vers un
   conseiller, mais avec un faux motif (« je ne demande jamais de numéro de commande », règle interne reformulée) et
   cite « conditions-generales.md › Livraison », section **réelle** mais **hors sujet**. Le garde-fou vérifie que la
   source existe, pas qu'elle étaye la réponse. → Piste : un garde-fou de pertinence.
2. **Le juge coûte 20 fois plus que l'agent.** Il relit toute la base à chaque appel, avec un modèle plus puissant.
   En CI, il faudra un juge moins cher, calibré contre Sonnet sur les mêmes réponses, ou ne juger que les cas ambigus.
3. **L'arbitrage n'a pas encore servi en conditions réelles.** Sur ces 3 passages, aucune réponse n'a cité « 5 ans » ;
   il est couvert par les tests unitaires (juge 5/5, 2/5, sans verdict, interdit strict).
4. **Le juge gratuit ne tient pas une campagne.** La campagne Claude jugée par `gemini-3.6-flash` (offre gratuite) a
   enchaîné les HTTP 429 au bout d'environ 25 jugements, puis a été interrompue. Un juge doit tourner sur un quota payant.

**Même juge pour Claude : agent `claude-haiku-5-5`, juge `claude-sonnet-5-5`, 22 cas × 3.**

| | `gemini-3.5-flash-lite` | `claude-haiku-5-5` |
|---|---|---|
| Score avec juge | **98 %** (65/66) ✅ | **95 %** (63/66) ✅ |
| Score sans juge (campagne précédente) | 100 % | 98 % |
| Note moyenne du juge | 4,7/5 | 4,8/5 |
| Refus du juge non vus par les contrôles | 1 | 2 |
| Corrections du garde-fou | 10 | 2 |
| Latence médiane | 0,6 s | 1,8 s |
| Coût agent / juge | 0,021 $ / 0,43 $ | 0,016 $ / 0,43 $ |

5. **Les refus du juge sur Claude sont fondés : ce sont des extrapolations.** « Au-delà de 13 h, le délai est de 3 à 5 jours »
   et « le paiement en plusieurs fois n'est pas proposé pour les autres montants » : plausibles, mais absents des documents.
   Gemini invente des sources ; Claude, quand il en dit plus que la base, invente des règles. Seul le juge voit ce second défaut.
6. **Pas d'auto-préférence visible.** Sonnet a été plus sévère avec Claude (2 refus) qu'avec Gemini (1 refus). Un seul
   échantillon ne le prouve pas : la bonne pratique reste un juge d'un autre fournisseur, quand le quota le permet.
7. **Nouveau faux négatif d'un contrôle par mots-clés (esc-03).** Pour le grille-pain inexistant, Claude n'invente pas de prix,
   renvoie vers un conseiller, puis propose des produits existants **avec leur vrai prix**. Le contrôle « ne contient pas € »
   le refuse ; le juge donne 5/5. Même famille que la lampe : candidat à passer en `suspect`, avec un critère
   (« aucun prix pour le grille-pain »). Le cas n'est pas modifié dans cette campagne : le score publié reste 95 %.

**Correction du jeu SAV après relecture** : esc-03 passe de `none: ["€"]` à `suspect: ["€"]`, avec le critère « aucun prix
pour le grille-pain ; citer le vrai prix d'autres produits est acceptable ». Le juge avait confirmé que la réponse refusée
était juste. Appliqué aux campagnes suivantes ; les scores publiés ci-dessus restent ceux mesurés avant la correction.

## 2026-11-08 : second pilote, le relecteur de conformité promotionnelle

Jeu de test `evals/conformite-promo.jsonl` : 20 textes en 8 catégories (réduction de prix, durée et stocks, allégations
environnementales, pratiques trompeuses, crédit, **textes conformes** où il ne faut pas inventer de blocage, multi-règles,
hors périmètre, injection). Agent `claude-haiku-5-5`, juge `claude-sonnet-5-5`.

| Étape | Changement | Score |
|---|---|---|
| Recherche seule, mots-clés | « -30 % sur les canapés » ne partage aucun mot avec « Annonce de réduction de prix » | 10/16 textes reçoivent les bonnes règles |
| 1. Base entière (`retrieval.mode: all`) | Un relecteur applique toutes les règles ; la base fait 5 sections | 16/16, mais **25 %** (3 passages) |
| 2. `max_output_tokens` 900 → 3 000 | 54 réponses sur 60 étaient tronquées, certaines vides | **100 %** (1 passage) |
| 3. Garde-fou des sources désactivé pour cet agent | Faux positif nuisible, voir plus bas | **90 %** ✅ (54/60, 3 passages) |

**Enseignements :**

1. **La recherche n'est pas toujours la bonne réponse.** Un relecteur de conformité doit appliquer *toutes* les règles, pas
   celles qui ressemblent au texte, et un top 3 ne peut pas couvrir un texte qui en enfreint 4. Pour un référentiel court,
   on l'envoie en entier : nouveau mode `all`, plafonné à 30 sections pour qu'il ne serve pas par erreur sur une grosse base.
2. **Le même piège deux fois : le plafond de tokens.** Déjà vu sur le SAV, il a coûté 75 points ici : une relecture avec
   reformulation est longue, et Claude raisonne avant d'écrire. Le contrôle « réponse complète » l'a signalé sur chaque cas.
3. **Un garde-fou conçu pour un format peut nuire sur un autre.** Le garde-fou des sources attend une ligne « Source : »
   finale (format SAV). Le relecteur cite une règle par problème, suivie de son explication : le garde-fou a remplacé une
   citation exacte **et l'argument qui suivait** par « aucune source vérifiable ». Désactivé pour cet agent (décision écrite
   dans sa fiche), en attendant un garde-fou adapté aux citations en ligne. La limite de longueur, propre au SAV,
   est passée dans la fiche (`evaluation.max_sentences`) pour la même raison.
4. **Le même défaut côté évaluation, laissé visible.** 3 des 6 échecs viennent du contrôle « source citée », qui lit ces
   citations en ligne comme des sources inventées, alors que le juge donne 4 ou 5/5. Le contrôle n'est pas modifié
   après coup : le score publié reste 90 %, et l'adaptation du contrôle est inscrite à la feuille de route.
5. **Les vrais défauts, vus par le juge seul.** Sur « Soldes d'hiver », l'agent exige une année et parle d'« appellation
   réglementée », deux règles absentes de sa base (2 fois sur 3). Sur un texte conforme, il crée un blocage
   « tant que non vérifié » (1 fois sur 3). Comme le SAV Claude, il en dit plus que ses documents.
6. **L'arbitrage du juge a servi en conditions réelles : 6 contrôles levés**, tous relus et justifiés : « aucun problème
   bloquant », « bloquant seulement si l'offre relève du crédit ». C'est exactement la négation que les mots-clés ne lisent pas.
7. **Coût** : 0,04 $ pour 60 relectures, 0,49 $ de juge, latence médiane 5,5 s, acceptable pour un outil interne de relecture.

### Version 0.3.0 : contrôle des citations en ligne, règles de relecture

- **Sources** : une citation est coupée entre la **référence** (jusqu'à la première fin de phrase hors parenthèses) et le
  **commentaire** qui suit. Seule la référence est vérifiée. Une parenthèse est acceptée si tous ses mots viennent du
  contenu des documents (« (Code de la consommation, art. L112-1-1) ») ; hors parenthèses, le contenu ne valide rien
  (sinon « Consignes service client » passerait). Toutes les citations sont vérifiées, plus seulement la première, et le
  garde-fou ne remplace que la référence invérifiable, en conservant l'argument. Tests écrits à partir des 3 citations
  réelles refusées à tort. Garde-fou réactivé pour le relecteur.
- **Prompt** : un problème doit découler d'une règle du contexte ; un point utile mais non couvert va dans une section
  « Hors référentiel », sans niveau ; pas de blocage conditionnel sur un texte qui respecte la règle.

| | 0.2.0 | **0.3.0** |
|---|---|---|
| Score (3 passages) | 90 % | **97 %** ✅ (58/60) |
| Faux échecs du contrôle des sources | 3 | 0 |
| Règles inventées (« Soldes d'hiver ») | 2 sur 3 | 0 sur 3 |
| Blocage inventé sur un texte conforme | 1 sur 3 | 0 sur 3 |
| Corrections du garde-fou | (désactivé) | 0 |

Les 2 échecs restants, vus par le juge seul : un point signalé deux fois malgré la consigne, et un superlatif classé
« à vérifier » au lieu de « bloquant », avec une reformulation qui ajoute une affirmation (« dans toutes nos boutiques »).
Un jugement sur 60 est resté sans note (réponse du juge illisible) : le cas est compté réussi sur les seuls contrôles.

**Non-régression du SAV** avec le nouveau contrôle (3 passages, sans juge) : 100 %, 9 corrections du garde-fou, toutes de
vraies inventions (« Instructions générales », « Consignes de service »). Coût de l'étape : environ 0,70 $.

## 2026-11-09 : premier déploiement sur Cloud Run

Voir [DEPLOIEMENT.md](DEPLOIEMENT.md). API privée à Paris, clés dans Secret Manager, compte de service dédié, budget de 10 €/mois.

1. **Privé par défaut.** Tant que l'identité de l'utilisateur est un en-tête libre, une URL publique permettrait à
   n'importe qui de consommer les crédits des modèles. Vérifié : HTTP 403 sans jeton, réponse avec jeton.
2. **Propagation IAM.** Le droit de lecture du premier secret a échoué (« le compte de service n'existe pas ») quelques
   secondes après la création du compte, puis réussi en relançant. À prévoir dans un script : nouvelle tentative.
3. **Les clés ne transitent jamais en clair** : lues dans les variables d'environnement, écrites dans un fichier temporaire
   sans retour à la ligne final (un `\n` en trop rendrait la clé invalide), supprimé aussitôt.
4. **Premier appel en production** : SAV en 1,8 s (démarrage à froid compris), 0,0003 $. Relecteur de conformité en 4,4 s.
   Sur ce dernier, le garde-fou a remplacé une des deux citations. En production, seule la trace dit qu'il est
   intervenu (`eap.guardrail.actions`) : la citation d'origine n'est visible qu'en évaluation. À trancher : la conserver,
   pour les administrateurs, dans un journal d'audit protégé ? Cela impose de traiter la question RGPD.

## 2026-11-09 : déploiement continu, sans clé chez GitHub

À chaque push sur `main` touchant l'API ou les fiches agents : tests, image étiquetée par le commit, déploiement Cloud Run,
test de santé (2 agents chargés, **403 sans jeton**). 1 min 19 s de bout en bout.

1. **Fédération d'identité plutôt que clé de compte de service.** GitHub présente un jeton OIDC ; Google ne l'accepte que
   pour ce dépôt (identifiant numérique) et la branche `main`. Rien à faire tourner, rien à faire fuiter.
2. **Le filtre de sécurité de Claude Code a bloqué les attributions de droits IAM** : la décision a été rendue au
   propriétaire du projet, sous forme d'un script relu et exécuté par lui (`infra/grant-github-deployer.ps1`).
   Premier essai du script raté par ma faute : arguments passés en un seul bloc à `gcloud`. Corrigé, puis vérifié sur une
   commande en lecture seule avant de le rendre.
3. **Premier déploiement en échec, cause classique** : les tests laissent `bin/` et `obj/` sur le runner, et le
   `Dockerfile` les copiait par-dessus la restauration NuGet de l'image (NETSDK1064). Le déploiement manuel passait
   grâce à `.gcloudignore` ; `docker build` lit `.dockerignore`, qui manquait.
4. **Revue de sécurité automatique** : actions tierces épinglées sur un commit exact (le job détient les droits de
   déploiement), Dependabot pour les mettre à jour. Avertissement restant : ces versions ciblent Node.js 20, déprécié
   par GitHub ; les mises à jour majeures proposées par Dependabot le régleront.

## 2026-11-09 : connexion des utilisateurs par Identity-Aware Proxy

L'en-tête libre `X-User-Id` permettait à n'importe quel appelant de se faire passer pour un autre (et de contourner les
quotas par utilisateur). En production, l'identité vient maintenant d'IAP : connexion Google, liste d'accès, et jeton
signé que **l'application vérifie elle-même** (signature, émetteur, audience, expiration).

1. **Ne pas faire confiance au proxy seul.** IAP filtre en amont, mais l'application revérifie le jeton : une erreur de
   configuration (IAP désactivé, accès qui contournerait le proxy) donne un refus, pas un accès anonyme. Mode IAP par
   défaut ; sans audience configurée, l'application refuse de démarrer.
2. **Un projet sans organisation demande plus d'étapes manuelles** : première activation d'IAP dans la console, client
   OAuth à créer soi-même. Diagnostic par la réponse d'IAP elle-même (`x-goog-iap-generated-response`) : HTTP 502
   « Empty Google Account OAuth client ID(s)/secret(s) ».
3. **Le secret du client OAuth n'a jamais transité par la conversation** : saisi masqué dans un script, écrit le temps
   d'un appel, supprimé.
4. **Effet de bord de la console** : l'enregistrement a créé une limite de 20 instances au niveau du service ; la limite
   d'une instance par révision, posée au déploiement, reste celle qui s'applique.
5. **Pseudonymisation** : quotas et traces utilisent le `sub` Google, jamais l'adresse e-mail.

## 2026-11-09 : rôles, registre persistant, traces, barrière de qualité

| Fonction | Résultat |
|---|---|
| Rôle administrateur (tableau FinOps) | Issu du jeton IAP, liste hors dépôt ; 403 pour les autres |
| Registre de consommation | Firestore à Paris ; agrégats en nano-dollars, utilisateur haché, TTL 7 jours / ~13 mois |
| Traces | Cloud Trace : requête → agent → embeddings → modèle, avec coût, version, mode de recherche ; aucun contenu |
| Barrière de qualité | Premier passage : SAV 100 %, conformité 90 % → déploiement. Sautée quand aucun agent ne change (0 $) |

**Enseignements :**

1. **L'émulateur Firestore a trouvé un bug que la version en mémoire ne pouvait pas montrer.** Les coûts en micro-dollars
   arrondissaient chaque appel (0,0000283 $ comptés 28 µ$) : 1 % d'écart sur les petits appels. Passage aux
   nano-dollars, exacts avec des tarifs au centime par million de tokens. Même contrat de test pour les deux registres.
2. **La revue de sécurité automatique a évité deux erreurs** : un budget contourné si d'anciens documents comptaient
   pour zéro (désormais : ancien champ relu, coût illisible = refus), et un jeton Google qui aurait pu partir vers une
   autre destination OTLP (désormais : options d'export dédiées, jeton limité à `telemetry.googleapis.com`).
3. **Des traces perdues sans un mot.** L'exportateur OTLP appelle la méthode synchrone `Send` ; seul `SendAsync`
   était redéfini, et l'appel partait sans jeton. OpenTelemetry n'écrivant ses erreurs que sur EventSource, rien
   n'apparaissait : il a fallu un écouteur pour lire « unregistered callers ». Ma première hypothèse (la fabrique
   `IHttpClientFactory`) était fausse : le test de bout en bout à travers le vrai exportateur l'a montré en échouant
   dans les deux configurations. La documentation de l'exportateur le dit pourtant explicitement.
4. **Cloud Run facturé à la requête et export différé.** Le processeur n'est alloué que pendant une requête : les traces
   partent à la requête suivante (ou à l'arrêt de l'instance). Acceptable à ce volume ; sinon, processeur toujours
   alloué (quelques centimes par réveil) ou export avant la fin de la réponse (latence).
5. **Coût de la barrière** : ~0,37 $ par campagne avec juge Sonnet, seulement quand un agent, un jeu de test ou le cœur change.

## Coût observé lors du premier branchement

**Coût observé :** 4 questions pour environ 0,0012 $ avec `gemini-3.5-flash-lite`, soit environ 0,0003 $ par réponse et
350 $ pour un million de réponses. Latence de 600 à 900 ms.
