using UnityEngine;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.1 voice, §7.2, §8): what the neighbours say. Floating-text lines plus an
/// Animal-Crossing mumble (<see cref="WorldLifeIds.Sfx.Mumbles"/>, pitched per resident). Lines are picked by the cosmetic
/// dice and rate-limited by <see cref="ResidentDirector.Say"/> (one per NPC per 6 s, 3 on screen at once).
/// Package G tuned these: more takes per beat, district voices for the angry, driver and car-owner lines (indexed by
/// district byte 0 Mildly Pleasant .. 3 Bluffs), and the grudge glare. Everything stays cartoon: nobody is hurt.
/// </summary>
public static class ResidentLines
{
    public static readonly Color Hey = new Color(1f, 0.85f, 0.35f);
    public static readonly Color Yell = new Color(1f, 0.42f, 0.32f);
    public static readonly Color Happy = new Color(0.55f, 1f, 0.55f);
    public static readonly Color Chat = new Color(0.92f, 0.95f, 1f);
    public static readonly Color Sign = new Color(0.12f, 0.1f, 0.1f);

    // ---- the flop (§8.1)
    public static readonly string[] Airborne = { "WHOAAA!", "AAAAAH!", "WAAAH!", "NOT AGAIN!", "WHEEEE—", "MY COUPONS!", "I CAN SEE MY HOUSE!" };
    public static readonly string[] Oof = { "OOF!", "OOF!", "OOMPH!", "*thud*", "HURK!" };
    public static readonly string[] Lie = { "MY HIP!", "I'M OKAY!", "...ow.", "I saw my whole lawn flash", "mommy?", "I'M FINE!", "Is it Tuesday?", "Five more minutes..." };
    public static readonly string[] GiveUp = { "AND STAY OUT!", "...and STAY out!", "HMPH!", "I'm writing a LETTER!", "...my knees.", "NEXT TIME, BUSTER!" };
    static readonly string[][] Angry =
    {
        new[] { "I PAY MY TAXES!", "I'M CALLING THE CITY!", "MY COFFEE!!", "UNBELIEVABLE!", "THAT WAS MY GOOD ROBE!", "I'M TELLING GARY!", "NOT ON MY STREET!" },
        new[] { "THIS IS GOING IN THE NEWSLETTER", "I WANT YOUR MANAGER!", "THE HOA WILL HEAR ABOUT THIS", "I HAVE A MINIVAN AND I'M NOT AFRAID TO USE IT", "I'M POSTING THIS ON THE NEIGHBOURHOOD APP!", "THAT'S A BYLAW VIOLATION!" },
        new[] { "Do you know who I AM?!", "My solicitor will hear of this!", "How DARE you!", "Most UNCOUTH!", "Ghastly! Simply GHASTLY!", "I'll have you UNSUBSCRIBED!" },
        new[] { "I'll buy your company and FIRE you!", "Do you know what this robe COST?!", "SECURITY!", "My lawyers have lawyers!", "Do you know what my LAWN costs?!", "I'll put it on YOUR tab!" },
    };
    public static string[] AngryFor(byte district) => Angry[Mathf.Min(district, (byte)3)];

    // ---- nudges, horns, bumps (§8.1, §8.4, §8.5)
    public static readonly string[] Nudge = { "HEY! WATCH IT!", "Excuse me?!", "I'm WALKING here!", "Rude!", "Personal SPACE!" };
    public static readonly string[] Startle = { "AAH!", "EEK!", "GOOD GRIEF!", "YIPE!", "MY HEART!", "GADZOOKS!" };
    public static readonly string[] Stumble = { "HEY!", "Excuse YOU!", "Whoa there!", "Easy, tiger!" };
    public static readonly string[] Dodge = { "Ha! Missed me!", "NOT TODAY!", "Too slow!", "Ole!" };

