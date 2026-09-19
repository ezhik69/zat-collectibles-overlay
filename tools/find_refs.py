import argparse, json, struct, time
from pathlib import Path
import numpy as np
from inspect_game import Memory

p = argparse.ArgumentParser()
p.add_argument('pid', type=int)
p.add_argument('values', nargs='+', type=lambda x:int(x,0))
args = p.parse_args()
hits=[]
with Memory(args.pid) as m:
    for r in m.regions():
        if r.type != 0x20000 or r.protect not in (4,8,64,128): continue
        for off in range(0,r.size,1024*1024):
            d=m.read(r.base+off,min(1024*1024,r.size-off))
            a=np.frombuffer(d[:len(d)//4*4], dtype='<u4')
            for ix in np.flatnonzero(np.isin(a,args.values)):
                addr=r.base+off+int(ix)*4
                hits.append({'address':addr,'value':int(a[ix])})
            time.sleep(.001)
    for h in hits[:100]:
        print(hex(h['address']),hex(h['value']),m.read(h['address']-32,112).hex(' '))
Path('analysis/refs.json').write_text(json.dumps(hits,indent=2))
print('Hits',len(hits))
