using UnityEngine;
using Unity.Netcode;
using Cinemachine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using StarterAssets;
using TMPro;
using UnityEngine.InputSystem;

public enum GameResult { InProgress, WhiteWins, BlackWins, Stalemate, Draw }

public class ChessController : NetworkBehaviour, IInteractable
{
    /// <summary>Seat id meaning "Magnus is sitting here" (never a real client id).</summary>
    public const ulong MagnusSeatId = ulong.MaxValue - 1;

    /// <summary>Location codes: 0-63 is a square (rank * 8 + file), 64 and up is a slot in a captured-pieces sideline.</summary>
    public const byte CapturedLocation = 64;
    const byte NoLocation = 255;

    [Header("Game Components")]
    [Tooltip("The camera that focuses on the chessboard.")]
    [SerializeField] private CinemachineVirtualCamera whiteCam;
    [SerializeField] private CinemachineVirtualCamera blackCam;

    [Header("Captured Pieces")]
    [Tooltip("An empty GameObject to serve as the parent/anchor for captured white pieces.")]
    [SerializeField] private Transform whiteCapturedPiecesContainer;
    [Tooltip("An empty GameObject to serve as the parent/anchor for captured black pieces.")]
    [SerializeField] private Transform blackCapturedPiecesContainer;
    [Tooltip("Captured pieces line up along the container's forward axis; each new column starts further from the board.")]
    [SerializeField] private float capturedSpacing = 0.058f;
    [SerializeField] private int capturedPerColumn = 8;

    [SerializeField] private TextMeshProUGUI turnIndicatorText;

    [Header("Board Setup")]
    [Tooltip("The parent object holding all 64 square GameObjects.")]
    [SerializeField] private Transform boardParent;

    [Tooltip("The layer mask assigned to the board squares for raycasting.")]
    [SerializeField] private LayerMask boardLayer;
    [SerializeField] private LayerMask pieceLayer;

    [Header("Drag Smoothing")]
    [SerializeField, Tooltip("Seconds to reach ~63% of the way to the cursor")]
    private float dragSmoothTime = 0.06f;
    [SerializeField, Tooltip("Clamp the smoothing speed (units/sec)")]
    private float dragMaxSpeed = 40f;
    [SerializeField, Tooltip("Hover height above board while dragging")]
    private float dragHoverY = 0.12f;

    [SerializeField] float tiltMaxDeg = 15f;      // max lean angle
    [SerializeField] float tiltResponse = 10f;    // how quickly it leans/uprights

    private Quaternion pickedUpBaseRot;           // remember yaw/original when picked up

    private Vector3 dragTarget;     // where we want the piece to go
    private Vector3 dragVelocity;   // SmoothDamp velocity
    private bool isDragging;

    // --- Networked State ---
    private NetworkVariable<ulong> whitePlayerId = new(ulong.MaxValue);
    private NetworkVariable<ulong> blackPlayerId = new(ulong.MaxValue);
    private NetworkVariable<PieceColor> currentPlayerTurn = new(PieceColor.White);
    private NetworkVariable<int> whiteCapturedCount = new(0);
    private NetworkVariable<int> blackCapturedCount = new(0);
    private NetworkVariable<GameResult> gameResult = new(GameResult.InProgress);
    private NetworkVariable<Vector2Int> enPassantTargetSquare = new(new Vector2Int(-1, -1));
    private NetworkVariable<bool> boardBusy = new(false);   // Magnus is moving pieces or tidying up

    // --- Local Game State ---
    private ChessPiece selectedPiece;

    // Local-only gamepad square selector; chess never sends cursor position over the network.
    Vector2Int padSquare = new(4, 1);
    LineRenderer padOutline;
    Material padOutlineMaterial;
    float padRepeatAt;
    bool padSelecting;

    private ChessSquare originalSquare;

    private PieceState[,] boardState = new PieceState[8, 8]; // Snapshot of the logical board for ChessValidator
    private readonly ChessPiece[,] pieceAt = new ChessPiece[8, 8];
    private readonly Dictionary<ChessPiece, byte> locationOf = new();
    private Dictionary<Vector2Int, ChessSquare> squareLookup = new();
    private Camera mainCam;

    // Every piece's starting square and original look, so the board can be reset and promotions undone.
    private class PieceRecord
    {
        public byte home;
        public Quaternion homeRotation;
        public PieceType homeType;
    }
    private struct PieceLook
    {
        public Mesh mesh;
        public Material[] materials;
        public Vector3 scale;
        public Mesh colliderMesh;
        public float height, width;
    }
    private readonly List<ChessPiece> pieces = new();
    private readonly Dictionary<ChessPiece, PieceRecord> records = new();
    private readonly Dictionary<(PieceColor, PieceType), PieceLook> looks = new();
    private readonly Dictionary<ulong, ChessPiece> piecesById = new();
    private readonly Dictionary<ChessPiece, Coroutine> pieceTweens = new();
    private readonly HashSet<ChessPiece> toppled = new();

    // How high up (fraction of height) Magnus grips each kind of piece, and how wide it is there (fraction of its width).
    private static readonly Dictionary<PieceType, Vector2> GripFractions = new()
    {
        { PieceType.Pawn,   new Vector2(0.66f, 0.40f) },
        { PieceType.Knight, new Vector2(0.55f, 0.45f) },
        { PieceType.Bishop, new Vector2(0.62f, 0.42f) },
        { PieceType.Rook,   new Vector2(0.60f, 0.62f) },
        { PieceType.Queen,  new Vector2(0.60f, 0.42f) },
        { PieceType.King,   new Vector2(0.55f, 0.42f) },
    };

