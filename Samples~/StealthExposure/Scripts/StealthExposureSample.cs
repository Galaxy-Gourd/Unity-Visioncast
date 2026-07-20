using System.Collections.Generic;
using UnityEngine;
using GalaxyGourd.Visioncast;

namespace VisioncastSamples.StealthExposure
{
    /// <summary>
    /// Stealth exposure as weighted light COVERAGE across several lights, with occlusion. A multi-collider
    /// player (head, torso, arms, legs) walks through two spotlights past a wall; each body part is a separate
    /// vision target, so each light reports how much of each part is lit independently. A
    /// <see cref="VisioncastSourceCompound"/> aggregates the lights (Max), giving one "how lit across all
    /// lights" coverage per part via <see cref="VisioncastSourceCompound.TryGetCoverage"/> - and the wall
    /// drops coverage where it shadows a beam even though the part is geometrically in the cone.
    ///
    /// This sample - the CONSUMER - owns the last step: it folds the per-part coverage into one exposure score
    /// using the authored per-part weights (a lit head counts far more than a lit hand). The vision package
    /// never sees a weight.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    [AddComponentMenu("Visioncast/Samples/Stealth Exposure Sample")]
    public class StealthExposureSample : MonoBehaviour
    {
        #region CONFIG

        [Header("Pipeline")]
        [SerializeField] private float _gridCellSize = 20f;

        [Header("Player walk (ping-pong along X)")]
        [SerializeField] private float _walkExtent = 8f;
        [SerializeField] private float _walkSpeed = 3.5f;

        [Header("Lights")]
        [SerializeField] private float _lightRange = 15f;
        [SerializeField] private float _lightFov = 13f; // vision cone HALF-angle

        [Header("Coverage tint")]
        [SerializeField] private Color _shadow = new Color(0.09f, 0.12f, 0.22f);
        [SerializeField] private Color _lit = new Color(1f, 0.86f, 0.45f);

        private const int PlayerLayer = 3;   // Player - what the lights look for
        private const int OccluderLayer = 0; // Default - walls that block light
        private const int InertLayer = 2;    // Ignore Raycast - ground

        private const int SmoothRes = 3;
        private const int CoarseRes = 1;
        private static readonly VisionSampleMode SampleModeUsed = VisionSampleMode.BoundsVolumeGrid;

        #endregion CONFIG


        #region STATE

        private readonly List<LightVision> _lights = new();
        private VisioncastSourceCompound _compound;
        private Transform _player;
        private readonly List<StealthBodyPart> _parts = new();
        private readonly Dictionary<Collider, StealthBodyPart> _partByCollider = new();
        private Material _partMat;
        private readonly List<Material> _coneMats = new();
        private MaterialPropertyBlock _mpb;
        private static readonly int _colorId = Shader.PropertyToID("_BaseColor");
        private static readonly int _colorIdLegacy = Shader.PropertyToID("_Color");

        private float _walkT;
        private int _walkDir = 1;
        private float _exposure;
        private bool _smooth = true;
        private bool _paused;

        /// <summary>Overall weighted exposure in [0,1].</summary>
        public float Exposure => _exposure;
        /// <summary>Pause the walk to inspect a pose.</summary>
        public bool Paused { get => _paused; set => _paused = value; }
        /// <summary>Player position along the walk axis; set to scrub while paused.</summary>
        public float PlayerX
        {
            get => _walkT;
            set { _walkT = value; if (_player) _player.position = new Vector3(_walkT, 0f, 0f); }
        }
        /// <summary>Walk speed in units/sec (driven by the HUD slider).</summary>
        public float WalkSpeed { get => _walkSpeed; set => _walkSpeed = value; }

        #endregion STATE


        #region LIFECYCLE

        private void Awake()
        {
            VisionPipeline.UseDod(_gridCellSize);

            _mpb = new MaterialPropertyBlock();
            BuildEnvironment();
            VisioncastManager.Setup();
            BuildLights();
            BuildPlayer();
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _parts.Count; i++)
                if (_parts[i] && _parts[i].Collider)
                    VisionTargetsManifest.Unregister(_parts[i].Collider);

            VisioncastManager.Dispose();
            if (_partMat)
                Destroy(_partMat);
            for (int i = 0; i < _coneMats.Count; i++)
                if (_coneMats[i])
                    Destroy(_coneMats[i]);
        }

        #endregion LIFECYCLE


        #region TICK

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            if (!_paused)
            {
                _walkT += _walkDir * _walkSpeed * dt;
                if (_walkT >= _walkExtent) { _walkT = _walkExtent; _walkDir = -1; }
                else if (_walkT <= -_walkExtent) { _walkT = -_walkExtent; _walkDir = 1; }
                _player.position = new Vector3(_walkT, 0f, 0f);
            }

