using System;
using System.Collections.Generic;

/// <summary>Business-owned save data; all operations are deterministic and all capacities include reserved material.</summary>
[Serializable]
public sealed class WorkshopState
{
    public const int MaxBankGrams = 250000;
    public WorkshopMaterials bank, allocated;
    public string activeRecipe = "";
    public ushort completed;
    public int colorIndex;
    public List<WorkshopDisplayData> displays = new List<WorkshopDisplayData>();
    public bool Completed(string id) { var r = WorkshopCatalog.Get(id); return r != null && (completed & (1 << r.index)) != 0; }
    public float Progress => WorkshopCatalog.Get(activeRecipe) is WorkshopRecipe recipe ? (float)allocated.TotalGrams / recipe.cost.TotalGrams : 0f;
    public ItemInstance Display(string recipeId)
    { if (displays != null) foreach (var d in displays) if (d != null && d.recipeId == recipeId) return d.Preview(); return null; }
    public WorkshopState Clone()
    {
        var copy = new WorkshopState { bank = bank, allocated = allocated, activeRecipe = activeRecipe ?? "", completed = completed, colorIndex = colorIndex };
        if (displays != null) foreach (var d in displays) if (d != null) copy.displays.Add(d.Clone());
        return copy;
    }
    public bool Valid()
    {
        if ((completed & ~WorkshopCatalog.AllBits) != 0 || colorIndex < 0 || colorIndex >= WorkshopCatalog.ColorCount || displays == null || displays.Count > 4) return false;
        var recipe = WorkshopCatalog.Get(activeRecipe);
        if (!string.IsNullOrEmpty(activeRecipe) && (recipe == null || Completed(activeRecipe))) return false;
        for (int i = 0; i < 4; i++) {
            var m = (WorkshopMaterial)i; int b = bank.Get(m), a = allocated.Get(m);
            if (b < 0 || a < 0 || b > MaxBankGrams || a > MaxBankGrams - b || a > (recipe?.Cost(m) ?? 0)) return false;
        }
        if (recipe != null && allocated.TotalGrams >= recipe.cost.TotalGrams) return false; // finished work must already be installed
        var slots = new HashSet<string>(); var uids = new HashSet<string>();
        foreach (var d in displays) if (d == null || !d.Valid() || !Completed(d.recipeId) || !slots.Add(d.recipeId) || !uids.Add(d.uid)) return false;
        return true;
    }
    public bool Start(string id, out string why)
    {
        why = "";
        var recipe = WorkshopCatalog.Get(id);
        if (recipe == null) { why = "Unknown workshop project."; return false; }
        if (!string.IsNullOrEmpty(activeRecipe)) { why = "Finish or cancel your current project first."; return false; }
        if (Completed(id)) { why = "Already built!"; return false; }
        activeRecipe = id; allocated = default; Allocate(); return true;
    }
    public bool Cancel()
    {
        if (string.IsNullOrEmpty(activeRecipe)) return false;
        for (int i = 0; i < 4; i++) { var m = (WorkshopMaterial)i; bank.Set(m, bank.Get(m) + allocated.Get(m)); }
        allocated = default; activeRecipe = ""; return true;
    }
    public bool Donate(WorkshopMaterial material, int grams, out string why)
    {
        why = "";
        if ((int)material < 0 || (int)material > 3 || grams <= 0 || grams > MaxBankGrams) { why = "Not usable building material."; return false; }
        if (bank.Get(material) + allocated.Get(material) > MaxBankGrams - grams) { why = $"Your {material.ToString().ToLowerInvariant()} storage is full. Build something first."; return false; }
        bank.Set(material, bank.Get(material) + grams); Allocate(); return true;
    }
    void Allocate()
    {
        var recipe = WorkshopCatalog.Get(activeRecipe); if (recipe == null) return;
        for (int i = 0; i < 4; i++) {
            var m = (WorkshopMaterial)i; int take = Math.Min(bank.Get(m), recipe.Cost(m) - allocated.Get(m));
            bank.Set(m, bank.Get(m) - take); allocated.Set(m, allocated.Get(m) + take);
        }
        if (allocated.TotalGrams == recipe.cost.TotalGrams) { completed |= (ushort)(1 << recipe.index); activeRecipe = ""; allocated = default; }
    }
    public bool Pin(string recipeId, WorkshopDisplayData display)
    {
        if (!Completed(recipeId) || WorkshopCatalog.Get(recipeId)?.isDisplay != true || display != null && (!display.Valid() || display.recipeId != recipeId)) return false;
        displays.RemoveAll(d => d.recipeId == recipeId || display != null && d.uid == display.uid);
        if (display != null) displays.Add(display.Clone());
        return true;
    }
}

/// <summary>Visual-only reference. No value, sale data or inventory insertion path: selling/listing the original unpins it.</summary>
[Serializable]
public sealed class WorkshopDisplayData
{
    public string recipeId, uid, defId;
    public float massKg, condition;
    public bool Valid() => WorkshopCatalog.Get(recipeId)?.isDisplay == true && !string.IsNullOrEmpty(uid) && uid.Length <= 64 &&
        !string.IsNullOrEmpty(defId) && defId.Length <= 64 && ItemCatalog.Get(defId)?.category == ItemCategory.Valuable &&
        !float.IsNaN(massKg) && !float.IsInfinity(massKg) && massKg > 0f && massKg <= 100f &&
        !float.IsNaN(condition) && condition >= 0f && condition <= 1f;
    public ItemInstance Preview() => new ItemInstance { uid = uid, defId = defId, massKg = massKg, condition = condition };
    public WorkshopDisplayData Clone() => new WorkshopDisplayData { recipeId = recipeId, uid = uid, defId = defId, massKg = massKg, condition = condition };
    public static WorkshopDisplayData From(string recipeId, ItemInstance item) => item == null ? null : new WorkshopDisplayData
    { recipeId = recipeId, uid = item.uid, defId = item.defId, massKg = item.massKg, condition = item.condition };
}
