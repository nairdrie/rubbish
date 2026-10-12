#!/usr/bin/env python3
"""Compile the Rubbish assemblies with Unity's bundled Roslyn WITHOUT touching any Unity Editor.

Why: Nick plays in the live Editor while agents edit scripts, so nobody launches Unity, calls `unity command`, or waits for
the Editor's own recompile just to see whether a change compiles. This reuses the Editor's own response files (defines,
references, analyzers) from Library/Bee/artifacts/<hash>E.dag/<Assembly>.rsp, regenerates the source list from the folder
layout, and writes every output into a scratch folder (never into Library/ or the project).

usage: Tools/dev/compile_check.py [all | Rubbish.Net.Core | Assembly-CSharp | Assembly-CSharp-Editor | Rubbish.Net.Tests ...]
  all (default) compiles the four assemblies in dependency order, each against the ref.dll just built for the ones before it
  (so a removed or renamed public member is caught in the Editor scripts too, and a new class in Net/Core is seen by the game).
  Naming one assembly still builds its prerequisites only if you name them: use `all` unless you only touched one assembly
  that nothing else depends on.
env:  CC_OUT=<dir>   scratch output folder (default: /tmp/rubbish_cc/<pid>)
      CC_PROJ=<dir>  project root (default: this script's project)
exit: 0 = every requested assembly compiled, 1 = compile errors, 2 = usage / setup problem

Notes: needs a Library/Bee/artifacts/*E.dag with the .rsp files (any Editor that has compiled the project once). The source
lists follow the asmdef layout: Assets/Scripts/Net/Core = Rubbish.Net.Core, Assets/Tests = Rubbish.Net.Tests, any Editor folder =
Assembly-CSharp-Editor, everything else under Assets = Assembly-CSharp. If an asmdef is added, update PREDICATES below.
The EditMode tests reach game types by reflection (name strings), so grep Assets/Tests for names you remove or rename.
"""
import glob
import os
import re
import subprocess
import sys
import time

PROJ = os.environ.get('CC_PROJ') or os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
ORDER = ['Rubbish.Net.Core', 'Assembly-CSharp', 'Assembly-CSharp-Editor', 'Rubbish.Net.Tests']


def editor_version():
    try:
        for line in open(os.path.join(PROJ, 'ProjectSettings', 'ProjectVersion.txt'), encoding='utf-8'):
            if line.startswith('m_EditorVersion:'):
                return line.split(':', 1)[1].strip()
    except OSError:
        pass
    return None


def unity_contents():
    ver = editor_version()
    for base in ('/Applications/Unity/Hub/Editor/%s/Unity.app/Contents' % ver, '/Applications/Unity/Unity.app/Contents'):
        if ver and os.path.isdir(base):
            return base
    found = sorted(glob.glob('/Applications/Unity/Hub/Editor/*/Unity.app/Contents'))
    return found[-1] if found else None


def editor_dag():
    """The newest Editor (not player) dag that holds the response files, relative to the project."""
    best, best_t = None, -1.0
    for d in glob.glob(os.path.join(PROJ, 'Library', 'Bee', 'artifacts', '*E.dag')):
        rsp = os.path.join(d, 'Assembly-CSharp.rsp')
        if os.path.exists(rsp) and os.path.getmtime(rsp) > best_t:
            best, best_t = d, os.path.getmtime(rsp)
    return os.path.relpath(best, PROJ) if best else None


def in_editor(p): return '/Editor/' in p
def in_netcore(p): return p.startswith('Assets/Scripts/Net/Core/')
def in_tests(p): return p.startswith('Assets/Tests/')


PREDICATES = {
    'Assembly-CSharp': lambda p: not in_editor(p) and not in_netcore(p) and not in_tests(p),
    'Assembly-CSharp-Editor': lambda p: in_editor(p) and not in_netcore(p) and not in_tests(p),
    'Rubbish.Net.Core': lambda p: in_netcore(p) and not in_editor(p),
    'Rubbish.Net.Tests': lambda p: in_tests(p),
}


def sources_for(asm):
    pred = PREDICATES[asm]
    found = []
    for root, _dirs, files in os.walk(os.path.join(PROJ, 'Assets')):
        for f in files:
            if f.endswith('.cs'):
                rel = os.path.relpath(os.path.join(root, f), PROJ).replace(os.sep, '/')
                if pred(rel):
                    found.append(rel)
    return sorted(found)


def compile_one(asm, out, built, dotnet, csc, dag):
    rsp_path = os.path.join(PROJ, dag, asm + '.rsp')
    if not os.path.exists(rsp_path):
        print('[cc] missing %s (has the Editor ever compiled this assembly?)' % rsp_path)
        return False
    lines = open(rsp_path, encoding='utf-8').read().splitlines()
    keep = []
    for line in lines:
        s = line.strip()
        if re.match(r'^"?(Assets|Packages)/.*\.cs"?$', s):
            continue  # sources are regenerated below
        if s.startswith('-out:') or s.startswith('-refout:'):
            continue
        m = re.match(r'^-r:"(.*/)?([^/"]+)\.ref\.dll"$', s)
        if m and m.group(2) in built:  # a dependency rebuilt in this run: reference the fresh one
            keep.append('-r:"%s/%s.ref.dll"' % (out, m.group(2)))
            continue
        keep.append(line)
    srcs = sources_for(asm)
    keep.append('-out:"%s/%s.dll"' % (out, asm))
    keep.append('-refout:"%s/%s.ref.dll"' % (out, asm))
    keep.extend('"%s"' % s for s in srcs)
    new_rsp = os.path.join(out, asm + '.rsp')
    with open(new_rsp, 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(keep) + '\n')
    t0 = time.time()
    proc = subprocess.run([dotnet, csc, '-nologo', '-noconfig', '@' + new_rsp], cwd=PROJ, capture_output=True, text=True)
    dt = time.time() - t0
    text = (proc.stdout or '') + (proc.stderr or '')
    errors = [l for l in text.splitlines() if ': error ' in l]
    if proc.returncode != 0 or errors:
        print('[cc] FAILED %s (%d files, %.1fs): %d error(s)' % (asm, len(srcs), dt, len(errors)))
        seen = set()
        for l in errors:
            if l not in seen:
                seen.add(l)
                print('   ' + l.replace(PROJ + '/', ''))
        if not errors:
            print(text[-2000:])
        return False
    print('[cc] OK %s (%d files, %.1fs)' % (asm, len(srcs), dt))
    built.add(asm)
    return True


def main(argv):
    want = argv[1:] or ['all']
    if want == ['all']:
        want = ORDER
    for a in want:
        if a not in PREDICATES:
            print('unknown assembly: ' + a + '; choose from ' + ', '.join(ORDER) + ' or all')
            return 2
    contents = unity_contents()
    if not contents:
        print('[cc] no Unity install found for ' + str(editor_version()))
        return 2
    dotnet = contents + '/Resources/Scripting/NetCoreRuntime/dotnet'
    csc = contents + '/Resources/Scripting/DotNetSdkRoslyn/csc.dll'
    dag = editor_dag()
    if not dag:
        print('[cc] no Library/Bee/artifacts/*E.dag/Assembly-CSharp.rsp: open the project in the Editor once so it compiles')
        return 2
    out = os.environ.get('CC_OUT') or os.path.join('/tmp/rubbish_cc', str(os.getpid()))
    os.makedirs(out, exist_ok=True)
    built = set()
    ok = True
    for a in ORDER:
        if a not in want:
            continue
        if not compile_one(a, out, built, dotnet, csc, dag):
            ok = False
            break
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
