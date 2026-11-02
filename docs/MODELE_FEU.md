# Modèle de feu de Fire Department 3 — spécification

Document de référence pour la réimplémentation C#. Il décrit le comportement observé dans les
données du jeu et dans l'analyse de l'exécutable, reformulé ici avec nos propres termes.
Chaque règle porte un niveau de confiance :

- **[confirmé]** : vu dans les données et dans le code du jeu ;
- **[déduit]** : très probable, cohérent avec le code mais une partie reste à vérifier ;
- **[hypothèse]** : choix provisoire, à valider en comparant avec le jeu original.

## 1. Les acteurs

| Élément | Rôle |
|---|---|
| **Foyer** (hotpoint, type 11) | Point qui peut brûler. Porte une intensité. |
| **Point de fumée** (smokepoint, type 12) | Accumule la fumée d'une pièce, peut déclencher un embrasement généralisé (flashover). |
| **Lien** | Relie deux points. Porte un type, un sens, un tempo et éventuellement une ouverture (porte, fenêtre). |
| **Réglages de mission** | `[DIFFICULTY_*]` et `[WATER]` du fichier `.mis`. |

Chaque foyer appartient à une classe (`Design\Hotpoint\*.class`) résolue par héritage `[#INCLUDE]`.
Les valeurs du niveau (intensité actuelle, état) sont sauvegardées dans le `.level` (composants
`FD00`, `FE00`).

## 2. Paramètres d'un foyer (section `HOTPOINT_PROPS`)

| Paramètre | Sens | Unité |
|---|---|---|
| `InitialIntensity` | intensité donnée à l'allumage si le foyer était exactement à 0 (le jeu force 1) | points |
| `IntensityMax` / `IntensityMin` | bornes de l'intensité ; le minimum est négatif (foyer mouillé) | points |
| `LightingThreshold` | chaleur reçue au-delà de laquelle le foyer chauffe vers l'allumage | points |
| `LightingRange` | portée à laquelle ce foyer chauffe ses voisins reliés | unités (1 cellule = 100) |
| `Tempo` | durée minimale de chauffe avant allumage | secondes |
| `GrowthRate` | croissance de l'intensité pendant la phase de croissance | points / s |
| `GrowthTime` | durée de la phase de croissance (−1 = infinie) | secondes |
| `DeathRate` | variation après la phase de croissance (négative = déclin) | points / s |
| `LifeDamageRange`, `HeatRangeMax` | portée des dégâts, interpolée selon l'intensité | unités |
| `SlopeFactor` (section `HOTPOINT_TAGS`) | renforce la propagation vers le haut | sans unité |
| `UnderWindInfluence` | le vent agit sur ce foyer | 0 / 1 |

Multiplicateurs de difficulté `[confirmé]` : le tempo d'allumage est multiplié par
`Tempo` de la difficulté, la croissance par `GrowthRate` de la difficulté.

## 3. Cycle de vie d'un foyer `[confirmé]`

```
   éteint / mouillé ──(chaleur > seuil pendant Tempo)──▶ en croissance ──(GrowthTime écoulé)──▶ en déclin
        ▲                                                                                     │
        └─────────────── séchage (intensité < 0) ◀──── consumé ◀──(intensité passe sous 0)────┘
```

États internes : **éteint** (peut s'allumer), **en chauffe**, **consumé** (ne se rallume plus,
sèche seulement), **en croissance**, **en déclin**.

### 3.1 Séchage d'un foyer mouillé
Tant qu'un foyer non allumé a une intensité négative, elle remonte vers 0 :

> intensité += (`NaturalEvaporationRate` + `FireEvaporationFactor` × chaleur reçue) × dt, plafonné à 0.

Valeurs par défaut des missions : 1,5 et 0. La plupart des foyers sont sauvegardés à −30 : ils ne
peuvent donc pas s'allumer avant environ 20 s. C'est un délai voulu par les concepteurs.

### 3.2 Chaleur reçue
À chaque mise à jour, un foyer non allumé additionne la contribution de chacun de ses voisins
allumés (et non bloqués) :

> contribution = intensité du voisin + terme de pente + terme de vent, retenue seulement si positive.

