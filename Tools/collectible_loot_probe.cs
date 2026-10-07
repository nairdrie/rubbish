// Standalone audit harness: compile this with the actual ItemCatalog*.cs and BagTells.cs.
// Minimal engine/data adapters only; production RollBag, RNG, themes, weights and tell shaping run unchanged.
// This is outside Assets and never ships. Does not load Unity, scenes, player saves or an editor.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    public struct Color { public Color(float r, float g, float b) { } }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) { } }
    public static class Mathf
    {
        public static float Clamp01(float a) => Clamp(a, 0f, 1f);
        public static float Clamp(float a, float b, float c) => a < b ? b : a > c ? c : a;
        public static int Clamp(int a, int b, int c) => a < b ? b : a > c ? c : a;
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Round(float x) => (float)Math.Round(x);
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
    }
}
public enum ItemCategory { Junk, Plastic, Glass, Metal, Paper, Hazard, Valuable }
public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }
public class ItemDef
{
    public string id, name, flavor, visual;
    public ItemCategory category; public Rarity rarity;
    public float minMass, maxMass, minValue, maxValue;
    public UnityEngine.Color color;
}
public class ItemInstance
{
    public string uid, defId, foundAt, note;
    public float massKg, value, condition; public int dayFound;
    public ItemDef Def => ItemCatalog.Get(defId);
}
public class BagContents
{
    public string bagId, foundAt; public float bagMassKg = .15f; public int sackVariant;
    public List<ItemInstance> items = new List<ItemInstance>();
    public float TotalMassKg { get { float n = bagMassKg; foreach (var i in items) n += i.massKg; return n; } }
}
public class Carryable { public float MassKg; }
public class TrashBag : Carryable { public BagContents Contents; }
public class LootProfile
{
    public int tier, minItems, maxItems;
    public float p0, p1, valuableMult = 1f, springFlat, rareToEpic;
    public float[] rarity0, rarity1, categoryWeights, tierBoost;
}
public class DistrictDef { public LootProfile loot; }
public static class Districts { public static DistrictDef Starter = new DistrictDef { loot = Probe.Profile(1) }; }
public static class GameSession { static int uid; public static int Day = 1; public static string NewUid() => (++uid).ToString(); }
public static class Economy
{
    public const float HazardDisposalPay = 1, CompactorScrapPerKg = 1, MinRecyclePayout = 1;
    public static float RecycleRatePerKg(ItemCategory c) => 1;
    public static string Money(float x) => "$" + x;
}
public static class PickupVisuals
{
    public static int StableHash(string s)
    {
        unchecked { uint h = 2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return (int)h; }
    }
}
public static class Probe
{
    public static LootProfile Profile(int tier)
    {
        switch (tier)
        {
            case 2: return new LootProfile { tier = 2, p0=.006f, p1=.007f, minItems=6, maxItems=11, springFlat=.020f,
                rarity0=new[]{68f,25f,6f,1f},rarity1=new[]{58f,30f,10f,2f},tierBoost=new[]{1f,1f,5f,0f,0f},categoryWeights=new[]{34f,22f,16f,12f,11f,5f} };
            case 3: return new LootProfile { tier = 3, p0=.0068f, p1=.0068f, minItems=7, maxItems=12, springFlat=.025f,
                rarity0=new[]{52f,32f,13f,3f},rarity1=new[]{42f,35f,18f,5f},tierBoost=new[]{.6f,.6f,2f,8f,0f},categoryWeights=new[]{28f,22f,12f,14f,18f,6f} };
            case 4: return new LootProfile { tier = 4, p0=.0066f, p1=.0049f, minItems=8, maxItems=13, springFlat=.030f,
                rarity0=new[]{55f,30f,12f,3f},rarity1=new[]{47f,33f,15f,5f},tierBoost=new[]{.25f,.25f,.8f,3f,12f},categoryWeights=new[]{24f,20f,10f,16f,20f,10f} };
            default: return new LootProfile { tier = 1, p0=.005f, p1=.008f, minItems=5, maxItems=10, springFlat=.012f,
                rarity0=new[]{84f,14f,1.7f,.3f},rarity1=new[]{74f,20f,5f,1f},tierBoost=new[]{1f,1f,0f,0f,0f} };
        }
    }
    public static void Main(string[] args)
    {
        CheckInvariance();
        int loads = args.Length > 0 ? int.Parse(args[0]) : 4000;
        Console.WriteLine("Normal-loot variety by rarity: " + string.Join(" / ", Enumerable.Range(0,5).Select(r=>ItemCatalog.All.Count(d=>d.category==ItemCategory.Valuable && (int)d.rarity==r && ItemCatalog.TierOf(d)<=4 && !d.id.StartsWith("big_cleanup_")))));
        for (int tier=1; tier<=4; tier++)
        {
            var profile=Profile(tier);
            long[] counts=new long[5]; int two=0,zero=0,bags=0; double kg=0;
            for(int truck=0;truck<loads;truck++)
            {
                float load=0; int low=0;
                for(int attempt=0;attempt<100 && load<156f;attempt++)
                {
                    int seed=unchecked(17327 + truck*7919 + attempt*104729 + tier*10000019);
                    var bag=ItemCatalog.RollBag("probe/"+tier+"/"+truck+"/"+attempt,seed,.5f,"audit",profile,.6f);
                    if(load+bag.TotalMassKg>160f) continue;
                    load+=bag.TotalMassKg; bags++;
                    foreach(var item in bag.items)
                    {
                        var d=item.Def;
                        if(d.category!=ItemCategory.Valuable)continue;
                        counts[(int)d.rarity]++;
                        if(d.rarity<=Rarity.Uncommon) low++;
                    }
                }
                kg+=load;
                if(low>=2)two++; if(low==0)zero++;
            }
            Console.WriteLine($"tier={tier} trucks={loads} meanKg={kg/loads:F2} bags/load={bags/(double)loads:F2} C/U/R/E/L={string.Join("/",counts.Select(c=>(c/(double)loads).ToString("F3")))} low>=2={two*100.0/loads:F1}% low=0={zero*100.0/loads:F1}%");
        }
    }

