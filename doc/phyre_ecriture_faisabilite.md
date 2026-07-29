# Écrire un vrai fichier .phyre — étude de faisabilité

Question posée : peut-on écrire un `.dds.phyre` (texture) et un `.dae.phyre`
(modèle) **sans aucun gabarit ni blob**, en s'appuyant sur le code de
PhyreEngine (`coldsteel_studio/docs/ED8_12AssetTool-main`, le SDK PE 3.12 adapté
pour ED8) ?

Réponse courte : **oui pour la texture, et le chemin est court** ; **oui en
théorie pour le modèle, mais le travail n'est pas de même nature** — l'obstacle
n'est pas le conteneur, c'est le système de matériaux et de shaders.

Tout ce qui suit est mesuré, pas estimé au jugé. L'outil de mesure est
`--dump-phyre <fichier|paquet> [--members]`, ajouté pour cette étude.

---

## 1. Ce qu'est un cluster Phyre, section par section

Notre lecteur (`PhyreClusterMetadataReader`, `PhyreFixupReader`) décode déjà tout
le conteneur. En additionnant la taille de chaque section, on retombe **exactement**
sur le début des données GPU :

| Section | Contenu | Texture I_EFTEX000 | Modèle C_PLY000 |
|---|---|---|---|
| En-tête | 17 mots | 84 | 84 |
| Namespace empaqueté | noms de types, descripteurs de classes (36 o), membres (24 o), table de chaînes | 2 263 | 20 569 |
| En-têtes de groupes d'instances | 36 o par groupe | 72 | 1 908 |
| Données d'objets | objets puis tableaux, par groupe | 180 | 115 676 |
| Données des user fixups | chaînes | 17 | 232 |
| Descripteurs de user fixups | 12 o chacun | 24 | 504 |
| Section « header class » | 4 o par instance + 16 o par enfant | 0 | 24 864 |
| Fixups pointeur-tableau / pointeur / tableau | tables compressées | 13 | 8 896 |
| **Total** | | **2 653 / 2 653** | **172 733 / 172 733** |
| Charge GPU | pixels ou flux de sommets | 1 398 100 | 550 562 |

**Zéro octet inexpliqué dans les deux cas.** C'est le résultat central de
l'étude : il n'y a pas de zone opaque dans un cluster. Un écrivain est donc
l'**inverse exact d'un code que nous possédons déjà**, et il se valide de la
seule façon qui vaille : relire un fichier livré par le jeu, le ré-sérialiser
depuis le modèle en mémoire, et exiger l'égalité octet pour octet — la même
discipline que le round-trip des 1071 `.eff` et des 174 paquets de textures.

---

## 2. Texture (.dds.phyre)

### Ce qu'il y a réellement dedans

```
I_EFTEX000 : 2 653 octets de conteneur avant les pixels
  schéma  : 5 noms de types, 12 classes, 41 membres
  objets  : 2 groupes — 1 PAssetReference (40 o + 28 o de tableau), 1 PTexture2D (112 o)
  fixups  : 3 pointeur, 1 tableau, 0 pointeur-tableau, 2 user
  user fixups : PClassDescriptor = "PTexture2D", PTextureFormatBase = "ARGB8"
```

Une texture, c'est **deux objets**. Le reste (85 % du conteneur) est le schéma de
types — et ce schéma est du code source, pas un secret :

```cpp
// Core/Rendering/PhyreTexture2D.cpp
PHYRE_BIND_START(PTexture2DBase)
    PHYRE_ADD_MEMBER_FLAGS(DataMember, PE_CLASS_DATA_MEMBER_READ_ONLY)
        PHYRE_BIND_CLASS_DATA_MEMBER(m_width)
        PHYRE_BIND_CLASS_DATA_MEMBER(m_height)
PHYRE_BIND_END
```

Les 12 descripteurs de classes et leurs 41 membres se transcrivent depuis ces
macros de binding (`--dump-phyre … --members` donne la cible exacte : nom,
offset, taille, drapeaux de chaque membre). Aucun blob : une table de données en
C#, écrite depuis le SDK, et vérifiée contre les fichiers du jeu.

