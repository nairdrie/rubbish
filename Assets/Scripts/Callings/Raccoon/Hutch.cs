using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Critter Hutch at runtime (Raccoon calling): every machine reads rosters from the replicated workshop state, the
/// local player asks through <see cref="NightNet.RaccoonAsk"/>, and the host alone changes a roster (<see cref="HostEdit"/>).
/// Rules live in <see cref="HutchRules"/>; this class only connects them to businesses, the Ledger and the clock.
/// </summary>
public static class Hutch
{
    public const byte OpPair = 1, OpUnpair = 2, OpJob = 3, OpRename = 4, OpRelease = 5, OpRetire = 6;

    struct Cached { public string raw; public HutchState state; public float at; }
    static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.Ordinal);
    static readonly HutchState empty = new HutchState();
    static bool dirty = true;
    static float nextDayCheck;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { cache.Clear(); dirty = true; nextDayCheck = 0f; }

    // Workshop clears its handlers at SubsystemRegistration, so hook after that.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Hook() { Workshop.Changed -= MarkDirty; Workshop.Changed += MarkDirty; }
    static void MarkDirty() => dirty = true;

    // ------------------------------------------------------------------ reading (every machine)

    /// <summary>The business a client owns (solo and offline: this machine's own).</summary>
    public static bool TryBusinessOf(ulong client, out BusinessInfo business)
    {
        if (Businesses.TryGetForClient(client, out business)) return true;
        if (Businesses.Registry == null && client == Permissions.LocalClientId) { business = Businesses.Local; return business.IsValid; }
        business = default;
        return false;
    }

    /// <summary>A business's roster. A shared READ-ONLY instance (never change it; the host edits through
    /// <see cref="HostEdit"/>). Never null: an unknown business has an empty hutch.</summary>
    public static HutchState For(string businessId)
    {
        if (string.IsNullOrEmpty(businessId)) return empty;
        var registry = Businesses.Registry;
        cache.TryGetValue(businessId, out var entry);
        string raw;
        bool reread = false;
        if (registry != null) raw = registry.HutchRaw(businessId);
        else if (businessId != GameSession.BusinessId) raw = null;
        else if (!dirty && entry.state != null && Time.unscaledTime - entry.at < 1f) raw = entry.raw;
        else { raw = GameSession.WorkshopSave.hutch ?? ""; dirty = false; reread = true; } // no session: the save itself, at most once a second
        if (raw == null) return empty;
        if (entry.state != null && (ReferenceEquals(raw, entry.raw) || raw == entry.raw))
        {
            if (reread) { entry.at = Time.unscaledTime; cache[businessId] = entry; }
            return entry.state;
        }
        entry = new Cached { raw = raw, state = HutchState.Parse(raw) ?? new HutchState(), at = Time.unscaledTime };
        cache[businessId] = entry;
        return entry.state;
    }

    public static HutchState ForClient(ulong client) => TryBusinessOf(client, out var business) ? For(business.Id) : empty;

    public static string LocalBusinessId { get { var b = Businesses.Local; return b.IsValid ? b.Id : GameSession.BusinessId; } }
    /// <summary>This machine's own roster (read-only).</summary>
    public static HutchState Local => For(LocalBusinessId);

    /// <summary>A calling level of the business a client owns, readable on every machine.</summary>
    public static int LevelFor(ulong client, string nodeId) => Workshop.CallingLevelForClient(client, nodeId);
    public static int LocalLevel(string nodeId) => GameSession.GetUpgradeLevel(nodeId);
    public static bool LocalHasRoom => HutchRules.HasRoom(Local, LocalLevel(CallingIds.CritterHutch));

    // ------------------------------------------------------------------ asking (the local player)

    public static void RequestPair(int a, int b) => NightNet.RaccoonAsk(OpPair, a, b);
    public static void RequestUnpair(int id) => NightNet.RaccoonAsk(OpUnpair, id, 0);
    public static void RequestJob(int id, HutchJob job) => NightNet.RaccoonAsk(OpJob, id, (int)job);
    public static void RequestRename(int id, string name) => NightNet.RaccoonAsk(OpRename, id, 0, HutchRules.CleanName(name));
    public static void RequestRelease(int id) => NightNet.RaccoonAsk(OpRelease, id, 0);
    /// <summary>Retire a raccoon to the shop: it leaves the roster and becomes a "Pet Raccoon" in the keep crate.</summary>
    public static void RequestRetire(int id) => NightNet.RaccoonAsk(OpRetire, id, 0);

    // ------------------------------------------------------------------ deciding (host / solo)

    /// <summary>Host (or solo): change a business's roster and publish it. False when nothing changed, and always false on
    /// a connected guest. Callers validate first: this is the write, not the permission check.</summary>
    public static bool HostEdit(string businessId, Func<HutchState, bool> edit)
    {
        if (GameSession.IsConnectedGuest || string.IsNullOrEmpty(businessId) || edit == null) return false;
        var registry = Businesses.Registry;
        if (registry != null) return registry.HutchEdit(businessId, edit);
        if (businessId != GameSession.BusinessId) return false;
        var state = GameSession.WorkshopSave;
        var hutch = HutchState.Parse(state.hutch) ?? new HutchState();
        if (!edit(hutch) || !hutch.Valid()) return false;
        string encoded = hutch.Encode();
        if (encoded.Length > HutchState.MaxChars) return false;
        state.hutch = encoded;
        if (!state.Valid()) return false;
        GameSession.SetWorkshopSave(state);
        dirty = true;
        Workshop.RaiseChanged();
        return true;
    }

    /// <summary>Host: a hutch command from <paramref name="client"/> about their OWN roster, standing at their hutch.</summary>
    internal static void HostCommand(ulong client, byte op, int a, int b, string text)
    {
        if (!TryBusinessOf(client, out var business)) return;
        int hutchLevel = LevelFor(client, CallingIds.CritterHutch);
        if (hutchLevel <= 0) { NightNet.RaccoonTell(client, false, "Build the Critter Hutch first."); return; }
        if (!HutchWorld.IsNear(client, business.lot)) { NightNet.RaccoonTell(client, false, "Visit your hutch to do that."); return; }
        string why = "", petName = null; bool petRabid = false;
        bool ok = HostEdit(business.Id, h =>
        {
            switch (op)
            {
                case OpPair: return HutchRules.Pair(h, a, b, LevelFor(client, CallingIds.BreedingBox) > 0, out why);
                case OpUnpair: return HutchRules.Unpair(h, a, out why);
                case OpJob:
                    if (b < 0 || b > 2) return false;
                    return HutchRules.SetJob(h, a, (HutchJob)b, LevelFor(client, CallingIds.CollarChip), LevelFor(client, CallingIds.PackLeader), out why);
                case OpRename: return HutchRules.Rename(h, a, text, out why);
                case OpRelease: return HutchRules.Release(h, a, out why);
                case OpRetire:
                    var r = h.Find(a);
                    if (r == null || r.IsKit) { why = r == null ? "That raccoon isn't in your hutch." : "Kits stay with the family."; return false; }
                    petName = r.name; petRabid = r.rabid;
                    if (!HutchRules.Release(h, a, out why)) return false;
                    why = $"{petName} retired to the shop. Find a shelf for the little legend."; return true;
                default: why = "Unknown hutch command."; return false;
            }
        });
        if (ok && petName != null)
        {
            string uid = $"pet-{GameClock.Day}-h{a}-{(long)(GameClock.TotalHours * 1000) % 100000}";
            NightNet.HostGiveItem(client, RaccoonTrap.PetDefId, uid, petRabid ? 180f : 140f, $"{petName}. Retired from the hutch.");
        }
        if (!string.IsNullOrEmpty(why)) NightNet.RaccoonTell(client, ok, why);
        NightAudit.Event("hutch", new Dictionary<string, object> { { "op", (int)op }, { "by", (long)client }, { "ok", ok }, { "a", a }, { "b", b } });
    }

    /// <summary>Host: the raccoon in a sprung trap goes to <paramref name="client"/>'s hutch (traits rolled on the host's
    /// dice; a rabid catch stays rabid). False (and a toast for them) when they have no hutch or no room.</summary>
    public static bool HostSendFromTrap(ulong client, bool rabid, Vector3 trapPos)
    {
        if (!TryBusinessOf(client, out var business)) { NightNet.RaccoonTell(client, false, "You need a business of your own to keep raccoons."); return false; }
        int level = LevelFor(client, CallingIds.CritterHutch);
        if (level <= 0) { NightNet.RaccoonTell(client, false, "You need a Critter Hutch (print one in " + SolidWaste.Name + ") to keep raccoons."); return false; }
        HutchRaccoon admitted = null;
        bool ok = HostEdit(business.Id, h =>
        {
            if (h.Count == 0) h.day = GameClock.Day; // a hutch's days start with its first resident
            admitted = HutchRules.AdmitWild(h, level, rabid, NightDice.Host.Range);
            return admitted != null;
        });
        if (!ok || admitted == null)
        {
            NightNet.RaccoonTell(client, false, $"Your hutch is full ({HutchRules.Capacity(level)}). Release one, or build the Critter Condo.");
            return false;
        }
        NightNet.Say(trapPos + Vector3.up * 1.4f, $"{admitted.name} is off to the hutch!", 4);
        NightNet.RaccoonTell(client, true, $"{admitted.name} moved into your hutch{(rabid ? " (foamy)" : "")}. Zoom {admitted.speed}, Carry {admitted.carry}, Greed {admitted.greed}, Nerve {admitted.nerve}.");
        NightAudit.Event("hutch", new Dictionary<string, object> { { "op", "trap" }, { "by", (long)client }, { "id", admitted.id }, { "rabid", rabid } });
        return true;
    }

    /// <summary>
    /// Host: the daily tick of every hutch in town. Keyed by the roster's own saved day (not by this session's clock), so it
    /// survives a session restart: when the roster's day differs from today, the missed days run (at most three; a roster
    /// arriving from another Town's calendar runs one), food is charged to the OWNING business and the owner hears the news.
    /// </summary>
    internal static void HostDayTick()
    {
        if (GameSession.IsGuest || Time.unscaledTime < nextDayCheck) return;
        nextDayCheck = Time.unscaledTime + 2f;
        int today = GameClock.Day;
        foreach (var business in Businesses.All)
        {
            if (!business.IsValid) continue;
            var seen = For(business.Id);
            if (seen.Count == 0 || seen.day == today) continue;
            int level = LevelFor(business.ownerClientId, CallingIds.CritterHutch);
            int brain = LevelFor(business.ownerClientId, CallingIds.BrainChip);
            var news = new List<string>();
            int foodCents = 0;
            bool ok = HostEdit(business.Id, h =>
            {
                if (h.day == today) return false;
                int days = h.day > 0 && today > h.day && today - h.day <= 3 ? today - h.day : 1;
                for (int i = 0; i < days; i++)
                {
                    foodCents += HutchRules.FoodCents(h, brain);
                    HutchRules.AdvanceDay(h, Mathf.Max(1, level), NightDice.Host.Range, news);
                }
                h.day = today;
                return true;
            });
            if (!ok) continue;
            if (foodCents > 0)
                using (Ledger.ToBusiness(business.Id))
                    Ledger.Charge(foodCents / 100f, "Hutch kibble", RaccoonTuning.LedgerFood);
            string text = $"Hutch: fed everyone ({Economy.Money(foodCents / 100f)} of kibble).";
            if (news.Count > 0) text += " " + string.Join(" ", news);
            NightNet.RaccoonTell(business.ownerClientId, true, text);
            NightAudit.Event("hutch", new Dictionary<string, object> { { "op", "day" }, { "business", business.Id }, { "food", foodCents }, { "news", news.Count } });
        }
    }
}

/// <summary>Where the Raccoon calling's requests and cosmetic broadcasts land (see <see cref="NightNet.RaccoonAsk"/>).</summary>
public static class RaccoonCalling
{
    public const byte OpPounce = 10;
    public const byte FxFetch = 1, FxPounce = 2, FxKingLine = 3;

    /// <summary>Host: a request from <paramref name="sender"/> (already rate-limited). Every op validates for itself.</summary>
    internal static void HostAsk(ulong sender, byte op, int a, int b, string text)
    {
        if (GameSession.IsConnectedGuest) return;
        if (op == OpPounce) FeralStatus.HostPounce(sender);
        else Hutch.HostCommand(sender, op, a, b, text);
    }

    /// <summary>Every machine: play a cosmetic moment.</summary>
    internal static void PlayFx(byte kind, ulong who, Vector3 a, Vector3 b, string text)
    {
        switch (kind)
        {
            case FxFetch: PackCompanions.PlayFetch(who, a, b); break;
            case FxPounce: FeralStatus.PlayPounce(who, a); break;
            case FxKingLine: KingsRival.PlayLine(a, text); break;
        }
    }
}
