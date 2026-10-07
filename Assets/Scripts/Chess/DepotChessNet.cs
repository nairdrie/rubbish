using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using Engine = Chess.Core;

/// <summary>
/// One spawned session component owns all four Depot Row chess games. Pieces and boards are local scene visuals,
/// identified by lot + stable piece index; no copied depot contains an in-scene NetworkObject.
/// </summary>
[NetSessionComponent(Order = 34)]
public sealed class DepotChessNet : NetworkBehaviour
{
    public static DepotChessNet Instance { get; private set; }
    public const byte CapturedLocation = 64, NoLocation = 255;
    const int LotCount = DepotRowLayout.LotCount;
    const int MaxPieces = 32;

    public sealed class ChessState
    {
        public byte[] locations = Array.Empty<byte>();
        public byte[] types = Array.Empty<byte>();
        public byte[] moved = Array.Empty<byte>();
        public ulong whiteSeat = ulong.MaxValue, blackSeat = ulong.MaxValue;
        public PieceColor turn = PieceColor.White;
        public GameResult result = GameResult.InProgress;
        public sbyte epFile = -1, epRank = -1;
        public bool busy;
        public bool resultHandled;
        public int revision, plopSerial;
        public int ply;
        public string businessKey;
        // Host-only engine keeps castling/repetition/50-move history in step with the visual model.
        public Engine.Board engine;
    }

