// cs1_editor.h — DLL C ABI pour l'editeur de maps (C#). Tout en RAM, pas de fichiers.
// Socle : charger un binaire de script -> modele Document -> enumerer fonctions
//         (par index/nom) -> resérialiser (recompile des pointeurs d'en-tete).
// Editing (add/remove/modify fonctions et blocs) : couches suivantes.
#ifndef CS1_EDITOR_H
#define CS1_EDITOR_H
#include "cs1_script_parser.h"
#include <vector>
#include <string>
#include <cstring>
#include <utility>

#if defined(_WIN32)
  #define CS1_API extern "C" __declspec(dllexport)
#else
  #define CS1_API extern "C"
#endif


namespace cs1ed {
using namespace cs1;
struct Elem { bool isData; uint8_t opcode; std::vector<uint8_t> raw;
               std::vector<std::pair<int,int>> jumps; }; // jumps: (pos dans raw, index element cible ; -1 = non resolu)
struct Func { std::string name; bool named; int type; std::vector<uint8_t> bytes; bool hasRawPtrs; long ostart;
              std::vector<Elem> model; bool decoded; };
struct Doc  { std::string scene; std::vector<Func> funcs; std::vector<uint8_t> ser; };

static inline uint32_t R32(const uint8_t*b,size_t p){return b[p]|(b[p+1]<<8)|(b[p+2]<<16)|((uint32_t)b[p+3]<<24);}
static inline int16_t  R16(const uint8_t*b,size_t p){return (int16_t)(b[p]|(b[p+1]<<8));}
static inline void P32(std::vector<uint8_t>&v,uint32_t x){v.push_back(x&0xff);v.push_back((x>>8)&0xff);v.push_back((x>>16)&0xff);v.push_back((x>>24)&0xff);}
static inline void P16(std::vector<uint8_t>&v,uint16_t x){v.push_back(x&0xff);v.push_back((x>>8)&0xff);}
}


// Ajoute 'delta' a chaque cible de saut op3/op5/op6 d'une fonction, en ne modifiant
// que les segments d'instructions (les chunks data sont laisses tels quels).
// Renvoie true si un chunk data est present (pointeurs eventuels NON relocalises -> a signaler).
static inline bool cs1_reloc_jumps(std::vector<uint8_t>& fb, long delta, long lo, long hi){
    using namespace cs1;
    if(delta==0) { /* rien a faire mais on detecte quand meme les chunks */ }
    uint8_t* b=fb.data(); long e=(long)fb.size();
    Cs1Seg segs[16384];
    int ns=cs1_parse_function(b,0,(uint32_t)e,segs,16384);
    bool hasData=false;
    auto add32=[&](long pos){ if(pos+4<=e){ uint32_t v=b[pos]|(b[pos+1]<<8)|(b[pos+2]<<16)|((uint32_t)b[pos+3]<<24);
        if((long)v<lo||(long)v>=hi) return;   // cible hors fonction = mal identifiee -> on ne touche pas
        v=(uint32_t)((long)v+delta); b[pos]=v&0xff;b[pos+1]=(v>>8)&0xff;b[pos+2]=(v>>16)&0xff;b[pos+3]=(v>>24)&0xff; } };
    for(int i=0;i<ns;i++){
        if(segs[i].kind==CS1_SEG_DATA){ hasData=true; continue; }
        long p=segs[i].start; uint8_t o=b[p]; int L=cs1_instr_length(b+p,e-p); if(L<=0)continue;
        if(o==3) add32(p+1);
        else if(o==5){ int el=cs1_expr(b+p+1,e-(p+1)); if(el>0) add32(p+1+el); }
        else if(o==6){ int el=cs1_expr(b+p+1,e-(p+1)); if(el>0){ long q=p+1+el; int cnt=b[q]; q++;
            for(int c=0;c<cnt;c++){ add32(q+2); q+=6; } add32(q); } }
    }
    return hasData;
}

