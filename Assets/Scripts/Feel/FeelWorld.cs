using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Read-only view of the living world for the HUD: curb trash per house, freshness, late counts, the planned route
/// and City Cleanliness. It reads the frozen data directly (GameSession.CurbBags, GameClock, Economy) and binds to the
/// Trash system's newer members BY REFLECTION (TrashRoute.PlannedRoute / TotalCurbBags / LateBags and the
/// CityCleanliness static), so the HUD compiles and works before, during and after that rewrite: whatever is
/// missing is computed here from the same frozen data. Never throws; a member that throws is dropped (logged once).
/// Also hosts the shared freshness palette/labels so HUD rows, pay popups and (optionally) stop markers agree.
/// </summary>
public static class FeelWorld
{
    /// <summary>Curb trash at one house.</summary>
    public struct StopTrash
    {
        public int bags, late;
        /// <summary>Age in in-game hours of the oldest bag at this curb (0 when empty).</summary>
        public double oldestAge;
        /// <summary>The house's district (null = starter): lateness / neglect use its windows.</summary>
        public DistrictDef district;
        public bool Neglected => bags > 0 && Districts.IsNeglected(district, oldestAge);
    }

    public enum Lateness { None, OnTime, BitLate, Late, SuperLate, CurbClean }

    // ------------------------------------------------------------------ snapshot of GameSession.CurbBags

    static readonly Dictionary<string, StopTrash> byStop = new Dictionary<string, StopTrash>();
    static int snapFrame = -1;
    static int ownTotal, ownLate, neglectedHouses;
    static double ownDirt;

    static void Snapshot()
    {
        if (Application.isPlaying && Time.frameCount == snapFrame) return;
        snapFrame = Time.frameCount;
        byStop.Clear();
        ownTotal = ownLate = neglectedHouses = 0;
        ownDirt = 0;
        var bags = GameSession.CurbBags;
        for (int i = 0; i < bags.Count; i++)
        {
            var b = bags[i];
            if (b == null || string.IsNullOrEmpty(b.stopId)) continue;
            double age = GameClock.AgeHours(b.putOutAtHours);
            byStop.TryGetValue(b.stopId, out var s);
            if (s.bags == 0) s.district = Districts.ForStopId(b.stopId);
            s.bags++;
            if (Districts.IsLate(s.district, age)) { s.late++; ownLate++; }
            if (age > s.oldestAge) s.oldestAge = age;
            byStop[b.stopId] = s;
            ownTotal++;
            ownDirt += DirtWeight(age);
        }
        foreach (var kv in byStop) if (kv.Value.Neglected) neglectedHouses++;
    }

    /// <summary>Curb trash at a house (by PickupStop.stopId).</summary>
    public static StopTrash At(string stopId)
    {
        if (string.IsNullOrEmpty(stopId)) return default;
        Snapshot();
        return byStop.TryGetValue(stopId, out var s) ? s : default;
    }

    /// <summary>Houses with at least one bag out.</summary>
    public static int HousesWithTrash { get { Snapshot(); return byStop.Count; } }
    /// <summary>Houses whose oldest bag is past Economy.NeglectedHours.</summary>
    public static int NeglectedHouses { get { Snapshot(); return neglectedHouses; } }

    /// <summary>
    /// True once the world runs on random curb trash (the Trash rewrite is compiled in, or curb bags exist).
    /// False = legacy fixed daily route, and the HUD keeps its old "Stops 3/8" look.
    /// </summary>
    public static bool IsLive
    {
        get
        {
            Probe();
            return plannedMi != null || plannedIntoMi != null || cityType != null || totalGetter != null || GameSession.CurbBags.Count > 0;
        }
    }

    /// <summary>Bags at curbs right now (TrashRoute.TotalCurbBags when available, else counted here).</summary>
    public static int TotalCurbBags(TrashRoute route)
    {
        Snapshot();
        return ReadInt(totalGetter, route, ownTotal);
    }

