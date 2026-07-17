## AVANCEMENT (12 juil. 2026)

RÉSOLU (12 juil. 2026) — Édition de hiérarchie & création from scratch :
  - "New" button: crée un .eff minimal (v0x04, 1 segment root avec keyframes par défaut)
  - "+ Node" / "- Node": ajout/suppression de segments avec nettoyage automatique des d14.
  - edit_children (d14) : bouton "+ Add child", bouton "🗑" par entrée, édition des champs
    (child, count, per-burst, trigger, delay, interval).
  - edit_array (keyframes d09–d0E) : bouton "+ Add keyframe", bouton "🗑" par entrée.
    Les tracks vides sont maintenant affichées (plus de "return early").
  - Reparentage par drag & drop : glisser un nœud de l'arbre sur un autre pour changer
    son parent ; drop en bas de l'arbre = rendre root.

RÉSOLU (commit aeff18e) — Physique projectile pour mk_lp_vomi :
  - "gravité" = data_04[10] ; "VelY Init" = v0 = rand(data_04[8], data_04[9]) tiré au spawn ;
    "d04[9]=2.5" = borne haute de v0. Formule appliquée : y += v0*t − 0.5*g*t² (gate g≠0).
  - Validé sur les données : mk_lp_vomi seg4 水飛沫 v0=[10..2.5] g=9 ; seg1 houbutu g=8 (chute pure).
    -> le houbutu invisible devrait apparaître (il tombe dans le champ) ; à confirmer en jeu/preview.

