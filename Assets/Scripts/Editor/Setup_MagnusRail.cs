using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Magnus's cart and track in ShopScene (Docs/Progression.md §6.3), idempotent: every run deletes what it built last time
/// (and the hand-authored originals) and rebuilds it.
///   - The TRACK is one straight line between its only two stops: the chess board and the Sort Stop at the sorting table.
///     The old stretch down to the dump area behind the truck (and its "Truck Stop") is removed: he cannot go there.
///     This script builds the chess-end siding under "Magnus Track" (always visible); the rest of the line to the Sort
///     Stop comes with his job and is built by Setup_GarageUpgrades (level-gated) with <see cref="BuildTrack"/>.
///   - The CART is a little rail trolley that faces along that line: flanged wheels that sit on the rails, a square
///     deck for Magnus, painted side skirts with a MAGNUS nameplate, sprung buffers, a headlamp, a tail lantern and a
///     brass bell. Built under "Magnus Cart/Chassis"; MagnusTrack gets the wheel pivots and the travel frame.
/// Run after Setup_SortingStation and before Setup_GarageUpgrades (its cart props measure the chassis), then
/// Setup_DepotRow so the four lot interiors pick it up.
/// </summary>
public static class Setup_MagnusRail
{
    public const string ShopScenePath = "Assets/Scenes/ShopScene.unity";
    public const string MaterialDir = "Assets/Materials/Generated/Garage";
    public const string CartName = "Magnus Cart", TrackName = "Magnus Track", ChassisName = "Chassis", SidingName = "Siding";

    /// <summary>The line's two ends (ShopScene world XZ; y is the floor).</summary>
    public static readonly Vector3 ChessStop = new Vector3(3.275f, 0f, 0.72f);
    public static readonly Vector3 SortStop = new Vector3(-3.4f, 0f, 1.2f);
    public const float Gauge = 0.45f;            // rail centre to track centre
    public const float WheelRadius = 0.09f;
    public const float DeckTop = 0.40f;          // Magnus's base sits here (unchanged from the old cart)
    public const float CartHalfLength = 0.5f;
    /// <summary>The always-visible chess siding runs this far past the chess stop toward the table.</summary>
    public const float SidingReach = 0.75f;
    /// <summary>Buffer stops stand this far beyond a stop (cart half-length + the buffers).</summary>
    public const float BufferBeyond = 0.62f;

    public static Vector3 Dir => (SortStop - ChessStop).normalized;
    public static Quaternion Along => Quaternion.LookRotation(Dir, Vector3.up);

    [MenuItem("Rubbish/Setup/MagnusRail")]
    public static void Run() => Build(ShopScenePath);