    // --- Hooks for MagnusChessBot (server) ---
    /// <summary>Optional extra legality check for moves (Magnus plugs in the chess engine).</summary>
    public Func<Vector2Int, Vector2Int, bool> ExtraMoveCheck;
    public event Action<Vector2Int, Vector2Int, PieceType> ServerMoveCommitted;
    public event Action ServerBoardReset;
    /// <summary>Runs on each client right before an authoritative board update is applied.</summary>
    public Action BeforeBoardSync;

    // --- State Properties ---
    public bool HasWhitePlayer => whitePlayerId.Value != ulong.MaxValue;
    public bool HasBlackPlayer => blackPlayerId.Value != ulong.MaxValue;
    public bool MagnusSeated => blackPlayerId.Value == MagnusSeatId;
    public bool HasHumanPlayer => IsHumanSeat(whitePlayerId.Value) || IsHumanSeat(blackPlayerId.Value);
    /// <summary>Magnus plays Black against a person, and both sides when nobody is sitting at the board.</summary>
    public bool IsMagnusTurn => MagnusSeated && (currentPlayerTurn.Value == PieceColor.Black || !HasWhitePlayer);
    public PieceColor CurrentTurn => currentPlayerTurn.Value;
    public GameResult Result => gameResult.Value;
    public bool IsLocalPlayerTurn
    {
        get
        {
            if (!IsSpawned || gameResult.Value != GameResult.InProgress || boardBusy.Value) return false;
            ulong localId = NetworkManager.Singleton.LocalClientId;
            return (currentPlayerTurn.Value == PieceColor.White && whitePlayerId.Value == localId) ||
                   (currentPlayerTurn.Value == PieceColor.Black && blackPlayerId.Value == localId);
        }
    }

    static bool IsHumanSeat(ulong id) => id != ulong.MaxValue && id != MagnusSeatId;

    void Awake()
    {
        if (whiteCam) whiteCam.gameObject.SetActive(false);
        if (blackCam) blackCam.gameObject.SetActive(false);
    }

