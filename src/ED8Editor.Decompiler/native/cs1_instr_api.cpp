// cs1_instr_api : DLL C ABI exposant les scripts CS1 au niveau INSTRUCTION-PAR-BRANCHE.
// L'editeur ne voit que des noms d'instruction + des arguments types. Opcode, selecteurs,
// encodage octet sont invisibles (geres en interne). Pilote par cs1_opcodes_typed.json.
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cmath>
#include <string>
#include <vector>
#include <map>
#include <memory>
#include <algorithm>
#include "cs1_editor.h"   // cs1ed::Doc + cs1::cs1_cstr/cs1_expr/cs1_dialog + CS1_API


// ---------------- JSON minimal ----------------
namespace mj {
struct Val {
  enum T{NUL,BOOL,INT,DBL,STR,ARR,OBJ} t=NUL;
  bool b=false; long long i=0; double d=0; std::string s;
  std::vector<Val> arr; std::vector<std::pair<std::string,Val>> obj;
  const Val* get(const std::string&k) const { for(auto&kv:obj) if(kv.first==k) return &kv.second; return nullptr; }
  bool has(const std::string&k) const { return get(k)!=nullptr; }
  long asInt() const { return t==INT?(long)i:(t==DBL?(long)d:(t==BOOL?(long)b:0)); }
  const std::string& asStr() const { return s; }
};
struct P {
  const char*p,*e;
  P(const std::string&x):p(x.data()),e(x.data()+x.size()){}
  void ws(){ while(p<e&&(*p==' '||*p=='\t'||*p=='\n'||*p=='\r'))++p; }
  Val parse(){ ws(); return val(); }
  Val val(){ ws(); if(p>=e)return {};
    char c=*p;
    if(c=='{')return obj(); if(c=='[')return arr(); if(c=='"')return str();
    if(c=='t'){p+=4;Val v;v.t=Val::BOOL;v.b=true;return v;}
    if(c=='f'){p+=5;Val v;v.t=Val::BOOL;v.b=false;return v;}
    if(c=='n'){p+=4;return {};}
    return num(); }
  Val str(){ Val v;v.t=Val::STR; ++p; std::string o;
    while(p<e&&*p!='"'){ char c=*p++;
      if(c=='\\'&&p<e){ char x=*p++;
        switch(x){case 'n':o+='\n';break;case 't':o+='\t';break;case 'r':o+='\r';break;
          case '"':o+='"';break;case '\\':o+='\\';break;case '/':o+='/';break;
          case 'b':o+='\b';break;case 'f':o+='\f';break;
          case 'u':{ int cp=0; for(int k=0;k<4&&p<e;k++){char h=*p++;cp=cp*16+(h<='9'?h-'0':(h|32)-'a'+10);}
            if(cp<0x80)o+=(char)cp; else if(cp<0x800){o+=(char)(0xC0|(cp>>6));o+=(char)(0x80|(cp&0x3F));}
            else{o+=(char)(0xE0|(cp>>12));o+=(char)(0x80|((cp>>6)&0x3F));o+=(char)(0x80|(cp&0x3F));} break;}
          default:o+=x;} }
      else o+=c; }
    if(p<e)++p; v.s=o; return v; }
  Val num(){ Val v; const char*s0=p; bool dbl=false;
    while(p<e&&(*p=='-'||*p=='+'||*p=='.'||*p=='e'||*p=='E'||(*p>='0'&&*p<='9'))){ if(*p=='.'||*p=='e'||*p=='E')dbl=true; ++p; }
    std::string n(s0,p); if(dbl){v.t=Val::DBL;v.d=atof(n.c_str());} else {v.t=Val::INT;v.i=atoll(n.c_str());} return v; }
  Val arr(){ Val v;v.t=Val::ARR; ++p; ws(); if(p<e&&*p==']'){++p;return v;}
    while(p<e){ v.arr.push_back(val()); ws(); if(p<e&&*p==','){++p;continue;} if(p<e&&*p==']'){++p;break;} break; } return v; }
  Val obj(){ Val v;v.t=Val::OBJ; ++p; ws(); if(p<e&&*p=='}'){++p;return v;}
    while(p<e){ ws(); Val k=str(); ws(); if(p<e&&*p==':')++p; Val vv=val(); v.obj.push_back({k.s,vv});
      ws(); if(p<e&&*p==','){++p;continue;} if(p<e&&*p=='}'){++p;break;} break; } return v; }
};
} // namespace mj

// ---------------- Modele de noeud ----------------
namespace cs1i {
using mj::Val;
struct Node;
using NodeList=std::vector<Node>;
struct SwitchN{ bool peek=false; std::map<long,NodeList> cases; bool hasDefault=false; NodeList def; };
struct IfN{ std::string cond; NodeList then_, else_; };
struct IfValN{ bool useIn=false; std::vector<long> in; long eq=0; NodeList then_, else_; };
struct LoopN{ std::string count; NodeList body; };
struct Node{
  enum K{SCALAR,STR,EXPR,DIALOG,BYTES,FILL,SWITCH,IF,IFVAL,LOOP} k=SCALAR;
  std::string t;        // pour SCALAR : u8/s8/u16/s16/u32/s32/f32/ptr32
  int width=0;          // largeur scalaire
  int role=0;           // 0 aucun, 1 selector, 2 count, 3 sel16
  int size=0;           // BYTES
  int to=0;             // FILL
  std::string name;     // libelle humain de l'operande (optionnel)
  std::string sem;      // type semantique : color/position/vec2/vec3/vec4/file/tbl/func_index/func_name
  std::string semArg;   // parametre du sem : extension(s), "tbl:type", ...
  int semSpan=1;        // nb d'operandes consecutifs groupes par ce sem (ex position=3 floats)
  std::shared_ptr<SwitchN> sw;
  std::shared_ptr<IfN> iff;
  std::shared_ptr<IfValN> ifv;
  std::shared_ptr<LoopN> lp;
};
static int scalarW(const std::string&t){
  if(t=="u8"||t=="s8")return 1; if(t=="u16"||t=="s16")return 2;
  if(t=="u32"||t=="s32"||t=="f32"||t=="ptr32")return 4; return 0;
}
static NodeList buildNodes(const Val& arr);
static Node buildNode(const Val& nd){
  Node n;
  if(nd.has("switch")||nd.has("switch_peek")){
    n.k=Node::SWITCH; n.sw=std::make_shared<SwitchN>();
    const Val* sv=nd.get("switch"); if(!sv){sv=nd.get("switch_peek");n.sw->peek=true;}
    const Val* cs=sv->get("cases");
    if(cs) for(auto&kv:cs->obj) n.sw->cases[atol(kv.first.c_str())]=buildNodes(kv.second);
    const Val* df=sv->get("default"); if(df){n.sw->hasDefault=true;n.sw->def=buildNodes(*df);}
    return n;
  }
  if(nd.has("if")){ n.k=Node::IF; n.iff=std::make_shared<IfN>(); const Val* f=nd.get("if");
    n.iff->cond=f->get("cond")->asStr(); n.iff->then_=buildNodes(*f->get("then"));
    if(f->has("else"))n.iff->else_=buildNodes(*f->get("else")); return n; }
  if(nd.has("ifval")){ n.k=Node::IFVAL; n.ifv=std::make_shared<IfValN>(); const Val* f=nd.get("ifval");
    if(f->has("in")){n.ifv->useIn=true; for(auto&x:f->get("in")->arr)n.ifv->in.push_back(x.asInt());}
    else n.ifv->eq=f->get("eq")->asInt();
    n.ifv->then_=buildNodes(*f->get("then")); if(f->has("else"))n.ifv->else_=buildNodes(*f->get("else")); return n; }
  if(nd.has("loop")){ n.k=Node::LOOP; n.lp=std::make_shared<LoopN>(); const Val* f=nd.get("loop");
    n.lp->count=f->get("count")->asStr(); n.lp->body=buildNodes(*f->get("body")); return n; }
  const std::string& t=nd.get("t")->asStr();
  if(nd.has("role")){ const std::string&r=nd.get("role")->asStr();
    n.role = r=="selector"?1 : r=="count"?2 : r=="sel16"?3 : r=="peek"?4 : 0; }
  if(nd.has("name")&&nd.get("name")->t==Val::STR) n.name=nd.get("name")->asStr();
  if(nd.has("sem")&&nd.get("sem")->t==Val::STR) n.sem=nd.get("sem")->asStr();
  if(nd.has("sem_arg")&&nd.get("sem_arg")->t==Val::STR) n.semArg=nd.get("sem_arg")->asStr();
  if(nd.has("sem_span")) n.semSpan=(int)nd.get("sem_span")->asInt();
  if(t=="string"){n.k=Node::STR;} else if(t=="expr"){n.k=Node::EXPR;}
  else if(t=="dialog"){n.k=Node::DIALOG;} else if(t=="bytes"){n.k=Node::BYTES;n.size=(int)nd.get("size")->asInt();}
  else if(t=="fill"){n.k=Node::FILL;n.to=(int)nd.get("to")->asInt();}
  else {n.k=Node::SCALAR;n.t=t;n.width=scalarW(t);}
  return n;
}
static NodeList buildNodes(const Val& arr){ NodeList o; for(auto&nd:arr.arr)o.push_back(buildNode(nd)); return o; }

// ---------------- Contexte + eval condition ----------------
struct Ctx{ long sel=-1,sel2=-1,sel16=-1,count=0,control=-1; long laststr=0; bool haveSel=false; };
// mini-eval : gere && || puis comparaisons puis & puis literal/ident
struct Ev{
  const char*p,*e; const Ctx&c; bool ok=true;
  Ev(const std::string&s,const Ctx&cc):p(s.data()),e(s.data()+s.size()),c(cc){}
  void ws(){ while(p<e&&*p==' ')++p; }
  long ident(){
    // consomme un identifiant/appel type control_byte[0], code, control, control_byte2
    const char*s=p; while(p<e&&(isalnum((unsigned char)*p)||*p=='_'))++p;
    std::string id(s,p); ws(); if(p<e&&*p=='['){ while(p<e&&*p!=']')++p; if(p<e)++p; } // [0]
    if(id=="control_byte2")return c.sel2;
    if(id=="control_byte"||id=="code")return c.sel;
    if(id=="control")return c.control;
    ok=false; return 0;
  }
  long prim(){ ws();
    if(p<e&&*p=='('){++p; long v=orr(); ws(); if(p<e&&*p==')')++p; return v;}
    if(p<e&&*p=='!'){++p; return !prim();}
    if(p<e&&(*p=='-'||isdigit((unsigned char)*p))){
      // cast prefix deja retire en amont
      char*end; long v=strtol(p,&end,0); p=end; return v; }
    // cast (unsigned char) etc: saute
    if(p<e&&*p=='('){ return prim(); }
    return ident();
  }
  long band(){ long v=prim(); ws(); while(p<e&&*p=='&'&&!(p+1<e&&p[1]=='&')){++p; long r=prim(); v=v&r; ws();} return v; }
  long cmp(){ long v=band(); ws();
    while(p<e){
      if(p+1<e&&p[0]=='='&&p[1]=='='){p+=2;v=(v==band());}
      else if(p+1<e&&p[0]=='!'&&p[1]=='='){p+=2;v=(v!=band());}
      else if(p+1<e&&p[0]=='<'&&p[1]=='='){p+=2;v=(v<=band());}
      else if(p+1<e&&p[0]=='>'&&p[1]=='='){p+=2;v=(v>=band());}
      else if(p<e&&*p=='<'){++p;v=(v<band());}
      else if(p<e&&*p=='>'){++p;v=(v>band());}
      else break; ws();
    } return v; }
  long andd(){ long v=cmp(); ws(); while(p+1<e&&p[0]=='&'&&p[1]=='&'){p+=2; long r=cmp(); v=(v&&r); ws();} return v; }
  long orr(){ long v=andd(); ws(); while(p+1<e&&p[0]=='|'&&p[1]=='|'){p+=2; long r=andd(); v=(v||r); ws();} return v; }
};
static bool evalCond(const std::string& cond, const Ctx& c, bool& outOk){
  // retire les casts C
  std::string s; for(size_t i=0;i<cond.size();){
    if(cond.compare(i,15,"(unsigned char)")==0){i+=15;continue;}
    if(cond.compare(i,7,"(short)")==0){i+=7;continue;}
    if(cond.compare(i,5,"(int)")==0){i+=5;continue;}
    if(cond.compare(i,6,"(uint)")==0){i+=6;continue;}
    if(cond.compare(i,10,"(unsigned)")==0){i+=10;continue;}
    s+=cond[i++];
  }
  Ev ev(s,c); long v=ev.orr(); outOk=ev.ok; return v!=0;
}
} // namespace cs1i

