using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

/// <summary>
/// Builds a parallelogram shadow mesh each frame.
/// Uses sprite.vertices (the tight polygon) to find the real visual bounds,
/// so transparent padding around the sprite does not affect shadow placement.
/// Supports sun shadows via ShadowMaster and fire/torch shadows via ShadowCastingLight.
/// </summary>
[ExecuteInEditMode]
[RequireComponent(typeof(SpriteRenderer))]
public class MeshShadow2D : MonoBehaviour
{
    [Tooltip("Shadow length multiplier relative to the sprite's visual height. 1 = one sprite-height long.")]
    public float objectHeight = 1f;

    [Tooltip("Shadow length at peak sun/light height, as a fraction of the full (horizon) length. Stops the shadow squashing to nothing while still scaling with shadowDistanceMultiplier/objectHeight.")]
    public float minShadowLength = 0.3f;

    [Tooltip("Nudge the shadow base X in world units")]
    public float shadowOffsetX = 0f;

    [Tooltip("Nudge the shadow base Y in world units")]
    public float shadowOffsetY = 0f;

    public enum AnchorEdge { Bottom, Left, Right }

    [Tooltip("Sprite edge the shadow stays attached to. Bottom suits upright objects; Left/Right suit long objects laid out vertically (e.g. a bench running up the screen). The shadow still thins to a line when the light runs parallel to this edge.")]
    public AnchorEdge anchorEdge = AnchorEdge.Bottom;

    [Header("Day/Night Blending")]
    [Tooltip("Sun elevation below which fire shadows are at full intensity")]
    [Range(0f, 1f)]
    public float nightThreshold = 0.4f;
    [Tooltip("Sun elevation above which fire shadows are at minimum intensity")]
    [Range(0f, 1f)]
    public float dayThreshold = 0.6f;

    private SpriteRenderer spriteRenderer;

    // Sun shadow
    private GameObject shadowObject;
    private MeshFilter shadowMeshFilter;
    private MeshRenderer shadowMeshRenderer;
    private Mesh shadowMesh;
    private Material shadowMaterial;
    private float currentAlpha = 0f;

    // Fire/torch light shadows
    private List<ShadowCastingLight> autoLights      = new List<ShadowCastingLight>();
    private List<GameObject>         autoShadowObjects = new List<GameObject>();
    private List<Mesh>               autoMeshes       = new List<Mesh>();
    private List<Material>           autoMaterials    = new List<Material>();

    private Sprite lastSprite;

    // Cached tight visual bounds in sprite local space (from sprite.vertices)
    private float localMinX, localMaxX, localMinY, localMaxY;
    private Vector3[] cachedVerts = new Vector3[4];
    private Color[] cachedColors = new Color[4];

    // Uses the same single ShadowMaster.shadowMaterial as DynamicShadow2D (see that file). This
    // system bakes its own full sun/fire color per-vertex (see LateUpdate below) via raw
    // mesh.colors, read by the shader as vertex color — independent of DynamicShadow2D's
    // SpriteRenderer.color/unity_SpriteColor path (this uses MeshRenderer, not SpriteRenderer,
    // so unity_SpriteColor is never populated for it). As long as the shared material's own
    // Color stays at its default (1,1,1,1) — i.e. nothing ever calls material.SetColor on it —
    // this needs no extra guarding, even though the material is shared with DynamicShadow2D.
    //
    // Unlike SpriteRenderer, MeshRenderer gets NO automatic per-instance texture override — the
    // shared material's own texture would otherwise apply identically to every mesh shadow
    // regardless of that object's actual sprite. Texture genuinely varies here, but only across
    // a SMALL number of distinct tree/building textures — not per instance the way color would.
    // A MaterialPropertyBlock (the first version of this fix) delivers per-instance data
    // correctly, but unconditionally excludes the renderer from SRP Batcher, which turned out to
    // be the dominant cost once grass shadows' own batching got fixed. A small material cache
    // keyed by texture — one real shared Material per distinct sprite, same shader/instancing as
    // ShadowMaster.shadowMaterial — lets every mesh shadow using the same texture batch normally.
    private static readonly Dictionary<Texture, Material> textureMaterialCache = new Dictionary<Texture, Material>();