- **Pente** `[confirmé]` : avec `d` la composante verticale de la direction voisin → foyer
  (vecteur unitaire), si `d ≥ 0` : `(SlopeFactor − 1) × d × intensité` ; si `d < 0` la contribution
  est réduite : facteur `1 − |d| × (1 − 1/SlopeFactor)` appliqué, moins 1.
- **Vent** `[confirmé]` : si la puissance du vent `P > 1` et que les deux foyers sont soumis au
  vent, avec `c` le produit scalaire direction du vent · direction voisin → foyer : si `c ≥ 0`,
  ajout de `(P − 1) × c × intensité`.
- S'y ajoute la chaleur transmise par les liens à retardement (§ 4.2), remise à zéro ensuite.

### 3.3 Allumage
Si la chaleur reçue dépasse `LightingThreshold`, un compteur de chauffe avance de `dt`.
Quand il dépasse `Tempo × tempo de difficulté`, le foyer s'allume (intensité mise à 1 si elle
valait 0) et passe en croissance. `[confirmé]`
Si la chaleur retombe sous le seuil, le compteur cesse d'avancer mais n'est pas remis à zéro. `[déduit]`

### 3.4 Combustion
Le temps de combustion avance de `dt` tant que l'intensité est positive.
- Pendant `GrowthTime` (ou indéfiniment si −1) : intensité += `GrowthRate` × croissance de
  difficulté × dt, plafonnée à `IntensityMax`.
- Ensuite : intensité += `DeathRate` × dt. Si elle passe sous 0, le foyer s'éteint et devient
  **consumé**.

L'intensité est toujours bornée entre `IntensityMin` et `IntensityMax`. `[confirmé]`

### 3.5 Portée des dégâts
Portée = `LifeDamageRange` + (intensité / `IntensityMax`) × (`HeatRangeMax` − `LifeDamageRange`)
quand l'intensité est positive, sinon `LifeDamageRange`. `[confirmé]`

## 4. Les liens

Champs d'un lien sauvegardé : ouverture traversée, tempo, identifiant, type, sens, actif.

| Type | Relie | Effet |
|---|---|---|
| 0 | foyer ↔ foyer | voisinage direct : chacun compte dans la chaleur reçue de l'autre |
| 1 | foyer ↔ foyer (ou fumée ↔ fumée) | transmission à retardement |
| 2 | foyer ↔ fumée | le feu alimente la fumée |

Sens : 0 = dans les deux sens, 1 et 2 = un seul sens. `[confirmé]`

### 4.1 Voisinage (type 0) `[déduit]`
Si le lien est actif, les deux points deviennent voisins selon le sens. Sinon ils ne le deviennent
que si leur distance est inférieure à la `LightingRange` du foyer qui chauffe.

### 4.2 Retardement (type 1) `[confirmé]`
Le lien se met à « pousser » quand la source brûle et que son intensité atteint le
`LightingThreshold` de la cible. Son minuteur avance alors de `dt` ; une fois le tempo du lien
atteint, l'intensité de la source est ajoutée à la chaleur reçue par la cible à chaque mise à jour.

### 4.3 Ouvertures `[hypothèse]`
Un lien qui traverse une porte ou une fenêtre change d'état quand l'ouverture s'ouvre, se ferme
ou casse. Le détail reste à analyser.

## 5. Fumée (première lecture, à compléter)

- Paramètres : `HeatRange`, `InfluenceRange`, `FlashoverThreshold`, `FirstFlashoverTempo`,
  `FlashoverTempo`, `AccumulationThreshold`, `MinThresholdBeforeExtraction`, `Extractor`,
  `ExtractionRate`, dégâts chaleur/oxygène, `Corrosive`.
- **Extracteur** `[confirmé]` : un point extracteur perd `ExtractionRate` × dt d'intensité.
- **Répartition** `[déduit]` : la fumée se répartit entre points reliés d'une même pièce, sans
  dépasser leur maximum.
