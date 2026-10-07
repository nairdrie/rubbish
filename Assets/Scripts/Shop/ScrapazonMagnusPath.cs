using System;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The Magnus department's career strip (Docs/Progression.md §6.3, <see cref="MagnusCareer"/>): "Step N of 9", every
/// step as a tile (done / next / buyable / locked), what Magnus can do right now, and the next step with its price and
/// exactly why it's locked when it is. Built in code (UI Toolkit) into a host <see cref="ScrapazonApp"/> owns, above the
/// product grid; styles "sz-mc-*" in Assets/UI/shop/scrapazon.uss. Read-only: buying goes through the product card
/// (<paramref name="showAndBuy"/> spotlights it and presses its button, so the usual confirm / lock / cash juice applies).
/// </summary>
public static class ScrapazonMagnusPath
{
    /// <summary>Changes whenever anything the strip shows changes (levels, rank, cash relative to the next price).</summary>
    public static string Signature()
    {
        var sb = new StringBuilder();
        foreach (var s in MagnusCareer.Steps) sb.Append(s.Done ? '1' : '0');
        sb.Append('|').Append(Progression.Rank);
        int next = MagnusCareer.NextIndex;
        if (next >= 0)
        {
            var st = MagnusCareer.Steps[next];
            float shortfall = Mathf.Max(0f, st.Price - GameSession.Cash);
            sb.Append('|').Append(Mathf.CeilToInt(shortfall));
        }
        // the other buyable steps' affordability (tile colour)
        foreach (var s in MagnusCareer.Steps) sb.Append(!s.Done && GameSession.Cash + 0.001f >= s.Price ? 'a' : '-');
        return sb.ToString();
    }

    public static void Build(VisualElement host, Action<string> showAndBuy, Action<string, string> toast)
    {
        if (host == null) return;
        host.Clear();
        var steps = MagnusCareer.Steps;
        int done = MagnusCareer.DoneCount;
        int nextIdx = MagnusCareer.NextIndex;

        // ---- header
        var head = V("sz-mc-head");
        head.Add(L("Magnus's Career", "sz-mc-heading"));
        head.Add(L(done >= steps.Length ? $"All {steps.Length} steps done" : done == 0 ? $"Step 1 of {steps.Length}: hire him!" : $"{done} of {steps.Length} steps done",
                   "sz-mc-count" + (done >= steps.Length ? " sz-mc-count--max" : "")));
        head.Add(L(MagnusCareer.ChessLine(), "sz-mc-chess"));
        host.Add(head);
        host.Add(L("One path, nine steps. Each one adds exactly one thing, and every step makes him better at chess.", "sz-mc-sub"));

        // ---- the path
        var track = V("sz-mc-track");
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            bool isDone = s.Done;
            bool can = !isDone && MagnusCareer.CanBuyStep(s, out _);
            bool afford = GameSession.Cash + 0.001f >= s.Price;
            string state = isDone ? "sz-mc-step--done" : i == nextIdx ? (can ? "sz-mc-step--next" : "sz-mc-step--next-locked")
                         : can ? "sz-mc-step--open" : "sz-mc-step--locked";
            var tile = V("sz-mc-step " + state + (can && afford ? " sz-mc-step--afford" : ""));
            tile.Add(L((i + 1).ToString(), "sz-mc-step-num"));
            tile.Add(L(s.name, "sz-mc-step-name"));
            tile.Add(L(isDone ? "DONE" : can ? Economy.Money(s.Price) : $"Rank {s.Rank}", "sz-mc-step-price"));
            var step = s;
            int n = i + 1;
            Click(tile, () =>
            {
                if (step.Done) { toast?.Invoke($"Step {n}: {step.name}. Magnus {step.adds}. (installed)", "info"); return; }
                // only press a card's buy button when that card would buy THIS step (not an earlier level of the part)
                if (MagnusCareer.CanBuyStep(step, out string why)) showAndBuy?.Invoke(step.id);
                else toast?.Invoke($"Step {n}: {step.name} (Magnus {step.adds}). Locked: {why}.", "bad");
            });
            track.Add(tile);
            if (i < steps.Length - 1) track.Add(L("→", "sz-mc-arrow" + (isDone ? " sz-mc-arrow--done" : "")));
        }
        host.Add(track);

        // ---- now / next
        var cols = V("sz-mc-cols");
        var now = V("sz-mc-box sz-mc-now");
        now.Add(L("Magnus can now:", "sz-mc-box-h"));
        foreach (var line in MagnusCareer.CanNow()) now.Add(L("- " + line, "sz-mc-line"));
        cols.Add(now);

        var next = V("sz-mc-box sz-mc-next");
        if (nextIdx < 0)
        {
            next.Add(L("Career complete", "sz-mc-box-h"));
            next.Add(L("He is MAGNUS PRIME. He sorts all night and plays chess like a god. He will mention it.", "sz-mc-line"));
        }
        else
        {
            var s = steps[nextIdx];
            next.Add(L($"Next: Step {nextIdx + 1} of {steps.Length}", "sz-mc-box-h"));
            var it = s.Item;
            next.Add(L(it != null ? it.TitleFor(s.level) : s.name, "sz-mc-next-title"));
            next.Add(L($"Magnus {s.adds}.", "sz-mc-line sz-mc-line--adds"));
            bool can = MagnusCareer.CanBuyStep(s, out string why);
            var row = V("sz-mc-next-row");
            row.Add(L(Economy.Money(s.Price), "sz-mc-next-price"));
            if (!can)
            {
                row.Add(L("LOCKED: " + (why ?? "not yet"), "sz-mc-why"));
            }
            else
            {
                bool afford = GameSession.Cash + 0.001f >= s.Price;
                if (!afford) row.Add(L($"{Economy.Money(s.Price - GameSession.Cash)} to go", "sz-mc-note"));
                var b = L(afford ? $"Buy step {nextIdx + 1}" : "Show me", "sz-mc-btn" + (afford ? " sz-mc-btn--afford" : ""));
                string id = s.id;
                Click(b, () => showAndBuy?.Invoke(id));
                row.Add(b);
            }
            next.Add(row);
            // a later step that's buyable right now (prerequisites allow a little reordering)
            for (int i = nextIdx + 1; i < steps.Length; i++)
            {
                if (steps[i].Done || !MagnusCareer.CanBuyStep(steps[i], out _)) continue;
                next.Add(L($"Also available: Step {i + 1}: {steps[i].name} ({Economy.Money(steps[i].Price)})", "sz-mc-also"));
                break;
            }
        }
        cols.Add(next);
        host.Add(cols);
    }

    // ------------------------------------------------------------------ helpers

    static void Click(VisualElement e, Action action)
    {
        if (e == null) return;
        e.AddManipulator(new Clickable(() =>
        {
            Sfx.Play("click", null, 0.5f);
            action();
        }));
    }

    static Label L(string text, string classes)
    {
        var l = new Label(text);
        AddClasses(l, classes);
        return l;
    }

    static VisualElement V(string classes)
    {
        var v = new VisualElement();
        AddClasses(v, classes);
        return v;
    }

    static void AddClasses(VisualElement e, string classes)
    {
        if (string.IsNullOrEmpty(classes)) return;
        foreach (var c in classes.Split(' '))
            if (c.Length > 0) e.AddToClassList(c);
    }
}
