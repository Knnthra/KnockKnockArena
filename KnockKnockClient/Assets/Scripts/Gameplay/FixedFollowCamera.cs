using KnockKnockArena.Gameplay;
using UnityEngine;
using UnityEngine.InputSystem;


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

        [Header("Rotate with right mouse button")]
        [Tooltip("Hold the right mouse button and move the mouse sideways to turn the camera " +
                 "around the player. The aim stays on the spot it had while turning.")]
        [SerializeField] internal bool rightMouseRotate = true;
        [SerializeField] internal float rotateSensitivity = 0.2f;

        private Vector3 velocity = Vector3.zero;
        private float targetGroundY;
        private bool targetInitialized;

        // turning with the right mouse button
        private float yaw;
        private bool turning;
        private Vector2 cursorBeforeTurn;
        private CursorAim cursorAim;
        private RectTransform crosshair;
        private Canvas crosshairCanvas;
        private Camera cam;

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

            Vector3 groundPosition = new Vector3(target.position.x, targetGroundY, target.position.z);
            transform.position = groundPosition + Offset();
            transform.LookAt(groundPosition + Vector3.up * 1f);
        }

        // From the ground point under the player to the camera: behind and above, turned by
        // yaw (0 = looking north).
        private Vector3 Offset()
        {
            float angleRad = angle * Mathf.Deg2Rad;
            float horizontalDistance = distance * Mathf.Cos(angleRad);
            float verticalDistance = height + distance * Mathf.Sin(angleRad);
            return Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, verticalDistance, -horizontalDistance);
        }

        void Update()
        {
            Mouse mouse = Mouse.current;
            bool held = rightMouseRotate && mouse != null && mouse.rightButton.isPressed
                        && target != null && !GameMenu.Open && Application.isFocused;

            if (held && !turning) BeginTurn(mouse);
            else if (!held && turning) EndTurn(mouse);

            if (turning)
                yaw += mouse.delta.ReadValue().x * rotateSensitivity;
        }

        // While turning, the mouse is locked (it only turns) and CursorAim is paused, so its
        // AimPoint keeps the spot the player aimed at: the player keeps facing it while the
        // view turns around. Afterwards the cursor comes back on that spot.
        private void BeginTurn(Mouse mouse)
        {
            turning = true;
            cursorBeforeTurn = mouse.position.ReadValue();
            if (cursorAim == null)
            {
                cursorAim = FindAnyObjectByType<CursorAim>();
                CrosshairFollowMouse follow = FindAnyObjectByType<CrosshairFollowMouse>();
                if (follow != null)
                {
                    crosshair = follow.GetComponent<RectTransform>();
                    crosshairCanvas = follow.GetComponentInParent<Canvas>();
                }
                cam = GetComponent<Camera>();
            }
            if (cursorAim != null) cursorAim.enabled = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void EndTurn(Mouse mouse)
        {
            turning = false;
            Cursor.lockState = CursorLockMode.None;
            // back onto the aimed spot where it now is on screen, so the aim does not jump
            Vector2 back = cursorBeforeTurn;
            if (cam != null && cursorAim != null)
            {
                Vector3 s = cam.WorldToScreenPoint(cursorAim.AimPoint);
                if (s.z > 0f)
                    back = new Vector2(Mathf.Clamp(s.x, 0f, Screen.width - 1), Mathf.Clamp(s.y, 0f, Screen.height - 1));
            }
            if (mouse != null) mouse.WarpCursorPosition(back);
            if (cursorAim != null) cursorAim.enabled = true;
        }

        void OnDisable()
        {
            if (turning) EndTurn(Mouse.current);
        }

        void LateUpdate()
        {
            if (target == null) return;

            if (!targetInitialized)
            {
                targetGroundY    = target.position.y;
                targetInitialized = true;
            }

            Vector3 groundPosition = new Vector3(target.position.x, targetGroundY, target.position.z);
            Vector3 desiredPosition = groundPosition + Offset();

            // While turning: no smoothing, or the camera would lag sideways out of the turn.
            transform.position = turning
                ? desiredPosition
                : Vector3.SmoothDamp(transform.position, desiredPosition, ref velocity, followSmoothTime);

            transform.LookAt(groundPosition + Vector3.up * 1f);

            if (turning)
                KeepCrosshairOnAim();
        }

        // CrosshairFollowMouse puts the crosshair at the (locked) mouse in its Update; this
        // runs after it and keeps the crosshair on the spot that is still being aimed at.
        private void KeepCrosshairOnAim()
        {
            if (crosshair == null || crosshairCanvas == null || cam == null || cursorAim == null) return;
            Vector2 screen = cam.WorldToScreenPoint(cursorAim.AimPoint);
            if (crosshairCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
                crosshair.position = screen;
            else if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                         crosshairCanvas.transform as RectTransform, screen, crosshairCanvas.worldCamera,
                         out Vector2 local))
                crosshair.localPosition = local;
        }
    }
