using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Small, purpose-built town dressing kits. Ground-level pivot; frontage faces -Z.
/// Geometry is actual 3D, shared meshes/materials are persistent assets, and no gameplay identities
/// or network behaviours are smuggled into decorative prefabs.</summary>
public static class TownAssetKitBuilder
{
    public const string Root = "Assets/Prefabs/TownAuthoring";
    public static readonly string[] Ids = {
        "corner-grocer", "corner-cafe", "bakery", "courtyard-apartment", "workshop", "warehouse",
        "bus-shelter", "pocket-park", "wooded-grove", "service-alley", "raised-planter", "bicycle-parking",
        "picket-fence", "brick-wall", "hedge-border", "garden-gateway", "parking-bay", "utility-corner",
        "market-stall", "garden-pergola", "evergreen", "flower-border", "bollard-row", "small-fountain",
        "sidewalk-module", "community-planters"
    };

    static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color> {
        { "cream", new Color(.87f,.82f,.66f) }, { "mint", new Color(.45f,.66f,.54f) },
        { "peach", new Color(.79f,.52f,.39f) }, { "brick", new Color(.59f,.33f,.26f) },
        { "stone", new Color(.56f,.60f,.57f) }, { "navy", new Color(.16f,.24f,.29f) },
        { "glass", new Color(.30f,.51f,.57f) }, { "white", new Color(.91f,.90f,.79f) },
        { "wood", new Color(.44f,.31f,.20f) }, { "timber", new Color(.65f,.48f,.28f) },
        { "leaf", new Color(.31f,.49f,.26f) }, { "leaf-light", new Color(.48f,.62f,.29f) },
        { "pine", new Color(.19f,.37f,.30f) }, { "soil", new Color(.24f,.21f,.15f) },
        { "paving", new Color(.66f,.66f,.57f) }, { "asphalt", new Color(.28f,.31f,.31f) },
        { "metal", new Color(.36f,.41f,.40f) }, { "yellow", new Color(.91f,.67f,.20f) },
        { "coral", new Color(.87f,.37f,.29f) }, { "lavender", new Color(.57f,.49f,.70f) },
        { "water", new Color(.24f,.59f,.65f) }
    };
    static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

    public static string PrefabPath(string id) => Root + "/Kits/" + id + ".prefab";

