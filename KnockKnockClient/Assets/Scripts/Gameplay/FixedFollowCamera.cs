using UnityEngine;


    internal class FixedFollowCamera : MonoBehaviour
    {
        [Header("Target Settings")]
        [SerializeField] internal Transform target;

        [Header("Camera Position")]
        [SerializeField] internal float distance = 10f;
        [SerializeField] internal float height = 8f;
        [SerializeField] internal float angle = 45f;

        [Header("Follow Settings")]
        [SerializeField] internal float followSmoothTime = 0.1f;

        private Vector3 velocity = Vector3.zero;
        private float targetGroundY;
        private bool targetInitialized;

        void Start()
        {
            if (target != null)
            {
                targetGroundY    = target.position.y;
                targetInitialized = true;
            }
        }

        /// <summary>
        /// Assigns the transform to follow and snaps to it. Public so it can be
        /// wired in the Inspector to NetworkBootstrap's LocalPlayerSpawned event
        /// (dynamic Transform) — the camera itself knows nothing about networking.
        /// Pass null to release the target (e.g. on disconnect).
        /// </summary>
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            if (target == null) return;
            targetGroundY = target.position.y;
            targetInitialized = true;
            SnapToTarget();
        }

        internal void SnapToTarget()
        {
            if (target == null) return;
            velocity = Vector3.zero;

            float angleRad = angle * Mathf.Deg2Rad;
            float horizontalDistance = distance * Mathf.Cos(angleRad);
            float verticalDistance = height + distance * Mathf.Sin(angleRad);

            Vector3 groundPosition = new Vector3(target.position.x, targetGroundY, target.position.z);
            transform.position = groundPosition + new Vector3(0f, verticalDistance, -horizontalDistance);
            transform.LookAt(groundPosition + Vector3.up * 1f);
        }

        void LateUpdate()
        {
            if (target == null) return;

            if (!targetInitialized)
            {
                targetGroundY    = target.position.y;
                targetInitialized = true;
            }

            float angleRad = angle * Mathf.Deg2Rad;
            float horizontalDistance = distance * Mathf.Cos(angleRad);
            float verticalDistance = height + distance * Mathf.Sin(angleRad);

            Vector3 groundPosition = new Vector3(target.position.x, targetGroundY, target.position.z);
            Vector3 desiredPosition = groundPosition + new Vector3(0f, verticalDistance, -horizontalDistance);

            transform.position = Vector3.SmoothDamp(transform.position, desiredPosition, ref velocity, followSmoothTime);

            transform.LookAt(groundPosition + Vector3.up * 1f);
        }
    }