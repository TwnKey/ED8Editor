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
                 std::vector<std::string> argTypes; NodeList read; };

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
static void schemaScan(const NodeList& nl, std::vector<std::string>& out){
  for(auto&n:nl){
    switch(n.k){
      case Node::SCALAR: if(n.role!=1) out.push_back(n.t); break; // selector cache
      case Node::STR: out.push_back("string"); break;
      case Node::EXPR: out.push_back("expr"); break;
      case Node::DIALOG: out.push_back("dialog"); break;
      case Node::BYTES: out.push_back("bytes"); break;
      case Node::FILL: break;
      case Node::IF: schemaScan(n.iff->then_,out); break;
      case Node::IFVAL: schemaScan(n.ifv->then_,out); break;
      case Node::LOOP: out.push_back("list"); break;
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
    NodeList rl=buildNodes(*iv.get("read")); schemaScan(rl,r.argTypes); r.read=rl;
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
    if(n.k==Node::SCALAR){ if(ai>=args.size())return false; const Arg&a=args[ai++]; long val=a.ival; enc_scalar(n.t,a,out);
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

// ================= Couche C ABI =================
namespace cs1i {
struct IDoc{ cs1ed::Doc* base=nullptr; std::string scene; bool ui=false;
             std::vector<std::vector<Instr>> dec; std::vector<char> isCode;
             long nextId=0; std::vector<long> funcEndId;
             std::vector<long> origStart, origEnd; };
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
  d->funcEndId.resize(nf,-1); d->origStart.resize(nf,0); d->origEnd.resize(nf,0);
  for(int i=0;i<nf;i++){ long ost=d->base->funcs[i].ostart; d->origStart[i]=ost; d->origEnd[i]=ost+(long)d->base->funcs[i].bytes.size();
    int ty=cs1_doc_func_type(d->base,i);
    if(ty==0){ const uint8_t* fb=cs1_doc_func_bytes(d->base,i); int fl=cs1_doc_func_size(d->base,i);
      std::vector<Instr> v; if(decodeFunc(fb,fl,d->ui,ost,v)){ d->dec[i]=std::move(v); d->isCode[i]=1; } } }
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

// ---- Instructions d'une fonction ----
static Instr* pick(IDoc*d,int f,int k){ if(!d||f<0||f>=(int)d->dec.size())return nullptr; auto&v=d->dec[f]; if(k<0||k>=(int)v.size())return nullptr; return &v[k]; }
CS1_API int32_t cs1i_func_ninstr(IDoc* d,int32_t f){ if(!d||f<0||f>=(int)d->dec.size())return -1; return (int32_t)d->dec[f].size(); }
CS1_API int32_t cs1i_instr_reg(IDoc* d,int32_t f,int32_t k){ Instr*in=pick(d,f,k); return in?in->reg:-1; }
CS1_API int32_t cs1i_instr_op(IDoc* d,int32_t f,int32_t k){ cs1i::Instr*in=pick(d,f,k); return in?in->op:-1; }
CS1_API const char* cs1i_instr_name(IDoc* d,int32_t f,int32_t k){ Instr*in=pick(d,f,k); if(!in||in->reg<0)return nullptr; return G.regs[in->reg].name.c_str(); }
CS1_API int32_t cs1i_instr_argc(IDoc* d,int32_t f,int32_t k){ Instr*in=pick(d,f,k); if(!in)return -1; return visibleCount(in->args); }
CS1_API const char* cs1i_instr_argtype(IDoc* d,int32_t f,int32_t k,int32_t a){ Instr*in=pick(d,f,k); if(!in)return nullptr; int r=visibleToReal(in->args,a); if(r<0)return nullptr;
  const Arg&x=in->args[r]; if(x.kind==0)return x.type.c_str(); return x.kind==1?"string":x.kind==2?"expr":x.kind==3?"dialog":"bytes"; }
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
  cs1i::Instr in; in.op=r.op; in.reg=ri; in.path=r.path; in.id=d->nextId++;
  cs1i::Ctx c; size_t pi=0; buildDefault(r.read,r.path,pi,c,in.args);
  long nid=in.id; auto& v=d->dec[f]; if(pos<0)pos=0; if(pos>(int)v.size())pos=(int)v.size();
  v.insert(v.begin()+pos,std::move(in)); return (int32_t)nid;
}
CS1_API int32_t cs1i_instr_move(IDoc* d,int32_t f,int32_t from,int32_t to){
  if(!d||f<0||f>=(int)d->dec.size())return 0; auto&v=d->dec[f];
  if(from<0||from>=(int)v.size()||to<0||to>=(int)v.size())return 0;
  cs1i::Instr t=std::move(v[from]); v.erase(v.begin()+from); v.insert(v.begin()+to,std::move(t)); return 1;
}
// definit une cible de saut de maniere SYMBOLIQUE : (tf,ti) instruction cible, ti<0 = fin de fonction tf
CS1_API int32_t cs1i_instr_set_jump(IDoc* d,int32_t f,int32_t k,int32_t a,int32_t tf,int32_t ti){
  cs1i::Instr* in=pick(d,f,k); if(!in)return 0; int r=visibleToReal(in->args,a);
  if(r<0||in->args[r].type!="ptr32")return 0; long tid;
  if(ti<0){ if(tf<0||tf>=(int)d->funcEndId.size()||d->funcEndId[tf]<0)return 0; tid=d->funcEndId[tf]; }
  else { if(tf<0||tf>=(int)d->dec.size()||ti>=(int)d->dec[tf].size())return 0; tid=d->dec[tf][ti].id; }
  in->args[r].isRef=true; in->args[r].targetId=tid; return 1;
}

CS1_API const uint8_t* cs1i_serialize(IDoc* d,int32_t* outlen){
  if(!d){ if(outlen)*outlen=0; return nullptr; }
  int nf=(int)d->dec.size(); auto& F=d->base->funcs;
  std::vector<std::vector<uint8_t>> fb(nf); std::vector<std::vector<long>> ioff(nf);
  // pass1 : encode + offsets internes
  for(int f=0;f<nf;f++){
    if(d->isCode[f]){ std::vector<uint8_t> buf; for(auto&in:d->dec[f]){ ioff[f].push_back((long)buf.size()); if(!encodeInstr(in,d->ui,buf)){ if(outlen)*outlen=0; return nullptr; } } fb[f]=std::move(buf); }
    else fb[f]=F[f].bytes;
  }
  std::string scene=d->base->scene; uint32_t nsize=(uint32_t)scene.size()+1, nb=nf;
  uint32_t names_len=0; for(int k=0;k<nf;k++) names_len+=(uint32_t)F[k].name.size()+1;
  uint32_t ptr_area=0x20+nsize, names_pos_area=ptr_area+nb*4;
  uint32_t funcs_meta_end=names_pos_area+nb*2+names_len;
  int mult=4; if(nb>0 && !F[0].name.empty() && F[0].name[0]=='_') mult=0x10;
  uint32_t pad=((funcs_meta_end+mult-1)/mult)*mult - funcs_meta_end;
  uint32_t funcs_start=funcs_meta_end+pad;
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
    } else { // fonctions non decodees : relocation uniforme (comme l'ancien serialize) pour type 0/-1
      long delta=(long)addrs[f]-d->origStart[f];
      if(delta!=0 && (F[f].type==0||F[f].type==-1)) cs1_reloc_jumps(fb[f],delta,d->origStart[f],d->origStart[f]+(long)fb[f].size());
    } }
  // assemblage fichier
  std::vector<uint8_t> h;
  auto P32=[&](uint32_t x){h.push_back(x&0xff);h.push_back((x>>8)&0xff);h.push_back((x>>16)&0xff);h.push_back((x>>24)&0xff);};
  auto P16=[&](uint16_t x){h.push_back(x&0xff);h.push_back((x>>8)&0xff);};
  P32(0x20);P32(0x20);P32(ptr_area);P32(nb*4);P32(names_pos_area);P32(nb);P32(funcs_meta_end);P32(0xABCDEF00);
  for(char ch:scene)h.push_back((uint8_t)ch); h.push_back(0);
  for(int k=0;k<nf;k++)P32(addrs[k]);
  uint32_t noff=0; for(int k=0;k<nf;k++){ P16((uint16_t)(names_pos_area+nb*2+noff)); noff+=(uint32_t)F[k].name.size()+1; }
  for(int k=0;k<nf;k++){ for(char ch:F[k].name)h.push_back((uint8_t)ch); h.push_back(0); }
  for(uint32_t i=0;i<pad;i++)h.push_back(0);
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