### Travail estimé

1. **Écrivain de conteneur** — en-tête, namespace, groupes, objets, fixups, dans
   l'ordre du tableau ci-dessus. ~600 à 900 lignes de C#, miroir de nos lecteurs.
2. **Garde-fou** — ré-sérialiser les 174 clusters de textures du jeu et exiger
   l'égalité octet pour octet. Si ça passe, l'écrivain est prouvé.
3. **Table de schéma** — les 12 classes depuis les bindings du SDK, pour n'avoir
   plus besoin d'ouvrir un paquet du jeu.

Étapes 1+2 suppriment déjà le gabarit *runtime* (on n'aurait plus besoin de
`I_EFTEX000` que pour le schéma) ; l'étape 3 supprime la dernière dépendance.

### Risques

- Les tables de fixups sont écrites sous forme **compressée** (voir
  `compressPointerFixups`, `PhyreClusterWriterBinary.cpp` l. 2073). Notre lecteur
  les décode, donc le format est connu, mais l'égalité octet pour octet exige de
  reproduire le même encodage — c'est justement ce que le garde-fou mesure.
- Deux mots de l'en-tête du namespace que notre lecteur saute (`reader.Skip`)
  devront être capturés. Anodin, mais à faire avant de viser le byte-exact.

---

## 3. Modèle (.dae.phyre)

### Ce qu'il y a dedans

```
C_PLY000 : 172 733 octets de conteneur avant la charge GPU
  schéma  : 15 noms de types, 127 classes, 368 membres
  objets  : 53 groupes, 2 877 objets
  fixups  : 2 110 pointeur, 954 tableau, 5 pointeur-tableau, 42 user
```

Le maillage lui-même est minuscule dans ce total : 1 `PMesh`, 16 `PMeshSegment`,
1 `PMeshInstance`, 83 `PNode`, 65 `PSkeletonJointBounds`, 111 `PSkinBoneRemap`.
**L'écrasante majorité des objets est de la plomberie de shaders** :

| Objets | Rôle |
|---|---|
| 874 `PSamplerState` | états d'échantillonnage par paramètre de shader |
| 676 `PShaderParameterDefinition` | définitions de paramètres |
| 256 `PDataBlockD3D11` + 256 `PVertexStream` | blocs de données GPU |
| 26 `PParameterBuffer` | buffers de paramètres (572 ou 588 o chacun) |
| 229 `PMatrix4`, 32 `PMaterial`, 17 `PWorldMatrix` | matériaux et transformations |

### Le paquet ne contient pas que le modèle

`C_PLY000.pkg` porte, à côté du `.dae.phyre` (723 Ko) et de ses 12 textures,
**14 clusters de shaders compilés** `ed8.fx#<hash MD5>.phyre`, de 1,08 à 1,23 Mo
chacun — soit ~15 Mo de shaders pour 723 Ko de modèle. Les matériaux du modèle
les désignent par ces hachages (les user fixups `PAssetReferenceImport` du
cluster servent à ça).

Conséquence directe sur la faisabilité : un modèle qui utiliserait une
combinaison de matériaux **inédite** demanderait un shader compilé correspondant.
Réutiliser les permutations déjà livrées est donc le chemin praticable, et c'est
une raison de plus de viser « remplacer la géométrie » avant « écrire un modèle
de zéro ».

### Pourquoi c'est un autre métier

Le conteneur, lui, ne pose pas de problème nouveau : mêmes sections, mêmes
règles, zéro octet inexpliqué. Ce qui coince est **ce qu'il faut mettre dedans**,
et le README de l'outil ED8_12AssetTool le dit sans détour :

