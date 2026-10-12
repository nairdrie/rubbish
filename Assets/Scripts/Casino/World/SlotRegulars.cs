using UnityEngine;

/// <summary>
/// The cosmetic regulars at two of the slot machines (1 and 3; Docs/Casino.md §8.4, §11): one person per machine standing at it, now and
/// then hauling the lever (the drums spin to random stops, like a real pull but nothing to do with the host), a small reaction, a rare
/// cheer. When a real player takes the machine the regular walks to an idle spot nearby; 60 s after the machine is free again they
/// come back. Built from <see cref="CasinoStaffer"/> (the pose <see cref="CasinoRegularPose.SlotStander"/>); purely local, never touches the
/// host state, never stands where the player stands.
/// </summary>
public sealed class SlotRegulars : CasinoStationPresenter
{
    public static readonly int[] Machines = { 1, 3 };
    /// <summary>A regular comes back this long after the machine was last freed.</summary>
    public const double ReturnAfterSeconds = 60.0;
    const float WalkSeconds = 1.5f;

    enum Mode { AtMachine, Leaving, Away, Returning }

    sealed class Regular
    {
        public int machine;
        public CasinoStaffer staffer;
        public Transform anchor;
        public Vector3 home, away;
        public Quaternion faceMachine;
        public Mode mode = Mode.AtMachine;
        public double modeAt, nextPull, spinAt = -1, reactAt = -1, nextBark, returnAt = -1;
        public int s0, s1, s2;
        public PuppetDice dice;
        public bool wasOccupied;
        public Vector3 walkFrom;
    }

    readonly Regular[] regulars = new Regular[Machines.Length];

    /// <summary>The mode of a regular, for tests and the debug menu.</summary>
    public string ModeOf(int machine)
    {
        for (int i = 0; i < regulars.Length; i++) if (regulars[i] != null && regulars[i].machine == machine) return regulars[i].mode.ToString();
        return "none";
    }

    public bool PresentAt(int machine)
    {
        for (int i = 0; i < regulars.Length; i++) if (regulars[i] != null && regulars[i].machine == machine) return regulars[i].mode == Mode.AtMachine;
        return false;
    }

    public override void Build(CasinoPresenters host)
    {
        this.host = host;
        for (int i = 0; i < Machines.Length; i++)
        {
            int m = Machines[i];
            var st = CasinoStation.Find(CasinoStationKind.Slot, m);
            var spot = st != null ? st.Spot(0) : null;
            if (spot == null || spot.stand == null) { host.WarnOnce("reg" + m, "no stand spot at slot machine " + m + ": no regular there"); continue; }
            var r = new Regular { machine = m, dice = new PuppetDice((ulong)(0x51071E50UL + (ulong)m * 7919UL + (ulong)System.Environment.TickCount)) };
            Transform stand = spot.stand;
            // beside the player's stand (toward the lever side), a little closer to the cabinet: never ON the stand spot
            r.home = stand.position + stand.right * 0.56f + stand.forward * 0.28f;
            r.away = stand.position + stand.right * 0.8f - stand.forward * 1.6f; // out of the row, clear of the next machine's stand
            r.faceMachine = stand.rotation;
            var go = new GameObject("RegularAnchor " + m) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(host.PropRoot, false);
            go.transform.SetPositionAndRotation(r.home, r.faceMachine);
            r.anchor = go.transform;
            var post = new CasinoStaff.Post { label = "Slot regular " + m, role = CasinoRole.Regular, pose = CasinoRegularPose.SlotStander, seated = false, seed = 40 + m * 3, anchor = r.anchor };
            try { r.staffer = new CasinoStaffer(null, post); }
            catch (System.Exception e) { Debug.LogException(e); host.WarnOnce("regbuild" + m, "regular at machine " + m + " failed to build: " + e.Message); continue; }
            r.nextPull = CasinoPresenters.Now + r.dice.Range(3f, 9f);
            r.nextBark = CasinoPresenters.Now + r.dice.Range(25f, 60f);
            regulars[i] = r;
        }
    }

    /// <summary>A real spin on a machine (the regular there, if any, is already out of the way; nothing to do).</summary>
    public void RealSpin(int machine) { }

    /// <summary>A player took or left a machine (the per-frame check also catches the state at a late join).</summary>
    public void Occupancy(int machine, bool occupied) { }

    public override void Tick(double now)
    {
        float dt = Mathf.Min(Time.deltaTime, 0.1f);
        Transform near = null;
        var me = CasinoWorld.Me;
        if (me != null) near = me.transform;
        for (int i = 0; i < regulars.Length; i++)
        {
            var r = regulars[i];
            if (r == null || r.staffer == null || !r.staffer.Exists) continue;
            var sp = host.Slots[r.machine];
            bool occupied = sp != null && sp.OccupantKey >= 0;
            Step(r, sp, occupied, now);
            // heads turn to a player who comes near (the staffer ignores anyone further than 5.5 m)
            Transform watch = near != null && (near.position - r.anchor.position).sqrMagnitude < 16f ? near : null;
            r.staffer.Tick(dt, Time.time, watch, false);
        }
    }