- **Flashover** `[confirmé]` : dans une pièce où tous les points de fumée sont au-dessus de
  `FlashoverThreshold` et où aucun foyer ne brûle encore, un minuteur avance jusqu'à
  `FirstFlashoverTempo` (puis `FlashoverTempo` pour les suivants). À l'échéance, tous les foyers
  non allumés de la pièce s'enflamment d'un coup.

## 6. Extinction — première mesure (London Facile, médecin, sauvegardes Eau0 à Eau3)

Foyer arrosé : `ground_l109` (max 120, GrowthRate 3, Tempo 40). Temps de jeu des sauvegardes :
47,0 s / 67,0 s / 78,2 s / 101,8 s. Intensité : 120 → 59,8 → 0 → 0 ; temps de combustion arrêté à
**73,2 s** (moment de l'extinction).

**Règles `[mesuré]`** :
- Perte d'intensité par seconde = `FireAttackPower` de l'unité (7, classe `base_fireman`, le médecin
  en hérite) × `FireAttackPower` de la difficulté (London Facile : 1,6) = 11,2, **moins la croissance
  du feu qui continue** (3 × 0,6 = 1,8), soit 9,4 par seconde net. Avec un début d'arrosage à 60,6 s
  (déduit de la 2e sauvegarde), la simulation éteint le foyer à 73,4 s (jeu : 73,2 s).
- Éteint, le foyer tombe à 0 (pas en dessous), son temps de combustion s'arrête et il redevient
  « en chauffe » (octet d'état +13 du bloc final : 3 = en feu, 1 = en chauffe). Ses voisins en feu
  le chauffent tout de suite (263 reçus, seuil 30) : **il se rallume Tempo × 1,5 = 60 s plus tard**
  (vers 133 s) si personne ne l'arrose de nouveau. Un foyer mort de sa belle mort, lui, ne se rallume pas.
  **Confirmé par la sauvegarde Eau4 (136,4 s)** : rallumé à 133,2 s, intensité 6,76 = 1 + 1,8 × 3,2
  (exact). Le temps de combustion **reprend là où il s'était arrêté** (76,4 = 73,2 + 3,2), il n'est pas
  remis à zéro : le déclin arrivera donc plus tôt pour un feu déjà éteint une fois.
- Portée de dégâts (+92) : `LifeDamageRange + (HeatRangeMax − LifeDamageRange) × intensité / max`
  (400 à 120, 299,7 à 59,8, 200 éteint) `[confirmé]`.

**Réserve `[confirmé, Eau0–Eau4]`** : la réserve du médecin (250 au départ) vaut 244,88 dans Eau1 et
239,76 dans Eau2 à Eau4 : elle baisse de `FireAttackCost` de l'unité (1) × `FireAttackCost` de la
difficulté (0,8) = 0,8 par seconde d'arrosage. Cela date le début de l'arrosage à 60,6 s,
exactement la valeur déduite de l'intensité : les deux mesures se confirment. La simulation
(`Extinguisher`, classe lue dans `Unit\Fireman\…`) donne la même réserve finale, 239,76.

**Types de feu et agents `[lu dans les données]`** : chaque classe de foyer a un type
(`HOTPOINT_TAGS / Type`) : A ordinaire, B liquides (essence, cuves), C électrique, D métaux/produits
toxiques. L'efficacité d'un agent dépend du type (`base_fireman.class`) :

| Agent | A | B | C | D |
|---|---|---|---|---|
| Eau | 1 | 0,3 | 1 | 0 |
| Mousse | 0,3 | **3** | 0,3 | 0 |
| Halon | 1 | 1 | 1 | 1 |
| Poudre | 0,2 | 0,2 | 0 | 1 |
| Métal | 0 | 0 | 0 | 1 |

La mousse n'est donc vraiment utile que sur les feux B (10 fois plus efficace que l'eau). Feux B :
Tutorial 25 (avec un camion de mousse), London 0, Fireworks 0 ; nombreux dans les données des autres
missions (Duseldorf 286, Mriya 237, Military 231…). Commande : `typesfeu <Data> [missions…]`.
**Feux électriques (C) `[observé par le joueur + textes du jeu]`** : tant que le courant n'est pas
coupé, aucun agent n'a d'effet (le jeu dit « VOCAL_IMPOSSIBLE_TO_ATTACK_ELECTRICAL_FIRE ») et le
pompier est électrocuté (tâche `TskElectrocut`) : il perd de l'endurance, `ElectricStaminaCoef` = 20
pour un pompier, multiplié par `ElectricityStamina` de la mission (London Facile 0,6). Le courant se
coupe en utilisant une armoire électrique (Tutorial : `elec_closet_6_1`, objet à état logique) ;
l'efficacité 1 de l'eau sur C ne vaut sans doute qu'après la coupure (à vérifier).

Hypothèse à vérifier : perte = puissance × difficulté × efficacité, moins la croissance.

**Reste à mesurer** : mouillage d'un foyer éteint (IntensityMin = −60 mais l'intensité est restée à 0),
mousse et autres agents sur des feux B/C/D, effet de `FireAttackSplash` (150) sur les foyers voisins.