    static Material GetMaterialForTexture(Texture tex)
    {
        Material baseMaterial = ShadowMaster.Instance != null ? ShadowMaster.Instance.shadowMaterial : null;
        if (baseMaterial == null) return null;

        Texture key = tex != null ? tex : Texture2D.whiteTexture;
        if (textureMaterialCache.TryGetValue(key, out Material mat) && mat != null)
            return mat;

        // No enableInstancing here on purpose: GPU Instancing only pays off when multiple
        // renderers share the exact same mesh, and every shadow mesh here is unique (built
        // per-object from that object's own bounds/sprite each frame). It can never actually
        // be picked up by the instancing path, so leaving it on just risks Unity evaluating
        // (and failing) that path instead of taking the plain SRP Batcher fast path.
        mat = new Material(baseMaterial) { mainTexture = key };
        textureMaterialCache[key] = mat;
        return mat;
    }

    // -------------------------------------------------------------------------

#if UNITY_EDITOR
    void OnValidate()
    {
        lastSprite = null; // anchorEdge may have changed; forces UVs to rebuild next frame

        if (spriteRenderer == null)
            spriteRenderer = GetComponent<SpriteRenderer>();

        Sprite s = spriteRenderer != null ? spriteRenderer.sprite : null;
        if (s == null) return;

        Rect tr = s.textureRect;
        Vector2[] verts = s.vertices;
        if (verts.Length == 0) return;

        float ppu   = s.pixelsPerUnit;
        float rectW = tr.width  / ppu;
        float rectH = tr.height / ppu;

        float vMinX = verts[0].x, vMaxX = verts[0].x;
        float vMinY = verts[0].y, vMaxY = verts[0].y;
        for (int i = 1; i < verts.Length; i++)
        {
            if (verts[i].x < vMinX) vMinX = verts[i].x;
            if (verts[i].x > vMaxX) vMaxX = verts[i].x;
            if (verts[i].y < vMinY) vMinY = verts[i].y;
            if (verts[i].y > vMaxY) vMaxY = verts[i].y;
        }

        float thresholdX = rectW * 0.9f;
        float thresholdY = rectH * 0.9f;
        if ((vMaxX - vMinX) < thresholdX || (vMaxY - vMinY) < thresholdY)
        {
            Debug.LogWarning(
                $"[MeshShadow2D] '{gameObject.name}': sprite '{s.name}' appears to have " +
                $"transparent whitespace padding. Crop the PNG to remove whitespace, or set " +
                $"Mesh Type to Tight in the sprite import settings.", this);
        }
    }
#endif

    // -------------------------------------------------------------------------