    public override void OnNetworkSpawn()
    {
        mainCam = Camera.main;
        currentPlayerTurn.OnValueChanged += OnTurnChanged;
        gameResult.OnValueChanged += OnGameResultChanged;
        UpdateTurnIndicator(PieceColor.White, currentPlayerTurn.Value);

        BuildSquareLookup();
        RegisterPieces();

        if (IsServer)
        {
            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (padOutline != null) Destroy(padOutline.gameObject);
        if (padOutlineMaterial != null) Destroy(padOutlineMaterial);
        padOutline = null;
        padOutlineMaterial = null;
        currentPlayerTurn.OnValueChanged -= OnTurnChanged;
        gameResult.OnValueChanged -= OnGameResultChanged;
        if (IsServer && NetworkManager)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    void Update()
    {
        if (!IsLocalPlayerTurn)
        {
            if (selectedPiece != null) CancelDrag();
            if (padOutline != null) padOutline.enabled = false;
            return;
        }
        if (Gamepad.current != null && PlayerController.Local != null && PlayerController.Local.currentChessController == this) // coop:allow(local) this machine's board cursor only
            UpdatePadChess();
        else if (padOutline != null) padOutline.enabled = false;
        if(!mainCam) return; // Ensure mainCam is assigned
        var ray = mainCam.ScreenPointToRay(Input.mousePosition);
        // Mouse down: select a piece
        if (Input.GetMouseButtonDown(0))
        {
            padSelecting = false;
            if (Physics.Raycast(ray, out var hit, 100f, pieceLayer))
            {
                var piece = hit.collider.GetComponentInParent<ChessPiece>();
                if (piece != null) HandleMouseDown(piece);
            }
        }

        // Drag selected piece along the board
        if (selectedPiece != null && !padSelecting)
        {
            if (Physics.Raycast(ray, out var hitOnBoard, 100f, boardLayer))
            {
                // existing SmoothDamp positioning...
                dragTarget = new Vector3(hitOnBoard.point.x, boardParent.position.y + dragHoverY, hitOnBoard.point.z);
                selectedPiece.transform.position = Vector3.SmoothDamp(
                    selectedPiece.transform.position,
                    dragTarget,
                    ref dragVelocity,
                    dragSmoothTime,
                    dragMaxSpeed
                );

                // --- Lean toward cursor (pitch), keep yaw from when you picked it up ---
                Vector3 flatDir = new Vector3(dragTarget.x - selectedPiece.transform.position.x, 0f,
                                            dragTarget.z - selectedPiece.transform.position.z);
                if (flatDir.sqrMagnitude > 1e-5f)
                {
                    flatDir.Normalize();

                    // axis to tilt around = "right" of the motion direction in world space
                    // (swap the cross order or negate angle if the tilt goes the wrong way on your models)
                    Vector3 rightAxis = Vector3.Cross(Vector3.up, flatDir).normalized;

                    // tilt proportional to speed; clamp to avoid cartoon flips
                    float tilt = Mathf.Clamp(dragVelocity.magnitude * 1.0f, 0f, tiltMaxDeg);

                    Quaternion lean = Quaternion.AngleAxis(tilt, rightAxis);
                    Quaternion targetRot = lean * pickedUpBaseRot;  // add lean on top of original yaw

                    // smooth the lean (time-based)
                    selectedPiece.transform.rotation = Quaternion.Slerp(
                        selectedPiece.transform.rotation,
                        targetRot,
                        1f - Mathf.Exp(-tiltResponse * Time.deltaTime)
                    );
                }
            }

            // Mouse up: drop onto a square
            if (Input.GetMouseButtonUp(0))
            {
                ChessSquare square = null;
                if (Physics.Raycast(ray, out var dropHit, 100f, boardLayer))
                    square = dropHit.collider.GetComponent<ChessSquare>();

                HandleMouseUp(square);
            }
        }
    }

    public bool CanInteract() => true;

    public string GetInteractionPrompt()
    {
        ulong localId = NetworkManager.Singleton.LocalClientId;
        if (whitePlayerId.Value == localId || blackPlayerId.Value == localId) return "Leave Game";
        if (MagnusSeated) return HasWhitePlayer ? "Watch Magnus" : "Play Magnus";
        if (!HasWhitePlayer) return "Play as White";
        if (!HasBlackPlayer) return "Play as Black";
        return "Spectate";
    }

    public void OnInteract(GameObject interactor)
    {
        var pc = interactor.GetComponent<PlayerController>();
        if (pc == null) return;
        var clientId = pc.OwnerClientId;

        if (whitePlayerId.Value == clientId || blackPlayerId.Value == clientId) LeaveBoard(pc);
        else if (!HasWhitePlayer) SitAtBoard(pc, PieceColor.White);
        else if (!HasBlackPlayer) SitAtBoard(pc, PieceColor.Black);
    }

    public void Highlight(bool highlight) { }

    public void CollectCandidates(PlayerController player, List<InteractionCandidate> results)
    {
        ulong clientId = player.OwnerClientId;
        if ((whitePlayerId.Value == clientId || blackPlayerId.Value == clientId) && CanInteract())
        {
            results.Add(new InteractionCandidate { Target = this, Score = 0.5f });
        }
    }

    private void SitAtBoard(PlayerController pc, PieceColor color)
    {
        padSquare = color == PieceColor.White ? new Vector2Int(4, 1) : new Vector2Int(4, 6);
        padRepeatAt = 0f;
        padSelecting = false;
        var thirdPersonController = pc.GetComponent<ThirdPersonController>();
        var charController = pc.GetComponent<CharacterController>();

        thirdPersonController.enabled = false;
        charController.enabled = false;

        pc.playerCamVCam.Priority = 5;
        if (color == PieceColor.White)
        {
            whiteCam.gameObject.SetActive(true);
            whiteCam.Priority = 11;
        }
        else
        {
            blackCam.gameObject.SetActive(true);
            blackCam.Priority = 11;
        }

        pc.IsPlayingChess = true;
        pc.currentChessController = this;
        SetBoardCursor(pc, true);

        RequestSitServerRpc(color);
    }

    private void LeaveBoard(PlayerController pc)
    {
        StandUpLocally(pc);
        RequestStandServerRpc();
    }

    private void StandUpLocally(PlayerController pc)
    {
        CancelDrag();
        if (padOutline != null) padOutline.enabled = false;
        var thirdPersonController = pc.GetComponent<ThirdPersonController>();
        var charController = pc.GetComponent<CharacterController>();

        thirdPersonController.enabled = true;
        charController.enabled = true;

        pc.playerCamVCam.Priority = 11;
        if (whiteCam) {
            whiteCam.Priority = 5;
            whiteCam.gameObject.SetActive(false);
        }
        if (blackCam) {
            blackCam.Priority = 5;
            blackCam.gameObject.SetActive(false);
        }

        pc.IsPlayingChess = false;
        pc.currentChessController = null;
        SetBoardCursor(pc, false);
    }

    /// <summary>
    /// Seated players pick pieces with the mouse, so free and show the cursor while seated (and stop the Starter Assets
    /// input from re-locking it on focus); restore third-person mouse-look when standing up. Not PlayerController's UI
    /// lock: that would also block the E "Leave Game" prompt.
    /// </summary>
    static void SetBoardCursor(PlayerController pc, bool seated)
    {
        if (pc == null || !pc.IsOwner) return;
        var inputs = pc.GetComponent<StarterAssets.StarterAssetsInputs>();
        if (inputs != null)
        {
            inputs.cursorLocked = !seated;
            inputs.cursorInputForLook = !seated;
            if (seated) inputs.look = Vector2.zero;
        }
        if (seated)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (!PlayerController.IsUIBlocking)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    // Called when the mouse button is pressed down on a piece
    public void HandleMouseDown(ChessPiece piece)
    {
        if (!IsLocalPlayerTurn || piece.Color != currentPlayerTurn.Value) return;
        if (!locationOf.TryGetValue(piece, out byte loc) || loc >= CapturedLocation) return;

        selectedPiece = piece;
        selectedPiece.OriginalPosition = piece.transform.position;
        originalSquare = squareLookup[SquareCoords(loc)];
        pickedUpBaseRot = selectedPiece.transform.rotation;  // store base rotation
        dragVelocity = Vector3.zero;
        isDragging = true;
    }

    // Called when the mouse button is released over a square
    public void HandleMouseUp(ChessSquare square)
    {
        if (selectedPiece == null) return; // Guard against no piece selected

        padSelecting = false;

        var piece = selectedPiece;
        var from = originalSquare;
        selectedPiece = null;
        isDragging = false;
        piece.transform.rotation = pickedUpBaseRot;

        if (square == null || from == null || square == from)
        {
            piece.transform.position = piece.OriginalPosition; // Snap back
            return;
        }

        Debug.Log($"Attempting to move {piece.Color} {piece.Type} from {from.squareNotation} to {square.squareNotation}");

        RebuildBoardState();
        if (!ChessValidator.IsValidMove(boardState, from.coordinates, square.coordinates, currentPlayerTurn.Value, enPassantTargetSquare.Value))
        {
            Debug.LogWarning("Invalid move attempted.");
            piece.transform.position = piece.OriginalPosition;
            return;
        }

        // Show it landing straight away; the server's board update confirms it (or puts it back).
        piece.transform.position = square.transform.position;
        RequestMoveServerRpc(from.coordinates, square.coordinates);
    }

    private void CancelDrag()
    {
        if (selectedPiece != null)
        {
            selectedPiece.transform.position = selectedPiece.OriginalPosition;
            selectedPiece.transform.rotation = pickedUpBaseRot;
        }
        selectedPiece = null;
        isDragging = false;
        padSelecting = false;
    }

    void UpdatePadChess()
    {
        var pad = Gamepad.current;
        if (pad == null) return;
        if (GameControls.PadDown(GameControls.Action.Back))
        {
            if (selectedPiece != null) CancelDrag();
            else LeaveBoard(PlayerController.Local); // coop:allow(local) B stands up this machine's seated player only
            return;
        }

        Vector2 axis = pad.dpad.ReadValue();
        if (axis.sqrMagnitude < 0.25f) axis = pad.leftStick.ReadValue();
        if (axis.sqrMagnitude > 0.3f && Time.unscaledTime >= padRepeatAt)
        {
            padRepeatAt = Time.unscaledTime + 0.18f;
            bool whiteView = whitePlayerId.Value == NetworkManager.Singleton.LocalClientId;
            int sign = whiteView ? 1 : -1;
            if (Mathf.Abs(axis.x) > Mathf.Abs(axis.y)) padSquare.x = Mathf.Clamp(padSquare.x + (axis.x > 0f ? sign : -sign), 0, 7);
            else padSquare.y = Mathf.Clamp(padSquare.y + (axis.y > 0f ? sign : -sign), 0, 7);
        }

        ShowPadSquare();
        if (!GameControls.PadDown(GameControls.Action.Jump)) return;
        if (selectedPiece == null)
        {
            var piece = PieceAt(padSquare);
            if (piece != null) HandleMouseDown(piece);
            padSelecting = selectedPiece != null;
        }
        else if (squareLookup.TryGetValue(padSquare, out var square)) HandleMouseUp(square);
    }

    void ShowPadSquare()
    {
        if (!squareLookup.TryGetValue(padSquare, out var square)) return;
        if (padOutline == null)
        {
            var go = new GameObject("Chess gamepad cursor");
            go.transform.SetParent(transform, false);
            padOutline = go.AddComponent<LineRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader != null) padOutlineMaterial = new Material(shader);
            if (padOutlineMaterial != null) padOutline.material = padOutlineMaterial;
            padOutline.useWorldSpace = true;
            padOutline.loop = true;
            padOutline.positionCount = 4;
            padOutline.widthMultiplier = Mathf.Max(0.009f, SquareSize() * 0.055f);
            padOutline.startColor = padOutline.endColor = new Color(1f, 0.76f, 0.14f);
        }
        Vector3 center = square.transform.position + Vector3.up * 0.035f;
        Vector3 file = (squareLookup[new Vector2Int(1, 0)].transform.position - squareLookup[new Vector2Int(0, 0)].transform.position) * 0.44f;
        Vector3 rank = (squareLookup[new Vector2Int(0, 1)].transform.position - squareLookup[new Vector2Int(0, 0)].transform.position) * 0.44f;
        padOutline.SetPosition(0, center - file - rank);
        padOutline.SetPosition(1, center + file - rank);
        padOutline.SetPosition(2, center + file + rank);
        padOutline.SetPosition(3, center - file + rank);
        padOutline.enabled = true;
    }

    private void UpdateTurnIndicator(PieceColor previous, PieceColor current)
    {
        if (turnIndicatorText == null) return;

        switch (gameResult.Value)
        {
            case GameResult.InProgress:
                turnIndicatorText.text = $"{current}'s Turn";
                break;
            case GameResult.WhiteWins:
                turnIndicatorText.text = "Checkmate! White wins.";
                break;
            case GameResult.BlackWins:
                turnIndicatorText.text = "Checkmate! Black wins.";
                break;
            case GameResult.Stalemate:
                turnIndicatorText.text = "Stalemate! It's a draw.";
                break;
            case GameResult.Draw:
                turnIndicatorText.text = "It's a draw.";
                break;
        }
    }

    private void OnGameResultChanged(GameResult previous, GameResult current)
    {
        UpdateTurnIndicator(currentPlayerTurn.Value, currentPlayerTurn.Value);
    }

    private void OnTurnChanged(PieceColor previous, PieceColor current)
    {
        UpdateTurnIndicator(previous, current);
    }

    // ------------------------------------------------------------------ board model

    private void BuildSquareLookup()
    {
        if (squareLookup.Count > 0) return;
        foreach (var square in boardParent.GetComponentsInChildren<ChessSquare>())
        {
            squareLookup[square.coordinates] = square;
        }
    }

    // Record where every piece starts and what it looks like, and build the logical board from that.
    private void RegisterPieces()
    {
        if (pieces.Count > 0) return;
        foreach (var piece in GetComponentsInChildren<ChessPiece>(true))
        {
            var rend = piece.GetComponent<Renderer>();
            Bounds bounds = rend ? rend.bounds : new Bounds(piece.transform.position, Vector3.one * 0.05f);
            var record = new PieceRecord
            {
                home = NearestSquare(piece.transform.position),
                homeRotation = piece.transform.rotation,
                homeType = piece.Type,
            };
            records[piece] = record;
            pieces.Add(piece);

            var key = (piece.Color, piece.Type);
            if (!looks.ContainsKey(key))
            {
                var meshCollider = piece.GetComponent<MeshCollider>();
                looks[key] = new PieceLook
                {
                    mesh = piece.GetComponent<MeshFilter>().sharedMesh,
                    materials = rend ? rend.sharedMaterials : null,
                    scale = piece.transform.localScale,
                    colliderMesh = meshCollider ? meshCollider.sharedMesh : null,
                    height = bounds.size.y,
                    width = Mathf.Min(bounds.size.x, bounds.size.z),
                };
            }
            SetLocation(piece, record.home);
        }
        RebuildBoardState();
    }

    private byte NearestSquare(Vector3 position)
    {
        byte best = NoLocation;
        float bestDistance = float.MaxValue;
        foreach (var pair in squareLookup)
        {
            Vector3 d = pair.Value.transform.position - position;
            float distance = d.x * d.x + d.z * d.z;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = SquareLocation(pair.Key);
            }
        }
        float squareSize = SquareSize();
        return bestDistance < squareSize * squareSize * 0.36f ? best : NoLocation;
    }

    private float SquareSize()
    {
        if (squareLookup.TryGetValue(new Vector2Int(0, 0), out var a) && squareLookup.TryGetValue(new Vector2Int(1, 0), out var b))
            return Vector3.Distance(a.transform.position, b.transform.position);
        return 0.08f;
    }

    public static byte SquareLocation(Vector2Int c) => (byte)(c.y * 8 + c.x);
    private static Vector2Int SquareCoords(byte location) => new(location % 8, location / 8);
    private static bool OnBoard(Vector2Int c) => c.x >= 0 && c.x < 8 && c.y >= 0 && c.y < 8;

    public ChessPiece PieceAt(Vector2Int c) => OnBoard(c) ? pieceAt[c.x, c.y] : null;

    private void SetLocation(ChessPiece piece, byte location)
    {
        if (locationOf.TryGetValue(piece, out byte old) && old < CapturedLocation)
        {
            var c = SquareCoords(old);
            if (pieceAt[c.x, c.y] == piece) pieceAt[c.x, c.y] = null;
        }
        locationOf[piece] = location;
        if (location < CapturedLocation)
        {
            var c = SquareCoords(location);
            pieceAt[c.x, c.y] = piece;
        }
    }

    private void RebuildBoardState()
    {
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                var p = pieceAt[x, y];
                boardState[x, y] = p != null ? new PieceState(p.Type, p.Color, p.HasMoved.Value) : default;
            }
    }

