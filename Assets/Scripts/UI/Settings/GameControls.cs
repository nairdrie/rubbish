using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Local, context-sensitive gamepad buttons alongside the existing keyboard defaults. Each client has one
/// local player; remote copies must never poll this class. The same face button can do different things at a
/// sorting station, on the truck, and on foot, just as E/Space already do on keyboard.
/// </summary>
public static class GameControls
{
    public static bool HasGamepad => Gamepad.current != null;
    public static bool UsingGamepad
    {
        get
        {
            var local = PlayerController.Local; // coop:allow(local) control prompts follow this machine's owned player only
            var pi = local != null ? local.GetComponent<PlayerInput>() : null;
            return pi != null ? pi.currentControlScheme == "Gamepad" : Gamepad.current != null && Keyboard.current == null;
        }
    }
    public enum Action
    {
        Interact, Alternative, Drop, Throw, Shove, Jump, Flop,
        Pole, Trap, Chug, Stomp, Cannon,
        Horn, Arm, Recover, Drift, TipIt,
        SortPrimary, SortSecondary, StationExit,
        Snag, SafetyBar, Cruise, Map, Back, Pause, PreviousTab, NextTab,
        Boost, Crush,
    }

    static ButtonControl Button(Action action)
    {
        var p = Gamepad.current;
        if (p == null) return null;
        return action switch
        {
            Action.Interact => p.buttonWest,       // X / Square
            Action.Alternative => p.buttonNorth,  // Y / Triangle
            Action.Drop or Action.StationExit or Action.Back or Action.TipIt => p.buttonEast, // B / Circle (TIP IT: HOLD B while driving)
            Action.Throw or Action.SortPrimary or Action.Snag => p.rightTrigger,
            Action.Cannon => p.dpad.up,
            Action.Shove or Action.SortSecondary => p.rightShoulder,
            Action.Jump or Action.Drift => p.buttonSouth, // A / Cross
            Action.Flop => p.rightStickButton,
            Action.Pole => p.dpad.left,
            Action.Trap => p.dpad.right,
            Action.Chug => p.dpad.down,
            Action.Stomp => p.leftStickButton,
            Action.Horn or Action.SafetyBar => p.buttonNorth,
            Action.Arm => p.leftShoulder,
            Action.Recover => p.dpad.up,
            Action.Cruise => p.leftShoulder,
            Action.Map => p.selectButton,
            Action.Pause => p.startButton,
            Action.PreviousTab => p.leftShoulder,
            Action.NextTab => p.rightShoulder,
            Action.Boost => p.leftShoulder,   // Rocket Blades (on foot LB is otherwise free)
            Action.Crush => p.buttonNorth,    // Compactor Backpack: HOLD Y
            _ => null,
        };
    }

    public static bool Down(KeyCode key, Action action) => Input.GetKeyDown(key) || (Button(action)?.wasPressedThisFrame ?? false);
    public static bool Held(KeyCode key, Action action) => Input.GetKey(key) || (Button(action)?.isPressed ?? false);
    public static bool Up(KeyCode key, Action action) => Input.GetKeyUp(key) || (Button(action)?.wasReleasedThisFrame ?? false);
    public static bool PadDown(Action action) => Button(action)?.wasPressedThisFrame ?? false;
    public static bool PadHeld(Action action) => Button(action)?.isPressed ?? false;

    public static bool MenuNavigationRequested
    {
        get
        {
            var p = Gamepad.current;
            return p != null && (p.dpad.up.wasPressedThisFrame || p.dpad.down.wasPressedThisFrame ||
                                 p.leftStick.ReadValue().sqrMagnitude > 0.25f || p.buttonSouth.wasPressedThisFrame);
        }
    }

    /// <summary>Keyboard axes remain usable with a connected idle pad; stick steers, triggers drive/reverse.</summary>
    public static Vector2 DriveAxes()
    {
        var result = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        var p = Gamepad.current;
        if (p == null) return result;
        float steer = p.leftStick.ReadValue().x;
        float throttle = p.rightTrigger.ReadValue() - p.leftTrigger.ReadValue();
        if (Mathf.Abs(steer) > Mathf.Abs(result.x)) result.x = steer;
        if (Mathf.Abs(throttle) > Mathf.Abs(result.y)) result.y = throttle;
        return result;
    }

    public static float RideLean => Gamepad.current != null ? Gamepad.current.leftStick.ReadValue().x : 0f;
}
