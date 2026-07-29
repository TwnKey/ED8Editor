# Écrire un modèle de zéro — feuille de route

Objectif fixé par Antoine, et il ne se négocie pas : **l'utilisateur choisit son
FBX, l'outil l'insère en jeu comme nouveau personnage.** Pas de question posée,
pas de gabarit à désigner, pas de « sur quel personnage tu t'appuies ». Donc pas
de modèle donneur : on écrit le `.dae.phyre` intégralement.

Ce document décrit le flot qui reste. Il est écrit pour survivre à une perte de
contexte : chaque phase dit ce qu'on produit, comment on le vérifie, et ce qui
reste inconnu à son entrée.

---

## 0. Décisions déjà prises

**Les shaders ne sont pas écrits de zéro.** Un `PMaterial` désigne une
permutation compilée `ed8.fx#<hash>` livrée avec le jeu ; en produire une neuve
demanderait de reproduire la chaîne de compilation de Falcom à l'identique.
On lie donc les shaders existants par leur hash, exactement comme le jeu le
fait. C'est la seule dépendance au contenu livré, et elle est légitime : ce sont
les shaders du moteur, pas le gabarit d'un personnage.

**Conséquence à ne jamais perdre de vue** : lier un shader oblige à connaître
*précisément la taille et la disposition de ses paramètres*. Un
`PParameterBuffer` mal dimensionné n'est pas une approximation qui donne un
rendu moyen — c'est de la mémoire lue de travers par le GPU. La taille des
paramètres se lit dans les `PShaderParameterDefinition` du cluster shader ; elle
se déduit, elle ne s'estime pas.

**Les animations, il en faut deux sortes.** Les deux cas d'usage existent :

1. **Animations custom** — celles du FBX importé, écrites depuis zéro.
2. **Adaptation des animations du jeu** — reprendre un clip livré et le porter
   sur un squelette maison.

Le second n'est pas un repli sur le premier : c'est une fonctionnalité à part
entière, et c'est elle qui rend un personnage neuf immédiatement utilisable
(marche, course, combat) sans que l'auteur ait à animer quoi que ce soit. La
méthode est connue et déjà éprouvée ailleurs : retarget **rotation locale
seulement**, appariement par nom de bone. Un squelette maison exige donc soit
des noms de bones reconnaissables, soit une table de correspondance éditable —
et quand l'appariement échoue, l'outil le **dit précisément**, il n'approxime
jamais en silence.

---

## 1. Acquis — ce qui est déjà prouvé à l'octet près

Ne pas refaire ce travail. État vérifié :

| Brique | Preuve |
|---|---|
| Conteneur complet (11 sections) | 617/617 clusters reconstruits depuis leur contenu |
| Namespace empaqueté | produit depuis le schéma parsé, identique |
| Tables de fixups (5 empaquetages, tri moteur) | identiques, y compris modèles |
| Texture entière depuis une image | 174/174 |
| Charge GPU d'un modèle réécrite | 7/7 `ply000`, 0 différence |
| Localisation des tampons + adresse de leurs descripteurs | `PMeshSegment` +0x40/+0x48, `PDataBlockD3D11` +0x28/+0x30 |
| Règle de disposition | alignement 4 o, compté depuis le début de **chaque** région |

Ce qui **n'a jamais été fait** : charger un fichier écrit par nous **dans le
jeu**. Aucune validation in-game n'existe à ce jour, pour aucun asset.

---

## 2. Phase A — le schéma des classes en code — **FAITE**

`src/ED8Editor.Phyre/Authoring/PhyreSchemaLibrary.cs`, générée par le mode
`--emit-library` de `tools/PhyreAuthoringProbe`.