    void Awake()
    {
        // Must happen in Awake (not OnEnable/Start): Unity guarantees all Awake calls
        // finish before any Start call, but Start order between objects is undefined.
        // ShadowCastingLight.Start() can call into RegisterAutoLight() -> CreateAutoShadowMesh()
        // before this object's own Start()/OnEnable() would otherwise have run.
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    void OnEnable()
    {
        if (spriteRenderer == null)
            spriteRenderer = GetComponent<SpriteRenderer>();
        SetupShadowObject();
    }

    void Start()
    {
        if (spriteRenderer == null)
            spriteRenderer = GetComponent<SpriteRenderer>();
        SetupShadowObject();
    }

    void SetupShadowObject()
    {
        if (shadowObject != null)
            return;

        if (ShadowMaster.Instance == null)
            return;

        // A script recompile drops the private shadowObject reference but leaves the DontSave
        // child in the hierarchy, still drawing its last geometry. Clear it before rebuilding.
        Transform stale = transform.Find(name + "_MeshShadow");
        if (stale != null) DestroyImmediate(stale.gameObject);

        shadowObject = new GameObject(name + "_MeshShadow");
        shadowObject.hideFlags = HideFlags.DontSave;

        shadowMeshFilter   = shadowObject.AddComponent<MeshFilter>();
        shadowMeshRenderer = shadowObject.AddComponent<MeshRenderer>();

        shadowObject.transform.SetParent(transform, false);

        shadowMesh = new Mesh();
        shadowMesh.MarkDynamic();
        shadowMesh.vertices  = new Vector3[4];
        shadowMesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
        shadowMesh.uv        = new Vector2[4];
        shadowMeshFilter.mesh = shadowMesh;

        shadowMaterial = GetMaterialForTexture(spriteRenderer.sprite != null ? spriteRenderer.sprite.texture : null);
        shadowMeshRenderer.sharedMaterial = shadowMaterial;
        if (shadowMaterial == null)
            Debug.LogWarning("MeshShadow2D: ShadowMaster.shadowMaterial is not assigned — shadow will render with no material.", this);
        shadowMeshRenderer.sortingLayerID = spriteRenderer.sortingLayerID;
        shadowMeshRenderer.sortingOrder   = spriteRenderer.sortingOrder - 1;
    }

    // -------------------------------------------------------------------------

    /// <summary>Called by ShadowCastingLight when this object enters its range.</summary>
    public void RegisterAutoLight(ShadowCastingLight light)
    {
        if (light == null || autoLights.Contains(light)) return;
        autoLights.Add(light);
        CreateAutoShadowMesh(autoLights.Count - 1);
    }

    /// <summary>Called by ShadowCastingLight when this object exits its range.</summary>
    public void UnregisterAutoLight(ShadowCastingLight light)
    {
        int index = autoLights.IndexOf(light);
        if (index < 0) return;

        if (index < autoShadowObjects.Count && autoShadowObjects[index] != null)
        {
            if (Application.isPlaying) Destroy(autoShadowObjects[index]);
            else                       DestroyImmediate(autoShadowObjects[index]);
        }

        autoLights.RemoveAt(index);
        if (index < autoShadowObjects.Count) autoShadowObjects.RemoveAt(index);
        if (index < autoMeshes.Count)        autoMeshes.RemoveAt(index);
        if (index < autoMaterials.Count)     autoMaterials.RemoveAt(index);

        // Everything after the removed slot shifted down one; re-sync names and sorting to match.
        for (int i = index; i < autoShadowObjects.Count; i++)
        {
            var go = autoShadowObjects[i];
            if (go == null) continue;
            go.name = name + "_MeshAutoShadow_" + i;
            go.GetComponent<MeshRenderer>().sortingOrder = spriteRenderer.sortingOrder - 2 - i;
        }
    }

    void CreateAutoShadowMesh(int index)
    {
        if (spriteRenderer == null)
            spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer == null)
            return;

        var go = new GameObject(name + "_MeshAutoShadow_" + index);
        go.hideFlags = HideFlags.DontSave;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        var m = new Mesh();
        m.MarkDynamic();
        m.vertices  = new Vector3[4];
        m.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
        m.uv = shadowMesh != null ? shadowMesh.uv : new Vector2[4];
        mf.mesh = m;

        var mat = GetMaterialForTexture(spriteRenderer.sprite != null ? spriteRenderer.sprite.texture : null);
        mr.sharedMaterial = mat;
        mr.sortingLayerID = spriteRenderer.sortingLayerID;
        mr.sortingOrder   = spriteRenderer.sortingOrder - 2 - index;

        autoShadowObjects.Add(go);
        autoMeshes.Add(m);
        autoMaterials.Add(mat);
    }

    // -------------------------------------------------------------------------

