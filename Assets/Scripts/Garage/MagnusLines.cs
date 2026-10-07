using UnityEngine;

/// <summary>
/// Magnus's chess trash talk by strength band (Docs/Progression.md §6.3 "Banter"). Lines are picked by a seed the host
/// passes in (deterministic, no UnityEngine.Random), then broadcast as a speech bubble.
/// </summary>
public static class ChessBanter
{
    public static readonly Color BubbleColor = new Color(0.55f, 0.95f, 1f);

    static readonly string[] Band0 = { "Horsey go jump.", "I have placed your pawn in RECYCLING. Wait. That was my queen.", "Bishop goes... diagonal? Diagonal-ish.", "Beep. I am thinking. Beep." };
    static readonly string[] Band1 = { "Your bishop is Junk category.", "I see a fork. I also see a spoon. Mostly the spoon.", "Check! Also your plastic bin is 80% full.", "Knight to... the good square." };
    static readonly string[] Band2 = { "Mate in 4. Please take out the trash in 3.", "I calculated 40,000 positions. 39,000 involved raccoons.", "Your king smells like Tuesday's bags.", "I have seen this opening. In a dumpster." };
    static readonly string[] Band3 = { "I have sorted every possible future. You lose in all of them. In one you also get rabies.", "Resistance is compostable.", "Your position is a wet cardboard box." };
    static readonly string[] Band4 = { "I AM THE COMPACTOR OF KINGS.", "Your pieces are already in the bale. You just don't know it yet.", "I have achieved Garbage Enlightenment." };

    static readonly string[] BlunderLines =
    {
        "Horsey go jump.", "Oops. I sorted that move into the wrong bin.", "That was... a sacrifice. Yes. On purpose.",
        "My claw slipped. It was the tongs.", "I meant to do that. (I did not mean to do that.)",
    };

    static readonly string[] LostLow = { "GG. I was sorting you a victory.", "You win! I let you. I think. Did I?", "Good game. I am going to go stare at a can." };
    static readonly string[] LostMid = { "Hmm. My brain module needs a firmware update.", "Well played. I blame the conveyor noise." };
    static readonly string[] LostPrime = { "...I am going to go sort something. Alone.", "Impossible. Recalculating. Recalculating. Sulking." };
    static readonly string[] WonLines = { "Checkmate! Please recycle your king responsibly.", "Victory! Into the KEEP crate it goes.", "GG. Your pieces are going in the compactor." };
    static readonly string[] DrawLines = { "A draw. Like two bags of equal stink.", "Nobody wins. Very zero-waste of us." };

    static string Pick(string[] lines, int seed) => lines[(int)((uint)seed % (uint)lines.Length)];

    static string[] Band(int tier) => tier <= 2 ? Band0 : tier <= 5 ? Band1 : tier <= 8 ? Band2 : tier <= 10 ? Band3 : Band4;

    /// <summary>Idle trash talk while playing a person.</summary>
    public static string Move(int tier, int seed) => Pick(Band(tier), seed);

    /// <summary>He just played a random legal move.</summary>
    public static string Blunder(int tier, int seed) => tier <= 2 ? Pick(Band0, seed) : Pick(BlunderLines, seed);

    public static string Lost(int tier, int seed) => tier >= 11 ? Pick(LostPrime, seed) : tier >= 6 ? Pick(LostMid, seed) : Pick(LostLow, seed);
    public static string Won(int tier, int seed) => tier >= 11 ? Pick(Band4, seed) : Pick(WonLines, seed);
    public static string Drew(int tier, int seed) => Pick(DrawLines, seed);
}

/// <summary>What Magnus says on the job at the sort station (picked deterministically by item / bag id).</summary>
public static class SorterLines
{
    static readonly string[] Oops =
    {
        "My bad. Knight to trash-6.", "Hmm. That one looked recyclable from the inside.", "I have made a sorting decision. It was wrong.",
        "Contamination? I prefer 'creative recycling'.", "Oops. My googly eyes drifted.",
    };

    static readonly string[] ShiftStart = { "I AM HELPING.", "Shift started. Please clap.", "Magnus is on the clock. Beep boop.", "Commute complete. Traffic was terrible. (There is no traffic.)" };
    static readonly string[] ShiftEnd = { "Clocking out. Chess time.", "Shift over. My claw is tired.", "Back to the board. The pawns missed me." };
    static readonly string[] Shooed = { "Fine. FINE. I'll go play chess.", "Okay boss. Your table now.", "Shooing acknowledged. Sulking initiated." };
    static readonly string[] Leftover = { "Magnus doesn't know what this is. It's your problem now.", "Not my department.", "This one is above my pay grade. I am not paid." };
    static readonly string[] TooHeavy = { "Too heavy. Magnus is a delicate boy.", "Nope. That weighs more than my feelings." };
    static readonly string[] Treasure = { "Ooh, shiny. KEEP crate.", "Treasure! I am a pirate now.", "For you, boss." };
    static readonly string[] Rip = { "RIP! (the bag, not me)", "Bag. Opened. Respectfully.", "SLAM. I learned that from you." };
    static readonly string[] Smash = { "Pressing the big red button. I love the big red button.", "SMASH. Was that allowed? It felt allowed." };
    static readonly string[] Floor = { "Floor is not a bin. Hup!", "Five-second rule. Back you go.", "Who dropped that? (Me. I dropped that.)" };

    static string Pick(string[] lines, string key) => lines[(int)((uint)PickupVisuals.StableHash(key ?? "") % (uint)lines.Length)];

    public static string Mistake(string key) => Pick(Oops, key);
    public static string Start(string key) => Pick(ShiftStart, key);
    public static string End(string key) => Pick(ShiftEnd, key);
    public static string Shoo(string key) => Pick(Shooed, key);
    public static string Leftovers(string key) => Pick(Leftover, key);
    public static string Heavy(string key) => Pick(TooHeavy, key);
    public static string Kept(string key) => Pick(Treasure, key);
    public static string Ripped(string key) => Pick(Rip, key);
    public static string Smashed(string key) => Pick(Smash, key);
    public static string FloorRescue(string key) => Pick(Floor, key);
}

/// <summary>
/// Deterministic rolls for garage / Magnus decisions (host-decided, same answer on every machine; no UnityEngine.Random:
/// Docs/Multiplayer.md §9 rule 2). FNV-1a (PickupVisuals.StableHash) plus a murmur3 finalizer, because FNV's low bits
/// barely change when only the last characters of the key do.
/// </summary>
public static class GarageRoll
{
    public static uint Hash(string key)
    {
        unchecked
        {
            uint h = (uint)PickupVisuals.StableHash(key ?? "");
            h ^= h >> 16; h *= 0x85ebca6b;
            h ^= h >> 13; h *= 0xc2b2ae35;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>0..1 from a key.</summary>
    public static float Value(string key) => (Hash(key) & 0xFFFFFF) / 16777215f;

    /// <summary>True with probability <paramref name="chance"/> (≥ 1 always, ≤ 0 never).</summary>
    public static bool Chance(string key, float chance) => chance >= 1f || (chance > 0f && Value(key) < chance);

    /// <summary>0..count-1 from a key.</summary>
    public static int Index(string key, int count) => count <= 1 ? 0 : (int)(Hash(key) % (uint)count);
}
