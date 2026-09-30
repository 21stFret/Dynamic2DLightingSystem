using UnityEngine;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteInEditMode]
public class ShadowMaster : MonoBehaviour
{
    public static ShadowMaster Instance;

    [Header("Sun Settings")]
    [Tooltip("The position of the sun in world space")]
    public Vector2 sunPosition = new Vector2(0, 10);

    [Tooltip("Minimum sun height required for shadows to be visible")]
    [Range(0f, 1f)]
    public float minSunHeightForShadows = 0.1f;

    [Tooltip("Optional: Reference to a transform that represents the sun")]
    public Transform sunTransform;

    [Tooltip("Global multiplier for shadow distance from object")]
    [Range(0f, 5f)]
    public float shadowDistanceMultiplier = 1f;

    [Tooltip("Shadow tint (RGB only; alpha is driven by shadowIntensity and sun height)")]
    public Color shadowColor = Color.black;

    [Tooltip("Global shadow intensity")]
    [Range(0f, 1f)]
    public float shadowIntensity = 0.5f;

    [Tooltip("How much the shadow's x-scale shrinks as sun gets lower (0 = no shrink, 1 = maximum shrink)")]
    [Range(0f, 1f)]
    public float shadowXScaleShrinkAmount = 0.3f;

    [Header("Performance")]
    [Tooltip("Update shadows every frame during Play mode")]
    public bool autoUpdate = true;

    [Tooltip("Update shadows every frame in the editor (Scene view), outside Play mode — lets you scrub Sun2DManager's time of day, or any other sun movement, and see shadows react live. Disable for better editor performance in large scenes.")]
    public bool updateInEditor = true;

    // Cached values, computed once per frame and shared across all shadows
    private Vector2 shadowDirection;
    private float sunElevation;
    private Color baseShadowColor;
    private float baseShadowAlpha;
    private float shadowXScale;
    private Quaternion shadowQuaternion;

    private List<DynamicShadow2D> shadows = new List<DynamicShadow2D>();

    public GameObject shadowPrefab;
    public float shadowFadeSpeed = 1f;

    [Header("Shadow Material")]
    [Tooltip("Single material shared by every shadow caster (sun + auto fire/torch, DynamicShadow2D + MeshShadow2D). Use a real URP sprite shader (e.g. Sprite-Unlit-Default) with its Color left at default white — DynamicShadow2D/MeshShadow2D drive the actual per-instance tint via SpriteRenderer.color / vertex color, not the material.")]
    public Material shadowMaterial;

    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else if (Instance != this)
        {
            DestroyImmediate(this);
            return;
        }
    }

    void OnEnable()
    {
        Instance = this;
    }

    void OnValidate()
    {
        if (!Application.isPlaying && updateInEditor)
        {
#if UNITY_EDITOR
            // Instantiating shadow objects here would trigger internal Unity
            // SendMessage calls (e.g. OnSpriteRendererBoundsChanged), which Unity
            // disallows while still inside OnValidate. Defer until afterward.
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                Instance = this;
                RefreshShadows();
                CalculateShadowProperties();
                ApplyToAllShadows();
            };
#endif
        }
    }

    void Start()
    {
        RefreshShadows();
    }

    void Update()
    {
        if (!Application.isPlaying)
        {
            // [ExecuteInEditMode] means this otherwise ticks every frame in the Scene view even
            // outside Play mode. Gated by updateInEditor rather than always skipped, since this
            // is what reacts to the sun moving from another component (e.g. scrubbing
            // Sun2DManager's time of day) — OnValidate above only fires on ShadowMaster's own
            // fields changing, not on an unrelated component's.
            if (updateInEditor)
            {
                CalculateShadowProperties();
                ApplyToAllShadows();
            }
            return;
        }

        if (autoUpdate)
        {
            CalculateShadowProperties();
            ApplyToAllShadows();
        }
    }

    void CalculateShadowProperties()
    {
        if (sunTransform != null)
        {
            sunPosition = sunTransform.position;
            if(Sun2DManager.Instance !=null)
            {
                sunElevation = Sun2DManager.Instance.GetSunElevation();
            }
            else
            {
                sunElevation = Mathf.Clamp01(sunTransform.position.z / 10f);
            }
        }

        Vector2 directionToSun = sunPosition.normalized;
        shadowDirection = new Vector2(-directionToSun.x, -directionToSun.y);

        float angle = Mathf.Atan2(shadowDirection.y, shadowDirection.x) * Mathf.Rad2Deg - 90f;
        shadowQuaternion = Quaternion.Euler(0f, 0f, angle);

        baseShadowColor = shadowColor;

        float targetAlpha = sunElevation < minSunHeightForShadows
            ? 0f
            : Mathf.Lerp(0.2f, shadowIntensity, sunElevation);

        baseShadowAlpha = Mathf.MoveTowards(baseShadowAlpha, targetAlpha, Time.deltaTime * shadowFadeSpeed);
        baseShadowColor.a = baseShadowAlpha;

        shadowXScale = CalculateShadowXScale();
    }

    float CalculateShadowXScale()
    {
        float scaleReduction = (1f - sunElevation) * shadowXScaleShrinkAmount;
        return Mathf.Max(1f - scaleReduction, 0.1f);
    }

    void ApplyToAllShadows()
    {
        for (int i = shadows.Count - 1; i >= 0; i--)
        {
            if (shadows[i] == null) { shadows.RemoveAt(i); continue; }
            shadows[i].ApplyShadowFromMaster(baseShadowColor, shadowQuaternion, shadowDistanceMultiplier, sunElevation, shadowXScale);
        }
    }

    // Finds and registers all DynamicShadow2D components in the scene.
    // Called at startup and from OnValidate in editor. Dynamic objects self-register via OnEnable.
    public void RefreshShadows()
    {
        shadows.Clear();
        DynamicShadow2D[] foundShadows = FindObjectsByType<DynamicShadow2D>();
        foreach (DynamicShadow2D shadow in foundShadows)
        {
            if (!shadow.enabled) continue;
            RegisterShadow(shadow);
        }

    }

    public void RegisterShadow(DynamicShadow2D shadow)
    {
        if (shadow == null || shadows.Contains(shadow))
            return;
        shadows.Add(shadow);
        shadow.shadowMaster = this;
    }

    public void UnregisterShadow(DynamicShadow2D shadow)
    {
        shadows.Remove(shadow);
    }

    public void ForceUpdateAllShadows()
    {
        CalculateShadowProperties();
        ApplyToAllShadows();
    }

    public int GetShadowCount() => shadows.Count;

    public Vector2 GetShadowDirection() => shadowDirection;

    public float GetSunElevation() => sunElevation;
}
