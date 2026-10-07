using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// WorldLife package E setup (Docs/WorldLife.md §8, §9, §12.1). E needs no assets and no scene changes (the authority, the
/// sensor and the director boot at runtime), so the umbrella's Rules step (<see cref="Run"/>) only runs
/// <see cref="SelfTest"/>: the whole decide → present → consequence pipeline on fake bodies with in-memory books
/// (<see cref="MemoryBooks"/>), so it never touches a save, the cash, the Rep or the inbox.
/// <list type="bullet">
/// <item><b>Rubbish/WorldLife/Rules Self Test</b> runs it.</item>
/// <item><b>Rubbish/WorldLife/Neighbor Fines: Toggle</b> flips the Town toggle (play mode, host: fines ↔ receipt shame only).</item>
/// <item><see cref="Kit"/> holds the play-test helpers the acceptance evals use (a real resident standing still, the
/// horn, a ledger log).</item>
/// </list>
/// </summary>
public static class Setup_WorldLifeRules
{
    [MenuItem("Rubbish/Setup/WorldLife Rules")]
    public static void RunMenu() => Run();

    public static string Run()
    {
        string report = "[Setup_WorldLifeRules] no assets or scene changes needed\n" + SelfTest();
        if (report.Contains("FAIL")) Debug.LogWarning(report); else Debug.Log(report);
        return report;
    }

    [MenuItem("Rubbish/WorldLife/Rules Self Test")]
    public static void SelfTestMenu() => Debug.Log("[WorldLife Rules]\n" + SelfTest());

