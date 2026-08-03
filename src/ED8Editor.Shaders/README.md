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
