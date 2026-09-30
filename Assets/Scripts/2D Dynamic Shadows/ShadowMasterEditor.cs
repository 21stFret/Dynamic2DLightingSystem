#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

/// <summary>
/// Custom editor for ShadowMaster that adds helpful buttons for managing shadows
/// </summary>
[CustomEditor(typeof(ShadowMaster))]
public class ShadowMasterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // Draw the default inspector
        DrawDefaultInspector();
        
        ShadowMaster shadowMaster = (ShadowMaster)target;
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Shadow Management", EditorStyles.boldLabel);
        
        // Display shadow count
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"Registered Shadows: {shadowMaster.GetShadowCount()}");
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.Space(5);
        
        // Button to refresh shadows
        if (GUILayout.Button("Refresh All Shadows", GUILayout.Height(30)))
        {
            shadowMaster.RefreshShadows();
            EditorUtility.SetDirty(shadowMaster);
        }
        
        // Button to force update
        if (GUILayout.Button("Force Update All Shadows", GUILayout.Height(30)))
        {
            shadowMaster.ForceUpdateAllShadows();
            EditorUtility.SetDirty(shadowMaster);
        }
        
        EditorGUILayout.Space(5);
        
        // Help box
        EditorGUILayout.HelpBox(
            "Use 'Refresh All Shadows' to detect new shadow components.\n" +
            "Use 'Clean Up Duplicate Shadows' if you see multiple shadow objects.\n" +
            "Use 'Force Update All Shadows' to manually update shadow rendering.",
            MessageType.Info
        );
    }
}
#endif
