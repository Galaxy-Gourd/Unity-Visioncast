using System.Collections.Generic;
using UnityEngine;
using GalaxyGourd.Visioncast;

namespace VisioncastSamples.InteractionFocus
{
    /// <summary>
    /// "Look to interact" demo for <see cref="VisioncastSourceFiltered.TargetedObject"/>. An interactor faces a
    /// fan of props; several are in view at once, but only the ONE nearest the centre of the view is "focused" -
    /// it glows, grows, and lifts. Aim left/right with the slider and the focus hops from prop to prop; click
    /// Interact to act on whatever is focused. This is the single-object disambiguation an interaction system
    /// needs: not "what can I see" but "what am I pointing at".
    /// </summary>
    [DefaultExecutionOrder(-500)]
    [AddComponentMenu("Visioncast/Samples/Interaction Focus Sample")]
    public class InteractionFocusSample : MonoBehaviour
    {
        #region CONFIG

        [Header("Pipeline")]
        [SerializeField] private float _gridCellSize = 25f;

        [Header("Interactor")]
        [SerializeField] private Vector3 _interactorPos = new Vector3(0f, 1f, -6f);
        [SerializeField] private float _range = 20f;
        [SerializeField] private float _fov = 48f;      // vision cone HALF-angle
        [SerializeField] private float _aimLimit = 46f; // max |aim| yaw

        [Header("Props")]
        [SerializeField] private float _arcRadius = 7f;
        [SerializeField] private float _arcHalf = 44f;  // half-spread of the prop fan, degrees

        [SerializeField] private Color _coneColor = new Color(0.3f, 0.85f, 1f);

        private const int PropLayer = 3;  // interactable props (what the interactor looks for)
        private const int InertLayer = 2; // Ignore Raycast - ground, interactor body

        #endregion CONFIG


        #region STATE

        private InteractorVision _interactor;
        private Material _coneMat;
        private readonly List<Interactable> _props = new();
        private readonly List<Collider> _propColliders = new();
        private readonly Dictionary<Collider, Interactable> _byCollider = new();
        private readonly List<Material> _ownedMats = new();

        private static readonly int _colorId = Shader.PropertyToID("_BaseColor");
        private static readonly int _colorIdLegacy = Shader.PropertyToID("_Color");

        private float _aimYaw;
        private Interactable _focused;
        private Interactable _lastInteracted;

        /// <summary>Aim yaw in degrees (0 = straight ahead). Clamped to +/- the aim limit.</summary>
        public float AimYaw
        {
            get => _aimYaw;
            set => _aimYaw = Mathf.Clamp(value, -_aimLimit, _aimLimit);
        }

        /// <summary>Display name of the focused prop, or "" when nothing is centred.</summary>
        public string FocusedName => _focused ? _focused.DisplayName : "";

        /// <summary>Interact with whatever is currently focused (no-op if nothing is focused).</summary>
        public void InteractWithFocused()
        {
            if (_focused)
            {
                _focused.Interact();
                _lastInteracted = _focused;
            }
        }

        #endregion STATE


        #region LIFECYCLE

        private void Awake()
        {
            VisionPipeline.UseDod(_gridCellSize);
            VisioncastManager.Setup();

            BuildEnvironment();
            BuildProps();
            BuildInteractor();
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _propColliders.Count; i++)
                if (_propColliders[i])
                    VisionTargetsManifest.Unregister(_propColliders[i]);

            VisioncastManager.Dispose();
            if (_coneMat) Destroy(_coneMat);
            for (int i = 0; i < _ownedMats.Count; i++)
                if (_ownedMats[i]) Destroy(_ownedMats[i]);
        }

        #endregion LIFECYCLE


        #region TICK

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            _interactor.transform.rotation = Quaternion.Euler(0f, _aimYaw, 0f);

            // Source moved via transform this step; push into the physics scene before the vision raycast.
            Physics.SyncTransforms();
            VisioncastManager.TickVisioncasts(dt);

            // Resolve the focused collider to its prop and update highlights.
            _focused = null;
            Collider focusCol = _interactor.Focused;
            if (focusCol)
                _byCollider.TryGetValue(focusCol, out _focused);

