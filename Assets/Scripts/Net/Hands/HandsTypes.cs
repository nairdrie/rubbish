using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// Package N2 "Hands on" + N2b "Tug of war" (Docs/Multiplayer.md §4.5, §4.6, §5.1, §5.2, §13.5b). Wire types.

/// <summary>Where a networked carryable is (§4.6's diagram).</summary>
public enum HandsPhase : byte
{
    /// <summary>At rest: the host simulates it (nudges), everyone else holds it still at the host's pose.</summary>
    Rest = 0,
    /// <summary>In <see cref="CarryNetState.holder"/>'s hands (posed at the hands on every machine).</summary>
    Held = 1,
    /// <summary>Thrown or dropped: <see cref="CarryNetState.holder"/>'s machine simulates it and streams its pose.</summary>
    Flight = 2,
    /// <summary>A rhythm tug between holder and <see cref="CarryNetState.other"/> (N2b).</summary>
    Tug = 3,
    /// <summary>Gone (a receiver took it, or it burst). <see cref="CarryNetState.flags"/> says how, for the effects.</summary>
    Gone = 4,
}

/// <summary>What a networked carryable is, so every machine can build its own copy (no contents: §6.3).</summary>
public struct CarryDesc : INetworkSerializable
{
    public const byte KindBag = 1, KindItem = 2;
    public byte kind;
    /// <summary>Bag: its bagId (curb bags keep their record id). Item: its uid.</summary>
    public FixedString64Bytes key;
    public FixedString32Bytes defId;
    public float mass, value, condition;
    public byte sack;
    public bool revealed;
    /// <summary>Personal reveal audience (ulong.MaxValue = automated/unknown finder).</summary>
    public ulong finderClientId;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref kind);
        s.SerializeValue(ref key);
        s.SerializeValue(ref defId);
        s.SerializeValue(ref mass);
        s.SerializeValue(ref value);
        s.SerializeValue(ref condition);
        s.SerializeValue(ref sack);
        s.SerializeValue(ref revealed);
        s.SerializeValue(ref finderClientId);
    }
}

/// <summary>A networked carryable's state as the host last decided it.</summary>
public struct CarryNetState : INetworkSerializable
{
    public const byte FlagPass = 1, FlagCaught = 2, FlagHeave = 4, FlagBurst = 8, FlagReceived = 16, FlagYoink = 32, FlagRip = 64;

    public uint id, seq;
    public byte phase, flags;
    /// <summary>Held: the holder. Flight: the machine simulating it. Tug: the holder.</summary>
    public ulong holder;
    /// <summary>Tug: the challenger. A pass: who threw it.</summary>
    public ulong other;
    public Vector3 pos, vel;
    public Quaternion rot;

    public HandsPhase Phase => (HandsPhase)phase;
    public bool Has(byte f) => (flags & f) != 0;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref seq);
        s.SerializeValue(ref phase);
        s.SerializeValue(ref flags);
        s.SerializeValue(ref holder);
        s.SerializeValue(ref other);
        s.SerializeValue(ref pos);
        s.SerializeValue(ref vel);
        s.SerializeValue(ref rot);
    }
}

/// <summary>A player's body as the host decided it (§5.1 "Body state").</summary>
public enum BodyState : byte
{
    Normal = 0,
    /// <summary>Stumbling after a shove or a mid bonk (≤0.5 s).</summary>
    Stunned = 1,
    /// <summary>Knocked down: flat on the floor and dizzy. Carryable until recovered.</summary>
    KnockedOut = 2,
    /// <summary>Playing dead (Z): you chose it, so friends may pick you up.</summary>
    Flopped = 3,
    /// <summary>In someone's arms (<see cref="BodyNet.other"/>), max 10 s, wriggle free with 5 taps.</summary>
    Carried = 4,
    /// <summary>Thrown by a friend: your own machine flies you; you land knocked out for 1 s.</summary>
    Thrown = 5,
    /// <summary>On a truck's rear step (<see cref="BodyNet.other"/> = the truck's NetworkObjectId, <see cref="BodyNet.step"/>).</summary>
    Riding = 6,
}

public struct BodyNet : INetworkSerializable, IEquatable<BodyNet>
{
    public ulong client;
    public byte state, step;
    /// <summary>Carried: by whom. Riding: the truck's NetworkObjectId.</summary>
    public ulong other;
    /// <summary>The player this one is carrying (or <see cref="HandsNet.None"/>).</summary>
    public ulong carrying;
    /// <summary>Server time the timed state ends (0 = not timed).</summary>
    public double until;
    /// <summary>Server time hit immunity ends; this does not prevent pickup while down.</summary>
    public double immuneUntil;
    /// <summary>Server time before which nobody may pick this player up again (an escape).</summary>
    public double noGrabUntil;

