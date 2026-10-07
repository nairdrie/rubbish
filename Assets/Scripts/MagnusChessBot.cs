using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using Engine = Chess.Core;

/// <summary>
/// Magnus's brain. The server runs the chess engine in Assets/Scripts/Core and decides what Magnus
/// does: while nobody sits at the board he plays both sides against himself, and when a player sits
/// down he tidies the board and plays Black against them. Every physical move is broadcast so the
/// arm animates the same way on every client, and the move is only committed once the piece has
/// been set down.
///
/// Strength comes from his upgrades (Docs/Progression.md §6.3): chess tier = rail + gripper + vision + brain
/// (<see cref="UpgradeEffects.MagnusChessTier"/>), which sets his think time, the search depth cap and how often he
/// blunders against a person (a deterministic roll; a blunder is a random legal move with a shrug). He trash-talks by
/// tier band, and every finished game against a person raises <see cref="ProgressionEvents.ChessGameEnded"/> (server).
/// </summary>
[RequireComponent(typeof(ChessController))]
public class MagnusChessBot : NetworkBehaviour
{
    [SerializeField] MagnusArm arm;
    [Tooltip("Optional: Magnus's rail cart. He only plays while it's parked at the chess board.")]
    [SerializeField] MagnusTrack track;
    [Tooltip("Turn off to hand the Black seat back to human players.")]
    [SerializeField] bool magnusPlays = true;

    [Header("Playing a person")]
    [Tooltip("Legacy fallback (seconds per move) when the strength tiers are off. Normally the tier's think time is used.")]
    [SerializeField] float thinkSeconds = 0.6f;
    [Tooltip("Strength from Magnus's upgrades (chess tier 0..11). Off = today's fixed think time, no depth cap, no blunders.")]
    [SerializeField] bool useStrengthTiers = true;

    [Header("Playing himself while nobody's at the board")]
    [SerializeField] Vector2 selfPlayThinkSeconds = new(0.4f, 1.4f);
    [SerializeField] Vector2 selfPlayPauseSeconds = new(0.4f, 1.2f);
    [Tooltip("Self-play games are called a draw after this many half-moves.")]
    [SerializeField] int selfPlayMaxPlies = 300;
    [SerializeField] float gameOverPauseSeconds = 5f;

    // A few popular opening moves so self-play games don't all start the same way.
    static readonly string[] OpeningMoves =
    {
        "e2e4", "d2d4", "c2c4", "g1f3", "e7e5", "c7c5", "e7e6", "c7c6", "d7d5", "g8f6",
        "b1c3", "f1c4", "f1b5", "d7d6", "g7g6", "b8c6", "f2f4", "e2e3",
    };

    ChessController chess;
    Engine.Board board;
    Engine.Searcher searcher;
    readonly Engine.MoveGenerator moveGenerator = new();
    Task searchTask;
    int nextPlanId = 1;
    int pliesThisGame;
    int gamesPlayed;
    Coroutine serverLoop;

    public bool MagnusPlays => magnusPlays;

    /// <summary>The chess tier Magnus plays at right now (0..11, from his upgrades).</summary>
    public static int Tier => UpgradeEffects.MagnusChessTier;
    /// <summary>Seconds he searches per move at the current tier.</summary>
    public float ThinkSeconds => useStrengthTiers ? UpgradeEffects.Chess(Tier).thinkSeconds : thinkSeconds;
    /// <summary>Iterative-deepening cap at the current tier (0 = uncapped).</summary>
    public int MaxDepth => useStrengthTiers ? UpgradeEffects.Chess(Tier).maxDepth : 0;
    /// <summary>Chance a move against a person is a random legal move (a blunder) at the current tier.</summary>
    public float BlunderChance => useStrengthTiers ? UpgradeEffects.Chess(Tier).blunder : 0f;
    /// <summary>Moves Magnus blundered (server, this session). Handy for tests and banter.</summary>
    public int Blunders { get; private set; }

    void Awake()
    {
        chess = GetComponent<ChessController>();
        // If this client's arm is still mid-move when the server's board update lands, finish it first.
        chess.BeforeBoardSync = () => { if (arm && (!track || track.AtChess) && arm.HasPendingPlans) arm.FastForward(); };
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        board = Engine.Board.CreateBoard(chess.ServerGetFen());
        searcher = new Engine.Searcher(board);
        chess.ExtraMoveCheck = IsLegalForEngine;
        chess.ServerMoveCommitted += OnMoveCommitted;
        chess.ServerBoardReset += OnBoardReset;
        serverLoop = StartCoroutine(ServerLoop());
    }

    public override void OnNetworkDespawn()
    {
        if (serverLoop != null) StopCoroutine(serverLoop);
        searcher?.EndSearch();
        if (chess)
        {
            chess.ExtraMoveCheck = null;
            chess.ServerMoveCommitted -= OnMoveCommitted;
            chess.ServerBoardReset -= OnBoardReset;
        }
    }

