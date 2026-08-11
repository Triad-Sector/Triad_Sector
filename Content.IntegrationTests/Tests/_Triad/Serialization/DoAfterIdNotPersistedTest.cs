// SPDX-FileCopyrightText: 2026 Triad Sector contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Reflection;
using Content.Shared.DoAfter;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Content.IntegrationTests.Tests._Triad.Serialization;

/// <summary>
/// Guards the whole family of bugs behind a live ship-save outage: a <see cref="DoAfterId"/> reaching
/// a persisted data field.
///
/// A do-after id is a runtime handle, a user plus an index into that user's live do-after list. It has
/// no type serializer and no data definition, so the serializer throws "No type serializer or data
/// definition found" the moment it is asked to write one. A component that marks such a field
/// [DataField] therefore hard-fails map serialization as soon as the field goes non-null, taking the
/// entire ship-grid save with it. In production this was MagicMirror on a pair of barber scissors,
/// which blocked every save of the grid it sat on.
///
/// Eight components carried that mistake, all inherited. The point of this test is that the ninth
/// cannot land quietly, including by arriving through an upstream merge.
/// </summary>
[TestFixture]
[TestOf(typeof(DoAfterId))]
public sealed class DoAfterIdNotPersistedTest
{
    [Test]
    public async Task NoRegisteredComponentPersistsADoAfterHandle()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var componentFactory = server.ResolveDependency<IComponentFactory>();

        var offenders = new List<string>();

        foreach (var registration in componentFactory.GetAllRegistrations())
        {
            foreach (var (memberName, memberType) in DataFieldMembers(registration.Type))
            {
                if (ReachesDoAfterId(memberType, new HashSet<Type>()))
                    offenders.Add($"{registration.Name}.{memberName} ({registration.Type.Name})");
            }
        }

        Assert.That(offenders, Is.Empty,
            "These data fields persist a runtime do-after handle, which throws on any map or ship save " +
            "once the field goes non-null. Drop the [DataField] and keep the member as [ViewVariables] " +
            $"runtime state:\n  {string.Join("\n  ", offenders)}");

        // Without this the pair dirty-disposes, which NUnit reports as a skip rather than a pass. A
        // guard test that silently skips is worse than no guard at all.
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Control for the sweep above. An empty offender list is only meaningful if the walk can actually
    /// find a handle, and a reflection walk that silently returns nothing looks exactly like a clean
    /// codebase. This asserts the detector fires on a type built to be caught, and holds its fire on the
    /// same type without the attribute.
    /// </summary>
    [Test]
    public void DetectorFindsAPlantedHandle()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HasPersistedHandle(typeof(PlantedOffender)), Is.True,
                "The detector missed a direct [DataField] do-after handle, so the sweep proves nothing.");

            Assert.That(HasPersistedHandle(typeof(PlantedNestedOffender)), Is.True,
                "The detector missed a handle reached through a nested data definition.");

            Assert.That(HasPersistedHandle(typeof(PlantedCollectionOffender)), Is.True,
                "The detector missed a handle held in a collection.");

            Assert.That(HasPersistedHandle(typeof(RuntimeOnlyControl)), Is.False,
                "The detector flagged a handle that is runtime-only state, so it would fail the fixed code.");
        });
    }

    private static bool HasPersistedHandle(Type type)
    {
        foreach (var (_, memberType) in DataFieldMembers(type))
        {
            if (ReachesDoAfterId(memberType, new HashSet<Type>()))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Yields every member the serializer would write, walking the base chain by hand because
    /// GetFields does not return private fields declared on a base type.
    /// </summary>
    private static IEnumerable<(string Name, Type Type)> DataFieldMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.Instance
                                   | BindingFlags.DeclaredOnly;

        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(flags))
            {
                if (IsDataField(field))
                    yield return (field.Name, field.FieldType);
            }

            foreach (var property in current.GetProperties(flags))
            {
                if (IsDataField(property))
                    yield return (property.Name, property.PropertyType);
            }
        }
    }

    /// <summary>
    /// Both [DataField] and [IncludeDataField] derive from DataFieldBaseAttribute, and both put the
    /// member on the serializer's write path.
    /// </summary>
    private static bool IsDataField(MemberInfo member)
    {
        foreach (var attribute in member.GetCustomAttributes(true))
        {
            if (attribute is DataFieldBaseAttribute)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True if the serializer could reach a <see cref="DoAfterId"/> through this type: directly, through
    /// a nullable, through a collection or array element, or through a nested data definition's own
    /// fields. The visited set both terminates cycles and keeps repeat work down.
    /// </summary>
    private static bool ReachesDoAfterId(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
            return false;

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(DoAfterId))
            return true;

        if (underlying.IsArray)
            return underlying.GetElementType() is { } element && ReachesDoAfterId(element, visited);

        if (underlying.IsGenericType)
        {
            foreach (var argument in underlying.GetGenericArguments())
            {
                if (ReachesDoAfterId(argument, visited))
                    return true;
            }
        }

        foreach (var (_, memberType) in DataFieldMembers(underlying))
        {
            if (ReachesDoAfterId(memberType, visited))
                return true;
        }

        return false;
    }

}

// The planted types below sit at namespace scope rather than inside the fixture because a data
// definition must be partial, and a nested one would force the fixture to be partial with it.

/// <summary>
/// A handle persisted directly, which is the shape the eight fixed components had.
/// </summary>
[DataDefinition]
internal sealed partial class PlantedOffender
{
    [DataField]
    public DoAfterId? Handle = null;
}

/// <summary>
/// A handle reached only through a nested data definition, which serializes just the same.
/// </summary>
[DataDefinition]
internal sealed partial class PlantedNestedOffender
{
    [DataField]
    public PlantedOffender Nested = new();
}

/// <summary>
/// A handle held in a collection, which the write path walks element by element.
/// </summary>
[DataDefinition]
internal sealed partial class PlantedCollectionOffender
{
    [DataField]
    public List<DoAfterId> Handles = new();
}

/// <summary>
/// Shaped like the fixed components: the handle is still there, it is simply not persisted. This is
/// the negative control, and a detector that flags it would fail the corrected codebase.
/// </summary>
internal sealed class RuntimeOnlyControl
{
    [ViewVariables]
    public DoAfterId? Handle = null;
}
