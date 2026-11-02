"""Pièce de chaque objet (foyers et fumées) : python roomall.py <dossier gfx> <fichier .sg> <objets.json> <sortie.json>"""
import sys,json,os,numpy as np
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from sg import nodes,world,mesh_bbox
g,sgname,objs,out=sys.argv[1:5]
d=open(os.path.join(g,sgname),'rb').read(); N=nodes(d); rooms={}
for k in N:
    if '_ROOM' in k.upper() and 'MASK' not in k.upper() and os.path.exists(os.path.join(g,k+'.mesh')):
        mn,mx=mesh_bbox(os.path.join(g,k+'.mesh')); W=world(N,k)
        C=np.array([[x,y,z,1] for x in (mn[0],mx[0]) for y in (mn[1],mx[1]) for z in (mn[2],mx[2])])@W
        rooms[k]=(C[:,:3].min(0),C[:,:3].max(0))
res={}
for o in json.load(open(objs)):
    if o['type'] not in (11,12): continue
    hit=[(np.prod(b-a),k) for k,(a,b) in rooms.items() if a[0]-1<=o['x']<=b[0]+1 and a[1]-1<=o['y']<=b[1]+1 and a[2]-60<=o['z']<=b[2]+60]
    res[str(o['id'])]=min(hit)[1] if hit else ''
json.dump(res,open(out,'w'))
print(len(rooms),'pièces ;',sum(1 for v in res.values() if v),'objets en pièce sur',len(res))