    /// <summary>World position for a location code (a square, or a slot in that colour's captured sideline).</summary>
    public Vector3 LocationPosition(byte location, PieceColor color)
    {
        if (location < CapturedLocation) return squareLookup[SquareCoords(location)].transform.position;

        Transform container = color == PieceColor.White ? whiteCapturedPiecesContainer : blackCapturedPiecesContainer;
        int index = location - CapturedLocation;
        int column = index / Mathf.Max(1, capturedPerColumn), row = index % Mathf.Max(1, capturedPerColumn);
        Vector3 outward = Vector3.Project(container.position - boardParent.position, boardParent.right);
        outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : boardParent.right;
        return container.position + container.forward * (row * capturedSpacing) + outward * (column * capturedSpacing);
    }

    public ChessPiece PieceFromId(ulong networkObjectId)
    {
        if (piecesById.TryGetValue(networkObjectId, out var piece) && piece) return piece;
        piecesById.Clear();
        foreach (var p in pieces)
            if (p && p.IsSpawned) piecesById[p.NetworkObjectId] = p;
        return piecesById.TryGetValue(networkObjectId, out piece) ? piece : null;
    }

    /// <summary>Where Magnus should grip a piece (height above its base) and how wide it is there.</summary>
    public void GetGrip(ChessPiece piece, out float gripHeight, out float gripWidth)
    {
        var look = looks.TryGetValue((piece.Color, piece.Type), out var l) ? l : new PieceLook { height = 0.08f, width = 0.05f };
        var fraction = GripFractions.TryGetValue(piece.Type, out var f) ? f : new Vector2(0.6f, 0.45f);
        gripHeight = look.height * fraction.x;
        gripWidth = look.width * fraction.y;
    }

