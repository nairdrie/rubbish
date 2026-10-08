using UnityEngine;

public static partial class ItemVisuals
{
    // Story items (ItemCatalog.Story.cs, "story_*" keys). Renderer-only like every other piece of junk.
    static bool BuildStory(Kit k, string key)
    {
        Color c = k.c;
        switch (key)
        {
            case "story_floppy": // a 3.5" floppy: blue shell, sliding metal shutter, a hand-written label, the write-protect notch
                k.Box(Vector3.zero, new Vector3(.36f, .028f, .38f), c);
                k.Box(new Vector3(.155f, 0, .175f), new Vector3(.06f, .03f, .04f), Mul(c, .55f), new Vector3(0, 45, 0)); // the clipped corner
                k.Box(new Vector3(-.02f, .006f, .125f), new Vector3(.2f, .03f, .13f), Silver, default, Surface.Metal);    // shutter
                k.Box(new Vector3(-.055f, .012f, .125f), new Vector3(.05f, .032f, .09f), Black);                          // its window
                k.Box(new Vector3(0, .012f, -.075f), new Vector3(.29f, .01f, .2f), OffWhite);                             // label
                for (int i = 0; i < 3; i++) k.Box(new Vector3(-.01f, .019f, -.02f - i * .05f), new Vector3(.22f - i * .04f, .004f, .012f), Mul(c, .6f));
                k.Box(new Vector3(0, .019f, .012f), new Vector3(.29f, .005f, .02f), Red);                                 // the red stripe
                k.Box(new Vector3(.145f, .012f, -.16f), new Vector3(.03f, .012f, .03f), Black);                           // write-protect tab
                k.Cyl(new Vector3(0, -.016f, 0), .1f, .008f, Silver, default, Surface.Metal);                             // hub (underside)
                return true;
        }
        return false;
    }
}
