public static partial class GameSession
{
    static WorkshopState workshop = new WorkshopState();
    /// <summary>A copy: live edits must pass through the workshop's authoritative transaction.</summary>
    public static WorkshopState WorkshopSave
    {
        get {
            var copy = workshop.Clone();
            copy.displays.RemoveAll(pin => {
                var item = Workshop.OwnedItem(pin.uid); return item == null || item.defId != pin.defId;
            });
            return copy;
        }
    }
    internal static void SetWorkshopSave(WorkshopState state)
    {
        if (state == null || !state.Valid()) return;
        workshop = state.Clone();
        NotifyChanged(); SaveSoon();
    }
    static void LoadWorkshop(WorkshopState state) => workshop = state != null && state.Valid() ? state.Clone() : new WorkshopState();
}
