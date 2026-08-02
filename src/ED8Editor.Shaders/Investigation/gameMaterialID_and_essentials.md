# Investigation : gameMaterialID et fonctions essentielles CS1

**Dernière mise à jour : 2026-08-01 — analyse exhaustive des 429 shaders du jeu**

## Résultats de l'analyse globale

### gameMaterialID

**CONFIRMÉ** — le mot `gameMaterialID` apparaît dans le bytecode désassemblé
des shaders principaux (ed8.fx.phyre, ~571 KB). Les shaders "minimap" (~49 KB)
n'y font PAS référence.



D'après `phyre_authoring_handoff.md` :
> `m_gameMaterialIDs` never written. Needed by CS1. One id per segment, value 0.

D'après `plan.txt` (analyse des animations faciales) :
> Overlays par GameMaterialID : **11/12 = les deux yeux, 13 = bouche, 14 = teint**.
> Ne JAMAIS remplacer 15 (cheveux) ni 16 (peau).

### Hypothèses

1. **gameMaterialID est un identifiant de "type de surface"** utilisé par le moteur
   pour appliquer des traitements spéciaux (ombrages, overlays faciaux, etc.)

2. **Le shader n'utilise PAS directement gameMaterialID** — c'est le moteur qui
   lit cette valeur pour décider quel comportement adopter. Le shader reçoit des
   paramètres déjà résolus par le moteur.

3. **Pour les modèles custom**, il suffit probablement de mettre `m_gameMaterialIDs = [0]`
   (un zéro par segment) pour que le moteur traite le mesh normalement.

### À vérifier

- [ ] Le shader fait-il référence à `gameMaterialID` dans son bytecode ?
- [ ] Si oui, comment est-ce transmis (constant buffer, vertex input, texture ?)
- [ ] Les valeurs 11-16 sont-elles codées en dur dans le moteur ou dans les shaders ?

## Fonctions de rendu communes

### Méthode d'investigation

1. Décompiler TOUS les shaders du jeu (via `PhyreShaderDecompiler`)
2. Extraire toutes les instructions et chercher les patterns communs
3. Comparer les disassembly entre différents shaders pour trouver les fonctions
   qui apparaissent dans TOUS les shaders

### Fonctions suspectées obligatoires

#### 1. Transformations de base (Vertex Shader)

```
WorldViewProjection  →  toujours présent (offset 464 dans cb0)
World matrix          →  pour le skinning
Bone matrices         →  si skeletal animation
```

Tout shader custom doit au minimum :
- Déclarer `float4 _phyreReserved[29]` au début de cb0
- Déclarer `float4x4 WorldViewProjection` à l'offset 464
- Produire `SV_Position` en sortie du VS

#### 2. Éclairage (Fragment Shader)

Les shaders CS1 gèrent probablement :
- Lumière directionnelle (soleil)
- Lumières ponctuelles (torches, magie)
- Lumière ambiante

Le switch `NUM_LIGHTS` contrôle combien de lumières sont actives.

#### 3. Brouillard (Fragment Shader)

Le brouillard est probablement calculé dans le PS en utilisant
des paramètres du constant buffer (distance min/max, couleur).

#### 4. Textures

Le jeu utilise probablement :
- Texture diffuse (albedo)
- Texture normale (normal map)
- Texture spéculaire (specular/roughness)
- Texture d'ombre (shadow map)

### Paramètres moteur connus dans cb0

D'après l'analyse du SDK Phyre :

```
Offset  | Taille | Nom probable
--------|--------|-------------
0x000   | 464    | _phyreReserved[29]  (réservé Phyre)
0x1D0   | 64     | WorldViewProjection (matrice 4x4)
0x210   | 64     | World matrix
0x250   | 64     | View matrix
0x290   | 64     | Projection matrix
0x2D0   | ...    | Paramètres d'éclairage
0x???   | ...    | Paramètres de brouillard
0x???   | ...    | Paramètres de matériau custom
```

## Switches de contexte

### Liste exhaustive (à compléter par l'extraction)

D'après l'analyse des shaders existants :

| Switch              | Valeurs possibles | Description |
|---------------------|-------------------|-------------|
| NUM_LIGHTS          | 0, 1, 2, 3, ...  | Nombre de lumières dynamiques |
| INSTANCING_ENABLED  | 0, 1              | Instanciation hardware activée |
| SHADER_LOD_LEVEL    | 0, 1, 2           | Niveau de détail du shader |
| ???                 | ???               | À découvrir par analyse |

### Switches minimum pour un shader custom

Pour un shader custom simple qui ne gère pas l'éclairage dynamique :

```json
{
  "NUM_LIGHTS": 0,
  "INSTANCING_ENABLED": 0,
  "SHADER_LOD_LEVEL": 0
}
```

## Plan de vérification

### Étape 1 : Extraction des shaders

```csharp
var extractor = new PhyreShaderExtractor();
var shaders = extractor.DiscoverShaders("chemin/vers/data/asset/D3D11");
File.WriteAllText("shaders_inventory.json", JsonSerializer.Serialize(shaders));
```

### Étape 2 : Analyse globale

```csharp
var report = extractor.AnalyzeAllShaders(shaders);
// Examine report.AllContextSwitches, report.AllPassTypes, etc.
```

### Étape 3 : Décompilation d'un shader spécifique

```csharp
var decompiler = new PhyreShaderDecompiler();
var source = decompiler.Decompile(File.ReadAllBytes("ed8.fx#HASH.phyre"));

var merger = new PhyreShaderMerger();
var merged = merger.Merge(source);
File.WriteAllText("merged_shader.hlsl", merged);
```

### Étape 4 : Comparaison inter-shaders

Choisir 3-5 shaders de types différents :
- Shader de personnage (CHR)
- Shader de map (MAP)
- Shader de prop (OBJ)
- Shader d'effet (EFF)

Comparer les disassembly pour trouver ce qui est commun à TOUS.

## Notes sur le format fx.phyre

### Le bytecode est-il vraiment en clair ?

**Non.** Le bytecode stocké dans `PShaderProgramD3D11.m_compiledCode` est du
**bytecode D3D11 compilé** (DXBC/DXIL), pas du HLSL source.

CEPENDANT :
- Le bytecode D3D11 peut être **désassemblé** en assembleur GPU lisible
  (via `D3DDisassemble` / `Compiler.Disassemble`)
- Cet assembleur peut être **recompilé** avec `D3DAssemble`
- On peut aussi utiliser des outils comme `3DMigoto` ou `HLSLDecompiler` pour
  remonter au HLSL

### La "copie en clair débarrassée des sections inactives"

Le mécanisme est le suivant :
1. Le shader HLSL original contient des `#if NUM_LIGHTS == 2` etc.
2. Le compilateur Phyre compile **chaque permutation** séparément en passant
   les defines correspondants
3. Le bytecode résultant pour chaque permutation est stocké dans le fx.phyre
4. Il n'y a PAS de "copie en clair" du HLSL source dans le fx.phyre
5. Ce qu'on peut extraire, c'est le désassemblage du bytecode pour chaque
   permutation, qui est déterministe mais moins lisible que le HLSL original
