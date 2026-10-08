using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CoastalBackdropTests
{
    static float NorthStart(Rect townBounds, IEnumerable<Bounds> footprints)
    {
        var type = Type.GetType("NeighborhoodBuilder, Assembly-CSharp-Editor", true);
        var method = type.GetMethod("CoastalBackdropNorthStart", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (float)method.Invoke(null, new object[] { townBounds, footprints });
    }

    [Test]
    public void EntireBackdropHillStaysTwelveMetresBeyondNorthernTownBoundary()
    {
        var town = Rect.MinMaxRect(-100, -200, 1200, 950);
        float centre = NorthStart(town, Array.Empty<Bounds>());
        float southernEdge = centre - 95f * .5f;
        Assert.GreaterOrEqual(southernEdge, town.yMax + 12f);
        Assert.AreEqual(town.yMax + 12f, southernEdge, .0001f);
    }

    [Test]
    public void BackdropAlsoClearsPropertiesExtendingBeyondTownBoundary()
    {
        var town = Rect.MinMaxRect(-100, -200, 1200, 950);
        var footprints = new[]
        {
            new Bounds(new Vector3(620, 1, 960), new Vector3(60, 2, 40)),
            new Bounds(new Vector3(710, 1, 890), new Vector3(30, 2, 30)),
        };
        float centre = NorthStart(town, footprints);
        float southernEdge = centre - 95f * .5f;
        Assert.GreaterOrEqual(southernEdge, town.yMax + 12f);
        foreach (var footprint in footprints)
            Assert.GreaterOrEqual(southernEdge, footprint.max.z + 12f);
        Assert.AreEqual(footprints[0].max.z + 12f, southernEdge, .0001f);
    }
}
