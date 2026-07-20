# ED8Editor.Decompiler

Module d'intégration du décompilateur de scripts CS1 dans l'éditeur.

Il encapsule le **moteur natif validé** (`cs1_decompiler.dll`, C ABI, roundtrip
octet-parfait) et expose un modèle managé propre que l'éditeur consomme pour
représenter chaque instruction par un **bloc** avec ses arguments typés, ses
expressions et ses sauts (branches).

## Ce que ça fournit

`ScriptDecompiler.Decompile(datPath)` → `DecompiledScript` :

- `SceneName` — l'identifiant du script (header) ;
- `Functions` — les fonctions (scènes) avec leur nom ; `IsCode` distingue le code
  des tables de données ;
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

## À venir (côté écriture / édition)

Le moteur natif gère déjà l'édition (insert/remove/move, retype d'arguments,
pose de saut symbolique) et la **réassemblage avec relocation complète de tous
les pointeurs** (y compris ceux enfouis dans une expression via un redispatch).
Ces fonctions ne sont pas encore exposées côté managé — à brancher quand l'UI
d'édition sera prête. La création/remplacement d'expression (popup) réutilisera
la structure `ExprElement`.
