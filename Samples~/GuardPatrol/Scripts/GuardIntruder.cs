using UnityEngine;

namespace VisioncastSamples.GuardPatrol
{
    /// <summary>
    /// Identity for the intruder. Its head and torso colliders are each registered against THIS component
    /// (<c>VisionTargetsManifest.Register(collider, actor)</c>), so the vision system resolves them to one
    /// target - the guard sees "the intruder", not two separate body parts. This is the multi-collider actor
    /// pattern: many colliders, one identity.
    /// </summary>
    public class GuardIntruder : MonoBehaviour
    {
    }
}
