using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Cursor-driven world-space aim: casts from the camera through the mouse cursor
    /// and exposes what it lands on.
    ///
    /// It hits the REAL GEOMETRY first — the crate you are pointing at, the wall, the
    /// ground — and only falls back to a flat plane at floor height when the cursor
    /// is off the map. Hitting the plane alone was wrong the moment the map got
    /// anything tall: pointing at the top of a crate gave a spot on the ground BEHIND
    /// it, so shots flew off toward that spot instead of at the crate.
    ///
    /// Part of the student base project: this DELIVERS the aim point (HANDOVER 6.1);
    /// packing it into input packets is the networking layer's job.
    /// </summary>
    public sealed class CursorAim : MonoBehaviour
    {
        [SerializeField] private Camera _camera;

        [Tooltip("What the cursor can land on. Everything by default — the point is to " +
                 "aim at whatever you can see.")]
        [SerializeField] private LayerMask _aimLayers = ~0;

        [Tooltip("How far the cursor ray reaches before giving up on geometry.")]
        [SerializeField] private float _maxAimDistance = 1000f;

        /// <summary>World-space aim point on the XZ plane; valid when HasAim is true.</summary>
        public Vector3 AimPoint { get; private set; }

        public bool HasAim { get; private set; }

        // The aim plane sits at the playable floor height, so the cursor hits the
        // spot the player visually points at (with an angled camera, projecting
        // onto y=0 under an elevated floor would skew the aim point).
        // Falls back to y=0 when no arena is loaded yet, so the cursor keeps working
        // in a scene without an ArenaMapStamp instead of throwing every frame.
        private static Plane GroundPlane =>
            new Plane(Vector3.up, new Vector3(0f,
                KnockKnockArena.Shared.Simulation.ArenaMap.IsLoaded
                    ? KnockKnockArena.Shared.Simulation.ArenaMap.GroundY
                    : 0f, 0f));

        private void Awake()
        {
            if (_camera == null)
                _camera = Camera.main;
        }

        private void Update()
        {
            if (_camera == null)
            {
                HasAim = false;
                return;
            }

            Ray ray = _camera.ScreenPointToRay(Input.mousePosition);

            // Real geometry first: this is what makes the crosshair and the shot agree
            // about where you are pointing, at any height.
            if (Physics.Raycast(ray, out RaycastHit hit, _maxAimDistance, _aimLayers,
                    QueryTriggerInteraction.Ignore))
            {
                AimPoint = hit.point;
                HasAim = true;
                return;
            }

            // Nothing under the cursor (pointing at the sky): fall back to the floor
            // plane so aiming never simply stops working.
            if (GroundPlane.Raycast(ray, out float distance))
            {
                AimPoint = ray.GetPoint(distance);
                HasAim = true;
                return;
            }

            HasAim = false;
        }
    }
}