// ================= Registre + document =================
namespace cs1i {
struct Arg; struct Instr;
struct ExprElem{ uint8_t subop=0; std::vector<uint8_t> payload; std::shared_ptr<Instr> nested; };
struct Arg{
  int kind=0;            // 0 scalar,1 str,2 expr,3 dialog,4 bytes,5 fill,6 sel(cache),7 loop
  std::string type;      // pour scalaire
  bool hidden=false;     // sel/fill : invisibles editeur
  long ival=0; double fval=0; std::vector<uint8_t> raw;
  bool isRef=false; long targetId=-1;              // ptr32 -> reference symbolique vers une instruction
  std::vector<std::vector<Arg>> groups;            // kind 7 : corps de boucle (ex op6)
  std::vector<ExprElem> expr;                      // kind 2 : sous-ops de l'expression
};
struct Instr{ int reg=-1; int op=0; std::vector<long> path; std::vector<Arg> args; long id=-1; long origOff=0; };
struct RegInstr{ std::string name, opname; int op=0; bool ui=false; std::vector<long> path;
                 std::vector<std::string> argTypes, argNames, argSems, argSemArgs; std::vector<int> argSemSpans; NodeList read; };

struct Registry{
  NodeList opread[160]; bool hasop[160]={false};
  NodeList uiread; bool hasui=false; int uiop=19;
  std::map<std::string,int> byPath;         // "op|p1,p2|u" -> reg index
  std::vector<RegInstr> regs;
  std::map<int,std::vector<int>> byOp, byOpUi;  // op -> reg indices (candidats), trie concret d'abord
  std::vector<std::string> uiFiles;
  bool loaded=false;

  static std::string key(int op,const std::vector<long>&path,bool ui){
    std::string k=std::to_string(op)+"|"; for(size_t i=0;i<path.size();i++){k+=std::to_string(path[i]);k+=",";} k+= ui?"|u":"|";
    return k;
  }
  bool isUiFile(const std::string& base){ for(auto&f:uiFiles) if(f==base)return true; return false; }
};
static Registry G;

// schema d'arguments visibles (scan lineaire best-effort du read aplati)
static void schemaPush(RegInstr& r, const std::string& ty, const Node& n){
  r.argTypes.push_back(ty); r.argNames.push_back(n.name); r.argSems.push_back(n.sem);
  r.argSemArgs.push_back(n.semArg); r.argSemSpans.push_back(n.semSpan);
}
static void schemaScan(const NodeList& nl, RegInstr& r){
  for(auto&n:nl){
    switch(n.k){
      case Node::SCALAR: if(n.role!=1) schemaPush(r,n.t,n); break; // selector cache
      case Node::STR: schemaPush(r,"string",n); break;
      case Node::EXPR: schemaPush(r,"expr",n); break;
      case Node::DIALOG: schemaPush(r,"dialog",n); break;
      case Node::BYTES: schemaPush(r,"bytes",n); break;
      case Node::FILL: break;
      case Node::IF: schemaScan(n.iff->then_,r); break;
      case Node::IFVAL: schemaScan(n.ifv->then_,r); break;
      case Node::LOOP: schemaPush(r,"list",n); break;
      case Node::SWITCH: break; // n'apparait pas dans un read aplati d'instruction
    }
  }
}
// Charge le fichier INSTRUCTIONS (source de verite unique de la DLL) : { ui_files:[...], instructions:[...] }
static bool loadDoc(const std::string& json){
  mj::P pp(json); Val root=pp.parse();
  const Val* uf=root.get("ui_files"); if(uf) for(auto&x:uf->arr)G.uiFiles.push_back(x.asStr());
  const Val* ins=root.get("instructions"); if(!ins)return false;
  for(auto&iv:ins->arr){ RegInstr r; r.name=iv.get("name")->asStr(); r.op=(int)iv.get("op")->asInt();
    if(iv.has("opname")&&iv.get("opname")->t==Val::STR)r.opname=iv.get("opname")->asStr();
    r.ui = iv.has("scope") && iv.get("scope")->asStr()=="ui_files";
    for(auto&s:iv.get("selectors")->arr){ const Val* vv=s.get("value");
      if(vv->t==Val::STR){ r.path.push_back(vv->asStr()=="default"?-1000:-1001); } // default=-1000, other=-1001
      else r.path.push_back(vv->asInt()); }
    NodeList rl=buildNodes(*iv.get("read")); schemaScan(rl,r); r.read=rl;
    G.byPath[Registry::key(r.op,r.path,r.ui)]=(int)G.regs.size(); G.regs.push_back(std::move(r));
  }
  // candidats par opcode, tries : moins de sentinelles (default/other) d'abord
  auto nsent=[](const RegInstr&r){ int n=0; for(long v:r.path) if(v<0)n++; return n; };
  for(int i=0;i<(int)G.regs.size();i++){ auto&r=G.regs[i]; (r.ui?G.byOpUi:G.byOp)[r.op].push_back(i); }
  auto srt=[&](std::vector<int>&v){ std::stable_sort(v.begin(),v.end(),[&](int a,int b){ return nsent(G.regs[a])<nsent(G.regs[b]); }); };
  for(auto&kv:G.byOp)srt(kv.second); for(auto&kv:G.byOpUi)srt(kv.second);
  G.loaded=true; return true;
}

// ================= decode / encode =================
static void setCtxScalar(const Node&n,const uint8_t*b,long p,Ctx&c){
  if(n.role==1){ if(!c.haveSel){c.sel=b[p];c.haveSel=true;} else c.sel2=b[p]; }
  if(n.role==2)c.count=b[p];
  if(n.role==3)c.sel16=b[p]|(b[p+1]<<8);
  if(n.t=="s16"&&c.control<0)c.control=b[p]|(b[p+1]<<8);
}
static long rd_scalar(const std::string&t,const uint8_t*b,long p,Arg&a){
  int w=scalarW(t); a.kind=0; a.type=t;
  long v=0; for(int i=0;i<w;i++)v|=(long)b[p+i]<<(8*i);
  if(t=="s8"&&(v&0x80))v-=0x100; if(t=="s16"&&(v&0x8000))v-=0x10000;
  if(t=="s32"&&(v&0x80000000L))v-=0x100000000L;
  if(t=="f32"){ uint32_t u=(uint32_t)v; float f; memcpy(&f,&u,4); a.fval=f; }
  a.ival=v; return w;
}
// --- Decodage PILOTE PAR LES INSTRUCTIONS ---
// decode une branche (read aplati) en verifiant selecteurs/peek contre 'path'.
// retour: >=0 longueur ; -2 = selecteur ne matche pas (essayer candidat suivant) ; -1 = corps invalide.
static int exprPayload(uint8_t s){ switch(s){case 0x00:return 4;case 0x1e:return 2;case 0x1f:case 0x20:case 0x23:return 1;case 0x21:return 3;default:return 0;} }
static long decodeExpr(const uint8_t*b,long p,long e,std::vector<ExprElem>&out);
static bool decodeOne(const uint8_t*b,long p,long e,bool ui,Instr&chosen,long&len);
static bool encodeInstr(const Instr& in,bool ui,std::vector<uint8_t>&out);
static void enc_scalar(const std::string&t,const Arg&a,std::vector<uint8_t>&out){
  int w=scalarW(t); uint32_t v;
  if(t=="f32"){ float f=(float)a.fval; memcpy(&v,&f,4);} else v=(uint32_t)a.ival;
  for(int i=0;i<w;i++)out.push_back((v>>(8*i))&0xff);
}
static void encodeExpr(const std::vector<ExprElem>&elems,std::vector<uint8_t>&out);
static long decodeMatch(const NodeList& nl,const std::vector<long>&path,size_t&lvl,const uint8_t*b,long p,long e,Ctx&c,std::vector<Arg>&args){
  long i0=p;
  for(auto&n:nl){
    if(n.k==Node::IF){ bool ok; bool r=evalCond(n.iff->cond,c,ok); if(!ok)return -1;
      long L=decodeMatch(r?n.iff->then_:n.iff->else_,path,lvl,b,p,e,c,args); if(L<-1)return L; if(L<0)return -1; p+=L; continue; }
    if(n.k==Node::IFVAL){ bool take=n.ifv->useIn?(std::find(n.ifv->in.begin(),n.ifv->in.end(),c.sel16)!=n.ifv->in.end()):(c.sel16==n.ifv->eq);
      long L=decodeMatch(take?n.ifv->then_:n.ifv->else_,path,lvl,b,p,e,c,args); if(L<-1)return L; if(L<0)return -1; p+=L; continue; }
    if(n.k==Node::LOOP){ long cnt=(n.lp->count=="count")?c.count:0; Arg a; a.kind=7; a.type="list";
      for(long k=0;k<cnt;k++){ size_t dl=lvl; std::vector<Arg> sub;
        long L=decodeMatch(n.lp->body,path,dl,b,p,e,c,sub); if(L<0)return -1; p+=L; a.groups.push_back(std::move(sub)); }
      args.push_back(std::move(a)); continue; }
    if(n.k==Node::SCALAR){ if(p+n.width>e)return -1; Arg a; long L=rd_scalar(n.t,b,p,a);
      if(n.role==1||n.role==3||n.role==4){ long want=(lvl<(long)path.size())?path[lvl]:-1; lvl++;
        if(want>=0 && a.ival!=want) return -2;
        if(n.role==1||n.role==3){ a.kind=6; a.hidden=true; } }
      setCtxScalar(n,b,p,c); args.push_back(a); p+=L; continue; }
    if(n.k==Node::STR){ int L=cs1::cs1_cstr(b+p,e-p); if(L<0)return -1; Arg a;a.kind=1;a.raw.assign(b+p,b+p+L-1);c.laststr=L;args.push_back(a);p+=L;continue; }
    if(n.k==Node::EXPR){ Arg a;a.kind=2; long L=decodeExpr(b,p,e,a.expr); if(L<0)return -1; a.raw.assign(b+p,b+p+L); args.push_back(std::move(a)); p+=L; continue; }
    if(n.k==Node::DIALOG){ int L=cs1::cs1_dialog(b+p,e-p); if(L<0)return -1; Arg a;a.kind=3;a.raw.assign(b+p,b+p+L);args.push_back(a);p+=L;continue; }
    if(n.k==Node::BYTES){ if(p+n.size>e)return -1; Arg a;a.kind=4;a.raw.assign(b+p,b+p+n.size);args.push_back(a);p+=n.size;continue; }
    if(n.k==Node::FILL){ long L=c.laststr>n.to?0:(n.to-c.laststr); if(p+L>e)return -1; Arg a;a.kind=5;a.hidden=true;a.raw.assign(b+p,b+p+L);args.push_back(a);p+=L;continue; }
    if(n.k==Node::SWITCH){ return -1; }
  }
  return p-i0;
}
static bool decodeOne(const uint8_t*b,long p,long e,bool ui,Instr&chosen,long&len){
  uint8_t o=b[p]; std::vector<int>* cands=nullptr;
  if(ui){ auto it=G.byOpUi.find(o); if(it!=G.byOpUi.end()) cands=&it->second; }
  if(!cands){ auto it=G.byOp.find(o); if(it==G.byOp.end()) return false; cands=&it->second; }
  for(int ri:*cands){ Ctx c; std::vector<Arg> args; size_t lvl=0;
    long L=decodeMatch(G.regs[ri].read,G.regs[ri].path,lvl,b,p+1,e,c,args);
    if(L==-2) continue;
    if(L<0) return false;
    chosen=Instr(); chosen.op=o; chosen.reg=ri; chosen.path=G.regs[ri].path; chosen.args=std::move(args);
    len=1+L; return true;
  }
  return false;
}
static long decodeExpr(const uint8_t*b,long p,long e,std::vector<ExprElem>&out){
  long i=p;
  while(i<e){ uint8_t s=b[i];
    if(s==0x01){ ExprElem el; el.subop=0x01; out.push_back(el); return (i+1)-p; }
    if(s==0x1c){ Instr nested; long nl; if(!decodeOne(b,i+1,e,false,nested,nl)) return -1;
      ExprElem el; el.subop=0x1c; el.nested=std::make_shared<Instr>(std::move(nested)); out.push_back(std::move(el)); i+=1+nl; continue; }
    int pl=exprPayload(s); if(i+1+pl>e) return -1;
    ExprElem el; el.subop=s; el.payload.assign(b+i+1,b+i+1+pl); out.push_back(std::move(el)); i+=1+pl;
  }
  return -1;
}
static bool decodeFunc(const uint8_t*b,long len,bool ui,long absBase,std::vector<Instr>&out){
  long p=0;
  while(p<len){ Instr chosen; long L=0;
    if(!decodeOne(b,p,len,ui,chosen,L)) return false;
    chosen.origOff=absBase+p; out.push_back(std::move(chosen)); p+=L;
  }
  return p==len;
}
static void encodeExpr(const std::vector<ExprElem>&elems,std::vector<uint8_t>&out){
  for(auto&el:elems){ out.push_back(el.subop);
    if(el.subop==0x1c && el.nested) encodeInstr(*el.nested,false,out);
    else out.insert(out.end(),el.payload.begin(),el.payload.end());
  }
}
static bool encodeFlat(const NodeList& nl,const std::vector<Arg>&args,size_t&ai,Ctx&c,std::vector<uint8_t>&out){
  for(auto&n:nl){
    if(n.k==Node::IF){ bool ok; bool r=evalCond(n.iff->cond,c,ok); if(!ok)return false;
      if(!encodeFlat(r?n.iff->then_:n.iff->else_,args,ai,c,out))return false; continue; }
    if(n.k==Node::IFVAL){ bool take=n.ifv->useIn?(std::find(n.ifv->in.begin(),n.ifv->in.end(),c.sel16)!=n.ifv->in.end()):(c.sel16==n.ifv->eq);
      if(!encodeFlat(take?n.ifv->then_:n.ifv->else_,args,ai,c,out))return false; continue; }
    if(n.k==Node::LOOP){ if(ai>=args.size())return false; const Arg&a=args[ai++];
      for(auto&grp:a.groups){ size_t gai=0; if(!encodeFlat(n.lp->body,grp,gai,c,out))return false; } continue; }
    if(n.k==Node::SCALAR){ if(ai>=args.size())return false; Arg a=args[ai++];
      // count (role 2) : derive du nombre reel d'iterations de la boucle suivante (auto-sync
      // apres ajout/suppression d'iteration). Roundtrip preserve : groups.size()==count d'origine.
      if(n.role==2 && ai<args.size() && args[ai].kind==7) a.ival=(long)args[ai].groups.size();
      long val=a.ival; enc_scalar(n.t,a,out);
      if(n.role==1){ if(!c.haveSel){c.sel=val;c.haveSel=true;} else c.sel2=val; }
      if(n.role==2)c.count=val; if(n.role==3)c.sel16=val&0xffff; if(n.t=="s16"&&c.control<0)c.control=val&0xffff; continue; }
    if(n.k==Node::STR){ if(ai>=args.size())return false; const Arg&a=args[ai++]; out.insert(out.end(),a.raw.begin(),a.raw.end()); out.push_back(0); c.laststr=(long)a.raw.size()+1; continue; }
    if(n.k==Node::EXPR){ if(ai>=args.size())return false; const Arg&a=args[ai++]; encodeExpr(a.expr,out); continue; }
    if(n.k==Node::DIALOG||n.k==Node::BYTES||n.k==Node::FILL){ if(ai>=args.size())return false; const Arg&a=args[ai++]; out.insert(out.end(),a.raw.begin(),a.raw.end()); continue; }
    if(n.k==Node::SWITCH){ return false; }
  }
  return true;
}
static bool encodeInstr(const Instr& in,bool ui,std::vector<uint8_t>&out){
  (void)ui; if(in.reg<0||in.reg>=(int)G.regs.size())return false;
  out.push_back((uint8_t)in.op);
  Ctx c; size_t ai=0; return encodeFlat(G.regs[in.reg].read,in.args,ai,c,out);
}
} // namespace cs1i

