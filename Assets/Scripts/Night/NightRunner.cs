using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// F4's per-machine housekeeping (one DontDestroyOnLoad object, no scene setup): headlamps on every player and their light
/// budget, a roof spotlight on every truck, the Rabies Shot first-aid box on every shop laptop, rabid looks on other
/// players, the trap key (T), solo traps across scene loads, and the Raccoon King's boss bar.
/// </summary>
[DefaultExecutionOrder(-30)]
public class NightRunner : MonoBehaviour
{
    public static NightRunner Instance { get; private set; }

    float nextScan, nextBudget, nextLaptopScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null) return;
        var go = new GameObject("[Night]");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<NightRunner>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        nextScan = 0f;
        nextLaptopScan = Time.unscaledTime + 1f;
        NightTraps.SoloRebuild();
    }

    void Update()
    {
        float now = Time.unscaledTime;
        if (now >= nextScan)
        {
            nextScan = now + 1f;
            foreach (var pc in NightPlayers.All)
            {
                if (pc == null) continue;
                if (pc.GetComponent<PlayerHeadlamp>() == null) pc.gameObject.AddComponent<PlayerHeadlamp>();
                if (!NightPlayers.IsLocal(pc) && pc.GetComponent<RabiesLook>() == null) pc.gameObject.AddComponent<RabiesLook>();
            }
            foreach (var t in TruckControllerV2.All) RoofSpotlight.Ensure(t);
        }
        if (now >= nextLaptopScan)
        {
            nextLaptopScan = now + 5f;
            foreach (var l in FindObjectsByType<LaptopController>(FindObjectsSortMode.None)) RabiesShotKiosk.Ensure(l);
        }
        if (now >= nextBudget)
        {
            nextBudget = now + 0.5f;
            var cam = Camera.main;
            PlayerHeadlamp.Budget(cam != null ? cam.transform.position : Vector3.zero);
        }
        NightTraps.LocalTick();
    }

    // ---------------------------------------------------------------- the King's boss bar

    void OnGUI()
    {
        if (!RaccoonKingEvent.HudState(out int hp, out int max, out Vector3 pos)) return;
        var me = NightPlayers.LocalPlayer;
        if (me == null || CritterWorld.Flat(me.transform.position - pos).magnitude > 90f) return;
        float w = Mathf.Min(560f, Screen.width - 40f);
        var box = new Rect((Screen.width - w) * 0.5f, 18f, w, 56f);
        Fill(box, new Color(0f, 0f, 0f, 0.55f));
        Label(new Rect(box.x, box.y + 2f, w, 24f), "THE RACCOON KING  —  throw stuff at him!", 18, new Color(1f, 0.82f, 0.25f));
        var bar = new Rect(box.x + 16f, box.y + 30f, w - 32f, 16f);
        Fill(bar, new Color(0.25f, 0.2f, 0.2f));
        Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(max > 0 ? (float)hp / max : 0f), bar.height), new Color(0.9f, 0.25f, 0.3f));
        Label(bar, $"{hp} / {max}", 12, Color.white);
    }

    static void Fill(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    static GUIStyle style;

    static void Label(Rect r, string text, int size, Color c)
    {
        style ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.fontSize = size;
        style.normal.textColor = c;
        GUI.Label(r, text, style);
    }
}

/// <summary>
/// Another player's rabies, on this machine (F4 "rabies per player": everyone sees everyone's foam). Reads the host's list
/// (<see cref="NightNet.InfectedAt"/>): foaming at the mouth once symptoms start, and goofy "*hic*" bubbles (their own machine
/// does the actual hiccup hop, which their movement carries here).
/// </summary>
[DisallowMultipleComponent]
public class RabiesLook : MonoBehaviour
{
    PlayerController pc;
    FoamBubbles foam;
    float nextHic, nextCheck;
    bool rabid;

    void Awake() => pc = GetComponent<PlayerController>();

    void OnDisable()
    {
        if (foam != null) { Destroy(foam); foam = null; }
    }

    void Update()
    {
        if (pc == null || NightPlayers.IsLocal(pc)) { enabled = false; return; }
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 0.5f;
            rabid = NightNet.Symptomatic(NightPlayers.ClientOf(pc));
        }
        bool show = rabid && !pc.IsDriving && !pc.IsPlayingChess;
        if (!show)
        {
            if (foam != null) { Destroy(foam); foam = null; }
            return;
        }
        if (foam == null)
        {
            foam = gameObject.AddComponent<FoamBubbles>();
            foam.size = 0.045f;
            var puppet = GetComponent<ChuckPuppet>();
            Transform mouth = puppet != null ? puppet.MouthPoint : null;
            foam.follow = () => mouth != null ? mouth.position + transform.forward * 0.05f : transform.position + Vector3.up * 1.4f + transform.forward * 0.2f;
            foam.forward = () => transform.forward;
            foam.rate = 5f;
            nextHic = Time.time + NightDice.Cosmetic.Range(3f, 8f);
        }
        if (Time.time >= nextHic)
        {
            nextHic = Time.time + NightDice.Cosmetic.Range(6f, 14f);
            Vector3 head = transform.position + Vector3.up * 1.9f;
            GameHUD.FloatingText(head, "*hic*", new Color(0.6f, 1f, 0.45f), 0.9f);
        }
    }
}
