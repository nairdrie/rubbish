// Isolated-save MPPM fixture: seated guest (white) can play Qg6-g7# against Magnus.
// Must run on the host after the guest has sat at lot 0. Never runs against a real save.
if (!SaveRoot.IsOverridden || NetBoot.Manager == null || !NetBoot.Manager.IsServer)
    throw new System.InvalidOperationException("This fixture requires an isolated host test save.");
var net = DepotChessNet.Instance;
if (!net.TryState(0, out var s) || s.whiteSeat == ulong.MaxValue || s.busy)
    throw new System.InvalidOperationException("Seat a white player and wait for the initial reset first.");
var board = DepotRowScene.Lot(0).interior.GetComponentInChildren<DepotChessBoard>();
for (int i = 0; i < board.pieces.Length; i++)
{
    var p = board.pieces[i];
    s.locations[i] = (byte)(64 + i);
    s.types[i] = (byte)p.startingType;
    s.moved[i] = 1;
    if (p.startingType == PieceType.King) s.locations[i] = p.color == PieceColor.White ? (byte)45 : (byte)63;
    if (p.startingType == PieceType.Queen && p.color == PieceColor.White) s.locations[i] = 46;
}
s.turn = PieceColor.White;
s.result = GameResult.InProgress;
s.blackSeat = ChessController.MagnusSeatId;
s.epFile = s.epRank = -1;
s.ply = 42;
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
string fen = (string)typeof(DepotChessNet).GetMethod("Fen", flags).Invoke(net, new object[]{s, board});
s.engine = Chess.Core.Board.CreateBoard(fen);
typeof(DepotChessNet).GetMethod("Broadcast", flags).Invoke(net, new object[]{0, true});
return fen;
