using UnityEngine;

/// <summary>
/// The rider's screen (package F1, §14.1): the grip arc by the hands, lean arrows, the HOLD ON! prompt with a closing
/// ring, SAVED / MISSED, the AIR TIME card with your personal best, the STREAK card and the snag marker over a bag in
/// reach. Immediate-mode GUI like the tug HUD; draws nothing unless this machine's player rides, flies or just landed.
/// </summary>
public class RideHud : MonoBehaviour
{
    static GUIStyle style;
    static Texture2D dot;

    void OnGUI()
    {
        var r = RideRider.LocalRider;
        if (r == null || r.Player == null) return;
        var cam = Camera.main;
        float now = Time.unscaledTime;
        bool riding = r.TryGetRide(out var rt, out int hold);

        if (riding && cam != null)
        {
            Vector3 hand = RideHolds.Point(rt.transform, RideHolds.HandMetres(hold));
            Vector3 sp = cam.WorldToScreenPoint(hand);
            if (sp.z > 0f)
            {
                var c = new Vector2(sp.x, Screen.height - sp.y);
                float rad = Mathf.Clamp(Screen.height * 0.045f, 26f, 48f);
                DrawArc(c, rad, r.Grip / RideTuning.GripMax, r.DrainNow > 1f && Mathf.Repeat(now * 6f, 1f) < 0.5f);
                Label(new Rect(c.x - 40f, c.y - 9f, 80f, 18f), Mathf.CeilToInt(r.Grip).ToString(), 13, Color.white);
                Label(new Rect(c.x - 40f, c.y + rad + 2f, 80f, 16f), "GRIP", 11, new Color(1f, 1f, 1f, 0.8f));
                if (r.Lean != 0)
                    Label(new Rect(c.x + r.Lean * (rad + 30f) - 30f, c.y - 12f, 60f, 24f), r.Lean < 0 ? "◄◄" : "►►", 20, new Color(0.5f, 1f, 0.6f));
            }
            if (r.SnagCandidate != null)
            {
                Vector3 bp = cam.WorldToScreenPoint(r.SnagCandidate.WorldCenter + Vector3.up * 0.6f);
                if (bp.z > 0f)
                {
                    float pulse = 1f + 0.12f * Mathf.Sin(now * 10f);
                    Label(new Rect(bp.x - 70f, Screen.height - bp.y - 14f, 140f, 28f), GameControls.UsingGamepad ? "[RT] SNAG!" : "[LMB] SNAG!", Mathf.RoundToInt(16 * pulse), new Color(1f, 0.92f, 0.35f));
                }
            }
            if (rt.BarLevel > 0)
                Label(new Rect(Screen.width - 230f, Screen.height * 0.5f, 210f, 22f), r.BarDown ? (GameControls.UsingGamepad ? "SAFETY BAR: DOWN  [Y]" : "SAFETY BAR: DOWN  [B]") :
                      (GameControls.UsingGamepad ? "SAFETY BAR: UP  [Y]" : "SAFETY BAR: UP  [B]"), 13,
                      r.BarDown ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.5f, 0.3f), TextAnchor.MiddleRight);
        }

        if (r.Player.IsDriving && r.Player.currentTruck != null)
            DrawCrewGrip(r.Player.currentTruck);

