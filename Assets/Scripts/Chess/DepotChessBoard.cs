using System;
using System.Collections;
using System.Collections.Generic;
using Cinemachine;
using StarterAssets;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The *local* board, cameras and chess pieces in one copied Depot Row interior. The only authority is
/// DepotChessNet on NetSession; this component deliberately has no NetworkObject or NetworkBehaviour.
/// </summary>
[DisallowMultipleComponent]
public sealed class DepotChessBoard : MonoBehaviour, IInteractable
{
    public int lot;
    public CinemachineVirtualCamera whiteCam, blackCam;
    public Transform boardParent, whiteCaptured, blackCaptured;
    public TextMeshProUGUI turnIndicator;
    public MagnusArm arm;
    public MagnusTrack track;
    public bool magnusPlays = true;
    public LayerMask boardLayer, pieceLayer;
    public DepotChessPiece[] pieces;
    public float capturedSpacing = 0.058f;
    public int capturedPerColumn = 8;

    readonly Dictionary<Vector2Int, ChessSquare> squares = new();
    readonly Dictionary<(PieceColor, PieceType), PieceLook> looks = new();
    readonly Dictionary<DepotChessPiece, Coroutine> tweens = new();
    readonly Dictionary<DepotChessPiece, Coroutine> toppleTweens = new();
    readonly HashSet<DepotChessPiece> toppled = new();
    byte[] homeLocations;
    Quaternion[] homeRotations;
    Camera mainCam;
    LineRenderer padOutline;
    Material padOutlineMaterial;
    DepotChessPiece selected;
    Vector2Int originalSquare, padSquare = new(4, 1);
    Vector3 dragVelocity;
    Quaternion pickedUpRotation;
    bool padSelecting, registered;
    float padRepeatAt;

    struct PieceLook
    {
        public Mesh mesh, colliderMesh;
        public Material[] materials;
        public Vector3 scale;
        public float height, width;
    }

    void Awake()
    {
        if (whiteCam) whiteCam.gameObject.SetActive(false);
        if (blackCam) blackCam.gameObject.SetActive(false);
        mainCam = Camera.main;
        BuildLookups();
    }

    void OnEnable()
    {
        if (!registered) StartCoroutine(RegisterWhenReady());
    }

    IEnumerator RegisterWhenReady()
    {
        while (DepotChessNet.Instance == null || !DepotChessNet.Instance.IsSpawned) yield return null;
        if (this && isActiveAndEnabled)
        {
            DepotChessNet.Instance.Register(this);
            DepotChessNet.Instance.Refresh(this);
            registered = true;
        }
    }

    void OnDisable()
    {
        if (PlayerController.Local != null && PlayerController.Local.currentDepotChessBoard == this) // coop:allow(local) only the seated player's camera/input is restored
        {
            StandLocally(PlayerController.Local); // coop:allow(local) local input/camera cleanup
            if (DepotChessNet.Instance != null) DepotChessNet.Instance.LocalStand(lot);
        }
        if (registered && DepotChessNet.Instance != null) DepotChessNet.Instance.Unregister(this);
        registered = false;
    }

    void OnDestroy()
    {
        if (registered && DepotChessNet.Instance != null) DepotChessNet.Instance.Unregister(this);
        if (padOutline != null) Destroy(padOutline.gameObject);
        if (padOutlineMaterial != null) Destroy(padOutlineMaterial);
    }

    public void BuildLookups()
    {
        if (boardParent != null && squares.Count == 0)
        {
            foreach (var square in boardParent.GetComponentsInChildren<ChessSquare>(true))
            {
                string notation = square.transform.parent.name + square.name;
                if (notation.Length < 2) continue;
                int x = char.ToLowerInvariant(notation[0]) - 'a';
                int y = notation[1] - '1';
                if (x < 0 || x > 7 || y < 0 || y > 7) continue;
                square.coordinates = new Vector2Int(x, y);
                square.squareNotation = notation;
                squares[square.coordinates] = square;
            }
        }
        if (pieces == null) return;
        foreach (var p in pieces)
        {
            if (!p) continue;
            var key = (p.color, p.startingType);
            if (looks.ContainsKey(key)) continue;
            var renderer = p.GetComponent<Renderer>();
            var mesh = p.GetComponent<MeshFilter>();
            var collider = p.GetComponent<MeshCollider>();
            var bounds = renderer ? renderer.bounds : new Bounds(p.transform.position, Vector3.one * 0.05f);
            looks[key] = new PieceLook
            {
                mesh = mesh ? mesh.sharedMesh : null,
                colliderMesh = collider ? collider.sharedMesh : null,
                materials = renderer ? renderer.sharedMaterials : null,
                scale = p.transform.localScale,
                height = bounds.size.y,
                width = Mathf.Min(bounds.size.x, bounds.size.z)
            };
        }
        if (homeLocations == null && squares.Count == 64)
        {
            homeLocations = new byte[pieces.Length];
            homeRotations = new Quaternion[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                homeLocations[i] = NearestSquare(pieces[i].transform.position);
                homeRotations[i] = pieces[i].transform.rotation;
                pieces[i].type = pieces[i].startingType;
            }
        }
    }

