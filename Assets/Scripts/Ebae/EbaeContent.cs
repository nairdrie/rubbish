using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// All the dumb words on ebae: buyer names, offer messages, reviews, ticker lines and the
/// item-aware "special buyers" (definitelynotdave shows up for Dave's wedding ring).
/// Pure data + string picking; the economy logic lives in <see cref="EbaeMarket"/>.
/// Message placeholders: {0} = offer amount ("$220"), {1} = item name, {2} = where it was found.
/// </summary>
public static class EbaeContent
{
    /// <summary>A buyer who only turns up for certain items (matched by id/name tokens).</summary>
    public class SpecialBuyer
    {
        public string[] keywords;   // whole-word match against item id / name tokens (plural "s" ok)
        public bool searchLore;     // also substring-match note + flavor text (for "dave")
        public string buyer;
        public string message;
        public float valueMult;     // offers value × this (capped at asking)
    }

    public static readonly string[] Buyers =
    {
        "xX_TrashLord_Xx", "Grandma_Joan62", "Karen_from_HOA", "raccoon_enjoyer",
        "BinChicken99", "gary_from_accounting", "MrsWhiskers_Official", "dumpster_dan", "sk8rboi_2003",
        "LegitBuyer100percent", "the_real_santa", "crypto_bro_77", "OpossumQueen", "uncle_rick_deals",
        "not_a_raccoon", "Bargain_Barbara", "xXx_ShinyThings_xXx", "NanaBakes4U", "lil_landfill",
        "TheAntiquesGuy", "sir_haggles_a_lot", "brenda.from.church", "mystery_buyer_404", "seagull_steve",
        "moist_mike", "tax_evader_ted", "DadJokesDave_NotThatDave", "yard_sale_yolanda", "chad.thundercart",
    };

    /// <summary>Buyer pays the asking price (lowball listings get a lot of these).</summary>
    public static readonly string[] AtAsking =
    {
        "BUY IT NOW. i am not a patient man",
        "sold!! ship it to my mom's basement pls",
        "yes. yes. YES. full price",
        "full price because i'm a good person (lie)",
        "take my money. it's in a sock. the sock is also for you",
        "my therapist said to treat myself",
        "no haggle. i respect the hustle",
        "wow what a steal. literally, did u steal this",
        "instant buy. don't tell my wife",
        "paying asking. i have no self control",
    };

    /// <summary>Normal haggling. {0} = amount.</summary>
    public static readonly string[] Haggles =
    {
        "would u do {0}? i have 3 kids and a boat",
        "{0} final offer. my cat is watching me type this",
        "hey friend. {0}. no lowballs pls (this is a lowball)",
        "{0} and i'll leave u a nice review",
        "can do {0} cash, meet at the gas station at 3am",
        "is {0} ok? i can throw in a coupon for a free lasagna",
        "{0}?? pls reply fast i'm on my lunch break",
        "{0}. it has a scratch. i can smell the scratch",
        "offering {0}. is it still available. is it still available. hello",
        "{0}. i found the same thing in a ditch once but i lost it",
        "{0} and a firm handshake",
        "{0} but only if it's not haunted. is it haunted",
    };

    /// <summary>Greedy listings get roasted. {0} = amount.</summary>
    public static readonly string[] GreedyCallouts =
    {
        "lol. lmao even. {0}",
        "bro it's literally trash. {0}",
        "who hurt you. {0}",
        "i found this exact thing in MY trash. {0}",
        "{0} and not a penny more, you goblin",
        "sir this is a garbage website. {0}",
        "that price made my dog cry. {0}",
        "{0}. take it or leave it. (please take it)",
    };

    /// <summary>Tiny joke offers: fixed amount + message.</summary>
    public static readonly (float amount, string message)[] JokeOffers =
    {
        (3f, "will u take 3 dollars and a hug"),
        (1f, "i can offer $1 and my sincere respect"),
        (4.20f, "4.20 lol"),
        (2f, "trade for a slightly used sandwich? ok fine $2"),
        (0.5f, "50 cents and a compliment: nice garbage"),
        (5f, "$5 or i tell ur mom"),
        (7f, "i have $7 in quarters. they are sticky. don't ask"),
        (1.25f, "$1.25 and i'll name my firstborn after you"),
        (6f, "6 bucks and a coupon for 10% off a divorce lawyer"),
    };

