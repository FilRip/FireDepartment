"""Fumée dans une sauvegarde : python smokesave.py carte.level partie.sav pieces.json [sortie.json]"""
import struct,sys,json
sys.path.insert(0,'/home/claude/fd3/tools')
from objs import objects
def main(lv,sv,rj,out=None):
    d=open(lv,'rb').read(); s=open(sv,'rb').read(); rooms=json.load(open(rj))
    rows={}
    for o in objects(d):
        cs=[c for c in o['comps'] if c[0]==0xfe01]
        if not cs: continue
        c=cs[0]; lb=d[c[1]:c[1]+c[2]]
        p=s.find(b'\x01\xfe'+struct.pack('<I',o['id']))-5
        if p<0: continue
        f=lambda b,i: struct.unpack_from('<f',b,i)[0]
        rows[o['name']]=dict(room=rooms.get(str(o['id']),''),v0=f(lb,32),v=f(s,p+32),lit=s[p+27])
    tot={}
    for n,r in rows.items():
        t=tot.setdefault(r['room'] or '(dehors)',[0,0,0]); t[0]+=r['v0']; t[1]+=r['v']; t[2]+=1
    for k,(a,b,n) in sorted(tot.items()):
        if a or b: print(f'{k:28s} n={n:3d} départ={a:7.1f} sauvegarde={b:7.1f}')
    nz=[(n,round(r['v'],1)) for n,r in rows.items() if r['v']>0.5]
    print(len(rows),'points ; non vides',len(nz),':',nz[:40])
    if out: json.dump(rows,open(out,'w'))
if __name__=='__main__': main(*sys.argv[1:])
