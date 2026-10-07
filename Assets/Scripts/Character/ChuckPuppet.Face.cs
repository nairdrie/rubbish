using UnityEngine;

/// <summary>
/// Chuck's face and idle fidgets (Docs/Character.md sections 9 and 11): mismatched blinks (the right eye is late),
/// squints, brows, mouth shapes hidden under the mustache, strain tint + cheek puff, and the six idle fidgets
/// (sniff-sniff, tummy pat, hat pop, googly cross, stretch-yawn, look-see). Everything is scale/offset tweaks on
/// existing parts; no extra objects, no allocations.
/// </summary>
public partial class ChuckPuppet
{
    enum Mouth : byte { Closed, SmallO, Open, BigO, Yawn }
    enum Fidget : byte { None, Sniff, TummyPat, HatPop, GooglyCross, StretchYawn, LookSee }

    // mouth shapes (scale, offset from the closed position), HeadBobble-local rig metres
    readonly Vector3[] mouthScale = new Vector3[5];
    readonly Vector3[] mouthOffset = new Vector3[5];
    Vector3 mouthRestPos;
    Vector3 mouthPosCur, mouthScaleCur;

    // expression timers (seconds left)
    float hupT, yellT, happyT, oofT;
    // blink
    float nextBlinkAt, blinkStart = -10f, blink2Start = -10f;
    float eyeOpenSm = 1f;
    // strain
    float strainSm;
    // brows (spring targets)
    float browYT, browInnerT;

    // fidgets
    Fidget fidget, lastFidget;
    float fidgetT, fidgetDur, idleTime, nextFidgetAt = 6f;
    float fidgetSplay, fidgetHeadPitch, fidgetHeadYaw, fidgetCapY, fidgetPatW, fidgetPatOut, fidgetNose = 1f;
    int fidgetStep;

    void InitFace()
    {
        mouthRestPos = restPos[ChuckPartsData.Mouth];
        SetMouthShapes(ChuckPartsData.Variants[0]);
        mouthPosCur = mouthRestPos;
        mouthScaleCur = mouthScale[0];
        nextBlinkAt = Random.Range(1f, 3f);
        lobeTwitchL = Random.Range(2f, 5f);
        lobeTwitchR = Random.Range(2f, 5f);
    }

    void SetMouthShapes(ChuckVariantDef def)
    {
        if (def.hasMouth)
        {
            // Visible smile (no mustache, P2 Dot).
            mouthRestPos = def.mouthPos;
            Vector3 c = def.mouthClosed, o = def.mouthOpen;
            mouthScale[0] = c; mouthOffset[0] = Vector3.zero;
            mouthScale[1] = Vector3.Lerp(c, o, 0.5f); mouthOffset[1] = new Vector3(0f, -0.005f, 0.004f);
            mouthScale[2] = o; mouthOffset[2] = new Vector3(0f, -0.01f, 0.008f);
            mouthScale[3] = new Vector3(o.x * 0.85f, o.y * 1.25f, o.z); mouthOffset[3] = new Vector3(0f, -0.012f, 0.008f);
            mouthScale[4] = o * 1.1f; mouthOffset[4] = new Vector3(0f, -0.014f, 0.008f);
        }
        else
        {
            mouthRestPos = defaultPos != null ? defaultPos[ChuckPartsData.Mouth] : mouthRestPos;
            mouthScale[0] = ChuckPartsData.Parts[ChuckPartsData.Mouth].scale; mouthOffset[0] = Vector3.zero;
            mouthScale[1] = new Vector3(0.08f, 0.05f, 0.04f); mouthOffset[1] = new Vector3(0f, -0.012f, 0.006f);
            mouthScale[2] = new Vector3(0.10f, 0.075f, 0.05f); mouthOffset[2] = new Vector3(0f, -0.02f, 0.01f);
            mouthScale[3] = new Vector3(0.08f, 0.08f, 0.05f); mouthOffset[3] = new Vector3(0f, -0.02f, 0.01f);
            mouthScale[4] = new Vector3(0.11f, 0.09f, 0.05f); mouthOffset[4] = new Vector3(0f, -0.025f, 0.01f);
        }
        mouthPosCur = mouthRestPos;
        mouthScaleCur = mouthScale[0];
    }

    // ------------------------------------------------------------------ triggers

    void Hup() { hupT = 0.5f; }
    void Yell(float seconds) { yellT = Mathf.Max(yellT, seconds); sStache.KickPeak(-8f, WStache, ZStache); }
    void Happy() { happyT = 0.45f; sCowX.KickPeak(15f, WCow, ZCow); }
    void Oof(float seconds) { oofT = Mathf.Max(oofT, seconds); }
    void SnapMouthShut() { yellT = 0f; mouthPosCur = mouthRestPos; mouthScaleCur = mouthScale[0]; }