    public byte InitialLocation(int index)
    {
        if (pieces == null || index < 0 || index >= pieces.Length || pieces[index] == null) return 255;
        BuildLookups();
        return homeLocations != null ? homeLocations[index] : (byte)255;
    }

    byte NearestSquare(Vector3 position)
    {
        float best = float.MaxValue;
        byte found = 255;
        foreach (var kv in squares)
        {
            var delta = kv.Value.transform.position - position;
            float distance = delta.x * delta.x + delta.z * delta.z;
            if (distance < best) { best = distance; found = (byte)(kv.Key.y * 8 + kv.Key.x); }
        }
        return best < 0.1f ? found : (byte)255;
    }

    public bool CanInteract()
    {
        var net = DepotChessNet.Instance;
        if (net == null || !net.BoardAvailable(lot)) return false;
        if (!net.TryState(lot, out var state)) return false;
        var me = PlayerController.Local; // coop:allow(local) only local focus/seat prompt is considered
        return me != null && (me.currentDepotChessBoard == this || state.whiteSeat == me.OwnerClientId ||
                              state.blackSeat == me.OwnerClientId || state.whiteSeat == ulong.MaxValue || state.blackSeat == ulong.MaxValue);
    }

    public string GetInteractionPrompt()
    {
        var net = DepotChessNet.Instance;
        var me = PlayerController.Local; // coop:allow(local) interaction prompt belongs to this machine's player
        if (net == null || me == null || !net.TryState(lot, out var state)) return "Chess board";
        ulong id = me.OwnerClientId;
        if (state.whiteSeat == id || state.blackSeat == id) return "Leave chess game";
        if (!net.BoardReadyForPlayers(lot)) return "Magnus is working — wait";
        if (state.blackSeat == ChessController.MagnusSeatId) return state.whiteSeat == ulong.MaxValue ? "Play Magnus" : "Watch Magnus";
        if (state.whiteSeat == ulong.MaxValue) return "Play as White";
        if (state.blackSeat == ulong.MaxValue) return "Play as Black";
        return "Game in progress";
    }

    public void OnInteract(GameObject interactor)
    {
        var pc = interactor ? interactor.GetComponent<PlayerController>() : null;
        var net = DepotChessNet.Instance;
        if (pc == null || !pc.IsOwner || net == null || !net.TryState(lot, out var state)) return;
        ulong id = pc.OwnerClientId;
        if (pc.currentDepotChessBoard == this || state.whiteSeat == id || state.blackSeat == id)
        {
            StandLocally(pc);
            net.LocalStand(lot);
            return;
        }
        if (!net.BoardReadyForPlayers(lot)) return;
        if (pc.IsPlayingChess || pc.IsDriving) return;
        PieceColor color;
        if (state.whiteSeat == ulong.MaxValue) color = PieceColor.White;
        else if (state.blackSeat == ulong.MaxValue) color = PieceColor.Black;
        else return;
        SitLocally(pc, color);
        net.LocalSit(lot, color);
    }

    public void CollectCandidates(PlayerController player, List<InteractionCandidate> results)
    {
        if (player != null && player.currentDepotChessBoard == this)
            results.Add(new InteractionCandidate { Target = this, Score = 3f });
    }

    public void Highlight(bool highlight) { }

    void SitLocally(PlayerController pc, PieceColor color)
    {
        var third = pc.GetComponent<ThirdPersonController>();
        var character = pc.GetComponent<CharacterController>();
        if (third) third.enabled = false;
        if (character) character.enabled = false;
        pc.playerCamVCam.Priority = 5;
        var cam = color == PieceColor.White ? whiteCam : blackCam;
        if (cam) { cam.gameObject.SetActive(true); cam.Priority = 11; }
        pc.currentDepotChessBoard = this;
        pc.IsPlayingChess = true;
        padSquare = color == PieceColor.White ? new Vector2Int(4, 1) : new Vector2Int(4, 6);
        SetCursor(pc, true);
    }