**Le fait fondateur, mesuré et non supposé** : sur les **32 796 clusters** que le
jeu livre, on compte **175 classes distinctes et 521 membres**, et **aucune
classe n'est jamais décrite de deux façons différentes** (`--schema-union`,
0 conflit). Une définition de classe est donc une constante du moteur, pas une
disposition par fichier — ce qui autorise une bibliothèque unique, valable pour
tout cluster, modèle compris.

**La bibliothèque parle en noms, jamais en ids.** À l'intérieur d'un cluster, une
classe ou un type est un indice dans les tables de ce cluster ; l'indice ne veut
plus rien dire hors du fichier. Règle de résolution, lue dans
`PhyreNamespacePacked.cpp` (`PPackedIDMapping::getType` ligne 339,
`PNamespaceMapping::getType` ligne 2068) :

- identifiant de type d'un membre : sous le nombre de types il désigne un type,
  au-delà il désigne `classe[id - nombreDeTypes - 1]` ;
- identifiant de superclasse : espace séparé, **1-based sur les classes seules**,
  0 signifiant « aucune ».

`PhyreSchemaLibrary.Descriptors(types, classes)` applique cette règle à
l'envers, pour l'ordre de classes que l'appelant choisit.

**Vérification passée** (`--library-check`) : pour chacun des **32 796** clusters,
on ne reprend du fichier que la liste de ses noms de types et de classes, on
reconstruit la table depuis la bibliothèque seule, on émet le namespace —
**32 796 identiques, 0 écart**.

Reste à faire, mineur : `PhyreTextureSchema` fait doublon avec la bibliothèque et
pourra être retiré une fois le chemin texture rebasculé dessus.

---

## 3. Phase B — l'écrivain d'objets — **le noyau est fait**

`PhyreObjectWriter` écrit les objets d'une liste d'instances depuis ce qu'ils
contiennent : on les dimensionne, on les met à zéro, on pose les membres de
toute la chaîne d'héritage, puis on ajoute la charge qu'une classe en-tête porte
au-delà de sa taille déclarée.

**Contrôle** (`--object-write`) : chaque objet livré est relu en ne gardant que
ce que ses membres nomment, puis réécrit. Un objet qui revient identique prouve
qu'il n'y avait rien d'autre dedans.

**Résultat sur le corpus entier : 32 796 clusters, 5 171 709 objets réécrits,
64 qui ne reviennent pas identiques** — 53 `PCameraOrthographic` et 11
`PCameraPerspective`, c'est-à-dire précisément les objets que la mesure de
couverture avait désignés, atteinte par un chemin indépendant. Ces deux classes
portent des octets non nuls qu'aucun membre déclaré ne couvre ; aucune n'entre
dans le chemin d'un personnage, et elles restent à traiter le jour où l'on
écrirait une caméra.

### Le cluster entier, assemblé

`PhyreClusterWriter.RebuildFromContents` met tout ensemble : le schéma vient de
la bibliothèque, les objets de l'écrivain d'objets, les tables de fixups sont
ré-encodées, les sections composées. Restent recopiés — et c'est la mesure
exacte de ce qui n'a pas encore de forme structurée : les données de tableaux,
la section en-tête, les en-têtes de listes d'instances et la charge GPU.

**Résultat (`--whole-cluster`) : 32 534 clusters sur 32 796 reconstruits à
l'octet près, dont 7 664 modèles sur 7 926.** Les 262 restants sont localisés :

| Où | Combien | Cause |
|---|---|---|
| fixups pointeurs | 227 | choix d'empaquetage à taille **égale** — une égalité tranchée autrement |
| données d'objets | 35 | les deux classes de caméra, déjà connues |

Les 35 recoupent exactement les 64 objets caméra du contrôle précédent, atteints
par un chemin indépendant.

**Un empaquetage manquait** : `PE_PACKED_FIXUP_STRIDED`. Les identifiants
d'objets régulièrement espacés s'écrivent « premier, pas, longueur » au lieu
d'une liste. Le moteur ne le retient que si la série couvre **autant
d'identifiants qu'il y a de fixups** — `m_matchingCount` étant « le nombre de
fixups, qui peuvent partager des objets », un bloc à identifiants dupliqués est
donc exclu. Sans cette seconde condition, on le choisit trop souvent : la
première version corrigeait 344 clusters et en cassait 837.

