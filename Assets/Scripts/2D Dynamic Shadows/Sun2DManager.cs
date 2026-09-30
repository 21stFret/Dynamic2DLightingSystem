using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Drives a Light2D "sun" across an arc over a day cycle, purely for lighting/shadow showcase
/// purposes. No save system, game ticks, or gameplay events — generic replacement for a
/// project-specific day/night manager.
/// </summary>
public class Sun2DManager : MonoBehaviour
{
    public static Sun2DManager Instance { get; private set; }

    [Header("Light References")]
    [Tooltip("Spotlight that acts as the sun, moves across the sky and casts shadows")]
    public Light2D sunLight;

    [Tooltip("Optional global light for ambient day/night tinting")]
    public Light2D ambientLight;

    [Header("Cycle")]
    [Tooltip("Length of a full day in seconds")]
    public float dayLengthInSeconds = 60f;
    public bool autoAdvance = true;
    public bool clockwise = true;
    [Range(0f, 1f)] public float sunriseTime = 0.25f;
    [Range(0f, 1f)] public float sunsetTime = 0.75f;
    [Range(0f, 1f)] public float timeOfDay = 0.5f;

    [Header("Sun Arc")]
    public float sunArcRadius = 20f;
    [Range(0.01f, 10f)] public float sunHeightTarget = 5f;

    [Header("Sun Light")]
    public float sunIntensity = 1f;
    public Color sunColor = new Color(1f, 0.95f, 0.9f);
    public Color dawnDuskColor = new Color(1f, 0.7f, 0.5f);

    [Header("Ambient Light")]
    public float ambientDayIntensity = 0.4f;
    public float ambientNightIntensity = 0.15f;
    public Color ambientDayColor = new Color(0.8f, 0.9f, 1f);
    public Color ambientNightColor = new Color(0.2f, 0.3f, 0.5f);

    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }

    void Update()
    {
        if (autoAdvance && Application.isPlaying)
        {
            timeOfDay += Time.deltaTime / dayLengthInSeconds;
            if (timeOfDay >= 1f) timeOfDay -= 1f;
        }
        ApplyTimeOfDay();
    }

    public void ApplyTimeOfDay()
    {
        bool isSunUp = IsDaytime();

        if (sunLight != null)
        {
            if (isSunUp)
            {
                float dayProgress = (timeOfDay - sunriseTime) / (sunsetTime - sunriseTime);
                float angleRad = Mathf.Lerp(-180f, 0f, dayProgress) * Mathf.Deg2Rad;

                float x = Mathf.Cos(angleRad) * sunArcRadius;
                float y = Mathf.Sin(angleRad) * sunArcRadius * (clockwise ? -1f : 1f);
                float z = Mathf.Abs(Mathf.Sin(angleRad) * sunHeightTarget);
                sunLight.transform.position = new Vector3(x, y, z);

                float curve = Mathf.Sin(dayProgress * Mathf.PI);
                sunLight.intensity = sunIntensity * curve;
                sunLight.color = Color.Lerp(dawnDuskColor, sunColor, Mathf.Pow(curve, 0.5f));
            }
            else
            {
                sunLight.intensity = 0f;
            }
        }

        if (ambientLight != null)
        {
            float cycle = Mathf.Sin((timeOfDay - 0.25f) * 2f * Mathf.PI);
            float dayBlend = Mathf.Clamp01((cycle + 1f) * 0.5f);
            ambientLight.intensity = Mathf.Lerp(ambientNightIntensity, ambientDayIntensity, dayBlend);
            ambientLight.color = Color.Lerp(ambientNightColor, ambientDayColor, dayBlend);
        }
    }

    public bool IsDaytime() => timeOfDay >= sunriseTime && timeOfDay <= sunsetTime;

    /// <summary>Sun elevation as 0-1 (0 = horizon, 1 = noon peak). Used by ShadowMaster.</summary>
    public float GetSunElevation()
    {
        if (!IsDaytime()) return 0f;
        float dayProgress = (timeOfDay - sunriseTime) / (sunsetTime - sunriseTime);
        // Same as the sun's z / 10 — matches ShadowMaster's fallback so edit and play mode agree.
        return Mathf.Clamp01(Mathf.Sin(dayProgress * Mathf.PI) * sunHeightTarget / 10f);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!Application.isPlaying)
            ApplyTimeOfDay();
    }
#endif
}