Commande : `simulate … "arroser=ground_l109@60.6-74"` (option `x<puissance>` pour une autre unité).

## 7. Premier essai (London, difficulté normale, 15 minutes simulées)

15 foyers en feu au départ, environ 50 au bout de 5 minutes, premiers foyers consumés vers
6 minutes, 44 consumés à 10 minutes, puis 20 foyers qui brûlent indéfiniment (classes à
`GrowthTime` = −1). Seuls 178 foyers sur 382 sont reliés à un voisin : les autres ne peuvent
pas s'allumer dans cette version, ce qui indique un second mécanisme de voisinage (question 6).

## 7 bis. Comparaison avec le jeu (vidéos du 31/10, mini-carte analysée seconde par seconde)

La mini-carte affiche les foyers en feu en rouge. En la superposant au niveau (axe Y inversé par
rapport aux coordonnées du fichier), 70 % (London) et 81 % (Fireworks) des pixels rouges tombent
sur un foyer.

| | London (jeu) | Fireworks (jeu) |
|---|---|---|
| En feu au début du jeu | 41 | 65 |
| Pic / plateau | ≈ 96 vers 3 min 30, puis déclin (49 à 7 min) | ≈ 220 après 2 min |
| Foyers touchés au total | 154 sur 382 | 239 sur 1 162 |

**Propagation par proximité `[confirmé par l'observation]`** : sur 167 allumages en cours de jeu
dans Fireworks, 165 n'avaient aucun lien avec un foyer déjà en feu ; le feu le plus proche était
à 390 unités en médiane (750 pour 90 % des cas), soit l'ordre de la `LightingRange`.
La simulation utilise donc : un foyer allumé chauffe tous les foyers dans sa `LightingRange`,
plus les liens.

**Feux de départ** : allumés pendant la cinématique (explosion du carambolage dans London),
pas par l'intensité sauvegardée ni par le script.

**Écarts restants** :
- Fireworks : simulation un peu lente et plafond trop bas (≈ 190 contre 220).
- London : simulation trop étendue (165 contre un pic de 96) et pas de déclin vers 4 min.
  Il manque une barrière (murs, pièces) : bloquer la chaleur par les cellules de grille de zone 0
  est trop strict ; il faut sans doute raisonner par pièce.

## 7 ter. Pièces, portes et durée de combustion (3e vidéo London)

**Pièces `[confirmé]`** : les pièces sont des volumes 3D invisibles dans l'archive graphique
de la mission (`Bank_ROOM1`… : 28 pour London, 13 pour Fireworks). Leur boîte englobante est
stockée à la fin du fichier `.mesh` ; leur position vient du graphe de scène `.sg`
(pièce → bâtiment → sol). Elles coïncident exactement avec les murs de la grille.
213 foyers de London sur 382 sont dans une pièce, les autres dans les couloirs, la cour ou dehors.
Les zones de la grille, elles, ne sont pas des pièces (aucun effet de barrière mesuré).

**Reproductibilité** : deux parties de London donnent 154 et 135 foyers touchés, dont 127 en commun.

**Règles testées** :
| Variante | Écart London | Foyers en trop | Foyers manquants |
|---|---|---|---|
| Proximité seule | 64 | — | — |
| Proximité limitée à la pièce | 60 | 77 | 47 |
| + liens bloqués par une porte fermée | 51 | 64 | 52 |

