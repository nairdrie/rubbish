using System;
using UnityEngine;

/// <summary>
/// Shows the garage upgrade props by level (Docs/Progression.md §9.5 C.6): everything Setup_GarageUpgrades builds under
/// the GarageUpgrades root is always in the scene, and this just switches each piece on when its upgrade level is in
/// range (on load and on GameSession.UpgradeChanged). A DepotRow copy reads its own lot's replicated levels rather
/// than the viewer's business. Newly bought pieces pop in with a puff and a sparkle.
/// No runtime scene edits beyond SetActive (and UpgradeAttachment's one-time re-parent onto Magnus).
/// </summary>
public class GarageUpgradeVisuals : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public string id;
        public int minLevel = 1;
        public int maxLevel = 99;
        public GameObject target;
        [Tooltip("Optional second condition (both must hold), e.g. the job's kitchen tongs: magnus_brain >= 1 AND magnus_gripper == 0.")]
        public string andId;
        public int andMinLevel;
        public int andMaxLevel = 99;

        /// <summary>Should the target show at the current upgrade levels?</summary>
        public bool Wanted() => Wanted(UpgradeEffects.Level);

        public bool Wanted(Func<string, int> levelFor)
        {
            int lvl = levelFor(CurrentId(id));
            if (lvl < minLevel || lvl > maxLevel) return false;
            if (string.IsNullOrEmpty(andId)) return true;
            int l2 = levelFor(CurrentId(andId));
            return l2 >= andMinLevel && l2 <= andMaxLevel;
        }
    }

    public Entry[] entries = new Entry[0];

    /// <summary>The upgrade an authored gate answers to today. Scene copies built before save v6 gate the delivery chute on
    /// the removed Kevin's Crew upgrade; the chute belongs to the raccoon crew now, so it shows with the Collar Chip.</summary>
    public static string CurrentId(string authoredId) => authoredId == KevinSeverance.LegacyUpgradeId ? CallingIds.CollarChip : authoredId;

    bool applied;

    void OnEnable()
    {
        // Existing authored ShopScene/DepotRow copies predate the removed floor annotation.
        var oldLane = transform.Find("RailBranch/LaneText");
        if (oldLane != null) oldLane.gameObject.SetActive(false);
        GameSession.UpgradeChanged += OnUpgradeChanged;
        BusinessUpgradeNet.Changed += OnRemoteUpgradeChanged;
        Apply(false);
    }

    void OnDisable()
    {
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        BusinessUpgradeNet.Changed -= OnRemoteUpgradeChanged;
    }

    void OnUpgradeChanged(string id, int level) => Apply(true);
    void OnRemoteUpgradeChanged() => Apply(false);

    /// <summary>Match every entry to its upgrade level. <paramref name="celebrate"/> = juice what just appeared.</summary>
    public void Apply(bool celebrate)
    {
        if (entries == null) return;
        var lot = GetComponentInParent<DepotLot>(true);
        bool remoteLot = lot != null && lot.lot != DepotRowWorld.LocalLot;
        Func<string, int> levelFor = remoteLot
            ? id => BusinessUpgradeNet.HasStateForLot(lot.lot) ? BusinessUpgradeNet.LevelForLot(lot.lot, id) : 0
            : UpgradeEffects.Level;
        foreach (var e in entries)
        {
            if (e == null || e.target == null || string.IsNullOrEmpty(e.id)) continue;
            // Replaced by the joint-driven industrial arm, including existing authored/saved depot layouts.
            if (MagnusArmVisuals.IsLegacyClaw(e.target.name)) { e.target.SetActive(false); continue; }
            bool want = e.Wanted(levelFor);
            if (e.target.activeSelf == want) continue;
            e.target.SetActive(want);
            if (want && celebrate && applied && Application.isPlaying)
            {
                var t = e.target.transform;
                Juice.PopIn(t, 0.35f);
                Vector3 p = SortingKit.RendererBounds(e.target).center;
                Fx.Burst(p, SortingKit.Gold, 24, 3.5f);
                Fx.Dust(p, 0.8f);
                Sfx.Play("pop", p, 0.9f, 0.9f);
                Sfx.Play("sparkle", p, 0.7f);
            }
        }
        applied = true;
    }

    /// <summary>Is this target shown right now? (Tests.)</summary>
    public bool IsShown(GameObject target) => target != null && target.activeSelf;
}
