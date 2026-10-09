using System.Collections;
using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Cosmetic character behaviour ported from the KnockKnock player: rotation
    /// toward the aim point, hop-bobbing while moving, dash leap arc and ghost
    /// trail. PURELY VISUAL — position comes from whoever moves the root (the
    /// networking layer for online play), and this script only animates children.
    ///
    /// Part of the student base project. Knows nothing about networking; the
    /// state setters below are the coupling surface.
    /// </summary>
    public sealed class CharacterVisual : MonoBehaviour
    {
        [Header("Rotation")]
        [SerializeField] private float _rotationSpeed = 10f;

        [Header("Hop (ported from CharacterBase)")]
        [Tooltip("Transform of the visual model that bobs up and down.")]
        [SerializeField] private Transform _visual;
        [SerializeField] private float _hopHeight = 0.5f;
        [SerializeField] private float _hopCooldown = 0.15f;
        [SerializeField] private float _sprintHopCooldown = 0.1f;
        [Tooltip("Optional hand transforms that bob during hops.")]
        [SerializeField] private Transform _leftHand;
        [SerializeField] private Transform _rightHand;
        [SerializeField] private float _handBobHeight = 0.15f;
        [Tooltip("Idle float: the hands never sit perfectly still, even standing still.")]
        [SerializeField] private float _handIdleBobAmount = 0.05f;
        [SerializeField] private float _handIdleBobSpeed = 2f;

        [Header("Dash (ported from GhostDash)")]
        [SerializeField] private float _leapHeight = 1.5f;
        [SerializeField] private float _ghostDuration = 0.6f;
        [SerializeField] private float _startAlpha = 0.4f;
        [SerializeField] private int _ghostCount = 3;

        [Header("Held weapon")]
        [Tooltip("Hand the weapon model is parented to. ONE hand holds a weapon — the " +
                 "other stays free, so there is no dual wielding.")]
        [SerializeField] private Transform _weaponHand;

        [Header("Punch (ported from CharacterCombat)")]
        [Tooltip("How long one swing takes. Split 25% wind back, 40% thrust, 35% return.")]
        [SerializeField] private float _punchSwingDuration = 0.12f;
        [Tooltip("How far the hand pulls BACK before the thrust.")]
        [SerializeField] private float _punchWindBack = 0.15f;
        [Tooltip("How far the fist travels forward at full extension.")]
        [SerializeField] private float _punchThrust = 0.4f;
        [Tooltip("How much the punch crosses the body. 0 = straight ahead; the source " +
                 "game uses 0.6 so each fist swings across toward the other side.")]
        [SerializeField] private float _punchCrossAmount = 0.6f;

        [Header("Recoil (ported from HandRecoil)")]
        [Tooltip("How far the weapon hand is thrown back by a shot.")]
        [SerializeField] private float _recoilKickback = 0.25f;
        [Tooltip("How far the weapon hand is tilted up by a shot, in degrees.")]
        [SerializeField] private float _recoilRotation = 12f;

        [Header("Spawn weapon")]
        [Tooltip("The weapon every player starts with, and its model. Needed because a " +
                 "spawn weapon is never picked up, so no pickup can register it. This is " +
                 "the ONLY weapon named here — every other weapon's model comes from its " +
                 "own pickup, so adding weapons never touches the character.")]
        [SerializeField] private Shared.Protocol.Udp.WeaponId _spawnWeapon =
            Shared.Protocol.Udp.WeaponId.Glock;
        [SerializeField] private GameObject _spawnWeaponPrefab;

        [Header("Landing")]
        [SerializeField] private ParticleSystem _landingDustPrefab;
        [SerializeField] private AudioClip[] _landingSounds;
        [SerializeField] [Range(0f, 10f)] private float _landingVolume = 1f;

        [Header("Punch sound")]
        [Tooltip("Played on every swing, hit or miss. One is picked at random.")]
        [SerializeField] private AudioClip[] _punchSwingSounds;
        [SerializeField] [Range(0f, 10f)] private float _punchSwingVolume = 1f;
        [Tooltip("Played where a punch lands on someone. One is picked at random.")]
        [SerializeField] private AudioClip[] _punchHitSounds;
        [SerializeField] [Range(0f, 10f)] private float _punchHitVolume = 1f;

        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        private static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");

        // State pushed from the outside (see SetMotionState / SetAimPoint).
        private bool _isMoving;
        private bool _isSprinting;
        private bool _isDashing;
        private Vector3? _aimPoint;

        // Hop state (same math as CharacterBase.UpdateHop).
        private float _hopTimer;
        private float _hopVelocityY;
        private float _visualOffsetY;
        private float _hopGravity;
        private bool _isHopping;

        // Dash visual state.
        private float _dashElapsed;
        private float _dashVisualDuration;
        private float _ghostSpawnTimer;
        private float _ghostSpawnInterval;
        private int _ghostsRemaining;

        private Vector3 _leftHandRest;
        private Vector3 _rightHandRest;

        // Punch: how far each fist currently reaches along its own swing direction.
        // Negative during the wind back, positive through the thrust. Driven by a
        // coroutine per hand, applied in UpdateHandBob on top of everything else.
        private float _leftPunchReach;
        private float _rightPunchReach;
        private bool _leftPunching;
        private bool _rightPunching;

        /// <summary>Fists alternate, so holding the button throws left, right, left.</summary>
        private bool _nextPunchIsLeft = true;
        private ParticleSystem _landingDustInstance;

        // Recoil offsets on the weapon hand, recovering linearly so they are back to
        // zero exactly one fire interval later — a faster weapon settles faster.
        private Vector3 _recoilOffset;
        private Vector3 _recoilEuler;
        private float _recoilRecoverSeconds;
        private float _recoilPositionMagnitude;
        private float _recoilRotationMagnitude;

        // Held weapon model, swapped when the carried weapon changes.
        private GameObject _heldInstance;
        private Shared.Protocol.Udp.WeaponId _heldWeapon;
        private bool _heldWeaponSet;

        private void Awake()
        {
            if (_leftHand != null) _leftHandRest = _leftHand.localPosition;
            if (_rightHand != null) _rightHandRest = _rightHand.localPosition;
            PickupMarker.RegisterHeldPrefab(_spawnWeapon, _spawnWeaponPrefab);
        }

        // ── Coupling surface (called by whoever owns the movement state) ──────

        /// <summary>
        /// Shows the weapon the player is carrying, in their hand. Safe to call every
        /// frame — swapping models only happens when the weapon actually changes.
        /// A weapon with no model in the table (Punch) simply empties the hand.
        /// </summary>
        public void SetWeapon(Shared.Protocol.Udp.WeaponId weapon)
        {
            if (_heldWeaponSet && _heldWeapon == weapon)
                return;
            _heldWeapon = weapon;
            _heldWeaponSet = true;

            if (_heldInstance != null)
                Destroy(_heldInstance);
            _heldInstance = null;

            if (_weaponHand == null)
                return;

            // The model comes from the weapon's own PICKUP — whatever hands this
            // weapon out also says what it looks like in a hand. Nothing about
            // weapons is configured on the character.
            GameObject prefab = PickupMarker.HeldPrefabFor(weapon);
            if (prefab == null)
                return;

            _heldInstance = Instantiate(prefab, _weaponHand);
            WeaponGrip grip = prefab.GetComponent<WeaponGrip>();
            _heldInstance.transform.localPosition = grip != null ? grip.Position : Vector3.zero;
            _heldInstance.transform.localRotation = Quaternion.Euler(grip != null ? grip.Rotation : Vector3.zero);
            _heldInstance.transform.localScale = Vector3.one * (grip != null ? grip.Scale : 1f);

            // A held weapon must not behave like a pickup lying on the ground.
            foreach (Collider collider in _heldInstance.GetComponentsInChildren<Collider>())
                Destroy(collider);
            foreach (HoverAndRotate hover in _heldInstance.GetComponentsInChildren<HoverAndRotate>())
                Destroy(hover);
        }

        /// <summary>
        /// Kicks the weapon hand for one shot. The offsets recover linearly so they
        /// reach zero in exactly one fire interval — pass the weapon's cooldown, and a
        /// fast weapon settles quickly while a slow one lingers, as in the source game.
        /// </summary>
        public void ApplyRecoil(float fireIntervalSeconds)
        {
            _recoilOffset += Vector3.back * _recoilKickback;
            _recoilEuler += new Vector3(
                -_recoilRotation,
                Random.Range(-_recoilRotation * 0.4f, _recoilRotation * 0.4f),
                0f);
            _recoilPositionMagnitude = _recoilOffset.magnitude;
            _recoilRotationMagnitude = _recoilEuler.magnitude;
            _recoilRecoverSeconds = Mathf.Max(0.05f, fireIntervalSeconds);
        }

        /// <summary>
        /// Where shots should appear to come from: the muzzle of the weapon actually
        /// in the hand. False when empty-handed (fists), so the caller can fall back.
        /// </summary>
        public bool TryGetMuzzle(out Vector3 worldPosition)
        {
            if (_heldInstance != null)
            {
                WeaponGrip grip = _heldInstance.GetComponent<WeaponGrip>();
                if (grip != null)
                {
                    worldPosition = grip.MuzzleWorldPosition;
                    return true;
                }
            }
            worldPosition = Vector3.zero;
            return false;
        }

        /// <summary>The held weapon's cosmetic description (muzzle, projectile), or
        /// null when empty-handed.</summary>
        public WeaponGrip HeldWeaponGrip =>
            _heldInstance != null ? _heldInstance.GetComponent<WeaponGrip>() : null;

        /// <summary>Where a weapon is held. Public so the weapon-table exporter can
        /// measure how far the muzzle ends up from the player's centre — the server
        /// has no model to ask, so that distance has to be measured here and written
        /// into the weapon table as numbers.</summary>
        public Transform WeaponHand => _weaponHand;

        /// <summary>World-space point the character looks at (the player's aim).</summary>
        public void SetAimPoint(Vector3 point) => _aimPoint = point;

        public void ClearAimPoint() => _aimPoint = null;

        /// <summary>Push the current movement state; drives hop, dash arc and ghosts.
        /// dashDurationSeconds only matters on the frame a dash starts.</summary>
        public void SetMotionState(bool isMoving, bool isSprinting, bool isDashing, float dashDurationSeconds)
        {
            _isMoving = isMoving;
            _isSprinting = isSprinting;

            if (isDashing && !_isDashing)
            {
                // Dash just started: reset the leap arc and begin the ghost trail.
                _dashElapsed = 0f;
                _dashVisualDuration = Mathf.Max(0.05f, dashDurationSeconds);
                _ghostsRemaining = _ghostCount;
                _ghostSpawnInterval = _dashVisualDuration / (_ghostCount + 1);
                _ghostSpawnTimer = 0f;
                SpawnGhost();
                _ghostsRemaining--;
            }
            _isDashing = isDashing;
        }

        // ── Per-frame cosmetics ───────────────────────────────────────────────

        private void Update()
        {
            UpdateRotation();

            if (_isDashing)
                UpdateDashArc();
            else
                UpdateHop();

            if (_visual != null)
                _visual.localPosition = new Vector3(
                    _visual.localPosition.x, _visualOffsetY, _visual.localPosition.z);

            UpdateHandBob();
            UpdateWeaponHand(); // aim + recoil, after the bob has set the rest position
        }

        private void UpdateRotation()
        {
            if (!_aimPoint.HasValue)
                return;

            Vector3 direction = _aimPoint.Value - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.01f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(direction);
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, targetRotation, _rotationSpeed * Time.deltaTime);
            }
        }

        private void UpdateDashArc()
        {
            _dashElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_dashElapsed / _dashVisualDuration);

            // Parabola peaking at leapHeight at t = 0.5 — the visual leap the
            // original got from CharacterController physics.
            _visualOffsetY = 4f * _leapHeight * t * (1f - t);

            if (_ghostsRemaining > 0)
            {
                _ghostSpawnTimer += Time.deltaTime;
                if (_ghostSpawnTimer >= _ghostSpawnInterval)
                {
                    _ghostSpawnTimer -= _ghostSpawnInterval;
                    SpawnGhost();
                    _ghostsRemaining--;
                }
            }

            if (t >= 1f)
            {
                _visualOffsetY = 0f;
                SpawnLandingDust();
            }
        }

        private void UpdateHop()
        {
            _hopTimer -= Time.deltaTime;

            bool hopMoving = _isMoving;
            if (hopMoving && !_isHopping && _hopTimer <= 0f)
            {
                float arcTime = _isSprinting ? _sprintHopCooldown : _hopCooldown;
                _hopGravity = 8f * _hopHeight / (arcTime * arcTime);
                _hopVelocityY = 4f * _hopHeight / arcTime;
                _hopTimer = arcTime;
                _isHopping = true;
            }

            if (_isHopping)
            {
                _hopVelocityY -= _hopGravity * Time.deltaTime;
                _visualOffsetY += _hopVelocityY * Time.deltaTime;

                if (_visualOffsetY <= 0f)
                {
                    _visualOffsetY = 0f;
                    _hopVelocityY = 0f;
                    _isHopping = false;
                    SpawnLandingDust();
                }
            }
            else if (_visualOffsetY > 0f)
            {
                _visualOffsetY = Mathf.MoveTowards(_visualOffsetY, 0f, _hopHeight * 10f * Time.deltaTime);
            }
        }

        /// <summary>
        /// Points the weapon hand at what the player is aiming at and layers the recoil
        /// kick on top — so the weapon visibly tracks the cursor and jumps when it
        /// fires. Where the shot actually GOES is the server's call; this only makes
        /// the hand agree with it.
        /// </summary>
        private void UpdateWeaponHand()
        {
            if (_weaponHand == null)
                return;

            if (_recoilRecoverSeconds > 0f)
            {
                _recoilOffset = Vector3.MoveTowards(_recoilOffset, Vector3.zero,
                    _recoilPositionMagnitude / _recoilRecoverSeconds * Time.deltaTime);
                _recoilEuler = Vector3.MoveTowards(_recoilEuler, Vector3.zero,
                    _recoilRotationMagnitude / _recoilRecoverSeconds * Time.deltaTime);
            }

            // Aim only while the target is far enough away and in front — close or
            // behind, the LookAt would spin the arm around wildly.
            if (_aimPoint.HasValue)
            {
                Vector3 toTarget = _aimPoint.Value - _weaponHand.position;
                if (toTarget.sqrMagnitude > 4f && Vector3.Dot(transform.forward, toTarget) > 0f)
                {
                    _weaponHand.LookAt(_aimPoint.Value);
                    _weaponHand.localRotation *= Quaternion.Euler(_recoilEuler);
                    return;
                }
            }
            _weaponHand.localRotation = Quaternion.Euler(_recoilEuler);
        }

        private void UpdateHandBob()
        {
            // Two layers, as in the source game's HandRecoil: a continuous idle float
            // that runs whatever the character is doing, plus a dip while hopping.
            // Without the idle layer the arms freeze solid when standing still.
            float idle = Mathf.Sin(Time.time * _handIdleBobSpeed) * _handIdleBobAmount;
            float t = _isHopping ? Mathf.Clamp01(_visualOffsetY / _hopHeight) : 0f;
            float offset = idle - Mathf.Sin(t * Mathf.PI) * _handBobHeight;

            // The weapon hand also carries the recoil kick, so a shot shoves it back
            // on top of whatever the bob is doing — and a punching fist carries its
            // swing on top of that again. Three layers, all additive.
            if (_leftHand != null)
                _leftHand.localPosition = _leftHandRest + new Vector3(0f, offset, 0f)
                    + (_leftHand == _weaponHand ? _recoilOffset : Vector3.zero)
                    + PunchOffsetFor(_leftHand, _leftPunchReach, true);
            if (_rightHand != null)
                _rightHand.localPosition = _rightHandRest + new Vector3(0f, offset, 0f)
                    + (_rightHand == _weaponHand ? _recoilOffset : Vector3.zero)
                    + PunchOffsetFor(_rightHand, _rightPunchReach, false);
        }

        // ── Punch (ported from CharacterCombat) ───────────────────────────────

        /// <summary>
        /// Throws a punch with the next fist in the alternation. Purely cosmetic: the
        /// server decided whether this swing connects before the animation started,
        /// and the fist is only catching up with a decision already made.
        ///
        /// Called on every melee swing, including other players' — a punch you see
        /// someone else throw is the same animation, driven by their fire event.
        /// </summary>
        /// <summary>The thud of this character's punch landing, at the hit point.</summary>
        public void PlayPunchHit(Vector3 position) =>
            SpatialAudio.Play(_punchHitSounds, position, _punchHitVolume);

        public void Punch()
        {
            bool left = _nextPunchIsLeft;

            // Both fists busy means the swings are coming faster than the animation
            // can show them; dropping this one beats restarting a fist mid-thrust.
            if (left && _leftPunching) left = false;
            else if (!left && _rightPunching) left = true;
            if (left ? _leftPunching : _rightPunching)
                return;

            _nextPunchIsLeft = !left;
            StartCoroutine(PunchRoutine(left));
            SpatialAudio.Play(_punchSwingSounds, transform.position, _punchSwingVolume);
        }

        /// <summary>
        /// Wind back, thrust, return — the source game's three phases at 25/40/35% of
        /// the swing. The wind back is what sells it: without it the fist just slides
        /// forward, with it the arm cocks first.
        /// </summary>
        private IEnumerator PunchRoutine(bool left)
        {
            SetPunching(left, true);

            float windBackTime = _punchSwingDuration * 0.25f;
            for (float t = 0f; t < windBackTime; t += Time.deltaTime)
            {
                SetPunchReach(left, Mathf.Lerp(0f, -_punchWindBack, t / windBackTime));
                yield return null;
            }

            float thrustTime = _punchSwingDuration * 0.4f;
            for (float t = 0f; t < thrustTime; t += Time.deltaTime)
            {
                SetPunchReach(left, Mathf.Lerp(-_punchWindBack, _punchThrust, t / thrustTime));
                yield return null;
            }
            SetPunchReach(left, _punchThrust);

            float returnTime = _punchSwingDuration * 0.35f;
            for (float t = 0f; t < returnTime; t += Time.deltaTime)
            {
                SetPunchReach(left, Mathf.Lerp(_punchThrust, 0f, t / returnTime));
                yield return null;
            }

            SetPunchReach(left, 0f);
            SetPunching(left, false);
        }

        private void SetPunchReach(bool left, float value)
        {
            if (left) _leftPunchReach = value;
            else _rightPunchReach = value;
        }

        private void SetPunching(bool left, bool value)
        {
            if (left) _leftPunching = value;
            else _rightPunching = value;
        }

        /// <summary>
        /// The swing offset in the hand's PARENT space, so it adds cleanly to the rest
        /// pose. The punch travels forward plus a bit across the body — each fist
        /// swings toward the other side, which is what makes it read as a punch rather
        /// than a shove.
        /// </summary>
        private Vector3 PunchOffsetFor(Transform hand, float reach, bool left)
        {
            if (Mathf.Approximately(reach, 0f))
                return Vector3.zero;

            Transform body = _visual != null ? _visual : transform;
            Vector3 world = (body.forward + body.right * (_punchCrossAmount * (left ? 1f : -1f)))
                .normalized * reach;
            return hand.parent != null ? hand.parent.InverseTransformDirection(world) : world;
        }

        // ── Ghost trail (ported from GhostDash) ───────────────────────────────

        private void SpawnGhost()
        {
            if (_visual == null)
                return;

            GameObject ghost = new GameObject("Ghost");
            ghost.transform.position = _visual.position;
            ghost.transform.rotation = _visual.rotation;

            GameObject clone = Instantiate(_visual.gameObject, _visual.position, _visual.rotation, ghost.transform);
            clone.transform.localScale = _visual.lossyScale;
            foreach (Component component in clone.GetComponentsInChildren<Component>())
            {
                if (component is Transform || component is MeshFilter || component is Renderer)
                    continue;
                Destroy(component);
            }

            foreach (Renderer renderer in ghost.GetComponentsInChildren<Renderer>())
            {
                Material[] ghostMaterials = renderer.materials;
                for (int i = 0; i < ghostMaterials.Length; i++)
                {
                    ghostMaterials[i] = new Material(ghostMaterials[i]);
                    Material material = ghostMaterials[i];
                    if (material.HasProperty(AlphaId)) material.SetFloat(AlphaId, _startAlpha);
                    if (material.HasProperty(OutlineWidthId)) material.SetFloat(OutlineWidthId, 0f);
                    material.SetInt("_ZWrite", 0);
                    material.renderQueue = 3000;
                }
                renderer.materials = ghostMaterials;
            }

            ghost.AddComponent<GhostFader>().Initialize(_ghostDuration, _startAlpha);
        }

        private void SpawnLandingDust()
        {
            if (_landingDustPrefab != null)
            {
                if (_landingDustInstance == null)
                    _landingDustInstance = Instantiate(
                        _landingDustPrefab, transform.position, Quaternion.Euler(90f, 0f, 0f));
                _landingDustInstance.transform.position = transform.position;
                _landingDustInstance.Play();
            }

            SpatialAudio.Play(_landingSounds, transform.position, _landingVolume);
        }
    }

    /// <summary>Fades a dash ghost's _Alpha to zero and destroys it.</summary>
    public sealed class GhostFader : MonoBehaviour
    {
        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");

        private float _duration;
        private float _startAlpha;
        private float _age;
        private Renderer[] _renderers;

        public void Initialize(float duration, float startAlpha)
        {
            _duration = duration;
            _startAlpha = startAlpha;
            _renderers = GetComponentsInChildren<Renderer>();
        }

        private void Update()
        {
            _age += Time.deltaTime;
            float alpha = Mathf.Lerp(_startAlpha, 0f, _age / _duration);

            foreach (Renderer r in _renderers)
                foreach (Material material in r.materials)
                    if (material.HasProperty(AlphaId))
                        material.SetFloat(AlphaId, alpha);

            if (_age >= _duration)
                Destroy(gameObject);
        }
    }
}
