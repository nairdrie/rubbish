using System.Collections;
using UnityEngine;

/// <summary>
/// A drunk player as seen from outside (Docs/Casino.md §5): a procedural sway and stagger lean on the puppet's Skeleton
/// child, "*hic*" text over other players, and dizzy stars round the head when Wasted. On every player puppet, this
/// machine's own included (the third-person camera shows you staggering). Others' levels come from the host's list
/// (<see cref="DrunkNet.LevelOf"/>); the local player's from <see cref="DrunkStatus.Level"/>. Their stumbles and hiccup
/// hops are real movement and arrive with their position.
///
/// The lean is layered on, never owned: it is added in LateUpdate (late, after whoever poses the skeleton: flops, carries,
/// the slip, the ride) and taken off again once the frame has rendered, so anything that reads or caches the skeleton's
/// rest pose between frames sees it clean.
/// </summary>
[DefaultExecutionOrder(9000)]
[DisallowMultipleComponent]
public class DrunkLook : MonoBehaviour
{
    PlayerController pc;
    PlayerBody body;
    Transform skeleton;
    float level, shown, seed, nextCheck, nextHic = -1f, nextStars;
    bool applied;
    Quaternion baseRot, setRot;
    Coroutine undoLoop;
    static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();
    static readonly Color TextColor = new Color(1f, 0.82f, 0.42f);

    void Awake()
    {
        pc = GetComponent<PlayerController>();
        body = GetComponent<PlayerBody>();
        skeleton = transform.Find("Skeleton");
        seed = NightDice.Cosmetic.Range(0f, 100f);
    }

    void OnEnable() => undoLoop = StartCoroutine(UndoAfterRender());

    void OnDisable()
    {
        if (undoLoop != null) { StopCoroutine(undoLoop); undoLoop = null; }
        Undo();
    }

    IEnumerator UndoAfterRender()
    {
        while (true)
        {
            yield return EndOfFrame;
            Undo();
        }
    }

    void Update()
    {
        if (pc == null) { enabled = false; return; }
        bool local = NightPlayers.IsLocal(pc);
        if (Time.unscaledTime >= nextCheck)
        {
            nextCheck = Time.unscaledTime + 0.25f;
            level = local ? DrunkStatus.Level : DrunkNet.LevelOf(NightPlayers.ClientOf(pc));
        }
        shown = Mathf.MoveTowards(shown, DrunkRules.Walk01(level), Time.deltaTime * 0.8f);

        var stage = DrunkRules.Stage(level);
        if (stage == DrunkStage.Sober || pc.IsDriving || pc.IsPlayingChess) { nextHic = -1f; return; }

        // The local player's own hiccups (text, hop, squeak) come from DrunkStatus; here only for the others.
        if (!local)
        {
            if (nextHic < 0f) nextHic = Time.time + NightDice.Cosmetic.Range(4f, 9f);
            else if (Time.time >= nextHic)
            {
                nextHic = Time.time + NightDice.Cosmetic.Range(7f, 16f);
                GameHUD.FloatingText(transform.position + Vector3.up * 1.9f, "*hic*", TextColor, 0.9f);
            }
        }
        if (stage == DrunkStage.Wasted && Time.time >= nextStars)
        {
            nextStars = Time.time + 2f;
            DizzyStars.Attach(transform, 3f, 0.3f, 1.95f); // extends the stars already there (a bonk's), never doubles them
        }
    }

    bool Upright => body == null || HandsNet.Instance == null || body.State == BodyState.Normal;

    void LateUpdate()
    {
        Undo(); // (the end-of-frame pass does not run while nothing renders)
        if (skeleton == null || pc == null || shown <= 0.01f || pc.IsDriving || pc.IsPlayingChess || !Upright) return;

        float t = Time.time + seed;
        // A slow side-to-side sway and a nod, plus now and then a longer lurch one way that he has to catch.
        float lurch = Mathf.Pow(Mathf.Clamp01(Mathf.PerlinNoise(t * 0.33f, seed) * 1.6f - 0.6f), 2f);
        float lurchSide = Mathf.Sin(t * 0.21f + seed) >= 0f ? 1f : -1f;
        float roll = (Mathf.Sin(t * 1.25f) * 5.5f + Mathf.Sin(t * 0.57f + 1f) * 4f + lurch * lurchSide * 11f) * shown;
        float pitch = (Mathf.Sin(t * 0.9f + 2f) * 3.5f + Mathf.Sin(t * 2.1f) * 1.2f + lurch * 5f) * shown;

        baseRot = skeleton.localRotation;
        setRot = Quaternion.Euler(pitch, 0f, roll) * baseRot;
        skeleton.localRotation = setRot;
        applied = true;
    }

    void Undo()
    {
        if (!applied) return;
        applied = false;
        // Only put it back if nobody re-posed the skeleton since we leaned it.
        if (skeleton != null && skeleton.localRotation == setRot) skeleton.localRotation = baseRot;
    }
}