        if (r.HoldOnActive)
        {
            float t = (Time.time - r.HoldOnStart) / RideTuning.HoldOnWindow;
            float w = 420f;
            var box = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.3f, w, 70f);
            bool flash = Mathf.Repeat(now * 8f, 1f) < 0.5f;
            Label(box, "HOLD ON!  [SPACE]", 40, flash ? new Color(1f, 0.3f, 0.2f) : Color.white);
            float rr = Mathf.Lerp(60f, 14f, Mathf.Clamp01(t));
            Ring(new Vector2(Screen.width * 0.5f, box.yMax + 30f), rr, new Color(1f, 0.85f, 0.3f));
            Ring(new Vector2(Screen.width * 0.5f, box.yMax + 30f), 14f, new Color(0.45f, 1f, 0.45f));
        }
        if (now < r.FeedbackUntil && !string.IsNullOrEmpty(r.Feedback))
            Label(new Rect(0f, Screen.height * 0.3f + 74f, Screen.width, 40f), r.Feedback, 28, r.FeedbackColor);

        float air = now - r.AirShownAt;
        if (air < 3.5f)
        {
            float pop = air < 0.2f ? Mathf.Lerp(0.6f, 1.15f, air / 0.2f) : Mathf.Lerp(1.15f, 1f, Mathf.Clamp01((air - 0.2f) / 0.2f));
            var box = new Rect(0f, Screen.height * 0.18f, Screen.width, 60f);
            Shadowed(box, $"AIR TIME  {r.LastAirM:0.0} m!", Mathf.RoundToInt(44 * pop), new Color(0.55f, 0.9f, 1f));
            string sub = r.LastAirBest ? "NEW PERSONAL BEST!" : $"{r.LastAirS:0.0} s in the air   ·   best {RideStats.BestAirM:0.0} m";
            Shadowed(new Rect(0f, box.yMax, Screen.width, 30f), sub, 20, r.LastAirBest ? new Color(1f, 0.85f, 0.2f) : Color.white);
        }

        float st = now - r.StreakShownAt;
        if (st < 1.8f && r.Streak > 0)
        {
            string s = r.Streak >= 2 ? $"STREAK x{r.Streak}!" : "SNAGGED!";
            if (r.StreakPay > 0f) s += $"   +${r.StreakPay:0.00}";
            Shadowed(new Rect(0f, Screen.height * 0.24f, Screen.width, 44f), s, r.Streak >= 2 ? 32 : 24, new Color(1f, 0.8f, 0.2f));
        }
    }

    static void DrawArc(Vector2 c, float rad, float fill, bool blink)
    {
        const int N = 36;
        const float a0 = 135f, span = 270f;
        int lit = Mathf.RoundToInt(Mathf.Clamp01(fill) * N);
        Color on = fill > 0.6f ? Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(0.35f, 1f, 0.45f), (fill - 0.6f) / 0.4f)
                              : Color.Lerp(new Color(1f, 0.25f, 0.2f), new Color(1f, 0.85f, 0.2f), fill / 0.6f);
        if (blink && fill < 0.35f) on = Color.white;
        for (int i = 0; i < N; i++)
        {
            float a = (a0 + span * (i + 0.5f) / N) * Mathf.Deg2Rad;
            var p = new Vector2(c.x + Mathf.Cos(a) * rad, c.y + Mathf.Sin(a) * rad);
            Fill(new Rect(p.x - 3.5f, p.y - 3.5f, 7f, 7f), i < lit ? on : new Color(0f, 0f, 0f, 0.45f));
        }
    }

    static void DrawCrewGrip(TruckControllerV2 truck)
    {
        var rt = RideTruck.For(truck);
        if (rt == null) return;
        float x = Screen.width - 234f, y = Screen.height * 0.17f;
        Label(new Rect(x, y, 218f, 22f), "CREW GRIP", 14, Color.white, TextAnchor.MiddleLeft);
        for (int hold = 0; hold < RideHolds.Count; hold++)
        {
            ulong client = RideRider.RiderAt(rt, hold);
            if (client == HandsNet.None) continue;
            float grip = RideRider.For(client)?.GripForHud ?? -1f;
            float row = y + 27f + hold * 26f;
            string side = RideHolds.Side(hold) < 0 ? "L" : "R";
            string name = RideHolds.IsRung(hold) ? side + " RUNG" : side + " STEP";
            Label(new Rect(x, row, 75f, 18f), name, 11, Color.white, TextAnchor.MiddleLeft);
            Fill(new Rect(x + 78f, row + 4f, 93f, 10f), new Color(0f, 0f, 0f, 0.55f));
            if (grip >= 0f)
            {
                Color c = grip > 60f ? new Color(0.35f, 1f, 0.45f) : grip > 25f ?
                    new Color(1f, 0.84f, 0.2f) : new Color(1f, 0.3f, 0.2f);
                Fill(new Rect(x + 78f, row + 4f, 93f * Mathf.Clamp01(grip / 100f), 10f), c);
            }
            Label(new Rect(x + 177f, row, 35f, 18f), grip >= 0f ? Mathf.CeilToInt(grip).ToString() : "…", 11,
                Color.white, TextAnchor.MiddleRight);
        }
    }

    static void Ring(Vector2 c, float rad, Color col)
    {
        const int N = 28;
        for (int i = 0; i < N; i++)
        {
            float a = i * Mathf.PI * 2f / N;
            Fill(new Rect(c.x + Mathf.Cos(a) * rad - 2f, c.y + Mathf.Sin(a) * rad - 2f, 4f, 4f), col);
        }
    }

    static void Fill(Rect r, Color c)
    {
        if (dot == null) dot = Texture2D.whiteTexture;
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, dot);
        GUI.color = old;
    }

    static void Shadowed(Rect r, string text, int size, Color c)
    {
        Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, size, new Color(0f, 0f, 0f, 0.7f));
        Label(r, text, size, c);
    }

    static void Label(Rect r, string text, int size, Color c, TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        style ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        style.fontSize = size;
        style.alignment = anchor;
        style.normal.textColor = c;
        GUI.Label(r, text, style);
    }
}
