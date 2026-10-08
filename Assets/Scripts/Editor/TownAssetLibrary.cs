using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Curated, persistent palette. Built-in IDs remain stable across refreshes, and user-added
/// prefab entries are never removed by rebuilding the shipped collection.</summary>
public static class TownAssetLibrary
{
    public const string BuiltInPrefix = "builtin/";
    public static readonly string[] Categories = {
        "Trees & greenery", "Houses", "Shops & buildings", "Cars", "Street furniture",
        "Parks", "Industrial", "Ground & boundaries", "Custom"
    };
    const string Pack = "Assets/Suburbs Town Pack/PREFABS/";
    static TownAssetPalette cached;
    // The render pipeline's light settings are visual metadata, not game logic. Match the exact official
    // runtime type: unrelated MonoBehaviours (and subclasses) do not inherit a blanket exemption.
    static readonly Type AdditionalLightDataType = Type.GetType(
        "UnityEngine.Rendering.Universal.UniversalAdditionalLightData, Unity.RenderPipelines.Universal.Runtime", false);
    static readonly Type[] RuntimeDriverTypes = {
        typeof(Camera), typeof(AudioListener), typeof(AudioSource), typeof(Animator), typeof(Animation),
        typeof(ParticleSystem), typeof(UnityEngine.Playables.PlayableDirector),
        typeof(UnityEngine.AI.NavMeshAgent), typeof(UnityEngine.AI.NavMeshObstacle), typeof(WindZone)
    };

    public static TownAssetPalette Ensure()
    {
        if (cached && cached.entries != null && cached.entries.Count > 0) return cached;
        TownAssetKitBuilder.EnsureBuiltIns();
        cached = AssetDatabase.LoadAssetAtPath<TownAssetPalette>(TownAssetPalette.AssetPath);
        if (!cached)
        {
            TownAssetKitBuilder.Folder("Assets/Editor/TownAuthoring");
            cached = ScriptableObject.CreateInstance<TownAssetPalette>();
            AssetDatabase.CreateAsset(cached, TownAssetPalette.AssetPath);
        }
        if (cached.entries == null) cached.entries = new List<TownAssetEntry>();
        // Preserve customisation of an existing entry on ordinary window open. Explicit rebuild
        // restores shipped entries, but still leaves every custom entry intact.
        bool changed = false;
        foreach (TownAssetEntry entry in BuiltIns())
        {
            if (cached.entries.Any(e => e != null && e.id == entry.id)) continue;
            cached.entries.Add(entry); changed = true;
        }
        if (changed) { EditorUtility.SetDirty(cached); AssetDatabase.SaveAssetIfDirty(cached); }
        return cached;
    }

    public static TownAssetPalette RebuildBuiltIns()
    {
        TownAssetKitBuilder.EnsureBuiltIns(true);
        TownAssetPalette palette = Ensure();
        var custom = palette.entries.Where(e => e != null && !(e.id ?? "").StartsWith(BuiltInPrefix, StringComparison.Ordinal)).ToList();
        palette.entries = BuiltIns().Concat(custom).ToList();
        EditorUtility.SetDirty(palette); AssetDatabase.SaveAssetIfDirty(palette); return palette;
    }