    public static string Build(string scenePath)
    {
        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = false;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            opened = true;
        }
        var log = new System.Text.StringBuilder();
        try
        {
            Transform cart = null, track = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == CartName) cart = root.transform;
                else if (root.name == TrackName) track = root.transform;
            }
            if (cart == null || track == null)
            {
                log.AppendLine("ShopScene has no 'Magnus Cart' / 'Magnus Track' root: nothing built.");
                Debug.LogWarning("[Setup_MagnusRail] " + log);
                return log.ToString();
            }
            var m = new Mats(MaterialDir);
            Physics.SyncTransforms();

            // ---- track: drop everything but the chess stop (the old rail to the dump area, its ties, end stops and Truck Stop)
            Transform chessStop = null;
            int removed = 0;
            for (int i = track.childCount - 1; i >= 0; i--)
            {
                var c = track.GetChild(i);
                if (c.name == "Chess Stop") { chessStop = c; continue; }
                Object.DestroyImmediate(c.gameObject);
                removed++;
            }
            if (chessStop == null)
            {
                chessStop = new GameObject("Chess Stop").transform;
                chessStop.SetParent(track, false);
            }
            chessStop.position = new Vector3(ChessStop.x, cart.position.y, ChessStop.z);
            float floor = track.position.y;
            var siding = Node(track, SidingName, new Vector3(0f, floor, 0f), Quaternion.identity);
            BuildTrack(siding, m, ChessStop - Dir * BufferBeyond, ChessStop + Dir * SidingReach, floor, true, false, false);
            log.AppendLine($"Track: removed {removed} old pieces (incl. the dump-area rail and Truck Stop); built the chess siding.");

            // ---- cart: drop the old box-on-wheels (and any previous chassis), build the trolley
            for (int i = cart.childCount - 1; i >= 0; i--)
            {
                var c = cart.GetChild(i);
                if (c.name == "Body" || c.name == "Bumper" || c.name == "Wheel" || c.name == ChassisName) Object.DestroyImmediate(c.gameObject);
            }
            var wheels = BuildCart(cart, m);

            var mt = cart.GetComponent<MagnusTrack>();
            if (mt != null)
            {
                var so = new SerializedObject(mt);
                so.FindProperty("chessStop").objectReferenceValue = chessStop;
                var w = so.FindProperty("wheels");
                w.arraySize = wheels.Count;
                for (int i = 0; i < wheels.Count; i++) w.GetArrayElementAtIndex(i).objectReferenceValue = wheels[i];
                so.FindProperty("wheelRadius").floatValue = WheelRadius;
                so.FindProperty("travelFrame").objectReferenceValue = cart.Find(ChassisName);
                so.ApplyModifiedPropertiesWithoutUndo();
                log.AppendLine("MagnusTrack: chess stop, 4 wheel pivots, wheel radius and travel frame wired; no truck stop any more.");
            }
            else log.AppendLine("WARNING: 'Magnus Cart' has no MagnusTrack component.");

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            log.AppendLine($"Built the cart and the chess siding in {scenePath}.");
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
        Debug.Log("[Setup_MagnusRail] " + log);
        return log.ToString();
    }

    // =====================================================================================================
    // Track pieces (shared with Setup_GarageUpgrades for the job's stretch to the Sort Stop)
    // =====================================================================================================

    /// <summary>
    /// A stretch of track from <paramref name="a"/> to <paramref name="b"/> (world XZ; y = floor): two steel rails on
    /// timber sleepers with little rail chairs, and a striped buffer stop (with a lamp) at either end when asked.
    /// </summary>
    public static void BuildTrack(Transform parent, Mats m, Vector3 a, Vector3 b, float floor, bool bufferAtA, bool bufferAtB, bool bracesAtA = true)
    {
        a.y = b.y = floor;
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.05f) return;
        Vector3 dir = d / len, side = Vector3.Cross(Vector3.up, dir).normalized;
        Quaternion along = Quaternion.LookRotation(dir, Vector3.up);
        Vector3 mid = (a + b) * 0.5f;
        foreach (float s in new[] { -Gauge, Gauge })
        {
            WBox(parent, "Rail", mid + side * s + Vector3.up * 0.045f, new Vector3(0.045f, 0.035f, len), m.Steel, along);
            WBox(parent, "RailFoot", mid + side * s + Vector3.up * 0.022f, new Vector3(0.09f, 0.012f, len), m.Frame, along, false);
        }
        int ties = Mathf.Max(2, Mathf.RoundToInt(len / 0.42f));
        for (int i = 0; i < ties; i++)
        {
            Vector3 p = a + dir * (len * (i + 0.5f) / ties);
            WBox(parent, "Sleeper", p + Vector3.up * 0.012f, new Vector3(1.16f, 0.024f, 0.13f), m.Wood, along, false);
            foreach (float s in new[] { -Gauge, Gauge })
                WBox(parent, "Chair", p + side * s + Vector3.up * 0.03f, new Vector3(0.11f, 0.014f, 0.07f), m.Dark, along, false);
        }
        if (bufferAtA) BufferStop(parent, m, a, -dir, floor, bracesAtA);
        if (bufferAtB) BufferStop(parent, m, b, dir, floor, true);
    }

    /// <summary>A buffer stop at <paramref name="at"/> facing back down the track (<paramref name="outward"/> points off the end).</summary>
    static void BufferStop(Transform parent, Mats m, Vector3 at, Vector3 outward, float floor, bool braces)
    {
        at.y = floor;
        Vector3 side = Vector3.Cross(Vector3.up, outward).normalized;
        Quaternion face = Quaternion.LookRotation(outward, Vector3.up);
        var g = WNode(parent, "BufferStop", at, face);
        foreach (float s in new[] { -Gauge, Gauge })
        {
            WBox(g, "Post", at + side * s + Vector3.up * 0.15f, new Vector3(0.06f, 0.3f, 0.06f), m.Frame, face);
            // a raked brace back down to the rail foot
            if (!braces) continue;   // the chess end stands against the office step: no room for braces behind it
            Vector3 top = at + side * s + Vector3.up * 0.27f, foot = at + side * s + outward * 0.24f + Vector3.up * 0.03f;
            WBox(g, "Brace", (top + foot) * 0.5f, new Vector3(0.035f, Vector3.Distance(top, foot), 0.035f), m.Frame,
                 Quaternion.FromToRotation(Vector3.up, (top - foot).normalized));
        }
        WBox(g, "Beam", at + Vector3.up * 0.25f, new Vector3(Gauge * 2f + 0.22f, 0.11f, 0.06f), m.Red, face);
        for (int i = -2; i <= 2; i++)
            WBox(g, "BeamStripe", at + side * (i * 0.22f) + Vector3.up * 0.25f - outward * 0.031f, new Vector3(0.09f, 0.112f, 0.004f), m.White, face, false);
        foreach (float s in new[] { -0.25f, 0.25f })
            WCyl(g, "Pad", at + side * s + Vector3.up * 0.25f - outward * 0.05f, new Vector3(0.1f, 0.02f, 0.1f), m.Rubber, face * Quaternion.Euler(90f, 0f, 0f));
        WCyl(g, "LampStem", at + Vector3.up * 0.34f, new Vector3(0.025f, 0.04f, 0.025f), m.Dark, Quaternion.identity);
        WSphere(g, "Lamp", at + Vector3.up * 0.41f, 0.08f, m.LampRed);
    }

    // =====================================================================================================
    // The cart
    // =====================================================================================================

    /// <summary>Build "Chassis" under the cart, facing along the track. Returns the four wheel pivots (local X = axle).</summary>
    static List<Transform> BuildCart(Transform cart, Mats m)
    {
        var ch = WNode(cart, ChassisName, cart.position, Along);
        Vector3 P(float x, float y, float z) => ch.TransformPoint(new Vector3(x, y, z));
        Quaternion R(float x = 0f, float y = 0f, float z = 0f) => ch.rotation * Quaternion.Euler(x, y, z);
        const float L = CartHalfLength;                     // half length along the track
        const float axleZ = 0.3f, wheelY = 0.06f + WheelRadius;

        // Under-frame: two side sills over the wheels, two headstocks, a centre spine.
        foreach (float x in new[] { -0.36f, 0.36f })
            WBox(ch, "Sill", P(x, 0.24f, 0f), new Vector3(0.07f, 0.09f, L * 2f - 0.06f), m.Dark, R());
        foreach (float z in new[] { -L + 0.03f, L - 0.03f })
            WBox(ch, "Headstock", P(0f, 0.24f, z), new Vector3(0.86f, 0.1f, 0.06f), m.Dark, R());
        WBox(ch, "Spine", P(0f, 0.22f, 0f), new Vector3(0.1f, 0.06f, L * 2f - 0.1f), m.Frame, R(), false);

        // Deck: Magnus's square platform, a brass turntable ring on top of it and a cream trim under its lip.
        WBox(ch, "Deck", P(0f, DeckTop - 0.03f, 0f), new Vector3(0.94f, 0.06f, 0.94f), m.Frame, R());
        WBox(ch, "DeckTrim", P(0f, DeckTop - 0.07f, 0f), new Vector3(0.97f, 0.025f, 0.97f), m.Cream, R(), false);
        WCyl(ch, "Turntable", P(0f, DeckTop + 0.004f, 0f), new Vector3(0.74f, 0.004f, 0.74f), m.Gold, R());
        for (int i = 0; i < 8; i++)
        {
            Quaternion q = Quaternion.Euler(0f, i * 45f + 22.5f, 0f);
            WCyl(ch, "Rivet", P(0f, DeckTop + 0.01f, 0f) + ch.rotation * (q * new Vector3(0f, 0f, 0.42f)), new Vector3(0.03f, 0.006f, 0.03f), m.Steel, R());
        }

        // Painted side skirts with a nameplate (the side faces are what you see from the walkway and the table).
        foreach (float sx in new[] { -1f, 1f })
        {
            WBox(ch, "Skirt", P(sx * 0.455f, 0.255f, 0f), new Vector3(0.03f, 0.17f, 0.74f), m.CartPaint, R());
            WBox(ch, "SkirtLine", P(sx * 0.472f, 0.315f, 0f), new Vector3(0.006f, 0.018f, 0.74f), m.Cream, R(), false);
            WBox(ch, "Plate", P(sx * 0.473f, 0.245f, 0f), new Vector3(0.008f, 0.085f, 0.36f), m.Cream, R(), false);
            WText(ch, "Name", "MAGNUS", P(sx * 0.479f, 0.245f, 0f), R(0f, sx > 0f ? -90f : 90f, 0f), new Vector2(0.33f, 0.07f), new Color(0.1f, 0.2f, 0.22f));
            // little leaf springs over each axle box
            foreach (float z in new[] { -axleZ, axleZ })
            {
                WBox(ch, "AxleBox", P(sx * 0.47f, wheelY + 0.01f, z), new Vector3(0.04f, 0.07f, 0.09f), m.Frame, R());
                WBox(ch, "Spring", P(sx * 0.47f, wheelY + 0.065f, z), new Vector3(0.035f, 0.018f, 0.2f), m.Steel, R(), false);
            }
        }

        // Wheels: a pivot per wheel (local X = the axle), a tyre, a flange inside the rail and a brass hub cap.
        var wheels = new List<Transform>();
        foreach (float z in new[] { -axleZ, axleZ })
        {
            WCyl(ch, "Axle", P(0f, wheelY, z), new Vector3(0.03f, Gauge + 0.02f, 0.03f), m.Steel, R(0f, 0f, 90f));
            foreach (float sx in new[] { -1f, 1f })
            {
                var pivot = WNode(ch, "WheelPivot", P(sx * Gauge, wheelY, z), R());
                WCyl(pivot, "Tyre", pivot.position, new Vector3(WheelRadius * 2f, 0.022f, WheelRadius * 2f), m.Dark, R(0f, 0f, 90f));
                WCyl(pivot, "Flange", P(sx * (Gauge - 0.03f), wheelY, z), new Vector3(WheelRadius * 2f + 0.035f, 0.006f, WheelRadius * 2f + 0.035f), m.Steel, R(0f, 0f, 90f));
                WCyl(pivot, "Hub", P(sx * (Gauge + 0.026f), wheelY, z), new Vector3(0.07f, 0.006f, 0.07f), m.Gold, R(0f, 0f, 90f));
                // one spoke mark so you can see it turn
                WBox(pivot, "Spoke", P(sx * (Gauge + 0.024f), wheelY, z), new Vector3(0.004f, WheelRadius * 1.6f, 0.022f), m.Cream, R(), false);
                wheels.Add(pivot);
            }
        }

        // Buffers at both ends, headlamp toward the table (+Z), tail lantern toward the chess board (−Z).
        foreach (float sz in new[] { -1f, 1f })
        {
            foreach (float x in new[] { -0.27f, 0.27f })
            {
                WCyl(ch, "BufferShank", P(x, 0.24f, sz * (L + 0.03f)), new Vector3(0.035f, 0.035f, 0.035f), m.Steel, R(90f, 0f, 0f));
                WCyl(ch, "BufferHead", P(x, 0.24f, sz * (L + 0.07f)), new Vector3(0.1f, 0.012f, 0.1f), m.Rubber, R(90f, 0f, 0f));
            }
            WBox(ch, "Hook", P(0f, 0.2f, sz * (L + 0.02f)), new Vector3(0.05f, 0.05f, 0.05f), m.Frame, R(), false);
        }
        WCyl(ch, "HeadlampRim", P(0f, 0.325f, L + 0.012f), new Vector3(0.11f, 0.02f, 0.11f), m.Gold, R(90f, 0f, 0f));
        WCyl(ch, "HeadlampLens", P(0f, 0.325f, L + 0.034f), new Vector3(0.085f, 0.004f, 0.085f), m.LampWarm, R(90f, 0f, 0f));
        WCyl(ch, "TailRim", P(0f, 0.325f, -L - 0.012f), new Vector3(0.08f, 0.02f, 0.08f), m.Dark, R(90f, 0f, 0f));
        WCyl(ch, "TailLens", P(0f, 0.325f, -L - 0.034f), new Vector3(0.06f, 0.004f, 0.06f), m.LampRed, R(90f, 0f, 0f));

        // A brass bell on the rear corner of the deck (ding ding).
        Vector3 bell = P(0.39f, DeckTop, -0.39f);
        WCyl(ch, "BellPost", bell + Vector3.up * 0.08f, new Vector3(0.018f, 0.08f, 0.018f), m.Dark, Quaternion.identity);
        WBox(ch, "BellArm", bell + Vector3.up * 0.165f + ch.rotation * new Vector3(-0.03f, 0f, 0f), new Vector3(0.08f, 0.014f, 0.014f), m.Dark, R(), false);
        var dome = WSphere(ch, "Bell", bell + Vector3.up * 0.12f + ch.rotation * new Vector3(-0.06f, 0f, 0f), 0.09f, m.Gold);
        dome.localScale = new Vector3(0.09f, 0.075f, 0.09f);
        WSphere(ch, "Clapper", bell + Vector3.up * 0.075f + ch.rotation * new Vector3(-0.06f, 0f, 0f), 0.025f, m.Dark);

        // One collider for the whole trolley (the old body had one; keeps players from walking through the deck).
        var col = new GameObject("Collider");
        col.transform.SetParent(ch, false);
        col.transform.localPosition = new Vector3(0f, 0.25f, 0f);
        var box = col.AddComponent<BoxCollider>();
        box.size = new Vector3(0.94f, 0.3f, 0.98f);
        return wheels;
    }

    // =====================================================================================================
    // Primitive helpers (world-space placement)
    // =====================================================================================================

    static Transform Node(Transform parent, string name, Vector3 worldPos, Quaternion worldRot) => WNode(parent, name, worldPos, worldRot);

    public static Transform WNode(Transform parent, string name, Vector3 worldPos, Quaternion worldRot)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldPos, worldRot);
        go.layer = parent != null ? parent.gameObject.layer : 0;
        return go.transform;
    }

    static Transform Prim(PrimitiveType type, Transform parent, string name, Vector3 worldPos, Vector3 worldScale, Material mat, Quaternion worldRot, bool shadows)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(worldPos, worldRot);
        Vector3 ps = parent != null ? parent.lossyScale : Vector3.one;
        go.transform.localScale = new Vector3(worldScale.x / ps.x, worldScale.y / ps.y, worldScale.z / ps.z);
        go.layer = parent != null ? parent.gameObject.layer : 0;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        if (!shadows) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }

    public static Transform WBox(Transform parent, string name, Vector3 worldPos, Vector3 size, Material mat, Quaternion? rot = null, bool shadows = true) =>
        Prim(PrimitiveType.Cube, parent, name, worldPos, size, mat, rot ?? Quaternion.identity, shadows);

    /// <summary>Unity cylinder: height 2 along local Y, so size.y = half-height in metres.</summary>
    public static Transform WCyl(Transform parent, string name, Vector3 worldPos, Vector3 size, Material mat, Quaternion? rot = null) =>
        Prim(PrimitiveType.Cylinder, parent, name, worldPos, size, mat, rot ?? Quaternion.identity, true);

    public static Transform WSphere(Transform parent, string name, Vector3 worldPos, float diameter, Material mat) =>
        Prim(PrimitiveType.Sphere, parent, name, worldPos, Vector3.one * diameter, mat, Quaternion.identity, true);

    public static TextMeshPro WText(Transform parent, string name, string text, Vector3 worldPos, Quaternion worldRot, Vector2 box, Color color)
    {
        // TMP swaps in a RectTransform whose serialized anchoredPosition wins over localPosition on reload,
        // so position a plain holder and keep the text itself at the holder's origin.
        var holder = WNode(parent, name, worldPos, worldRot);
        var go = new GameObject("Text");
        go.transform.SetParent(holder, false);
        go.layer = holder.gameObject.layer;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.fontStyle = FontStyles.Bold;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 0.1f;
        tmp.fontSizeMax = 12f;
        tmp.margin = Vector4.zero;
        var rt = tmp.rectTransform;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = box;
        rt.anchoredPosition = Vector2.zero;
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        var r = go.GetComponent<MeshRenderer>();
        if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tmp.ForceMeshUpdate(true, true);
        return tmp;
    }

    // =====================================================================================================
    // Materials (shared Garage_* assets; the cart adds its own paint)
    // =====================================================================================================

    public class Mats
    {
        readonly string dir;
        public Material Steel, Frame, Dark, Red, Gold, White, Wood, Rubber, Cream, CartPaint, LampWarm, LampRed;

        public Mats(string dir)
        {
            this.dir = dir;
            if (!AssetDatabase.IsValidFolder(dir))
            {
                string parent = System.IO.Path.GetDirectoryName(dir)?.Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(dir));
            }
            Steel = Lit("Steel", new Color(0.72f, 0.74f, 0.78f), 0.75f, 0.8f);
            Frame = Lit("Frame", new Color(0.33f, 0.35f, 0.38f), 0.45f, 0.5f);
            Dark = Lit("Dark", new Color(0.09f, 0.09f, 0.1f), 0.3f);
            Red = Lit("Red", new Color(0.92f, 0.12f, 0.1f), 0.55f);
            Gold = Lit("Gold", new Color(1f, 0.82f, 0.2f), 0.65f, 0.35f);
            White = Lit("White", new Color(0.97f, 0.97f, 0.95f), 0.5f);
            Wood = Lit("Wood", new Color(0.55f, 0.34f, 0.16f), 0.15f);
            Rubber = Lit("Rubber", new Color(0.12f, 0.12f, 0.13f), 0.15f);
            Cream = Lit("CartCream", new Color(0.94f, 0.9f, 0.78f), 0.4f);
            CartPaint = Lit("CartPaint", new Color(0.13f, 0.5f, 0.54f), 0.6f, 0.1f);
            LampWarm = Lit("CartLampWarm", new Color(1f, 0.93f, 0.6f), 0.8f, 0f, new Color(1.6f, 1.3f, 0.6f));
            LampRed = Lit("CartLampRed", new Color(1f, 0.2f, 0.15f), 0.8f, 0f, new Color(1.4f, 0.15f, 0.1f));
        }

        Material Lit(string name, Color c, float smooth, float metal = 0f, Color? emission = null)
        {
            string path = $"{dir}/Garage_{name}.mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (shader != null && mat.shader != shader) mat.shader = shader;
            mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metal);
            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
