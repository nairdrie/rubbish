using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Real authored Magnus bones, cosmetic builder only: no chess game, business or player save.</summary>
public class MagnusArmVisualTests
{
    static Type Visuals => Type.GetType("MagnusArmVisuals, Assembly-CSharp", true);
    static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    GameObject root;
    Component visuals;
    Transform shoulder, elbow, wrist, pointer, thumb, claw;
    IDictionary materials;
    readonly Dictionary<object, object> priorMaterials = new Dictionary<object, object>();

    [SetUp]
    public void SetUp()
    {
        materials = (IDictionary)Runtime("ShopMaterials").GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        foreach (DictionaryEntry entry in materials) priorMaterials.Add(entry.Key, entry.Value);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Magnus.prefab");
        Assert.NotNull(prefab);
        root = Object.Instantiate(prefab);
        root.name = "Magnus visual test";
        root.transform.position = new Vector3(8000f, 1000f, 8000f);
        shoulder = root.transform.Find("BasePivot/Shoulder");
        elbow = shoulder.Find("Elbow"); wrist = elbow.Find("Wrist");
        pointer = wrist.Find("Pointer"); thumb = wrist.Find("Thumb"); claw = wrist.Find("Claw Center");
        visuals = root.AddComponent(Visuals);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        var added = new List<object>();
        foreach (DictionaryEntry entry in materials)
        {
            if (priorMaterials.ContainsKey(entry.Key)) continue;
            if (entry.Value is Material material && material != null) Object.DestroyImmediate(material);
            added.Add(entry.Key);
        }
        foreach (object key in added) materials.Remove(key);
        priorMaterials.Clear();
    }

    void Build(int brain, int gripper) => Visuals.GetMethod("RebuildForLevels").Invoke(visuals, new object[] { brain, gripper });
    static bool Generated(Transform t)
    {
        for (; t != null; t = t.parent) if (t.name.StartsWith("Magnus Industrial Arm / ")) return true;
        return false;
    }

    List<Renderer> GeneratedRenderers()
    {
        var result = new List<Renderer>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (Generated(r.transform)) result.Add(r);
        return result;
    }

