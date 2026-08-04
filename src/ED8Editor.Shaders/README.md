# ED8Editor.Shaders — Pipeline de shaders Cold Steel 1 (Phyre Engine)

## Vue d'ensemble

Le but est de permettre l'importation de shaders HLSL **custom** dans Cold Steel 1,
en contournant les limitations du pipeline Phyre Engine où :

1. Le `.dae.phyre` (modèle) référence un `.fx.phyre` (shader) via un hash MD5
2. Les paramètres du shader dans le `.dae.phyre` (`PParameterBuffer`, `PSamplerState`,
   `PShaderParameterDefinition`) doivent correspondre **strictement** (taille, nombre,
   ordre) à ceux déclarés dans le `.fx.phyre`
3. Le `.fx.phyre` contient le bytecode D3D11 compilé pour **chaque permutation**
   de context switches, plus les définitions de paramètres
4. Le moteur sélectionne la permutation au runtime selon les switches de contexte
   (NUM_LIGHTS, INSTANCING_ENABLED, SHADER_LOD_LEVEL, etc.)

## Anatomie d'un fx.phyre

Un cluster `.fx.phyre` contient les groupes suivants (lus par `PhyreEffectRenderPassReader`) :

| Groupe                    | Rôle |
|---------------------------|------|
| `PAssetReference`          | Identifiant unique du shader (ex: `shaders/ed8.fx#<HASH>`) |
| `PEffect`                  | Définit les **context switches** (noms) et les **context variants** |
| `PNodeContext`             | Chaque variant stocke les valeurs packées (uint) pour chaque switch |
| `PEffectVariant`           | Contient les **material switches** (paires clé/valeur) et la liste des scene render passes |
| `PSceneRenderPass`         | Un par type de passe (Opaque, Transparent, Shadow, etc.) |
| `PShader`                  | Un par context variant, référence ses `PShaderPass` |
| `PShaderPass`              | État de rendu (blend, rasterizer) + pointeurs vers les programmes |
| `PShaderProgramD3D11`      | Bytecode D3D compilé (vertex ou fragment), taille du constant buffer |
| `PShaderParameterDefinition` | Définition de chaque paramètre (nom, type, offset, taille) |
| `PShaderStreamDefinition`  | Définition des flux d'entrée (sémantiques vertex) |
| `PStreamInputDescD3D11`    | Description détaillée des éléments du layout d'entrée |
| `PMaterialSwitch`          | Paires nom/valeur pour les switches matériau |

### Chaîne de dépendances

```
PEffect
  ├── contextSwitches: ["NUM_LIGHTS", "INSTANCING_ENABLED", ...]
  └── contextVariantSwitches: [PNodeContext, PNodeContext, ...]
        └── Chaque PNodeContext → packedSwitchValues: [uint, uint, ...]

PEffectVariant
  ├── materialSwitches: {"TECHNIQUE": "DEFAULT", ...}
  ├── sceneRenderPasses: [PSceneRenderPass, ...]
  └── sceneRenderPassLookup: [PSceneRenderPass, ...] (redondant ?)

PSceneRenderPass
  ├── passType: "Opaque" | "Transparent" | ...
  └── shaders: [PShader, PShader, ...]  // un par context variant

PShader
  └── passes: [PShaderPass, ...]

PShaderPass
  ├── vertexProgram → PShaderVertexProgram → PShaderProgramD3D11
  └── fragmentProgram → PShaderFragmentProgram → PShaderProgramD3D11
```

## Ce qui est déjà implémenté dans ED8Editor

### Lecture (OK)
- `PhyreEffectRenderPassReader` lit complètement un fx.phyre → `PhyreEffectMetadata` + `CpuEffectProgram`
- `D3D11ShaderProgramInspector` reflète le bytecode D3D pour extraire signatures, CBs, resources
- `D3D11ShaderPermutationSelector` sélectionne la permutation selon la politique de contexte