Depuis, **plus aucun écart de taille** : les 227 restants font exactement la
longueur du fichier livré et ne diffèrent que par la forme choisie. C'est donc
une question de départage entre deux encodages de même longueur, pas de règle
manquante.

Ce qui reste de la phase B est le graphe : quels objets pointent vers quels
autres, et les tableaux qu'ils portent.

### Le détail d'origine

**Produire** : les objets d'un modèle depuis une description structurée
(`PMesh`, `PMeshSegment`, `PMeshInstance`, `PNode`, `PMaterial`,
`PDataBlockD3D11`, `PVertexStream`, `PSkinBoneRemap`, `PSkeletonJointBounds`,
`PWorldMatrix`, `PMatrix4`, `PString`, `PAssetReference`/`Import`, `PLocator`…),
plus le graphe de pointeurs entre eux et la section « header class ».

**Comment mesurer le progrès** : on connaît la géométrie, les matériaux et le
squelette d'un personnage livré — donc on peut tenter de **régénérer ses octets
d'objets** et diffé. Le progrès devient chiffrable : « 2 100 objets sur 2 877
reproduits ». C'est exactement la méthode qui a fait passer la texture du
gabarit au zéro.

### La section « header class » — élucidée

Ce n'était pas un trou du conteneur, c'était la description des paramètres de
shader. Le moteur la lit dans `loadAndFixHeaderClasses`
(`PhyreClusterReaderBinary.cpp:1003`) :

- il parcourt les **groupes d'instances**, et pour chacun dont la classe porte
  `PE_CLASS_DESCRIPTOR_HEADER` (`1 << 2`, soit le bit 0x4 des drapeaux de
  classe), il prend un compteur dans la première zone (4 o par instance) ;
- puis autant d'enregistrements de 16 o dans la seconde : `PHeaderClassChildArray`
  = `{ identifiant de type, offset dans le parent, drapeaux, nombre }`
  (`PhyreSerializationTypes.h:44`) ;
- chaque enregistrement décrit des objets posés **à l'intérieur** de l'objet
  en-tête, au-delà de la taille déclarée de sa classe.

**Mesure sur `ply000.dae.phyre`** : 32 instances en-tête, 1 546 enfants — et les
32 sont des `PParameterBuffer`, un par groupe, un objet chacun (22 groupes à 48
enfants, 10 à 49 : 1 546 ✓). La classe est déclarée à **16 octets** et ses
enfants vont jusqu'à l'offset **568** : un tampon de paramètres est un en-tête
court suivi d'une charge de taille variable, et la section dit exactement ce
qu'il y a dedans — « à 448, quatre `float` », « à 432, un
`PShaderParameterCaptureBufferSampler` », etc.

**Portée** : c'est la réponse à la question que pose la liaison d'un shader
compilé — connaître la taille et la disposition de ses paramètres. La donnée est
lisible, pas à deviner ; elle est même redondante avec le membre
`m_tweakableShaderParameterDefinitions` que le tampon porte, un
`PArray<PShaderParameterDefinition>` où chaque entrée donne nom, type de donnée,
nombre d'éléments et emplacement (`PShaderParameterDefinition`, 16 o :
`m_arrayElementCount` @0, `m_parameterType` @2, `m_dataType` @3, `m_name` @4,
`m_bufferLoc` @8, `m_constantBufferLocation` @12).

**Deux classes en-tête dans tout le jeu**, et pas une de plus :

| Classe | Drapeaux | Lecture |
|---|---|---|
| `PParameterBuffer` | `0xC` | en-tête + `NO_DEFAULT_CONSTRUCTOR` — empaquetage lâche |
| `PAnimationClipBinding` | `0x14` | en-tête + `HEADER_TIGHTLY_PACKED` |

