#!/usr/bin/env python3
"""
Run Esc > Unstuck's truck-call rules WITHOUT Unity (Docs/UIPass.md §5.5).

What it does, in a scratch folder (nothing is written into the project, no Editor is started):
  1. cuts the pure parts of the real source text out of TruckCall.cs (the landing-spot search, distance to the truck's body box,
     road frames, collider names), Unstuck.FellOutOfWorld, DepotRowLayout.InteriorAt and CasinoLayout.Contains, and compiles
     them into a stand-in "Assembly-CSharp.dll" with Unity's own Roslyn;
  2. runs Scenarios.cs against it: TruckCall.Pick on made-up streets (a road with pavements, an open park, a fenced yard that
     fits the truck and one that doesn't, a river, an island) plus 300 random streets where the pruned search must price its
     answer exactly like a brute-force search over the same rings;
  3. runs Assets/Tests/EditMode/Business/TruckCallTests.cs (the same reflection tests Unity's Test Runner runs) with a tiny NUnit
     runner, using the nunit.framework.dll from the package cache (read only).
It runs on the .NET 6 runtime bundled with the Editor and loads UnityEngine.CoreModule.dll for Vector3/Mathf only: anything that
needs the native engine (physics, Bounds.Contains) is not available here, and the physics probes in TruckCall are NOT covered.

usage: Tools/truckcall_check/check.py        (exit code 0 = every check passed)
env:   UNITY_CONTENTS=<.../Unity.app/Contents> to override the Editor found from ProjectSettings/ProjectVersion.txt
"""
import glob, os, re, shutil, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, '..', '..'))
OUT = os.path.join(tempfile.gettempdir(), 'rubbish_truckcall_check')


def unity_contents():
    env = os.environ.get('UNITY_CONTENTS')
    if env: return env
    version = re.search(r'm_EditorVersion:\s*(\S+)', open(os.path.join(PROJ, 'ProjectSettings', 'ProjectVersion.txt')).read()).group(1)
    return f'/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents'


U = unity_contents()
DOTNET = U + '/Resources/Scripting/NetCoreRuntime/dotnet'
CSC = U + '/Resources/Scripting/DotNetSdkRoslyn/csc.dll'
FW = glob.glob(U + '/Resources/Scripting/NetCoreRuntime/shared/Microsoft.NETCore.App/*')[0]
UNITY_CORE = U + '/Resources/Scripting/Managed/UnityEngine/UnityEngine.CoreModule.dll'
REFS = ['-r:' + os.path.join(FW, f) for f in sorted(os.listdir(FW))
        if f.endswith('.dll') and (f.startswith('System.') or f in ('mscorlib.dll', 'netstandard.dll', 'Microsoft.CSharp.dll'))]


def read(rel):
    return open(os.path.join(PROJ, rel), encoding='utf-8').read()


def between(text, start, end):
    a = text.index(start)
    return text[a:text.index(end, a)]


def csc(out, target, sources, extra=()):
    cmd = [DOTNET, CSC, '-nologo', '-noconfig', '-langversion:9.0', '-target:' + target, '-nullable:disable', '-out:' + out,
           '-r:' + UNITY_CORE] + REFS + list(extra) + list(sources)
    p = subprocess.run(cmd, capture_output=True, text=True)
    text = (p.stdout or '') + (p.stderr or '')
    if p.returncode != 0 or ': error ' in text:
        print(text[-3000:]); sys.exit(2)


def run(dll):
    open(dll[:-4] + '.runtimeconfig.json', 'w').write('{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}')
    r = subprocess.run([DOTNET, dll], capture_output=True, text=True, cwd=OUT)
    print(r.stdout + r.stderr, end='')
    return r.returncode