- il a fallu **recompiler PhyreEngine en 32 bits** pour que les pointeurs fassent
  4 octets comme dans les assets de Falcom (donc toutes les tailles de classes et
  tous les offsets de membres du schéma sont ceux d'une cible x86) ;
- `PParameterBuffer` porte **0x10 octets mystérieux** après son en-tête, à
  coder en dur pour CS2 et à **retirer pour CS1** ;
- `PDataBlock` **n'existe pas dans le runtime de CS1** : un objet de ce type y
  fait planter le jeu, alors qu'il peut apparaître dans le namespace ;
- il faut **désactiver `repackOptimizeClass`**, sans quoi toutes les tailles de
  classes et de membres changent ;
- `m_memoryType` doit valoir VRAM, `PhyreContextSwitches` / `PhyreMaterialSwitches`
  doivent être retirés pour CS1, `PMaterial` veut `remapFrom/To = NULL` ;
- et, textuellement : *« The most difficult and important part of importing the
  model correctly is getting the shaderParameter values right. »*

Autrement dit : écrire le fichier n'est pas le problème, **produire des matériaux
que le shader du jeu accepte** l'est.

### Chemin en trois paliers

1. **Round-trip d'un modèle** — relire `C_PLY000`, le ré-écrire, exiger l'égalité
   octet pour octet. Ça ne demande **aucune** connaissance des shaders et ça
   prouve l'écrivain sur le cas général (2 877 objets, 127 classes, 3 069 fixups)
   là où la texture n'en teste que 2 objets. C'est le palier à viser en premier.
2. **Remplacer la géométrie d'un modèle existant** — nouveaux sommets, nouveaux
   indices, nouveaux bones, en **gardant les objets de matériau du modèle
   d'origine**. C'est ce que veulent 90 % des mods (remplacer un maillage), et ça
   devient possible dès le palier 1.
3. **Écrire un modèle de zéro** — c'est là qu'il faut porter le système de
   matériaux : `PParameterBuffer`, `PShaderParameterDefinition`, `PSamplerState`,
   et la correspondance avec les `.fx` du jeu. Gros morceau, à ne pas entamer
   avant que 1 et 2 tiennent.

---

## 4. Ce qui est déjà écrit (boîte noire)

`src/ED8Editor.Phyre/Authoring/` — autonome, ne dépend de rien de l'éditeur et
rien de l'éditeur n'en dépend encore. Sonde : `tools/PhyreAuthoringProbe`, hors
solution.

- **`PhyreClusterSections`** — découpe un cluster en ses onze sections et le
  recompose. Prouvé : **174 textures + 257 clusters de 15 paquets de personnages
  (modèles inclus) se recomposent à l'octet près**.
- **`PhyreNamespaceWriter`** — réémet le namespace empaqueté (85 % du conteneur
  d'une texture) **depuis le schéma parsé**, plus depuis les octets d'origine.
  Prouvé sur les mêmes 431 clusters, byte-exact.
- **`PhyreTextureSource` / `PhyreModelSource`** — la frontière de la boîte noire :
  des DTO simples (pixels RGBA ; sommets, segments par matériau, squelette) que
  l'import FBX du Character Studio remplira. Rien ici ne connaît FBX, rien ici ne
  connaît l'éditeur. `PhyreModelSource.Problems()` dit ce qui empêcherait
  d'écrire, avant de produire quoi que ce soit.

### Règles trouvées en écrivant (elles n'étaient pas dans l'étude)

- L'en-tête du namespace fait **huit mots** : quatre compteurs et quatre mots
  encore sans nom (repris tels quels).
- La table de noms est remplie en **trois passes** : les noms de types **triés**,
  puis les noms de classes **triés**, puis les noms de membres dans l'ordre où
  les classes les déclarent. Les entrées qui pointent dessus, elles, gardent
  l'ordre du fichier — c'est ce décalage qui faisait échouer les premières
  tentatives.
- L'alignement d'une classe est stocké en **exposant**, dans le quartet haut du
  mot qui porte aussi sa taille.

- **`PhyreTextureSchema`** — les 12 classes et 41 membres d'une texture **écrits
  en C#**, plus lus d'un fichier. Prouvé : le namespace qu'ils produisent, sans
  qu'aucun fichier du jeu ne soit ouvert, est celui des **174 textures livrées,
  octet pour octet**. C'est le point où le gabarit disparaît pour 85 % du
  conteneur.
  On y lit au passage pourquoi l'outil Rust patchait l'offset 80 :
  `PClusterHeaderD3D11.m_maxTextureBufferSize` est à 80, et l'en-tête du fichier
  *est* un objet de cette classe.

- **`PhyreFixupWriter`** — réencode les tables de fixups depuis les fixups
  décodés. Prouvé : **174 textures sur 174, octet pour octet**.
  L'encodage est entièrement lisible : un bloc commence par un octet qui porte le
  mode d'empaquetage sur trois bits et, au-dessus, un masque de ce que le bloc
  omet parce que c'est absent (pas d'index de tableau, pas de user fixup, pas
  d'offset de destination) ; les nombres suivent en longueur variable, sept bits
  par octet ; la source d'un fixup est un index de membre, ou un offset quand son
  bit de poids fort est mis — bit qui voyage dans le bit de poids FAIBLE du
  nombre encodé, pour que les deux restent courts.

### L'en-tête, champ par champ

Le schéma en code donne l'en-tête entier, puisque l'en-tête EST un objet
`PClusterHeaderD3D11` :

```
+0  m_phyreMarker "RYHP"      +28 m_pointerFixupSize        +56 m_totalDataSize
+4  m_size (84)               +32 m_pointerFixupCount       +60 m_headerClassInstanceCount
+8  m_packedNamespaceSize     +36 m_pointerArrayFixupSize   +64 m_headerClassChildCount
+12 m_platformID "11XD"       +40 m_pointerArrayFixupCount  +68 m_physicsEngineID
+16 m_instanceListCount       +44 m_pointersInArraysCount   +72 m_indexBufferSize
+20 m_arrayFixupSize          +48 m_userFixupCount          +76 m_vertexBufferSize
+24 m_arrayFixupCount         +52 m_userFixupDataSize       +80 m_maxTextureBufferSize
```

Et l'en-tête de groupe d'instances, de même (`PInstanceListHeader`, 36 octets) :
`m_classID, m_count, m_size, m_objectsSize, m_arraysSize, m_pointersInArraysCount,
m_arrayFixupCount, m_pointerFixupCount, m_pointerArrayFixupCount`.

- **`PhyreTextureClusterWriter`** — écrit le cluster ENTIER depuis une image.
  Prouvé : **174 textures sur 174 reproduites à l'octet près**, à partir de rien
  d'autre que le chemin, la taille, le format, le nombre de mips et les pixels.
  Le gabarit a disparu.

### Et hors du corpus

Le round-trip prouve que l'écrivain est d'accord avec Falcom sur ce que Falcom a
écrit. Restait à savoir s'il tient là où Falcom n'a rien écrit : sept textures de
tailles et de formats qu'aucun fichier du jeu n'utilise (1x1, 3x5, 100x60,
2048x8, du DXT1 en 40x24, du DXT5 en 256x256) sont écrites puis relues par notre
propre lecteur — taille, format, nombre de mips et pixels identiques à ce qui a
été demandé, à chaque fois (`--synthetic`).

Ce qui reste hors de portée d'une vérification hors ligne : **le jeu ne les a pas
encore chargées**. Le byte-exact sur 247 textures dit que chaque champ écrit est
celui que Falcom écrit pour cette image-là ; seule une exécution dira le reste.

### Le cluster entier, reconstruit depuis ce qu'il contient

`PhyreClusterWriter` réécrit un cluster à partir de ce qu'il HOLDS et non de ses
octets : l'en-tête depuis les compteurs, le schéma depuis les classes, les
en-têtes de groupes depuis les groupes, les user fixups depuis leurs noms, les
trois tables depuis les fixups. Seules deux zones sont encore recopiées telles
quelles — les objets eux-mêmes et la section « header class » — parce que les
écrire suppose de connaître la disposition des 127 classes d'un modèle.

Prouvé sur **617 clusters de quarante paquets de personnages, modèles compris :
617 sur 617 reconstruits à l'octet près**, plus les 174 textures.

### Et la charge GPU d'un modèle ?

Même méthode, un cran plus loin (`--geometry`) : sur `ply000.dae.phyre`, les
tampons que les objets désignent couvrent **550 556 octets d'une charge de
550 562** — 533 372 de sommets, 17 184 d'indices, 16 primitives. **Six octets**
restent, en fin de zone : de l'alignement.

`PhyreModelGeometry` localise maintenant chaque tampon **par le champ qui le
décrit**, et pas seulement sa valeur : pour chaque segment, l'adresse du décalage
d'indices (0x40) et de leur taille (0x48) ; pour chaque bloc de données,
l'adresse du décalage de sommets (0x28) et de leur taille (0x30). C'est
l'adresse du nombre qui compte, puisque remplacer la géométrie veut dire le
corriger.

Deux chemins indépendants tombent d'accord sur `ply000` : **272 tampons
localisés, 6 octets non réclamés**, exactement l'écart que donnait le compte via
le lecteur de modèles. Et le motif tient sur les autres tenues :

```
ply000_c00 : 322 tampons,  8 octets non réclamés
ply000_c01 : 153 tampons,  4
ply000_c02 : 187 tampons,  6
ply000_c05 : 272 tampons,  6
ply000_c06 : 299 tampons,  8
```

Jamais plus de huit octets : de l'alignement de fin, rien d'autre.

`PhyreModelGeometryWriter` réécrit la charge et corrige les couples
(décalage, taille) à leurs adresses, plus les deux tailles de région de
l'en-tête. L'épreuve est l'identité : rendre à un modèle ses propres tampons doit
redonner ce modèle à l'octet près.

**Épreuve réussie** : les modèles de personnage se réécrivent à l'octet près en
repassant par la disposition — sept sur sept des tenues de `ply000`.

Deux règles, trouvées en suivant le champ qui divergeait plutôt qu'en supposant :

1. **Les tampons ne sont pas jointifs : chacun commence sur une frontière de
   quatre octets.** Sur `ply000`, trois segments d'indices finissent sur une
   adresse impaire de deux et le suivant saute au multiple de quatre — trois
   trous de deux octets, soit exactement les « six octets non réclamés » que la
   mesure précédente signalait sans les expliquer. Ce n'était pas du bourrage de
   fin, c'était de l'alignement au fil de l'eau.
2. **L'alignement se compte depuis le début de sa région**, pas depuis le début
   de la charge : la région de sommets commence là où celle des indices s'arrête,
   fût-ce sur une adresse non alignée. Aligner en absolu ajoutait deux octets à
   la région de sommets — et le contrôle l'a dit tout de suite, au champ
   `m_vertexBufferSize`.

Le champ qui divergeait était le décalage d'indices du segment 3 (`PMeshSegment`
+0x40), à deux octets près : le localiser a suffi à voir la trame.

C'est la condition qui manquait pour le remplacement de géométrie : la charge
d'un modèle ne contient rien d'autre que ses tampons. La remplacer revient donc à
réécrire ces tampons, puis à corriger les compteurs et les décalages qui les
décrivent — le nombre d'indices et leur décalage dans `PMeshSegment` (0x24, 0x40,
0x48), les blocs de données, et les deux tailles de tampon de l'en-tête. Aucun
matériau, aucun shader n'entre là-dedans.

