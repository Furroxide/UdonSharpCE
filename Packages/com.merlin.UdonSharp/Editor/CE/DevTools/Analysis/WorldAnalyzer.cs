using System;
using System.Collections.Generic;
using System.Linq;
using UdonSharp;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UdonSharp.CE.Editor.DevTools.Analysis
{
    /// <summary>
    /// Analyzes all UdonSharpBehaviours in a scene for network bandwidth usage.
    /// Provides world-wide totals and identifies bandwidth budget issues.
    /// </summary>
    public class WorldAnalyzer
    {
        private readonly BehaviourAnalyzer _behaviourAnalyzer = new BehaviourAnalyzer();

        #region Constants

        /// <summary>
        /// Network budget in KB/s.
        /// </summary>
        public const float NETWORK_BUDGET_KBPS = BehaviourAnalyzer.NETWORK_BUDGET_KBPS;

        /// <summary>
        /// Warning threshold as percentage of budget.
        /// </summary>
        public const float WARNING_THRESHOLD = 0.7f;

        /// <summary>
        /// Error threshold as percentage of budget.
        /// </summary>
        public const float ERROR_THRESHOLD = 1.0f;

        #endregion

        #region Public Methods

        /// <summary>
        /// Analyzes all UdonSharpBehaviours in the current scene.
        /// </summary>
        /// <returns>World analysis result with all behaviours and aggregate data.</returns>
        public WorldAnalysisResult AnalyzeScene()
        {
            var result = new WorldAnalysisResult();

            // Find all UdonSharpBehaviours in scene
            var behaviours = FindAllUdonSharpBehaviours();

            // Group by type
            var byType = behaviours.GroupBy(b => b.GetType());

            foreach (var group in byType)
            {
                try
                {
                    // The instances go in with the type: a synced array's length lives on the object.
                    List<UdonSharpBehaviour> instances = group.ToList();

                    var typeAnalysis = _behaviourAnalyzer.Analyze(group.Key, null, instances);
                    typeAnalysis.InstanceCount = instances.Count;

                    // Only include behaviours that actually cost variable-sync bandwidth. This used to
                    // also admit anything whose SyncMode was not "None", which now that an absent
                    // [UdonBehaviourSyncMode] correctly resolves to Continuous would list every behaviour
                    // in the scene and bury the ones that really sync.
                    if (typeAnalysis.Fields.Count > 0)
                    {
                        result.BehaviourResults.Add(typeAnalysis);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CE.DevTools] Failed to analyze {group.Key.Name}: {ex.Message}");
                }
            }

            // Calculate world totals
            CalculateTotals(result);

            // Check world-level violations
            CheckWorldViolations(result);

            return result;
        }

        /// <summary>
        /// Analyzes a specific list of behaviours (for custom filtering).
        /// </summary>
        /// <param name="behaviours">The behaviours to analyze.</param>
        /// <returns>World analysis result.</returns>
        public WorldAnalysisResult AnalyzeBehaviours(IEnumerable<UdonSharpBehaviour> behaviours)
        {
            var result = new WorldAnalysisResult();

            if (behaviours == null)
                return result;

            // Group by type. A destroyed behaviour is still a managed object with a type, but there is
            // nothing left on it to measure.
            var byType = behaviours.Where(b => b != null).GroupBy(b => b.GetType());

            foreach (var group in byType)
            {
                try
                {
                    List<UdonSharpBehaviour> instances = group.ToList();

                    var typeAnalysis = _behaviourAnalyzer.Analyze(group.Key, null, instances);
                    typeAnalysis.InstanceCount = instances.Count;

                    if (typeAnalysis.Fields.Count > 0)
                    {
                        result.BehaviourResults.Add(typeAnalysis);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CE.DevTools] Failed to analyze {group.Key.Name}: {ex.Message}");
                }
            }

            CalculateTotals(result);
            CheckWorldViolations(result);

            return result;
        }

        #endregion

        #region Private Methods

        private List<UdonSharpBehaviour> FindAllUdonSharpBehaviours()
        {
            var behaviours = new List<UdonSharpBehaviour>();

            // Search all loaded scenes
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                var rootObjects = scene.GetRootGameObjects();
                foreach (var root in rootObjects)
                {
                    var found = root.GetComponentsInChildren<UdonSharpBehaviour>(true);
                    behaviours.AddRange(found);
                }
            }

            // Also check DontDestroyOnLoad objects
#if UNITY_EDITOR
            // In editor, we can use FindObjectsOfType as well
            var allBehaviours = UnityEngine.Object.FindObjectsOfType<UdonSharpBehaviour>(true);
            foreach (var b in allBehaviours)
            {
                if (!behaviours.Contains(b))
                    behaviours.Add(b);
            }
#endif

            return behaviours;
        }

        private void CalculateTotals(WorldAnalysisResult result)
        {
            result.TotalMinBytes = 0;
            result.TotalMaxBytes = 0;
            result.TotalEstimatedBandwidthKBps = 0;
            result.TotalSyncedBehaviours = 0;
            result.TotalSyncedFields = 0;

            foreach (var behaviour in result.BehaviourResults)
            {
                result.TotalMinBytes += behaviour.MinTotalBytes * behaviour.InstanceCount;
                result.TotalMaxBytes += behaviour.MaxTotalBytes * behaviour.InstanceCount;
                result.TotalEstimatedBandwidthKBps += behaviour.TotalBandwidthKBps;
                result.TotalSyncedBehaviours += behaviour.InstanceCount;
                result.TotalSyncedFields += behaviour.Fields.Count * behaviour.InstanceCount;
            }
        }

        private void CheckWorldViolations(WorldAnalysisResult result)
        {
            // Check total bandwidth
            if (result.TotalEstimatedBandwidthKBps > NETWORK_BUDGET_KBPS)
            {
                result.Violations.Add(LimitViolation.Error(
                    $"Total bandwidth ({result.TotalEstimatedBandwidthKBps:F1} KB/s) exceeds {NETWORK_BUDGET_KBPS} KB/s limit",
                    "Reduce sync frequency, compress data, or reduce number of synced behaviours"
                ));
            }
            else if (result.TotalEstimatedBandwidthKBps > NETWORK_BUDGET_KBPS * WARNING_THRESHOLD)
            {
                result.Violations.Add(LimitViolation.Warning(
                    $"Bandwidth usage high ({result.TotalEstimatedBandwidthKBps:F1} KB/s) - may degrade with more players",
                    "Consider optimization to leave headroom for player-specific sync"
                ));
            }

            // Aggregate behaviour-level violations
            foreach (var behaviour in result.BehaviourResults)
            {
                foreach (var violation in behaviour.Violations)
                {
                    if (violation.Severity == AnalysisSeverity.Error)
                    {
                        // Propagate errors to world level with context. The behaviour travels with the
                        // copy so a consumer can attribute it without going back to the per-behaviour
                        // list, which is where the copy came from and would give it the same error twice.
                        LimitViolation propagated = LimitViolation.Error(
                            $"{behaviour.BehaviourType.Name}: {violation.Message}",
                            violation.Recommendation
                        );

                        propagated.BehaviourType = behaviour.BehaviourType;
                        result.Violations.Add(propagated);
                    }
                }
            }

            // Check for too many synced behaviours
            if (result.TotalSyncedBehaviours > 100)
            {
                result.Violations.Add(LimitViolation.Warning(
                    $"Large number of synced behaviours ({result.TotalSyncedBehaviours})",
                    "Consider consolidating sync logic or using object pooling"
                ));
            }
        }

        #endregion

        #region Static Helpers

        /// <summary>
        /// Gets the bandwidth budget in KB/s.
        /// </summary>
        public static float GetBandwidthBudget() => NETWORK_BUDGET_KBPS;

        /// <summary>
        /// Calculates what percentage of the budget a given bandwidth represents.
        /// </summary>
        public static float GetBudgetPercentage(float bandwidthKBps)
        {
            return (bandwidthKBps / NETWORK_BUDGET_KBPS) * 100f;
        }

        /// <summary>
        /// Determines if a bandwidth value is within acceptable limits.
        /// </summary>
        public static bool IsWithinBudget(float bandwidthKBps)
        {
            return bandwidthKBps <= NETWORK_BUDGET_KBPS;
        }

        /// <summary>
        /// Gets the severity level for a given bandwidth value.
        /// </summary>
        public static AnalysisSeverity GetBandwidthSeverity(float bandwidthKBps)
        {
            float ratio = bandwidthKBps / NETWORK_BUDGET_KBPS;
            if (ratio > ERROR_THRESHOLD)
                return AnalysisSeverity.Error;
            if (ratio > WARNING_THRESHOLD)
                return AnalysisSeverity.Warning;
            return AnalysisSeverity.Info;
        }

        #endregion
    }
}

