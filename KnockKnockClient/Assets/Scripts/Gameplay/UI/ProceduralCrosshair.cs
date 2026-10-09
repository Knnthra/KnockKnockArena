using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Procedurally generated crosshair UI element with multiple styles and customization options.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(RectTransform))]
// A Graphic draws through a CanvasRenderer. Unity adds one when the component is
// added in the Inspector, but NOT when the GameObject is built in code — and the
// result is a crosshair that exists, moves and updates, yet renders nothing.
[RequireComponent(typeof(CanvasRenderer))]
public class ProceduralCrosshair : Graphic
{
    /// <summary>
    /// Available crosshair rendering styles.
    /// </summary>
    public enum Style { Cross, Circle, Dot, CrossAndCircle, None }

    [Header("Style")]
    [SerializeField] private Style style = Style.CrossAndCircle;
    private float rotationDegrees = 0f;

    [Header("Rendering")]
    [SerializeField] private bool pixelSnap = true;

    [Header("Cross (arms)")]
    [SerializeField, Range(0, 100)] private float thickness = 2f;
    [SerializeField, Range(0, 100)] private float armLength = 12f;
    [SerializeField, Range(0, 100)] private float gap = 6f;

    [Header("Circle / Ring")]
    [SerializeField, Range(0, 100)] private float circleRadius = 10f;
    [SerializeField, Range(0, 100)] private float ringThickness = 2f;
    [SerializeField, Range(3, 256)] private int circleSegments = 64;

    [Header("Center Dot")]
    [SerializeField] private bool drawCenterDot = false;
    [SerializeField, Range(0, 100)] private float centerDotSize = 3f;

    [Header("Runtime")]
    [SerializeField, Range(0, 100)] private float spread = 0f;

    [Header("Outline")]
    [SerializeField] private bool useOutline = true;
    [SerializeField, Range(0, 100)] private float outlineWidth = 2f;
    [SerializeField] private Color outlineColor = Color.black;

    // NEW: control inner-edge coverage + optional gap compensation
    [SerializeField] private bool outlineArmsInner = true;
    [SerializeField] private bool outlineRingInner = false;
    [SerializeField] private bool compensateGapForInnerOutline = false;

    public Style CrosshairStyle { get => style; set { style = value; SetVerticesDirty(); } }
    public float Spread { get => spread; set { spread = Mathf.Max(0f, value); SetVerticesDirty(); } }
    public float Gap { get => gap; set { gap = Mathf.Max(0f, value); SetVerticesDirty(); } }
    public float ArmLength { get => armLength; set { armLength = Mathf.Max(0f, value); SetVerticesDirty(); } }
    public float Thickness { get => thickness; set { thickness = Mathf.Max(0.1f, value); SetVerticesDirty(); } }
    public float CircleRadius { get => circleRadius; set { circleRadius = Mathf.Max(0f, value); SetVerticesDirty(); } }
    public float RingThickness { get => ringThickness; set { ringThickness = Mathf.Max(0.1f, value); SetVerticesDirty(); } }

    public bool UseOutline { get => useOutline; set { useOutline = value; SetVerticesDirty(); } }
    public float OutlineWidth { get => outlineWidth; set { outlineWidth = Mathf.Max(0f, value); SetVerticesDirty(); } }
    public Color OutlineColor { get => outlineColor; set { outlineColor = value; SetVerticesDirty(); } }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Vector2 origin = Vector2.zero; // pivot should be center (0.5, 0.5)
        float ang = rotationDegrees * Mathf.Deg2Rad;

        float innerFill = gap + spread;

        // If we draw inner outline into the gap, optionally push the fill outward to keep visible gap constant.
        if (useOutline && outlineArmsInner && compensateGapForInnerOutline)
            innerFill += outlineWidth;

        // ---------- 1) OUTLINE (behind fill) ----------
        if (useOutline && outlineWidth > 0f)
        {
            if (style == Style.Cross || style == Style.CrossAndCircle)
            {
                // Perpendicular thickness grows by 2w
                float thO = thickness + 2f * outlineWidth;

                // Inner edge for outline:
                // - If compensating, innerFill already moved out by +w, so subtract to get original inner
                float innerO = innerFill - (compensateGapForInnerOutline ? outlineWidth : 0f);
                // If outlining inner edge, intrude by w into the gap
                if (outlineArmsInner) innerO -= outlineWidth;

                // Length grows outward by +w; if inner edge is outlined, also grow by +w inward
                float lenO = armLength + outlineWidth + (outlineArmsInner ? outlineWidth : 0f);

                AddArm(vh, origin, ang + 0f, innerO, lenO, thO, outlineColor);
                AddArm(vh, origin, ang + Mathf.PI * 0.5f, innerO, lenO, thO, outlineColor);
                AddArm(vh, origin, ang + Mathf.PI, innerO, lenO, thO, outlineColor);
                AddArm(vh, origin, ang + Mathf.PI * 1.5f, innerO, lenO, thO, outlineColor);
            }

            if (style == Style.Circle || style == Style.CrossAndCircle)
            {
                float r = Mathf.Max(0f, circleRadius + spread);
                float innerR = Mathf.Max(0f, r - ringThickness * 0.5f);
                float outerR = r + ringThickness * 0.5f;

                float oInnerR = outlineRingInner ? Mathf.Max(0f, innerR - outlineWidth) : innerR;
                float oOuterR = outerR + outlineWidth;

                AddRingIO(vh, origin, oInnerR, oOuterR, circleSegments, 0f, Mathf.PI * 2f, outlineColor);
            }

            if (style == Style.Dot || drawCenterDot)
            {
                float sO = centerDotSize + 2f * outlineWidth;
                AddRect(vh, origin, sO, sO, 0f, outlineColor, allowSnap: false); // dot can alias if snapped
            }
        }

