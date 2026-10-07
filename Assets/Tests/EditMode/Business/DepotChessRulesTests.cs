using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>Reflection because Depot Row gameplay lives in Assembly-CSharp, outside this test asmdef.</summary>
public sealed class DepotChessRulesTests
{
    static Type Net => Type.GetType("DepotChessNet, Assembly-CSharp", true);
    static Type State => Type.GetType("DepotChessNet+ChessState, Assembly-CSharp", true);
    static Type Color => Type.GetType("PieceColor, Assembly-CSharp", true);
    static Type Kind => Type.GetType("PieceType, Assembly-CSharp", true);
    static Type Result => Type.GetType("GameResult, Assembly-CSharp", true);
    static object White => Enum.Parse(Color, "White");
    static object Black => Enum.Parse(Color, "Black");

    static object NewState(byte[] locations = null, byte[] types = null)
    {
        var s = Activator.CreateInstance(State);
        if (locations != null) Put(s, "locations", locations);
        if (types != null) Put(s, "types", types);
        if (locations != null) Put(s, "moved", new byte[locations.Length]);
        return s;
    }

    static void Put(object target, string field, object value) => State.GetField(field).SetValue(target, value);
    static T Get<T>(object target, string field) => (T)State.GetField(field).GetValue(target);
    static object Call(string method, params object[] args) => Net.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static object C(string name) => Enum.Parse(Color, name);
    static byte P(string name) => Convert.ToByte(Enum.Parse(Kind, name));

    static Array Colors(params string[] names)
    {
        var colors = Array.CreateInstance(Color, names.Length);
        for (int i = 0; i < names.Length; i++) colors.SetValue(C(names[i]), i);
        return colors;
    }

    [Test]
    public void SeatRequiresBoardAndRangeAndNeverChangesAnotherLot()
    {
        var games = Array.CreateInstance(State, 4);
        for (int i = 0; i < 4; i++) games.SetValue(NewState(), i);
        Assert.IsFalse((bool)Call("TrySeat", games, 2, White, 17UL, false, true));
        Assert.IsFalse((bool)Call("TrySeat", games, 2, White, 17UL, true, false));
        Assert.AreEqual(ulong.MaxValue, Get<ulong>(games.GetValue(2), "whiteSeat"));
        Assert.IsTrue((bool)Call("TrySeat", games, 2, White, 17UL, true, true));
        Assert.AreEqual(17UL, Get<ulong>(games.GetValue(2), "whiteSeat"));
        Assert.AreEqual(ulong.MaxValue, Get<ulong>(games.GetValue(1), "whiteSeat"));
        Assert.IsFalse((bool)Call("TrySeat", games, 1, White, 17UL, true, true));
        Assert.IsFalse((bool)Call("TrySeat", games, 2, White, 18UL, true, true));
        Assert.IsTrue((bool)Call("TrySeat", games, 2, Black, 18UL, true, true));
        Assert.AreEqual(18UL, Get<ulong>(games.GetValue(2), "blackSeat"));
        Assert.IsTrue((bool)Call("TryStand", games, 2, 17UL));
        Assert.AreEqual(ulong.MaxValue, Get<ulong>(games.GetValue(2), "whiteSeat"));
        Assert.IsFalse((bool)Call("TryStand", games, 1, 18UL));
        Assert.AreEqual(18UL, Get<ulong>(games.GetValue(2), "blackSeat"));
    }

    [Test]
    public void OnlySeatedSideMayMoveAndBusyOrFinishedBoardRejects()
    {
        var game = NewState();
        Put(game, "whiteSeat", 17UL);
        Assert.IsTrue((bool)Call("CanMove", game, 17UL, (byte)12, (byte)28));
        Assert.IsFalse((bool)Call("CanMove", game, 18UL, (byte)12, (byte)28));
        Assert.IsFalse((bool)Call("CanMove", game, 17UL, (byte)64, (byte)28));
        Put(game, "busy", true);
        Assert.IsFalse((bool)Call("CanMove", game, 17UL, (byte)12, (byte)28));
        Put(game, "busy", false);
        Put(game, "result", Enum.Parse(Result, "Draw"));
        Assert.IsFalse((bool)Call("CanMove", game, 17UL, (byte)12, (byte)28));
    }

