using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps the trash that is lying around in a scene when you leave it through a door (bags tipped onto the
/// shop's tipping floor, half-sorted items on the table, bits spilled from a burst bag at the curb) and puts
/// it back where it was when you return. Every door transition is an NGO Single load, which used to destroy
/// all of it — e.g. on a two-trip route the whole first load vanished the moment you drove out again.
///
/// SceneController calls <see cref="Capture"/> while the screen is black, right before the load. The restore
/// runs from a throwaway object's Start in the freshly loaded scene (after its load callbacks, so particles
/// aren't cleared by the scene change). In-memory only, like the truck's cargo.
/// Curb bags that still belong to a PickupStop are skipped: the stop re-spawns uncollected bags itself.
/// Bags riding the garage conveyor (the Bag Belt, or hopping into its queue rack: scripted carriers up in the air) come
/// back on the tipping floor instead (ConveyorFeeder.Rehome), so nothing drops from 3.7 m on the way back in.
/// </summary>
public class SceneTrashKeeper : MonoBehaviour
{
    struct Entry
    {
        public BagContents bag;      // set for a TrashBag
        public ItemInstance item;    // set for a LooseItem
        public bool revealed;
        public Vector3 position;
        public Quaternion rotation;
    }

    static readonly Dictionary<string, List<Entry>> saved = new Dictionary<string, List<Entry>>();

    /// <summary>How many things are waiting to be restored in <paramref name="sceneName"/>.</summary>
    public static int SavedCount(string sceneName) =>
        !string.IsNullOrEmpty(sceneName) && saved.TryGetValue(sceneName, out var l) ? l.Count : 0;

    /// <summary>Snapshot every loose TrashBag / LooseItem in <paramref name="scene"/> (replaces an older snapshot). Never throws.</summary>
    public static void Capture(Scene scene)
    {
        try
        {
            if (!scene.IsValid()) return;
            var list = new List<Entry>();
            int bags = 0, items = 0;
            foreach (var bag in FindObjectsByType<TrashBag>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (bag == null || bag.gameObject.scene != scene) continue;
                if (bag.State == CarryState.Consumed || bag.HasBurst || bag.Contents == null) continue;
                if (bag.SourceStop != null) continue;
                Vector3 pos = bag.transform.position;
                Quaternion rot = bag.transform.rotation;
                var home = ConveyorFeeder.Rehome(bag, bags);
                if (home.HasValue) { pos = home.Value; rot = Quaternion.Euler(0f, rot.eulerAngles.y, 0f); }
                list.Add(new Entry { bag = bag.Contents, position = pos, rotation = rot });
                bags++;
            }
            foreach (var li in FindObjectsByType<LooseItem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (li == null || li.gameObject.scene != scene) continue;
                if (li.State == CarryState.Consumed || li.Item == null) continue;
                list.Add(new Entry { item = li.Item, revealed = li.Revealed, position = li.transform.position, rotation = li.transform.rotation });
                items++;
            }
            if (list.Count == 0) { saved.Remove(scene.name); return; }
            saved[scene.name] = list;
            Debug.Log($"[SceneTrashKeeper] Kept {bags} bag(s) + {items} item(s) lying around in '{scene.name}'.");
        }
        catch (System.Exception e) { Debug.LogException(e); }
    }

    /// <summary>Drop a snapshot without restoring it (the scene never actually unloaded, e.g. a failed load).</summary>
    public static void Forget(string sceneName)
    {
        if (!string.IsNullOrEmpty(sceneName)) saved.Remove(sceneName);
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Application.isPlaying || SavedCount(scene.name) == 0) return;
        var go = new GameObject("[Restored Trash]");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<SceneTrashKeeper>();
    }

    void Start()
    {
        Restore(gameObject.scene);
        Destroy(gameObject);
    }

    /// <summary>Re-spawn (and forget) the snapshot for <paramref name="scene"/>. Returns how many things came back.</summary>
    public static int Restore(Scene scene)
    {
        int n = 0;
        if (saved.TryGetValue(scene.name, out var list))
        {
            saved.Remove(scene.name);
            foreach (var e in list)
            {
                try
                {
                    GameObject spawned = null;
                    if (e.bag != null)
                    {
                        var bag = TrashBag.Spawn(e.bag, e.position + Vector3.up * 0.02f, e.rotation);
                        if (bag != null) spawned = bag.gameObject;
                    }
                    else if (e.item != null)
                    {
                        var li = LooseItem.Spawn(e.item, e.position + Vector3.up * 0.02f, e.rotation, Vector3.zero, false);
                        if (li != null)
                        {
                            if (e.revealed) li.RestoreRevealed();
                            spawned = li.gameObject;
                        }
                    }
                    if (spawned == null) continue;
                    if (spawned.transform.parent == null && spawned.scene != scene) SceneManager.MoveGameObjectToScene(spawned, scene);
                    n++;
                }
                catch (System.Exception ex) { Debug.LogException(ex); }
            }
            Debug.Log($"[SceneTrashKeeper] Put back {n} thing(s) in '{scene.name}'.");
        }
        return n;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        saved.Clear();
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }
}
