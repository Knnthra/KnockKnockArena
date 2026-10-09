using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Blood at the point a hit landed. Built on the source game's SplatterEffect
    /// (KnockKnock, Scripts/Props/SplatterEffect.cs), which bursts watermelons —
    /// same two-system structure, retuned for blood: everything is red, and the
    /// droplets are roughly half the size.
    ///
    /// The watermelon version's second system was near-black seeds, which is exactly
    /// what it looked like on a player. Here it is a darker, finer red instead: the
    /// two sizes together read as spray rather than as one uniform spatter.
    ///
    /// Cosmetic and self-contained, like the explosion: the systems are built
    /// stopped, and whoever instantiates the prefab calls <see cref="Play"/>. In the
    /// arena that is the PlayerDamaged TCP event, which carries the point the shot
    /// went in — the server computes it during hit detection, and it is the only
    /// place that knows it.
    /// </summary>
    public class SplatterEffect : MonoBehaviour
    {
        [Tooltip("The main droplets. Bright arterial red.")]
        [SerializeField] private Material dropletMaterial;

        [Tooltip("The finer, darker spray mixed through them.")]
        [SerializeField] private Material fineDropletMaterial;

        [Header("Tuning")]
        [SerializeField] private int   amount          = 60;
        [SerializeField] private float pieceSizeMin    = 0.8f;
        [SerializeField] private float pieceSizeMax    = 1.2f;
        [SerializeField] private float spread          = 1f;
        [SerializeField] private float autoDestroyDelay = 3f;

        private void Awake()
        {
            CreateDroplets();
            CreateFineSpray();
        }

        public void Play()
        {
            foreach (ParticleSystem ps in GetComponentsInChildren<ParticleSystem>())
                ps.Play();

            Destroy(gameObject, autoDestroyDelay);
        }

        /// <summary>Scales the burst to the damage dealt, so a glancing shot is a
        /// fleck and a rocket is a mess. 1 = the authored amount.</summary>
        public void SetIntensity(float scale)
        {
            amount = Mathf.Max(4, Mathf.RoundToInt(amount * scale));
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

        private void CreateDroplets()
        {
            ParticleSystem ps = CreateSystem("Droplets", dropletMaterial);

            int count = Mathf.RoundToInt(amount * 0.6f);

            var main = ps.main;
            main.duration        = 0.1f;
            main.loop            = false;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.6f, 1.3f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(3f * spread, 9f * spread);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.06f * pieceSizeMin, 0.17f * pieceSizeMax);
            main.startColor      = new ParticleSystem.MinMaxGradient(
                                       new Color(0.75f, 0.03f, 0.05f),
                                       new Color(0.95f, 0.12f, 0.12f));
            main.gravityModifier = 2.5f;
            main.startRotation   = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius    = 0.12f;

            // Darkens as it flies: fresh blood is bright, and it goes to a deep
            // red rather than to the watermelon version's near-black.
            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(1f,   0.15f, 0.15f), 0f),
                    new GradientColorKey(new Color(0.55f, 0.02f, 0.03f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLife.color = g;
        }

        private void CreateFineSpray()
        {
            ParticleSystem ps = CreateSystem("FineSpray", fineDropletMaterial);

            int count = Mathf.RoundToInt(amount * 0.4f);

            var main = ps.main;
            main.duration        = 0.1f;
            main.loop            = false;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            // Faster and lighter than the droplets, so the mist outruns them.
            main.startSpeed      = new ParticleSystem.MinMaxCurve(4f * spread, 12f * spread);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.03f * pieceSizeMin, 0.09f * pieceSizeMax);
            main.startColor      = new Color(0.45f, 0.02f, 0.03f);
            main.gravityModifier = 1.6f;
            main.startRotation   = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius    = 0.08f;

            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.6f,  0.04f, 0.05f), 0f),
                    new GradientColorKey(new Color(0.35f, 0.01f, 0.02f), 1f)
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.6f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLife.color = g;
        }
    }
}