    /// <summary>Second offer after you decline. {0} = amount.</summary>
    public static readonly string[] Counters =
    {
        "ok ok. {0}. my wife can't know",
        "fine. FINE. {0}",
        "{0}. this is my final offer. (it's not)",
        "you drive a hard bargain for a garbage person. {0}",
        "i sold my car for this. {0}",
        "i checked the couch cushions. {0}",
    };

    /// <summary>What a buyer says when you decline and they're going to come back.</summary>
    public static readonly string[] CounterTeasers =
    {
        "wait wait wait don't go",
        "hold on. checking my couch cushions...",
        "ok hang on let me ask my mom",
        "you'll be hearing from me",
    };

    /// <summary>What a buyer says when you decline and they're gone for good.</summary>
    public static readonly string[] DeclineReplies =
    {
        "ok :(",
        "you'll regret this",
        "wow. rude. blocked",
        "fine i'll go buy real stuff",
        "my lawyer will hear about this (i don't have a lawyer)",
        "cool cool cool cool cool",
    };

    public static readonly string[] JokeDeclineReplies =
    {
        "worth a shot",
        "ok but the hug offer stands",
        "lmao ok",
        "u can't blame a guy",
    };

    /// <summary>Feedback left after a sale.</summary>
    public static readonly string[] Reviews =
    {
        "A+++ smelled like garbage, would buy again",
        "item as described (disgusting)",
        "fast shipping. slow brain. 5 stars",
        "seller might be a raccoon? 10/10",
        "came with a free fly. bonus",
        "arrived damp. perfect",
        "i cried. good cries",
        "would dumpster dive with again",
        "my wife left me but this is nice",
        "exactly the garbage i was looking for",
        "5 stars. the smell is part of the charm",
    };

    public static readonly string[] JokeSaleReviews =
    {
        "LMAO they actually took it. A+++",
        "i offered a hug and got the item. best day of my life",
        "seller is either very kind or very dumb. 5 stars",
    };

    public const string PawnBuyer = "Pawn Pete's Instant Cash";
    public static readonly string[] PawnReviews =
    {
        "pleasure doing business. no refunds. no eye contact",
        "pete gave it a sniff and nodded",
        "cash in hand. dignity not included",
    };

    /// <summary>Fake "live" ticker lines + tips across the top of ebae.</summary>
    public static readonly string[] Ticker =
    {
        "someone in Ohio just bought a used toothbrush for $4",
        "Grandma_Joan62 is watching 14 items",
        "HOT RIGHT NOW: slightly damp couches",
        "raccoon_enjoyer left feedback: 'smelled great'",
        "BREAKING: local man sells his own trash back to himself",
        "ebae tip: greedy prices get fewer, lower offers. sad but true",
        "ebae tip: Pawn Pete pays instantly, but only 45%. he's got bills",
        "ebae tip: price it low and buyers swarm like seagulls",
        "ebae tip: offers expire in about a minute. buyers are flaky",
        "ebae tip: decline a lowball and they might come back higher",
        "xX_TrashLord_Xx just won an auction for 40 lbs of 'mystery wire'",
        "this just in: 'it's not junk, it's vintage' says man holding junk",
    };

