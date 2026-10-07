using UnityEngine;

/// <summary>
/// A prop that rides on a moving part of the existing garage (Magnus's cart, wrist or claw). Built under the
/// GarageUpgrades root; on Awake it re-parents onto <see cref="targetPath"/> (a ShopScene root path, e.g.
/// "Magnus Cart/Magnus/BasePivot/Shoulder/Elbow/Wrist") with the local pose Setup_GarageUpgrades measured.
/// In DepotRow the shop roots are nested inside a lot's Interior, so the target must be found in that same lot.
/// </summary>
public class UpgradeAttachment : MonoBehaviour
{
    public string targetPath;
    public Vector3 localPosition;
    public Quaternion localRotation = Quaternion.identity;
    public Vector3 localScale = Vector3.one;

    void Awake()
    {
        if (MagnusArmVisuals.IsLegacyClaw(name)) { gameObject.SetActive(false); return; }
        if (!Application.isPlaying || string.IsNullOrEmpty(targetPath)) return;
        var target = Resolve(transform, targetPath);
        if (target == null) return;
        transform.SetParent(target, false);
        transform.localPosition = localPosition;
        transform.localRotation = localRotation;
        transform.localScale = localScale;
    }

    /// <summary>
    /// Resolve a ShopScene root path from this attachment's context. A DepotRow copy may only attach to the
    /// Magnus inside its own lot; if that target is missing, never fall back to another lot or a scene root.
    /// </summary>
    public static Transform Resolve(Transform attachment, string path)
    {
        if (attachment == null || string.IsNullOrEmpty(path)) return null;
        var lot = attachment.GetComponentInParent<DepotLot>(true);
        if (lot != null)
        {
            var interior = lot.interior != null ? lot.interior.transform : null;
            if (interior == null || interior.parent != lot.transform) return null;
            return interior.Find(path);
        }
        return Resolve(attachment.gameObject.scene, path);
    }

    /// <summary>Find "Root/Child/..." among a scene's root objects.</summary>
    public static Transform Resolve(UnityEngine.SceneManagement.Scene scene, string path)
    {
        if (!scene.IsValid() || string.IsNullOrEmpty(path)) return null;
        int slash = path.IndexOf('/');
        string rootName = slash < 0 ? path : path.Substring(0, slash);
        foreach (var r in scene.GetRootGameObjects())
        {
            if (r.name != rootName) continue;
            if (slash < 0) return r.transform;
            var t = r.transform.Find(path.Substring(slash + 1));
            if (t != null) return t;
        }
        return null;
    }
}
