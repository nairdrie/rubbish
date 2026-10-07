using NUnit.Framework;

/// <summary>B10: <c>+connect_lobby</c> parsing for cold start, rich presence and launch lines (Docs/Multiplayer.md §8.7.4).</summary>
public class ConnectArgsTests
{
    // universe 1, account type 8 (chat), instance flags lobby|mms (0x60000): a real-shaped lobby id
    const ulong Lobby = 109775241045848137UL;
    const ulong User = 76561197960287930UL;

    [Test]
    public void LobbyIdShape()
    {
        Assert.IsTrue(ConnectArgs.LooksLikeLobbyId(Lobby));
        Assert.IsTrue(ConnectArgs.LooksLikeLobbyId((1UL << 56) | (8UL << 52) | (0x40000UL << 32) | 12345UL), "lobby flag alone");
        Assert.IsFalse(ConnectArgs.LooksLikeLobbyId(User), "an individual account isn't a lobby");
        Assert.IsFalse(ConnectArgs.LooksLikeLobbyId((1UL << 56) | (8UL << 52) | 12345UL), "a chat room without the lobby flag");
        Assert.IsFalse(ConnectArgs.LooksLikeLobbyId(0));
        Assert.IsFalse(ConnectArgs.LooksLikeLobbyId(ulong.MaxValue));
    }

    [Test]
    public void ForLobbyRoundTrips()
    {
        string s = ConnectArgs.ForLobby(Lobby);
        Assert.AreEqual("+connect_lobby " + Lobby, s);
        Assert.IsTrue(ConnectArgs.TryParse(s, out ulong id));
        Assert.AreEqual(Lobby, id);
    }

    static readonly object[] Good =
    {
        new object[] { new[] { "Rubbish.exe", "+connect_lobby", "109775241045848137" } },
        new object[] { new[] { "Rubbish.exe", "-steam", "+connect_lobby", "109775241045848137", "-dataroot", "x" } },
        new object[] { new[] { "Rubbish.exe", "+connect_lobby=109775241045848137" } },
        new object[] { new[] { "Rubbish.exe", "+CONNECT_LOBBY", "\"109775241045848137\"" } },
        new object[] { new[] { "Rubbish.exe", "-connect_lobby", "109775241045848137" } },
    };

    static readonly object[] Bad =
    {
        new object[] { new[] { "Rubbish.exe" } },
        new object[] { new[] { "Rubbish.exe", "+connect_lobby" } },
        new object[] { new[] { "Rubbish.exe", "+connect_lobby", "-steam" } },
        new object[] { new[] { "Rubbish.exe", "+connect_lobby", "76561197960287930" } },
        new object[] { new[] { "Rubbish.exe", "+connect_lobby", "12x45" } },
        new object[] { new[] { "Rubbish.exe", "+connect_lobby", "999999999999999999999999" } },
        new object[] { new[] { "Rubbish.exe", "+connect", "109775241045848137" } },
        new object[] { new[] { "Rubbish.exe", "--connect_lobby", "109775241045848137" } },
    };

    [TestCaseSource(nameof(Good))]
    public void CommandLineFindsTheLobby(string[] args)
    {
        Assert.IsTrue(ConnectArgs.TryParse(args, out ulong id));
        Assert.AreEqual(Lobby, id);
    }

    [TestCaseSource(nameof(Bad))]
    public void CommandLineRejects(string[] args)
    {
        Assert.IsFalse(ConnectArgs.TryParse(args, out ulong id));
        Assert.AreEqual(0UL, id);
    }

    [Test]
    public void LinesAndQuotes()
    {
        Assert.IsTrue(ConnectArgs.TryParse("  +connect_lobby   109775241045848137  ", out ulong a));
        Assert.AreEqual(Lobby, a);
        Assert.IsTrue(ConnectArgs.TryParse("-steam \"+connect_lobby\" \"109775241045848137\"", out ulong b));
        Assert.AreEqual(Lobby, b);
        CollectionAssert.AreEqual(new[] { "a b", "c" }, ConnectArgs.Tokenize("\"a b\" c"));
        Assert.IsFalse(ConnectArgs.TryParse((string)null, out _));
        Assert.IsFalse(ConnectArgs.TryParse("", out _));
        Assert.IsFalse(ConnectArgs.TryParse(new string('x', ConnectArgs.MaxLineChars + 1), out _), "oversized lines are ignored");
    }

    [Test]
    public void FirstValidLobbyWins()
    {
        var args = new[] { "+connect_lobby", "123", "+connect_lobby", "109775241045848137" };
        Assert.IsTrue(ConnectArgs.TryParse(args, out ulong id));
        Assert.AreEqual(Lobby, id);
    }
}
