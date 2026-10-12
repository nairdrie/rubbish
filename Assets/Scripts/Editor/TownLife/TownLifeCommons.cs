using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Town Life for Coupon Commons, the commercial district (Docs/CityAuthoring.md, "Town Life layer"). The base builder
/// leaves it as shop boxes on bare grass; this dresses it as a working high street:
/// <list type="bullet">
/// <item>street trees in grates, planters, benches, bins, parking meters, hydrants and news boxes along the six streets,
/// with parking bays and zebra crossings painted on the asphalt and bunting across Coupon Street;</item>
/// <item>a forecourt for each of the nine shops that take collections: paving, planters, a pylon sign and a roof sign
/// with the shop's own name, cart corrals and vending machines (shops) or cafe tables (restaurants), and the
/// <b>dumpster pad</b> the runtime dumpster stands on (<see cref="DumpsterRules.PadLocal"/>), screened by a wall and
/// kept clear in front so a rear-loader can back up to it;</item>
/// <item>two customer car parks, a food court inside the Restaurant Lane loop, and a freight yard inside the Loading
/// Lane loop;</item>
/// <item>back-door clutter and shopfront displays for the decorative shop rows (no colliders: their footprints veto them).</item>
/// </list>
/// It keeps its own occupancy map: the shared one stops at the river. Everything is deterministic from stable ids.
/// </summary>
public static class TownLifeCommons
{
    public const string Name = "commons";
    public const string District = "coupon_commons";

    public static void Build(TownLifeContext ctx)
    {
        if (!ctx.options.District(District)) return;
        new Job(ctx).Run();
    }

    static readonly string[] MainRoads = { "commercial-south", "commercial-north", "commercial-west", "commercial-east", "shopping", "market" };
    static readonly string[] AllRoads =
    {
        "commercial-south", "commercial-north", "commercial-west", "commercial-east", "shopping", "market", "restaurant-lane",
        "loading-lane", "west-bridge", "town-bridge", "district-link", "hills-west", "hills-east",
    };

    static readonly Dictionary<string, string> ShopNames = new Dictionary<string, string>
    {
        { "cc-market-1", "BIG LOU'S BULK BARN" }, { "cc-market-2", "COUPON CLIPPER" }, { "cc-market-3", "DOLLAR-ISH" },
        { "cc-high-1", "THE SOCK OUTLET" }, { "cc-high-2", "PRETTY OKAY PHONES" }, { "cc-dining-1", "SOGGY NOODLE" },
        { "cc-dining-2", "LAST BITE DINER" }, { "cc-loading-1", "BOX BARN WHOLESALE" }, { "cc-loading-2", "MATTRESS MAYBE" },
    };

    sealed class Job
    {
        readonly TownLifeContext ctx;
        readonly TownLifeOccupancy occ;
        Transform root;
        int trees, furniture, signs, clutter, cars;
        readonly List<string> skipped = new List<string>();

        public Job(TownLifeContext ctx)
        {
            this.ctx = ctx;
            occ = new TownLifeOccupancy(new Rect(52f, 338f, 346f, 224f));
        }

        Vector3 G(Vector3 p) { p.y = ctx.scene.GroundY(p); return p; }
        static Vector3 At(Vector3 p, float y) { p.y = y; return p; }
        static float R(System.Random rng, float a, float b) => TownLifeContext.Range(rng, a, b);

        public void Run()
        {
            root = ctx.Group("Commons");
            Palette();
            Props();
            SeedKeepOuts();
            var stops = Stops();
            foreach (var stop in stops) ReserveLot(stop);
            foreach (var stop in stops) Forecourt(stop);
            CarPark("west", new Rect(147f, 424.4f, 46f, 12.4f), 420.2f);
            CarPark("east", new Rect(233f, 421.4f, 62f, 15f), 417.2f);
            FoodCourt(new Rect(156.5f, 379.8f, 23.3f, 30f));
            FreightYard(new Rect(329f, 380f, 26.5f, 26.8f));
            foreach (string id in MainRoads) Street(id);
            Bunting();
            foreach (var site in ctx.town.GetComponentsInChildren<CoastalInfillSite>(true)) ShopRow(site);
            Greens();
            ctx.Log(Name, $"{stops.Count} forecourts with dumpster pads, {trees} street trees, {furniture} street furniture, {signs} signs, " +
                          $"{cars} parked cars, {clutter} shop-row dressings" + (skipped.Count > 0 ? "; skipped: " + string.Join(", ", skipped) : ""));
        }

        // ------------------------------------------------------------------------------------------ palette, props

        void Palette()
        {
            TownLifeKit.Define("commons.concrete", new Color(0.7f, 0.69f, 0.66f), 0.1f);
            TownLifeKit.Define("commons.concrete.dark", new Color(0.5f, 0.5f, 0.5f), 0.1f);
            TownLifeKit.Define("commons.asphalt", new Color(0.2f, 0.2f, 0.22f), 0.12f);
            TownLifeKit.Define("commons.line", new Color(0.93f, 0.93f, 0.9f), 0.1f);
            TownLifeKit.Define("commons.hazard", new Color(0.98f, 0.8f, 0.1f), 0.2f);
            TownLifeKit.Define("commons.pave.a", new Color(0.78f, 0.66f, 0.56f), 0.1f);
            TownLifeKit.Define("commons.pave.b", new Color(0.66f, 0.62f, 0.6f), 0.1f);
            TownLifeKit.Define("commons.steel", new Color(0.55f, 0.57f, 0.6f), 0.5f, 0.6f);
            TownLifeKit.Define("commons.dark", new Color(0.14f, 0.15f, 0.17f), 0.3f);
            TownLifeKit.Define("commons.box.red", new Color(0.72f, 0.2f, 0.16f), 0.25f);
            TownLifeKit.Define("commons.box.blue", new Color(0.16f, 0.36f, 0.6f), 0.25f);
            TownLifeKit.Define("commons.box.green", new Color(0.2f, 0.5f, 0.32f), 0.25f);
            TownLifeKit.Define("commons.cardboard", new Color(0.72f, 0.56f, 0.38f), 0.05f);
            TownLifeKit.Define("commons.screen", new Color(0.4f, 0.75f, 0.9f), 0.7f);
            TownLifeKit.DefineEmissive("commons.bulb", new Color(1f, 0.9f, 0.55f), new Color(1f, 0.8f, 0.4f) * 1.6f);
        }

