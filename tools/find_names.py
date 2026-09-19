import argparse, json, re, time
from pathlib import Path
from inspect_game import Memory

p = argparse.ArgumentParser()
p.add_argument('pid', type=int)
args = p.parse_args()
hits = []
total = 0
pattern = re.compile(rb'gold[ _]ingot|blood_bottle|AsuraEntityClass_PickupObject', re.I)
with Memory(args.pid) as m:
    for r in m.regions():
        if r.type != 0x20000 or r.protect not in (4, 8, 64, 128):
            continue
        for off in range(0, r.size, 1024 * 1024):
            d = m.read(r.base + off, min(1024 * 1024 + 128, r.size - off))
            total += len(d)
            for match in pattern.finditer(d):
                address = r.base + off + match.start()
                context = d[max(0, match.start()-32):match.end()+100]
                hits.append({'address':address, 'text':repr(context)})
            time.sleep(.002)
Path('analysis').mkdir(exist_ok=True)
Path('analysis/name_hits.json').write_text(json.dumps(hits, indent=2))
print('Read MB:', round(total / 1024**2, 1), 'Hits:', len(hits))
for h in hits[:70]:
    print(hex(h['address']), h['text'])
