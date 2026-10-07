using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The day/night clock. One in-game day = DayLengthSeconds of real time (20 min), running while the server
/// runs. Driven by NGO's synchronized ServerTime, so every client agrees on the time of day with no extra
/// network objects; falls back to local time in menus / offline. The world never resets on a new day —
/// the day number is just a counter (GameSession.Day) and an event.
/// <para><b>One clock per Town (Docs/Multiplayer.md §13, N1).</b> The host's clock is the Town's: when a hosted session starts
/// (solo included) the time of day is restored from the host's save (<see cref="GameSession.SavedClockHours"/>; a fresh save
/// starts at 7 AM) and the day continues from <see cref="GameSession.Day"/>. Guests take the host's offset and base day
/// (<see cref="ApplyNetClock"/>, replicated by <c>WorldState</c>), so every machine shows the same time, day and bag ages, and a
/// guest's debug skips do nothing.</para>
/// FROZEN CONTRACT: members below are used by lighting, trash spawning, critters, HUD and rabies.
/// </summary>
public static class GameClock
{
    public const float DayLengthSeconds = 1200f;   // 20 real minutes per in-game day
    public const float StartHour = 7f;             // a fresh server starts at 7:00 AM

    // Phase boundaries (in-game hours)
    public const float DawnStart = 5f;             // sky starts to brighten
    public const float DayStart = 6.5f;            // full daylight
    public const float DuskStart = 19f;            // sun going down
    public const float NightStart = 20.5f;         // full night (raccoons out)

    public static float SecondsPerGameHour => DayLengthSeconds / 24f;

    static double debugOffsetSeconds;
    static double localStart = -1;
    static int baseDay = 1;
    static bool netClock, sessionClockSet;
    static double netOffsetSeconds;

    /// <summary>Real seconds the clock has run (server time when networked).</summary>
    public static double ElapsedSeconds
    {
        get
        {
            double t = held ? heldAtRaw : RawSeconds();
            return Math.Max(0, t + (netClock ? netOffsetSeconds : debugOffsetSeconds));
        }
    }

    /// <summary>The offset (seconds) the host adds to server time: restored time of day, debug skips, solo pauses.</summary>
    public static double OffsetSeconds => netClock ? netOffsetSeconds : debugOffsetSeconds;

    /// <summary>The day number at <see cref="DayIndex"/> 0.</summary>
    public static int BaseDay => baseDay;

    /// <summary>This machine follows the host's clock (a guest).</summary>
    public static bool IsNetClock => netClock;

