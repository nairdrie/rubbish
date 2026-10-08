// READ ONLY: exercise Town Builder's complete road-draft operation on a deep copy.
// unity command eval_file --file Tools/bots/eval/town_road_probe.cs
// No scene opening/saving, asset imports, cache replacement, physics, player saves or file writes.
// This checks the draft's shared-junction graph, not a newly published live StreetGraph or driving physics.
if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
    throw new System.InvalidOperationException("Run the read-only road probe in Edit Mode.");

string sourcePath = CoastalCityLayout.BlueprintPath;
string sourceText = System.IO.File.ReadAllText(sourcePath);
string sourceHash = UnityEngine.Hash128.Compute(sourceText).ToString();
var original = Newtonsoft.Json.JsonConvert.DeserializeObject<CoastalCityLayout.Blueprint>(sourceText);
string originalSerialized = Newtonsoft.Json.JsonConvert.SerializeObject(original);
var sceneBefore = new System.Collections.Generic.Dictionary<int, string>();
for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
{
    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
    sceneBefore[scene.handle] = scene.path + ":" + scene.isLoaded + ":" + scene.isDirty;
}
var errors = new System.Collections.Generic.List<string>();
var attempts = new System.Collections.Generic.List<string>();
CoastalCityLayout.Blueprint draft = null;
string parentId = null, addedId = "qa-read-only-street-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
UnityEngine.Vector3[] clicked = null;
foreach (var parent in original.roads.Where(r => !r.bridge && !r.curve && r.districtId == Districts.MildlyPleasant).OrderBy(r => r.id == "home" ? 0 : 1))
{
    if (draft != null) break;
    var road = CoastalCityLayout.ResolveRoad(original, parent.id);
    foreach (float station in new[] { .41381f, .23147f, .68123f })
    {
        if (draft != null) break;
        var pose = road.AtNormalized(station);
        foreach (int side in new[] { -1, 1 })
        {
            var route = new[] { pose.position, pose.position + pose.rotation * UnityEngine.Vector3.right * side * 24f };
            try
            {
                draft = TownRoadDraftOperations.AddRoad(original, new CoastalCityLayout.RoadSpec
                {
                    id = addedId, name = "READ ONLY QA street", districtId = parent.districtId,
                    halfWidth = 5f, curve = false, bridge = false, service = false
                }, route, 2f);
                parentId = parent.id; clicked = route; break;
            }
            catch (System.Exception ex) { attempts.Add(parent.id + " at " + station + " side " + side + ": " + ex.Message); }
        }
    }
}
if (draft == null) throw new System.InvalidOperationException("No valid copied-blueprint branch candidate was found:\n" + string.Join("\n", attempts));
errors.AddRange(TownRoadDraftOperations.Validate(draft));
var added = draft.roads.Single(r => r.id == addedId);
var parentAfter = draft.roads.Single(r => r.id == parentId);
string[] shared = added.nodes.Intersect(parentAfter.nodes).ToArray();
if (shared.Length == 0) errors.Add("Branch and parent do not use an actual shared junction identity.");

var adjacency = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(System.StringComparer.Ordinal);
foreach (var road in draft.roads)
{
    foreach (string node in road.nodes)
        if (!adjacency.ContainsKey(node)) adjacency[node] = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
    for (int i = 1; i < road.nodes.Length; i++)
    { adjacency[road.nodes[i - 1]].Add(road.nodes[i]); adjacency[road.nodes[i]].Add(road.nodes[i - 1]); }
}
var visited = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
var queue = new System.Collections.Generic.Queue<string>();
string start = draft.roads.Single(r => r.id == draft.depot.roadId).nodes[0];
visited.Add(start); queue.Enqueue(start);
while (queue.Count > 0)
    foreach (string next in adjacency[queue.Dequeue()]) if (visited.Add(next)) queue.Enqueue(next);
if (added.nodes.Any(n => !visited.Contains(n))) errors.Add("A drafted street junction is unreachable from the depot.");
if (visited.Count != adjacency.Count) errors.Add("The copied town's road graph is disconnected.");

float maximumAnchorDrift = 0, maximumFacingDrift = 0, maximumLengthDrift = 0;
foreach (var spec in original.roads)
{
    var before = CoastalCityLayout.ResolveRoad(original, spec.id);
    var after = CoastalCityLayout.ResolveRoad(draft, spec.id);
    maximumLengthDrift = UnityEngine.Mathf.Max(maximumLengthDrift, UnityEngine.Mathf.Abs(before.Length - after.Length));
    for (int i = 0; i <= 30; i++)
    {
        var a = before.AtNormalized(i / 30f); var b = after.AtNormalized(i / 30f);
        maximumAnchorDrift = UnityEngine.Mathf.Max(maximumAnchorDrift, UnityEngine.Vector3.Distance(a.position, b.position));
        maximumFacingDrift = UnityEngine.Mathf.Max(maximumFacingDrift, UnityEngine.Quaternion.Angle(a.rotation, b.rotation));
    }
}
if (maximumAnchorDrift > .001f || maximumLengthDrift > .001f || maximumFacingDrift > .05f)
    errors.Add("Adding a branch unexpectedly moved an existing road-relative property pose.");
if (!original.roads.Select(r => r.id).All(id => draft.roads.Any(r => r.id == id))) errors.Add("An existing road identity was lost.");
if (Newtonsoft.Json.JsonConvert.SerializeObject(original.collectionSites) != Newtonsoft.Json.JsonConvert.SerializeObject(draft.collectionSites)) errors.Add("Generated collection-site identities changed.");
if (Newtonsoft.Json.JsonConvert.SerializeObject(original.gates) != Newtonsoft.Json.JsonConvert.SerializeObject(draft.gates)) errors.Add("Existing permit gates changed.");
bool originalObjectUnchanged = Newtonsoft.Json.JsonConvert.SerializeObject(original) == originalSerialized;
bool diskUnchanged = System.IO.File.ReadAllText(sourcePath) == sourceText;
bool scenesUnchanged = UnityEngine.SceneManagement.SceneManager.sceneCount == sceneBefore.Count;
for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
{
    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
    scenesUnchanged &= sceneBefore.TryGetValue(scene.handle, out string state) && state == scene.path + ":" + scene.isLoaded + ":" + scene.isDirty;
}
if (!originalObjectUnchanged || !diskUnchanged || !scenesUnchanged) errors.Add("The read-only contract was violated.");
return Newtonsoft.Json.JsonConvert.SerializeObject(new
{
    passed = errors.Count == 0, readOnly = true, sourceHash,
    originalObjectUnchanged, diskUnchanged, scenesUnchanged,
    originalRoads = original.roads.Length, draftedRoads = draft.roads.Length,
    addedRoad = addedId, parentRoad = parentId, district = added.districtId,
    requestedPoints = clicked.Select(p => new[] { p.x, p.y, p.z }).ToArray(),
    sharedJunctions = shared, reachableJunctions = visited.Count, totalJunctions = adjacency.Count,
    maximumAnchorDriftMetres = maximumAnchorDrift, maximumFacingDriftDegrees = maximumFacingDrift,
    maximumRoadLengthDriftMetres = maximumLengthDrift,
    skippedCandidateCount = attempts.Count, errors,
    scope = "Pure draft creation, connected shared-junction graph, existing property-pose/identity preservation and no writes. Not a physical clearance, live bake or multiplayer driving test."
}, Newtonsoft.Json.Formatting.Indented);