    [MenuItem("Rubbish/WorldLife/Neighbor Fines: Toggle (play mode)")]
    public static void ToggleFines()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[WorldLife Rules] The fines toggle lives in the company save: enter play mode (host) first."); return; }
        bool on = !NeighborConsequences.FinesOn;
        if (!NeighborConsequences.SetFines(on)) { Debug.LogWarning("[WorldLife Rules] Guests can't change Town rules."); return; }
        GameHUD.Toast(on ? "Neighbour fines: ON" : "Neighbour fines: receipt shame only", ToastStyle.Info, 2.5f);
        Debug.Log("[WorldLife Rules] Neighbour fines " + (on ? "on" : "off (shame only)"));
    }

    // ================================================================================================= self test

    /// <summary>A body for the self test: always hittable, records what it was told to play.</summary>
    public sealed class FakeBody : ILifeBody
    {
        public LifeId id; public LifeBodyKind kind; public Vector3 pos; public float yaw; public string stop = "";
        public bool hittable = true; public Vector3 he = new Vector3(0.3f, 0.85f, 0.3f);
        public readonly List<NpcHit> played = new List<NpcHit>();
        public LifeId Id => id;
        public LifeBodyKind BodyKind => kind;
        public Vector3 Position => pos;
        public Quaternion Rotation => Quaternion.Euler(0f, yaw, 0f);
        public Vector3 HalfExtents => he;
        public bool Hittable => hittable;
        public string StopId => stop;
        public void PlayHit(in NpcHit hit) => played.Add(hit);
        public NpcHitKind Last => played.Count > 0 ? played[played.Count - 1].kind : NpcHitKind.None;
    }

    static int pass, fail;
    static StringBuilder log;

    static void Check(bool ok, string what)
    {
        if (ok) pass++; else fail++;
        log.AppendLine((ok ? "  ok   " : "  FAIL ") + what);
    }

    static Vector3 PointIn(byte district)
    {
        var all = Districts.All;
        var d = all[district];
        Vector3 c = new Vector3(d.boundsXZ.center.x, 0f, d.boundsXZ.center.y);
        if (Districts.AtPosition(c) == d) return c;
        // The starter district has no bounds of its own: anywhere outside the others.
        for (int x = -50; x <= 450; x += 25)
        for (int z = -30; z <= 630; z += 25)
        {
            var p = new Vector3(x, 0f, z);
            if (Districts.AtPosition(p) == d) return p;
        }
        return c;
    }

    public static string SelfTest()
    {
        if (Application.isPlaying) return "Rules self test: run it in edit mode (it swaps the books and clears LifeBodies)\n";
        pass = fail = 0;
        log = new StringBuilder();
        var books = new MemoryBooks { hours = 7.5, realSeconds = 1000.0 };
        var prevBooks = RulesBooks.Active == (IRulesBooks)RulesBooks.Game ? null : RulesBooks.Active;
        var bodies = new List<FakeBody>();
        try
        {
            RulesBooks.Active = books;
            LifeBodies.Clear();
            NeighborConsequences.ResetStats();
            NightNoise.ResetForTests();
            NeighborLetters.ResetPopupLimit();
            var A = WorldLifeAuthority.Instance;
            A.Forget(); A.ResetStats();

            // ---- 1. the §9.1 table and escalation
            float[] flat = { 3f, 12f, 40f, 120f }, shunt = { 5f, 20f, 60f, 200f };
            bool table = true;
            for (byte d = 0; d < 4; d++)
            {
                table &= Mathf.Approximately(NeighborConsequences.Price(NpcHitKind.Flatten, d, 1, true).charged, flat[d]);
                table &= Mathf.Approximately(NeighborConsequences.Price(NpcHitKind.Shunt, d, 1, true).charged, shunt[d]);
            }
            Check(table, "fines: flattened $3 / $12 / $40 / $120, car shunted $5 / $20 / $60 / $200");
            var esc = new StringBuilder();
            bool escOk = true;
            float[] want = { 1f, 1.25f, 1.5f, 1.75f, 2f, 2.25f, 2.5f, 2.75f, 3f, 3f, 3f };
            for (int n = 1; n <= 11; n++)
            {
                var b = NeighborConsequences.Price(NpcHitKind.Flatten, 0, n, true);
                escOk &= Mathf.Approximately(b.mult, want[n - 1]);
                esc.Append(n == 1 ? "" : " ").Append(b.charged.ToString("0.##"));
            }
            Check(escOk, $"escalation ×min(3, 1 + 0.25(n−1)), Mildly Pleasant n = 1..11: ${esc}");
            var off = NeighborConsequences.Price(NpcHitKind.Flatten, 3, 1, false);
            Check(off.waived && off.charged == 0f, "fines off (Town toggle): waived, $0 charged");

            // ---- 2. a pedestrian in Mildly Pleasant: speeds, the flatten, the books
            Vector3 p0 = PointIn(0);
            var gary = Body(bodies, LifeId.Resident(4100), LifeBodyKind.Person, p0, "maple-14");
            Vector3 truckVel = new Vector3(0f, 0f, 6f);
            var slow = Probe(A, Claim(gary.id, 0.5f, 10, 0), gary, books);
            Check(slow.kind == NpcHitKind.None, $"0.5 m/s: nothing ({slow.kind})");
            var nudge = Probe(A, Claim(gary.id, 1.5f, 11, 0), gary, books);
            Check(nudge.kind == NpcHitKind.Nudge && Mathf.Abs(nudge.launch.magnitude - WorldLifeTuning.Hits.NudgeDistance) < 0.01f,
                $"1.5 m/s: nudge 0.6 m aside ({nudge.kind}, {nudge.launch.magnitude:F2} m)");
            var hit = A.ClaimNpcHit(Claim(gary.id, 6f, 12, 0));
            Vector3 wantLaunch = WorldLifeTuning.Hits.FlopLaunch(truckVel, hit.launch.x >= 0f ? Vector3.right : Vector3.left);
            Check(hit.kind == NpcHitKind.Flatten && gary.Last == NpcHitKind.Flatten && (hit.launch - wantLaunch).magnitude < 0.01f,
                $"6 m/s: flattened, presented, launch {hit.launch:F2} (= §8.1 {wantLaunch:F2}), delay {hit.delayCs} cs");
            Check(books.charges.Count == 1 && books.charges[0].ledgerId == LedgerIds.NeighborDamages && Mathf.Approximately(books.charges[0].amount, 3f)
                  && books.charges[0].districtId == Districts.MildlyPleasant,
                $"books: one neighbor_damages ${(books.charges.Count > 0 ? books.charges[0].amount : 0):0.00} in {(books.charges.Count > 0 ? books.charges[0].districtId : "-")} ('{(books.charges.Count > 0 ? books.charges[0].reason : "")}')");
            Check(books.IncidentCount(NeighborIncidentKind.Flattened) == 1 && books.incidents[0].stopId == "maple-14",
                $"Rep fact: Flattened for maple-14 (Progression applies −4 × the district multiplier)");
            Check(NeighborGrudge.Has("maple-14", books) && Math.Abs(books.GetStamp(NeighborGrudge.Key("maple-14")) - 24.0) < 1e-6 && !NeighborGrudge.Has("maple-16", books),
                $"grudge on maple-14 until midnight (24.0 h), none next door");
            Check(books.letters.Count == 1 && books.letters[0].popup && books.letters[0].body.Contains("flattened"),
                $"letter: '{(books.letters.Count > 0 ? books.letters[0].subject : "-")}' (pop-up) {(books.letters.Count > 0 ? books.letters[0].body.Replace('\n', ' ') : "")}");
            int ov = 0; foreach (var o in LifeOverrides.All) if (o.id == gary.id) { ov++; }
            Check(ov == 1 && Mathf.Approximately(LifeOverrides.DelaySeconds(gary.id, RulesBooks.DayOf(books.hours)), 10f), $"timetable override: +10 s delay for {gary.id}");

            // ---- 3. immunity: 12 s for anyone, 20 s for the same driver
            books.realSeconds += 5;
            var again = A.ClaimNpcHit(Claim(gary.id, 6f, 300, 0));
            Check(again.kind == NpcHitKind.Dodge && books.charges.Count == 1 && books.incidents.Count == 1,
                $"+5 s, same driver: {again.kind} (\"missed me!\"), no second fine or Rep");
            books.realSeconds += 8;   // 13 s
            var other = A.ClaimNpcHit(Claim(gary.id, 6f, 700, 1));
            Check(other.kind == NpcHitKind.Flatten && books.charges.Count == 2 && Mathf.Approximately(books.charges[1].amount, 3.75f),
                $"+13 s, another driver: {other.kind}, 2nd flatten today ×1.25 = ${(books.charges.Count > 1 ? books.charges[1].amount : 0):0.00}");
            books.realSeconds += 13;  // 26 s after the first; 13 s after the second
            var same = A.ClaimNpcHit(Claim(gary.id, 6f, 1300, 0));
            Check(same.kind == NpcHitKind.Flatten && books.charges.Count == 3, $"+26 s, the first driver again: {same.kind} (×{NeighborConsequences.LastBill.mult:0.##})");

            // ---- 4. the grudge as pay
            float extra = NeighborGrudge.ExtraHours("maple-14"), none = NeighborGrudge.ExtraHours("maple-16");
            var dd = Districts.Starter;
            float age = dd.onTimeHours + 0.5f;
            float payGrudge = dd.fee * Districts.PayFraction(dd, age + extra), payNormal = Districts.PickupPay(dd, age);
            Check(Mathf.Approximately(extra, 1f) && none == 0f && payGrudge < payNormal,
                $"grudge pay hook: maple-14 +{extra:0.#} h, maple-16 +{none:0.#} h; a {age:0.0} h bag pays ${payGrudge:0.00} instead of ${payNormal:0.00} (and no tip)");
            books.hours = 24.01;
            Check(!NeighborGrudge.Has("maple-14", books), "grudge gone at midnight (24.01 h)");
            books.hours = 7.5;
            books.SetStamp(NeighborGrudge.Key("maple-20"), 7.5 + 30.0);
            Check(!NeighborGrudge.Has("maple-20", books), "a stamp 30 h ahead (a restarted clock) is stale and ignored");
            Check(NeighborGrudge.Clear("maple-14", books) && !NeighborGrudge.Has("maple-14", books), "clearing the curb forgives (Clear)");

            // ---- 5. dogs always leap; a real near miss costs Rep −1, once per 20 s
            var biscuit = Body(bodies, LifeId.Dog(3), LifeBodyKind.Dog, p0 + Vector3.right * 4f, "maple-14");
            int inc0 = books.incidents.Count;
            var dl = A.ClaimNpcHit(Claim(biscuit.id, 1.2f, 1400, 0));
            var dn = A.ClaimNpcHit(Claim(biscuit.id, 5f, 1401, 0));
            var dn2 = A.ClaimNpcHit(Claim(biscuit.id, 5f, 1402, 0));
            Check(dl.kind == NpcHitKind.DogLeap && dn.kind == NpcHitKind.DogLeap && dn2.kind == NpcHitKind.DogLeap
                  && books.IncidentCount(NeighborIncidentKind.DogNearMiss) == 1 && books.incidents.Count == inc0 + 1 && books.charges.Count == 3,
                "dog: 1.2 m/s leap (free), 5 m/s leap + DogNearMiss (Rep −1, no fine), again at once: leap, no Rep");

            // ---- 6. cars: a wall below 2.5 m/s, a shunt above, immune 10 s
            Vector3 p1 = PointIn(1);
            var van = Body(bodies, LifeId.Car(7), LifeBodyKind.Car, p1, "mm-juicebox-200");
            van.he = new Vector3(0.95f, 0.75f, 2.2f);
            var c1 = A.ClaimNpcHit(Claim(van.id, 2f, 1500, 0));
            var c2 = A.ClaimNpcHit(Claim(van.id, 7f, 1501, 0));
            var c3 = A.ClaimNpcHit(Claim(van.id, 7f, 1502, 0));
            float meadowsShunt = books.charges.Count > 3 ? books.charges[3].amount : -1f;
            Check(c1.kind == NpcHitKind.None && c2.kind == NpcHitKind.Shunt && c3.kind == NpcHitKind.None && Mathf.Approximately(meadowsShunt, 20f)
                  && books.IncidentCount(NeighborIncidentKind.CarShunt) == 1 && NeighborGrudge.Has("mm-juicebox-200", books)
                  && c2.launch.magnitude <= WorldLifeTuning.Hits.ShuntSlideMax + 1e-3f,
                $"car: 2 m/s nothing (a wall), 7 m/s shunt ${meadowsShunt:0} in the Meadows + Rep −3 + grudge, slide {c2.launch.magnitude:F2} m; again at once: {c3.kind} (10 s immunity)");
            books.realSeconds += 10.5;
            var c4 = A.ClaimNpcHit(Claim(van.id, 7f, 2100, 0));
            Check(c4.kind == NpcHitKind.Shunt && books.charges.Count == 5 && Mathf.Approximately(books.charges[4].amount, 25f),
                $"car after 10.5 s: shunt again, 2nd incident in the Meadows today ×1.25 = ${(books.charges.Count > 4 ? books.charges[4].amount : 0):0}");

            // ---- 7. fines off: shame only, the rest still applies
            books.finesOn = false;
            Vector3 p3 = PointIn(3);
            var butler = Body(bodies, LifeId.Resident(4200), LifeBodyKind.Person, p3, "bb-estate-1");
            int charges0 = books.charges.Count;
            var bh = A.ClaimNpcHit(Claim(butler.id, 8f, 2200, 0));
            Check(bh.kind == NpcHitKind.Flatten && books.charges.Count == charges0 && NeighborConsequences.LastBill.waived
                  && books.IncidentCount(NeighborIncidentKind.Flattened) == 4 && NeighborGrudge.Has("bb-estate-1", books),
                "fines off: a Bluffs flatten charges nothing (shame only) but still books Rep, a grudge and a letter");
            books.finesOn = true;
            var lastLetter = books.letters[books.letters.Count - 1];
            Check(lastLetter.subject.Contains("Dewey") && lastLetter.body.Contains("waived"), $"Bluffs letter: '{lastLetter.subject}', mentions the waiver");

            // ---- 8. letters pop up at most once per in-game hour
            int pops = 0; foreach (var l in books.letters) if (l.popup) pops++;
            Check(pops == 1 && books.letters.Count >= 5, $"letters: {books.letters.Count} sent within the hour, {pops} popped up (the rest went to the inbox)");

            // ---- 9. City Works: capped per district per day, one invoice at midnight
            books.hours = 30.0;   // day 1, 06:00
            int day1 = RulesBooks.DayOf(books.hours);
            for (int i = 0; i < 30; i++) CityWorksInvoice.Tally(Fact(KnockKind.Lamp, 0), books);
            CityWorksInvoice.Tally(Fact(KnockKind.Hydrant, 3), books);
            CityWorksInvoice.Tally(Fact(KnockKind.Cone, 2), books);
            float t0 = CityWorksInvoice.Today(0, books), t3 = CityWorksInvoice.Today(3, books), t2 = CityWorksInvoice.Today(2, books);
            Check(Mathf.Approximately(t0, 5f) && Mathf.Approximately(t3, 60f) && Mathf.Approximately(t2, 5f),
                $"City Works tally day {day1}: 30 lamps in Mildly Pleasant ${t0:0.00} (cap $5), a Bluffs hydrant ${t3:0.00} ($15 × 4), a Snootington cone ${t2:0.00}");
            int letters0 = books.letters.Count, charges1 = books.charges.Count;
            int none0 = CityWorksInvoice.BillDue(books);
            books.hours = 48.02;  // midnight
            int billed = CityWorksInvoice.BillDue(books);
            float cw = 0f; int cwRows = 0;
            for (int i = charges1; i < books.charges.Count; i++) if (books.charges[i].ledgerId == LedgerIds.CityWorks) { cw += books.charges[i].amount; cwRows++; }
            int again2 = CityWorksInvoice.BillDue(books);
            Check(none0 == 0 && billed == 3 && cwRows == 3 && Mathf.Approximately(cw, 70f) && again2 == 0 && books.letters.Count == letters0 + 3,
                $"midnight: {billed} invoices (city_works ${cw:0.00} = 5 + 60 + 5, each ≤ its cap), 3 letters, nothing billed twice");

            // ---- 10. night honks: ≤ 3 complaints a night per district, one letter in the morning
            var g = StreetGraph.BuildTest();
            try
            {
                Vector3 door = g.nodes[g.houses[0].door].pos;
                books.hours = 48.0 + 23.0;   // 23:00
                int ok = 0;
                for (int i = 0; i < 5; i++) if (NightNoise.Check(books, g, door + Vector3.forward * 12f)) ok++;
                books.hours = 48.0 + 15.0;   // 15:00: not quiet hours
                bool dayHonk = NightNoise.Check(books, g, door);
                Check(ok == 3 && NightNoise.Capped == 2 && !dayHonk && books.IncidentCount(NeighborIncidentKind.NoiseComplaint) == 3,
                    $"night honks 12 m from a door at 23:00: 5 honks → {ok} complaints ({NightNoise.Capped} over the cap); a 15:00 honk: none");
                books.hours = 72.0 + 2.0;    // 02:00, still before the morning
                bool early = NightNoise.MaybeSendMorningLetter(books);
                books.hours = 72.0 + 6.5;
                int l0 = books.letters.Count;
                bool sent = NightNoise.MaybeSendMorningLetter(books);
                Check(!early && sent && books.letters.Count == l0 + 1 && books.letters[books.letters.Count - 1].body.Contains("(3 complaints)"),
                    $"morning letter at 06:30: '{books.letters[books.letters.Count - 1].body.Replace('\n', ' ')}'");
                Vector3 far = door + Vector3.forward * 40f;
                books.hours = 72.0 + 23.0;
                Check(!NightNoise.Check(books, g, far + Vector3.right * 2000f), "a night honk 30+ m from every house: no complaint");
            }
            finally { UnityEngine.Object.DestroyImmediate(g); }

            // ---- 11. the horn's geometry (§8.5): radius 12, measured from the truck's body box (fixer pass; the box is
            // ±1.3 m wide and ±3.0 m long about its centre, so "12 m past the nose" is 15 m from the centre)
            Vector3 fwd = Vector3.forward;
            var hb = new Vector2(TruckControllerV2.BodyBoxMetres.extents.x, TruckControllerV2.BodyBoxMetres.extents.z);
            float nose = hb.y, rear = -hb.y;
            bool horn =
                HonkReactions.Classify(LifeBodyKind.Person, new Vector3(2f, 0f, nose + 11f), fwd, fwd, 12f, false, hb) == HornReaction.Startle &&
                HonkReactions.Classify(LifeBodyKind.Person, new Vector3(0f, 0f, nose + 13f), fwd, fwd, 12f, false, hb) == HornReaction.None &&
                HonkReactions.Classify(LifeBodyKind.Person, new Vector3(0f, 0f, rear - 10f), fwd, fwd, 12f, false, hb) == HornReaction.None &&
                HonkReactions.Classify(LifeBodyKind.Person, new Vector3(0.5f, 0f, rear - 5f), fwd, fwd, 12f, false, hb) == HornReaction.Startle &&
                HonkReactions.Classify(LifeBodyKind.Person, new Vector3(hb.x + 5f, 0f, -1f), fwd, fwd, 12f, false, hb) == HornReaction.Startle &&
                HonkReactions.Classify(LifeBodyKind.Car, new Vector3(0.5f, 0f, nose + 10f), fwd, fwd, 12f, true, hb) == HornReaction.PullOver &&
                HonkReactions.Classify(LifeBodyKind.Car, new Vector3(-5f, 0f, nose + 10f), fwd, -fwd, 12f, true, hb) == HornReaction.HonkBack &&
                HonkReactions.Classify(LifeBodyKind.Car, new Vector3(0.5f, 0f, nose + 10f), fwd, fwd, 12f, false, hb) == HornReaction.None &&
                HonkReactions.Classify(LifeBodyKind.Dog, new Vector3(0f, 0f, rear - 11f), fwd, fwd, 12f, false, hb) == HornReaction.Bark &&
                // the old centre-point geometry (no box) still holds
                HonkReactions.Classify(LifeBodyKind.Person, new Vector3(0f, 0f, 13f), fwd, fwd, 12f, false) == HornReaction.None;
            Check(horn, "horn (from the truck's box): person 11 m past the nose startles, 13 m past doesn't; 10 m behind the rear doesn't, 5 m behind and 5 m beside do; car 10 m ahead in lane pulls over, oncoming honks back, parked ignores; dog 11 m behind barks");

            // ---- 12. thrown things and bumps (§8.4)
            Vector3 p2 = PointIn(0) + Vector3.right * 9f;
            var linda = Body(bodies, LifeId.Resident(4300), LifeBodyKind.Person, p2, "maple-22");
            var t4 = A.ClaimThrownHit(linda, new Vector3(4f, 0f, 0f), 0, 3000);
            books.realSeconds += 4.1;
            var t8 = A.ClaimThrownHit(linda, new Vector3(8f, 1f, 0f), 0, 3200);
            var tx = A.ClaimThrownHit(linda, new Vector3(13f, 1f, 0f), 0, 3201);
            books.realSeconds += 4.1;
            int thrown0 = books.IncidentCount(NeighborIncidentKind.ThrownFlop);
            var t13 = A.ClaimThrownHit(linda, new Vector3(13f, 1f, 0f), 0, 3400);
            var t13b = A.ClaimThrownHit(linda, new Vector3(13f, 1f, 0f), 1, 3401);
            Check(t4.kind == NpcHitKind.Boop && t8.kind == NpcHitKind.Stumble && tx.kind == NpcHitKind.None && t13.kind == NpcHitKind.ShortFlop
                  && books.IncidentCount(NeighborIncidentKind.ThrownFlop) == thrown0 + 2 && t13b.kind == NpcHitKind.ShortFlop && t13.launch.y > 3f,
                $"thrown: 4 m/s boop, 8 m/s stumble, again inside 4 s nothing, 13 m/s short flop + Rep −1 (another thrower too); no fines");
            var w = A.ClaimBump(linda, linda.pos - Vector3.right * 0.7f, new Vector3(2f, 0f, 0f), 0, 4000);
            books.realSeconds += 1.1;
            var sp = A.ClaimBump(linda, linda.pos - Vector3.right * 0.7f, new Vector3(5.3f, 0f, 0f), 0, 4100);
            var sp2 = A.ClaimBump(linda, linda.pos - Vector3.right * 0.7f, new Vector3(5.3f, 0f, 0f), 0, 4101);
            Check(w.target == linda.id && w.kind == NpcHitKind.None && sp.kind == NpcHitKind.Stumble && Mathf.Abs(sp.launch.magnitude - 0.5f) < 0.01f && sp2.target != linda.id,
                "bump: walking 2 m/s = \"pardon me\" (no act), sprinting 5.3 m/s = stumble 0.5 m back, again inside 1 s nothing");

            // ---- 13. hand-overs and guests
            int nb0 = books.IncidentCount(NeighborIncidentKind.Neighborly);
            A.ClaimHandOver(new HandOverFact { resident = linda.id, bagId = "maple-22/x", stopId = "maple-22", pos = p2 });
            Check(books.IncidentCount(NeighborIncidentKind.Neighborly) == nb0 + 1, "hand-over: Neighborly fact (+1 Rep; Progression caps 10 a day)");
            books.host = false;
            var guestHit = Body(bodies, LifeId.Resident(4400), LifeBodyKind.Person, p0 + Vector3.left * 20f, "maple-30");
            int inc1 = books.incidents.Count, ch1 = books.charges.Count;
            var gh = A.ClaimNpcHit(Claim(guestHit.id, 6f, 5000, 2));
            Check(gh.kind == NpcHitKind.Flatten && guestHit.Last == NpcHitKind.Flatten && books.incidents.Count == inc1 && books.charges.Count == ch1 && !NeighborGrudge.Has("maple-30", books),
                "a guest's own hit (no F yet): the flop plays locally, nothing is booked (rules 15, 16)");
            books.host = true;

            // ---- 13b. the sensor's own body wins when two share an id (D's car while it leaves or reaches its driveway)
            var parked = Body(bodies, LifeId.Car(90), LifeBodyKind.Car, PointIn(0) + Vector3.forward * 30f, "maple-40");
            var shell = Body(bodies, LifeId.Car(90), LifeBodyKind.Car, PointIn(0) + Vector3.forward * 31f, "maple-40");
            var seenHit = A.ClaimNpcHit(Claim(parked.id, 7f, 6000, 0), parked);
            Check(ReferenceEquals(LifeBodies.Find(parked.id), shell) && seenHit.kind == NpcHitKind.Shunt && parked.Last == NpcHitKind.Shunt && shell.played.Count == 0,
                "two bodies share Car#90 (Find returns the newer): the claim with the body the sensor saw shunts that one, not the other");

            // ---- 14. allocations: the steady-state paths (empty sweeps; a decision on an immune target)
            var sweep = new TruckSweep
            {
                center = p0 + new Vector3(60f, 1.86f, 60f), rotation = Quaternion.identity, halfExtents = new Vector3(1.3f, 1.45f, 3.3f),
                velocity = truckVel, speed = 6f, forwardSpeed = 6f, tick = 9000, fixedDeltaTime = 0.02f,
            };
            for (int i = 0; i < 50; i++) { sweep.tick++; TruckLifeSensor.Instance.Sense(in sweep); }
            long sweepBytes = AllocBytes(() => { for (int i = 0; i < 2000; i++) { sweep.tick++; TruckLifeSensor.Instance.Sense(in sweep); } }, out bool cal);
            var immuneClaim = Claim(gary.id, 6f, 9500, 0);
            for (int i = 0; i < 10; i++) A.Decide(in immuneClaim, gary, books, out _, out _);
            long decideBytes = AllocBytes(() => { for (int i = 0; i < 2000; i++) A.Decide(in immuneClaim, gary, books, out _, out _); }, out _);
            Check(sweepBytes == 0 && decideBytes == 0,
                $"allocations: 2,000 empty sweeps {sweepBytes} B, 2,000 decisions on an immune target {decideBytes} B ({(cal ? "GC Allocated In Frame, calibrated" : "GC.GetTotalMemory")})");

            log.Insert(0, $"Rules self test: {(fail == 0 ? "ALL PASS" : "FAILURES")} ({pass} ok, {fail} failed)\n");
        }
        catch (Exception e)
        {
            log.AppendLine("  FAIL exception: " + e);
        }
        finally
        {
            RulesBooks.Active = prevBooks;
            LifeBodies.Clear();
            LifeOverrides.Clear();
            WorldLifeAuthority.Instance.Forget();
            WorldLifeAuthority.Instance.ResetStats();
            NeighborConsequences.ResetStats();
            NightNoise.ResetForTests();
            NeighborLetters.ResetPopupLimit();
            TruckLifeSensor.Instance.Clear();
            TruckLifeSensor.Instance.ResetStats();
        }
        return log.ToString();
    }

    static FakeBody Body(List<FakeBody> list, LifeId id, LifeBodyKind kind, Vector3 pos, string stop)
    {
        var b = new FakeBody { id = id, kind = kind, pos = pos, stop = stop };
        if (kind == LifeBodyKind.Dog) b.he = new Vector3(0.18f, 0.25f, 0.35f);
        list.Add(b);
        LifeBodies.Register(b);
        return b;
    }

    static NpcHitClaim Claim(LifeId id, float speed, int tick, byte actor) =>
        new NpcHitClaim { target = id, tick = tick, truckVelocity = new Vector3(0f, 0f, speed), actorSlot = actor, truck = null };

    static KnockFact Fact(KnockKind kind, byte district) => new KnockFact { knockId = 1, kind = kind, district = district };

    /// <summary>Decide only (no present / commit), for the speed table.</summary>
    static NpcHit Probe(WorldLifeAuthority a, NpcHitClaim c, ILifeBody b, IRulesBooks books)
    {
        a.Decide(in c, b, books, out var hit, out _);
        return hit;
    }

    /// <summary>Bytes allocated by <paramref name="body"/> ("GC Allocated In Frame" deltas, checked against a known
    /// 64 B allocation; GC.GetAllocatedBytesForCurrentThread reads 0 in this editor).</summary>
    static long AllocBytes(Action body, out bool calibrated)
    {
        using (var r = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory, "GC Allocated In Frame"))
        {
            long c0 = r.CurrentValue;
            calibrateSink = new object[4];
            long c1 = r.CurrentValue;
            calibrated = c1 - c0 >= 32;
            long a0 = r.CurrentValue;
            body();
            long a1 = r.CurrentValue;
            if (calibrated) return a1 - a0;
        }
        long m0 = GC.GetTotalMemory(false);
        body();
        return GC.GetTotalMemory(false) - m0;
    }
    static object calibrateSink;

    // ================================================================================================= play-test kit

    /// <summary>Helpers for the acceptance evals (play mode).</summary>
    public static class Kit
    {
        /// <summary>
        /// A test director (slot Schedule, so it draws before C's tick) that keeps real residents standing (or walking a
        /// short line) where an eval put them, through <see cref="ResidentDirector.Show"/>, with D told to leave them
        /// alone. Real cast ids carry their house (stopId), so grudges and letters are the real thing.
        /// </summary>
        public sealed class People : WorldLifeDirector
        {
            public static People Installed;
            public override WorldLifeSlot Slot => WorldLifeSlot.Schedule;
            public struct Spot { public LifeId id; public Vector3 pos; public float yaw; public PersonPose pose; public bool dog; }
            public readonly List<Spot> spots = new List<Spot>();

            public static People Install()
            {
                if (Installed != null) WorldLifeRunner.Unregister(Installed);
                Installed = new People();
                WorldLifeRunner.Register(Installed);
                return Installed;
            }

            public static void Uninstall()
            {
                if (Installed != null) WorldLifeRunner.Unregister(Installed);
                Installed = null;
            }

            public void Add(LifeId id, Vector3 pos, float yaw, PersonPose pose = PersonPose.Stand, bool dog = false)
            {
                spots.Add(new Spot { id = id, pos = pos, yaw = yaw, pose = pose, dog = dog });
            }

            public override void Tick(WorldLifeContext ctx)
            {
                var pd = PedestrianDirector.Instance;
                for (int i = 0; i < spots.Count; i++)
                {
                    var s = spots[i];
                    if (pd != null) pd.Suppress(s.id, ctx.Hours + 0.05);
                    ResidentDirector.Show(s.id, s.pos, s.yaw, s.pose, 0f, s.dog);
                }
            }
        }

        /// <summary>A resident (cast id) of a district whose house is in that district (for grudges / letters).</summary>
        public static LifeId ResidentOf(byte district, int skip = 0)
        {
            for (int i = 0; i < ResidentCast.Count; i++)
            {
                var id = LifeId.Resident(i);
                if (ResidentCast.Get(id).district != district) continue;
                if (skip-- > 0) continue;
                return id;
            }
            return LifeId.None;
        }

        public static string StopOf(LifeId id)
        {
            var ctx = WorldLifeRunner.Context;
            if (ctx == null || !ResidentCast.IsValid(id)) return "";
            int h = ResidentCast.Get(id).house;
            return h < ctx.Graph.houses.Length ? ctx.Graph.houses[h].stopId : "";
        }

        /// <summary>Press the truck's horn (TruckControllerV2.Honk is private; this is the H key's path).</summary>
        public static void Honk(TruckControllerV2 t)
        {
            var m = typeof(TruckControllerV2).GetMethod("Honk", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(t, null);
        }

        /// <summary>Every Ledger posting since <see cref="StartLedgerLog"/>.</summary>
        public static readonly List<LedgerEntry> LedgerLog = new List<LedgerEntry>();
        static bool logging;
        public static void StartLedgerLog()
        {
            LedgerLog.Clear();
            if (logging) return;
            logging = true;
            Ledger.Posted += e => LedgerLog.Add(e);
        }

        public static string DescribeLedger()
        {
            var sb = new StringBuilder();
            foreach (var e in LedgerLog) sb.AppendLine($"    {e.kind} {e.id} {e.amount:0.00} '{e.reason}' {e.districtId} @{e.atHours:F2}");
            return sb.ToString();
        }
    }
}