    void ForceBlink()
    {
        if (clock - blinkStart < 0.2f) return;
        blinkStart = clock;
        nextBlinkAt = clock + Random.Range(2.5f, 5.5f);
    }

    static float BlinkCurve(float t)
    {
        if (t < 0f) return 1f;
        if (t < 0.05f) return Mathf.Lerp(1f, 0.1f, t / 0.05f);
        if (t < 0.09f) return 0.1f;
        if (t < 0.15f) return Mathf.Lerp(0.1f, 1f, (t - 0.09f) / 0.06f);
        return 1f;
    }

    // ------------------------------------------------------------------ per frame

    void UpdateFace(float dt)
    {
        hupT = Mathf.Max(0f, hupT - dt);
        yellT = Mathf.Max(0f, yellT - dt);
        happyT = Mathf.Max(0f, happyT - dt);
        oofT = Mathf.Max(0f, oofT - dt);

        // Strain: heavy bag and/or throw charge.
        float carriedMass = carryW > 0.01f || charging ? heldMass : 0f;
        float k = Mathf.Max(0.7f * charge, 0.6f * Mathf.Clamp01((carriedMass - 6f) / 9f));
        strainSm = Mathf.Lerp(strainSm, k, 1f - Mathf.Exp(-(k > strainSm ? 8f : 4f) * dt));
        float strainN = strainSm / 0.7f;

        // --- eyes (EyeRoot scale.y): squint targets, surprise when flailing
        float eye = 1f - 0.45f * strainN;
        if (happyT > 0f) eye = Mathf.Min(eye, 0.6f);
        if (oofT > 0f) eye = Mathf.Min(eye, 0.6f);
        if (fidget == Fidget.StretchYawn) eye = Mathf.Min(eye, Mathf.Lerp(1f, 0.5f, FidgetW()));
        if (fidget == Fidget.TummyPat) eye = Mathf.Min(eye, Mathf.Lerp(1f, 0.6f, FidgetW()));
        if (flailW > 0f) eye = Mathf.Lerp(eye, 1.15f, flailW);
        eyeOpenSm = Mathf.Lerp(eyeOpenSm, eye, 1f - Mathf.Exp(-18f * dt));

        // blink schedule (20% double blinks), the right eye runs 0.03 s late
        if (clock >= nextBlinkAt)
        {
            blinkStart = clock;
            blink2Start = Random.value < 0.2f ? clock + 0.2f : -10f;
            nextBlinkAt = clock + Random.Range(2.5f, 5.5f);
        }

        // --- pupils: float up in free fall, jitter at full throw charge
        if (flailW > 0f)
        {
            Vector2 upPull = new Vector2(0f, 2.5f * flailW);
            if (eyeL != null) eyeL.Pull(upPull);
            if (eyeR != null) eyeR.Pull(upPull);
            if (eyeBadge != null) eyeBadge.Pull(upPull);
        }
        if (charge >= 1f && Random.value < 0.25f) KickPupils(0.08f);

        // --- brows
        float by = 0f, bi = 0f;
        if (hupT > 0f) { by += 0.012f; bi += 8f; }
        by -= 0.01f * strainN; bi -= 12f * charge;
        if (yellT > 0f) by += 0.015f;
        by += 0.015f * flailW;
        if (happyT > 0f) by += 0.01f;
        if (fidget != Fidget.None) by += FidgetBrow();
        browYT = Mathf.Clamp(by, -0.02f, 0.025f);
        browInnerT = bi;

        // --- mouth
        Mouth shape = Mouth.Closed;
        if (flailW > 0.3f) shape = Mouth.BigO;
        else if (yellT > 0f) shape = Mouth.Open;
        else if (hupT > 0.2f) shape = Mouth.SmallO;
        else if (happyT > 0.15f) shape = Mouth.Open;
        else if (fidget == Fidget.StretchYawn && FidgetW() > 0.3f && fidgetT > 0.4f && fidgetT < fidgetDur - 0.5f) shape = Mouth.Yawn;
        else if ((fidget == Fidget.GooglyCross || fidget == Fidget.Sniff) && fidgetT > 0.8f) shape = Mouth.SmallO;
        int si = (int)shape;
        Vector3 mp = mouthRestPos + mouthOffset[si];
        float mk = 1f - Mathf.Exp(-30f * dt);
        mouthPosCur = Vector3.Lerp(mouthPosCur, mp, mk);
        mouthScaleCur = Vector3.Lerp(mouthScaleCur, mouthScale[si], mk);
    }