    [TestCase(0, 0, 0)]
    [TestCase(0, 2, 0)]
    [TestCase(1, 0, 1)]
    [TestCase(1, 1, 2)]
    [TestCase(3, 2, 3)]
    [TestCase(4, 20, 3)]
    public void VisualStagesFollowExistingJobAndGripperLevels(int brain, int gripper, int tier)
    {
        Assert.AreEqual(tier, Visuals.GetMethod("TierFor").Invoke(null, new object[] { brain, gripper }));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void EachEmployedTierBuildsSolidAttachedRendererOnlyMachinery(int gripper)
    {
        Build(1, gripper);
        var renderers = GeneratedRenderers();
        Assert.Greater(renderers.Count, 50, "A complete base, jointed arm and working gripper—not two added sticks.");
        Assert.Less(renderers.Count, 160, "Keep the four-depot presentation lightweight.");
        foreach (var renderer in renderers)
        {
            Assert.NotNull(renderer.sharedMaterial);
            var filter = renderer.GetComponent<MeshFilter>();
            Assert.NotNull(filter.sharedMesh);
            Assert.Greater(filter.sharedMesh.vertexCount, 0);
            Assert.Less(renderer.bounds.size.magnitude, 2.1f, "Imported rig scale60 must not magnify metre-sized cosmetics.");
            Assert.IsNull(renderer.GetComponent<Collider>());
            Assert.IsNull(renderer.GetComponent<Rigidbody>());
            Assert.IsTrue(renderer.transform.IsChildOf(root.transform));
        }
    }

    [Test]
    public void CosmeticsLeaveAllIKAndNetworkJointTransformsUntouched()
    {
        var joints = new[] { root.transform.Find("BasePivot"), shoulder, elbow, wrist, pointer, thumb, claw };
        var positions = new Vector3[joints.Length]; var scales = new Vector3[joints.Length]; var rotations = new Quaternion[joints.Length];
        for (int i = 0; i < joints.Length; i++) { positions[i] = joints[i].localPosition; scales[i] = joints[i].localScale; rotations[i] = joints[i].localRotation; }
        for (int tier = 0; tier < 3; tier++)
        {
            Build(4, tier);
            for (int i = 0; i < joints.Length; i++)
            {
                Assert.AreEqual(positions[i], joints[i].localPosition);
                Assert.AreEqual(scales[i], joints[i].localScale);
                Assert.AreEqual(rotations[i], joints[i].localRotation);
            }
        }
    }

    [TestCase(0, 2)]
    [TestCase(1, 2)]
    [TestCase(2, 4)]
    public void ActualAnimatedFingersKeepPadsAtTheExistingClawCentre(int gripper, int expectedPads)
    {
        Build(1, gripper);
        pointer.localRotation = Quaternion.Euler(19.5f, 0f, 0f);
        thumb.localRotation = Quaternion.Euler(-19.5f, 0f, 0f);
        int pads = 0;
        foreach (var r in GeneratedRenderers())
        {
            if (!r.name.StartsWith("Replaceable rubber jaw pad")) continue;
            pads++;
            Assert.Less(Vector3.Distance(r.transform.position, claw.position), gripper == 2 ? .125f : .025f);
            Assert.IsTrue(r.transform.IsChildOf(pointer) || r.transform.IsChildOf(thumb), "Every jaw rides a real replicated finger joint.");
        }
        Assert.AreEqual(expectedPads, pads, "Quad-claw must have four moving fingers, not three static prongs.");
    }

    [Test]
    public void HeavyTierActuatorsFollowActualMovingElbowAndWrist()
    {
        Build(1, 1);
        var rods = new List<Transform>(); var before = new List<Vector3>();
        foreach (var r in GeneratedRenderers()) if (r.name == "Chrome piston rod") { rods.Add(r.transform); before.Add(r.transform.position); }
        Assert.AreEqual(3, rods.Count);
        elbow.localRotation *= Quaternion.Euler(35f, 0f, 0f);
        wrist.localRotation *= Quaternion.Euler(-30f, 0f, 0f);
        Visuals.GetMethod("UpdateActuators").Invoke(visuals, null);
        for (int i = 0; i < rods.Count; i++)
        {
            Assert.Greater(Vector3.Distance(rods[i].position, before[i]), .015f);
            Assert.Greater(rods[i].localScale.y, .01f);
            Assert.Less(rods[i].localScale.y, 1.5f);
        }
    }

    [Test]
    public void RebuildIsIdempotentAndChangingTierReleasesOwnedMeshes()
    {
        Build(1, 0);
        var old = GeneratedRenderers();
        Mesh casting = null;
        foreach (var r in old) if (r.name == "Upper chamfered casting") casting = r.GetComponent<MeshFilter>().sharedMesh;
        Assert.NotNull(casting);
        Build(1, 0);
        Assert.AreEqual(old.Count, GeneratedRenderers().Count);
        Assert.IsTrue(old[0] != null, "Unchanged levels do not rebuild the model.");
        Build(1, 2);
        Assert.IsTrue(casting == null, "Tier replacement destroys its runtime meshes.");
        Build(0, 0);
        Assert.IsEmpty(GeneratedRenderers());
    }

    [Test]
    public void ExistingEyesAndVisionAttachmentsAreNotHiddenOrMoved()
    {
        var eye = new GameObject("eye", typeof(MeshRenderer));
        eye.transform.SetParent(wrist, false);
        eye.transform.localPosition = new Vector3(-.003f, .001f, -.002f);
        var before = eye.transform.localPosition;
        Build(4, 2);
        Assert.IsTrue(eye.GetComponent<Renderer>().enabled);
        Assert.AreSame(wrist, eye.transform.parent);
        Assert.AreEqual(before, eye.transform.localPosition);
        Assert.IsTrue((bool)Visuals.GetMethod("IsLegacyClaw").Invoke(null, new object[] { "Tongs0" }));
        Assert.IsTrue((bool)Visuals.GetMethod("IsLegacyClaw").Invoke(null, new object[] { "ArcadeClaw" }));
        Assert.IsFalse((bool)Visuals.GetMethod("IsLegacyClaw").Invoke(null, new object[] { "XRayEyes" }));
        Assert.IsFalse((bool)Visuals.GetMethod("IsLegacyClaw").Invoke(null, new object[] { "BrainJar1" }));
    }

    [Test]
    public void CastingsHaveOutwardFacingClosedSurfacesAndCrispPlanarNormals()
    {
        Build(1, 0);
        int checkedMeshes = 0;
        foreach (var renderer in GeneratedRenderers())
        {
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            if (!mesh.name.StartsWith("Magnus cast ")) continue;
            checkedMeshes++;
            Vector3[] vertices = mesh.vertices, normals = mesh.normals;
            int[] indices = mesh.triangles;
            double signedVolume = 0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                signedVolume += Vector3.Dot(vertices[a], Vector3.Cross(vertices[b], vertices[c])) / 6.0;
                Vector3 face = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;
                Assert.Greater(Vector3.Dot(face, normals[a]), .99f, mesh.name + " has a flat outward face normal.");
                Assert.Greater(Vector3.Dot(face, normals[b]), .99f);
                Assert.Greater(Vector3.Dot(face, normals[c]), .99f);
            }
            Assert.Greater(signedVolume, 0.000001, mesh.name + " must not be inside-out/back-face culled.");
        }
        Assert.GreaterOrEqual(checkedMeshes, 7);
    }

