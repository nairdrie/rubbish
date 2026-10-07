// Standalone deterministic probe against the ACTUAL WorkshopCatalog.cs and WorkshopState.cs.
// No Unity, editor, scenes, saves, networking or player files are accessed.
using System;
using System.Collections.Generic;

public enum ItemCategory { Junk, Plastic, Glass, Metal, Paper, Hazard, Valuable }
public class ItemDef { public string id; public ItemCategory category; }
public class ItemInstance {
    public string uid, defId; public float massKg, condition, value;
    public ItemDef Def => ItemCatalog.Get(defId);
}
public static class ItemCatalog {
    public static ItemDef Get(string id) => Enum.TryParse(id, out ItemCategory category) ? new ItemDef { id = id, category = category } : null;
}
static class WorkshopStateProbe
{
    static void Check(bool okay, string reason) { if (!okay) throw new Exception(reason); }
    public static void Main()
    {
        int checks = 0, completed = 0, refused = 0;
        foreach (var recipe in WorkshopCatalog.All) {
            var state = new WorkshopState(); Check(state.Start(recipe.id, out _), "start");
            for (int m = 0; m < 4; m++) {
                int grams = recipe.Cost((WorkshopMaterial)m);
                if (grams > 0) Check(state.Donate((WorkshopMaterial)m, grams, out _), "fund");
            }
            Check(state.Completed(recipe.id) && state.Valid(), "instant installation"); checks++;
        }
        var random = new Random(43604);
        for (int session = 0; session < 1000; session++) {
            var state = new WorkshopState(); var accepted = new long[4];
            for (int action = 0; action < 200; action++) {
                int operation = random.Next(5);
                if (operation <= 2) {
                    var material = (WorkshopMaterial)random.Next(4); int grams = random.Next(1, 100001);
                    if (state.Donate(material, grams, out _)) accepted[(int)material] += grams; else refused++;
                } else if (operation == 3) state.Start(WorkshopCatalog.All[random.Next(WorkshopCatalog.All.Count)].id, out _);
                else state.Cancel();
                Check(state.Valid(), "state invariant");
                for (int m = 0; m < 4; m++) {
                    long spent = 0; foreach (var recipe in WorkshopCatalog.All) if (state.Completed(recipe.id)) spent += recipe.Cost((WorkshopMaterial)m);
                    Check(accepted[m] == spent + state.bank.Get((WorkshopMaterial)m) + state.allocated.Get((WorkshopMaterial)m), "gram conservation");
                }
                checks++;
            }
            foreach (var recipe in WorkshopCatalog.All) if (state.Completed(recipe.id)) completed++;
        }
        foreach (ItemCategory category in Enum.GetValues(typeof(ItemCategory))) {
            var item = new ItemInstance { defId = category.ToString(), massKg = 1.234f };
            bool good = WorkshopCatalog.TryGrams(item, out _, out int grams);
            Check(good == (category == ItemCategory.Metal || category == ItemCategory.Plastic || category == ItemCategory.Glass || category == ItemCategory.Paper), "category protection");
            if (good) Check(grams == 1234, "rounding");
        }
        Console.WriteLine($"PASS: {checks} deterministic state checks across 1000 sessions / 200000 operations; {completed} projects completed; {refused} full-bank donations safely rejected; per-material gram conservation exact.");
    }
}
