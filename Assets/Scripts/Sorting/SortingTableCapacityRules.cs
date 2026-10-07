/// <summary>Pure arithmetic for a sorting table's bag admission. A bag is all-or-nothing.</summary>
public static class SortingTableCapacityRules
{
    public static bool Fits(int capacity, int looseOnTop, int pendingContents, int newContents) =>
        capacity > 0 && looseOnTop >= 0 && pendingContents >= 0 && newContents >= 0 &&
        (long)looseOnTop + pendingContents + newContents <= capacity;
}
