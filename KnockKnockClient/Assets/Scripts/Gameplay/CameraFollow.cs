using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Fixed-angle follow camera: north (+Z) is always up on screen, the camera
    /// never rotates — it only translates with its target. Placeholder for the
    /// KnockKnock FixedFollowCamera/CameraManager scripts, which drop in here later.
    ///
    /// Part of the student base project. Knows nothing about networking; whoever
    /// owns the local player assigns <see cref="Target"/>.
    /// </summary>
    public sealed class CameraFollow : MonoBehaviour
    {
        [Tooltip("The transform to follow. Assigned at runtime (e.g. when the local player spawns).")]
        public Transform Target;

        [SerializeField] private Vector3 _offset = new Vector3(0f, 16f, -9f);

        /// <summary>Inspector-wirable target assignment (same contract as
        /// FixedFollowCamera.SetTarget) — hook it to LocalPlayerSpawned.</summary>
        public void SetTarget(Transform newTarget)
        {
            Target = newTarget;
        }

        private void Start()
        {
            transform.rotation = Quaternion.LookRotation(-_offset, Vector3.up);
        }

        private void LateUpdate()
        {
            if (Target != null)
                transform.position = Target.position + _offset;
        }
    }
}