    [Test]
    public void WristFaceMountLeavesTheMeasuredStockEyePlaneExposed()
    {
        Build(1, 0);
        // The generated frame's name contains a literal slash, not a Transform.Find path.
        Transform frame = null;
        foreach (Transform child in wrist) if (child.name == "Magnus Industrial Arm / Wrist") frame = child;
        Assert.NotNull(frame);
        var face = frame.Find("Face mounting plate");
        Assert.NotNull(face);
        var localBounds = face.GetComponent<MeshFilter>().sharedMesh.bounds;
        float faceFrontX = face.localPosition.x + localBounds.min.x * face.localScale.x;
        Assert.Greater(faceFrontX, -.02f, "The stock eyes sit at wrist x=-.025m and must be in front of the housing.");
        Assert.IsNull(frame.Find("Tool quick release"), "A full collar ring covers both eyes.");
    }

    Component[] Eyes() => root.GetComponentsInChildren(Runtime("GooglyEye"), true);

    int RepairEyes() => (int)Runtime("MagnusEyeFallback").GetMethod("RepairMissing").Invoke(null, new object[] { root.transform });

    void RemoveEyeMeshes()
    {
        foreach (var eye in Eyes())
            foreach (var filter in eye.GetComponentsInChildren<MeshFilter>(true)) filter.sharedMesh = null;
    }

    [Test]
    public void MissingImportedEyesReuseExactlyTheAuthoredSixRenderers()
    {
        RemoveEyeMeshes();
        var eyes = Eyes();
        Assert.AreEqual(2, eyes.Length);
        int renderers = root.GetComponentsInChildren<Renderer>(true).Length;
        var roots = new Vector3[2]; var pupils = new Vector3[2]; var pupilRefs = new Transform[2];
        for (int i = 0; i < eyes.Length; i++)
        {
            roots[i] = eyes[i].transform.localPosition;
            pupilRefs[i] = (Transform)eyes[i].GetType().GetField("Eye").GetValue(eyes[i]);
            pupils[i] = pupilRefs[i].localPosition;
        }

        Assert.AreEqual(6, RepairEyes());
        Assert.AreEqual(0, RepairEyes(), "Repair is idempotent, including repeated visual upgrade checks.");
        Assert.AreEqual(renderers, root.GetComponentsInChildren<Renderer>(true).Length);
        for (int i = 0; i < eyes.Length; i++)
        {
            Assert.AreEqual(roots[i], eyes[i].transform.localPosition);
            Assert.AreSame(pupilRefs[i], eyes[i].GetType().GetField("Eye").GetValue(eyes[i]));
            Assert.AreEqual(pupils[i], pupilRefs[i].localPosition, "Do not reset an animated pupil's jiggle position.");
            Assert.AreEqual(3, eyes[i].GetComponentsInChildren<Renderer>(true).Length);
            foreach (var filter in eyes[i].GetComponentsInChildren<MeshFilter>(true))
            {
                Assert.NotNull(filter.sharedMesh);
                Assert.Greater(filter.sharedMesh.vertexCount, 0);
            }
        }
    }

