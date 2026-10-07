using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Esc menu's Messages (Docs/UIPass.md §5.6): the last <see cref="Capacity"/> toasts of this session with their in-game day
/// and time, newest first. Never saved. Every <see cref="GameHUD.Toast"/> already logs "[Toast/Style] text", so the log listens
/// to Unity's log callback and needs no edit to the frozen Feel API; UP-1's toast lane (and UP-5's invite feed) can also call
/// <see cref="Add"/> directly. Identical texts within 10 s merge into one entry ("×3"). Invites carry a Join action: UP-5's
/// in-game invite toasts, then adds the same line with its [JOIN], which lands on the toast's own entry (one row, not two).
/// </summary>
public static class NotificationLog
{
    public const int Capacity = 30;
    const float MergeSeconds = 10f;

    public sealed class Entry
    {
        public string text;
        public ToastStyle style;
        public int day;
        public string time;
        public float realtime;
        public int repeats = 1;
        /// <summary>Invites (UP-5): [JOIN] in Messages runs this.</summary>
        public Action join;
    }

    static readonly List<Entry> entries = new List<Entry>(Capacity + 1); // oldest first
    static bool hooked;
    static int readCount;

    /// <summary>Messages this session (at most <see cref="Capacity"/>).</summary>
    public static int Count => entries.Count;

    /// <summary>Messages added since the log was last viewed.</summary>
    public static int Unread => Mathf.Max(0, added - readCount);

    static int added;

    /// <summary>Bumped on every add, merge or clear (views rebuild when it moves).</summary>
    public static int Version { get; private set; }

    /// <summary>A message was added (or merged).</summary>
    public static event Action Changed;

    /// <summary>Entry <paramref name="i"/>, newest first.</summary>
    public static Entry Newest(int i) => i >= 0 && i < entries.Count ? entries[entries.Count - 1 - i] : null;

    /// <summary>Add a message (any thread-safe caller on the main thread). Empty text is ignored.</summary>
    public static void Add(string text, ToastStyle style = ToastStyle.Info, Action join = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = text.Trim();
        float now = Time.realtimeSinceStartup;
        if (entries.Count > 0)
        {
            var last = entries[entries.Count - 1];
            if (last.text == text && last.join == null && now - last.realtime < MergeSeconds)
            {
                if (join != null)
                {
                    // the same line again with an action (an invite's toast was captured from the log a moment ago): give that
                    // entry its [JOIN] instead of listing the invite twice
                    last.join = join;
                    last.style = style;
                }
                else last.repeats++;
                last.realtime = now;
                Raise();
                return;
            }
        }
        var e = new Entry { text = text, style = style, join = join, realtime = now };
        Stamp(e);
        entries.Add(e);
        if (entries.Count > Capacity) entries.RemoveAt(0);
        added++;
        Raise();
    }

    /// <summary>The Messages page was looked at.</summary>
    public static void MarkRead() => readCount = added;

    /// <summary>Forget everything (a new session; tests).</summary>
    public static void Clear()
    {
        entries.Clear();
        added = readCount = 0;
        Raise();
    }

    static void Stamp(Entry e)
    {
        try
        {
            e.day = GameClock.Day;
            e.time = GameClock.ClockText;
        }
        catch (Exception)
        {
            e.day = 0;
            e.time = "";
        }
    }

    static void Raise()
    {
        Version++;
        try { Changed?.Invoke(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    // ------------------------------------------------------------------ toast capture

    const string Prefix = "[Toast/";

    static void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Log || message == null || !message.StartsWith(Prefix, StringComparison.Ordinal)) return;
        int close = message.IndexOf("] ", Prefix.Length, StringComparison.Ordinal);
        if (close < 0) return;
        string styleName = message.Substring(Prefix.Length, close - Prefix.Length);
        if (!Enum.TryParse(styleName, out ToastStyle style)) style = ToastStyle.Info;
        Add(message.Substring(close + 2), style);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (hooked || !Application.isPlaying) return;
        hooked = true;
        Application.logMessageReceived += OnLog;
        // A new hosted or joined game starts a fresh log (the log is per session, §5.6).
        NetSession.StateChanged += s =>
        {
            if (s == NetSessionState.Preparing || s == NetSessionState.Joining) Clear();
        };
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        if (hooked) Application.logMessageReceived -= OnLog;
        hooked = false;
        entries.Clear();
        added = readCount = 0;
        Version = 0;
        Changed = null;
    }
}
