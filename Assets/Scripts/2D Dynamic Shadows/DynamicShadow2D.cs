using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Creates dynamic 2D shadows for sprites based on sun position and nearby fire/torch lights.
/// Sun shadows come from ShadowMaster, fire shadows come from ShadowCastingLight components.
/// </summary>
[ExecuteInEditMode]
[RequireComponent(typeof(SpriteRenderer))]
public class DynamicShadow2D : MonoBehaviour
{
    [Header("Shadow Settings")]
    [Tooltip("Shadow length multiplier relative to the sprite's height. Higher = taller object = longer shadow.")]
    public float objectHeight = 1f;
    [Tooltip("Nudge the shadow's base X position in world units, relative to this object")]
    public float shadowOffsetX = 0;
    [Tooltip("Nudge the shadow's base Y position in world units, relative to this object")]
    public float shadowOffsetY = 0;
    [Tooltip("How far the shadow slides sideways as the sun crosses from one side of the sky to the other (0 = no slide)")]
    public float shadowHorizontalMovement = 0;

    private SpriteRenderer spriteRenderer;
    private GameObject shadowObject;
    private SpriteRenderer shadowRenderer;

    // Set automatically by ShadowMaster.RegisterShadow — not meant to be assigned in the inspector.
    [HideInInspector] public ShadowMaster shadowMaster;

    [Header("Day/Night Blending")]
    [Tooltip("Sun elevation threshold below which fire shadows are at full intensity")]
    [Range(0f, 1f)]
    public float nightThreshold = 0.4f;
    [Tooltip("Sun elevation threshold above which fire shadows are at minimum intensity")]
    [Range(0f, 1f)]
    public float dayThreshold = 0.6f;

    // Runtime data for auto-registered lights (from ShadowCastingLight)
    private List<ShadowCastingLight> autoLights = new List<ShadowCastingLight>();
    private List<GameObject> autoShadowObjects = new List<GameObject>();
    private List<SpriteRenderer> autoShadowRenderers = new List<SpriteRenderer>();

    private float nightBlendFactor = 0f;

    // All shadow casters (sun + auto) share ONE material — ShadowMaster.shadowMaterial — a real
    // URP sprite shader gets Unity's automatic per-SpriteRenderer texture override, so one
    // material correctly renders every different sprite/texture; no per-texture cache needed.
    //
    // Shadow tint/alpha is set via SpriteRenderer.color, same as any normal sprite. The shader
    // reads this back through unity_SpriteColor — a built-in Unity feeds per-renderer that's
    // specifically designed to survive both SRP Batcher and GPU Instancing correctly, so this
    // needs no MaterialPropertyBlock or shader-global workaround.

    // All sun shadows share this ONE fixed sortingOrder rather than trailing their own object by
    // -1. Per-object placement interleaves shadow draws between individual grass blades in draw
    // order (shadow_A, grass_A, shadow_B, grass_B, ...) — even though each draw is individually
    // fine, that interleaving breaks up what would otherwise be one long contiguous run of
    // identical-material grass blades that GPU Instancing could merge into large batches,
    // collapsing it into isolated singletons instead (confirmed via Frame Debugger: every
    // Non-SRP-Compatible entry in a grass-only scene is the wind shader, not this shadow shader —
    // shadows were never the incompatible draws, they were just breaking up the grass's own runs).
    // Pinning every sun shadow to one shared low order removes the interleaving: all shadows
    // become one contiguous block, and grass becomes an uninterrupted, freely-instanceable block
    // of its own. Safe for flat ground shadows that only need to sit behind everything on their
    // layer — NOT yet verified for a mixed scene where a shadow might need to sit behind one
    // specific nearby object but in front of another (e.g. once trees/buildings share this).
    public const int SunShadowSortingOrder = -32000;

