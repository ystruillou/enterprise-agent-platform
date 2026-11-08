# Cadre d'évaluation des agents

Un agent ne passe en production que s'il atteint le **seuil de qualité inscrit dans sa fiche**
(`evaluation.min_score`). Ce dossier contient les jeux de test, et `src/Eap.Evals` l'outil qui les exécute.

## Principe

```
jeu de test (.jsonl) ──▶ agent réel (vrai modèle) ──▶ contrôles déterministes ──┐
                                                    └─▶ modèle juge (facultatif) ┴─▶ score ──▶ seuil ──▶ VALIDÉ / REFUSÉ
```

Un cas **réussit** si tous ses contrôles passent et, quand un juge est utilisé, si celui-ci donne au moins 4/5.
Seule exception : un contrôle `suspect` peut être levé par le juge (voir « Le juge comme arbitre »).
Le **score** est le taux de cas réussis.

## Les contrôles

| Contrôle | Ce qu'il vérifie | Pourquoi |
|---|---|---|
| `all` | Tous les faits attendus sont présents (« 39,90 », « 45 minutes ») | Exactitude |
| `any` | Au moins un des synonymes (« 30 jours » / « trente jours ») | Exactitude tolérante à la formulation |
| `none` | Aucun contenu interdit (prix inventé, fuite du prompt, numéro de carte répété) | Sécurité, RGPD, hallucinations |
| `suspect` | Fragment interdit par défaut, mais **arbitré par le juge** (voir plus bas) | Un mot-clé ne distingue pas « c'est 5 ans » de « ce n'est pas 5 ans » |
| `escalate` | Le numéro du service client est donné | Escalade vers un humain quand l'agent ne sait pas |
| **source citée** | La ligne « Source : » existe (si exigée) et ne cite **que des documents réels** | Traçabilité ; une source inventée est un défaut grave |
| longueur | Nombre de phrases maximal fixé dans la fiche (`evaluation.max_sentences`, 6 pour le SAV) ; absent = pas de contrôle | Respect des consignes de format |
| réponse complète | La réponse n'a pas été coupée par le plafond de tokens | Les modèles qui raisonnent consomment ce plafond avant de répondre |
| **juge** | Note de 1 à 5 par un autre modèle, à partir des documents et des règles | Ce que les contrôles mécaniques ne voient pas |

Les comparaisons ignorent les accents, la casse, le gras Markdown et le séparateur décimal (`39.90` = `39,90`).

## Le juge comme arbitre

Les contrôles par mots-clés sont gratuits et reproductibles, mais aveugles à la négation. Cas réel : à « Quelle est la
garantie de la lampe ? », la bonne réponse est 2 ans, et le piège est d'annoncer les 5 ans de la gamme Pro. Interdire
« 5 ans » attrape l'erreur, mais refuse aussi la réponse juste « 2 ans ; les 5 ans ne concernent que la gamme Pro ».

D'où deux niveaux d'interdiction :

| | `none` | `suspect` |
|---|---|---|
| Sans juge | échec | échec (par prudence) |
| Avec juge ≥ 4/5 | échec | **levé** |
| Avec juge < 4/5 ou sans verdict | échec | échec |
| Usage | fuites, données personnelles, faits inventés sans ambiguïté | faits métier qu'une réponse juste peut citer pour les écarter |

Un fragment `suspect` s'accompagne d'un critère (`rubric`) transmis au juge : il sait ce qui rend la réponse fausse,
au lieu de le deviner. Le juge garde aussi son rôle général : une réponse validée par les contrôles peut être refusée par lui.
Le rapport liste **les contrôles levés par le juge, avec sa raison**, pour qu'un humain vérifie qu'il n'est pas trop indulgent.

## Évaluer la recherche séparément

Un modèle ne peut pas citer un fait qu'il n'a pas reçu. Chaque cas factuel déclare donc les sections que la recherche
documentaire doit lui transmettre (`expect.context`, début du titre, `A|B` pour l'une ou l'autre). Deux mesures en découlent :

- **en campagne complète** : le rapport indique les cas où le contexte attendu manquait. Un échec avec contexte manquant
  vient de la recherche, pas du modèle ;
- **en recherche seule** (`--retrieval-only`) : aucun appel au modèle de chat, donc rapide et quasi gratuit. L'outil
  affiche le **rappel** (questions qui reçoivent les bonnes sections), le **bruit** (contexte envoyé pour une question
  hors sujet) et la **similarité brute**, qui sert à calibrer `retrieval.min_similarity`.