(`PhyreClassDescriptor.h:22`.) La seconde concerne directement la phase F : écrire
une animation demandera de produire cette section aussi.

**Vérifié sur le corpus** (`--header-class-check`) : **7 937 clusters portent une
section en-tête, 47 572 instances**, et sur ce total —

- **0** cluster où le nombre de groupes marqués en-tête diffère du compte
  déclaré : la section s'indexe donc sans la lire, par simple parcours des
  groupes ;
- **0** tampon de paramètres dont le nombre d'enfants diffère du nombre de
  paramètres de shader qu'il déclare. **Un enfant par paramètre, sans exception.**

Sur `ply000`, les enfants du premier tampon vont de 16 à 572, soit exactement la
taille de l'objet dans la liste d'instances — l'objet fait 572 octets pour une
classe déclarée à 16.

**Ce qui reste ouvert** : l'ordre des enfants n'est pas celui des offsets
(568, 564, 560, puis 448 avec quatre éléments…), il suit l'ordre des paramètres
du shader, la charge étant rangée par taille d'élément. Reproduire ce rangement
n'est pas nécessaire tant qu'on **lie** un shader existant : la disposition se
reprend telle quelle depuis un matériau livré qui utilise le même shader. Il le
deviendrait si l'on voulait un jeu de paramètres inédit.

### Acquis : ce qu'un objet contient vraiment

Une classe ne se décrit qu'en partie — `PTexture2D` fait 112 octets et ne
déclare aucun membre, ses champs venant des classes dont elle dérive. Deux
règles, mesurées :

1. **Les membres se cumulent le long de la chaîne d'héritage**, et un tableau
   fixe compte une fois par élément : un `PMatrix4` déclare un membre de quatre
   octets répété seize fois, pas quatre octets de matrice et soixante de
   mystère. (`member.Size × max(FixedArraySize, 1)`.)
2. **Ce que les membres ne couvrent pas est à zéro dans le fichier** — ce sont
   les champs que le moteur remplit au chargement, pointeurs de ressource et
   compagnie.

Mesure sur le corpus entier (`--object-coverage`) : **5 171 709 objets,
181 570 432 octets, 158 155 417 couverts par des membres déclarés, 23 415 015
laissés libres — dont 155 seulement ne sont pas nuls**, tous dans
`PCameraOrthographic` (125) et `PCameraPerspective` (30). Aucune classe de
modèle, de matériau ou de squelette n'en fait partie.

**Portée exacte de cette mesure** : elle lit chaque objet sur la taille de sa
classe, qui est ce que `PhyreClusterData.GetObject` rend. Il fallait donc
vérifier que les listes d'instances ne rangent pas des objets plus grands que ça
— sans quoi des octets échappaient au comptage. `--object-extent` le mesure sur
le corpus : **212 220 groupes, 211 250 172 octets d'objets rangés, 181 570 432
couverts par les tailles de classe, 29 679 740 jamais examinés** — et ils
tombent **exactement** sur les deux classes en-tête :

| Classe | Octets en trop | Groupes |
|---|---|---|
| `PParameterBuffer` | 24 744 208 | 41 712 |
| `PAnimationClipBinding` | 4 935 532 | 5 859 |

Toute autre classe du jeu range ses objets à la taille exacte de sa classe.

**Conséquence pour la phase B**, en deux règles au lieu d'une :

1. Pour une classe ordinaire, écrire un objet c'est *remplir de zéros, puis
   poser les membres* — rien à comprendre du reste. Les deux classes de caméra
   restent une exception à traiter si un jour on en écrit une.
2. Pour une classe en-tête, l'objet est plus grand que sa classe, et ce qui
   dépasse est la charge que la section en-tête décrit — un enregistrement par
   paramètre. Les deux morceaux se répondent, et ensemble ils rendent compte de
   la totalité de la zone d'objets.

