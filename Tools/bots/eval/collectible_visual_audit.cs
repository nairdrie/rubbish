// Photograph the production models with the production eBae thumbnail renderer.
// Spoiler contact sheet stays in the isolated QA root, never a player save or shipped asset.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var defs = ItemCatalog.All.Where(d => d.visual != null && d.visual.StartsWith("find_", System.StringComparison.Ordinal)).ToArray();
var photos = new EbaeItemThumbnails();
var target = UnityEngine.RenderTexture.GetTemporary(192,192,0);
var previous = UnityEngine.RenderTexture.active;
var sheet = new UnityEngine.Texture2D(960,960,UnityEngine.TextureFormat.RGB24,false);
try
{
    var requested = defs.Select(d => photos.Request(d.id,d.name)).ToArray();
    for(int i=0;i<(requested.Length+1)/2;i++)photos.Tick();
    for(int i=0;i<requested.Length;i++)
    {
        if(requested[i].image==null)throw new System.InvalidOperationException("Missing model photo at " + i);
        UnityEngine.Graphics.Blit(requested[i].image,target);
        UnityEngine.RenderTexture.active=target;
        sheet.ReadPixels(new UnityEngine.Rect(0,0,192,192),(i%5)*192,(4-i/5)*192);
    }
    sheet.Apply();
    System.IO.File.WriteAllBytes(System.IO.Path.Combine(SaveRoot.Path,"collectibles-qa-spoilers.png"),sheet.EncodeToPNG());
    return new {count=defs.Length,photos=requested.Count(x=>x.image!=null)};
}
finally
{
    UnityEngine.RenderTexture.active=previous;
    UnityEngine.RenderTexture.ReleaseTemporary(target);
    UnityEngine.Object.Destroy(sheet);
    photos.Dispose();
}