### Écriture (partiel)
- `PhyreMinimalEffectWriter` écrit un fx.phyre **minimal** de zéro (position → WVP, couleur fixe)
  - Compile HLSL → bytecode via Vortice.D3DCompiler
  - Construit manuellement la structure de cluster
  - Preuve de concept qu'on PEUT écrire un fx.phyre custom

### Liaison modèle-shader
- `PhyreMaterialTable` extrait le bloc de paramètres d'un shader existant pour le réinjecter
  dans un modèle (taille, définitions, sampler states, etc.)

## Verrous identifiés pour les shaders custom

### 1. gameMaterialIDs (CS1 spécifique)

`PMeshInstance` a un champ `m_gameMaterialIDs` — un tableau d'entiers, un par segment.
Dans CS1, ce champ doit être présent et avoir la valeur 0 (d'après `phyre_authoring_handoff.md`).
Potentiellement, certaines valeurs sont utilisées pour des fonctions de rendu spéciales
(overlays faciaux: 11/12 = yeux, 13 = bouche, 14 = teint, 15 = cheveux, 16 = peau).

**À investiguer** : est-ce que le shader lui-même utilise `gameMaterialID` pour
brancher sur des comportements différents ? Ou est-ce purement au niveau du modèle ?

### 2. Fonctions de rendu indispensables

Le code HLSL dans les shaders Cold Steel contient probablement des fonctions appelées
par le moteur qui DOIVENT exister. Suspects :
- `_phyreReserved[29]` dans le constant buffer global (b0) — 29 float4 réservés par Phyre
- `WorldViewProjection` à l'offset 29×16 = 464 bytes dans le CB global
- Fonctions de brouillard (fog)
- Calcul d'éclairage (lights)
- Gestion des textures d'ombre (shadow mapping)
- Fonctions spécifiques aux matériaux du jeu

### 3. Context switches obligatoires

Le moteur CS1 s'attend à certains context switches. Si un shader ne les déclare pas,
le moteur peut ne pas trouver de permutation valide. Les switches connus :
- `NUM_LIGHTS` — nombre de lumières dynamiques
- `INSTANCING_ENABLED` — instanciation hardware
- `SHADER_LOD_LEVEL` — niveau de détail

**À investiguer** : liste exhaustive des switches utilisés par les shaders CS1.

### 4. Paramètres moteur dans le constant buffer

Le constant buffer `b0` (`$Globals` / `PhyreGlobals`) contient des paramètres
que le moteur écrit directement. Un shader custom doit :
- Soit déclarer le même layout de CB (avec les _phyreReserved)
- Soit comprendre quels paramètres sont lus et où

La taille du CB global est critique : `PShaderProgramD3D11.m_constantBufferSize`
et `m_globalConstantBufferIndex` doivent correspondre.

### 5. Ordre des paramètres dans le PParameterBuffer

Le `PParameterBuffer` dans le `.dae.phyre` doit avoir exactement les mêmes
paramètres (nom, type, offset, taille) que les `PShaderParameterDefinition`
dans le `.fx.phyre`. Tout écart → crash.

## Plan d'action

### Phase 1 : Investigation (ce dossier)
- [ ] 1a. Extraire TOUS les shaders du jeu (fx.phyre) et les décompiler
- [ ] 1b. Analyser les context switches utilisés → liste exhaustive
- [ ] 1c. Analyser les signatures de constant buffer attendues par le moteur
- [ ] 1d. Identifier les fonctions de rendu communes à tous les shaders
- [ ] 1e. Vérifier le rôle exact de `gameMaterialID` dans le pipeline shader

### Phase 2 : Outillage
- [ ] 2a. Décompilateur fx.phyre → HLSL (extraction + décompilation du bytecode)
- [ ] 2b. Mergeur de shaders : fusionner toutes les permutations en un seul fichier
      HLSL avec `#if`/`#define` pour les switches
- [ ] 2c. Validateur de paramètres : vérifier qu'un PParameterBuffer correspond à un fx.phyre

### Phase 3 : Compilation custom
- [ ] 3a. Compilateur HLSL → fx.phyre (génération du cluster complet)
- [ ] 3b. Éditeur de switches dans l'UI
- [ ] 3c. Preview des shaders dans le viewport

### Phase 4 : Intégration
- [ ] 4a. Interface dans l'éditeur pour sélectionner/uploader un shader custom
- [ ] 4b. Génération automatique du PParameterBuffer correspondant
- [ ] 4c. Test in-game

## Fichiers

```
src/ED8Editor.Shaders/
├── ED8Editor.Shaders.csproj
├── README.md                        ← ce fichier
├── Investigation/
│   ├── PhyreShaderExtractor.cs      ← extraction de tous les fx.phyre d'un package
│   ├── PhyreShaderDecompiler.cs     ← décompilation bytecode D3D → HLSL
│   ├── PhyreShaderAnalyzer.cs       ← analyse des patterns communs
│   └── PhyreShaderMerger.cs         ← fusion des permutations en un seul HLSL
├── Compilation/
│   ├── PhyreCustomShaderCompiler.cs ← compilation HLSL → fx.phyre
│   └── PhyreParameterValidator.cs  ← validation des paramètres
└── Tests/
    └── ShaderTests.cs               ← tests unitaires
```

## Quel programme correspond à quoi — mesuré sur `ed8.fx#D2620AD4…`

La correspondance n'est pas à deviner : elle est écrite dans le cluster.

`PEffectVariant` pointe 4 `PSceneRenderPass`, chacune nommant son type et six
`PShader` — les six contextes :

```
[0] "Opaque"            PShaderPassInfo[0]   PShader[0..5]
[1] "ForceTransparent"  PShaderPassInfo[1]   PShader[6..11]
[2] "EdgeTransparent"   PShaderPassInfo[2]   PShader[12..17]
[3] "Shadow"            PShaderPassInfo[3]   PShader[18..23]
```

`PShaderPassInfo` porte les points d'entrée par nom et les profils (`vs50`, `ps50`).
Les sept noms du groupe : `DefaultVPShader`, `DefaultFPShader`,
`ForceTransparentFPShader`, `EdgeVPShader`, `EdgeFPShader`, `ShadowVPShader`,
`ShadowFPShader` — sept et non huit, `DefaultVPShader` servant aux deux premières
passes.

`PEffect.m_contextSwitches` nomme trois commutateurs — `INSTANCING_ENABLED`,
`NUM_LIGHTS`, `SHADER_LOD_LEVEL` — et les six `PNodeContext` en donnent les valeurs :

```
contexte 0 (   0, 0, 0)    contexte 3 (  17, 1, 0)
contexte 1 (   0, 1, 0)    contexte 4 (1553, 0, 0)
contexte 2 (  17, 0, 0)    contexte 5 (1553, 1, 0)
```

Trois configurations de lumière fois deux états d'instanciation. Deux mesures
indépendantes le confirment :

- l'instanciation ne concerne que le sommet, d'où **24** programmes sommet
  (3 × 2 × 4 passes) contre **12** pixel (3 × 4)
- les tailles de bytecode vont par paires — 5616, 5616 puis 5928, 5928 puis
  6208, 6208 — c'est-à-dire qu'elles ne changent qu'avec la lumière

La disposition dans le groupe est `programme[contexte * 4 + passe]`, et la région de
tableaux est les blobs bout à bout dans cet ordre, sans bourrage : les 24 sommes font
exactement les 135 512 octets déclarés, les 12 pixel 47 636.

## Le modèle de défines est complet

Pour chaque emplacement `programme[contexte * 4 + passe]` :

- le **point d'entrée** et le **profil** viennent de `PShaderPassInfo`
- les **commutateurs de matériau** viennent de `PMaterialSwitch`, identiques aux deux
  étages — `PShaderGatherCacheEntryCgFX::populate` les passe une seule fois
- `PHYRE_D3DFX`, sans quoi `FRAG_OUTPUT_COLOR0` s'étend en `COLOR0`
- les **commutateurs de contexte** décodés du `PNodeContext` : quatre bits de compte,
  puis cinq de type de lumière et cinq de type d'ombre par lumière, donnant
  `NUM_LIGHTS`, `LIGHTTYPE_<i>` et `SHADOWTYPE_<i>`

Les tables de remappage par étage (`VpIgnoreContextSwitches`,
`FpIgnoreContextSwitches`) ne portent que des commutateurs de **contexte** : ici le
pixel ignore `INSTANCING_ENABLED`, d'où douze programmes pixel pour vingt-quatre
sommet.

Avec ce modèle, **35 emplacements sur 36** reviennent à la taille du jeu au nom du
compilateur près, et sur ceux qu'on a ouverts `ISGN`, `OSGN` et `SHEX` reviennent
octet pour octet.

Le trente-sixième — le pixel par défaut du contexte à une lumière sans ombre — a la
même interface et la même disposition de paramètres, mais 288 octets de `SHEX` en
plus :

```
ISGN  180 = 180      OSGN  44 = 44      STAT 148 = 148
RDEF 4476 vs 4488    (-12, le nom du compilateur)
SHEX 2164 vs 1876    (+288, notre code)
```

Ce n'est donc pas un défine qui manque : c'est le compilateur de 2010 qui optimise
mieux ce shader-là. Le programme est correct, simplement plus long.

## L'interface de paramètres, calculée et non recopiée

Tant que les tables de paramètres étaient recopiées d'un modèle, seul un HLSL gardant
l'interface de `ed8.fx` pouvait être écrit. Elles se déduisent en fait du bytecode
compilé, ce que `ShaderForge reflect`, `ShaderForge interface` et `ShaderForge plan`
établissent.

`PShaderParameterDefinition` (16 octets) porte quatre choses :

- `m_dataType` et la taille, lues de la réflexion : classe 0 scalaire → `0` pour un
  flottant et `8` pour un entier ; classe 1 vecteur → `1`, `2` ou `3` selon deux,
  trois ou quatre colonnes ; classe 3 matrice → `49` ; une ressource → `52`. Les
  tailles valent 4, 4, 8, 12, 16, 64 et 16.
- `m_parameterType`, la sémantique, qui est **une propriété du nom** : sur 117 noms
  récoltés dans dix-sept effets livrés (`ShaderForge semantics`), aucun désaccord.
  Douze noms seulement sont des sémantiques que le moteur alimente lui-même — les
  matrices monde, la lumière courante, ses cascades d'ombre, le matériau de jeu.
  Tout autre nom retombe sur `64` s'il s'agit d'une constante, `66` d'une liaison
  échantillonneur, `71` d'une texture seule. **C'est ce qui rend un HLSL quelconque
  écrivable** : un uniforme inventé n'a pas besoin d'être connu du moteur, le
  matériau le fournit par son nom.
- `m_constantBufferLocation`, qui est l'offset en octets dans le `$Globals` du
  programme, tel quel.
- `m_bufferLoc`, soit `(taille << 16) | offset` dans le capture buffer. Deux
  réservoirs — les matrices d'un côté, le reste de l'autre — démarrant chacun à
  seize et tassés par taille décroissante. Cette disposition-là n'appartient qu'à
  l'effet ; rien d'extérieur ne la lit.

Les 193 définitions d'une variante ne forment pas une table unique : les cinquante-sept
premières sont celles de l'effet, les suivantes vingt-quatre séries contiguës, une par
`PShader`, qui portent les paramètres liés au moment du tracé — la lumière, l'ombre,
le matériau de jeu. C'est nécessaire, le pixel n'ayant pas la même disposition que le
sommet : `AlphaThreshold` est à 632 d'un côté et 600 de l'autre.

`ShaderForge plan <effet.phyre>` reconstruit la table d'un effet livré depuis ses
propres programmes et la compare à ce qu'il déclare. Les dix-sept effets essayés
passent, 548 paramètres, sans un écart de sémantique, de type ni de taille. Les
offsets de capture, eux, ne sont pas comparés : un effet livré déclare tout ce que la
source `ed8.fx` expose, y compris ce qu'aucun de ses programmes n'utilise, là où une
reconstruction ne voit que ce que la variante compile.

### D'où vient la sémantique

Le premier registre écrit ici associait un nom à une sémantique, ce qui marchait mais
n'expliquait rien. Les sources de PhyreEngine, présentes sur le disque avec l'outil
d'assets de Cold Steel 3, donnent la vraie règle.

Une sémantique se déclare en HLSL, après le deux-points :

```hlsl
float4x4 World                : World;
float3   m_direction          : LIGHTDIRECTIONWS;
float4   GameMaterialDiffuse  : NodeMaterialDiffuse
float    AlphaThreshold       : ALPHATHRESHOLD
float4   GameMaterialTexcoord //: NodeMaterialTexcoord
```

D3D ne conserve pas la sémantique d'une uniforme : elle a disparu du bytecode. Elle se
lit donc dans la source, et se résout comme `Core/Rendering/PhyreSemantic.cpp` le
fait — table exacte de noms d'abord, puis le nom du paramètre contre la même table,
puis une recherche des mots que le moteur cherche : « shadow » suivi de « map », de
« transform » ou de « distance » ; « light » suivi de « colorinten », de « dir » puis
« ws » ; « eye » ou « cam » suivi de « po ». C'est ainsi que `m_direction` devient
`LIGHT_DIRECTION_WORLD_SPACE`, soit 203.

Ce que rien ne reconnaît revient au matériau : `CONSTANT` (64) pour une constante,
`TEXTURE2D`, `TEXTURE3D` ou `TEXTURECUBE` (66, 67, 68) selon la dimension que le
bytecode déclare, `SAMPLER` (71) pour un échantillonneur. La dernière ligne de
l'exemple le montre à l'envers : Falcom a commenté la sémantique de
`GameMaterialTexcoord`, et l'effet livré la déclare bien en `CONSTANT`.

Trois sémantiques n'existent pas dans le SDK — `NodeEdgeParameters`,
`NodeMaterialDiffuse` et `NodeMaterialEmission`, soit 223, 224 et 225. Falcom les a
ajoutées au bout de la plage ; elles sont lues des fichiers de CS1.

`ShaderForge plan` fait passer les dix-sept effets livrés avec ce seul mécanisme,
sans aucune liste de noms écrite à la main.

### Le hachage d'un flux de sommets

`PShaderStreamDefinition.m_nameHash` est `PHashTableTree::Hash`, graine 1973 :

```
h = 1973 ; pour chaque caractere : h = h * 33 + (c & 0x1f)
```

gardé sur seize bits. Le masque `& 0x1f` sur chaque caractère est ce qui met en échec
toute tentative de reconnaître la fonction depuis ses seules sorties — six flux
récoltés dans les effets livrés n'y suffisaient pas. `ShaderForge streams` vérifie le
calcul contre eux.

### Retrouver la disposition du tampon de capture

Un effet ne nomme que ce qu'un matériau remplit. Les paramètres de la scène — la vue,
la projection, le brouillard, les lumières ponctuelles — sont dans les programmes et
dans les tables de localisation des passes, mais **nulle part par leur nom**. Or ils
sont indispensables : ajouter une seule uniforme redispose tout le constant buffer, et
chaque entrée doit alors être réécrite avec son offset de capture.

Ils se retrouvent par composition. La réflexion d'un programme donne nom → offset de
constant buffer ; la table `PShaderParameterCaptureBufferLocationTypeConstantBuffer` de
sa passe donne offset de constant buffer → offset de capture. Les deux se recollent.

`ShaderForge capture <effet.phyre>` fait la composition et la vérifie contre ce que
l'effet déclare : sur les dix-sept effets livrés, **887 constantes situées, et toutes
celles que l'effet nomme tombent exactement sur l'offset qu'il déclare**. Les autres —
27 sur cs1shader, celles de la scène — sont récupérées du même coup.

Un effet réécrit garde donc les offsets de capture du modèle pour tout ce qu'il
partage avec lui, et n'alloue de nouveaux offsets que pour les uniformes que son HLSL
ajoute. C'est ce qui permet d'en ajouter sans rien déplacer de ce que le moteur
remplit lui-même.
