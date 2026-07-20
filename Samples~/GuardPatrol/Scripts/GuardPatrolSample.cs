using System.Collections.Generic;
using UnityEngine;
using GalaxyGourd.Visioncast;

namespace VisioncastSamples.GuardPatrol
{
    /// <summary>
    /// AI perception demo. A guard scans its sector; an intruder circles through the guard's view and behind a
    /// wall. The guard uses <see cref="GuardVision"/> (a <see cref="VisioncastSourceFiltered"/>) whose
    /// NewlySeen / NewlyLost events drive Patrol -> Alert -> Search: it locks on and tracks when it sees the
    /// intruder, heads to the last-known spot and counts down when it loses line of sight, and gives up back to
    /// patrol on timeout. The intruder is a multi-collider actor (head + torso under one identity). The FOV
    /// cone is drawn semi-transparent and tinted by state (green / red / yellow).
    /// </summary>
    [DefaultExecutionOrder(-500)]
    [AddComponentMenu("Visioncast/Samples/Guard Patrol Sample")]
    public class GuardPatrolSample : MonoBehaviour
    {
        #region CONFIG

        [Header("Pipeline")]
        [SerializeField] private float _gridCellSize = 25f;

        [Header("Guard")]
        [SerializeField] private Vector3 _guardPos = new Vector3(0f, 1f, -7f);
        [SerializeField] private float _guardRange = 26f;
        [SerializeField] private float _guardFov = 40f;   // vision cone HALF-angle
        [SerializeField] private float _searchDuration = 3f;
        [SerializeField] private float _sweepArc = 34f;   // patrol scan half-arc, degrees
        [SerializeField] private float _sweepSpeed = 22f; // deg/sec
        [SerializeField] private float _turnSpeed = 200f; // deg/sec toward a target/last-known

        [Header("Intruder loop")]
        [SerializeField] private Vector3 _loopCenter = new Vector3(0f, 1f, 2f);
        [SerializeField] private float _loopRadius = 4f;
        [SerializeField] private float _loopSpeed = 42f;  // deg/sec

        [Header("State colors (cone)")]
        [SerializeField] private Color _colPatrol = new Color(0.35f, 1f, 0.45f);
        [SerializeField] private Color _colAlert = new Color(1f, 0.3f, 0.3f);
        [SerializeField] private Color _colSearch = new Color(1f, 0.85f, 0.3f);

        private const int PlayerLayer = 3;   // intruder colliders (what the guard looks for)
        private const int OccluderLayer = 0; // Default - walls that block sight
        private const int InertLayer = 2;    // Ignore Raycast - ground, guard

        #endregion CONFIG


        #region STATE

        private GuardVision _guard;
        private Material _coneMat;
        private Transform _intruder;
        private GuardIntruder _intruderActor;
        private readonly List<Collider> _intruderColliders = new();
        private Material _intruderMat;

        private static readonly int _colorId = Shader.PropertyToID("_BaseColor");
        private static readonly int _colorIdLegacy = Shader.PropertyToID("_Color");

        private float _sweep;
        private int _sweepDir = 1;
        private float _loopAngle;

        #endregion STATE


        #region LIFECYCLE

        private void Awake()
        {
            VisionPipeline.UseDod(_gridCellSize);
            VisioncastManager.Setup();

            BuildEnvironment();
            BuildIntruder();
            BuildGuard();
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _intruderColliders.Count; i++)
                if (_intruderColliders[i])
                    VisionTargetsManifest.Unregister(_intruderColliders[i]);

            VisioncastManager.Dispose();
            if (_coneMat) Destroy(_coneMat);
            if (_intruderMat) Destroy(_intruderMat);
        }

        #endregion LIFECYCLE


        #region TICK

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            // Intruder circles through the guard's view and behind the wall.
            _loopAngle += _loopSpeed * dt * Mathf.Deg2Rad;
            _intruder.position = _loopCenter + new Vector3(
                Mathf.Cos(_loopAngle) * _loopRadius, 0f, Mathf.Sin(_loopAngle) * _loopRadius);

