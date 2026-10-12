using UnityEngine;

/// <summary>
/// The rider's screen (package F1, §14.1): the snag marker over a bag in reach, the Safety Bar's position, the feedback line
/// (bar flips, a crash the bar soaked up, "No free spot!"), the AIR TIME card with your personal best and the STREAK card.
/// There is no grip meter: riders stay on unless the truck crashes. Immediate-mode GUI like the tug HUD; draws nothing unless
/// this machine's player rides, flies or just landed.
/// </summary>
public class RideHud : MonoBehaviour
{
    static GUIStyle style;

    void OnGUI()
    {
        var r = RideRider.LocalRider;
        if (r == null || r.Player == null) return;
        var cam = Camera.main;
        float now = Time.unscaledTime;
        bool riding = r.TryGetRide(out var rt, out _);

        if (riding && cam != null)
        {
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