    public BodyState State => (BodyState)state;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref client);
        s.SerializeValue(ref state);
        s.SerializeValue(ref step);
        s.SerializeValue(ref other);
        s.SerializeValue(ref carrying);
        s.SerializeValue(ref until);
        s.SerializeValue(ref immuneUntil);
        s.SerializeValue(ref noGrabUntil);
    }

    public bool Equals(BodyNet o) => client == o.client && state == o.state && step == o.step && other == o.other &&
                                     carrying == o.carrying && until.Equals(o.until) && immuneUntil.Equals(o.immuneUntil) &&
                                     noGrabUntil.Equals(o.noGrabUntil);
    public override bool Equals(object obj) => obj is BodyNet o && Equals(o);
    public override int GetHashCode() => client.GetHashCode();
}

/// <summary>A shared grip, refereed by the host. Legacy field names/order retained on the wire.</summary>
public struct TugNet : INetworkSerializable, IEquatable<TugNet>
{
    public uint tug, item;
    public ulong holder, challenger;
    /// <summary>Server time the shared grip began (there are no beats).</summary>
    public double beat0;
    /// <summary>−1 (holder) … +1 (challenger).</summary>
    public float meter;
    public float strainH, strainC, ripAt;
    /// <summary>Reserved legacy fields; shared movement does not use button timing.</summary>
    public byte goodH, goodC;
    /// <summary>0 sharing; 1 holder keeps; 2 challenger keeps; 3 ripped; 4 safely dropped/interrupted.</summary>
    public byte result;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref tug);
        s.SerializeValue(ref item);
        s.SerializeValue(ref holder);
        s.SerializeValue(ref challenger);
        s.SerializeValue(ref beat0);
        s.SerializeValue(ref meter);
        s.SerializeValue(ref strainH);
        s.SerializeValue(ref strainC);
        s.SerializeValue(ref ripAt);
        s.SerializeValue(ref goodH);
        s.SerializeValue(ref goodC);
        s.SerializeValue(ref result);
    }

    public bool Equals(TugNet o) => tug == o.tug && item == o.item && holder == o.holder && challenger == o.challenger &&
                                    beat0.Equals(o.beat0) && meter.Equals(o.meter) && strainH.Equals(o.strainH) &&
                                    strainC.Equals(o.strainC) && ripAt.Equals(o.ripAt) && goodH == o.goodH && goodC == o.goodC &&
                                    result == o.result;
    public override bool Equals(object obj) => obj is TugNet o && Equals(o);
    public override int GetHashCode() => (int)tug;
}

/// <summary>The numbers of §4.5–4.6 in one place (rule 12: every physical verb declares its limits).</summary>
public static class HandsTuning
{
    // grab and heave (§4.6)
    public const float HeaveWindow = 0.25f, ContestRadius = 3f, GrabRange = 2.7f, LagSlack = 1.5f, QualifyRange = 1.5f;
    public const float StampSlack = 0.05f, RegrabCooldown = 0.5f;
    /// <summary>Clamped press stamps closer than this (one 30 fps frame) are a tie, decided by a fair coin.</summary>
    public const float TieWindow = 0.035f;
    /// <summary>How far a grabber's own reported position may be from the host's view of them and still count.</summary>
    public const float ClaimSlack = 6f;
    // pass and catch (§4.5)
    public const float PassRange = 8f, PassCone = 15f, CatchRadius = 0.9f, CatchFacing = 70f;
    // bonk (§4.5)
    public const float BoopSpeed = 6f, KnockoutSpeed = 11f, MaxThrowSpeed = 15f, SameBonkCooldown = 4f;
    // punch / swing (§4.5). Three landed bare-fist punches on the same target within the window knock them down.
    public const float ShoveRange = 1.6f, SwingRange = 2.1f, ShoveCone = 60f, ShoveCooldown = 0.58f, SwingCooldown = 1.5f, HostShoveRange = 2.6f;
    public const float PunchComboWindow = 2.8f, PunchKnockdownSeconds = 3.5f;
    // body (§4.1 #4, §4.5)
    public const float Immunity = 5f, StumbleSeconds = 0.45f, KnockoutSeconds = 1.2f, ThrownKnockout = 1.0f;
    public const float CarryMax = 10f, EscapeTaps = 5, NoRegrab = 3f, PlayerMassKg = 60f;
    // hitch (§4.4)
    public const float HitchMaxSpeed = 4.5f, HopOffSafeSpeed = 3f;
    // Shared carry / pull tuning lives in SharedGripRules.
}
