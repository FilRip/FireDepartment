import struct
def classlist(d):
    p=0
    def u32():
        nonlocal p; v=struct.unpack_from('<I',d,p)[0]; p+=4; return v
    def s():
        nonlocal p; n=u32(); v=d[p:p+n-1].decode('latin1'); p+=n; return v
    hdr=[s(),s(),s(),u32(),s()]
    p+=19
    lists=[]
    for _ in range(2):
        n=u32(); L=[]
        for i in range(n):
            name=s(); t,i_,x=struct.unpack_from('<HIi',d,p); p+=10; L.append((name,t,i_,x))
        lists.append(L)
    return hdr,lists,p
