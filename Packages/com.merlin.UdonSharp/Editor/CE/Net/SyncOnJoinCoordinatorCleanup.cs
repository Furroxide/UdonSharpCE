using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;
using VRC.Udon;
// 'using System' would otherwise make the bare name ambiguous with System.Object.
using Object = UnityEngine.Object;

namespace UdonSharp.CE.Editor.Net
{
    /// <summary>
    /// Removes the obsolete generated <c>__CE_SyncOnJoinCoordinator</c> object from scenes.
    ///
    /// <c>[SyncOnJoin]</c> no longer uses a coordinator. Each behaviour now carries its own
    /// <c>[UdonSynced]</c> payload string, so VRChat delivers late-join state natively and authority
    /// follows normal object ownership.
    ///
    /// The old design routed every payload through one hidden master-owned object that read each
    /// behaviour's state from the *master's local copy*, which meant state authored by any other client
    /// never reached late joiners at all. A stale coordinator left in a scene is now worse than useless:
    /// it would push snapshots into an inbound field that no longer exists.
    /// </summary>
    public static class SyncOnJoinCoordinatorCleanup
    {
        // Both generated shapes share this prefix, which is also what the prefab prefilter searches the raw file for.
        internal const string CoordinatorNamePrefix = "__CE_SyncOnJoin";
        internal const string CoordinatorName = CoordinatorNamePrefix + "Coordinator";

        [MenuItem("CE Tools/Remove Obsolete SyncOnJoin Coordinators")]
        public static void RemoveFromLoadedScenesMenu()
        {
            int removed = RemoveFromLoadedScenes(true);

            // The menu item is also the manual re-run for the automatic migration, so it sweeps prefabs too.
            // Without that there would be no way back if the one-shot sweep failed partway through.
            removed += RemoveFromPrefabAssets(true);

            EditorUtility.DisplayDialog(
                "CE SyncOnJoin",
                removed > 0
                    ? $"Removed {removed} obsolete SyncOnJoin coordinator object(s). Save the scene to persist this."
                    : "No obsolete SyncOnJoin coordinators found.",
                "OK");
        }

        public static int RemoveFromLoadedScenes(bool logChanges = false)
        {
            int removed = 0;

            // EditorSceneManager.sceneCount, not SceneManager.loadedSceneCount: GetSceneAt indexes every scene open
            // in the hierarchy including unloaded ones, so pairing it with the loaded-only count makes the loop stop
            // short as soon as the user has one unloaded scene open and silently leaves later scenes unswept.
            // RemoveFromScene skips the unloaded ones itself.
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                removed += RemoveFromScene(EditorSceneManager.GetSceneAt(i), logChanges);

            return removed;
        }

        public static int RemoveFromScene(Scene scene, bool logChanges = false)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return 0;

            var toRemove = new List<GameObject>();

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                CollectCoordinators(root.transform, toRemove);
            }

            foreach (GameObject coordinator in toRemove)
            {
                if (logChanges)
                {
                    Debug.Log(
                        $"[CE.SyncOnJoin] Removing obsolete coordinator '{coordinator.name}' from scene " +
                        $"'{scene.path}'. [SyncOnJoin] state now syncs directly on each behaviour.");
                }

                Undo.DestroyObjectImmediate(coordinator);
            }

            if (toRemove.Count > 0)
                EditorSceneManager.MarkSceneDirty(scene);

