using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class PublishedTownSignatureTests
{
    static byte[] Signature(byte value=1) { var result=new byte[32]; result[0]=value; result[31]=99; return result; }
    static JoinRequest Request() { var r=JoinRequest.ForThisBuild(PlayerKey.Dev("map-test")); r.worldSignature=Signature(); return r; }

    [Test] public void MatchingPublishedMapsAreCompatible()
    { Assert.AreEqual(JoinReject.None,PublishedTownSignature.CheckCompatibility(Request(),Signature())); }

    [Test] public void DifferentPublishedMapsAreRefused()
    {
        Assert.AreEqual(JoinReject.TownMapMismatch,PublishedTownSignature.CheckCompatibility(Request(),Signature(2)));
        StringAssert.Contains("same published town",JoinRejectText.For(JoinReject.TownMapMismatch));
    }

    [Test] public void CosmeticContentVersionMismatchStillWorks()
    {
        var r=Request(); r.content=unchecked((ushort)(NetVersion.Content+123));
        Assert.AreEqual(JoinReject.None,PublishedTownSignature.CheckCompatibility(r,Signature()));
    }

    [Test] public void ProtocolMismatchStillTakesPrecedence()
    {
        var r=Request(); r.proto=999;
        Assert.AreEqual(JoinReject.ProtocolMismatch,PublishedTownSignature.CheckCompatibility(r,new byte[32]));
    }

    [Test] public void MissingOrInvalidSignaturesNeverMatchEvenEachOther()
    {
        var r=Request();
        foreach(var missing in new byte[][] {null,new byte[0],new byte[31],new byte[32],new byte[33]})
        {
            Assert.AreEqual(JoinReject.TownNotPublished,PublishedTownSignature.CheckCompatibility(r,missing));
            r.worldSignature=missing;
            Assert.AreEqual(JoinReject.TownNotPublished,PublishedTownSignature.CheckCompatibility(r,missing));
            Assert.AreEqual(JoinReject.TownNotPublished,PublishedTownSignature.CheckCompatibility(r,Signature()));
            r=Request();
        }
    }

    [Test] public void FullWorldDigestSurvivesJoinWireRoundTrip()
    {
        var r=Request(); for(int i=0;i<32;i++) r.worldSignature[i]=(byte)(i*7);
        Assert.IsTrue(JoinRequest.TryDecode(r.Encode(),out var decoded,out _));
        CollectionAssert.AreEqual(r.worldSignature,decoded.worldSignature);
    }

    [Test] public void MissingPublicationStillProducesAnEncodableRequestButCannotJoin()
    {
        var r=JoinRequest.ForThisBuild(PlayerKey.Dev("first-publication"));
        r.worldSignature=PublishedTownSignature.BytesFrom(null);
        Assert.AreEqual(32,r.worldSignature.Length);
        Assert.IsTrue(JoinRequest.TryDecode(r.Encode(),out var decoded,out _));
        Assert.AreEqual(JoinReject.TownNotPublished,PublishedTownSignature.CheckCompatibility(decoded,Signature()));
    }

    [TestCase(null)] [TestCase("")] [TestCase("abcd")]
    [TestCase("0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("z123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void InvalidHexFailsClosed(string value) { Assert.IsFalse(PublishedTownSignature.TryParse(value,out _)); }

    [Test] public void HexSupportsEitherCase()
    {
        string value="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.IsTrue(PublishedTownSignature.TryParse(value,out var lower));
        Assert.IsTrue(PublishedTownSignature.TryParse(value.ToUpperInvariant(),out var upper));
        CollectionAssert.AreEqual(lower,upper); Assert.AreEqual(32,lower.Length);
    }

    static string Manifest(params KeyValuePair<string,string>[] entries)
    {
        Type type=Type.GetType("TownWorldSignatureBuilder, Assembly-CSharp-Editor",true);
        return (string)type.GetMethod("HashManifest",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{entries});
    }
    static KeyValuePair<string,string> Part(string path,string value) => new KeyValuePair<string,string>(path,value);

    [Test] public void ManifestOrderingIsStable()
    { Assert.AreEqual(Manifest(Part("scene","a"),Part("road","b")),Manifest(Part("road","b"),Part("scene","a"))); }

    [Test] public void SceneOrDerivedContentChangeChangesIdentity()
    {
        string original=Manifest(Part("scene","a"),Part("graph","b"));
        Assert.AreNotEqual(original,Manifest(Part("scene","changed"),Part("graph","b")));
        Assert.AreNotEqual(original,Manifest(Part("scene","a"),Part("graph","changed")));
        Assert.AreNotEqual(original,Manifest(Part("scene","a"),Part("graph","b"),Part("new-prefab","c")));
    }

    [Test] public void PublicationAssetCannotHashItself()
    {
        const string output="Assets/Resources/City/PublishedTownSignature.asset";
        string original=Manifest(Part("scene","a"));
        Assert.AreEqual(original,Manifest(Part("scene","a"),Part(output,"first"),Part(output+".meta","guid")));
        Assert.AreEqual(original,Manifest(Part("scene","a"),Part(output,"second")));
    }

    static bool TracksDirtySource(UnityEngine.Object asset) => (bool)Type.GetType("TownWorldSignatureBuilder, Assembly-CSharp-Editor",true)
        .GetMethod("TracksUnsavedSourceObject",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{asset});

    [Test] public void ImportedTruckMaterialCacheDoesNotBlockPublishing()
    {
        var imported=AssetDatabase.LoadAllAssetsAtPath("Assets/Models/TruckBody.fbx");
        Assert.IsNotEmpty(imported);
        int materials=0;
        foreach(var asset in imported)
        {
            if(asset is Material) materials++;
            Assert.IsFalse(TracksDirtySource(asset),asset.name+" is importer-owned, not a saveable source object.");
        }
        Assert.Greater(materials,0,"Regression fixture includes the embedded materials Unity marks dirty.");
    }

    [Test] public void NativeMaterialsAndGeneratedMeshesStillProtectUnsavedEdits()
    {
        var material=AssetDatabase.LoadAssetAtPath<Material>("Assets/Prefabs/TownAuthoring/Materials/stone.mat");
        var mesh=AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Prefabs/TownAuthoring/Meshes/gable.asset");
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/TownAuthoring/Kits/warehouse.prefab");
        Assert.NotNull(material); Assert.NotNull(mesh); Assert.NotNull(prefab);
        Assert.IsTrue(TracksDirtySource(material)); Assert.IsTrue(TracksDirtySource(mesh)); Assert.IsTrue(TracksDirtySource(prefab));
        Assert.IsFalse(TracksDirtySource(null));
    }

    [Test] public void ImportedSourceAndMetaChangesStillChangePublishedIdentity()
    {
        var source=Part("Assets/Models/TruckBody.fbx","source-v1");
        var meta=Part("Assets/Models/TruckBody.fbx.meta","import-settings-v1");
        string original=Manifest(source,meta);
        Assert.AreNotEqual(original,Manifest(Part(source.Key,"source-v2"),meta));
        Assert.AreNotEqual(original,Manifest(source,Part(meta.Key,"import-settings-v2")));
    }

    [TestCase("Assets/Scenes/Neighborhood.unity","DefaultImporter",false)]
    [TestCase("Assets/Scenes/Neighborhood.unity","NativeFormatImporter",false)]
    [TestCase("Assets/Scenes/DepotRow.UNITY","NativeFormatImporter",false)]
    [TestCase("Assets/Models/TruckBody.fbx","ModelImporter",false)]
    [TestCase("Assets/UI/map/town_map.png","TextureImporter",false)]
    [TestCase("Assets/Resources/City/CoastalCity.json","TextScriptImporter",false)]
    [TestCase("Assets/Scripts/World/TownPlacedAsset.cs","MonoImporter",false)]
    [TestCase("Assets/Resources/StreetGraph.asset","NativeFormatImporter",true)]
    [TestCase("Assets/Prefabs/TownAuthoring/Materials/stone.mat","NativeFormatImporter",true)]
    [TestCase("Assets/Prefabs/TownAuthoring/Materials/stone.mat","AssetImporter",true)]
    [TestCase("Assets/Prefabs/TownAuthoring/Meshes/gable.asset","AssetImporter",true)]
    [TestCase("Assets/Prefabs/TownAuthoring/Kits/warehouse.prefab","PrefabImporter",true)]
    [TestCase(null,"NativeFormatImporter",false)]
    public void DirtyScanNeverLoadsScenesOrForeignImportOutputs(string path,string importer,bool expected)
    {
        var method=Type.GetType("TownWorldSignatureBuilder, Assembly-CSharp-Editor",true)
            .GetMethod("ShouldInspectNativeObjects",BindingFlags.Public|BindingFlags.Static);
        Assert.AreEqual(expected,method.Invoke(null,new object[]{path,importer}));
    }

    [Test] public void ActualProjectImportersChooseTheCorrectDirtyScan()
    {
        var method=Type.GetType("TownWorldSignatureBuilder, Assembly-CSharp-Editor",true)
            .GetMethod("ShouldInspectNativeObjects",BindingFlags.Public|BindingFlags.Static);
        foreach(string path in new[]{"Assets/Scenes/Neighborhood.unity","Assets/Models/TruckBody.fbx"})
        {
            var importer=AssetImporter.GetAtPath(path); Assert.NotNull(importer,path);
            Assert.IsFalse((bool)method.Invoke(null,new object[]{path,importer.GetType().Name}),path);
        }
        foreach(string path in new[]{"Assets/Prefabs/TownAuthoring/Materials/stone.mat","Assets/Prefabs/TownAuthoring/Kits/warehouse.prefab"})
        {
            var importer=AssetImporter.GetAtPath(path); Assert.NotNull(importer,path);
            Assert.IsTrue((bool)method.Invoke(null,new object[]{path,importer.GetType().Name}),path);
        }
    }
}