// ================= Tables de donnees (portage de table_parsers.py) =================
// Les fonctions non-code portant un nom de table (guess_type_by_name) sont des
// structures de donnees, pas du code. On les decode en champs types (chaque champ
// garde ses octets bruts -> re-serialisation = concat = roundtrip 0-diff). Si le
// parse structure ne colle pas au format Ghidra, on marque stale (perime/malforme,
// typiquement les fichiers debug 'al*') et on preserve un blob opaque.
namespace cs1tbl {
struct TField{ std::string type; long ival=0; double fval=0; std::vector<uint8_t> raw; std::string text; int fill=-1; long off=0; };
struct Table{ std::string kind; int id=-1; bool opaque=false; bool stale=false; std::vector<TField> fields; long dataEnd=0; };

static bool typeForName(const std::string& nm, std::string& kind, int& id){
  if(nm.empty())return false;
  struct M{const char* n;int id;};
  static const M exact[]={{"CreateMonsters",256},{"EffectsInstr",257},{"ActionTable",258},
    {"AlgoTable",259},{"WeaponAttTable",260},{"BreakTable",261},{"SummonTable",262},
    {"ReactionTable",263},{"PartTable",264},{"AnimeClipTable",265},{"FieldMonsterData",266},
    {"FieldFollowData",267},{"AddCollision",271}};
  for(auto&m:exact) if(nm==m.n){ kind=m.n; id=m.id; return true; }
  if(nm.rfind("FC_auto",0)==0){ kind="FC_autoX"; id=268; return true; }
  if(nm.rfind("BookData",0)==0){
    // BookData<N>_<M> : M==99 -> Book99 sinon BookX
    size_t us=nm.rfind('_'); std::string mm = us!=std::string::npos? nm.substr(us+1):"";
    if(mm=="99"){ kind="BookData99"; id=269; } else { kind="BookDataX"; id=270; }
    return true;
  }
  return false;
}

static bool typeForParserId(int parserId, std::string& kind, int& id){
  struct M{int parserId; const char* kind; int publicId;};
  static const M types[]={
    {1,"CreateMonsters",256},{2,"EffectsInstr",257},{3,"ActionTable",258},
    {4,"AlgoTable",259},{5,"WeaponAttTable",260},{6,"BreakTable",261},
    {7,"SummonTable",262},{8,"ReactionTable",263},{9,"PartTable",264},
    {10,"AnimeClipTable",265},{11,"FieldMonsterData",266},{12,"FieldFollowData",267},
    {13,"FC_autoX",268},{14,"BookData99",269},{15,"BookDataX",270},
    {16,"AddCollision",271},
  };
  for(const auto& type:types) if(type.parserId==parserId){
    kind=type.kind; id=type.publicId; return true;
  }
  return false;
}

struct TR{
  const uint8_t* b; long p=0, size; bool ok=true;
  TR(const uint8_t* b_, long len):b(b_),size(len){}
  bool chk(long n){ if(n<0||p+n>size){ ok=false; return false; } return true; }
  TField raw(long n,const char* ty){ TField f; f.type=ty; f.off=p; if(!chk(n))return f; f.raw.assign(b+p,b+p+n); p+=n; return f; }
  TField u8(){ TField f=raw(1,"u8"); if(ok&&!f.raw.empty())f.ival=f.raw[0]; return f; }
  TField s16(){ TField f=raw(2,"s16"); if(ok)f.ival=(int16_t)(f.raw[0]|(f.raw[1]<<8)); return f; }
  TField s32(){ TField f=raw(4,"s32"); if(ok)f.ival=(int32_t)(f.raw[0]|(f.raw[1]<<8)|(f.raw[2]<<16)|((uint32_t)f.raw[3]<<24)); return f; }
  TField f32(){ TField f=raw(4,"f32"); if(ok){ uint32_t u=f.raw[0]|(f.raw[1]<<8)|(f.raw[2]<<16)|((uint32_t)f.raw[3]<<24); float v; memcpy(&v,&u,4); f.fval=v; } return f; }
  TField str(){ TField f; f.type="string"; f.off=p; long s=p; while(p<size&&b[p]!=0)p++; if(p>=size){ok=false;return f;} p++; f.raw.assign(b+s,b+p); f.text.assign((const char*)b+s,(size_t)(p-1-s)); return f; }
  void strfill(std::vector<TField>&F,long width,const char* ft="fill"){ TField s=str(); F.push_back(s); if(!ok)return; long pad=width-(long)s.raw.size(); if(pad<0){ok=false;return;} TField fl=raw(pad,ft); fl.fill=(int)width; F.push_back(fl); }
  uint8_t peek_u8(){ if(p>=size){ok=false;return 0;} return b[p]; }
  int16_t peek_s16(){ if(p+2>size){ok=false;return 0;} return (int16_t)(b[p]|(b[p+1]<<8)); }
  uint16_t peek_u16(){ if(p+2>size){ok=false;return 0;} return (uint16_t)(b[p]|(b[p+1]<<8)); }
  int32_t peek_s32(){ if(p+4>size){ok=false;return 0;} return (int32_t)(b[p]|(b[p+1]<<8)|(b[p+2]<<16)|((uint32_t)b[p+3]<<24)); }
};

// ------- schemas de record (blocs fixes retypables, pilotes par cs1_tables.json) -------
struct SchemaField{ std::string type; int size; };
static std::map<std::string,std::vector<SchemaField>> g_defSchema, g_ovSchema;
static void initSchemas(){ if(!g_defSchema.empty())return;
  g_defSchema["AlgoTable"]={{"s16",2},{"s16",2},{"s16",2},{"bytes",14},{"s32",4},{"s32",4},{"bytes",4}};
  g_defSchema["ActionTableFixed"]={{"bytes",42}};
  g_defSchema["FieldMonsterPrefix"]={{"s32",4},{"s16",2},{"s16",2}};
  g_defSchema["FieldFollowData"]={{"f32",4},{"f32",4},{"f32",4},{"f32",4},{"f32",4}};
  g_defSchema["AddCollisionRow"]={{"s32",4},{"f32",4},{"f32",4},{"f32",4},{"f32",4},{"f32",4}};
  g_defSchema["ReactionRow"]={{"s16",2},{"s16",2},{"s16",2},{"s16",2},{"s16",2},{"s16",2}};
}
static int schemaLen(const std::string&n){ initSchemas(); int s=0; for(auto&f:g_defSchema[n])s+=f.size; return s; }
static const std::vector<SchemaField>& effSchema(const std::string&n){ initSchemas();
  auto it=g_ovSchema.find(n); if(it!=g_ovSchema.end()){ int s=0; for(auto&f:it->second)s+=f.size; if(s==schemaLen(n)) return it->second; }
  return g_defSchema[n]; }
static TField readTyped(TR&r,const std::string&t,int size){ TField f=r.raw(size,t.c_str()); if(!r.ok||(int)f.raw.size()<size)return f; const uint8_t*b=f.raw.data();
  if(t=="u8")f.ival=b[0]; else if(t=="s8")f.ival=(int8_t)b[0];
  else if(t=="u16")f.ival=(uint16_t)(b[0]|(b[1]<<8)); else if(t=="s16")f.ival=(int16_t)(b[0]|(b[1]<<8));
  else if(t=="u32")f.ival=(int32_t)(uint32_t)(b[0]|(b[1]<<8)|(b[2]<<16)|((uint32_t)b[3]<<24));
  else if(t=="s32")f.ival=(int32_t)(b[0]|(b[1]<<8)|(b[2]<<16)|((uint32_t)b[3]<<24));
  else if(t=="f32"){ uint32_t u=b[0]|(b[1]<<8)|(b[2]<<16)|((uint32_t)b[3]<<24); float v; memcpy(&v,&u,4); f.fval=v; }
  return f; }
static void readSchema(TR&r,const char*name,std::vector<TField>&F){ for(auto&sf:effSchema(name)) F.push_back(readTyped(r,sf.type,sf.size)); }
// parseur JSON minimal pour {"nom":[["type",N],...],...}
static void loadSchemaJson(const char* js){ g_ovSchema.clear(); if(!js)return; std::string s=js; size_t i=0;
  while(i<s.size()){ size_t q1=s.find('"',i); if(q1==std::string::npos)break; size_t q2=s.find('"',q1+1); if(q2==std::string::npos)break;
    std::string key=s.substr(q1+1,q2-q1-1); i=q2+1; size_t lb=s.find('[',i); if(lb==std::string::npos)break;
    std::vector<SchemaField> fields; size_t k=lb+1;
    while(k<s.size()){ if(s[k]==']')break;
      if(s[k]=='['){ size_t t1=s.find('"',k); size_t t2=s.find('"',t1+1); std::string ty=s.substr(t1+1,t2-t1-1);
        size_t comma=s.find(',',t2); size_t rb=s.find(']',comma); int num=atoi(s.substr(comma+1,rb-comma-1).c_str());
        fields.push_back({ty,num}); k=rb+1; } else k++; }
    if(!fields.empty()) g_ovSchema[key]=fields; i=k; }
}

// chaque parseur remplit F et renvoie ok ; s'appuie sur TR
static bool p_FieldMonsterData(TR&r,std::vector<TField>&F){ readSchema(r,"FieldMonsterPrefix",F); while(r.ok&&r.peek_s32()!=1) F.push_back(r.f32()); return r.ok; }
static bool p_FieldFollowData(TR&r,std::vector<TField>&F){ readSchema(r,"FieldFollowData",F); return r.ok; }
static bool p_FC_autoX(TR&r,std::vector<TField>&F){ F.push_back(r.str()); return r.ok; }
static bool p_BookData99(TR&r,std::vector<TField>&F){ F.push_back(r.s16()); F.push_back(r.s16()); return r.ok; }
static bool p_BookDataX(TR&r,std::vector<TField>&F){ int16_t ctrl=r.peek_s16(); F.push_back(r.s16()); if(!r.ok)return false;
  if(ctrl>0){ F.push_back(r.s16()); r.strfill(F,0x10,"bytes"); for(int i=0;i<10;i++)F.push_back(r.s16()); F.push_back(r.str()); }
  else { if(r.ok&&r.peek_u8()!=1) F.push_back(r.str()); } return r.ok; }
static bool p_WeaponAtt(TR&r,std::vector<TField>&F){ F.push_back(r.raw(4,"bytes")); return r.ok; }
static bool p_AddCollision(TR&r,std::vector<TField>&F){ int n=r.peek_u8(); F.push_back(r.u8()); for(int i=0;i<n&&r.ok;i++) readSchema(r,"AddCollisionRow",F); return r.ok; }
static bool p_PartTable(TR&r,std::vector<TField>&F){ int n=r.peek_u8(); F.push_back(r.u8()); for(int i=0;i<n&&r.ok;i++){ F.push_back(r.s32()); r.strfill(F,0x20); r.strfill(F,0x20); } return r.ok; }
static bool p_ReactionTable(TR&r,std::vector<TField>&F){ uint16_t n=r.peek_u16(); F.push_back(r.s16()); for(int i=0;i<n&&r.ok;i++) readSchema(r,"ReactionRow",F); return r.ok; }
static bool p_SummonTable(TR&r,std::vector<TField>&F){ int n=r.peek_u8(); F.push_back(r.u8()); int cnt=0; while(cnt<n&&r.ok){ uint16_t sh=r.peek_u16(); F.push_back(r.s16()); if(sh==0xFFFF)break; F.push_back(r.u8()); F.push_back(r.u8()); r.strfill(F,0x20); cnt++; } return r.ok; }
static bool p_BreakTable(TR&r,std::vector<TField>&F){ int cnt=0; while(r.ok){ TField sf=r.s16(); F.push_back(sf); if((int16_t)sf.ival==0)break; F.push_back(r.s16()); if(++cnt>=0x40)break; } F.push_back(r.raw(2,"bytes")); return r.ok; }
static bool p_AnimeClipTable(TR&r,std::vector<TField>&F){ while(r.ok&&r.peek_s32()!=0){ F.push_back(r.s32()); r.strfill(F,0x20); r.strfill(F,0x20); } F.push_back(r.s32()); F.push_back(r.s16()); return r.ok; }
static bool p_EffectsInstr(TR&r,std::vector<TField>&F){ while(r.ok&&r.peek_u8()!=0x01){ F.push_back(r.s16()); F.push_back(r.s16()); F.push_back(r.s32()); r.strfill(F,0x20); } return r.ok; }
// ActionTable (Ghidra FUN_004906f0) : count(1) + count*(42 fixes + str[0x20] + str[0x30])
static bool p_ActionTable(TR&r,std::vector<TField>&F){ int n=r.peek_u8(); F.push_back(r.u8()); for(int i=0;i<n&&r.ok;i++){ readSchema(r,"ActionTableFixed",F); r.strfill(F,0x20); r.strfill(F,0x30); } return r.ok; }
// AlgoTable (Ghidra FUN_0048d2c0) : count(1) + count*record(0x20), decoupe pilotee par schema
static bool p_AlgoTable(TR&r,std::vector<TField>&F){ int n=r.peek_u8(); F.push_back(r.u8()); for(int i=0;i<n&&r.ok;i++) readSchema(r,"AlgoTable",F); return r.ok; }
// CreateMonsters (le plus complexe)
static bool p_CreateMonsters(TR&r,std::vector<TField>&F,long end){ long initial=r.p; int32_t first=r.peek_s32();
  if(first==-1){ F.push_back(r.raw(0x1C,"bytes")); return r.ok; }
  r.strfill(F,0x10); F.push_back(r.s32()); for(int i=0;i<6;i++)F.push_back(r.s16());
  while(r.ok){ F.push_back(r.s32());
    for(int c=0;c<8;c++) r.strfill(F,0x10);
    for(int ib=0;ib<8;ib++) F.push_back(r.u8());
    if(r.ok&&r.peek_u8()==0) F.push_back(r.raw(8,"bytes")); else r.strfill(F,12,"bytes");
    if(!r.ok)break; first=r.peek_s32();
    if(!((first!=-1)&&(r.p!=end-4)))break; }
  if(r.ok&&r.p==end-4){ if(r.peek_s32()!=1){r.ok=false;} return r.ok; }
  if(r.ok)F.push_back(r.raw(0x1C,"bytes")); (void)initial; return r.ok; }

// Decode une table : structure Ghidra si consommation exacte jusqu'au terminateur
// op1(0x01)+padding ; sinon blob opaque + stale.
static void decodeAs(const std::string& kind,int id,const uint8_t* b,long len,Table& out){
  out.kind=kind; out.id=id;
  TR r(b,len); std::vector<TField> F; bool ok=false;
  if(kind=="FieldMonsterData")ok=p_FieldMonsterData(r,F);
  else if(kind=="FieldFollowData")ok=p_FieldFollowData(r,F);
  else if(kind=="FC_autoX")ok=p_FC_autoX(r,F);
  else if(kind=="BookData99")ok=p_BookData99(r,F);
  else if(kind=="BookDataX")ok=p_BookDataX(r,F);
  else if(kind=="WeaponAttTable")ok=p_WeaponAtt(r,F);
  else if(kind=="AddCollision")ok=p_AddCollision(r,F);
  else if(kind=="PartTable")ok=p_PartTable(r,F);
  else if(kind=="ReactionTable")ok=p_ReactionTable(r,F);
  else if(kind=="SummonTable")ok=p_SummonTable(r,F);
  else if(kind=="BreakTable")ok=p_BreakTable(r,F);
  else if(kind=="AnimeClipTable")ok=p_AnimeClipTable(r,F);
  else if(kind=="EffectsInstr")ok=p_EffectsInstr(r,F);
  else if(kind=="ActionTable")ok=p_ActionTable(r,F);
  else if(kind=="AlgoTable")ok=p_AlgoTable(r,F);
  else if(kind=="CreateMonsters")ok=p_CreateMonsters(r,F,len);
  // verif terminaison : reste (b[p..len]) = 0x01 optionnel + zeros
  bool cleanEnd=false; if(ok){ long q=len; while(q>r.p&&b[q-1]==0)q--; cleanEnd=(q==r.p)||(q==r.p+1&&b[r.p]==0x01); }
  if(ok&&cleanEnd){ out.opaque=false; out.stale=false; out.dataEnd=r.p; out.fields=std::move(F); return; }
  // repli : blob opaque (perime/malforme) — retire terminateur op1 + padding de fin
  long q=len; while(q>0&&b[q-1]==0)q--; if(q>0&&b[q-1]==0x01)q--;
  TField blob; blob.type="bytes"; blob.off=0; blob.raw.assign(b,b+q);
  out.opaque=true; out.stale=true; out.dataEnd=q; out.fields.clear(); out.fields.push_back(blob);
}
static void decode(const std::string& name,const uint8_t* b,long len,Table& out){
  std::string kind; int id; if(!typeForName(name,kind,id))return;
  decodeAs(kind,id,b,len,out);
}
} // namespace cs1tbl

