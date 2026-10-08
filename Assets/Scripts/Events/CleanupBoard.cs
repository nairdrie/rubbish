using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// F5 (Docs/Multiplayer.md §14.5): the Big Cleanup board on Depot Row, a big green city noticeboard on two posts just north of
/// lot 1, facing the road. Built at runtime whenever the town is loaded (no scene edit), it shows the live scoreboard while
/// an event runs, the countdown before it, the winner after it, and "next Big Cleanup ~09:40" the rest of the time. Local
/// presentation of replicated state only.
/// </summary>
public sealed class CleanupBoard : MonoBehaviour
{
    public static Vector3 Spot
    {
        get
        {
            int lot = DepotRowLayout.LotCount - 1;
            var edge = DepotRowLayout.PadLocalEdge(lot);
            return DepotRowLayout.StreetPoint(lot, new Vector3(edge.x - 6f, 0f, edge.y - 1.5f));
        }
    }

    GameObject board;
    TextMeshPro title, body;
    Transform lamp;
    float nextText, punch;
    string lastWinner = "";
    readonly List<CleanupScore> ranked = new List<CleanupScore>(4);
    readonly StringBuilder sb = new StringBuilder(256);

    void Start() => BigCleanupNet.PhaseChanged += OnPhase;
    void OnDestroy() => BigCleanupNet.PhaseChanged -= OnPhase;

    void OnPhase(CleanupPhase from, CleanupState s)
    {
        punch = 1f;
        if (s.Phase == CleanupPhase.Results && s.winnerId.Length > 0)
            lastWinner = $"<color={BusinessColors.Hex(s.winnerColor)}>●</color> {ShortName(s.winnerName.ToString(), 22)}";
    }

    void Update()
    {
        if (!WorldLifeRunner.IsAwake)
        {
            if (board != null) { Destroy(board); board = null; }
            return;
        }
        if (board == null) Build();
        if (board == null) return;
        punch = Mathf.MoveTowards(punch, 0f, Time.deltaTime * 2f);
        board.transform.localScale = Vector3.one * (1f + 0.08f * Mathf.Sin(punch * Mathf.PI * 2f) * punch);
        if (Time.unscaledTime < nextText) return;
        nextText = Time.unscaledTime + 0.25f;
        Fill();
    }

