# 2D Dynamic Shadows

Fake-but-convincing shadows for top-down / 2.5D sprites in URP 2D. Shadows follow a moving sun
through a day cycle and fade with sun height. Fires and torches cast their own extra shadows that
get stronger at night.

Every shadow is the caster's own sprite silhouette, tinted one colour, so it always matches the
art with no extra shadow sprites to draw.

**Requires:** Unity 6 (6000.4+), URP 17.4+ with the 2D Renderer.

---

## Contents

| File | Purpose |
|---|---|
| `ShadowMaster.cs` | Scene singleton. Owns the sun direction, elevation, shadow colour/intensity and the shared material. |
| `ShadowMasterEditor.cs` | Inspector buttons for ShadowMaster: shadow count, Refresh, Force Update. |
| `Sun2DManager.cs` | Optional day cycle. Moves a sun Light2D along an arc and drives an ambient light. |
| `DynamicShadow2D.cs` | Sprite shadow: rotates and stretches a copy of the sprite. For upright objects. |
| `MeshShadow2D.cs` | Mesh shadow: a quad pinned to the sprite's bottom edge. For wide, grounded objects. |
| `ShadowCastingLight.cs` | Add to a Light2D (fire, torch) so nearby casters get a shadow pointing away from it. |
| `ShadowSilhouette2D.shader` | The shadow shader: tint colour, masked by the sprite's alpha. |

Also used:

- `Assets/Art/Sprites/ShadowSilhouette.mat`: shadow material (`Custom/ShadowSilhouette2D`).
- `Assets/ShadowPrefab.prefab`: the prefab DynamicShadow2D spawns per shadow. Only needs a SpriteRenderer.

---

## Quick setup

1. **ShadowMaster**: add to an empty GameObject and assign:
   - **Shadow Material**: `ShadowSilhouette.mat`. Leave its colour white; the scripts drive the tint.
   - **Shadow Prefab**: `ShadowPrefab.prefab`.
   - **Sun Transform**: your sun light. Sun shadows stay hidden until this is set.
2. **Sun2DManager** (optional): assign a Light2D as **Sun Light**, and optionally a global Light2D
   as **Ambient Light**. Use that same sun light as ShadowMaster's Sun Transform.
3. **Casters**: add `DynamicShadow2D` *or* `MeshShadow2D` to any object with a SpriteRenderer.
   Don't add both to the same object.
