using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

public class LoadingSystem : MonoBehaviour
{
    static LoadingSystem _instance;
    Canvas _canvas;
    CanvasGroup _group;
    RawImage _img;
    VideoPlayer _vp;

    static bool _waitingForReady;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null) return;
        var go = new GameObject("LoadingSystem");
        _instance = go.AddComponent<LoadingSystem>();
        DontDestroyOnLoad(go);
        _instance.BuildUI();
    }

    void BuildUI()
    {
        // Canvas
        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = short.MaxValue;

        // Keep pixel sizes literal (764x432 stays 764x432)
        var scaler = gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;

        _group = gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 0f; 
        _canvas.enabled = false;

        // --- Background (#1c1922) ---
        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(_canvas.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color32(0x1c, 0x19, 0x22, 0xFF);
        var bgRT = bgGO.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        // --- Video overlay centered at 764x432 ---
        var imgGO = new GameObject("Overlay");
        imgGO.transform.SetParent(_canvas.transform, false);
        _img = imgGO.AddComponent<RawImage>();
        _img.raycastTarget = false;

        var rt = imgGO.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(764, 432); // fixed size, centered

        // VideoPlayer -> RenderTexture -> RawImage
        var tex = new RenderTexture(764, 432, 0);
        _img.texture = tex;

        _vp = gameObject.AddComponent<VideoPlayer>();
        _vp.renderMode = VideoRenderMode.RenderTexture;
        _vp.targetTexture = tex;
        _vp.isLooping = true;
        _vp.audioOutputMode = VideoAudioOutputMode.Direct;
        _vp.playOnAwake = false;
        _vp.source = VideoSource.Url;
        _vp.url = System.IO.Path.Combine(Application.streamingAssetsPath, "loading.mp4");
        _vp.waitForFirstFrame = true;
    }


    void Show()
    {
        _canvas.enabled = true;
        _group.alpha = 1f;
        if (!_vp.isPlaying) _vp.Play();
    }

    void Hide()
    {
        if (_vp.isPlaying) _vp.Stop();
        _group.alpha = 0f;
        _canvas.enabled = false;
    }

    // PUBLIC API --------

    /// Show loader now, load scene async, keep loader on top until Ready() is called.
    public static void LoadSceneAndWaitReady(string sceneName)
    {
        Ensure();
        _instance.StartCoroutine(_instance.LoadSceneFlow(sceneName));
    }

    /// Call this from your new scene when initialization is complete.
    public static void Ready()
    {
        _waitingForReady = false;
    }

    /// Generic wrapper for any coroutine you want to run behind the loader.
    public static void RunWithLoader(IEnumerator routine)
    {
        Ensure();
        _instance.StartCoroutine(_instance.Wrap(routine));
    }

    static void Ensure()
    {
        if (_instance == null) Bootstrap();
    }

    // FLOWS --------

    IEnumerator LoadSceneFlow(string sceneName)
    {
        Show();

        // Start loading
        var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        // Let Unity activate when ready; we’ll keep the overlay up ourselves.
        op.allowSceneActivation = true;

        // Wait until load completes
        while (!op.isDone)
            yield return null;

        // Scene is active now; wait for game to finish its own init.
        _waitingForReady = true;
        while (_waitingForReady)
            yield return null;

        Hide();
    }

    IEnumerator Wrap(IEnumerator routine)
    {
        Show();
        yield return routine;
        Hide();
    }
}
