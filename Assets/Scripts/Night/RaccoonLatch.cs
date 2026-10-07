using System.Collections.Generic;
using StarterAssets;
using UnityEngine;

/// <summary>Host-side lookups for latched raccoons (F4).</summary>
public static class NightLatch
{
    /// <summary>The raccoon latched onto <paramref name="client"/> (host), or null.</summary>
    public static Raccoon On(ulong client)
    {
        var all = Critter.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i] is Raccoon r && !r.NetPuppet && !r.Despawning && r.LatchVictim == client && r.NetState == (byte)Raccoon.State.Latched) return r;
        return null;
    }

    public static bool IsLatched(ulong client) => On(client) != null;

    /// <summary>Host: the victim mashed free.</summary>
    public static void HostShake(ulong client)
    {
        var r = On(client);
        if (r != null) r.EndLatch("mash", client, false);
    }
}

/// <summary>
/// A raccoon on your face (F4 Latch, Docs/Multiplayer.md §14.4). Added at runtime to the local player by
/// <see cref="CritterSpawner"/>. While latched (the host says so, ≤4 s): your movement is scrambled (left is right, forward
/// wobbles), you move 40% slower, and the screen says MASH SPACE. Mashing fills a bar; full = shaken off. A friend can bonk it
/// off (E near you, a shove, a throw). You always have control (scrambled, never frozen), so the stun-chain rule holds: mash
/// progress is yours, and after it lets go nothing critter-shaped can touch you for 5 s.
/// <para>Also measures the longest stretch this player had no control at all (knockbacks, knock-outs) for the bot gate
/// (<c>night.helpless</c> audit lines).</para>
/// </summary>
[DefaultExecutionOrder(-79)]
[DisallowMultipleComponent]
public class RaccoonLatch : MonoBehaviour
{
    public const int MashToFree = 6;
    public const float Slow = 0.6f;

    public static RaccoonLatch Local { get; private set; }

    /// <summary>Latched right now (this machine's player).</summary>
    public bool Latched { get; private set; }
    /// <summary>0..1 mash progress.</summary>
    public float Progress { get; private set; }
    float latchedAt, sentShakeAt = -9f;
    Raccoon critter;

