using System.Numerics;
using Content.Shared.CCVar;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Preferences;
using Robust.Client.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Humanoid;

public sealed partial class HumanoidAppearanceSystem : SharedHumanoidAppearanceSystem
{
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private MarkingManager _markingManager = default!;
    [Dependency] private IConfigurationManager _configurationManager = default!;
    [Dependency] private SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HumanoidAppearanceComponent, AfterAutoHandleStateEvent>(OnHandleState);
        Subs.CVar(_configurationManager, CCVars.AccessibilityClientCensorNudity, OnCvarChanged, true);
        Subs.CVar(_configurationManager, CCVars.AccessibilityServerCensorNudity, OnCvarChanged, true);
    }

    private void OnHandleState(EntityUid uid, HumanoidAppearanceComponent component, ref AfterAutoHandleStateEvent args)
    {
        UpdateSprite(uid, component, Comp<SpriteComponent>(uid));
    }

    private void OnCvarChanged(bool value)
    {
        var humanoidQuery = EntityManager.AllEntityQueryEnumerator<HumanoidAppearanceComponent, SpriteComponent>();
        while (humanoidQuery.MoveNext(out var uid, out var humanoidComp, out var spriteComp))
        {
            UpdateSprite(uid, humanoidComp, spriteComp);
        }
    }

    private void UpdateSprite(EntityUid uid, HumanoidAppearanceComponent component, SpriteComponent sprite)
    {
        UpdateLayers(uid, component, sprite);
        ApplyMarkingSet(uid, component, sprite);

        sprite[_sprite.LayerMapReserve((uid, sprite), HumanoidVisualLayers.Eyes)].Color = component.EyeColor;
    }

    private static bool IsHidden(HumanoidAppearanceComponent humanoid, HumanoidVisualLayers layer)
        => humanoid.HiddenLayers.ContainsKey(layer) || humanoid.PermanentlyHidden.Contains(layer);

    private void UpdateLayers(EntityUid uid, HumanoidAppearanceComponent component, SpriteComponent sprite)
    {
        var oldLayers = new HashSet<HumanoidVisualLayers>(component.BaseLayers.Keys);
        component.BaseLayers.Clear();

        // add default species layers
        var speciesProto = _prototypeManager.Index(component.Species);
        var baseSprites = _prototypeManager.Index<HumanoidSpeciesBaseSpritesPrototype>(speciesProto.SpriteSet);
        foreach (var (key, id) in baseSprites.Sprites)
        {
            oldLayers.Remove(key);
            if (!component.CustomBaseLayers.ContainsKey(key))
                SetLayerData(uid, component, sprite, key, id, sexMorph: true);
        }

        // add custom layers
        foreach (var (key, info) in component.CustomBaseLayers)
        {
            oldLayers.Remove(key);
            // Shitmed Change: For whatever reason these weren't actually ignoring the skin color as advertised.
            SetLayerData(uid, component, sprite, key, info.Id, sexMorph: false, color: info.Color, overrideSkin: true);
        }

        // hide old layers
        // TODO maybe just remove them altogether?
        foreach (var key in oldLayers)
        {
            if (_sprite.LayerMapTryGet((uid, sprite), key, out var index, false))
                sprite[index].Visible = false;
        }
    }

    private void SetLayerData(
        EntityUid uid,
        HumanoidAppearanceComponent component,
        SpriteComponent sprite,
        HumanoidVisualLayers key,
        string? protoId,
        bool sexMorph = false,
        Color? color = null,
        bool overrideSkin = false) // Shitmed Change
    {
        var layerIndex = _sprite.LayerMapReserve((uid, sprite), key);
        var layer = sprite[layerIndex];
        layer.Visible = !IsHidden(component, key);

        if (color != null)
            layer.Color = color.Value;

        if (protoId == null)
            return;

        if (sexMorph)
            protoId = HumanoidVisualLayersExtension.GetSexMorph(key, component.Sex, protoId);

        var proto = _prototypeManager.Index<HumanoidSpeciesSpriteLayer>(protoId);
        component.BaseLayers[key] = proto;

        if (proto.MatchSkin && !overrideSkin) // Shitmed Change
            layer.Color = component.SkinColor.WithAlpha(proto.LayerAlpha);

        if (proto.BaseSprite != null)
            _sprite.LayerSetSprite((uid, sprite), layerIndex, proto.BaseSprite);
    }

    /// <summary>
    ///     Loads a profile directly into a humanoid.
    /// </summary>
    /// <param name="uid">The humanoid entity's UID</param>
    /// <param name="profile">The profile to load.</param>
    /// <param name="humanoid">The humanoid entity's humanoid component.</param>
    /// <remarks>
    ///     This should not be used if the entity is owned by the server. The server will otherwise
    ///     override this with the appearance data it sends over.
    /// </remarks>
    public override void LoadProfile(EntityUid uid, HumanoidCharacterProfile? profile, HumanoidAppearanceComponent? humanoid = null)
    {
        if (profile == null)
            return;

        if (!Resolve(uid, ref humanoid))
        {
            return;
        }

        var customBaseLayers = new Dictionary<HumanoidVisualLayers, CustomBaseLayerInfo>();

        var speciesPrototype = _prototypeManager.Index<SpeciesPrototype>(profile.Species);
        var markings = new MarkingSet(speciesPrototype.MarkingPoints, _markingManager, _prototypeManager);

        // Add markings that doesn't need coloring. We store them until we add all other markings that doesn't need it.
        var markingFColored = new Dictionary<Marking, MarkingPrototype>();
        foreach (var marking in profile.Appearance.Markings)
        {
            if (_markingManager.TryGetMarking(marking, out var prototype))
            {
                if (!prototype.ForcedColoring)
                {
                    markings.AddBack(prototype.MarkingCategory, marking);
                }
                else
                {
                    markingFColored.Add(marking, prototype);
                }
            }
        }

        // legacy: remove in the future?
        //markings.RemoveCategory(MarkingCategories.Hair);
        //markings.RemoveCategory(MarkingCategories.FacialHair);

        // We need to ensure hair before applying it or coloring can try depend on markings that can be invalid
        var hairColor = _markingManager.MustMatchSkin(profile.Species, HumanoidVisualLayers.Hair, out var hairAlpha, _prototypeManager)
            ? profile.Appearance.SkinColor.WithAlpha(hairAlpha)
            : profile.Appearance.HairColor;
        var hair = new Marking(profile.Appearance.HairStyleId,
            new[] { hairColor });

        var facialHairColor = _markingManager.MustMatchSkin(profile.Species, HumanoidVisualLayers.FacialHair, out var facialHairAlpha, _prototypeManager)
            ? profile.Appearance.SkinColor.WithAlpha(facialHairAlpha)
            : profile.Appearance.FacialHairColor;
        var facialHair = new Marking(profile.Appearance.FacialHairStyleId,
            new[] { facialHairColor });

        if (_markingManager.CanBeApplied(profile.Species, profile.Sex, hair, _prototypeManager))
        {
            markings.AddBack(MarkingCategories.Hair, hair);
        }
        if (_markingManager.CanBeApplied(profile.Species, profile.Sex, facialHair, _prototypeManager))
        {
            markings.AddBack(MarkingCategories.FacialHair, facialHair);
        }

        // Finally adding marking with forced colors
        foreach (var (marking, prototype) in markingFColored)
        {
            var markingColors = MarkingColoring.GetMarkingLayerColors(
                prototype,
                profile.Appearance.SkinColor,
                profile.Appearance.EyeColor,
                markings
            );
            markings.AddBack(prototype.MarkingCategory, new Marking(marking.MarkingId, markingColors));
        }

        markings.EnsureSpecies(profile.Species, profile.Appearance.SkinColor, _markingManager, _prototypeManager);
        markings.EnsureSexes(profile.Sex, _markingManager);
        markings.EnsureDefault(
            profile.Appearance.SkinColor,
            profile.Appearance.EyeColor,
            _markingManager);

        DebugTools.Assert(IsClientSide(uid));

        humanoid.MarkingSet = markings;
        humanoid.PermanentlyHidden = new HashSet<HumanoidVisualLayers>();
        humanoid.HiddenLayers = new Dictionary<HumanoidVisualLayers, SlotFlags>();
        humanoid.CustomBaseLayers = customBaseLayers;
        humanoid.Sex = profile.Sex;
        humanoid.Gender = profile.Gender;
        humanoid.Age = profile.Age;
        humanoid.Species = profile.Species;
        humanoid.SkinColor = profile.Appearance.SkinColor;
        humanoid.EyeColor = profile.Appearance.EyeColor;
        humanoid.Height = profile.Appearance.Height;
        humanoid.Width = profile.Appearance.Width;

        // Apply scaling for client-side preview (width, height)
        var sprite = Comp<SpriteComponent>(uid);
        // Check to prevent sprite scale errors for old profiles
        var width = profile.Appearance.Width <= 0.005f ? 1.0f : profile.Appearance.Width;
        var height = profile.Appearance.Height <= 0.005f ? 1.0f : profile.Appearance.Height;
        _sprite.SetScale((uid, sprite), new Vector2(width, height));

        UpdateSprite(uid, humanoid, Comp<SpriteComponent>(uid));
    }

    private void ApplyMarkingSet(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        // I am lazy and I CBF resolving the previous mess, so I'm just going to nuke the markings.
        // Really, markings should probably be a separate component altogether.
        ClearAllMarkings(uid, humanoid, sprite);

        var censorNudity = _configurationManager.GetCVar(CCVars.AccessibilityClientCensorNudity) ||
                           _configurationManager.GetCVar(CCVars.AccessibilityServerCensorNudity);
        // The reason we're splitting this up is in case the character already has undergarment equipped in that slot.
        var applyUndergarmentTop = censorNudity;
        var applyUndergarmentBottom = censorNudity;

        foreach (var markingList in humanoid.MarkingSet.Markings.Values)
        {
            foreach (var marking in markingList)
            {
                if (_markingManager.TryGetMarking(marking, out var markingPrototype))
                {
                    ApplyMarking(uid, markingPrototype, marking.MarkingColors, marking.Visible, humanoid, sprite);
                    if (markingPrototype.BodyPart == HumanoidVisualLayers.UndergarmentTop)
                        applyUndergarmentTop = false;
                    else if (markingPrototype.BodyPart == HumanoidVisualLayers.UndergarmentBottom)
                        applyUndergarmentBottom = false;
                }
            }
        }

        humanoid.ClientOldMarkings = new MarkingSet(humanoid.MarkingSet);

        AddUndergarments(uid, humanoid, sprite, applyUndergarmentTop, applyUndergarmentBottom);
    }

    private void ClearAllMarkings(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite)
    {
        foreach (var markingList in humanoid.ClientOldMarkings.Markings.Values)
        {
            foreach (var marking in markingList)
            {
                RemoveMarking(uid, marking, sprite);
            }
        }

        humanoid.ClientOldMarkings.Clear();

        foreach (var markingList in humanoid.MarkingSet.Markings.Values)
        {
            foreach (var marking in markingList)
            {
                RemoveMarking(uid, marking, sprite);
            }
        }
    }

    private void RemoveMarking(EntityUid uid, Marking marking, SpriteComponent spriteComp)
    {
        if (!_markingManager.TryGetMarking(marking, out var prototype))
        {
            return;
        }

        foreach (var sprite in prototype.Sprites)
        {
            if (sprite is not SpriteSpecifier.Rsi rsi)
            {
                continue;
            }

            var layerId = $"{marking.MarkingId}-{rsi.RsiState}";
            if (!_sprite.LayerMapTryGet((uid, spriteComp), layerId, out var index, false))
            {
                continue;
            }

            _sprite.LayerMapRemove((uid, spriteComp), layerId);
            _sprite.RemoveLayer((uid, spriteComp), index);
        }
    }

    private void AddUndergarments(EntityUid uid, HumanoidAppearanceComponent humanoid, SpriteComponent sprite, bool undergarmentTop, bool undergarmentBottom)
    {
        if (undergarmentTop && humanoid.UndergarmentTop != null)
        {
            var marking = new Marking(humanoid.UndergarmentTop, new List<Color> { new Color() });
            if (_markingManager.TryGetMarking(marking, out var prototype))
            {
                // Markings are added to ClientOldMarkings because otherwise it causes issues when toggling the feature on/off.
                humanoid.ClientOldMarkings.Markings.Add(MarkingCategories.UndergarmentTop, new List<Marking>{ marking });
                ApplyMarking(uid, prototype, null, true, humanoid, sprite);
            }
        }

        if (undergarmentBottom && humanoid.UndergarmentBottom != null)
        {
            var marking = new Marking(humanoid.UndergarmentBottom, new List<Color> { new Color() });
            if (_markingManager.TryGetMarking(marking, out var prototype))
            {
                humanoid.ClientOldMarkings.Markings.Add(MarkingCategories.UndergarmentBottom, new List<Marking>{ marking });
                ApplyMarking(uid, prototype, null, true, humanoid, sprite);
            }
        }
    }

    private void ApplyMarking(
        EntityUid uid,
        MarkingPrototype markingPrototype,
        IReadOnlyList<Color>? colors,
        bool visible,
        HumanoidAppearanceComponent humanoid,
        SpriteComponent sprite
        )
    {
        // FLOOF ADD START
        // make a handy dict of filename -> colors
        // cus we might need to access it by filename to link
        // one sprite's colors to another
        var colorDict = new Dictionary<string, Color>();
        for (var i = 0; i < markingPrototype.Sprites.Count; i++)
        {
            var spriteName = markingPrototype.Sprites[i] switch
            {
                SpriteSpecifier.Rsi rsi => rsi.RsiState,
                SpriteSpecifier.Texture texture => texture.TexturePath.Filename,
                _ => null
            };

            if (spriteName != null)
            {
                if (colors != null && i < colors.Count)
                    colorDict.Add(spriteName, colors[i]);
                else
                    colorDict.Add(spriteName, Color.White);
            }
        }
        // now, rearrange them, copying any parented colors to children set to
        // inherit them
        if (markingPrototype.ColorLinks != null)
        {
            foreach (var (child, parent) in markingPrototype.ColorLinks)
            {
                if (colorDict.TryGetValue(parent, out var color))
                {
                    colorDict[child] = color;
                }
            }
        }
        // and, since we can't rely on the iterator knowing where the heck to put
        // each sprite when we have one marking setting multiple layers,
        // lets just kinda sorta do that ourselves
        var layerDict = new Dictionary<string, int>();

        for (var j = 0; j < markingPrototype.Sprites.Count; j++)
        {
            // FLOOF CHANGE START
            var markingSprite = markingPrototype.Sprites[j];
            if (markingSprite is not SpriteSpecifier.Rsi rsi)
            {
                continue;
            }

            var layerSlot = markingPrototype.BodyPart;
            // first, try to see if there are any custom layers for this marking
            if (markingPrototype.Layering != null)
            {
                var name = rsi.RsiState;
                if (markingPrototype.Layering.TryGetValue(name, out var layerName))
                {
                    layerSlot = Enum.Parse<HumanoidVisualLayers>(layerName);
                }
            }
            // update the layerDict
            // if it doesnt have this, add it at 0, otherwise increment it
            if (layerDict.TryGetValue(layerSlot.ToString(), out var layerIndex))
            {
                layerDict[layerSlot.ToString()] = layerIndex + 1;
            }
            else
            {
                layerDict.Add(layerSlot.ToString(), 0);
            }

            if (!_sprite.LayerMapTryGet((uid, sprite), layerSlot, out var targetLayer, false))
            {
                continue;
            }

            visible &= !IsHidden(humanoid, markingPrototype.BodyPart);
            visible &= humanoid.BaseLayers.TryGetValue(markingPrototype.BodyPart, out var setting)
               && setting.AllowsMarkings;

            var layerId = $"{markingPrototype.ID}-{rsi.RsiState}";
            // FLOOF CHANGE END

            if (!_sprite.LayerMapTryGet((uid, sprite), layerId, out _, false))
            {
                // for layers that are supposed to be behind everything,
                // adding 1 to the layer index makes it not be behind
                // everything. fun! FLOOF ADD =3
                // var targLayerAdj = targetLayer == 0 ? 0 + j : targetLayer + j + 1;
                var targLayerAdj = targetLayer + layerDict[layerSlot.ToString()] + 1;
                var layer = _sprite.AddLayer((uid, sprite), markingSprite, targLayerAdj);
                _sprite.LayerMapSet((uid, sprite), layerId, layer);
                _sprite.LayerSetSprite((uid, sprite), layerId, rsi);
            }
		    // impstation edit begin - check if there's a shader defined in the markingPrototype's shader datafield, and if there is...
			if (markingPrototype.Shader != null)
			{
			// use spriteComponent's layersetshader function to set the layer's shader to that which is specified.
				sprite.LayerSetShader(layerId, markingPrototype.Shader);
			}
			// impstation edit end
            _sprite.LayerSetVisible((uid, sprite), layerId, visible);

            if (!visible || setting == null) // this is kinda implied
            {
                continue;
            }

            // Okay so if the marking prototype is modified but we load old marking data this may no longer be valid
            // and we need to check the index is correct.
            // So if that happens just default to white?
            // FLOOF ADD =3
            _sprite.LayerSetColor((uid, sprite), layerId, colorDict.TryGetValue(rsi.RsiState, out var color) ? color : Color.White);

            // FLOOF CHANGE
            // if (colors != null && j < colors.Count)
            // {
            //     sprite.LayerSetColor(layerId, colors[j]);
            // }
            // else
            // {
            //     sprite.LayerSetColor(layerId, Color.White);            // }
        }
    }

    public override void SetSkinColor(EntityUid uid, Color skinColor, bool sync = true, bool verify = true, HumanoidAppearanceComponent? humanoid = null)
    {
        if (!Resolve(uid, ref humanoid) || humanoid.SkinColor == skinColor)
            return;

        base.SetSkinColor(uid, skinColor, false, verify, humanoid);

        if (!TryComp(uid, out SpriteComponent? sprite))
            return;

        foreach (var (layer, spriteInfo) in humanoid.BaseLayers)
        {
            if (!spriteInfo.MatchSkin)
                continue;

            var index = _sprite.LayerMapReserve((uid, sprite), layer);
            sprite[index].Color = skinColor.WithAlpha(spriteInfo.LayerAlpha);
        }
    }

    public override void SetLayerVisibility(
        Entity<HumanoidAppearanceComponent> ent,
        HumanoidVisualLayers layer,
        bool visible,
        SlotFlags? slot,
        ref bool dirty)
    {
        base.SetLayerVisibility(ent, layer, visible, slot, ref dirty);

        var sprite = Comp<SpriteComponent>(ent);
        if (!_sprite.LayerMapTryGet((ent.Owner, sprite), layer, out var index, false))
        {
            if (!visible)
                return;
            index = _sprite.LayerMapReserve((ent.Owner, sprite), layer);
        }

        var spriteLayer = sprite[index];
        if (spriteLayer.Visible == visible)
            return;

        spriteLayer.Visible = visible;

        // I fucking hate this. I'll get around to refactoring sprite layers eventually I swear
        // Just a week away...

        foreach (var markingList in ent.Comp.MarkingSet.Markings.Values)
        {
            foreach (var marking in markingList)
            {
                if (_markingManager.TryGetMarking(marking, out var markingPrototype) && markingPrototype.BodyPart == layer)
                    ApplyMarking(ent.Owner, markingPrototype, marking.MarkingColors, marking.Visible, ent, sprite);
            }
        }
    }
}