// positions (dans le raw d'une instruction op3/5/6) des u32 cibles de saut.
static inline void cs1_jump_positions(const uint8_t* b, long len, std::vector<int>& out){
    using namespace cs1;
    if(len<1) return; uint8_t o=b[0];
    if(o==3){ if(len>=5) out.push_back(1); }
    else if(o==5){ int el=cs1_expr(b+1,len-1); if(el>0 && 1+el+4<=len) out.push_back(1+el); }
    else if(o==6){ int el=cs1_expr(b+1,len-1); if(el>0){ long q=1+el; if(q<len){ int cnt=b[q]; q++;
        for(int c=0;c<cnt;c++){ if(q+6<=len) out.push_back((int)q+2); q+=6; } if(q+4<=len) out.push_back((int)q); } } }
}
static inline uint32_t cs1_rd32(const uint8_t*b,int p){return b[p]|(b[p+1]<<8)|(b[p+2]<<16)|((uint32_t)b[p+3]<<24);}
static inline void cs1_wr32(uint8_t*b,int p,uint32_t v){b[p]=v&0xff;b[p+1]=(v>>8)&0xff;b[p+2]=(v>>16)&0xff;b[p+3]=(v>>24)&0xff;}

CS1_API cs1ed::Doc* cs1_doc_load(const uint8_t* data, int32_t len){
    using namespace cs1ed;
    if(!data||len<0x20) return nullptr;
    Doc* d=new Doc();
    uint32_t nb=R32(data,0x14), fnpos=R32(data,0x04), pa=R32(data,0x08);
    { size_t q=fnpos; while(q<(size_t)len&&data[q])q++; d->scene.assign((const char*)data+fnpos,q-fnpos); }
    for(uint32_t k=0;k<nb;k++){
        uint32_t st=R32(data,pa+4*k), en=(k<nb-1)?R32(data,pa+4*(k+1)):(uint32_t)len;
        int16_t np=R16(data,pa+4*nb+2*k);
        Func f; f.named=false;
        if(np>=0&&np<len){ size_t q=np; while(q<(size_t)len&&data[q])q++; f.name.assign((const char*)data+np,q-np); f.named=!f.name.empty(); }
        f.bytes.assign(data+st,data+en);
        f.type=cs1_classify_function(data,f.named?f.name.c_str():"",st,en);
        f.hasRawPtrs=false; f.ostart=(long)st; f.decoded=false;
        if(f.type==0||f.type==-1) f.hasRawPtrs=cs1_reloc_jumps(f.bytes, 0, (long)st, (long)st+(long)f.bytes.size());  // detection chunk seulement (delta 0)
        d->funcs.push_back(std::move(f));
    }
    return d;
}
CS1_API void    cs1_doc_free(cs1ed::Doc*d){ delete d; }
CS1_API int32_t cs1_doc_func_count(cs1ed::Doc*d){ return d?(int32_t)d->funcs.size():0; }
CS1_API const char* cs1_doc_scene_name(cs1ed::Doc*d){ return d?d->scene.c_str():nullptr; }
CS1_API const char* cs1_doc_func_name(cs1ed::Doc*d,int32_t i){ if(!d||i<0||i>=(int)d->funcs.size())return nullptr; return d->funcs[i].named?d->funcs[i].name.c_str():nullptr; }
CS1_API int32_t cs1_doc_func_type(cs1ed::Doc*d,int32_t i){ if(!d||i<0||i>=(int)d->funcs.size())return -2; return d->funcs[i].type; }
CS1_API int32_t cs1_doc_func_size(cs1ed::Doc*d,int32_t i){ if(!d||i<0||i>=(int)d->funcs.size())return -1; return (int32_t)d->funcs[i].bytes.size(); }
CS1_API const uint8_t* cs1_doc_func_bytes(cs1ed::Doc*d,int32_t i){ if(!d||i<0||i>=(int)d->funcs.size())return nullptr; return d->funcs[i].bytes.data(); }
CS1_API int32_t cs1_doc_func_has_raw_ptrs(cs1ed::Doc*d,int32_t i){ if(!d||i<0||i>=(int)d->funcs.size())return 0; return d->funcs[i].hasRawPtrs?1:0; }
CS1_API int32_t cs1_doc_index_by_name(cs1ed::Doc*d,const char*nm){ if(!d||!nm)return -1; for(size_t i=0;i<d->funcs.size();i++) if(d->funcs[i].named&&d->funcs[i].name==nm)return (int)i; return -1; }

