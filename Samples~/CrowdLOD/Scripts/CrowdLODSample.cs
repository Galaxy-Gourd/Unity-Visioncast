using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using GalaxyGourd.Visioncast;
using Debug = UnityEngine.Debug;

namespace VisioncastSamples.CrowdLOD
{
    /// <summary>
    /// Self-contained showcase of vision LOD / relevance at scale. One component builds the whole scene at
    /// runtime: a field of rotating vision sources plus scattered targets, an orbiting camera that acts as
    /// the relevance origin, and an on-screen readout. It also drives the DoD pipeline synchronously so the
    /// per-tick cost is visible.
    ///
    /// The point it makes: with <see cref="VisionLOD"/> tiers on, only near agents cast every tick — mid and
    /// far agents cast rarely, and out-of-range agents not at all — so the measured tick cost collapses.
    /// Each agent brightens the tick it casts and dims as its data goes stale, so the scheduler is visible:
    /// a shimmer near the camera, a still, dim crowd far out.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    [AddComponentMenu("Visioncast/Samples/Crowd LOD Sample")]
    public class CrowdLODSample : MonoBehaviour
    {
        #region CONFIG

        [Header("Pipeline")]
        [SerializeField] private float _gridCellSize = 25f;

        [Header("Field")]
        [SerializeField] private int _agentCount = 300;
        [SerializeField] private int _targetCount = 400;
        [SerializeField] private float _fieldRadius = 140f;
        [SerializeField] private float _agentRange = 25f;
        [SerializeField] private float _agentFov = 45f;

        [Header("Camera")]
        [SerializeField] private float _camOrbitRadius = 95f;
        [SerializeField] private float _camHeight = 90f;
        [SerializeField] private float _camOrbitSpeed = 12f; // deg/sec

        [Header("Visualization")]
        [SerializeField] private float _staleAfter = 0.35f;  // seconds until a non-casting agent reads fully dim
        [SerializeField] private Color _fresh = new Color(0.35f, 1f, 0.45f);
        [SerializeField] private Color _stale = new Color(0.12f, 0.16f, 0.18f);

        // Layers (reused stock layers so the sample needs no project setup):
        //   targets on "Interactable" (what agents look for); ground/agents on "Ignore Raycast" (never block);
        //   obstruction mask = empty "Water" layer, so nothing occludes and every in-FOV target is "seen"
        //   (the sample is about scheduling cost at scale, not occlusion — the full pipeline still runs).
        private const int TargetLayer = 6;      // Interactable
        private const int InertLayer = 2;       // Ignore Raycast
        private const int ObstructionLayer = 4; // Water (expected empty in this scene)

        #endregion CONFIG


        #region STATE

        private readonly List<CrowdAgentVision> _agents = new();
        private readonly List<Collider> _targets = new();
        private Transform _fieldRoot;
        private Transform _cam;
        private Material _sharedMat;
        private MaterialPropertyBlock _mpb;
        private static readonly int _colorId = Shader.PropertyToID("_BaseColor");
        private static readonly int _colorIdLegacy = Shader.PropertyToID("_Color");

        private bool _lodOn = true;
        private VisionLODTier[] _lodTiers;
        private VisionLODTier[] _flatTiers;

        private float _camAngle;
        private double _tickMs;

        #endregion STATE


        #region METRICS

        /// <summary>Number of active vision sources (agents) in the field.</summary>
        public int AgentCount => _agents.Count;
        /// <summary>Number of registered targets.</summary>
        public int TargetCount => _targets.Count;
        /// <summary>Smoothed cost of the vision tick, in milliseconds.</summary>
        public double TickMs => _tickMs;

        /// <summary>Toggles the tiered LOD policy on/off (off = every agent casts every tick).</summary>
        public bool LodEnabled
        {
            get => _lodOn;
            set
            {
                _lodOn = value;
                VisionLOD.Tiers = _lodOn ? _lodTiers : _flatTiers;
            }
        }

        /// <summary>How many agents actually cast on the most recent tick (fresh this cycle).</summary>
        public int CastingThisTick()
        {
            int c = 0;
            for (int i = 0; i < _agents.Count; i++)
                if (_agents[i].TimeSinceUpdate <= 1e-4f)
                    c++;
            return c;
        }

        #endregion METRICS


        #region LIFECYCLE

