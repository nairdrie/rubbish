if (!SaveRoot.IsOverridden || NetBoot.Manager == null || !NetBoot.Manager.IsServer || Businesses.All.Count != 2)
    throw new System.InvalidOperationException("Two-business isolated host required.");
if (Payroll.Jobs.Count != 0) throw new System.InvalidOperationException("Quit jobs before independent-business tests.");
var net = Businesses.Registry;
if (CurbTrashSpawner.Instance != null) CurbTrashSpawner.Instance.enabled = false;
var rows = new System.Collections.Generic.List<object>();
void Ages(float host, float guest)
{
    for(int i=0;i<net.List.Count;i++)
    {
        var b=net.List[i];b.municipalHours=b.IsHostBusiness?host:guest;net.List[i]=b;
        if(b.IsHostBusiness)MunicipalEconomy.SaveLocalHours(host);
    }
}
void Check(string name,float host,float guest,bool dirty)
{
    CurbTrashSpawner.DebugClearAll();
    if(dirty)CurbTrashSpawner.DebugPutOut(38,30);
    Ages(host,guest);
    var before=Businesses.All.ToDictionary(x=>x.Id,x=>x.cashCents);
    var entries=new System.Collections.Generic.List<object>();
    System.Action<LedgerEntry> onPost=e=>{if(e.id==LedgerIds.CleanStreets||e.id==LedgerIds.NeglectFine)
        entries.Add(new{e.businessId,e.id,e.cents,e.reason});};
    Ledger.PostedAnyBusiness+=onPost;
    MidnightReport report;
    try{report=CityCleanliness.MidnightInspection();}finally{Ledger.PostedAnyBusiness-=onPost;}
    rows.Add(new{name,report.finesPaid,pool=report.cleanBonusPaid.Values.Sum(),neglected=report.neglectedCurbs.Values.Sum(),
        businesses=Businesses.All.Select(b=>new{b.Id,b.IsHostBusiness,b.municipalHours,delta=b.cashCents-before[b.Id]}).ToArray(),entries});
}
Check("clean split",0,0,false);
Check("both grace",0,0,true);
Check("guest mature host grace",0,120,true);
Check("host mature guest grace",120,0,true);
Check("rookie caps",60,60,true);
string json=Newtonsoft.Json.JsonConvert.SerializeObject(rows,Newtonsoft.Json.Formatting.Indented);
System.IO.File.WriteAllText(System.IO.Path.Combine(SaveRoot.Path,"municipal-probe.json"),json);
return json;