// ---- edition au niveau fonction (couche 1). Retour: 0 = ok, <0 = erreur. ----
static inline int _cs1_classify_bytes(cs1ed::Func&f){
    return cs1::cs1_classify_function(f.bytes.data(), f.named?f.name.c_str():"", 0, (long)f.bytes.size());
}
CS1_API int32_t cs1_doc_func_rename(cs1ed::Doc*d,int32_t i,const char*name){
    if(!d||i<0||i>=(int)d->funcs.size())return -1;
    if(name&&name[0]){ d->funcs[i].name=name; d->funcs[i].named=true; }
    else { d->funcs[i].name.clear(); d->funcs[i].named=false; }
    d->funcs[i].type=_cs1_classify_bytes(d->funcs[i]);
    return 0;
}
CS1_API int32_t cs1_doc_func_remove(cs1ed::Doc*d,int32_t i){
    if(!d||i<0||i>=(int)d->funcs.size())return -1;
    d->funcs.erase(d->funcs.begin()+i); return 0;
}
CS1_API int32_t cs1_doc_func_set_bytes(cs1ed::Doc*d,int32_t i,const uint8_t*bytes,int32_t len){
    if(!d||i<0||i>=(int)d->funcs.size()||len<0||(len>0&&!bytes))return -1;
    d->funcs[i].bytes.assign(bytes,bytes+len);
    d->funcs[i].type=_cs1_classify_bytes(d->funcs[i]);
    d->funcs[i].hasRawPtrs=false; d->funcs[i].ostart=0; d->funcs[i].decoded=false; return 0;
}
// Insere une fonction a l'index 'at' (si at<0 ou >count : ajoute a la fin). name peut etre NULL.
CS1_API int32_t cs1_doc_func_insert(cs1ed::Doc*d,int32_t at,const char*name,const uint8_t*bytes,int32_t len){
    if(!d||len<0||(len>0&&!bytes))return -1;
    cs1ed::Func f; f.named=(name&&name[0]); if(f.named)f.name=name;
    f.bytes.assign(bytes,bytes+len); f.type=_cs1_classify_bytes(f); f.hasRawPtrs=false; f.ostart=0; f.decoded=false;
    if(at<0||at>(int)d->funcs.size())at=(int)d->funcs.size();
    d->funcs.insert(d->funcs.begin()+at,std::move(f)); return at;
}
// Deplace la fonction 'from' vers l'index 'to' (preserve l'ordre).
CS1_API int32_t cs1_doc_func_move(cs1ed::Doc*d,int32_t from,int32_t to){
    if(!d||from<0||from>=(int)d->funcs.size()||to<0||to>=(int)d->funcs.size())return -1;
    cs1ed::Func f=std::move(d->funcs[from]);
    d->funcs.erase(d->funcs.begin()+from);
    d->funcs.insert(d->funcs.begin()+to,std::move(f)); return 0;
}

// Resérialise le document (recompile en-tete + pointeurs). Le pointeur reste valide
// jusqu'au prochain appel a serialize/free sur ce Doc.
CS1_API const uint8_t* cs1_doc_serialize(cs1ed::Doc*d,int32_t* outlen){
    using namespace cs1ed;
    if(!d){ if(outlen)*outlen=0; return nullptr; }
    std::vector<uint8_t> h;
    std::vector<uint8_t> namebytes(d->scene.begin(),d->scene.end()); namebytes.push_back(0);
    uint32_t nsize=(uint32_t)namebytes.size(); uint32_t nb=(uint32_t)d->funcs.size();
    uint32_t names_len=0; for(auto&f:d->funcs) names_len+=(uint32_t)f.name.size()+1;
    uint32_t ptr_area=0x20+nsize;
    uint32_t names_pos_area=ptr_area+nb*4;
    uint32_t funcs_meta_end=names_pos_area+nb*2+names_len;   // avant padding
    // en-tete (8 ints)
    P32(h,0x20); P32(h,0x20); P32(h,ptr_area); P32(h,nb*4);
    P32(h,names_pos_area); P32(h,nb); P32(h,funcs_meta_end); P32(h,0xABCDEF00);
    for(uint8_t c:namebytes) h.push_back(c);
    // table des pointeurs (adresses de fonction) — recalculees plus bas
    // d'abord on connait l'offset de debut de la section fonctions (apres padding)
    // padding : multiple 4, ou 0x10 si 1er nom commence par '_'
    int mult=4; if(nb>0 && !d->funcs[0].name.empty() && d->funcs[0].name[0]=='_') mult=0x10;
    uint32_t pad = ((funcs_meta_end + mult - 1)/mult)*mult - funcs_meta_end;
    uint32_t funcs_start = funcs_meta_end + pad;
    // adresses de fonction
    std::vector<uint32_t> addrs(nb); uint32_t acc=funcs_start;
    for(uint32_t k=0;k<nb;k++){ addrs[k]=acc; acc+=(uint32_t)d->funcs[k].bytes.size(); }
    for(uint32_t k=0;k<nb;k++) P32(h,addrs[k]);
    // pre-calcule les octets de fonction avec cibles ABSOLUES (delta = +addrs[k])
    std::vector<std::vector<uint8_t>> outfb(nb);
    for(uint32_t k=0;k<nb;k++){ outfb[k]=d->funcs[k].bytes;
        long delta=(long)addrs[k]-d->funcs[k].ostart;
        if(delta!=0 && (d->funcs[k].type==0||d->funcs[k].type==-1)) cs1_reloc_jumps(outfb[k], delta, d->funcs[k].ostart, d->funcs[k].ostart+(long)outfb[k].size()); }
    // section noms-positions + noms
    std::vector<uint8_t> namesblob; uint32_t noff=0;
    for(uint32_t k=0;k<nb;k++){
        P16(h,(uint16_t)(names_pos_area+nb*2+noff));
        for(char c:d->funcs[k].name) namesblob.push_back((uint8_t)c);
        namesblob.push_back(0); noff+=(uint32_t)d->funcs[k].name.size()+1;
    }
    for(uint8_t c:namesblob) h.push_back(c);
    for(uint32_t i=0;i<pad;i++) h.push_back(0);
    for(uint32_t k=0;k<nb;k++) for(uint8_t c:outfb[k]) h.push_back(c);
    d->ser=std::move(h);
    if(outlen)*outlen=(int32_t)d->ser.size();
    return d->ser.data();
}