        private void Awake()
        {
            // DoD selection MUST precede Setup; these statics reset each play session, so set them every run.
            VisionPipeline.UseDod(_gridCellSize);

            _lodTiers = new[]
            {
                new VisionLODTier { MaxDistance = 45f,  Cadence = 1,  SampleResolution = 0 }, // near: every tick
                new VisionLODTier { MaxDistance = 100f, Cadence = 4,  SampleResolution = 1 }, // mid
                new VisionLODTier { MaxDistance = 180f, Cadence = 16, SampleResolution = 1 }, // far
                // beyond 180 => dormant (never casts)
            };
            _flatTiers = new[] { new VisionLODTier { MaxDistance = float.PositiveInfinity, Cadence = 1 } };
            VisionLOD.Tiers = _lodOn ? _lodTiers : _flatTiers;

            _mpb = new MaterialPropertyBlock();
            _cam = EnsureCamera();
            // Relevance = HORIZONTAL distance from the camera's ground point; smaller = nearer / higher
            // priority. Ignoring height keeps the tiers meaningful from a raised, angled camera, so the
            // frequently-casting cluster reads as a zone on the ground that follows the camera.
            VisionRelevance.Provider = src =>
            {
                Vector3 g = _cam.position; g.y = 0f;
                Vector3 p = src.Position;  p.y = 0f;
                return Vector3.Distance(g, p);
            };

            VisioncastManager.Setup();

            BuildGround();
            BuildField(_agentCount, _targetCount);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i])
                    VisionTargetsManifest.Unregister(_targets[i]);

            VisionRelevance.Provider = null;
            VisioncastManager.Dispose();

