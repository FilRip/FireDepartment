import sys,json,struct,numpy as np,os
sys.path.insert(0,'/home/claude/fd3/tools')
from sg import nodes,world,mesh_bbox
from objs import objects; from classes import classlist
def roommap(gfxdir, sgname, levelpath):
    d=open(os.path.join(gfxdir,sgname),'rb').read(); N=nodes(d)
    rooms={}
    for k in N:
        if '_ROOM' in k.upper() and 'MASK' not in k.upper() and os.path.exists(os.path.join(gfxdir,k+'.mesh')):
            mn,mx=mesh_bbox(os.path.join(gfxdir,k+'.mesh')); W=world(N,k)
            C=np.array([[x,y,z,1] for x in (mn[0],mx[0]) for y in (mn[1],mx[1]) for z in (mn[2],mx[2])])@W
            rooms[k]=(C[:,:3].min(0),C[:,:3].max(0))
    dl=open(levelpath,'rb').read(); O=objects(dl); h,L,p=classlist(dl); byid={c[2]:c for c in L[0]+L[1]}
    res={}
    for o in O:
        if byid[o['id']][1]!=11: continue
        a=[c for c in o['comps'] if c[0]==0xa001][0]; x,y,z=struct.unpack_from('<26f',dl,a[1]+22)[13:16]
        hit=[(np.prod(b-a),k) for k,(a,b) in rooms.items() if a[0]-1<=x<=b[0]+1 and a[1]-1<=y<=b[1]+1 and a[2]-60<=z<=b[2]+60]
        res[str(o['id'])]=min(hit)[1] if hit else ''
    return rooms,res
if __name__=='__main__':
    rooms,res=roommap(sys.argv[1],sys.argv[2],sys.argv[3])
    json.dump(res,open(sys.argv[4],'w'))
    import collections
    print(len(rooms),'pièces;',sum(1 for v in res.values() if v),'foyers en pièce sur',len(res))