// ============ COUCHE 2 : modele d'instructions symbolique ============
// Decode la fonction i en elements editables (instructions/data), sauts -> refs d'element.
CS1_API int32_t cs1_func_decode(cs1ed::Doc*d,int32_t i){
    using namespace cs1ed; using namespace cs1;
    if(!d||i<0||i>=(int)d->funcs.size())return -1;
    Func& f=d->funcs[i];
    // copie relative (cibles relatives a la fonction)
    std::vector<uint8_t> rel=f.bytes;
    if(f.type==0||f.type==-1) cs1_reloc_jumps(rel, -f.ostart, f.ostart, f.ostart+(long)rel.size());
    long e=(long)rel.size();
    Cs1Seg segs[16384]; int ns=cs1_parse_function(rel.data(),0,(uint32_t)e,segs,16384);
    if(ns<0)return -1;
    f.model.clear();
    // offset -> index element
    std::vector<long> starts;
    for(int k=0;k<ns;k++){ Elem el; el.isData=(segs[k].kind==CS1_SEG_DATA);
        el.raw.assign(rel.begin()+segs[k].start, rel.begin()+segs[k].start+segs[k].len);
        el.opcode=el.raw.empty()?0:el.raw[0]; starts.push_back(segs[k].start); f.model.push_back(std::move(el)); }
    auto elemAt=[&](long off)->int{ for(int k=0;k<ns;k++) if(starts[k]==off) return k; return -1; };
    // resout les sauts
    for(int k=0;k<ns;k++){ Elem& el=f.model[k]; if(el.isData)continue;
        std::vector<int> pos; cs1_jump_positions(el.raw.data(),(long)el.raw.size(),pos);
        for(int p:pos){ uint32_t tgt=cs1_rd32(el.raw.data(),p); int ti=elemAt((long)tgt);
            el.jumps.push_back({p,ti}); } }
    f.decoded=true;
    return (int32_t)f.model.size();
}
CS1_API int32_t cs1_func_elem_count(cs1ed::Doc*d,int32_t i){ if(!d||i<0||i>=(int)d->funcs.size()||!d->funcs[i].decoded)return -1; return (int32_t)d->funcs[i].model.size(); }
CS1_API int32_t cs1_func_elem_kind(cs1ed::Doc*d,int32_t i,int32_t e){ if(!d||i<0||i>=(int)d->funcs.size()||e<0||e>=(int)d->funcs[i].model.size())return -1; return d->funcs[i].model[e].isData?1:0; }
CS1_API int32_t cs1_func_elem_opcode(cs1ed::Doc*d,int32_t i,int32_t e){ if(!d||i<0||i>=(int)d->funcs.size()||e<0||e>=(int)d->funcs[i].model.size())return -1; return d->funcs[i].model[e].opcode; }
CS1_API int32_t cs1_func_elem_len(cs1ed::Doc*d,int32_t i,int32_t e){ if(!d||i<0||i>=(int)d->funcs.size()||e<0||e>=(int)d->funcs[i].model.size())return -1; return (int32_t)d->funcs[i].model[e].raw.size(); }
CS1_API const uint8_t* cs1_func_elem_bytes(cs1ed::Doc*d,int32_t i,int32_t e){ if(!d||i<0||i>=(int)d->funcs.size()||e<0||e>=(int)d->funcs[i].model.size())return nullptr; return d->funcs[i].model[e].raw.data(); }
CS1_API int32_t cs1_func_elem_njumps(cs1ed::Doc*d,int32_t i,int32_t e){ if(!d||i<0||i>=(int)d->funcs.size()||e<0||e>=(int)d->funcs[i].model.size())return -1; return (int32_t)d->funcs[i].model[e].jumps.size(); }
CS1_API int32_t cs1_func_elem_jump_target(cs1ed::Doc*d,int32_t i,int32_t e,int32_t j){ if(!d||i<0||i>=(int)d->funcs.size()||e<0||e>=(int)d->funcs[i].model.size())return -2; auto&J=d->funcs[i].model[e].jumps; if(j<0||j>=(int)J.size())return -2; return J[j].second; }