    /// <summary>Turn a piece into another type (promotion) or back, swapping its mesh to match.</summary>
    public void SetPieceType(ChessPiece piece, PieceType type)
    {
        if (piece.Type == type) return;
        piece.Type = type;
        if (!looks.TryGetValue((piece.Color, type), out var look)) return;

        piece.GetComponent<MeshFilter>().sharedMesh = look.mesh;
        var rend = piece.GetComponent<Renderer>();
        if (rend && look.materials != null) rend.sharedMaterials = look.materials;
        piece.transform.localScale = look.scale;
        var meshCollider = piece.GetComponent<MeshCollider>();
        if (meshCollider && look.colliderMesh) meshCollider.sharedMesh = look.colliderMesh;
    }

    /// <summary>The position as a FEN string, for the chess engine.</summary>
    public string ServerGetFen()
    {
        var sb = new StringBuilder();
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                var p = pieceAt[file, rank];
                if (p == null) { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                char c = p.Type switch
                {
                    PieceType.Pawn => 'p', PieceType.Knight => 'n', PieceType.Bishop => 'b',
                    PieceType.Rook => 'r', PieceType.Queen => 'q', _ => 'k',
                };
                sb.Append(p.Color == PieceColor.White ? char.ToUpper(c) : c);
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }

        sb.Append(currentPlayerTurn.Value == PieceColor.White ? " w " : " b ");
        string castling = (CanStillCastle(PieceColor.White, true) ? "K" : "") + (CanStillCastle(PieceColor.White, false) ? "Q" : "")
                        + (CanStillCastle(PieceColor.Black, true) ? "k" : "") + (CanStillCastle(PieceColor.Black, false) ? "q" : "");
        sb.Append(castling.Length > 0 ? castling : "-");
        var ep = enPassantTargetSquare.Value;
        sb.Append(ep.x >= 0 ? $" {(char)('a' + ep.x)}{ep.y + 1}" : " -");
        sb.Append(" 0 1");
        return sb.ToString();
    }