// ================= Couche C ABI =================
namespace cs1i {
struct IDoc{ cs1ed::Doc* base=nullptr; std::string scene; bool ui=false;
             std::vector<std::vector<Instr>> dec; std::vector<char> isCode;
             std::vector<char> isTable; std::vector<cs1tbl::Table> tables;
             long nextId=0; std::vector<long> funcEndId;
             std::vector<long> origStart, origEnd;
             std::vector<uint8_t> origHeader; long paOff=0; // header original (byte-perfect) + offset de la table de ptr
             int origNb=0; long origFnpos=0, origPa=0; }; // pour reconstruire fidelement si nb change (add/remove)
// resout recursivement les ptr32 -> targetId via off2id ; sinon laisse brut (isRef=false)
static void resolveRefs(std::vector<Arg>& args, std::map<long,long>& off2id){
  for(auto&a:args){
    if(a.kind==0 && a.type=="ptr32"){ auto it=off2id.find(a.ival); if(it!=off2id.end()){ a.isRef=true; a.targetId=it->second; } }
    if(a.kind==7) for(auto&g:a.groups) resolveRefs(g,off2id);
    if(a.kind==2) for(auto&el:a.expr) if(el.nested) resolveRefs(el.nested->args,off2id);
  }
}
static std::string baseName(const char* fn){ std::string s=fn?fn:""; size_t sl=s.find_last_of("/\\"); if(sl!=std::string::npos)s=s.substr(sl+1); size_t dot=s.rfind('.'); if(dot!=std::string::npos)s=s.substr(0,dot); return s; }
// index de l'arg visible v -> index reel dans args
static int visibleToReal(const std::vector<Arg>&a,int v){ int c=0; for(size_t i=0;i<a.size();i++){ if(!a[i].hidden){ if(c==v)return (int)i; c++; } } return -1; }
static int visibleCount(const std::vector<Arg>&a){ int c=0; for(auto&x:a)if(!x.hidden)c++; return c; }
}
using namespace cs1i;

