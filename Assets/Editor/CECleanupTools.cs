using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

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


    [MenuItem("CE Tools/Cleanup/Fix Unsupported Shaders")]
    public static void FixUnsupportedShaders()
    {
        string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/Materials" });
        Shader vrcShader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.vrchat.worlds/Shaders/VRChat/Mobile/StandardLite.shader");
        
        if (vrcShader == null)
        {
            Debug.LogError("[CE] Could not find VRChat/Mobile/Standard Lite shader!");
            return;
        }
        
        int fixedCount = 0;
        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            
            if (mat != null && mat.shader != null && mat.shader.name == "Standard")
            {
                Color color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                mat.shader = vrcShader;
                mat.SetColor("_Color", color);
                EditorUtility.SetDirty(mat);
                fixedCount++;
                Debug.Log($"[CE] Fixed shader for: {path}");
            }
        }
        
        if (fixedCount > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log($"[CE] Fixed {fixedCount} material(s) with unsupported shaders.");
        }
        else
        {
            Debug.Log("[CE] No materials with unsupported Standard shader found.");
        }
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
