using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Controls → Look sensitivity and Invert Y (Docs/UIPass.md §6.2). Applied as a <b>processor override</b> on the local
/// player's <c>PlayerInput</c> "Look" action: each binding's own processors are rewritten with its ScaleVector2 multiplied by
/// the sensitivity and its InvertVector2's Y flipped. Overrides are runtime-only (InputBinding's override fields are never
/// serialized), so the .inputactions asset is never edited. At 1.00× and not inverted the overrides are removed, so the
/// defaults are byte-for-byte the authored processors. The third-person camera and the sort station (when it reads
/// StarterAssetsInputs.look) both get it for free; the station's legacy-mouse fallback multiplies by the setting itself.
/// </summary>
public static class LookApplier
{
    const string LookAction = "Look";
    static PlayerInput applied;
    static float appliedSens = float.NaN;
    static bool appliedInvert;

    /// <summary>Bindings currently overridden (tests).</summary>
    public static int Overridden { get; private set; }

    /// <summary>Force a re-apply on the next tick (settings changed).</summary>
    public static void Refresh() => appliedSens = float.NaN;

    /// <summary>Called every frame by the settings applier: re-applies when the local player or the setting changed.</summary>
    public static void Tick()
    {
        var local = PlayerController.Local; // coop:allow(local) input only: the local player's own look action
        var pi = local != null ? local.GetComponent<PlayerInput>() : null;
        float s = GameSettings.LookSensitivity;
        bool inv = GameSettings.InvertY;
        if (pi == applied && Mathf.Approximately(s, appliedSens) && inv == appliedInvert) return;
        applied = pi;
        appliedSens = s;
        appliedInvert = inv;
        if (pi == null || pi.actions == null) { Overridden = 0; return; }
        Overridden = Apply(pi.actions.FindAction(LookAction), s, inv);
    }

    /// <summary>Rewrite the action's binding processors for <paramref name="sens"/> / <paramref name="invertY"/>. Returns the
    /// number of bindings overridden (0 = defaults).</summary>
    public static int Apply(InputAction look, float sens, bool invertY)
    {
        if (look == null) return 0;
        bool identity = Mathf.Approximately(sens, 1f) && !invertY;
        int n = 0;
        var bindings = look.bindings;
        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (b.isComposite) continue;
            if (identity)
            {
                if (!string.IsNullOrEmpty(b.overrideProcessors)) look.RemoveBindingOverride(i);
                continue;
            }
            string procs = Rewrite(b.processors, sens, invertY);
            look.ApplyBindingOverride(i, new InputBinding { overrideProcessors = procs });
            n++;
        }
        return n;
    }

    /// <summary>
    /// "InvertVector2(invertX=false),ScaleVector2(x=0.05,y=0.05)" at 2× inverted →
    /// "InvertVector2(invertX=false,invertY=false),ScaleVector2(x=0.1,y=0.1)". A binding without an invert gets one appended
    /// when inverting; one without a scale gets one appended when scaling.
    /// </summary>
    public static string Rewrite(string processors, float sens, bool invertY)
    {
        var sb = new StringBuilder();
        bool sawScale = false, sawInvert = false;
        string src = processors ?? "";
        int depth = 0, start = 0;
        for (int i = 0; i <= src.Length; i++)
        {
            char c = i < src.Length ? src[i] : ',';
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c != ',' || depth > 0) continue;
            string p = src.Substring(start, i - start).Trim();
            start = i + 1;
            if (p.Length == 0) continue;
            string name = p.IndexOf('(') >= 0 ? p.Substring(0, p.IndexOf('(')).Trim() : p;
            string args = p.IndexOf('(') >= 0 ? p.Substring(p.IndexOf('(') + 1).TrimEnd(')') : "";
            if (name == "ScaleVector2")
            {
                sawScale = true;
                float x = Arg(args, "x", 1f) * sens, y = Arg(args, "y", 1f) * sens;
                p = $"ScaleVector2(x={F(x)},y={F(y)})";
            }
            else if (name == "InvertVector2")
            {
                sawInvert = true;
                bool ix = ArgBool(args, "invertX", true), iy = ArgBool(args, "invertY", true);
                if (invertY) iy = !iy;
                p = $"InvertVector2(invertX={B(ix)},invertY={B(iy)})";
            }
            if (sb.Length > 0) sb.Append(',');
            sb.Append(p);
        }
        if (invertY && !sawInvert) { if (sb.Length > 0) sb.Append(','); sb.Append("InvertVector2(invertX=false,invertY=true)"); }
        if (!Mathf.Approximately(sens, 1f) && !sawScale) { if (sb.Length > 0) sb.Append(','); sb.Append($"ScaleVector2(x={F(sens)},y={F(sens)})"); }
        return sb.ToString();
    }

    static float Arg(string args, string key, float def)
    {
        foreach (var kv in args.Split(','))
        {
            int eq = kv.IndexOf('=');
            if (eq < 0 || kv.Substring(0, eq).Trim() != key) continue;
            if (float.TryParse(kv.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return v;
        }
        return def;
    }

    static bool ArgBool(string args, string key, bool def)
    {
        foreach (var kv in args.Split(','))
        {
            int eq = kv.IndexOf('=');
            if (eq < 0 || kv.Substring(0, eq).Trim() != key) continue;
            string v = kv.Substring(eq + 1).Trim();
            if (v == "true" || v == "True") return true;
            if (v == "false" || v == "False") return false;
        }
        return def;
    }

    static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    static string B(bool v) => v ? "true" : "false";

    /// <summary>Remove every override this applied (leaving play mode).</summary>
    public static void RestoreAll()
    {
        if (applied != null && applied.actions != null)
        {
            var look = applied.actions.FindAction(LookAction);
            if (look != null) look.RemoveAllBindingOverrides();
        }
        applied = null;
        appliedSens = float.NaN;
        Overridden = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        applied = null;
        appliedSens = float.NaN;
        appliedInvert = false;
        Overridden = 0;
    }
}
