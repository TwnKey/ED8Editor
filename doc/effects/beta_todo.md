# eff_editor — TODO route vers la beta (édition + clarté UI)

But : rendre l'outil utilisable par un non-initié. Tout en **anglais** dans l'UI.
Principe général : si on connaît la signification d'un bit/flag → **sélecteur** (cases à
cocher / combo) avec **valeur custom** possible pour combler les trous de connaissance.

---

## 1. Renommage / clarification des champs ✅ FAIT (passe principale)
Tous les champs renommés en anglais avec les significations connues, sélecteurs de
bits (d02[0] container, d02[1] orientation/enable, d02[3] draw-order + mesh subdiv),
hints dynamiques d04/d06/d08 (d08 selon la shape), tracks keyframes nommés + sélecteur
de mode (Additive/Uniform/Random/Loop) + borne random, couleurs (multiply/glow),
tooltip blend. Reste : quelques champs encore "Unknown" (d04[5-7], d05, d06[0-4],
d02[7], d02[4] byte2) — normal, on ne les connaît pas.

## 1b. (détail original ci-dessous)

Remplacer les noms bruts d02/d04/… par des noms explicites. Ce qu'on SAIT :

### d02 (8× u32) — en-tête du segment
- **d02[0]** — "Flags A" (def+0x30). Connu : **bit0 = Container** (quad non dessiné,
  segment persistant/loop) ; bit7 (0x80) utilisé par l'init des trails. Reste inconnu.
  → checkbox "Container (don't draw quad)" + hex custom.
