using System;
using System.Text;
using NUnit.Framework;

/// <summary>
/// NUnit proof for the pure assemblies (Docs/Multiplayer.md §8.7.4, package T2, the wave-1 blocker). Run it in the build
/// copy, never the live project: <c>Tools/bots/sync_build.sh --tests --filter CoreSmokeTests</c>.
/// </summary>
public class CoreSmokeTests
{
    [Test]
    public void AllChecksPass()
    {
        var results = CoreSmoke.RunAll();
        Assert.That(CoreSmoke.AllOk(results), Is.True, CoreSmoke.Describe(results));
    }

    [Test]
    public void EveryCheckRuns()
    {
        Assert.That(CoreSmoke.RunAll().Count, Is.GreaterThanOrEqualTo(6));
        Assert.That(CoreSmoke.AllOk(Array.Empty<CoreSmoke.Result>()), Is.False, "an empty run must not count as a pass");
    }

    [Test]
    public void NetcodeAndCollectionsResolve()
    {
        Assert.AreEqual("Unity.Netcode.Runtime", typeof(Unity.Netcode.NetworkManager).Assembly.GetName().Name);
        Assert.AreEqual("Rubbish.Net.Core", typeof(CoreSmoke).Assembly.GetName().Name, "CoreSmoke must live in the pure assembly");
        Assert.AreEqual("dev:editor", new Unity.Collections.FixedString64Bytes("dev:editor").ToString());
    }

    [TestCase("", 0xcbf29ce484222325UL)]
    [TestCase("a", 0xaf63dc4c8601ec8cUL)]
    [TestCase("foobar", 0x85944171f73967e8UL)]
    public void Fnv1a64KnownVectors(string input, ulong expected)
    {
        Assert.AreEqual(expected, CoreSmoke.Fnv1a64(input));
    }

    [Test]
    public void GzipShrinksAndRestores()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++) sb.Append("bag-").Append(i % 50).Append(';');
        byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] gz = CoreSmoke.Gzip(raw);
        Assert.Less(gz.Length, raw.Length / 4);
        CollectionAssert.AreEqual(raw, CoreSmoke.Gunzip(gz));
    }

    [TestCase("-botsmoke", true)]
    [TestCase("-BOTSMOKE", true)]
    [TestCase("-bot", false)]
    [TestCase("-botsmokes", false)]
    public void HasFlagMatchesWholeArguments(string arg, bool expected)
    {
        Assert.AreEqual(expected, CoreSmoke.HasFlag(new[] { "RubbishBots", "-batchmode", arg }, CoreSmoke.Flag));
    }

    [Test]
    public void TryGetArgReadsBothForms()
    {
        string[] args = { "RubbishBots", "-playerid", "dev:bot-3", "-dataroot=/tmp/r/bot-3", "-nosteam", "-proto" };
        Assert.IsTrue(CoreSmoke.TryGetArg(args, "-playerid", out string id));
        Assert.AreEqual("dev:bot-3", id);
        Assert.IsTrue(CoreSmoke.TryGetArg(args, "-DATAROOT", out string root));
        Assert.AreEqual("/tmp/r/bot-3", root);
        Assert.IsFalse(CoreSmoke.TryGetArg(args, "-dataroot-x", out _));
        Assert.IsFalse(CoreSmoke.TryGetArg(args, "-proto", out _), "a trailing flag has no value");
        Assert.IsFalse(CoreSmoke.TryGetArg(args, "-missing", out _));
        Assert.IsFalse(CoreSmoke.TryGetArg(null, "-playerid", out _));
    }

    [Test]
    public void SmokeBootIsInertInTheEditor()
    {
        // The boot hook only acts in players launched with -botsmoke; a test Editor never has it.
        Assert.IsFalse(CoreSmoke.HasFlag(Environment.GetCommandLineArgs(), CoreSmoke.Flag));
    }
}
