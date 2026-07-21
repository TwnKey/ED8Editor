# ED8Editor.Decompiler

Module d'intégration du décompilateur de scripts CS1 dans l'éditeur.

Il encapsule le **moteur natif validé** (`cs1_decompiler.dll`, C ABI, roundtrip
octet-parfait) et expose un modèle managé propre que l'éditeur consomme pour
représenter chaque instruction par un **bloc** avec ses arguments typés, ses
expressions et ses sauts (branches).

## Ce que ça fournit

`ScriptDecompiler.Decompile(datPath)` → `DecompiledScript` :

- `SceneName` — l'identifiant du script (header) ;
- `Functions` — les fonctions (scènes) avec leur nom. Trois cas : `IsCode` vrai
  (flot d'instructions), `Table` non nul (table de données), sinon données brutes ;
- pour chaque fonction code, la liste des `DecompiledInstruction` :
  - `Name` (ex. `OP48_3`, `UI_OP19_0`), `Offset`, `Opcode` ;
  - `Arguments` typés : `scalar` (u8/s16/f32/ptr32/…), `string`, `dialog`,
    `bytes`, ou `expr` ;
  - pour un `expr`, la liste `ExprElement` (opérateurs + opérandes typés, ex.
    `sys[4] push 10 ==`) ;
  - `Jumps` : chaque saut pointe vers l'index de l'instruction cible dans la
    fonction (pour tracer les branches).

L'opcode brut et les sélecteurs restent internes au moteur : l'éditeur ne voit
que des noms d'instruction et des arguments.

### Tables de données

Les fonctions nommées-table (ActionTable, AlgoTable, FieldMonsterData, BookDataX,
CreateMonsters, …) sont exposées via `DecompiledFunction.Table` (`DecompiledTable`) :

- `Kind` / `Id` — le type de table (routé par nom, comme le jeu) ;
- `Fields` — les champs typés `TableField` : `Type` (`u8`/`s16`/`s32`/`f32`/`string`/
  `fill`/`bytes`), `IntValue`, `FloatValue`, `Text` (pour `string`), et toujours
  `Raw` (octets bruts → réassemblage octet-parfait) ;
- `IsStale` — vrai si la table ne suit pas le format du jeu (fichier périmé/malformé,
  typiquement du debug Falcom `al*`) : `Fields` contient alors un unique blob brut
  préservé. L'éditeur peut ainsi afficher un badge « périmé » plutôt qu'un faux parse.

Le découpage des tables est un portage fidèle des readers du jeu (décompiles Ghidra),
vérifié contre le modèle Python de référence (2226 tables structurées identiques).

**Édition de tables** (côté natif, via `NativeMethods`) :

- champs : `cs1i_table_set_field_i/_f/_text/_bytes`. Scalaires et strings largeur-fixe
  préservent la taille (le fill se rééquilibre) → aucun reflow ;
- tables entières : `cs1i_table_add(pos, name, bytes, len)` et `cs1i_func_remove(f)`.

`cs1i_serialize` réassemble le fichier avec **relocation complète** : header préservé à
l'octet près quand le nombre de fonctions est inchangé, sinon **reconstruit fidèlement**
(ordre des sections d'origine conservé) ; tous les pointeurs (sauts de code y compris
ceux enfouis dans les expressions) sont recalculés. Roundtrip **byte-perfect vérifié sur
tout le corpus (1959 fichiers)**.

## Construire

1. **La DLL native** (une fois, ou à chaque changement du moteur) :
   ouvrir un *« x64 Native Tools Command Prompt for VS »* puis :

   ```bat
   cd src\ED8Editor.Decompiler\native
   build.cmd
   ```

   (ou via CMake : `cmake -B build -A x64 && cmake --build build --config Release`).
   Produit `native\cs1_decompiler.dll`, recopié automatiquement à côté des exe .NET.

2. **Ajouter les projets à la solution** :

   ```bat
   dotnet sln add src\ED8Editor.Decompiler\ED8Editor.Decompiler.csproj
   dotnet sln add src\ED8Editor.DecompilerProbe\ED8Editor.DecompilerProbe.csproj
   ```

3. **Tester** le décodage sur un script :

   ```bat
   dotnet run --project src\ED8Editor.DecompilerProbe -- "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\scripts\scena\dat\a0000.dat"
   ```

## Fichiers

- `native/` — sources C++ du moteur (`cs1_instr_api.cpp` + headers) + `build.cmd` / `CMakeLists.txt`.
- `cs1_instructions.json` — le document (source de vérité), copié à côté de l'exe.
- `NativeMethods.cs` — P/Invoke vers la DLL.
- `DecompiledModel.cs` — modèle managé (records).
- `ScriptDecompiler.cs` — API : charge le registre, ouvre un `.dat`, construit le modèle.

## Édition (moteur natif, P/Invoke exposés)

- **Instructions** : `set_i/f/s`, `insert`, `remove`, `move`, `set_jump` (saut symbolique).
- **Tables** : champs `set_field_i/f/text/bytes` ; tables entières `table_add`/`func_remove` ;
  **lignes** via `table_field_insert`/`table_field_delete` (+ `schema_record_len`/`schema_field_*`
  pour dimensionner une ligne selon le schéma).
- **Retypage des tables** : les blocs fixes des records (AlgoTable, bloc 42o d'ActionTable,
  préfixe FieldMonster, lignes AddCollision/Reaction/FieldFollow) sont pilotés par un
  **schéma éditable** (`cs1_tables.json`, chargé via `cs1i_load_tables_schema`). On peut
  retyper/découper un blob `bytes` en champs typés ; l'invariant (somme = longueur du record)
  garantit le round-trip. Éditable dans l'analyseur (fenêtre « 🧬 Schémas tables »).
- **Boucles** (instructions à corps répété) : `arg_loop_count/dup/remove` + accès aux champs
  d'itération ; le compteur est **auto-synchronisé** à l'encodage.
- **Expressions** : `arg_expr_clear` + `arg_expr_push(subop, value)` construisent une
  expression postfixe (opérandes + opérateurs), terminateur géré automatiquement.

`cs1i_serialize` réassemble avec **relocation complète** (tous les pointeurs, y compris ceux
enfouis dans une expression via redispatch) et **header byte-perfect** (préservé verbatim si
le nombre de fonctions est inchangé, sinon reconstruit fidèlement). Round-trip **byte-perfect
vérifié sur tout le corpus (1959 fichiers)**.