    public override void OnDestroy()
    {
        searcher?.EndSearch();
        base.OnDestroy();
    }

    void Update()
    {
        if (!arm || !IsSpawned) return;
        // Away from the board (commuting / sorting): the sort job owns his idle pose.
        if (track && !track.AtChess) return;
        // Face the player while they think; otherwise hover over the board.
        bool humansTurn = chess.HasHumanPlayer && chess.Result == GameResult.InProgress && !chess.IsMagnusTurn;
        arm.Idle = humansTurn ? MagnusArm.IdlePose.Watch : MagnusArm.IdlePose.Think;
    }

    // ------------------------------------------------------------------ server game loop

    IEnumerator ServerLoop()
    {
        yield return new WaitForSeconds(1f);
        bool humanGame = chess.HasHumanPlayer;

        while (true)
        {
            bool atBoard = !track || track.AtChess;
            chess.ServerSetMagnusSeat(magnusPlays && arm && atBoard);
            if (!magnusPlays || !arm || !atBoard)
            {
                yield return null;
                continue;
            }

            // Someone sat down or got up: start a fresh game.
            if (chess.HasHumanPlayer != humanGame)
            {
                humanGame = chess.HasHumanPlayer;
                yield return ResetBoard();
                continue;
            }

            if (chess.Result != GameResult.InProgress)
            {
                yield return GameOver(humanGame);
                continue;
            }

            if (!chess.IsMagnusTurn)
            {
                yield return null;
                continue;
            }

            if (!humanGame)
            {
                yield return new WaitForSeconds(Random.Range(selfPlayPauseSeconds.x, selfPlayPauseSeconds.y));
                if (chess.HasHumanPlayer != humanGame || track != null && !track.AtChess) continue;
            }

            // Search budget comes from his tier; self-play keeps its relaxed visual pace on top (same strength).
            float search = ThinkSeconds;
            float seconds = humanGame ? search : Mathf.Max(search, Random.Range(selfPlayThinkSeconds.x, selfPlayThinkSeconds.y));
            Engine.Move move = Engine.Move.NullMove;
            yield return Think(seconds, search, !humanGame, m => move = m);

            // Abandon the move if someone sat down or left while Magnus was thinking.
            if (chess.HasHumanPlayer != humanGame || move.IsNull || chess.Result != GameResult.InProgress ||
                track != null && !track.AtChess) continue;

            // Blunders only against a person (self-play never blunders): a random legal move and a shrug.
            bool blundered = false;
            if (humanGame && TryBlunder(out var blunder))
            {
                move = blunder;
                blundered = true;
                Blunders++;
                PlayGestureClientRpc(MagnusArm.Gesture.Shrug);
                Say(ChessBanter.Blunder(Tier, BanterSeed(17)));
            }
            else if (humanGame && Hash01(BanterSeed(3)) < 0.18f) Say(ChessBanter.Move(Tier, BanterSeed(5)));

            yield return PlayMove(move);
            if (blundered) yield return new WaitForSeconds(0.2f);
            // Garage Disco Ball: now and then he busts a move between self-play moves.
            else if (!humanGame && UpgradeEffects.Level(ShopIds.GarageDiscoBall) > 0 && Hash01(BanterSeed(29)) < 0.12f)
            {
                PlayGestureClientRpc(MagnusArm.Gesture.Celebrate);
                yield return new WaitForSeconds(2.5f);
            }
        }
    }

    /// <summary>
    /// Search for up to <paramref name="searchSeconds"/> (capped at the tier's depth), then keep "thinking" until
    /// <paramref name="seconds"/> have passed (self-play pacing).
    /// </summary>
    IEnumerator Think(float seconds, float searchSeconds, bool varyOpening, System.Action<Engine.Move> done)
    {
        if (varyOpening && pliesThisGame < 4 && TryOpeningMove(out var opening))
        {
            yield return new WaitForSeconds(seconds * 0.6f);
            done(opening);
            yield break;
        }

        searcher.MaxDepth = MaxDepth;
        float started = Time.time;
        searchTask = Task.Run(() => searcher.StartSearch());
        for (float t = 0f; t < searchSeconds && !searchTask.IsCompleted; t += Time.deltaTime) yield return null;
        searcher.EndSearch();
        while (!searchTask.IsCompleted) yield return null;
        while (Time.time - started < seconds) yield return null;

        if (searchTask.IsFaulted)
        {
            Debug.LogException(searchTask.Exception);
            var moves = LegalMoves();
            done(moves.Count > 0 ? moves[Random.Range(0, moves.Count)] : Engine.Move.NullMove);
        }
        else
        {
            done(searcher.GetSearchResult().move);
        }
        searchTask = null;
    }

