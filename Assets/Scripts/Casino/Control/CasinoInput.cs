using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The seat-mode input map (Docs/Casino.md §12) and the one place that polls it: keyboard keys, the mouse wheel and the pad's
/// buttons. Every query reads the legacy <c>Input</c> class AND the Input System's <c>Keyboard</c> / <c>Mouse</c> / <c>Gamepad</c>
/// (the project runs both), so real hardware and Input System test devices (<c>InputSystem.QueueStateEvent</c>) behave the same.
/// A key is polled once per frame by its controller, so the OR of the two backends never double fires.
/// Local player only: nothing here is networked.
/// </summary>
public static class CasinoInput
{
    // ------------------------------------------------------------------ the map (also printed by Keybinds and the hint strip)

    /// <summary>Hold: lean in over the cards / the felt.</summary>
    public const KeyCode LeanKey = KeyCode.LeftShift;
    /// <summary>Hold: look at the dealer / the wheel / the pay table / the bartender.</summary>
    public const KeyCode WatchKey = KeyCode.V;
    public const KeyCode PrevChipKey = KeyCode.Q, NextChipKey = KeyCode.E;
    public const KeyCode UndoKey = KeyCode.Z, ClearKey = KeyCode.C, RebetKey = KeyCode.R, HintKey = KeyCode.H;
    public const KeyCode HitKey = KeyCode.Space, StandKey = KeyCode.S, DoubleKey = KeyCode.D, SplitKey = KeyCode.P;
    public const KeyCode InsuranceKey = KeyCode.I, NoInsuranceKey = KeyCode.N, SitOutKey = KeyCode.O;

    // ------------------------------------------------------------------ scripted keys (tools and play tests)

    static readonly Dictionary<KeyCode, int> injectedDown = new Dictionary<KeyCode, int>(), injectedUp = new Dictionary<KeyCode, int>();
    static readonly HashSet<KeyCode> injectedHeld = new HashSet<KeyCode>();

    /// <summary>
    /// Press or release a key from a tool or a play test, without any device: it reads like a real key to every controller (and to the seat
    /// camera's Esc). Call it before the controllers' <c>Update</c> of the frame (a pump with a negative execution order): the press is "down"
    /// for the rest of THAT frame, then held until released. Nothing in the game calls this.
    /// </summary>
    public static void Inject(KeyCode k, bool pressed)
    {
        if (pressed) { injectedHeld.Add(k); injectedDown[k] = Time.frameCount; }
        else if (injectedHeld.Remove(k)) injectedUp[k] = Time.frameCount;
    }

    public static void ClearInjected() { injectedHeld.Clear(); injectedDown.Clear(); injectedUp.Clear(); }

    // ------------------------------------------------------------------ keys

    public static bool Down(KeyCode k)
    {
        if (injectedDown.TryGetValue(k, out int f) && f == Time.frameCount) return true;
        if (Input.GetKeyDown(k)) return true;
        var kb = Keyboard.current;
        return kb != null && TryKey(k, out var key) && kb[key].wasPressedThisFrame;
    }

    public static bool Held(KeyCode k)
    {
        if (injectedHeld.Contains(k)) return true;
        if (Input.GetKey(k)) return true;
        var kb = Keyboard.current;
        return kb != null && TryKey(k, out var key) && kb[key].isPressed;
    }

    public static bool Up(KeyCode k)
    {
        if (injectedUp.TryGetValue(k, out int f) && f == Time.frameCount) return true;
        if (Input.GetKeyUp(k)) return true;
        var kb = Keyboard.current;
        return kb != null && TryKey(k, out var key) && kb[key].wasReleasedThisFrame;
    }

    /// <summary>Number row 1..9 pressed this frame: the digit (1..9) or 0.</summary>
    public static int DigitDown()
    {
        for (int i = 1; i <= 9; i++) if (Down(KeyCode.Alpha0 + i) || Down(KeyCode.Keypad0 + i)) return i;
        return 0;
    }

    public static bool EnterDown() => Down(KeyCode.Return) || Down(KeyCode.KeypadEnter);

    /// <summary>Mouse wheel notches this frame: +1 up / -1 down (0 none).</summary>
    public static int WheelStep()
    {
        float y = Input.mouseScrollDelta.y;
        var m = Mouse.current;
        if (Mathf.Abs(y) < 0.01f && m != null) y = m.scroll.ReadValue().y / 120f;
        return y > 0.01f ? 1 : y < -0.01f ? -1 : 0;
    }

