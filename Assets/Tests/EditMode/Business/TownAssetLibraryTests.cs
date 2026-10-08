using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

/// <summary>Reads the shipped palette without regenerating it. Dangerous-content checks use isolated
/// preview scenes, so these tests never modify a town, prefab asset, palette or player save.</summary>
public class TownAssetLibraryTests
{
    const string PalettePath="Assets/Editor/TownAuthoring/TownPalette.asset";
    const string KitRoot="Assets/Prefabs/TownAuthoring/Kits/";
    static Type E(string name) => Type.GetType(name+", Assembly-CSharp-Editor",true);
    static T Field<T>(object value,string name) => (T)value.GetType().GetField(name).GetValue(value);
    static object[] Entries()
    {
        var palette=AssetDatabase.LoadMainAssetAtPath(PalettePath);
        Assert.NotNull(palette,"Generate the shipped palette before testing its assets.");
        return ((IEnumerable)palette.GetType().GetField("entries").GetValue(palette)).Cast<object>()
            .Where(e=>e!=null && Field<string>(e,"id").StartsWith("builtin/",StringComparison.Ordinal)).ToArray();
    }
    static bool Validate(string method,GameObject prefab,out string reason)
    {
        object[] args={prefab,null}; bool result=(bool)E("TownAssetLibrary").GetMethod(method).Invoke(null,args);
        reason=(string)args[1]; return result;
    }
    Scene preview;
    GameObject instance;