    public void RejectSeat()
    {
        if (PlayerController.Local != null && PlayerController.Local.currentDepotChessBoard == this) // coop:allow(local) targeted seat denial
            StandLocally(PlayerController.Local); // coop:allow(local) local camera/input cleanup
    }

    public void StandLocally(PlayerController pc)
    {
        if (pc == null || pc.currentDepotChessBoard != this) return;
        CancelDrag();
        if (padOutline != null) padOutline.enabled = false;
        var third = pc.GetComponent<ThirdPersonController>();
        var character = pc.GetComponent<CharacterController>();
        if (third) third.enabled = true;
        if (character) character.enabled = true;
        pc.playerCamVCam.Priority = 11;
        if (whiteCam) { whiteCam.Priority = 5; whiteCam.gameObject.SetActive(false); }
        if (blackCam) { blackCam.Priority = 5; blackCam.gameObject.SetActive(false); }
        pc.currentDepotChessBoard = null;
        pc.IsPlayingChess = false;
        SetCursor(pc, false);
    }

    static void SetCursor(PlayerController pc, bool seated)
    {
        if (pc == null || !pc.IsOwner) return;
        var input = pc.GetComponent<StarterAssetsInputs>();
        if (input != null)
        {
            input.cursorLocked = !seated;
            input.cursorInputForLook = !seated;
            if (seated) input.look = Vector2.zero;
        }
        Cursor.lockState = seated ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = seated;
    }

    void Update()
    {
        var net = DepotChessNet.Instance;
        var local = PlayerController.Local; // coop:allow(local) each client reads only its own chess input
        if (local != null && local.currentDepotChessBoard == this && Gamepad.current != null &&
            GameControls.PadDown(GameControls.Action.Back))
        {
            if (selected != null) CancelDrag();
            else OnInteract(local.gameObject);
            return;
        }
        if (net == null || local == null || local.currentDepotChessBoard != this || !net.LocalTurn(lot))
        {
            if (selected != null) CancelDrag();
            if (padOutline != null) padOutline.enabled = false;
            return;
        }
        if (Gamepad.current != null) UpdatePad();
        else if (padOutline != null) padOutline.enabled = false;
        if (!mainCam) mainCam = Camera.main;
        if (!mainCam) return;
        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
        if (Input.GetMouseButtonDown(0) && Physics.Raycast(ray, out var pieceHit, 100f, pieceLayer))
        {
            var piece = pieceHit.collider.GetComponentInParent<DepotChessPiece>();
            if (piece != null && Array.IndexOf(pieces, piece) >= 0) Select(piece);
        }
        if (selected == null || padSelecting) return;
        if (Physics.Raycast(ray, out var boardHit, 100f, boardLayer))
        {
            var square = boardHit.collider.GetComponent<ChessSquare>();
            if (square != null && squares.TryGetValue(square.coordinates, out var own) && own == square)
            {
                Vector3 target = boardHit.point;
                target.y = boardParent.position.y + 0.12f;
                selected.transform.position = Vector3.SmoothDamp(selected.transform.position, target, ref dragVelocity, 0.06f, 40f);
            }
        }
        if (Input.GetMouseButtonUp(0))
        {
            ChessSquare square = null;
            if (Physics.Raycast(ray, out var dropHit, 100f, boardLayer))
            {
                var candidate = dropHit.collider.GetComponent<ChessSquare>();
                if (candidate != null && squares.TryGetValue(candidate.coordinates, out var own) && own == candidate) square = candidate;
            }
            Drop(square);
        }
    }

    void UpdatePad()
    {
        var pad = Gamepad.current;
        Vector2 axis = pad.dpad.ReadValue();
        if (axis.sqrMagnitude < 0.25f) axis = pad.leftStick.ReadValue();
        if (axis.sqrMagnitude > 0.3f && Time.unscaledTime >= padRepeatAt)
        {
            padRepeatAt = Time.unscaledTime + 0.18f;
            bool white = DepotChessNet.Instance.LocalColor(lot) == PieceColor.White;
            int sign = white ? 1 : -1;
            if (Mathf.Abs(axis.x) > Mathf.Abs(axis.y)) padSquare.x = Mathf.Clamp(padSquare.x + (axis.x > 0 ? sign : -sign), 0, 7);
            else padSquare.y = Mathf.Clamp(padSquare.y + (axis.y > 0 ? sign : -sign), 0, 7);
        }
        ShowPadSquare();
        if (!GameControls.PadDown(GameControls.Action.Jump)) return;
        if (selected == null)
        {
            var p = PieceAt(padSquare);
            if (p != null) Select(p);
            padSelecting = selected != null;
        }
        else if (squares.TryGetValue(padSquare, out var square)) Drop(square);
    }