    static IEnumerable<TownAssetEntry> BuiltIns()
    {
        var entries = new List<TownAssetEntry>();
        Action<string,string,string,string,TownAssetRole,float> add = (folder, name, label, category, role, spacing) => {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Pack + folder + "/" + name + ".prefab");
            if (!prefab) { Debug.LogWarning("Town palette skipped missing prefab " + name); return; }
            entries.Add(new TownAssetEntry {
                id = BuiltInPrefix + name, displayName = label, category = category, prefab = prefab,
                role = role, spacing = spacing, defaultScale = 1, randomYaw = category == "Trees & greenery",
                houseModel = role == TownAssetRole.House ? name : "",
                description = role == TownAssetRole.House ? "Working household: complete property, curb collection, resident and garbage rules. Snaps to a street."
                    : role == TownAssetRole.ParkedCar ? "Parked vehicle with solid collision geometry. Not a traffic spawn or a knockable vehicle."
                    : role == TownAssetRole.StreetProp ? "Street prop: uses the town's physical prop interaction. Does not create garbage by itself."
                    : "Decorative scenery. Does not create garbage, residents or traffic."
            });
        };
        string[] houseNames = { "Cozy bungalow", "Porch bungalow", "Garage family home", "Wide family home", "Tall gabled home", "Grand suburban home", "Double-gabled estate" };
        for (int i=1;i<=7;i++) add("Buildings", "SM_house_suburb_"+i.ToString("000"), houseNames[i-1], "Houses", TownAssetRole.House, i<=2?18:i<=5?24:31);
        for (int i=1;i<=3;i++) add("Nature","SM_tree_town_"+i.ToString("00"),new[]{"Rounded shade tree","Spreading street tree","Tall garden tree"}[i-1],"Trees & greenery",TownAssetRole.Decoration,6);
        add("Nature","SM_bush_02","Rounded shrub","Trees & greenery",TownAssetRole.Decoration,2);
        add("Nature","SM_bush_03","Low hedge shrub","Trees & greenery",TownAssetRole.Decoration,2);
        for (int i=1;i<=4;i++) add("Cars","SM_car_town_"+i.ToString("00"),"Parked car · model "+i,"Cars",TownAssetRole.ParkedCar,5.5f);
        for (int i=1;i<=3;i++) add("Props","SM_streetlamp_"+i.ToString("00"),"Street lamp · "+new[]{"classic","modern","curved"}[i-1],"Street furniture",TownAssetRole.StreetProp,8);
        for (int i=1;i<=2;i++) add("Props","SM_mailbox_"+i.ToString("00"),"Mailbox · "+i,"Street furniture",TownAssetRole.StreetProp,2);
        for (int i=1;i<=6;i++) add("Props","SM_garbagecan_"+i.ToString("00"),"Curb bin · "+i,"Street furniture",TownAssetRole.StreetProp,1.5f);
        for (int i=1;i<=4;i++) add("Props","SM_trashbin_"+i.ToString("00"),"Public waste bin · "+i,"Street furniture",TownAssetRole.StreetProp,1.5f);
        for (int i=1;i<=11;i++) add("Signs","SM_roadsign_"+i.ToString("000"),"Road sign · "+i.ToString("00"),"Street furniture",TownAssetRole.StreetProp,2);
        for (int i=1;i<=4;i++) add("Props","SM_clothes_drying_rack_"+i.ToString("00"),"Laundry line · "+i,"Parks",TownAssetRole.Decoration,4);
        add("Props","SM_lemonade_shop","Lemonade stall","Parks",TownAssetRole.Decoration,3);
        add("Props","SM_whirlpool","Garden hot tub","Parks",TownAssetRole.Decoration,4);
        add("Props","SM_swimming_pool","Backyard swimming pool","Parks",TownAssetRole.Decoration,9);
        add("Playground","SM_climbingrack","Climbing frame","Parks",TownAssetRole.Decoration,6);
        add("Playground","SM_slide","Playground slide","Parks",TownAssetRole.Decoration,5);
        add("Playground","SM_swing","Swing frame","Parks",TownAssetRole.Decoration,6);
        add("Playground","SM_bench_playground","Park bench","Parks",TownAssetRole.Decoration,3);
        add("Playground","SM_trashbin_park","Park litter bin","Street furniture",TownAssetRole.StreetProp,2);
        for(int i=1;i<=3;i++) add("Playground","SM_sandbox_playground_"+i.ToString("00"),"Sand play area · "+i,"Parks",TownAssetRole.Decoration,5);
        for(int i=1;i<=2;i++) add("Playground","SM_playground_object_"+i.ToString("00"),"Playground feature · "+i,"Parks",TownAssetRole.Decoration,4);
        add("Modular","SM_fence_01","Low garden fence","Ground & boundaries",TownAssetRole.Decoration,3);
        add("Modular","SM_fence_02","Tall garden fence","Ground & boundaries",TownAssetRole.Decoration,3);
        add("Modular","SM_garden_fence","Short picket section","Ground & boundaries",TownAssetRole.Decoration,3);
        add("Modular","SM_garden_fence_long","Long picket section","Ground & boundaries",TownAssetRole.Decoration,6);
        add("Modular","SM_garden_door","Garden gate (decorative)","Ground & boundaries",TownAssetRole.Decoration,2);
        add("Modular","SM_path_garden","Garden stepping path","Ground & boundaries",TownAssetRole.Decoration,3);
        for(int i=1;i<=5;i++) add("Modular","SM_pedestrian_walk_suburbs_"+i.ToString("00"),"Pavement trim · "+i,"Ground & boundaries",TownAssetRole.Decoration,3);
        add("Modular","SM_pedestrian_crossing","Crosswalk marking (visual)","Ground & boundaries",TownAssetRole.Decoration,5);
        add("Modular","SM_solarpane","Roof solar panels","Industrial",TownAssetRole.Decoration,3);
        for(int i=1;i<=2;i++) add("Modular","SM_antenna_"+i.ToString("00"),"Roof antenna · "+i,"Industrial",TownAssetRole.Decoration,2);

