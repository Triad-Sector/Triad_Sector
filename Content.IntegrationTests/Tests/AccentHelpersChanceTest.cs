using System.Text.RegularExpressions;
using Content.Server.Speech.EntitySystems;
using Robust.Shared.Random;

namespace Content.IntegrationTests.Tests;

[TestFixture]
[TestOf(typeof(AccentHelpers))]
public sealed class AccentHelpersChanceTest
{
    private static readonly Regex W = new("w", RegexOptions.IgnoreCase);

    [Test]
    public void ChanceOneAlwaysReplaces()
    {
        var rng = new RobustRandom();
        Assert.That(AccentHelpers.ReplaceCasePreserving("wow wow", W, "v", rng, 1f), Is.EqualTo("vov vov"));
    }

    [Test]
    public void ChanceZeroNeverReplaces()
    {
        var rng = new RobustRandom();
        Assert.That(AccentHelpers.ReplaceCasePreserving("wow wow", W, "v", rng, 0f), Is.EqualTo("wow wow"));
    }

    [Test]
    public void CasePreservedOnReplace()
    {
        var rng = new RobustRandom();
        Assert.That(AccentHelpers.ReplaceCasePreserving("WOW", W, "v", rng, 1f), Is.EqualTo("VOV"));
    }
}