RÉSOLU (commit 86db24a) — Crop/UV (rain00, "texture hors sujet" gameover, cadre runhorse) :
  - UV = crop brut ; flip (cl>cr, runhorse), ligne dégénérée (cl==cr, gameover seg23 background
    (加算) qui affichait tout l'atlas), tiling (crop>1, rain00 R=80/100) via GL_REPEAT.
  - Cadre rouge de l'aperçu affiché aussi pour flip/dégénéré/tiling.

RESTE À FAIRE :
  - mk_talk : tailles TALK/croix trop petites -> sans doute le quad devrait suivre le ratio du crop
    (crop en bande étirée sur un quad carré). À creuser.
  - gameover "aura noire devant" : Z-order. Les fonds 下地/背景 ont z=252-255 (dessinés PAR-DESSUS
    les lettres z=0-1). Mais mk_lp marchait -> z n'est PAS un simple painter-key. À reverser :
    trouver où le byte d02[3]>>8 est consommé au draw (layer/passe séparée ?) avant de coder.
  - Modèle de visibilité : d02[0]&1 (conteneur) déjà correct. force_billboard reste un override
    global ; affiner si besoin après le Z-order.

---
Ci-dessous les points d'investigation d'origine avec indices jeu/décompilé :
Pour "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects\system\mk_lp_vomi.eff" :
- Il faut implémenter la gravité 
Elle est lue dans l'appelant :
*(float *)(param_1 + 0x454) <--- ici

uint __thiscall lapellant(int param_1,float param_2)

{
  float fVar1;
  float fVar2;
  float fVar3;
  int iVar4;
  code *pcVar5;
  char cVar6;
  uint in_EAX;
  undefined4 *puVar7;
  undefined4 uVar8;
  float *pfVar9;
  uint uVar10;
  int extraout_ECX;
  undefined *puVar11;
  char *indice_3_contient_count;
  int iVar12;
  undefined4 *puVar13;
  float fVar14;
  float10 fVar15;
  undefined1 local_17c [64];
  byte *local_13c;
  undefined4 local_138;
  undefined4 local_134;
  undefined4 local_130;
  float local_12c;
  float local_128;
  float local_124;
  undefined4 local_120;
  float local_11c;
  float local_118;
  float local_114;
  undefined4 local_110;
  float local_10c;
  float local_108;
  float local_104;
  undefined4 local_100;
  undefined4 local_fc [4];
  undefined4 local_ec;
  undefined4 local_e8;
  undefined4 local_e4;
  undefined4 local_e0;
  undefined4 local_dc;
  undefined4 local_d8;
  undefined4 local_d4;
  undefined4 local_d0;
  undefined4 local_cc;
  undefined4 local_c8;
  undefined4 local_c4;
  undefined4 local_c0;
  byte *local_bc;
  float local_b8;
  float local_b4;
  undefined4 local_b0;
  float local_ac;
  float local_a8;
  float local_a4;
  float local_9c;
  byte local_95;
  float local_94;
  float local_90;
  float local_8c;
  undefined4 local_88;
  byte *local_84;
  float local_80;
  float local_7c;
  float local_78;
  float local_74;
  float local_70;
  float local_6c;
  undefined4 local_68;
  float local_64;
  float local_60;
  float local_5c;
  char local_51;
  float local_50;
  float local_4c;
  float local_48;
  float local_44;
  undefined4 local_40;
  char local_3b;
  byte local_3a;
  byte local_39;
  float local_38;
  float local_34;
  float local_30;
  float local_2c;
  float local_28;
  float local_24;
  float local_20;
  byte *local_1c;
  char local_15;
  float local_14;
  float local_10;
  byte local_9;
  float local_8;
  bool count_byte;
  
  if (*(char *)(param_1 + 0x478) != '\0') goto LAB_00591de2;
  *(undefined1 *)(param_1 + 0x478) = 1;
  if ((*(int *)(param_1 + 0xc) != 0) && (*(char *)(*(int *)(param_1 + 0xc) + 0x478) == '\0')) {
    lapellant(param_2,0);
  }
  local_80 = *(float *)(param_1 + 0x220);
  local_84 = *(byte **)(param_1 + 0x21c);
  local_7c = *(float *)(param_1 + 0x224);
  *(float *)(param_1 + 0x450) = *(float *)(param_1 + 0x450) + param_2;
  local_78 = *(float *)(param_1 + 0x228);
  local_3b = '\0';
  *(float *)(param_1 + 0x448) = *(float *)(param_1 + 0x448) + param_2;
  local_51 = '\0';
  *(float *)(param_1 + 0x458) = param_2 * *(float *)(param_1 + 0x45c) + *(float *)(param_1 + 0x458);
  if (((*(byte *)(*(int *)(param_1 + 0x3dc) + 0x34) & 0x10) != 0) ||
     (local_15 = '\0', (*(byte *)(*(int *)(param_1 + 0x14) + 0x158) & 0x10) != 0)) {
    local_15 = '\x01';
  }
  local_9c = (float)thunk_FUN_005a46a0();
  if ((*(byte *)(param_1 + 0x1c) & 1) != 0) {
    interpolation(param_1 + 0x294,param_2,param_1 + 0x25c,*(int *)(param_1 + 0x3dc) + 0xd0);
    interpolation(param_1 + 0x314,param_2,param_1 + 0x2dc,*(int *)(param_1 + 0x3dc) + 0xd8);
    interpolation(param_1 + 0x2d4,param_2,param_1 + 0x29c,*(int *)(param_1 + 0x3dc) + 0xe0);
                    /* ici
                        */
    interpolation(param_1 + 0x354,param_2,param_1 + 0x31c,*(int *)(param_1 + 0x3dc) + 0xe8);
    if ((~*(uint *)(param_1 + 0x1c) & 0x200) != 0) {
      interpolation(param_1 + 0x394,param_2,param_1 + 0x35c,*(int *)(param_1 + 0x3dc) + 0xf0);
      interpolation(param_1 + 0x3d4,param_2,param_1 + 0x39c,*(int *)(param_1 + 0x3dc) + 0xf8);
    }
    local_2c = *(float *)(param_1 + 0x2e4);
    local_28 = *(float *)(param_1 + 0x2e8);
    local_24 = *(float *)(param_1 + 0x2ec);
    puVar7 = (undefined4 *)thunk_FUN_0044d1f0(&local_4c,&local_2c);
    *(undefined4 *)(param_1 + 0x2e4) = *puVar7;
    *(undefined4 *)(param_1 + 0x2e8) = puVar7[1];
    *(undefined4 *)(param_1 + 0x2ec) = puVar7[2];
    local_2c = *(float *)(param_1 + 0x324);
    local_28 = *(float *)(param_1 + 0x328);
    local_24 = *(float *)(param_1 + 0x32c);
    puVar7 = (undefined4 *)thunk_FUN_0044d1f0(&local_4c,&local_2c);
    *(undefined4 *)(param_1 + 0x324) = *puVar7;
    *(undefined4 *)(param_1 + 0x328) = puVar7[1];
    *(undefined4 *)(param_1 + 0x32c) = puVar7[2];
  }
  local_64 = *(float *)(param_1 + 0x264);
  iVar12 = *(int *)(param_1 + 0x14);
  local_60 = *(float *)(param_1 + 0x268);
  local_3a = *(byte *)(iVar12 + 0x15a) & 1;
  local_5c = *(float *)(param_1 + 0x26c);
  local_74 = *(float *)(param_1 + 0x2e4);
  local_70 = *(float *)(param_1 + 0x2e8);
  local_6c = *(float *)(param_1 + 0x2ec);
  local_1c = *(byte **)(param_1 + 0x2a4);
  local_38 = *(float *)(param_1 + 0x2a8);
  local_a4 = *(float *)(param_1 + 0x2ac);
  local_2c = *(float *)(param_1 + 0x324);
  local_28 = *(float *)(param_1 + 0x328);
  local_24 = *(float *)(param_1 + 0x32c);
  if (local_3a != 0) {
    local_74 = -local_74;
    local_70 = -local_70;
    local_6c = -local_6c;
  }
  local_34 = 0.0;
  if (*(float *)(param_1 + 0x454) != 0.0) {
    fVar14 = *(float *)(param_1 + 0x450);
    fVar3 = fVar14 * *(float *)(param_1 + 0x454);
    local_34 = *(float *)(param_1 + 0x468) - fVar3;
    local_60 = (*(float *)(param_1 + 0x468) * fVar14 - fVar14 * fVar3 * 0.5) + local_60;
  }
  uVar10 = *(uint *)(param_1 + 0x20);
  local_50 = local_a4;
  if ((uVar10 & 0x4000) == 0) {
    if ((uVar10 & 0x20000) != 0) {
      fVar14 = *(float *)(param_1 + 0x1dc);
      fVar1 = *(float *)(param_1 + 0x1e0);
      local_50 = *(float *)(param_1 + 0x1e4);
      fVar2 = *(float *)(param_1 + 0x1dc);
      fVar3 = *(float *)(param_1 + 0x1e0);
      local_8 = *(float *)(param_1 + 0x1e4);
      goto LAB_00590470;
    }
  }
  else {
    iVar4 = *(int *)(param_1 + 0xc);
    if (iVar4 != 0) {
      fVar14 = *(float *)(iVar4 + 0x20c);
      fVar1 = *(float *)(iVar4 + 0x210);
      local_50 = *(float *)(iVar4 + 0x214);
      fVar2 = *(float *)(iVar4 + 0x20c);
      fVar3 = *(float *)(iVar4 + 0x210);
      local_8 = *(float *)(iVar4 + 0x214);
LAB_00590470:
      local_1c = (byte *)(fVar14 * (float)local_1c);
      local_38 = local_38 * fVar1;
      local_50 = local_a4 * local_50;
      local_60 = fVar3 * local_60;
      local_64 = fVar2 * local_64;
      local_5c = local_8 * local_5c;
    }
  }
  local_128 = local_38;
  local_ac = (float)local_1c;
  local_a8 = local_38;
  if ((uVar10 & 0x100000) == 0) {
    local_38 = *(float *)(iVar12 + 0x34);
  }
  else {
    local_38 = 1.0;
  }
  local_1c = (byte *)(local_38 * (float)local_1c);
  if ((uVar10 & 0x200000) == 0) {
    local_38 = *(float *)(iVar12 + 0x38);
  }
  else {
    local_38 = 1.0;
  }
  local_128 = local_38 * local_128;
  if ((uVar10 & 0x400000) == 0) {
    local_10 = *(float *)(iVar12 + 0x3c);
  }
  else {
    local_10 = 1.0;
  }
  local_114 = local_10 * local_50;
  local_bc = local_1c;
  if (local_3a != 0) {
    local_bc = (byte *)-(float)local_1c;
  }
  local_13c = local_bc;
  local_138 = 0;
  local_134 = 0;
  local_130 = 0;
  local_12c = 0.0;
  local_124 = 0.0;
  local_120 = 0;
  local_11c = 0.0;
  local_118 = 0.0;
  local_110 = 0;
  local_10c = 0.0;
  local_108 = 0.0;
  local_104 = 0.0;
  local_100 = 0x3f800000;
  local_b8 = local_128;
  local_b4 = local_114;
  local_44 = local_114;
  local_38 = local_128;
  thunk_FUN_00436c40(&local_13c);
  puVar7 = (undefined4 *)(param_1 + 0x68);
  *puVar7 = local_fc[0];
  *(undefined4 *)(param_1 + 0x6c) = local_fc[1];
  *(undefined4 *)(param_1 + 0x70) = local_fc[2];
  *(undefined4 *)(param_1 + 0x74) = local_fc[3];
  *(undefined4 *)(param_1 + 0x78) = local_ec;
  *(undefined4 *)(param_1 + 0x7c) = local_e8;
  *(undefined4 *)(param_1 + 0x80) = local_e4;
  *(undefined4 *)(param_1 + 0x84) = local_e0;
  *(undefined4 *)(param_1 + 0x88) = local_dc;
  *(undefined4 *)(param_1 + 0x8c) = local_d8;
  *(undefined4 *)(param_1 + 0x90) = local_d4;
  *(undefined4 *)(param_1 + 0x94) = local_d0;
  *(undefined4 *)(param_1 + 0x98) = local_cc;
  *(undefined4 *)(param_1 + 0x9c) = local_c8;
  *(undefined4 *)(param_1 + 0xa0) = local_c4;
  *(undefined4 *)(param_1 + 0xa4) = local_c0;
  if ((*(uint *)(param_1 + 0x20) & 0x400) == 0) {
    uVar8 = thunk_FUN_0044ee50(&local_13c,&local_74);
    thunk_FUN_00436c40(uVar8);
    *(undefined4 *)(param_1 + 0xe8) = local_fc[0];
    *(undefined4 *)(param_1 + 0xec) = local_fc[1];
    *(undefined4 *)(param_1 + 0xf0) = local_fc[2];
    *(undefined4 *)(param_1 + 0xf4) = local_fc[3];
    *(undefined4 *)(param_1 + 0xf8) = local_ec;
    *(undefined4 *)(param_1 + 0xfc) = local_e8;
    *(undefined4 *)(param_1 + 0x100) = local_e4;
    *(undefined4 *)(param_1 + 0x104) = local_e0;
    *(undefined4 *)(param_1 + 0x108) = local_dc;
    *(undefined4 *)(param_1 + 0x10c) = local_d8;
    *(undefined4 *)(param_1 + 0x110) = local_d4;
    *(undefined4 *)(param_1 + 0x114) = local_d0;
    *(undefined4 *)(param_1 + 0x118) = local_cc;
    *(undefined4 *)(param_1 + 0x11c) = local_c8;
    *(undefined4 *)(param_1 + 0x120) = local_c4;
    *(undefined4 *)(param_1 + 0x124) = local_c0;
  }
  else {
    uVar8 = thunk_FUN_0058d630(local_fc,param_1 + 0x22c);
    thunk_FUN_0042a680(uVar8);
  }
  iVar12 = param_1 + 0xe8;
  uVar8 = ici(local_fc,puVar7);
  thunk_FUN_0042a680(uVar8);
  uVar8 = thunk_FUN_0044ee50(&local_13c,&local_2c);
  thunk_FUN_00436c40(uVar8);
  if ((*(uint *)(param_1 + 0x20) & 0x80000) != 0) {
    uVar8 = ici(&local_13c,puVar7);
    thunk_FUN_0042a680(uVar8);
    uVar8 = ici(&local_13c,iVar12);
    thunk_FUN_0042a680(uVar8);
  }
  if ((~*(uint *)(param_1 + 0x20) & 0x800000) != 0) {
    iVar4 = *(int *)(param_1 + 0x14);
    local_64 = *(float *)(iVar4 + 0x34) * local_64;
    local_60 = *(float *)(iVar4 + 0x38) * local_60;
    local_8 = *(float *)(iVar4 + 0x3c);
    local_10 = local_5c;
    local_5c = local_8 * local_5c;
  }
  *(float *)(param_1 + 0x98) = local_64;
  *(float *)(param_1 + 0x9c) = local_60;
  *(float *)(param_1 + 0xa0) = local_5c;
  local_24 = local_5c;
  local_2c = local_64;
  local_28 = local_60;
  local_20 = 1.0;
  thunk_FUN_0045c3c0(&local_4c,&local_2c);
  *(float *)(param_1 + 0x98) = local_4c;
  *(float *)(param_1 + 0x9c) = local_48;
  *(float *)(param_1 + 0xa0) = local_44;
  *(undefined4 *)(param_1 + 0xa4) = local_40;
  if ((*(uint *)(param_1 + 0x20) & 0x2000) == 0) {
    if ((*(uint *)(param_1 + 0x20) & 0x10000) != 0) {
      uVar8 = ici(local_fc,iVar12);
      thunk_FUN_0042a680(uVar8);
      uVar8 = ici(local_fc,puVar7);
      thunk_FUN_0042a680(uVar8);
      local_74 = *(float *)(param_1 + 0x1cc) + local_74;
      local_70 = *(float *)(param_1 + 0x1d0) + local_70;
      local_6c = *(float *)(param_1 + 0x1d4) + local_6c;
    }
  }
  else {
    pfVar9 = *(float **)(param_1 + 0x1a8);
    if (pfVar9 == (float *)0x0) {
      if (*(int *)(param_1 + 0xc) == 0) {
        uVar8 = ici(local_fc,iVar12);
        thunk_FUN_0042a680(uVar8);
        uVar8 = ici(local_fc,puVar7);
        thunk_FUN_0042a680(uVar8);
      }
      else {
        uVar8 = ici(local_fc,iVar12);
        thunk_FUN_0042a680(uVar8);
        uVar8 = ici(local_fc,puVar7);
        thunk_FUN_0042a680(uVar8);
        thunk_FUN_0042d790(*(int *)(param_1 + 0xc) + 0x1fc);
      }
    }
    else {
      local_2c = pfVar9[8];
      local_28 = pfVar9[9];
      puVar7 = &DAT_00c7bec0;
      puVar13 = local_fc;
      for (iVar12 = 0x10; iVar12 != 0; iVar12 = iVar12 + -1) {
        *puVar13 = *puVar7;
        puVar7 = puVar7 + 1;
        puVar13 = puVar13 + 1;
      }
      local_104 = pfVar9[10];
      local_11c = pfVar9[4];
      local_118 = pfVar9[5];
      local_114 = pfVar9[6];
      local_12c = *pfVar9;
      local_128 = pfVar9[1];
      local_124 = pfVar9[2];
      local_10c = local_2c;
      local_108 = local_28;
      local_94 = local_12c;
      local_90 = local_128;
      local_8c = local_124;
      local_4c = local_11c;
      local_48 = local_118;
      local_44 = local_114;
      local_24 = local_104;
      thunk_FUN_0042e8c0(&local_12c);
      thunk_FUN_0058d490(local_fc,0);
      uVar8 = ici(&local_13c,param_1 + 0x68);
      thunk_FUN_0042a680(uVar8);
    }
  }
  if (local_3a != 0) {
    local_2c = *(float *)(param_1 + 0x98);
    local_28 = *(float *)(param_1 + 0x9c);
    local_24 = *(float *)(param_1 + 0xa0);
    local_4c = -1.0;
    local_48 = 1.0;
    local_44 = 1.0;
    uVar8 = thunk_FUN_004365b0(&local_13c,&local_4c);
    uVar8 = thunk_FUN_00437140(local_17c,(undefined4 *)(param_1 + 0x68),uVar8);
    thunk_FUN_00436c40(uVar8);
    *(undefined4 *)(param_1 + 0x68) = local_fc[0];
    *(undefined4 *)(param_1 + 0x6c) = local_fc[1];
    *(undefined4 *)(param_1 + 0x70) = local_fc[2];
    *(undefined4 *)(param_1 + 0x74) = local_fc[3];
    *(undefined4 *)(param_1 + 0x78) = local_ec;
    *(undefined4 *)(param_1 + 0x7c) = local_e8;
    *(undefined4 *)(param_1 + 0x80) = local_e4;
    *(undefined4 *)(param_1 + 0x84) = local_e0;
    *(undefined4 *)(param_1 + 0x88) = local_dc;
    *(undefined4 *)(param_1 + 0x8c) = local_d8;
    *(undefined4 *)(param_1 + 0x90) = local_d4;
    *(undefined4 *)(param_1 + 0x94) = local_d0;
    *(undefined4 *)(param_1 + 0x98) = local_cc;
    *(undefined4 *)(param_1 + 0x9c) = local_c8;
    *(undefined4 *)(param_1 + 0xa0) = local_c4;
    *(undefined4 *)(param_1 + 0xa4) = local_c0;
    *(float *)(param_1 + 0x98) = local_2c;
    *(float *)(param_1 + 0x9c) = local_28;
    *(float *)(param_1 + 0xa0) = local_24;
  }
  if ((*(uint *)(param_1 + 0x20) & 0x1000) == 0) {
    if ((*(uint *)(param_1 + 0x20) & 0x8000) != 0) {
      local_24 = *(float *)(param_1 + 0xa0);
      local_4c = *(float *)(param_1 + 0x1bc) + *(float *)(param_1 + 0x98);
      local_48 = *(float *)(param_1 + 0x1c0) + *(float *)(param_1 + 0x9c);
      local_5c = *(float *)(param_1 + 0x1c4);
      goto LAB_00590d35;
    }
  }
  else {
    iVar12 = *(int *)(param_1 + 0x1a8);
    local_4c = *(float *)(param_1 + 0x98);
    if (iVar12 == 0) {
      iVar12 = *(int *)(param_1 + 0xc);
      local_24 = *(float *)(param_1 + 0xa0);
      if (iVar12 == 0) {
        iVar12 = *(int *)(param_1 + 0x14);
        local_4c = local_4c + *(float *)(iVar12 + 0x14);
        local_48 = *(float *)(iVar12 + 0x18) + *(float *)(param_1 + 0x9c);
        local_5c = *(float *)(iVar12 + 0x1c);
      }
      else {
        local_4c = *(float *)(iVar12 + 0x1ec) + local_4c;
        local_48 = *(float *)(iVar12 + 0x1f0) + *(float *)(param_1 + 0x9c);
        local_5c = *(float *)(iVar12 + 500);
      }
LAB_00590d35:
      local_5c = local_5c + local_24;
      local_64 = local_4c;
      local_60 = local_48;
      *(float *)(param_1 + 0xa0) = local_5c;
      *(float *)(param_1 + 0x98) = local_4c;
      *(float *)(param_1 + 0x9c) = local_48;
      local_44 = local_5c;
    }
    else {
      local_48 = *(float *)(param_1 + 0x9c);
      local_44 = *(float *)(param_1 + 0xa0);
      local_94 = *(float *)(iVar12 + 0x30) + local_4c;
      local_90 = *(float *)(iVar12 + 0x34) + local_48;
      local_8c = *(float *)(iVar12 + 0x38) + local_44;
      *(float *)(param_1 + 0x98) = local_94;
      *(float *)(param_1 + 0x9c) = local_90;
      *(float *)(param_1 + 0xa0) = local_8c;
    }
  }
  *(float *)(param_1 + 0x1ec) = local_64;
  *(float *)(param_1 + 0x1f0) = local_60;
  *(float *)(param_1 + 500) = local_5c;
  *(float *)(param_1 + 0x1fc) = local_74;
  *(float *)(param_1 + 0x200) = local_70;
  *(float *)(param_1 + 0x204) = local_6c;
  *(float *)(param_1 + 0x20c) = local_ac;
  *(float *)(param_1 + 0x210) = local_a8;
  *(float *)(param_1 + 0x214) = local_50;
  local_ac = *(float *)(param_1 + 0x98);
  local_a8 = *(float *)(param_1 + 0x9c);
  local_a4 = *(float *)(param_1 + 0xa0);
  if (*(float *)(param_1 + 0x454) != 0.0) {
    if (local_a8 <= *(float *)(*(int *)(param_1 + 0x14) + 0x1b8)) {
      local_51 = '\x01';
      if ((*(uint *)(param_1 + 0x20) & 0x800) == 0) {
        if (*(float *)(*(int *)(param_1 + 0x3dc) + 0x7c) != 0.0) {
          *(float *)(param_1 + 0x468) = -local_34;
          *(float *)(param_1 + 0x468) = -local_34 * *(float *)(*(int *)(param_1 + 0x3dc) + 0x7c);
          *(undefined4 *)(param_1 + 0x450) = 0;
          if (*(float *)(param_1 + 0x468) < 0.4) {
            *(undefined4 *)(param_1 + 0x454) = 0;
          }
          *(undefined4 *)(param_1 + 0x1c0) = *(undefined4 *)(*(int *)(param_1 + 0x14) + 0x1b8);
          if (*(int *)(param_1 + 0xc) == 0) {
            *(uint *)(param_1 + 0x20) = *(uint *)(param_1 + 0x20) & 0xffffefff;
          }
        }
      }
      else {
        local_3b = '\x01';
      }
    }
    *(float *)(param_1 + 0x98) = local_ac;
    *(float *)(param_1 + 0x9c) = local_a8;
    *(float *)(param_1 + 0xa0) = local_a4;
  }
  local_2c = local_ac - (float)local_84;
  local_34 = local_a8 - local_80;
  local_24 = local_a4 - local_7c;
  local_8 = local_24 * local_24 + local_34 * local_34 + local_2c * local_2c;
  local_28 = local_34;
  local_10 = local_24;
  if (local_8 != 0.0) {
    local_94 = local_2c;
    local_90 = local_34;
    local_8c = local_24;
    puVar7 = (undefined4 *)thunk_FUN_00538b70(&local_2c,local_2c,local_34,local_24,local_88);
    *(undefined4 *)(param_1 + 0x22c) = *puVar7;
    *(undefined4 *)(param_1 + 0x230) = puVar7[1];
    *(undefined4 *)(param_1 + 0x234) = puVar7[2];
  }
  uVar10 = *(uint *)(*(int *)(param_1 + 0x3dc) + 0x34);
  if ((uVar10 & 0xc) != 0) {
    puVar11 = &DAT_00c7c4a8;
    if ((uVar10 & 8) != 0) {
      puVar11 = (undefined *)((int)local_9c + 0x218);
    }
    if (local_15 != '\0') {
      puVar11 = (undefined *)((int)local_9c + 600);
    }
    local_64 = local_ac;
    local_60 = local_a8;
    local_5c = local_a4;
    if ((*(uint *)(param_1 + 0x20) & 0x400) != 0) {
      fVar15 = (float10)thunk_FUN_0058d8b0(&local_84,&local_ac);
      local_6c = (float)fVar15;
    }
    local_74 = 0.0;
    local_70 = 0.0;
    uVar8 = thunk_FUN_0058da20(local_17c,&local_64,&local_74,&local_bc,puVar11);
    thunk_FUN_0042a680(uVar8);
  }
  local_2c = *(float *)(param_1 + 0x198);
  local_28 = *(float *)(param_1 + 0x19c);
  local_24 = *(float *)(param_1 + 0x1a0);
  local_20 = *(float *)(param_1 + 0x1a4);
  if (local_20 != 0.0) {
    uVar8 = ici(local_17c,param_1 + 0x168);
    thunk_FUN_0042a680(uVar8);
  }
  local_2c = *(float *)(param_1 + 0x98);
  local_28 = *(float *)(param_1 + 0x9c);
  local_24 = *(float *)(param_1 + 0xa0);
  *(float *)(param_1 + 0x21c) = local_2c;
  *(float *)(param_1 + 0x220) = local_28;
  *(float *)(param_1 + 0x224) = local_24;
  *(undefined4 *)(param_1 + 0x1ec) = *(undefined4 *)(param_1 + 0x21c);
  *(undefined4 *)(param_1 + 0x1f0) = *(undefined4 *)(param_1 + 0x220);
  *(undefined4 *)(param_1 + 500) = *(undefined4 *)(param_1 + 0x224);
  if (((*(uint *)(param_1 + 0x1c) & 1) != 0) && ((~*(uint *)(param_1 + 0x1c) & 0x200) != 0)) {
    thunk_FUN_0058c430(param_2);
    iVar12 = *(int *)(param_1 + 0x14);
    fVar14 = *(float *)(iVar12 + 0x180);
    fVar3 = *(float *)(iVar12 + 0x184);
    fVar2 = *(float *)(iVar12 + 0x188);
    local_1c = *(byte **)(param_1 + 0x368);
    *(float *)(param_1 + 0x23c) = *(float *)(iVar12 + 0x17c) * *(float *)(param_1 + 0x364);
    *(float *)(param_1 + 0x240) = fVar14 * (float)local_1c;
    *(float *)(param_1 + 0x244) = fVar3 * *(float *)(param_1 + 0x36c);
    *(float *)(param_1 + 0x248) = fVar2 * *(float *)(param_1 + 0x370);
    iVar12 = *(int *)(param_1 + 0x14);
    local_80 = *(float *)(iVar12 + 400);
    local_84 = *(byte **)(iVar12 + 0x18c);
    local_78 = *(float *)(iVar12 + 0x198);
    local_7c = *(float *)(iVar12 + 0x194);
    local_8 = local_78 * (float)local_84;
    local_34 = *(float *)(param_1 + 0x3a4);
    local_10 = local_78 * local_80;
    local_50 = *(float *)(param_1 + 0x3a8);
    local_14 = local_78 * local_7c;
    local_30 = *(float *)(param_1 + 0x3ac);
    local_38 = *(float *)(param_1 + 0x3b0);
    *(float *)(param_1 + 0x24c) = local_8 + local_34;
    *(float *)(param_1 + 0x250) = local_10 + local_50;
    *(float *)(param_1 + 0x254) = local_14 + local_30;
    *(float *)(param_1 + 600) = local_38;
    iVar12 = thunk_FUN_005a46c0();
    if (*(float *)(iVar12 + 0x1224) != 0.0) {
      local_28 = *(float *)(iVar12 + 0x123c);
      local_2c = *(float *)(iVar12 + 0x1238);
      local_24 = *(float *)(iVar12 + 0x1240);
      local_20 = *(float *)(iVar12 + 0x1244);
      thunk_FUN_00436d80((int)local_9c + 4);
      thunk_FUN_0042a810(&local_4c,param_1 + 0x21c);
      local_30 = *(float *)(iVar12 + 0x1230);
      local_8 = *(float *)(iVar12 + 0x1228);
      local_14 = -local_44 - local_8;
      local_34 = local_30 * local_14;
      if (local_34 <= 0.0) {
        local_34 = 0.0;
        local_1c = (byte *)0x0;
      }
      else {
        local_1c = (byte *)local_34;
        if (1.0 <= local_34) {
          local_1c = (byte *)0x3f800000;
        }
      }
      local_1c = (byte *)((float)local_1c * *(float *)(iVar12 + 0x1224));
      if (*(char *)(*(int *)(param_1 + 0x3dc) + 0x41) == '\x04') {
        fVar15 = (float10)thunk_FUN_004208c0(local_1c,0,0x3f800000);
        fVar15 = (float10)thunk_FUN_00420890((float)fVar15);
        local_1c = (byte *)(float)fVar15;
        local_2c = *(float *)(param_1 + 0x23c);
        local_28 = *(float *)(param_1 + 0x240);
        local_24 = *(float *)(param_1 + 0x244);
        local_14 = 1.0 - (float)local_1c;
        local_4c = local_14 * local_2c;
        local_48 = local_28 * local_14;
        local_44 = local_14 * local_24;
        *(float *)(param_1 + 0x23c) = local_4c;
        *(float *)(param_1 + 0x240) = local_48;
        *(float *)(param_1 + 0x244) = local_44;
        thunk_FUN_0058b810(local_14);
      }
      else {
        local_4c = *(float *)(param_1 + 0x24c);
        local_48 = *(float *)(param_1 + 0x250);
        local_44 = *(float *)(param_1 + 0x254);
        puVar7 = (undefined4 *)thunk_FUN_004a2560(&local_bc,local_1c,&local_4c,&local_2c);
        *(undefined4 *)(param_1 + 0x24c) = *puVar7;
        *(undefined4 *)(param_1 + 0x250) = puVar7[1];
        *(undefined4 *)(param_1 + 0x254) = puVar7[2];
        fVar15 = (float10)thunk_FUN_004208c0(*(undefined4 *)(param_1 + 600),0,0x3f800000);
        fVar15 = (float10)thunk_FUN_00420890((float)fVar15);
        local_1c = (byte *)(float)fVar15;
        *(byte **)(param_1 + 600) = local_1c;
        local_4c = *(float *)(param_1 + 0x23c);
        local_48 = *(float *)(param_1 + 0x240);
        local_44 = *(float *)(param_1 + 0x244);
        puVar7 = (undefined4 *)thunk_FUN_004a2560(&local_bc,local_1c,&local_4c,&local_2c);
        *(undefined4 *)(param_1 + 0x23c) = *puVar7;
        *(undefined4 *)(param_1 + 0x240) = puVar7[1];
        *(undefined4 *)(param_1 + 0x244) = puVar7[2];
      }
    }
    pcVar5 = *(code **)(*(int *)(param_1 + 0x14) + 0x108);
    if (pcVar5 != (code *)0x0) {
      (*pcVar5)(param_1,0,0);
    }
    thunk_FUN_0042a680(param_1 + 0x68);
    if ((*(float *)(*(int *)(param_1 + 0x3dc) + 0x8c) != 0.0) && (local_15 == '\0')) {
      pfVar9 = (float *)thunk_FUN_0058c7b0(&local_4c);
      local_2c = *pfVar9 - local_64;
      local_28 = pfVar9[1] - local_60;
      local_24 = pfVar9[2] - local_5c;
      thunk_FUN_0042d910(&local_4c,&local_2c);
      local_14 = *(float *)(*(int *)(param_1 + 0x3dc) + 0x8c);
      local_2c = local_14 * local_4c;
      local_28 = local_48 * local_14;
      local_24 = local_14 * local_44;
      uVar8 = thunk_FUN_00516e70(local_17c,&local_2c);
      thunk_FUN_00436c40(uVar8);
      uVar8 = ici(local_17c,param_1 + 0x28);
      thunk_FUN_0042a680(uVar8);
    }
    if (*(int **)(param_1 + 0x440) != (int *)0x0) {
      (**(code **)(**(int **)(param_1 + 0x440) + 0x30))
                (*(undefined4 *)(param_1 + 0x23c),*(undefined4 *)(param_1 + 0x240),
                 *(undefined4 *)(param_1 + 0x244),*(undefined4 *)(param_1 + 0x248),0x3c23d70a,0);
      local_84 = *(byte **)(param_1 + 0x24c);
      local_80 = *(float *)(param_1 + 0x250);
      local_7c = *(float *)(param_1 + 0x254);
      local_14 = *(float *)(param_1 + 600);
      thunk_FUN_0058b6a0(local_14);
      (**(code **)(**(int **)(param_1 + 0x440) + 0x34))
                (local_84,local_80,local_7c,local_78,0x3c23d70a,0);
      (**(code **)(**(int **)(param_1 + 0x440) + 0x28))(param_2);
    }
    if (*(char *)(*(int *)(param_1 + 0x3dc) + 0x3c) == '\x02') {
      DAT_00c7c49c = 1;
    }
  }
  fVar14 = *(float *)(param_1 + 0x458);
  if (((!NAN(fVar14) && 1.0 < fVar14 != (fVar14 == 1.0)) &&
      (*(float *)(*(int *)(param_1 + 0x3dc) + 0x60) != 0.0)) &&
     ((~*(uint *)(param_1 + 0x24) & 0x800) != 0)) {
    local_3b = '\x01';
  }
  local_15 = 0;
  if (*(char *)(*(int *)(param_1 + 0x14) + 0x1dc) != '\0') {
    *(undefined1 *)(param_1 + 0x425) = 0;
  }
  if ((*(char *)(param_1 + 0x425) != '\0') && (*(char *)(param_1 + 0x424) == '\0')) {
    local_30 = ABS(*(float *)(*(int *)(param_1 + 0x3dc) + 0x100));
    local_9c = 0.0;
    if (local_30 != 0.0) {
      local_50 = 0.0;
      local_1c = (byte *)(param_1 + 0x3e6);
      do {
        indice_3_contient_count =
             (char *)(*(int *)(*(int *)(param_1 + 0x3dc) + 0x104) + (int)local_50);
        if ((indice_3_contient_count[2] != '\0') &&
           ((indice_3_contient_count[2] != -1 || (*(float *)(indice_3_contient_count + 0x24) != 0.0)
            ))) {
          if (0x17f < (uint)local_50) break;
          local_14 = param_2 + *(float *)(local_1c + 2);
          count_byte = false;
          *(float *)(local_1c + 2) = local_14;
          local_95 = indice_3_contient_count[2];
          if ((-1 < indice_3_contient_count[1]) && ((*local_1c == 0xff || (*local_1c < local_95))))
          {
            cVar6 = indice_3_contient_count[4];
            if (cVar6 == '\x02') {
              if (1.0 < *(float *)(param_1 + 0x458) != (*(float *)(param_1 + 0x458) == 1.0)) {
                *local_1c = local_95;
                count_byte = true;
              }
            }
            else if (cVar6 == '\x01') {
              if (1.0 < *(float *)(param_1 + 0x458) != (*(float *)(param_1 + 0x458) == 1.0)) {
                *local_1c = local_95;
              }
              if (local_51 != '\0') {
                count_byte = true;
              }
            }
            else if (cVar6 == '\0') {
              fVar14 = *(float *)(param_1 + 0x448);
              fVar3 = *(float *)(indice_3_contient_count + 0x20);
              if (local_1c[-2] == 0) {
                if (fVar3 < fVar14 != (fVar3 == fVar14)) {
                  local_1c[-2] = 1;
LAB_005918b9:
                  local_1c[2] = 0;
                  local_1c[3] = 0;
                  local_1c[4] = 0;
                  local_1c[5] = 0;
                  count_byte = true;
                }
              }
              else if ((fVar3 < fVar14 != (fVar3 == fVar14)) &&
                      (*(float *)(indice_3_contient_count + 0x24) < local_14 !=
                       (*(float *)(indice_3_contient_count + 0x24) == local_14))) goto LAB_005918b9;
            }
            local_3a = 0;
            if (count_byte) {
              local_74 = *(float *)(param_1 + 0x1ec);
              local_70 = *(float *)(param_1 + 0x1f0);
              local_6c = *(float *)(param_1 + 500);
              local_68 = *(undefined4 *)(param_1 + 0x1f8);
              local_9 = 0;
              if (indice_3_contient_count[3] != '\0') {
                do {
                  local_39 = *local_1c;
                  local_38 = *(float *)(param_1 + 0x474);
                  fVar14 = 0.0;
                  local_10 = 0.0;
                  local_34 = 0.0;
                  if ((indice_3_contient_count[5] == '\x02') && (*(int *)(param_1 + 0x440) != 0)) {
                    local_34 = 4.2039e-45;
                    iVar12 = thunk_FUN_00633930(indice_3_contient_count + 0x10);
                    if (iVar12 != 0) {
                      local_74 = *(float *)(iVar12 + 0x40);
                      fVar14 = (float)(iVar12 + 0x10);
                      local_70 = *(float *)(iVar12 + 0x44);
                      local_6c = *(float *)(iVar12 + 0x48);
                      local_2c = local_74;
                      local_28 = local_70;
                      local_24 = local_6c;
                      local_10 = fVar14;
                    }
                  }
                  if (*indice_3_contient_count == '\0') {
                    local_74 = *(float *)(param_1 + 0x21c);
                    local_70 = *(float *)(param_1 + 0x220);
                    local_6c = *(float *)(param_1 + 0x224);
                    if ((indice_3_contient_count[5] != '\x03') &&
                       (indice_3_contient_count[5] == '\x01')) {
                      iVar12 = *(int *)(param_1 + 0x14);
                      if (local_95 == 0xfe) {
                        cVar6 = (**(code **)(iVar12 + 0x1cc))
                                          (3,iVar12,param_1,0,(uint)local_39 + (uint)local_9,
                                           &local_38);
                        local_3a = cVar6 == '\0';
                      }
                      else {
                        (**(code **)(iVar12 + 0x1cc))
                                  (2,iVar12,param_1,0,(uint)local_39 + (uint)local_9,&local_38);
                      }
                    }
                    thunk_FUN_0058f770((int)indice_3_contient_count[1],&local_74,0,param_1 + 0x20c,
                                       param_1,(uint)local_39 + (uint)local_9,local_38,0,0,fVar14);
                  }
                  else if (*indice_3_contient_count == '\x01') {
                    if (indice_3_contient_count[5] == '\x01') {
                      (**(code **)(*(int *)(param_1 + 0x14) + 0x1cc))
                                (2,*(int *)(param_1 + 0x14),param_1,0,(uint)local_39 + (uint)local_9
                                 ,&local_38);
                    }
                    local_74 = *(float *)(param_1 + 0x21c);
                    local_70 = *(float *)(param_1 + 0x220);
                    local_6c = *(float *)(param_1 + 0x224);
                    iVar12 = thunk_FUN_0058cb00();
                    if ((iVar12 != 0) &&
                       ((uint)(int)indice_3_contient_count[1] <
                        (*(uint *)(*(int *)(param_1 + 0x3e0) + 0x18) & 0x7fffffff))) {
                      thunk_FUN_0058ff10(iVar12,*(undefined4 *)(extraout_ECX + 0x1d0),&local_74,
                                         extraout_ECX + 0x84,extraout_ECX + 0x34,
                                         *(undefined4 *)
                                          (*(int *)(*(int *)(param_1 + 0x3e0) + 0x1c) + 0x20 +
                                          indice_3_contient_count[1] * 0x24),extraout_ECX,0,
                                         *(undefined4 *)(extraout_ECX + 0x1c8),local_38,local_10,
                                         (uint)local_34 & 0xffff,0);
                    }
                  }
                  local_9 = local_9 + 1;
                } while (local_9 < (byte)indice_3_contient_count[3]);
              }
              *local_1c = *local_1c + 1;
              if (local_3a != 0) goto LAB_00591b04;
            }
            if (indice_3_contient_count[2] == 0xff) {
              if ((*(byte *)(param_1 + 0x1c) & 1) != 0) {
LAB_00591afe:
                local_15 = 1;
              }
            }
            else if (*local_1c < (byte)indice_3_contient_count[2]) goto LAB_00591afe;
          }
        }
LAB_00591b04:
        local_50 = (float)((int)local_50 + 0x30);
        local_1c = local_1c + 8;
        local_9c = (float)((int)local_9c + 1);
      } while ((uint)local_9c < (uint)local_30);
    }
  }
  pcVar5 = *(code **)(*(int *)(param_1 + 0x14) + 0x104);
  if (pcVar5 != (code *)0x0) {
    (*pcVar5)(param_1,*(undefined4 *)(param_1 + 0x474),0);
  }
  if (((((*(uint *)(param_1 + 0x24) & 0x200) != 0) &&
       (iVar12 = *(int *)(param_1 + 0xc), iVar12 != 0)) && (*(char *)(iVar12 + 0x425) == '\0')) &&
     ((~*(uint *)(iVar12 + 0x1c) & 1) != 0)) {
    *(uint *)(param_1 + 0x1c) = *(uint *)(param_1 + 0x1c) & 0xfffffffe;
    local_3b = '\x01';
    local_15 = 0;
  }
  *(char *)(param_1 + 0x425) = local_15;
  local_9 = '\0';
  if (local_3b == '\0') {
    iVar12 = *(int *)(param_1 + 0x3dc);
    if (((*(float *)(iVar12 + 0x60) == 0.0) ||
        (*(float *)(param_1 + 0x448) < *(float *)(iVar12 + 0x60))) &&
       ((*(float *)(iVar12 + 0x60) != 0.0 ||
        (local_9 = '\0', *(char *)(*(int *)(param_1 + 0x14) + 0x1dc) == '\0')))) {
      local_9 = '\x01';
    }
    if (((*(char *)(*(int *)(param_1 + 0x14) + 0x1dc) == '\0') &&
        (*(float *)(iVar12 + 0x60) < *(float *)(param_1 + 0x448) !=
         (*(float *)(iVar12 + 0x60) == *(float *)(param_1 + 0x448)))) &&
       ((*(uint *)(param_1 + 0x24) & 0x800) != 0)) {
      *(undefined4 *)(param_1 + 0x458) = 0;
      *(undefined4 *)(param_1 + 0x448) = 0;
      local_9 = '\x01';
      thunk_FUN_00598ef0();
      thunk_FUN_00598c40();
      thunk_FUN_00598c40();
      thunk_FUN_00598c40();
      thunk_FUN_00598c40();
      thunk_FUN_00598c40();
      thunk_FUN_00598c40();
      interpolation(param_1 + 0x294,0,param_1 + 0x25c,*(int *)(param_1 + 0x3dc) + 0xd0);
      interpolation(param_1 + 0x314,0,param_1 + 0x2dc,*(int *)(param_1 + 0x3dc) + 0xd8);
      interpolation(param_1 + 0x2d4,0,param_1 + 0x29c,*(int *)(param_1 + 0x3dc) + 0xe0);
      interpolation(param_1 + 0x354,0,param_1 + 0x31c,*(int *)(param_1 + 0x3dc) + 0xe8);
      if ((~*(uint *)(param_1 + 0x1c) & 0x200) != 0) {
        interpolation(param_1 + 0x394,0,param_1 + 0x35c,*(int *)(param_1 + 0x3dc) + 0xf0);
        interpolation(param_1 + 0x3d4,0,param_1 + 0x39c,*(int *)(param_1 + 0x3dc) + 0xf8);
      }
    }
  }
  in_EAX = *(uint *)(param_1 + 0x3dc);
  cVar6 = *(char *)(in_EAX + 0x40);
  if (cVar6 == '\x0f') {
    if (*(int *)(param_1 + 0x484) != 0) {
      in_EAX = thunk_FUN_0059ec70(param_2,*(int *)(param_1 + 0xc) + 0x28,param_1 + 0x28,
                                  *(undefined4 *)(in_EAX + 0x50),*(undefined4 *)(in_EAX + 0x54),
                                  *(undefined4 *)(in_EAX + 0x58),*(undefined4 *)(in_EAX + 0x5c),
                                  *(undefined4 *)(param_1 + 0x20c),*(undefined4 *)(in_EAX + 0xb8),
                                  *(undefined4 *)(in_EAX + 200),
                                  (byte)(*(uint *)(in_EAX + 0x34) >> 2) & 1);
    }
  }
  else if (((cVar6 == '\x14') || (cVar6 == '\x15')) &&
          (in_EAX = *(uint *)(param_1 + 0x480), in_EAX != 0)) {
    if (local_9 == '\0') {
      *(undefined1 *)(in_EAX + 0x1d) = 0;
    }
    iVar12 = *(int *)(param_1 + 0x480);
    if ((-1 < *(int *)(iVar12 + 4)) && (*(char *)(iVar12 + 0x1f) == '\0')) {
      iVar4 = *(int *)(param_1 + 0x3dc);
      local_4c = *(float *)(iVar4 + 0xb0);
      local_48 = *(float *)(iVar4 + 0xb4);
      local_44 = *(float *)(iVar4 + 0xb8);
      local_40 = 0x3f800000;
      local_2c = local_4c;
      local_28 = local_48;
      local_24 = local_44;
      thunk_FUN_0045c3c0(&local_ac,&local_4c);
      local_4c = *(float *)(iVar4 + 0xc0);
      local_48 = *(float *)(iVar4 + 0xc4);
      local_44 = *(float *)(iVar4 + 200);
      local_40 = 0x3f800000;
      local_2c = local_4c;
      local_28 = local_48;
      local_24 = local_44;
      thunk_FUN_0045c3c0(&local_84,&local_4c);
      if ((-1 < *(int *)(iVar12 + 4)) && (*(char *)(iVar12 + 0x1d) != '\0')) {
        local_bc = local_84;
        local_b8 = local_80;
        local_b4 = local_7c;
        local_94 = local_ac;
        local_90 = local_a8;
        local_8c = local_a4;
        thunk_FUN_0059e260(param_2,local_ac,local_a8,local_a4,local_88,local_84,local_80,local_7c,
                           local_b0,*(undefined4 *)(iVar4 + 0x50),*(undefined4 *)(iVar4 + 0x54),
                           *(undefined4 *)(iVar4 + 0x58),*(undefined4 *)(iVar4 + 0x5c),
                           *(undefined4 *)(param_1 + 0x23c),*(undefined4 *)(param_1 + 0x240),
                           *(undefined4 *)(param_1 + 0x244),*(undefined4 *)(param_1 + 0x248),
                           *(undefined4 *)(iVar4 + 0x68),*(undefined2 *)(iVar4 + 0x48));
      }
      local_bc = local_84;
      local_b8 = local_80;
      local_b4 = local_7c;
      local_94 = local_ac;
      local_90 = local_a8;
      local_8c = local_a4;
      uVar10 = thunk_FUN_0059f280(param_2,local_ac,local_a8,local_a4,local_88,local_84,local_80,
                                  local_7c,local_b0);
      return uVar10 & 0xffffff00;
    }
  }
  if (local_9 == '\0') {
    *(uint *)(param_1 + 0x1c) = *(uint *)(param_1 + 0x1c) & 0xfffffffe;
  }
LAB_00591de2:
  return in_EAX & 0xffffff00;
}


- Il faut implémenter ce qu'on a appelé "VelY Init"

C'est lu ici : (fVar1 = (float)iVar4;)
float10 FUN_0044cd20(float param_1,float param_2)

{
  float fVar1;
  uint uVar2;
  uint uVar3;
  int iVar4;
  
  uVar2 = DAT_00c7be68 ^ 0xbc602f;
  uVar3 = uVar2 * -0x61c88647;
  DAT_00c7be68 = DAT_00c7be68 + 1;
  uVar3 = uVar3 >> 0x1a ^ uVar2 * -0x722191c0 ^ uVar3;
  uVar2 = uVar3 * -0x61c88647;
  iVar4 = (uVar2 >> 0xc ^ uVar3 * -0x3910c8e0) + uVar2;
  fVar1 = (float)iVar4;
  if (iVar4 < 0) {
    fVar1 = fVar1 + 4.2949673e+09;
  }
  return (float10)(fVar1 * (param_2 - param_1) * 2.3283064e-10 + param_1);
}

qui est utilisé par là : 

      param_1[0x119] = 0x3f800000;
      fVar10 = (float10)thunk_FUN_0044cd20(*(undefined4 *)((int)param_6 + 0x70),
                                           *(undefined4 *)((int)param_6 + 0x74));
      param_1[0x11a] = (float)fVar10;
      local_4dc[0x10] = *(float *)((int)param_6 + 0x60);
      if (local_4dc[0x10] == 0.0) {
        param_1[0x117] = 0;
      }
      else {
        param_1[0x116] = 0;
        param_1[0x117] = 1.0 / local_4dc[0x10];
      }
      if ((param_1[8] & 0x10000) != 0) {
        if (param_13 == (float *)0x0) {
          if (param_1[3] == 0) goto LAB_00592842;
          thunk_FUN_0042a680(param_1[3] + 0xe8);
        }
        else {
          pfVar8 = local_4dc;
          for (iVar7 = 0x10; iVar7 != 0; iVar7 = iVar7 + -1) {
            *pfVar8 = *param_13;
            param_13 = param_13 + 1;
            pfVar8 = pfVar8 + 1;
          }
          local_4dc[0xc] = 0.0;
          local_4dc[0xd] = 0.0;
          local_4dc[0xe] = 0.0;
          thunk_FUN_0058d490(local_4dc,0);
          param_1[0x2a] = local_4dc[0];
          param_1[0x2b] = local_4dc[1];
          param_1[0x2c] = local_4dc[2];
          param_1[0x2d] = local_4dc[3];
          param_1[0x2e] = local_4dc[4];
          param_1[0x2f] = local_4dc[5];
          param_1[0x30] = local_4dc[6];
          param_1[0x31] = local_4dc[7];
          param_1[0x32] = local_4dc[8];
          param_1[0x33] = local_4dc[9];
          param_1[0x34] = local_4dc[10];
          param_1[0x35] = local_4dc[0xb];
          param_1[0x36] = local_4dc[0xc];
          param_1[0x37] = local_4dc[0xd];
          param_1[0x38] = local_4dc[0xe];
          param_1[0x39] = local_4dc[0xf];
          param_6 = local_498;
        }
        iVar7 = param_1[3];
        param_1[0x73] = *(undefined4 *)(iVar7 + 0x1fc);
        param_1[0x74] = *(undefined4 *)(iVar7 + 0x200);
        param_1[0x75] = *(undefined4 *)(iVar7 + 0x204);
      }
LAB_00592842:
      if (((param_1[8] & 0x20000) != 0) && (iVar7 = param_1[3], iVar7 != 0)) {
        param_1[0x77] = *(undefined4 *)(iVar7 + 0x20c);
        param_1[0x78] = *(undefined4 *)(iVar7 + 0x210);
        param_1[0x79] = *(undefined4 *)(iVar7 + 0x214);
      }
      if (param_5 == 0) {
        param_1[8] = param_1[8] | 0x1b000;
      }
      if (param_1[3] != 0) {
        piVar1 = (int *)(param_1[3] + 0x470);
        *piVar1 = *piVar1 + 1;
      }
      if ((*(char *)(param_1[0xf7] + 0x40) == '\x14') || (*(char *)(param_1[0xf7] + 0x40) == '\x15')
         ) {
        if (*(int *)(DAT_00c7c498 + 0xa1404) == 0) {
          iVar7 = 0;
        }
        else {
          iVar7 = thunk_FUN_005995d0();
        }
        param_1[0x120] = iVar7;
        if (iVar7 != 0) {
          uVar2 = *(uint *)(param_1[0xf7] + 0x30);
          cVar4 = *(char *)(param_1[0xf7] + 0x40);
          *(undefined4 *)(iVar7 + 4) = 0;
          *(bool *)(iVar7 + 0x1c) = cVar4 == '\x15';
          *(byte *)(iVar7 + 0x1e) = (byte)(uVar2 >> 7) & 1;
        }
        param_1[9] = param_1[9] | 0x200;
      }
      if (*(char *)(param_1[0xf7] + 0x40) == '\x0f') {
        uVar5 = thunk_FUN_0059eba0();
        param_1[0x121] = uVar5;
      }
      local_4dc[0] = 1.0;
      local_4dc[5] = 1.0;
      local_4dc[10] = 1.0;
      local_4dc[0xf] = 1.0;
      local_4dc[1] = 0.0;
      local_4dc[2] = 0.0;
      local_4dc[3] = 0.0;
      local_4dc[4] = 0.0;
      local_4dc[6] = 0.0;
      local_4dc[7] = 0.0;
      local_4dc[8] = 0.0;
      local_4dc[9] = 0.0;
      local_4dc[0xb] = 0.0;
      local_4dc[0xc] = 0.0;
      local_4dc[0xd] = 0.0;
      local_4dc[0xe] = 0.0;
      thunk_FUN_00436c40(local_4dc);
      param_1[0x5a] = local_51c;
      param_1[0x5b] = local_518;
      param_1[0x5c] = local_514;
      param_1[0x5d] = local_510;
      param_1[0x5e] = local_50c;
      param_1[0x5f] = local_508;
      param_1[0x60] = local_504;
      param_1[0x61] = local_500;
      param_1[0x62] = local_4fc;
      param_1[99] = local_4f8;
      param_1[100] = local_4f4;
      param_1[0x65] = local_4f0;
      param_1[0x66] = local_4ec;
      param_1[0x67] = local_4e8;
      param_1[0x68] = local_4e4;
      param_1[0x69] = local_4e0;
      iVar7 = param_1[0xf7];
      if ((((*(float *)(iVar7 + 0xa0) == 0.0) && (*(float *)(iVar7 + 0xa4) == 0.0)) &&
          (*(float *)(iVar7 + 0xa8) == 0.0)) && (*(float *)(iVar7 + 0xac) == 0.0)) {
        param_1[0x66] = 0;
        param_1[0x67] = 0;
        param_1[0x68] = 0;
        param_1[0x69] = 0;
      }
      else {
        local_528 = *(float *)(iVar7 + 0xa4);
        local_524 = *(float *)(iVar7 + 0xa8);
        local_52c = *(undefined4 *)(iVar7 + 0xa0);
        local_4dc[0x10] = local_524;
        local_498 = local_528;
        thunk_FUN_0044d1f0(local_53c,&local_52c);
        local_52c = 0;
        local_528 = *(float *)(param_1[0xf7] + 0xac);
        local_524 = 0.0;
        uVar5 = thunk_FUN_00516e70(local_4dc,&local_52c);
        puVar11 = local_57c;
        thunk_FUN_0044ee50(local_5bc,local_53c,puVar11,uVar5);
        uVar5 = thunk_FUN_004364d0(puVar11,uVar5);
        thunk_FUN_00436c40(uVar5);
        param_1[0x5a] = local_51c;
        param_1[0x5b] = local_518;
        param_1[0x5c] = local_514;
        param_1[0x5d] = local_510;
        param_1[0x5e] = local_50c;
        param_1[0x5f] = local_508;
        param_1[0x60] = local_504;
        param_1[0x61] = local_500;
        param_1[0x62] = local_4fc;
        param_1[99] = local_4f8;
        param_1[100] = local_4f4;
        param_1[0x65] = local_4f0;
        param_1[0x66] = local_4ec;
        param_1[0x67] = local_4e8;
        param_1[0x68] = local_4e4;
        param_1[0x69] = local_4e0;
      }


- d04[9] vaut 2.5 : à quoi sert-il ?

C'est pareil utilisé dans :
float10 FUN_0044cd20(float param_1,float param_2)

{
  float fVar1;
  uint uVar2;
  uint uVar3;
  int iVar4;
  
  uVar2 = DAT_00c7be68 ^ 0xbc602f;
  uVar3 = uVar2 * -0x61c88647;
  DAT_00c7be68 = DAT_00c7be68 + 1;
  uVar3 = uVar3 >> 0x1a ^ uVar2 * -0x722191c0 ^ uVar3;
  uVar2 = uVar3 * -0x61c88647;
  iVar4 = (uVar2 >> 0xc ^ uVar3 * -0x3910c8e0) + uVar2;
  fVar1 = (float)iVar4;
  if (iVar4 < 0) {
    fVar1 = fVar1 + 4.2949673e+09;
  }
  return (float10)(fVar1 * (param_2 - param_1) * 2.3283064e-10 + param_1);
}

il est utilisé dans return (float10)(fVar1 * (param_2 - param_1) * 2.3283064e-10 + param_1); je crois que c'est param_2 mais pas sûr (ebp+C)

appelée ici :
      fVar10 = (float10)thunk_FUN_0044cd20(*(undefined4 *)((int)param_6 + 0x70),
                                           *(undefined4 *)((int)param_6 + 0x74));
      param_1[0x11a] = (float)fVar10;
      local_4dc[0x10] = *(float *)((int)param_6 + 0x60);
      if (local_4dc[0x10] == 0.0) {
        param_1[0x117] = 0;
      }
      else {
        param_1[0x116] = 0;
        param_1[0x117] = 1.0 / local_4dc[0x10];
      }
      if ((param_1[8] & 0x10000) != 0) {
        if (param_13 == (float *)0x0) {
          if (param_1[3] == 0) goto LAB_00592842;
          thunk_FUN_0042a680(param_1[3] + 0xe8);
        }
        else {
          pfVar8 = local_4dc;
          for (iVar7 = 0x10; iVar7 != 0; iVar7 = iVar7 + -1) {
            *pfVar8 = *param_13;
            param_13 = param_13 + 1;
            pfVar8 = pfVar8 + 1;
          }
          local_4dc[0xc] = 0.0;
          local_4dc[0xd] = 0.0;
          local_4dc[0xe] = 0.0;
          thunk_FUN_0058d490(local_4dc,0);
          param_1[0x2a] = local_4dc[0];
          param_1[0x2b] = local_4dc[1];
          param_1[0x2c] = local_4dc[2];
          param_1[0x2d] = local_4dc[3];
          param_1[0x2e] = local_4dc[4];
          param_1[0x2f] = local_4dc[5];
          param_1[0x30] = local_4dc[6];
          param_1[0x31] = local_4dc[7];
          param_1[0x32] = local_4dc[8];
          param_1[0x33] = local_4dc[9];
          param_1[0x34] = local_4dc[10];
          param_1[0x35] = local_4dc[0xb];
          param_1[0x36] = local_4dc[0xc];
          param_1[0x37] = local_4dc[0xd];
          param_1[0x38] = local_4dc[0xe];
          param_1[0x39] = local_4dc[0xf];
          param_6 = local_498;
        }
        iVar7 = param_1[3];
        param_1[0x73] = *(undefined4 *)(iVar7 + 0x1fc);
        param_1[0x74] = *(undefined4 *)(iVar7 + 0x200);
        param_1[0x75] = *(undefined4 *)(iVar7 + 0x204);
      }
LAB_00592842:
      if (((param_1[8] & 0x20000) != 0) && (iVar7 = param_1[3], iVar7 != 0)) {
        param_1[0x77] = *(undefined4 *)(iVar7 + 0x20c);
        param_1[0x78] = *(undefined4 *)(iVar7 + 0x210);
        param_1[0x79] = *(undefined4 *)(iVar7 + 0x214);
      }
      if (param_5 == 0) {
        param_1[8] = param_1[8] | 0x1b000;
      }
      if (param_1[3] != 0) {
        piVar1 = (int *)(param_1[3] + 0x470);
        *piVar1 = *piVar1 + 1;
      }
      if ((*(char *)(param_1[0xf7] + 0x40) == '\x14') || (*(char *)(param_1[0xf7] + 0x40) == '\x15')
         ) {
        if (*(int *)(DAT_00c7c498 + 0xa1404) == 0) {
          iVar7 = 0;
        }
        else {
          iVar7 = thunk_FUN_005995d0();
        }
        param_1[0x120] = iVar7;
        if (iVar7 != 0) {
          uVar2 = *(uint *)(param_1[0xf7] + 0x30);
          cVar4 = *(char *)(param_1[0xf7] + 0x40);
          *(undefined4 *)(iVar7 + 4) = 0;
          *(bool *)(iVar7 + 0x1c) = cVar4 == '\x15';
          *(byte *)(iVar7 + 0x1e) = (byte)(uVar2 >> 7) & 1;
        }
        param_1[9] = param_1[9] | 0x200;
      }
      if (*(char *)(param_1[0xf7] + 0x40) == '\x0f') {
        uVar5 = thunk_FUN_0059eba0();
        param_1[0x121] = uVar5;
      }
      local_4dc[0] = 1.0;
      local_4dc[5] = 1.0;
      local_4dc[10] = 1.0;
      local_4dc[0xf] = 1.0;
      local_4dc[1] = 0.0;
      local_4dc[2] = 0.0;
      local_4dc[3] = 0.0;
      local_4dc[4] = 0.0;
      local_4dc[6] = 0.0;
      local_4dc[7] = 0.0;
      local_4dc[8] = 0.0;
      local_4dc[9] = 0.0;
      local_4dc[0xb] = 0.0;
      local_4dc[0xc] = 0.0;
      local_4dc[0xd] = 0.0;
      local_4dc[0xe] = 0.0;
      thunk_FUN_00436c40(local_4dc);
      param_1[0x5a] = local_51c;
      param_1[0x5b] = local_518;
      param_1[0x5c] = local_514;
      param_1[0x5d] = local_510;
      param_1[0x5e] = local_50c;
      param_1[0x5f] = local_508;
      param_1[0x60] = local_504;
      param_1[0x61] = local_500;
      param_1[0x62] = local_4fc;
      param_1[99] = local_4f8;
      param_1[100] = local_4f4;
      param_1[0x65] = local_4f0;
      param_1[0x66] = local_4ec;
      param_1[0x67] = local_4e8;
      param_1[0x68] = local_4e4;
      param_1[0x69] = local_4e0;

- Pourquoi on ne voit pas [1] houbutu, alors qu'en jeu on le voit

Pour "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects\system\mk_talk.eff":
- les tailles de "TALK" et de la croix rouge sont trop petites

Pour "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects\system\rain00.eff": 
- Preview complètement invisible dans tous les cas!! (le crop R est bizarre, il vaut 80, donc je pense que ça le place bizarrement dans la preview.)

Pour "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects\system\runhorse.eff":

- Il est complètement invisible mais son crop est raisonnable...
on ne voit pas le cadre rouge sur la preview texture qui correponde à son crop. Il a un inherit à 0xA0

Pour "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects\system\gameover.eff": 
les lettres ok, mais ya une texture hors sujet en fond, et le game over semble derrière une aura noire au lieu d'être devant comme en jeu (je pense que le force billboard affiche même des textures qui sont complètement pas à afficher) Il faudrait idéalement réussir à distinguer les textures jamais affichées et celles affichées conditionnellement. Par exemple je note que root et [7] dans "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects_us\battle\mk_lp.eff" ont d02[0] à 9, et ne sont pas affichés, tandis que talk dans "C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\effects\system\mk_talk.eff" a d02[0] à 8 et il n'est pas non plus affiché, mais a vocation à être affiché.