def stand_in_source():
    truck = read('Assets/Scripts/Shop/Truck/TruckCall.cs')
    pure = between(truck, '    // ------------------------------------------------------------------ the search (pure',
                   "    // ------------------------------------------------------------------ the game's physics")
    names = truck[truck.index('    /// <summary>What kind of ground a collider is, by the names'):truck.rindex('}')]
    depot = read('Assets/Scripts/World/DepotRow/DepotRowLayout.cs')
    depot_part = ('    public const int LotCount = 4;\n' +
                  between(depot, '    /// <summary>World offset of lot k', '    /// <summary>The lot whose street pad contains'))
    casino = read('Assets/Scripts/Casino/World/CasinoLayout.cs')
    casino_part = ('    public const float HalfX = 11f, HalfZ = 6f;\n' +
                   between(casino, '    public static readonly Vector3 Origin', '    public const float HalfX') +
                   between(casino, '    /// <summary>A box around the room', '    /// <summary>Below the room'))
    unstuck = read('Assets/Scripts/UI/Pause/Unstuck.cs')
    fell = between(unstuck, '    /// <summary>Fell through the floor', '    static void Teleport')
    return '''using System;
using System.Collections.Generic;
using UnityEngine;

public static class CoastalCityLayout { public sealed class RoadDef { public string id; public Vector3[] points; public float halfWidth; } }

// stand-in: the real Bounds.Contains is a native call, which a console program does not have
public struct Bounds
{
    public Vector3 center, size;
    public Bounds(Vector3 c, Vector3 s) { center = c; size = s; }
    public Vector3 extents => size * 0.5f;
    public Vector3 min => center - extents;
    public Vector3 max => center + extents;
    public bool Contains(Vector3 p) { var a = min; var b = max; return p.x >= a.x && p.x <= b.x && p.y >= a.y && p.y <= b.y && p.z >= a.z && p.z <= b.z; }
}

public static class TruckCall
{
    public const float MaxReach = 60f, MinGap = 1.4f, RightHere = 3.2f, RootLift = 0.15f;
    const float LeftCost = 0.2f, BackwardsCost = 0.4f, SightCost = 8f;
    static bool Finite(Vector3 p) => !(float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z));
''' + pure + names + '''
}

public static class DepotRowLayout
{
''' + depot_part + '''
}

public static class CasinoLayout
{
''' + casino_part + '''
}

public static class Unstuck
{
    const float FellBelow = -12f;
''' + fell + '''
}
'''


def main():
    if os.path.isdir(OUT): shutil.rmtree(OUT)
    os.makedirs(OUT)
    shutil.copy(UNITY_CORE, OUT)
    open(os.path.join(OUT, 'GameStub.cs'), 'w', encoding='utf-8').write(stand_in_source())
    asm = os.path.join(OUT, 'Assembly-CSharp.dll')
    csc(asm, 'library', [os.path.join(OUT, 'GameStub.cs')])

    print('--- TruckCall.Pick on made-up streets')
    csc(os.path.join(OUT, 'scenarios.dll'), 'exe', [os.path.join(HERE, 'Scenarios.cs')], ['-r:' + asm])
    code = run(os.path.join(OUT, 'scenarios.dll'))

    nunit = glob.glob(os.path.join(PROJ, 'Library', 'PackageCache', 'com.unity.ext.nunit@*', 'net40', 'unity-custom', 'nunit.framework.dll'))
    if not nunit:
        print('--- (no nunit.framework.dll in the package cache: skipping TruckCallTests)')
        return code
    print('--- TruckCallTests (the EditMode tests, run here with a tiny NUnit runner)')
    shutil.copy(nunit[0], OUT)
    open(os.path.join(OUT, 'Runner.cs'), 'w').write('''using System;
using System.Linq;
using System.Reflection;
public static class Runner
{
    public static int Main()
    {
        int pass = 0, fail = 0;
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            foreach (var m in type.GetMethods().Where(m => m.GetCustomAttributes(typeof(NUnit.Framework.TestAttribute), false).Length > 0))
            {
                try { m.Invoke(Activator.CreateInstance(type), null); pass++; }
                catch (TargetInvocationException e)
                {
                    fail++;
                    Exception x = e.InnerException;
                    while (x is TargetInvocationException && x.InnerException != null) x = x.InnerException;
                    Console.WriteLine("FAIL  " + type.Name + "." + m.Name + ": " + x.GetType().Name + ": " + x.Message);
                }
            }
        Console.WriteLine(pass + " tests passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
''')
    tests = os.path.join(PROJ, 'Assets', 'Tests', 'EditMode', 'Business', 'TruckCallTests.cs')
    csc(os.path.join(OUT, 'tests.dll'), 'exe', [tests, os.path.join(OUT, 'Runner.cs')], ['-r:' + nunit[0], '-r:' + asm])
    return max(code, run(os.path.join(OUT, 'tests.dll')))


if __name__ == '__main__':
    sys.exit(main())