            Physics.SyncTransforms();
            VisioncastManager.TickVisioncasts(dt);
            _compound.Combine(); // deterministic same-frame aggregate (auto-combine is off)

            ReadCoverageAndScore();
        }

        /// <summary>
        /// The consumer's logic: ask the compound for each body part's combined coverage across all lights,
        /// tint the part, and fold the parts into one weighted exposure. None of this is in the vision
        /// package - it reported the counts; the compound aggregated them across lights; the weighting is ours.
        /// </summary>
        private void ReadCoverageAndScore()
        {
            float weightSum = 0f, weighted = 0f;
            for (int i = 0; i < _parts.Count; i++)
            {
                StealthBodyPart p = _parts[i];
                p.LitFraction = _compound.TryGetCoverage(p.Collider, out float cov) ? cov : 0f;

                weighted += p.Weight * p.LitFraction;
                weightSum += p.Weight;

                Color c = Color.Lerp(_shadow, _lit, p.LitFraction);
                p.Renderer.GetPropertyBlock(_mpb);
                _mpb.SetColor(_colorId, c);
                _mpb.SetColor(_colorIdLegacy, c);
                p.Renderer.SetPropertyBlock(_mpb);
            }

            _exposure = weightSum > 0f ? weighted / weightSum : 0f;
        }

        #endregion TICK


        #region BUILD

        private void BuildEnvironment()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.06f, 0.07f, 0.1f);
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional)
                    l.intensity = 0.12f;

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.layer = InertLayer;
            ground.transform.localScale = Vector3.one * 4f;
            var gcol = ground.GetComponent<Collider>();
            if (gcol) Destroy(gcol);

            // A wall between the two lights and the mid path: at x~0 the player is inside both cones but the
            // wall shadows the beams, so its COVERAGE (lit) drops while its in-cone count stays high.
            MakeWall(new Vector3(0f, 1.5f, -1.6f), new Vector3(2.2f, 3f, 0.4f));

            var cam = Camera.main;
            if (cam)
            {
                cam.transform.position = new Vector3(8f, 5.5f, -9f);
                cam.transform.rotation = Quaternion.LookRotation((new Vector3(0f, 1f, 0f) - cam.transform.position).normalized);
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 90f);
            }
        }

        private void MakeWall(Vector3 pos, Vector3 size)
        {
            var w = GameObject.CreatePrimitive(PrimitiveType.Cube);
            w.name = "Wall";
            w.layer = OccluderLayer;
            w.transform.position = pos;
            w.transform.localScale = size;
        }

        private void BuildLights()
        {
            _compound = gameObject.AddComponent<VisioncastSourceCompound>();
            _compound.AutoCombine = false;                      // we combine right after the tick
            _compound.Aggregation = VisibilityAggregation.Max;  // a part is as lit as its brightest light

            AddLight(new Vector3(-5f, 6f, -4f), new Vector3(-2.4f, 1f, 0f), new Color(1f, 0.85f, 0.5f));
            AddLight(new Vector3(5f, 6f, -4f), new Vector3(2.4f, 1f, 0f), new Color(0.55f, 0.78f, 1f));
        }

        private void AddLight(Vector3 pos, Vector3 aim, Color tint)
        {
            var go = new GameObject("Spotlight");
            go.SetActive(false); // configure the source before OnEnable registers it
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((aim - pos).normalized);

            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.range = _lightRange + 8f;
            light.spotAngle = _lightFov * 2f + 10f;
            light.intensity = 18f;
            light.color = tint;
            light.shadows = LightShadows.Soft;

            var lv = go.AddComponent<LightVision>();
            // Obstruction mask includes the target layer (Player - required so a ray can hit the part and
            // count as lit) AND the occluder layer (walls, which block by being hit first).
            lv.Configure(1 << PlayerLayer, (1 << PlayerLayer) | (1 << OccluderLayer), _lightRange, _lightFov);
            lv.SetSampling(SampleModeUsed, _smooth ? SmoothRes : CoarseRes);

            go.SetActive(true);
            _lights.Add(lv);
            _compound.AddSource(lv);

            BuildLightCone(lv, tint);
        }

        /// <summary>
        /// Reuses the package's <see cref="VisionCone"/> (the debug cone-mesh builder) to draw the light's
        /// FOV/range cone as a semi-transparent volume, so the beams are visible in play. The lights are
        /// static, so the mesh is built once.
        /// </summary>
        private void BuildLightCone(LightVision lv, Color tint)
        {
            var coneGo = new GameObject("LightCone");
            coneGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var cone = coneGo.AddComponent<VisionCone>(); // auto-adds MeshFilter + MeshRenderer, runs Awake
            var mr = coneGo.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            Material mat = MakeConeMaterial(tint);
            mr.sharedMaterial = mat;
            _coneMats.Add(mat);

            cone.CalculateCone(lv);
        }

        private static Material MakeConeMaterial(Color tint)
        {
            var mat = new Material(FindUnlitShader());
            // URP/Unlit transparent, double-sided so the cone volume reads. Set the render-state properties
            // the shader actually blends with (setting _Surface alone doesn't run the material GUI).
            mat.SetFloat("_Surface", 1f);   // transparent
            mat.SetFloat("_Blend", 0f);     // alpha
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", 0f);      // render both faces
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            Color c = tint; c.a = 0.13f;
            mat.SetColor("_BaseColor", c);
            mat.SetColor("_Color", c);
            return mat;
        }

        private void BuildPlayer()
        {
            // Unlit so each part shows its computed coverage tint directly, independent of scene lighting.
            _partMat = new Material(FindUnlitShader());
            _player = new GameObject("Player").transform;
            _player.position = new Vector3(_walkT, 0f, 0f);

            AddPart("Head", 3f, new Vector3(0f, 1.68f, 0f), new Vector3(0.46f, 0.46f, 0.46f), true);
            AddPart("Torso", 2f, new Vector3(0f, 1.12f, 0f), new Vector3(0.55f, 0.85f, 0.32f), false);
            AddPart("L.Arm", 0.5f, new Vector3(-0.5f, 1.25f, 0f), new Vector3(0.5f, 0.18f, 0.18f), false);
            AddPart("R.Arm", 0.5f, new Vector3(0.5f, 1.25f, 0f), new Vector3(0.5f, 0.18f, 0.18f), false);
            AddPart("Legs", 1f, new Vector3(0f, 0.48f, 0f), new Vector3(0.45f, 0.95f, 0.32f), false);
        }

        private void AddPart(string label, float weight, Vector3 localPos, Vector3 size, bool sphere)
        {
            var go = GameObject.CreatePrimitive(sphere ? PrimitiveType.Sphere : PrimitiveType.Cube);
            go.name = label;
            go.layer = PlayerLayer;
            go.transform.SetParent(_player, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = _partMat;

            var part = go.AddComponent<StealthBodyPart>();
            part.Weight = weight;
            part.Label = label;
            part.Collider = go.GetComponent<Collider>();
            part.Renderer = go.GetComponent<Renderer>();

            // Standalone registration: each part is its own target identity, so the lights report it as a
            // separate entry (no per-actor collapse) - which is exactly what per-part weighting needs.
            VisionTargetsManifest.Register(part.Collider);
            _parts.Add(part);
            _partByCollider[part.Collider] = part;
        }

        #endregion BUILD


        #region HUD

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 380, 500), GUI.skin.box);
            GUILayout.Label("<b>VisionCast - Stealth Exposure (weighted coverage)</b>", Rich());

            GUILayout.Space(6);
            GUILayout.Label($"<b>Exposure: {_exposure:0.00}</b>  (weighted across body parts, {_lights.Count} lights)", Rich());
            Bar(_exposure, Color.Lerp(new Color(0.2f, 0.3f, 0.5f), new Color(1f, 0.85f, 0.4f), _exposure));

            GUILayout.Space(8);
            GUILayout.Label("Per-part coverage (lit across lights), weight:");
            for (int i = 0; i < _parts.Count; i++)
            {
                StealthBodyPart p = _parts[i];
                GUILayout.Label($"{p.Label}  (w {p.Weight:0.#})   {p.LitFraction:0.00}");
                Bar(p.LitFraction, new Color(0.5f, 0.85f, 0.6f));
            }

            GUILayout.Space(10);
            GUILayout.Label($"Walk speed: {_walkSpeed:0.0} units/s");
            _walkSpeed = GUILayout.HorizontalSlider(_walkSpeed, 0f, 8f, GUILayout.Height(22));

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            bool ns = GUILayout.Toggle(_smooth, "  Dense sampling");
            if (ns != _smooth)
            {
                _smooth = ns;
                for (int i = 0; i < _lights.Count; i++)
                    _lights[i].SetSampling(SampleModeUsed, _smooth ? SmoothRes : CoarseRes);
            }
            _paused = GUILayout.Toggle(_paused, "  Pause walk");
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        private static void Bar(float t, Color fill)
        {
            Rect r = GUILayoutUtility.GetRect(330, 14);
            GUI.color = new Color(0f, 0f, 0f, 0.35f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = fill;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(t), r.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        private static GUIStyle _rich;
        private static GUIStyle Rich()
        {
            if (_rich == null)
                _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            return _rich;
        }

        private static Shader FindUnlitShader()
        {
            return Shader.Find("Universal Render Pipeline/Unlit")
                   ?? Shader.Find("Unlit/Color")
                   ?? Shader.Find("Standard");
        }

        #endregion HUD
    }
}
