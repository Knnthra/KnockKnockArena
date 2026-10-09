using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// The barrel explosion, ported 1:1 from the source game (KnockKnock,
    /// Scripts/Props/ExplosionEffect.cs) — here it is what a rocket leaves behind.
    /// Four particle systems built in code from four materials: a near-white flash,
    /// the fireball, slow smoke, and sparks that fall under heavy gravity.
    ///
    /// Everything here is in the ART'S units (flash 4-6 m, fireball up to 5 m):
    /// the two projects share the same world scale, so the numbers port unchanged.
    ///
    /// Cosmetic and self-contained — the systems are built stopped, and whoever
    /// instantiates the prefab calls <see cref="Play"/>. In the arena that is the
    /// RocketExploded TCP event (Video 6.7: effects play on events; the snapshot
    /// only despawns the entity). There is a second, unrelated ExplosionEffect in
    /// the Networking assembly: the code-built placeholder sphere, kept as the
    /// fallback for a launcher with no effect assigned.
    /// </summary>
    public class ExplosionEffect : MonoBehaviour
    {
        [SerializeField] private Material flashMaterial;
        [SerializeField] private Material fireballMaterial;
        [SerializeField] private Material smokeMaterial;
        [SerializeField] private Material sparksMaterial;
        [SerializeField] private float autoDestroyDelay = 6f;

        private void Awake()
        {
            CreateFlash();
            CreateFireball();
            CreateSmoke();
            CreateSparks();
        }

        public void Play()
        {
            foreach (ParticleSystem ps in GetComponentsInChildren<ParticleSystem>())
                ps.Play();

            Destroy(gameObject, autoDestroyDelay);
        }

        private ParticleSystem CreateSystem(string systemName, Material material)
        {
            GameObject go = new GameObject(systemName);
            go.transform.SetParent(transform, false);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            if (material != null)
                r.material = material;

            return ps;
        }

        private void CreateFlash()
        {
            ParticleSystem ps = CreateSystem("Flash", flashMaterial);

            var main = ps.main;
            main.duration      = 0.05f;
            main.loop          = false;
            main.startLifetime = 0.12f;
            main.startSpeed    = 0f;
            main.startSize     = new ParticleSystem.MinMaxCurve(4f, 6f);
            main.startColor    = new Color(1f, 1f, 0.9f); // near white
            main.scalingMode   = ParticleSystemScalingMode.Local;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });

            // Grow from nothing, peak, then vanish
            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            AnimationCurve curve = new AnimationCurve(
                new Keyframe(0f,   0f),
                new Keyframe(0.35f, 1f),
                new Keyframe(1f,   0f)
            );
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, curve);
        }

        private void CreateFireball()
        {
            ParticleSystem ps = CreateSystem("Fireball", fireballMaterial);

            var main = ps.main;
            main.duration        = 0.3f;
            main.loop            = false;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.4f, 0.8f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(3f, 8f);
            main.startSize       = new ParticleSystem.MinMaxCurve(2f, 5f);
            main.startColor      = new Color(1f, 0.5f, 0f);
            main.gravityModifier = -0.1f;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 30) });

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius    = 0.3f;

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f,   0.3f),
                new Keyframe(0.2f, 1f),
                new Keyframe(1f,   0.6f)
            );
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f,   0.95f, 0.5f), 0f),
                    new GradientColorKey(new Color(1f,   0.4f,  0f),   0.3f),
                    new GradientColorKey(new Color(0.6f, 0.1f,  0f),   0.7f),
                    new GradientColorKey(new Color(0.15f, 0.1f, 0.1f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f,  0f),
                    new GradientAlphaKey(1f,  0.4f),
                    new GradientAlphaKey(0f,  1f)
                }
            );
            colorOverLife.color = gradient;
        }

        private void CreateSmoke()
        {
            ParticleSystem ps = CreateSystem("Smoke", smokeMaterial);

            var main = ps.main;
            main.duration        = 0.4f;
            main.loop            = false;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(2f, 4f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(1f, 4f);
            main.startSize       = new ParticleSystem.MinMaxCurve(1f, 5f);
            main.startColor      = new ParticleSystem.MinMaxGradient(
                                       new Color(0.35f, 0.35f, 0.35f),
                                       new Color(0.1f,  0.1f,  0.1f));
            main.gravityModifier = -0.2f;
            main.startRotation   = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] {
                new ParticleSystem.Burst(0f,    8),
                new ParticleSystem.Burst(0.05f, 6),
                new ParticleSystem.Burst(0.1f,  5)
            });

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius    = 1f;

            // Slow spin over lifetime breaks the circular silhouette
            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f,   0.1f),
                new Keyframe(0.3f, 1f),
                new Keyframe(1f,   2.5f)
            );
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.6f, 0.5f, 0.4f), 0f),
                    new GradientColorKey(new Color(0.3f, 0.3f, 0.3f), 0.3f),
                    new GradientColorKey(new Color(0.1f, 0.1f, 0.1f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0f,   0f),
                    new GradientAlphaKey(0.7f, 0.15f),
                    new GradientAlphaKey(0.5f, 0.6f),
                    new GradientAlphaKey(0f,   1f)
                }
            );
            colorOverLife.color = gradient;
        }

        private void CreateSparks()
        {
            ParticleSystem ps = CreateSystem("Sparks", sparksMaterial);

            var main = ps.main;
            main.duration        = 0.3f;
            main.loop            = false;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.8f, 1.5f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(6f, 14f);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
            main.startColor      = new Color(1f, 0.9f, 0.1f);
            main.gravityModifier = 3f;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 35) });

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius    = 0.5f;

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 1f,  0.5f), 0f),
                    new GradientColorKey(new Color(1f, 0.3f, 0f),  1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLife.color = gradient;
        }
    }
}
