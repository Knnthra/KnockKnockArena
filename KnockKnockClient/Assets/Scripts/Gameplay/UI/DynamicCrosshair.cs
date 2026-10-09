using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Expands the procedural crosshair based on the current spread of the weapon.
    /// Ported from the source game; the one change is where the spread comes from.
    /// There it read the equipped Gun directly — here the spread is PUSHED IN from
    /// outside, because the weapon and its bloom live on the server and reach this
    /// client in snapshots. That keeps the crosshair free of any netcode reference.
    ///
    /// Part of the student base project: it draws a crosshair, nothing more.
    /// </summary>
    [RequireComponent(typeof(ProceduralCrosshair))]
    public sealed class DynamicCrosshair : MonoBehaviour
    {
        [SerializeField] private ProceduralCrosshair _crosshair;

        [Header("Spread Mapping")]
        [SerializeField] private bool _showSpread = true;
        [Tooltip("Pixels the crosshair opens per degree of spread.")]
        [SerializeField] private float _spreadMultiplier = 6f;
        [Tooltip("How fast the crosshair shrinks back.")]
        [SerializeField] private float _contractSpeed = 8f;

        [Header("Placement")]
        [Tooltip("Keep the crosshair under the mouse cursor. Leave OFF when a separate " +
                 "follow-the-mouse component is on the same object — two scripts writing " +
                 "the same transform fight each other.")]
        [SerializeField] private bool _followMouse;

        private float _targetSpread;
        private float _currentVisualSpread;

        /// <summary>The weapon's accumulated spread in degrees. Push this every frame;
        /// 0 means fully recovered.</summary>
        public void SetSpread(float spreadDegrees) => _targetSpread = Mathf.Max(0f, spreadDegrees);

        /// <summary>Hides the crosshair (between rounds, while dead, before joining).</summary>
        public void SetVisible(bool visible)
        {
            if (_crosshair != null)
                _crosshair.enabled = visible;
        }

        /// <summary>Whether the crosshair is currently shown. While it is not, the
        /// system mouse cursor is — see CrosshairFollowMouse.</summary>
        public bool IsVisible => _crosshair != null && _crosshair.enabled;

        private void Awake()
        {
            if (_crosshair == null)
                _crosshair = GetComponent<ProceduralCrosshair>();
        }

        private void Update()
        {
            if (_crosshair == null)
                return;

            if (_followMouse)
                transform.position = Input.mousePosition;

            if (!_showSpread)
                return;

            float target = _targetSpread * _spreadMultiplier;

            // Snap up immediately when spread increases (feels responsive on each shot),
            // lerp back smoothly when the weapon recovers (avoids an abrupt shrink).
            if (target > _currentVisualSpread)
                _currentVisualSpread = target;
            else
                _currentVisualSpread = Mathf.Lerp(_currentVisualSpread, target, _contractSpeed * Time.deltaTime);

            _crosshair.Spread = _currentVisualSpread;
        }
    }
}
