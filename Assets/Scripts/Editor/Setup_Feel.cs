using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Optional polish for the Feel system (everything already works with zero setup): creates unlit particle
/// materials under Assets/Materials/Generated/Feel/ and registers them in RubbishAssets as "mat/fx_unlit"
/// (opaque URP Unlit) and "mat/fx_additive" (additive URP Unlit, used for sparkles). Idempotent.
/// </summary>
public static class Setup_Feel
{
    const string Folder = "Assets/Materials/Generated/Feel";
    const string UnlitPath = Folder + "/fx_unlit.mat";
    const string AdditivePath = Folder + "/fx_additive.mat";

    [MenuItem("Rubbish/Setup/Feel")]
    public static void Run()
    {
        EnsureFolder(Folder);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            Debug.LogWarning("[Setup_Feel] URP Unlit shader not found; Fx will fall back to the default primitive material.");
            return;
        }

        var unlit = GetOrCreate(UnlitPath, shader, out bool unlitNew);
        ConfigureOpaque(unlit);
        var additive = GetOrCreate(AdditivePath, shader, out bool addNew);
        ConfigureAdditive(additive);
        EditorUtility.SetDirty(unlit);
        EditorUtility.SetDirty(additive);

        RubbishAssets.EditorRegisterMaterial("mat/fx_unlit", unlit);
        RubbishAssets.EditorRegisterMaterial("mat/fx_additive", additive);
        AssetDatabase.SaveAssets();

        Debug.Log($"[Setup_Feel] fx_unlit {(unlitNew ? "created" : "updated")}, fx_additive {(addNew ? "created" : "updated")}; " +
                  "registered mat/fx_unlit + mat/fx_additive in RubbishAssets.");
    }

    static Material GetOrCreate(string path, Shader shader, out bool created)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        created = m == null;
        if (created)
        {
            m = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(m, path);
        }
        else if (m.shader != shader) m.shader = shader;
        return m;
    }

    static void ConfigureOpaque(Material m)
    {
        m.SetFloat("_Surface", 0f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_SrcBlend", (float)BlendMode.One);
        m.SetFloat("_DstBlend", (float)BlendMode.Zero);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.Zero);
        m.SetFloat("_ZWrite", 1f);
        m.SetFloat("_Cull", (float)CullMode.Back);
        m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Opaque");
        m.renderQueue = (int)RenderQueue.Geometry;
        m.SetColor("_BaseColor", Color.white);
        m.enableInstancing = true;
    }

    static void ConfigureAdditive(Material m)
    {
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 2f); // URP BlendMode.Additive
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)BlendMode.One);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_Cull", (float)CullMode.Off);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
        m.SetShaderPassEnabled("DepthOnly", false);
        m.SetShaderPassEnabled("ShadowCaster", false);
        m.SetColor("_BaseColor", Color.white);
        m.enableInstancing = true;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}
