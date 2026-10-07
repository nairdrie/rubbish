# Rubbish: Player Character "Chuck Dumpling" (design + build contract)

This is the only spec the builder needs for the player's look. It covers the art direction, every mesh part, the materials, the runtime motion and the prefab changes. Every number in the parts tables was generated from the same data file as the preview renders in `Docs/CharacterRefs/`. `Docs/CharacterRefs/chuck_parts.json` is a machine-readable copy of the tables; the editor builder can read it directly.

**TL;DR**
- **Look:** a short, stout, cute binman built from primitives.
  - Mario-style grammar: big nose, walrus mustache, cap, overalls, round body, stubby limbs, big boots.
  - Rubbish Co. sanitation kit instead of any Nintendo look: a hi-vis tangerine cap and shirt, truck-olive overalls, hazard-yellow reflective bands, yellow rubber gloves and brown rubber boots.
  - Derp: googly eyes, one bigger than the other, and a googly "Company Eye" pin on the cap.
- **Count:** 60 mesh renderers and 13 empty pivots, plus 8 variant-only objects that are built inactive. There are no colliders.
- **Scale:** the Player root is scaled uniformly to **0.8**. That makes him **1.59 m** tall, with his eyes at 1.29 m, and a wheelie bin reaches his shoulder. The prefab changes that go with the scale are in section 5.
- **Motion:** the rig, Animator and Starter Assets clips are unchanged. On top of them, one runtime layer (`ChuckPuppet`) does three things:
  - splays the arms around the belly;
  - hugs carried bags with two-bone IK;
  - drives the springs: cap, bobble head, belly, mustache, cowlick, rag and duck.

![turnaround](CharacterRefs/chuck_turnaround.png)

---

## 1. Who he is

**Chuck Dumpling** is Rubbish Co.'s proudest and roundest binman.

- **Pitch:** a pocket-sized, barrel-bellied garbage man built like a vinyl toy. He has a cap, a big nose, a walrus mustache and overalls, and he hugs every trash bag like a long-lost friend before he chucks it into the truck. His googly eyes never agree on where to look, and one is a bit bigger than the other. His hat pops off on every jump. Admiral, a rubber duck, rides in his back pocket and watches you from behind. Blush and baby jowls keep him cute rather than gross.
- **Flavor:**
  - He is a third-generation binman. His grandma drove the first FEED ME truck, and he still wears her bottle caps as overall buttons.
  - He calls the truck Gertrude and has a name for every bin on the route.
  - He believes nothing is ever *really* garbage ("that's a perfectly good lamp!").
  - He is terrified of raccoons.
  - He found Admiral on his very first pickup, and the duck has ridden in his back pocket ever since.
- **Personality in motion:** earnest, busy and a bit clumsy. He scurries on stubby legs, bear-hugs bags against his belly, winds up with a strained red face, and yeets with a "HWAAH!".

## 2. Judging: scores (1-10 per criterion)

| Concept | Cute | Mario-esque yet original | Derpy friendslop | Fits low-poly flat style | Reads at 5 m | Buildable (no bad clipping) | Perf (< ~60) | **Total /70** |
|---|---|---|---|---|---|---|---|---|
| **Plumber lens: "Chuck Dumpling" (orange cap, olive overalls, googly Company Eye)** | 9 | 7 | 8 | 9 | 9 | 8 | 8 | **58 (winner)** |
| Friendslop lens: "Sal Vage" plus "Binny", a wheelie-bin backpack | 7 | 9 | 10 | 8 | 7 | 6 | 8 | 55 |
| Chibi lens: "Chuck Dumpling" (green cap, orange overalls, rubber duck) | 8 | 8 | 7 | 7 | 6 | 6 | 8 | 50 |

**Plumber lens:** the most complete and best-proven spec.
- Its raycast previews show that it is adorable, thanks to the jowls, blush and googly eyes.
- Its palette comes straight from the game: overalls in the truck olive, hazard yellow from the HUD and the FEED ME plate, and a googly pin that echoes the googly trash bag on the depot roof.
- It reads well from behind: an orange dome, dark suspenders on the orange back, a yellow belt, the rag and yellow shin rings.
- It got the TrashCarrier units right: the hold target is in world units.
- It named the real integration traps: the truck re-enabling renderers, bind-pose sampling, and the units GooglyPupil uses.
- It loses points on originality. White gloves, brown boots and a warm-colored cap and shirt over contrasting overalls sit close to Mario's formula, so the final design changes the gloves.

**Friendslop lens:** the funniest concept. Binny is great, and so are the strain face, the free-fall flail and the mismatched eyes.
- **Readability:** the backpack covers the back, which is the view players see about 90% of the time, with a blue box. That hides the Mario silhouette (straps and round butt) and adds 8 parts.
- **Buildability:** it misreads `TrashCarrier.holdLocalPosition` as root-local. `HoldTarget()` actually returns `transform.position + up*y + forward*z` in world units, so its (0, 1.05, 0.62) would put the bag in his face.
- **Clipping:** the fixed 22° arm splay does not clear the run-cycle forearms.

**Chibi lens:** has strong ideas (Admiral the duck, a bobble-head pivot, the Animator culling fix) and a nice orange hi-vis body.
- **Readability:** its green cap is the biggest color mass the chase camera sees, and it sits on green grass next to an olive truck.
- **Buildability:** the eyelid rig relies on scaled nested empties (eye roots at scale 0.13), which is fragile with GooglyPupil. It also has no preview.

## 3. What was grafted, and from where

The winner is the Plumber lens (Chuck Dumpling). Everything below is folded into one character with one palette.

| Grafted idea | From | How it lands |
|---|---|---|
| **Yellow rubber work gloves** instead of white mitts | Friendslop | Reads as sanitation (rubber gloves) and breaks Mario's white-glove signature. White is now used **only** on googly eyes, so his face pops. |
| **Mismatched googly eyes**: the right eye is 8% bigger and blinks 0.03 s late; its brow sits 3 mm higher | Friendslop | Subtle derp that stays cute. |
| **Boot shaft on the LowerLeg bone** instead of Foot | Friendslop | The shaft stays aligned with the shin when the ankle flexes, and the boot body hides the joint. |
| **Out-of-sync two-lobe mustache** (a pivot per lobe, different ω) | Friendslop | Looks like the mustache is trying to leave. |
| **Cap floats at the jump apex, then slams down with a spin on landing** | Friendslop | Merged with the Plumber cap pop and bob. |
| **Strain face** (skin toward red, cheeks puff, squint, tremble) for heavy bags and throw charge | Friendslop | Uses a per-player Skin material instance, not a MaterialPropertyBlock, so the SRP Batcher keeps working. |
| **Free-fall flail** with an "O" mouth, a **stretch-yawn** fidget, a small **speed lean** | Friendslop | |
| **PlayerCameraRoot local y 1.72**, a GroundedRadius/Offset fix, and the build-at-scale-1-then-scale order | Friendslop | Keeps the world camera identical to today. |
| **Backwards-cap variant** made by yawing CapPivot 180° | Friendslop and Chibi | P2's googly badge now stares at the player from behind. |
| **Admiral the rubber duck** | Chibi | Moved from the bib pocket to the **back-left pocket, facing the camera**, so players see it all game. The red rag hangs in the right pocket. |
| **HeadBobble pivot at the chin** | Chibi | A bobble-head spring for the heavy, plush head. |
| **Animator culling set to AlwaysAnimate** | Chibi | The skinned mesh is deactivated, so culling must not depend on it. |
| **Tummy-pat IK fidget, look-see fidget** | Chibi | |
| Hard hat, beanie and pigtail variant ideas; "calls the truck Gertrude, names every bin" | Chibi and Plumber | Merged into the co-op family in section 14. |
| "Terrified of raccoons" | Friendslop | A hook for the existing Raccoon critters. |

**Deliberately not grafted:**
- **Binny the bin backpack.** Parked as a future unlockable cosmetic (a Scrapazon item), because it hides the back silhouette.
- **The Chibi green-cap palette.** It is poor for readability from behind.
- **Skin eyelids.** Googly eyes are stuck-on props; squash-blinking is cheaper and sturdier.
- **The Chibi back patch.** It adds clutter; the straps, belt, rag and duck already carry the back.
- **The Plumber `Cap_BadgeRing`.** Cut to stay at 60 renderers; the white disc reads on orange without it.
- **The optional Mario overhead wind-up.** TrashCarrier launches from the chest hold point, so the bag would teleport from over his head, and it would block the camera.
- **The Friendslop hold-point numbers.** They assume the wrong units.

## 4. Silhouette and proportions

All numbers are rig metres at root scale 1, with world values at root scale 0.8 in brackets.

- **Height:** 1.98 to the top of the cap button [**1.59 m**]. Eyes are at 1.615 [1.29], shoulders at about 1.42 [1.14], the belt at 0.93 [0.74], and the boot tops at 0.32 [0.26].
- **Proportions:** about **3.2 heads tall**. Head plus cap is 0.61 (31% of his height). The egg belly is 0.66 wide by 0.68 tall by 0.58 deep [0.53 m wide] and is the widest part of him. There is no neck: the head sinks into the orange shoulders. The low round seat hides the thighs, so only about 25 cm of pant leg shows above the big boots.
- **Key shapes, top to bottom:**
  - a yellow button on an orange dome cap with a thick yellow duck-bill brim;
  - the googly pin;
  - brows, then two googly eyes, then a ball nose, then a walrus mustache wider than the jaw;
  - an orange collar and a square olive bib with bottle-cap buttons;
  - the egg belly with a glowing yellow belt ring;
  - a round seat, stubby olive legs with yellow shin rings, and big brown boots with tan toes.
- **From the chase camera at about 5 m:** he fills about 43% of the screen height; the mannequin filled 49%. From behind you read:
  - an orange dome with a yellow dot, and a brown hair band with a bed-head flick;
  - orange shoulders crossed by dark-olive suspenders;
  - an olive egg with a yellow belt;
  - the red rag on the right and Admiral on the left;
  - yellow shin rings and brown boot heels.
