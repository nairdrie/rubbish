using System.Collections;
using UnityEngine;

/// <summary>
/// Kevin's Crew deliveries (Docs/Progression.md §6.5): bags Kevin collected wait in GameSession.DepotDeliveries and land
/// on the tipping floor when you come into the garage, and at 18:00 while you're here. The physical pile is capped at
/// <see cref="pileCap"/> bags; the rest stay in the list for the next drop. Host decides; the bags tumble in through a
/// chute high on the wall over the pile.
/// </summary>
public class DepotDeliveryDropper : MonoBehaviour
{
    public GarageRig rig;
    [Tooltip("Where delivered bags drop from (high over the tipping floor).")]
    public Transform chute;
    public int pileCap = 40;
    public int dropHour = 18;
    public float dropInterval = 0.12f;
    [Tooltip("Seconds after the garage loads before the first drop (let the scene settle).")]
    public float entryDelay = 2.5f;

    bool dropping;

    /// <summary>Bags dropped this session (tests / HUD).</summary>
    public int Dropped { get; private set; }

    void Awake()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
    }

    void OnEnable() => GameClock.HourChanged += OnHour;
    void OnDisable() => GameClock.HourChanged -= OnHour;

    IEnumerator Start()
    {
        yield return new WaitForSeconds(entryDelay);
        DropPending("Kevin's Crew dropped off");
    }

    void OnHour(int hour)
    {
        if (hour == dropHour) DropPending("18:00 delivery! Kevin's Crew dropped off");
    }

    /// <summary>Drop waiting deliveries onto the tipping floor (up to the pile cap). Returns how many will drop.</summary>
    public int DropPending(string what)
    {
        if (dropping || !GarageRig.Decides || GameSession.DepotDeliveries.Count == 0) return 0;
        int room = pileCap - GaragePile.CountInPile(rig != null ? rig.compactor : null);
        int n = Mathf.Min(room, GameSession.DepotDeliveries.Count);
        if (n <= 0)
        {
            GameHUD.Toast($"Kevin's bags are waiting: the tipping floor is full ({pileCap} bags)!", ToastStyle.Bad, 2.5f);
            return 0;
        }
        StartCoroutine(DropRoutine(n, what));
        return n;
    }

    IEnumerator DropRoutine(int n, string what)
    {
        dropping = true;
        var un = rig != null ? rig.unloader : null;
        Vector3 center = un != null ? un.pileCenter : transform.position;
        Vector2 size = un != null ? un.pileSize : new Vector2(1.5f, 2f);
        Vector3 from = chute != null ? chute.position : center + Vector3.up * 3.5f;
        Sfx.Play("horn", from, 0.5f, 1.5f);
        Sfx.Play("beep", from, 0.7f, 0.9f);
        int left = GameSession.DepotDeliveries.Count - n;
        GameHUD.Toast(left > 0 ? $"{what} {n} {SortingKit.Plural(n, "bag", "bags")} ({left} still waiting). He says hi."
            : $"{what} {n} {SortingKit.Plural(n, "bag", "bags")}. He says hi.", ToastStyle.Good, 3f);
        for (int i = 0; i < n; i++)
        {
            var contents = GameSession.TakeDepotDelivery();
            if (contents == null) break;
            int h = PickupVisuals.StableHash(contents.bagId ?? i.ToString());
            Vector3 target = new Vector3(center.x + ((h & 255) / 255f - 0.5f) * size.x, center.y + 0.05f, center.z + (((h >> 8) & 255) / 255f - 0.5f) * size.y);
            var bag = TrashBag.Spawn(contents, from, Quaternion.Euler(0f, h % 360, 0f), SortingKit.Ballistic(from, target, 0.7f));
            if (bag != null && bag.Body != null) bag.Body.angularVelocity = new Vector3((h & 7) - 3, (h >> 3 & 7) - 3, (h >> 6 & 7) - 3);
            Dropped++;
            Sfx.Play("whoosh", from, 0.4f, 1.1f + (i % 5) * 0.05f);
            if (i % 3 == 0) Fx.Dust(target, 0.5f);
            yield return new WaitForSeconds(dropInterval);
        }
        dropping = false;
    }
}