    /// <summary>Item-aware buyers. First keyword hit wins; each buyer shows up once per listing.</summary>
    public static readonly SpecialBuyer[] Specials =
    {
        // Dave: matched on the note/flavor ("Forever yours - Dave" rings, Dave's love letters).
        new SpecialBuyer { keywords = new[] { "dave" }, searchLore = true, buyer = "definitelynotdave",
            message = "hey so about that {1}. no reason. i'll pay {0}. please don't tell my wife. or linda", valueMult = 1.25f },
        new SpecialBuyer { keywords = new[] { "ring", "wedding" }, buyer = "Karen_from_HOA",
            message = "is this the ring from {2}?? asking for a friend. {0}", valueMult = 0.9f },
        new SpecialBuyer { keywords = new[] { "doll", "puppet", "clown" }, buyer = "cursed_doll_collector",
            message = "i collect cursed dolls pls. does it whisper? {0}, extra if it whispers", valueMult = 1.15f },
        new SpecialBuyer { keywords = new[] { "watch", "clock", "rollex", "rolex" }, buyer = "gary_from_accounting",
            message = "is it waterproof? asking bc i will immediately drop it in the toilet. {0}", valueMult = 0.95f },
        new SpecialBuyer { keywords = new[] { "trophy", "medal", "award" }, buyer = "participation_andy",
            message = "finally i can pretend i won something. {0}", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "gnome" }, buyer = "gnome_liberation_front",
            message = "we have come to rescue our brother. {0}", valueMult = 1.2f },
        new SpecialBuyer { keywords = new[] { "coin", "pennies", "penny", "doubloon" }, buyer = "uncle_rick_deals",
            message = "i'll count them. i LOVE counting. {0}", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "dentures", "denture", "teeth" }, buyer = "Grandma_Joan62",
            message = "my husband lost his teeth in 1998 are these them. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "sock" }, buyer = "Grandma_Joan62",
            message = "that is MY sock. i'll buy it back. don't tell my grandson. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "painting", "portrait", "art", "canvas" }, buyer = "TheAntiquesGuy",
            message = "is that an original? don't answer. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "pigcasso", "pig" }, buyer = "wilbur_the_pig",
            message = "oink. (i painted that. i want it back.) {0}", valueMult = 1.2f },
        new SpecialBuyer { keywords = new[] { "card", "charizord", "holo" }, buyer = "lil_landfill",
            message = "is it holo. please say it's holo. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "comic", "raccoon" }, buyer = "raccoon_enjoyer",
            message = "HE'S ME. i am him. {0}", valueMult = 1.15f },
        new SpecialBuyer { keywords = new[] { "gameboi", "console", "cartridge", "tamagochu", "controller" }, buyer = "lil_landfill",
            message = "does it have save files on it. i NEED to know. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "tamagochu" }, buyer = "not_a_raccoon",
            message = "i will keep it alive this time. i promise. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "phone", "smartphone", "laptop" }, buyer = "crypto_bro_77",
            message = "does it still have my seed phrase on it. unrelated. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "urn", "ashes" }, buyer = "ghost_of_ebae_past",
            message = "that's me. in the urn. please send me home. {0}", valueMult = 1.2f },
        new SpecialBuyer { keywords = new[] { "necklace", "diamond", "gold", "chain", "jewel", "jewelry", "pearl", "earring", "bracelet", "tiara", "crown" },
            buyer = "xXx_ShinyThings_xXx", message = "SHINY. mine. MINE. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "earring" }, buyer = "one_earring_eddie",
            message = "i have the other one!!! we can be complete. {0}", valueMult = 1.3f },
        new SpecialBuyer { keywords = new[] { "vinyl", "record", "turntable" }, buyer = "dad_with_a_turntable",
            message = "the crackle is part of the experience. {0}", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "mixtape", "cassette" }, buyer = "sk8rboi_2003",
            message = "is it the one i made for jessica in 2003. she never listened to it. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "letter", "letters", "diary", "journal" }, buyer = "brenda.from.church",
            message = "is my name in it. asking for no reason. {0}", valueMult = 0.95f },
        new SpecialBuyer { keywords = new[] { "lottery", "ticket", "scratcher" }, buyer = "uncle_rick_deals",
            message = "is it a winner? i'll take the risk. i always take the risk. {0}", valueMult = 0.9f },
        new SpecialBuyer { keywords = new[] { "figurine", "statue", "bust", "candlestick", "egg", "faberge" }, buyer = "TheAntiquesGuy",
            message = "ah yes. a classic. from the... era. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "candlestick" }, buyer = "colonel_mustard",
            message = "i was in the library the whole time. unrelated: {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "cat", "kitten" }, buyer = "MrsWhiskers_Official",
            message = "i am a cat. i want it. i have money. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "camera", "instapix", "polaroid", "camcorder" }, buyer = "moist_mike",
            message = "are there photos on it. of what. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "moon" }, buyer = "definitely_an_astronaut",
            message = "i dropped that in 1969. finally. {0}", valueMult = 1.15f },
        new SpecialBuyer { keywords = new[] { "lava", "lamp" }, buyer = "hippie_harold",
            message = "far out. {0}. do u accept good vibes as partial payment", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "bowling" }, buyer = "strike_king_1987",
            message = "i will name it gerald. {0}", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "fondue" }, buyer = "Grandma_Joan62",
            message = "i had the exact same one. it tried to kill my husband. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "drill" }, buyer = "uncle_rick_deals",
            message = "does it come with the battery. no? {0} then. final. ok maybe not final", valueMult = 0.95f },
        new SpecialBuyer { keywords = new[] { "globe" }, buyer = "brenda.from.church",
            message = "i collect tiny towns trapped in eternal blizzards. {0}", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "baseball", "bat", "glove" }, buyer = "sports_dad_92",
            message = "is it signed by a real guy. like a real real guy. {0}", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "guitar", "ukulele", "banjo", "violin" }, buyer = "sk8rboi_2003",
            message = "does it know wonderwall. {0}", valueMult = 1.05f },
        new SpecialBuyer { keywords = new[] { "sword", "katana", "knife", "axe" }, buyer = "LegitBuyer100percent",
            message = "for cutting bread. normal bread. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "toaster", "microwave", "blender" }, buyer = "haunted_appliance_guy",
            message = "does it toast. or does it TOAST. {0}", valueMult = 1.0f },
        new SpecialBuyer { keywords = new[] { "sneaker", "sneakers", "shoe", "boot", "boots" }, buyer = "sneakerhead_steve",
            message = "do they still have the original stink? that's where the value is. {0}", valueMult = 1.1f },
        new SpecialBuyer { keywords = new[] { "duck", "ducky" }, buyer = "raccoon_enjoyer",
            message = "quack quack (that means {0})", valueMult = 1.0f },
    };

    // ---------- picking ----------

    public static string Pick(string[] pool) =>
        pool == null || pool.Length == 0 ? "" : pool[UnityEngine.Random.Range(0, pool.Length)];

    /// <summary>Formats a message template. Never throws on a bad template.</summary>
    public static string Format(string template, float amount, string itemName, string foundAt)
    {
        if (string.IsNullOrEmpty(template)) return "";
        try { return string.Format(template, Economy.Money(amount), itemName ?? "it", string.IsNullOrEmpty(foundAt) ? "down the street" : foundAt); }
        catch (FormatException) { return template; }
    }

    /// <summary>Special buyers whose keywords match this item, excluding ones already used.</summary>
    public static List<SpecialBuyer> MatchingSpecials(ItemInstance item, ICollection<string> alreadyUsed)
    {
        var result = new List<SpecialBuyer>();
        if (item == null) return result;
        var def = item.Def;
        var tokens = new HashSet<string>();
        AddTokens(tokens, item.defId);
        if (def != null) AddTokens(tokens, def.name);
        string lore = ((item.note ?? "") + " " + (def != null ? def.flavor : "")).ToLowerInvariant();

        foreach (var s in Specials)
        {
            if (alreadyUsed != null && alreadyUsed.Contains(s.buyer)) continue;
            bool hit = false;
            foreach (var k in s.keywords)
            {
                if (tokens.Contains(k) || tokens.Contains(k + "s") || tokens.Contains(k + "es")) { hit = true; break; }
                if (s.searchLore && lore.Contains(k)) { hit = true; break; }
            }
            if (hit) result.Add(s);
        }
        return result;
    }

    static void AddTokens(HashSet<string> set, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var parts = text.ToLowerInvariant().Split(new[] { ' ', '_', '-', '\'', '"', '#', '.', ',', '!', '?', ':', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts) set.Add(p);
    }
}