Autrement dit, tout ce qui entoure les objets est désormais généré. Ce qui reste
pour écrire un MODÈLE neuf est précisément ce qui manquait au départ : les
données d'objets, c'est-à-dire la disposition de PMesh, PMeshSegment,
PVertexStream et de leurs voisines — un travail de schéma, comme celui déjà fait
pour les douze classes d'une texture, mais dix fois plus grand.

### Deux champs que seule la vérification pouvait donner

En écrivant les objets, deux différences ont résisté, et chacune apprend quelque
chose sur le format :

- `m_mipmapCount` et `m_maxMipLevel` ne disent PAS la même chose. Le premier
  compte les mips réellement stockés sous le plus grand ; le second est la
  profondeur que la TAILLE autorise. Sur une texture à chaîne complète les deux
  coïncident — c'est pourquoi la confusion passait sur 140 fichiers sur 174 — mais
  une texture 1024x1024 qui ne stocke qu'un niveau écrit 0 et 10.
- `m_textureFlags` vaut 2 exactement quand la chaîne stockée est plus courte que
  la taille ne l'autorise, et 0 quand elle est complète. Vérifié sur les 174.

### Reste à écrire pour boucler la texture

Rien : la texture est bouclée. Ce qui suit reste comme carte du format.

Contenu observé (`--fixups`, `--objects`) :

