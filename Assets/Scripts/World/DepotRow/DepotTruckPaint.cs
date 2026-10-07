using UnityEngine;

/// <summary>A business truck's paint (MainBody, material slot 0 of TruckBody) in its business colour.</summary>
[DisallowMultipleComponent]
public class DepotTruckPaint : MonoBehaviour
{
    public int Lot { get; private set; } = -1;
    int color = -1;
    Renderer body;
    MaterialPropertyBlock mpb;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    public void Apply(int lot, int colorIndex)
    {
        Lot = lot;
        if (colorIndex == color && body != null) return;
        if (body == null)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r.name == "TruckBody") { body = r; break; }
        }
        if (body == null) return;
        color = colorIndex;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        body.GetPropertyBlock(mpb, 0);
        mpb.SetColor(BaseColorId, Color.Lerp(DepotColors.Get(colorIndex), Color.black, 0.12f));
        body.SetPropertyBlock(mpb, 0);
    }
}
