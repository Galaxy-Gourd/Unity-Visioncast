using UnityEngine;

namespace VisioncastSamples.StealthExposure
{
    /// <summary>
    /// Marks one collider of the player as a weighted stealth body part. The weight is pure game policy - the
    /// vision system never sees it. The consumer (<see cref="StealthExposureSample"/>) reads each part's
    /// per-collider coverage from the vision results and combines them into one exposure score using these
    /// weights: a lit head counts for much more than a lit hand.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class StealthBodyPart : MonoBehaviour
    {
        [Tooltip("How much this part contributes to the player's overall exposure. Head > torso > limbs.")]
        public float Weight = 1f;
        [Tooltip("Shown in the HUD.")]
        public string Label = "Part";

        [HideInInspector] public Collider Collider;
        [HideInInspector] public Renderer Renderer;
        [HideInInspector] public float LitFraction; // 0..1, written each tick by the sample

        private void Awake()
        {
            Collider = GetComponent<Collider>();
            Renderer = GetComponent<Renderer>();
        }
    }
}
