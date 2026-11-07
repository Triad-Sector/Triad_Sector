// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Triad.ShipSize;
using NUnit.Framework;

namespace Content.Tests.Shared._Triad.ShipSize;

[Parallelizable]
[TestFixture]
[TestOf(typeof(ShipSizeSystem))]
public sealed class ShipSizeSystemTest
{
    [Parallelizable]
    [Test]
    [TestCase(0, ExpectedResult = ShipSizeClass.Cutter)]
    [TestCase(1, ExpectedResult = ShipSizeClass.Cutter)]
    [TestCase(96, ExpectedResult = ShipSizeClass.Cutter)]
    [TestCase(97, ExpectedResult = ShipSizeClass.Corvette)]
    [TestCase(192, ExpectedResult = ShipSizeClass.Corvette)]
    [TestCase(193, ExpectedResult = ShipSizeClass.Frigate)]
    [TestCase(384, ExpectedResult = ShipSizeClass.Frigate)]
    [TestCase(385, ExpectedResult = ShipSizeClass.Cruiser)]
    [TestCase(768, ExpectedResult = ShipSizeClass.Cruiser)]
    [TestCase(769, ExpectedResult = ShipSizeClass.Capital)]
    [TestCase(1536, ExpectedResult = ShipSizeClass.Capital)]
    [TestCase(1537, ExpectedResult = ShipSizeClass.SuperCapital)]
    [TestCase(5000, ExpectedResult = ShipSizeClass.SuperCapital)]
    public ShipSizeClass TestClassFromTileCount(int tiles)
    {
        return ShipSizeSystem.ClassFromTileCount(tiles);
    }
}
