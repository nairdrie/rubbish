using System.Collections.Generic;
using UnityEngine;

/// <summary>A renderer-only adult crossing guard. The director owns position and crossing rules; this class owns
/// the hi-vis outfit, readable double-sided paddle, and friendly stop/usher gestures. The director performs
/// rule-based bonks without physical colliders; it can pose <see cref="Rig"/> directly during a cartoon tumble.</summary>
public sealed class CrossingGuardLook
{
    static readonly Color Navy = new Color(.10f, .17f, .25f);
    static readonly Color HiVis = new Color(.83f, 1f, .10f);
    static readonly Color Reflective = new Color(.95f, .98f, .91f);
    static readonly Color StopRed = new Color(.82f, .025f, .035f);
    static Mesh paddleMesh, reflectiveMesh;
    readonly PuppetRig rig;
    readonly Transform paddle;
    PuppetDice dice;
    float stopBlend;

    public Transform Root => rig.root;
    public PuppetRig Rig => rig;

    public CrossingGuardLook(Transform parent, ulong seed)
    {
        dice = new PuppetDice(seed);
        var style = ResidentLook.StyleFor(ResidentArchetype.Commuter, 0, seed);
        style.height = dice.Range(1.65f, 1.8f);
        style.build = PuppetBuild.Ball;
        style.top = style.bottom = Navy;
        style.shoes = new Color(.09f, .10f, .12f);
        style.bareLegs = style.slippers = false;
        style.hat = PuppetHat.Cap;
        style.hatColor = Navy;
        style.hairKind = PuppetHair.Short;
        style.outfit = PuppetOutfit.Vest;
        style.outfitColor = HiVis;
        style.prop = PuppetProp.None;
        style.accent = style.accent2 = PuppetAccent.None;
        var spec = new RigSpec();
        ResidentLook.Compose(in style, false, false, spec);
        spec[PartSlot.MittL].color = spec[PartSlot.MittR].color = Reflective;
        rig = PuppetRig.Build(parent, "Crossing guard");
        rig.Apply(spec);

        // Reflective ribbon follows the actual round vest, so it does not float like box strips.
        var vest = spec[PartSlot.Outfit];
        var stripes = MeshPart(rig.torso, "Reflective vest bands", Reflective);
        if (reflectiveMesh == null) reflectiveMesh = BuildReflectiveMesh();
        stripes.GetComponent<MeshFilter>().sharedMesh = reflectiveMesh;
        stripes.transform.localPosition = vest.pos;
        stripes.transform.localScale = vest.scale;
        stripes.sharedMaterial = PuppetKit.Lit(Reflective, .75f, .08f);

        var capBand = Primitive(rig.hatPivot, "Hi-vis cap band", PuppetKit.Shape.Cylinder, HiVis,
            new Vector3(0f, -.38f * rig.layout.headR, -.02f * rig.layout.headR),
            new Vector3(2.09f * rig.layout.headR, .065f * rig.layout.headR, 2.09f * rig.layout.headR));
        capBand.localRotation = Quaternion.Euler(-8f, 0f, 0f);

        paddle = PuppetKit.Node(rig.handR, "STOP paddle");
        Primitive(paddle, "Paddle handle", PuppetKit.Shape.Cylinder, Navy,
            new Vector3(0f, .17f, 0f), new Vector3(.032f, .22f, .032f));
        var sign = MeshPart(paddle, "Double-sided STOP octagon", Reflective);
        if (paddleMesh == null) paddleMesh = BuildPaddleMesh();
        sign.GetComponent<MeshFilter>().sharedMesh = paddleMesh;
        sign.sharedMaterials = new[] { PuppetKit.Lit(Reflective, .4f), PuppetKit.Lit(StopRed, .25f) };
        sign.transform.localPosition = new Vector3(0f, .46f, 0f);
        Pose(Vector3.zero, Quaternion.identity, 0f, 0f, false, 0f);
    }

    /// <param name="phase">Elapsed cosmetic seconds, not a normalized crossing phase.</param>
    public void Pose(Vector3 position, Quaternion rotation, float walkSpeed, float phase, bool stopping, float deltaTime)
    {
        if (rig.go == null) return;
        rig.root.SetPositionAndRotation(position, rotation);
        float dt = Mathf.Clamp(deltaTime, 0f, .1f);
        stopBlend = Mathf.MoveTowards(stopBlend, stopping ? 1f : 0f, dt * 5f);
        float walking = Mathf.Clamp01(Mathf.Abs(walkSpeed) / 1.2f);
        float step = Mathf.Sin(phase * 7.5f);
        float sway = Mathf.Sin(phase * 2f);
        rig.Body(new Vector3(0f, Mathf.Abs(step) * .026f * walking, 0f),
            Quaternion.Euler(0f, 0f, step * 2.3f * walking), .004f * sway * (1f - walking));
        rig.Torso(walking * 4f, 0f);
        rig.Legs(step * 25f * walking, -step * 25f * walking, 2f);
        rig.Head(-3f, Mathf.Sin(phase * .75f) * (stopping ? 14f : 6f), 0f);
        // Right hand raises the paddle out to the side; left hand sweeps a reassuring "come along".
        rig.Arms(Mathf.Lerp(-step * 18f * walking, -75f + sway * 13f, stopBlend),
            Mathf.Lerp(10f, 43f + sway * 10f, stopBlend),
            Mathf.Lerp(step * 9f * walking, -12f, stopBlend), Mathf.Lerp(12f, 103f, stopBlend));
        // Cancel the arm rotation at the wrist to keep STOP vertical and legible while raising it.
        rig.handR.localRotation = Quaternion.Inverse(rig.armR.localRotation);
        paddle.localRotation = Quaternion.Euler(0f, Mathf.Sin(phase * .5f) * 7f * stopBlend, 0f);
        rig.SetMouth(.08f + .035f * (sway + 1f), 1.15f);
        rig.TickEyes(dt, ref dice);
    }