        void Props()
        {
            // Street trees: a kit tree standing in a square iron grate
            string[] kinds = { "kit.tree.round.m", "kit.tree.maple.m", "kit.tree.blossom.m" };
            for (int i = 0; i < kinds.Length; i++)
                TownLifeKit.DefineProp("commons.tree." + (char)('a' + i))
                    .Box("commons.dark", new Vector3(0f, 0.02f, 0f), new Vector3(1.5f, 0.04f, 1.5f))
                    .Box("soil", new Vector3(0f, 0.03f, 0f), new Vector3(1.1f, 0.05f, 1.1f))
                    .Add(kinds[i], Vector3.zero)
                    .ColliderTrunk(0.2f, 2.6f);
            TownLifeKit.DefineProp("commons.planter.box")
                .Box("commons.concrete", new Vector3(0f, 0.3f, 0f), new Vector3(1.7f, 0.6f, 0.75f))
                .Box("soil", new Vector3(0f, 0.6f, 0f), new Vector3(1.5f, 0.04f, 0.55f))
                .Blob("hedge", new Vector3(-0.35f, 0.85f, 0f), new Vector3(0.5f, 0.38f, 0.36f), 11)
                .Blob("hedge.light", new Vector3(0.4f, 0.8f, 0f), new Vector3(0.42f, 0.32f, 0.34f), 17)
                .Blob("flower.pink", new Vector3(0.05f, 0.72f, 0.2f), new Vector3(0.3f, 0.14f, 0.12f), 5)
                .ColliderBox(new Vector3(0f, 0.3f, 0f), new Vector3(1.7f, 0.6f, 0.75f));
            TownLifeKit.DefineProp("commons.meter")
                .Cyl("commons.steel", Vector3.zero, 0.035f, 1.05f, 6)
                .Box("commons.dark", new Vector3(0f, 1.18f, 0f), new Vector3(0.16f, 0.26f, 0.12f))
                .Box("commons.screen", new Vector3(0f, 1.22f, 0.065f), new Vector3(0.1f, 0.08f, 0.01f)).NoShadows();
            TownLifeKit.DefineProp("commons.hydrant")
                .Cyl("paint.red", Vector3.zero, 0.13f, 0.5f, 8)
                .Dome("paint.red", new Vector3(0f, 0.5f, 0f), new Vector3(0.14f, 0.1f, 0.14f))
                .Cyl("commons.steel", new Vector3(0f, 0.32f, 0.08f), 0.06f, 0.1f, 6, -1f, new Vector3(90f, 0f, 0f))
                .Box("paint.red", new Vector3(0f, 0.32f, 0f), new Vector3(0.42f, 0.09f, 0.09f)).NoShadows();
            TownLifeKit.DefineProp("commons.newsbox")
                .Box("commons.box.blue", new Vector3(0f, 0.5f, 0f), new Vector3(0.46f, 0.9f, 0.4f))
                .Box("commons.screen", new Vector3(0f, 0.68f, 0.205f), new Vector3(0.34f, 0.4f, 0.01f))
                .Box("commons.dark", new Vector3(0f, 0.04f, 0f), new Vector3(0.36f, 0.08f, 0.3f));
            TownLifeKit.DefineProp("commons.bikerack")
                .Torus("commons.steel", new Vector3(-0.7f, 0f, 0f), 0.42f, 0.03f, 12, 5, new Vector3(0f, 0f, 90f))
                .Torus("commons.steel", new Vector3(0f, 0f, 0f), 0.42f, 0.03f, 12, 5, new Vector3(0f, 0f, 90f))
                .Torus("commons.steel", new Vector3(0.7f, 0f, 0f), 0.42f, 0.03f, 12, 5, new Vector3(0f, 0f, 90f))
                // one bike leaning on it
                .Torus("commons.dark", new Vector3(0.36f, 0.32f, -0.45f), 0.3f, 0.025f, 12, 4, new Vector3(0f, 0f, 90f))
                .Torus("commons.dark", new Vector3(0.36f, 0.32f, 0.5f), 0.3f, 0.025f, 12, 4, new Vector3(0f, 0f, 90f))
                .Bar("paint.teal", new Vector3(0.36f, 0.32f, -0.45f), new Vector3(0.36f, 0.75f, 0.1f), 0.04f)
                .Bar("paint.teal", new Vector3(0.36f, 0.75f, 0.1f), new Vector3(0.36f, 0.32f, 0.5f), 0.04f)
                .Bar("commons.dark", new Vector3(0.2f, 0.9f, 0.3f), new Vector3(0.52f, 0.9f, 0.3f), 0.03f).NoShadows();
            foreach (var c in new[] { "red", "yellow", "teal" })
                TownLifeKit.DefineProp("commons.sandwich." + c)
                    .Box("paint." + c, new Vector3(0f, 0.45f, 0.12f), new Vector3(0.6f, 0.9f, 0.03f), new Vector3(-14f, 0f, 0f))
                    .Box("paint." + c, new Vector3(0f, 0.45f, -0.12f), new Vector3(0.6f, 0.9f, 0.03f), new Vector3(14f, 0f, 0f))
                    .Box("paint.white", new Vector3(0f, 0.5f, 0.142f), new Vector3(0.46f, 0.6f, 0.01f), new Vector3(-14f, 0f, 0f)).NoShadows();
            foreach (var c in new[] { "red", "yellow", "teal", "orange" })
                TownLifeKit.DefineProp("commons.cafe." + c)
                    .Cyl("commons.steel", Vector3.zero, 0.05f, 0.72f, 6)
                    .Cyl("paint.white", new Vector3(0f, 0.72f, 0f), 0.48f, 0.04f, 10)
                    .Cyl("commons.steel", new Vector3(0f, 0.76f, 0f), 0.025f, 1.6f, 6)
                    .Cone("paint." + c, new Vector3(0f, 2.05f, 0f), 1.25f, 0.5f, 8)
                    .Box("wood.warm", new Vector3(0.75f, 0.42f, 0f), new Vector3(0.38f, 0.05f, 0.38f))
                    .Box("wood.warm", new Vector3(0.93f, 0.66f, 0f), new Vector3(0.04f, 0.5f, 0.38f))
                    .Box("wood.warm", new Vector3(-0.75f, 0.42f, 0f), new Vector3(0.38f, 0.05f, 0.38f))
                    .Box("wood.warm", new Vector3(-0.93f, 0.66f, 0f), new Vector3(0.04f, 0.5f, 0.38f))
                    .Box("wood.warm", new Vector3(0f, 0.42f, 0.75f), new Vector3(0.38f, 0.05f, 0.38f))
                    .Box("wood.warm", new Vector3(0f, 0.66f, 0.93f), new Vector3(0.38f, 0.5f, 0.04f))
                    .ColliderCapsule(new Vector3(0f, 0.5f, 0f), 0.45f, 1f);
            TownLifeKit.DefineProp("commons.cartcorral")
                .Bar("commons.steel", new Vector3(-1.1f, 0.9f, -1.6f), new Vector3(-1.1f, 0.9f, 1.6f), 0.06f)
                .Bar("commons.steel", new Vector3(1.1f, 0.9f, -1.6f), new Vector3(1.1f, 0.9f, 1.6f), 0.06f)
                .Bar("commons.steel", new Vector3(-1.1f, 0.9f, -1.6f), new Vector3(1.1f, 0.9f, -1.6f), 0.06f)
                .Bar("commons.steel", new Vector3(-1.1f, 0f, -1.6f), new Vector3(-1.1f, 0.9f, -1.6f), 0.06f)
                .Bar("commons.steel", new Vector3(1.1f, 0f, -1.6f), new Vector3(1.1f, 0.9f, -1.6f), 0.06f)
                .Bar("commons.steel", new Vector3(-1.1f, 0f, 1.6f), new Vector3(-1.1f, 0.9f, 1.6f), 0.06f)
                .Bar("commons.steel", new Vector3(1.1f, 0f, 1.6f), new Vector3(1.1f, 0.9f, 1.6f), 0.06f)
                .Box("paint.blue", new Vector3(0f, 1.25f, -1.6f), new Vector3(1.6f, 0.4f, 0.04f))
                // three nested trolleys
                .Box("commons.steel", new Vector3(-0.35f, 0.62f, -0.9f), new Vector3(0.55f, 0.42f, 0.8f))
                .Box("commons.steel", new Vector3(-0.35f, 0.62f, -0.4f), new Vector3(0.5f, 0.4f, 0.8f))
                .Box("commons.steel", new Vector3(-0.35f, 0.62f, 0.1f), new Vector3(0.46f, 0.38f, 0.8f))
                .Box("paint.red", new Vector3(-0.35f, 0.98f, 0.55f), new Vector3(0.5f, 0.04f, 0.05f))
                .Box("commons.steel", new Vector3(0.45f, 0.62f, 0.4f), new Vector3(0.55f, 0.42f, 0.8f), new Vector3(0f, 14f, 0f));
            TownLifeKit.DefineProp("commons.vending")
                .Box("paint.red", new Vector3(0f, 0.92f, 0f), new Vector3(0.95f, 1.84f, 0.72f))
                .Box("commons.screen", new Vector3(-0.12f, 1.15f, 0.365f), new Vector3(0.58f, 1.1f, 0.01f))
                .Box("commons.dark", new Vector3(0.32f, 1.05f, 0.365f), new Vector3(0.16f, 0.5f, 0.01f))
                .Box("commons.dark", new Vector3(-0.12f, 0.3f, 0.365f), new Vector3(0.5f, 0.16f, 0.01f))
                .ColliderBox(new Vector3(0f, 0.92f, 0f), new Vector3(0.95f, 1.84f, 0.72f));
            TownLifeKit.DefineProp("commons.icebox")
                .Box("paint.white", new Vector3(0f, 0.55f, 0f), new Vector3(1.5f, 1.1f, 0.75f))
                .Box("commons.box.blue", new Vector3(0f, 0.72f, 0.38f), new Vector3(1.2f, 0.4f, 0.01f))
                .Box("commons.steel", new Vector3(0f, 1.12f, 0.1f), new Vector3(1.5f, 0.05f, 0.6f), new Vector3(-8f, 0f, 0f))
                .ColliderBox(new Vector3(0f, 0.55f, 0f), new Vector3(1.5f, 1.1f, 0.75f));
            TownLifeKit.DefineProp("commons.pylon")
                .Box("commons.dark", new Vector3(-1.25f, 2.3f, 0f), new Vector3(0.16f, 4.6f, 0.16f))
                .Box("commons.dark", new Vector3(1.25f, 2.3f, 0f), new Vector3(0.16f, 4.6f, 0.16f))
                .Box("commons.concrete", new Vector3(0f, 0.15f, 0f), new Vector3(3f, 0.3f, 0.7f))
                .Box("commons.steel", new Vector3(0f, 2.55f, 0f), new Vector3(2.5f, 0.5f, 0.1f))
                .ColliderBox(new Vector3(0f, 0.6f, 0f), new Vector3(3f, 1.2f, 0.7f));
            TownLifeKit.DefineProp("commons.roofsign.frame")
                .Bar("commons.dark", new Vector3(-4.6f, 0f, -0.6f), new Vector3(-4.6f, 1.6f, 0f), 0.1f)
                .Bar("commons.dark", new Vector3(4.6f, 0f, -0.6f), new Vector3(4.6f, 1.6f, 0f), 0.1f)
                .Bar("commons.dark", new Vector3(0f, 0f, -0.6f), new Vector3(0f, 1.6f, 0f), 0.1f)
                .Box("commons.dark", new Vector3(0f, 0.1f, 0f), new Vector3(10.6f, 0.2f, 0.2f));
            TownLifeKit.DefineProp("commons.screenwall")
                .Box("brick", new Vector3(0f, 0.85f, 0f), new Vector3(3.8f, 1.7f, 0.24f))
                .Box("commons.concrete", new Vector3(0f, 1.74f, 0f), new Vector3(3.9f, 0.1f, 0.32f))
                .ColliderBox(new Vector3(0f, 0.85f, 0f), new Vector3(3.8f, 1.7f, 0.24f));
            TownLifeKit.DefineProp("commons.wheelstop")
                .Box("commons.concrete", new Vector3(0f, 0.07f, 0f), new Vector3(1.8f, 0.14f, 0.22f))
                .Box("commons.hazard", new Vector3(0f, 0.145f, 0f), new Vector3(1.2f, 0.01f, 0.1f)).NoShadows();
            TownLifeKit.DefineProp("commons.psign")
                .Cyl("commons.steel", Vector3.zero, 0.05f, 2.6f, 6)
                .Box("commons.box.blue", new Vector3(0f, 2.9f, 0f), new Vector3(0.8f, 0.8f, 0.06f))
                .Box("paint.white", new Vector3(-0.08f, 2.9f, 0.035f), new Vector3(0.14f, 0.5f, 0.01f))
                .Box("paint.white", new Vector3(0.06f, 3.03f, 0.035f), new Vector3(0.24f, 0.24f, 0.01f));
            foreach (var c in new[] { "red", "blue", "green" })
            {
                var r = TownLifeKit.DefineProp("commons.container." + c).Box("commons.box." + c, new Vector3(0f, 1.3f, 0f), new Vector3(2.44f, 2.6f, 6.06f));
                for (int i = -5; i <= 5; i++) r.Box("commons.box." + c, new Vector3(0f, 1.3f, i * 0.52f), new Vector3(2.52f, 2.4f, 0.12f));
                r.Box("commons.dark", new Vector3(0f, 1.3f, 3.04f), new Vector3(2.2f, 2.3f, 0.02f))
                 .Bar("commons.steel", new Vector3(-0.3f, 0.3f, 3.06f), new Vector3(-0.3f, 2.3f, 3.06f), 0.05f)
                 .Bar("commons.steel", new Vector3(0.3f, 0.3f, 3.06f), new Vector3(0.3f, 2.3f, 3.06f), 0.05f)
                 .ColliderBox(new Vector3(0f, 1.3f, 0f), new Vector3(2.44f, 2.6f, 6.06f)).Occluder();
            }
            TownLifeKit.DefineProp("commons.pallets")
                .Box("wood.pale", new Vector3(0f, 0.07f, 0f), new Vector3(1.2f, 0.14f, 1f))
                .Box("wood.pale", new Vector3(0.03f, 0.21f, -0.02f), new Vector3(1.2f, 0.14f, 1f), new Vector3(0f, 6f, 0f))
                .Box("wood.pale", new Vector3(-0.02f, 0.35f, 0.02f), new Vector3(1.2f, 0.14f, 1f), new Vector3(0f, -4f, 0f))
                .Box("commons.cardboard", new Vector3(-0.2f, 0.72f, 0f), new Vector3(0.7f, 0.6f, 0.8f), new Vector3(0f, 9f, 0f))
                .Box("commons.cardboard", new Vector3(0.36f, 0.62f, 0.1f), new Vector3(0.4f, 0.4f, 0.5f));
            TownLifeKit.DefineProp("commons.crates")
                .Box("commons.box.green", new Vector3(-0.3f, 0.2f, 0f), new Vector3(0.6f, 0.4f, 0.42f))
                .Box("commons.box.red", new Vector3(-0.28f, 0.6f, 0.02f), new Vector3(0.6f, 0.4f, 0.42f), new Vector3(0f, 8f, 0f))
                .Box("commons.box.blue", new Vector3(0.4f, 0.2f, 0.05f), new Vector3(0.6f, 0.4f, 0.42f), new Vector3(0f, -12f, 0f))
                .Box("commons.cardboard", new Vector3(0.42f, 0.56f, 0.03f), new Vector3(0.46f, 0.32f, 0.36f), new Vector3(0f, 20f, 0f));
            TownLifeKit.DefineProp("commons.acunit")
                .Box("commons.steel", new Vector3(0f, 0.5f, 0f), new Vector3(1.1f, 0.9f, 0.5f))
                .Cyl("commons.dark", new Vector3(0f, 0.5f, 0.255f), 0.34f, 0.02f, 10, -1f, new Vector3(90f, 0f, 0f))
                .Box("commons.concrete", new Vector3(0f, 0.03f, 0f), new Vector3(1.3f, 0.06f, 0.7f));
            TownLifeKit.DefineProp("commons.boxes")
                .Box("commons.cardboard", new Vector3(0f, 0.25f, 0f), new Vector3(0.6f, 0.5f, 0.5f))
                .Box("commons.cardboard", new Vector3(0.05f, 0.68f, 0.02f), new Vector3(0.45f, 0.36f, 0.4f), new Vector3(0f, 17f, 0f))
                .Box("commons.cardboard", new Vector3(0.62f, 0.18f, 0.1f), new Vector3(0.42f, 0.36f, 0.42f), new Vector3(0f, -24f, 0f))
                .Box("commons.dark", new Vector3(-0.6f, 0.3f, 0.05f), new Vector3(0.36f, 0.6f, 0.36f));
            TownLifeKit.DefineProp("commons.display")
                .Box("wood.warm", new Vector3(0f, 0.4f, 0f), new Vector3(1.4f, 0.06f, 0.6f))
                .Box("wood.warm", new Vector3(-0.62f, 0.2f, 0f), new Vector3(0.06f, 0.4f, 0.5f))
                .Box("wood.warm", new Vector3(0.62f, 0.2f, 0f), new Vector3(0.06f, 0.4f, 0.5f))
                .Blob("flower.yellow", new Vector3(-0.4f, 0.55f, 0f), new Vector3(0.2f, 0.14f, 0.2f), 3)
                .Blob("flower.red", new Vector3(0f, 0.55f, 0f), new Vector3(0.2f, 0.14f, 0.2f), 5)
                .Blob("flower.purple", new Vector3(0.4f, 0.55f, 0f), new Vector3(0.2f, 0.14f, 0.2f), 7).NoShadows();
            TownLifeKit.DefineProp("commons.forklift")
                .Box("commons.hazard", new Vector3(0f, 0.75f, -0.3f), new Vector3(1.1f, 0.9f, 1.5f))
                .Box("commons.dark", new Vector3(0f, 1.45f, -0.75f), new Vector3(1f, 0.5f, 0.5f))
                .Bar("commons.dark", new Vector3(-0.45f, 1.2f, 0.3f), new Vector3(-0.45f, 2.1f, 0.3f), 0.07f)
                .Bar("commons.dark", new Vector3(0.45f, 1.2f, 0.3f), new Vector3(0.45f, 2.1f, 0.3f), 0.07f)
                .Box("commons.dark", new Vector3(0f, 2.12f, -0.2f), new Vector3(1.05f, 0.06f, 1.1f))
                .Bar("commons.steel", new Vector3(-0.35f, 0.15f, 0.55f), new Vector3(-0.35f, 2.4f, 0.55f), 0.1f)
                .Bar("commons.steel", new Vector3(0.35f, 0.15f, 0.55f), new Vector3(0.35f, 2.4f, 0.55f), 0.1f)
                .Box("commons.steel", new Vector3(-0.3f, 0.2f, 1.15f), new Vector3(0.12f, 0.06f, 1.2f))
                .Box("commons.steel", new Vector3(0.3f, 0.2f, 1.15f), new Vector3(0.12f, 0.06f, 1.2f))
                .Cyl("commons.dark", new Vector3(-0.55f, 0.3f, 0.2f), 0.3f, 0.2f, 10, -1f, new Vector3(0f, 0f, 90f))
                .Cyl("commons.dark", new Vector3(0.75f, 0.3f, 0.2f), 0.3f, 0.2f, 10, -1f, new Vector3(0f, 0f, 90f))
                .Cyl("commons.dark", new Vector3(-0.5f, 0.22f, -0.8f), 0.22f, 0.18f, 10, -1f, new Vector3(0f, 0f, 90f))
                .Cyl("commons.dark", new Vector3(0.68f, 0.22f, -0.8f), 0.22f, 0.18f, 10, -1f, new Vector3(0f, 0f, 90f))
                .ColliderBox(new Vector3(0f, 0.9f, 0f), new Vector3(1.2f, 1.8f, 2.4f));
            TownLifeKit.DefineProp("commons.foodtruck")
                .Box("paint.teal", new Vector3(0f, 1.55f, -0.5f), new Vector3(2.2f, 2.3f, 3.9f))
                .Box("paint.cream", new Vector3(0f, 1.25f, 2.05f), new Vector3(2.1f, 1.7f, 1.3f))
                .Box("commons.screen", new Vector3(0f, 1.7f, 2.71f), new Vector3(1.8f, 0.7f, 0.02f))
                .Box("commons.dark", new Vector3(1.11f, 1.75f, -0.5f), new Vector3(0.02f, 0.9f, 2.4f))
                .Box("paint.orange", new Vector3(1.75f, 2.35f, -0.5f), new Vector3(1.3f, 0.06f, 2.8f), new Vector3(0f, 0f, -14f))
                .Box("wood.warm", new Vector3(1.35f, 1.28f, -0.5f), new Vector3(0.5f, 0.06f, 2.4f))
                .Box("paint.orange", new Vector3(0f, 2.9f, -0.5f), new Vector3(1.6f, 0.5f, 2.6f))
                .Cyl("commons.dark", new Vector3(-1.15f, 0.42f, 1.7f), 0.42f, 0.3f, 10, -1f, new Vector3(0f, 0f, 90f))
                .Cyl("commons.dark", new Vector3(0.85f, 0.42f, 1.7f), 0.42f, 0.3f, 10, -1f, new Vector3(0f, 0f, 90f))
                .Cyl("commons.dark", new Vector3(-1.15f, 0.42f, -1.6f), 0.42f, 0.3f, 10, -1f, new Vector3(0f, 0f, 90f))
                .Cyl("commons.dark", new Vector3(0.85f, 0.42f, -1.6f), 0.42f, 0.3f, 10, -1f, new Vector3(0f, 0f, 90f))
                .ColliderBox(new Vector3(0f, 1.4f, 0.1f), new Vector3(2.2f, 2.8f, 5.2f)).Occluder();
            TownLifeKit.DefineProp("commons.pole")
                .Cyl("commons.dark", Vector3.zero, 0.09f, 6.6f, 8, 0.06f)
                .Box("commons.concrete", new Vector3(0f, 0.12f, 0f), new Vector3(0.4f, 0.24f, 0.4f));
            // A 1 m run of bunting along +Z (scaled to the span): the cord sags a little, six flags hang from it
            var bunting = TownLifeKit.DefineProp("commons.bunting").Bar("commons.dark", new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f), 0.02f).NoShadows();
            string[] flags = { "paint.red", "paint.yellow", "paint.teal", "paint.orange", "paint.white", "paint.blue" };
            for (int i = 0; i < 6; i++)
            {
                float z0 = i / 6f + 0.02f, z1 = (i + 1) / 6f - 0.02f;
                bunting.Sheet(flags[i], new Vector3(0f, 0f, z0), new Vector3(0f, 0f, z1), new Vector3(0f, -0.7f, (z0 + z1) * 0.5f), new Vector3(0f, -0.7f, (z0 + z1) * 0.5f));
            }
            var lights = TownLifeKit.DefineProp("commons.stringlights").Bar("commons.dark", Vector3.zero, new Vector3(0f, 0f, 1f), 0.015f).NoShadows();
            for (int i = 0; i < 7; i++) lights.Sphere("commons.bulb", new Vector3(0f, -0.09f, (i + 0.5f) / 7f), new Vector3(0.07f, 0.09f, 0.01f), 6, 4);
            TownLifeKit.DefineProp("commons.busshelter")
                .Box("commons.dark", new Vector3(-1.6f, 1.2f, -0.6f), new Vector3(0.08f, 2.4f, 0.08f))
                .Box("commons.dark", new Vector3(1.6f, 1.2f, -0.6f), new Vector3(0.08f, 2.4f, 0.08f))
                .Box("commons.dark", new Vector3(-1.6f, 1.2f, 0.5f), new Vector3(0.08f, 2.4f, 0.08f))
                .Box("commons.dark", new Vector3(1.6f, 1.2f, 0.5f), new Vector3(0.08f, 2.4f, 0.08f))
                .Box("commons.screen", new Vector3(0f, 1.3f, -0.6f), new Vector3(3.1f, 1.8f, 0.03f))
                .Box("paint.teal", new Vector3(0f, 2.45f, 0f), new Vector3(3.5f, 0.1f, 1.5f))
                .Box("wood.warm", new Vector3(0f, 0.5f, -0.3f), new Vector3(2.4f, 0.07f, 0.4f))
                .ColliderBox(new Vector3(0f, 1.2f, -0.6f), new Vector3(3.2f, 2.4f, 0.12f));
        }