    void ShowPadSquare()
    {
        if (!squares.TryGetValue(padSquare, out var square) ||
            !squares.TryGetValue(new Vector2Int(0, 0), out var a) ||
            !squares.TryGetValue(new Vector2Int(1, 0), out var b) ||
            !squares.TryGetValue(new Vector2Int(0, 1), out var c)) return;
        if (padOutline == null)
        {
            var go = new GameObject("Depot chess pad cursor");
            go.transform.SetParent(transform, false);
            padOutline = go.AddComponent<LineRenderer>();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader != null) padOutlineMaterial = new Material(shader);
            if (padOutlineMaterial != null) padOutline.material = padOutlineMaterial;
            padOutline.useWorldSpace = true;
            padOutline.loop = true;
            padOutline.positionCount = 4;
            padOutline.widthMultiplier = Mathf.Max(0.009f, Vector3.Distance(a.transform.position, b.transform.position) * 0.055f);
            padOutline.startColor = padOutline.endColor = new Color(1f, 0.76f, 0.14f);
        }
        Vector3 center = square.transform.position + Vector3.up * 0.035f;
        Vector3 file = (b.transform.position - a.transform.position) * 0.44f;
        Vector3 rank = (c.transform.position - a.transform.position) * 0.44f;
        padOutline.SetPosition(0, center - file - rank);
        padOutline.SetPosition(1, center + file - rank);
        padOutline.SetPosition(2, center + file + rank);
        padOutline.SetPosition(3, center - file + rank);
        padOutline.enabled = true;
    }

    DepotChessPiece PieceAt(Vector2Int square)
    {
        if (!DepotChessNet.Instance.TryState(lot, out var state)) return null;
        byte location = (byte)(square.y * 8 + square.x);
        for (int i = 0; i < pieces.Length && i < state.locations.Length; i++)
            if (state.locations[i] == location) return pieces[i];
        return null;
    }

    void Select(DepotChessPiece piece)
    {
        var net = DepotChessNet.Instance;
        if (!net.TryState(lot, out var state) || piece.color != state.turn) return;
        byte location = state.locations[piece.index];
        if (location >= 64) return;
        selected = piece;
        originalSquare = new Vector2Int(location % 8, location / 8);
        piece.originalPosition = piece.transform.position;
        pickedUpRotation = piece.transform.rotation;
        dragVelocity = Vector3.zero;
    }

    void Drop(ChessSquare square)
    {
        if (selected == null) return;
        var piece = selected;
        selected = null;
        padSelecting = false;
        piece.transform.rotation = pickedUpRotation;
        if (square == null || square.coordinates == originalSquare)
        {
            piece.transform.position = piece.originalPosition;
            return;
        }
        if (!DepotChessNet.Instance.LocalMove(lot, originalSquare, square.coordinates))
            piece.transform.position = piece.originalPosition;
        else piece.transform.position = square.transform.position;
    }

    void CancelDrag()
    {
        if (selected != null)
        {
            selected.transform.position = selected.originalPosition;
            selected.transform.rotation = pickedUpRotation;
        }
        selected = null;
        padSelecting = false;
    }

    public Vector3 LocationPosition(byte location, PieceColor color)
    {
        if (location < 64 && squares.TryGetValue(new Vector2Int(location % 8, location / 8), out var sq)) return sq.transform.position;
        Transform parent = color == PieceColor.White ? whiteCaptured : blackCaptured;
        if (!parent) return transform.position;
        int index = location - 64;
        int column = index / Mathf.Max(1, capturedPerColumn), row = index % Mathf.Max(1, capturedPerColumn);
        Vector3 outward = Vector3.Project(parent.position - boardParent.position, boardParent.right);
        outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : boardParent.right;
        return parent.position + parent.forward * row * capturedSpacing + outward * column * capturedSpacing;
    }

    public void Apply(DepotChessNet.ChessState state, bool reset)
    {
        BuildLookups();
        CancelDrag();
        // A board/seat snapshot must never finish a sorter delivery or a bag rip at the other end of the rail.
        if (arm && (!track || track.AtChess) && arm.HasPendingPlans) arm.FastForward();
        if (reset)
        {
            foreach (var routine in toppleTweens.Values) if (routine != null) StopCoroutine(routine);
            toppleTweens.Clear();
            toppled.Clear();
        }
        for (int i = 0; i < pieces.Length && i < state.locations.Length; i++)
        {
            var p = pieces[i];
            if (!p) continue;
            byte location = state.locations[i];
            SetType(p, (PieceType)state.types[i]);
            if (location == 255) continue;
            if (tweens.TryGetValue(p, out var running) && running != null) StopCoroutine(running);
            var collider = p.GetComponent<Collider>();
            if (collider) collider.enabled = location < 64;
            if (reset && homeRotations != null) p.transform.rotation = homeRotations[i];
            if (!toppled.Contains(p)) tweens[p] = StartCoroutine(Glide(p.transform, LocationPosition(location, p.color), reset ? 0.8f : 0.2f));
        }
        if (turnIndicator)
            turnIndicator.text = state.result == GameResult.InProgress ? $"{state.turn}'s Turn" :
                state.result == GameResult.WhiteWins ? "Checkmate! White wins." :
                state.result == GameResult.BlackWins ? "Checkmate! Black wins." : "Draw";
        if (state.result == GameResult.WhiteWins || state.result == GameResult.BlackWins)
        {
            PieceColor loser = state.result == GameResult.WhiteWins ? PieceColor.Black : PieceColor.White;
            foreach (var p in pieces)
                if (p && p.color == loser && p.type == PieceType.King && toppled.Add(p))
                    toppleTweens[p] = StartCoroutine(Topple(p));
        }
        if (arm && (!track || track.AtChess))
            arm.Idle = state.whiteSeat != ulong.MaxValue && state.turn == PieceColor.White ? MagnusArm.IdlePose.Watch : MagnusArm.IdlePose.Think;
    }

    IEnumerator Topple(DepotChessPiece king)
    {
        yield return new WaitForSeconds(0.25f);
        Quaternion start = king.transform.rotation;
        Vector3 basePos = king.transform.position;
        Vector3 axis = Vector3.Cross(Vector3.up, boardParent.forward).normalized;
        float radius = looks.TryGetValue((king.color, PieceType.King), out var look) ? look.width * 0.5f : 0.03f;
        for (float t = 0f; t < 0.7f; t += Time.deltaTime)
        {
            float s = t / 0.7f;
            s *= s;
            king.transform.rotation = Quaternion.AngleAxis(88f * s, axis) * start;
            king.transform.position = basePos + Vector3.up * radius * s;
            yield return null;
        }
        king.transform.rotation = Quaternion.AngleAxis(88f, axis) * start;
        king.transform.position = basePos + Vector3.up * radius;
    }

    static IEnumerator Glide(Transform piece, Vector3 destination, float seconds)
    {
        Vector3 start = piece.position;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            piece.position = Vector3.Lerp(start, destination, Mathf.SmoothStep(0f, 1f, t / seconds));
            yield return null;
        }
        piece.position = destination;
    }

    void SetType(DepotChessPiece piece, PieceType type)
    {
        if (piece.type == type) return;
        piece.type = type;
        if (!looks.TryGetValue((piece.color, type), out var look)) return;
        var mesh = piece.GetComponent<MeshFilter>();
        if (mesh) mesh.sharedMesh = look.mesh;
        var renderer = piece.GetComponent<Renderer>();
        if (renderer && look.materials != null) renderer.sharedMaterials = look.materials;
        piece.transform.localScale = look.scale;
        var collider = piece.GetComponent<MeshCollider>();
        if (collider && look.colliderMesh) collider.sharedMesh = look.colliderMesh;
    }

    public void PlayPlan(int id, byte[] indices, byte[] targets, byte promotionIndex, PieceType promotion)
    {
        if (!arm) return;
        var steps = new List<MagnusArm.Step>();
        for (int i = 0; i < indices.Length && i < targets.Length; i++)
        {
            int index = indices[i];
            if (index >= pieces.Length || !pieces[index]) continue;
            var p = pieces[index];
            var look = looks.TryGetValue((p.color, p.type), out var l) ? l : new PieceLook { height = 0.08f, width = 0.05f };
            var step = new MagnusArm.Step
            {
                piece = p.transform,
                to = LocationPosition(targets[i], p.color),
                gripHeight = look.height * 0.6f,
                gripWidth = look.width * 0.45f
            };
            if (index == promotionIndex) step.onPlaced = () => SetType(p, promotion);
            steps.Add(step);
        }
        arm.PlayPlan(id, steps);
    }

    public void PlayGesture(MagnusArm.Gesture gesture) { if (arm) arm.PlayGesture(gesture); }

    public void Say(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        Vector3 head = arm ? arm.HeadPosition : transform.position + Vector3.up;
        GameHUD.FloatingText(head + Vector3.up * 0.35f, line, ChessBanter.BubbleColor, 0.85f);
        Sfx.Play("beep", head, 0.35f, 1.6f);
    }
}