    [Test]
    public void CastleMovesRookAndResetRestoresHomesWithoutTouchingOtherGame()
    {
        var home = new byte[] { 4, 7, 60 };
        var types = new byte[] { P("King"), P("Rook"), P("King") };
        var game = NewState((byte[])home.Clone(), (byte[])types.Clone());
        var other = NewState(new byte[] { 4, 7, 60 }, (byte[])types.Clone());
        Assert.IsTrue((bool)Call("ApplyMoveState", game, Colors("White", "White", "Black"), (byte)4, (byte)6));
        CollectionAssert.AreEqual(new byte[] { 6, 5, 60 }, Get<byte[]>(game, "locations"));
        CollectionAssert.AreEqual(new byte[] { 1, 1, 0 }, Get<byte[]>(game, "moved"));
        Assert.AreEqual(Black, Get<object>(game, "turn"));
        CollectionAssert.AreEqual(home, Get<byte[]>(other, "locations"));
        Call("ResetMoveState", game, home, types);
        CollectionAssert.AreEqual(home, Get<byte[]>(game, "locations"));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, Get<byte[]>(game, "moved"));
        Assert.AreEqual(White, Get<object>(game, "turn"));
    }

    [Test]
    public void EnPassantCaptureAndPromotionRemainInReplicatedPieceSlots()
    {
        // e5 white pawn, d5 black pawn, both kings.
        var game = NewState(new byte[] { 36, 35, 4, 60 }, new byte[] { P("Pawn"), P("Pawn"), P("King"), P("King") });
        Put(game, "epFile", (sbyte)3);
        Put(game, "epRank", (sbyte)5);
        Assert.IsTrue((bool)Call("ApplyMoveState", game, Colors("White", "Black", "White", "Black"), (byte)36, (byte)43));
        CollectionAssert.AreEqual(new byte[] { 43, 64, 4, 60 }, Get<byte[]>(game, "locations"));
        Assert.AreEqual((sbyte)-1, Get<sbyte>(game, "epFile"));

        var promotion = NewState(new byte[] { 48, 4, 60 }, new byte[] { P("Pawn"), P("King"), P("King") });
        Assert.IsTrue((bool)Call("ApplyMoveState", promotion, Colors("White", "White", "Black"), (byte)48, (byte)56));
        Assert.AreEqual(P("Queen"), Get<byte[]>(promotion, "types")[0]);
        Assert.AreEqual((byte)56, Get<byte[]>(promotion, "locations")[0]);
    }

    [Test]
    public void ValidatorAcceptsPawnDoublePushButRejectsImpossibleJump()
    {
        Type pieceState = Type.GetType("PieceState, Assembly-CSharp", true);
        Type validator = Type.GetType("ChessValidator, Assembly-CSharp", true);
        var board = Array.CreateInstance(pieceState, 8, 8);
        object Make(string type, object color) => Activator.CreateInstance(pieceState, Enum.Parse(Kind, type), color, false);
        board.SetValue(Make("King", White), 4, 0);
        board.SetValue(Make("King", Black), 4, 7);
        board.SetValue(Make("Pawn", White), 4, 1);
        var method = validator.GetMethod("IsValidMove", BindingFlags.Public | BindingFlags.Static);
        bool Valid(Vector2Int from, Vector2Int to) => (bool)method.Invoke(null, new object[] { board, from, to, White, new Vector2Int(-1, -1) });
        Assert.IsTrue(Valid(new Vector2Int(4, 1), new Vector2Int(4, 3)));
        Assert.IsFalse(Valid(new Vector2Int(4, 1), new Vector2Int(4, 4)));
    }

    [Test]
    public void ValidatorHonorsCastleThroughCheckEnPassantAndPromotionSquares()
    {
        Type pieceState = Type.GetType("PieceState, Assembly-CSharp", true);
        Type validator = Type.GetType("ChessValidator, Assembly-CSharp", true);
        var method = validator.GetMethod("IsValidMove", BindingFlags.Public | BindingFlags.Static);
        object Make(string type, object color) => Activator.CreateInstance(pieceState, Enum.Parse(Kind, type), color, false);
        bool Valid(Array board, Vector2Int from, Vector2Int to, Vector2Int ep) =>
            (bool)method.Invoke(null, new object[] { board, from, to, White, ep });

        var castle = Array.CreateInstance(pieceState, 8, 8);
        castle.SetValue(Make("King", White), 4, 0);
        castle.SetValue(Make("Rook", White), 7, 0);
        castle.SetValue(Make("King", Black), 4, 7);
        Assert.IsTrue(Valid(castle, new Vector2Int(4, 0), new Vector2Int(6, 0), new Vector2Int(-1, -1)));
        castle.SetValue(Make("Rook", Black), 5, 7); // f-file attacks the square the king must cross
        Assert.IsFalse(Valid(castle, new Vector2Int(4, 0), new Vector2Int(6, 0), new Vector2Int(-1, -1)));

        var special = Array.CreateInstance(pieceState, 8, 8);
        special.SetValue(Make("King", White), 4, 0);
        special.SetValue(Make("King", Black), 4, 7);
        special.SetValue(Make("Pawn", White), 4, 4); // e5
        special.SetValue(Make("Pawn", Black), 3, 4); // d5
        Assert.IsTrue(Valid(special, new Vector2Int(4, 4), new Vector2Int(3, 5), new Vector2Int(3, 5)));
        special.SetValue(Make("Pawn", White), 0, 6); // a7 promotes by reaching a8
        Assert.IsTrue(Valid(special, new Vector2Int(0, 6), new Vector2Int(0, 7), new Vector2Int(-1, -1)));
    }
}