            if (_sharedMat)
                Destroy(_sharedMat);
        }

        #endregion LIFECYCLE


        #region TICK

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            // Orbit the camera (moves the relevance origin) and spin the agents (churns their visible sets).
            _camAngle += _camOrbitSpeed * dt * Mathf.Deg2Rad;
            _cam.position = new Vector3(Mathf.Cos(_camAngle) * _camOrbitRadius, _camHeight,
                                        Mathf.Sin(_camAngle) * _camOrbitRadius);
            _cam.rotation = Quaternion.LookRotation(-_cam.position, Vector3.up);

            for (int i = 0; i < _agents.Count; i++)
            {
                CrowdAgentVision a = _agents[i];
                a.transform.Rotate(0f, a.SpinSpeed * dt, 0f, Space.World);
            }

            // Sources moved via transforms this step; push into the physics scene before the vision raycast.
            Physics.SyncTransforms();

            var sw = Stopwatch.StartNew();
            VisioncastManager.TickVisioncasts(dt);
            sw.Stop();
            _tickMs = Mathf.Lerp((float)_tickMs, (float)sw.Elapsed.TotalMilliseconds, 0.1f);
        }

        private void Update()
        {
            // Tint each agent by how recently it cast: fresh -> _fresh, stale/dormant -> _stale.
            for (int i = 0; i < _agents.Count; i++)
            {
                CrowdAgentVision a = _agents[i];
                if (!a.Renderer)
                    continue;

                float t = Mathf.Clamp01(a.TimeSinceUpdate / _staleAfter);
                Color c = Color.Lerp(_fresh, _stale, t);
                a.Renderer.GetPropertyBlock(_mpb);
                _mpb.SetColor(_colorId, c);
                _mpb.SetColor(_colorIdLegacy, c);
                a.Renderer.SetPropertyBlock(_mpb);
            }
        }

        #endregion TICK


        #region BUILD

        private Transform EnsureCamera()
        {
            Camera cam = Camera.main;
            if (!cam)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
            }
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, _fieldRadius * 3f);
            return cam.transform;
        }

        private void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.layer = InertLayer;
            ground.transform.localScale = Vector3.one * (_fieldRadius * 0.25f);
            var col = ground.GetComponent<Collider>();
            if (col) Destroy(col);
        }

        private void BuildField(int agents, int targets)
        {
            ClearField();
            _fieldRoot = new GameObject("Field").transform;
            _sharedMat = new Material(FindOpaqueShader());

            for (int i = 0; i < targets; i++)
            {
                Vector2 p = RandomDiscDeterministic(i * 2 + 1, _fieldRadius);
                var t = GameObject.CreatePrimitive(PrimitiveType.Cube);
                t.name = "Target";
                t.layer = TargetLayer;
                t.transform.SetParent(_fieldRoot, false);
                t.transform.position = new Vector3(p.x, 0.5f, p.y);
                var mr = t.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _sharedMat;
                var col = t.GetComponent<Collider>();
                VisionTargetsManifest.Register(col);
                _targets.Add(col);
            }

            for (int i = 0; i < agents; i++)
                SpawnAgent(i);
        }

        private void SpawnAgent(int i)
        {
            Vector2 p = RandomDiscDeterministic(i * 2, _fieldRadius);
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Agent";
            go.SetActive(false); // configure before OnEnable registers it
            go.layer = InertLayer;
            go.transform.SetParent(_fieldRoot, false);
            go.transform.position = new Vector3(p.x, 1f, p.y);
            go.transform.rotation = Quaternion.Euler(0f, Hash01(i * 7 + 3) * 360f, 0f);
            var col = go.GetComponent<Collider>();
            if (col) Destroy(col);

            var vis = go.AddComponent<CrowdAgentVision>();
            vis.Configure(1 << TargetLayer, 1 << ObstructionLayer, _agentRange, _agentFov, 1);
            vis.SpinSpeed = 25f + Hash01(i * 13 + 5) * 65f;
            vis.Renderer = go.GetComponent<Renderer>();

            go.SetActive(true);
            _agents.Add(vis);
        }

        private void ClearField()
        {
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i])
                    VisionTargetsManifest.Unregister(_targets[i]);
            _targets.Clear();
            _agents.Clear();
            if (_fieldRoot)
                Destroy(_fieldRoot.gameObject);
            if (_sharedMat)
                Destroy(_sharedMat); // recreated in BuildField; avoid leaking one material per rebuild
        }

        /// <summary>Rebuild the field with a new agent count (the HUD count buttons call this).</summary>
        public void Rebuild(int agents)
        {
            _agentCount = agents;
            BuildField(_agentCount, _targetCount);
        }

        #endregion BUILD


        #region HUD

        private void OnGUI()
        {
            const int w = 340;
            GUILayout.BeginArea(new Rect(12, 12, w, 260), GUI.skin.box);

            GUILayout.Label("<b>VisionCast — Crowd LOD</b>", RichLabel());
            GUILayout.Space(4);

            int casting = CastingThisTick();

            GUILayout.Label($"Agents (sources): {_agents.Count}");
            GUILayout.Label($"Targets: {_targets.Count}");
            GUILayout.Label($"<b>Casting this tick: {casting} / {_agents.Count}</b>", RichLabel());
            GUILayout.Label($"<b>Vision tick: {_tickMs:0.00} ms</b>", RichLabel());

            GUILayout.Space(6);
            bool newLod = GUILayout.Toggle(_lodOn, _lodOn ? "  LOD: ON (tiered cadence)" : "  LOD: OFF (every agent, every tick)");
            if (newLod != _lodOn)
                LodEnabled = newLod;

            GUILayout.Space(6);
            GUILayout.Label("Agent count:");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("100")) Rebuild(100);
            if (GUILayout.Button("300")) Rebuild(300);
            if (GUILayout.Button("600")) Rebuild(600);
            if (GUILayout.Button("1000")) Rebuild(1000);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label(_lodOn
                ? "<i>Near = bright (every tick), far = dim (rarely), beyond = dormant.</i>"
                : "<i>Every agent casts every tick — watch the ms jump.</i>", RichLabel());

            GUILayout.EndArea();
        }

        private static GUIStyle _rich;
        private static GUIStyle RichLabel()
        {
            if (_rich == null)
                _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            return _rich;
        }

        #endregion HUD


        #region UTILITY

        // Deterministic scatter (no Random state) so rebuilds are stable and reviewable.
        private static Vector2 RandomDiscDeterministic(int seed, float radius)
        {
            float a = Hash01(seed * 2 + 1) * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Hash01(seed * 2 + 2)) * radius;
            return new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }

        private static float Hash01(int n)
        {
            uint x = (uint)n * 2654435761u + 1013904223u;
            x ^= x >> 16; x *= 2246822519u; x ^= x >> 13; x *= 3266489917u; x ^= x >> 16;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }

        private static Shader FindOpaqueShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit")
                   ?? Shader.Find("Standard")
                   ?? Shader.Find("Sprites/Default");
        }

        #endregion UTILITY
    }
}