**Durée de combustion `[écart majeur]`** : dans le jeu, les foyers s'éteignent bien plus tôt que
ne le prévoient leurs paramètres.
| Classe | Durée observée (médiane) | Durée prévue par les paramètres |
|---|---|---|
| ground_s | 239 s | 240 s |
| ground_m | 154 s | 320 s |
| ground_l | 172 s | 380 s |
| groundmulti_l | 214 s | ≈ 1 370 s |

Piste principale : le combustible, c'est-à-dire les meubles rattachés à chaque foyer
(liste du composant `FE00`, objets « carbonisables »).

## 7 quater. Mesure par sauvegarde de partie (London, 455,6 s de jeu) — référence fiable

Une sauvegarde contient l'état complet de chaque foyer. C'est la mesure la plus fiable :
**la mini-carte n'affiche pas tous les feux** (75 points rouges pour 159 foyers allumés),
ce qui faussait les conclusions des sections 7 bis et 7 ter.

**Composant `FE00` `[confirmé]`**
- Début (bloc v3, après l'en-tête de 17 octets) : +5 allumé (octet), +10 intensité courante,
  +14 intensité max (flottants).
- Bloc final de 96 octets (entre les deux derniers marqueurs de fin) : paramètres **propres au
  foyer**, qui remplacent ceux de la classe dans 25 à 35 % des cas (réglage manuel des concepteurs) :
  +34 LifeDamageRange, +42 temps de combustion, +46 IntensityMin, +54 HeatRangeMax,
  +58 LightingRange, +62 LightingThreshold, +66 Tempo, +70 GrowthRate, +74 DeathRate,
  +78 GrowthTime, +83 SlopeFactor, +92 portée de dégâts courante.
- La carte d'origine contient déjà l'état de départ : **34 foyers allumés** dans London.
- Le composant `FD00` ne contient que des valeurs de départ inutilisées en cours de partie.

**Croissance `[confirmé]`** : intensité = min(max, 1 + GrowthRate × coefficient de difficulté × temps),
vérifié exactement sur des foyers allumés pendant la partie (normal : 0,7).

**Historique réel** (temps de combustion soustrait du temps de la sauvegarde), foyers allumés
cumulés toutes les 30 s : 34, 36, 37, 61, 66, 74, 86, 93, 102, 117, 125, 133, 139, 152, 154, 160.
Certains groupes s'allument ensemble à intervalles réguliers (85,6 s).

**Comparaison avec la simulation, état initial et paramètres lus dans `FE00`**
| Variante | Écart moyen |
|---|---|
| Liens seuls (modèle du code du jeu) | **12** |
| Proximité | 126 |
| Proximité par pièce | 116 |
| Pièce + portes fermées | 106 |

Conclusion : le modèle par liens, tiré du code, est le bon ; la proximité était un artefact.
Hotpoint par hotpoint, 58 allumages sur 128 coïncident. Écarts restants :
- 48 foyers de rue (hors pièce) allumés vers 60–77 s dans le jeu et pas dans la simulation :
  zone du carambolage, mécanisme des véhicules en feu à ajouter ;
- 42 foyers en pièce allumés par la simulation mais pas par le jeu : passage par les portes à revoir.

## 7 quinquies. Modèle retenu (validé sur la sauvegarde London, difficulté Facile)

**Difficulté de la partie de référence : Facile** (tempo × 1,5, croissance × 0,6), établie par
deux mesures exactes : délai d'allumage = Tempo × 1,5 (52,7 s pour 35, 60,2 s pour 40, 75,4 s
pour 50) et croissance 1 + 5 × 0,6 × 29 = 88.

**Liens : réinterprétation `[confirmé]`**. Dans les 35 octets d'un lien, le flottant que l'on
prenait pour le tempo est le **minuteur en cours** (0 dans la carte), et l'octet suivant indique
que le lien « pousse ». Seuls des liens de type 1 sans porte ont poussé pendant la partie.

