using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The Raccoon calling's pure rules (Callings/Raccoon: HutchState, HutchRules, CrewRules, FeralRules, KingsRivalRules,
/// RaccoonStandingRules, RaccoonTuning): the roster codec and its budget inside the replicated workshop JSON, trapping,
/// breeding by in-game days, crew sweep maths and how the crew scales, Embrace It gating, the King's Rival rule set and standing
/// tiers. Game code is reached by reflection (this assembly cannot reference Assembly-CSharp), like CallingsTests.
/// </summary>
public class RaccoonCallingTests
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
    static object New(string name) => Activator.CreateInstance(T(name));
    static object Val(object value, string member)
    {
        var type = value as Type ?? value.GetType(); object target = value is Type ? null : value;
        var field = type.GetField(member, Flags); if (field != null) return field.GetValue(target);
        var property = type.GetProperty(member, Flags); Assert.NotNull(property, type.Name + "." + member);
        return property.GetValue(target);
    }
    static int I(object value, string member) => (int)Val(value, member);
    static bool B(object value, string member) => (bool)Val(value, member);
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    static object Static(string type, string method, params object[] args) => T(type).GetMethod(method, Flags).Invoke(null, args);
    static int Tune(string name) => Convert.ToInt32(T("RaccoonTuning").GetField(name, Flags).GetValue(null));
    static float TuneF(string name) => Convert.ToSingle(T("RaccoonTuning").GetField(name, Flags).GetValue(null));

    static object Rng(uint seed) => Static("HutchRules", "Seeded", seed);
    static object Hutch() => New("HutchState");
    static IList Roster(object hutch) => (IList)Val(hutch, "raccoons");
    static object Find(object hutch, int id) => Call(hutch, "Find", id);
    static string Encode(object hutch) => (string)Call(hutch, "Encode");
    static object Parse(string text) => Static("HutchState", "Parse", text);
    static bool Valid(object hutch) => (bool)Call(hutch, "Valid");
    static object Admit(object hutch, int level, bool rabid, object rng) => Static("HutchRules", "AdmitWild", hutch, level, rabid, rng);
    static object Job(string name) => Enum.Parse(T("HutchJob"), name);
    static bool SetJob(object hutch, int id, string job, int collar, int pack) => (bool)Static("HutchRules", "SetJob", hutch, id, Job(job), collar, pack, null);
    static bool Pair(object hutch, int a, int b, bool box) => (bool)Static("HutchRules", "Pair", hutch, a, b, box, null);
    static List<string> Day(object hutch, int level, object rng)
    {
        var news = new List<string>(); Static("HutchRules", "AdvanceDay", hutch, level, rng, news); return news;
    }
    static object Full(uint seed, int level = 2)
    {
        var hutch = Hutch(); var rng = Rng(seed);
        while (Admit(hutch, level, Roster(hutch).Count % 2 == 1, rng) != null) { }
        return hutch;
    }
    static int[] Traits(object r) => new[] { I(r, "speed"), I(r, "carry"), I(r, "greed"), I(r, "nerve") };

    // ------------------------------------------------------------------ roster: capacity, codec, budget

    [Test]
    public void TrappedRaccoonsFillTheBunksWithWildTraitsAndStayRabid()
    {
        Assert.AreEqual(0, (int)Static("HutchRules", "Capacity", 0)); Assert.AreEqual(3, (int)Static("HutchRules", "Capacity", 1)); Assert.AreEqual(6, (int)Static("HutchRules", "Capacity", 2));
        Assert.AreEqual(Tune("MaxRaccoons"), (int)Static("HutchRules", "Capacity", 2), "the saved roster's hard ceiling is the Condo");
        var none = Hutch(); Assert.IsNull(Admit(none, 0, false, Rng(1)), "no hutch, no bunk");
        var small = Full(7, 1); Assert.AreEqual(3, Roster(small).Count); Assert.IsNull(Admit(small, 1, false, Rng(2)), "a full hutch refuses");
        Assert.NotNull(Admit(small, 2, false, Rng(2)), "the Condo opens three more");
        var hutch = Full(11);
        Assert.AreEqual(6, Roster(hutch).Count); Assert.IsTrue(Valid(hutch));
        var ids = new HashSet<int>(); var names = new HashSet<string>();
        int index = 0;
        foreach (var r in Roster(hutch)) {
            Assert.IsTrue(ids.Add(I(r, "id"))); Assert.IsTrue(names.Add((string)Val(r, "name")), "names are not repeated");
            foreach (int trait in Traits(r)) Assert.That(trait, Is.InRange(Tune("WildTraitMin"), Tune("WildTraitMax")), "8-10 only comes from breeding");
            Assert.AreEqual(index++ % 2 == 1, B(r, "rabid"), "a rabid catch stays rabid, a clean one clean");
            Assert.IsFalse(B(r, "IsKit")); Assert.AreEqual(0, I(r, "job"));
        }
        // the host's dice decide: the same seed is the same raccoon, another seed another
        Assert.AreEqual(Encode(Full(11)), Encode(hutch)); Assert.AreNotEqual(Encode(Full(12)), Encode(hutch));
    }

    [Test]
    public void AFullHutchRoundTripsAndFitsTheReplicatedWorkshopBudget()
    {
        var hutch = Full(3);
        // the worst text a roster can carry: longest names, biggest numbers, everybody busy
        int n = 0;
        foreach (var r in Roster(hutch)) {
            Set(r, "name", new string((char)('A' + n), Tune("NameMaxChars"))); Set(r, "id", 999990 + n);
            Set(r, "speed", 10); Set(r, "carry", 10); Set(r, "greed", 10); Set(r, "nerve", 10); Set(r, "look", 65535); Set(r, "variant", 4);
            Set(r, "brought", 99); Set(r, "ate", 99); Set(r, "evt", 5); Set(r, "total", 99999); Set(r, "awayDays", 9); Set(r, "job", 1);
            n++;
        }
        Set(hutch, "nextId", 1000000); Set(hutch, "day", 999999); Set(hutch, "shift", 999999); Set(hutch, "reportDay", 999999); Set(hutch, "collars", 999); Set(hutch, "gnomes", 3);
        Assert.IsTrue(Valid(hutch));
        string text = Encode(hutch);
        int cap = (int)T("HutchState").GetField("MaxChars").GetRawConstantValue();
        Assert.AreEqual(cap, (int)T("WorkshopState").GetField("MaxHutchChars").GetRawConstantValue(), "WorkshopState repeats the cap so it stays free of HutchState");
        Assert.LessOrEqual(text.Length, cap); Assert.Less(text.Length, 520, "six raccoons are a few hundred characters");
        Assert.IsTrue((bool)Static("WorkshopState", "HutchTextValid", text), "nothing in a roster needs escaping in JSON");
        var back = Parse(text); Assert.NotNull(back); Assert.IsTrue(Valid(back)); Assert.AreEqual(text, Encode(back), "byte for byte");
        var copy = Call(hutch, "Clone"); Set(Roster(copy)[0], "name", "Changed"); Roster(copy).RemoveAt(5);
        Assert.AreEqual(text, Encode(hutch), "a clone is independent");

        // inside the workshop state: next to four worst-case displays and the whole tree it stays far under the 4000 cap
        var state = New("WorkshopState");
        Set(state, "completed", Convert.ToUInt16(Val(T("WorkshopCatalog"), "AllBits")));
        var displays = (IList)Val(state, "displays");
        foreach (string recipe in new[] { "bottle_cabinet", "rotating_pedestal", "wall_display", "grand_display" }) {
            var pin = New("WorkshopDisplayData");
            Set(pin, "recipeId", recipe); Set(pin, "uid", new string('u', 64)); Set(pin, "defId", new string('d', 64)); Set(pin, "massKg", 99.999f); Set(pin, "condition", 0.123456f);
            displays.Add(pin);
        }
        foreach (var node in ((IEnumerable)Val(T("CallingCatalog"), "All")).Cast<object>())
            Call(state, "SetCallingLevel", I(node, "index"), I(node, "maxLevel"));
        Set(state, "hutch", text);
        int length = JsonUtility.ToJson(state).Length;
        Assert.Less(length, 2700, "the replicated workshop JSON is capped at 4000 characters and a FixedString4096");
        // and through the real wire parser, as the owner's upload or the host's publish would send it
        var real = New("WorkshopState"); Set(real, "hutch", Encode(Full(5)));
        Assert.IsTrue((bool)Call(real, "Valid"));
        string json = JsonUtility.ToJson(real); var restored = Static("BusinessRegistry", "ParseWorkshop", json);
        Assert.NotNull(restored); Assert.AreEqual(json, JsonUtility.ToJson(restored)); Assert.AreEqual(Val(real, "hutch"), Val(Call(real, "Clone"), "hutch"));
    }

    [Test]
    public void OldSavesHaveAnEmptyHutchAndABadRosterIsRepairedOrRefused()
    {
        var old = Static("BusinessRegistry", "ParseWorkshop", "{\"completed\":16,\"displays\":[]}");
        Assert.NotNull(old, "a workshop saved before the hutch existed is still valid"); Assert.AreEqual("", Val(old, "hutch"));
        Assert.AreEqual(0, Roster(Parse("")).Count); Assert.AreEqual(0, Roster(Parse(null)).Count); Assert.IsTrue(Valid(Parse("")));
        Assert.IsNull(Parse("hello"), "not a roster"); Assert.IsNull(Parse("2;0;0;0;0;1;0;0"), "a future version is not guessed at");
        Assert.IsNull(Parse(new string('1', 701)));
        // text the wire refuses outright: too long, or anything JSON would have to escape
        Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", "{\"displays\":[],\"hutch\":\"" + new string('a', 701) + "\"}"));
        Assert.IsNull(Static("BusinessRegistry", "ParseWorkshop", "{\"displays\":[],\"hutch\":\"1;0;0;0;0;1;0;0|1,B\\u00e9b\\u00e9,5,5,5,5,0,0,0,0,0,0,0,0,0,0,0,0\"}"));
        // a hand-edited save cannot smuggle in a super raccoon, a job for a kit or a one-sided marriage
        var cheat = Parse("1;5;5;0;2;1;7;99|1,Sir <b>Cheats</b> A Lot Indeed,99,-4,55,10,1,70000,2,3,2,0,0,0,500,500,9,9999999|2,Mate,5,5,5,5,0,1,1,0,9,4,0,0,0,0,0,0|2,Twin,5,5,5,5,0,1,0,0,0,0,0,0,0,0,0,0");
        Assert.NotNull(cheat); Assert.IsTrue(Valid(cheat)); Assert.AreEqual(2, Roster(cheat).Count, "a duplicate id is dropped");
        var first = Find(cheat, 1);
        Assert.AreEqual(new[] { 10, 1, 10, 10 }, Traits(first)); Assert.AreEqual(0, I(first, "job"), "kits hold no job"); Assert.AreEqual(0, I(first, "mate"));
        Assert.LessOrEqual(((string)Val(first, "name")).Length, Tune("NameMaxChars")); StringAssert.DoesNotContain("<", (string)Val(first, "name"));
        Assert.AreEqual(0, I(Find(cheat, 2), "mate"), "half a pair is no pair"); Assert.AreEqual(0, I(Find(cheat, 2), "dueDays"));
        Assert.AreEqual(3, I(cheat, "gnomes")); Assert.AreEqual(3, I(cheat, "nextId"), "ids are never reused");
        // a LOCAL workshop save with a broken hutch string keeps its installed pieces and loses only the hutch
        var state = New("WorkshopState"); Set(state, "hutch", "\"quoted\"");
        Assert.IsFalse((bool)Call(state, "Valid")); Assert.IsTrue((bool)Call(state, "ValidIgnoringCallings"));
        Call(state, "NormalizeCallings"); Assert.IsTrue((bool)Call(state, "Valid")); Assert.AreEqual("", Val(state, "hutch"));
    }

    // ------------------------------------------------------------------ jobs and the Breeding Box

    [Test]
    public void JobsNeedTheirChipAndThePackHasALimit()
    {
        var hutch = Full(21);
        int a = I(Roster(hutch)[0], "id"), b = I(Roster(hutch)[1], "id"), c = I(Roster(hutch)[2], "id"), d = I(Roster(hutch)[3], "id");
        Assert.IsFalse(SetJob(hutch, a, "Crew", 0, 0), "no Collar Chip, no crew"); Assert.IsTrue(SetJob(hutch, a, "Crew", 1, 0));
        Assert.IsFalse(SetJob(hutch, b, "Companion", 1, 0), "no Pack Leader, no companion");
        Assert.IsTrue(SetJob(hutch, b, "Companion", 1, 1)); Assert.IsFalse(SetJob(hutch, c, "Companion", 1, 1), "Pack Leader is one raccoon");
        Assert.IsTrue(SetJob(hutch, c, "Companion", 1, 2)); Assert.IsTrue(SetJob(hutch, d, "Companion", 1, 2)); Assert.IsTrue(SetJob(hutch, b, "Companion", 1, 2), "already in the pack");
        Assert.IsFalse(SetJob(hutch, a, "Companion", 1, 2), "Alpha is three");
        Assert.AreEqual(3, (int)Call(hutch, "CountJob", Job("Companion"))); Assert.AreEqual(1, (int)Call(hutch, "CountJob", Job("Crew")));
        Assert.IsTrue(SetJob(hutch, b, "Rest", 0, 0)); Assert.IsFalse(SetJob(hutch, 424242, "Rest", 1, 2)); Assert.IsTrue(Valid(hutch));
        // pairing takes both off duty; the busy cannot be hired
        Assert.IsFalse(Pair(hutch, a, b, false), "no Breeding Box"); Assert.IsFalse(Pair(hutch, a, a, true)); Assert.IsTrue(Pair(hutch, a, b, true));
        Assert.AreEqual(0, I(Find(hutch, a), "job")); Assert.IsFalse(SetJob(hutch, a, "Crew", 1, 2)); Assert.IsFalse(Pair(hutch, a, c, true), "one partner at a time");
        Assert.IsTrue((bool)Static("HutchRules", "Unpair", hutch, b, null)); Assert.IsTrue(SetJob(hutch, a, "Crew", 1, 2)); Assert.IsTrue(Valid(hutch));
        // rename is cleaned, release repairs links
        Assert.IsTrue((bool)Static("HutchRules", "Rename", hutch, a, "  Mr  <i>Biscuit</i>!!  ", null)); Assert.AreEqual("Mr iBiscuiti", Val(Find(hutch, a), "name"));
        Assert.IsFalse((bool)Static("HutchRules", "Rename", hutch, a, "!!!", null));
        Assert.IsTrue(Pair(hutch, c, d, true)); Assert.IsTrue((bool)Static("HutchRules", "Release", hutch, c, null));
        Assert.IsNull(Find(hutch, c)); Assert.AreEqual(0, I(Find(hutch, d), "mate")); Assert.IsTrue(Valid(hutch));
    }

    [Test]
    public void KitsArriveAfterGestationDaysGrowUpAndNeverOverfillTheHutch()
    {
        var rng = Rng(77);
        var hutch = Hutch(); var mum = Admit(hutch, 2, false, rng); var dad = Admit(hutch, 2, false, rng);
        Assert.IsTrue(Pair(hutch, I(mum, "id"), I(dad, "id"), true));
        int gestation = Tune("GestationDays"), grow = Tune("GrowUpDays");
        for (int day = 1; day < gestation; day++) { Day(hutch, 2, rng); Assert.AreEqual(2, Roster(hutch).Count, "not yet"); Assert.IsTrue(Valid(hutch)); }
        // a session restart in the middle changes nothing: the countdown is in the saved roster, not in a clock
        hutch = Parse(Encode(hutch));
        var news = Day(hutch, 2, rng);
        int kits = Roster(hutch).Count - 2;
        Assert.That(kits, Is.InRange(1, Tune("LitterMax"))); Assert.AreEqual(kits, news.Count(l => l.Contains("had a kit")));
        Assert.IsTrue(Valid(hutch));
        mum = Find(hutch, I(mum, "id")); dad = Find(hutch, I(dad, "id"));
        Assert.AreEqual(0, I(mum, "mate")); Assert.AreEqual(Tune("RestDaysAfterLitter"), I(mum, "restDays")); Assert.IsFalse(B(dad, "Available"), "new parents rest");
        var kit = Roster(hutch)[2];
        Assert.IsTrue(B(kit, "IsKit")); Assert.AreEqual(grow, I(kit, "kitDays")); Assert.IsFalse(SetJob(hutch, I(kit, "id"), "Crew", 1, 2), "kits don't work");
        Assert.IsFalse(Pair(hutch, I(kit, "id"), I(mum, "id"), true));
        for (int day = 1; day < grow; day++) { Day(hutch, 2, rng); Assert.IsTrue(B(Find(hutch, I(kit, "id")), "IsKit")); }
        news = Day(hutch, 2, rng);
        Assert.IsFalse(B(kit, "IsKit")); Assert.IsTrue(news.Any(l => l.Contains("grown up"))); Assert.IsTrue(SetJob(hutch, I(kit, "id"), "Crew", 1, 2));
        Assert.IsTrue(B(Find(hutch, I(mum, "id")), "Available"), "rested again");

        // capacity limits the litter, and with no room at all the kits wait
        var tight = Hutch(); rng = Rng(5);
        var p1 = Admit(tight, 1, false, rng); var p2 = Admit(tight, 1, false, rng);
        Assert.IsTrue(Pair(tight, I(p1, "id"), I(p2, "id"), true));
        for (int day = 0; day < gestation; day++) Day(tight, 1, rng);
        Assert.AreEqual(3, Roster(tight).Count, "a hutch of three has room for exactly one kit");
        var packed = Full(9, 1);
        Assert.IsTrue(Pair(packed, I(Roster(packed)[0], "id"), I(Roster(packed)[1], "id"), true));
        for (int day = 0; day < gestation + 3; day++) { news = Day(packed, 1, rng); Assert.AreEqual(3, Roster(packed).Count); Assert.IsTrue(Valid(packed)); }
        Assert.IsTrue(news.Any(l => l.Contains("no room"))); Assert.AreNotEqual(0, I(Roster(packed)[0], "mate"), "still waiting");
        Assert.IsTrue((bool)Static("HutchRules", "Release", packed, I(Roster(packed)[2], "id"), null));
        Day(packed, 1, rng); Assert.AreEqual(3, Roster(packed).Count, "room made: the kit arrives the next day");
        Assert.AreEqual(3 * Tune("FoodCentsPerHeadPerDay"), (int)Static("HutchRules", "FoodCents", packed, 0), "kits eat too");
        Assert.AreEqual(3 * Tune("FoodCentsPerHeadPerDay"), (int)Static("HutchRules", "FoodCents", packed, 2), "and nobody here is on the crew, so no chip makes it dearer");
    }

    [Test]
    public void KitsInheritTheirParentsTraitsWithSmallMutationsDeterministically()
    {
        var a = New("HutchRaccoon"); var b = New("HutchRaccoon");
        Set(a, "speed", 2); Set(a, "carry", 9); Set(a, "greed", 4); Set(a, "nerve", 10);
        Set(b, "speed", 4); Set(b, "carry", 9); Set(b, "greed", 9); Set(b, "nerve", 10);
        float[] average = { 3f, 9f, 6.5f, 10f };
        int variants = 0, big = 0, rabidKits = 0; var seen = new HashSet<string>();
        const int runs = 3000;
        for (uint seed = 1; seed <= runs; seed++) {
            var kit = Static("HutchRules", "Inherit", a, b, Rng(seed)); var again = Static("HutchRules", "Inherit", a, b, Rng(seed));
            int[] traits = Traits(kit);
            CollectionAssert.AreEqual(traits, Traits(again), "the same dice give the same kit"); Assert.AreEqual(I(kit, "look"), I(again, "look"));
            bool special = I(kit, "variant") != 0; if (special) variants++;
            for (int i = 0; i < 4; i++) {
                Assert.That(traits[i], Is.InRange(Tune("TraitMin"), Tune("TraitMax")));
                float off = Mathf.Abs(traits[i] - average[i]);
                Assert.LessOrEqual(off, special ? 5.5f : 2.5f, "a kit stays near its parents' average");
                if (!special && off > 1.5f) big++;
            }
            Assert.AreEqual(Tune("GrowUpDays"), I(kit, "kitDays")); Assert.IsFalse(B(kit, "rabid"), "clean parents, clean kit");
            seen.Add(string.Join(",", traits));
        }
        Assert.Greater(seen.Count, 20, "litters differ"); Assert.That(variants, Is.InRange(runs / 50, runs / 8), "special variants are occasional");
        Assert.Greater(big, 0, "now and then a bigger jump"); Assert.Less(big, runs, "but mostly a small one");
        Set(a, "rabid", true);
        for (uint seed = 1; seed <= 400; seed++) if (B(Static("HutchRules", "Inherit", a, b, Rng(seed)), "rabid")) rabidKits++;
        Assert.That(rabidKits, Is.InRange(120, 280), "one rabid parent: about half the kits");
        Set(b, "rabid", true);
        for (uint seed = 1; seed <= 50; seed++) Assert.IsTrue(B(Static("HutchRules", "Inherit", a, b, Rng(seed)), "rabid"), "two rabid parents: always");
    }

    // ------------------------------------------------------------------ crews

    static object Crew(int count, int carry, int greed, int nerve, int speed = 1, bool rabid = false)
    {
        var hutch = Hutch();
        for (int i = 0; i < count; i++) {
            var r = New("HutchRaccoon");
            Set(r, "id", i + 1); Set(r, "name", "R" + i); Set(r, "carry", carry); Set(r, "greed", greed); Set(r, "nerve", nerve); Set(r, "speed", speed);
            Set(r, "rabid", rabid); Set(r, "job", 1);
            Roster(hutch).Add(r);
        }
        Set(hutch, "nextId", count + 1);
        return hutch;
    }
    static IList Sweep(object hutch, int collar, int brain, int available, uint seed, int hutchLevel = 2) =>
        (IList)Static("CrewRules", "Sweep", hutch, hutchLevel, collar, brain, available, Rng(seed));
    static int Cap(int hutchLevel, int collar, int brain) => (int)Static("CrewRules", "SweepCap", hutchLevel, collar, brain);
    static object District(string id) => Static("Districts", "Get", id);
    static float F(object value, string member) => Convert.ToSingle(Val(value, member));

    [Test]
    public void ASweepNeverTakesMoreThanIsThereOrThanTheCapAndGreedEatsTheHaul()
    {
        Assert.AreEqual(1, (int)Static("CrewRules", "BaseBags", 1)); Assert.AreEqual(1, (int)Static("CrewRules", "BaseBags", 5));
        Assert.AreEqual(2, (int)Static("CrewRules", "BaseBags", 6)); Assert.AreEqual(3, (int)Static("CrewRules", "BaseBags", 10));
        Assert.AreEqual(0, Sweep(Crew(6, 10, 1, 10), 0, 0, 50, 1).Count, "no Collar Chip, no crew");
        int cap = Cap(2, 1, 1);
        long delivered = 0, eaten = 0, takenGreedy = 0, eatenGreedy = 0;
        for (uint seed = 1; seed <= 300; seed++) {
            foreach (int available in new[] { 0, 1, 5, 50 }) {
                var hutch = Crew(6, 10, 3, 10, 10); var takes = Sweep(hutch, 1, 1, available, seed);
                int taken = 0;
                foreach (var take in takes) {
                    int t = I(take, "taken"), e = I(take, "eaten");
                    Assert.That(e, Is.InRange(0, t)); Assert.AreEqual(t - e, I(take, "Delivered")); Assert.LessOrEqual(t, 7, "three by carry, three more on the Brain Chip's cart, plus one by speed");
                    taken += t; delivered += t - e; eaten += e;
                }
                Assert.LessOrEqual(taken, Mathf.Min(available, cap));
                if (available >= 50) Assert.AreEqual(cap, taken, "a strong reliable crew fills the cap, never more");
                // the roster's shift numbers are exactly what the sweep reported
                int brought = Roster(hutch).Cast<object>().Sum(r => I(r, "brought")), ate = Roster(hutch).Cast<object>().Sum(r => I(r, "ate"));
                Assert.AreEqual(takes.Cast<object>().Sum(t => I(t, "Delivered")), brought); Assert.AreEqual(takes.Cast<object>().Sum(t => I(t, "eaten")), ate);
                Assert.IsTrue(Valid(hutch));
            }
            foreach (var take in Sweep(Crew(6, 5, 10, 10), 1, 1, 50, seed)) { takenGreedy += I(take, "taken"); eatenGreedy += I(take, "eaten"); }
        }
        float share = eaten / (float)(delivered + eaten), greedy = eatenGreedy / (float)takenGreedy;
        Assert.That(share, Is.InRange(0.05f, 0.14f), "greed 3 eats about 9%"); Assert.That(greedy, Is.InRange(0.24f, 0.36f), "greed 10 eats about 30%");
        // resting, paired, away and kit raccoons do not sweep; neither do companions
        var idle = Crew(4, 10, 1, 10);
        Set(Roster(idle)[0], "job", 0); Set(Roster(idle)[1], "job", 2); Set(Roster(idle)[2], "awayDays", 1); Set(Roster(idle)[3], "restDays", 1); Set(Roster(idle)[3], "job", 0);
        Assert.AreEqual(0, Sweep(idle, 1, 1, 50, 3).Count);
    }

    [Test]
    public void CollarChipsAreUnreliableBrainChipsAreNot()
    {
        int collarWork = 0, brainWork = 0, collarEvents = 0; var collarSeen = new HashSet<int>(); var brainSeen = new HashSet<int>();
        long cents = 0;
        const int runs = 2000;
        for (uint seed = 1; seed <= runs; seed++) {
            var collar = Crew(1, 5, 1, 5); var take = Sweep(collar, 1, 0, 50, seed)[0];
            if (I(take, "taken") > 0) collarWork++;
            int evt = I(take, "evt"); collarSeen.Add(evt); if (evt != 0) collarEvents++;
            cents += I(take, "cents");
            if (evt == 2) Assert.AreEqual(Tune("WanderAwayDays"), I(Roster(collar)[0], "awayDays"), "a wanderer is away, and keeps its job for when it is back");
            if (evt == 2) Assert.AreEqual(1, I(Roster(collar)[0], "job"));
            if (evt == 3) { Assert.AreEqual(-Tune("GnomeDamagesCents"), I(take, "cents")); Assert.AreEqual(1, I(collar, "gnomes")); }
            if (evt == 4) Assert.AreEqual(Tune("WalletCents"), I(take, "cents"));
            if (evt == 1 || evt == 2 || evt == 5) Assert.AreEqual(0, I(take, "taken"), "chased off, wandered off or asleep: nothing brought");
            Assert.IsTrue(Valid(collar));
            var brain = Crew(1, 5, 1, 5); take = Sweep(brain, 1, 1, 50, seed)[0];
            if (I(take, "taken") > 0) brainWork++;
            brainSeen.Add(I(take, "evt"));
        }
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3, 4, 5 }, collarSeen, "every antic happens on a Collar Chip");
        CollectionAssert.IsSubsetOf(brainSeen, new[] { 0, 1, 4 }, "a Brain Chip never naps, wanders or steals gnomes");
        Assert.That(collarWork / (float)runs, Is.InRange(0.72f, 0.86f), "about a fifth of Collar sweeps are lost");
        Assert.Greater(brainWork / (float)runs, 0.96f); Assert.Greater(brainWork, collarWork);
        Assert.That(Mathf.Abs(cents / (float)runs), Is.LessThan(100f), "antics are small change either way");
        // a rabid raccoon fears no dog but eats more; nerve 10 fears none either
        Assert.AreEqual(0, (int)Static("CrewRules", "ScarePercent", Roster(Crew(1, 5, 5, 1, 1, true))[0], false));
        Assert.AreEqual(0, (int)Static("CrewRules", "ScarePercent", Roster(Crew(1, 5, 5, 10))[0], false));
        Assert.AreEqual(18, (int)Static("CrewRules", "ScarePercent", Roster(Crew(1, 5, 5, 1))[0], false)); Assert.AreEqual(3, (int)Static("CrewRules", "ScarePercent", Roster(Crew(1, 5, 5, 1))[0], true));
        Assert.Greater((int)Static("CrewRules", "EatPercent", Roster(Crew(1, 5, 5, 5, 1, true))[0]), (int)Static("CrewRules", "EatPercent", Roster(Crew(1, 5, 5, 5))[0]));

        // money: a share of the fee x lateness, never a tip; even a night bag (x1.5 like every night pickup) is worth far
        // less than collecting it yourself
        float night = (float)T("NightShift").GetField("NightPayBonus").GetRawConstantValue();
        float pay = (float)Static("CrewRules", "BagPay", 40f, 1f);
        Assert.AreEqual(40f * Tune("CrewFeeSharePercent") / 100f, pay, 1e-4f); Assert.AreEqual(pay * 0.25f, (float)Static("CrewRules", "BagPay", 40f, 0.25f), 1e-4f);
        Assert.Less(pay * (1f + night), 40f * 0.6f, "a raccoon's night bag pays well under what the same bag pays in your own truck");
    }

    // ------------------------------------------------------------------ the crew as THE background automation

    [Test]
    public void TheSweepCapGrowsWithTheHutchAndTheChipAndABrainChipGivesEveryRaccoonACart()
    {
        // [chip][hutch]: Collar 6 / 10, Brain 12 / 20, Full-Time 16 / 26
        CollectionAssert.AreEqual(new[] { 6, 10, 12, 20, 16, 26 }, new[] { Cap(1, 1, 0), Cap(2, 1, 0), Cap(1, 1, 1), Cap(2, 1, 1), Cap(1, 1, 2), Cap(2, 1, 2) });
        Assert.AreEqual(0, Cap(2, 0, 2), "no Collar Chip, no crew"); Assert.AreEqual(0, Cap(0, 1, 1), "no hutch, no crew");
        Assert.AreEqual(Cap(2, 1, 2), Cap(9, 9, 9), "levels past the top change nothing"); Assert.AreEqual(Tune("CrewSweepCapMax"), Cap(2, 1, 2));
        for (int hutch = 1; hutch <= 2; hutch++) for (int brain = 0; brain <= 2; brain++) {
            if (hutch < 2) Assert.Less(Cap(hutch, 1, brain), Cap(hutch + 1, 1, brain), "a bigger hutch hauls a bigger sweep");
            if (brain < 2) Assert.Less(Cap(hutch, 1, brain), Cap(hutch, 1, brain + 1), "a better chip hauls a bigger sweep");
        }
        // per raccoon: 1-3 by Carry on a collar; the Brain Chip's cart adds 1 + Carry / 5; the Full-Time Chip adds hours, not arms
        CollectionAssert.AreEqual(new[] { 1, 1, 2, 2, 3 }, new[] { 1, 5, 6, 9, 10 }.Select(c => (int)Static("CrewRules", "CarryBags", c, 0)).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 2, 3, 4, 4, 6 }, new[] { 1, 4, 5, 6, 9, 10 }.Select(c => (int)Static("CrewRules", "CarryBags", c, 1)).ToArray());
        for (int carry = 1; carry <= 10; carry++) {
            Assert.AreEqual((int)Static("CrewRules", "CarryBags", carry, 1), (int)Static("CrewRules", "CarryBags", carry, 2));
            if (carry > 1) Assert.GreaterOrEqual((int)Static("CrewRules", "CarryBags", carry, 1), (int)Static("CrewRules", "CarryBags", carry - 1, 1), "more Carry never carries less");
        }
        // the cap really is what one sweep takes, at every level, however strong the crew
        foreach (int hutch in new[] { 1, 2 }) foreach (int brain in new[] { 0, 1, 2 })
            for (uint seed = 1; seed <= 40; seed++) {
                var takes = Sweep(Crew(hutch == 1 ? 3 : 6, 10, 1, 10, 10), 1, brain, 500, seed, hutch);
                Assert.LessOrEqual(takes.Cast<object>().Sum(t => I(t, "taken")), Cap(hutch, 1, brain));
            }
    }

    /// <summary>Bags a crew delivers in one day with a kerb that never runs out (so only the crew's own limits count).</summary>
    static float BagsPerDay(Func<uint, object> crew, int hutchLevel, int brain, out float eatenPerDay)
    {
        int sweeps = ((int[])Static("CrewRules", "SweepHours", 1, brain)).Length;
        const int days = 300; long delivered = 0, eaten = 0;
        for (uint day = 1; day <= days; day++) {
            var hutch = crew(day);
            for (uint sweep = 0; sweep < sweeps; sweep++) {
                foreach (var r in Roster(hutch)) { Set(r, "awayDays", 0); Set(r, "brought", 0); Set(r, "ate", 0); } // one day at a time
                foreach (var take in Sweep(hutch, 1, brain, 500, day * 31 + sweep, hutchLevel)) { delivered += I(take, "Delivered"); eaten += I(take, "eaten"); }
            }
        }
        eatenPerDay = eaten / (float)days;
        return delivered / (float)days;
    }
    static object WildCrew(uint seed, int count)
    {
        var hutch = Full(seed, count <= 3 ? 1 : 2);
        foreach (var r in Roster(hutch)) { Set(r, "rabid", false); Set(r, "job", 1); }
        return hutch;
    }

    [Test]
    public void EarlyCrewsStaySmallAndAFullyInvestedCrewCoversTheCheaperDistrictsWithoutOutEarningWhatItReplaces()
    {
        // The three cheaper districts (Districts.cs): what a late-game owner wants collected while they work the rich ones.
        var cheap = new[] { "mildly_pleasant", "minivan_meadows", "coupon_commons" }.Select(District).ToArray();
        float cheapBags = cheap.Sum(d => F(d, "bagsPerDay")), cheapFees = cheap.Sum(d => F(d, "bagsPerDay") * F(d, "fee"));
        Assert.AreEqual(93f, cheapBags, 0.01f); Assert.AreEqual(3870f, cheapFees, 0.01f);
        foreach (var d in cheap) Assert.IsTrue((bool)Static("CrewRules", "WorksTier", I(d, "tier")));
        Assert.IsTrue((bool)Static("CrewRules", "WorksTier", I(District("stackington_heights"), "tier")), "spare capacity may spill into tier 3");
        Assert.IsFalse((bool)Static("CrewRules", "WorksTier", I(District("billionaire_bluffs"), "tier")), "the Bluffs' Butler Drops are always the owner's");
        Assert.IsFalse((bool)Static("CrewRules", "WorksTier", 0));

        // EARLY: Collar Chip, the small hutch, three trapped raccoons. A handful of bags a night, and lumpy.
        float early = BagsPerDay(seed => WildCrew(seed, 3), 1, 0, out float earlyEaten);
        Assert.That(early, Is.InRange(3.5f, 8f), "early stays small"); Assert.Greater(earlyEaten, 0.3f, "and they eat some");
        Assert.LessOrEqual(2 * Cap(1, 1, 0), 12, "the early ceiling is a dozen bags a night");
        // MID: Brain Chip, the Condo, six trapped raccoons. Roughly half of the cheaper districts.
        float mid = BagsPerDay(seed => WildCrew(seed, 6), 2, 1, out _);
        Assert.That(mid, Is.InRange(38f, 56f)); Assert.Less(mid, cheapBags * 0.62f, "an unbred crew cannot cover the cheaper districts");
        // TOP: Full-Time Chip, the Condo, six bred raccoons (Carry 9, Zoom 9, Greed 2, Nerve 9).
        float top = BagsPerDay(seed => Crew(6, 9, 2, 9, 9), 2, 2, out float topEaten);
        Assert.GreaterOrEqual(top + topEaten, cheapBags, "a bred full-time crew takes every bag the cheaper districts put out");
        Assert.That(top, Is.InRange(cheapBags - 3f, 104f), "and delivers nearly all of them");
        int ceiling = Cap(2, 1, 2) * ((int[])Static("CrewRules", "SweepHours", 1, 2)).Length;
        Assert.AreEqual(104, ceiling); Assert.LessOrEqual(ceiling, 113, "never more than the 113 bags a day the old top-level Kevin's Crew covered");
        Assert.Greater(top, 1.5f * mid, "breeding and the Full-Time Chip are what make it a real route");
        // with three of the six out in the owner's pack, the crew is half a crew
        Assert.Less(BagsPerDay(seed => Crew(3, 9, 2, 9, 9), 2, 2, out _), cheapBags * 0.62f);

        // KIBBLE is the wage. Plain kibble for everybody; the crew eats dearer once there is a Brain Chip.
        int plain = Tune("FoodCentsPerHeadPerDay"), brainFood = Tune("CrewFoodCentsBrain"), premium = Tune("CrewFoodCentsFullTime");
        Assert.AreEqual(1500, plain); Assert.Less(plain, brainFood); Assert.Less(brainFood * 5, premium);
        var six = Crew(6, 9, 2, 9, 9);
        Assert.AreEqual(6 * plain, (int)Static("HutchRules", "FoodCents", six, 0)); Assert.AreEqual(6 * brainFood, (int)Static("HutchRules", "FoodCents", six, 1));
        Assert.AreEqual(6 * premium, (int)Static("HutchRules", "FoodCents", six, 2)); Assert.AreEqual(6 * premium, (int)Static("HutchRules", "FoodCents", six, 7));
        Set(Roster(six)[0], "job", 0); Set(Roster(six)[1], "job", 2); Set(Roster(six)[2], "job", 0); Set(Roster(six)[2], "kitDays", 1);
        Assert.AreEqual(3 * premium + 3 * plain, (int)Static("HutchRules", "FoodCents", six, 2), "resting raccoons, the pack and kits stay on plain kibble");
        Assert.AreEqual(0, (int)Static("HutchRules", "FoodCents", null, 2)); Assert.AreEqual(0, (int)Static("CrewRules", "FoodCents", null, 2));

        // MONEY, as an upper bound nobody reaches: every bag on time, every bag at the night rate, spare capacity spent in
        // Snootington. Even then the top crew nets less than the old Kevin's Crew did at its top level on paper
        // (70% x 95% of its 113 bags' fees, less the $900 wage = about $594 a day; x3 here since the 2026-10-12 pace pass, which
        // tripled every fee and the kibble with them), and the smaller crews net next to nothing.
        float share = Tune("CrewFeeSharePercent") / 100f, night = 1f + (float)T("NightShift").GetField("NightPayBonus").GetRawConstantValue();
        float snoot = F(District("snootington_hills"), "fee");
        float topBest = share * night * (cheapFees + (ceiling - cheapBags) * snoot) - 6 * premium / 100f;
        Assert.That(topBest, Is.InRange(300f, 1782f));
        // mid, the same way: 60 bags at most, cheapest first (all of the Acres, then Meadows)
        float midBags = Cap(2, 1, 1) * 3, acres = F(cheap[0], "bagsPerDay");
        float midBest = share * night * (acres * F(cheap[0], "fee") + (midBags - acres) * F(cheap[1], "fee")) - 6 * brainFood / 100f;
        Assert.That(midBest, Is.InRange(0f, 675f), "under what the old mid-level Kevin's Crew netted on paper ($225 a day, x3)");
        // early: twelve bags of the Acres at the very best, against three bowls of kibble
        float earlyBest = share * night * 2 * Cap(1, 1, 0) * F(cheap[0], "fee") - 3 * plain / 100f;
        Assert.That(earlyBest, Is.InRange(-45f, 45f), "an early crew is company, not income");
    }

    [Test]
    public void CollarChipsTakeTheOldestBagAndABrainChipWorksTheCheapestDistrictFirst()
    {
        int Order(int brain, float feeA, double outA, string a, float feeB, double outB, string b) => (int)Static("CrewRules", "CompareBags", brain, feeA, outA, a, feeB, outB, b);
        // a collar knows nothing about money: oldest (smelliest) first, whatever the street
        Assert.Less(Order(0, 55f, 1.0, "st-1/b0", 4f, 2.0, "1/b0"), 0); Assert.Greater(Order(0, 4f, 2.0, "1/b0", 55f, 1.0, "st-1/b0"), 0);
        // a brain plans a route: the cheap district first even when its bag is fresher, then oldest inside a district
        foreach (int brain in new[] { 1, 2 }) {
            Assert.Greater(Order(brain, 55f, 1.0, "st-1/b0", 4f, 2.0, "1/b0"), 0); Assert.Less(Order(brain, 4f, 2.0, "1/b0", 15f, 1.0, "mm-1/b0"), 0);
            Assert.Less(Order(brain, 15f, 1.0, "mm-1/b0", 15f, 3.0, "mm-2/b0"), 0);
        }
        // a total order: ties fall to the bag id, and a bag is never before itself
        foreach (int brain in new[] { 0, 1 }) {
            Assert.Less(Order(brain, 4f, 1.0, "a", 4f, 1.0, "b"), 0); Assert.Greater(Order(brain, 4f, 1.0, "b", 4f, 1.0, "a"), 0);
            Assert.AreEqual(0, Order(brain, 4f, 1.0, "a", 4f, 1.0, "a"));
        }
        // sorting a mixed kerb with it: the brain's route is the Acres, Meadows, Commons, then Snootington
        var kerb = new List<(float fee, double putOut, string id)> { (40f, 0.5, "sh"), (4f, 9.0, "mp-new"), (25f, 1.0, "cc"), (4f, 2.0, "mp-old"), (15f, 3.0, "mm") };
        kerb.Sort((x, y) => Order(1, x.fee, x.putOut, x.id, y.fee, y.putOut, y.id));
        CollectionAssert.AreEqual(new[] { "mp-old", "mp-new", "mm", "cc", "sh" }, kerb.Select(b => b.id).ToArray());
        kerb.Sort((x, y) => Order(0, x.fee, x.putOut, x.id, y.fee, y.putOut, y.id));
        CollectionAssert.AreEqual(new[] { "sh", "cc", "mp-old", "mm", "mp-new" }, kerb.Select(b => b.id).ToArray());
    }

    [Test]
    public void EveryChipAddsSweepHoursAndEverySlotRunsExactlyOnce()
    {
        int[] none = (int[])Static("CrewRules", "SweepHours", 0, 0), collar = (int[])Static("CrewRules", "SweepHours", 1, 0), brain = (int[])Static("CrewRules", "SweepHours", 1, 1);
        Assert.AreEqual(0, none.Length); Assert.AreEqual(0, ((int[])Static("CrewRules", "SweepHours", 0, 1)).Length, "a Brain Chip without a collar is nothing");
        CollectionAssert.IsSubsetOf(collar, brain, "the Brain Chip only adds sweeps"); Assert.Greater(brain.Length, collar.Length);
        int[] fullTime = (int[])Static("CrewRules", "SweepHours", 1, 2);
        CollectionAssert.IsSubsetOf(brain, fullTime, "the Full-Time Chip only adds sweeps"); Assert.AreEqual(brain.Length + 1, fullTime.Length);
        CollectionAssert.AreEqual(fullTime, (int[])Static("CrewRules", "SweepHours", 3, 9)); CollectionAssert.AllItemsAreUnique(fullTime);
        CollectionAssert.AreEquivalent(fullTime, (int[])Val(T("RaccoonTuning"), "FullTimeSweepHours"), "the host ticks the widest set of hours and filters per business");
        // no kerb waits longer than ten hours for a full-time crew (a noon-only day shift left a ten-hour gap twice a day)
        var sorted = fullTime.OrderBy(h => h).ToArray();
        for (int i = 0; i < sorted.Length; i++) Assert.LessOrEqual((sorted[(i + 1) % sorted.Length] - sorted[i] + 24) % 24, 10);
        CollectionAssert.AreEqual(new[] { 26, 31, 36, 46, 50, 55 }, (List<int>)Static("CrewRules", "DueSlots", 24.0, 55.0, fullTime));
        float nightStart = (float)T("GameClock").GetField("NightStart").GetRawConstantValue(), dawn = (float)T("GameClock").GetField("DawnStart").GetRawConstantValue();
        foreach (int h in collar) Assert.IsTrue(h >= nightStart || h < dawn, "Collar Chip raccoons work at night");
        Assert.IsTrue(brain.Any(h => h < nightStart && h >= dawn), "the Brain Chip adds a day sweep");

        List<int> Due(double from, double to) => (List<int>)Static("CrewRules", "DueSlots", from, to, brain);
        // ticking in any step sizes over two days runs each slot once, in order, and a restart point is exclusive
        var all = Due(7.0, 55.0); CollectionAssert.AreEqual(new[] { 12, 22, 26, 36, 46, 50 }, all);
        var stepped = new List<int>(); double t = 7.0; var step = Rng(4);
        while (t < 55.0) { double next = Math.Min(55.0, t + ((int)((Func<int, int, int>)step)(1, 400)) / 100.0); stepped.AddRange(Due(t, next)); t = next; }
        CollectionAssert.AreEqual(all, stepped);
        Assert.AreEqual(0, Due(22.0, 22.0).Count); Assert.AreEqual(0, Due(22.0, 21.0).Count, "a clock that went backwards (new session) sweeps nothing");
        Assert.AreEqual(1, Due(21.99, 22.0).Count); Assert.AreEqual(0, Due(22.0, 22.5).Count, "the slot the last tick already ran is not run again");
        Assert.LessOrEqual(Due(0.0, 2400.0).Count, 2 * brain.Length + brain.Length, "a huge jump catches up two days at most");

        // one night is one shift (06:00 to 06:00), so the 22:00 and 02:00 sweeps share a report
        Assert.AreEqual(12, (int)Static("CrewRules", "ShiftOf", 12, 22f)); Assert.AreEqual(12, (int)Static("CrewRules", "ShiftOf", 13, 2f));
        Assert.AreEqual(13, (int)Static("CrewRules", "ShiftOf", 13, 6f)); Assert.AreEqual(13, (int)Static("CrewRules", "ShiftOf", 13, 12f));
    }

    [Test]
    public void TheMorningReportSaysWhatEachRaccoonDidOncePerShift()
    {
        var hutch = Crew(3, 5, 5, 5);
        Set(Roster(hutch)[0], "name", "Biscuit"); Set(Roster(hutch)[0], "brought", 6); Set(Roster(hutch)[0], "ate", 2); Set(Roster(hutch)[0], "evt", 1);
        Set(Roster(hutch)[1], "name", "Gary"); Set(Roster(hutch)[1], "evt", 5);
        Set(Roster(hutch)[2], "name", "Noodle");
        Set(hutch, "shift", 4);
        var lines = (List<string>)Static("CrewRules", "ReportLines", hutch);
        CollectionAssert.AreEqual(new[] { "Biscuit brought 6, ate 2, got chased by a dog.", "Gary fell asleep in a bin." }, lines);
        Assert.IsFalse((bool)Static("CrewRules", "BeginShift", hutch, 4), "same shift: the numbers keep adding up");
        Assert.IsTrue((bool)Static("CrewRules", "CloseShift", hutch)); Assert.AreEqual(1, I(hutch, "reportDay")); Assert.IsTrue(B(hutch, "told"));
        Assert.IsFalse((bool)Static("CrewRules", "CloseShift", hutch), "a shift is reported once"); Assert.AreEqual(1, I(hutch, "reportDay"));
        Assert.AreEqual(2, ((List<string>)Static("CrewRules", "ReportLines", hutch)).Count, "the cards keep last night's numbers until the next shift starts");
        Assert.IsTrue((bool)Static("CrewRules", "BeginShift", hutch, 5)); Assert.IsFalse(B(hutch, "told"));
        Assert.AreEqual(0, ((List<string>)Static("CrewRules", "ReportLines", hutch)).Count); Assert.AreEqual(0, I(Roster(hutch)[0], "brought"));
        Assert.IsFalse((bool)Static("CrewRules", "CloseShift", hutch), "a quiet shift closes without a report"); Assert.AreEqual(1, I(hutch, "reportDay"));
        // the shift survives the wire
        Set(Roster(hutch)[2], "brought", 3); Set(hutch, "told", false);
        var back = Parse(Encode(hutch)); CollectionAssert.AreEqual(new[] { "Noodle brought 3." }, (List<string>)Static("CrewRules", "ReportLines", back));
        Assert.AreEqual(5, I(back, "shift")); Assert.IsFalse(B(back, "told"));
    }

    // ------------------------------------------------------------------ Embrace It, the King, standing

    [Test]
    public void EmbracingRabiesStopsThePassOutTheHospitalAndTheAutoShot()
    {
        Assert.IsTrue((bool)Static("FeralRules", "PassesOut", 0), "not embraced: the old rule (20 h, hospital bill)"); Assert.IsTrue((bool)Static("FeralRules", "AutoShot", 0));
        Assert.IsTrue((bool)Static("FeralRules", "Stumbles", 0)); Assert.AreEqual(0.8f, (float)Static("FeralRules", "ShownIntensity", 0.8f, 0));
        for (int level = 1; level <= 3; level++) {
            Assert.IsFalse((bool)Static("FeralRules", "PassesOut", level)); Assert.IsFalse((bool)Static("FeralRules", "AutoShot", level), "a stored shot waits for you");
            Assert.IsFalse((bool)Static("FeralRules", "Stumbles", level));
            Assert.AreEqual(TuneF("FeralSymptomCap"), (float)Static("FeralRules", "ShownIntensity", 1f, level)); Assert.AreEqual(0.05f, (float)Static("FeralRules", "ShownIntensity", 0.05f, level));
        }
        Assert.Less(TuneF("FeralSymptomCap"), 0.25f, "a hint, not the trip");
        // perks need the level AND the virus
        Assert.IsFalse((bool)Static("FeralRules", "PerksActive", 1, true)); Assert.IsFalse((bool)Static("FeralRules", "PerksActive", 3, false)); Assert.IsTrue((bool)Static("FeralRules", "PerksActive", 2, true));
        Assert.AreEqual(1f, (float)Static("FeralRules", "SpeedMult", 1, true, true)); Assert.AreEqual(1f, (float)Static("FeralRules", "SpeedMult", 3, false, true));
        Assert.AreEqual(TuneF("FeralNightSpeed"), (float)Static("FeralRules", "SpeedMult", 2, true, true)); Assert.AreEqual(1f, (float)Static("FeralRules", "SpeedMult", 2, true, false));
        Assert.AreEqual(TuneF("FeralDaySpeed"), (float)Static("FeralRules", "SpeedMult", 3, true, false)); Assert.Greater(TuneF("FeralNightSpeed"), TuneF("FeralDaySpeed"));
        Assert.IsFalse((bool)Static("FeralRules", "CanPounce", 2, true)); Assert.IsFalse((bool)Static("FeralRules", "CanPounce", 3, false)); Assert.IsTrue((bool)Static("FeralRules", "CanPounce", 3, true));
        // who the wild ones leave alone: the feral (while rabid) and an Alpha
        Assert.IsFalse((bool)Static("FeralRules", "RaccoonsDefer", 0, true, 0)); Assert.IsFalse((bool)Static("FeralRules", "RaccoonsDefer", 1, true, 1));
        Assert.IsFalse((bool)Static("FeralRules", "RaccoonsDefer", 3, false, 1), "cured: fair game again"); Assert.IsTrue((bool)Static("FeralRules", "RaccoonsDefer", 2, true, 0));
        Assert.IsTrue((bool)Static("FeralRules", "RaccoonsDefer", 0, false, 2));
        // the gates RabiesStatus, CritterSpawner, ShopEffectsRunner and BootsMods read exist and are default-safe with no save loaded
        foreach (string gate in new[] { "LocalPassesOut", "LocalAutoShot", "LocalStumbles" }) Assert.NotNull(T("FeralStatus").GetProperty(gate, Flags), gate);
        Assert.NotNull(T("FeralStatus").GetProperty("LocalSpeedMult", Flags)); Assert.NotNull(T("FeralStatus").GetMethod("LocalShown", Flags));
    }

    [Test]
    public void TheKingHuntsHisRivalPaysTheOneWhoBeatsHimAndTaxesTheOnesWhoDont()
    {
        Assert.IsFalse((bool)Static("KingsRivalRules", "IsRival", 0)); Assert.IsTrue((bool)Static("KingsRivalRules", "IsRival", 1));
        float range = TuneF("RivalFocusRange");
        Assert.IsTrue((bool)Static("KingsRivalRules", "PrefersRival", true, range)); Assert.IsFalse((bool)Static("KingsRivalRules", "PrefersRival", true, range + 0.1f));
        Assert.IsFalse((bool)Static("KingsRivalRules", "PrefersRival", false, 3f), "a Rival in a truck or a shop is not hunted");
        Assert.AreEqual(Tune("RivalBonusCents"), (int)Static("KingsRivalRules", "BonusCents", 1, 1)); Assert.AreEqual(0, (int)Static("KingsRivalRules", "BonusCents", 1, 0), "watching earns nothing");
        Assert.AreEqual(0, (int)Static("KingsRivalRules", "BonusCents", 0, 9), "not a Rival: the ordinary pot only");
        Assert.IsTrue((bool)Static("KingsRivalRules", "WinsCollar", 1, 3)); Assert.IsFalse((bool)Static("KingsRivalRules", "WinsCollar", 1, 0)); Assert.IsFalse((bool)Static("KingsRivalRules", "WinsCollar", 0, 3));
        Assert.AreEqual(Tune("TributeCents"), (int)Static("KingsRivalRules", "TributeCents", 1)); Assert.AreEqual(0, (int)Static("KingsRivalRules", "TributeCents", 0));
        Assert.Greater(Tune("RivalBonusCents"), Tune("TributeCents") * 2, "fighting is worth it");
        for (int pick = -3; pick < 40; pick++) {
            foreach (string line in new[] { (string)Static("KingsRivalRules", "ArriveLine", "A Player With A Long Name", pick), (string)Static("KingsRivalRules", "HitLine", pick),
                         (string)Static("KingsRivalRules", "WinLine", pick), (string)Static("KingsRivalRules", "LeaveLine", "A Player With A Long Name", pick) }) {
                Assert.IsNotEmpty(line); Assert.LessOrEqual(System.Text.Encoding.UTF8.GetByteCount(line), 120, "a line rides in a 128-byte network string");
            }
        }
        StringAssert.Contains("Nick", (string)Static("KingsRivalRules", "ArriveLine", "Nick", 0));
    }

    [Test]
    public void StandingTiersClimbWithTheCallingAndStartSafe()
    {
        string Tier(int hutch, int embrace, int collar, int brain, int pack, int rival, int collars) =>
            Static("RaccoonStandingRules", "Tier", hutch, embrace, collar, brain, pack, rival, collars).ToString();
        Assert.AreEqual("Stranger", Tier(0, 0, 0, 0, 0, 0, 0)); Assert.AreEqual("Stranger", Tier(0, 0, 0, 0, 0, 0, 5), "collars without the capstone mean nothing");
        Assert.AreEqual("Friend", Tier(1, 0, 0, 0, 0, 0, 0)); Assert.AreEqual("Friend", Tier(0, 1, 0, 0, 0, 0, 0));
        Assert.AreEqual("Family", Tier(1, 2, 0, 0, 0, 0, 0)); Assert.AreEqual("Family", Tier(1, 0, 1, 0, 0, 0, 0)); Assert.AreEqual("Family", Tier(2, 2, 0, 0, 1, 0, 0));
        Assert.AreEqual("Made", Tier(2, 2, 1, 1, 0, 0, 0)); Assert.AreEqual("Made", Tier(2, 2, 0, 0, 2, 0, 0));
        Assert.AreEqual("Rival", Tier(2, 3, 1, 1, 2, 1, 0)); Assert.AreEqual("Boss", Tier(2, 3, 1, 1, 2, 1, 1));
        var tiers = Enum.GetValues(T("RaccoonTier")).Cast<object>().ToArray();
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, tiers.Select(t => (int)t), "tiers are an ordered scale other systems may compare");
        foreach (var tier in tiers) Assert.IsNotEmpty((string)Static("RaccoonStandingRules", "Title", tier));
        // the runtime query is default-safe: with no session and an unknown client, a Stranger nobody protects
        Assert.AreEqual("Stranger", Static("RaccoonStanding", "For", 987654321UL).ToString()); Assert.IsFalse((bool)Static("RaccoonStanding", "Protects", 987654321UL));
        Assert.IsFalse((bool)Static("RaccoonStanding", "Protects", ulong.MaxValue));
    }

    // ------------------------------------------------------------------ the catalog rows this stage ships

    [Test]
    public void TheRaccoonNodesAreBuildableWithTextThatMatchesTheTuning()
    {
        var shipped = new Dictionary<string, int> {
            { "calling_critter_hutch", 2 }, { "calling_embrace_it", 3 }, { "calling_collar_chip", 1 }, { "calling_breeding_box", 1 },
            { "calling_brain_chip", 2 }, { "calling_pack_leader", 2 }, { "calling_kings_rival", 1 } };
        foreach (var kv in shipped) {
            var node = Static("CallingCatalog", "Get", kv.Key); Assert.NotNull(node, kv.Key);
            Assert.AreEqual(kv.Value, I(node, "implementedLevels"), kv.Key); Assert.AreEqual(kv.Value, I(node, "maxLevel"), kv.Key + ": every level ships");
            Assert.AreEqual("Raccoon", Val(node, "calling").ToString());
        }
        string Effect(string id, int level) => (string)Val(Call(Static("CallingCatalog", "Get", id), "Level", level), "effect");
        StringAssert.Contains(Tune("HutchCapacityL1") + " bunks", Effect("calling_critter_hutch", 1)); StringAssert.Contains(Tune("HutchCapacityL2") + " bunks", Effect("calling_critter_hutch", 2));
        StringAssert.Contains("$" + Tune("FoodCentsPerHeadPerDay") / 100, Effect("calling_critter_hutch", 1));
        StringAssert.Contains(Tune("CrewFeeSharePercent") + "%", Effect("calling_collar_chip", 1));
        foreach (int hour in (int[])Val(T("RaccoonTuning"), "CollarSweepHours")) StringAssert.Contains(hour.ToString("00") + ":00", Effect("calling_collar_chip", 1));
        // the crew's scaling, as the cards promise it: caps by chip and hutch, the added sweeps, the kibble
        StringAssert.Contains("up to " + Cap(1, 1, 0) + " a sweep", Effect("calling_collar_chip", 1)); StringAssert.Contains("never the Bluffs", Effect("calling_collar_chip", 1));
        StringAssert.Contains("up to " + Cap(2, 1, 0) + " bags, " + Cap(2, 1, 1) + " with a Brain Chip", Effect("calling_critter_hutch", 2));
        string brain = Effect("calling_brain_chip", 1), fullTime = Effect("calling_brain_chip", 2);
        StringAssert.Contains("noon", brain); StringAssert.Contains("up to " + Cap(1, 1, 1) + " bags a sweep (" + Cap(2, 1, 1) + " with the Condo)", brain);
        StringAssert.Contains("cheapest streets first", brain); StringAssert.Contains("$" + Tune("CrewFoodCentsBrain") / 100 + " a head", brain);
        StringAssert.Contains("2-6 bags each", brain); Assert.AreEqual(2, (int)Static("CrewRules", "CarryBags", 1, 1)); Assert.AreEqual(6, (int)Static("CrewRules", "CarryBags", 10, 1));
        int morning = ((int[])Val(T("RaccoonTuning"), "FullTimeSweepHours")).Except((int[])Val(T("RaccoonTuning"), "BrainSweepHours")).Single();
        StringAssert.Contains(morning.ToString("00") + ":00", fullTime); StringAssert.Contains("up to " + Cap(1, 1, 2) + " bags a sweep (" + Cap(2, 1, 2) + " with the Condo)", fullTime);
        StringAssert.Contains("$" + Tune("CrewFoodCentsFullTime") / 100 + " a head", fullTime);
        foreach (string text in new[] { brain, fullTime, Effect("calling_collar_chip", 1) }) StringAssert.DoesNotContain("Kevin", text);
        StringAssert.Contains(Tune("GestationDays") + " days", Effect("calling_breeding_box", 1));
        StringAssert.Contains(Mathf.RoundToInt((TuneF("FeralNightSpeed") - 1f) * 100f) + "%", Effect("calling_embrace_it", 2));
        StringAssert.Contains(TuneF("PounceDistance") + " m", Effect("calling_embrace_it", 3)); StringAssert.Contains(TuneF("PounceCooldown") + " s", Effect("calling_embrace_it", 3));
        StringAssert.Contains(TuneF("FetchCooldown") + " s", Effect("calling_pack_leader", 1));
        StringAssert.Contains("$" + Tune("RivalBonusCents") / 100, Effect("calling_kings_rival", 1)); StringAssert.Contains("$" + Tune("TributeCents") / 100, Effect("calling_kings_rival", 1));
        // the whole calling builds with exactly its recipes, and the capstone needs the rest of it (a business that has its SolidWaste 3D disk: DiskTests)
        var state = New("WorkshopState"); Set(state, "disk", 1);
        foreach (string id in new[] { "work_gloves", "calling_critter_hutch", "calling_critter_hutch", "calling_embrace_it", "calling_embrace_it", "calling_embrace_it",
                     "calling_collar_chip", "calling_breeding_box", "calling_brain_chip", "calling_pack_leader" })
            Assert.IsTrue(BuildWithParts(state, id), id);
        Assert.IsFalse(BuildWithParts(state, "calling_kings_rival"), "the capstone needs Alpha");
        Assert.IsTrue(BuildWithParts(state, "calling_pack_leader")); Assert.IsTrue(BuildWithParts(state, "calling_kings_rival"));
        Assert.IsTrue(BuildWithParts(state, "calling_brain_chip"), "the Full-Time Chip is the Brain Chip's second level");
        Assert.IsFalse(BuildWithParts(state, "calling_brain_chip"), "and its last");
        Assert.IsTrue((bool)Call(state, "Valid"));
    }

    [Test]
    public void TheHutchStandsAtACanonicalPoseClearOfEverythingElseOnTheLot()
    {
        Vector3 hutch = (Vector3)Val(T("HutchWorld"), "StreetLocal");
        // door-local slots already taken on a street lot (WorkshopWorld.StreetPlacement / MonumentLocal, DepotRowLayout, DepotLot's lease sign, the Hauler's hatch)
        var taken = new Dictionary<string, Vector3> {
            { "business sign", new Vector3(8.3f, 0, 2.2f) }, { "doorbell", new Vector3(4.9f, 0, .35f) }, { "monument", (Vector3)Val(T("WorkshopWorld"), "MonumentLocal") },
            { "bag hoop", (Vector3)Val(T("WorkshopWorld"), "HoopLocal") }, { "walk-up hatch", (Vector3)Val(T("HaulerTuning"), "HatchLocal") }, { "on foot exit", new Vector3(5.8f, 0, 2.2f) },
            { "truck", new Vector3(0f, 0, 5.5f) }, { "truck rollout", new Vector3(0f, 0, 3f) }, { "for lease sign", new Vector3(7f, 0, 8f) } };
        for (int nth = 0; nth < 4; nth++) taken["spawn " + nth] = new Vector3(-6f + 1.2f * nth, 0, 9f);
        foreach (var kv in taken) Assert.Greater(Vector2.Distance(new Vector2(hutch.x, hutch.z), new Vector2(kv.Value.x, kv.Value.z)), 3f, "too close to the " + kv.Key);
        Assert.Greater(Mathf.Abs(hutch.x), 3.5f, "never in the truck's lane out of the door"); Assert.Greater(hutch.z, 1.5f, "in front of the shop front, not in it");
        // the same pose on every machine, straight from the layout (authority never takes a client's word for it)
        for (int lot = 0; lot < 4; lot++) {
            var pose = (Pose)Static("HutchWorld", "PoseFor", lot);
            Assert.AreEqual((Pose)Static("DepotRowLayout", "StreetPose", lot, hutch, 0f), pose);
            // and clear of the pieces that really stand on that lot, wherever their slots are moved to
            foreach (string piece in ((string[])Val(T("WorkshopModels"), "RecipeIds")).Where(id => (bool)Static("WorkshopWorld", "StreetRecipe", id))) {
                Vector3 at = ((Pose)Static("WorkshopWorld", "PlacementFor", lot, piece)).position;
                Assert.Greater(Vector2.Distance(new Vector2(pose.position.x, pose.position.z), new Vector2(at.x, at.z)), 3f, "lot " + lot + ": too close to the " + piece);
            }
            Vector3 monument = ((Pose)Static("WorkshopWorld", "MonumentPose", lot)).position;
            Assert.Greater(Vector2.Distance(new Vector2(pose.position.x, pose.position.z), new Vector2(monument.x, monument.z)), 3f, "lot " + lot + ": too close to the Monument");
            Assert.AreEqual(lot, (int)Static("DepotRowLayout", "PadAt", pose.position), "on its own lot's pad");
        }
        // the hutch has no indoor copy any more (the workshop room is gone): in the solo flow it stands outside the legacy shop, on the lot that stands in for it
        int legacy = (int)T("DepotFlowRules").GetField("LegacyShopLot").GetRawConstantValue();
        Assert.AreEqual((Pose)Static("HutchWorld", "PoseFor", legacy), (Pose)Static("HutchWorld", "PoseFor", -1));
        Assert.AreEqual(-1, (int)Static("DepotRowLayout", "InteriorAt", ((Pose)Static("HutchWorld", "PoseFor", -1)).position), "the street, not anybody's interior");
        Assert.IsEmpty(T("HutchWorld").GetMember("AnnexLocal", Flags)); Assert.IsNull(Type.GetType("WorkshopAnnex, Assembly-CSharp", false));
    }

    /// <summary>Build the next level of a node with exactly its recipe's parts (one def id per item, as the bench offers them).</summary>
    static bool BuildWithParts(object state, string id)
    {
        var node = Static("CallingCatalog", "Get", id);
        var recipe = Call(state, "NextRecipe", node);
        object parts = recipe != null ? Call(recipe, "Pick", (Func<string, int>)(_ => 99)) : new System.Collections.Generic.List<string>();
        return (bool)Call(state, "BuildCalling", id, parts, null);
    }
}
