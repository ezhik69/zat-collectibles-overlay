import argparse, json, struct
from pathlib import Path
from inspect_game import Memory

p=argparse.ArgumentParser()
p.add_argument('pid',type=int)
p.add_argument('owner',type=lambda x:int(x,0))
p.add_argument('model',type=lambda x:int(x,0))
p.add_argument('output')
a=p.parse_args()
with Memory(a.pid) as m:
    owner=m.read(a.owner,0x240)
    model=m.read(a.model,0x240)
result={
    'owner':a.owner,
    'model':a.model,
    'owner_u32':[struct.unpack_from('<I',owner,i)[0] for i in range(0,len(owner),4)],
    'model_u32':[struct.unpack_from('<I',model,i)[0] for i in range(0,len(model),4)],
}
Path(a.output).write_text(json.dumps(result,indent=2))
print(f"owner={a.owner:#x} model={a.model:#x} owner_vtable={result['owner_u32'][0]:#x}")