    bool TryOpeningMove(out Engine.Move move)
    {
        var legal = LegalMoves();
        var candidates = new List<Engine.Move>();
        foreach (string uci in OpeningMoves)
            foreach (var m in legal)
                if (Engine.MoveUtility.GetMoveNameUCI(m) == uci) candidates.Add(m);
        move = candidates.Count > 0 ? candidates[Random.Range(0, candidates.Count)] : Engine.Move.NullMove;
        return candidates.Count > 0;
    }

    IEnumerator PlayMove(Engine.Move move)
    {
        Vector2Int from = Coord(move.StartSquare), to = Coord(move.TargetSquare);
        PieceType promotion = move.IsPromotion ? ToPieceType(move.PromotionPieceType) : PieceType.Queen;

        var plan = chess.ServerPlanMove(from, to, promotion);
        plan.id = nextPlanId++;

        chess.ServerSetBusy(true);
        PlayPlanClientRpc(plan);
        if (IsClient)
        {
            // The host watches its own arm finish before committing.
            while (arm.LastCompletedPlanId < plan.id) yield return null;
        }
        else
        {
            // A dedicated server has no one to animate for; give clients time to play it out.
            yield return new WaitForSeconds(4f + 2.5f * plan.pieces.Length);
        }

        chess.ServerCommitMove(from, to, promotion);
        chess.ServerSetBusy(false);
    }

    IEnumerator ResetBoard()
    {
        chess.ServerSetBusy(true);
        PlayGestureClientRpc(MagnusArm.Gesture.Sweep);
        yield return new WaitForSeconds(0.7f);
        chess.ServerResetBoard();
        yield return new WaitForSeconds(1.8f);
        chess.ServerSetBusy(false);
    }

    IEnumerator GameOver(bool humanGame)
    {
        var result = chess.Result;
        MagnusArm.Gesture reaction = MagnusArm.Gesture.Shrug;
        if (humanGame && result == GameResult.BlackWins) reaction = MagnusArm.Gesture.Celebrate;
        else if (humanGame && result == GameResult.WhiteWins) reaction = MagnusArm.Gesture.Sulk;
        else if (!humanGame && result != GameResult.Draw && result != GameResult.Stalemate) reaction = MagnusArm.Gesture.Celebrate;
        // Disco ball installed: draws in self-play become a little dance too.
        if (!humanGame && reaction == MagnusArm.Gesture.Shrug && UpgradeEffects.Level(ShopIds.GarageDiscoBall) > 0)
            reaction = MagnusArm.Gesture.Celebrate;

        gamesPlayed++;
        if (humanGame)
        {
            // The human plays White whenever Magnus holds the Black seat.
            bool humanWon = result == GameResult.WhiteWins;
            int tier = Tier;
            ProgressionEvents.RaiseChessGameEnded(humanWon, pliesThisGame, tier);
            Say(humanWon ? ChessBanter.Lost(tier, BanterSeed(11)) : result == GameResult.BlackWins ? ChessBanter.Won(tier, BanterSeed(13)) : ChessBanter.Drew(tier, BanterSeed(7)));
        }

        yield return new WaitForSeconds(1f);
        PlayGestureClientRpc(reaction);
        yield return new WaitForSeconds(gameOverPauseSeconds);
        yield return ResetBoard();
    }

    // ------------------------------------------------------------------ strength: blunders + banter

    /// <summary>
    /// Deterministic blunder roll for this position (same answer for the same game, ply and position), so it's
    /// host-decided and replayable. No UnityEngine.Random: Docs/Multiplayer.md §9 rule 2.
    /// </summary>
    bool TryBlunder(out Engine.Move move)
    {
        move = Engine.Move.NullMove;
        float chance = BlunderChance;
        if (chance <= 0f) return false;
        ulong key = board.CurrentGameState.zobristKey;
        if (!RollBlunder(chance, key, pliesThisGame, gamesPlayed)) return false;
        var legal = LegalMoves();
        if (legal.Count == 0) return false;
        move = legal[PickIndex(legal.Count, key, pliesThisGame, gamesPlayed)];
        return true;
    }

    /// <summary>Pure blunder roll (exposed for tests): hash(position, ply, game) &lt; chance.</summary>
    public static bool RollBlunder(float chance, ulong positionKey, int ply, int game)
    {
        if (chance <= 0f) return false;
        if (chance >= 1f) return true;
        return GarageRoll.Chance($"{positionKey:x}/{ply}/{game}/blunder", chance);
    }

    /// <summary>Pure "which random legal move" pick (exposed for tests).</summary>
    public static int PickIndex(int count, ulong positionKey, int ply, int game) =>
        GarageRoll.Index($"{positionKey:x}/{ply}/{game}/blunder-move", count);