    private bool CanStillCastle(PieceColor color, bool kingside)
    {
        int rank = color == PieceColor.White ? 0 : 7;
        var king = pieceAt[4, rank];
        var rook = pieceAt[kingside ? 7 : 0, rank];
        return king && rook && king.Type == PieceType.King && rook.Type == PieceType.Rook
            && king.Color == color && rook.Color == color && !king.HasMoved.Value && !rook.HasMoved.Value;
    }

    private Vector2Int FindKingOnBoard(PieceColor color)
    {
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                if (boardState[x, y].Type == PieceType.King && boardState[x, y].Color == color)
                {
                    return new Vector2Int(x, y);
                }
            }
        }
        return new Vector2Int(-1, -1); // Not found
    }

    private void CheckForGameOver()
    {
        // This is called on the server after a move has been made and the turn has switched.
        // We need to check if the NEW current player has any legal moves.
        PieceColor playerToMove = currentPlayerTurn.Value;

        // Check for any valid move for the current player
        for (int startX = 0; startX < 8; startX++)
        {
            for (int startY = 0; startY < 8; startY++)
            {
                PieceState piece = boardState[startX, startY];
                if (piece.Type != PieceType.None && piece.Color == playerToMove)
                {
                    Vector2Int startPos = new Vector2Int(startX, startY);
                    for (int endX = 0; endX < 8; endX++)
                    {
                        for (int endY = 0; endY < 8; endY++)
                        {
                            if (ChessValidator.IsValidMove(boardState, startPos, new Vector2Int(endX, endY), playerToMove, enPassantTargetSquare.Value))
                            {
                                return; // A legal move was found, so the game is not over.
                            }
                        }
                    }
                }
            }
        }

        // If we get here, no legal moves were found. It's either checkmate or stalemate.
        Vector2Int kingPos = FindKingOnBoard(playerToMove);
        if (ChessValidator.IsSquareAttacked(boardState, kingPos, playerToMove))
        {
            ServerDeclareResult(playerToMove == PieceColor.White ? GameResult.BlackWins : GameResult.WhiteWins);
        }
        else { ServerDeclareResult(GameResult.Stalemate); }
    }

    // ------------------------------------------------------------------ server-side game logic

    private struct MoveEffects
    {
        public ChessPiece mover, captured, rook;
        public Vector2Int rookTo;
        public bool doublePawnPush;
    }

    private MoveEffects Effects(Vector2Int from, Vector2Int to)
    {
        var e = new MoveEffects { mover = PieceAt(from) };
        bool isCastle = e.mover.Type == PieceType.King && Mathf.Abs(to.x - from.x) == 2;
        bool isEnPassant = e.mover.Type == PieceType.Pawn && from.x != to.x && PieceAt(to) == null && to == enPassantTargetSquare.Value;

        e.captured = PieceAt(isEnPassant ? new Vector2Int(to.x, from.y) : to);
        if (e.captured != null && e.captured.Color == e.mover.Color) e.captured = null;
        if (isCastle)
        {
            bool isKingside = to.x > from.x;
            e.rook = PieceAt(new Vector2Int(isKingside ? 7 : 0, from.y));
            e.rookTo = new Vector2Int(isKingside ? 5 : 3, from.y);
        }
        e.doublePawnPush = e.mover.Type == PieceType.Pawn && Mathf.Abs(to.y - from.y) == 2;
        return e;
    }

    private byte NextCapturedSlot(PieceColor color) =>
        (byte)(CapturedLocation + (color == PieceColor.White ? whiteCapturedCount.Value : blackCapturedCount.Value));

    /// <summary>
    /// Server: apply a move to the board (captures, castling, en passant, promotion), pass the turn,
    /// and send every client the new position.
    /// </summary>
    public bool ServerCommitMove(Vector2Int from, Vector2Int to, PieceType promotion = PieceType.Queen)
    {
        if (!IsServer || PieceAt(from) == null) return false;
        var e = Effects(from, to);

        if (e.captured != null)
        {
            SetLocation(e.captured, NextCapturedSlot(e.captured.Color));
            if (e.captured.Color == PieceColor.White) whiteCapturedCount.Value++;
            else blackCapturedCount.Value++;
        }

        SetLocation(e.mover, SquareLocation(to));
        e.mover.HasMoved.Value = true;

        if (e.rook != null)
        {
            SetLocation(e.rook, SquareLocation(e.rookTo));
            e.rook.HasMoved.Value = true;
        }

        enPassantTargetSquare.Value = e.doublePawnPush
            ? new Vector2Int(from.x, (from.y + to.y) / 2)
            : new Vector2Int(-1, -1);

        if (e.mover.Type == PieceType.Pawn && (to.y == 7 || to.y == 0)) SetPieceType(e.mover, promotion);

        currentPlayerTurn.Value = (currentPlayerTurn.Value == PieceColor.White) ? PieceColor.Black : PieceColor.White;
        RebuildBoardState();
        CheckForGameOver();

        ServerMoveCommitted?.Invoke(from, to, promotion);
        SyncBoardClientRpc(BuildSync(false));
        if (!IsClient) SnapAllPieces();
        return true;
    }

    /// <summary>Server: the pieces Magnus has to carry for a move, in order, without changing the board yet.</summary>
    public RobotPlan ServerPlanMove(Vector2Int from, Vector2Int to, PieceType promotion)
    {
        var e = Effects(from, to);
        var carried = new List<ulong>();
        var targets = new List<byte>();
        if (e.captured != null)
        {
            carried.Add(e.captured.NetworkObjectId);
            targets.Add(NextCapturedSlot(e.captured.Color));
        }
        carried.Add(e.mover.NetworkObjectId);
        targets.Add(SquareLocation(to));
        if (e.rook != null)
        {
            carried.Add(e.rook.NetworkObjectId);
            targets.Add(SquareLocation(e.rookTo));
        }

        bool promotes = e.mover.Type == PieceType.Pawn && (to.y == 7 || to.y == 0);
        return new RobotPlan
        {
            pieces = carried.ToArray(),
            to = targets.ToArray(),
            promotePiece = promotes ? e.mover.NetworkObjectId : 0,
            promoteType = (byte)promotion,
        };
    }

    /// <summary>Server: put every piece back on its starting square and start a new game.</summary>
    public void ServerResetBoard()
    {
        if (!IsServer) return;
        foreach (var piece in pieces)
        {
            var record = records[piece];
            SetPieceType(piece, record.homeType);
            piece.HasMoved.Value = false;
        }
        foreach (var piece in pieces) locationOf.Remove(piece);
        Array.Clear(pieceAt, 0, pieceAt.Length);
        foreach (var piece in pieces) SetLocation(piece, records[piece].home);

        whiteCapturedCount.Value = 0;
        blackCapturedCount.Value = 0;
        enPassantTargetSquare.Value = new Vector2Int(-1, -1);
        currentPlayerTurn.Value = PieceColor.White;
        gameResult.Value = GameResult.InProgress;
        RebuildBoardState();

        ServerBoardReset?.Invoke();
        SyncBoardClientRpc(BuildSync(true));
        if (!IsClient) SnapAllPieces();
    }

    public void ServerDeclareResult(GameResult result)
    {
        if (!IsServer || gameResult.Value != GameResult.InProgress || result == GameResult.InProgress) return;
        gameResult.Value = result;
        if (result == GameResult.WhiteWins || result == GameResult.BlackWins)
        {
            var loser = result == GameResult.WhiteWins ? PieceColor.Black : PieceColor.White;
            RebuildBoardState();
            var kingPos = FindKingOnBoard(loser);
            var king = PieceAt(kingPos);
            if (king != null) ToppleKingClientRpc(king.NetworkObjectId);
        }
    }

    public void ServerSetBusy(bool busy)
    {
        if (IsServer && boardBusy.Value != busy) boardBusy.Value = busy;
    }

    /// <summary>Server: Magnus takes (or gives up) the Black seat.</summary>
    public void ServerSetMagnusSeat(bool seated)
    {
        if (!IsServer) return;
        if (seated && !HasBlackPlayer) blackPlayerId.Value = MagnusSeatId;
        else if (!seated && MagnusSeated) blackPlayerId.Value = ulong.MaxValue;
    }

    private bool ServerIsLegal(Vector2Int from, Vector2Int to)
    {
        if (!OnBoard(from) || !OnBoard(to)) return false;
        var mover = PieceAt(from);
        if (mover == null || mover.Color != currentPlayerTurn.Value) return false;
        RebuildBoardState();
        if (!ChessValidator.IsValidMove(boardState, from, to, currentPlayerTurn.Value, enPassantTargetSquare.Value)) return false;
        return ExtraMoveCheck == null || ExtraMoveCheck(from, to);
    }

    private bool IsSeatedForTurn(ulong clientId) =>
        (currentPlayerTurn.Value == PieceColor.White && whitePlayerId.Value == clientId) ||
        (currentPlayerTurn.Value == PieceColor.Black && blackPlayerId.Value == clientId);

    private void OnClientConnected(ulong clientId)
    {
        if (clientId != NetworkManager.ServerClientId) SyncBoardClientRpc(BuildSync(false), To(clientId));
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (whitePlayerId.Value == clientId) whitePlayerId.Value = ulong.MaxValue;
        if (blackPlayerId.Value == clientId) blackPlayerId.Value = ulong.MaxValue;
    }

    private static ClientRpcParams To(ulong clientId) =>
        new() { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };

    // ------------------------------------------------------------------ keeping clients in step

    /// <summary>What Magnus has to carry for one move; each client animates it locally.</summary>
    public struct RobotPlan : INetworkSerializable
    {
        public int id;
        public ulong[] pieces;
        public byte[] to;
        public ulong promotePiece;
        public byte promoteType;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref id);
            serializer.SerializeValue(ref pieces);
            serializer.SerializeValue(ref to);
            serializer.SerializeValue(ref promotePiece);
            serializer.SerializeValue(ref promoteType);
        }
    }

    private struct BoardSync : INetworkSerializable
    {
        public ulong[] pieces;
        public byte[] locations;
        public byte[] types;
        public bool reset;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref pieces);
            serializer.SerializeValue(ref locations);
            serializer.SerializeValue(ref types);
            serializer.SerializeValue(ref reset);
        }
    }

    private BoardSync BuildSync(bool reset)
    {
        var sync = new BoardSync
        {
            pieces = new ulong[pieces.Count],
            locations = new byte[pieces.Count],
            types = new byte[pieces.Count],
            reset = reset,
        };
        for (int i = 0; i < pieces.Count; i++)
        {
            sync.pieces[i] = pieces[i].NetworkObjectId;
            sync.locations[i] = locationOf.TryGetValue(pieces[i], out byte loc) ? loc : NoLocation;
            sync.types[i] = (byte)pieces[i].Type;
        }
        return sync;
    }

    [ClientRpc]
    private void SyncBoardClientRpc(BoardSync sync, ClientRpcParams clientRpcParams = default)
    {
        BeforeBoardSync?.Invoke();
        CancelDrag();
        if (sync.reset) toppled.Clear();

        for (int i = 0; i < sync.pieces.Length; i++)
        {
            var piece = PieceFromId(sync.pieces[i]);
            if (piece == null) continue;

            SetPieceType(piece, (PieceType)sync.types[i]);
            byte location = sync.locations[i];
            if (location == NoLocation) continue;
            SetLocation(piece, location);
            if (toppled.Contains(piece)) continue;   // leave the beaten king lying where it fell

            if (sync.reset) piece.transform.rotation = records[piece].homeRotation;
            var pieceCollider = piece.GetComponent<Collider>();
            if (pieceCollider) pieceCollider.enabled = location < CapturedLocation;
            Glide(piece, LocationPosition(location, piece.Color), sync.reset ? 1.1f : 0.2f, sync.reset ? 0.08f : 0f);
        }
        RebuildBoardState();
    }

    // Slide a piece to where it belongs (a little hop when the board resets).
    private void Glide(ChessPiece piece, Vector3 target, float seconds, float hop)
    {
        if (pieceTweens.TryGetValue(piece, out var running) && running != null) StopCoroutine(running);
        if ((piece.transform.position - target).sqrMagnitude < 1e-8f) return;
        pieceTweens[piece] = StartCoroutine(GlideRoutine(piece.transform, target, seconds, hop));
    }

    private IEnumerator GlideRoutine(Transform piece, Vector3 target, float seconds, float hop)
    {
        Vector3 start = piece.position;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            float s = Mathf.SmoothStep(0f, 1f, t / seconds);
            piece.position = Vector3.Lerp(start, target, s) + Vector3.up * (hop * 4f * s * (1f - s));
            yield return null;
        }
        piece.position = target;
    }

    private void SnapAllPieces()
    {
        foreach (var piece in pieces)
            if (locationOf.TryGetValue(piece, out byte loc) && loc != NoLocation)
                piece.transform.position = LocationPosition(loc, piece.Color);
    }

    // --- Server RPCs ---

    [ServerRpc(RequireOwnership = false)]
    private void RequestSitServerRpc(PieceColor color, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (color == PieceColor.White && !HasWhitePlayer)
        {
            whitePlayerId.Value = sender;
            // Hold the board until Magnus has tidied up his own game and set up a fresh one.
            if (MagnusSeated) boardBusy.Value = true;
        }
        else if (color == PieceColor.Black && !HasBlackPlayer) blackPlayerId.Value = sender;
        else SeatTakenClientRpc(To(sender));
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestStandServerRpc(ServerRpcParams rpcParams = default)
    {
        if (whitePlayerId.Value == rpcParams.Receive.SenderClientId) whitePlayerId.Value = ulong.MaxValue;
        else if (blackPlayerId.Value == rpcParams.Receive.SenderClientId) blackPlayerId.Value = ulong.MaxValue;
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestMoveServerRpc(Vector2Int fromCoords, Vector2Int toCoords, ServerRpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;
        if (gameResult.Value != GameResult.InProgress || boardBusy.Value || !IsSeatedForTurn(sender) || !ServerIsLegal(fromCoords, toCoords))
        {
            SyncBoardClientRpc(BuildSync(false), To(sender)); // put their piece back
            return;
        }
        ServerCommitMove(fromCoords, toCoords, PieceType.Queen);
    }

    [ClientRpc]
    private void SeatTakenClientRpc(ClientRpcParams clientRpcParams = default)
    {
        // Someone else got there first; stand back up.
        var player = NetworkManager.LocalClient?.PlayerObject;
        var pc = player ? player.GetComponent<PlayerController>() : null;
        if (pc != null && pc.currentChessController == this) StandUpLocally(pc);
    }

    [ClientRpc]
    private void ToppleKingClientRpc(ulong losingKingNetworkId)
    {
        var king = PieceFromId(losingKingNetworkId);
        if (king == null) return;
        toppled.Add(king);
        if (pieceTweens.TryGetValue(king, out var running) && running != null) StopCoroutine(running);
        pieceTweens[king] = StartCoroutine(ToppleRoutine(king.transform));
    }

    // Tip the losing king over onto its side.
    private IEnumerator ToppleRoutine(Transform king)
    {
        Vector3 basePos = king.position;
        Quaternion startRot = king.rotation;
        Vector3 axis = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * Vector3.right;
        float radius = looks.TryGetValue((king.GetComponent<ChessPiece>().Color, PieceType.King), out var look) ? look.width * 0.5f : 0.03f;
        for (float t = 0f; t < 0.7f; t += Time.deltaTime)
        {
            float s = t / 0.7f;
            s *= s;   // falls faster as it goes
            king.rotation = Quaternion.AngleAxis(88f * s, axis) * startRot;
            king.position = basePos + Vector3.up * (radius * s);
            yield return null;
        }
        king.rotation = Quaternion.AngleAxis(88f, axis) * startRot;
        king.position = basePos + Vector3.up * radius;
    }
}
