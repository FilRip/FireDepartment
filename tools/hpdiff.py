"""État des foyers dans plusieurs sauvegardes : python hpdiff.py carte.level a.sav b.sav ..."""
import sys,struct
sys.path.insert(0,'/home/claude/fd3/tools'); from objs import objects
E=b'\xcc\xcc\xbb\xbb'
def tail(b):
    last=b.rfind(E); prev=b.rfind(E,0,last); return b[prev+4:last]
f=lambda b,i: struct.unpack_from('<f',b,i)[0]
def state(d,o,s):
    p=s.find(b'\x00\xfe'+struct.pack('<I',o['id']))-5
    if p<0: return None
    sz=struct.unpack_from('<I',s,p-4)[0]; sb=s[p:p+sz]; st=tail(sb)
    if len(st)!=96: return None
    return dict(lit=sb[27],cur=f(sb,32),mx=f(sb,36),burn=f(st,42),heatT=f(st,38),heat=f(st,87),flag=st[13],sb=sb,st=st)
d=open(sys.argv[1],'rb').read(); S=[open(x,'rb').read() for x in sys.argv[2:]]
O=[o for o in objects(d) if any(c[0]==0xfe00 for c in o['comps'])]
R={o['name']:[state(d,o,s) for s in S] for o in O}
R={k:v for k,v in R.items() if all(v)}
T=[max(v[i]['burn'] for v in R.values()) for i in range(len(S))]
print('temps (combustion max):',[round(t,1) for t in T])
# foyers dont l'évolution n'est pas normale : intensité qui baisse sans être en déclin, ou bloc d'état différent
for n,v in R.items():
    curs=[x['cur'] for x in v]
    if any(curs[i+1]<curs[i]-0.01 for i in range(len(curs)-1)) or any(x['cur']<0 for x in v):
        print(n,[(x['lit'],round(x['cur'],1),round(x['burn'],1),round(x['heatT'],1),round(x['heat'],1)) for x in v])