    static float Hash01(int seed) => GarageRoll.Value(seed + "/banter");
    int BanterSeed(int salt) => unchecked(gamesPlayed * 7919 + pliesThisGame * 131 + salt);

    /// <summary>A speech bubble over Magnus's head on every machine (host decides the line).</summary>
    void Say(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        SayClientRpc(line);
    }

    [ClientRpc]
    void SayClientRpc(string line)
    {
        Vector3 head = arm ? arm.HeadPosition : transform.position + Vector3.up;
        GameHUD.FloatingText(head + Vector3.up * 0.35f, line, ChessBanter.BubbleColor, 0.85f);
        Sfx.Play("beep", head, 0.35f, 1.6f);
    }

    // ------------------------------------------------------------------ engine bookkeeping

    bool IsLegalForEngine(Vector2Int from, Vector2Int to)
    {
        if (searchTask != null) return false;   // never touch the board while a search runs
        return FindLegalMove(from, to, PieceType.Queen, out _);
    }

    void OnMoveCommitted(Vector2Int from, Vector2Int to, PieceType promotion)
    {
        if (FindLegalMove(from, to, promotion, out var move))
        {
            board.MakeMove(move);
        }
        else
        {
            Debug.LogWarning($"Magnus: move {from}->{to} isn't legal for the engine; resyncing from the board.");
            board.LoadPosition(chess.ServerGetFen());
        }
        pliesThisGame++;

        if (chess.Result != GameResult.InProgress) return;
        var state = Engine.Arbiter.GetGameState(board);
        switch (state)
        {
            case Engine.GameResult.WhiteIsMated: chess.ServerDeclareResult(GameResult.BlackWins); break;
            case Engine.GameResult.BlackIsMated: chess.ServerDeclareResult(GameResult.WhiteWins); break;
            case Engine.GameResult.Stalemate: chess.ServerDeclareResult(GameResult.Stalemate); break;
            case Engine.GameResult.Repetition:
            case Engine.GameResult.FiftyMoveRule:
            case Engine.GameResult.InsufficientMaterial:
                chess.ServerDeclareResult(GameResult.Draw);
                break;
            default:
                if (!chess.HasHumanPlayer && pliesThisGame >= selfPlayMaxPlies) chess.ServerDeclareResult(GameResult.Draw);
                break;
        }
    }

    void OnBoardReset()
    {
        board.LoadPosition(chess.ServerGetFen());
        searcher.ClearForNewPosition();
        pliesThisGame = 0;
    }

    List<Engine.Move> LegalMoves()
    {
        var list = new List<Engine.Move>();
        foreach (var m in moveGenerator.GenerateMoves(board)) list.Add(m);
        return list;
    }

    bool FindLegalMove(Vector2Int from, Vector2Int to, PieceType promotion, out Engine.Move found)
    {
        int start = from.y * 8 + from.x, target = to.y * 8 + to.x;
        found = Engine.Move.NullMove;
        foreach (var m in LegalMoves())
        {
            if (m.StartSquare != start || m.TargetSquare != target) continue;
            if (m.IsPromotion && ToPieceType(m.PromotionPieceType) != promotion) continue;
            found = m;
            return true;
        }
        return false;
    }

    static Vector2Int Coord(int square) => new(square % 8, square / 8);

    static PieceType ToPieceType(int enginePieceType) => enginePieceType switch
    {
        Engine.Piece.Knight => PieceType.Knight,
        Engine.Piece.Bishop => PieceType.Bishop,
        Engine.Piece.Rook => PieceType.Rook,
        _ => PieceType.Queen,
    };

    // ------------------------------------------------------------------ what every client plays

    [ClientRpc]
    void PlayPlanClientRpc(ChessController.RobotPlan plan)
    {
        if (!arm) return;
        var steps = new List<MagnusArm.Step>();
        for (int i = 0; i < plan.pieces.Length; i++)
        {
            var piece = chess.PieceFromId(plan.pieces[i]);
            if (!piece) continue;
            chess.GetGrip(piece, out float gripHeight, out float gripWidth);
            var step = new MagnusArm.Step
            {
                piece = piece.transform,
                to = chess.LocationPosition(plan.to[i], piece.Color),
                gripHeight = gripHeight,
                gripWidth = gripWidth,
            };
            if (plan.promotePiece == plan.pieces[i] && plan.to[i] < 64)
            {
                var promoteTo = (PieceType)plan.promoteType;
                step.onPlaced = () => chess.SetPieceType(piece, promoteTo);
            }
            steps.Add(step);
        }
        arm.PlayPlan(plan.id, steps);
    }

    [ClientRpc]
    void PlayGestureClientRpc(MagnusArm.Gesture gesture)
    {
        if (arm) arm.PlayGesture(gesture);
    }
}
