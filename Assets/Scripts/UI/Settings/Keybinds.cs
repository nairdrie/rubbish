using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The read-only keybinds cheat sheet (Docs/UIPass.md §6.3, D9). Keys are hard-coded <c>KeyCode</c>s in about 15 files today,
/// so this table holds the code's defaults; <see cref="Defaults"/> lists the serialized values an acceptance eval checks
/// against (Truck V2's hornKey, Player's dropKey/throwMouseButton, the side loader's armKey, the truck tipper's key, the
/// sort station's exitKey, IInteractable's keys). Rebinding needs every poll moved to Input System actions first (a follow-up).
/// </summary>
public static class Keybinds
{
    /// <summary>One line: the key(s) as key-cap text ("E", "LMB", "E/LMB", "Hold R", "W/S"), what it does, and an optional gate.</summary>
    public readonly struct Bind
    {
        public readonly string keys, action, gate;
        public Bind(string keys, string action, string gate = null) { this.keys = keys; this.action = action; this.gate = gate; }
    }

    /// <summary>A context: "On foot", "Truck", "Sort station", "Anywhere".</summary>
    public readonly struct Group
    {
        public readonly string title;
        public readonly Bind[] binds;
        public Group(string title, params Bind[] binds) { this.title = title; this.binds = binds; }
    }

    /// <summary>The defaults the table is written from (checked against the prefabs/scenes by an eval).</summary>
    public static class Defaults
    {
        public const KeyCode Interact = KeyCode.E, AltInteract = KeyCode.F;
        public const KeyCode Drop = KeyCode.G;
        public const KeyCode Punch = KeyCode.Mouse1, Flop = KeyCode.Z;
        public const int ThrowMouseButton = 0;
        public const KeyCode Chug = KeyCode.J;
        public const KeyCode Pole = KeyCode.V, Trap = KeyCode.T;
        public const KeyCode Horn = KeyCode.H;
        public const KeyCode Arm = KeyCode.X;
        public const KeyCode Recover = KeyCode.R;
        public const KeyCode TipIt = KeyCode.G;
        public const KeyCode StationExit = KeyCode.Q;
    }

    static Group[] groups;
    static Group[] controllerGroups;

    /// <summary>The cheat sheet, grouped by context.</summary>
    public static IReadOnlyList<Group> Groups => groups ??= Build();

    /// <summary>Xbox button names; PlayStation uses the same positions (A/Cross, B/Circle, X/Square, Y/Triangle).</summary>
    public static IReadOnlyList<Group> ControllerGroups => controllerGroups ??= BuildController();

    static Group[] BuildController() => new[]
    {
        new Group("On foot",
            new Bind("Left stick", "Move"),
            new Bind("Right stick", "Look around"),
            new Bind("A", "Jump / shake off raccoon"),
            new Bind("LT", "Sprint"),
            new Bind("X", "Grab / use / get in"),
            new Bind("X", "Pick up a downed player", "flop or knockout"),
            new Bind("Y", "Other nearby option"),
            new Bind("B", "Drop what you're holding"),
            new Bind("Hold RT", "Wind up and throw"),
            new Bind("RB", "Punch / item swing", "combo knocks down"),
            new Bind("D-pad ← / →", "Grabby Hand / trap", "if owned"),
            new Bind("LB", "Rocket boost", "Rocket Blades Lv 2"),
            new Bind("Hold Y", "Crush the bags on your back", "Compactor Backpack Lv 2"),
            new Bind("D-pad ↓ / ↑", "Juice / bag cannon", "if owned"),
            new Bind("R3", "Flop / get up", "allows pickup"),
            new Bind("A", "Mash to wriggle free", "while carried"),
            new Bind("L3", "Stomp", "Stompy Boots Lv 2")),
        new Group("Truck",
            new Bind("Left stick", "Steer"),
            new Bind("RT / LT", "Drive / brake & reverse"),
            new Bind("Right stick", "Look around"),
            new Bind("A", "Drift", "if upgraded"),
            new Bind("Y", "HONK"),
            new Bind("LB", "Side-loader arm", "if fitted"),
            new Bind("Hold D-pad ↑", "Tow or unflip"),
            new Bind("Hold B", "TIP IT: dump the load behind the truck", "stopped"),
            new Bind("X", "Get out")),
        new Group("Sorting table",
            new Bind("Right stick", "Aim at trash"),
            new Bind("RT", "Grab / slam open / sort"),
            new Bind("RB", "Throw the selected piece"),
            new Bind("B", "Step away")),
        new Group("Riding / menus / chess",
            new Bind("Left stick", "Lean into corners"),
            new Bind("A", "Hold on / hop off"),
            new Bind("RT / RB", "Snag bag / shove rider"),
            new Bind("Y", "Toggle Safety Bar", "if fitted"),
            new Bind("LB", "Toggle solo cruise", "offline only"),
            new Bind("View", "Map"),
            new Bind("Menu", "Pause / back"),
            new Bind("B", "Back", "in menus"),
            new Bind("LB / RB", "Previous / next tab"),
            new Bind("A", "Select", "in menus"),
            new Bind("D-pad + A", "Choose / move piece", "at chess"),
            new Bind("B", "Cancel / leave", "at chess")),
    };