    /// <summary>This machine runs the Town's clock: hosting (or solo) and the session's start time is applied.</summary>
    public static bool IsHostClock
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return sessionClockSet && !netClock && nm != null && nm.IsListening && nm.IsServer;
        }
    }

    /// <summary>Guest: follow the host's clock (WorldState calls this whenever the host's values change).</summary>
    public static void ApplyNetClock(double offsetSeconds, int hostBaseDay)
    {
        netClock = true;
        netOffsetSeconds = offsetSeconds;
        baseDay = Mathf.Max(1, hostBaseDay);
    }

    /// <summary>Guest left the Town: back to this machine's own clock (the next session sets it up again).</summary>
    public static void ClearNetClock()
    {
        netClock = false;
        netOffsetSeconds = 0;
    }

    /// <summary>
    /// Host: on the first call of a hosted session, put the clock at the saved time of day and continue the saved day. Called
    /// by the ticker and by CurbTrashSpawner before it starts (so the restored hour never counts as time to catch up).
    /// </summary>
    public static void EnsureSessionClock()
    {
        var nm = NetworkManager.Singleton;
        bool hosting = nm != null && nm.IsListening && nm.IsServer;
        if (!hosting) { sessionClockSet = false; return; }
        if (sessionClockSet || netClock) return;
        sessionClockSet = true;
        held = false;
        float saved = GameSession.SavedClockHours;
        double startHours = saved >= 0f && saved < 24f ? Mathf.Repeat(saved - StartHour, 24f) : 0.0;
        debugOffsetSeconds = startHours * SecondsPerGameHour - RawSeconds();
        baseDay = Mathf.Max(1, GameSession.Day - DayIndex);
    }

    static double RawSeconds()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) return nm.ServerTime.Time;
        if (localStart < 0) localStart = Time.unscaledTimeAsDouble;
        return Time.unscaledTimeAsDouble - localStart;
    }

    // ---------------- solo pause hold (Docs/UIPass.md §5.4, §10.6; added by UP-3) ----------------
    static bool held;
    static double heldAtRaw;

    /// <summary>The clock is held by the solo pause: <see cref="ElapsedSeconds"/> stands still until released.</summary>
    public static bool Held => held;

    /// <summary>
    /// Hold or release the clock for the solo pause (UI/Pause/PauseService). While held, <see cref="ElapsedSeconds"/> (so
    /// <see cref="TotalHours"/>, bag ages and the day) stands still; on release the held time is folded into the existing
    /// offset, so the clock carries on from where it stopped. Holding is refused while remote clients are connected or on a
    /// guest (a shared world never stops). Returns whether the clock is held after the call.
    /// </summary>
    public static bool SetHeld(bool hold)
    {
        if (hold == held) return held;
        if (hold)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening && (!nm.IsServer || nm.ConnectedClientsIds.Count > 1)) return false;
            heldAtRaw = RawSeconds();
            held = true;
        }
        else
        {
            debugOffsetSeconds -= RawSeconds() - heldAtRaw;
            held = false;
        }
        return held;
    }

    /// <summary>Monotonic in-game hours since the clock started (7.0 at start; 31.0 = 7 AM next day).</summary>
    public static double TotalHours => StartHour + ElapsedSeconds / DayLengthSeconds * 24.0;

    /// <summary>Hour of day, 0..24.</summary>
    public static float Hours => (float)(TotalHours % 24.0);

    /// <summary>Days completed since the clock started (0 on the first day).</summary>
    public static int DayIndex => (int)Math.Floor(TotalHours / 24.0);

    /// <summary>Displayed day number (continues the saved day count).</summary>
    public static int Day => baseDay + DayIndex;

    public static bool IsNight => Hours >= NightStart || Hours < DawnStart;

    /// <summary>0 at night, 1 in full daylight, smooth ramps at dawn and dusk.</summary>
    public static float Daylight01
    {
        get
        {
            float h = Hours;
            if (h >= DayStart && h <= DuskStart) return 1f;
            if (h >= NightStart || h < DawnStart) return 0f;
            if (h < DayStart) return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(DawnStart, DayStart, h));
            return Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(DuskStart, NightStart, h));
        }
    }

    /// <summary>1 - Daylight01.</summary>
    public static float Night01 => 1f - Daylight01;

    /// <summary>Age in in-game hours of something stamped at totalHours.</summary>
    public static double AgeHours(double stampedAtTotalHours) => Math.Max(0, TotalHours - stampedAtTotalHours);

    /// <summary>"7:42 AM"</summary>
    public static string ClockText => FormatHours(Hours);

    public static string FormatHours(float hours)
    {
        hours = Mathf.Repeat(hours, 24f);
        int h = Mathf.FloorToInt(hours);
        int m = Mathf.FloorToInt((hours - h) * 60f);
        int h12 = h % 12 == 0 ? 12 : h % 12;
        return $"{h12}:{m:00} {(h < 12 ? "AM" : "PM")}";
    }

    // ---------------- events (fired by the ticker on the main thread) ----------------
    /// <summary>Whole hour ticked over (arg = hour 0..23).</summary>
    public static event Action<int> HourChanged;
    /// <summary>Night began (NightStart crossed).</summary>
    public static event Action NightFell;
    /// <summary>Morning came (DayStart crossed).</summary>
    public static event Action MorningCame;

    /// <summary>Debug: jump the clock forward (e.g. to test night). Hours can be fractional.</summary>
    public static void DebugSkipHours(float hours)
    {
        debugOffsetSeconds += hours * SecondsPerGameHour;
    }

    /// <summary>Debug: set the time of day (keeps the day number).</summary>
    public static void DebugSetHour(float hour)
    {
        float delta = Mathf.Repeat(hour - Hours, 24f);
        DebugSkipHours(delta);
    }

    // ---------------- ticker ----------------
    class Ticker : MonoBehaviour
    {
        int lastHour = -1;
        bool lastNight;
        int lastDayIndex = -1;
        bool wasListening;

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool listening = nm != null && nm.IsListening;
            if (listening && !wasListening)
            {
                // A (new) session started: the host continues its saved day and time of day; a guest follows the host.
                if (nm.IsServer) EnsureSessionClock();
                lastHour = -1; lastDayIndex = -1;
            }
            else if (!listening && wasListening)
            {
                sessionClockSet = false;
                ClearNetClock();
            }
            wasListening = listening;

            float h = Hours;
            int hour = Mathf.FloorToInt(h);
            bool night = IsNight;
            int dayIndex = DayIndex;

            if (lastHour < 0) { lastHour = hour; lastNight = night; lastDayIndex = dayIndex; return; }

            if (hour != lastHour)
            {
                lastHour = hour;
                Safe(() => HourChanged?.Invoke(hour));
            }
            if (night != lastNight)
            {
                lastNight = night;
                if (night) Safe(() => NightFell?.Invoke());
            }
            bool fullDay = h >= DayStart && h < DuskStart;
            if (fullDay && lastWasNotDay) Safe(() => MorningCame?.Invoke());
            lastWasNotDay = !fullDay;

            if (dayIndex != lastDayIndex)
            {
                lastDayIndex = dayIndex;
                if (listening) GameSession.SetDayFromClock(baseDay + dayIndex);
            }
        }
        bool lastWasNotDay;

        static void Safe(Action a)
        {
            try { a(); } catch (Exception e) { Debug.LogException(e); }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        HourChanged = null; NightFell = null; MorningCame = null;
        debugOffsetSeconds = 0; localStart = -1; baseDay = 1;
        held = false; heldAtRaw = 0;
        netClock = sessionClockSet = false; netOffsetSeconds = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[GameClock]");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<Ticker>();
    }
}
