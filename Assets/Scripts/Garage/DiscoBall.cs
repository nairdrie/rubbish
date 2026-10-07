using UnityEngine;

/// <summary>Garage Disco Ball (cosmetic, garage_disco_ball): spins, and a few coloured lights sweep the garage.</summary>
public class DiscoBall : MonoBehaviour
{
    public Transform ball;
    public Light[] lights = new Light[0];
    public float spin = 40f;
    public float sweep = 70f;

    void Update()
    {
        if (ball != null) ball.Rotate(Vector3.up, spin * Time.deltaTime, Space.World);
        float t = Time.time;
        for (int i = 0; i < lights.Length; i++)
        {
            var l = lights[i];
            if (l == null) continue;
            l.transform.rotation = Quaternion.Euler(55f + Mathf.Sin(t * 0.7f + i) * 20f, t * sweep + i * (360f / Mathf.Max(1, lights.Length)), 0f);
            l.color = Color.HSVToRGB(Mathf.Repeat(t * 0.1f + i / (float)Mathf.Max(1, lights.Length), 1f), 0.8f, 1f);
        }
    }
}