    static Group[] Build() => new[]
    {
        new Group("On foot",
            new Bind("WASD", "Move"),
            new Bind("Mouse", "Look around"),
            new Bind("Space", "Jump"),
            new Bind("Shift", "Sprint"),
            new Bind(Cap(Defaults.Interact), "Grab, toss in, get in"),
            new Bind(Cap(Defaults.Interact), "Pick up a downed player", "flop or knockout"),
            new Bind(Cap(Defaults.AltInteract), "The other option"),
            new Bind(Cap(Defaults.Drop), "Drop what you're holding"),
            new Bind("Hold " + MouseCap(Defaults.ThrowMouseButton), "Wind up and chuck it"),
            new Bind(Cap(Defaults.Punch), "Punch / item swing", "combo knocks down"),
            new Bind(Cap(Defaults.Flop), "Flop / get up", "allows pickup"),
            new Bind("Space", "Mash to wriggle free", "while carried"),
            new Bind(Cap(Defaults.Chug), "Chug Garbage Juice", "if you have it"),
            new Bind(Cap(Defaults.Trap), "Set a raccoon trap", "if you have one"),
            new Bind("Y", "Feral pounce", "Fully Feral, while rabid"),
            new Bind("K K", "Use a stored Rabies Shot", "Embrace It: it no longer fires by itself"),
            new Bind(Cap(Defaults.Pole), "Use the Grabby Hand", "if you have one"),
            new Bind("Q", "Rocket boost", "Rocket Blades Lv 2"),
            new Bind("C", "Bag onto your back / hold to crush", "Compactor Backpack"),
            new Bind("B", "Eject the whole pack", "Compactor Backpack Lv 3"),
            new Bind("Space", "Shake off a raccoon (mash!)", "when one's on your face"),
            new Bind("Ctrl", "Stomp", "Stompy Boots Lv 2"),
            new Bind("N", "Call It (holding a bag)", "Scavenger capstone")),
        new Group("Truck",
            new Bind("W/S", "Drive and reverse"),
            new Bind("A/D", "Steer"),
            new Bind(Cap(Defaults.Horn), "HONK"),
            new Bind("Space", "Drift"),
            new Bind(Cap(Defaults.Arm), "Side-loader arm", "if fitted"),
            new Bind("Hold " + Cap(Defaults.Recover), "Tow or unflip"),
            new Bind("Hold " + Cap(Defaults.TipIt), "TIP IT: dump the load behind the truck", "stopped"),
            new Bind(Cap(Defaults.Interact), "Get out")),
        new Group("Sort station",
            new Bind(Cap(Defaults.Interact) + "/LMB", "Grab or slam it open"),
            new Bind("RMB", "Chuck it"),
            new Bind(Cap(Defaults.StationExit) + "/Esc", "Step away")),
        new Group("Anywhere",
            new Bind("Esc", "Menu / back"),
            new Bind("M", "Map"),
            new Bind("Hold Tab", "Peek at the map"),
            new Bind("F2", "Invite Steam friends"),
            new Bind("Shift+Tab", "Steam overlay")),
    };

    /// <summary>A KeyCode as key-cap text ("E", "Ctrl", "Space", "LMB").</summary>
    public static string Cap(KeyCode k) => k switch
    {
        KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
        KeyCode.LeftShift or KeyCode.RightShift => "Shift",
        KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
        KeyCode.Escape => "Esc",
        KeyCode.Return or KeyCode.KeypadEnter => "Enter",
        KeyCode.Space => "Space",
        KeyCode.Tab => "Tab",
        KeyCode.Mouse0 => "LMB",
        KeyCode.Mouse1 => "RMB",
        KeyCode.Mouse2 => "MMB",
        >= KeyCode.Alpha0 and <= KeyCode.Alpha9 => ((int)(k - KeyCode.Alpha0)).ToString(),
        _ => k.ToString(),
    };

    /// <summary>Mouse button index → "LMB" / "RMB" / "MMB".</summary>
    public static string MouseCap(int button) => button == 1 ? "RMB" : button == 2 ? "MMB" : "LMB";
}