**Règles du modèle retenu** :
1. État initial et paramètres de chaque foyer lus dans `FE00` (section 7 quater).
2. Un foyer allumé chauffe les foyers **de la même pièce** situés à moins de sa `LightingRange`
   (distance 3D). Les foyers hors pièce (rue, cour, couloirs) forment un même espace.
3. Liens de type 1 sans porte : transmission de la chaleur une fois que la source dépasse le seuil
   de la cible. Les liens de voisinage (type 0) ne transmettent pas la chaleur.
4. Allumage après Tempo × multiplicateur de difficulté passé au-dessus du seuil ; croissance,
   déclin et séchage selon la section 3.

**Résultat** (simulation complète contre la sauvegarde, 455,6 s) :
| | Réel | Simulé |
|---|---|---|
| Foyers allumés au total | 160 | 164 |
| Allumages en jeu reproduits | — | 110 sur 128 |
| Dont à moins de 15 s de la vraie date | — | 94 |
| Allumages en trop | — | 20 |

Courbe cumulée toutes les 30 s — réel : 34, 36, 37, 61, 66, 74, 86, 93, 102, 117, 125, 133, 139,
152, 154, 160 ; simulé : 34, 34, 41, 72, 78, 88, 104, 110, 118, 127, 134, 145, 150, 159, 160, 164.

Commande : `simulate <Data> London 7.6 EASY pieces=<carte des pièces> sans-voisinage 3d`
(ou `pieces=auto` : pièces calculées en C# par `FD3.Formats.Rooms`, résultat identique aux scripts Python).

## 7 sexies. Validation sur Fireworks (sauvegarde en Facile, 202,8 s)

82 foyers allumés au départ, 156 en cours de partie. Sans aucun réglage, le modèle retenu sur
London reproduit **122 des 156 allumages et n'en invente aucun**, avec environ 35 s de retard.

Le retard vient des **événements scriptés** de la mission (`fireworks_main.scp`) : une explosion
fait sauter un mur ; le script ajoute de l'intensité aux groupes `explo_fire` (+100, 23 foyers
allumés d'un coup à 24,8 s), `echelon_1/2/3` (+10, +10, +30 à environ 44, 59 et 69 s), et double
la croissance des groupes `officefire` et `warefire` (« GrowthBoost », vers 43 s).

Rejoués naïvement (+100 sur chacun des 70 foyers du groupe), ces événements font trop de feu
(349 contre 235). Hypothèse à vérifier : `FIR_HPAddIntensity` sur un groupe répartit la quantité
entre ses foyers (la lecture d'intensité d'un groupe renvoie une somme). Il faudra un
interpréteur fidèle des scripts de mission.

Commande : `simulate <Data> Fireworks 3.4 EASY pieces=<…> sans-voisinage 3d evenements=<…>`.

## 7 septies. Interpréteur de scripts et retour sur Fireworks

**Interpréteur (`FD3.Script`)** : les 56 fichiers `.scp` du jeu (1 519 fonctions) sont analysés sans
erreur. La commande `mission` joue une mission complète (fonction de démarrage, boucles
`MissionScript1/2` toutes les 0,5 s, appels différés) en même temps que le feu. Fireworks tourne
sans erreur de script et reproduit l'échec de mission vers 4 min observé par le joueur.

**Fireworks, corrections** :
- L'explosion scriptée (`explo_fire`, `echelon_*`) n'a **pas** eu lieu dans la partie de référence :
  aucun de ses foyers ne s'est allumé. Elle dépend d'une action du joueur.
- Les réglages de difficulté sont propres à chaque mission (Fireworks Facile : tempo × 1,4,
  croissance × 0,6 ; London Facile : tempo × 1,5).
- Horloge : le compte à rebours `TrainTimer` (sauvegardé à 17, parti de 210, décrémenté toutes les
  0,95 s à partir de 10 s) date la sauvegarde à environ 193 s de temps de script, contre 202,8 s
  d'après les temps de combustion : le feu démarre environ 10 s avant les scripts. Les dates
  d'allumage mesurées sur l'horloge du feu restent valables.

**Écart non expliqué** : 23 foyers extérieurs (`ground_l`, tempo 40) s'allument ensemble à 24,8 s,
alors que le modèle impose au moins 40 × 1,4 = 56 s. Aucun compteur de chauffe n'est stocké dans
la carte (bloc d'état `FE00` à zéro). Pistes : explosion de caisses de feux d'artifice
(1 297 objets explosifs dans la carte), dégâts aux objets, ou une règle de chauffe différente
quand plusieurs sources sont proches.

## 7 octies. Sauvegardes rapprochées de Fireworks (16,8 s et 35 s)

- **Reproductibilité** : la même salve de 23 foyers se produit à 24,8 s que dans la partie
  précédente. Le jeu est déterministe.
- **Nouveaux champs du bloc de 96 octets `[confirmé]`** : +38 = compteur de chauffe (secondes passées
  au-dessus du seuil), +87 = chaleur reçue (somme des intensités des voisins en feu), +13 = drapeau
  de chauffe. Dans la carte, ils valent 0.
- **Liste d'objets rattachés** (début de `FE00`) : elle s'allonge en cours de partie quand un objet
  mobile (caisse projetée) entre dans la zone du foyer.
- **Allumage** : certains foyers s'allument exactement à Tempo × 1,4 (conforme au modèle) ; d'autres,
  chauffés depuis le début, s'allument plus tôt, par vagues synchronisées (16,6 s, 24,8 s, 28 s,
  33,2 s), quelle que soit la quantité de chaleur reçue. Leur compteur de chauffe vaut pourtant
  16,8 s à 16,8 s comme les autres.
- **Écartés** : explosion scriptée, objet déclencheur commun, décalage d'horloge, vent (aucune
  entité vent dans les cartes), compteur de chauffe pré-rempli dans la carte.