4. **Fire / torch** (optional): add `ShadowCastingLight` to a point Light2D. See
   [Fire and torch lights](#fire-and-torch-lights).

With **Update In Editor** enabled on ShadowMaster, scrub `Sun2DManager > Time Of Day` to preview
the cycle without entering Play mode.

---

## How it works

### The sun

ShadowMaster computes three values once per frame, and every caster reads them:

- **Direction**: `-sunPosition.normalized`. The sun is directional and measured from the
  **world origin**, so every object shares one direction. Keep your scene roughly centred on the
  origin; Sun2DManager arcs the sun around it.
- **Elevation (0–1)**: 0 = horizon, 1 = overhead, 0 at night. It comes from
  `Sun2DManager.GetSunElevation()`, or from `sunTransform.position.z / 10` without Sun2DManager.
  With Sun2DManager, peak (noon) elevation is `Sun Height Target / 10`.
- **Alpha**: 0 below **Min Sun Height For Shadows**, otherwise `Lerp(0.2, Shadow Intensity, elevation)`,
  eased at **Shadow Fade Speed**.

Low sun → long, faint shadows. High sun → short, strong shadows.

### DynamicShadow2D

Spawns a hidden child SpriteRenderer (`<name>_Shadow`) showing the same sprite. Each frame it:

- rotates it to face the shadow direction,
- stretches it by `Shadow Distance Multiplier × Object Height`, shortening toward 0.3 as the sun rises,
- narrows it by up to **Shadow X Scale Shrink Amount** when the sun is low,
- sets its tint and alpha through `SpriteRenderer.color`.

It rotates around the sprite's pivot, so use a **Bottom Center** pivot to make shadows come out of
the object's feet. It works at every sun angle.

All sun shadows share one sorting order (`-32000`) so they draw as a single batched block behind
everything on their layer. This keeps large grass fields cheap, but a sun shadow can't sit in
front of another sprite on the same layer.

### MeshShadow2D

Spawns a hidden child MeshRenderer (`<name>_MeshShadow`) and rebuilds a 4-vertex quad each frame:

```
bl, br  = bottom corners of the sprite's tight outline (+ Shadow Offset X/Y)
length  = Shadow Distance Multiplier × Object Height × sprite height
          × Lerp(1, Min Shadow Length, elevation)
quad    = bl, br, br + dir × length, bl + dir × length
```

The sprite texture is mapped with its bottom row on the base and its top row at the far end. The
shadow stays attached along the whole base and **skews** with the sun instead of rotating.

- Placement uses `sprite.vertices`, so transparent padding is ignored. UVs still use the full
  texture rect, though, so **crop padded PNGs or set Mesh Type to Tight**. A console warning
  flags sprites that look padded.
- Sorts at the sprite's `sortingOrder - 1`.
- One material is cloned per distinct texture and shared, so mesh shadows keep SRP Batcher batching.

### Shader

`Custom/ShadowSilhouette2D` discards the sprite's colour: output = tint RGB, alpha `tint.a × sprite.a`.
DynamicShadow2D supplies the tint through `SpriteRenderer.color`; MeshShadow2D bakes it into vertex colours.

---

## Fire and torch lights

`ShadowCastingLight` gives every caster within the light's **outer radius** an extra shadow.

- **Direction**: from the light through the caster's pivot.
- **Length**: same formula as the sun, with **Light Height** in place of elevation (lower = longer).
  Distance to the light doesn't change the length.
- **Strength**: `Shadow Intensity × Light2D.intensity × (1 − distance / radius)`, blended from
  **Daytime Intensity** by day to full strength at night. The caster's **Night / Day Threshold**
  set where that blend happens.

**Requirements**

- Casters need a **Collider2D** on a layer in **Shadow Caster Layer Mask**.
- The light adds its own trigger `CircleCollider2D`, sized to its radius.
- Casters already in range are found at Start. Casters that **move** in or out later are
  tracked by trigger events, which need a **Rigidbody2D** on one side. A Kinematic Rigidbody2D on
  the light is simplest.
- Fire shadows only run in Play mode.

---

## Settings

### ShadowMaster

| Setting | Effect |
|---|---|
| Sun Position | Sun position used when no Sun Transform is set (direction only). |
| Sun Transform | Sun object. Needed for elevation. |
| Min Sun Height For Shadows | Elevation below which sun shadows fade out. |
| Shadow Distance Multiplier | Global length multiplier. |
| Shadow Color | Tint RGB. Alpha is automatic. |
| Shadow Intensity | Maximum sun-shadow opacity. |
| Shadow X Scale Shrink Amount | DynamicShadow2D only: how much a low sun narrows shadows. |
| Auto Update | Update every frame in Play mode. |
| Update In Editor | Update in the Scene view outside Play mode. |
| Shadow Prefab | Prefab DynamicShadow2D spawns. |
| Shadow Fade Speed | How fast shadow alpha eases in and out. |
| Shadow Material | Material shared by every shadow. |

### Sun2DManager

| Setting | Effect |
|---|---|
| Sun Light / Ambient Light | Lights to drive. |
| Day Length In Seconds | Length of a full cycle. |
| Auto Advance | Advance time in Play mode. |
| Clockwise | Arc direction. |
| Sunrise / Sunset Time | Fraction of the day (0–1) when the sun rises and sets. |
| Time Of Day | Current time (0–1). Scrub to preview. |
| Sun Arc Radius | Distance of the sun from the origin. |
| Sun Height Target | Peak sun height. Noon elevation = value / 10. |
| Sun / Ambient colours and intensities | Light colour and brightness over the day. |

### DynamicShadow2D

| Setting | Effect |
|---|---|
| Object Height | Per-object length multiplier. |
| Shadow Offset X / Y | Nudge the shadow base. |
| Shadow Horizontal Movement | Stylistic sideways slide as the sun crosses the sky (0 = off). |
| Night / Day Threshold | Elevations where fire shadows blend between day and night strength. |

### MeshShadow2D

| Setting | Effect |
|---|---|
| Object Height | Length as a multiple of the sprite's visual height. |
| Min Shadow Length | Length at noon as a fraction of the horizon length. |
| Shadow Offset X / Y | Nudge the shadow base in world units. |
| Night / Day Threshold | As above. |

### ShadowCastingLight

| Setting | Effect |
|---|---|
| Light Height | 0–1, lower = longer shadows. |
| Shadow Intensity | Opacity right next to the light. |
| Daytime Intensity | Daytime strength relative to night (0 = hidden by day). |
| Shadow Caster Layer Mask | Layers checked for casters. |

---

## Which caster?

| | DynamicShadow2D | MeshShadow2D |
|---|---|---|
| Attached at | The pivot | The whole bottom edge |
| At an angle | Rotates | Skews |
| Works at every sun angle | Yes | No (see below) |
| Previews in the editor | Yes | Yes |
| Cost | Very cheap, batches well, fine for thousands | Small mesh update per object each frame |
| Good for | Trees, characters, props, grass | Buildings, walls, fences, benches |

---

## Known limitations

- **MeshShadow2D thins to a line when the sun is directly to the side.** The base edge is always
  horizontal, so a horizontal shadow has no area. It's the same for a torch level with the object.
  Use DynamicShadow2D where every angle matters.
- **The sun is directional from the world origin.** There are no per-object sun directions.
- **Shared sun-shadow sorting** (DynamicShadow2D): sun shadows always sit behind everything on their layer.
- **Shadows fall on the ground plane only.** They don't wrap over other objects.

---

## Troubleshooting

| Problem | Check |
|---|---|
| No sun shadows | Sun Transform assigned? Daytime? Noon elevation (`Sun Height Target / 10`) above Min Sun Height For Shadows? |
| Shadows are coloured, not dark | Material must use `Custom/ShadowSilhouette2D` with colour left white. |
| Mesh shadow squashed or offset | Sprite has transparent padding: crop it or set Mesh Type to Tight. |
| No fire shadows | Collider2D on the caster, layer in the mask, Rigidbody2D for moving objects, Play mode. |
| Duplicate shadows after recompiling | **Refresh All Shadows** on ShadowMaster, or toggle the object off and on. |
| Shadow in the wrong place | Sprite pivot (DynamicShadow2D) or Shadow Offset X/Y. |