    static void CheckInvariance()
    {
        var core = typeof(ItemCatalog).GetMethod("RollBagCore", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        int rarePlus=0, replacements=0;
        for(int tier=1;tier<=4;tier++) for(int seed=0;seed<1000;seed++)
        {
            var profile=Profile(tier);
            profile.valuableMult=seed%2==0?1f:4f;
            profile.rareToEpic=seed%7==0?.1f:0f;
            var call=new object[]{"invariance/"+tier+"/"+seed,seed*7919+17327,(seed%3)*.5f,"audit",profile,.6f,false};
            var before=(BagContents)core.Invoke(null,call);
            call[6]=true;
            var after=(BagContents)core.Invoke(null,call);
            if(before.TotalMassKg!=after.TotalMassKg || before.items.Count!=after.items.Count) throw new Exception("Mass/count changed");
            for(int i=0;i<before.items.Count;i++)
            {
                var a=before.items[i];var b=after.items[i];
                if(a.massKg!=b.massKg)throw new Exception("Slot weight changed");
                if(a.Def.category==ItemCategory.Valuable)
                {
                    if(a.defId!=b.defId || a.value!=b.value || a.condition!=b.condition)throw new Exception("Original treasure changed");
                    if(a.Def.rarity>=Rarity.Rare)rarePlus++;
                }
                else if(b.Def.category==ItemCategory.Valuable)
                {
                    replacements++;
                    if(b.Def.rarity>Rarity.Uncommon || b.massKg<b.Def.minMass || b.massKg>b.Def.maxMass)throw new Exception("Illegal extra find");
                }
            }
        }
        Console.WriteLine($"PASS: 4000 matched seeds across all tiers/wealth/rookie+gnome settings; exact unchanged mass/slots/original valuables; Rare+ checked={rarePlus}; legal low-tier replacements={replacements}.");
    }
}
