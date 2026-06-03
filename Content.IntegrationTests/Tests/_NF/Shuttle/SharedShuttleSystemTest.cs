using Content.IntegrationTests.Pair;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NF.Shuttle;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class ServiceFlagsSuffixTests
{
    TestPair _pair;
    SharedShuttleSystem _shuttle;

    [SetUp]
    public async Task Setup()
    {
        _pair = await PoolManager.GetServerClient();
        var server = _pair.Server;

        var entManager = server.ResolveDependency<IEntityManager>();
        _shuttle = entManager.System<SharedShuttleSystem>();
    }

    [TearDown]
    public async Task TearDownInternal()
    {
        await _pair.CleanReturnAsync();
    }

    [Test]
    public void GetServiceFlagsPrefix_None_ReturnsEmptyString()
    {
        var result = _shuttle.GetServiceFlagsPrefix(ServiceFlags.None);
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void GetServiceFlagsPrefix_SingleFlag_ReturnsSingleCharacter()
    {
        var result = _shuttle.GetServiceFlagsPrefix(ServiceFlags.Services);
        Assert.That(result.Length, Is.Positive);
    }

    [Test]
    public void GetServiceFlagsPrefix_MultipleFlags_ReturnsDistinctShortforms()
    {
        // Assemble all enum values into one
        var valueCount = 0;
        var allFlags = ServiceFlags.None;
        foreach (var flag in Enum.GetValues<ServiceFlags>())
        {
            if (flag == ServiceFlags.None)
                continue;
            allFlags |= flag;
            valueCount++;
        }

        // Extract the characters between brackets
        var characters = _shuttle.GetServiceFlagsPrefix(allFlags).Trim('[', ']', ' ');

        // Check that we have three separate character combinations.
        Assert.Multiple(() =>
        {
            Assert.That(characters, Is.Unique);
            Assert.That(characters.Length, Is.EqualTo(valueCount));

            foreach (var flag in Enum.GetValues<ServiceFlags>())
            {
                if (flag == ServiceFlags.None)
                    continue;

                var oneFlagResult = _shuttle.GetServiceFlagsPrefix(flag);
                // Extract the characters between brackets
                var oneFlagCharacters = oneFlagResult.Trim('[', ']', ' ');
                // Check that we have three separate character combination.
                Assert.That(oneFlagCharacters.Length, Is.EqualTo(1));
                Assert.That(characters.Contains(oneFlagCharacters[0]), Is.True);
            }
        });
    }
}
