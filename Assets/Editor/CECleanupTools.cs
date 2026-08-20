using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps project materials on the right shader set for the active build target.
/// VRChat's mobile allowlist (VRC.SDKBase.Validation.WorldValidation.ShaderWhiteList) does not
/// include the built-in Standard shader, so the SDK panel warns about every Standard material
/// when the build target is Android/iOS. Swapping to VRChat/Mobile/Standard Lite (and back)
/// is lossless: Unity keeps serialized material properties and keywords across shader changes,
/// and Standard Lite carries the same _Color/_MainTex/_Metallic/_Glossiness/_EmissionColor
/// properties this project uses.
/// Note: the "unsupported shader 'TextMeshPro/...'" warning has no fix — the allowlist contains
/// no text-capable shader at all, so any world with TMP text carries it. It is advisory only.
/// </summary>
public class CEPlatformShaderSwap : IActiveBuildTargetChanged
{
    public int callbackOrder => 0;

    public void OnActiveBuildTargetChanged(BuildTarget previousTarget, BuildTarget newTarget)
    {
        bool nowMobile = newTarget == BuildTarget.Android || newTarget == BuildTarget.iOS;
        bool wasMobile = previousTarget == BuildTarget.Android || previousTarget == BuildTarget.iOS;

        if (nowMobile)
        {
            CECleanupTools.UseMobileShaders();
        }
        else if (wasMobile)
        {
            CECleanupTools.UseDesktopShaders();
        }
    }
}

/// <summary>
/// Clears Allow Roll on billboard particle systems as a scene is saved.
/// VRChat's world validation flags every Billboard renderer that still has allowRoll enabled
/// (VRCSdkControlPanelWorldBuilder: renderMode == Billboard && allowRoll), because a view-aligned
/// billboard that rolls with the camera tilts with the player's head — which reads as the world
/// tilting rather than the particles. Unity defaults allowRoll to true, so every newly added
/// particle system reintroduces the finding, and the SDK panel reports it only as advisory
/// information that is easy to scroll past. Fixing it on save keeps it out of the panel entirely.
/// </summary>
[InitializeOnLoad]
public static class CEParticleRollGuard
{
    static CEParticleRollGuard()
    {
        UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += OnSceneSaving;
    }

    private static void OnSceneSaving(Scene scene, string path)
    {
        // No MarkSceneDirty here: the save in flight serializes the change we just made.
        int cleared = CECleanupTools.ClearParticleRoll(scene, markDirty: false);
        if (cleared > 0)
        {
            Debug.Log($"[CE] Disabled camera roll on {cleared} billboard particle system(s) while saving {scene.name}.");
        }
    }
}

