#!/usr/bin/env python3
"""Which C# scripts will make the Editor ask for a town bake ("Bake and play") when you edit them?

TownPublishGuard refuses Play when any file the town scenes depend on changed since the last bake
(TownWorldSignatureBuilder hashes AssetDatabase.GetDependencies of Neighborhood.unity, DepotRow.unity and the town assets), and a
script that a scene or prefab in that closure refers to by GUID counts. This approximates the closure WITHOUT Unity by following
GUID references through the text-serialised assets, so an agent can check a file before editing it while Nick is playing.

usage: Tools/dev/town_closure.py                    list every script in the closure
       Tools/dev/town_closure.py Assets/A.cs ...    print IN / OUT for each file given (IN = editing it forces a re-bake)
Scripts that are only added at runtime (AddComponent) are never in the closure; hook new behaviour in from those.
"""
import os
import re
import sys

P = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
os.chdir(P)

# the seeds TownWorldSignatureBuilder uses (scenes + the baked town assets); keep in step with it
SEEDS = ['Assets/Scenes/Neighborhood.unity', 'Assets/Scenes/DepotRow.unity', 'Assets/Resources/StreetGraph.asset',
         'Assets/Resources/HouseDirectory.asset', 'Assets/Resources/TownMap.asset']
TEXT_EXT = ('.unity', '.prefab', '.asset', '.mat', '.controller', '.anim', '.overrideController', '.playable', '.mask',
            '.physicMaterial', '.physicsMaterial2D', '.flare', '.lighting', '.renderTexture', '.shadergraph', '.signal',
            '.preset', '.inputactions')


def main(argv):
    guid_path = {}
    for root, _dirs, files in os.walk('Assets'):
        for f in files:
            if f.endswith('.meta'):
                try:
                    txt = open(os.path.join(root, f), encoding='utf-8', errors='ignore').read(600)
                except OSError:
                    continue
                m = re.search(r'^guid: ([0-9a-f]{32})', txt, re.M)
                if m:
                    guid_path[m.group(1)] = os.path.join(root, f)[:-5]
    seen = set()
    stack = list(SEEDS)
    guid_re = re.compile(r'guid: ([0-9a-f]{32})')
    while stack:
        p = stack.pop()
        if p in seen or not os.path.exists(p):
            continue
        seen.add(p)
        if not p.endswith(TEXT_EXT) or os.path.getsize(p) > 200_000_000:
            continue
        try:
            with open(p, 'rb') as fh:
                data = fh.read()
            if not data.startswith(b'%YAML'):
                continue
            txt = data.decode('utf-8', 'ignore')
        except OSError:
            continue
        for g in set(guid_re.findall(txt)):
            q = guid_path.get(g)
            if q and q not in seen:
                stack.append(q)
    scripts = sorted(p for p in seen if p.endswith('.cs'))
    if len(argv) > 1:
        for a in argv[1:]:
            print(('IN  ' if a in seen else 'OUT ') + a)
    else:
        print('%d assets in closure; %d scripts' % (len(seen), len(scripts)))
        print('\n'.join(scripts))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
