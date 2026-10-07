using System.Collections;
using UnityEngine;

/// <summary>
/// "Double-Wide Table w/ Cupholders &amp; Magnet Strip" (sorting_table L2, Docs/Progression.md §6.2): "Metal just... leaves."
/// Every <see cref="interval"/> seconds one revealed Metal item resting on the table slides across the top to the back
/// edge and hops into the METAL bin: a correct sort (pay + fact), but no combo. Host decides.
/// </summary>
public class MagnetStrip : MonoBehaviour
{
    public SortingTable table;
    public SortBin metalBin;
    public float interval = 1.5f;
    [Tooltip("Optional: the strip visual (buzzes when it pulls).")]
    public Transform stripVisual;

    float next;
    bool busy;

    void Awake()
    {
        if (table == null) table = GetComponentInParent<SortingTable>();
        // Built under the garage rig: re-resolve if the sorting station was rebuilt since setup.
        var rig = GetComponentInParent<GarageRig>();
        if (rig != null)
        {
            rig.Resolve();
            if (table == null) table = rig.table;
            if (metalBin == null) metalBin = rig.BinFor(ItemCategory.Metal);
        }
    }

    void Update()
    {
        if (DepotMachineContext.Level(this, ShopIds.SortingTable) < 2 || busy || GameSession.IsGuest || table == null || metalBin == null) return;
        if (Time.time < next) return;
        next = Time.time + interval;
        var li = PickMetal();
        if (li != null) StartCoroutine(Pull(li));
    }

    LooseItem PickMetal()
    {
        LooseItem best = null;
        float bestZ = float.MinValue;
        var all = LooseItem.All;
        for (int i = 0; i < all.Count; i++)
        {
            var li = all[i];
            if (li == null || !li.Revealed || li.Category != ItemCategory.Metal || !SortingKit.IsLoose(li)) continue;
            if (li.Body == null || li.Body.isKinematic || li.IsInScriptedFlight || li.Body.linearVelocity.sqrMagnitude > 0.05f) continue;
            if (!table.IsOnTop(li.transform.position)) continue;
            // The one nearest the strip (the back edge) goes first.
            float z = table.transform.InverseTransformPoint(li.transform.position).z;
            if (z > bestZ) { bestZ = z; best = li; }
        }
        return best;
    }

    IEnumerator Pull(LooseItem li)
    {
        busy = true;
        li.SetInFlight();
        Vector3 from = li.transform.position;
        Vector3 l = table.transform.InverseTransformPoint(metalBin.TossTargetPosition);
        Vector3 edge = table.TopPoint(Mathf.Clamp(l.x - table.topOffset.x, -table.topSize.x * 0.5f + 0.2f, table.topSize.x * 0.5f - 0.2f),
            table.topSize.y * 0.5f - 0.12f) + Vector3.up * 0.04f;
        Sfx.Play("beep", from, 0.35f, 2f);
        if (stripVisual != null) Juice.Punch(stripVisual, 0.15f, 0.2f);
        Fx.Burst(from, new Color(0.6f, 0.75f, 1f), 6, 1.5f);
        // Slide (it skitters across the top toward the strip), then the bin's arc takes it the rest of the way.
        float d = Vector3.Distance(from, edge);
        float dur = Mathf.Clamp(d * 0.35f, 0.15f, 0.5f);
        for (float t = 0f; t < dur; t += Time.deltaTime)
        {
            if (li == null || li.State == CarryState.Consumed || li.State == CarryState.Carried) { busy = false; yield break; }
            float k = SortingKit.EaseInQuad(t / dur);
            li.transform.position = Vector3.Lerp(from, edge, k) + Vector3.up * Mathf.Abs(Mathf.Sin(t * 40f)) * 0.01f;
            yield return null;
        }
        busy = false;
        if (li == null || li.State == CarryState.Consumed || li.State == CarryState.Carried) yield break;
        GameHUD.FloatingText(edge + Vector3.up * 0.3f, "*clink*", new Color(0.7f, 0.8f, 1f), 0.8f);
        Sfx.Play("toss_in", edge, 0.5f, 1.6f);
        metalBin.AcceptFromRobot(li, table.RandomTopPoint(0.3f), false);
    }
}
