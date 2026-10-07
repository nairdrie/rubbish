using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Trucks lose grip in juice puddles (F3, Docs/Multiplayer.md §14.3) and on wet roads (<see cref="Puddles.RainWetness"/>, F5):
/// each wheel standing in one has its sideways stiffness × (1 − 0.65·slip) and forward × (1 − 0.35·slip) for that physics step.
/// Runs after <see cref="TruckHandlingMods"/> (order 100) and only scales what's there: it puts back exactly what it changed, and
/// if someone else rewrote a curve meanwhile (drift, a new handling level) it leaves theirs alone. Physics only where this machine
/// simulates the truck. Added to every truck by <see cref="TellsRuntime"/>.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(110)]
public class TruckPuddleGrip : MonoBehaviour
{
    struct Wheel
    {
        public WheelCollider wc;
        public bool touched;
        public WheelFrictionCurve fwdBase, sideBase;   // what was there before we scaled it
        public float fwdSet, sideSet;                  // the stiffness we wrote (to notice someone else's write)
    }

    readonly List<Wheel> wheels = new List<Wheel>();
    Rigidbody body;
    float nextSplash, nextSkrrt;
    bool wasIn;

    /// <summary>0..1 how slippery it is under this truck's wheels right now (max over wheels).</summary>
    public float Slip { get; private set; }

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        foreach (var wc in GetComponentsInChildren<WheelCollider>(true)) wheels.Add(new Wheel { wc = wc });
    }

    void OnDisable() => RestoreAll();

    void FixedUpdate()
    {
        if (body == null || body.isKinematic) { RestoreAll(); Slip = 0f; return; }
        float max = 0f;
        for (int i = 0; i < wheels.Count; i++)
        {
            var w = wheels[i];
            if (w.wc == null) continue;
            Restore(ref w);
            float k = 0f;
            if (w.wc.isGrounded && w.wc.GetGroundHit(out var hit)) k = Puddles.SlipAt(hit.point);
            if (k > 0.03f)
            {
                w.fwdBase = w.wc.forwardFriction;
                w.sideBase = w.wc.sidewaysFriction;
                var f = w.fwdBase; f.stiffness *= 1f - 0.35f * k;
                var s = w.sideBase; s.stiffness *= 1f - 0.65f * k;
                w.wc.forwardFriction = f;
                w.wc.sidewaysFriction = s;
                w.fwdSet = f.stiffness;
                w.sideSet = s.stiffness;
                w.touched = true;
            }
            wheels[i] = w;
            if (k > max) max = k;
        }
        Slip = max;
        SplashFx(max);
    }

    void Restore(ref Wheel w)
    {
        if (!w.touched || w.wc == null) return;
        w.touched = false;
        // Put ours back only if nobody rewrote the curve since (TruckHandlingMods' drift / level change wins).
        if (Mathf.Approximately(w.wc.forwardFriction.stiffness, w.fwdSet)) w.wc.forwardFriction = w.fwdBase;
        if (Mathf.Approximately(w.wc.sidewaysFriction.stiffness, w.sideSet)) w.wc.sidewaysFriction = w.sideBase;
    }

    void RestoreAll()
    {
        for (int i = 0; i < wheels.Count; i++) { var w = wheels[i]; Restore(ref w); wheels[i] = w; }
    }

    void SplashFx(float k)
    {
        bool inPuddle = k > 0.5f && Puddles.Count > 0;
        float speed = body.linearVelocity.magnitude;
        if (inPuddle && speed > 3f && Time.time >= nextSplash && (!wasIn || Time.time - nextSplash > 1f))
        {
            nextSplash = Time.time + 0.6f;
            Vector3 p = transform.position + Vector3.up * 0.3f;
            Fx.Burst(p, new Color(0.42f, 0.46f, 0.14f), 14, Mathf.Min(7f, 2f + speed * 0.4f));
            Sfx.Play("splat", p, 0.5f, ShopModKit.Range(0.7f, 0.9f));
        }
        if (inPuddle && speed > 6f && Time.time >= nextSkrrt)
        {
            float side = Mathf.Abs(Vector3.Dot(body.linearVelocity, transform.right));
            if (side > 2f)
            {
                nextSkrrt = Time.time + 6f;
                GameHUD.FloatingText(transform.position + Vector3.up * 3.6f, "SKRRRT! (juice)", new Color(0.8f, 0.95f, 0.4f), 1.1f);
                Sfx.Play("tire_screech", transform.position, 0.5f, 1.2f);
            }
        }
        wasIn = inPuddle;
    }
}