// ---- Registre ----
CS1_API int32_t cs1i_load_registry(const char* json_utf8){ if(!json_utf8)return 0; return loadDoc(std::string(json_utf8))?1:0; }
// Charge le schema editable des records de tables (cs1_tables.json). Optionnel : sans
// appel, les schemas par defaut (Ghidra) sont utilises. Renvoie 1.
CS1_API int32_t cs1i_load_tables_schema(const char* json_utf8){ cs1tbl::loadSchemaJson(json_utf8); return 1; }
CS1_API int32_t cs1i_reg_count(){ return (int32_t)G.regs.size(); }
CS1_API const char* cs1i_reg_name(int32_t i){ if(i<0||i>=(int)G.regs.size())return nullptr; return G.regs[i].name.c_str(); }
CS1_API const char* cs1i_reg_opname(int32_t i){ if(i<0||i>=(int)G.regs.size())return nullptr; return G.regs[i].opname.c_str(); }
CS1_API int32_t cs1i_reg_argc(int32_t i){ if(i<0||i>=(int)G.regs.size())return -1; return (int32_t)G.regs[i].argTypes.size(); }
CS1_API const char* cs1i_reg_argtype(int32_t i,int32_t j){ if(i<0||i>=(int)G.regs.size()||j<0||j>=(int)G.regs[i].argTypes.size())return nullptr; return G.regs[i].argTypes[j].c_str(); }
CS1_API int32_t cs1i_reg_find(const char* name){ if(!name)return -1; for(size_t i=0;i<G.regs.size();i++)if(G.regs[i].name==name)return (int)i; return -1; }

// ---- Document ----
CS1_API IDoc* cs1i_open(const uint8_t* data,int32_t len,const char* filename){
  if(!G.loaded)return nullptr;
  IDoc* d=new IDoc(); d->base=cs1_doc_load(data,len); if(!d->base){delete d;return nullptr;}
  d->scene=baseName(filename); d->ui=G.isUiFile(d->scene);
  int nf=cs1_doc_func_count(d->base); d->dec.resize(nf); d->isCode.resize(nf,0);
  d->isTable.resize(nf,0); d->tables.resize(nf);
  d->funcEndId.resize(nf,-1); d->origStart.resize(nf,0); d->origEnd.resize(nf,0);
  for(int i=0;i<nf;i++){ long ost=d->base->funcs[i].ostart; d->origStart[i]=ost; d->origEnd[i]=ost+(long)d->base->funcs[i].bytes.size();
    const char* nm=cs1_doc_func_name(d->base,i); std::string tk; int tid;
    // Routage PAR NOM (comme guess_type_by_name / le modele Python) : une fonction
    // nommee-table est TOUJOURS une table, jamais du code -- meme si ses octets
    // seraient decodables en opcodes (cas des tables malformees/perimees).
    if(nm && cs1tbl::typeForName(nm,tk,tid)){
      const uint8_t* fb=cs1_doc_func_bytes(d->base,i); int fl=cs1_doc_func_size(d->base,i);
      cs1tbl::decode(nm,fb,fl,d->tables[i]); d->isTable[i]=1; continue; }
    int ty=cs1_doc_func_type(d->base,i);
    if(ty>0 && cs1tbl::typeForParserId(ty,tk,tid)){
      const uint8_t* fb=cs1_doc_func_bytes(d->base,i); int fl=cs1_doc_func_size(d->base,i);
      cs1tbl::decodeAs(tk,tid,fb,fl,d->tables[i]); d->isTable[i]=1; continue; }
    if(ty==0){ const uint8_t* fb=cs1_doc_func_bytes(d->base,i); int fl=cs1_doc_func_size(d->base,i);
      std::vector<Instr> v; if(decodeFunc(fb,fl,d->ui,ost,v)){ d->dec[i]=std::move(v); d->isCode[i]=1; } } }
  // header original conserve verbatim (byte-perfect) : tout ce qui precede la 1re fonction.
  // paOff = ptr_area (table des offsets de fonctions @0x08), repatchee a la serialisation.
  d->origNb=nf;
  if(nf>0 && d->origStart[0]>0 && d->origStart[0]<=len){
    d->origHeader.assign(data, data+d->origStart[0]);
    auto R=[&](int o){ return (long)(data[o]|(data[o+1]<<8)|(data[o+2]<<16)|((uint32_t)data[o+3]<<24)); };
    d->origFnpos=R(0x04); d->origPa=R(0x08); d->paOff=d->origPa;
  }
  // ids stables + carte offset->id (instructions + fin de fonction)
  std::map<long,long> off2id;
  for(int i=0;i<nf;i++){ if(!d->isCode[i])continue;
    for(auto&in:d->dec[i]){ in.id=d->nextId++; off2id[in.origOff]=in.id; }
    d->funcEndId[i]=d->nextId++; off2id[d->origEnd[i]]=d->funcEndId[i]; }
  // resolution des ptr32 (sauts) en references symboliques
  for(int i=0;i<nf;i++){ if(!d->isCode[i])continue; for(auto&in:d->dec[i]) resolveRefs(in.args,off2id); }
  return d;
}
CS1_API void cs1i_close(IDoc* d){ if(!d)return; if(d->base)cs1_doc_free(d->base); delete d; }
CS1_API const char* cs1i_scene_name(IDoc* d){ return d?d->scene.c_str():nullptr; }
CS1_API int32_t cs1i_func_count(IDoc* d){ return d?(int32_t)d->dec.size():0; }
CS1_API const char* cs1i_func_name(IDoc* d,int32_t i){ return d?cs1_doc_func_name(d->base,i):nullptr; }
CS1_API int32_t cs1i_func_is_code(IDoc* d,int32_t i){ if(!d||i<0||i>=(int)d->isCode.size())return 0; return d->isCode[i]; }

// ---- Tables de donnees (structure separee du code, exposee a l'editeur) ----
static cs1tbl::Table* tbl(IDoc* d,int i){ if(!d||i<0||i>=(int)d->isTable.size()||!d->isTable[i])return nullptr; return &d->tables[i]; }
CS1_API int32_t cs1i_func_is_table(IDoc* d,int32_t i){ return tbl(d,i)?1:0; }
CS1_API const char* cs1i_table_kind(IDoc* d,int32_t i){ cs1tbl::Table*t=tbl(d,i); return t?t->kind.c_str():nullptr; }
CS1_API int32_t cs1i_table_id(IDoc* d,int32_t i){ cs1tbl::Table*t=tbl(d,i); return t?t->id:-1; }
CS1_API int32_t cs1i_table_is_stale(IDoc* d,int32_t i){ cs1tbl::Table*t=tbl(d,i); return (t&&t->stale)?1:0; }
CS1_API int32_t cs1i_table_field_count(IDoc* d,int32_t i){ cs1tbl::Table*t=tbl(d,i); return t?(int32_t)t->fields.size():-1; }
static cs1tbl::TField* tfld(IDoc* d,int i,int j){ cs1tbl::Table*t=tbl(d,i); if(!t||j<0||j>=(int)t->fields.size())return nullptr; return &t->fields[j]; }
CS1_API const char* cs1i_table_field_type(IDoc* d,int32_t i,int32_t j){ cs1tbl::TField*f=tfld(d,i,j); return f?f->type.c_str():nullptr; }
CS1_API long cs1i_table_field_i(IDoc* d,int32_t i,int32_t j){ cs1tbl::TField*f=tfld(d,i,j); return f?f->ival:0; }
CS1_API double cs1i_table_field_f(IDoc* d,int32_t i,int32_t j){ cs1tbl::TField*f=tfld(d,i,j); return f?f->fval:0; }
CS1_API const char* cs1i_table_field_text(IDoc* d,int32_t i,int32_t j){ cs1tbl::TField*f=tfld(d,i,j); return (f&&f->type=="string")?f->text.c_str():nullptr; }
CS1_API int32_t cs1i_table_field_bytes(IDoc* d,int32_t i,int32_t j,uint8_t* out,int32_t cap){ cs1tbl::TField*f=tfld(d,i,j); if(!f)return -1; int n=(int)f->raw.size(); if(out&&cap>0){ int c=n<cap?n:cap; memcpy(out,f->raw.data(),c); } return n; }

// ---- Edition de champs de table (scalaire/f32/string-largeur-fixe = taille preservee) ----
CS1_API int32_t cs1i_table_set_field_i(IDoc* d,int32_t i,int32_t j,long v){ cs1tbl::TField*f=tfld(d,i,j); if(!f)return 0;
  if(f->type=="u8"){ f->raw.assign(1,(uint8_t)(v&0xff)); f->ival=(uint8_t)(v&0xff); return 1; }
  if(f->type=="s16"){ f->raw={(uint8_t)(v&0xff),(uint8_t)((v>>8)&0xff)}; f->ival=(int16_t)(v&0xffff); return 1; }
  if(f->type=="s32"){ f->raw={(uint8_t)(v&0xff),(uint8_t)((v>>8)&0xff),(uint8_t)((v>>16)&0xff),(uint8_t)((v>>24)&0xff)}; f->ival=(int32_t)v; return 1; }
  return 0; }
CS1_API int32_t cs1i_table_set_field_f(IDoc* d,int32_t i,int32_t j,double v){ cs1tbl::TField*f=tfld(d,i,j); if(!f||f->type!="f32")return 0;
  float fv=(float)v; uint32_t u; memcpy(&u,&fv,4); f->raw={(uint8_t)(u&0xff),(uint8_t)((u>>8)&0xff),(uint8_t)((u>>16)&0xff),(uint8_t)((u>>24)&0xff)}; f->fval=fv; return 1; }
// string : si suivi d'un champ 'fill' (champ largeur fixe), on rebalance le fill -> taille du record inchangee.
// sinon (string libre type FC_autoX) la taille varie -> serialize reflowe (offsets + sauts recalcules).
CS1_API int32_t cs1i_table_set_field_text(IDoc* d,int32_t i,int32_t j,const char* s){ cs1tbl::Table*t=tbl(d,i); if(!t||!s||j<0||j>=(int)t->fields.size())return 0;
  cs1tbl::TField& f=t->fields[j]; if(f.type!="string")return 0;
  std::vector<uint8_t> nr((const uint8_t*)s,(const uint8_t*)s+strlen(s)); nr.push_back(0);
  if(j+1<(int)t->fields.size() && t->fields[j+1].fill>=0){ int width=t->fields[j+1].fill;
    if((long)nr.size()>width)return 0; // ne rentre pas dans le champ largeur fixe
    f.raw=nr; f.text=s; t->fields[j+1].raw.assign(width-(long)nr.size(),0); return 1; }
  f.raw=nr; f.text=s; return 1; }
CS1_API int32_t cs1i_table_set_field_bytes(IDoc* d,int32_t i,int32_t j,const uint8_t* b,int32_t n){ cs1tbl::TField*f=tfld(d,i,j); if(!f||!b||n<0)return 0; f->raw.assign(b,b+n); return 1; }

// ---- Ajout/suppression de CHAMPS dans une table (pour ajouter/retirer des lignes) ----
// Ajouter une ligne = inserer la sequence de champs du record puis incrementer le count
// (via set_field_i). La taille de la table change -> serialize relocalise le fichier.
CS1_API int32_t cs1i_table_field_insert(IDoc* d,int32_t i,int32_t at,const char* type,int32_t size){
  cs1tbl::Table*t=tbl(d,i); if(!t||!type||size<0)return 0; if(at<0)at=0; if(at>(int)t->fields.size())at=(int)t->fields.size();
  cs1tbl::TField f; f.type=type; f.raw.assign(size,0);
  t->fields.insert(t->fields.begin()+at,f); t->dataEnd+=size; return 1; }
CS1_API int32_t cs1i_table_field_delete(IDoc* d,int32_t i,int32_t j){
  cs1tbl::Table*t=tbl(d,i); if(!t||j<0||j>=(int)t->fields.size())return 0;
  t->dataEnd-=(long)t->fields[j].raw.size(); t->fields.erase(t->fields.begin()+j); return 1; }