- **Pistes restantes** : traitement des foyers par lots dans le gestionnaire de feu
  (« UseWorkingHotPoints »), ou une seconde condition d'allumage dans le code, à relire.

## 9. Fumée — mesures (Fireworks, carte et sauvegardes à 16,8 s, 35 s, 202,8 s)

**Composant `FE01` `[confirmé]`** (115 octets) : bloc v3 (+5 actif, +10 intensité courante, +14 max),
puis un bloc de paramètres propres au point : FirstFlashoverTempo, MinThresholdBeforeExtraction,
AccumulationThreshold, FlashoverTempo, taux d'extraction, dégâts chaleur/oxygène, HeatRange,
FlashoverThreshold, HotPointIntensityDamage, InfluenceRange.

**Observations** :
- Au départ, 71 points contiennent 25 ou 50 (fumée posée par les concepteurs), 179 sont vides.
- **Le total est exactement conservé** (2 650 à chaque sauvegarde) et **conservé pièce par pièce** :
  une pièce à 250 reste à 250 (5 × 50), l'extérieur garde 2 400 répartis entre ses points.
- Les points vides au départ deviennent actifs quand un voisin leur cède de la fumée (41 à l'extérieur) ;
  51 points extérieurs, trop loin ou derrière un mur, restent vides et inactifs.
- Aucune production de fumée par les foyers pendant ces 3 minutes : les feux au sol ont
  `GenerateSmoke = 0` et Fireworks n'a aucun lien foyer → fumée (type 2). London en a 10.

**Diffusion `[mesuré, ajusté sur 3 sauvegardes]`** : deux points de la même pièce (ou tous deux
dehors), dont au moins un est actif, échangent de la fumée si la distance horizontale `d` est
inférieure à la somme de leurs `InfluenceRange` (`R = Ri + Rj`, soit 1 200 pour deux points XL) et
s'il n'y a pas de mur de la grille entre eux. Débit de i vers j : `0,3 × (1 − d/R) × (vi − vj)` par
seconde. La quantité totale est conservée. Écart moyen avec le jeu : 2 unités (sur 0–50) à 16,8 s,
35 s et 202,8 s ; 2 points sur 250 reçoivent de la fumée à tort (test de mur approximatif).

**Production `[vérifié sur London, sauvegarde « London7 » à 419 s]`** : un foyer en feu relié à
un point de fumée par un lien de type 2 le remplit jusqu'au maximum. Seuls `co2_int_m45` et
`co2_int_m46` (pièce `Bank_ROOM20`, 2 points) sont à 100, comme dans la simulation ; les 8 autres
liens de type 2 partent de foyers jamais allumés. Le débit exact (`SmokeGenerationRate`, 20 par
défaut) reste à mesurer : il suffit qu'il dépasse 2 par seconde.

**Extraction et flashover** : codés selon la lecture du code (`ExtractionRate` × dt pour un point
extracteur ; flashover = pièce entièrement au-dessus de `FlashoverThreshold`, sans foyer allumé,
pendant `FirstFlashoverTempo` puis `FlashoverTempo` secondes → `HotPointIntensityDamage` ajouté aux
foyers éteints de la pièce). Pas encore observés dans une sauvegarde.

**Découverte annexe (feu, London7)** : le temps de combustion de certains foyers allumés au départ
n'avance pas pendant une partie de la mission. Les 7 foyers de `Bank_ROOM20` n'ont que 52,6 s de
combustion à 419 s, 6 foyers `groundmulti_xs` 104 s ; les autres 419 s. Ces foyers semblent
« en sommeil » puis réveillés vers 366 s et 315 s (ce qui correspond au feu de la banque vu dans
la vidéo vers 6 min). Le simulateur, lui, les fait brûler dès le début puis décliner et s'éteindre
avant 419 s. Le mécanisme de réveil reste à trouver (piste : `UseWorkingHotPoints`).

Commande : `simulate <Data> Fireworks 3.38 EASY pieces=<…> sans-voisinage 3d fumee=tools/rooms_all_Fireworks.json fumee-murs`
(aussi disponible pour la commande `mission`).

## 8. Questions ouvertes

1. Fréquence de mise à jour du feu (nous simulons à pas fixe de 0,1 s en attendant).
6. Voisinage hors liens : par pièce (zones de la grille), via les listes d'ouvertures des foyers
   (`FE00`) ou via le gros bloc de données de la section 2 du `.level`.
2. Rôle exact de l'état « actif » d'un lien et effet des portes.
3. Débit exact de production de fumée (`SmokeGenerationRate`) ; extraction et flashover à observer.
7. Foyers allumés « en sommeil » (London : banque) et leur réveil.
4. Extinction : mouillage, mousse, réserve d'eau (voir § 6).
5. Drapeaux sauvegardés dans `FD00` (dont l'activation initiale).

## 10. Portes et fenêtres

**Données `[lu dans les fichiers]`** : objets dont `[OCCUPATION] Occupation_Type` vaut `DOOR` ou `WINDOW`
(London : 34 portes, 32 fenêtres). État de départ dans le composant `A027` (état logique : 0 = `Close`,
1 = `Open`). `[CAPACITIES_ACTIONS] CouldBeBreak` / `CouldBeUse` disent si l'ouverture se casse et si elle
s'ouvre à la main (les portes du cinéma de London : `CouldBeBreak = 0`, `CouldBeUse = 1`). Vie :
`[DAMAGE] LifePoints` (100) ; `HitDamageCoef` = 10,73 pour une porte, 166 pour une fenêtre.
Côté unités : `CanBreak` (pompiers : 1, médecin : 0), `CanUse`, `BreakPower` (1).

**Règles retenues** : un coup de hache toutes les 1,2 s retire `HitDamageCoef × BreakPower × 3,11`
(facteur réglé pour qu'une porte tombe en 3 coups, comme dans le jeu ; une fenêtre tombe en 1 coup).
Une porte fermée bloque le chemin ; ouverte ou cassée, on passe. Les liens de feu de type 0 portent aussi
une ouverture (`ouverture` dans l'outil `liens`), non utilisée pour l'instant.

**Fumée `[estimé]`** : une porte ouverte ou une ouverture cassée relie les points de fumée des pièces de part et
d'autre (dans leur `InfluenceRange`), ou, s'il n'y a qu'une pièce autour, le point le plus proche d'une
autre pièce ou du dehors (moins de 15 m). Échange `0,3 × poids × (vi − vj)`, quantité conservée. Les
portes ouvertes dès le départ ne changent rien aux mesures de Fireworks et London (vérifié).
