using UnityEngine;

/// <summary>
/// The original googly eyes reference meshes imported from a .blend file. On machines without that importer the
/// filters survive but their meshes do not. Repair just those missing slots, on the existing authored eye objects.
/// </summary>
public static class MagnusEyeFallback
{
    /// <summary>Number of missing filters repaired. Existing imported geometry and its transforms remain untouched.</summary>
    public static int RepairMissing(Transform magnus)
    {
        if (magnus == null) return 0;
        int repaired = 0;
        foreach (var eye in magnus.GetComponentsInChildren<GooglyEye>(true))
        {
            // Authored eye roots are .06m/unit at Magnus scale60. The white is .114m across; the pupil .051m.
            // Keep the root pose and the animated pupil's position: GooglyEye still owns its +/- .25 XY jiggle.
            repaired += Repair(eye.transform.Find("backplate"), new Vector3(1.9f, 1.9f, .16f),
                ShopMaterials.Get("magnus_eye_white", new Color(.97f, .98f, .96f), .48f), false);
            repaired += Repair(eye.Eye, new Vector3(.85f, .85f, .10f),
                ShopMaterials.Get("magnus_eye_pupil", new Color(.035f, .045f, .05f), .6f), false);
            // The missing transparent shell becomes a thin dark mounting rim behind the white, not an opaque
            // replacement shell covering the pupil. Reuse its renderer rather than adding a second eye assembly.
            repaired += Repair(eye.transform.Find("container"), new Vector3(2.05f, 2.05f, .18f),
                ShopMaterials.Get("magnus_eye_rim", new Color(.14f, .16f, .17f), .3f), true);
        }
        return repaired;
    }

    static int Repair(Transform part, Vector3 scale, Material material, bool rim)
    {
        if (part == null) return 0;
        var filter = part.GetComponent<MeshFilter>();
        var renderer = part.GetComponent<MeshRenderer>();
        if (filter == null || renderer == null || filter.sharedMesh != null) return 0;
        var sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        if (sphere == null) return 0;
        filter.sharedMesh = sphere;
        part.localScale = scale;
        if (rim) part.localPosition = new Vector3(0f, 0f, -.055f);
        renderer.sharedMaterial = material;
        return 1;
    }
}
