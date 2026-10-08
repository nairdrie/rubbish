// Runs the actual paint/line implementation, not synthetic mouse input. Use a clean town.
if (UnityEditor.EditorApplication.isPlaying || TownPlacementTools.Scene().isDirty || TownPlacementTools.Root().childCount != 0)
    throw new System.InvalidOperationException("Probe requires a clean town and empty authored layer.");
var window = UnityEngine.Resources.FindObjectsOfTypeAll<TownEditorWindow>().First();
string saved = UnityEditor.EditorJsonUtility.ToJson(window);
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var type = typeof(TownEditorWindow);
void Set(string name, object value) => type.GetField(name, flags).SetValue(window, value);
void Call(string name, params object[] args) => type.GetMethod(name, flags).Invoke(window, args);
var modeType = type.GetField("mode", flags).FieldType;
var output = new System.Collections.Generic.List<string>();
bool stroke = false;
try
{
    Set("activeId", "builtin/evergreen"); Set("mixIds", new System.Collections.Generic.List<string>());
    Set("scale", 1f); Set("radius", 8f); Set("spacing", 5f); Set("density", 4);
    Set("allowOverlap", false); Set("snapGrid", false);
    foreach (string mode in new[] { "Brush", "Line" })
    {
        Set("mode", System.Enum.Parse(modeType, mode));
        if (!TownPlacementTools.GroundAt(new UnityEngine.Vector3(135.94f, 0, 6.43f), out var point)) throw new System.Exception("Probe ground missing.");
        Call("StartStroke"); stroke = true;
        if (mode == "Brush") Call("Paint", point);
        else { Set("lineStart", point); Call("PaintLine", point + UnityEngine.Vector3.right * 20); }
        Call("EndStroke");
        int count = TownPlacementTools.Root().childCount;
        if (count < 2) throw new System.Exception(mode + " produced fewer than two trees: " + count);
        UnityEditor.Undo.PerformUndo(); stroke = false;
        if (TownPlacementTools.Root().childCount != 0) throw new System.Exception(mode + " did not undo as one stroke.");
        output.Add(mode + ": " + count + " trees; one Undo restored empty layer.");
    }
}
finally
{
    if (stroke) { Call("EndStroke"); UnityEditor.Undo.PerformUndo(); }
    UnityEditor.EditorJsonUtility.FromJsonOverwrite(saved, window); window.Repaint();
}
System.IO.File.WriteAllText("Logs/TownAuthoringQA/brush.txt", string.Join("\n", output));
return output;