    PlayerController pc;
    StarterAssetsInputs inputs;
    bool scrambleActive;
    Vector2 scrambleBase, scrambleWritten;

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        inputs = GetComponent<StarterAssetsInputs>();
    }

    bool IsMine => pc != null && NightPlayers.IsLocal(pc);

    /// <summary>The host latched / unlatched a raccoon onto this machine's player.</summary>
    public static void OnLatch(bool on, Raccoon r, bool infect)
    {
        var me = Local;
        if (me == null)
        {
            var p = NightPlayers.LocalPlayer;
            if (p != null) me = p.GetComponent<RaccoonLatch>() ?? p.gameObject.AddComponent<RaccoonLatch>();
        }
        if (me == null) return;
        if (on) me.Begin(r);
        else me.End(infect);
    }

    void Begin(Raccoon r)
    {
        Latched = true;
        Progress = 0f;
        latchedAt = Time.time;
        critter = r;
        Juice.Shake(0.25f, 0.3f);
        Sfx.Play("raccoon_scream", transform.position + Vector3.up * 1.7f, 1f, 1.1f);
        GameHUD.Toast("A RACCOON IS ON YOUR FACE! Mash SPACE to shake it off (or get a friend to bonk it).", ToastStyle.Bad, 2.5f);
        NightAudit.Event("latched", new Dictionary<string, object> { { "on", true } });
    }

    void End(bool infect)
    {
        if (!Latched) return;
        Latched = false;
        RemoveScramble();
        float held = Time.time - latchedAt;
        critter = null;
        if (infect && !GameSession.HasRabies) GameSession.Infect(GameClock.TotalHours);
        NightAudit.Event("latched", new Dictionary<string, object> { { "on", false }, { "held", System.Math.Round(held, 2) }, { "infect", infect } });
    }

    /// <summary>One mash (Space; bots call it directly).</summary>
    public void Mash()
    {
        if (!Latched) return;
        Progress = Mathf.Min(1f, Progress + 1f / MashToFree);
        Juice.Shake(0.06f, 0.08f);
        Sfx.Play("squeak", transform.position + Vector3.up * 1.6f, 0.5f, 0.8f + Progress * 0.6f);
        if (Progress >= 1f && Time.time - sentShakeAt > 0.5f)
        {
            sentShakeAt = Time.time;
            NightNet.RequestLatchShake();
        }
    }

    void Update()
    {
        if (!IsMine) return;
        Local = this;
        HelplessMeter();
        if (!Latched) { RemoveScramble(); return; }
        if (Time.time - latchedAt > Raccoon.LatchSeconds + 2.5f) { End(false); return; } // the host's word got lost: never stuck
        Progress = Mathf.Max(0f, Progress - Time.deltaTime * 0.12f);
        if (!PlayerController.IsUIBlocking && GameControls.Down(KeyCode.Space, GameControls.Action.Jump)) Mash();
        ApplyScramble();
    }

    void LateUpdate() => RemoveScramble();

    void ApplyScramble()
    {
        RemoveScramble();
        if (inputs == null || pc == null || pc.IsDriving || PlayerController.IsUIBlocking) return;
        Vector2 m = inputs.move;
        if (m.sqrMagnitude < 0.0001f) return;
        // Left is right, forward wobbles: the raccoon is steering now.
        float t = Time.time - latchedAt;
        float ang = 140f + Mathf.Sin(t * 5f) * 50f;
        float rad = ang * Mathf.Deg2Rad;
        Vector2 s = new Vector2(m.x * Mathf.Cos(rad) - m.y * Mathf.Sin(rad), m.x * Mathf.Sin(rad) + m.y * Mathf.Cos(rad)) * Slow;
        scrambleBase = m;
        scrambleWritten = s;
        inputs.move = s;
        scrambleActive = true;
    }

    void RemoveScramble()
    {
        if (!scrambleActive) return;
        scrambleActive = false;
        if (inputs != null && inputs.move == scrambleWritten) inputs.move = scrambleBase;
    }

    // ---------------------------------------------------------------- the stun-chain meter (bots / audit)

    float helplessSince = -1f, helplessMax, nextReport;

    /// <summary>Longest stretch with no control at all this session (s).</summary>
    public float HelplessMax => helplessMax;

    void HelplessMeter()
    {
        bool helpless = false;
        var kb = GetComponent<CritterKnockback>();
        if (kb != null && kb.IsKnocking) helpless = true;
        var net = HandsNet.Instance;
        if (net != null && net.Active)
        {
            var st = net.BodyOf(NightPlayers.ClientOf(pc)).State;
            // Flopped / carried / riding are chosen (consent); stunned, knocked out and flung are not.
            if (st == BodyState.Stunned || st == BodyState.KnockedOut || st == BodyState.Thrown) helpless = true;
        }
        if (helpless) { if (helplessSince < 0f) helplessSince = Time.time; }
        else if (helplessSince >= 0f)
        {
            float span = Time.time - helplessSince;
            helplessSince = -1f;
            if (span > helplessMax) helplessMax = span;
            if (span > 0.05f) NightAudit.Event("helpless", new Dictionary<string, object> { { "span", System.Math.Round(span, 3) } });
        }
    }

    void OnDisable()
    {
        RemoveScramble();
        if (Local == this) Local = null;
    }

    void OnGUI()
    {
        if (!Latched || !IsMine) return;
        float w = Mathf.Min(460f, Screen.width - 40f);
        var box = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.7f, w, 74f);
        Fill(box, new Color(0f, 0f, 0f, 0.6f));
        bool wobble = Mathf.Repeat(Time.time * 6f, 1f) < 0.5f;
        Label(new Rect(box.x, box.y + 4f, w, 30f), wobble ? "MASH [SPACE]!!" : "MASH  [SPACE] !!", 24, new Color(1f, 0.4f, 0.3f));
        var bar = new Rect(box.x + 20f, box.y + 42f, w - 40f, 18f);
        Fill(bar, new Color(0.25f, 0.25f, 0.28f));
        Fill(new Rect(bar.x, bar.y, bar.width * Progress, bar.height), new Color(0.45f, 1f, 0.4f));
        float left = Mathf.Max(0f, Raccoon.LatchSeconds - (Time.time - latchedAt));
        Label(new Rect(box.x, box.yMax - 2f, w, 18f), $"raccoon on your face ({left:0.0}s)", 12, Color.white);
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Local = null;
}
