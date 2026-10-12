using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One slot machine (<c>slot0</c>..<c>slot4</c>) as every player in the room sees it (Docs/Casino.md §11), drawn ONLY from the replicated
/// <see cref="SlotEventInfo"/> (the real stops, the line, who, the host time it starts) and the occupancy events: the three drums spin up and
/// stop one after the other on the true strip positions, the lever is pulled, a win lights the glow, throws coins at the tray and floats
/// "+$X" over the machine (a big win adds the celebration lights, kept under 2.5 Hz and off under Reduce Flashing), a beacon and a name
/// tag show who is playing, the pay plate lists the real pay table and "RETURNS xx.x% over the long run", and a machine nobody uses
/// breathes a gentle glow. The same machinery plays the NPC regulars' cosmetic spins (<see cref="SpinCosmetic"/>).
/// </summary>
public sealed class SlotPresenter : CasinoStationPresenter
{
    readonly CasinoStation st;
    readonly int machine;

    // reels
    readonly int[] restStop = new int[3];
    readonly int[] stops = new int[3];
    readonly float[] stopTime = new float[3];
    readonly bool[] stopped = new bool[3], skinned = new bool[3];
    bool spinning, cosmetic, celebrated;
    double startLocal;
    SlotEventInfo spin;
    int lastLeverStep = -1;

    // glow / lamp / tag
    Renderer[] glow = new Renderer[0];
    Color[] glowBase = new Color[0];
    MaterialPropertyBlock block;
    float glowShown = -1f, celebrateUntil, celebrateStart;
    bool celebrateBig;
    int occupantKey = -1;
    string occupantName = "";
    CasinoLabel tag;
    GameObject beacon;
    Material beaconOn, beaconOff;
    MeshRenderer beaconRenderer;
    GameObject beaconHalo;
    static Material haloMaterial;
    static Mesh haloMesh;
    static Texture2D haloTexture;
    /// <summary>The IN USE lamp: a lens twice the old size, a bright emission and a soft red halo that always faces the viewer (readable from the far end of the room).</summary>
    const float BeaconScale = 2.4f, BeaconHaloSize = 0.62f;
    /// <summary>Height (machine local) of the "X is playing" tag: above the toppers and the neon sign, under the ceiling coffers.</summary>
    const float TagHeight = 3.42f;
    float trayLightBase = -1f;

    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

    public SlotPresenter(CasinoStation station, int index) { st = station; machine = index; }

    public CasinoStation Station => st;
    public int Machine => machine;
    public int OccupantKey => occupantKey;
    /// <summary>A real player's spin is on the reels (a regular's cosmetic spin never blocks one).</summary>
    public bool RealSpinning => spinning && !cosmetic;
    public bool Spinning => spinning;

    // ------------------------------------------------------------------ build

    public override void Build(CasinoPresenters host)
    {
        this.host = host;
        BuildPlate();
        for (int r = 0; r < 3; r++)
        {
            int len = SlotRules.ReelLength(machine, r);
            restStop[r] = (machine * 7 + r * 5) % len; // where the last player "left it": a fixed, honest stop
            stops[r] = restStop[r];
            if (st.reels != null && r < st.reels.Length && st.reels[r] != null)
            {
                CasinoPaint.SkinReel(st.reels[r], machine, r, restStop[r]);
                st.reels[r].localRotation = Quaternion.Euler(CasinoReelMath.RestAngleDeg(restStop[r]), 0f, 0f);
            }
        }
        if (st.reels == null || st.reels.Length < 3) host.WarnOnce("slot.reels" + machine, "slot machine " + machine + " has no three reel drums");
        // the glow renderers and their normal emission
        if (st.glow != null)
        {
            glow = st.glow;
            glowBase = new Color[glow.Length];
            for (int i = 0; i < glow.Length; i++)
            {
                var m = glow[i] != null ? glow[i].sharedMaterial : null;
                glowBase[i] = m != null && m.HasProperty(EmissionId) ? m.GetColor(EmissionId) : Color.black;
            }
        }
        block = new MaterialPropertyBlock();
        if (st.coinTrayLight != null) trayLightBase = st.coinTrayLight.intensity;
        BuildBeacon();
        SetGlow(IdleGlow(CasinoPresenters.Now), true);
    }