    void UpdateSpriteData(Sprite s)
    {
        Rect tr  = s.textureRect;
        float tw = s.texture.width;
        float th = s.texture.height;

        float u0 = tr.x / tw;
        float v0 = tr.y / th;
        float u1 = (tr.x + tr.width)  / tw;
        float v1 = (tr.y + tr.height) / th;

        // Verts are [edgeA, edgeB, edgeB + ext, edgeA + ext]: the anchored edge's texels sit at the
        // base and the opposite side of the sprite lands at the far end of the shadow.
        Vector2[] uvs;
        switch (anchorEdge)
        {
            case AnchorEdge.Left:
                uvs = new[] { new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0) };
                break;
            case AnchorEdge.Right:
                uvs = new[] { new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1), new Vector2(u0, v0) };
                break;
            default:
                uvs = new[] { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1) };
                break;
        }

        shadowMesh.uv = uvs;

        // Texture changed — repoint at the (cached, shared) material for the new texture rather
        // than mutating a shared material in place. Sync UVs + material to every auto light
        // shadow mesh too.
        shadowMaterial = GetMaterialForTexture(s.texture);
        shadowMeshRenderer.sharedMaterial = shadowMaterial;
        foreach (var m in autoMeshes) m.uv = uvs;
        for (int i = 0; i < autoShadowObjects.Count; i++)
        {
            if (autoShadowObjects[i] == null) continue;
            var mr = autoShadowObjects[i].GetComponent<MeshRenderer>();
            if (mr == null) continue;
            Material autoMat = GetMaterialForTexture(s.texture);
            if (i < autoMaterials.Count) autoMaterials[i] = autoMat;
            mr.sharedMaterial = autoMat;
        }

        // Tight visual bounds from sprite.vertices
        Vector2[] sv = s.vertices;
        localMinX = localMaxX = sv[0].x;
        localMinY = localMaxY = sv[0].y;
        for (int i = 1; i < sv.Length; i++)
        {
            if (sv[i].x < localMinX) localMinX = sv[i].x;
            if (sv[i].x > localMaxX) localMaxX = sv[i].x;
            if (sv[i].y < localMinY) localMinY = sv[i].y;
            if (sv[i].y > localMaxY) localMaxY = sv[i].y;
        }

        lastSprite = s;
    }

    // -------------------------------------------------------------------------

    void LateUpdate()
    {
        var master = ShadowMaster.Instance;
        if (master == null)
            return;

        // Follows ShadowMaster's own editor gate so scrubbing the sun previews mesh shadows too.
        if (!Application.isPlaying && !master.updateInEditor)
            return;

        if (shadowObject == null)
        {
            SetupShadowObject();
            if (shadowObject == null)
                return;
        }

        if (!shadowObject.activeSelf)
            shadowObject.SetActive(true);

        // Shadow objects live at world origin — vertex positions are world positions.
        shadowObject.transform.position   = Vector3.zero;
        shadowObject.transform.rotation   = Quaternion.identity;
        shadowObject.transform.localScale = Vector3.one;

        Sprite s = spriteRenderer.sprite;
        if (s == null)
            return;

        if (s != lastSprite)
            UpdateSpriteData(s);

        // Anchored edge (tight visual corners, world space) and the sprite's depth across it,
        // which is what shadow length scales from.
        Vector3 edgeA, edgeB;
        float depth;
        switch (anchorEdge)
        {
            case AnchorEdge.Left:
                edgeA = transform.TransformPoint(new Vector3(localMinX, localMinY, 0f));
                edgeB = transform.TransformPoint(new Vector3(localMinX, localMaxY, 0f));
                depth = Mathf.Abs(transform.lossyScale.x) * (localMaxX - localMinX);
                break;
            case AnchorEdge.Right:
                edgeA = transform.TransformPoint(new Vector3(localMaxX, localMinY, 0f));
                edgeB = transform.TransformPoint(new Vector3(localMaxX, localMaxY, 0f));
                depth = Mathf.Abs(transform.lossyScale.x) * (localMaxX - localMinX);
                break;
            default:
                edgeA = transform.TransformPoint(new Vector3(localMinX, localMinY, 0f));
                edgeB = transform.TransformPoint(new Vector3(localMaxX, localMinY, 0f));
                depth = Mathf.Abs(transform.lossyScale.y) * (localMaxY - localMinY);
                break;
        }

        Vector3 offset = new Vector3(shadowOffsetX, shadowOffsetY, 0f);
        edgeA += offset;
        edgeB += offset;

        // --- Sun shadow ---
        float sunElevation = master.GetSunElevation();
        Vector2 shadowDir  = master.GetShadowDirection();

        float fullLength   = master.shadowDistanceMultiplier * Mathf.Max(0f, objectHeight) * depth;
        float sunExtension = fullLength * Mathf.Lerp(1f, minShadowLength, sunElevation);

        Vector3 sunVec = new Vector3(shadowDir.x, shadowDir.y, 0f) * sunExtension;

        cachedVerts[0] = edgeA;
        cachedVerts[1] = edgeB;
        cachedVerts[2] = edgeB + sunVec;
        cachedVerts[3] = edgeA + sunVec;


        shadowMesh.vertices = cachedVerts;
        shadowMesh.RecalculateBounds();

        float targetAlpha = sunElevation < master.minSunHeightForShadows
            ? 0f
            : Mathf.Lerp(0.2f, master.shadowIntensity, sunElevation);

        float deltaTime = Application.isPlaying ? Time.deltaTime : 1f;
        currentAlpha = Mathf.MoveTowards(currentAlpha, targetAlpha, deltaTime * master.shadowFadeSpeed);

        Color sunColor = master.shadowColor;
        sunColor.a = currentAlpha;
        cachedColors[0] = sunColor;
        cachedColors[1] = sunColor;
        cachedColors[2] = sunColor;
        cachedColors[3] = sunColor;

        shadowMesh.colors = cachedColors;

        // --- Fire/torch light shadows ---
        if (Application.isPlaying)
            UpdateAutoLightShadows(sunElevation, edgeA, edgeB, depth, master);
    }

    void UpdateAutoLightShadows(float sunElevation, Vector3 edgeA, Vector3 edgeB,
                                float depth, ShadowMaster master)
    {
        // 0 = full day, 1 = full night
        float nightBlend;
        if      (sunElevation >= dayThreshold)   nightBlend = 0f;
        else if (sunElevation <= nightThreshold) nightBlend = 1f;
        else nightBlend = 1f - Mathf.InverseLerp(nightThreshold, dayThreshold, sunElevation);

        for (int i = 0; i < autoLights.Count; i++)
        {
            var light = autoLights[i];
            if (light == null || i >= autoShadowObjects.Count || autoShadowObjects[i] == null)
                continue;

            autoShadowObjects[i].transform.position   = Vector3.zero;
            autoShadowObjects[i].transform.rotation   = Quaternion.identity;
            autoShadowObjects[i].transform.localScale = Vector3.one;

            if (!autoShadowObjects[i].activeSelf)
                autoShadowObjects[i].SetActive(true);

            Vector2 toLight    = (Vector2)(light.transform.position - transform.position);
            float   distance   = toLight.magnitude;
            Vector2 shadowDir  = -toLight.normalized;

            float lightElevation = light.GetLightHeight();
            float extension = master.shadowDistanceMultiplier * Mathf.Max(0f, objectHeight) * depth
                            * Mathf.Lerp(1f, minShadowLength, lightElevation);

            Vector3 sv = new Vector3(shadowDir.x, shadowDir.y, 0f) * extension;

            // Mesh.vertices/colors copy the array, so the sun path's scratch arrays are safe to reuse.
            cachedVerts[0] = edgeA;
            cachedVerts[1] = edgeB;
            cachedVerts[2] = edgeB + sv;
            cachedVerts[3] = edgeA + sv;
            autoMeshes[i].vertices = cachedVerts;
            autoMeshes[i].RecalculateBounds();

            // Intensity: light strength × distance falloff × day/night blend
            float intensity = light.GetShadowIntensity();
            Light2D l2d = light.GetLight();
            if (l2d != null) intensity *= l2d.intensity;

            float radius = light.GetRadius();
            intensity *= 1f - Mathf.Clamp01(distance / radius);
            intensity *= Mathf.Lerp(light.GetDaytimeIntensity(), 1f, nightBlend);

            Color c = master.shadowColor;
            c.a = Mathf.Clamp01(intensity);
            cachedColors[0] = cachedColors[1] = cachedColors[2] = cachedColors[3] = c;
            autoMeshes[i].colors = cachedColors;
        }
    }

    // -------------------------------------------------------------------------

    void OnDisable()
    {
        if (shadowObject != null)
            shadowObject.SetActive(false);

        foreach (var go in autoShadowObjects)
            if (go != null) go.SetActive(false);
    }

    void OnDestroy()
    {
        DestroyObject(shadowObject);
        foreach (var go in autoShadowObjects) DestroyObject(go);
        autoShadowObjects.Clear();
        autoMeshes.Clear();
        autoMaterials.Clear();
        autoLights.Clear();
    }

    void DestroyObject(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else                       DestroyImmediate(go);
    }
}
