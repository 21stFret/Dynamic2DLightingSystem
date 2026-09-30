using UnityEngine;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

/// <summary>
/// Attach to a Light2D (fire, torch, etc.) to make nearby objects cast shadows from this light.
/// Uses a trigger collider to efficiently detect shadow casters entering/leaving range.
/// Works with both DynamicShadow2D and MeshShadow2D.
/// </summary>
[RequireComponent(typeof(Light2D))]
public class ShadowCastingLight : MonoBehaviour
{
    [Header("Shadow Settings")]
    [Tooltip("Height of this light for shadow calculations (0-1, lower = longer shadows)")]
    [Range(0f, 1f)]
    public float lightHeight = 0.3f;
    [Tooltip("Shadow intensity multiplier at closest distance")]
    [Range(0f, 1f)]
    public float shadowIntensity = 0.5f;
    [Tooltip("Shadow intensity during daytime (0 = invisible, 1 = same as night)")]
    [Range(0f, 1f)]
    public float daytimeIntensity = 0.3f;

    private Light2D light2D;
    private CircleCollider2D triggerCollider;
    private HashSet<DynamicShadow2D> registeredShadows     = new HashSet<DynamicShadow2D>();
    private HashSet<MeshShadow2D>    registeredMeshShadows = new HashSet<MeshShadow2D>();

    public LayerMask shadowCasterLayerMask = ~0; // Default to all layers

    void Awake()
    {
        light2D = GetComponent<Light2D>();
        SetupTrigger();
    }

    void Start()
    {
        FindObjectsInRange();
    }

    void SetupTrigger()
    {
        triggerCollider = GetComponent<CircleCollider2D>();
        if (triggerCollider == null)
            triggerCollider = gameObject.AddComponent<CircleCollider2D>();

        triggerCollider.isTrigger = true;
        SyncRadiusToLight();
    }

    void SyncRadiusToLight()
    {
        if (light2D != null && triggerCollider != null)
            triggerCollider.radius = light2D.pointLightOuterRadius;
    }

    void FindObjectsInRange()
    {
        if (light2D == null) return;

        float radius = light2D.pointLightOuterRadius;
        Collider2D[] colliders = Physics2D.OverlapCircleAll(transform.position, radius, shadowCasterLayerMask);

        foreach (var col in colliders)
        {
            TryRegister(col);
        }
    }

    void OnValidate()
    {
        if (light2D == null)
            light2D = GetComponent<Light2D>();
        SyncRadiusToLight();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        TryRegister(other);
    }

    void OnTriggerExit2D(Collider2D other)
    {
        TryUnregister(other);
    }

    void TryRegister(Collider2D col)
    {
        var ds = col.GetComponent<DynamicShadow2D>();
        if (ds != null && !registeredShadows.Contains(ds))
        {
            ds.RegisterAutoLight(this);
            registeredShadows.Add(ds);
        }

        var ms = col.GetComponent<MeshShadow2D>();
        if (ms != null && !registeredMeshShadows.Contains(ms))
        {
            ms.RegisterAutoLight(this);
            registeredMeshShadows.Add(ms);
        }
    }

    void TryUnregister(Collider2D col)
    {
        var ds = col.GetComponent<DynamicShadow2D>();
        if (ds != null && registeredShadows.Contains(ds))
        {
            ds.UnregisterAutoLight(this);
            registeredShadows.Remove(ds);
        }

        var ms = col.GetComponent<MeshShadow2D>();
        if (ms != null && registeredMeshShadows.Contains(ms))
        {
            ms.UnregisterAutoLight(this);
            registeredMeshShadows.Remove(ms);
        }
    }

    void OnDisable()
    {
        foreach (var s in registeredShadows)
            if (s != null) s.UnregisterAutoLight(this);
        registeredShadows.Clear();

        foreach (var s in registeredMeshShadows)
            if (s != null) s.UnregisterAutoLight(this);
        registeredMeshShadows.Clear();
    }

    void OnDestroy()
    {
        foreach (var s in registeredShadows)
            if (s != null) s.UnregisterAutoLight(this);
        registeredShadows.Clear();

        foreach (var s in registeredMeshShadows)
            if (s != null) s.UnregisterAutoLight(this);
        registeredMeshShadows.Clear();
    }

    public Light2D GetLight()           => light2D;
    public float GetLightHeight()       => lightHeight;
    public float GetShadowIntensity()   => shadowIntensity;
    public float GetDaytimeIntensity()  => daytimeIntensity;
    public float GetRadius()            => light2D != null ? light2D.pointLightOuterRadius : 5f;
}