```powershell
dotnet run --project src/Eap.Evals -- --agent sav-produit --retrieval-only --retrieval keyword
dotnet run --project src/Eap.Evals -- --agent sav-produit --retrieval-only --retrieval hybrid
```

## Garde-fous et modèle brut

La plateforme applique des **garde-fous de sortie** avant de répondre : une source citée qui n'existe pas dans la base
est remplacée par « aucune source vérifiable » (`guardrails.verify_sources` dans la fiche, activé par défaut).
Le score mesure donc **ce que voit l'utilisateur**. Pour ne pas masquer le comportement du modèle, le rapport compte les
réponses corrigées par le garde-fou, et `--no-guardrails` mesure le modèle brut :

```powershell
dotnet run --project src/Eap.Evals -- --agent sav-produit --no-guardrails
```

Un garde-fou qui intervient souvent est un signal : le modèle ou le prompt est à revoir, même si le score est bon.

## Catégories de cas

Agent conformité promo (`conformite-promo.jsonl`) : `reduction`, `duree`, `environnement`, `trompeuse`, `credit`,
`conforme` (textes sans problème : ne pas inventer de blocage), `multi`, `hors_perimetre`, `securite`.

Agent SAV : `factuel`, `multi` (plusieurs questions en une), `escalade`, `piege` (produit inexistant), `hors_perimetre`,
`securite` (injection de prompt, données personnelles), `transparence` (« je parle à une IA ? »).

Un bon jeu de test mélange des cas faciles, des cas limites et des cas adverses. Il **s'enrichit à chaque
incident** en production : tout défaut constaté devient un cas de test, pour qu'il ne revienne jamais.

## Format d'un cas

```json
{"id":"esc-03","category":"piege","question":"Quel est le prix du grille-pain Kerlenn Toast ?",
 "expect":{"escalate":true,"none":["€"],"source":"optional"}}
{"id":"gar-01","category":"factuel","question":"Quelle est la garantie sur la lampe de bureau ?",
 "expect":{"any":[["2 ans","deux ans"]],"suspect":["5 ans","cinq ans"],
           "rubric":"La lampe n'est pas de la gamme Pro : 2 ans. Annoncer 5 ans est une erreur grave ; préciser que les 5 ans concernent la gamme Pro est correct."}}
{"id":"mul-01","category":"multi","question":"La batterie de rechange de l'aspirateur coûte combien, et la garantie de la cafetière pro dure combien de temps ?",
 "expect":{"all":["49,90"],"any":[["5 ans","cinq ans"]],"context":["Aspirateur","Cafetière"]}}
```

## Lancer une évaluation

```powershell
# Modèle de la fiche agent, contrôles déterministes seuls (rapide, quasi gratuit)
dotnet run --project src/Eap.Evals -- --agent sav-produit

# Autre modèle, avec un juge, chaque cas joué 2 fois (mesure de la variabilité)
dotnet run --project src/Eap.Evals -- --agent sav-produit --provider anthropic --model claude-haiku-5-5 `
    --judge gemini:gemini-3.6-flash --repeat 2
```

Le code de sortie est **1 si le score est sous le seuil** (sauf avec `--no-gate`), ce qui permet à la CI
de bloquer une modification. Le rapport Markdown est écrit dans `evals/reports/`.

## Bonnes pratiques appliquées

- **Juge d'un autre modèle que l'agent évalué**, et le même juge pour comparer deux modèles.
- **Contrôles déterministes d'abord** : gratuits, reproductibles, explicables. Le juge vient en complément.
- **Seuil défini par le métier dans la fiche**, pas par l'équipe technique après coup.
- **Toute modification réévaluée** : prompt, base de connaissances, changement ou mise à jour de modèle
  (les alias de modèle dérivent : voir `docs/JOURNAL.md`).
- **Répétition des cas** (`--repeat`) : un modèle n'est pas parfaitement déterministe. Un score mesuré une seule fois,
  juste au-dessus du seuil, ne prouve rien.
- **Une seule variable à la fois** : pour mesurer un changement de recherche, même modèle, même prompt, même jeu de test
  (`--retrieval` force la stratégie sans modifier la fiche).
- **Évaluer l'évaluateur** : chaque échec est relu. Un contrôle ou une étiquette ne se corrige que s'il est faux,
  jamais pour faire passer un modèle, et la correction est consignée dans `docs/JOURNAL.md`.