    void OnEnable()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        CleanupDuplicateShadows();
        if (ShadowMaster.Instance != null)
            ShadowMaster.Instance.RegisterShadow(this);
    }

    void Start()
    {
        shadowMaster = ShadowMaster.Instance;
        if (shadowObject == null)
        {
            CreateShadow();
        }
        else if (shadowRenderer != null && shadowMaster != null)
        {
            // CleanupDuplicateShadows() may have just adopted a shadow object that already
            // existed in the scene (survives domain reload since it's HideFlags.DontSave, not
            // destroyed) rather than creating a fresh one — CreateShadow() never ran for it, so
            // it could still be carrying whatever material it had before shadowMaterial last
            // changed. Re-sync unconditionally so an adopted shadow can never silently go stale.
            shadowRenderer.sharedMaterial = shadowMaster.shadowMaterial;
        }
    }

    void CleanupDuplicateShadows()
    {
        Transform[] children = GetComponentsInChildren<Transform>(true);
        int shadowCount = 0;

        foreach (Transform child in children)
        {
            if (child != transform && child.name.Contains("_Shadow"))
            {
                if (child.name.Contains("_AutoShadow_"))
                {
                    continue;
                }

                shadowCount++;

                if (shadowCount == 1 && shadowObject == null)
                {
                    shadowObject = child.gameObject;
                    shadowRenderer = child.GetComponent<SpriteRenderer>();
                }
                else
                {
                    if (Application.isPlaying)
                    {
                        Destroy(child.gameObject);
                    }
                    else
                    {
                        DestroyImmediate(child.gameObject);
                    }
                }
            }
        }
    }

    void CreateShadow()
    {
        if (shadowObject != null)
            return;

        if (shadowMaster == null)
        {
            shadowMaster = ShadowMaster.Instance;
            if (shadowMaster == null)
            {
                Debug.LogWarning("DynamicShadow2D: No ShadowMaster found in scene. Cannot create shadow.");
                return;
            }
        }

        shadowObject = Instantiate(shadowMaster.shadowPrefab);
        shadowObject.name = gameObject.name + "_Shadow";
        shadowObject.transform.SetParent(transform);
        shadowObject.transform.localPosition = new Vector3(shadowOffsetX, shadowOffsetY, 0f);
        shadowObject.transform.localRotation = Quaternion.identity;
        shadowObject.transform.localScale = Vector3.one;
        shadowObject.hideFlags = HideFlags.DontSave;

        shadowRenderer = shadowObject.GetComponent<SpriteRenderer>();
        if (shadowRenderer == null)
        {
            Debug.LogError($"DynamicShadow2D: shadowPrefab '{shadowMaster.shadowPrefab.name}' has no SpriteRenderer.", this);
            return;
        }
        shadowRenderer.sprite = spriteRenderer.sprite;
        shadowRenderer.sortingOrder = SunShadowSortingOrder;
        shadowRenderer.sortingLayerID = spriteRenderer.sortingLayerID;

        shadowRenderer.sharedMaterial = shadowMaster.shadowMaterial;
        if (shadowRenderer.sharedMaterial == null)
            Debug.LogWarning("DynamicShadow2D: ShadowMaster.shadowMaterial is not assigned — shadow will use the shadow prefab's default material.", this);
    }

    /// <summary>
    /// Called by ShadowCastingLight when this object enters its range
    /// </summary>
    public void RegisterAutoLight(ShadowCastingLight light)
    {
        if (light == null || autoLights.Contains(light)) return;

        autoLights.Add(light);
        CreateAutoShadowForLight(autoLights.Count - 1);
    }

    /// <summary>
    /// Called by ShadowCastingLight when this object exits its range
    /// </summary>
    public void UnregisterAutoLight(ShadowCastingLight light)
    {
        int index = autoLights.IndexOf(light);
        if (index < 0) return;

        if (index < autoShadowObjects.Count && autoShadowObjects[index] != null)
        {
            if (Application.isPlaying)
                Destroy(autoShadowObjects[index]);
            else
                DestroyImmediate(autoShadowObjects[index]);
        }

        autoLights.RemoveAt(index);
        if (index < autoShadowObjects.Count) autoShadowObjects.RemoveAt(index);
        if (index < autoShadowRenderers.Count) autoShadowRenderers.RemoveAt(index);
    }

    void CreateAutoShadowForLight(int index)
    {
        if (shadowMaster == null) return;

        GameObject autoShadow = Instantiate(shadowMaster.shadowPrefab);
        autoShadow.name = gameObject.name + "_AutoShadow_" + index;
        autoShadow.transform.SetParent(transform);
        autoShadow.transform.localPosition = new Vector3(shadowOffsetX, shadowOffsetY, 0f);
        autoShadow.transform.localRotation = Quaternion.identity;
        autoShadow.transform.localScale = Vector3.one;
        autoShadow.hideFlags = HideFlags.DontSave;

        SpriteRenderer autoRenderer = autoShadow.GetComponent<SpriteRenderer>();
        autoRenderer.sprite = spriteRenderer.sprite;
        autoRenderer.sortingOrder = spriteRenderer.sortingOrder - 1;
        autoRenderer.sortingLayerID = spriteRenderer.sortingLayerID;
        autoRenderer.sharedMaterial = shadowMaster.shadowMaterial;


        autoShadowObjects.Add(autoShadow);
        autoShadowRenderers.Add(autoRenderer);
    }

    public void ApplyShadowFromMaster(Color shadowColor, Quaternion shadowRotation, float shadowDistanceMultiplier, float sunElevation, float xScale)
    {
        // After a script-recompile domain reload, ShadowMaster can re-register and update this
        // shadow before this object's own OnEnable has re-run (cross-object order isn't
        // guaranteed), leaving spriteRenderer still null. Re-fetch defensively rather than throw.
        if (spriteRenderer == null)
            spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer == null)
            return;

        // Ensure shadow exists (may not have been created if ShadowMaster wasn't ready in OnEnable)
        if (shadowObject == null)
        {
            CreateShadow();
        }

        // Dirty-checked rather than assigned unconditionally: sortingOrder determines literal
        // draw sequence, not just per-object shader data, so rewriting it every frame (even to
        // an unchanged value, which is the common case for static objects like grass that never
        // move) risks forcing Unity's 2D renderer to re-evaluate draw order every frame — far
        // more disruptive to batching than an ordinary transform/color update.
        //
        // The sun shadow uses the fixed shared SunShadowSortingOrder (see its declaration above)
        // rather than this object's own -1 offset. Fire/torch shadows stay per-object: only a
        // handful are ever active at once, so they were never the batching bottleneck, and their
        // placement relative to their own object still matters for correctness.
        int fireShadowTargetOrder = spriteRenderer.sortingOrder - 1;
        if (autoShadowRenderers.Count > 0 && autoShadowRenderers[0].sortingOrder != fireShadowTargetOrder)
            autoShadowRenderers[0].sortingOrder = fireShadowTargetOrder;
        if (shadowRenderer.sortingOrder != SunShadowSortingOrder)
            shadowRenderer.sortingOrder = SunShadowSortingOrder;

        // Calculate night blend factor (0 = day, 1 = night)
        if (sunElevation >= dayThreshold)
        {
            nightBlendFactor = 0f;
        }
        else if (sunElevation <= nightThreshold)
        {
            nightBlendFactor = 1f;
        }
        else
        {
            nightBlendFactor = 1f - Mathf.InverseLerp(nightThreshold, dayThreshold, sunElevation);
        }

        // Update main sun shadow
        UpdateShadow(shadowObject, shadowRenderer, shadowColor, shadowRotation, shadowDistanceMultiplier, sunElevation, xScale);

        // Update auto-registered light shadows
        UpdateAutoLightShadows(shadowDistanceMultiplier);
    }

    void UpdateAutoLightShadows(float shadowDistanceMultiplier)
    {
        for (int i = 0; i < autoLights.Count; i++)
        {
            var light = autoLights[i];
            if (light == null) continue;
            if (i >= autoShadowObjects.Count || autoShadowObjects[i] == null) continue;

            Vector2 objectPosition = transform.position;
            Vector2 lightPosition = light.transform.position;
            float distance = Vector2.Distance(objectPosition, lightPosition);

            Vector2 directionToLight = (lightPosition - objectPosition).normalized;
            Vector2 shadowDirection = -directionToLight;

            float rotation = Mathf.Atan2(shadowDirection.y, shadowDirection.x) * Mathf.Rad2Deg - 90f;
            Quaternion shadowRotation = Quaternion.Euler(0f, 0f, rotation);

            float lightElevation = light.GetLightHeight();
            float scaleReduction = (1f - lightElevation) * 0.3f;
            float xScale = Mathf.Max(1f - scaleReduction, 0.1f);

            Color shadowColor = CalculateAutoLightShadowColor(light, distance);

            UpdateShadow(autoShadowObjects[i], autoShadowRenderers[i], shadowColor, shadowRotation, shadowDistanceMultiplier, lightElevation, xScale);
        }
    }

    Color CalculateAutoLightShadowColor(ShadowCastingLight light, float distance)
    {
        float intensity = light.GetShadowIntensity();

        Light2D light2D = light.GetLight();
        if (light2D != null)
        {
            intensity *= light2D.intensity;
        }

        // Fade based on distance - stronger when close, fades at edge
        float radius = light.GetRadius();
        float distanceFade = 1f - Mathf.Clamp01(distance / radius);
        intensity *= distanceFade;

        // Apply day/night blend - fire shadows always visible but stronger at night
        // nightBlendFactor: 0 = day, 1 = night
        // Lerp from light's daytime intensity to full intensity based on night blend
        float daytimeIntensity = light.GetDaytimeIntensity();
        float dayNightMultiplier = Mathf.Lerp(daytimeIntensity, 1f, nightBlendFactor);
        intensity *= dayNightMultiplier;

        Color shadowColor = shadowMaster != null ? shadowMaster.shadowColor : Color.black;
        shadowColor.a = Mathf.Clamp01(intensity);

        return shadowColor;
    }

    void UpdateShadow(GameObject shadowObj, SpriteRenderer shadowRend, Color shadowColor, Quaternion shadowRotation, float shadowDistanceMultiplier, float lightElevation, float xScale)
    {
        if (shadowRend == null || spriteRenderer == null || shadowObj == null)
            return;

        if (shadowRend.sprite != spriteRenderer.sprite)
        {
            // One shared material handles every texture automatically (see field comment above)
            // — no repointing needed when the sprite/texture changes.
            shadowRend.sprite = spriteRenderer.sprite;
        }

        shadowObj.transform.localPosition = new Vector3(shadowOffsetX, shadowOffsetY, 0f);

        // Quaternion.eulerAngles.z is always reported in 0..360, so as the sun's angle crosses
        // the 0/360 seam it jumps from ~359 to ~0 — which Lerp below would read as a full swing
        // across the whole shadowHorizontalMovement range in a single frame. Re-wrap into
        // -180..180 first so the angle changes continuously with no seam to jump across.
        float sunAngle = shadowRotation.eulerAngles.z;
        if (sunAngle > 180f)
            sunAngle -= 360f;

        // Map -180..180 to 0..1, then use it to slide the shadow from one side to the other as
        // the sun sweeps across the sky (purely a stylistic exaggeration of the shadow's motion).
        float dividedAngle = (sunAngle + 180f) / 360f;
        float horizontalPosition = Mathf.Lerp(-shadowHorizontalMovement, shadowHorizontalMovement, dividedAngle);
        shadowObj.transform.localPosition += new Vector3(horizontalPosition, 0f, 0f);

        shadowObj.transform.rotation = shadowRotation;

        float shadowLength = shadowDistanceMultiplier * objectHeight;
        float scaleY = Mathf.Lerp(shadowLength, 0.3f, lightElevation);
        shadowObj.transform.localScale = new Vector3(
            transform.localScale.x * xScale,
            transform.localScale.y * scaleY,
            transform.localScale.z
        );

        shadowRend.color = shadowColor;

        shadowRend.flipX = spriteRenderer.flipX;
        shadowRend.flipY = spriteRenderer.flipY;
    }

    void OnDestroy()
    {
        if (shadowObject != null)
        {
            if (Application.isPlaying)
            {
                Destroy(shadowObject);
            }
            else
            {
                DestroyImmediate(shadowObject);
            }
        }

        // shadowMaster.shadowMaterial is shared with every other shadow caster in the scene —
        // do not destroy it here.

        foreach (var shadow in autoShadowObjects)
        {
            if (shadow != null)
            {
                if (Application.isPlaying)
                    Destroy(shadow);
                else
                    DestroyImmediate(shadow);
            }
        }
        autoShadowObjects.Clear();
        autoShadowRenderers.Clear();
        autoLights.Clear();
    }

    void OnDisable()
    {
        if (shadowObject != null)
            shadowObject.SetActive(false);
        foreach (var shadow in autoShadowObjects)
            if (shadow != null)
                shadow.SetActive(false);
        if (ShadowMaster.Instance != null)
            ShadowMaster.Instance.UnregisterShadow(this);
    }
}