    void WriteFace()
    {
        // eyes
        float bl = Mathf.Min(BlinkCurve(clock - blinkStart), BlinkCurve(clock - blink2Start));
        float br = Mathf.Min(BlinkCurve(clock - blinkStart - 0.03f), BlinkCurve(clock - blink2Start - 0.03f));
        parts[ChuckPartsData.EyeRoot_L].localScale = new Vector3(1f, eyeOpenSm * bl, 1f);
        parts[ChuckPartsData.EyeRoot_R].localScale = new Vector3(1f, eyeOpenSm * br, 1f);

        // brows (the right one rides 3 mm higher on its bigger eye; that's baked into its rest pose)
        Vector3 fwd = Vector3.forward;
        int i = ChuckPartsData.Brow_L;
        parts[i].localPosition = restPos[i] + new Vector3(0f, sBrowY.x, 0f);
        parts[i].localRotation = Quaternion.AngleAxis(sBrowInner.x, fwd) * restRot[i];
        i = ChuckPartsData.Brow_R;
        parts[i].localPosition = restPos[i] + new Vector3(0f, sBrowY.x, 0f);
        parts[i].localRotation = Quaternion.AngleAxis(-sBrowInner.x, fwd) * restRot[i];
        i = ChuckPartsData.Unibrow;
        parts[i].localPosition = restPos[i] + new Vector3(0f, sBrowY.x, 0f);

        // mouth
        i = ChuckPartsData.Mouth;
        parts[i].localPosition = mouthPosCur;
        parts[i].localScale = mouthScaleCur;

        // cheeks puff with the strain, nose sniffs
        float puff = 1f + 0.4f * (strainSm / 0.7f);
        i = ChuckPartsData.Blush_L; parts[i].localScale = restScale[i] * puff;
        i = ChuckPartsData.Blush_R; parts[i].localScale = restScale[i] * puff;
        i = ChuckPartsData.Nose; parts[i].localScale = restScale[i] * fidgetNose;
    }

    void ApplyStrainTint()
    {
        if (instSkin == null) return;
        float s = Mathf.Round(strainSm * 200f) / 200f;
        if (Mathf.Abs(s - shownStrain) < 0.004f) return;
        shownStrain = s;
        instSkin.SetColor(BaseColorId, Color.Lerp(skinBase, ChuckPartsData.StrainRed, s));
    }

    // ------------------------------------------------------------------ idle fidgets

    bool IsIdle()
    {
        if (!grounded || speedParam > 0.1f || planarSpeed > 0.25f) return false;
        if (carryW > 0.01f || charging || flailW > 0f) return false;
        if (carrier != null && carrier.IsCarrying) return false;
        if (inputs != null && carrier != null && carrier.IsLocalOwner && (inputs.move.sqrMagnitude > 0.01f || inputs.jump)) return false;
        return true;
    }

