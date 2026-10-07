using UnityEngine;

/// <summary>
/// The garage upgrades' wiring board (on the GarageUpgrades root that Setup_GarageUpgrades builds in ShopScene). It holds
/// references to the existing garage machines (sort station, table, compactor, TIP IT lever, Magnus) so the upgrade
/// components never search the world for them (Docs/Multiplayer.md §9 rule 3). Setup wires them; if one is missing
/// (e.g. Setup_SortingStation rebuilt its root after this was wired) it is re-resolved from the same scene's roots,
/// or strictly from this DepotLot's Interior in DepotRow.
/// </summary>
[DefaultExecutionOrder(-50)]
public class GarageRig : MonoBehaviour
{
    public SortStation station;
    public SortingTable table;
    public Compactor compactor;
    public TruckUnloader unloader;
    public MagnusTrack track;
    public MagnusArm arm;
    public MagnusChessBot chessBot;
    public ChessController chess;

    [Header("Built by Setup_GarageUpgrades")]
    [Tooltip("The XL table's fold-out front leaf (handed to SortingTable.xlLeaf at runtime).")]
    public GameObject tableLeaf;
    [Tooltip("Where Magnus parks to sort (end of the rail branch; handed to MagnusTrack at runtime).")]
    public Transform sortStop;

    bool resolved;

    /// <summary>Is this machine the one that decides garage automation? (The host / single player; never a guest.)</summary>
    public static bool Decides => !GameSession.IsGuest;

    void Awake() => Resolve();

    /// <summary>Fill missing references without ever borrowing a machine from another DepotLot. Idempotent.</summary>
    public void Resolve()
    {
        var lot = GetComponentInParent<DepotLot>(true);
        if (lot != null)
        {
            var scope = lot.interior != null ? lot.interior.transform : null;
            // A broken/missing interior is safer than wiring a different business's table or Magnus.
            if (scope == null || scope.parent != lot.transform)
            {
                station = null;
                table = null;
                compactor = null;
                unloader = null;
                track = null;
                arm = null;
                chessBot = null;
                chess = null;
                tableLeaf = null;
                sortStop = null;
                resolved = false;
                return;
            }
            if (!Inside(station, scope)) station = null;
            if (!Inside(table, scope)) table = null;
            if (!Inside(compactor, scope)) compactor = null;
            if (!Inside(unloader, scope)) unloader = null;
            if (!Inside(track, scope)) track = null;
            if (!Inside(arm, scope)) arm = null;
            if (!Inside(chessBot, scope)) chessBot = null;
            if (!Inside(chess, scope)) chess = null;
            if (tableLeaf != null && !tableLeaf.transform.IsChildOf(scope)) tableLeaf = null;
            if (sortStop != null && !sortStop.IsChildOf(scope)) sortStop = null;

            if (!station) station = scope.GetComponentInChildren<SortStation>(true);
            if (!table) table = scope.GetComponentInChildren<SortingTable>(true);
            if (!compactor) compactor = scope.GetComponentInChildren<Compactor>(true);
            if (!unloader) unloader = scope.GetComponentInChildren<TruckUnloader>(true);
            if (!track) track = scope.GetComponentInChildren<MagnusTrack>(true);
            if (!arm) arm = scope.GetComponentInChildren<MagnusArm>(true);
            if (!chessBot) chessBot = scope.GetComponentInChildren<MagnusChessBot>(true);
            if (!chess) chess = scope.GetComponentInChildren<ChessController>(true);
            if (!tableLeaf) { var leaf = transform.Find("Table/XLLeaf"); if (leaf) tableLeaf = leaf.gameObject; }
            if (!sortStop) sortStop = transform.Find("Sort Stop");
            resolved = true;
            HandOver();
            return;
        }

        if (resolved && station && table && compactor && unloader && track && arm) { HandOver(); return; }
        resolved = true;
        var scene = gameObject.scene;
        if (scene.IsValid())
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!station) station = root.GetComponentInChildren<SortStation>(true);
                if (!table) table = root.GetComponentInChildren<SortingTable>(true);
                if (!compactor) compactor = root.GetComponentInChildren<Compactor>(true);
                if (!unloader) unloader = root.GetComponentInChildren<TruckUnloader>(true);
                if (!track) track = root.GetComponentInChildren<MagnusTrack>(true);
                if (!arm) arm = root.GetComponentInChildren<MagnusArm>(true);
                if (!chessBot) chessBot = root.GetComponentInChildren<MagnusChessBot>(true);
                if (!chess) chess = root.GetComponentInChildren<ChessController>(true);
            }
        }
        if (station != null)
        {
            if (!table) table = station.table;
            if (!compactor) compactor = station.compactor;
        }
        HandOver();
    }

    static bool Inside(Component component, Transform scope) => component != null && component.transform.IsChildOf(scope);

    void HandOver()
    {
        // Migrate the authored solo/four-lot copies before their conveyors start feeding. All machine references,
        // upgrade visibility gates and business permissions stay attached to the same objects.
        if (Application.isPlaying) GarageConveyorLayout.Ensure(this);
        if (table != null && tableLeaf != null && table.xlLeaf != tableLeaf)
        {
            // The leaf's own catch trigger feeds the table (re-pointed if the table was rebuilt since setup).
            foreach (var trig in tableLeaf.GetComponentsInChildren<CarryReceiverTrigger>(true))
                if (trig.receiverOverride == null) trig.receiverOverride = table;
            table.xlLeaf = tableLeaf;
            table.ApplyUpgrades();
        }
        if (track != null && sortStop != null && track.SortStop != sortStop) track.SortStop = sortStop;
    }

    /// <summary>The SortBin for a category in the station (null if none).</summary>
    public SortBin BinFor(ItemCategory category)
    {
        var bins = station != null ? station.bins : null;
        if (bins == null || bins.Length == 0)
        {
            if (station != null)
            {
                var root = station.transform.parent != null ? station.transform.parent : station.transform;
                bins = root.GetComponentsInChildren<SortBin>();
            }
        }
        if (bins == null) return null;
        foreach (var b in bins) if (b != null && b.category == category) return b;
        return null;
    }
}