        // ------------------------------------------------------------------------------------------ keep-outs

        void SeedKeepOuts()
        {
            var E = TownLifeOccupancy.Block.Everything;
            var nodes = new List<Vector3>();
            foreach (string id in AllRoads)
            {
                var road = CoastalCityLayout.Road(id);
                if (road == null) continue;
                occ.ReservePolyline(road.points, road.halfWidth + 3f, TownLifeTags.Sidewalk, E);
                foreach (var p in road.points) nodes.Add(p);
                if (road.service || road.bridge) continue;
                // the lamp line of the base builder: both sides, every 42 m from station 18
                for (float s = 18f; s < road.Length; s += 42f)
                {
                    var c = TownLifeGeo.PointAt(road.points, s, out var t);
                    for (int side = -1; side <= 1; side += 2) occ.ReserveCircle(c + TownLifeGeo.Right(t) * side * (road.halfWidth + 2.4f), 1.3f, "lamp");
                }
            }
            // Junctions: where a road point lies on (or ends at) another road
            for (int i = 0; i < nodes.Count; i++)
                for (int j = i + 1; j < nodes.Count; j++)
                    if (TownLifeGeo.Dist(nodes[i], nodes[j]) < 0.6f) { occ.ReserveCircle(nodes[i], 11f, "junction"); break; }
            foreach (var site in ctx.town.GetComponentsInChildren<CoastalInfillSite>(true))
            {
                Vector3 into = Flat(site.transform.forward), right = Flat(site.transform.right);
                occ.ReserveQuad(TownLifeGeo.Quad(site.transform.position + into * site.depth * 0.5f, right, into, site.width * 0.5f, site.depth * 0.5f), "infill", E, 0.3f);
            }
            foreach (var gate in new[] { new Vector3(115.7f, 0f, 354.6f), new Vector3(367.2f, 0f, 351.7f), new Vector3(401.4f, 0f, 445.9f) })
                occ.ReserveCircle(gate, 9f, "gate", E);
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.normalized; }

