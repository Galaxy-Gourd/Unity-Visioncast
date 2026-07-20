using UnityEngine;

namespace VisioncastSamples.InteractionFocus
{
    /// <summary>
    /// A focusable prop. Its collider is registered as a vision target; when the interactor's
    /// <see cref="InteractorVision.Focused"/> resolves to it, the sample calls <see cref="SetFocused"/> to make it
    /// glow, grow, and lift, and <see cref="Interact"/> to give it a little pop. Purely presentation - the vision
    /// system decides WHICH object is focused; this just reacts.
    /// </summary>
    public class Interactable : MonoBehaviour
    {
        public string DisplayName { get; private set; }
        public int InteractCount { get; private set; }

        private static readonly int _colorId = Shader.PropertyToID("_BaseColor");
        private static readonly int _colorIdLegacy = Shader.PropertyToID("_Color");
        private static readonly int _emissionId = Shader.PropertyToID("_EmissionColor");

        private Material _mat;
        private Color _baseColor;
        private Vector3 _restPos;
        private float _restScale;
        private bool _focused;
        private float _glow;      // 0..1 eased highlight
        private float _popTimer;  // >0 while playing the interact bounce

        public void Init(string displayName, Material mat, Color baseColor)
        {
            DisplayName = displayName;
            _mat = mat;
            _baseColor = baseColor;
            _restPos = transform.position;
            _restScale = transform.localScale.x;
        }

        public void SetFocused(bool focused) => _focused = focused;

        public void Interact()
        {
            InteractCount++;
            _popTimer = 0.45f;
        }

        private void Update()
        {
            // Ease the glow toward the focus state.
            _glow = Mathf.MoveTowards(_glow, _focused ? 1f : 0f, Time.deltaTime * 6f);

            // Colour + emission brighten with focus.
            Color c = Color.Lerp(_baseColor, Color.white, _glow * 0.35f);
            _mat.SetColor(_colorId, c);
            _mat.SetColor(_colorIdLegacy, c);
            _mat.SetColor(_emissionId, _baseColor * (_glow * 1.6f));

            // Focused props grow slightly and lift; an interact adds a short vertical bounce.
            float scale = _restScale * (1f + _glow * 0.15f);
            transform.localScale = Vector3.one * scale;

            float lift = _glow * 0.3f;
            if (_popTimer > 0f)
            {
                _popTimer -= Time.deltaTime;
                lift += Mathf.Sin((1f - _popTimer / 0.45f) * Mathf.PI) * 0.6f;
            }
            transform.position = _restPos + Vector3.up * lift;
        }
    }
}