// Longueur (octets) d'un record de schema donne (pour dimensionner l'insertion d'une ligne).
CS1_API int32_t cs1i_schema_record_len(const char* name){ if(!name)return -1; auto& s=cs1tbl::effSchema(name); int n=0; for(auto&f:s)n+=f.size; return n; }
CS1_API int32_t cs1i_schema_field_count(const char* name){ if(!name)return -1; return (int)cs1tbl::effSchema(name).size(); }
CS1_API const char* cs1i_schema_field_type(const char* name,int32_t j){ if(!name)return nullptr; auto& s=cs1tbl::effSchema(name); if(j<0||j>=(int)s.size())return nullptr; return s[j].type.c_str(); }
CS1_API int32_t cs1i_schema_field_size(const char* name,int32_t j){ if(!name)return -1; auto& s=cs1tbl::effSchema(name); if(j<0||j>=(int)s.size())return -1; return s[j].size; }

// ---- Instructions d'une fonction ----
static Instr* pick(IDoc*d,int f,int k){ if(!d||f<0||f>=(int)d->dec.size())return nullptr; auto&v=d->dec[f]; if(k<0||k>=(int)v.size())return nullptr; return &v[k]; }
CS1_API int32_t cs1i_func_ninstr(IDoc* d,int32_t f){ if(!d||f<0||f>=(int)d->dec.size())return -1; return (int32_t)d->dec[f].size(); }
CS1_API int32_t cs1i_instr_reg(IDoc* d,int32_t f,int32_t k){ Instr*in=pick(d,f,k); return in?in->reg:-1; }
CS1_API int32_t cs1i_instr_op(IDoc* d,int32_t f,int32_t k){ cs1i::Instr*in=pick(d,f,k); return in?in->op:-1; }
CS1_API const char* cs1i_instr_name(IDoc* d,int32_t f,int32_t k){ Instr*in=pick(d,f,k); if(!in||in->reg<0)return nullptr; return G.regs[in->reg].name.c_str(); }
CS1_API int32_t cs1i_instr_argc(IDoc* d,int32_t f,int32_t k){ Instr*in=pick(d,f,k); if(!in)return -1; return visibleCount(in->args); }
CS1_API const char* cs1i_instr_argtype(IDoc* d,int32_t f,int32_t k,int32_t a){ Instr*in=pick(d,f,k); if(!in)return nullptr; int r=visibleToReal(in->args,a); if(r<0)return nullptr;
  const Arg&x=in->args[r]; if(x.kind==0)return x.type.c_str(); return x.kind==1?"string":x.kind==2?"expr":x.kind==3?"dialog":"bytes"; }
// annotations semantiques de l'operande visible a (definies dans le json, indexees en ordre visible)
static const RegInstr* instrReg(IDoc*d,int f,int k){ Instr*in=pick(d,f,k); if(!in||in->reg<0||in->reg>=(int)G.regs.size())return nullptr; return &G.regs[in->reg]; }
CS1_API const char* cs1i_instr_argname(IDoc* d,int32_t f,int32_t k,int32_t a){ const RegInstr*r=instrReg(d,f,k); if(!r||a<0||a>=(int)r->argNames.size())return nullptr; const std::string&s=r->argNames[a]; return s.empty()?nullptr:s.c_str(); }
CS1_API const char* cs1i_instr_argsem(IDoc* d,int32_t f,int32_t k,int32_t a){ const RegInstr*r=instrReg(d,f,k); if(!r||a<0||a>=(int)r->argSems.size())return nullptr; const std::string&s=r->argSems[a]; return s.empty()?nullptr:s.c_str(); }
CS1_API const char* cs1i_instr_argsem_arg(IDoc* d,int32_t f,int32_t k,int32_t a){ const RegInstr*r=instrReg(d,f,k); if(!r||a<0||a>=(int)r->argSemArgs.size())return nullptr; const std::string&s=r->argSemArgs[a]; return s.empty()?nullptr:s.c_str(); }
CS1_API int32_t cs1i_instr_argsem_span(IDoc* d,int32_t f,int32_t k,int32_t a){ const RegInstr*r=instrReg(d,f,k); if(!r||a<0||a>=(int)r->argSemSpans.size())return 1; return r->argSemSpans[a]; }
// idem au niveau registre (definition), sans instance
CS1_API const char* cs1i_reg_argname(int32_t i,int32_t j){ if(i<0||i>=(int)G.regs.size()||j<0||j>=(int)G.regs[i].argNames.size())return nullptr; const std::string&s=G.regs[i].argNames[j]; return s.empty()?nullptr:s.c_str(); }
CS1_API const char* cs1i_reg_argsem(int32_t i,int32_t j){ if(i<0||i>=(int)G.regs.size()||j<0||j>=(int)G.regs[i].argSems.size())return nullptr; const std::string&s=G.regs[i].argSems[j]; return s.empty()?nullptr:s.c_str(); }
CS1_API const char* cs1i_reg_argsem_arg(int32_t i,int32_t j){ if(i<0||i>=(int)G.regs.size()||j<0||j>=(int)G.regs[i].argSemArgs.size())return nullptr; const std::string&s=G.regs[i].argSemArgs[j]; return s.empty()?nullptr:s.c_str(); }
CS1_API int32_t cs1i_reg_argsem_span(int32_t i,int32_t j){ if(i<0||i>=(int)G.regs.size()||j<0||j>=(int)G.regs[i].argSemSpans.size())return 1; return G.regs[i].argSemSpans[j]; }
CS1_API long cs1i_instr_argi(IDoc* d,int32_t f,int32_t k,int32_t a){ Instr*in=pick(d,f,k); if(!in)return 0; int r=visibleToReal(in->args,a); return r<0?0:in->args[r].ival; }
CS1_API double cs1i_instr_argf(IDoc* d,int32_t f,int32_t k,int32_t a){ Instr*in=pick(d,f,k); if(!in)return 0; int r=visibleToReal(in->args,a); return r<0?0:in->args[r].fval; }
CS1_API int32_t cs1i_instr_argbytes(IDoc* d,int32_t f,int32_t k,int32_t a,uint8_t* out,int32_t cap){ Instr*in=pick(d,f,k); if(!in)return -1; int r=visibleToReal(in->args,a); if(r<0)return -1;
  const auto&raw=in->args[r].raw; int n=(int)raw.size(); if(out&&cap>=n)memcpy(out,raw.data(),n); return n; }

// ===== Introspection des EXPRESSIONS (pour l'editeur de maps) =====
// Une expression = suite de sous-ops : operateurs (pop/push) + valeurs poussees (types),
// + eventuellement des instructions imbriquees (redispatch). L'editeur sait ainsi ou est
// l'expression (arg de type "expr") et de quoi elle est faite (chaque element type).
static const char* exprOpName(uint8_t s){
  switch(s){
    case 0x02:return "=="; case 0x03:return "!="; case 0x04:return "<"; case 0x05:return ">";
    case 0x06:return "<="; case 0x07:return ">="; case 0x08:return "==0"; case 0x09:return "&&";
    case 0x0a: case 0x19:return "&"; case 0x0b: case 0x1b:return "|";
    case 0x0c: case 0x17:return "+"; case 0x0d: case 0x18:return "-"; case 0x0e:return "neg";
    case 0x0f: case 0x1a:return "^"; case 0x10: case 0x14:return "*";
    case 0x11: case 0x15:return "/"; case 0x12: case 0x16:return "%"; case 0x1d:return "~";
    case 0x13:return "nop"; case 0x22:return "rand"; default:return nullptr;
  }
}
static cs1i::Arg* exprArg(IDoc*d,int f,int k,int a){ cs1i::Instr*in=pick(d,f,k); if(!in)return nullptr; int r=visibleToReal(in->args,a); if(r<0)return nullptr; cs1i::Arg&x=in->args[r]; return x.kind==2?&x:nullptr; }
static long exprPayVal(const cs1i::ExprElem&el){ long v=0; for(size_t i=0;i<el.payload.size();i++)v|=(long)el.payload[i]<<(8*i); return v; }
static const char* exprKind(uint8_t s){
  switch(s){ case 0x01:return "end"; case 0x1c:return "redispatch"; case 0x00:return "push";
    case 0x1e:return "flag"; case 0x1f:return "reg"; case 0x20:return "sys"; case 0x23:return "work";
    case 0x21:return "query"; case 0x22:return "rand"; }
  return exprOpName(s)?"op":"nop";
}
static std::string exprElemLabel(const cs1i::ExprElem&el){
  char buf[64];
  switch(el.subop){
    case 0x01:return "END"; case 0x1c:return "call";
    case 0x00:snprintf(buf,64,"push %ld",exprPayVal(el));return buf;
    case 0x1e:snprintf(buf,64,"flag[%ld]",exprPayVal(el));return buf;
    case 0x1f:snprintf(buf,64,"reg[%ld]",exprPayVal(el));return buf;
    case 0x20:snprintf(buf,64,"sys[%ld]",exprPayVal(el));return buf;
    case 0x23:snprintf(buf,64,"work[%ld]",exprPayVal(el));return buf;
    case 0x21:{ long u=el.payload.size()>=2?(el.payload[0]|(el.payload[1]<<8)):0; long b2=el.payload.size()>=3?el.payload[2]:0; snprintf(buf,64,"query[%ld,%ld]",u,b2);return buf; }
  }
  const char* op=exprOpName(el.subop); if(op)return op;
  snprintf(buf,64,"op%02X",el.subop); return buf;
}
CS1_API int32_t cs1i_arg_is_expr(IDoc*d,int32_t f,int32_t k,int32_t a){ return exprArg(d,f,k,a)?1:0; }
CS1_API int32_t cs1i_expr_count(IDoc*d,int32_t f,int32_t k,int32_t a){ cs1i::Arg*x=exprArg(d,f,k,a); return x?(int32_t)x->expr.size():-1; }
CS1_API int32_t cs1i_expr_subop(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t i){ cs1i::Arg*x=exprArg(d,f,k,a); if(!x||i<0||i>=(int)x->expr.size())return -1; return x->expr[i].subop; }
CS1_API const char* cs1i_expr_kind(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t i){ cs1i::Arg*x=exprArg(d,f,k,a); if(!x||i<0||i>=(int)x->expr.size())return nullptr; return exprKind(x->expr[i].subop); }
CS1_API long cs1i_expr_value(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t i){ cs1i::Arg*x=exprArg(d,f,k,a); if(!x||i<0||i>=(int)x->expr.size())return 0; return exprPayVal(x->expr[i]); }
CS1_API const char* cs1i_expr_elem_label(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t i){ static std::string s; cs1i::Arg*x=exprArg(d,f,k,a); if(!x||i<0||i>=(int)x->expr.size())return nullptr; s=exprElemLabel(x->expr[i]); return s.c_str(); }
CS1_API int32_t cs1i_expr_nested_reg(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t i){ cs1i::Arg*x=exprArg(d,f,k,a); if(!x||i<0||i>=(int)x->expr.size())return -1; auto&el=x->expr[i]; return (el.subop==0x1c&&el.nested)?el.nested->reg:-1; }
CS1_API const char* cs1i_expr_nested_name(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t i){ cs1i::Arg*x=exprArg(d,f,k,a); if(!x||i<0||i>=(int)x->expr.size())return nullptr; auto&el=x->expr[i]; if(el.subop!=0x1c||!el.nested||el.nested->reg<0)return nullptr; return G.regs[el.nested->reg].name.c_str(); }
CS1_API const char* cs1i_expr_text(IDoc*d,int32_t f,int32_t k,int32_t a){ static std::string s; cs1i::Arg*x=exprArg(d,f,k,a); if(!x)return nullptr; s.clear();
  for(size_t i=0;i<x->expr.size();i++){ if(i)s+=" "; auto&el=x->expr[i]; if(el.subop==0x1c&&el.nested&&el.nested->reg>=0){ s+="call "; s+=G.regs[el.nested->reg].name; } else s+=exprElemLabel(el); } return s.c_str(); }

