import struct,re,numpy as np,glob,os
def nodes(d):
    """Noeuds du graphe de scène : nom -> (parent, matrice locale 4x4, convention vecteur-ligne)."""
    out={}
    pr=lambda b: len(b)>0 and all(32<=c<127 for c in b)
    for i in range(0,len(d)-80):
        n=d[i]|d[i+1]<<8|d[i+2]<<16|d[i+3]<<24
        if not 2<=n<=80 or d[i+3+n]!=0: continue
        name=d[i+4:i+3+n]
        if not pr(name): continue
        p=i+4+n
        n2=struct.unpack_from('<I',d,p)[0]
        if n2>80 or (n2 and d[p+3+n2]!=0): continue
        parent=d[p+4:p+3+n2] if n2 else b''
        if n2 and not pr(parent): continue
        p+=4+n2
        if p+64>len(d): continue
        M=np.array(struct.unpack_from('<16f',d,p)).reshape(4,4)
        if not np.all(np.isfinite(M)) or abs(M[3,3]-1)>1e-4 or np.abs(M[:,3][:3]).max()>1e-4: continue
        if np.abs(M[:3,:3]).max()>1e3: continue
        out.setdefault(name.decode(),(parent.decode(),M))
    return out
def world(nodes,name,cache={}):
    key=(id(nodes),name)
    if key in cache: return cache[key]
    par,M=nodes[name]
    W=M if not par or par not in nodes or par==name else M@world(nodes,par)
    cache[key]=W; return W
def mesh_bbox(path):
    d=open(path,'rb').read()
    n,ni=struct.unpack_from('<II',d,0); p=8+ni*2
    i=d.find(b'.meshdata\x00',p)
    q=i+len(b'.meshdata\x00')+4
    f=struct.unpack_from('<11f',d,q)
    return np.array(f[4:7]),np.array(f[7:10])