    void UpdateFidget(float dt)
    {
        fidgetSplay = 0f; fidgetHeadPitch = 0f; fidgetHeadYaw = 0f; fidgetCapY = 0f; fidgetPatW = 0f; fidgetPatOut = 0f; fidgetNose = 1f;

        if (!IsIdle())
        {
            idleTime = 0f;
            nextFidgetAt = 6f;
            if (fidget != Fidget.None) AbortFidget();
            return;
        }
        idleTime += dt;
        if (fidget == Fidget.None)
        {
            if (idleTime >= nextFidgetAt && !previewMode) StartFidget();
            return;
        }

        fidgetT += dt;
        if (fidgetT >= fidgetDur)
        {
            fidget = Fidget.None;
            nextFidgetAt = idleTime + Random.Range(5f, 9f);
            return;
        }
        float w = FidgetW();
        float t = fidgetT;
        switch (fidget)
        {
            case Fidget.Sniff:
                // Nose pulses x1.12 three times at 7 Hz with a head dip... then brows rocket up: treasure?
                if (t < 0.45f) fidgetNose = 1f + 0.12f * Mathf.Abs(Mathf.Sin(t * Mathf.PI * 7f));
                fidgetHeadPitch = 6f * w * (t < 0.8f ? 1f : Mathf.Clamp01((1.1f - t) / 0.3f));
                if (fidgetStep == 0 && t >= 0.8f)
                {
                    fidgetStep = 1;
                    KickPupils(0.25f);
                }
                break;
            case Fidget.TummyPat:
                fidgetPatW = w;
                if (t > 0.3f && t < 1.3f)
                {
                    float ph = (t - 0.3f) * 3f; // 3 pats at 3 Hz
                    fidgetPatOut = 0.03f * (0.5f + 0.5f * Mathf.Cos(ph * 2f * Mathf.PI));
                    int pat = Mathf.FloorToInt(ph);
                    if (pat >= fidgetStep && ph - pat > 0.45f && fidgetStep < 3)
                    {
                        fidgetStep = pat + 1;
                        sBelly.KickPeak(-0.05f, WBelly, ZBelly);
                    }
                }
                else fidgetPatOut = 0.03f;
                break;
            case Fidget.HatPop:
                if (fidgetStep == 0 && t > 0.1f)
                {
                    fidgetStep = 1;
                    sCapY.KickPeak(0.06f, WCap, ZCap);
                    sCapPitch.KickPeak(-6f, WCap, ZCap);
                    sCowX.KickPeak(22f, WCow, ZCow);
                }
                break;
            case Fidget.GooglyCross:
                if (t > 0.1f && t < 0.75f)
                {
                    if (eyeL != null) eyeL.Pull(new Vector2(3f, 0.6f));
                    if (eyeR != null) eyeR.Pull(new Vector2(-3f, 0.6f));
                }
                if (fidgetStep == 0 && t > 0.1f)
                {
                    fidgetStep = 1;
                    if (eyeL != null) eyeL.Kick(new Vector2(0.3f, 0f));
                    if (eyeR != null) eyeR.Kick(new Vector2(-0.3f, 0f));
                }
                break;
            case Fidget.StretchYawn:
                // Arms ramp up to 70 deg (base 18 + 52) over 0.8 s, hold, relax.
                float up = Mathf.Clamp01(t / 0.8f), down = Mathf.Clamp01((fidgetDur - t) / 0.6f);
                fidgetSplay = 52f * Mathf.SmoothStep(0f, 1f, Mathf.Min(up, down));
                fidgetHeadPitch = -5f * Mathf.Min(up, down);
                break;
            case Fidget.LookSee:
                // Nervous look left and right (raccoons?!), pupils slosh.
                float u = t / fidgetDur;
                fidgetHeadYaw = 30f * Mathf.Sin(u * 2f * Mathf.PI) * w;
                if (fidgetStep == 0 && u > 0.2f) { fidgetStep = 1; KickPupils(0.35f); }
                if (fidgetStep == 1 && u > 0.7f) { fidgetStep = 2; KickPupils(0.35f); }
                break;
        }
    }

    float FidgetW()
    {
        if (fidget == Fidget.None) return 0f;
        return Mathf.Clamp01(Mathf.Min(fidgetT / 0.15f, (fidgetDur - fidgetT) / 0.15f));
    }

    float FidgetBrow()
    {
        float w = FidgetW();
        switch (fidget)
        {
            case Fidget.Sniff: return fidgetT > 0.8f ? 0.02f * w : 0f;
            case Fidget.StretchYawn: return 0.015f * w;
            case Fidget.HatPop: return 0.01f * w;
            case Fidget.GooglyCross: return 0.008f * w;
            case Fidget.LookSee: return 0.01f * w;
            case Fidget.TummyPat: return 0.006f * w;
        }
        return 0f;
    }

    void StartFidget()
    {
        Fidget f;
        int guard = 0;
        do { f = (Fidget)Random.Range(1, 7); } while (f == lastFidget && ++guard < 8);
        StartFidget(f);
    }

    void StartFidget(Fidget f)
    {
        fidget = lastFidget = f;
        fidgetT = 0f;
        fidgetStep = 0;
        switch (f)
        {
            case Fidget.Sniff: fidgetDur = 1.6f; break;
            case Fidget.TummyPat: fidgetDur = 1.7f; break;
            case Fidget.HatPop: fidgetDur = 1.0f; break;
            case Fidget.GooglyCross: fidgetDur = 1.1f; break;
            case Fidget.StretchYawn: fidgetDur = 2.0f; break;
            default: fidgetDur = 1.6f; break;
        }
    }

    /// <summary>Stop the current fidget instantly (any input).</summary>
    void AbortFidget()
    {
        fidget = Fidget.None;
        fidgetT = 0f;
        fidgetSplay = 0f; fidgetHeadPitch = 0f; fidgetHeadYaw = 0f; fidgetCapY = 0f; fidgetPatW = 0f; fidgetNose = 1f;
    }

    void KickPupils(float s)
    {
        if (eyeL != null) eyeL.Kick(Random.insideUnitCircle * s);
        if (eyeR != null) eyeR.Kick(Random.insideUnitCircle * s);
        if (eyeBadge != null) eyeBadge.Kick(Random.insideUnitCircle * s);
    }

    /// <summary>Debug / preview: play a fidget now (1 Sniff, 2 TummyPat, 3 HatPop, 4 GooglyCross, 5 StretchYawn, 6 LookSee).</summary>
    public void DebugFidget(int which)
    {
        if (!inited) Init();
        if (!valid) return;
        StartFidget((Fidget)Mathf.Clamp(which, 1, 6));
    }
}