```
données d'objets : 2 objets — PAssetReference (m_id @24, m_asset @28, m_assetType @32)
                              PTexture2D (m_format @0, m_mipmapCount @12, m_width @28, m_height @32)
fixups pointeur  : 10 octets — 58 02 00 01 48 04 01 48 48 02
   m_asset      -> l'objet PTexture2D
   m_assetType  -> user fixup 0 = "PTexture2D"
   m_format     -> user fixup 1 = "ARGB8"
fixups tableau   : 3 octets — 08 31 00   (le tampon de la chaîne de m_id)
header class     : vide pour une texture
```

Le premier octet d'un bloc de fixups porte le mode d'empaquetage sur trois bits :
ici `0x58` = mode 0 (« tous les objets »), le plus simple des sept, et le seul
dont une texture ait besoin puisqu'elle n'a qu'un objet par groupe. L'encodeur à
écrire est donc l'inverse d'une seule des sept branches du décodeur.

---

## 5. Branchement, et où en sont les modèles

### Les textures sont branchées

`EffTextureImport` n'ouvre plus aucun fichier du jeu : le cluster vient de
`PhyreTextureClusterWriter`, le manifeste est écrit à partir du nom demandé, et
le paquet de `PkgArchiveWriter`. Vérifié de bout en bout dans
`--verify-texture-import` : une image 48x24 devient `I_EFTEX900.pkg`, relue
48x24 ARGB8, 6 mips, symbole déclaré.