- **From the front three-quarter view that co-op friends see:** the brim, eyes, nose and mustache stack is the Mario read; the googly pin and hi-vis rings are the sanitation read.
- **Arms:** in idle they stick out about 40° like penguin flippers (from ArmSplay), with big yellow fists.

![game camera](CharacterRefs/chuck_gamecam.png)
*The game camera at root scale 0.8 with PlayerCameraRoot at local 1.72, next to a 1.0 m wheelie bin. The screen center lands at cap height, as it does on the mannequin's head today.*

![scale](CharacterRefs/chuck_scale.png)
*The red line is the old 1.8 m mannequin height. The bin is a world-size wheelie bin.*

## 5. Root scale: YES, uniform 0.8 (decided)

Proportions alone cannot make him short. The rig's Head bone is fixed at 1.62 m, so any chibi head built on it tops out near 2 m. At 0.8 he is 1.59 m, which is short and stout next to bins, doors and the truck. Going below 0.75 makes the tuning snowball; do not go there without re-running this section.

**Build every part at root scale 1, then set the scale.** Apply all of these to `Assets/Prefabs/Player.prefab`:

| Setting | Old | New | Why / world result |
|---|---|---|---|
| Player root `localScale` | 1 | **(0.8, 0.8, 0.8)** | NetworkTransform has SyncScale X/Y/Z = 0, so every client uses the prefab value. |
| CharacterController `height` | 1.8 | **1.90** | 1.52 m world. |
| CharacterController `radius` | 0.28 | **0.32** | 0.256 m world. That is fatter than an unchanged 0.224 (the belly is 0.26), but still under the old 0.28, so there are no doorway or truck-side regressions. |
| CharacterController `center` | (0, 0.93, 0) | **(0, 0.95, 0)** | 0.76 m world; the capsule bottom stays at the feet. |
| CharacterController `stepOffset` | 0.25 | 0.25 | Verify he climbs the sidewalk curb; raise it to 0.30 if he snags. |
| ThirdPersonController `GroundedRadius` | 0.28 | **0.25** | World units: it must not be wider than the capsule, or he reports grounded against curb edges. |
| ThirdPersonController `GroundedOffset` | -0.14 | **-0.11** | The ground sphere still reaches 0.14 m below the feet. |
| `PlayerCameraRoot` localPosition.y | 1.375 | **1.72** | 1.376 m world, so the look height and the throw aim are unchanged. |
| Cinemachine ThirdPersonFollow (ShoulderOffset (0,0,-3), VerticalArmLength 0.4, CameraDistance 2) | | **unchanged** | World units. |
| Animator `cullingMode` | CullUpdateTransforms | **AlwaysAnimate** | The skinned mesh is deactivated; at most 4 players, so the cost is negligible. |
| Animator `speed` | 1 | **1.2**, set in `ChuckPuppet.Awake` on every client | The stride is 20% shorter at the same MoveSpeed, so this stops foot slide. It reads as a stubby-leg scurry. |
| MoveSpeed 3 / SprintSpeed 5.335 / JumpHeight 1.2 | | unchanged | The 1.2 m jump is now 76% of his height: a big Mario hop. |
| `TrashCarrier.holdLocalPosition` | (0, 1.05, 0.55) | **(0, 0.90, 0.48)** | **World units.** The bag rests on his belly and his eyes peek over it. Section 12 has the details. |
| New TrashCarrier fields `chargePullIn` / `chargeLift` (replace the hard-coded 0.22 / 0.14 in `HoldTarget`; keep those as defaults) | 0.22 / 0.14 | **0 / 0.08** on the Player | Stops the wind-up from shoving the bag 25 cm into his belly and face. |
| New TrashCarrier field `bellyStop` (and the hold spring damps against the hold target's velocity) | none (the spring trailed 2ζV/ω, 12-30 cm, behind a walking carrier) | **0.04** | With the bag resting on his belly, any trailing lag is clipping. Relative damping removes the steady lag while walking; the stop caps start/turn sag at 4 cm. |
| New getter `TrashCarrier.HeldExtents => heldExtents` | | add | Bag half-extents for the hug IK. Adding a member is allowed. |
| New method `GooglyPupil.Kick(Vector2 localVelocity)` | | add | The cross-eyed fidget and scripted pupil kicks. |
| `ShopHeadlamp.FollowHead` | head bone + 0.13 up + 0.1 fwd | if a `HeadlampMount` child exists, use `rig.position = mount.position`, no extra offsets, and set `rig.localScale = root.lossyScale` | Otherwise the lamp spawns inside the cap. |
| Truck exit | | ChuckPuppet re-asserts `localScale = 0.8` when the root has no parent and the scale drifted by more than 0.001 | Defensive. The truck root is scale 1 and uses `parent =` / `SetParent(null, true)`, so the scale normally survives. |

Unaffected (all world units):
- the interaction ray origin at `position + up*1.0`, which is now his chest;
- the proximity scan;
- move speeds and gravity;
- the truck seat, whose renderers are hidden;
- carried items, which are never parented to the player;
- the first-person sort-station hand.

## 6. Palette and materials

All materials are URP/Lit, Opaque, Metallic workflow, flat base color with no textures. Metallic is 0 except BottleCap (0.7). Receive shadows is on. Name them `M_Chuck_<Role>` in `Assets/Materials/Chuck/`. Share them across players, except for the three per-player instances noted below. Do not use MaterialPropertyBlocks anywhere, so the SRP Batcher stays on.

| Role | Hex | Smoothness | Used on / notes |
|---|---|---|---|
| PlayerColor | `#FF7A1F` | 0.15 | P1 Tangerine hi-vis orange: cap crown, shirt, 4 sleeve pieces (per-player material instance) |
| HazardYellow | `#FFC21A` | 0.25 | cap brim + cap button (matches HUD yellow and the FEED ME plate) |
| HiVis | `#FFD41F` | 0.45 | reflective tape: waistband + shin bands. URP Lit Emission #FFD41F x 0.35 |
| Overalls | `#648A33` | 0.12 | Rubbish Co. olive (a step brighter than truck #4E6B2E so it reads on asphalt) |
| OverallsShade | `#3F5A1F` | 0.12 | suspender straps |
| RubberYellow | `#FFE14D` | 0.45 | rubber work gloves + Admiral the rubber duck |
| Skin | `#F6C29B` | 0.15 | head, jowls, ears (per-player instance so the strain tint can lerp it) |
| NoseRosy | `#F2A07E` | 0.35 | glossy button nose |
| Blush | `#FF8A9A` | 0.10 | cheek dots |
| Hair | `#7A4E2E` | 0.10 | brows, mustache, back hair, cowlick (per-variant instance) |
| EyeWhite | `#FFFFFF` | 0.75 | googly eye discs + cap badge. The ONLY pure white on him |
| Pupil | `#121216` | 0.85 | pupils |
| MouthDark | `#5A1E2B` | 0.20 | hidden mouth |
| BottleCap | `#C8CED6` | 0.55 | grandma's bottle-cap buttons. Metallic 0.7 |
| Boot | `#6B4428` | 0.20 | boot shaft + body |
| BootToe | `#A87444` | 0.30 | tan steel-toe domes |
| Sole | `#2D2926` | 0.10 | slab soles |
| Rag | `#E2493B` | 0.10 | red shop rag |
| DuckBeak | `#FF6A2B` | 0.30 | Admiral's beak |
| *PlayerColor P2 Bubblegum* | `#FF6FAE` | 0.15 | co-op variant (section 14) |
| *PlayerColor P3 Seafoam* | `#1FC2B0` | 0.15 | co-op variant |
| *PlayerColor P4 Cornflower* | `#4F8DFF` | 0.15 | co-op variant |
| *Hair P2 Auburn / P3 Ginger / P4 Silver* | `#9C4A2A` / `#C8642B` / `#B8B2A8` | 0.10 | co-op variant |
| *StrainRed* (runtime target, not a material) | `#FF8F80` | | Skin lerps toward this while straining |

**Per-player material instances (3):**
- `PlayerColor` covers Cap_Crown, Shirt_Chest, the 4 sleeves, and variant P4's round brim and ridge.
- `Hair` covers the brows, the 3 mustache lobes, Hair_Back, Cowlick, and the variant pigtails and unibrow.
- `Skin` covers Head_Ball, Head_Jowls and both ears; it is animated by the strain tint.

Create these instances once per puppet in Awake with `new Material(shared)` and assign them as `sharedMaterial`.

**Color logic:**
- **White** is only on the googly eyes, so the face reads first.
- **Hazard and hi-vis yellow** is the company accent: the brim, belt and shin bands. It ties him to the HUD and the FEED ME plate.
- **Rubber yellow** is a paler lemon, used for the gloves and Admiral.
- **Olive** means the company, and matches the truck.
- **The player color** covers the cap and shirt, which are the biggest masses the chase camera sees.
- **HiVis emission** makes the bands glow a little in the dim shop and at night.

## 7. Build conventions (read before the tables)

**Conventions:**
- **Units:** all numbers are rig metres, with the Player root at scale 1, in **Player-root axes**: +X is his right, +Y is up, +Z is forward.
- **`Offset from bone`** is the part centre minus the bone's bind-pose position. Use the actual bone transform in the saved prefab pose, which is the A-pose with arms 42° down. The rounded positions in the bone list below are within 5 mm of it.
- **`Euler`** is a Unity Euler (applied Z, then X, then Y) expressed in Player-root axes at bind pose.
- **`Scale`** is Unity primitive `localScale`:
  - Sphere and Cube: full size.
  - **Cylinder and Capsule: x and z are diameters, and y is HALF the length**, because Unity's cylinder and capsule meshes are 2 units tall. A capsule's total length is 2y and must be at least its diameter.
- **`Parent`** is the transform the object ends up under: a bone, or a named pivot (Empty). Pivots are always listed before their children.
- **`Local under X`** in the notes is the same placement expressed in the pivot's local space, as a cross-check.
- `_R` rows are mirrors of `_L` rows (x becomes -x; Euler (x, y, z) becomes (x, -y, -z)). Every row is written out in full anyway. **Exception:** the eyes and brows are deliberately NOT mirrors.

**Build procedure** (editor script `Assets/Scripts/Editor/ChuckPuppetBuilder.cs`, menu *Rubbish/Characters/Rebuild Chuck Puppet*; it must be idempotent):
1. Load `Player.prefab` contents with the root at scale 1 and the bones in the saved bind pose. **Never build from a playing or animated pose.** Delete every object carrying a `PuppetPart` marker component from a previous build.
2. For each row, in table order:
   - Create a primitive, or a bare GameObject for an Empty. `DestroyImmediate` its collider and add the `PuppetPart` marker.
   - Place it at `root.TransformPoint(root.InverseTransformPoint(bone.position) + offset)`, with rotation `root.rotation * Quaternion.Euler(euler)`.
   - Set `localScale = scale` while it is unparented.
   - Call `SetParent(parent, worldPositionStays: true)`. Every bone and pivot has scale 1 at build time, so localScale stays exactly equal to the table value; assert it to within 1e-4.
   - Assign `sharedMaterial` by color role. Set `shadowCastingMode = Off` where the table says OFF. Put it on the same layer as the root.
   - Variant-only rows get `SetActive(false)`.
3. Hide the mannequin: `Geometry/Armature_Mesh.SetActive(false)`. **Do not** use `renderer.enabled`, because the truck and chess code re-enable every child renderer. Leave `Skeleton` active.
4. Add `GooglyPupil` to `EyeRoot_L`, `EyeRoot_R` and `BadgeEyeRoot` with the values in the notes. Add `ChuckPuppet` (and its helpers) to the root.
5. Apply the section 5 table, including root scale 0.8, and save the prefab.

Bone bind positions (Player-local, rounded): Hips (0,0.98,-0.02) · Spine (0,1.04,-0.01) · Chest (0,1.14,-0.01) · UpperChest (0,1.25,-0.01) · Head (0,1.62,-0.01) · Left_UpperArm (-0.16,1.42,-0.06) · Left_LowerArm (-0.35,1.21,-0.05) · Left_Hand (-0.44,1.07,0.12) · Left_UpperLeg (-0.09,0.93,-0.03) · Left_LowerLeg (-0.10,0.51,-0.03) · Left_Foot (-0.12,0.10,-0.06) · Left_Toes (-0.13,0.03,0.06). Right bones mirror these in x.

## 8. Parts tables

**Totals:**
- 60 active mesh renderers:
  - head 24;
  - torso 12;
  - arms 10;
  - legs 14.
- 13 active empties.
- 8 variant-only objects (6 meshes and 2 pivots), built inactive. At most 60 renderers are ever active in any variant.
- 16 small parts have shadows OFF.

### 8.1 Head (Head bone; everything sits under `HeadBobble`)
| # | Name | Bone | Parent | Prim | Offset from bone (x, y, z) | Euler | Scale | Colour | Shadow | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `HeadBobble` | Head | Head | Empty | (0, -0.22, 0.02) | (0, 0, 0) | 1 | - | - | Pivot at the chin/neck join (rig 0, 1.40, 0.01). Parent of EVERY head part and head pivot. Bobble-head spring rotates it. |
| 2 | `Head_Ball` | Head | HeadBobble | Sphere | (0, 0.02, 0.03) | (0, 0, 0) | (0.54, 0.5, 0.5) | Skin | on | Big skull ball, rig y 1.39-1.89. No neck: it sinks into the shoulders. |
| 3 | `Head_Jowls` | Head | HeadBobble | Sphere | (0, -0.12, 0.06) | (0, 0, 0) | (0.46, 0.26, 0.4) | Skin | on | Baby-round cheeks/chin (bottom rig y 1.37). The mustache and mouth sit on it. |
| 4 | `Nose` | Head | HeadBobble | Sphere | (0, -0.11, 0.3) | (0, 0, 0) | (0.16, 0.135, 0.14) | NoseRosy | on | Big glossy ball, ~30% of face width; overlaps the lower-inner eye corners and rests on the mustache. |
| 5 | `Ear_L` | Head | HeadBobble | Sphere | (-0.268, -0.02, 0.01) | (0, 0, 0) | (0.06, 0.1, 0.085) | Skin | OFF | Half embedded under the cap edge; visible from the chase cam. |
| 6 | `Ear_R` | Head | HeadBobble | Sphere | (0.268, -0.02, 0.01) | (0, 0, 0) | (0.06, 0.1, 0.085) | Skin | OFF | Mirror of Ear_L. |
| 7 | `Blush_L` | Head | HeadBobble | Sphere | (-0.16, -0.095, 0.205) | (0, -38, 0) | (0.075, 0.045, 0.025) | Blush | OFF | Pink cheek dot under the outer eye corner, ~1 cm proud. Puffs x1.4 when straining. |
| 8 | `Blush_R` | Head | HeadBobble | Sphere | (0.16, -0.095, 0.205) | (0, 38, 0) | (0.075, 0.045, 0.025) | Blush | OFF | Mirror of Blush_L. |
| 9 | `EyeRoot_L` | Head | HeadBobble | Empty | (-0.066, -0.005, 0.265) | (0, -12, 0) | 1 | - | - | GooglyPupil: pupil=Pupil_L, maxOffset 0.028, gravity 0.8, motionKick 10, bounciness 0.55, twitch 0.12. +Z points out of the face. Blink = localScale.y. |
| 10 | `EyeWhite_L` | Head | EyeRoot_L | Sphere | (-0.066, -0.005, 0.265) | (0, -12, 0) | (0.115, 0.14, 0.048) | EyeWhite | on | Local under EyeRoot_L: (0, 0, 0). Tall glossy googly disc; ~2 cm proud of the face; eyes nearly touch at the bridge. |
| 11 | `Pupil_L` | Head | EyeRoot_L | Sphere | (-0.0706, -0.017, 0.2865) | (0, -12, 0) | (0.055, 0.055, 0.02) | Pupil | OFF | Local under EyeRoot_L: (0, -0.012, 0.022). Starts sagged low; GooglyPupil drives local x/y, keeps z. |
| 12 | `EyeRoot_R` | Head | HeadBobble | Empty | (0.07, -0.003, 0.266) | (0, 12, 0) | 1 | - | - | DELIBERATELY 8% BIGGER EYE (derp). GooglyPupil: pupil=Pupil_R, maxOffset 0.031, other values as EyeRoot_L. Not a mirror. |
| 13 | `EyeWhite_R` | Head | EyeRoot_R | Sphere | (0.07, -0.003, 0.266) | (0, 12, 0) | (0.124, 0.151, 0.052) | EyeWhite | on | Local under EyeRoot_R: (0, 0, 0). The bigger eye. |
| 14 | `Pupil_R` | Head | EyeRoot_R | Sphere | (0.075, -0.016, 0.2895) | (0, 12, 0) | (0.059, 0.059, 0.021) | Pupil | OFF | Local under EyeRoot_R: (0, -0.013, 0.024).  |
| 15 | `Brow_L` | Head | HeadBobble | Sphere | (-0.07, 0.072, 0.278) | (0, -10, 6) | (0.105, 0.036, 0.045) | Hair | on | Bushy lozenge tucked under the brim, touching the eye top; inner end up 6 deg (earnest). y + roll animated. |
| 16 | `Brow_R` | Head | HeadBobble | Sphere | (0.074, 0.075, 0.278) | (0, 10, -6) | (0.105, 0.036, 0.045) | Hair | on | Sits 3 mm higher than Brow_L (on the bigger eye). Not an exact mirror. |
| 17 | `StachePivot` | Head | HeadBobble | Empty | (0, -0.165, 0.28) | (0, 0, 0) | 1 | - | - | Under the nose. Roll + pitch spring (wiggle/flap). SetActive(false) = no-mustache variant. |
| 18 | `Stache_Mid` | Head | StachePivot | Sphere | (0, -0.165, 0.275) | (0, 0, 0) | (0.22, 0.09, 0.11) | Hair | on | Local under StachePivot: (0, 0, -0.005). Centre lobe; nose sits on it; hides the mouth. |
| 19 | `StachePivot_L` | Head | StachePivot | Empty | (-0.05, -0.17, 0.27) | (0, 0, 0) | 1 | - | - | Inner root of the left lobe. Own roll spring, out of sync with the right one. |
| 20 | `Stache_L` | Head | StachePivot_L | Sphere | (-0.105, -0.177, 0.25) | (0, -28, 14) | (0.17, 0.09, 0.1) | Hair | on | Walrus lobe, outer end droops + sweeps back. Total span ~0.40 m, wider than the jaw. |
| 21 | `StachePivot_R` | Head | StachePivot | Empty | (0.05, -0.17, 0.27) | (0, 0, 0) | 1 | - | - | Mirror of StachePivot_L. |
| 22 | `Stache_R` | Head | StachePivot_R | Sphere | (0.105, -0.177, 0.25) | (0, 28, -14) | (0.17, 0.09, 0.1) | Hair | on | Mirror of Stache_L. |
| 23 | `Mouth` | Head | HeadBobble | Sphere | (0, -0.195, 0.235) | (0, 0, 0) | (0.07, 0.03, 0.03) | MouthDark | OFF | Hidden under the mustache. OPEN = scale (0.10,0.075,0.05), move y -0.02, z +0.01. |
| 24 | `Hair_Back` | Head | HeadBobble | Sphere | (0, -0.045, -0.085) | (0, 0, 0) | (0.555, 0.21, 0.36) | Hair | on | Wide ear-to-ear hair band under the cap back; separates orange cap from orange shirt in the chase cam. Keep it WIDE. |
| 25 | `CapPivot` | Head | HeadBobble | Empty | (0, 0.145, 0.01) | (0, 0, 0) | 1 | - | - | Crown centre. Cap pop/float/bob spring. Variants yaw/scale children of THIS pivot. |
| 26 | `Cap_Crown` | Head | CapPivot | Sphere | (0, 0.145, 0.01) | (0, 0, 0) | (0.58, 0.4, 0.57) | PlayerColor | on | Local under CapPivot: (0, 0, 0). Dome, top rig y 1.965; lower half buried in the skull. Biggest colour mass in the chase cam = player colour. |
| 27 | `Cap_Brim` | Head | CapPivot | Cylinder | (0, 0.113, 0.31) | (-6, 0, 0) | (0.42, 0.02, 0.32) | HazardYellow | on | Local under CapPivot: (0, -0.032, 0.3). Duck-bill brim, 4 cm thick, front tipped UP 6 deg, ~18 cm past the face. Rear half buried in the crown. |
| 28 | `Cap_Badge` | Head | CapPivot | Cylinder | (0, 0.18, 0.291) | (76, 0, 0) | (0.13, 0.01, 0.13) | EyeWhite | on | Local under CapPivot: (0, 0.035, 0.281). Rubbish Co. "Company Eye" pin: white disc, face tilted up 14 deg. No letters. Edges lift ~1 cm off the curve (intended). |
| 29 | `BadgeEyeRoot` | Head | CapPivot | Empty | (0, 0.18, 0.291) | (-14, 0, 0) | 1 | - | - | +Z = badge normal. GooglyPupil: pupil=Badge_Pupil, maxOffset 0.036, gravity 1.0, motionKick 12, bounciness 0.5, twitch 0.2 (rattles MORE than his eyes). |
| 30 | `Badge_Pupil` | Head | BadgeEyeRoot | Sphere | (0, 0.1637, 0.3085) | (-14, 0, 0) | (0.048, 0.048, 0.014) | Pupil | OFF | Local under BadgeEyeRoot: (0, -0.02, 0.013).  |
| 31 | `Cap_Button` | Head | CapPivot | Sphere | (0, 0.342, 0.01) | (0, 0, 0) | (0.065, 0.04, 0.065) | HazardYellow | OFF | Local under CapPivot: (0, 0.197, 0). Yellow dot on top; visible from the elevated chase cam. |
| 32 | `HeadlampMount` | Head | CapPivot | Empty | (0, 0.27, 0.245) | (0, 0, 0) | 1 | - | - | Local under CapPivot: (0, 0.125, 0.235). Where ShopHeadlamp puts the Scrapazon headlamp: crown surface above the badge. Rides with the cap pop. |
| 33 | `CowlickPivot` | Head | HeadBobble | Empty | (0.188, 0.005, -0.122) | (-53, -60, 0) | 1 | - | - | Base of the bed-head flick inside the skull, back-right. NOT under CapPivot (hair stays when the hat pops). Boing spring. |
| 34 | `Cowlick` | Head | CowlickPivot | Capsule | (0.2399, 0.0501, -0.1519) | (-53, -60, 0) | (0.055, 0.075, 0.055) | Hair | on | Local under CowlickPivot: (0, 0.075, 0). 15 cm tuft flicking out/up/back behind the RIGHT ear only. Never on top of the cap (reads as a pumpkin stem). |

### 8.2 Torso (Hips / Spine / Chest / UpperChest)
| # | Name | Bone | Parent | Prim | Offset from bone (x, y, z) | Euler | Scale | Colour | Shadow | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 35 | `Shirt_Chest` | UpperChest | UpperChest | Sphere | (0, 0.05, -0.02) | (0, 0, 0) | (0.6, 0.4, 0.46) | PlayerColor | on | Shoulders + upper back (rig y 1.10-1.50): orange collar under the jowls, orange back panel crossed by the straps. |
| 36 | `BellyPivot` | Spine | Spine | Empty | (0, -0.05, 0.04) | (0, 0, 0) | 1 | - | - | Belly centre (rig 0, 0.99, 0.03). Jiggle spring scales this pivot. |
| 37 | `Overalls_Belly` | Spine | BellyPivot | Sphere | (0, -0.05, 0.04) | (0, 0, 0) | (0.66, 0.68, 0.58) | Overalls | on | Local under BellyPivot: (0, 0, 0). Egg belly rig y 0.65-1.33; widest part of him; hides waist + hips. Front at rig z 0.32. |
| 38 | `HiVis_Waistband` | Spine | BellyPivot | Cylinder | (0, -0.11, 0.04) | (0, 0, 0) | (0.67, 0.025, 0.6) | HiVis | on | Local under BellyPivot: (0, -0.06, 0). 5 cm reflective belt ring at rig y 0.93, 10-15 mm proud. Under BellyPivot so it jiggles with the belly. |
| 39 | `Overalls_Seat` | Hips | Hips | Sphere | (0, -0.2, -0.03) | (0, 0, 0) | (0.58, 0.44, 0.48) | Overalls | on | Low round seat, bottom rig y 0.56. Hides the thighs (main short-legs trick). Jiggles by its own localScale. |
| 40 | `Overalls_Bib` | Chest | Chest | Cube | (0, 0.14, 0.2) | (-8, 0, 0) | (0.24, 0.12, 0.05) | Overalls | on | Square bib over the shirt, top at rig y 1.34; bottom buried in the belly. |
| 41 | `Strap_L` | UpperChest | UpperChest | Cylinder | (-0.13, 0.05, -0.02) | (0, 0, 90) | (0.39, 0.035, 0.444) | OverallsShade | on | RING TRICK: elliptical cylinder on the X axis sized to Shirt_Chest cross-section at x=-0.13 (+1.5 cm). Only the proud arc shows = suspender over the shoulder and straight down the back. Recompute if Shirt_Chest changes: 2*(radii*sqrt(1-(0.13/0.30)^2)+0.015). |
| 42 | `Strap_R` | UpperChest | UpperChest | Cylinder | (0.13, 0.05, -0.02) | (0, 0, -90) | (0.39, 0.035, 0.444) | OverallsShade | on | Mirror of Strap_L. |
| 43 | `Button_L` | Chest | Chest | Cylinder | (-0.1, 0.175, 0.225) | (82, 0, 0) | (0.062, 0.01, 0.062) | BottleCap | OFF | Grandma's bottle cap where the strap meets the bib corner. |
| 44 | `Button_R` | Chest | Chest | Cylinder | (0.1, 0.175, 0.225) | (82, 0, 0) | (0.062, 0.01, 0.062) | BottleCap | OFF | Mirror of Button_L. |
| 45 | `RagPivot` | Hips | Hips | Empty | (0.116, -0.088, -0.231) | (15, -15, 6) | 1 | - | - | Top edge of the rag at the back-RIGHT pocket. Pendulum spring. |
| 46 | `Rag` | Hips | RagPivot | Cube | (0.1302, -0.1697, -0.2498) | (15, -15, 6) | (0.11, 0.17, 0.014) | Rag | on | Local under RagPivot: (0, -0.085, 0). Red shop rag = little swinging tail from the chase cam. |
| 47 | `DuckPivot` | Hips | Hips | Empty | (-0.12, -0.175, -0.265) | (0, 0, 0) | 1 | - | - | Back-LEFT pocket rim. Nod spring (footfalls). |
| 48 | `Duck_Head` | Hips | DuckPivot | Sphere | (-0.12, -0.15, -0.265) | (0, 0, 0) | (0.095, 0.095, 0.095) | RubberYellow | on | Local under DuckPivot: (0, 0.025, 0). ADMIRAL, the rubber duck Chuck found on his first route, riding in his back pocket and facing the camera. Top stays below the waistband. |
| 49 | `Duck_Beak` | Hips | DuckPivot | Sphere | (-0.12, -0.158, -0.311) | (10, 0, 0) | (0.055, 0.022, 0.05) | DuckBeak | OFF | Local under DuckPivot: (0, 0.017, -0.046). Points BACKWARD at the player. |

### 8.3 Arms
| # | Name | Bone | Parent | Prim | Offset from bone (x, y, z) | Euler | Scale | Colour | Shadow | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 50 | `Sleeve_Upper_L` | Left_UpperArm | Left_UpperArm | Capsule | (-0.105, -0.115, 0.005) | (0, 0, -42) | (0.155, 0.15, 0.155) | PlayerColor | on | Puffy sleeve along shoulder->elbow; inner half buried in the chest. |
| 51 | `Sleeve_Upper_R` | Right_UpperArm | Right_UpperArm | Capsule | (0.105, -0.115, 0.005) | (0, 0, 42) | (0.155, 0.15, 0.155) | PlayerColor | on | Mirror of Sleeve_Upper_L. |
| 52 | `Sleeve_Lower_L` | Left_LowerArm | Left_LowerArm | Capsule | (-0.04, -0.06, 0.075) | (-54, -28, 0) | (0.135, 0.13, 0.135) | PlayerColor | on | Along elbow->wrist (bind dir -0.38,-0.59,0.71). |
| 53 | `Sleeve_Lower_R` | Right_LowerArm | Right_LowerArm | Capsule | (0.04, -0.06, 0.075) | (-54, 28, 0) | (0.135, 0.13, 0.135) | PlayerColor | on | Mirror of Sleeve_Lower_L. |
| 54 | `Glove_Cuff_L` | Left_Hand | Left_Hand | Cylinder | (0.008, 0.012, -0.014) | (-54, -28, 0) | (0.155, 0.028, 0.155) | RubberYellow | on | Flared rubber-glove cuff, wider than the sleeve. |
| 55 | `Glove_Cuff_R` | Right_Hand | Right_Hand | Cylinder | (-0.008, 0.012, -0.014) | (-54, 28, 0) | (0.155, 0.028, 0.155) | RubberYellow | on | Mirror of Glove_Cuff_L. |
| 56 | `Glove_Mitt_L` | Left_Hand | Left_Hand | Sphere | (-0.027, -0.035, 0.048) | (-54, -28, 0) | (0.15, 0.17, 0.14) | RubberYellow | on | Big round rubber mitt, centre 6.5 cm along the hand; ignore finger bones. |
| 57 | `Glove_Mitt_R` | Right_Hand | Right_Hand | Sphere | (0.027, -0.035, 0.048) | (-54, 28, 0) | (0.15, 0.17, 0.14) | RubberYellow | on | Mirror of Glove_Mitt_L. |
| 58 | `Glove_Thumb_L` | Left_Hand | Left_Hand | Capsule | (0.045, -0.03, 0.07) | (-34, 22, 0) | (0.06, 0.045, 0.06) | RubberYellow | OFF | Stubby thumb, inner-front of the mitt. |
| 59 | `Glove_Thumb_R` | Right_Hand | Right_Hand | Capsule | (-0.045, -0.03, 0.07) | (-34, -22, 0) | (0.06, 0.045, 0.06) | RubberYellow | OFF | Mirror of Glove_Thumb_L. |

### 8.4 Legs and boots
| # | Name | Bone | Parent | Prim | Offset from bone (x, y, z) | Euler | Scale | Colour | Shadow | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 60 | `Pants_Thigh_L` | Left_UpperLeg | Left_UpperLeg | Capsule | (-0.02, -0.215, 0) | (0, 0, -1) | (0.22, 0.25, 0.22) | Overalls | on | Rig y 0.465-0.965; top buried in seat/belly (same colour: overlap reads as squish). 2 cm outward so thighs just touch. |
| 61 | `Pants_Thigh_R` | Right_UpperLeg | Right_UpperLeg | Capsule | (0.02, -0.215, 0) | (0, 0, 1) | (0.22, 0.25, 0.22) | Overalls | on | Mirror of Pants_Thigh_L. |
| 62 | `Pants_Shin_L` | Left_LowerLeg | Left_LowerLeg | Capsule | (-0.02, -0.16, -0.012) | (4, 0, -3) | (0.21, 0.17, 0.21) | Overalls | on | Baggy stub of pant leg, tucked into the boot shaft; ~3 cm daylight between shins. |
| 63 | `Pants_Shin_R` | Right_LowerLeg | Right_LowerLeg | Capsule | (0.02, -0.16, -0.012) | (4, 0, 3) | (0.21, 0.17, 0.21) | Overalls | on | Mirror of Pants_Shin_L. |
| 64 | `HiVis_ShinBand_L` | Left_LowerLeg | Left_LowerLeg | Cylinder | (-0.022, -0.09, -0.007) | (4, 0, -3) | (0.225, 0.022, 0.225) | HiVis | OFF | Reflective ring at rig y 0.42, 7.5 mm proud. |
| 65 | `HiVis_ShinBand_R` | Right_LowerLeg | Right_LowerLeg | Cylinder | (0.022, -0.09, -0.007) | (4, 0, 3) | (0.225, 0.022, 0.225) | HiVis | OFF | Mirror of HiVis_ShinBand_L. |
| 66 | `Boot_Shaft_L` | Left_LowerLeg | Left_LowerLeg | Cylinder | (-0.028, -0.295, -0.022) | (4, 0, -3) | (0.23, 0.105, 0.25) | Boot | on | Rubber-boot upper rig y 0.11-0.32, on the LOWER LEG (not Foot) so it stays aligned with the shin when the ankle flexes; Boot_Body hides the joint. |
| 67 | `Boot_Shaft_R` | Right_LowerLeg | Right_LowerLeg | Cylinder | (0.028, -0.295, -0.022) | (4, 0, 3) | (0.23, 0.105, 0.25) | Boot | on | Mirror of Boot_Shaft_L. |
| 68 | `Boot_Body_L` | Left_Foot | Left_Foot | Sphere | (-0.015, -0.005, 0.06) | (0, 0, 0) | (0.24, 0.2, 0.37) | Boot | on | Heel + instep; round heel sticks out ~12 cm behind the ankle. |
| 69 | `Boot_Body_R` | Right_Foot | Right_Foot | Sphere | (0.015, -0.005, 0.06) | (0, 0, 0) | (0.24, 0.2, 0.37) | Boot | on | Mirror of Boot_Body_L. |
| 70 | `Boot_Toe_L` | Left_Toes | Left_Toes | Sphere | (-0.01, 0.05, 0.08) | (0, 0, 0) | (0.235, 0.16, 0.21) | BootToe | on | Tan steel-toe dome to rig z 0.24; on Toes so it rolls at push-off. |
| 71 | `Boot_Toe_R` | Right_Toes | Right_Toes | Sphere | (0.01, 0.05, 0.08) | (0, 0, 0) | (0.235, 0.16, 0.21) | BootToe | on | Mirror of Boot_Toe_L. |
| 72 | `Boot_Sole_L` | Left_Foot | Left_Foot | Cube | (-0.015, -0.075, 0.085) | (0, 0, 0) | (0.235, 0.05, 0.29) | Sole | on | Slab sole, bottom at rig y 0 (foot-local z -0.06..0.23). Short and chunky so it reads as a sole, not a skate blade, in side view. |
| 73 | `Boot_Sole_R` | Right_Foot | Right_Foot | Cube | (0.015, -0.075, 0.085) | (0, 0, 0) | (0.235, 0.05, 0.29) | Sole | on | Mirror of Boot_Sole_L. |

### 8.5 Variant-only parts (built inactive; see section 14)
| # | Name | Bone | Parent | Prim | Offset from bone (x, y, z) | Euler | Scale | Colour | Shadow | Notes |
|---|---|---|---|---|---|---|---|---|---|---|
| 74 | `PigtailPivot_L` | Head | HeadBobble | Empty | (-0.24, -0.03, -0.1) | (0, 0, 20) | 1 | - | - | P2 Dot only. Pendulum spring (rag settings, clamp 30 deg). |
| 75 | `Pigtail_L` | Head | PigtailPivot_L | Sphere | (-0.2827, -0.0881, -0.11) | (0, 0, 20) | (0.15, 0.19, 0.15) | Hair | on | Local under PigtailPivot_L: (-0.06, -0.04, -0.01). Bouncy side puff below the cap, reads from behind. |
| 76 | `PigtailPivot_R` | Head | HeadBobble | Empty | (0.24, -0.03, -0.1) | (0, 0, -20) | 1 | - | - | Mirror of PigtailPivot_L. |
| 77 | `Pigtail_R` | Head | PigtailPivot_R | Sphere | (0.2827, -0.0881, -0.11) | (0, 0, -20) | (0.15, 0.19, 0.15) | Hair | on | Local under PigtailPivot_R: (0.06, -0.04, -0.01). Mirror of Pigtail_L. |
| 78 | `Cap_BeanieCuff` | Head | CapPivot | Cylinder | (0, 0.1, 0.012) | (0, 0, 0) | (0.6, 0.035, 0.59) | HazardYellow | on | P3 Ozzie only: knit cuff ring of the beanie. |
| 79 | `Cap_RoundBrim` | Head | CapPivot | Cylinder | (0, 0.105, 0.03) | (0, 0, 0) | (0.66, 0.012, 0.68) | PlayerColor | on | P4 Bert only: full round hard-hat brim. |
| 80 | `Cap_Ridge` | Head | CapPivot | Cube | (0, 0.33, 0) | (0, 0, 0) | (0.05, 0.04, 0.46) | PlayerColor | on | P4 Bert only: hard-hat crown ridge. |
| 81 | `Unibrow` | Head | HeadBobble | Capsule | (0, 0.076, 0.285) | (0, 0, 90) | (0.045, 0.1, 0.045) | Hair | on | P4 Bert only: replaces Brow_L/Brow_R. |

## 9. Face construction

Everything sits on HeadBobble, which is under the Head bone. It is stacked Mario-style from top to bottom, from rig y 1.72 down to 1.37.

![face](CharacterRefs/chuck_face.png)

- **Head.** A skin ball (0.54 × 0.50 × 0.50) centred 2 cm above and 3 cm in front of the Head bone. Below it sits a **jowls** ellipsoid (0.46 × 0.26 × 0.40), 12 cm lower, that makes a baby-round chin. There is no neck.
- **Cap line.** The crown's front edge crosses the face just above the eyes. The yellow brim sticks out 18 cm and tips up 6°, and the brows tuck right under it.
- **Eyes: googly, and mismatched on purpose.**
  - Two tall, glossy white discs. The left is 0.115 × 0.14 × 0.048; the right is 8% bigger at 0.124 × 0.151 × 0.052.
  - Each is yawed 12° outward and sits about 2 cm proud of the face, with only about 1.6 cm between them at the bridge.
  - Each has a black pupil (5.5 cm on the left, 5.9 cm on the right) on an unscaled `EyeRoot` running `GooglyPupil`.
  - The pupils start sagged low, rattle on every footfall, slosh on turns and twitch on their own. One usually ends up lower than the other.
- **Brows.** Short bushy brown lozenges (0.105 × 0.036 × 0.045), inner ends up 6° for an earnest look. The right brow is 3 mm higher and 4 mm further out, riding its bigger eye. They pop up on pickups and treasure, and dip inner-down (determined) while charging a throw.
- **Nose.** A big glossy rosy ball (0.16 × 0.135 × 0.14), about 30% of the face width, sticking out 11 cm. It overlaps the lower-inner corners of the eyes but stays below where the pupils rest, so it never hides them. It rests on the mustache.
- **Mustache.** A three-lobe walrus in warm brown, about 0.40 m across, wider than the jaw.
  - The centre lobe (0.22 wide) sits under the nose on `StachePivot`.
  - Two side lobes (0.17), swept back 28° and drooping 14°, each hang from their own pivot at the inner root, so they wiggle out of sync.
- **Mouth.** A dark maroon oval hidden under the mustache. It pops open below the mustache for HUP!, HWAAH!, om-nom, the free-fall "O" and yawns.
- **Extras.**
  - Pink blush dots under the outer eye corners; they puff when straining.
  - Small ears, half out under the cap edge.
  - A wide brown hair band across the back of the head, which separates the orange cap from the orange shirt in the chase camera.
  - One bed-head flick behind the right ear only.
- **Expression states.** These are all scale and offset tweaks; there are no extra parts.

| State | Eyes (EyeRoot scale.y) | Brows | Mouth | Other |
|---|---|---|---|---|
| Neutral | 1 | rest (inner up 6°) | closed | pupils sag |
| Pickup "hup" | 1 | +0.012 y, inner +8°, for 0.5 s | small "o" (0.08, 0.05, 0.04) for 0.3 s | belly squash, cowlick boing |
| Strain (heavy carry or charge) | 0.55 at full | -0.01 y, inner down -12° × charge | closed (gritted) | cheeks x(1 + 0.4k), Skin toward StrainRed |
| Yell (throw release, jump) | 1 | +0.015 y | OPEN | mustache flap |
| Surprise / free-fall | 1.15 | +0.015 y | "O" (0.08, 0.08, 0.05) | cap lifted |
| Happy (bag accepted by the truck) | 0.6 for 0.4 s | +0.01 y | "om-nom" open 0.3 s | cowlick boing |
| Treasure (sort-table rare find nearby) | 1.1 | +0.02 y for 1 s | "o" | pupils twitch |

It reads as Mario-esque (big nose, walrus mustache, eyes under the brim) but is not Mario: the eyes are googly with no blue irises, and he has rosy blush, baby jowls, a googly cap pin and yellow rubber gloves.

## 10. Uniform details (Rubbish Co. sanitation kit)

- **Cap.**
  - A dome crown in the player color (P1 tangerine hi-vis orange) with a bold, 4 cm-thick hazard-yellow duck-bill brim tipped up 6°, and a yellow top button.
  - The front carries the **Rubbish Co. "Company Eye" pin**: a white disc with a live googly pupil that rattles harder than his real eyes. It is original, has no letters, and nods to the googly trash-bag mascot on the depot roof.
- **Shirt.** A long-sleeved hi-vis work shirt in the player color with puffy sleeves, giving the "colored shirt under overalls" contrast.
- **Overalls.** Rubbish Co. olive (#648A33, a step brighter than truck #4E6B2E so they read on asphalt):
  - an egg belly and a square bib;
  - two dark-olive suspenders that wrap over the shoulders and run straight down the orange back (the ring trick);
  - a low round seat;
  - stubby baggy legs tucked into the boots.
- **Buttons.** Grandma's two silver bottle caps, on the bib corners where the straps meet it.
- **Hi-vis.** Reflective hazard-yellow tape with a slight emission: a belt ring around the tummy (it doubles as the belt) plus a ring on each shin. It reads from every angle, including straight behind, and glows a little at night and in the shop.
- **Gloves.** Chunky **yellow rubber work gloves** with flared cuffs and stubby thumbs.
- **Boots.** Tall brown rubber work boots (0.26 m world) with tan steel-toe domes and charcoal slab soles.
- **Pockets.** A red shop rag hangs from the back-right pocket like a tail. **Admiral**, the rubber duck, rides in the back-left pocket and faces backward at the player.
- **Deliberately avoided:**
  - a red cap with blue overalls;
  - any letter or initial emblem;
  - white gloves;
  - a green pipe-like badge (an early bin icon read as a warp pipe);
  - the Luigi, Wario and Waluigi color pairs in every variant.

## 11. Runtime: ChuckPuppet (secondary motion, splay, face)

**Architecture.** Put these in `Assets/Scripts/Player/Puppet/`:
- `ChuckPuppet.cs`: bones, lean, ArmSplay, carry IK, flail, variant, animator speed, scale guard.
- `ChuckSprings.cs`: every pivot spring.
- `ChuckFace.cs`: blink, squint, brows, mouth, strain tint, fidget faces.

**Rules they share:**
- **Where they run:** on the Player root with `[DefaultExecutionOrder(1000)]`. Everything that touches bones or pivots runs in **LateUpdate, after the Animator**, on **every client**. They are cosmetic and never write NetworkVariables.
- **Inputs:** replicated Animator parameters (`Speed`, `MotionSpeed`, `Grounded`, `Jump`, `FreeFall`) and root and bone velocities. Local-owner extras come from the `TrashCarrier` on the same object (`IsCarrying`, `Held.MassKg`, `IsCharging`, `Charge01`, `HoldPoint`, `HeldExtents`, and the `PickedUp` / `Released` events).
- **Footfall kicks:** add `void OnFootstep(AnimationEvent e)` to ChuckPuppet. Unity calls it on every component of the Animator's GameObject, as ThirdPersonController already does. Only react when `e.animatorClipInfo.weight > 0.5`.
- **Guard (critical):** if `!animator.isActiveAndEnabled` (seated in the truck), skip every bone write. Additive bone rotations would otherwise accumulate every frame and spin the arms.
- **Rest poses:** cache every pivot's rest `localPosition`, `localRotation` and `localScale` in Awake, and always write `rest ∘ offset`. Never accumulate.
- **Springs:** semi-implicit damped oscillators, `a = -ω²(x - target) - 2ζωv + impulses`. Clamp dt to 1/30 and sub-step at 1/120 or less. Local offsets are in **rig metres**, because pivot parents are unscaled rig space. Accelerations are world m/s², measured from `(v - vPrev) / dt` of the named transform and expressed in root axes.

**ArmSplay (required).** The Starter Assets idle and run arms hang about 12° from vertical and would sink about 8 cm into the 0.66 m belly.
- **Base:** each LateUpdate, rotate `UpperArm` by `AngleAxis(sign * θ, root.forward)` on top of the animated pose. `sign` is **-1 for the left arm and +1 for the right**; this moves the elbow away from the midline.
- **Search:** θ = 18° base, plus the smallest extra in [0°, 27°] found by 4 binary-search steps that clears three test points: the elbow (`LowerArm.position`), the wrist (`Hand.position`) and the mitt centre (`Glove_Mitt_*.position`).
- **Test:** a point clears if it is outside all three inflated ellipsoids: `Overalls_Belly`, `Shirt_Chest` and `Overalls_Seat`. For part T with table scale s, compute `d = T.InverseTransformPoint(p)`; the point is inside if `Σ (d_i·s_i)² / (0.5·s_i + m)² < 1`. Use m = 0.07 for the elbow and wrist and 0.08 for the mitt. Because the test uses the parts' own transforms, it follows the belly jiggle.
- **Forearm fallback:** if the mitt still intersects at 45°, rotate `LowerArm` about root.forward (same sign) by up to 20° more.
- **Smoothing:** raise the angle instantly (never show a clipping frame) and relax it with `1 - exp(-12·dt)`.
- **Weight:** multiply by `(1 - carryWeight)`. Free-fall flail and the stretch-yawn add on top.
- **Target look:** upper arms about 35-45° out, like penguin flippers.
- **Raised arms:** the splay (and the fling/yawn extras) fades out as the upper arm rises from 60° to 100° of frontal elevation, because the same rotation swings a raised mitt in over his head.
- **Head clearance (last, every frame):** the elbow, mid-forearm, wrist and mitt must also clear `Head_Ball` and `Cap_Crown` (m = 0.07, mitt 0.08). If not, swing the upper arm about the shoulder by the smallest angle (≤ 70°, 5 binary steps) that clears the head without entering the belly: first straight away from the head centre, then the frontal swing either way, and as a last resort fold the forearm out. The JumpStart crouch and the InAir clip otherwise put his mitts in his face.

**Secondary motion and tuning** (starting values):

| Layer | Transform and channels | ω (rad/s) | ζ | Clamp | Drivers |
|---|---|---|---|---|---|
| Bobble head | `HeadBobble` pitch and roll | 11 | 0.35 | pitch ±7°, roll ±5° | Hips world accel: forward accel tips the head back (-0.8° per m/s²), lateral accel rolls it (0.6° per m/s²). Footfall nod +1.5° × speed/5. Landing -4° × clamp01(\|vy\|/6). |
| Speed lean | Chest pitch (additive) | none | none | 0-4° | +4° × planarSpeed/5.3, with the Head countered by -2°. |
| Cap bob / pop / float | `CapPivot` local y, pitch, yaw | 14 (yaw 10) | 0.35 (yaw 0.3) | y [-0.02, +0.07] m, pitch [-14°, +10°], yaw ±30° | Jump takeoff: y +0.05 and pitch -10° (brim up, the hat lifts off). **Apex float:** target y +0.04 while airborne with \|vy\| < 2. Landing with fall speed > 3: y -0.02, then +0.06, a yaw kick of ±25° (random sign) and pitch +6°. Running: pitch lag ±5° from forward accel. Full throw charge: pitch -5°. |
| Belly jiggle | `BellyPivot` scale: y = 1 + s, x = z = 1/√(1 + s) | 16 | 0.25 | s ∈ [-0.12, +0.08] | Landing s -= 0.12 × clamp01(\|vy\|/6). Footfall -0.025 × speed/5. Takeoff +0.06 (stretch). Idle breath 0.015·sin(2π·0.3t). Each belly pat -0.05. Pickup -0.06. |
| Seat jiggle | `Overalls_Seat` own scale, same formula | 13 (lags) | 0.25 | same | Same inputs as the belly. |
| Mustache flap | `StachePivot` pitch | 18 | 0.2 | ±10° | -8° on every mouth-open and on pickups, plus scale.x 1 + 0.1 × charge (flare). |
| Mustache lobes | `StachePivot_L` / `_R` roll (independent) | 18 / 21 | 0.2 | ±12° | Footfalls alternate ±1.5° × speed/3, head yaw rate × 0.02, a random twitch of ±6° every 3-6 s, and a big flutter on throw release. |
| Cowlick boing | `CowlickPivot` two-axis rotation | 20 | 0.15 | ±35° | Head accel × 1.2° per m/s², +25° on landing, and a boing when a bag hits the hopper or on a hat pop. |
| Rag swing | `RagPivot` pitch and roll | 7 | 0.25 | pitch swings only **away** from the butt, 0 to 60° (verify sign), roll ±30° | Root accel and turn rate; streams back about 20° at sprint and keeps flapping for about 1 s after he stops. |
| Admiral nod | `DuckPivot` pitch | 16 | 0.3 | ±15° | Footfall nod +6° × speed/5, landing +12°. |
| Pigtails (P2) | `PigtailPivot_L` / `_R` pitch and roll | 8 | 0.25 | ±30° | Same as the rag. |
| Brows | `Brow_*` local y and roll | 20 | 0.5 | y ±0.02 m | Expression states (section 9). |
| Googly pupils | `GooglyPupil` on the EyeRoots | built in | built in | built in | L: maxOffset **0.028**, gravity **0.8**, motionKick **10**, bounciness **0.55**, twitch **0.12**. R: the same but maxOffset **0.031**. Badge: 0.036 / 1.0 / 12 / 0.5 / 0.2. These are rig-metre units; **never use the depot mascot defaults** (0.22 / 6 / 40), which fling the pupils off the eyes. Tune motionKick between 6 and 20. |
| Blink | `EyeRoot_*` localScale.y 1 to 0.1 to 1 | tween | tween | tween | Close 0.05 s, hold 0.04 s, open 0.06 s, every 2.5-5.5 s. 20% chance of a double blink. **The right eye runs 0.03 s late.** Forced blink on a hard landing and on throw release. **While scale.y < 1, set each GooglyPupil.motionKick = base × scale.y**, because InverseTransformVector would otherwise amplify the kick 10×. |
| Strain tint | per-puppet Skin material `_BaseColor` | lerp | lerp | 0-0.7 | Toward #FF8F80 by max(0.7 × charge, 0.6 × clamp01((mass - 6)/9)). Head tremble ±1.5° at 2 Hz on HeadBobble when mass > 12 kg. |
| Free-fall flail | ArmSplay override | none | none | 45-135° | When FreeFall has been true for more than 0.35 s **and he is dropping faster than 2 m/s**: elbows straighten (75%), the upper arms windmill between 45° and 135° of elevation at 5 Hz (left and right half a cycle apart) with a ±30° forward swing a quarter-cycle behind, mouth "O", brows up, cap target y +0.08, pupils kicked upward. Never over the head. On landing: belly squash, cap pop, mouth snaps shut, 0.25 s "oof" squint (0.6). |

**Idle fidgets.** They start after 6 s of idle (grounded, Speed < 0.1, not carrying). Then play one every 5-9 s, picked at random, never the same one twice in a row. Each is 1-2 s long, blends in and out over 0.15 s, and aborts instantly on any input.
- **a. Sniff-sniff… treasure?** The nose scale pulses to 1.12 three times at 7 Hz, the head dips 6°, then the brows rocket up (+0.02) and both pupils twitch.
- **b. Tummy pat.** Right-hand IK to the belly front at root-local rig (0.14, 1.02, 0.33), then 3 pats at 3 Hz, 3 cm in and out. Each pat is a -0.05 belly impulse. Happy squint.
- **c. Hat pop.** CapPivot kicks up 0.04 and settles; the cowlick boings.
- **d. Googly cross.** `GooglyPupil.Kick` the left pupil +x and the right pupil -x (0.3 each), so he goes cross-eyed for about 0.5 s.
- **e. Stretch-yawn.** ArmSplay ramps up to 70° over 0.8 s, the mouth goes wide (0.11, 0.09, 0.05), the eyes squint to 0.5 and the brows go up, then it all relaxes.
- **f. Look-see.** HeadBobble yaws +30° then -30° over 1.6 s, and the pupils slosh wildly.

## 12. Carry pose: the bear hug

![carry](CharacterRefs/chuck_carry.png)

His stubby arms can't reach around the bag, so he hugs it against his belly with his elbows flared. His eyes peek over the top.

- **Trigger.** The local owner has `IsCarrying` and `Held.State == Carried`, **and** the HoldPoint is within 0.9 m of his chest. That last condition turns the pose off during the sort-station first-person hold override. Blend the weight w from 0 to 1 over 0.15 s (ease-out) on `PickedUp`, and from 1 to 0 over 0.12 s on `Released`.
- **Hold point.** Set `holdLocalPosition` to **(0, 0.90, 0.48)**, in world units. For the default 6 kg bag (about 0.55 × 0.66 × 0.53 m) that puts the bag centre 0.90 m up and 0.48 m forward. Its back rests on the belly with about 2 cm of soft overlap, which reads as squish, and its top is at about 1.23 m, just under his eyes at 1.29 m. TrashCarrier already pushes bigger bags further out.
- **Hand targets** (world, each LateUpdate):
  - P = `carrier.HoldPoint.position`. It is spring-lagged, so the arms wobble with the bag for free.
  - e = `carrier.HeldExtents`; halfW = clamp(e.x, 0.15, 0.32) and halfD = clamp(e.z, 0.14, 0.30).
  - Mitt targets: `M_L/R = P ∓ r·0.85·halfW − f·0.70·halfD + up·0.05`, where r and f are root.right and root.forward with f flattened. The mitts grab the bag's upper back flanks.
  - Wrist target: `W = M − (Glove_Mitt.position − Hand.position)`. Solve twice so the mitt offset follows the new hand orientation.
- **Solver.** Analytic two-bone IK on UpperArm, LowerArm and Hand, with bone lengths measured in world space each frame, so it is scale-safe.
  - Clamp the reach to 97% of L1 + L2. The default bag needs about 91%.
  - Elbow pole: `shoulder + r·(±0.30) − up·0.10 − f·0.05`, so the elbows go out and down like hugging a beach ball.
  - Roll the Hand about the forearm axis so the thumb points up and the palm faces the bag centre.
  - Slerp the result against the animated pose by w. ArmSplay is multiplied by (1 − w).
- **Body.**
  - Lean back by `4° + 6°·clamp01(mass/20)`, applied **to Chest (60%) and UpperChest (40%) only**. Never lean the Spine, or the belly, which hangs below the Spine bone, swings forward into the bag.
  - Head pitch +4°, peeking over the bag.
  - For bags over 10 kg, add a waddle: Hips roll ±3° at step frequency with a Chest counter-roll, and the rag and belly jiggle harder.
- **Pickup.** A "hup" small-o mouth, brows up, a -0.06 belly squash, a cowlick boing and a mustache wiggle.
- **Throw charge** (hold LMB).
  - With chargeLift 0.08 and chargePullIn 0, the bag rises 8 cm, squashes and tilts back. The IK follows it and the elbows bend more.
  - On top of that: Chest leans back -10° × charge, the strain face ramps up, the mustache flares, the cap tilts back 5°, and the pupils jitter at full charge.
- **Release.**
  - The IK weight drops to 0 over 0.12 s while ArmSplay springs 18° → 55° → 18°, so the arms fling up and out.
  - The mouth pops open for 0.3 s, pairing with the existing YEET / HNNNG / FWOOMP floaters.
  - The cap pops +0.03, the mustache flutters, he blinks, and the Chest snaps forward +8° and springs back.
- **Toss (E).** The same, but gentler: the splay overshoots to 40°. When a bag lands in the truck: happy squint and om-nom.
- **Drop (G).** The arms flop (weight to 0 over 0.2 s), with a small "oof" squint.
- **Remote players.** Carry state is local-only today, so remote players show the normal arm swing. Phase 2 (co-op) adds an owner-write `NetworkVariable<bool> IsCarrying` (plus a byte for the mass class) on a small puppet NetworkBehaviour. Remote clients then aim the IK at `root + up·0.90 + f·0.48`.

## 13. Derpy touches (the friendslop checklist)

- Googly eyes that sag, rattle on every step, slosh on turns and twitch on their own. The right one is bigger and blinks a beat late.
- A third googly eye on his cap, the Company Eye pin, which rattles even harder than his real eyes.
- A walrus mustache wider than his head whose two sides wiggle out of sync, as if it's trying to leave.
- His hat floats off at the top of every jump, slams back down with a spin, and sometimes pops off during idle.
- One bed-head flick behind his right ear that boings on every landing.
- A big belly and seat that jiggle on every footfall and squash on landings, with the glowing belt riding along.
- A bobble head that nods with every step.
- Stubby arms that barely reach around a bag, so he bear-hugs it with his elbows flared and peeks over the top.
- He goes tomato-red and trembles under heavy bags, and his cheeks puff and his eyes squeeze while he winds up a throw, followed by a "HWAAH!" mouth pop.
- He windmills his arms with an "O" mouth whenever he falls for more than 0.35 s.
- Admiral the rubber duck, staring at you from his back pocket, nodding along.
- A red shop rag flapping from his other back pocket like a tail.
- Grandma's bottle caps as overall buttons, and yellow rubber gloves on a garbage man.
- Idle sniff-sniff… *treasure?*, tummy pats, a hat pop, going cross-eyed, a big stretch-yawn and a nervous look-see.
- A fast little scurry cadence on stubby legs, with a slightly duck-footed stance.

## 14. Co-op variants: the Dumpling family

![variants](CharacterRefs/chuck_variants.png)
*Front and back of P1 Chuck, P2 Dot, P3 Ozzie and P4 Bert.*

**How variants work:**
- **Shared uniform:** everyone wears the same Rubbish Co. uniform: olive overalls, dark straps, hi-vis rings, yellow rubber gloves, brown boots, the rag and the duck. That gives a team read.
- **Per player:** a **player color** (cap, shirt, sleeves), a **hair color**, and a **headwear silhouette** that reads from behind, which is the view teammates actually see.
- **Selection (since M0, 1 Oct 2026):** `variant` = the player's **roster slot** in their crew. The host is slot 0, so the host is always Chuck; guests get the lowest free slot in join order, and the slot is remembered by PlayerKey, so a player who rejoins gets their Dumpling back. `RosterView` (`Net/Player/RosterView.cs`) writes the slot from `NetRoster` into `ChuckPuppet.variantOverride` (-1 means none; it also works for testing in single player). `OwnerClientId % 4` stays only as the fallback for offline previews and for the frame or two before a joining player's roster entry arrives. A per-company tint comes later, with more than one company per Town (`Multiplayer.md` M7).
- **Toggling:** apply with `GameObject.SetActive` only, never `renderer.enabled`.
- **Transform overrides:** below, offsets are Head-bone-relative, like the tables. Subtract (0, 0.145, 0.01) to get the CapPivot-local value.

| Variant | Colors | Headwear / face changes | Back silhouette |
|---|---|---|---|
| **P1 Chuck** (host) | Tangerine #FF7A1F, hair #7A4E2E | Default: cap forward, walrus mustache, cowlick. | Orange dome, yellow button, hair band plus flick. |
| **P2 Dot**, Chuck's sister | Bubblegum #FF6FAE, auburn hair #9C4A2A | **Backwards cap:** CapPivot local yaw 180°; the brim sits over the nape and the googly pin stares at the player from behind. HeadlampMount CapPivot-local z becomes -0.235 so the lamp stays in front. StachePivot and CowlickPivot are inactive (no mustache). PigtailPivot_L/R are active. Mouth becomes a visible smile: offset (0, -0.19, 0.228), scale (0.075, 0.028, 0.03), open at (0.10, 0.06, 0.045). | Brim tongue plus googly pin at the back, two bouncy side pigtails. |
| **P3 Ozzie**, cousin | Seafoam #1FC2B0, ginger hair #C8642B | **Knit beanie:** Cap_Brim inactive and Cap_BeanieCuff active. Cap_Crown offset (0, 0.175, 0.01), scale (0.58, 0.50, 0.57). Cap_Button becomes the pom-pom at offset (0, 0.46, 0.01), scale 0.13. Cap_Badge and BadgeEyeRoot move to z 0.296. HeadlampMount CapPivot-local z is 0.276. **Handlebar mustache:** Stache_L Euler (0, -28, -18) and Stache_R (0, 28, 18), so the ends turn up. | Tall dome, yellow cuff, pom-pom. |
| **P4 Bert**, uncle and night-shift legend | Cornflower #4F8DFF, silver hair #B8B2A8 | **Hard hat:** Cap_Brim and Cap_Button inactive; Cap_RoundBrim and Cap_Ridge active. Cap_Crown scale (0.56, 0.38, 0.55). Cap_Badge and BadgeEyeRoot move to z 0.283. HeadlampMount CapPivot-local z is 0.219. **Unibrow** active, with Brow_L/R inactive. **Push-broom mustache:** Stache_Mid scale (0.20, 0.07, 0.10); Stache_L/R scale (0.145, 0.075, 0.085). | Wide flat disc with a ridge, silver hair band. |

Never ship these combinations: red cap with blue overalls (Mario), green cap with blue or navy overalls (Luigi), yellow cap with purple (Wario), or purple (Waluigi). The overalls stay olive for everyone.

## 15. Risks and how to avoid them

1. **The mannequin comes back.** `TruckControllerV2` (enter and exit) and `PlayerController.OnDriveStateChanged` / `OnPlayChessStateChanged` set `renderer.enabled` on every child renderer.
   - Hide the mannequin with `Armature_Mesh.SetActive(false)`, because inactive objects are skipped.
   - Puppet parts are correctly hidden while he is seated and shown again on exit.
   - Variant and optional parts must use SetActive only.
   - SortStation hides and restores only renderers that were already enabled, so it is fine.
2. **Bone writes while the Animator is off.** In the truck the Animator is disabled. Any additive rotation (ArmSplay, lean, IK) would then accumulate every frame. Guard with `animator.isActiveAndEnabled` and restore on re-enable.
3. **Arms clipping the belly.** ArmSplay is mandatory. It must run after the Animator, hand off to the carry IK, and raise the angle instantly with no clipping frames. If a particular clip still clips, widen the margin m to 0.09 before touching the belly size.
4. **Bind-pose sampling.** All offsets and Eulers are relative to the saved A-pose. Building from an animated or idle frame gets every rotation wrong. Build in the prefab-contents scene with the Animator not evaluated.
5. **Root-scale side effects.** Forgetting any row of section 5 breaks the feel:
   - the old capsule is thinner than his belly;
   - the grounded sphere would be wider than the capsule;
   - the camera would drop to 1.10 m;
   - his feet would slide;
   - the bag would sit in his face.
   Check each item on the acceptance list.
6. **Throw wind-up clipping.** Without `chargePullIn = 0` / `chargeLift = 0.08`, the stock charge pulls the bag 22 cm into his belly and nose. Keep the new fields' defaults at 0.22 and 0.14 so nothing else changes.
7. **GooglyPupil units and the blink kick.** Offsets are in EyeRoot-local rig metres, so use the tuned values, not the mascot defaults. Never give an EyeRoot a permanent non-1 scale. Scale motionKick by scale.y during blinks and squints.
8. **Headlamp inside the cap.** ShopHeadlamp's +0.13 m offset from the Head bone lands inside the crown. Use `HeadlampMount`.
9. **Head bone pivot.** The Head bone (1.62) sits in the upper half of the big head, so animated head nods swing the chin. HeadBobble's spring (pivot at 1.40) is additive and small. If the Starter Assets head motion looks too big, damp it by 50% in LateUpdate.
10. **Bag sizes.** The hold point and IK were tuned for the default bag. Bags scale ×0.9-1.18 with mass, and very large bags hit the 97% reach clamp, so the hands visibly strain toward the bag, which is intended. Verify with a 3 kg bag and a 15 kg bag.
11. **Near-flush surfaces.** These sit close to other surfaces and can z-fight: the waistband (10-15 mm proud), the shin bands (7.5 mm), the strap-ring inner edge (about 2 mm), and the badge and buttons.
   - If any of them flicker at the far camera distance, push them out along the surface normal (strap semi-axes +0.005). Do not shrink them.
   - Belly jiggle is capped at 12% squash, because the bib and straps live on other bones.
12. **Strap ring trick.** The straps are sized to Shirt_Chest's cross-section at x = ±0.13. If Shirt_Chest changes, recompute them (the formula is in the table notes).
13. **Camera up close.** In the tight shop, Cinemachine collision can pull the camera into his head. Use the camera distance to the Head bone, and hide it with SetActive (never renderer.enabled):
    - under 0.9 m world, deactivate `CapPivot`;
    - under 0.6 m world, deactivate all of `HeadBobble`.
14. **Leg and foot overlap.** Cross-legged frames in the turn and walk clips can briefly intersect the boots; that is acceptable. Do not widen the boots beyond the spec. Jump and fall knee tucks push the thighs into the belly, which is the same color and reads as squish.
15. **Carry on remote players.** It is not replicated yet (section 12, phase 2). Everything else is driven by replicated Animator parameters and motion, so it looks the same on every client; only blink and fidget timing differ.
16. **Performance.** 60 renderers per player, up to 240 for 4 players, with about 19 shared materials and 3 per-player instances. That is fine under the SRP Batcher with no MaterialPropertyBlocks. Keep shadows OFF on the 16 marked parts; there are no colliders anywhere. If it is ever needed, bake each bone's static parts into one mesh per bone, for about 20 renderers.
17. **IP guardrails.** The big nose, mustache, cap, overalls and round body are generic cartoon-worker tropes. His distinctness comes from:
    - the googly eyes and the googly Company Eye pin;
    - the orange, olive and hi-vis palette;
    - the yellow rubber gloves;
    - Admiral and the rag.
    Never add a letter emblem, white gloves, or the red and blue pairing.
18. **"Falling" while standing on props.** The Starter Assets ground check only sees the Ground layer, so on sandboxes, tyres, bags or the truck roof the Animator reports FreeFall forever (and plays the in-air pose). The puppet also counts him as grounded when the owner's CharacterController is grounded or the root has moved less than 0.35 m/s vertically for 0.25 s, so he doesn't flail, float his cap or skip fidgets there.

## 16. Acceptance checklist (capture a screenshot of each)

- [ ] Chase camera on a street, idle and sprinting. Visible: orange dome with button, hair band, straps on the orange back, yellow belt, red rag, Admiral, shin bands, boots. The arms never enter the belly.
- [ ] Front three-quarter view in daylight: googly eyes rattle and the right eye is visibly bigger; the badge pupil rattles; blush is visible.
- [ ] Carrying a 3 kg, a 6 kg and a 15 kg bag: bear hug, bag resting on the belly, eyes peeking over it, no hand popping. Throw charge with no bag in his belly or nose, then release fling.
- [ ] Enter and exit the truck twice: the mannequin never comes back, the scale stays 0.8, and the arms are not spun.
- [ ] Jump: the cap floats and pops. Fall off a curb: the flail shows. Landing: squash, cap spin, cowlick boing.
- [ ] Sort station (first person): body hidden locally, no IK. Shop at night: the bands glow; the headlamp sits on the crown.
- [ ] 30 s of idle: at least 3 different fidgets, blinks (double blinks included), and the out-of-sync mustache twitch.
- [ ] Use the `variantOverride` 0-3 lineup to check that the four back silhouettes are distinct.
- [ ] He climbs the sidewalk curb, walks through the garage door, and the grounded state is correct on curb edges.

## 17. Reference files

- `Docs/CharacterRefs/chuck_turnaround.png`: front, side, back and three-quarter views of the exact table (P1).
- `Docs/CharacterRefs/chuck_face.png`: face close-up and three-quarter back view.
- `Docs/CharacterRefs/chuck_carry.png`: carry pose solved with the section 12 numbers (about 91% reach), including a 5° lean.
- `Docs/CharacterRefs/chuck_gamecam.png`: the real game camera at root scale 0.8, next to a wheelie bin.
- `Docs/CharacterRefs/chuck_scale.png`: scale against a wheelie bin and the old 1.8 m mannequin line.
- `Docs/CharacterRefs/chuck_variants.png`: P1-P4, front and back.
- `Docs/CharacterRefs/chuck_parts.json`: the same parts, palette and variant data as the tables, readable by the editor builder (JsonUtility-friendly: `parts[]` with `offset` / `euler` / `scale` float arrays).