    void Step(Regular r, SlotPresenter sp, bool occupied, double now)
    {
        if (occupied != r.wasOccupied)
        {
            r.wasOccupied = occupied;
            if (occupied)
            {
                // a real player: step aside
                if (r.mode == Mode.AtMachine || r.mode == Mode.Returning) BeginWalk(r, Mode.Leaving, now);
                r.returnAt = -1;
            }
            else r.returnAt = now + ReturnAfterSeconds;
        }
        switch (r.mode)
        {
            case Mode.AtMachine:
                if (occupied) { BeginWalk(r, Mode.Leaving, now); break; }
                TickAtMachine(r, sp, now);
                break;
            case Mode.Leaving:
            {
                float u = Walk(r, r.away, now);
                if (u >= 1f) { r.mode = Mode.Away; r.modeAt = now; r.staffer.Walking = false; r.anchor.rotation = r.faceMachine; }
                break;
            }
            case Mode.Away:
                // the machine has been free for a minute: back to it
                if (!occupied && r.returnAt >= 0 && now >= r.returnAt && (sp == null || !sp.RealSpinning)) BeginWalk(r, Mode.Returning, now);
                break;
            case Mode.Returning:
            {
                if (occupied) { BeginWalk(r, Mode.Leaving, now); break; }
                float u = Walk(r, r.home, now);
                if (u >= 1f)
                {
                    r.mode = Mode.AtMachine; r.modeAt = now; r.staffer.Walking = false; r.anchor.rotation = r.faceMachine;
                    r.nextPull = now + r.dice.Range(4f, 10f);
                }
                break;
            }
        }
    }

    void BeginWalk(Regular r, Mode mode, double now)
    {
        // start from wherever the person is now (a walk can be interrupted halfway)
        r.mode = mode; r.modeAt = now; r.staffer.Walking = true;
        r.spinAt = -1; r.reactAt = -1;
        r.walkFrom = r.anchor.position;
    }

    float Walk(Regular r, Vector3 to, double now)
    {
        Vector3 start = r.walkFrom;
        float u = Mathf.Clamp01((float)(now - r.modeAt) / WalkSeconds);
        float e = u * u * (3f - 2f * u);
        Vector3 p = Vector3.Lerp(start, to, e);
        r.anchor.position = p;
        Vector3 d = to - start; d.y = 0f;
        if (u < 1f && d.sqrMagnitude > 1e-4f) r.anchor.rotation = Quaternion.Slerp(r.anchor.rotation, Quaternion.LookRotation(d), 0.2f);
        return u;
    }

    void TickAtMachine(Regular r, SlotPresenter sp, double now)
    {
        if (sp == null) return;
        if (r.spinAt < 0 && now >= r.nextPull && !sp.Spinning)
        {
            // haul the lever, then the drums go a moment later
            r.staffer.Pull();
            r.spinAt = now + 0.45;
            r.s0 = r.dice.Range(0, SlotRules.ReelLength(r.machine, 0));
            r.s1 = r.dice.Range(0, SlotRules.ReelLength(r.machine, 1));
            r.s2 = r.dice.Range(0, SlotRules.ReelLength(r.machine, 2));
        }
        if (r.spinAt >= 0 && now >= r.spinAt)
        {
            r.spinAt = -1;
            sp.SpinCosmetic(r.s0, r.s1, r.s2);
            r.reactAt = now + 3.6; // after the last drum has landed
        }
        if (r.reactAt >= 0 && now >= r.reactAt)
        {
            r.reactAt = -1;
            var res = SlotRules.Resolve(r.machine, r.s0, r.s1, r.s2);
            var machine = SlotRules.Machine(r.machine);
            double mult = res.rule >= 0 ? machine.pays[res.rule].multiple : 0;
            if (mult >= 10) { r.staffer.Cheer(); r.staffer.Say("Yes! YES!"); }                 // rare: only a real prize
            else if (res.rule >= 0 && r.dice.Value < 0.5f) { r.staffer.Cheer(); }
            else if (r.dice.Value < 0.4f) r.staffer.Shrug();
            r.nextPull = now + r.dice.Range(9f, 20f);
            if (now >= r.nextBark && r.dice.Value < 0.5f)
            {
                r.nextBark = now + r.dice.Range(50f, 110f);
                r.staffer.Say(res.rule >= 0 ? "Told you." : "It's due. It's DUE.");
            }
        }
    }

    public override void Leave()
    {
        // the player left the room: nothing to tidy (the rigs stay, the interior is off)
    }

    public override void Teardown()
    {
        for (int i = 0; i < regulars.Length; i++)
        {
            var r = regulars[i];
            if (r == null) continue;
            if (r.staffer != null) r.staffer.Destroy();
            if (r.anchor != null) Object.Destroy(r.anchor.gameObject);
            regulars[i] = null;
        }
    }
}
