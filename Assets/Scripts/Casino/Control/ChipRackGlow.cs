using UnityEngine;

/// <summary>
/// Lights the SELECTED chip bin on a rack (and the hovered one) through a property block on the bin's own renderer: the pulse is
/// re-applied every frame, so hover changes and the pickables' own property-block resets cannot wipe it. Local and cosmetic.
/// </summary>
public sealed class ChipRackGlow
{
    readonly Renderer[] bins;
    readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
    readonly bool[] touched;
    int selected = -1, hovered = -1;

    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

    public ChipRackGlow(Transform[] rack)
    {
        int n = rack == null ? 0 : rack.Length;
        bins = new Renderer[n];
        touched = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (rack[i] == null) continue;
            var t = rack[i].Find("Bin");
            bins[i] = t != null ? t.GetComponent<Renderer>() : rack[i].GetComponentInChildren<Renderer>();
        }
    }

    public int Selected => selected;
    public void Set(int selectedIndex, int hoveredIndex) { selected = selectedIndex; hovered = hoveredIndex; }

    /// <summary>Apply the glow (call every frame while seated).</summary>
    public void Refresh(float time)
    {
        for (int i = 0; i < bins.Length; i++)
        {
            var r = bins[i];
            if (r == null) continue;
            if (i == selected)
            {
                float k = GameSettings.ReduceFlashing ? 1.5f : 1.5f + 0.6f * Mathf.Sin(time * 6f); // the accessibility setting holds the pulse still
                block.Clear();
                block.SetColor(EmissionColor, new Color(1.0f, 0.78f, 0.25f) * k);
                r.SetPropertyBlock(block);
                touched[i] = true;
            }
            else if (i == hovered)
            {
                block.Clear();
                block.SetColor(EmissionColor, new Color(0.9f, 0.7f, 0.25f) * 0.8f);
                r.SetPropertyBlock(block);
                touched[i] = true;
            }
            else if (touched[i])
            {
                r.SetPropertyBlock(null);
                touched[i] = false;
            }
        }
    }

    /// <summary>Put every bin back as it was.</summary>
    public void Clear()
    {
        for (int i = 0; i < bins.Length; i++)
            if (bins[i] != null && touched[i]) { bins[i].SetPropertyBlock(null); touched[i] = false; }
        selected = hovered = -1;
    }
}
