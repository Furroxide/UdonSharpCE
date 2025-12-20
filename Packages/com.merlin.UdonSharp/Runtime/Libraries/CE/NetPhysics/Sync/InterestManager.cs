using JetBrains.Annotations;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Determines which entities are relevant to a client for syncing.
    /// </summary>
    [PublicAPI]
    public class InterestManager
    {
        public float AlwaysRelevantRadius = 30f;
        public float NeverRelevantRadius = 200f;
        public int MaxEntitiesPerClient = 32;

        /// <summary>
        /// Fills <paramref name="output"/> with indices into <paramref name="allEntities"/> that are relevant.
        /// </summary>
        public void GetRelevantEntities(
            int clientId,
            VRCPlayerApi clientPlayer,
            NetPhysicsEntity[] allEntities,
            int entityCount,
            int[] output,
            out int outputCount)
        {
            outputCount = 0;
            if (clientPlayer == null || !clientPlayer.IsValid() || allEntities == null || output == null)
                return;

            int maxOut = Mathf.Min(output.Length, MaxEntitiesPerClient);
            Vector3 clientPos = clientPlayer.GetPosition();

            // Always-relevant pass.
            for (int i = 0; i < entityCount && outputCount < maxOut; i++)
            {
                var e = allEntities[i];
                if (e != null && e.AlwaysRelevant)
                    output[outputCount++] = i;
            }

            // Distance-based pass: select top N by priority without full sorting.
            // We use simple selection of the best remaining entities.
            while (outputCount < maxOut)
            {
                int bestIndex = -1;
                float bestScore = -1f;

                for (int i = 0; i < entityCount; i++)
                {
                    var e = allEntities[i];
                    if (e == null || e.AlwaysRelevant)
                        continue;

                    if (IsAlreadySelected(output, outputCount, i))
                        continue;

                    float dist = Vector3.Distance(e.transform.position, clientPos);
                    if (dist > NeverRelevantRadius)
                        continue;

                    float score;
                    if (dist < AlwaysRelevantRadius)
                        score = 100f;
                    else
                        score = 1f - (dist / Mathf.Max(0.01f, NeverRelevantRadius));

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                    break;

                output[outputCount++] = bestIndex;
            }
        }

        private static bool IsAlreadySelected(int[] output, int outputCount, int index)
        {
            for (int i = 0; i < outputCount; i++)
            {
                if (output[i] == index)
                    return true;
            }
            return false;
        }
    }
}