Le branchement a corrigé un vrai défaut au passage : l'import copiait le
manifeste du paquet gabarit, donc une texture importée sous le nom `I_EFTEX900`
se **déclarait comme `I_EFTEX000`** — le manifeste porte le symbole que le
chargeur résout.

### Ce que le jeu fait vraiment de ses fixups

Recensement sur tous les blocs de 20 paquets de personnages :

| Empaquetage | Blocs | Fixups |
|---|---|---|
| 1 — cibles groupées | 1 582 | 90 964 |
| 0 — tous les objets | 10 549 | 87 421 |
| 3 — liste d'exclus | 535 | 35 201 |
| 4 — masque de bits | 562 | 17 689 |
| 5 — brut | 505 | 9 319 |
| 2 — liste d'inclus | 0 | 0 |
| 6 — pas fixe | 0 | 0 |

Cinq modes sur sept sont utilisés ; deux ne le sont jamais — deux encodeurs à ne
pas écrire.

Les cinq sont maintenant écrits (0, 1, 3, 4, 5), avec la règle « le plus court
gagne » : le bloc est essayé dans chaque forme et la plus petite est retenue. Le
mode 1 groupe les fixups par CIBLE — une passe par destination, qui donne sa
charge utile une fois puis dit quels objets la prennent, dans la forme la plus
courte pour cette passe.

Sur `ply000.dae.phyre`, la table des fixups pointeur est passée de 2 181 octets
(un bloc par fixup, ce qui était d'ailleurs FAUX pour un groupe de plusieurs
objets) à **7 717 contre 7 113 chez Falcom** — les 0x68 premiers octets
coïncident.