        Kit(entries,"corner-grocer","Corner grocery","Shops & buildings",14,"Detailed two-storey grocer with framed displays, striped awning, roof trim and delivery crates.","shop");
        Kit(entries,"corner-cafe","Sidewalk cafe","Shops & buildings",15,"Peach corner cafe with bistro tables, chairs, deep awning and planted terrace.","restaurant");
        Kit(entries,"bakery","Neighborhood bakery","Shops & buildings",14,"Cream bakery with coral awning, display windows, chimney and entrance planter.","restaurant");
        Kit(entries,"courtyard-apartment","Balcony apartment","Shops & buildings",20,"Four-storey brick apartment: projecting balconies, individual rails, cornices and rooftop stair house.","tower");
        Kit(entries,"workshop","Repair workshop","Industrial",17,"Small pitched-roof workshop with roller doors, clerestory windows, drainpipes and safety bollards.","shop");
        Kit(entries,"warehouse","Loading warehouse","Industrial",23,"Large loading hall with raised gable roof, vent equipment, slatted bay doors and pallet crates.","shop");
        Kit(entries,"bus-shelter","Bus stop shelter","Street furniture",8,"Three-dimensional shelter, tinted glass, bench, timetable and stop pole. Scenic stop; does not add bus AI.");
        Kit(entries,"pocket-park","Pocket park kit","Parks",15,"Complete walk-through park: crossing paths, mature trees, planted bed, benches and rear fence. Entry remains open.");
        Kit(entries,"wooded-grove","Wooded grove kit","Trees & greenery",15,"Five varied faceted trees, stepping stones, quiet bench and boulder. Leave walking space between groves.");
        Kit(entries,"service-alley","Service alley kit","Industrial",8,"Coherent paved service nook with masonry boundary, dumpster, stacked delivery crates and drain.");
        Kit(entries,"raised-planter","Flowering street planter","Trees & greenery",4.5f,"Raised stone planter with inset soil, dimensional shrubs and warm-color flowers.");
        Kit(entries,"bicycle-parking","Bicycle rack cluster","Street furniture",6,"Four individual U-racks mounted to a slab; retains open space between racks.");
        Kit(entries,"picket-fence","Picket fence · 6 m","Ground & boundaries",6,"Connecting 6 m boundary with individual pointed pickets, rails, posts and caps.");
        Kit(entries,"brick-wall","Brick garden wall · 6 m","Ground & boundaries",6,"Low wall with staggered mortar, end piers and projecting stone coping.");
        Kit(entries,"hedge-border","Clipped hedge · 6 m","Ground & boundaries",6,"Solid green boundary with low-poly rounded crowns rather than a single flat plane.");
        Kit(entries,"garden-gateway","Open garden arch","Ground & boundaries",8,"Fence wings and trellised garden arch; clear central walk-through is deliberately unobstructed.");
        Kit(entries,"parking-bay","Two parking bays","Cars",8,"Two marked spaces, wheel stops, reflectors and a planted end border. Place parked cars separately.");
        Kit(entries,"utility-corner","Utility cabinet cluster","Industrial",6,"Transformer, louvres, service door, warning plate, communications box and protective bollards.");
        Kit(entries,"market-stall","Produce market stall","Shops & buildings",8,"Wood-framed stall, pitched cloth canopy, counter, produce trays and delivery crates. Scenic, not a trading UI.");
        Kit(entries,"garden-pergola","Garden pergola","Parks",8,"Open timber structure with beams, nine rafters, seating and flowering entrance planters.");
        Kit(entries,"evergreen","Layered evergreen","Trees & greenery",5,"Six-metre conifer with three faceted canopy tiers and a physical trunk.");
        Kit(entries,"flower-border","Long flower border","Trees & greenery",6,"Long planted border with modeled leaves, flowers, soil and a stone rim.");
        Kit(entries,"bollard-row","Safety bollards","Street furniture",6,"Four individually colliding yellow bollards with reflective bands and octagonal footings.");
        Kit(entries,"small-fountain","Octagonal fountain","Parks",7,"Stepped octagonal basin, water, pedestal and upper bowl. Decorative water does not apply slippery weather.");
        Kit(entries,"sidewalk-module","Paved sidewalk · 6 m","Ground & boundaries",6,"Seamed surface and low curb edge. Decoration only: use the street tool for navigable roads.");
        Kit(entries,"community-planters","Community garden kit","Parks",11,"Four raised beds, crossing paths, bench and notice board. A ready-made little destination between homes.");
        return entries;
    }

    static void Kit(List<TownAssetEntry> list,string id,string label,string category,float spacing,string description,string collectionType=null)
    {
        GameObject prefab=AssetDatabase.LoadAssetAtPath<GameObject>(TownAssetKitBuilder.PrefabPath(id));
        if(!prefab) { Debug.LogWarning("Town kit missing: "+id); return; }
        bool collection=!string.IsNullOrEmpty(collectionType);
        list.Add(new TownAssetEntry {
            id=BuiltInPrefix+id,displayName=label,category=category,prefab=prefab,
            role=collection?TownAssetRole.CollectionBuilding:TownAssetRole.Decoration,
            collectionType=collectionType??"",propertyFrontYaw=collection?180f:0f,defaultScale=1,spacing=spacing,
            randomYaw=category=="Trees & greenery",description=description+(collection?" Working collection stop: street snapping, curb garbage and route metadata are added automatically.":" Decorative kit; no new collection stop.")
        });
    }

    public static bool ValidateCustomPrefab(GameObject prefab,out string error)
    {
        if(!prefab || !AssetDatabase.Contains(prefab) || !PrefabUtility.IsPartOfPrefabAsset(prefab))
        { error="Choose a prefab or imported model from the Project window, not a live scene object."; return false; }
        if(prefab.transform.parent)
        { error="Choose the prefab root, not an individual child."; return false; }
        return ValidateVisualContents(prefab,out error);
    }

    /// <summary>Content-only safety check, also usable on an isolated prefab preview. Assets added to the
    /// palette must stay static scenery; the placement backend adds approved gameplay behaviours itself.</summary>
    public static bool ValidateVisualContents(GameObject prefab,out string error)
    {
        if(!prefab) { error="Choose a visible prefab."; return false; }
        // These native engine components are not MonoBehaviours. Without an explicit check a prefab
        // could introduce an extra game camera/listener, autoplay sound or start moving after publication.
        // Check disabled components too: static scenery must not contain latent runtime drivers.
        foreach(var type in RuntimeDriverTypes)
            if(prefab.GetComponentInChildren(type,true)!=null)
            { error="Custom scenery cannot contain the runtime driver " + type.Name + ". Remove cameras, audio, animation, particles and navigation drivers; static meshes, colliders and lights are supported."; return false; }
        foreach(var behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            if(!behaviour || behaviour.GetType()!=AdditionalLightDataType || behaviour.GetComponent<Light>()==null)
            { error="Custom scenery must be a visual-only prefab with no gameplay scripts. URP light metadata is allowed; use a palette House or collection building for automatic game integration."; return false; }
        if(prefab.GetComponentsInChildren<Rigidbody>(true).Length>0 || prefab.GetComponentsInChildren<Rigidbody2D>(true).Length>0 ||
            prefab.GetComponentsInChildren<ArticulationBody>(true).Length>0 || prefab.GetComponentsInChildren<Joint>(true).Length>0 ||
            prefab.GetComponentsInChildren<Joint2D>(true).Length>0 || prefab.GetComponentsInChildren<CharacterController>(true).Length>0 ||
            prefab.GetComponentsInChildren<WheelCollider>(true).Length>0 || prefab.GetComponentsInChildren<Cloth>(true).Length>0)
        { error="Custom scenery must not contain rigidbodies, character controllers or other simulated physics. Static colliders are supported."; return false; }
        Renderer[] renderers=prefab.GetComponentsInChildren<Renderer>(true);
        if(renderers.Length==0) { error="This prefab has no visible renderers."; return false; }
        foreach(Renderer renderer in renderers)
        {
            if(renderer.sharedMaterials.Length==0)
            { error="The prefab has a renderer with no material. Assign URP-compatible materials first."; return false; }
            foreach(Material material in renderer.sharedMaterials)
                if(!material || !material.shader || !material.shader.isSupported || material.shader.name.Contains("InternalErrorShader"))
                { error="The prefab has a missing or error material. Assign URP-compatible materials first."; return false; }
        }
        error=null; return true;
    }

    public static bool TryAddCustom(TownAssetPalette palette,GameObject prefab,string displayName,out TownAssetEntry entry,out string error)
    {
        entry=null;
        if(!ValidateCustomPrefab(prefab,out error)) return false;
        if(!palette) { error="No town palette is loaded."; return false; }
        string id="custom/"+AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
        entry=palette.entries.FirstOrDefault(e=>e!=null&&e.id==id);
        if(entry!=null) { error="This prefab is already in the palette."; return false; }
        Undo.RecordObject(palette,"Add custom town prefab");
        entry=new TownAssetEntry {
            id=id,displayName=string.IsNullOrWhiteSpace(displayName)?prefab.name:displayName.Trim(),
            category="Custom",prefab=prefab,role=TownAssetRole.Decoration,defaultScale=1,spacing=4,randomYaw=false,
            description="Custom static scenery. Its colliders are preserved; no residents, trash or network identities are added."
        };
        palette.entries.Add(entry); EditorUtility.SetDirty(palette); AssetDatabase.SaveAssetIfDirty(palette); error=null; return true;
    }
}
