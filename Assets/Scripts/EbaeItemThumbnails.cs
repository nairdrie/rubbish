using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

/// <summary>Local product photos made from the exact renderer-only models used by loose junk.
/// No item instances, physics, purchases or network objects are created. Two queued photos per
/// UI frame keep a full keep-crate from stalling the laptop; each definition is rendered once.</summary>
public sealed class EbaeItemThumbnails : IDisposable
{
    public const int Resolution = 192;
    const int PreviewLayer = 31;
    readonly Dictionary<string, Texture2D> photos = new Dictionary<string, Texture2D>();
    readonly Dictionary<string, List<Image>> waiting = new Dictionary<string, List<Image>>();
    readonly Queue<string> queue = new Queue<string>();
    GameObject studio;
    Camera camera;
    bool warned;

    public Image Request(string defId, string itemName)
    {
        var picture = new Image { scaleMode = ScaleMode.ScaleToFit, tooltip = itemName,
            pickingMode = PickingMode.Ignore };
        picture.AddToClassList("item-photo");
        if (string.IsNullOrEmpty(defId) || ItemCatalog.Get(defId) == null) return picture;
        if (photos.TryGetValue(defId, out var photo)) picture.image = photo;
        else
        {
            if (!waiting.TryGetValue(defId, out var requests))
            {
                waiting[defId] = requests = new List<Image>();
                queue.Enqueue(defId);
            }
            requests.Add(picture);
        }
        return picture;
    }

    public void Tick()
    {
        for (int i = 0; i < 2 && queue.Count > 0; i++)
        {
            string id = queue.Dequeue();
            Texture2D photo = null;
            try { photo = Photograph(ItemCatalog.Get(id)); }
            catch (Exception e)
            {
                if (!warned) Debug.LogWarning("[eBae] Item photograph unavailable: " + e.Message);
                warned = true;
            }
            photos[id] = photo;
            foreach (var image in waiting[id]) image.image = photo;
            waiting.Remove(id);
        }
    }

    void EnsureStudio()
    {
        if (studio != null) { studio.SetActive(true); return; }
        studio = new GameObject("eBae photo studio (visuals only)") { hideFlags = HideFlags.HideAndDontSave, layer = PreviewLayer };
        // Outside the playable town and every gameplay camera's far clip. Layer isolation prevents
        // the product lamps lighting the world; no shared RenderSettings/camera state is changed.
        studio.transform.position = new Vector3(0f, -10000f, 0f);
        var cameraObject = new GameObject("Photo camera") { hideFlags = HideFlags.HideAndDontSave };
        cameraObject.transform.SetParent(studio.transform, false);
        camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.orthographic = true;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.94f, 0.93f, 0.88f, 1f);
        camera.cullingMask = 1 << PreviewLayer;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 12f;
        Lamp("Key", new Vector3(-2f, 3f, 3f), 35f, new Color(1f, 0.96f, 0.89f));
        Lamp("Fill", new Vector3(3f, 1.5f, -1f), 25f, new Color(0.84f, 0.92f, 1f));
    }

    void Lamp(string name, Vector3 position, float intensity, Color color)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(studio.transform, false);
        go.transform.localPosition = position;
        var lamp = go.AddComponent<Light>();
        lamp.type = LightType.Point;
        lamp.range = 9f;
        lamp.intensity = intensity;
        lamp.color = color;
        lamp.cullingMask = 1 << PreviewLayer;
        lamp.shadows = LightShadows.None;
    }

    /// <summary>Padding around a unit-normalized model, including diagonal corners at the photo angle.</summary>
    public static float FrameSize(Vector3 size) => Mathf.Max(0.1f, size.magnitude * 0.59f);

    Texture2D Photograph(ItemDef def)
    {
        if (def == null) return null;
        EnsureStudio();
        GameObject model = null;
        Texture2D pixels = null;
        var target = RenderTexture.GetTemporary(Resolution, Resolution, 24, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        try
        {
            model = ItemVisuals.Build(def, studio.transform);
            foreach (var child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = PreviewLayer;
            var local = SortingKit.LocalRendererBounds(model.transform);
            model.transform.localScale = Vector3.one / Mathf.Max(0.01f, Mathf.Max(local.size.x, local.size.y, local.size.z));
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return null;
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            var direction = new Vector3(1f, 0.72f, 1.3f).normalized;
            camera.transform.SetPositionAndRotation(bounds.center + direction * 4f, Quaternion.LookRotation(-direction));
            camera.orthographicSize = FrameSize(bounds.size);
            camera.targetTexture = target;
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(camera, request)) RenderPipeline.SubmitRenderRequest(camera, request);
            else if (GraphicsSettings.currentRenderPipeline == null) camera.Render();
            else throw new InvalidOperationException("The active render pipeline cannot render product photos.");
            RenderTexture.active = target;
            pixels = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false)
                { name = "eBae photo: " + def.id, hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear };
            pixels.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
            pixels.Apply(false, true);
            return pixels;
        }
        catch { if (pixels != null) Destroy(pixels); throw; }
        finally
        {
            camera.targetTexture = null;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            // Destroy is deferred in play mode, so hide this model before photographing the next one.
            if (model != null) { model.SetActive(false); Destroy(model); }
            studio.SetActive(false);
        }
    }

    public void Dispose()
    {
        foreach (var texture in photos.Values) if (texture != null) Destroy(texture);
        photos.Clear(); waiting.Clear(); queue.Clear();
        if (studio != null) { studio.SetActive(false); Destroy(studio); }
        studio = null; camera = null;
    }

    static void Destroy(Object obj)
    {
        if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
    }
}
