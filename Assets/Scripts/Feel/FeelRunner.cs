using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Hidden DontDestroyOnLoad host for the Feel system (HUD, sound, particles, juice, night ambience). Created lazily by the
/// first FeelApi call in play mode, and eagerly after the first scene loads so the HUD appears with zero
/// scene setup. Never created in edit mode or while the application is quitting.
/// </summary>
[DefaultExecutionOrder(-50)]
public class FeelRunner : MonoBehaviour
{
    static FeelRunner instance;
    static bool quitting;
    static int errorsLogged;
    const int MaxErrorsLogged = 20;

    FeelSfxPlayer sfx;
    FeelFx fx;
    FeelJuice juice;
    FeelHud hud;
    readonly FeelAmbience ambience = new FeelAmbience();
    int prewarmIndex;

    /// <summary>Sound pool (created on first use).</summary>
    public FeelSfxPlayer Sfx => sfx ??= new FeelSfxPlayer(transform);
    /// <summary>Mesh-particle system (created on first use).</summary>
    public FeelFx Fx => fx ??= new FeelFx(transform);
    /// <summary>Scale tweens + hit stop.</summary>
    public FeelJuice Juice => juice ??= new FeelJuice();
    /// <summary>Runtime-built HUD canvas (null only if building it failed).</summary>
    public FeelHud Hud
    {
        get
        {
            if (hud == null)
            {
                try { hud = FeelHud.Create(transform); }
                catch (Exception e) { Report(e); }
            }
            return hud;
        }
    }

    /// <summary>The live runner, creating it if needed. Null in edit mode / while quitting.</summary>
    public static FeelRunner Get()
    {
        if (instance != null) return instance;
        if (!Application.isPlaying || quitting) return null;
        try
        {
            var go = new GameObject("[Feel]");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<FeelRunner>();
        }
        catch (Exception e) { Report(e); }
        return instance;
    }

    /// <summary>Log a swallowed exception from a Feel call (capped so a broken caller can't spam).</summary>
    public static void Report(Exception e)
    {
        if (errorsLogged >= MaxErrorsLogged) return;
        errorsLogged++;
        Debug.LogWarning($"[Feel] swallowed exception: {e}");
    }

    /// <summary>Destroy that also works in edit mode (previews / verification evals).</summary>
    public static void SafeDestroy(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        GameClock.MorningCame += OnMorningCame;
        _ = Hud; // build the HUD right away so it can listen to cash/day events
    }

    void OnMorningCame()
    {
        try { ambience.OnMorning(); }
        catch (Exception e) { Report(e); }
    }

    void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        GameClock.MorningCame -= OnMorningCame;
        juice?.RestoreTime();
        fx?.Dispose();
        if (instance == this) instance = null;
    }

    void OnApplicationQuit()
    {
        quitting = true;
        juice?.RestoreTime();
    }

    void OnActiveSceneChanged(Scene from, Scene to)
    {
        try
        {
            fx?.ClearAll();
            hud?.OnSceneChanged();
        }
        catch (Exception e) { Report(e); }
    }

    void Update()
    {
        try { juice?.TickTime(); }
        catch (Exception e) { Report(e); }
        try { ambience.Tick(); }
        catch (Exception e) { Report(e); }

        // Synthesize one clip per frame in the background so first plays never hitch.
        if (prewarmIndex < FeelSfxSynth.KnownIds.Length)
        {
            try { FeelSfxSynth.GetClip(FeelSfxSynth.KnownIds[prewarmIndex]); }
            catch (Exception e) { Report(e); }
            prewarmIndex++;
        }
    }

    void LateUpdate()
    {
        try { juice?.Tick(Time.unscaledDeltaTime); }
        catch (Exception e) { Report(e); }
        try { fx?.Tick(Time.deltaTime); }
        catch (Exception e) { Report(e); }
    }

    // Statics survive play sessions when domain reload is disabled; start clean each play session.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        quitting = false;
        errorsLogged = 0;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
        FeelSfxPlayer.ResetStatics();
    }

    static void OnQuitting() => quitting = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBoot() => Get();
}
