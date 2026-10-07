// Tools/bots/eval/mppm_probe.cs: read-only probe for MPPM2 / play-mode co-op checks (Docs/Multiplayer.md §8.7.6, package B5).
//   unity command eval_file --file "$PWD/Tools/bots/eval/mppm_probe.cs" --json
// Returns one JSON object: this editor's autostart decision, bot phase, session, roster, players (owner, position, enabled
// PlayerInputs), hash compare totals (guests' totals as reported to the host), and NetAudit.SnapshotJson(). Changes nothing.
var nm = Unity.Netcode.NetworkManager.Singleton;
var sb = new System.Text.StringBuilder();
System.Func<string, string> q = s => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
sb.Append("{\"playMode\":").Append(UnityEngine.Application.isPlaying ? "true" : "false");
#if UNITY_EDITOR
sb.Append(",\"autostart\":").Append(q(CoopAutoStart.Decision));
sb.Append(",\"mainEditor\":").Append(Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor ? "true" : "false");
#endif
var bot = BotDriver.Instance;
sb.Append(",\"bot\":").Append(bot == null ? "null" : "{\"phase\":" + q(bot.Phase) + ",\"options\":" + q(BotDriver.Options?.ToString()) +
    ",\"viaNetSession\":" + (bot.ViaNetSession ? "true" : "false") + ",\"walked\":" + bot.Walked.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "}");
sb.Append(",\"saveRoot\":").Append(q(SaveRoot.Path)).Append(",\"saveRootOverridden\":").Append(SaveRoot.IsOverridden ? "true" : "false");
sb.Append(",\"saveForeign\":").Append(SaveGuard.Foreign ? "true" : "false");
sb.Append(",\"net\":");
if (nm == null || !nm.IsListening) sb.Append("null");
else
{
    sb.Append("{\"server\":").Append(nm.IsServer ? "true" : "false").Append(",\"client\":").Append(nm.LocalClientId)
      .Append(",\"connected\":").Append(nm.ConnectedClientsIds.Count).Append(",\"spawned\":").Append(nm.SpawnManager.SpawnedObjectsList.Count).Append('}');
}
sb.Append(",\"roster\":[");
var roster = NetRoster.Instance;
if (roster != null && roster.Entries != null)
    for (int i = 0; i < roster.Entries.Count; i++) sb.Append(i > 0 ? "," : "").Append(q(roster.Entries[i].ToString()));
sb.Append("],\"players\":[");
bool first = true;
foreach (var pc in UnityEngine.Object.FindObjectsByType<PlayerController>(UnityEngine.FindObjectsSortMode.None))
{
    var no = pc.GetComponent<Unity.Netcode.NetworkObject>();
    var pi = pc.GetComponent<UnityEngine.InputSystem.PlayerInput>();
    var p = pc.transform.position;
    sb.Append(first ? "" : ",").Append("{\"owner\":").Append(no != null ? no.OwnerClientId.ToString() : "null")
      .Append(",\"mine\":").Append(no != null && no.IsOwner ? "true" : "false")
      .Append(",\"pos\":").Append(q(p.ToString("F2")))
      .Append(",\"playerInput\":").Append(pi == null ? "null" : pi.enabled ? "\"enabled\"" : "\"disabled\"").Append('}');
    first = false;
}
sb.Append("],\"hash\":{\"compares\":").Append(NetAuditNet.Compares).Append(",\"matches\":").Append(NetAuditNet.Matches)
  .Append(",\"mismatches\":").Append(NetAuditNet.Mismatches).Append(",\"guests\":").Append(q(NetAuditNet.GuestTotalsText())).Append('}');
sb.Append(",\"netaudit\":").Append(NetAudit.SnapshotJson()).Append('}');
return sb.ToString();