            // Guard facing: scan while patrolling, otherwise turn toward the (last-known) intruder.
            if (_guard.State == GuardVision.GuardState.Patrol)
            {
                _sweep += _sweepDir * _sweepSpeed * dt;
                if (_sweep >= _sweepArc) { _sweep = _sweepArc; _sweepDir = -1; }
                else if (_sweep <= -_sweepArc) { _sweep = -_sweepArc; _sweepDir = 1; }
                _guard.transform.rotation = Quaternion.Euler(0f, _sweep, 0f);
            }
            else
            {
                Vector3 to = _guard.LastKnownPosition - _guard.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f)
                {
                    Quaternion target = Quaternion.LookRotation(to.normalized, Vector3.up);
                    _guard.transform.rotation = Quaternion.RotateTowards(
                        _guard.transform.rotation, target, _turnSpeed * dt);
                }
            }

            // Sources moved via transforms this step; push into the physics scene before the vision raycast.
            Physics.SyncTransforms();
            VisioncastManager.TickVisioncasts(dt);
            _guard.TickSearch(dt);

            // Cone tint by state (mesh is parented to the guard, so it follows the facing automatically).
            Color c = _guard.State switch
            {
                GuardVision.GuardState.Alert => _colAlert,
                GuardVision.GuardState.Search => _colSearch,
                _ => _colPatrol
            };
            c.a = 0.16f;
            _coneMat.SetColor(_colorId, c);
            _coneMat.SetColor(_colorIdLegacy, c);
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

            // The occluder: a wall across the far side of the intruder's loop. It spans the full loop width so
            // the intruder is hidden for the whole back half of its circuit - long enough for the guard's search
            // to time out and lapse back to Patrol before the intruder rounds the near side and is seen again.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.layer = OccluderLayer;
            wall.transform.position = _loopCenter + new Vector3(0f, 0.6f, 0.2f);
            wall.transform.localScale = new Vector3(8f, 3f, 0.4f);

            var cam = Camera.main;
            if (cam)
            {
                cam.transform.position = new Vector3(11f, 10f, -13f);
                cam.transform.rotation = Quaternion.LookRotation(
                    (new Vector3(0f, 1f, 1f) - cam.transform.position).normalized, Vector3.up);
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 120f);
            }
        }

        private void BuildIntruder()
        {
            var root = new GameObject("Intruder");
            root.transform.position = _loopCenter + new Vector3(_loopRadius, 0f, 0f);
            _intruderActor = root.AddComponent<GuardIntruder>();
            _intruder = root.transform;

            _intruderMat = new Material(FindOpaqueShader()) { color = new Color(1f, 0.25f, 0.8f) };
            _intruderMat.SetColor(_colorId, new Color(1f, 0.25f, 0.8f));

            AddIntruderPart("Torso", new Vector3(0f, 0f, 0f), new Vector3(0.55f, 0.9f, 0.4f), false);
            AddIntruderPart("Head", new Vector3(0f, 0.62f, 0f), new Vector3(0.42f, 0.42f, 0.42f), true);
        }

        private void AddIntruderPart(string label, Vector3 localPos, Vector3 size, bool sphere)
        {
            var go = GameObject.CreatePrimitive(sphere ? PrimitiveType.Sphere : PrimitiveType.Capsule);
            go.name = label;
            go.layer = PlayerLayer;
            go.transform.SetParent(_intruder, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = _intruderMat;

            var col = go.GetComponent<Collider>();
            // Register each collider against the SAME actor - the filter collapses them to one target.
            VisionTargetsManifest.Register(col, _intruderActor);
            _intruderColliders.Add(col);
        }

        private void BuildGuard()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "Guard";
            go.SetActive(false); // configure before OnEnable registers it as a source
            go.layer = InertLayer;
            go.transform.position = _guardPos;
            go.transform.rotation = Quaternion.identity; // faces +Z
            var gcol = go.GetComponent<Collider>();
            if (gcol) Destroy(gcol);
            go.GetComponent<MeshRenderer>().sharedMaterial.color = new Color(0.7f, 0.7f, 0.72f);

            _guard = go.AddComponent<GuardVision>();
            // RaycastLayers = intruder layer (so a ray can hit the target and count as seen) | occluders.
            _guard.Configure(1 << PlayerLayer, (1 << PlayerLayer) | (1 << OccluderLayer),
                             _guardRange, _guardFov, _intruderActor, _searchDuration);

            go.SetActive(true);
            BuildGuardCone(go.transform);
        }

        private void BuildGuardCone(Transform guard)
        {
            var coneGo = new GameObject("GuardCone");
            coneGo.transform.SetParent(guard, false); // parented, so the cone follows the guard's facing
            var cone = coneGo.AddComponent<VisionCone>();
            var mr = coneGo.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _coneMat = MakeConeMaterial(_colPatrol);
            mr.sharedMaterial = _coneMat;
            cone.CalculateCone(_guard); // Range/FoV are fixed, so build the mesh once
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
            Color c = tint; c.a = 0.16f;
            mat.SetColor("_BaseColor", c);
            mat.SetColor("_Color", c);
            return mat;
        }

        #endregion BUILD


        #region HUD

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 320, 180), GUI.skin.box);
            GUILayout.Label("<b>VisionCast - Guard Patrol (AI perception)</b>", Rich());
            GUILayout.Space(6);

            string state = _guard.State.ToString().ToUpperInvariant();
            string colHex = _guard.State switch
            {
                GuardVision.GuardState.Alert => "#ff5555",
                GuardVision.GuardState.Search => "#ffd94d",
                _ => "#5cff73"
            };
            GUILayout.Label($"State: <b><color={colHex}>{state}</color></b>", Rich());
            GUILayout.Label($"Intruder in sight: {(_guard.TargetVisible ? "YES" : "no")}");
            if (_guard.State == GuardVision.GuardState.Search)
                GUILayout.Label($"Giving up in: {Mathf.Max(0f, _guard.SearchTimer):0.0}s");

            GUILayout.Space(6);
            GUILayout.Label("<i>Green = patrol/scan, red = tracking, yellow = searching last-known. " +
                            "The intruder (head+torso = one target) circles behind the wall to break sight.</i>",
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
