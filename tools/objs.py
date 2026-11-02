import sys,struct,collections
sys.path.insert(0,'/home/claude/fd3/tools')
from tree import parse
def objects(d):
    root=parse(d)
    lst=root['kids'][0]['kids'][0]['kids'][0]
    out=[]
    for ob in lst['kids']:
        inner=ob['kids'][0]
        while inner['ver']!=7 or not inner['kids']: inner=inner['kids'][0]
        p=inner['start']+5
        hdr=inner['kids'][0]; oid,otype=struct.unpack_from('<IH',d,hdr['start']+5)
        p=hdr['end']
        n=struct.unpack_from('<I',d,p)[0]; name=d[p+4:p+3+n].decode('latin1'); p+=4+n
        rec={'wrapver':ob['ver'],'ver':inner['ver'],'id':oid,'type':otype,'name':name,'start':ob['start'],'end':ob['end'],'comps':[]}
        # find component count: scan for the u32 count followed by comp id
        # parse the remaining region: find first kid after header... use known: after name: u32,byte, v2 node, then u32 count
        rec['pre']=d[p:p+5].hex()
        p+=5
        k=[k for k in inner['kids'] if k['start']>=p]
        # properties node
        pn=k[0]; rec['props_node']=(pn['start'],pn['end']); p=pn['end']
        cnt=struct.unpack_from('<I',d,p)[0]; p+=4; rec['ncomp']=cnt
        for i in range(cnt):
            cid,sz=struct.unpack_from('<HI',d,p); rec['comps'].append((cid,p+6,sz)); p+=6+sz
        rec['tail']=d[p:inner['end']-4].hex()
        out.append(rec)
    return out
if __name__=='__main__':
    d=open(sys.argv[1],'rb').read()
    O=objects(d)
    print(len(O))
    print(collections.Counter((o['wrapver'],o['ver']) for o in O))
    print(collections.Counter(o['pre'] for o in O).most_common(5))
    print(collections.Counter(o['tail'] for o in O).most_common(5))
    print(collections.Counter(o['type'] for o in O))
    cc=collections.Counter(); 
    for o in O:
        for c in o['comps']: cc[hex(c[0])]+=1
    print(sorted(cc.items()))