// ---- Edition ----
CS1_API int32_t cs1i_instr_set_i(IDoc* d,int32_t f,int32_t k,int32_t a,long v){ Instr*in=pick(d,f,k); if(!in)return 0; int r=visibleToReal(in->args,a); if(r<0||in->args[r].kind!=0)return 0; in->args[r].ival=v; return 1; }
CS1_API int32_t cs1i_instr_set_f(IDoc* d,int32_t f,int32_t k,int32_t a,double v){ Instr*in=pick(d,f,k); if(!in)return 0; int r=visibleToReal(in->args,a); if(r<0||in->args[r].kind!=0)return 0; in->args[r].fval=v; return 1; }
CS1_API int32_t cs1i_instr_set_s(IDoc* d,int32_t f,int32_t k,int32_t a,const char* s){ Instr*in=pick(d,f,k); if(!in||!s)return 0; int r=visibleToReal(in->args,a); if(r<0||in->args[r].kind!=1)return 0; in->args[r].raw.assign((const uint8_t*)s,(const uint8_t*)s+strlen(s)); return 1; }
CS1_API int32_t cs1i_instr_remove(IDoc* d,int32_t f,int32_t k){ if(!d||f<0||f>=(int)d->dec.size())return 0; auto&v=d->dec[f]; if(k<0||k>=(int)v.size())return 0; v.erase(v.begin()+k); return 1; }

// ---- Re-encode une fonction (octets) ----
CS1_API int32_t cs1i_func_encode(IDoc* d,int32_t f,uint8_t* out,int32_t cap){ if(!d||f<0||f>=(int)d->dec.size())return -1;
  std::vector<uint8_t> buf; for(auto&in:d->dec[f]){ if(!encodeInstr(in,d->ui,buf))return -1; }
  int n=(int)buf.size(); if(out&&cap>=n)memcpy(out,buf.data(),n); return n; }
CS1_API const uint8_t* cs1i_func_orig_bytes(IDoc* d,int32_t f){ return d?cs1_doc_func_bytes(d->base,f):nullptr; }
CS1_API int32_t cs1i_func_orig_size(IDoc* d,int32_t f){ return d?cs1_doc_func_size(d->base,f):-1; }

// ============ Insertion / serialisation avec relocation complete ============
static void buildDefault(const NodeList& nodes,const std::vector<long>& path,size_t& pi,cs1i::Ctx& c,std::vector<cs1i::Arg>& args){
  using namespace cs1i;
  for(auto&n:nodes){
    if(n.k==Node::IF){ bool ok; bool r=evalCond(n.iff->cond,c,ok); buildDefault(r?n.iff->then_:n.iff->else_,path,pi,c,args); continue; }
    if(n.k==Node::IFVAL){ bool take=n.ifv->useIn?(std::find(n.ifv->in.begin(),n.ifv->in.end(),c.sel16)!=n.ifv->in.end()):(c.sel16==n.ifv->eq); buildDefault(take?n.ifv->then_:n.ifv->else_,path,pi,c,args); continue; }
    if(n.k==Node::LOOP){ Arg a; a.kind=7; a.type="list"; args.push_back(a); continue; } // 0 iterations par defaut
    if(n.k==Node::SCALAR){ Arg a; a.kind=0; a.type=n.t; a.ival=0; a.fval=0;
      if(n.role==1||n.role==3){ a.hidden=true; a.kind=6; long v=(pi<path.size()&&path[pi]>=0)?path[pi]:0; pi++; a.ival=v;
        if(n.role==3)c.sel16=v&0xffff; else { if(!c.haveSel){c.sel=v;c.haveSel=true;} else c.sel2=v; } }
      else if(n.role==4){ long v=(pi<path.size()&&path[pi]>=0)?path[pi]:0; pi++; a.ival=v; }
      if(n.role==2)c.count=0; if(n.role==3)c.sel16=0; if(n.t=="s16"&&c.control<0)c.control=0;
      args.push_back(a); continue; }
    if(n.k==Node::STR){ Arg a;a.kind=1; args.push_back(a); c.laststr=1; continue; }
    if(n.k==Node::EXPR){ Arg a;a.kind=2; ExprElem t; t.subop=0x01; a.expr.push_back(t); a.raw={0x01}; args.push_back(a); continue; }  // expr vide
    if(n.k==Node::DIALOG){ Arg a;a.kind=3;a.raw={0x00}; args.push_back(a); continue; }      // dialog vide (NUL)
    if(n.k==Node::BYTES){ Arg a;a.kind=4;a.raw.assign(n.size,0); args.push_back(a); continue; }
    if(n.k==Node::FILL){ Arg a;a.kind=5;a.hidden=true; args.push_back(a); continue; }
  }
}
static void fixRefs(std::vector<cs1i::Arg>& args,std::map<long,long>& id2new,IDoc* d,std::vector<uint32_t>& addrs){
  using namespace cs1i;
  for(auto&a:args){
    if(a.kind==0 && a.type=="ptr32"){
      if(a.isRef){ auto it=id2new.find(a.targetId); if(it!=id2new.end()) a.ival=it->second; }
      else { for(size_t g=0;g<d->origStart.size();g++) if(a.ival>=d->origStart[g] && a.ival<d->origEnd[g]){ a.ival=a.ival+((long)addrs[g]-d->origStart[g]); break; } }
    }
    if(a.kind==7) for(auto&gg:a.groups) fixRefs(gg,id2new,d,addrs);
    if(a.kind==2) for(auto&el:a.expr) if(el.nested) fixRefs(el.nested->args,id2new,d,addrs);
  }
}

CS1_API int32_t cs1i_instr_insert(IDoc* d,int32_t f,int32_t pos,const char* name){
  if(!d||f<0||f>=(int)d->dec.size()||!d->isCode[f]||!name)return -1;
  int ri=cs1i_reg_find(name); if(ri<0)return -1; cs1i::RegInstr& r=G.regs[ri];
  cs1i::Instr in; in.op=r.op; in.reg=ri; in.path=r.path; in.id=d->nextId++; in.origOff=-1;
  cs1i::Ctx c; size_t pi=0; buildDefault(r.read,r.path,pi,c,in.args);
  long nid=in.id; auto& v=d->dec[f]; if(pos<0)pos=0; if(pos>(int)v.size())pos=(int)v.size();
  v.insert(v.begin()+pos,std::move(in)); return (int32_t)nid;
}
CS1_API int32_t cs1i_instr_replace(IDoc* d,int32_t f,int32_t k,const char* name){
  if(!d||f<0||f>=(int)d->dec.size()||!d->isCode[f]||!name)return 0;
  auto&v=d->dec[f]; if(k<0||k>=(int)v.size())return 0;
  int ri=cs1i_reg_find(name); if(ri<0)return 0; cs1i::RegInstr&r=G.regs[ri];
  cs1i::Instr in; in.op=r.op; in.reg=ri; in.path=r.path;
  in.id=v[k].id; in.origOff=v[k].origOff;
  cs1i::Ctx c; size_t pi=0; buildDefault(r.read,r.path,pi,c,in.args);
  v[k]=std::move(in); return 1;
}
CS1_API int32_t cs1i_instr_move(IDoc* d,int32_t f,int32_t from,int32_t to){
  if(!d||f<0||f>=(int)d->dec.size())return 0; auto&v=d->dec[f];
  if(from<0||from>=(int)v.size()||to<0||to>=(int)v.size())return 0;
  cs1i::Instr t=std::move(v[from]); v.erase(v.begin()+from); v.insert(v.begin()+to,std::move(t)); return 1;
}
// ---- Ajout / suppression de FONCTIONS entieres (tables) ----
// Supprime la fonction f (et toutes ses metadonnees). Le nb de fonctions change ->
// la serialisation reconstruira le header fidelement.
CS1_API int32_t cs1i_func_remove(IDoc* d,int32_t f){
  if(!d||f<0||f>=(int)d->dec.size())return 0;
  d->base->funcs.erase(d->base->funcs.begin()+f);
  d->dec.erase(d->dec.begin()+f); d->isCode.erase(d->isCode.begin()+f);
  d->isTable.erase(d->isTable.begin()+f); d->tables.erase(d->tables.begin()+f);
  d->origStart.erase(d->origStart.begin()+f); d->origEnd.erase(d->origEnd.begin()+f);
  d->funcEndId.erase(d->funcEndId.begin()+f);
  return 1;
}
// Insere une nouvelle table nommee 'name' a l'index pos, avec 'bytes' comme contenu
// initial (le contenu de la table, hors terminateur -- un op1 + padding sont ajoutes).
// Renvoie l'index de la nouvelle fonction, ou -1.
CS1_API int32_t cs1i_table_add(IDoc* d,int32_t pos,const char* name,const uint8_t* bytes,int32_t len){
  if(!d||!name)return -1; std::string tk; int tid; if(!cs1tbl::typeForName(name,tk,tid))return -1;
  int nf=(int)d->dec.size(); if(pos<0)pos=nf; if(pos>nf)pos=nf;
  // octets complets de la fonction = contenu + terminateur op1 + padding d'alignement 4
  std::vector<uint8_t> fbytes; if(bytes&&len>0)fbytes.assign(bytes,bytes+len); fbytes.push_back(0x01);
  while(fbytes.size()%4)fbytes.push_back(0x00);
  cs1ed::Func nfn; nfn.name=name; nfn.named=true; nfn.type=tid; nfn.bytes=fbytes; nfn.hasRawPtrs=false; nfn.ostart=0; nfn.decoded=false;
  d->base->funcs.insert(d->base->funcs.begin()+pos,std::move(nfn));
  d->dec.insert(d->dec.begin()+pos,std::vector<cs1i::Instr>());
  d->isCode.insert(d->isCode.begin()+pos,0);
  cs1tbl::Table t; cs1tbl::decode(name,d->base->funcs[pos].bytes.data(),(long)d->base->funcs[pos].bytes.size(),t);
  d->isTable.insert(d->isTable.begin()+pos,1);
  d->tables.insert(d->tables.begin()+pos,std::move(t));
  d->origStart.insert(d->origStart.begin()+pos,-1); d->origEnd.insert(d->origEnd.begin()+pos,-1);
  d->funcEndId.insert(d->funcEndId.begin()+pos,d->nextId++);
  return pos;
}
// definit une cible de saut de maniere SYMBOLIQUE : (tf,ti) instruction cible, ti<0 = fin de fonction tf
CS1_API int32_t cs1i_instr_set_jump(IDoc* d,int32_t f,int32_t k,int32_t a,int32_t tf,int32_t ti){
  cs1i::Instr* in=pick(d,f,k); if(!in)return 0; int r=visibleToReal(in->args,a);
  if(r<0||in->args[r].type!="ptr32")return 0; long tid;
  if(ti<0){ if(tf<0||tf>=(int)d->funcEndId.size()||d->funcEndId[tf]<0)return 0; tid=d->funcEndId[tf]; }
  else { if(tf<0||tf>=(int)d->dec.size()||ti>=(int)d->dec[tf].size())return 0; tid=d->dec[tf][ti].id; }
  in->args[r].isRef=true; in->args[r].targetId=tid; return 1;
}

// Renvoie la cible symbolique courante d'un ptr32. Le resultat vaut l'index de
// l'instruction, -1 pour la fin d'une fonction, -2 pour une adresse brute non resolue
// et -3 si l'operande n'est pas un saut. outFunction recoit la fonction cible.
CS1_API int32_t cs1i_instr_jump_target(IDoc* d,int32_t f,int32_t k,int32_t a,int32_t* outFunction){
  if(outFunction)*outFunction=-1; cs1i::Instr* in=pick(d,f,k); if(!in)return -3;
  int r=visibleToReal(in->args,a); if(r<0||in->args[r].type!="ptr32")return -3;
  cs1i::Arg&arg=in->args[r];
  if(arg.isRef){
    for(int tf=0;tf<(int)d->dec.size();tf++){
      for(int ti=0;ti<(int)d->dec[tf].size();ti++) if(d->dec[tf][ti].id==arg.targetId){
        if(outFunction)*outFunction=tf; return ti;
      }
      if(tf<(int)d->funcEndId.size()&&d->funcEndId[tf]==arg.targetId){
        if(outFunction)*outFunction=tf; return -1;
      }
    }
    return -2;
  }
  for(int tf=0;tf<(int)d->dec.size();tf++){
    for(int ti=0;ti<(int)d->dec[tf].size();ti++) if(d->dec[tf][ti].origOff==arg.ival){
      if(outFunction)*outFunction=tf; return ti;
    }
    if(tf<(int)d->origEnd.size()&&d->origEnd[tf]==arg.ival){
      if(outFunction)*outFunction=tf; return -1;
    }
  }
  return -2;
}