            return toRemove.Count;
        }

        /// <summary>
        /// Removes coordinators that were saved into prefab assets. Nothing else in the migration can reach these:
        /// a prefab is only ever seen through an instance, and by then the stale coordinator is already live.
        /// </summary>
        public static int RemoveFromPrefabAssets(bool logChanges = false)
        {
            return RemoveFromPrefabAssets(logChanges, out _);
        }

        /// <summary>
        /// <paramref name="cancelled"/> reports that the user stopped the sweep partway through, so a caller running
        /// the one-shot migration can avoid recording it as done when most of the project was never looked at.
        /// </summary>
        internal static int RemoveFromPrefabAssets(bool logChanges, out bool cancelled)
        {
            int removed = 0;
            var changedPrefabPaths = new List<string>();
            var toRemove = new List<GameObject>();

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

            cancelled = false;

            try
            {
                for (int prefabIndex = 0; prefabIndex < prefabGuids.Length; prefabIndex++)
                {
                    // The sweep is kicked off from delayCall on the first domain reload after the upgrade. Without a
                    // bar the user just sees the editor stop responding for however long the project takes, and
                    // without a cancel there is no way out of it at all.
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "CE SyncOnJoin",
                            $"Scanning prefabs for obsolete coordinators ({prefabIndex + 1}/{prefabGuids.Length})",
                            (float)prefabIndex / prefabGuids.Length))
                    {
                        cancelled = true;
                        break;
                    }

                    string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[prefabIndex]);

                    // Cheap gate before anything is handed to the asset database. LoadAssetAtPath deserializes a
                    // prefab's whole object graph and keeps it resident, and doing that for every prefab in a large
                    // project is exactly the startup freeze this sweep must not cause. A coordinator is identified
                    // by its GameObject name, which is stored verbatim in both the text and binary serializations,
                    // so a file that does not contain that string cannot contain a coordinator.
                    if (!FileMayContainCoordinator(prefabPath))
                        continue;

                    GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

                    if (prefabAsset == null)
                        continue;

                    // The loaded asset is only used to decide whether this prefab is worth opening, since
                    // LoadPrefabContents spins up a temporary scene for every prefab it is handed.
                    toRemove.Clear();
                    CollectCoordinators(prefabAsset.transform, toRemove);

                    if (toRemove.Count == 0)
                        continue;

                    if (toRemove.Contains(prefabAsset))
                    {
                        // A prefab whose root *is* the coordinator cannot be repaired by editing it, and deleting
                        // assets is not something a migration should do behind the user's back.
                        Debug.LogWarning(
                            $"[CE.SyncOnJoin] Prefab '{prefabPath}' is itself an obsolete SyncOnJoin coordinator. " +
                            "Delete the prefab, it no longer does anything.");
                        continue;
                    }

                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);

                    if (PrefabUtility.IsPartOfImmutablePrefab(prefabRoot))
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                        continue;
                    }

                    int prefabRemoved = 0;

                    try
                    {
                        toRemove.Clear();
                        CollectCoordinators(prefabRoot.transform, toRemove);

                        foreach (GameObject coordinator in toRemove)
                        {
                            // Undo has no meaning inside the temporary prefab editing scene, so destroy directly.
                            Object.DestroyImmediate(coordinator);
                            prefabRemoved++;
                        }

                        if (prefabRemoved > 0)
                            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                    }

                    if (prefabRemoved > 0)
                    {
                        removed += prefabRemoved;
                        changedPrefabPaths.Add(prefabPath);
                    }
                }
            }
            finally
            {
                // In a finally because a throw anywhere above would otherwise leave the editor permanently stuck
                // behind a modal progress bar with no way to dismiss it.
                EditorUtility.ClearProgressBar();
            }

            if (logChanges && removed > 0)
            {
                Debug.Log(
                    $"[CE.SyncOnJoin] Removed {removed} obsolete coordinator object(s) from " +
                    $"{changedPrefabPaths.Count} prefab(s): {string.Join(", ", changedPrefabPaths)}. " +
                    "[SyncOnJoin] state now syncs directly on each behaviour.");
            }

            return removed;
        }

        /// <summary>
        /// Prefilter in front of the expensive per-prefab work: scans the serialized file for the coordinator name
        /// instead of loading the prefab. Errs towards true, because a false positive costs one prefab load while a
        /// false negative leaves a stale coordinator in the project forever.
        ///
        /// A dependency-table filter cannot be used here the way the sync-mode pass uses one: after the upgrade the
        /// coordinator's generated program asset is gone, so the prefab no longer depends on any U# asset at all.
        /// </summary>
        private static bool FileMayContainCoordinator(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return false;

            try
            {
                // ASCII so binary-serialized prefabs decode one char per byte; the name being searched for is ASCII,
                // so it survives intact either way, and streaming avoids materializing a large prefab as one string.
                using (StreamReader reader = new StreamReader(prefabPath, Encoding.ASCII))
                {
                    int overlap = CoordinatorNamePrefix.Length - 1;
                    char[] buffer = new char[8192];
                    int carried = 0;

                    while (true)
                    {
                        int read = reader.Read(buffer, carried, buffer.Length - carried);

                        if (read <= 0)
                            return false;

                        int available = carried + read;

                        if (new string(buffer, 0, available).IndexOf(CoordinatorNamePrefix, StringComparison.Ordinal) >= 0)
                            return true;

                        // Carry the tail forward so a name straddling two reads is still found.
                        carried = Mathf.Min(overlap, available);
                        Array.Copy(buffer, available - carried, buffer, 0, carried);
                    }
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static void CollectCoordinators(Transform transform, List<GameObject> results)
        {
            if (transform == null)
                return;

            if (transform.gameObject.name == CoordinatorName)
            {
                results.Add(transform.gameObject);
                return;
            }

            // A coordinator whose script was already deleted shows up as an UdonBehaviour with a null
            // program source on a hidden object; catch that shape too so cleanup is not order-dependent.
            if ((transform.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0 &&
                transform.gameObject.GetComponent<UdonBehaviour>() != null &&
                transform.gameObject.name.StartsWith(CoordinatorNamePrefix, StringComparison.Ordinal))
            {
                results.Add(transform.gameObject);
                return;
            }

            for (int i = 0; i < transform.childCount; i++)
                CollectCoordinators(transform.GetChild(i), results);
        }
    }

    /// <summary>
    /// Runs <see cref="SyncOnJoinCoordinatorCleanup"/> without the user having to know the menu item exists.
    ///
    /// Scenes are cleaned as they are opened rather than by force-opening every scene in the project, because a
    /// stale coordinator only does damage once it is loaded and force-opening scenes would dirty work the user
    /// never asked to touch. Prefab assets have no equivalent moment, so they are swept once per project.
    /// </summary>
    [InitializeOnLoad]
    internal static class SyncOnJoinCoordinatorMigration
    {
        // EditorPrefs is per-user and not per-project, so the project path is part of the key. A shared key would
        // mark every other project on the machine as already migrated before it was ever looked at.
        private static string PrefabSweepDoneKey =>
            "UdonSharpCE.SyncOnJoinCoordinatorPrefabSweep." + Application.dataPath;

        static SyncOnJoinCoordinatorMigration()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;

            // Deferred because the asset database is not safe to query while the domain is still reloading.
            EditorApplication.delayCall += RunMigration;
        }

        /// <summary>
        /// The migration destroys objects and dirties scenes, so it has to stay out of the way of anything that is
        /// already driving the editor. The VRChat asset bundle build opens scenes programmatically and must not have
        /// handlers mutating them underneath it - that is exactly why UdonSharp gates its own sceneOpened handler on
        /// <see cref="UdonSharpEditorManager.SkipSceneOpen"/>, and a scene dirtied mid-build either breaks the upload
        /// or ships edits the user never saw. Compiling and importing are excluded for the same reason: objects
        /// destroyed while the domain or the asset database is mid-flight come back as broken references.
        /// </summary>
        private static bool CanRunMigration()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode &&
                   !BuildPipeline.isBuildingPlayer &&
                   !EditorApplication.isCompiling &&
                   !EditorApplication.isUpdating &&
                   !UdonSharpEditorManager.SkipSceneOpen;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (!CanRunMigration())
                return;

            SyncOnJoinCoordinatorCleanup.RemoveFromScene(scene, true);
        }

        private static void RunMigration()
        {
            if (!CanRunMigration())
            {
                // Re-queued rather than dropped, and queued before the done-flag is written: the blockers are all
                // transient, and the first domain reload of a project routinely lands while the asset database is
                // still importing. Dropping the sweep there would leave stale coordinators in the project until
                // someone happened to find the menu item. delayCall fires once per editor tick, so polling for the
                // blocker to clear costs nothing measurable.
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorApplication.delayCall += RunMigration;

                return;
            }

            // The scene that was already open when the domain reloaded never raises sceneOpened, so it is swept
            // here. This only logs or dirties anything when a coordinator is actually found, so repeating it on
            // each domain reload cannot turn into console spam.
            SyncOnJoinCoordinatorCleanup.RemoveFromLoadedScenes(true);

            if (EditorPrefs.GetBool(PrefabSweepDoneKey, false))
                return;

            // Flagged before the sweep runs: if scanning throws, that is worth one report, not one per domain
            // reload for the rest of the project's life. The menu item re-runs it on demand.
            EditorPrefs.SetBool(PrefabSweepDoneKey, true);

            SyncOnJoinCoordinatorCleanup.RemoveFromPrefabAssets(true, out bool sweepCancelled);

            // A sweep the user stopped never reached the remaining prefabs, so it must not count as done.
            if (sweepCancelled)
                EditorPrefs.SetBool(PrefabSweepDoneKey, false);
        }
    }

    /// <summary>
    /// Strips obsolete coordinators before an upload so they cannot ship in a world.
    /// Runs before <c>UdonSharpBuildCompile</c> (order 100).
    /// </summary>
    internal sealed class SyncOnJoinCoordinatorCleanupBuildProcessor : IVRCSDKBuildRequestedCallback
    {
        public int callbackOrder => 90;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            SyncOnJoinCoordinatorCleanup.RemoveFromLoadedScenes(true);
            return true;
        }
    }
}