    public static bool MouseDown(int button)
    {
        if (Input.GetMouseButtonDown(button)) return true;
        var m = Mouse.current;
        if (m == null) return false;
        var c = button == 0 ? m.leftButton : button == 1 ? m.rightButton : m.middleButton;
        return c.wasPressedThisFrame;
    }

    public static bool MouseHeld(int button)
    {
        if (Input.GetMouseButton(button)) return true;
        var m = Mouse.current;
        if (m == null) return false;
        var c = button == 0 ? m.leftButton : button == 1 ? m.rightButton : m.middleButton;
        return c.isPressed;
    }

    // ------------------------------------------------------------------ pad

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => ClearInjected();

    public static Gamepad Pad => Gamepad.current;
    public static bool PadDown(System.Func<Gamepad, UnityEngine.InputSystem.Controls.ButtonControl> pick)
    {
        var p = Pad; return p != null && pick(p).wasPressedThisFrame;
    }
    public static bool PadHeld(System.Func<Gamepad, UnityEngine.InputSystem.Controls.ButtonControl> pick)
    {
        var p = Pad; return p != null && pick(p).isPressed;
    }

    public static bool PadA => PadDown(p => p.buttonSouth);
    public static bool PadX => PadDown(p => p.buttonWest);
    public static bool PadY => PadDown(p => p.buttonNorth);
    public static bool PadLb => PadDown(p => p.leftShoulder);
    public static bool PadRb => PadDown(p => p.rightShoulder);
    public static bool PadDpadLeft => PadDown(p => p.dpad.left);
    public static bool PadDpadRight => PadDown(p => p.dpad.right);
    public static bool PadDpadDown => PadDown(p => p.dpad.down);
    public static bool PadL3 => PadDown(p => p.leftStickButton);
    public static bool PadLeftTriggerHeld { get { var p = Pad; return p != null && p.leftTrigger.ReadValue() > 0.5f; } }
    public static bool PadDpadUpHeld => PadHeld(p => p.dpad.up);

    /// <summary>Hold: lean (Shift / left trigger).</summary>
    public static bool LeanHeld => Held(LeanKey) || Held(KeyCode.RightShift) || PadLeftTriggerHeld;
    /// <summary>Hold: watch the dealer / wheel / pay table / bartender (V / D-pad up).</summary>
    public static bool WatchHeld => Held(WatchKey) || PadDpadUpHeld;

    /// <summary>Previous / next chip or drink: Q / E, the wheel, LB / RB, the D-pad. -1, 0 or +1.</summary>
    public static int CycleStep()
    {
        int s = 0;
        if (Down(PrevChipKey) || PadLb || PadDpadLeft) s--;
        if (Down(NextChipKey) || PadRb || PadDpadRight) s++;
        int w = WheelStep();
        if (w != 0) s += w > 0 ? 1 : -1; // wheel up = next chip
        return Mathf.Clamp(s, -1, 1);
    }

    public static bool UsingPad => GameControls.UsingGamepad;

    // ------------------------------------------------------------------ KeyCode -> Key

    static bool TryKey(KeyCode k, out Key key)
    {
        key = Key.None;
        if (k >= KeyCode.A && k <= KeyCode.Z) { key = Key.A + (k - KeyCode.A); return true; }
        if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) { key = k == KeyCode.Alpha0 ? Key.Digit0 : Key.Digit1 + (k - KeyCode.Alpha1); return true; }
        if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) { key = Key.Numpad0 + (k - KeyCode.Keypad0); return true; }
        switch (k)
        {
            case KeyCode.Space: key = Key.Space; return true;
            case KeyCode.Return: key = Key.Enter; return true;
            case KeyCode.KeypadEnter: key = Key.NumpadEnter; return true;
            case KeyCode.Backspace: key = Key.Backspace; return true;
            case KeyCode.Escape: key = Key.Escape; return true;
            case KeyCode.Tab: key = Key.Tab; return true;
            case KeyCode.LeftShift: key = Key.LeftShift; return true;
            case KeyCode.RightShift: key = Key.RightShift; return true;
            case KeyCode.LeftControl: key = Key.LeftCtrl; return true;
            case KeyCode.RightControl: key = Key.RightCtrl; return true;
            case KeyCode.UpArrow: key = Key.UpArrow; return true;
            case KeyCode.DownArrow: key = Key.DownArrow; return true;
            case KeyCode.LeftArrow: key = Key.LeftArrow; return true;
            case KeyCode.RightArrow: key = Key.RightArrow; return true;
        }
        return false;
    }
}