    // ---- walk-outs (§7.2)
    public static readonly string[] Morning = { "Trash day!", "Morning!", "Phew, heavy one.", "*hums*", "Out you go.", "Bye bye, bag.", "Smells like Tuesday." };
    public static readonly string[] Wave = { "Yoo-hoo!", "Hi, trash man!", "Morning!", "Thanks, dear!", "There he is!", "Our hero!" };
    public static readonly string[] Glare = { "Disgraceful.", "Still here, huh?", "*sniff* ugh.", "Two DAYS." };
    /// <summary>A grudged household glaring at the truck (§9.1): after their walk-out, and when a truck passes.</summary>
    public static readonly string[] Grudge = { "Hmph.", "Oh. It's YOU.", "We remember.", "*glares*", "Don't you DARE wave.", "Still sore, thanks." };
    public static readonly string[] Thanks = { "Oh! Thanks, dear!", "What service!", "Oh! You're a gem!", "Well, aren't YOU keen!", "Saved me a trip!", "What a sweetie!" };
    public static readonly string[] Shrug = { "Oh. Okay then.", "...huh.", "Guess someone wanted it.", "Well, I never." };
    public static readonly string[] Raccoon = { "RACCOON!", "RACCOON!!", "NOPE! RACCOON!", "NOT TONIGHT, BANDIT!" };
    public static readonly string[] NightBag = { "*yawn*", "brr...", "Shoo, raccoons.", "Nobody saw me in these." };

    // ---- neglected piles (§7.2 witness)
    public static readonly string[] SignText = { "IT'S BEEN\n2 DAYS", "SMELL\nTHAT?", "HOA\nNOTIFIED", "WE PAY\nYOU!", "HELLO??", "STILL\nWAITING", "HONK IF\nYOU STINK" };
    public static readonly string[] Finally = { "FINALLY!", "FINALLY!!", "About TIME.", "Was that so hard?", "Hallelujah!" };
    public static readonly string[] Tapping = { "*tap tap tap*", "Any day now...", "Hmph.", "Tick tock." };
    public static readonly string[] Goblin = { "EEK! A GOBLIN!", "NOT THE GOBLIN!", "IT'S IN THE PILE!" };

    // ---- dogs (§8.2)
    public static readonly string[] Woof = { "WOOF!", "BORK!", "ARF ARF!", "YIP!" };
    public static string NearlyHit(string dogName) => "YOU NEARLY HIT " + (string.IsNullOrEmpty(dogName) ? "MY DOG" : dogName.ToUpperInvariant()) + "!";

    // ---- butlers (§3.5)
    public static readonly string[] Butler = { "Your refuse, sir.", "Voilà.", "As requested.", "Ahem.", "Today's selection, sir." };
    public static readonly string[] ButlerHit = { "Most irregular!", "I shall inform the Master.", "Good HEAVENS!", "My cravat!" };

    // ---- cars (§8.3): the shunted car's driver gets out / the parked car's owner storms out of the front door
    static readonly string[][] Driver =
    {
        new[] { "MY CAR!", "I JUST GOT IT DETAILED!", "MY INSURANCE!!", "Oh, COME ON!", "That's my COMMUTE!" },
        new[] { "MY MINIVAN!", "That's a LEASE!", "THE KIDS' JUICE BOXES!", "My dashcam got ALL of that!" },
        new[] { "My TOWN CAR!", "That is Italian leather!", "Do you know what that WAS?!", "Unacceptable!" },
        new[] { "MY LIMO!", "That was the GOOD limo!", "Driver! Get his NAME!", "My stock portfolio felt that!" },
    };
    static readonly string[][] Owner =
    {
        new[] { "HEY!! THAT'S MY CAR!", "MY CAR!!", "I SAW THAT!", "Not the paint job!" },
        new[] { "MY MINIVAN!!", "IN MY OWN DRIVEWAY?!", "I SAW THAT!", "THAT'S A LEASE!" },
        new[] { "In my OWN driveway?!", "Not the town car!", "I saw that, you scamp!" },
        new[] { "THE LIMO?!", "SECURITY!!", "That's my third-favourite limo!" },
    };
    public static string[] DriverFor(byte district) => Driver[Mathf.Min(district, (byte)3)];
    public static string[] OwnerFor(byte district) => Owner[Mathf.Min(district, (byte)3)];

    /// <summary>A car stuck behind the truck (§7.3), after 3 s.</summary>
    public static readonly string[] Honk = { "HONK HONK! GARBAGE DAY, HUH?!", "MOVE IT, TRASH MAN!", "SOME OF US HAVE JOBS!", "BEEEEP!", "I'M LATE FOR PILATES!", "TAKE YOUR TIME, I GUESS!" };
}
