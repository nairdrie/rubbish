using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

/// <summary>Playtest metrics (Docs/Metrics.md): the helpers that shape every line, and that edit mode never opens a file.</summary>
public class MetricsTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type Metrics => Type.GetType("Metrics, Assembly-CSharp", true);

    [Test]
    public void ThePlayerTagIsEightHexCharactersAndNeverTheId()
    {
        var tag = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), Metrics.GetMethod("Tag", Static));
        string a = tag("steam:76561198000000001"), b = tag("steam:76561198000000002");
        Assert.AreEqual(8, a.Length);
        StringAssert.IsMatch("^[0-9a-f]{8}$", a);
        Assert.AreEqual(a, tag("steam:76561198000000001"), "stable");
        Assert.AreNotEqual(a, b);
        StringAssert.DoesNotContain("7656", a);
        Assert.AreEqual("", tag(null));
    }

    [Test]
    public void StringsAreEscapedAndCutOff()
    {
        var append = Metrics.GetMethod("AppendString", Static);
        var sb = new StringBuilder();
        append.Invoke(null, new object[] { sb, "a\"b\\c\nd\te\u0001", 100 });
        Assert.AreEqual("\"a\\\"b\\\\c\\nd\\te\\u0001\"", sb.ToString());
        sb.Clear();
        append.Invoke(null, new object[] { sb, "abcdefghij", 4 });
        Assert.AreEqual("\"abcd…\"", sb.ToString());
        sb.Clear();
        append.Invoke(null, new object[] { sb, null, 4 });
        Assert.AreEqual("null", sb.ToString());
    }

    [Test]
    public void CounterMapsComeOutSortedWithInvariantNumbers()
    {
        var json = Type.GetType("Metrics+Json, Assembly-CSharp", true);
        var map = new Dictionary<string, double> { ["inc.pickup"] = 12.5, ["bags"] = 3, ["exp.fine"] = double.NaN };
        string s = (string)json.GetMethod("Map", Static).Invoke(null, new object[] { map, "0.###" });
        Assert.AreEqual("{\"bags\":3,\"exp.fine\":0,\"inc.pickup\":12.5}", s);
        Assert.AreEqual("{}", json.GetMethod("Map", Static).Invoke(null, new object[] { new Dictionary<string, double>(), "0.###" }));
        Assert.AreEqual("[1,2.5,0]", json.GetMethod("Array", Static).Invoke(null, new object[] { new List<double> { 1, 2.5, double.PositiveInfinity }, "0.##" }));
    }

    [Test]
    public void EditModeNeverOpensAFileAndEveryCallIsHarmless()
    {
        Metrics.GetMethod("ResetStatics", Static).Invoke(null, null);    // whatever an earlier test left open
        var begin = Metrics.GetMethod("Begin", Static);
        object ev = begin.Invoke(null, new object[] { "nothing" });
        var type = ev.GetType();
        object e2 = type.GetMethod("Int").Invoke(ev, new object[] { "n", 5L });
        type.GetMethod("End").Invoke(e2, null);
        Assert.IsFalse((bool)Metrics.GetProperty("Enabled", Static).GetValue(null));
        Assert.IsNull(Metrics.GetProperty("FilePath", Static).GetValue(null));
    }

    // ------------------------------------------------------------------ the file itself

    static object Make(string type, params (string field, object value)[] fields)
    {
        var t = Type.GetType(type + ", Assembly-CSharp", true);
        object o = Activator.CreateInstance(t);
        foreach (var (field, value) in fields)
        {
            var f = t.GetField(field);
            f.SetValue(o, value is int && f.FieldType != typeof(int) ? Convert.ChangeType(value, f.FieldType) : value);
        }
        return o;
    }

    [Test]
    public void EveryLineTheRunnerWritesIsValidJsonWithTheFieldsTheSummaryReads()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rubbish-metrics-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        GameObject go = null;
        try
        {
            string file = (string)Metrics.GetMethod("OpenForTests", Static).Invoke(null, new object[] { dir });
            go = new GameObject("metrics-test");
            var runnerType = Type.GetType("MetricsRunner, Assembly-CSharp", true);
            var runner = go.AddComponent(runnerType);
            object Invoke(string name, params object[] args) =>
                runnerType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(runner, args);

            Invoke("WriteHeader");
            Invoke("WriteSnapshot", "test");
            Invoke("OnBag", Make("BagCollectedFact", ("bagId", "mm-1/old3"), ("districtId", "minivan_meadows"), ("ageHours", 21.5), ("onTime", false), ("pay", 3f)));
            Invoke("OnRank", 1, 2);
            Invoke("OnDistrictUnlocked", "minivan_meadows");
            Invoke("OnNight");
            Invoke("OnCleanupPhase", Enum.ToObject(Type.GetType("CleanupPhase, Assembly-CSharp", true), 2), Activator.CreateInstance(Type.GetType("CleanupState, Assembly-CSharp", true)));
            Invoke("OnLedger", Make("LedgerEntry", ("kind", "credit"), ("id", "pickup"), ("amount", 4.5f)));
            Invoke("OnLedger", Make("LedgerEntry", ("kind", "charge"), ("id", "neglect_fine"), ("amount", -2f)));
            Invoke("OnLedger", Make("LedgerEntry", ("kind", "rep"), ("id", "bag_ontime"), ("amount", 3f)));
            Metrics.GetMethod("Begin", Static).Invoke(null, new object[] { "hold_break" });   // never ended: must be dropped, not break the file
            Invoke("WriteSnapshot", "tick");
            Metrics.GetMethod("Flush", Static).Invoke(null, null);
            Metrics.GetMethod("Close", Static).Invoke(null, null);

            string[] lines = System.IO.File.ReadAllLines(file);
            Assert.GreaterOrEqual(lines.Length, 8);
            var names = new List<string>();
            foreach (string line in lines)
            {
                Assert.IsTrue(JsonSyntax.IsValid(line), "not JSON: " + line);
                StringAssert.StartsWith("{\"e\":\"", line);
                int a = line.IndexOf("\"e\":\"", StringComparison.Ordinal) + 5, b = line.IndexOf('"', a);
                names.Add(line.Substring(a, b - a));
                foreach (string k in new[] { "\"t\":", "\"rt\":", "\"gh\":", "\"d\":" }) StringAssert.Contains(k, line, "every event is stamped");
            }
            CollectionAssert.IsSubsetOf(new[] { "session", "snap", "bag", "rank", "district_unlock", "night_fell", "big_cleanup" }, names);
            string bag = lines[names.IndexOf("bag")];
            foreach (string field in new[] { "\"dist\":\"minivan_meadows\"", "\"age\":21.5", "\"on\":false", "\"heap\":true", "\"pay\":3" }) StringAssert.Contains(field, bag);
            string snap = lines[names.LastIndexOf("snap")];
            foreach (string field in new[] { "\"dist\":[", "\"camp\":null", "\"act\":{", "\"c\":{", "\"ft\":[", "\"inc.pickup\":4.5", "\"exp.neglect_fine\":2", "\"rep.bag_ontime\":3" })
                StringAssert.Contains(field, snap);
            CollectionAssert.DoesNotContain(names, "hold_break", "an event that was never ended is dropped");
        }
        finally
        {
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
            Metrics.GetMethod("ResetStatics", Static).Invoke(null, null);
            try { System.IO.Directory.Delete(dir, true); } catch { /* temp folder */ }
        }
    }

    /// <summary>A strict little JSON checker (objects, arrays, strings with escapes, numbers, true / false / null).</summary>
    static class JsonSyntax
    {
        public static bool IsValid(string s)
        {
            int i = 0;
            try { Skip(s, ref i); Value(s, ref i); Skip(s, ref i); return i == s.Length; }
            catch (FormatException) { return false; }
        }

        static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static void Value(string s, ref int i)
        {
            if (i >= s.Length) throw new FormatException();
            char c = s[i];
            if (c == '{')
            {
                i++; Skip(s, ref i);
                if (s[i] == '}') { i++; return; }
                while (true)
                {
                    Skip(s, ref i); Str(s, ref i); Skip(s, ref i);
                    if (s[i++] != ':') throw new FormatException();
                    Skip(s, ref i); Value(s, ref i); Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == '}') return;
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                i++; Skip(s, ref i);
                if (s[i] == ']') { i++; return; }
                while (true)
                {
                    Skip(s, ref i); Value(s, ref i); Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i++] == ']') return;
                    throw new FormatException();
                }
            }
            if (c == '"') { Str(s, ref i); return; }
            foreach (string lit in new[] { "true", "false", "null" })
                if (string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0) { i += lit.Length; return; }
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            if (i == start || !double.TryParse(s.Substring(start, i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new FormatException();
        }

        static void Str(string s, ref int i)
        {
            if (s[i++] != '"') throw new FormatException();
            while (true)
            {
                if (i >= s.Length) throw new FormatException();
                char c = s[i++];
                if (c == '"') return;
                if (c < 0x20) throw new FormatException();
                if (c == '\\') { if (i >= s.Length || "\"\\/bfnrtu".IndexOf(s[i++]) < 0) throw new FormatException(); }
            }
        }
    }
}