    struct ChessWire : INetworkSerializable
    {
        public byte lot, turn, result;
        public ulong whiteSeat, blackSeat;
        public sbyte epFile, epRank;
        public bool busy, reset;
        public int revision, plopSerial;
        public byte[] locations, types, moved;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref lot);
            serializer.SerializeValue(ref turn);
            serializer.SerializeValue(ref result);
            serializer.SerializeValue(ref whiteSeat);
            serializer.SerializeValue(ref blackSeat);
            serializer.SerializeValue(ref epFile);
            serializer.SerializeValue(ref epRank);
            serializer.SerializeValue(ref busy);
            serializer.SerializeValue(ref reset);
            serializer.SerializeValue(ref revision);
            serializer.SerializeValue(ref plopSerial);
            serializer.SerializeValue(ref locations);
            serializer.SerializeValue(ref types);
            serializer.SerializeValue(ref moved);
        }
    }

    readonly ChessState[] states = new ChessState[LotCount];
    readonly DepotChessBoard[] boards = new DepotChessBoard[LotCount];
    readonly Coroutine[] botLoops = new Coroutine[LotCount];
    readonly Engine.Searcher[] activeSearches = new Engine.Searcher[LotCount];
    readonly bool[] botSawHuman = new bool[LotCount];
    readonly float[] nextSnapshotRequest = new float[LotCount];
    readonly int[] nextPlanId = new int[LotCount];
    readonly Dictionary<ulong, float> lastRequest = new();
    float nextScan;

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (IsServer && NetworkManager != null) NetworkManager.OnClientDisconnectCallback += ClientLeft;
        nextScan = 0f;
        ScanBoards();
    }

    public override void OnNetworkDespawn()
    {
        for (int i = 0; i < boards.Length; i++) if (boards[i] != null) boards[i].RejectSeat();
        if (IsServer && NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= ClientLeft;
        for (int i = 0; i < activeSearches.Length; i++) activeSearches[i]?.EndSearch();
        for (int i = 0; i < botLoops.Length; i++) if (botLoops[i] != null) StopCoroutine(botLoops[i]);
        Array.Clear(botLoops, 0, botLoops.Length);
        Array.Clear(states, 0, states.Length);
        Array.Clear(boards, 0, boards.Length);
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (!IsSpawned || Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.6f;
        ScanBoards();
        if (IsServer)
        {
            for (int i = 0; i < LotCount; i++) EnsureHostState(i);
        }
        else
        {
            for (int i = 0; i < LotCount; i++)
                if (boards[i] != null && states[i] == null && Time.unscaledTime >= nextSnapshotRequest[i])
                {
                    nextSnapshotRequest[i] = Time.unscaledTime + 3f;
                    SnapshotRequestServerRpc((byte)i);
                }
        }
    }

    void ScanBoards()
    {
        var found = FindObjectsOfType<DepotChessBoard>(true);
        foreach (var board in found) Register(board);
    }

    public void Register(DepotChessBoard board)
    {
        if (board == null || board.lot < 0 || board.lot >= LotCount) return;
        int lot = board.lot;
        if (boards[lot] == board) return;
        boards[lot] = board;
        board.BuildLookups();
        if (states[lot] != null && board.isActiveAndEnabled) board.Apply(states[lot], true);
        if (IsServer) EnsureHostState(lot);
        else if (IsSpawned) SnapshotRequestServerRpc((byte)lot);
    }

    public void Unregister(DepotChessBoard board)
    {
        if (board == null || board.lot < 0 || board.lot >= LotCount) return;
        if (boards[board.lot] == board) boards[board.lot] = null;
    }

    public void Refresh(DepotChessBoard board)
    {
        if (board != null && board.isActiveAndEnabled && TryState(board.lot, out var state)) board.Apply(state, true);
    }

    public bool TryState(int lot, out ChessState state)
    {
        state = lot >= 0 && lot < LotCount ? states[lot] : null;
        return state != null;
    }

    public bool BoardAvailable(int lot)
    {
        var row = DepotRowNet.Instance;
        return row != null && row.OneWorld && row.TryGetLot(lot, out var owner) && owner.Claimed && owner.Online &&
               TryState(lot, out var state) && state.plopSerial == owner.plopSerial;
    }

    /// <summary>Used by MagnusSorter to keep the cart at chess while a person is playing on this lot.</summary>
    public bool HasHumanPlayer(int lot) => TryState(lot, out var s) &&
        (s.whiteSeat != ulong.MaxValue || s.blackSeat != ulong.MaxValue && s.blackSeat != ChessController.MagnusSeatId);

    public bool BoardReadyForPlayers(int lot) => BoardAvailable(lot) && boards[lot] != null &&
        (boards[lot].track == null || boards[lot].track.AtChess);

    public PieceColor LocalColor(int lot)
    {
        if (!TryState(lot, out var state) || NetworkManager == null) return PieceColor.White;
        return state.blackSeat == NetworkManager.LocalClientId ? PieceColor.Black : PieceColor.White;
    }

    public bool LocalTurn(int lot)
    {
        if (!BoardAvailable(lot) || !TryState(lot, out var s) || s.result != GameResult.InProgress || s.busy || NetworkManager == null) return false;
        ulong id = NetworkManager.LocalClientId;
        return s.turn == PieceColor.White ? s.whiteSeat == id : s.blackSeat == id;
    }

    public void LocalSit(int lot, PieceColor color)
    {
        if (!IsSpawned || lot < 0 || lot >= LotCount) return;
        if (IsServer) HostSit((byte)lot, color, NetworkManager.LocalClientId);
        else SitServerRpc((byte)lot, (byte)color);
    }

    public void LocalStand(int lot)
    {
        if (!IsSpawned || lot < 0 || lot >= LotCount) return;
        if (IsServer) HostStand((byte)lot, NetworkManager.LocalClientId);
        else StandServerRpc((byte)lot);
    }

    public bool LocalMove(int lot, Vector2Int from, Vector2Int to)
    {
        if (!LocalTurn(lot) || !OnBoard(from) || !OnBoard(to) || !TryState(lot, out var s)) return false;
        if (!ChessValidator.IsValidMove(Model(s, boards[lot]), from, to, s.turn, Ep(s))) return false;
        if (IsServer) HostMove((byte)lot, (byte)(from.y * 8 + from.x), (byte)(to.y * 8 + to.x), NetworkManager.LocalClientId);
        else MoveServerRpc((byte)lot, (byte)(from.y * 8 + from.x), (byte)(to.y * 8 + to.x));
        return true;
    }

    static bool OnBoard(Vector2Int c) => c.x >= 0 && c.x < 8 && c.y >= 0 && c.y < 8;
    static Vector2Int Coord(byte square) => new(square % 8, square / 8);
    static Vector2Int Ep(ChessState s) => new(s.epFile, s.epRank);

    static PieceState[,] Model(ChessState s, DepotChessBoard board)
    {
        var model = new PieceState[8, 8];
        if (board == null || board.pieces == null) return model;
        for (int i = 0; i < s.locations.Length && i < board.pieces.Length; i++)
        {
            byte loc = s.locations[i];
            if (loc >= 64 || board.pieces[i] == null) continue;
            model[loc % 8, loc / 8] = new PieceState((PieceType)s.types[i], board.pieces[i].color, s.moved[i] != 0);
        }
        return model;
    }

    void EnsureHostState(int lot)
    {
        if (!IsServer || boards[lot] == null) return;
        var row = DepotRowNet.Instance;
        if (row == null || !row.OneWorld || !row.TryGetLot(lot, out var owner) || !owner.Claimed)
        {
            activeSearches[lot]?.EndSearch();
            states[lot] = null;
            return;
        }
        string key = owner.businessKey.ToString();
        if (states[lot] != null && states[lot].businessKey == key && states[lot].plopSerial == owner.plopSerial) return;
        activeSearches[lot]?.EndSearch();
        var board = boards[lot];
        if (board.pieces == null || board.pieces.Length != MaxPieces)
        {
            Debug.LogWarning($"[DepotChess] Lot {lot + 1}: expected 32 pieces; found {board.pieces?.Length ?? 0}.");
            return;
        }
        var state = new ChessState
        {
            businessKey = key,
            plopSerial = owner.plopSerial,
            locations = new byte[MaxPieces],
            types = new byte[MaxPieces],
            moved = new byte[MaxPieces]
        };
        for (int i = 0; i < MaxPieces; i++)
        {
            state.locations[i] = board.InitialLocation(i);
            state.types[i] = (byte)board.pieces[i].startingType;
        }
        state.engine = Engine.Board.CreateBoard(Fen(state, board));
        states[lot] = state;
        botSawHuman[lot] = false;
        if (board.isActiveAndEnabled) board.Apply(state, true);
        Broadcast(lot, true);
        if (botLoops[lot] == null) botLoops[lot] = StartCoroutine(BotLoop(lot));
    }

    string Fen(ChessState state, DepotChessBoard visual)
    {
        var model = Model(state, visual);
        var sb = new System.Text.StringBuilder();
        for (int y = 7; y >= 0; y--)
        {
            int empty = 0;
            for (int x = 0; x < 8; x++)
            {
                var p = model[x, y];
                if (p.Type == PieceType.None) { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                char c = p.Type switch { PieceType.Pawn => 'p', PieceType.Rook => 'r', PieceType.Knight => 'n', PieceType.Bishop => 'b', PieceType.Queen => 'q', _ => 'k' };
                sb.Append(p.Color == PieceColor.White ? char.ToUpperInvariant(c) : c);
            }
            if (empty > 0) sb.Append(empty);
            if (y > 0) sb.Append('/');
        }
        sb.Append(state.turn == PieceColor.White ? " w " : " b ");
        string castle = "";
        if (CanCastle(state, visual, PieceColor.White, true)) castle += "K";
        if (CanCastle(state, visual, PieceColor.White, false)) castle += "Q";
        if (CanCastle(state, visual, PieceColor.Black, true)) castle += "k";
        if (CanCastle(state, visual, PieceColor.Black, false)) castle += "q";
        sb.Append(castle.Length > 0 ? castle : "-");
        sb.Append(state.epFile >= 0 ? $" {(char)('a' + state.epFile)}{state.epRank + 1}" : " -");
        sb.Append(" 0 1");
        return sb.ToString();
    }

    static bool CanCastle(ChessState s, DepotChessBoard board, PieceColor color, bool kingSide)
    {
        byte rank = color == PieceColor.White ? (byte)0 : (byte)7;
        int king = At(s, (byte)(rank * 8 + 4)), rook = At(s, (byte)(rank * 8 + (kingSide ? 7 : 0)));
        return king >= 0 && rook >= 0 && s.types[king] == (byte)PieceType.King && s.types[rook] == (byte)PieceType.Rook &&
               board.pieces[king].color == color && board.pieces[rook].color == color && s.moved[king] == 0 && s.moved[rook] == 0;
    }

    static int At(ChessState s, byte square)
    {
        for (int i = 0; i < s.locations.Length; i++) if (s.locations[i] == square) return i;
        return -1;
    }

    /// <summary>Pure authority gate and state transition used by the host. A seat is scoped to exactly one lot.</summary>
    public static bool TrySeat(ChessState[] games, int lot, PieceColor color, ulong clientId, bool nearBoard, bool boardReady)
    {
        if (!nearBoard || !boardReady || games == null || lot < 0 || lot >= games.Length || games[lot] == null ||
            clientId == ulong.MaxValue || clientId == ChessController.MagnusSeatId) return false;
        for (int i = 0; i < games.Length; i++)
            if (games[i] != null && (games[i].whiteSeat == clientId || games[i].blackSeat == clientId)) return false;
        var game = games[lot];
        if (color == PieceColor.White && game.whiteSeat == ulong.MaxValue) { game.whiteSeat = clientId; return true; }
        if (color == PieceColor.Black && game.blackSeat == ulong.MaxValue) { game.blackSeat = clientId; return true; }
        return false;
    }

    /// <summary>Pure owner/turn guard. The host additionally applies chess legality before committing.</summary>
    public static bool CanMove(ChessState game, ulong clientId, byte from, byte to)
    {
        return game != null && from < 64 && to < 64 && !game.busy && game.result == GameResult.InProgress &&
               (game.turn == PieceColor.White ? game.whiteSeat : game.blackSeat) == clientId;
    }

    /// <summary>Leaving is idempotent and deliberately unthrottled, even immediately after sitting.</summary>
    public static bool TryStand(ChessState[] games, int lot, ulong clientId)
    {
        if (games == null || lot < 0 || lot >= games.Length || games[lot] == null) return false;
        var game = games[lot];
        if (game.whiteSeat == clientId) { game.whiteSeat = ulong.MaxValue; return true; }
        if (game.blackSeat == clientId) { game.blackSeat = ulong.MaxValue; return true; }
        return false;
    }

    static byte CaptureSlot(ChessState s, DepotChessBoard board, PieceColor color)
    {
        int count = 0;
        for (int i = 0; i < s.locations.Length; i++) if (s.locations[i] >= 64 && s.locations[i] < 255 && board.pieces[i].color == color) count++;
        return (byte)(64 + count);
    }

    /// <summary>Apply a previously validated move to compact replicated state; the host advances Chess.Core separately.</summary>
    public static bool ApplyMoveState(ChessState s, PieceColor[] colors, byte from, byte to)
    {
        if (s == null || colors == null || from >= 64 || to >= 64 ||
            s.locations == null || s.types == null || s.moved == null || colors.Length != s.locations.Length ||
            s.types.Length != s.locations.Length || s.moved.Length != s.locations.Length) return false;
        int mover = At(s, from);
        if (mover < 0) return false;
        PieceType movingType = (PieceType)s.types[mover];
        bool enPassant = movingType == PieceType.Pawn && from % 8 != to % 8 && At(s, to) < 0 &&
                         s.epFile == to % 8 && s.epRank == to / 8;
        byte captureSquare = enPassant ? (byte)(from / 8 * 8 + to % 8) : to;
        int captured = At(s, captureSquare);
        if (captured >= 0)
        {
            int count = 0;
            for (int i = 0; i < s.locations.Length; i++)
                if (s.locations[i] >= 64 && s.locations[i] < 255 && colors[i] == colors[captured]) count++;
            s.locations[captured] = (byte)(64 + count);
        }
        s.locations[mover] = to;
        s.moved[mover] = 1;
        if (movingType == PieceType.King && Math.Abs(to % 8 - from % 8) == 2)
        {
            int rank = from / 8;
            bool kingSide = to % 8 > from % 8;
            int rook = At(s, (byte)(rank * 8 + (kingSide ? 7 : 0)));
            if (rook >= 0) { s.locations[rook] = (byte)(rank * 8 + (kingSide ? 5 : 3)); s.moved[rook] = 1; }
        }
        s.epFile = s.epRank = -1;
        if (movingType == PieceType.Pawn && Math.Abs(to / 8 - from / 8) == 2)
        {
            s.epFile = (sbyte)(from % 8);
            s.epRank = (sbyte)((from / 8 + to / 8) / 2);
        }
        if (movingType == PieceType.Pawn && (to / 8 == 0 || to / 8 == 7)) s.types[mover] = (byte)PieceType.Queen;
        s.turn = s.turn == PieceColor.White ? PieceColor.Black : PieceColor.White;
        s.ply++;
        return true;
    }

    /// <summary>Reset compact state from this board's immutable home layout.</summary>
    public static void ResetMoveState(ChessState s, byte[] homeLocations, byte[] homeTypes)
    {
        if (s == null || homeLocations == null || homeTypes == null || homeLocations.Length != homeTypes.Length) return;
        s.locations = (byte[])homeLocations.Clone();
        s.types = (byte[])homeTypes.Clone();
        s.moved = new byte[homeLocations.Length];
        s.turn = PieceColor.White;
        s.result = GameResult.InProgress;
        s.epFile = s.epRank = -1;
        s.busy = false;
        s.ply = 0;
        s.resultHandled = false;
    }

    void ClientLeft(ulong id)
    {
        for (int i = 0; i < LotCount; i++)
        {
            var s = states[i];
            if (s == null) continue;
            bool changed = false;
            if (s.whiteSeat == id) { s.whiteSeat = ulong.MaxValue; changed = true; }
            if (s.blackSeat == id) { s.blackSeat = ulong.MaxValue; changed = true; }
            if (changed) Broadcast(i, false);
        }
    }

    static ClientRpcParams To(ulong id) => new() { Send = new ClientRpcSendParams { TargetClientIds = new[] { id } } };

    [ServerRpc(RequireOwnership = false)]
    void SnapshotRequestServerRpc(byte lot, ServerRpcParams rpc = default)
    {
        if (lot >= LotCount) return;
        EnsureHostState(lot);
        if (states[lot] != null) SnapshotClientRpc(Pack(lot, true), To(rpc.Receive.SenderClientId));
    }

    [ServerRpc(RequireOwnership = false)]
    void SitServerRpc(byte lot, byte color, ServerRpcParams rpc = default)
    {
        if (color <= 1) HostSit(lot, (PieceColor)color, rpc.Receive.SenderClientId);
    }

    [ServerRpc(RequireOwnership = false)]
    void StandServerRpc(byte lot, ServerRpcParams rpc = default) => HostStand(lot, rpc.Receive.SenderClientId);

    [ServerRpc(RequireOwnership = false)]
    void MoveServerRpc(byte lot, byte from, byte to, ServerRpcParams rpc = default) => HostMove(lot, from, to, rpc.Receive.SenderClientId);

    bool Allowed(ulong id, float interval)
    {
        float now = Time.unscaledTime;
        if (lastRequest.TryGetValue(id, out var last) && now - last < interval) return false;
        lastRequest[id] = now;
        return true;
    }

    bool ValidUser(byte lot, ulong id, bool mustBeNear)
    {
        if (lot >= LotCount || boards[lot] == null || !BoardAvailable(lot)) return false;
        if (!mustBeNear) return true;
        var player = NetworkManager.SpawnManager.GetPlayerNetworkObject(id);
        return player != null && Vector3.Distance(player.transform.position, boards[lot].transform.position) <= 7f;
    }

    void HostSit(byte lot, PieceColor color, ulong id)
    {
        if (lot < LotCount) EnsureHostState(lot);
        bool near = ValidUser(lot, id, true);
        bool ready = lot < LotCount && BoardReadyForPlayers(lot);
        if (!Allowed(id, 0.15f) || !TrySeat(states, lot, color, id, near, ready)) { RejectSeatClientRpc(lot, To(id)); return; }
        var s = states[lot];
        s.busy = s.blackSeat == ChessController.MagnusSeatId;
        Broadcast(lot, false);
    }

    void HostStand(byte lot, ulong id)
    {
        if (!TryStand(states, lot, id)) return;
        Broadcast(lot, false);
    }

    void HostMove(byte lot, byte from, byte to, ulong id)
    {
        if (lot < LotCount) EnsureHostState(lot);
        if (lot >= LotCount || from >= 64 || to >= 64 || states[lot] == null) return;
        if (!Allowed(id, 0.12f)) { SnapshotClientRpc(Pack(lot, false), To(id)); return; }
        var s = states[lot];
        if (!ValidUser(lot, id, false) || !CanMove(s, id, from, to) || !IsLegal(lot, from, to, out var engineMove))
        {
            SnapshotClientRpc(Pack(lot, false), To(id));
            return;
        }
        Commit(lot, from, to, engineMove);
    }

    bool IsLegal(int lot, byte from, byte to, out Engine.Move engineMove)
    {
        engineMove = Engine.Move.NullMove;
        var s = states[lot];
        var model = Model(s, boards[lot]);
        if (!ChessValidator.IsValidMove(model, Coord(from), Coord(to), s.turn, Ep(s))) return false;
        var generator = new Engine.MoveGenerator();
        foreach (var candidate in generator.GenerateMoves(s.engine))
        {
            if (candidate.StartSquare != from || candidate.TargetSquare != to) continue;
            if (candidate.IsPromotion && candidate.PromotionPieceType != Engine.Piece.Queen) continue;
            engineMove = candidate;
            return true;
        }
        return false;
    }

    void Commit(int lot, byte from, byte to, Engine.Move engineMove)
    {
        var s = states[lot];
        var board = boards[lot];
        var colors = new PieceColor[board.pieces.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = board.pieces[i].color;
        if (!ApplyMoveState(s, colors, from, to)) return;
        s.engine.MakeMove(engineMove);
        s.result = Engine.Arbiter.GetGameState(s.engine) switch
        {
            Engine.GameResult.WhiteIsMated => GameResult.BlackWins,
            Engine.GameResult.BlackIsMated => GameResult.WhiteWins,
            Engine.GameResult.Stalemate => GameResult.Stalemate,
            Engine.GameResult.Repetition or Engine.GameResult.FiftyMoveRule or Engine.GameResult.InsufficientMaterial => GameResult.Draw,
            _ => GameResult.InProgress
        };
        s.busy = false;
        if (s.result != GameResult.InProgress) HostReportResult(lot, s);
        Broadcast(lot, false);
    }

    void HostReportResult(int lot, ChessState s)
    {
        if (s == null || s.result == GameResult.InProgress || s.resultHandled) return;
        s.resultHandled = true;
        if (s.blackSeat != ChessController.MagnusSeatId) return;
        if (s.whiteSeat != ulong.MaxValue)
        {
            bool won = s.result == GameResult.WhiteWins;
            int resultTier = TierForLot(lot);
            ChessCareerClientRpc(won, s.ply, resultTier, To(s.whiteSeat));
            string line = won ? ChessBanter.Lost(resultTier, s.revision) :
                s.result == GameResult.BlackWins ? ChessBanter.Won(resultTier, s.revision) : ChessBanter.Drew(resultTier, s.revision);
            SayClientRpc((byte)lot, line);
        }
        GestureClientRpc((byte)lot, s.result == GameResult.BlackWins ? MagnusArm.Gesture.Celebrate : MagnusArm.Gesture.Sulk);
    }

    void Broadcast(int lot, bool reset)
    {
        var s = states[lot];
        if (s == null) return;
        s.revision++;
        if (boards[lot] != null && boards[lot].isActiveAndEnabled) boards[lot].Apply(s, reset);
        SnapshotClientRpc(Pack(lot, reset));
    }

    ChessWire Pack(int lot, bool reset)
    {
        var s = states[lot];
        return new ChessWire
        {
            lot = (byte)lot, turn = (byte)s.turn, result = (byte)s.result,
            whiteSeat = s.whiteSeat, blackSeat = s.blackSeat, epFile = s.epFile, epRank = s.epRank,
            busy = s.busy, reset = reset, revision = s.revision, plopSerial = s.plopSerial,
            locations = s.locations, types = s.types, moved = s.moved
        };
    }

    [ClientRpc]
    void SnapshotClientRpc(ChessWire wire, ClientRpcParams rpc = default)
    {
        if (IsServer || wire.lot >= LotCount || wire.locations == null || wire.locations.Length != MaxPieces ||
            wire.types == null || wire.types.Length != MaxPieces || wire.moved == null || wire.moved.Length != MaxPieces) return;
        int lot = wire.lot;
        if (states[lot] != null && (wire.plopSerial < states[lot].plopSerial ||
                                    wire.plopSerial == states[lot].plopSerial && wire.revision <= states[lot].revision)) return;
        states[lot] = new ChessState
        {
            locations = wire.locations, types = wire.types, moved = wire.moved,
            whiteSeat = wire.whiteSeat, blackSeat = wire.blackSeat,
            turn = (PieceColor)wire.turn, result = (GameResult)wire.result,
            epFile = wire.epFile, epRank = wire.epRank, busy = wire.busy,
            revision = wire.revision, plopSerial = wire.plopSerial
        };
        if (boards[lot] != null && boards[lot].isActiveAndEnabled) boards[lot].Apply(states[lot], wire.reset);
    }

    [ClientRpc]
    void RejectSeatClientRpc(byte lot, ClientRpcParams rpc = default)
    {
        if (lot < LotCount && boards[lot] != null) boards[lot].RejectSeat();
    }

    // Magnus shares the same authoritative model. The arm plan is addressed by local piece indices, never scene NObj IDs.
    IEnumerator BotLoop(int lot)
    {
        while (IsServer && IsSpawned)
        {
            var s = states[lot];
            var board = boards[lot];
            var row = DepotRowNet.Instance;
            if (s == null || board == null || row == null || !row.TryGetLot(lot, out var owner) || !owner.Claimed || !owner.Online)
            {
                yield return new WaitForSeconds(0.4f);
                continue;
            }
            bool atChess = board.magnusPlays && board.arm != null && (!board.track || board.track.AtChess);
            if (atChess && s.blackSeat == ulong.MaxValue) { s.blackSeat = ChessController.MagnusSeatId; Broadcast(lot, false); }
            else if (!atChess && s.blackSeat == ChessController.MagnusSeatId) { s.blackSeat = ulong.MaxValue; Broadcast(lot, false); }
            bool human = s.whiteSeat != ulong.MaxValue || s.blackSeat != ulong.MaxValue && s.blackSeat != ChessController.MagnusSeatId;
            if (human != botSawHuman[lot])
            {
                botSawHuman[lot] = human;
                ResetBoard(lot);
                yield return new WaitForSeconds(1f);
                continue;
            }
            if (s.result != GameResult.InProgress)
            {
                HostReportResult(lot, s);
                yield return new WaitForSeconds(5f);
                if (states[lot] == s) ResetBoard(lot);
                continue;
            }
            if (!atChess || s.busy || s.blackSeat != ChessController.MagnusSeatId)
            {
                yield return new WaitForSeconds(0.15f);
                continue;
            }
            if (human && s.turn != PieceColor.Black)
            {
                yield return new WaitForSeconds(0.15f);
                continue;
            }
            // A guest's Magnus strength belongs to that guest's business, not to the host's local save.
            if (!BusinessUpgradeNet.HasStateForLot(lot))
            {
                yield return new WaitForSeconds(0.25f);
                continue;
            }
            s.busy = true;
            Broadcast(lot, false);
            ulong expectedWhite = s.whiteSeat, expectedBlack = s.blackSeat;
            int tier = TierForLot(lot);
            var spec = UpgradeEffects.Chess(tier);
            var searcher = new Engine.Searcher(s.engine) { MaxDepth = spec.maxDepth };
            activeSearches[lot] = searcher;
            Task search = Task.Run(() => searcher.StartSearch());
            float think = Mathf.Max(0.12f, spec.thinkSeconds);
            float deadline = Time.realtimeSinceStartup + think;
            while (!search.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            searcher.EndSearch();
            while (!search.IsCompleted) yield return null;
            activeSearches[lot] = null;
            if (states[lot] != s) continue;
            if (s.whiteSeat != expectedWhite || s.blackSeat != expectedBlack ||
                board.track != null && !board.track.AtChess)
            {
                s.busy = false;
                Broadcast(lot, false);
                continue;
            }
            var moves = LegalMoves(s.engine);
            if (moves.Count == 0) { s.busy = false; Broadcast(lot, false); yield return null; continue; }
            Engine.Move move = search.IsFaulted ? Engine.Move.NullMove : searcher.GetSearchResult().move;
            if (move.IsNull) move = moves[NightDice.Host.Range(0, moves.Count)];
            if (human && MagnusChessBot.RollBlunder(spec.blunder, s.engine.CurrentGameState.zobristKey, s.revision, s.plopSerial))
            {
                move = moves[MagnusChessBot.PickIndex(moves.Count, s.engine.CurrentGameState.zobristKey, s.revision, s.plopSerial)];
                GestureClientRpc((byte)lot, MagnusArm.Gesture.Shrug);
                SayClientRpc((byte)lot, ChessBanter.Blunder(tier, s.revision));
            }
            byte from = (byte)move.StartSquare, to = (byte)move.TargetSquare;
            if (!IsLegal(lot, from, to, out var legal)) { s.busy = false; Broadcast(lot, false); continue; }
            BuildPlan(lot, from, to, out var indices, out var targets, out byte promotionIndex);
            int planId = ++nextPlanId[lot];
            PlanClientRpc((byte)lot, planId, indices, targets, promotionIndex, PieceType.Queen);
            bool watchLocalArm = IsClient && board.arm != null && board.isActiveAndEnabled;
            float armDeadline = Time.realtimeSinceStartup + (watchLocalArm ? 12f : 2.5f * indices.Length);
            while (Time.realtimeSinceStartup < armDeadline &&
                   (!watchLocalArm || board.arm.LastCompletedPlanId < planId) &&
                   s.whiteSeat == expectedWhite && s.blackSeat == expectedBlack &&
                   (board.track == null || board.track.AtChess)) yield return null;
            if (states[lot] != s) continue;
            if (s.whiteSeat != expectedWhite || s.blackSeat != expectedBlack ||
                board.track != null && !board.track.AtChess)
            {
                s.busy = false;
                Broadcast(lot, false);
                continue;
            }
            Commit(lot, from, to, legal);
            yield return new WaitForSeconds(human ? 0.35f : NightDice.Host.Range(0.4f, 1.2f));
        }
        botLoops[lot] = null;
    }

    // Chess.Core exposes a Span enumerator, which cannot live across a coroutine's yield points.
    static List<Engine.Move> LegalMoves(Engine.Board engine)
    {
        var moves = new List<Engine.Move>();
        foreach (var move in new Engine.MoveGenerator().GenerateMoves(engine)) moves.Add(move);
        return moves;
    }

    void BuildPlan(int lot, byte from, byte to, out byte[] indices, out byte[] targets, out byte promotionIndex)
    {
        var s = states[lot];
        var board = boards[lot];
        int mover = At(s, from);
        var ids = new List<byte>();
        var locations = new List<byte>();
        bool ep = mover >= 0 && s.types[mover] == (byte)PieceType.Pawn && from % 8 != to % 8 && At(s, to) < 0;
        int captured = At(s, ep ? (byte)(from / 8 * 8 + to % 8) : to);
        if (captured >= 0) { ids.Add((byte)captured); locations.Add(CaptureSlot(s, board, board.pieces[captured].color)); }
        ids.Add((byte)mover); locations.Add(to);
        if (s.types[mover] == (byte)PieceType.King && Math.Abs(to % 8 - from % 8) == 2)
        {
            int rank = from / 8; bool right = to % 8 > from % 8;
            int rook = At(s, (byte)(rank * 8 + (right ? 7 : 0)));
            if (rook >= 0) { ids.Add((byte)rook); locations.Add((byte)(rank * 8 + (right ? 5 : 3))); }
        }
        promotionIndex = s.types[mover] == (byte)PieceType.Pawn && (to / 8 == 0 || to / 8 == 7) ? (byte)mover : NoLocation;
        indices = ids.ToArray(); targets = locations.ToArray();
    }

    void ResetBoard(int lot)
    {
        var s = states[lot]; var board = boards[lot];
        if (s == null || board == null) return;
        var home = new byte[board.pieces.Length];
        var homeTypes = new byte[board.pieces.Length];
        for (int i = 0; i < s.locations.Length; i++)
        {
            home[i] = board.InitialLocation(i);
            homeTypes[i] = (byte)board.pieces[i].startingType;
        }
        ResetMoveState(s, home, homeTypes);
        s.engine = Engine.Board.CreateBoard(Fen(s, board));
        Broadcast(lot, true);
    }

    static int TierForLot(int lot)
    {
        return UpgradeEffects.ChessTierFor(
            BusinessUpgradeNet.LevelForLot(lot, ShopIds.MagnusRailMotor),
            BusinessUpgradeNet.LevelForLot(lot, ShopIds.MagnusGripper),
            BusinessUpgradeNet.LevelForLot(lot, ShopIds.MagnusVision),
            BusinessUpgradeNet.LevelForLot(lot, ShopIds.MagnusBrain));
    }

    [ClientRpc]
    void PlanClientRpc(byte lot, int planId, byte[] indices, byte[] targets, byte promotionIndex, PieceType promotion)
    {
        if (lot < LotCount && boards[lot] != null && boards[lot].isActiveAndEnabled)
            boards[lot].PlayPlan(planId, indices, targets, promotionIndex, promotion);
    }

    [ClientRpc]
    void GestureClientRpc(byte lot, MagnusArm.Gesture gesture)
    {
        if (lot < LotCount && boards[lot] != null && boards[lot].isActiveAndEnabled) boards[lot].PlayGesture(gesture);
    }

    [ClientRpc]
    void SayClientRpc(byte lot, string line)
    {
        if (lot < LotCount && boards[lot] != null && boards[lot].isActiveAndEnabled) boards[lot].Say(line);
    }

    [ClientRpc]
    void ChessCareerClientRpc(bool won, int ply, int tier, ClientRpcParams rpc = default)
    {
        if (GameSession.IsGuest) Progression.ApplyHostChessResult(won, ply, tier);
        else ProgressionEvents.RaiseChessGameEnded(won, ply, tier);
    }
}