// ---- Iterations de boucle (instructions a corps repete, ex op6 ; kind==7) ----
// Le champ count est auto-synchronise a l'encodage : ajouter/retirer une iteration suffit.
static cs1i::Arg* loopArg(IDoc*d,int f,int k,int a){ cs1i::Instr*in=pick(d,f,k); if(!in)return nullptr; int r=visibleToReal(in->args,a); if(r<0)return nullptr; cs1i::Arg&x=in->args[r]; return x.kind==7?&x:nullptr; }
CS1_API int32_t cs1i_arg_is_loop(IDoc*d,int32_t f,int32_t k,int32_t a){ return loopArg(d,f,k,a)?1:0; }
CS1_API int32_t cs1i_arg_loop_count(IDoc*d,int32_t f,int32_t k,int32_t a){ cs1i::Arg*x=loopArg(d,f,k,a); return x?(int32_t)x->groups.size():-1; }
// duplique l'iteration 'it' (copie inseree juste apres) -> pratique pour "ajouter une ligne"
CS1_API int32_t cs1i_arg_loop_dup(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t it){ cs1i::Arg*x=loopArg(d,f,k,a); if(!x||it<0||it>=(int)x->groups.size())return 0; x->groups.insert(x->groups.begin()+it+1,x->groups[it]); return 1; }
CS1_API int32_t cs1i_arg_loop_remove(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t it){ cs1i::Arg*x=loopArg(d,f,k,a); if(!x||it<0||it>=(int)x->groups.size())return 0; x->groups.erase(x->groups.begin()+it); return 1; }
CS1_API int32_t cs1i_arg_loop_elem_argc(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t it){ cs1i::Arg*x=loopArg(d,f,k,a); if(!x||it<0||it>=(int)x->groups.size())return -1; return (int32_t)x->groups[it].size(); }
CS1_API long cs1i_arg_loop_elem_i(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t it,int32_t e){ cs1i::Arg*x=loopArg(d,f,k,a); if(!x||it<0||it>=(int)x->groups.size())return 0; auto&g=x->groups[it]; if(e<0||e>=(int)g.size())return 0; return g[e].ival; }
CS1_API int32_t cs1i_arg_loop_set_elem_i(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t it,int32_t e,long v){ cs1i::Arg*x=loopArg(d,f,k,a); if(!x||it<0||it>=(int)x->groups.size())return 0; auto&g=x->groups[it]; if(e<0||e>=(int)g.size()||g[e].kind!=0)return 0; g[e].ival=v; return 1; }

// ---- Construction/remplacement d'EXPRESSION (popup editeur) ----
// Expression = suite postfixe de jetons (operandes + operateurs), terminee par 0x01.
// Sous-ops operandes : 0x00 push(u32), 0x1e flag(u16), 0x1f reg(u8), 0x20 sys(u8),
// 0x21 query(u16+u8), 0x23 work(u8). Operateurs 0x02..0x1d (payload 0). Terminateur auto.
CS1_API int32_t cs1i_arg_expr_clear(IDoc*d,int32_t f,int32_t k,int32_t a){ cs1i::Arg*x=exprArg(d,f,k,a); if(!x)return 0;
  x->expr.clear(); cs1i::ExprElem t; t.subop=0x01; x->expr.push_back(t); return 1; }
CS1_API int32_t cs1i_arg_expr_push(IDoc*d,int32_t f,int32_t k,int32_t a,int32_t subop,long value){ cs1i::Arg*x=exprArg(d,f,k,a); if(!x)return 0;
  if(subop==0x1c||subop==0x01)return 0;
  cs1i::ExprElem el; el.subop=(uint8_t)subop; int pl=cs1i::exprPayload((uint8_t)subop);
  for(int b=0;b<pl;b++) el.payload.push_back((uint8_t)((value>>(8*b))&0xff));
  if(!x->expr.empty() && x->expr.back().subop==0x01) x->expr.insert(x->expr.end()-1,std::move(el));
  else x->expr.push_back(std::move(el));
  return 1; }

CS1_API const uint8_t* cs1i_serialize(IDoc* d,int32_t* outlen){
  if(!d){ if(outlen)*outlen=0; return nullptr; }
  int nf=(int)d->dec.size(); auto& F=d->base->funcs;
  std::vector<std::vector<uint8_t>> fb(nf); std::vector<std::vector<long>> ioff(nf);
  // pass1 : encode + offsets internes
  for(int f=0;f<nf;f++){
    if(d->isCode[f]){ std::vector<uint8_t> buf; for(auto&in:d->dec[f]){ ioff[f].push_back((long)buf.size()); if(!encodeInstr(in,d->ui,buf)){ if(outlen)*outlen=0; return nullptr; } } fb[f]=std::move(buf); }
    else if(f<(int)d->isTable.size() && d->isTable[f]){ // table : champs (edites) + queue d'origine (terminateur op1 + padding)
      std::vector<uint8_t> buf; for(auto&fld:d->tables[f].fields) buf.insert(buf.end(),fld.raw.begin(),fld.raw.end());
      long de=d->tables[f].dataEnd; if(de>=0 && de<=(long)F[f].bytes.size()) buf.insert(buf.end(),F[f].bytes.begin()+de,F[f].bytes.end());
      fb[f]=std::move(buf); }
    else fb[f]=F[f].bytes;
  }
  // Deux cas :
  //  - nb inchange : header ORIGINAL conserve verbatim (byte-perfect), on ne repatchera
  //    que la table des offsets de fonctions.
  //  - nb change (add/remove de table) : reconstruction FIDELE, en preservant l'ordre des
  //    sections d'origine (filename avant ou apres les tables, style scena vs al*).
  bool keepHeader = !d->origHeader.empty() && nf==d->origNb;
  std::string scene=d->base->scene; uint32_t nsize=(uint32_t)scene.size()+1, nb=nf;
  uint32_t names_len=0; for(int k=0;k<nf;k++) names_len+=(uint32_t)F[k].name.size()+1;
  uint32_t rb_fnpos=0x20, rb_pa=0, rb_nameptr=0, rb_namestr=0; // positions reconstruites
  uint32_t funcs_start;
  if(keepHeader){ funcs_start=(uint32_t)d->origStart[0]; }
  else {
    bool fileFirst = d->origHeader.empty() ? true : (d->origFnpos <= d->origPa);
    uint32_t off=0x20;
    uint32_t fileLen=nsize, tblLen=nb*4+nb*2+names_len;
    if(fileFirst){ rb_fnpos=off; off+=fileLen; rb_pa=off; rb_nameptr=rb_pa+nb*4; rb_namestr=rb_nameptr+nb*2; off+=tblLen; }
    else { rb_pa=off; rb_nameptr=rb_pa+nb*4; rb_namestr=rb_nameptr+nb*2; off+=tblLen; rb_fnpos=off; off+=fileLen; }
    uint32_t funcs_meta_end=off;
    int mult=4; if(nb>0 && !F[0].name.empty() && F[0].name[0]=='_') mult=0x10;
    funcs_start=funcs_meta_end + (((funcs_meta_end+mult-1)/mult)*mult - funcs_meta_end);
  }
  std::vector<uint32_t> addrs(nf); uint32_t acc=funcs_start;
  for(int k=0;k<nf;k++){ addrs[k]=acc; acc+=(uint32_t)fb[k].size(); }
  // id -> nouvel offset absolu
  std::map<long,long> id2new;
  for(int f=0;f<nf;f++){ if(!d->isCode[f])continue;
    for(size_t k=0;k<d->dec[f].size();k++) id2new[d->dec[f][k].id]=(long)addrs[f]+ioff[f][k];
    id2new[d->funcEndId[f]]=(long)addrs[f]+(long)fb[f].size(); }
  // pass2 : relocation de TOUS les ptr + re-encode fonctions code
  for(int f=0;f<nf;f++){ if(d->isCode[f]){
      for(auto&in:d->dec[f]) fixRefs(in.args,id2new,d,addrs);
      std::vector<uint8_t> buf; for(auto&in:d->dec[f]) encodeInstr(in,d->ui,buf); fb[f]=std::move(buf);
    } else if(d->origStart[f]>=0){ // fonctions non decodees d'origine : relocation uniforme pour type 0/-1
      long delta=(long)addrs[f]-d->origStart[f];
      if(delta!=0 && (F[f].type==0||F[f].type==-1)) cs1_reloc_jumps(fb[f],delta,d->origStart[f],d->origStart[f]+(long)fb[f].size());
    } }
  // assemblage fichier
  std::vector<uint8_t> h;
  auto wr32=[&](std::vector<uint8_t>&v,long p,uint32_t x){ if(p>=0&&p+4<=(long)v.size()){ v[p]=x&0xff;v[p+1]=(x>>8)&0xff;v[p+2]=(x>>16)&0xff;v[p+3]=(x>>24)&0xff; } };
  if(keepHeader){
    // header ORIGINAL verbatim + repatch de la table des offsets de fonctions -> byte-perfect
    h=d->origHeader;
    for(int k=0;k<nf;k++) wr32(h,d->paOff+4*k,addrs[k]);
  } else {
    // reconstruction fidele (nb a change) : header 0x20 + sections dans l'ordre d'origine.
    h.assign(funcs_start, 0);
    auto W32=[&](long p,uint32_t x){ h[p]=x&0xff;h[p+1]=(x>>8)&0xff;h[p+2]=(x>>16)&0xff;h[p+3]=(x>>24)&0xff; };
    auto W16=[&](long p,uint16_t x){ h[p]=x&0xff;h[p+1]=(x>>8)&0xff; };
    W32(0x00,0x20); W32(0x04,rb_fnpos); W32(0x08,rb_pa); W32(0x0C,nb*4);
    W32(0x10,rb_nameptr); W32(0x14,nb); W32(0x18,rb_namestr+names_len); W32(0x1C,0xABCDEF00);
    for(uint32_t i=0;i<nsize-1;i++) h[rb_fnpos+i]=(uint8_t)scene[i]; // filename (null deja a 0)
    for(int k=0;k<nf;k++) W32(rb_pa+4*k,addrs[k]);                   // table des offsets de fonctions
    uint32_t noff=0; for(int k=0;k<nf;k++){ W16(rb_nameptr+2*k,(uint16_t)(rb_namestr+noff)); noff+=(uint32_t)F[k].name.size()+1; }
    noff=0; for(int k=0;k<nf;k++){ for(char ch:F[k].name) h[rb_namestr+noff++]=(uint8_t)ch; h[rb_namestr+noff++]=0; }
  }
  for(int k=0;k<nf;k++) h.insert(h.end(),fb[k].begin(),fb[k].end());
  d->base->ser=std::move(h);
  if(outlen)*outlen=(int32_t)d->base->ser.size();
  return d->base->ser.data();
}
// offset absolu d'une instruction dans le fichier courant (utile pour verifier les sauts)
CS1_API long cs1i_instr_offset(IDoc* d,int32_t f,int32_t k){ cs1i::Instr* in=pick(d,f,k); return in?in->origOff:-1; }
CS1_API long cs1i_dbg_nested_ptr(IDoc* d,int32_t f,int32_t k){ cs1i::Instr* in=pick(d,f,k); if(!in)return -1;
  for(auto&a:in->args) if(a.kind==2) for(auto&el:a.expr) if(el.nested)
    for(auto&na:el.nested->args) if(na.kind==0 && na.type=="ptr32") return na.ival;
  return -1; }