public static class CECleanupTools
{
    [MenuItem("CE Tools/Cleanup/Remove Missing Scripts in Scene")]
    public static void RemoveMissingScriptsInScene()
    {
        var scene = SceneManager.GetActiveScene();
        var rootObjects = scene.GetRootGameObjects();
        int removedCount = 0;
        
        foreach (var root in rootObjects)
        {
            removedCount += RemoveMissingScriptsRecursive(root);
        }
        
        if (removedCount > 0)
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[CE] Removed {removedCount} missing script(s) from scene.");
        }
        else
        {
            Debug.Log("[CE] No missing scripts found in scene.");
        }
    }
    
    private static int RemoveMissingScriptsRecursive(GameObject go)
    {
        int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
        
        foreach (Transform child in go.transform)
        {
            count += RemoveMissingScriptsRecursive(child.gameObject);
        }
        
        return count;
    }


    [MenuItem("CE Tools/Cleanup/Disable Particle Roll")]
    public static void DisableParticleRollInScene()
    {
        var scene = SceneManager.GetActiveScene();
        int cleared = ClearParticleRoll(scene, markDirty: true);

        if (cleared > 0)
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[CE] Disabled camera roll on {cleared} billboard particle system(s).");
        }
        else
        {
            Debug.Log("[CE] No billboard particle systems with camera roll enabled.");
        }
    }

    /// <summary>
    /// Same fix as the SDK panel's Auto Fix for "particle systems set to roll with the camera".
    /// Only touches renderers actually set to roll, so it is a no-op on a clean scene and cheap
    /// enough to run from <see cref="CEParticleRollGuard"/> on every save.
    /// </summary>
    internal static int ClearParticleRoll(Scene scene, bool markDirty)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            return 0;

        int cleared = 0;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (ParticleSystemRenderer psr in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (psr.renderMode != ParticleSystemRenderMode.Billboard || !psr.allowRoll)
                    continue;

                psr.allowRoll = false;

                // A direct property set is not recorded as an override on its own.
                if (PrefabUtility.IsPartOfPrefabInstance(psr))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(psr);

                if (markDirty)
                    EditorUtility.SetDirty(psr);

                cleared++;
            }
        }

        return cleared;
    }


    private const string StandardShaderName = "Standard";
    private const string StandardLiteShaderName = "VRChat/Mobile/Standard Lite";
    private static readonly string[] MaterialSwapRoots = { "Assets/Materials" };

    /// <summary>Legacy menu path; same as Platform/Use Mobile Shaders.</summary>
    [MenuItem("CE Tools/Cleanup/Fix Unsupported Shaders")]
    public static void FixUnsupportedShaders() => UseMobileShaders();

    [MenuItem("CE Tools/Platform/Use Mobile Shaders (Standard -> Standard Lite)")]
    public static void UseMobileShaders() => SwapShaders(StandardShaderName, StandardLiteShaderName);

    [MenuItem("CE Tools/Platform/Use Desktop Shaders (Standard Lite -> Standard)")]
    public static void UseDesktopShaders() => SwapShaders(StandardLiteShaderName, StandardShaderName);

    private static void SwapShaders(string fromShaderName, string toShaderName)
    {
        Shader toShader = Shader.Find(toShaderName);
        if (toShader == null)
        {
            Debug.LogError($"[CE] Could not find shader '{toShaderName}'!");
            return;
        }

        string[] materialGuids = AssetDatabase.FindAssets("t:Material", MaterialSwapRoots);
        int swappedCount = 0;
        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null || mat.shader == null || mat.shader.name != fromShaderName)
                continue;

            // Serialized properties and keywords survive the shader change, so the reverse
            // swap restores the original look without storing anything extra.
            mat.shader = toShader;

            // Standard only renders emission with the _EMISSION keyword and a non-"black"
            // GI flag; re-assert them for materials that carry a live emission color.
            if (toShaderName == StandardShaderName &&
                mat.HasProperty("_EmissionColor") &&
                mat.GetColor("_EmissionColor").maxColorComponent > 0f)
            {
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }

            EditorUtility.SetDirty(mat);
            swappedCount++;
        }

        if (swappedCount > 0)
        {
            AssetDatabase.SaveAssets();
        }
        Debug.Log($"[CE] Swapped {swappedCount} material(s): {fromShaderName} -> {toShaderName}. " +
                  "(The SDK's TextMeshPro shader warning on mobile targets is expected: the world " +
                  "shader allowlist has no text shader. It is advisory only.)");
    }


    [MenuItem("CE Tools/Cleanup/Fix TMP Mobile Shaders")]
    public static void FixTMPMobileShaders()
    {
        Shader desktopShader = Shader.Find("TextMeshPro/Distance Field");
        Shader mobileShader = Shader.Find("TextMeshPro/Mobile/Distance Field");
        
        if (desktopShader == null)
        {
            Debug.LogError("[CE] Could not find TextMeshPro/Distance Field shader!");
            return;
        }
        
        if (mobileShader == null)
        {
            Debug.LogWarning("[CE] Could not find mobile shader to compare. Will search by shader name.");
        }
        
        string[] assetGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/TextMesh Pro" });
        string[] fontAssetGuids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { "Assets/TextMesh Pro" });
        
        int fixedCount = 0;
        
        // Fix standalone materials
        foreach (string guid in assetGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            
            if (mat != null && mat.shader != null && mat.shader.name == "TextMeshPro/Mobile/Distance Field")
            {
                mat.shader = desktopShader;
                EditorUtility.SetDirty(mat);
                fixedCount++;
                Debug.Log($"[CE] Fixed TMP shader for: {path}");
            }
        }
        
        // Fix font asset embedded materials
        foreach (string guid in fontAssetGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            TMPro.TMP_FontAsset fontAsset = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(path);
            
            if (fontAsset != null && fontAsset.material != null && 
                fontAsset.material.shader != null && 
                fontAsset.material.shader.name == "TextMeshPro/Mobile/Distance Field")
            {
                fontAsset.material.shader = desktopShader;
                EditorUtility.SetDirty(fontAsset);
                EditorUtility.SetDirty(fontAsset.material);
                fixedCount++;
                Debug.Log($"[CE] Fixed TMP font shader for: {path}");
            }
        }
        
        if (fixedCount > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log($"[CE] Fixed {fixedCount} TMP material(s) with mobile shaders.");
        }
        else
        {
            Debug.Log("[CE] No TMP materials with mobile shaders found.");
        }
    }
}
