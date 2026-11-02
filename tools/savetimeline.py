"""Historique réel des allumages à partir d'une sauvegarde : python savetimeline.py carte.level partie.sav sortie.json"""
import struct,sys,json
sys.path.insert(0,'/home/claude/fd3/tools')
from objs import objects; from classes import classlist
E=b'\xcc\xcc\xbb\xbb'
def tail(b):
    last=b.rfind(E); prev=b.rfind(E,0,last); return b[prev+4:last]
def main(lv,sv,out):
    d=open(lv,'rb').read(); s=open(sv,'rb').read()
    O=objects(d); _,Lc,_=classlist(d); byid={c[2]:c for c in Lc[0]+Lc[1]}
    rows={}
    for o in O:
        if byid[o['id']][1]!=11: continue
        c=[c for c in o['comps'] if c[0]==0xfe00][0]; lb=d[c[1]:c[1]+c[2]]
        p=s.find(b'\x00\xfe'+struct.pack('<I',o['id']))-5
        if p<0: continue
        sz=struct.unpack_from('<I',s,p-4)[0]; sb=s[p:p+sz]
        lt,st=tail(lb),tail(sb)
        if len(lt)!=96 or len(st)!=96: continue
        f=lambda b,i: struct.unpack_from('<f',b,i)[0]
        rows[o['name']]=dict(lit0=lb[27],lit=sb[27],cur=f(sb,32),burn0=f(lt,42),burn=f(st,42))
    init=[n for n,r in rows.items() if r['lit0']]
    # temps de la sauvegarde : plus grand écart de temps de combustion parmi les foyers allumés au départ
    T=max(r['burn']-r['burn0'] for n,r in rows.items() if r['lit0'] and r['lit'])
    ign={n:T-r['burn'] for n,r in rows.items() if r['lit'] and not r['lit0']}
    json.dump({'T':T,'ign':ign,'init':init},open(out,'w'))
    curve=[len(init)+sum(1 for x in ign.values() if x<=t) for t in range(0,int(T)+1,30)]
    print(f'{len(rows)} foyers ; allumés au départ {len(init)} ; temps de la sauvegarde {T:.1f} s ; allumés en jeu {len(ign)}')
    print('cumul toutes les 30 s :',curve)
if __name__=='__main__': main(*sys.argv[1:4])