    public void Destroy() => rig.Destroy();

    static MeshRenderer MeshPart(Transform parent, string name, Color color)
    {
        var renderer = PuppetKit.Part(parent, name, out _);
        renderer.sharedMaterial = PuppetKit.Lit(color);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return renderer;
    }

    static Transform Primitive(Transform parent, string name, PuppetKit.Shape shape, Color color,
        Vector3 position, Vector3 scale)
    {
        var renderer = PuppetKit.Part(parent, name, out var filter);
        var part = new PartSpec { on = true, shape = shape, color = color, pos = position, scale = scale, shadow = true };
        PuppetRig.ApplyPart(renderer, filter, in part);
        return renderer.transform;
    }

    static Mesh BuildReflectiveMesh()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        AddRibbon(-9f, 1f, -180f, 180f, 2, 48);
        AddRibbon(25f, 34f, -180f, 180f, 2, 48);
        AddRibbon(-38f, 63f, -28f, -18f, 12, 2);
        AddRibbon(-38f, 63f, 18f, 28f, 12, 2);
        AddRibbon(-38f, 63f, 152f, 162f, 12, 2);
        AddRibbon(-38f, 63f, 198f, 208f, 12, 2);
        var mesh = new Mesh { name = "Crossing guard fitted reflective ribbon", hideFlags = HideFlags.DontSave };
        mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;

        void AddRibbon(float low, float high, float left, float right, int rows, int columns)
        {
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                {
                    float a = Mathf.Lerp(low, high, (float)y / rows), b = Mathf.Lerp(low, high, (float)(y + 1) / rows);
                    float c = Mathf.Lerp(left, right, (float)x / columns), d = Mathf.Lerp(left, right, (float)(x + 1) / columns);
                    int k = vertices.Count;
                    vertices.Add(OnVest(a, c)); vertices.Add(OnVest(b, c));
                    vertices.Add(OnVest(b, d)); vertices.Add(OnVest(a, d));
                    triangles.Add(k); triangles.Add(k + 2); triangles.Add(k + 1);
                    triangles.Add(k); triangles.Add(k + 3); triangles.Add(k + 2);
                }
        }
    }

    static Vector3 OnVest(float latitude, float longitude)
    {
        float lat = latitude * Mathf.Deg2Rad, lon = longitude * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(lat) * Mathf.Sin(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Cos(lon)) * .507f;
    }

    static Mesh BuildPaddleMesh()
    {
        var vertices = new List<Vector3>();
        var white = new List<int>();
        var red = new List<int>();
        // The lettering is geometry: no font atlas, texture, per-sign material, or billboard dependency.
        string[] glyphs = {
            "11111" + "10000" + "11111" + "00001" + "11111",
            "11111" + "00100" + "00100" + "00100" + "00100",
            "01110" + "10001" + "10001" + "10001" + "01110",
            "11110" + "10001" + "11110" + "10000" + "10000",
        };
        for (int side = -1; side <= 1; side += 2)
        {
            Disk(.28f, .014f * side, side, white);
            Disk(.25f, .015f * side, side, red);
            const float pixel = .017f, width = 23f * pixel;
            for (int letter = 0; letter < glyphs.Length; letter++)
                for (int row = 0; row < 5; row++)
                    for (int column = 0; column < 5; column++)
                    {
                        if (glyphs[letter][row * 5 + column] != '1') continue;
                        // A viewer looking toward the +Z face sees local -X on their right. Mirror the
                        // glyph layout (but not the word) for that view; the opposite face uses +X.
                        float x = (-width * .5f + (letter * 6 + column) * pixel) * -side;
                        float y = (.5f * 5f - row) * pixel;
                        int k = vertices.Count;
                        vertices.Add(new Vector3(x, y - pixel, .016f * side));
                        vertices.Add(new Vector3(x - pixel * side, y - pixel, .016f * side));
                        vertices.Add(new Vector3(x - pixel * side, y, .016f * side));
                        vertices.Add(new Vector3(x, y, .016f * side));
                        white.Add(k); white.Add(k + 2); white.Add(k + 1);
                        white.Add(k); white.Add(k + 3); white.Add(k + 2);
                    }
        }
        var mesh = new Mesh { name = "Crossing guard two-sided STOP paddle", hideFlags = HideFlags.DontSave, subMeshCount = 2 };
        mesh.SetVertices(vertices); mesh.SetTriangles(white, 0); mesh.SetTriangles(red, 1);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;

        void Disk(float radius, float z, int side, List<int> indices)
        {
            int center = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, z));
            for (int i = 0; i < 8; i++)
            {
                float angle = (22.5f + i * 45f) * Mathf.Deg2Rad;
                vertices.Add(new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, z));
            }
            for (int i = 0; i < 8; i++)
            {
                indices.Add(center);
                indices.Add(center + 1 + (side > 0 ? i : (i + 1) % 8));
                indices.Add(center + 1 + (side > 0 ? (i + 1) % 8 : i));
            }
        }
    }
}
