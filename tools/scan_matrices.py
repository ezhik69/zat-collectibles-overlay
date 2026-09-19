import sys,json,time
import numpy as np
from inspect_game import Memory

gold=np.array([[-86.734375,-8.75,-15.203125],[-126.8125,-.234375,-33.96875],[66.640625,2.265625,-115.3125],[-57.875,-.234375,-75.890625],[27.109375,.203125,-121.0625]])
hits=[]
with Memory(int(sys.argv[1])) as m, np.errstate(all='ignore'):
 for r in m.regions():
  if r.type!=0x20000 or r.protect!=4:continue
  for off in range(0,r.size,1024*1024):
   d=m.read(r.base+off,min(1024*1024+64,r.size-off));a=np.frombuffer(d[:len(d)//4*4],dtype='<f4')
   if len(a)<16:continue
   n=len(a)-15
   ix=np.flatnonzero((a[15:15+n]==1)&(a[3:3+n]==0)&(a[7:7+n]==0)&(a[11:11+n]==0))
   if not len(ix):continue
   w=a[ix[:,None]+np.arange(16)].reshape(-1,4,4)
   ok=np.isfinite(w).all(axis=(1,2))&(np.abs(w).max(axis=(1,2))<10000)
   ok&=(abs((w[:,:3,:3]**2).sum(axis=2)-1)<.002).all(axis=1)
   w=w[ok];ix=ix[ok]
   if not len(ix):continue
   pos=-np.einsum('ni,nji->nj',w[:,3,:3],w[:,:3,:3])
   distances=np.linalg.norm(pos[:,None,:]-gold[None,:,:],axis=2).min(axis=1)
   for j in np.flatnonzero(distances<8):
    if abs(np.linalg.det(w[j,:3,:3])-1)>.005:continue
    hit={'address':r.base+off+int(ix[j])*4,'matrix':w[j].tolist(),'position':pos[j].tolist(),'distance':float(distances[j])}
    hits.append(hit)
   time.sleep(.001)
open('analysis/heap_cameras.json','w').write(json.dumps(hits,indent=2))
print('Candidates',len(hits))
for h in hits[:80]:print(hex(h['address']),np.round(h['position'],3),round(h['distance'],2))