- **d02[1]** — "Orientation & enable" (def+0x34). **bit0 (0x1) = ENABLE** (obligatoire,
  sinon le segment ne s'initialise pas), **0x4 = orient-enable**, **0x8 = orient source
  (velocity/camera)**, **0x10 = camera billboard**. → cases à cocher.
- **d02[2]** — "Transform / parent inheritance" (def+0x38 → runtime +0x20). Deux groupes :
  - Héritage parent (octet 1) : 0x1000 pos=parent-live, 0x2000 attach, 0x8000 pos figée
    au spawn, 0x4000 scale=parent-live, 0x20000 scale figée au spawn.
  - Orientation/transform : 0x10000 rot parent@spawn, 0x80000 billboard=mouvement,
    0x400 pré-roll vélocité, 0x100000/0x200000/0x400000 lock scale X/Y/Z, 0x800000
    ignore scale d'environnement.
  → "Inherit" reste OK mais préférer "Parent inheritance" ; "Orient" → "Orientation".
- **d02[3]** — octet 1 = **Draw order (Z)** ; octets 2/3 = **subdivisions du mesh**
  (na/nb pour les shapes non-quad). → "Draw order" + "Mesh subdivisions (radial/stacks)".
- **d02[4]** — octet 0 = **Shape**, octet 1 = **Blend** (déjà des sélecteurs, OK).
- **d02[5]** — u16 haut = **Sound / child-effect ID** (def+0x46). → "Sound ID".
- **d02[6]** — u16 bas = **Trail UV framerate** (def+0x48, défaut 30). → "Trail framerate".
- **d02[7]** — float **inconnu**. → "Unknown (float)".

### d04 (12× f32)
- **[0..4]** — **Crop** (Left, Top, Right, Bottom) → éditeur interactif (cf. §3).
- **[4]** — **Lifetime (s)**.
- **[5], [6], [7]** — **INCONNUS** (def+0x64/68/6c). → "Unknown".
- **[8]** — **Initial Y velocity — min** (borne basse de v0, l'ancien "VelY Init").
- **[9]** — **Initial Y velocity — max** (borne haute de v0). CONNU.
- **[10]** — **Gravity** (agit sur l'axe Y ; "Gravity" suffit).
- **[11]** — **Bounce** (restitution au sol). Renommer "Restitution" → **"Bounce"**.

### d05 (3× f32, V0x04 seulement)
- **INCONNU** (def+0x80..8c, nul dans tout le corpus). → "Unknown (legacy CS1)".

### d06 (9× f32) — "Base orientation"
- **[0]** — ⚠️ l'ancien label **"Enable" est FAUX/périmé** — à supprimer. def+0x8c, facteur
  d'offset caméra/ombre dans lapellant (gated par !=0). → "Unknown (camera-offset factor)".
- **[1..4]** — inconnus.
- **[5]** — **Base rotation X (deg)**.
- **[6]** — **Base rotation Y (deg)**.
- **[7]** — **Base rotation Z (deg)**.
- **[8]** — **Base Y offset** (translation Y de la matrice de base).

### d08 (8× f32) — dépend de la shape (CONNU, à afficher dynamiquement)
- Shape **quad (0x00)** : 2 coins du quad (A = x,y,z,w ; B = x,y,z,w).
- Shapes **mesh** : paramètres du mesh — cylinder/halfcyl : d08[0]=r0, d08[4]=r1,
  d08[1]=h0, d08[5]=h1 ; sphere/dome : d08[0]=rayon horizontal, d08[1]=rayon vertical ;
  cross : Y de d08[1] à d08[5] (largeur unité).
- Trails **0x14/0x15** : couleurs tête/queue.
  → label le bloc selon la shape courante.

### Keyframes d09–d0E — "Animation tracks (keyframes)"
Indiquer clairement que ce sont des **keyframes**. Noms des tracks :
- **d09 = Position**, **d0A = Rotation (Euler deg)**, **d0B = Scale**,
  **d0C = Rotation 2 (Euler deg)**, **d0D = Color (multiply/tint)**,
  **d0E = Color Add (additive glow)**.
Chaque keyframe (48 o) :
- floats[0..4] = **valeur** (x,y,z,w ou r,g,b,a selon le track) → labelliser les 4 champs.
- floats[4..8] = **2ᵉ borne aléatoire** (affichée seulement si flag Random).
- floats[8] = **Time (s)**.
- ints[0] u16 bas = **mode** : bit0 Additive, bit1 Uniform, bit2 Random, bit4 Loop-start,
  bit5 Loop-end → **SÉLECTEUR** (cases à cocher) au lieu d'un nombre.
- ints[0] u16 haut = **track type** (0 = valeur simple).
- trailing (f32) = **inconnu**.

---

## 2. Couleurs — renommage
- **d0D "Color Start"** → **"Color (multiply / tint)"** (multiplie la texture ; "Start"
  n'est pas approprié).
- **d0E "Color Add"** → **"Color Add (additive glow)"** (c'est bien une ADDITION,
  confirmé via le shader prémultiplié).

## 3. Éditeur de crop interactif ✅ FAIT
- Glisser un rectangle sur la vignette de texture définit le crop (data_04[0..4], normalisé),
  synchronisé avec les champs Crop L/T/R/B de l'éditeur. Un snapshot au début du drag = un undo.
  Overlay rouge existant montre le crop. Reste possible : poignées de redimensionnement des
  bords (précision fine à la souris) — optionnel, les champs numériques couvrent déjà la précision.

## 4. Blend modes — expliquer
- d02[4] octet 1 : 0x00 Opaque, 0x01 Alpha, 0x02 Additive, 0x04 Subtract.
- **"Subtract"** = reverse-subtract (dst − src) : **assombrit le fond** proportionnellement
  à la luminosité de la texture. → tooltip d'explication pour chaque mode.

## 5. Remplacement de texture d'un segment ✅ FAIT
- **Cas 1 (texture existante)** : bouton "Use existing game texture…" → choisit un
  `.pkg` déjà dans `data/asset/D3D11`, pointe fn_name_1 dessus. Aucun packaging.
- **Cas 2 (image PC)** : bouton "Replace from image…" → charge PNG/JPG/DDS et **génère
  un `.dds.phyre` STANDALONE from-scratch** à la dimension de l'image (RGBA8 lossless),
  package dans un nouveau `.pkg` écrit à la sauvegarde (jamais d'écrasement). **One-click :
  plus aucun template ni prompt.** Prévisualisation immédiate + auto-décodage de validation.
- **Encodeur standalone** (`phyre::encode_phyre_texture`) : schéma PhyreEngine constant par
  format embarqué (`src/core/phyre_skel/*.bin` via include_bytes!, extraits d'après la source
  SDK + vrais fichiers), on génère dims/bufsize/nom/pixels. Validé : **reproduction byte-exacte**
  des vraies textures ARGB8/RGBA8/DXT5 + dims arbitraires (100×40 non-p2). Encodeurs pixel
  ARGB8/RGBA8/BC1/BC3.
- **Conventions reversées** : pkg `I_XXX.pkg` = **2 entrées** — (1) `asset_D3D11.xml`
  (manifeste OBLIGATOIRE liant `symbol="I_XXX"` au cluster `data/D3D11/effects/images/xxx.dds.phyre`,
  généré byte-identique au jeu, CRLF), (2) entrée interne `xxx.dds.phyre` (drop "I_", lowercase).
  Entrées **compressées NISLZSS (méthode 1, flags=1)** comme toutes les vraies textures
  du jeu (compresseur = inverse exact du décompresseur, round-trip validé + tailles ~=
  jeu, XML→184 o identique) ; repli non-compressé si ça ne gagne rien. Magic copié du
  template. Jeu confirmé sans `.pka` = charge les 4323 `.pkg` loose par nom.

## 6. Édition de la hiérarchie & des arrays (structure)
- **Créer un node** (segment) avec valeurs par défaut sensées.
- **Supprimer un node** (et gérer ses enfants / références d14).
- **Ajouter / supprimer des children** (descripteurs de spawn d14).
- **Ajouter / supprimer des keyframes** dans tous les tracks (d09–d0E) et plus
  généralement **tous les arrays éditables** (d0F–d14, d17, d1x…).
- **Reparentage par drag & drop** : glisser un enfant sur un autre node pour le
  déplacer dans sa hiérarchie (met à jour le d14 du nouveau parent / retire de l'ancien).
- ✅ (fait) **Checkbox de visibilité** dans la hiérarchie (remplace l'icône œil vide).

## 7. Créer un fichier .eff from scratch
- Nouveau fichier vide (root + defaults) éditable puis sauvegardable. S'appuie sur
  le writer + un jeu de valeurs par défaut par version.

## 8. Fondation : round-trip write ✅ FAIT
- **1072/1072 fichiers byte-perfect** (`eff-cli roundtrip-all`). Corrigé : 8 octets de
  trailing manquants ; noms de segment cp932 non round-trippables (octets bruts préservés) ;
  noms ascii fn_name_1/2 + effect_name (bruts) ; bloc data_17 CS1 16 octets (brut).
  Zéro perte de données. Reste optionnel : test in-game d'un fichier ré-écrit non modifié.

---

## Principes UI transverses
- Tout en **anglais**.
- Bit/flag connu → **sélecteur** + champ **hex/valeur custom** pour les bits inconnus.
- Mode d'un keyframe → sélecteur (pas un chiffre).
- Marquer explicitement les blocs "Animation tracks (keyframes)".
