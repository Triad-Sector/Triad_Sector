using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;

namespace Content.Client.Interactable.Components
{
    [RegisterComponent]
    public sealed partial class InteractionOutlineComponent : Component
    {
        [Dependency] private IPrototypeManager _prototypeManager = default!;
        [Dependency] private IEntityManager _entMan = default!;

        // Not a [Dependency]: components inject from the IoC container, which has no entity systems.
        // A [Dependency] SpriteSystem here generates an Inject() that throws on every entity spawn.
        private SpriteSystem Sprite => _entMan.System<SpriteSystem>();

        private const float DefaultWidth = 1;
        private const string PostShaderId = "InteractionOutline";

        private static readonly ProtoId<ShaderPrototype> ShaderInRange = "SelectionOutlineInrange";

        private static readonly ProtoId<ShaderPrototype> ShaderOutOfRange = "SelectionOutline";

        private bool _inRange;
        private ShaderInstance? _shader;
        private int _lastRenderScale;

        public void OnMouseEnter(EntityUid uid, bool inInteractionRange, int renderScale)
        {
            _lastRenderScale = renderScale;
            _inRange = inInteractionRange;
            if (_entMan.TryGetComponent(uid, out SpriteComponent? sprite) && !Sprite.HasPostShader((uid, sprite), PostShaderId))
            {
                // TODO why is this creating a new instance of the outline shader every time the mouse enters???
                _shader = MakeNewShader(inInteractionRange, renderScale);
                Sprite.SetPostShader((uid, sprite), new SpriteComponent.PostShaderArgs(PostShaderId, _shader));
            }
        }

        public void OnMouseLeave(EntityUid uid)
        {
            if (_entMan.TryGetComponent(uid, out SpriteComponent? sprite))
            {
                if (Sprite.TryGetPostShader((uid, sprite), PostShaderId, out var entry) && entry.Shader == _shader)
                    Sprite.RemovePostShader((uid, sprite), PostShaderId);
                sprite.RenderOrder = 0;
            }

            _shader?.Dispose();
            _shader = null;
        }

        public void UpdateInRange(EntityUid uid, bool inInteractionRange, int renderScale)
        {
            if (_entMan.TryGetComponent(uid, out SpriteComponent? sprite)
                && Sprite.TryGetPostShader((uid, sprite), PostShaderId, out var entry)
                && entry.Shader == _shader
                && (inInteractionRange != _inRange || _lastRenderScale != renderScale))
            {
                _inRange = inInteractionRange;
                _lastRenderScale = renderScale;

                _shader = MakeNewShader(_inRange, _lastRenderScale);
                Sprite.SetPostShader((uid, sprite), new SpriteComponent.PostShaderArgs(PostShaderId, _shader));
            }
        }

        private ShaderInstance MakeNewShader(bool inRange, int renderScale)
        {
            var shaderName = inRange ? ShaderInRange : ShaderOutOfRange;

            var instance = _prototypeManager.Index<ShaderPrototype>(shaderName).InstanceUnique();
            instance.SetParameter("outline_width", DefaultWidth * renderScale);
            return instance;
        }
    }
}