        /// <summary>A shop lot's frame: x along the kerb, z towards the road (negative = into the lot).</summary>
        sealed class Lot
        {
            public PickupStop stop;
            public Vector3 origin, right, road;
            public float yaw;
            public bool restaurant;
            public Vector3 P(float x, float z) => origin + right * x + road * z;
        }

        List<Lot> Stops()
        {
            var list = new List<Lot>();
            foreach (var stop in ctx.town.GetComponentsInChildren<PickupStop>(true))
            {
                if (stop == null || stop.districtId != District) continue;
                var lot = new Lot { stop = stop, origin = stop.transform.position, right = Flat(stop.transform.right), road = Flat(stop.transform.forward) };
                lot.yaw = TownLifeGeo.Yaw(lot.road);
                lot.restaurant = stop.stopId.Contains("dining");
                list.Add(lot);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.stop.stopId, b.stop.stopId));
            return list;
        }

        void ReserveLot(Lot lot)
        {
            var E = TownLifeOccupancy.Block.Everything;
            occ.ReserveQuad(TownLifeGeo.Quad(lot.P(0f, -17.5f), lot.right, lot.road, 12.5f, 7.5f), "building", E, 0.4f);
            occ.ReserveLine(lot.P(0f, 0f), lot.P(0f, -10f), 1.9f, "path", E);
            occ.ReserveCircle(lot.P(-3f, -0.35f), 1.3f, "stop", E);
            occ.ReserveCircle(lot.P(2f, -2f), 0.9f, "stop", E);
            // the dumpster, and a clear run from the road to its front for a truck to back along
            var pad = DumpsterRules.PadLocal;
            occ.ReserveQuad(TownLifeGeo.Quad(lot.P(pad.x, pad.z), lot.right, lot.road, 2.1f, 1.8f), "dumpster", E);
            occ.ReserveLine(lot.P(pad.x, pad.z + 1f), lot.P(pad.x, 6f), 2.3f, "dumpster.approach", E);
        }

        // ------------------------------------------------------------------------------------------ shop forecourts

        GameObject Put(Transform parent, string prop, Vector3 p, float yaw, float radius, string tag, float scale = 1f)
        {
            if (!occ.TryReserve(p, radius, tag + ".commons")) return null;
            return TownLifeKit.Spawn(parent, prop, G(p), yaw, scale);
        }

        void Forecourt(Lot lot)
        {
            string id = TownLifeKit.Sanitize(lot.stop.stopId);
            var g = TownLifeKit.Group(root, "TL_Shop_" + id);
            var rng = ctx.Rng("commons/forecourt/" + lot.stop.stopId);
            string name = ShopNames.TryGetValue(lot.stop.stopId, out var n) ? n : lot.stop.address.ToUpperInvariant();
            string[] accents = { "paint.red", "paint.teal", "paint.orange", "paint.blue", "paint.purple", "paint.green" };
            string accent = accents[(int)((uint)TownLifeContext.StableHash(lot.stop.stopId) % (uint)accents.Length)];

            // Paving from the back of the pavement to the shop front, in two tones
            var paving = new TownLifeKit.MeshData();
            Flat4(paving, lot.restaurant ? "commons.pave.a" : "commons.pave.b", lot, -12.2f, 12.2f, -9.95f, -2.25f, TownLifeContext.PavingY);
            Flat4(paving, "commons.concrete.dark", lot, -12.2f, 12.2f, -2.6f, -2.25f, TownLifeContext.MarkingY);
            // The dumpster pad: concrete, a yellow box round it, hatching out to the kerb
            var pad = DumpsterRules.PadLocal;
            Flat4(paving, "commons.concrete", lot, pad.x - 2f, pad.x + 2f, pad.z - 1.6f, pad.z + 1.5f, TownLifeContext.MarkingY);
            float y2 = TownLifeContext.MarkingY + TownLifeContext.DecalLift;
            Flat4(paving, "commons.hazard", lot, pad.x - 2f, pad.x + 2f, pad.z + 1.4f, pad.z + 1.55f, y2);
            Flat4(paving, "commons.hazard", lot, pad.x - 2f, pad.x - 1.85f, pad.z - 1.6f, pad.z + 1.5f, y2);
            Flat4(paving, "commons.hazard", lot, pad.x + 1.85f, pad.x + 2f, pad.z - 1.6f, pad.z + 1.5f, y2);
            for (int i = 0; i < 5; i++) // chevrons from the pad to the back of the pavement: keep clear, back in here
            {
                float z0 = pad.z + 1.9f + i * 0.42f;
                if (z0 + 0.2f > -2.3f) break;
                Flat4(paving, "commons.hazard", lot, pad.x - 1.6f, pad.x + 1.6f, z0, z0 + 0.2f, y2);
            }
            TownLifeKit.Custom(g, "commons_forecourt_" + id, paving);
            // Screen walls behind the dumpster and on its outer side
            TownLifeKit.Spawn(g, "commons.screenwall", G(lot.P(pad.x, pad.z - 2.0f)), lot.yaw);
            TownLifeKit.Spawn(g, "commons.screenwall", G(lot.P(pad.x + 2.25f, pad.z - 0.2f)), lot.yaw + 90f, new Vector3(0.9f, 1f, 1f));
            occ.ReserveLine(lot.P(pad.x - 2f, pad.z - 2f), lot.P(pad.x + 2.3f, pad.z - 2f), 0.4f, TownLifeTags.Structure + ".commons");

            // The shop's name: a pylon by the pavement and a board on the roof
            Vector3 pylon = lot.P(-10.2f, -3.6f);
            if (occ.TryReserve(pylon, 1.6f, TownLifeTags.Structure + ".commons"))
            {
                TownLifeKit.Spawn(g, "commons.pylon", G(pylon), lot.yaw);
                TownLifeKit.SignBoard(g, "commons_pylon_" + id, G(pylon) + Vector3.up * 3.75f, lot.yaw, new Vector3(2.9f, 1.5f, 0.14f), accent, name, Color.white, true);
                signs++;
            }
            Vector3 roof = lot.P(0f, -10.6f) + Vector3.up * 6.25f;
            roof.y = ctx.scene.GroundY(roof) + 6.5f;
            TownLifeKit.Spawn(g, "commons.roofsign.frame", roof, lot.yaw);
            TownLifeKit.SignBoard(g, "commons_roofsign_" + id, roof + Vector3.up * 1.15f + lot.road * 0.12f, lot.yaw, new Vector3(10.4f, 1.7f, 0.14f), accent, name, Color.white);
            signs++;

            // Planters either side of the door, and something to sit on
            Put(g, "commons.planter.box", lot.P(-2.9f, -8.9f), lot.yaw, 1f, TownLifeTags.Furniture);
            Put(g, "commons.planter.box", lot.P(2.9f, -8.9f), lot.yaw, 1f, TownLifeTags.Furniture);
            Put(g, "kit.bench.park", lot.P(-5.4f, -9.2f), lot.yaw, 1f, TownLifeTags.Furniture);
            Put(g, "kit.bin.park", lot.P(-3.9f, -4.2f), lot.yaw + R(rng, -20f, 20f), 0.5f, TownLifeTags.Furniture);
            Put(g, "commons.sandwich." + (lot.restaurant ? "yellow" : "red"), lot.P(2.4f, -4.4f), lot.yaw + R(rng, -25f, 25f), 0.5f, TownLifeTags.Furniture);
            Put(g, "commons.bikerack", lot.P(5.2f, -3.6f), lot.yaw + 90f, 1f, TownLifeTags.Furniture);
            furniture += 6;

            if (lot.restaurant)
            {
                string[] colours = { "red", "yellow", "teal", "orange" };
                int k = 0;
                for (int row = 0; row < 2; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        var p = lot.P(-9.6f + col * 2.6f + R(rng, -0.2f, 0.2f), -4.6f - row * 2.7f + R(rng, -0.2f, 0.2f));
                        if (Put(g, "commons.cafe." + colours[(k++ + row) % colours.Length], p, R(rng, 0f, 360f), 1.1f, TownLifeTags.Furniture) != null) furniture++;
                    }
                StringLights(g, "shop_" + id, new[] { lot.P(-11.4f, -3.2f), lot.P(-3.2f, -3.2f), lot.P(-3.2f, -9.6f), lot.P(-11.4f, -9.6f) }, true);
            }
            else
            {
                Put(g, "commons.cartcorral", lot.P(-7.6f, -6.2f), lot.yaw, 2f, TownLifeTags.Structure);
                Put(g, "commons.vending", lot.P(5.4f, -9.4f), lot.yaw, 0.7f, TownLifeTags.Furniture);
                Put(g, "commons.icebox", lot.P(3.9f + 2.9f, -9.4f), lot.yaw, 0.9f, TownLifeTags.Furniture);
                Put(g, "commons.newsbox", lot.P(-1.9f, -3.1f), lot.yaw, 0.4f, TownLifeTags.Furniture);
                Put(g, "commons.pallets", lot.P(-10.6f, -9.2f), lot.yaw + R(rng, -15f, 15f), 0.9f, TownLifeTags.Furniture);
                furniture += 5;
            }
        }

        static void Flat4(TownLifeKit.MeshData d, string mat, Lot lot, float x0, float x1, float z0, float z1, float y)
        {
            Vector3 a = At(lot.P(x0, z0), y), b = At(lot.P(x1, z0), y), c = At(lot.P(x1, z1), y), e = At(lot.P(x0, z1), y);
            d.QuadOut(mat, At((a + c) * 0.5f, y - 10f), a, b, c, e);
        }

        static void FlatRect(TownLifeKit.MeshData d, string mat, float x0, float x1, float z0, float z1, float y)
        {
            Vector3 a = new Vector3(x0, y, z0), b = new Vector3(x1, y, z0), c = new Vector3(x1, y, z1), e = new Vector3(x0, y, z1);
            d.QuadOut(mat, new Vector3((x0 + x1) * 0.5f, y - 10f, (z0 + z1) * 0.5f), a, b, c, e);
        }

        void StringLights(Transform parent, string key, IList<Vector3> corners, bool closed)
        {
            int n = corners.Count;
            for (int i = 0; i < n; i++) TownLifeKit.Spawn(parent, "commons.pole", G(corners[i]), 0f, new Vector3(1f, 0.52f, 1f));
            for (int i = 0; i < (closed ? n : n - 1); i++)
            {
                Vector3 a = G(corners[i]) + Vector3.up * 3.35f, b = G(corners[(i + 1) % n]) + Vector3.up * 3.35f;
                float len = TownLifeGeo.Dist(a, b);
                if (len < 0.5f) continue;
                TownLifeKit.Spawn(parent, "commons.stringlights", a, TownLifeGeo.Yaw(b - a), new Vector3(1f, 1f, len));
            }
        }

        // ------------------------------------------------------------------------------------------ car parks

        bool Claim(string what, Rect r, string tag)
        {
            var q = TownLifeGeo.Quad(r);
            if (!occ.FreeQuad(q, TownLifeOccupancy.What.Solid, 0.2f))
            {
                skipped.Add(what + " (" + occ.Blocker(new Vector3(r.center.x, 0f, r.center.y), Mathf.Min(r.width, r.height) * 0.5f) + ")");
                return false;
            }
            occ.ReserveQuad(q, tag + ".commons", TownLifeOccupancy.Block.Solids);
            return true;
        }

        /// <summary>A strip car park along X: one row of bays against the far (north) kerb, an aisle, and an entrance
        /// across the pavement to the service lane whose kerb is at <paramref name="laneEdgeZ"/>.</summary>
        void CarPark(string key, Rect r, float laneEdgeZ)
        {
            if (!Claim("car park " + key, r, "lawn")) return;
            var g = TownLifeKit.Group(root, "TL_CarPark_" + key);
            var rng = ctx.Rng("commons/carpark/" + key);
            float y = TownLifeContext.PavingY, ym = TownLifeContext.MarkingY;
            var d = new TownLifeKit.MeshData();
            FlatRect(d, "commons.asphalt", r.xMin, r.xMax, r.yMin, r.yMax, y);
            float mid = r.center.x;
            FlatRect(d, "commons.asphalt", mid - 3.5f, mid + 3.5f, laneEdgeZ, r.yMin, y); // the way in
            // bays: 2.7 m wide, 5.2 m deep, nose to the north kerb
            const float bayW = 2.7f, bayD = 5.2f;
            int bays = Mathf.FloorToInt((r.width - 3f) / bayW);
            float x0 = mid - bays * bayW * 0.5f, z1 = r.yMax - 0.4f, z0 = z1 - bayD;
            for (int i = 0; i <= bays; i++) FlatRect(d, "commons.line", x0 + i * bayW - 0.06f, x0 + i * bayW + 0.06f, z0, z1, ym);
            FlatRect(d, "commons.line", x0, x0 + bays * bayW, z1 - 0.06f, z1 + 0.06f, ym);
            // arrows down the aisle, a hatched end, and the kerb line
            for (float x = r.xMin + 6f; x < r.xMax - 6f; x += 12f)
            {
                FlatRect(d, "commons.line", x - 1.4f, x + 0.6f, z0 - 2.9f, z0 - 2.7f, ym);
                d.TriOut("commons.line", new Vector3(x, ym - 10f, z0 - 2.8f), new Vector3(x + 0.6f, ym, z0 - 3.3f), new Vector3(x + 1.6f, ym, z0 - 2.8f), new Vector3(x + 0.6f, ym, z0 - 2.3f));
            }
            for (int i = 0; i < 4; i++) FlatRect(d, "commons.hazard", r.xMin + 0.4f + i * 0.5f, r.xMin + 0.62f + i * 0.5f, z0, z1, ym);
            TownLifeKit.Custom(g, "commons_carpark_" + key, d);

            string[] models = { "SM_car_town_01", "SM_car_town_02", "SM_car_town_03", "SM_car_town_04" };
            for (int i = 0; i < bays; i++)
            {
                var nose = new Vector3(x0 + (i + 0.5f) * bayW, 0f, z1 - 0.55f);
                TownLifeKit.Spawn(g, "commons.wheelstop", G(nose) + Vector3.up * (y - TownLifeContext.GrassY), 0f);
                if (rng.NextDouble() > 0.42) continue;
                var at = new Vector3(nose.x + R(rng, -0.15f, 0.15f), y, z1 - 2.9f + R(rng, -0.2f, 0.2f));
                var car = TownLifeKit.SpawnPrefab(g, "Assets/Suburbs Town Pack/PREFABS/Cars/" + TownLifeContext.Pick(rng, models), at, (rng.Next(5) == 0 ? 180f : 0f) + R(rng, -4f, 4f), 1f, true);
                if (car != null) cars++;
            }
            // lamps and trees along the south edge, a P sign at the way in, a cart corral for the shops
            for (float x = r.xMin + 4f; x <= r.xMax - 4f; x += 14f)
            {
                var p = new Vector3(x, 0f, r.yMin + 0.9f);
                if (Mathf.Abs(x - mid) < 6f) continue;
                TownLifeKit.Spawn(g, ((int)(x / 14f) % 2 == 0) ? "kit.lamp.park" : "commons.tree.a", G(p) + Vector3.up * (y - TownLifeContext.GrassY), R(rng, 0f, 360f));
                trees++;
            }
            TownLifeKit.Spawn(g, "commons.psign", G(new Vector3(mid + 4.4f, 0f, r.yMin - 0.6f)), 180f);
            TownLifeKit.Spawn(g, "commons.cartcorral", G(new Vector3(r.xMax - 2.2f, 0f, r.yMin + 2.4f)) + Vector3.up * (y - TownLifeContext.GrassY), 90f);
            furniture += 2;
        }

        // ------------------------------------------------------------------------------------------ food court

        void FoodCourt(Rect r)
        {
            if (!Claim("food court", r, "lawn")) return;
            var g = TownLifeKit.Group(root, "TL_FoodCourt");
            var rng = ctx.Rng("commons/foodcourt");
            float y = TownLifeContext.PavingY, ym = TownLifeContext.MarkingY;
            var d = new TownLifeKit.MeshData();
            // a chequered plaza with a border
            FlatRect(d, "commons.concrete.dark", r.xMin, r.xMax, r.yMin, r.yMax, y);
            const float tile = 3f;
            int nx = Mathf.FloorToInt((r.width - 1.2f) / tile), nz = Mathf.FloorToInt((r.height - 1.2f) / tile);
            float ox = r.center.x - nx * tile * 0.5f, oz = r.center.y - nz * tile * 0.5f;
            for (int ix = 0; ix < nx; ix++)
                for (int iz = 0; iz < nz; iz++)
                    FlatRect(d, (ix + iz) % 2 == 0 ? "commons.pave.a" : "commons.pave.b", ox + ix * tile + 0.04f, ox + (ix + 1) * tile - 0.04f, oz + iz * tile + 0.04f, oz + (iz + 1) * tile - 0.04f, ym);
            TownLifeKit.Custom(g, "commons_foodcourt_plaza", d);
            float lift = y - TownLifeContext.GrassY;

            // two food trucks along the west side, serving hatches facing the tables
            TownLifeKit.Spawn(g, "commons.foodtruck", G(new Vector3(r.xMin + 2.6f, 0f, r.center.y + 6.5f)) + Vector3.up * lift, 0f);
            TownLifeKit.Spawn(g, "commons.foodtruck", G(new Vector3(r.xMin + 2.6f, 0f, r.center.y - 5.5f)) + Vector3.up * lift, 180f);
            TownLifeKit.SignBoard(g, "commons_foodtruck_a", G(new Vector3(r.xMin + 2.6f, 0f, r.center.y + 6f)) + Vector3.up * (lift + 3.05f) + Vector3.right * 0.82f, 90f, new Vector3(2.4f, 0.42f, 0.04f), "paint.orange", "TACO? TACO.", Color.white);
            TownLifeKit.SignBoard(g, "commons_foodtruck_b", G(new Vector3(r.xMin + 2.6f, 0f, r.center.y - 5f)) + Vector3.up * (lift + 3.05f) + Vector3.right * 0.82f, 90f, new Vector3(2.4f, 0.42f, 0.04f), "paint.orange", "SOUP OF THE DAY: YES", Color.white);
            signs += 2;
            string[] colours = { "red", "yellow", "teal", "orange" };
            int k = 0;
            for (float x = r.xMin + 8.5f; x <= r.xMax - 3f; x += 4.4f)
                for (float z = r.yMin + 4f; z <= r.yMax - 4f; z += 4.6f)
                {
                    TownLifeKit.Spawn(g, "commons.cafe." + colours[k++ % colours.Length], G(new Vector3(x + R(rng, -0.4f, 0.4f), 0f, z + R(rng, -0.4f, 0.4f))) + Vector3.up * lift, R(rng, 0f, 360f));
                    furniture++;
                }
            StringLights(g, "foodcourt", new[]
            {
                new Vector3(r.xMin + 6.2f, 0f, r.yMin + 1.2f), new Vector3(r.xMax - 1.2f, 0f, r.yMin + 1.2f),
                new Vector3(r.xMax - 1.2f, 0f, r.yMax - 1.2f), new Vector3(r.xMin + 6.2f, 0f, r.yMax - 1.2f),
            }, true);
            foreach (var corner in new[] { new Vector3(r.xMax - 1.6f, 0f, r.yMin + 3f), new Vector3(r.xMax - 1.6f, 0f, r.yMax - 3f), new Vector3(r.xMin + 7f, 0f, r.center.y) })
            {
                TownLifeKit.Spawn(g, "commons.planter.box", G(corner) + Vector3.up * lift, 90f);
                TownLifeKit.Spawn(g, "kit.bin.park", G(corner + new Vector3(0f, 0f, 1.5f)) + Vector3.up * lift, 0f);
                furniture += 2;
            }
            TownLifeKit.Spawn(g, "commons.pylon", G(new Vector3(r.xMax + 1.4f, 0f, r.yMin - 0.2f)), 90f);
            TownLifeKit.SignBoard(g, "commons_foodcourt_sign", G(new Vector3(r.xMax + 1.4f, 0f, r.yMin - 0.2f)) + Vector3.up * 3.75f, 90f, new Vector3(2.9f, 1.5f, 0.14f), "paint.orange", "LAST BITE\nFOOD COURT", Color.white, true);
            signs++;
        }

        // ------------------------------------------------------------------------------------------ freight yard

        void FreightYard(Rect r)
        {
            if (!Claim("freight yard", r, "lawn")) return;
            var g = TownLifeKit.Group(root, "TL_FreightYard");
            var rng = ctx.Rng("commons/freight");
            float y = TownLifeContext.PavingY, ym = TownLifeContext.MarkingY, lift = y - TownLifeContext.GrassY;
            var d = new TownLifeKit.MeshData();
            FlatRect(d, "commons.asphalt", r.xMin, r.xMax, r.yMin, r.yMax, y);
            // loading bays painted along the north side
            for (int i = 0; i < 4; i++)
            {
                float x = r.xMin + 3f + i * 5.2f;
                FlatRect(d, "commons.hazard", x, x + 0.14f, r.yMax - 9f, r.yMax - 1f, ym);
                FlatRect(d, "commons.hazard", x + 4.2f, x + 4.34f, r.yMax - 9f, r.yMax - 1f, ym);
                for (int s = 0; s < 6; s++) FlatRect(d, "commons.hazard", x + 0.4f, x + 3.9f, r.yMax - 1.6f - s * 0.5f, r.yMax - 1.45f - s * 0.5f, ym);
            }
            TownLifeKit.Custom(g, "commons_freight_yard", d);
            // three containers (two side by side, one across them on top), pallets, crates, a forklift
            Vector3 c0 = new Vector3(r.xMax - 4.5f, 0f, r.yMin + 5f);
            TownLifeKit.Spawn(g, "commons.container.red", G(c0) + Vector3.up * lift, 90f);
            TownLifeKit.Spawn(g, "commons.container.blue", G(c0 + new Vector3(0f, 0f, 2.7f)) + Vector3.up * lift, 90f);
            TownLifeKit.Spawn(g, "commons.container.green", G(c0 + new Vector3(0.4f, 0f, 1.3f)) + Vector3.up * (lift + 2.6f), 84f);
            TownLifeKit.Spawn(g, "commons.forklift", G(new Vector3(r.center.x - 2f, 0f, r.center.y - 4f)) + Vector3.up * lift, 38f);
            for (int i = 0; i < 7; i++)
            {
                var p = new Vector3(r.xMin + 2.2f + (i % 4) * 2.1f + R(rng, -0.2f, 0.2f), 0f, r.yMin + 2f + (i / 4) * 2f + R(rng, -0.2f, 0.2f));
                TownLifeKit.Spawn(g, i % 3 == 2 ? "commons.crates" : "commons.pallets", G(p) + Vector3.up * lift, R(rng, -20f, 20f));
                clutter++;
            }
            // a low wall on three sides, open to Loading Lane on the north
            TownLifeKit.Run(g, "commons_freight_wall", new List<Vector3>
            {
                new Vector3(r.xMin - 0.3f, 0f, r.yMax), new Vector3(r.xMin - 0.3f, 0f, r.yMin - 0.3f),
                new Vector3(r.xMax + 0.3f, 0f, r.yMin - 0.3f), new Vector3(r.xMax + 0.3f, 0f, r.yMax),
            }, new TownLifeKit.RunSpec { panel = "kit.panel.wall.brick", panelLength = 2f, post = "kit.post.stone", collider = TownLifeKit.RunCollider.Box, colliderHeight = 1f, colliderThickness = 0.3f, y = TownLifeContext.GrassY });
            TownLifeKit.Spawn(g, "commons.pylon", G(new Vector3(r.xMin + 1.8f, 0f, r.yMax - 0.6f)), 0f);
            TownLifeKit.SignBoard(g, "commons_freight_sign", G(new Vector3(r.xMin + 1.8f, 0f, r.yMax - 0.6f)) + Vector3.up * 3.75f, 0f, new Vector3(2.9f, 1.5f, 0.14f), "commons.box.blue", "COUPON\nFREIGHT", Color.white, true);
            signs++;
        }

        // ------------------------------------------------------------------------------------------ the streets

        void Street(string id)
        {
            var road = CoastalCityLayout.Road(id);
            if (road == null) { skipped.Add("road " + id); return; }
            var g = TownLifeKit.Group(root, "TL_Commons_" + TownLifeKit.Sanitize(id));
            float length = road.Length, hw = road.halfWidth;
            bool busy = id == "shopping" || id == "commercial-north";
            string[] treeIds = { "commons.tree.a", "commons.tree.b", "commons.tree.c" };
            string tree = treeIds[(int)((uint)TownLifeContext.StableHash(id) % 3u)];

            // Behind the pavement: a tree every 16 m, with a planter, a bench or a bin between them
            for (int side = -1; side <= 1; side += 2)
            {
                int i = 0;
                foreach (float s in TownLifeGeo.Stations(length, 8f, 9f, 9f))
                {
                    var rng = ctx.Rng("commons/street/" + id + "/" + side + "/" + i);
                    var c = TownLifeGeo.PointAt(road.points, s, out var t);
                    var p = c + TownLifeGeo.Right(t) * side * (hw + 3.75f);
                    float yaw = TownLifeGeo.Yaw(TownLifeGeo.Right(t) * -side);
                    if (i % 2 == 0)
                    {
                        if (occ.TryReserve(p, 0.6f, TownLifeTags.Tree + ".commons")) { TownLifeKit.Spawn(g, tree, G(p), R(rng, 0f, 360f), R(rng, 0.9f, 1.12f)); trees++; }
                    }
                    else
                    {
                        string prop = (i / 2) % 3 == 0 ? "commons.planter.box" : (i / 2) % 3 == 1 ? "kit.bench.park" : "kit.bin.park";
                        if (Put(g, prop, p, prop == "commons.planter.box" ? yaw + 90f : yaw, 0.7f, TownLifeTags.Furniture) != null) furniture++;
                    }
                    i++;
                }
            }
            // On the pavement's kerb edge: meters on the shopping streets, the odd hydrant and news box (no colliders)
            float kerbY = TownLifeContext.HolderY;
            for (int side = -1; side <= 1; side += 2)
            {
                int i = 0;
                foreach (float s in TownLifeGeo.Stations(length, 6f, 16f, 16f))
                {
                    var c = TownLifeGeo.PointAt(road.points, s, out var t);
                    var p = At(c + TownLifeGeo.Right(t) * side * (hw + 0.55f), kerbY);
                    float yaw = TownLifeGeo.Yaw(TownLifeGeo.Right(t) * -side);
                    bool nearJunction = occ.Within(p, 0.1f, "junction");
                    i++;
                    if (nearJunction) continue;
                    if (busy) { TownLifeKit.Spawn(g, "commons.meter", p, yaw); furniture++; }
                    if (i % 9 == 4) { TownLifeKit.Spawn(g, "commons.hydrant", At(p + TownLifeGeo.Dir(TownLifeGeo.Yaw(t)) * 1.6f, kerbY), yaw); furniture++; }
                    if (busy && i % 11 == 7) { TownLifeKit.Spawn(g, "commons.newsbox", At(p + TownLifeGeo.Right(t) * side * 0.5f + TownLifeGeo.Dir(TownLifeGeo.Yaw(t)) * 2.4f, kerbY), yaw); furniture++; }
                }
            }
            // On the asphalt: parking bays along both kerbs of the shopping streets, a zebra before each junction
            var marks = new TownLifeKit.MeshData();
            float ym = TownLifeContext.AsphaltY + 0.035f;
            if (busy)
                foreach (float s in TownLifeGeo.Stations(length, 6f, 17f, 17f))
                {
                    var c = TownLifeGeo.PointAt(road.points, s, out var t);
                    if (occ.Within(c, 4f, "junction")) continue;
                    Vector3 u = TownLifeGeo.Dir(TownLifeGeo.Yaw(t)), v = TownLifeGeo.Right(t);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Mark(marks, c + v * side * (hw - 1.3f), u, v, 0.06f, 1.1f, ym);
                        Mark(marks, c + v * side * (hw - 2.35f), u, v, 0.5f, 0.06f, ym);
                    }
                }
            foreach (float s in new[] { 13.5f, length - 13.5f })
            {
                if (length < 40f) break;
                var c = TownLifeGeo.PointAt(road.points, s, out var t);
                Vector3 u = TownLifeGeo.Dir(TownLifeGeo.Yaw(t)), v = TownLifeGeo.Right(t);
                for (float x = -hw + 0.7f; x <= hw - 0.7f; x += 1.1f) Mark(marks, c + v * x, u, v, 1.5f, 0.28f, ym);
            }
            if (marks.vertices.Count > 0) TownLifeKit.Custom(g, "commons_marks_" + TownLifeKit.Sanitize(id), marks);

            // One bus shelter on each shopping street
            if (busy)
                foreach (float at in new[] { 0.44f, 0.5f, 0.56f, 0.38f, 0.62f, 0.3f, 0.7f })
                {
                    var c = TownLifeGeo.PointAt(road.points, length * at, out var t);
                    var p = c + TownLifeGeo.Right(t) * (hw + 4f);
                    if (!occ.TryReserve(p, 0.95f, TownLifeTags.Structure + ".commons")) continue;
                    float yaw = TownLifeGeo.Yaw(-TownLifeGeo.Right(t));
                    TownLifeKit.Spawn(g, "commons.busshelter", G(p), yaw);
                    TownLifeKit.SignBoard(g, "commons_bus_" + TownLifeKit.Sanitize(id), G(p) + Vector3.up * 2.75f + TownLifeGeo.Dir(yaw) * 0.76f, yaw, new Vector3(2.4f, 0.4f, 0.04f), "paint.teal",
                        id == "shopping" ? "COUPON ST · BUS 7 (EVENTUALLY)" : "HIGH ST · BUS 7 (ALLEGEDLY)", Color.white);
                    signs++;
                    break;
                }
        }

        /// <summary>A painted rectangle on the road: half-length along u, half-width along v.</summary>
        static void Mark(TownLifeKit.MeshData d, Vector3 c, Vector3 u, Vector3 v, float halfU, float halfV, float y)
        {
            d.QuadOut("commons.line", At(c, y - 10f), At(c - u * halfU - v * halfV, y), At(c + u * halfU - v * halfV, y), At(c + u * halfU + v * halfV, y), At(c - u * halfU + v * halfV, y));
        }

        /// <summary>Bunting across Coupon Street between tall poles, four times along its length.</summary>
        void Bunting()
        {
            var road = CoastalCityLayout.Road("shopping");
            if (road == null) return;
            var g = TownLifeKit.Group(root, "TL_Commons_Bunting");
            foreach (float u in new[] { 0.14f, 0.3f, 0.47f, 0.63f, 0.86f })
            {
                var c = TownLifeGeo.PointAt(road.points, road.Length * u, out var t);
                Vector3 a = c + TownLifeGeo.Right(t) * (road.halfWidth + 3.5f), b = c - TownLifeGeo.Right(t) * (road.halfWidth + 3.5f);
                if (!occ.Free(a, 0.3f) || !occ.Free(b, 0.3f)) { a += TownLifeGeo.Dir(TownLifeGeo.Yaw(t)) * 2f; b += TownLifeGeo.Dir(TownLifeGeo.Yaw(t)) * 2f; }
                if (!occ.TryReserve(a, 0.3f, TownLifeTags.Furniture + ".commons") || !occ.TryReserve(b, 0.3f, TownLifeTags.Furniture + ".commons")) continue;
                TownLifeKit.Spawn(g, "commons.pole", G(a), 0f);
                TownLifeKit.Spawn(g, "commons.pole", G(b), 0f);
                Vector3 top = G(a) + Vector3.up * 6.45f;
                TownLifeKit.Spawn(g, "commons.bunting", top, TownLifeGeo.Yaw(b - a), new Vector3(1f, 1f, TownLifeGeo.Dist(a, b)));
                furniture += 3;
            }
        }

        /// <summary>The leftover lawns between the blocks: shade trees wherever nothing else is within a few metres.</summary>
        void Greens()
        {
            var g = TownLifeKit.Group(root, "TL_Commons_Greens");
            string[] kinds = { "kit.tree.round.l", "kit.tree.maple.l", "kit.tree.elm.m", "kit.tree.blossom.m", "kit.tree.round.m", "kit.clump.mixed.3" };
            for (int ix = 0; ix < 27; ix++)
                for (int iz = 0; iz < 15; iz++)
                {
                    var rng = ctx.Rng("commons/greens/" + ix + "/" + iz);
                    if (rng.NextDouble() > 0.6) continue;
                    var p = new Vector3(60f + ix * 12.5f + R(rng, -4.5f, 4.5f), 0f, 368f + iz * 12.5f + R(rng, -4.5f, 4.5f));
                    string kind = TownLifeContext.Pick(rng, kinds);
                    if (!occ.TryReserve(p, kind.Contains("clump") ? 5.5f : 3.4f, TownLifeTags.Tree + ".commons")) continue;
                    TownLifeKit.Spawn(g, kind, G(p), R(rng, 0f, 360f), R(rng, 0.9f, 1.2f));
                    trees++;
                    if (rng.Next(3) == 0 && occ.TryReserve(p + new Vector3(2.6f, 0f, 1.4f), 0.9f, TownLifeTags.Shrub + ".commons"))
                        TownLifeKit.Spawn(g, rng.Next(2) == 0 ? "kit.shrub.round.m" : "kit.shrub.flower.pink", G(p + new Vector3(2.6f, 0f, 1.4f)), R(rng, 0f, 360f));
                }
        }

        // ------------------------------------------------------------------------------------------ decorative shop rows

        /// <summary>Back-door clutter and a shopfront display for each bay of a decorative shop row. Renderers only:
        /// an infill footprint rejects any collider inside it.</summary>
        void ShopRow(CoastalInfillSite site)
        {
            if (site == null || site.kind != "storefront") return;
            Vector3 o = site.transform.position, into = Flat(site.transform.forward), right = Flat(site.transform.right);
            if (o.z < 340f || o.z > 560f || o.x > 396f) return;
            var g = TownLifeKit.Group(root, "TL_Row_" + TownLifeKit.Sanitize(site.siteId));
            var rng = ctx.Rng("commons/row/" + site.siteId);
            int bays = Mathf.Max(1, Mathf.RoundToInt(site.width / 11f));
            float bayWidth = (site.width - 0.5f) / bays, front = Mathf.Min(3f, site.depth * 0.19f);
            float yawOut = TownLifeGeo.Yaw(-into), yawBack = TownLifeGeo.Yaw(into);
            string[] back = { "commons.crates", "commons.pallets", "commons.acunit", "commons.boxes" };
            string[] sandwich = { "commons.sandwich.red", "commons.sandwich.yellow", "commons.sandwich.teal" };
            for (int i = 0; i < bays; i++)
            {
                float x = -site.width * 0.5f + 0.25f + bayWidth * (i + 0.5f);
                // out the back: beside the delivery door, on the bare strip
                Vector3 rear = o + right * (x + R(rng, 1.6f, 2.6f) * (rng.Next(2) == 0 ? 1f : -1f)) + into * (site.depth - R(rng, 0.9f, 1.4f));
                TownLifeKit.Spawn(g, TownLifeContext.Pick(rng, back), G(rear) + Vector3.up * 0.04f, yawBack + R(rng, -12f, 12f));
                // out the front: a board or a display table on the apron, clear of the door in the middle of the bay
                Vector3 apron = o + right * (x + (rng.Next(2) == 0 ? 2.6f : -2.6f)) + into * (front * 0.45f);
                TownLifeKit.Spawn(g, rng.Next(3) == 0 ? "commons.display" : TownLifeContext.Pick(rng, sandwich), G(apron) + Vector3.up * 0.1f, yawOut + R(rng, -18f, 18f));
                clutter += 2;
            }
        }
    }
}