        // ---------- 2) FILL (on top) ----------
        if (style == Style.Cross || style == Style.CrossAndCircle)
        {
            AddArm(vh, origin, ang + 0f, innerFill, armLength, thickness, color);
            AddArm(vh, origin, ang + Mathf.PI * 0.5f, innerFill, armLength, thickness, color);
            AddArm(vh, origin, ang + Mathf.PI, innerFill, armLength, thickness, color);
            AddArm(vh, origin, ang + Mathf.PI * 1.5f, innerFill, armLength, thickness, color);
        }

        if (style == Style.Circle || style == Style.CrossAndCircle)
        {
            float r = Mathf.Max(0f, circleRadius + spread);
            AddRing(vh, origin, r, ringThickness, circleSegments, 0f, Mathf.PI * 2f, color);
        }

        if (style == Style.Dot || drawCenterDot)
        {
            AddRect(vh, origin, centerDotSize, centerDotSize, 0f, color, allowSnap: false);
        }
    }

    // ----- Drawing helpers -----

    void AddArm(VertexHelper vh, Vector2 origin, float angle, float innerGap, float length, float thick, Color col)
    {
        Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        Vector2 center = origin + dir * (innerGap + length * 0.5f);
        AddRect(vh, center, length, thick, angle, col, allowSnap: true);
    }

    void AddRect(VertexHelper vh, Vector2 center, float width, float height, float angleRad, Color col, bool allowSnap)
    {
        float hw = width * 0.5f;
        float hh = height * 0.5f;

        float cos = Mathf.Cos(angleRad);
        float sin = Mathf.Sin(angleRad);

        Vector2 right = new Vector2(cos, sin);
        Vector2 up = new Vector2(-sin, cos);

        Vector3 v0 = center + (-right * hw) + (-up * hh);
        Vector3 v1 = center + (-right * hw) + (up * hh);
        Vector3 v2 = center + (right * hw) + (up * hh);
        Vector3 v3 = center + (right * hw) + (-up * hh);

        // Pixel-snap only for axis-aligned arms to avoid thickness bias
        if (allowSnap && pixelSnap && IsAxisAligned(angleRad))
        {
            v0 = Snap(v0); v1 = Snap(v1); v2 = Snap(v2); v3 = Snap(v3);
        }

        var vert = UIVertex.simpleVert;
        vert.color = col;

        UIVertex[] quad = new UIVertex[4];
        vert.position = v0; quad[0] = vert;
        vert.position = v1; quad[1] = vert;
        vert.position = v2; quad[2] = vert;
        vert.position = v3; quad[3] = vert;

        vh.AddUIVertexQuad(quad);
    }

    void AddRing(VertexHelper vh, Vector2 origin, float radius, float thickness, int segments, float startAngle, float endAngle, Color col)
    {
        float inner = Mathf.Max(0f, radius - thickness * 0.5f);
        float outer = radius + thickness * 0.5f;
        AddRingIO(vh, origin, inner, outer, segments, startAngle, endAngle, col);
    }

    // Draw a ring with explicit inner/outer radii
    void AddRingIO(VertexHelper vh, Vector2 origin, float inner, float outer, int segments, float startAngle, float endAngle, Color col)
    {
        segments = Mathf.Max(3, segments);
        float range = endAngle - startAngle;

        var vert = UIVertex.simpleVert;
        vert.color = col;

        for (int i = 0; i < segments; i++)
        {
            float a0 = startAngle + range * i / segments;
            float a1 = startAngle + range * (i + 1) / segments;

            Vector2 o0 = origin + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * outer;
            Vector2 o1 = origin + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * outer;
            Vector2 i0 = origin + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * inner;
            Vector2 i1 = origin + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * inner;

            UIVertex[] quad = new UIVertex[4];
            vert.position = o0; quad[0] = vert;
            vert.position = o1; quad[1] = vert;
            vert.position = i1; quad[2] = vert;
            vert.position = i0; quad[3] = vert;

            vh.AddUIVertexQuad(quad);
        }
    }

    // ----- Pixel-snap helpers -----

    bool IsAxisAligned(float angleRad)
    {
        // Close to multiples of 90°
        float s = Mathf.Abs(Mathf.Sin(angleRad));
        float c = Mathf.Abs(Mathf.Cos(angleRad));
        return (s < 1e-4f) || (c < 1e-4f);
    }

    float ScaleFactor()
    {
        var c = canvas ? (canvas.rootCanvas ? canvas.rootCanvas : canvas) : null;
        return c ? c.scaleFactor : 1f;
    }

    Vector3 Snap(Vector3 v)
    {
        float sf = ScaleFactor();
        v.x = Mathf.Round(v.x * sf) / sf;
        v.y = Mathf.Round(v.y * sf) / sf;
        return v;
    }
}
