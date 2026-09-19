import sys,json
import numpy as np
from inspect_game import Memory

base=16384000+6782976
with Memory(int(sys.argv[1])) as m:
    d=m.read(base,1264252)
    a=np.frombuffer(d[:len(d)//4*4],dtype='<f4')
    w=np.lib.stride_tricks.sliding_window_view(a,16)
    with np.errstate(all='ignore'):
        mask=np.isfinite(w).all(axis=1)&(np.abs(w).max(axis=1)<10000)
        mask &= (abs(w[:,15]-1)<.0001)&(abs(w[:,3])<.0001)&(abs(w[:,7])<.0001)&(abs(w[:,11])<.0001)
        mask &= (abs((w[:,0:3]**2).sum(axis=1)-1)<.001)&(abs((w[:,4:7]**2).sum(axis=1)-1)<.001)&(abs((w[:,8:11]**2).sum(axis=1)-1)<.001)
        mask &= (abs(w[:,12:15]).max(axis=1)>2)
    hits=[]
    for i in np.flatnonzero(mask):
        mat=w[i].reshape(4,4).copy()
        if abs(np.linalg.det(mat[:3,:3])-1)>.01: continue
        pos=(-mat[3,:3]@mat[:3,:3].T).tolist()
        hit={'address':base+int(i)*4,'matrix':mat.tolist(),'inverse_position':pos}
        hits.append(hit)
        print(hex(hit['address']),np.round(mat[3,:3],3),'inv',np.round(pos,3),'rot',np.round(mat[:3,:3].flatten(),3))
    open('analysis/cameras.json','w').write(json.dumps(hits,indent=2))
