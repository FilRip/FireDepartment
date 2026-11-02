import re,sys
B=b'\xaa\xaa\xff\xff'; E=b'\xcc\xcc\xbb\xbb'
def parse(d):
    a=[(m.start(),1) for m in re.finditer(re.escape(B),d)]
    c=[(m.start(),-1) for m in re.finditer(re.escape(E),d)]
    ev=sorted(a+c)
    root={'start':0,'ver':None,'kids':[],'end':len(d)}
    st=[root]
    for x,t in ev:
        if t==1:
            n={'start':x,'ver':d[x+4],'kids':[]}
            st[-1]['kids'].append(n); st.append(n)
        else:
            n=st.pop(); n['end']=x+4
    return root
def show(d,n,depth,maxd,lim=6):
    pos=n['start']+(5 if n['ver'] is not None else 0)
    for i,k in enumerate(n['kids']):
        gap=d[pos:k['start']]
        if i<lim or i>=len(n['kids'])-2:
            print('  '*depth+f'[gap {len(gap)}] '+gap[:40].hex())
            print('  '*depth+f'#{i} @{k["start"]:x} v{k["ver"]} len={k["end"]-k["start"]} kids={len(k["kids"])}')
            if depth<maxd: show(d,k,depth+1,maxd,lim)
        elif i==lim: print('  '*depth+'...')
        pos=k['end']
    gap=d[pos:n['end']-(4 if n['ver'] is not None else 0)]
    print('  '*depth+f'[tail {len(gap)}] '+gap[:40].hex())
if __name__=='__main__':
    d=open(sys.argv[1],'rb').read()
    show(d,parse(d),0,int(sys.argv[2]),int(sys.argv[3]) if len(sys.argv)>3 else 6)
