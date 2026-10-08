using System.Collections.Generic;
using UnityEngine;

/// Bollards with a flower tub between them along the west side of Elm Avenue.
public static class TownLifeExample
{
    public const string Name = "example";

    public static void Build(TownLifeContext ctx)
    {
        if (!ctx.options.District(TownLifeScene.Acres)) return;
        var road = ctx.scene.Road("elm"); if (road == null) { ctx.Error(Name, "no elm"); return; }

        // 1. palette + props (module-prefixed ids; stock colours need no Define)
        TownLifeKit.Define("example.petal", new Color(1f, 0.45f, 0.6f));
        TownLifeKit.DefineProp("example.bollard")
            .Cyl("stone.dark", Vector3.zero, 0.14f, 0.9f, 8)
            .Dome("paint.white", new Vector3(0f, 0.9f, 0f), new Vector3(0.16f, 0.12f, 0.16f))
            .ColliderBox(new Vector3(0f, 0.45f, 0f), new Vector3(0.3f, 0.9f, 0.3f))
            .NoShadows();
        TownLifeKit.DefineProp("example.tub")
            .Cyl("wood.warm", Vector3.zero, 0.5f, 0.45f, 8, 0.6f)
            .Blob("example.petal", new Vector3(0f, 0.6f, 0f), new Vector3(0.55f, 0.3f, 0.55f), 3)
            .NoShadows();

        // 2. walk the road: left side (-1), 1.2 m behind the sidewalk, every ~10 m, clear of the end junctions
        var group = ctx.Group("Example"); var occ = ctx.occupancy;
        float lateral = -(road.halfWidth + 3f + 1.2f);
        int placed = 0, skipped = 0, i = 0;
        foreach (float station in TownLifeGeo.Stations(road.length, 10f, 14f, 14f))
        {
            var rng = ctx.Rng("example/elm/" + i++);                       // one RNG per feature instance
            var centre = TownLifeGeo.PointAt(road.points, station, out var tangent);
            var p = centre + TownLifeGeo.Right(tangent) * lateral;
            p.y = ctx.scene.GroundY(p);
            string prop = rng.Next(3) == 0 ? "example.tub" : "example.bollard";
            float radius = prop == "example.tub" ? 0.7f : 0.3f;

            // 3. keep-outs and earlier modules: test + reserve in one call
            if (!occ.TryReserve(p, radius, TownLifeTags.Furniture + ".example")) { skipped++; continue; }
            TownLifeKit.Spawn(group, prop, p, TownLifeGeo.Yaw(tangent) + TownLifeContext.Range(rng, -8f, 8f));
            placed++;
        }

        // 4. a flat decal strip under them (Flat things may sit where solids may not)
        var strip = TownLifeGeo.Offset(road.points, lateral);
        if (occ.FreeLine(strip[0], strip[strip.Length - 1], 0.4f, TownLifeOccupancy.What.Flat))
        {
            TownLifeKit.Ribbon(group, "example_strip_elm", strip, 0.8f, "gravel", TownLifeContext.SoilY);
            occ.ReservePolyline(strip, 0.4f, TownLifeTags.Bed + ".example", TownLifeOccupancy.Block.Marker);
        }

        // 5. report
        ctx.Log(Name, placed + " placed, " + skipped + " skipped on Elm");
    }
}
