using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// CADMenuPanel's grab affordance: a glow drawn only outside the window, around the part of its
/// edge nearest the pointer. Plain UI geometry (default UI material, no masks or shaders):
/// along the panel's rounded outline, a strip over the thin border line and a soft strip
/// fading outward, both lit by a Gaussian of the distance to the pointer, so the rest of the
/// edge stays as it is. Stretched over the canvas; the outline is its rect grown by Inset.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class CADMenuEdgeGlow : MaskableGraphic
{
    public float Inset;           // The window's edge (panel background) = this rect grown by Inset.
    public float Radius;          // Corner radius of that edge.
    public float LineWidth = 2f;  // The border line, lit near the pointer.
    public float GlowSize = 16f;  // Soft glow beyond the border line.
    public float Reach = 55f;     // How far along the edge the light spreads (Gaussian sigma).
    public float GlowAlpha = 0.6f;
    public float Step = 6f;       // Outline sampling (canvas units).

    private struct Sample
    {
        public Vector2 Center; // Corner center (interpolated along straight edges).
        public Vector2 Normal; // Outward.
    }

    private readonly List<Sample> samples = new();

    /// <summary>0 (off) to 1.</summary>
    public float Intensity { get; private set; }
    /// <summary>The pointer, in this graphic's local (canvas) space.</summary>
    public Vector2 Cursor { get; private set; }

    public void SetState(float intensity, Vector2 cursor)
    {
        if (Mathf.Approximately(intensity, Intensity) && (cursor - Cursor).sqrMagnitude < 0.25f)
            return;
        bool visible = Intensity > 0f || intensity > 0f;
        Intensity = intensity;
        Cursor = cursor;
        if (visible)
            SetVerticesDirty();
    }

    /// <summary>How lit a canvas-local point on the edge is (0 far from the pointer).</summary>
    public float AlphaAt(Vector2 point) =>
        Intensity * Mathf.Exp(-(point - Cursor).sqrMagnitude / (2f * Reach * Reach));

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (Intensity <= 0f)
            return;

        BuildOutline();
        Color32 Tint(float alpha)
        {
            Color c = color;
            c.a *= Mathf.Clamp01(alpha);
            return c;
        }

        // Per sample: border inner/outer edge (lit), glow start (dimmer), glow end (clear).
        foreach (Sample s in samples)
        {
            Vector2 edge = s.Center + s.Normal * Radius;
            Vector2 line = s.Center + s.Normal * (Radius + LineWidth);
            Vector2 outer = s.Center + s.Normal * (Radius + LineWidth + GlowSize);
            float alpha = AlphaAt(line);
            vh.AddVert(edge, Tint(alpha), Vector4.zero);
            vh.AddVert(line, Tint(alpha), Vector4.zero);
            vh.AddVert(line, Tint(alpha * GlowAlpha), Vector4.zero);
            vh.AddVert(outer, Tint(0f), Vector4.zero);
        }

        int count = samples.Count;
        for (int i = 0; i < count; i++)
        {
            int a = i * 4, b = (i + 1) % count * 4;
            vh.AddTriangle(a, b, b + 1);
            vh.AddTriangle(a, b + 1, a + 1);
            vh.AddTriangle(a + 2, b + 2, b + 3);
            vh.AddTriangle(a + 2, b + 3, a + 3);
        }
    }

    /// <summary>The generated vertices' positions (tests: the glow never covers the window).</summary>
    public List<Vector3> GetVertexPositions()
    {
        var vh = new VertexHelper();
        OnPopulateMesh(vh);
        var positions = new List<Vector3>();
        var vertex = new UIVertex();
        for (int i = 0; i < vh.currentVertCount; i++)
        {
            vh.PopulateUIVertex(ref vertex, i);
            positions.Add(vertex.position);
        }
        vh.Dispose();
        return positions;
    }

    // Counter-clockwise around the rounded rect: each corner's arc, then the straight edge to
    // the next corner, sampled about every Step units.
    private void BuildOutline()
    {
        samples.Clear();
        Rect r = rectTransform.rect;
        float radius = Mathf.Max(0f, Radius);
        float xMin = r.xMin - Inset + radius, xMax = r.xMax + Inset - radius;
        float yMin = r.yMin - Inset + radius, yMax = r.yMax + Inset - radius;
        Vector2[] centers = { new(xMax, yMin), new(xMax, yMax), new(xMin, yMax), new(xMin, yMin) };
        int arcSteps = Mathf.Max(3, Mathf.CeilToInt(radius * Mathf.PI * 0.5f / Step));

        for (int k = 0; k < 4; k++)
        {
            float start = -90f + 90f * k;
            for (int j = 0; j <= arcSteps; j++)
            {
                float angle = (start + 90f * j / arcSteps) * Mathf.Deg2Rad;
                samples.Add(new Sample { Center = centers[k], Normal = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) });
            }

            Vector2 from = centers[k], to = centers[(k + 1) % 4];
            float endAngle = (start + 90f) * Mathf.Deg2Rad;
            var normal = new Vector2(Mathf.Cos(endAngle), Mathf.Sin(endAngle));
            int edgeSteps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / Step));
            for (int i = 1; i < edgeSteps; i++)
                samples.Add(new Sample { Center = Vector2.Lerp(from, to, (float)i / edgeSteps), Normal = normal });
        }
    }
}