    public static void EnsureBuiltIns(bool rebuild = false)
    {
        Folder(Root + "/Kits"); Folder(Root + "/Materials"); Folder(Root + "/Meshes");
        Materials.Clear();
        foreach (string id in Ids)
        {
            if (!rebuild && AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(id))) continue;
            GameObject root = new GameObject("Town · " + id);
            try
            {
                Build(root.transform, id);
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.isStatic = true;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(id));
            }
            finally { Object.DestroyImmediate(root); }
        }
        // CreateAsset/SaveAsPrefabAsset already persist this generator's new assets. Never flush an
        // unrelated dirty project asset just because the palette window was opened.
    }

    public static void Folder(string path)
    {
        string[] segments = path.Split('/'); string parent = segments[0];
        for (int i = 1; i < segments.Length; i++)
        {
            string next = parent + "/" + segments[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(parent, segments[i]);
            parent = next;
        }
    }

    static Material Mat(string key)
    {
        if (Materials.TryGetValue(key, out Material cached) && cached) return cached;
        string path = Root + "/Materials/" + key + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!material)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (!shader) throw new InvalidOperationException("Town kits need URP/Lit or Standard shader.");
            material = new Material(shader) { name = "Town " + key };
            Color color = Colors[key];
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            material.color = color;
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", key == "glass" || key == "water" ? .38f : .12f);
            AssetDatabase.CreateAsset(material, path);
        }
        Materials[key] = material; return material;
    }

    static Transform Group(Transform parent, string name, Vector3 position = default)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = position; return go.transform;
    }

    static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size, string material, bool solid = false)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
        go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = Mat(material);
        if (!solid) Object.DestroyImmediate(go.GetComponent<Collider>());
        else go.layer = 7; // Existing town's Walls layer: camera avoidance and truck/player collision.
        return go;
    }

    static GameObject Shape(Transform parent, string name, Vector3 position, Vector3 size, string material, string mesh)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.transform.localPosition = position; go.transform.localScale = size;
        go.AddComponent<MeshFilter>().sharedMesh = MeshAsset(mesh);
        go.AddComponent<MeshRenderer>().sharedMaterial = Mat(material); return go;
    }

    static Mesh MeshAsset(string kind)
    {
        string path = Root + "/Meshes/" + kind + ".asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path); if (mesh) return mesh;
        var vertices = new List<Vector3>(); var triangles = new List<int>();
        Action<Vector3, Vector3, Vector3> tri = (a,b,c) => {
            int n = vertices.Count; vertices.Add(a); vertices.Add(b); vertices.Add(c);
            triangles.Add(n); triangles.Add(n+1); triangles.Add(n+2);
        };
        Action<Vector3, Vector3, Vector3, Vector3> quad = (a,b,c,d) => { tri(a,b,c); tri(a,c,d); };
        if (kind == "gable")
        {
            Vector3 a = new Vector3(-.5f,0,-.5f), b = new Vector3(.5f,0,-.5f), c = new Vector3(0,1,-.5f);
            Vector3 d = new Vector3(-.5f,0,.5f), e = new Vector3(.5f,0,.5f), f = new Vector3(0,1,.5f);
            tri(a,c,b); tri(d,e,f); quad(a,d,f,c); quad(b,c,f,e); quad(a,b,e,d);
        }
        else
        {
            // Flat normals and eight-sided rings make recognisably faceted crowns, trunks and cones.
            float[] heights = kind == "crown" ? new[] { 0f,.25f,.72f,1f } : new[] {0f,1f};
            float[] radii = kind == "crown" ? new[] {.28f,.5f,.42f,.03f} : kind == "cone" ? new[] {.5f,0f} : new[] {.5f,.5f};
            Func<int,int,Vector3> v = (r,i) => {
                float angle = i * Mathf.PI / 4;
                return new Vector3(Mathf.Cos(angle)*radii[r], heights[r], Mathf.Sin(angle)*radii[r]);
            };
            for (int r=0; r<heights.Length-1; r++)
                for (int i=0; i<8; i++) quad(v(r,i),v(r+1,i),v(r+1,i+1),v(r,i+1));
            for (int i=0; i<8; i++)
            {
                tri(new Vector3(0,heights[0],0),v(0,i),v(0,i+1));
                int last=heights.Length-1; tri(new Vector3(0,heights[last],0),v(last,i+1),v(last,i));
            }
        }
        mesh = new Mesh { name = "Town " + kind }; mesh.SetVertices(vertices); mesh.SetTriangles(triangles,0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); AssetDatabase.CreateAsset(mesh,path); return mesh;
    }

    static void Build(Transform root, string id)
    {
        switch (id)
        {
            case "corner-grocer": Shop(root, "mint", "yellow", false); break;
            case "corner-cafe": Shop(root, "peach", "navy", true); break;
            case "bakery": Shop(root, "cream", "coral", false); break;
            case "courtyard-apartment": Apartment(root); break;
            case "workshop": Workshop(root,false); break;
            case "warehouse": Workshop(root,true); break;
            case "bus-shelter": BusShelter(root); break;
            case "pocket-park": PocketPark(root); break;
            case "wooded-grove": Grove(root); break;
            case "service-alley": Alley(root); break;
            case "raised-planter": Planter(root,Vector3.zero,3.6f,1.3f,true); break;
            case "bicycle-parking": BicycleParking(root); break;
            case "picket-fence": Fence(root,6,false); break;
            case "brick-wall": BrickWall(root,6); break;
            case "hedge-border": Hedge(root,6); break;
            case "garden-gateway": GardenGateway(root); break;
            case "parking-bay": ParkingBay(root); break;
            case "utility-corner": Utility(root); break;
            case "market-stall": Market(root); break;
            case "garden-pergola": Pergola(root); break;
            case "evergreen": Tree(root,Vector3.zero,6,true); break;
            case "flower-border": Planter(root,Vector3.zero,5,1.1f,true); break;
            case "bollard-row": for(int i=0;i<4;i++) Bollard(root,new Vector3(-2.25f+i*1.5f,0,0)); break;
            case "small-fountain": Fountain(root); break;
            case "sidewalk-module": Sidewalk(root); break;
            case "community-planters": CommunityGarden(root); break;
            default: throw new ArgumentException("Unknown town kit " + id);
        }
    }

    static void Window(Transform root, float x, float y, float z, float width, float height)
    {
        Box(root,"Recessed window",new Vector3(x,y,z),new Vector3(width,height,.12f),"glass");
        Box(root,"Window left jamb",new Vector3(x-width*.5f,y,z-.05f),new Vector3(.13f,height+.2f,.2f),"white");
        Box(root,"Window right jamb",new Vector3(x+width*.5f,y,z-.05f),new Vector3(.13f,height+.2f,.2f),"white");
        Box(root,"Window lintel",new Vector3(x,y+height*.5f,z-.05f),new Vector3(width+.26f,.13f,.2f),"white");
        Box(root,"Projecting sill",new Vector3(x,y-height*.5f,z-.12f),new Vector3(width+.35f,.15f,.38f),"white");
        Box(root,"Window mullion",new Vector3(x,y,z-.09f),new Vector3(.075f,height,.08f),"white");
    }

    static void Door(Transform root,float x,float z,string color="navy")
    {
        Box(root,"Entrance frame",new Vector3(x,1.38f,z),new Vector3(1.55f,2.76f,.19f),"white");
        Box(root,"Entrance door",new Vector3(x,1.32f,z-.12f),new Vector3(1.28f,2.64f,.12f),color);
        Box(root,"Door glass",new Vector3(x,1.8f,z-.2f),new Vector3(.94f,1.3f,.05f),"glass");
        Box(root,"Door handle",new Vector3(x+.42f,1.03f,z-.24f),new Vector3(.055f,.34f,.07f),"yellow");
        Box(root,"Entrance step",new Vector3(x,.05f,z-.36f),new Vector3(1.8f,.1f,.7f),"stone");
    }

    static void Shop(Transform root,string wall,string accent,bool cafe)
    {
        float width=cafe?9f:10f, depth=7f;
        Box(root,"Masonry shop",new Vector3(0,3.25f,0),new Vector3(width,6.5f,depth),wall,true);
        Box(root,"Stone plinth",new Vector3(0,.28f,0),new Vector3(width+.16f,.56f,depth+.16f),"stone");
        Box(root,"Cornice",new Vector3(0,6.48f,0),new Vector3(width+.32f,.26f,depth+.32f),"white");
        Box(root,"Dark recessed roof",new Vector3(0,6.66f,0),new Vector3(width-.32f,.12f,depth-.32f),"navy");
        foreach(int side in new[]{-1,1})
        {
            Box(root,"Raised roof coping",new Vector3(side*(width*.5f-.10f),6.86f,0),new Vector3(.23f,.5f,depth),"white");
            Window(root,side*width*.30f,1.70f,-depth*.5f-.07f,2.5f,2.15f);
            Window(root,side*width*.29f,5.12f,-depth*.5f-.07f,1.5f,1.55f);
            Box(root,"Corner quoin",new Vector3(side*(width*.5f-.2f),3.05f,-depth*.5f-.06f),new Vector3(.36f,6.1f,.2f),"white");
        }
        Door(root,0,-depth*.5f-.1f);
        Box(root,"Fascia sign board",new Vector3(0,3.88f,-depth*.5f-.18f),new Vector3(width-.65f,.75f,.28f),accent);
        // Raised physical emblem instead of tiny, unreadable baked lettering.
        Shape(root,"Shop emblem",new Vector3(0,3.90f,-depth*.5f-.4f),new Vector3(.5f,.10f,.5f),"white","octagon").transform.localRotation=Quaternion.Euler(90,0,0);
        GameObject canopy=Box(root,"Pitched fabric awning",new Vector3(0,3.20f,-depth*.5f-.78f),new Vector3(width-.35f,.15f,1.5f),accent);
        canopy.transform.localRotation=Quaternion.Euler(-8,0,0);
        Box(root,"Awning valance",new Vector3(0,2.98f,-depth*.5f-1.48f),new Vector3(width-.35f,.3f,.10f),accent);
        for(int i=0;i<7;i++) Box(root,"Awning stripe",new Vector3(-width*.4f+i*width*.133f,3.23f,-depth*.5f-.77f),new Vector3(.23f,.035f,1.42f),"white").transform.localRotation=Quaternion.Euler(-8,0,0);
        Box(root,"Chimney",new Vector3(width*.31f,7.05f,depth*.29f),new Vector3(.65f,1.1f,.65f),"brick");
        Box(root,"Chimney cap",new Vector3(width*.31f,7.62f,depth*.29f),new Vector3(.83f,.14f,.83f),"stone");
        Transform rearDoor=Group(root,"Rear service entrance",new Vector3(0,0,depth*.5f+.1f));
        rearDoor.localRotation=Quaternion.Euler(0,180,0); Door(rearDoor,0,0,"metal");
        if(cafe)
        {
            CafeTable(root,new Vector3(-3.1f,0,-5.75f)); CafeTable(root,new Vector3(3.1f,0,-5.75f));
            Planter(root,new Vector3(0,0,-6.35f),1.3f,.9f,true);
        }
        else
        {
            Planter(root,new Vector3(-width*.5f+.7f,0,-4.6f),1.1f,1.1f,true);
            Crates(root,new Vector3(width*.5f-1.05f,0,-4.55f),accent);
        }
    }

    static void Apartment(Transform root)
    {
        Box(root,"Brick apartment",new Vector3(0,6.4f,1),new Vector3(13,12.8f,10),"brick",true);
        Box(root,"Raised ground storey",new Vector3(0,1.6f,1),new Vector3(13.15f,3.2f,10.15f),"cream");
        for(int floor=0;floor<4;floor++)
        {
            float y=1.7f+floor*3.03f;
            for(int column=0;column<4;column++) Window(root,-4.9f+column*3.27f,y,-4.08f,1.85f,1.85f);
            Box(root,"Floor belt course",new Vector3(0,y+1.23f,1),new Vector3(13.28f,.14f,10.28f),"stone");
        }
        Door(root,0,-4.18f);
        Box(root,"Entry canopy",new Vector3(0,3.18f,-4.90f),new Vector3(2.8f,.2f,1.9f),"navy");
        Box(root,"Roof parapet",new Vector3(0,12.85f,1),new Vector3(13.5f,.35f,10.5f),"cream");
        Box(root,"Inset roof deck",new Vector3(0,13.05f,1),new Vector3(12.9f,.12f,9.9f),"navy");
        Box(root,"Rooftop stair house",new Vector3(3.8f,14.05f,3.5f),new Vector3(3.1f,2,3.4f),"cream");
        Box(root,"Stair house cap",new Vector3(3.8f,15.1f,3.5f),new Vector3(3.3f,.17f,3.6f),"stone");
        for(int floor=1;floor<4;floor++)
        {
            float y=floor*3.03f+.5f;
            foreach(int side in new[]{-1,1})
            {
                float x=side*4.9f;
                Box(root,"Projecting balcony",new Vector3(x,y,-4.8f),new Vector3(2.6f,.16f,1.55f),"stone");
                Box(root,"Balcony top rail",new Vector3(x,y+1,-5.52f),new Vector3(2.6f,.085f,.085f),"navy");
                for(int i=0;i<6;i++) Box(root,"Balcony baluster",new Vector3(x-1.23f+i*.49f,y+.5f,-5.52f),new Vector3(.065f,1,.065f),"navy");
            }
        }
        Planter(root,new Vector3(-4.25f,0,-6.2f),3.2f,1.1f,true);
        Planter(root,new Vector3(4.25f,0,-6.2f),3.2f,1.1f,true);
    }

    static void Workshop(Transform root,bool warehouse)
    {
        float w=warehouse?17:11,d=warehouse?12:8,h=warehouse?6.4f:4.4f;
        Box(root,"Industrial hall",new Vector3(0,h*.5f,0),new Vector3(w,h,d),warehouse?"stone":"mint",true);
        Shape(root,"Pitched standing-seam roof",new Vector3(0,h,0),new Vector3(w+.8f,1.65f,d+.8f),"navy","gable");
        for(int i=0;i<2;i++)
        {
            float x=(i==0?-1:1)*w*.245f;
            Box(root,"Roll-up loading door",new Vector3(x,1.85f,-d*.5f-.10f),new Vector3(w*.36f,3.7f,.17f),"metal");
            for(int r=0;r<8;r++) Box(root,"Door slat",new Vector3(x,.25f+r*.45f,-d*.5f-.21f),new Vector3(w*.35f,.045f,.06f),"navy");
            Window(root,x,h-.65f,-d*.5f-.1f,w*.31f,.7f);
            foreach(int side in new[]{-1,1}) Bollard(root,new Vector3(x+side*w*.20f,0,-d*.5f-.65f));
        }
        Box(root,"Front roof fascia",new Vector3(0,h+.06f,-d*.5f-.32f),new Vector3(w+.75f,.22f,.2f),"white");
        Box(root,"Roof exhaust base",new Vector3(w*.25f,h+1.5f,d*.2f),new Vector3(1.3f,.65f,1.3f),"metal");
        Shape(root,"Roof exhaust vent",new Vector3(w*.25f,h+1.8f,d*.2f),new Vector3(.9f,.6f,.9f),"metal","octagon");
        for(int side=-1;side<=1;side+=2)
            Box(root,"Downpipe",new Vector3(side*(w*.5f-.2f),h*.5f,-d*.5f-.13f),new Vector3(.12f,h,.13f),"navy");
        Crates(root,new Vector3(w*.5f+1,0,1),"wood");
    }

    static void Tree(Transform root,Vector3 position,float height,bool evergreen=false)
    {
        Transform tree=Group(root,evergreen?"Layered evergreen":"Broad canopy tree",position);
        Shape(tree,"Faceted trunk",Vector3.zero,new Vector3(.33f,height*.57f,.33f),"wood","octagon");
        Box(tree,"Trunk collision",new Vector3(0,height*.23f,0),new Vector3(.30f,height*.46f,.30f),"wood",true);
        if(evergreen)
            for(int i=0;i<3;i++) Shape(tree,"Pine tier",new Vector3(0,height*(.20f+i*.19f),0),new Vector3(height*(.56f-i*.12f),height*.44f,height*(.56f-i*.12f)),"pine","cone");
        else
        {
            Shape(tree,"Main faceted canopy",new Vector3(0,height*.34f,0),new Vector3(height*.73f,height*.66f,height*.66f),"leaf","crown");
            Shape(tree,"Sunlit crown",new Vector3(height*.18f,height*.46f,height*.05f),new Vector3(height*.52f,height*.48f,height*.48f),"leaf-light","crown");
        }
    }

    static void Planter(Transform root,Vector3 position,float width,float depth,bool flowers)
    {
        Transform p=Group(root,"Raised planted border",position);
        Box(p,"Planter body",new Vector3(0,.28f,0),new Vector3(width,.56f,depth),"stone",true);
        Box(p,"Planter rim",new Vector3(0,.58f,0),new Vector3(width+.10f,.12f,depth+.10f),"cream");
        Box(p,"Inset soil",new Vector3(0,.655f,0),new Vector3(width-.16f,.035f,depth-.16f),"soil");
        int n=Mathf.Max(1,Mathf.RoundToInt(width/.67f));
        for(int i=0;i<n;i++)
        {
            float x=-width*.5f+.35f+i*(width-.70f)/Mathf.Max(1,n-1);
            Shape(p,"Planted shrub",new Vector3(x,.66f,0),new Vector3(.65f,.6f,Mathf.Min(.7f,depth*.75f)),"leaf","crown");
            if(flowers) Shape(p,"Flower crown",new Vector3(x+.04f,1.1f,-.03f),new Vector3(.26f,.20f,.26f),i%2==0?"coral":"yellow","crown");
        }
    }

    static void Bench(Transform root,Vector3 position,float yaw=0)
    {
        Transform b=Group(root,"Slatted park bench",position); b.localRotation=Quaternion.Euler(0,yaw,0);
        foreach(int s in new[]{-1,1})
        {
            Box(b,"Bench leg",new Vector3(s*.73f,.34f,0),new Vector3(.12f,.68f,.62f),"navy",true);
            Box(b,"Back support",new Vector3(s*.73f,.83f,.30f),new Vector3(.1f,.95f,.1f),"navy");
            Box(b,"Armrest",new Vector3(s*.87f,.92f,0),new Vector3(.09f,.1f,.65f),"navy");
        }
        for(int i=0;i<3;i++)
        {
            Box(b,"Seat slat",new Vector3(0,.65f,-.21f+i*.21f),new Vector3(2,.1f,.17f),"timber");
            Box(b,"Back slat",new Vector3(0,.86f+i*.18f,.30f),new Vector3(2,.13f,.08f),"timber");
        }
    }

    static void CafeTable(Transform root,Vector3 position)
    {
        Transform t=Group(root,"Sidewalk cafe table",position);
        Shape(t,"Octagonal tabletop",new Vector3(0,.82f,0),new Vector3(1.05f,.10f,1.05f),"cream","octagon");
        Box(t,"Table pedestal",new Vector3(0,.4f,0),new Vector3(.14f,.8f,.14f),"navy",true);
        Box(t,"Table cross foot",new Vector3(0,.08f,0),new Vector3(.65f,.09f,.15f),"navy");
        foreach(int s in new[]{-1,1})
        {
            Transform chair=Group(t,"Bistro chair",new Vector3(s*.94f,0,0)); chair.localRotation=Quaternion.Euler(0,s*90,0);
            Box(chair,"Chair seat",new Vector3(0,.47f,0),new Vector3(.47f,.09f,.48f),"coral");
            Box(chair,"Chair back",new Vector3(0,.78f,.24f),new Vector3(.47f,.44f,.07f),"coral");
            foreach(int x in new[]{-1,1}) foreach(int z in new[]{-1,1}) Box(chair,"Chair leg",new Vector3(x*.19f,.23f,z*.19f),new Vector3(.055f,.46f,.055f),"navy");
        }
    }

    static void BusShelter(Transform root)
    {
        foreach(int side in new[]{-1,1})
        {
            Box(root,"Shelter column",new Vector3(side*2.5f,1.65f,.7f),new Vector3(.15f,3.3f,.15f),"navy",true);
            Box(root,"Shelter side glass",new Vector3(side*2.48f,1.55f,0),new Vector3(.06f,2.8f,1.5f),"glass");
        }
        Box(root,"Shelter glass back",new Vector3(0,1.55f,.72f),new Vector3(4.9f,2.8f,.07f),"glass",true);
        Box(root,"Floating shelter roof",new Vector3(0,3.29f,-.08f),new Vector3(5.5f,.22f,2.15f),"mint");
        Box(root,"Shelter roof trim",new Vector3(0,3.18f,-1.1f),new Vector3(5.5f,.22f,.09f),"cream");
        Bench(root,new Vector3(-.5f,0,.22f));
        Box(root,"Timetable board",new Vector3(1.62f,1.65f,.60f),new Vector3(.65f,.95f,.09f),"cream");
        for(int i=0;i<4;i++) Box(root,"Timetable lines",new Vector3(1.62f,1.9f-i*.18f,.53f),new Vector3(.44f,.04f,.03f),"navy");
        Box(root,"Bus stop pole",new Vector3(-3.15f,1.85f,-.55f),new Vector3(.09f,3.7f,.09f),"navy",true);
        Box(root,"Bus stop flag",new Vector3(-3.15f,3.48f,-.55f),new Vector3(.66f,.55f,.14f),"yellow");
    }

    static void Fence(Transform root,float width,bool plain)
    {
        foreach(int side in new[]{-1,1})
        {
            Box(root,"Fence post",new Vector3(side*width*.5f,.83f,0),new Vector3(.22f,1.66f,.22f),"white");
            Shape(root,"Post cap",new Vector3(side*width*.5f,1.66f,0),new Vector3(.28f,.18f,.28f),"white","cone");
        }
        for(int r=0;r<2;r++) Box(root,"Fence rail",new Vector3(0,.43f+r*.72f,.015f),new Vector3(width,.14f,.12f),"white");
        for(float x=-width*.5f+.27f;x<width*.5f;x+=.32f)
        {
            Box(root,"Picket",new Vector3(x,.83f,-.065f),new Vector3(.16f,1.44f,.09f),"white");
            Shape(root,"Picket tip",new Vector3(x,1.55f,-.065f),new Vector3(.16f,.12f,.09f),"white","gable");
        }
        Box(root,"Continuous fence collision",new Vector3(0,.8f,0),new Vector3(width,1.6f,.14f),"white",true).GetComponent<Renderer>().enabled=false;
    }

    static void BrickWall(Transform root,float width)
    {
        Box(root,"Garden masonry",new Vector3(0,.69f,0),new Vector3(width,1.38f,.33f),"brick",true);
        for(int row=0;row<4;row++)
        {
            Box(root,"Mortar bed",new Vector3(0,.16f+row*.32f,-.175f),new Vector3(width,.018f,.025f),"cream");
            for(float x=-width*.5f+(row%2)*.34f;x<width*.5f;x+=.68f)
                Box(root,"Mortar joint",new Vector3(x,.3f+row*.32f,-.175f),new Vector3(.018f,.29f,.025f),"cream");
        }
        foreach(int side in new[]{-1,1}) Box(root,"Brick pier",new Vector3(side*(width*.5f-.16f),.83f,0),new Vector3(.5f,1.66f,.52f),"brick");
        Box(root,"Stone wall coping",new Vector3(0,1.46f,0),new Vector3(width+.14f,.16f,.48f),"cream");
        foreach(int side in new[]{-1,1}) Box(root,"Pier cap",new Vector3(side*(width*.5f-.16f),1.7f,0),new Vector3(.66f,.16f,.66f),"cream");
    }

    static void Hedge(Transform root,float width)
    {
        Box(root,"Hedge lower body",new Vector3(0,.72f,0),new Vector3(width,1.44f,.8f),"leaf",true);
        for(int i=0;i<Mathf.CeilToInt(width/.8f);i++)
            Shape(root,"Rounded hedge crown",new Vector3(-width*.5f+.4f+i*.8f,1.15f,0),new Vector3(.95f,.6f,.95f),"leaf-light","crown");
    }

    static void GardenGateway(Transform root)
    {
        foreach(int side in new[]{-1,1})
        {
            Transform fence=Group(root,"Gate wing",new Vector3(side*2.25f,0,0)); Fence(fence,2.5f,true);
            Box(root,"Arch upright",new Vector3(side*.93f,1.5f,0),new Vector3(.2f,3,.2f),"white",true);
        }
        Box(root,"Garden arch lintel",new Vector3(0,2.9f,0),new Vector3(2.7f,.18f,.85f),"white");
        for(int i=0;i<6;i++) Box(root,"Arch trellis",new Vector3(-1.25f+i*.5f,3.08f,0),new Vector3(.1f,.13f,1.2f),"white");
        Planter(root,new Vector3(-1.5f,0,-.55f),.8f,.8f,true);
        Planter(root,new Vector3(1.5f,0,-.55f),.8f,.8f,true);
        // The central 1.65 m walk-through deliberately has no gate collider.
    }

    static void PocketPark(Transform root)
    {
        Box(root,"Gravel garden path",new Vector3(0,.028f,0),new Vector3(2.7f,.05f,10),"paving");
        Box(root,"Cross path",new Vector3(0,.03f,0),new Vector3(10,.05f,2.5f),"paving");
        Tree(root,new Vector3(-3.5f,0,3.2f),6.5f); Tree(root,new Vector3(3.45f,0,-3),5.2f);
        Planter(root,new Vector3(-3.4f,0,-3.2f),2.8f,2.4f,true);
        Bench(root,new Vector3(3.3f,0,2.4f),-90); Bench(root,new Vector3(-3.2f,0,.4f),90);
        foreach(int side in new[]{-1,1}) { Transform fence=Group(root,"Park rear border",new Vector3(side*3,0,5.1f)); Fence(fence,5.8f,false); }
    }

    static void Grove(Transform root)
    {
        Vector3[] positions={new Vector3(-4.5f,0,2.8f),new Vector3(-1.7f,0,5.0f),new Vector3(2.2f,0,3.7f),new Vector3(4.5f,0,-.7f),new Vector3(-3.9f,0,-2.7f)};
        for(int i=0;i<positions.Length;i++) Tree(root,positions[i],5.2f+(i%3)*1.25f,i==2);
        Bench(root,new Vector3(0,0,-1.4f));
        for(int i=0;i<5;i++)
            Shape(root,"Stone path paver",new Vector3(-.2f+i*.1f,.02f,-4.9f+i*.69f),new Vector3(1.6f,.06f,.5f),"stone","octagon");
        Shape(root,"Low boulder",new Vector3(3.2f,0,-3.2f),new Vector3(1.6f,.8f,1.25f),"stone","crown");
    }

    static void Crates(Transform root,Vector3 position,string material)
    {
        Transform p=Group(root,"Stacked delivery crates",position);
        Box(p,"Pallet",new Vector3(0,.10f,0),new Vector3(1.65f,.20f,1.35f),"wood");
        for(int row=0;row<2;row++)
        {
            Box(p,"Crate body",new Vector3(row*.15f,.58f+row*.86f,0),new Vector3(1.4f,.78f,1.12f),material,true);
            for(int i=0;i<4;i++) Box(p,"Crate slat",new Vector3(row*.15f,.29f+row*.86f+i*.19f,-.58f),new Vector3(1.44f,.10f,.07f),"timber");
            foreach(int side in new[]{-1,1}) Box(p,"Crate corner brace",new Vector3(side*.61f+row*.15f,.58f+row*.86f,-.63f),new Vector3(.10f,.80f,.08f),"wood");
        }
    }

    static void Alley(Transform root)
    {
        Box(root,"Alley paving",new Vector3(0,.02f,0),new Vector3(5,.04f,6),"asphalt");
        Transform wall=Group(root,"Service boundary",new Vector3(0,0,2.8f)); BrickWall(wall,5);
        Box(root,"Dumpster",new Vector3(-1.4f,.82f,.9f),new Vector3(1.8f,1.45f,1.3f),"mint",true);
        Box(root,"Dumpster lid",new Vector3(-1.4f,1.58f,.9f),new Vector3(1.92f,.13f,1.42f),"navy").transform.localRotation=Quaternion.Euler(-6,0,0);
        foreach(int side in new[]{-1,1}) Box(root,"Dumpster handle",new Vector3(-1.4f+side*.94f,1.24f,.9f),new Vector3(.12f,.1f,.54f),"metal");
        Crates(root,new Vector3(1.2f,0,1.4f),"wood");
        Box(root,"Drain frame",new Vector3(.2f,.055f,-1.8f),new Vector3(.8f,.035f,.5f),"navy");
        for(int i=0;i<5;i++) Box(root,"Drain grille",new Vector3(-.12f+i*.16f,.08f,-1.8f),new Vector3(.06f,.025f,.44f),"metal");
    }

    static void BicycleParking(Transform root)
    {
        for(int i=0;i<4;i++)
        {
            float x=-1.65f+i*1.1f;
            foreach(int side in new[]{-1,1}) Box(root,"Cycle rack upright",new Vector3(x,.46f,side*.42f),new Vector3(.07f,.92f,.07f),"navy",true);
            Box(root,"Cycle rack top",new Vector3(x,.92f,0),new Vector3(.07f,.075f,.90f),"navy");
        }
        Box(root,"Rack anchoring strip",new Vector3(0,.06f,0),new Vector3(4.4f,.1f,1.2f),"stone");
    }

    static void ParkingBay(Transform root)
    {
        Box(root,"Two parking bays",new Vector3(0,.02f,0),new Vector3(6.4f,.04f,6.3f),"asphalt");
        for(int i=0;i<3;i++) Box(root,"Parking stripe",new Vector3(-3.05f+i*3.05f,.047f,0),new Vector3(.1f,.025f,5.8f),"white");
        foreach(int side in new[]{-1,1})
        {
            Box(root,"Wheel stop",new Vector3(side*1.52f,.11f,2.35f),new Vector3(1.8f,.22f,.22f),"stone",true);
            Box(root,"Reflector",new Vector3(side*1.52f,.22f,2.35f),new Vector3(.75f,.03f,.24f),"yellow");
        }
        Planter(root,new Vector3(0,0,3.55f),6.4f,1.1f,false);
    }

    static void Bollard(Transform root,Vector3 position)
    {
        Shape(root,"Bollard footing",position,new Vector3(.38f,.10f,.38f),"stone","octagon");
        Box(root,"Safety bollard",position+new Vector3(0,.54f,0),new Vector3(.16f,1.08f,.16f),"yellow",true);
        Box(root,"Reflective bollard band",position+new Vector3(0,.84f,0),new Vector3(.17f,.15f,.17f),"navy");
    }

    static void Utility(Transform root)
    {
        Box(root,"Equipment pad",new Vector3(0,.04f,0),new Vector3(4.8f,.08f,2.9f),"paving");
        Box(root,"Transformer cabinet",new Vector3(-.6f,1,0),new Vector3(2.3f,1.9f,1.2f),"mint",true);
        Box(root,"Cabinet lid",new Vector3(-.6f,2,0),new Vector3(2.45f,.15f,1.32f),"metal");
        for(int i=0;i<7;i++) Box(root,"Vent louvre",new Vector3(-1.14f,.54f+i*.15f,-.62f),new Vector3(.76f,.045f,.06f),"navy");
        Box(root,"Service door seam",new Vector3(-.45f,1,-.63f),new Vector3(.025f,1.65f,.04f),"navy");
        Box(root,"Warning plate",new Vector3(.04f,1.47f,-.65f),new Vector3(.34f,.30f,.04f),"yellow");
        Box(root,"Communications cabinet",new Vector3(1.4f,.76f,0),new Vector3(.7f,1.52f,.65f),"cream",true);
        foreach(int side in new[]{-1,1}) Bollard(root,new Vector3(side*2.1f,0,-1.15f));
    }

    static void Market(Transform root)
    {
        foreach(int x in new[]{-1,1}) foreach(int z in new[]{-1,1}) Box(root,"Market canopy post",new Vector3(x*1.8f,1.7f,z*1.2f),new Vector3(.12f,3.4f,.12f),"wood",true);
        Shape(root,"Pitched market canopy",new Vector3(0,3.1f,0),new Vector3(4.4f,.95f,3.1f),"coral","gable");
        Box(root,"Market counter",new Vector3(0,.96f,-.45f),new Vector3(3.7f,.14f,1.4f),"timber",true);
        Box(root,"Counter skirt",new Vector3(0,.46f,-1.06f),new Vector3(3.7f,.86f,.08f),"mint");
        for(int i=0;i<3;i++)
        {
            Box(root,"Produce tray",new Vector3(-1.15f+i*1.15f,1.1f,-.45f),new Vector3(.94f,.15f,.85f),"wood");
            for(int j=0;j<3;j++) Shape(root,"Produce pile",new Vector3(-1.45f+i*1.15f+j*.30f,1.18f,-.45f),new Vector3(.34f,.24f,.52f),i==1?"yellow":"leaf-light","crown");
        }
        Crates(root,new Vector3(2.75f,0,.35f),"wood");
    }

    static void Pergola(Transform root)
    {
        foreach(int x in new[]{-1,1}) foreach(int z in new[]{-1,1}) Box(root,"Pergola post",new Vector3(x*2.3f,1.7f,z*1.7f),new Vector3(.22f,3.4f,.22f),"timber",true);
        foreach(int side in new[]{-1,1}) Box(root,"Pergola main beam",new Vector3(0,3.3f,side*1.7f),new Vector3(5.2f,.22f,.18f),"wood");
        for(int i=0;i<9;i++) Box(root,"Pergola rafter",new Vector3(-2.5f+i*.625f,3.53f,0),new Vector3(.12f,.20f,4.2f),"timber");
        Bench(root,new Vector3(0,0,1.35f));
        Planter(root,new Vector3(-2.3f,0,-1.7f),.8f,.8f,true); Planter(root,new Vector3(2.3f,0,-1.7f),.8f,.8f,true);
    }

    static void Fountain(Transform root)
    {
        Shape(root,"Octagonal basin plinth",Vector3.zero,new Vector3(4.2f,.33f,4.2f),"stone","octagon");
        Shape(root,"Basin lip",new Vector3(0,.33f,0),new Vector3(4.3f,.2f,4.3f),"cream","octagon");
        Shape(root,"Inset water surface",new Vector3(0,.535f,0),new Vector3(3.77f,.02f,3.77f),"water","octagon");
        Shape(root,"Fountain pedestal",new Vector3(0,.54f,0),new Vector3(.65f,1.15f,.65f),"stone","octagon");
        Shape(root,"Upper fountain bowl",new Vector3(0,1.6f,0),new Vector3(1.75f,.23f,1.75f),"cream","octagon");
        Shape(root,"Upper water",new Vector3(0,1.835f,0),new Vector3(1.5f,.025f,1.5f),"water","octagon");
        Shape(root,"Finial",new Vector3(0,1.86f,0),new Vector3(.3f,.6f,.3f),"stone","crown");
        var collider=root.gameObject.AddComponent<BoxCollider>(); collider.center=new Vector3(0,.32f,0); collider.size=new Vector3(4.1f,.64f,4.1f); root.gameObject.layer=7;
    }

    static void Sidewalk(Transform root)
    {
        // Surface-only dressing. Roads and navigation are authored through the road tool, not this stamp.
        Box(root,"Footpath paving",new Vector3(0,.025f,0),new Vector3(6,.05f,3),"paving");
        Box(root,"Curb edging",new Vector3(0,.055f,-1.43f),new Vector3(6,.11f,.14f),"cream");
        for(int i=0;i<5;i++) Box(root,"Paving joint",new Vector3(-2.4f+i*1.2f,.055f,0),new Vector3(.018f,.008f,2.82f),"stone");
        Box(root,"Centre paving joint",new Vector3(0,.055f,.05f),new Vector3(6,.008f,.018f),"stone");
    }

    static void CommunityGarden(Transform root)
    {
        for(int x=-1;x<=1;x+=2) for(int z=-1;z<=1;z+=2) Planter(root,new Vector3(x*2.1f,0,z*1.8f),2.8f,2.1f,true);
        Box(root,"Garden central path",new Vector3(0,.03f,0),new Vector3(1.3f,.05f,7.2f),"paving");
        Box(root,"Garden cross path",new Vector3(0,.035f,0),new Vector3(7,.05f,1.1f),"paving");
        Bench(root,new Vector3(0,0,4.3f));
        Box(root,"Community notice post",new Vector3(-3.4f,1.1f,-3.3f),new Vector3(.16f,2.2f,.16f),"wood",true);
        Box(root,"Community notice frame",new Vector3(-3.4f,1.9f,-3.3f),new Vector3(1.3f,.8f,.15f),"timber");
        Box(root,"Community notice inset",new Vector3(-3.4f,1.9f,-3.39f),new Vector3(1.05f,.56f,.04f),"cream");
    }
}