    void Build()
    {
        var scene = WorldLifeRunner.Context != null ? WorldLifeRunner.Context.Scene : default;
        Vector3 p = Spot;
        if (Physics.Raycast(p + Vector3.up * 40f, Vector3.down, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
        board = new GameObject("[CleanupBoard]");
        if (scene.IsValid()) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(board, scene);
        board.transform.SetPositionAndRotation(p, DepotRowLayout.StreetFacing);
        var mat = BoardMaterial();
        // Keep supports behind the panel; front-mounted posts hide the first letters of longer lines.
        Box("PostL", new Vector3(-2.3f, 1.6f, -0.18f), new Vector3(0.22f, 3.2f, 0.22f), new Color(0.35f, 0.35f, 0.38f), mat);
        Box("PostR", new Vector3(2.3f, 1.6f, -0.18f), new Vector3(0.22f, 3.2f, 0.22f), new Color(0.35f, 0.35f, 0.38f), mat);
        Box("Panel", new Vector3(0f, 3.0f, 0f), new Vector3(5.2f, 2.9f, 0.16f), new Color(0.12f, 0.42f, 0.18f), mat);
        Box("Trim", new Vector3(0f, 4.52f, 0f), new Vector3(5.4f, 0.18f, 0.24f), new Color(1f, 0.82f, 0.2f), mat);
        lamp = Box("Lamp", new Vector3(0f, 4.75f, 0.05f), new Vector3(0.35f, 0.25f, 0.35f), new Color(1f, 0.3f, 0.2f), mat).transform;

        title = Text("Title", new Vector3(0f, 4.05f, 0.1f), 4.4f, new Color(1f, 0.9f, 0.3f), new Vector2(5f, 0.7f));
        body = Text("Body", new Vector3(0f, 2.6f, 0.1f), 2.5f, Color.white, new Vector2(4.8f, 1.9f));
        body.alignment = TextAlignmentOptions.TopLeft;
        title.text = "BIG CLEANUP";
    }

    GameObject Box(string name, Vector3 local, Vector3 size, Color c, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(board.transform, false);
        go.transform.localPosition = local;
        go.transform.localScale = size;
        if (name != "PostL" && name != "PostR")
        {
            var collider = go.GetComponent<Collider>();
            if (Application.isPlaying) Destroy(collider);
            else DestroyImmediate(collider); // the production board is also exercised by EditMode visual checks
        }
        var r = go.GetComponent<MeshRenderer>();
        if (mat != null) r.sharedMaterial = mat;
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_BaseColor", c);
        mpb.SetColor("_Color", c);
        r.SetPropertyBlock(mpb);
        return go;
    }

    TextMeshPro Text(string name, Vector3 local, float size, Color c, Vector2 box)
    {
        var go = new GameObject(name);
        go.transform.SetParent(board.transform, false);
        go.transform.localPosition = local;
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // read from the road side (+Z)
        var t = go.AddComponent<TextMeshPro>();
        var font = UiTheme.BodyFont;
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = c;
        t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.richText = true;
        t.rectTransform.sizeDelta = box;
        return t;
    }

    static Material sharedMat;

    static string ShortName(string name, int max) =>
        string.IsNullOrEmpty(name) || name.Length <= max ? name ?? "" : name.Substring(0, max - 1) + "…";

    /// <summary>One compact row for the physical Depot Row board, with room left for the bag/kg columns.</summary>
    public static string ScoreRow(CleanupScore r, int rank) =>
        $"{rank}. <color={BusinessColors.Hex(r.color)}>●</color> {ShortName(r.Name, 18)}<pos=66%>{r.bags}{(r.bags == 1 ? " bag" : " bags")}<pos=86%>{r.Kg:0} kg";

    static Material BoardMaterial()
    {
        if (sharedMat != null) return sharedMat;
        // CreatePrimitive's default material is the built-in pipeline's Standard material, not a
        // guaranteed URP material. It made this board magenta in players. The serialized resource
        // also gives the build a real shader dependency; Shader.Find alone would not preserve it.
        sharedMat = Resources.Load<Material>("CleanupBoard");
        return sharedMat;
    }

    void Fill()
    {
        var n = BigCleanupNet.Instance;
        sb.Clear();
        bool live = n != null && n.IsSpawned;
        var s = live ? n.State : default;
        float blink = Mathf.PingPong(Time.time * 2f, 1f);
        if (lamp != null) lamp.localScale = new Vector3(0.35f, 0.25f, 0.35f) * (live && s.Phase != CleanupPhase.Idle ? 0.8f + 0.5f * blink : 0.8f);
        if (!live || s.Phase == CleanupPhase.Idle)
        {
            title.text = "BIG CLEANUP";
            int day = GameClock.Day;
            float h = BigCleanupNet.StartHourFor(day);
            if (GameClock.Hours > h) { day++; h = BigCleanupNet.StartHourFor(day); }
            sb.Append("Next: day ").Append(day).Append(", about ").Append(GameClock.FormatHours(h)).Append('\n');
            sb.Append("Double trash for 10 minutes.\nMost bags wins cash + the Cup!");
            if (lastWinner.Length > 0) sb.Append("\n\nLast winner: ").Append(lastWinner);
            body.text = sb.ToString();
            return;
        }
        switch (s.Phase)
        {
            case CleanupPhase.Announce:
                title.text = "STARTING IN " + Mathf.CeilToInt(n.SecondsLeft);
                body.text = "Get to your trucks!\nEvery curb bag counts.";
                return;
            case CleanupPhase.Results:
                title.text = "TIME'S UP!";
                body.text = s.winnerId.Length > 0
                    ? $"Winner:\n<color={BusinessColors.Hex(s.winnerColor)}>●</color> {ShortName(s.winnerName.ToString(), 22)}\n+{Economy.Money(s.prizeCents / 100f)}"
                    : "No winner this time.";
                return;
        }
        float left = n.SecondsLeft;
        title.text = $"BIG CLEANUP {(int)(left / 60f)}:{(int)(left % 60f):00}";
        n.RankedLive(ranked);
        for (int i = 0; i < ranked.Count && i < 4; i++)
        {
            var r = ranked[i];
            sb.Append(i > 0 ? "\n" : "").Append(ScoreRow(r, i + 1));
        }
        if (ranked.Count == 0) sb.Append("Nobody yet!");
        body.text = sb.ToString();
    }
}