    /// <summary>Curb bags past the on-time window (TrashRoute.LateBags when available, else counted here).</summary>
    public static int LateBags(TrashRoute route)
    {
        Snapshot();
        return ReadInt(lateGetter, route, ownLate);
    }

    /// <summary>Does this stop have trash waiting (live: curb bags; legacy: not cleared today)?</summary>
    public static bool HasTrash(PickupStop s, bool live)
    {
        if (s == null) return false;
        return live ? At(s.stopId).bags > 0 : !s.IsCleared;
    }

    // ------------------------------------------------------------------ route plan

    /// <summary>
    /// The next n stops as a chain (TrashRoute.PlannedRoute when it exists, else a greedy chain starting at
    /// TrashRoute.NextStop and hopping to the nearest house with trash). Fills `into`, returns its count.
    /// </summary>
    public static int PlannedRoute(TrashRoute route, Vector3 from, int n, List<PickupStop> into)
    {
        into.Clear();
        if (route == null || n <= 0) return 0;
        Probe();
        if (plannedIntoMi != null)
        {
            try
            {
                plannedIntoArgs[0] = from;
                plannedIntoArgs[1] = n;
                plannedIntoArgs[2] = into;
                plannedIntoMi.Invoke(plannedIntoMi.IsStatic ? null : route, plannedIntoArgs);
                plannedIntoArgs[2] = null;
                for (int i = into.Count - 1; i >= 0; i--) if (into[i] == null) into.RemoveAt(i);
                if (into.Count > n) into.RemoveRange(n, into.Count - n);
                return into.Count;
            }
            catch (Exception e) { Drop(ref plannedIntoMi, "TrashRoute.PlannedRoute(into)", e); into.Clear(); }
        }
        if (plannedMi != null)
        {
            try
            {
                plannedArgs[0] = from;
                plannedArgs[1] = n;
                if (plannedMi.Invoke(plannedMi.IsStatic ? null : route, plannedArgs) is IEnumerable list)
                    foreach (var o in list)
                        if (o is PickupStop s && s != null && !into.Contains(s)) { into.Add(s); if (into.Count >= n) break; }
                return into.Count;
            }
            catch (Exception e) { Drop(ref plannedMi, "TrashRoute.PlannedRoute", e); into.Clear(); }
        }

        bool live = IsLive;
        PickupStop first = null;
        try { first = route.NextStop(from); } catch (Exception e) { FeelRunner.Report(e); }
        if (first == null) return 0;
        into.Add(first);
        Vector3 at = first.transform.position;
        var stops = route.Stops;
        while (into.Count < n)
        {
            PickupStop best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < stops.Count; i++)
            {
                var s = stops[i];
                if (s == null || into.Contains(s) || !HasTrash(s, live)) continue;
                float d = (s.transform.position - at).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best == null) break;
            into.Add(best);
            at = best.transform.position;
        }
        return into.Count;
    }

    // ------------------------------------------------------------------ city cleanliness

    /// <summary>True when the value comes from the Trash system's CityCleanliness static (else computed here).</summary>
    public static bool CleanlinessFromTrash { get { Probe(); return cityGetter != null; } }

    /// <summary>City Cleanliness 0..100 (100 = spotless).</summary>
    public static float Cleanliness
    {
        get
        {
            Probe();
            if (cityGetter != null)
            {
                try
                {
                    double v = cityGetter();
                    if (!double.IsNaN(v) && !double.IsInfinity(v))
                    {
                        if (cityScale == Scale.Unit || (cityScale == Scale.Unknown && v <= 1.0001)) v *= 100.0;
                        return Mathf.Clamp((float)v, 0f, 100f);
                    }
                }
                catch (Exception e) { cityGetter = null; LogOnce("CityCleanliness." + cityMember, e); }
            }
            Snapshot();
            // Fallback (no loose litter term): fresh bags barely count, late ones a lot, neglected piles most.
            return Mathf.Clamp01(1f - (float)(ownDirt / FallbackDirtForZero)) * 100f;
        }
    }

    /// <summary>Dirt units at which the fallback cleanliness reaches 0 %.</summary>
    public const float FallbackDirtForZero = 45f;

    static double DirtWeight(double age)
    {
        if (age <= Economy.OnTimeHours) return 0.2;
        if (age <= Economy.LateFloorHours) return Mathf.Lerp(0.6f, 1.5f, Mathf.InverseLerp(Economy.OnTimeHours, Economy.LateFloorHours, (float)age));
        if (age <= Economy.NeglectedHours) return Mathf.Lerp(1.5f, 2.5f, Mathf.InverseLerp(Economy.LateFloorHours, Economy.NeglectedHours, (float)age));
        return 3.0;
    }

    /// <summary>Word for a cleanliness value: sparkling / clean / okay / grubby / STINKY.</summary>
    public static string CleanlinessWord(float pct) =>
        pct >= 90f ? "sparkling!" : pct >= 75f ? "clean" : pct >= 55f ? "okay" : pct >= 35f ? "grubby" : "STINKY";

    /// <summary>Red (0 %) → orange → yellow → green (100 %).</summary>
    public static Color CleanlinessColor(float pct)
    {
        if (pct < 35f) return Color.Lerp(new Color(1f, 0.22f, 0.2f), new Color(1f, 0.5f, 0.18f), pct / 35f);
        if (pct < 60f) return Color.Lerp(new Color(1f, 0.5f, 0.18f), new Color(1f, 0.85f, 0.2f), (pct - 35f) / 25f);
        if (pct < 85f) return Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(0.4f, 0.95f, 0.4f), (pct - 60f) / 25f);
        return Color.Lerp(new Color(0.4f, 0.95f, 0.4f), new Color(0.45f, 1f, 0.75f), (pct - 85f) / 15f);
    }

    // ------------------------------------------------------------------ freshness palette + labels

    /// <summary>
    /// Green fresh → yellow (going late soon) → orange late → red neglected. Uses the Trash system's
    /// CurbTrash.FreshnessColor when present so HUD dots match the stop markers exactly.
    /// </summary>
    public static Color FreshnessColor(double ageHours)
    {
        Probe();
        if (trashColor != null)
        {
            try { return trashColor(ageHours); }
            catch (Exception e) { trashColor = null; LogOnce("CurbTrash.FreshnessColor", e); }
        }
        float a = (float)ageHours, on = Economy.OnTimeHours;
        if (a <= on * 0.5f) return new Color(0.4f, 1f, 0.45f);
        if (a <= on) return Color.Lerp(new Color(0.4f, 1f, 0.45f), new Color(1f, 0.9f, 0.25f), (a - on * 0.5f) / (on * 0.5f));
        if (a <= Economy.LateFloorHours) return Color.Lerp(new Color(1f, 0.66f, 0.2f), new Color(1f, 0.45f, 0.16f), Mathf.InverseLerp(on, Economy.LateFloorHours, a));
        if (a < Economy.NeglectedHours) return Color.Lerp(new Color(1f, 0.45f, 0.16f), new Color(1f, 0.3f, 0.2f), Mathf.InverseLerp(Economy.LateFloorHours, Economy.NeglectedHours, a));
        return new Color(1f, 0.2f, 0.22f);
    }

    /// <summary>"2h left" / "40m left" / "just late" / "3h late" / "STINKY!" for the oldest bag at a curb.</summary>
    public static string FreshnessShort(double ageHours)
    {
        double on = Economy.OnTimeHours;
        if (ageHours <= on)
        {
            double left = on - ageHours;
            return left >= 1.0 ? $"{(int)Math.Floor(left)}h left" : $"{Math.Max(1, (int)Math.Round(left * 60.0))}m left";
        }
        if (ageHours >= Economy.NeglectedHours) return "STINKY!";
        double late = ageHours - on;
        return late < 1.0 ? "just late" : $"{(int)Math.Floor(late)}h late";
    }

    /// <summary>
    /// "3 bags · 2 h late" (freshness part colored). Uses CurbTrash.AgeText when present so the HUD rows read
    /// exactly like the label floating over that house; else "2h left" / "3h late" / "STINKY!".
    /// </summary>
    public static string BagsAndFreshness(int bags, double oldestAge, bool color = true)
    {
        Probe();
        string f = null;
        if (oldestAge >= Economy.NeglectedHours) f = "STINKY!"; // compact for HUD rows (markers say "14 h late · STINKY")
        else if (trashAgeText != null)
        {
            try { f = trashAgeText(oldestAge); }
            catch (Exception e) { trashAgeText = null; LogOnce("CurbTrash.AgeText", e); }
        }
        if (string.IsNullOrEmpty(f)) f = FreshnessShort(oldestAge);
        if (color) f = $"<color=#{FeelUi.Hex(FreshnessColor(oldestAge))}>{f}</color>";
        return $"{bags} bag{(bags == 1 ? "" : "s")} · {f}";
    }

    /// <summary>District-aware <see cref="FreshnessColor(double)"/>: a rich district's bags go late / stinky on its own
    /// windows (Meadows 3.5 h on time, the Bluffs 2 h...), so the HUD rows match the markers over those houses.</summary>
    public static Color FreshnessColor(DistrictDef d, double ageHours)
        => d == null || d == Districts.Starter ? FreshnessColor(ageHours) : CurbTrash.FreshnessColor(d, ageHours);

    /// <summary>District-aware <see cref="BagsAndFreshness(int, double, bool)"/> ("3 bags · 2 h late" on d's windows).</summary>
    public static string BagsAndFreshness(int bags, double oldestAge, DistrictDef d, bool color = true)
    {
        if (d == null || d == Districts.Starter) return BagsAndFreshness(bags, oldestAge, color);
        string f = Districts.IsNeglected(d, oldestAge) ? "STINKY!" : CurbTrash.AgeText(d, oldestAge);
        if (color) f = $"<color=#{FeelUi.Hex(FreshnessColor(d, oldestAge))}>{f}</color>";
        return $"{bags} bag{(bags == 1 ? "" : "s")} · {f}";
    }

    /// <summary>Lateness bucket from a cash reason ("On time!", "A bit late", "Late", "Super late", "Curb clean!").</summary>
    public static Lateness ParseLateness(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return Lateness.None;
        var r = reason.ToLowerInvariant();
        if (r.Contains("curb clean")) return Lateness.CurbClean;
        if (r.Contains("super late")) return Lateness.SuperLate;
        if (r.Contains("bit late")) return Lateness.BitLate;
        if (r.Contains("on time")) return Lateness.OnTime;
        if (r.Contains("late")) return Lateness.Late;
        return Lateness.None;
    }

    public static Color LatenessColor(Lateness l) => l switch
    {
        Lateness.OnTime => new Color(0.4f, 1f, 0.45f),
        Lateness.BitLate => new Color(1f, 0.88f, 0.25f),
        Lateness.Late => new Color(1f, 0.6f, 0.2f),
        Lateness.SuperLate => new Color(1f, 0.32f, 0.25f),
        Lateness.CurbClean => new Color(1f, 0.84f, 0.25f),
        _ => FeelUi.GoodGreen,
    };

    public static string LatenessText(Lateness l) => l switch
    {
        Lateness.OnTime => "On time!",
        Lateness.BitLate => "A bit late",
        Lateness.Late => "Late…",
        Lateness.SuperLate => "Super late…",
        Lateness.CurbClean => "Curb clean!",
        _ => "",
    };

    // ------------------------------------------------------------------ reflection binding

    enum Scale { Unknown, Unit, Percent }

    static bool probed;
    static MethodInfo plannedMi, plannedIntoMi;
    static object[] plannedArgs, plannedIntoArgs;
    static Func<double, Color> trashColor;
    static Func<double, string> trashAgeText;
    static Func<object, double> totalGetter, lateGetter;
    static Type cityType;
    static Func<double> cityGetter;
    static string cityMember;
    static Scale cityScale;
    static readonly HashSet<string> logged = new HashSet<string>();

    /// <summary>Which optional Trash members were found (for verification / debugging).</summary>
    public static string BindingReport
    {
        get
        {
            Probe();
            return $"PlannedRoute={(plannedIntoMi != null ? "bound(into)" : plannedMi != null ? "bound" : "fallback")}, " +
                   $"CurbTrash colors/labels={(trashColor != null ? "bound" : "fallback")}/{(trashAgeText != null ? "bound" : "fallback")}, TotalCurbBags={(totalGetter != null ? "bound" : "fallback")}, " +
                   $"LateBags={(lateGetter != null ? "bound" : "fallback")}, CityCleanliness={(cityGetter != null ? cityMember + "(" + cityScale + ")" : cityType != null ? "type-only→fallback" : "fallback")}";
        }
    }

    static void Probe()
    {
        if (probed) return;
        probed = true;
        try
        {
            var rt = typeof(TrashRoute);
            foreach (var m in rt.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != "PlannedRoute" || m.ContainsGenericParameters) continue;
                var ps = m.GetParameters();
                // non-alloc overload: PlannedRoute(Vector3 from, int n, List<PickupStop> into)
                if (ps.Length == 3 && ps[0].ParameterType == typeof(Vector3) && ps[1].ParameterType == typeof(int) &&
                    ps[2].ParameterType == typeof(List<PickupStop>))
                {
                    plannedIntoMi = m;
                    plannedIntoArgs = new object[3];
                }
            }
            foreach (var m in rt.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (m.Name != "PlannedRoute" || m.ContainsGenericParameters) continue;
                var ps = m.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(Vector3) || ps[1].ParameterType != typeof(int)) continue;
                if (!typeof(IEnumerable).IsAssignableFrom(m.ReturnType)) continue;
                bool ok = true;
                for (int i = 2; i < ps.Length; i++) if (!ps[i].IsOptional) ok = false;
                if (!ok) continue;
                plannedMi = m;
                plannedArgs = new object[ps.Length];
                for (int i = 2; i < ps.Length; i++) plannedArgs[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : Type.Missing;
                break;
            }
            totalGetter = BindNumber(rt, "TotalCurbBags");
            lateGetter = BindNumber(rt, "LateBags");

            cityType = rt.Assembly.GetType("CityCleanliness");
            if (cityType != null) BindCity(cityType);

            var curb = rt.Assembly.GetType("CurbTrash");
            if (curb != null)
            {
                const BindingFlags S = BindingFlags.Public | BindingFlags.Static;
                var fc = curb.GetMethod("FreshnessColor", S, null, new[] { typeof(double) }, null);
                if (fc != null && fc.ReturnType == typeof(Color))
                    trashColor = (Func<double, Color>)Delegate.CreateDelegate(typeof(Func<double, Color>), fc);
                var at = curb.GetMethod("AgeText", S, null, new[] { typeof(double) }, null);
                if (at != null && at.ReturnType == typeof(string))
                    trashAgeText = (Func<double, string>)Delegate.CreateDelegate(typeof(Func<double, string>), at);
            }
        }
        catch (Exception e) { LogOnce("probe", e); }
    }

    static void BindCity(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
        MemberInfo best = null;
        int bestScore = -1;
        foreach (var m in t.GetMembers(F))
        {
            Type type = m switch
            {
                PropertyInfo p when p.CanRead && p.GetIndexParameters().Length == 0 => p.PropertyType,
                FieldInfo f when !f.IsLiteral => f.FieldType,
                MethodInfo mi when !mi.IsSpecialName && !mi.ContainsGenericParameters && mi.GetParameters().Length == 0 => mi.ReturnType,
                _ => null
            };
            if (type == null || !IsNumeric(type)) continue;
            int s = ScoreCityName(m.Name);
            if (s > bestScore) { bestScore = s; best = m; }
        }
        if (best == null || bestScore <= 0) { LogOnce("CityCleanliness has no readable 0..1/0..100 value member; using the HUD fallback", null); return; }
        cityMember = best.Name;
        var lower = best.Name.ToLowerInvariant();
        cityScale = lower.Contains("01") ? Scale.Unit : (lower.Contains("percent") || lower.Contains("pct")) ? Scale.Percent : Scale.Unknown;
        cityGetter = best switch
        {
            PropertyInfo p => () => Convert.ToDouble(p.GetValue(null)),
            FieldInfo f => () => Convert.ToDouble(f.GetValue(null)),
            MethodInfo mi => () => Convert.ToDouble(mi.Invoke(null, null)),
            _ => null
        };
    }

    static int ScoreCityName(string name)
    {
        var l = name.ToLowerInvariant();
        foreach (var bad in new[] { "dirt", "stink", "pressure", "bonus", "max", "min", "hour", "weight", "count",
                                    "threshold", "radius", "litter", "bag", "rate", "chance", "spawn", "pay" })
            if (l.Contains(bad)) return -1;
        int s = 0;
        if (l.Contains("clean")) s += 4;
        if (l.Contains("percent") || l.Contains("pct")) s += 3;
        if (l.Contains("01")) s += 3;
        if (l == "value" || l == "current" || l == "score" || l == "level" || l == "now") s += 2;
        if (l.StartsWith("get") && s > 0) s -= 1;
        return s;
    }

    static Func<object, double> BindNumber(Type t, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        var p = t.GetProperty(name, F);
        if (p != null && p.CanRead && p.GetIndexParameters().Length == 0 && IsNumeric(p.PropertyType))
        {
            bool st = p.GetGetMethod() != null && p.GetGetMethod().IsStatic;
            return o => Convert.ToDouble(p.GetValue(st ? null : o));
        }
        var f = t.GetField(name, F);
        if (f != null && IsNumeric(f.FieldType)) return o => Convert.ToDouble(f.GetValue(f.IsStatic ? null : o));
        var m = t.GetMethod(name, F, null, Type.EmptyTypes, null);
        if (m != null && IsNumeric(m.ReturnType)) return o => Convert.ToDouble(m.Invoke(m.IsStatic ? null : o, null));
        return null;
    }

    static int ReadInt(Func<object, double> getter, TrashRoute route, int fallback)
    {
        if (getter == null || route == null) return fallback;
        try { return Mathf.Max(0, (int)Math.Round(getter(route))); }
        catch (Exception e)
        {
            if (getter == totalGetter) totalGetter = null; else if (getter == lateGetter) lateGetter = null;
            LogOnce("TrashRoute bag counts", e);
            return fallback;
        }
    }

    static bool IsNumeric(Type t) =>
        t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(decimal);

    static void Drop(ref MethodInfo mi, string what, Exception e)
    {
        mi = null;
        LogOnce(what, e);
    }

    static void LogOnce(string what, Exception e)
    {
        if (!logged.Add(what)) return;
        Debug.LogWarning($"[Feel] {what}{(e != null ? " failed, using HUD fallback: " + (e.InnerException ?? e).Message : "")}");
    }

    /// <summary>Forget cached bindings (tests / after a domain reload they re-probe anyway).</summary>
    public static void Reprobe()
    {
        probed = false;
        plannedMi = plannedIntoMi = null; plannedArgs = plannedIntoArgs = null; totalGetter = lateGetter = null;
        trashColor = null; trashAgeText = null;
        cityType = null; cityGetter = null; cityMember = null; cityScale = Scale.Unknown;
        snapFrame = -1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        snapFrame = -1;
        byStop.Clear();
        logged.Clear();
    }
}