static inline void _cs1_shift_refs(cs1ed::Func&f,int at,int delta){
    for(auto&el:f.model) for(auto&jp:el.jumps) if(jp.second>=at) jp.second+=delta;
}
CS1_API int32_t cs1_func_elem_remove(cs1ed::Doc*d,int32_t i,int32_t e){
    using namespace cs1ed; if(!d||i<0||i>=(int)d->funcs.size()||!d->funcs[i].decoded)return -1;
    Func&f=d->funcs[i]; if(e<0||e>=(int)f.model.size())return -1;
    f.model.erase(f.model.begin()+e); _cs1_shift_refs(f,e,-1);
    for(auto&el:f.model) for(auto&jp:el.jumps) if(jp.second==e) jp.second=-1; // saut vers element supprime -> non resolu
    return 0;
}
// Insere une instruction/bloc brut a la position 'at' (sans saut). Renvoie l'index, ou <0.
CS1_API int32_t cs1_func_elem_insert(cs1ed::Doc*d,int32_t i,int32_t at,const uint8_t*bytes,int32_t len){
    using namespace cs1ed; if(!d||i<0||i>=(int)d->funcs.size()||!d->funcs[i].decoded||len<1||!bytes)return -1;
    Func&f=d->funcs[i]; if(at<0||at>(int)f.model.size())at=(int)f.model.size();
    Elem el; el.raw.assign(bytes,bytes+len); el.opcode=bytes[0];
    el.isData = (cs1::cs1_instr_length(bytes,len)!=len);   // si pas exactement une instruction -> data
    _cs1_shift_refs(f,at,1);
    f.model.insert(f.model.begin()+at,std::move(el));
    return at;
}
// Reassemble le modele -> octets de la fonction (cibles relatives ; le doc serialize absolutise).
CS1_API int32_t cs1_func_reassemble(cs1ed::Doc*d,int32_t i){
    using namespace cs1ed; if(!d||i<0||i>=(int)d->funcs.size()||!d->funcs[i].decoded)return -1;
    Func&f=d->funcs[i];
    std::vector<long> off; long acc=0; for(auto&el:f.model){ off.push_back(acc); acc+=(long)el.raw.size(); }
    std::vector<uint8_t> out; out.reserve(acc);
    for(int k=0;k<(int)f.model.size();k++){ Elem el=f.model[k];
        for(auto&jp:el.jumps){ if(jp.second>=0&&jp.second<(int)off.size()) cs1_wr32(el.raw.data(),jp.first,(uint32_t)off[jp.second]); }
        out.insert(out.end(),el.raw.begin(),el.raw.end()); }
    f.bytes=std::move(out); f.ostart=0;                       // cibles relatives -> ostart 0
    f.type=cs1::cs1_classify_function(f.bytes.data(),f.named?f.name.c_str():"",0,(long)f.bytes.size());
    return (int32_t)f.bytes.size();
}

#endif