    [Test]
    public void WorkingImportedEyeGeometryAndMaterialsAreUntouched()
    {
        var sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        var parts = new List<MeshFilter>(); var scales = new List<Vector3>(); var materialsBefore = new List<Material>();
        foreach (var eye in Eyes())
            foreach (var filter in eye.GetComponentsInChildren<MeshFilter>(true))
            {
                filter.sharedMesh = sphere;
                filter.transform.localScale = Vector3.one * 1.17f;
                parts.Add(filter); scales.Add(filter.transform.localScale);
                materialsBefore.Add(filter.GetComponent<Renderer>().sharedMaterial);
            }
        Assert.AreEqual(0, RepairEyes());
        for (int i = 0; i < parts.Count; i++)
        {
            Assert.AreSame(sphere, parts[i].sharedMesh);
            Assert.AreEqual(scales[i], parts[i].transform.localScale);
            Assert.AreSame(materialsBefore[i], parts[i].GetComponent<Renderer>().sharedMaterial);
        }
    }

    [Test]
    public void FallbackEyeSizeAccountsForTheImportedScale60RigAndPupilStillMoves()
    {
        RemoveEyeMeshes(); RepairEyes();
        foreach (var eye in Eyes())
        {
            var white = eye.transform.Find("backplate");
            var pupil = (Transform)eye.GetType().GetField("Eye").GetValue(eye);
            Assert.That(Mathf.Abs(white.lossyScale.x), Is.EqualTo(.114f).Within(.002f));
            Assert.That(Mathf.Abs(pupil.lossyScale.x), Is.EqualTo(.051f).Within(.002f));
            Vector3 whiteBefore = white.position, pupilBefore = pupil.position;
            pupil.localPosition += new Vector3(.25f, 0f, 0f); // The displacement used by the existing GooglyEye motion.
            Assert.That(Vector3.Distance(pupilBefore, pupil.position), Is.EqualTo(.015f).Within(.001f));
            Assert.AreEqual(whiteBefore, white.position);
            Assert.Greater(pupil.localPosition.z - Mathf.Abs(pupil.localScale.z) * .5f,
                Mathf.Abs(white.localScale.z) * .5f, "The pupil is in front of the white, never buried inside it.");
        }
    }

    [Test]
    public void StockAndEveryUpgradeTierKeepBothFallbackEyesAndTheirPupils()
    {
        RemoveEyeMeshes();
        foreach (int brain in new[] { 0, 1, 2, 3, 4 })
        {
            Build(brain, Mathf.Clamp(brain - 1, 0, 2));
            foreach (var eye in Eyes())
                foreach (var filter in eye.GetComponentsInChildren<MeshFilter>(true))
                {
                    Assert.NotNull(filter.sharedMesh);
                    Assert.IsTrue(filter.GetComponent<Renderer>().enabled);
                }
        }
        Assert.AreEqual(2, Eyes().Length);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void WristBearingAndJawDrivesRemainBehindTheAuthoredEyePlane(int gripper)
    {
        Build(1, gripper);
        int checkedParts = 0;
        foreach (var renderer in GeneratedRenderers())
        {
            if (!(renderer.name.StartsWith("Wrist bearing") || renderer.name.StartsWith("Finger hinge") ||
                renderer.name.StartsWith("Finger drive block"))) continue;
            checkedParts++;
            var bounds = renderer.GetComponent<MeshFilter>().sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 inWrist = Quaternion.Inverse(wrist.rotation) * (renderer.transform.TransformPoint(p) - wrist.position);
                Assert.Greater(inWrist.x, -.02f, renderer.name + " must not project in front of eyes at wrist x=-.025m.");
            }
        }
        Assert.GreaterOrEqual(checkedParts, 11);
    }
}