            for (int i = 0; i < _props.Count; i++)
                _props[i].SetFocused(_props[i] == _focused);
        }

        #endregion TICK


        #region BUILD

        private void BuildEnvironment()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.layer = InertLayer;
            ground.transform.localScale = Vector3.one * 4f;
            var gcol = ground.GetComponent<Collider>();
            if (gcol) Destroy(gcol);

            var cam = Camera.main;
            if (cam)
            {
                cam.transform.position = new Vector3(9.5f, 10f, -12f);
                cam.transform.rotation = Quaternion.LookRotation(
                    (new Vector3(0f, 0.5f, -1f) - cam.transform.position).normalized, Vector3.up);
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 120f);
            }
        }

        private static readonly (string name, PrimitiveType shape, Color color)[] _propDefs =
        {
            ("Crate",    PrimitiveType.Cube,     new Color(1f, 0.5f, 0.15f)),
            ("Terminal", PrimitiveType.Cube,     new Color(0.2f, 0.8f, 1f)),
            ("Orb",      PrimitiveType.Sphere,   new Color(0.9f, 0.3f, 0.9f)),
            ("Lever",    PrimitiveType.Cylinder, new Color(0.35f, 0.9f, 0.45f)),
            ("Relic",    PrimitiveType.Capsule,  new Color(1f, 0.85f, 0.3f)),
        };

        private void BuildProps()
        {
            int n = _propDefs.Length;
            for (int i = 0; i < n; i++)
            {
                // Fan the props evenly across the arc, all at the same distance, so ANGLE alone decides focus.
                float t = n == 1 ? 0.5f : i / (float)(n - 1);
                float yaw = Mathf.Lerp(-_arcHalf, _arcHalf, t);
                Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
                Vector3 pos = _interactorPos + rot * (Vector3.forward * _arcRadius);
                pos.y = 0.7f;

                var (label, shape, color) = _propDefs[i];
                var go = GameObject.CreatePrimitive(shape);
                go.name = label;
                go.layer = PropLayer;
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * 0.9f;

                var mat = new Material(FindOpaqueShader());
                mat.EnableKeyword("_EMISSION");
                mat.SetColor(_colorId, color);
                mat.SetColor(_colorIdLegacy, color);
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;
                _ownedMats.Add(mat);

                var prop = go.AddComponent<Interactable>();
                prop.Init(label, mat, color);

                var col = go.GetComponent<Collider>();
                VisionTargetsManifest.Register(col); // each prop stands alone (its own identity)
                _props.Add(prop);
                _propColliders.Add(col);
                _byCollider[col] = prop;
            }
        }

        private void BuildInteractor()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Interactor";
            go.SetActive(false); // configure before OnEnable registers it as a source
            go.layer = InertLayer;
            go.transform.position = _interactorPos;
            go.transform.rotation = Quaternion.identity; // faces +Z
            var col = go.GetComponent<Collider>();
            if (col) Destroy(col);

            // Own material (don't mutate the primitive's shared default material asset).
            var bodyMat = new Material(FindOpaqueShader());
            Color bodyCol = new Color(0.75f, 0.75f, 0.78f);
            bodyMat.SetColor(_colorId, bodyCol);
            bodyMat.SetColor(_colorIdLegacy, bodyCol);
            go.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
            _ownedMats.Add(bodyMat);

            _interactor = go.AddComponent<InteractorVision>();
            _interactor.Configure(1 << PropLayer, 1 << PropLayer, _range, _fov);

            go.SetActive(true);
            BuildCone(go.transform);
        }

        private void BuildCone(Transform interactor)
        {
            var coneGo = new GameObject("ViewCone");
            coneGo.transform.SetParent(interactor, false); // follows the interactor's aim
            var cone = coneGo.AddComponent<VisionCone>();
            var mr = coneGo.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _coneMat = MakeConeMaterial(_coneColor);
            mr.sharedMaterial = _coneMat;
            cone.CalculateCone(_interactor); // Range/FoV are fixed, so build the mesh once
        }

        private static Material MakeConeMaterial(Color tint)
        {
            var mat = new Material(FindUnlitShader());
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            Color c = tint; c.a = 0.14f;
            mat.SetColor("_BaseColor", c);
            mat.SetColor("_Color", c);
            return mat;
        }

        #endregion BUILD


        #region HUD

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 340, 240), GUI.skin.box);
            GUILayout.Label("<b>VisionCast - Interaction Focus (look to interact)</b>", Rich());
            GUILayout.Space(6);

            if (_focused)
            {
                Vector3 to = _focused.transform.position - _interactor.transform.position;
                float ang = Vector3.Angle(_interactor.transform.forward, to);
                GUILayout.Label($"Looking at: <b><color=#7fe0ff>{_focused.DisplayName}</color></b>   " +
                                $"({ang:0}° off-centre, {to.magnitude:0.0}m)", Rich());
            }
            else
            {
                GUILayout.Label("Looking at: <b>—</b> <i>(nothing centred)</i>", Rich());
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Aim: {_aimYaw:0}°", GUILayout.Width(70));
            AimYaw = GUILayout.HorizontalSlider(_aimYaw, -_aimLimit, _aimLimit);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUI.enabled = _focused;
            if (GUILayout.Button(_focused ? $"Interact with {_focused.DisplayName}" : "Interact"))
                InteractWithFocused();
            GUI.enabled = true;

            if (_lastInteracted)
                GUILayout.Label($"Last interacted: <b>{_lastInteracted.DisplayName}</b> " +
                                $"(x{_lastInteracted.InteractCount})", Rich());

            GUILayout.Space(6);
            GUILayout.Label("<i>Several props are in view, but only the one nearest the centre of the cone is " +
                            "focused. Drag Aim to sweep; the focus follows where you point.</i>",
                            Rich());

            GUILayout.EndArea();
        }

        private static GUIStyle _rich;
        private static GUIStyle Rich()
        {
            if (_rich == null)
                _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            return _rich;
        }

        private static Shader FindOpaqueShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit")
                   ?? Shader.Find("Standard")
                   ?? Shader.Find("Sprites/Default");
        }

        private static Shader FindUnlitShader()
        {
            return Shader.Find("Universal Render Pipeline/Unlit")
                   ?? Shader.Find("Unlit/Color")
                   ?? FindOpaqueShader();
        }

        #endregion HUD
    }
}
