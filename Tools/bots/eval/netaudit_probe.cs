// Tools/bots/eval/netaudit_probe.cs: read-only. NetAudit counters, the last 64 events, and this process's hash compare totals.
//   unity command eval_file --file "$PWD/Tools/bots/eval/netaudit_probe.cs" --json
return "{\"compares\":" + NetAuditNet.Compares + ",\"mismatches\":" + NetAuditNet.Mismatches + ",\"snapshot\":" + NetAudit.SnapshotJson() + "}";