    GameObject Preview()
    {
        preview=EditorSceneManager.NewPreviewScene();
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(KitRoot+"raised-planter.prefab");
        Assert.NotNull(prefab);
        instance=(GameObject)PrefabUtility.InstantiatePrefab(prefab,preview); return instance;
    }
    [TearDown] public void CleanupPreview()
    {
        if(preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
        preview=default; instance=null;
    }

    [Test] public void ShippedPaletteHas100UniqueUsableEntries()
    {
        var entries=Entries(); Assert.AreEqual(100,entries.Length);
        Assert.AreEqual(100,entries.Select(e=>Field<string>(e,"id")).Distinct().Count());
        foreach(var entry in entries)
        {
            string id=Field<string>(entry,"id");
            Assert.NotNull(Field<GameObject>(entry,"prefab"),id);
            Assert.IsNotEmpty(Field<string>(entry,"displayName"),id);
            Assert.IsNotEmpty(Field<string>(entry,"description"),id);
            Assert.Greater(Field<float>(entry,"spacing"),0,id);
            Assert.Greater(Field<float>(entry,"defaultScale"),0,id);
        }
    }

    [Test] public void EveryAdvertisedCategoryHasContentExceptUserCustom()
    {
        string[] categories=(string[])E("TownAssetLibrary").GetField("Categories").GetValue(null);
        var used=Entries().Select(e=>Field<string>(e,"category")).ToArray();
        foreach(string category in categories.Where(c=>c!="Custom")) CollectionAssert.Contains(used,category);
    }

    [Test] public void GameplayRolesMatchCollectionAndVehiclePromises()
    {
        var entries=Entries();
        var homes=entries.Where(e=>Field<object>(e,"role").ToString()=="House").ToArray();
        var shops=entries.Where(e=>Field<object>(e,"role").ToString()=="CollectionBuilding").ToArray();
        Assert.AreEqual(7,homes.Length); Assert.AreEqual(6,shops.Length);
        Assert.AreEqual(4,entries.Count(e=>Field<object>(e,"role").ToString()=="ParkedCar"));
        foreach(var home in homes) StringAssert.StartsWith("SM_house_suburb_",Field<string>(home,"houseModel"));
        foreach(var shop in shops)
        {
            CollectionAssert.Contains(new[]{"shop","restaurant","tower"},Field<string>(shop,"collectionType"));
            Assert.AreEqual(180f,Field<float>(shop,"propertyFrontYaw"),"Generated fronts face local -Z.");
        }
    }

    [Test] public void AllPaletteAssetsHaveVisibleGeometryAndNonPinkMaterials()
    {
        foreach(var entry in Entries())
        {
            string id=Field<string>(entry,"id"); var prefab=Field<GameObject>(entry,"prefab");
            Renderer[] renderers=prefab.GetComponentsInChildren<Renderer>(true);
            Assert.IsNotEmpty(renderers,id);
            foreach(var renderer in renderers)
            {
                Assert.IsNotEmpty(renderer.sharedMaterials,id+" / "+renderer.name);
                foreach(var material in renderer.sharedMaterials)
                {
                    Assert.NotNull(material,id+" / "+renderer.name);
                    Assert.NotNull(material.shader,id+" / "+material.name);
                    Assert.IsTrue(material.shader.isSupported,id+" / "+material.shader.name);
                    StringAssert.DoesNotContain("InternalErrorShader",material.shader.name,id);
                }
            }
        }
    }

    [Test] public void EveryBuiltInPassesTheActualStampVisualSafetyGate()
    {
        foreach(var entry in Entries())
            Assert.IsTrue(Validate("ValidateVisualContents",Field<GameObject>(entry,"prefab"),out var reason),
                Field<string>(entry,"id")+": "+reason);
    }

    [Test] public void UrpLightMetadataIsAllowedButDoesNotExemptOtherScripts()
    {
        var lamp=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Suburbs Town Pack/PREFABS/Props/SM_streetlamp_01.prefab");
        Assert.NotNull(lamp);
        Assert.IsTrue(lamp.GetComponentsInChildren<MonoBehaviour>(true).Any(c=>c &&
            c.GetType().FullName=="UnityEngine.Rendering.Universal.UniversalAdditionalLightData"),"Regression fixture contains URP light settings.");
        Assert.IsTrue(Validate("ValidateVisualContents",lamp,out var reason),reason);
        preview=EditorSceneManager.NewPreviewScene();
        instance=(GameObject)PrefabUtility.InstantiatePrefab(lamp,preview);
        instance.AddComponent<Unity.Netcode.NetworkObject>();
        Assert.IsFalse(Validate("ValidateVisualContents",instance,out reason));
        StringAssert.Contains("gameplay scripts",reason);
    }

    [Test] public void TwentySixKitsUseSharedPersistentAssetsAndContainNoGameplayScripts()
    {
        var kits=Entries().Select(e=>Field<GameObject>(e,"prefab"))
            .Where(p=>AssetDatabase.GetAssetPath(p).StartsWith(KitRoot,StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(26,kits.Length);
        foreach(var kit in kits)
        {
            Assert.IsEmpty(kit.GetComponentsInChildren<MonoBehaviour>(true),kit.name);
            Assert.IsEmpty(kit.GetComponentsInChildren<Rigidbody>(true),kit.name);
            foreach(var filter in kit.GetComponentsInChildren<MeshFilter>(true))
            { Assert.NotNull(filter.sharedMesh,kit.name); Assert.IsTrue(EditorUtility.IsPersistent(filter.sharedMesh),kit.name); }
            foreach(var renderer in kit.GetComponentsInChildren<Renderer>(true))
                foreach(var material in renderer.sharedMaterials) Assert.IsTrue(EditorUtility.IsPersistent(material),kit.name);
        }
    }

    [Test] public void StaticPrefabIsAcceptedButSceneInstanceAndChildAreRejected()
    {
        var asset=AssetDatabase.LoadAssetAtPath<GameObject>(KitRoot+"raised-planter.prefab");
        Assert.IsTrue(Validate("ValidateCustomPrefab",asset,out var reason),reason);
        Assert.IsFalse(Validate("ValidateCustomPrefab",asset.transform.GetChild(0).gameObject,out reason));
        StringAssert.Contains("root",reason);
        Assert.IsFalse(Validate("ValidateCustomPrefab",Preview(),out reason));
        StringAssert.Contains("Project",reason);
    }

    [Test] public void RuntimeGameplayComponentsAreRejected()
    {
        var visual=Preview(); visual.AddComponent<Unity.Netcode.NetworkObject>();
        Assert.IsFalse(Validate("ValidateVisualContents",visual,out var reason));
        StringAssert.Contains("gameplay scripts",reason);
    }

    [TestCase(typeof(Rigidbody))] [TestCase(typeof(Rigidbody2D))]
    [TestCase(typeof(ArticulationBody))] [TestCase(typeof(CharacterController))]
    public void SimulatedPhysicsIsRejected(Type component)
    {
        var visual=Preview(); visual.AddComponent(component);
        Assert.IsFalse(Validate("ValidateVisualContents",visual,out var reason));
        StringAssert.Contains("physics",reason);
    }

    [TestCase(typeof(Camera))] [TestCase(typeof(AudioListener))] [TestCase(typeof(AudioSource))]
    [TestCase(typeof(Animator))] [TestCase(typeof(Animation))] [TestCase(typeof(ParticleSystem))]
    [TestCase(typeof(UnityEngine.Playables.PlayableDirector))]
    [TestCase(typeof(UnityEngine.AI.NavMeshAgent))] [TestCase(typeof(UnityEngine.AI.NavMeshObstacle))]
    [TestCase(typeof(WindZone))]
    public void NativeRuntimeDriversAreRejectedEvenWithoutGameplayScripts(Type component)
    {
        var visual=Preview(); var driver=visual.AddComponent(component);
        Assert.IsFalse(Validate("ValidateVisualContents",visual,out var reason));
        StringAssert.Contains("runtime driver",reason); StringAssert.Contains(component.Name,reason);
        if(driver is Behaviour behaviour)
        {
            behaviour.enabled=false;
            Assert.IsFalse(Validate("ValidateVisualContents",visual,out reason),"Disabled drivers must not bypass validation.");
            StringAssert.Contains(component.Name,reason);
        }
    }

    [Test] public void EmptyOrMissingMaterialsAreRejected()
    {
        var visual=Preview(); var renderer=visual.GetComponentInChildren<Renderer>();
        renderer.sharedMaterials=Array.Empty<Material>();
        Assert.IsFalse(Validate("ValidateVisualContents",visual,out var reason));
        StringAssert.Contains("material",reason);
        renderer.sharedMaterials=new Material[]{null};
        Assert.IsFalse(Validate("ValidateVisualContents",visual,out reason));
        StringAssert.Contains("material",reason);
    }

    [Test] public void InvisiblePrefabIsRejected()
    {
        var visual=Preview(); foreach(var renderer in visual.GetComponentsInChildren<Renderer>(true)) Object.DestroyImmediate(renderer);
        Assert.IsFalse(Validate("ValidateVisualContents",visual,out var reason));
        StringAssert.Contains("renderers",reason);
    }
}