### La source, plutôt que des hypothèses

Le SDK contient le writer : `Core/Serialization/Internal/PhyreFixupCompression.cpp`.
Quatre règles en sont sorties directement, là où je devinais :

1. **Les fixups sont TRIÉS avant d'être découpés en blocs** (`std::sort` avec
   `SortFixup`) : par source — les membres de classe avant les offsets bruts —
   puis par CIBLE (objet, liste, offset, index de tableau, user fixup), puis par
   objet source. Un bloc est ensuite un simple run de voisins qui partagent leur
   source. C'est le tri par cible qui met côte à côte les fixups d'une même
   destination, et c'est ce qui rend le groupement par cible et la liste
   partagée rentables. Sans lui, mes frontières de blocs ne pouvaient pas
   coïncider.
2. La forme « tous les objets » demande seulement qu'**aucun objet ne se répète**
   — pas que les identifiants soient croissants. Les payloads sont alors
   réordonnés par objet (`SortFixupBySource`).
3. La liste de destination est hissée quand tous les fixups du bloc nomment la
   même — un fixup qui nomme un user fixup compte comme portant la liste zéro —
   et seulement si le bloc en contient plus d'un.
4. Dans un bloc groupé, une passe ne prend **jamais** la forme « tous les
   objets » (le switch du moteur n'a pas ce cas) mais peut prendre le **pas
   fixe**, qui n'apparaît jamais comme type de bloc — ce que mon recensement, qui
   ne comptait que les blocs, ne pouvait pas voir.

Résultat sur `ply000.dae.phyre` : **7 129 octets contre 7 113**, les 950 premiers
identiques. Il reste seize octets.

### Ce qui bloque encore le round-trip d'un modèle

`PPartialIndexList::selectPackType` a été portée telle quelle : les tailles sont
comparées dans l'ordre masque de bits, liste d'inclus, liste d'exclus, puis pas
fixe, et chacune doit être STRICTEMENT plus petite pour gagner — donc une
égalité revient à la forme pesée en premier. Le pas fixe n'est pesé que si toute
la passe est une seule série (une passe d'un seul objet n'a pas de pas).

Il reste **seize octets**, et la divergence a un visage net. À l'offset 0x3B6 :

```
livré  : ... 75 69 04 10 | 10 00 | 11 01 | 12 02 | 13 03 | 14 04 ...
écrit  : ... 75 69 04 10 | 00 10 00 | 00 11 01 | 00 12 02 | 00 13 03 ...
```

J'écris trois octets par charge utile là où le jeu en écrit deux, et l'octet en
trop est toujours le même : le marqueur « pas de user fixup », un `00` en tête.
Autrement dit, **leur bloc porte le drapeau « aucun user fixup » et le mien
non** — donc mon bloc contient au moins un fixup qui en nomme un, et pas le
leur. C'est encore une frontière de bloc qui diffère, pas une taille mal
calculée.

**Les seize octets sont tombés.** `ply000.dae.phyre` — 2 877 objets, 127 classes,
3 069 fixups — se réécrit maintenant **à l'octet près**, et avec lui les treize
clusters de son paquet.

La cause : le bloc qui divergeait appartenait au groupe `PMesh`, qui ne contient
**qu'un seul objet**. Quand c'est le cas, l'identifiant d'objet ne s'écrit pas —
il n'y a rien à dire. Et cette exclusion ne fait PAS partie du masque du bloc :
le lecteur l'ajoute lui-même à partir du nombre d'objets
(`if(objectCount == 1) maskForFixups |= EXCLUDE_SOURCE_OBJECT_ID`), et l'écrivain
doit faire pareil. J'écrivais donc un octet de trop par fixup, seize fois.

Trouvé en faisant tracer ses blocs à l'écrivain et en comparant les deux listes :
**1 046 blocs contre 1 046, mêmes formes, mêmes masques, mêmes sources** — donc
le découpage était juste et seule une charge utile était trop longue.

**Les tables de fixups sont bouclées** : sur **617 clusters de quarante paquets
de personnages, 617 tables se réécrivent à l'octet près** — modèles compris.

Deux dernières règles sont venues de la source, toujours pas d'hypothèses :

- Un bloc est **re-trié par identifiant d'objet** avant d'être empaqueté, quelle
  que soit sa forme, la brute comprise (`std::sort(..., SortFixupBySource)`).
  J'avais gardé l'ordre du tri par cible pour la forme brute, ce qui permutait
  ses charges utiles.
- Le choix de la forme part de la **brute** et ne la lâche que pour strictement
  plus court — donc une égalité laisse la brute en place. Prendre « la plus
  courte » avec un autre départage donnait le masque de bits sur des égalités
  fréquentes.

Et un bug bien à moi, débusqué par la même mesure : ma forme « liste d'inclus »
n'avait pas de cas dans le switch d'écriture, donc elle produisait un encodage
tronqué — artificiellement court, et forcément choisi.

Le décodeur trace chaque bloc (`--blocks`), ce qui donne le bloc exact qui
diverge dans un fichier livré :

```
block at 0x393: packing 1, mask 0x68, source 0x31,        32 fixups
block at 0x399: packing 0, mask 0x50, source 0x80000004,   1 fixup
block at 0x39E: packing 0, mask 0x50, source 0x8000000C,   1 fixup
block at 0x3A3: packing 0, mask 0x50, source 0x80000014,   1 fixup
block at 0x3A8: packing 0, mask 0x50, source 0x8000001C,   1 fixup
block at 0x3AD: packing 0, mask 0x50, source 0x80000024,   1 fixup
block at 0x3B2: packing 5, mask 0x70, source 0x80000034,  16 fixups   <- celui-ci
```

Le bloc à 0x3B2 porte le masque 0x70, qui contient « aucun user fixup » (0x10) ;
le mien ne l'a pas, donc mon run pour la même source contient un fixup qui en
nomme un. Deux détails du relevé méritent d'être creusés : les sources 0x04,
0x0C, 0x14, 0x1C, 0x24 se suivent de huit en huit puis SAUTENT 0x2C pour aller à
0x34 — le fixup de 0x2C est donc ailleurs, et c'est vraisemblablement le même
que celui qui pollue mon bloc.

La prochaine étape est mécanique : faire tracer ses propres blocs à l'écrivain et
comparer les deux listes ligne à ligne. Le relevé du fichier est là ; il manque
celui du writer.

Une règle trouvée en chemin : le masque ne met le drapeau « liste de destination
partagée » que si le bloc contient PLUS D'UN fixup. Le hisser pour un seul ne
gagne rien, et le writer du jeu le laisse alors dans la charge utile — un octet
d'écart qui faisait échouer les 174 textures.

## 6. Verdict

| Cible | Faisable sans gabarit ? | Effort | Bloquant |
|---|---|---|---|
| Texture `.dds.phyre` | **Oui** | Écrivain de conteneur + table de 12 classes | Aucun connu |
| Modèle `.dae.phyre`, round-trip | **Oui** | Le même écrivain, éprouvé sur 2 877 objets | Aucun connu |
| Modèle, géométrie remplacée | **Oui** | + édition des flux de sommets | Aucun connu |
| Modèle, écrit de zéro | Oui *en théorie* | + tout le système de matériaux/shaders | Valeurs des paramètres de shader |

Ordre de travail proposé : **écrivain de conteneur → round-trip octet pour octet
sur les 174 textures → round-trip sur les modèles → table de schéma depuis les
bindings du SDK → géométrie → matériaux.**

Le premier palier a une propriété qui vaut d'être soulignée : il se prouve tout
seul. Tant que la ré-écriture d'un fichier du jeu n'est pas identique à l'octet
près, on sait qu'on n'a pas fini ; le jour où elle l'est, sur 174 textures puis
sur des modèles complets, on sait qu'on écrit du Phyre et pas une imitation.