    void BuildPlate()
    {
        var plate = st.payPlate;
        if (plate == null) { host.WarnOnce("slot.plate" + machine, "slot machine " + machine + " has no pay plate"); return; }
        var m = SlotRules.Machine(machine);
        plate.SetTitle("PAY TABLE");
        for (int i = 0; i < CasinoPayPlate.Lines; i++)
        {
            if (i < m.pays.Length)
            {
                string combo = SlotPlateText.Combo(m.pays[i].name);
                if (combo.Length > 15) combo = "<size=80%>" + combo + "</size>";
                plate.SetLine(i, combo, SlotPlateText.Payout(machine, i));
            }
            else plate.SetLine(i, "", "");
        }
        plate.SetReturns(SlotPlateText.Returns(machine));
        plate.SetStake(SlotPlateText.Stake(machine));
    }

    static Mesh HaloMesh()
    {
        if (haloMesh != null) return haloMesh;
        haloMesh = new Mesh { name = "CasinoBeaconHalo", hideFlags = HideFlags.DontSave };
        haloMesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
        haloMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        // the viewer looks along +Z of the halo (it is turned like text), so wind it to show its -Z face
        haloMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        haloMesh.RecalculateBounds();
        return haloMesh;
    }

    static Material HaloMaterial()
    {
        if (haloMaterial != null) return haloMaterial;
        if (haloTexture == null)
        {
            const int n = 48;
            haloTexture = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "CasinoBeaconHalo", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - (n - 1) * 0.5f) * (x - (n - 1) * 0.5f) + (y - (n - 1) * 0.5f) * (y - (n - 1) * 0.5f)) / ((n - 1) * 0.5f);
                    float a = Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
                    haloTexture.SetPixel(x, y, new Color(1f, 0.15f, 0.08f, a * 0.85f));
                }
            haloTexture.Apply(false, true);
        }
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        haloMaterial = new Material(shader) { name = "CasinoBeaconHalo", hideFlags = HideFlags.DontSave, mainTexture = haloTexture, color = Color.white };
        haloMaterial.renderQueue = 3010;
        return haloMaterial;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetHalo() { haloMaterial = null; haloMesh = null; haloTexture = null; }

    void BuildBeacon()
    {
        beacon = new GameObject("InUseBeacon") { hideFlags = HideFlags.DontSave };
        beacon.transform.SetParent(host.PropRoot, false);
        beacon.transform.position = st.transform.TransformPoint(new Vector3(-0.33f, 2.385f, 0.07f));
        var body = new GameObject("Lens") { hideFlags = HideFlags.DontSave };
        body.transform.SetParent(beacon.transform, false);
        body.AddComponent<MeshFilter>().sharedMesh = CasinoPropKit.Lathe("CasinoBeacon", new[]
        {
            new Vector2(0.001f, 0f), new Vector2(0.04f, 0f), new Vector2(0.04f, 0.015f), new Vector2(0.032f, 0.045f), new Vector2(0.016f, 0.068f), new Vector2(0.001f, 0.075f),
        }, 14);
        body.transform.localScale = Vector3.one * BeaconScale;
        beaconRenderer = body.AddComponent<MeshRenderer>();
        beaconOn = CasinoPropKit.Glow(new Color(1f, 0.08f, 0.04f), 4.5f);
        // the halo: a soft additive-looking red disc, turned to the camera that renders it, only while the machine is in use
        beaconHalo = new GameObject("Halo") { hideFlags = HideFlags.DontSave };
        beaconHalo.transform.SetParent(beacon.transform, false);
        beaconHalo.transform.localPosition = new Vector3(0f, 0.075f * BeaconScale * 0.5f, 0f);
        beaconHalo.AddComponent<MeshFilter>().sharedMesh = HaloMesh();
        var hr = beaconHalo.AddComponent<MeshRenderer>();
        hr.sharedMaterial = HaloMaterial();
        hr.shadowCastingMode = ShadowCastingMode.Off; hr.receiveShadows = false;
        beaconHalo.transform.localScale = Vector3.one * BeaconHaloSize;
        beaconHalo.SetActive(false);
        beaconOff = CasinoPropKit.Solid(new Color(0.25f, 0.05f, 0.04f), 0.7f);
        beaconRenderer.sharedMaterial = beaconOff;
        beaconRenderer.shadowCastingMode = ShadowCastingMode.Off; beaconRenderer.receiveShadows = false;
        beacon.transform.rotation = st.transform.rotation;
    }

    // ------------------------------------------------------------------ the occupant

    /// <summary>Who sits at the machine (-1 = nobody). Raises the beacon and the name tag; the regulars react through <see cref="CasinoPresenters"/>.</summary>
    public void SetOccupant(int key, string name)
    {
        if (host == null) return;
        occupantKey = key;
        occupantName = name ?? "";
        bool used = key >= 0;
        if (beaconRenderer != null) beaconRenderer.sharedMaterial = used ? beaconOn : beaconOff;
        if (beaconHalo != null)
        {
            beaconHalo.SetActive(used);
            if (used) CasinoFacing.Add(beaconHalo.transform, false); else CasinoFacing.Remove(beaconHalo.transform);
        }
        var spot = st.Spot(0);
        double now = CasinoPresenters.Now;
        if (!used || spot == null)
        {
            if (tag != null) { host.Labels.Hide(tag, now); tag = null; }
            return;
        }
        string text = string.IsNullOrEmpty(occupantName) ? "Somebody is playing" : occupantName + " is playing";
        // on the cabinet, above everything on it: clear of the topper (up to 2.8 m), the marquee and the red neon SLOTS sign (up to 3.3 m) on the wall behind
        Vector3 at = st.transform.TransformPoint(new Vector3(0f, TagHeight, 0.12f));
        if (tag != null && tag.Shown) { tag.SetText(text); tag.SetPosition(at); }
        else tag = host.Labels.Show(text, at, new Color(1f, 0.9f, 0.62f), 1.1f, now, 0f, 0f, 0.6f, true);
    }

    // ------------------------------------------------------------------ a spin

    /// <summary>A real pull anywhere in the room: the stops are the host's. Starts at the event's host time.</summary>
    public void Spin(SlotEventInfo e)
    {
        double start = CasinoClient.ToLocalTime(e.start);
        Begin(e.stop0, e.stop1, e.stop2, start, false, e);
    }

    /// <summary>A regular's cosmetic pull (random stops chosen by the caller, nothing to do with the host).</summary>
    public void SpinCosmetic(int stop0, int stop1, int stop2)
    {
        if (RealSpinning || OccupantKey >= 0) return;
        Begin(stop0, stop1, stop2, CasinoPresenters.Now + 0.15, true, default);
    }

    void Begin(int s0, int s1, int s2, double start, bool isCosmetic, SlotEventInfo e)
    {
        if (host == null) return;
        // keep the previous spin's resting stops if it was cut short
        if (spinning) for (int r = 0; r < 3; r++) if (stopped[r]) restStop[r] = stops[r];
        spin = e; cosmetic = isCosmetic; celebrated = false; spinning = true; startLocal = start; lastLeverStep = -1;
        stops[0] = s0; stops[1] = s1; stops[2] = s2;
        for (int r = 0; r < 3; r++) { stops[r] = Mathf.Clamp(stops[r], 0, SlotRules.ReelLength(machine, r) - 1); stopped[r] = false; skinned[r] = false; }
        bool tease = SlotRules.SymbolAt(machine, 0, stops[0]) == SlotRules.TopSymbol(machine) && SlotRules.SymbolAt(machine, 1, stops[1]) == SlotRules.TopSymbol(machine);
        for (int r = 0; r < 3; r++) stopTime[r] = CasinoReelMath.StopTime(r, tease);
        double now = CasinoPresenters.Now;
        if (now - start > stopTime[2] + 1.0)
        {
            // an old spin (the event took a long time to arrive): the drums just show where it ended
            for (int r = 0; r < 3; r++) { FinishReel(r, true); }
            spinning = false;
            return;
        }
        Sfx.Play("click", st.transform.position + Vector3.up, 0.5f, 0.8f);
    }

    void FinishReel(int r, bool quiet)
    {
        stopped[r] = true;
        restStop[r] = stops[r];
        if (st.reels != null && r < st.reels.Length && st.reels[r] != null)
        {
            if (!skinned[r]) { CasinoPaint.SkinReel(st.reels[r], machine, r, stops[r]); skinned[r] = true; }
            st.reels[r].localRotation = Quaternion.Euler(CasinoReelMath.RestAngleDeg(stops[r]), 0f, 0f);
        }
        if (!quiet) Sfx.Play("thud", st.transform.position + Vector3.up * 1.2f, 0.35f, 1.25f + r * 0.12f);
    }

    // ------------------------------------------------------------------ per frame

    public override void Tick(double now)
    {
        TickReels(now);
        TickLever(now);
        TickGlow(now);
    }

    void TickReels(double now)
    {
        if (!spinning) return;
        float t = (float)(now - startLocal);
        bool all = true;
        for (int r = 0; r < 3; r++)
        {
            if (stopped[r]) continue;
            all = false;
            if (t < 0f) continue;
            if (t >= stopTime[r]) { FinishReel(r, false); continue; }
            if (!skinned[r] && t >= 0.1f && st.reels != null && r < st.reels.Length && st.reels[r] != null)
            {
                // while the drum is a blur, repaint its faces for where it will stop
                CasinoPaint.SkinReel(st.reels[r], machine, r, stops[r]);
                skinned[r] = true;
            }
            if (st.reels != null && r < st.reels.Length && st.reels[r] != null)
                st.reels[r].localRotation = Quaternion.Euler(CasinoReelMath.AngleDeg(restStop[r], stops[r], t, stopTime[r]), 0f, 0f);
        }
        if (all || t > stopTime[2] + 0.2f)
        {
            for (int r = 0; r < 3; r++) if (!stopped[r]) FinishReel(r, true);
            spinning = false;
            if (!celebrated) { celebrated = true; Landed(now); }
        }
    }

    void TickLever(double now)
    {
        if (st.lever == null) return;
        float t = (float)(now - startLocal) + 0.2f; // pulled just before the reels start
        float len = 0.9f;
        if (!spinning && lastLeverStep < 0) return;
        if (t < 0f || t > len)
        {
            if (lastLeverStep >= 0) { st.lever.localRotation = Quaternion.identity; lastLeverStep = -1; }
            return;
        }
        float u = t / len;
        float k = u < 0.25f ? Ease(u / 0.25f) : 1f - Ease((u - 0.25f) / 0.75f);
        st.lever.localRotation = Quaternion.Euler(70f * k, 0f, 0f);
        lastLeverStep = 1;
    }

    static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

    // ---- the win

    void Landed(double now)
    {
        if (cosmetic) return;
        // the spin finished on this machine's clock: a win pays out with the coins, the lights and the floating total
        if (spin.winCents <= 0)
        {
            if (SlotRules.IsNearMiss(machine, spin.stop0, spin.stop1, spin.stop2) && st.coinTray != null) { /* honest near miss: nothing to add, the strips speak */ }
            return;
        }
        bool big = spin.big;
        // the amount hangs in front of the machine at belt height (the occupant sees it from the stool, so does the room); a big win adds the banner up top
        Vector3 at = st.transform.TransformPoint(new Vector3(0f, 1.0f, 0.45f));
        host.Labels.Show(CasinoChipMath.Signed(spin.winCents), at, big ? new Color(1f, 0.88f, 0.3f) : CasinoPaint.Win, big ? 0.95f : 0.8f, now, big ? 4.2f : 3f, 0.2f, 0.5f);
        if (big) host.Labels.Show("BIG WIN!", st.transform.TransformPoint(new Vector3(0f, 2.45f, 0.5f)), new Color(1f, 0.82f, 0.2f), 2.0f, now, 4.2f, 0.25f, 0.5f);
        if (st.coinTray != null)
        {
            int n = Mathf.Clamp((int)(spin.winCents / System.Math.Max(1, spin.stakeCents)), 6, 40);
            try { Fx.Burst(st.coinTray.position, CasinoPaint.BrassLight, n, 2.6f); } catch (System.Exception) { /* cosmetic */ }
        }
        Sfx.Play(big ? "rare" : "cash", st.transform.position + Vector3.up * 1.2f, big ? 0.8f : 0.6f);
        celebrateStart = (float)now;
        celebrateUntil = (float)now + (big ? 4.2f : 1.6f);
        celebrateBig = big;
    }

    // ---- the glow

    float IdleGlow(double now)
    {
        if (occupantKey >= 0) return 1f;
        // a machine nobody is using breathes slowly (0.25 Hz)
        return 0.78f + 0.22f * Mathf.Sin((float)now * 1.6f + machine);
    }

    void TickGlow(double now)
    {
        float v;
        if ((float)now < celebrateUntil)
        {
            float e = ((float)now - celebrateStart);
            float amp = celebrateBig ? 1.6f : 0.8f;
            // flashing stays under 2.5 Hz (2 Hz here), is steady with Reduce Flashing, and scales with the screen-effects slider
            float wave = GameSettings.ReduceFlashing ? 0.6f : 0.5f + 0.5f * Mathf.Sin(e * 2f * Mathf.PI * 2f);
            v = IdleGlow(now) + amp * wave * GameSettings.ScreenFx01 + (GameSettings.ReduceFlashing ? 0.6f * amp : 0f);
            if (st.coinTrayLight != null && trayLightBase >= 0f) st.coinTrayLight.intensity = trayLightBase + 2.5f * (1f - (e / (celebrateUntil - celebrateStart)));
        }
        else
        {
            v = IdleGlow(now);
            if (st.coinTrayLight != null && trayLightBase >= 0f && !Mathf.Approximately(st.coinTrayLight.intensity, trayLightBase)) st.coinTrayLight.intensity = trayLightBase;
        }
        SetGlow(v, false);
    }

    void SetGlow(float v, bool force)
    {
        if (!force && Mathf.Abs(v - glowShown) < 0.01f) return;
        glowShown = v;
        for (int i = 0; i < glow.Length; i++)
        {
            var r = glow[i];
            if (r == null) continue;
            r.GetPropertyBlock(block);
            block.SetColor(EmissionId, glowBase[i] * v);
            r.SetPropertyBlock(block);
        }
    }

    public override void Teardown()
    {
        if (beaconHalo != null) CasinoFacing.Remove(beaconHalo.transform);
        if (beacon != null) Object.Destroy(beacon);
        beacon = null; beaconHalo = null;
        for (int i = 0; i < glow.Length; i++) if (glow[i] != null) glow[i].SetPropertyBlock(null);
        if (st != null && st.lever != null) st.lever.localRotation = Quaternion.identity;
        if (st != null && st.coinTrayLight != null && trayLightBase >= 0f) st.coinTrayLight.intensity = trayLightBase;
    }
}
