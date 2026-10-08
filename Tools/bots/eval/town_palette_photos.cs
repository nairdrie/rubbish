// Render the real 3D kit prefabs in isolated preview scenes, without touching authored scenes or saves.
var palette = TownAssetLibrary.Ensure();
System.IO.Directory.CreateDirectory("Logs/TownAuthoringQA");
var rendered = new System.Collections.Generic.List<string>();
foreach (var entry in palette.entries.Where(e => UnityEditor.AssetDatabase.GetAssetPath(e.prefab).StartsWith("Assets/Prefabs/TownAuthoring/")))
{
    var preview = new UnityEditor.PreviewRenderUtility();
    UnityEngine.Texture2D photo = null;
    try
    {
        var go = UnityEngine.Object.Instantiate(entry.prefab);
        preview.AddSingleGO(go);
        var bounds = TownPlacementTools.PrefabBounds(entry.prefab);
        preview.camera.transform.position = bounds.center + new UnityEngine.Vector3(1.2f, .85f, -1.4f).normalized * bounds.size.magnitude * 1.2f;
        preview.camera.transform.LookAt(bounds.center);
        preview.camera.fieldOfView = 38; preview.camera.nearClipPlane = .05f; preview.camera.farClipPlane = 500;
        preview.camera.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
        preview.camera.backgroundColor = new UnityEngine.Color(.18f, .22f, .24f);
        preview.lights[0].intensity = 1.8f; preview.lights[0].transform.rotation = UnityEngine.Quaternion.Euler(45, -30, 0);
        preview.lights[1].intensity = .7f;
        preview.ambientColor = new UnityEngine.Color(.5f, .5f, .5f);
        preview.BeginStaticPreview(new UnityEngine.Rect(0, 0, 600, 500));
        preview.Render(true); photo = preview.EndStaticPreview();
        var path = "Logs/TownAuthoringQA/" + entry.id.Replace("builtin/", "") + ".png";
        System.IO.File.WriteAllBytes(path, photo.EncodeToPNG()); rendered.Add(path);
    }
    finally { if (photo) UnityEngine.Object.DestroyImmediate(photo); preview.Cleanup(); }
}
System.IO.File.WriteAllText("Logs/TownAuthoringQA/palette-photos.txt", string.Join("\n", rendered));
return rendered.Count;
