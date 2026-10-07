using System.IO;
using UnityEngine;
// coop_lint fixture: Core/GameSession*.cs is a legacy writer, exempt from `save` until M1.
public static partial class GameSession
{
    static void Go(string p) { File.Delete(p); var r = Application.persistentDataPath; }
}