---

## 4. Phase C — la couche matériaux

**Produire** : `PMaterial`, `PParameterBuffer`, `PShaderParameterDefinition`,
`PSamplerState`, `PEffectVariant`, et le `PAssetReferenceImport` qui nomme le
shader par son hash.

**Le point dur, et il est nommé comme tel par le SDK** : le contenu des buffers
de paramètres. Approche identique au reste — reproduire les buffers d'un
matériau **livré** à l'octet près avant d'en fabriquer un.

**Déjà documenté par ED8_12AssetTool** : les 0x10 octets après l'en-tête d'un
`PParameterBuffer`, à retirer pour CS1. On ne part pas de rien.

**À établir** : la table taille/type de chaque paramètre attendu par un shader
donné, lue depuis le cluster `ed8.fx#<hash>` — c'est le corollaire direct de la
décision « on lie, on ne compile pas ».

---

## 5. Phase D — la géométrie depuis le FBX

Comme on écrit les `PVertexStream` nous-mêmes, **c'est nous qui choisissons la
disposition des sommets** : plus de repacking contraint vers un format imposé.

Restent à calculer, pas à deviner :

- packing des types (positions, normales, tangentes, UV) ;
- skinning : indices de bones et poids quantifiés, via `PSkinBoneRemap` ;
- bornes : `PMeshInstanceBounds`, `PSkeletonJointBounds` ;
- découpe en segments par matériau.

**Filet** : reprendre un maillage du jeu, le re-packer avec notre code, exiger
ses octets d'origine.

**Acquis à réutiliser** : le réécrivain de charge GPU
(`PhyreModelGeometryWriter`) sait déjà poser les tampons et corriger les couples
(décalage, taille) ainsi que `m_indexBufferSize` @72 et `m_vertexBufferSize`
@76. Il n'a été prouvé qu'à tailles **inchangées** — le prouver à tailles
différentes reste un pas à faire, petit et sans inconnu.

---

## 6. Phase E — le squelette depuis le FBX

Hiérarchie de `PNode`, matrices monde et locales. Sans gabarit, le squelette du
FBX **devient** le squelette du personnage — ce qui supprime définitivement la
question du rig que l'utilisateur ne veut pas se poser.

---

## 7. Phase F — les animations, dans les deux sens

**F1 — écrire un clip depuis le FBX.** Le format d'animation Phyre n'a jamais
été attaqué côté écriture (seul `PhyreAnimationReader` existe, en lecture). Même
discipline : reproduire un clip livré à l'octet près, puis en fabriquer un.
Symbole attendu : `{asset}_CLIP_{nom}`.

**F2 — porter une animation du jeu sur un squelette maison.** Retarget par nom
de bone, **rotation locale uniquement** (la position monde ne se transpose pas
entre morphologies). Table de correspondance éditable quand les noms divergent,
et échec explicite plutôt que résultat approximatif.

F2 a plus de valeur d'usage immédiate que F1 : il donne un personnage qui bouge
sans que l'auteur anime.

---

## 8. Phase G — le câblage « nouveau personnage »

Sans inconnu technique, mais indispensable pour passer de « fichier valide » à
« personnage qui apparaît » :

- paquet `.pkg` + manifeste — **fait** ;
- entrée `t_name.tbl` (modèle, script ANI, set facial) ;
- `t_attach.tbl` pour l'arme ;
- le script ANI lui-même — l'éditeur sait déjà le créer et l'éditer.

---

## Ordre

A → B → C → D/E → F → G, chaque étape avec son contrôle byte-exact contre un
fichier livré.

**Jalon à ne pas repousser indéfiniment** : dès que la phase C donne un premier
fichier complet, le charger **dans le jeu**. Tout ce qui précède est vérifié
contre des octets, jamais contre le moteur.
