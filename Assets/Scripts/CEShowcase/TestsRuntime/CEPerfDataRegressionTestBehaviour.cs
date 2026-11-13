using System.Collections.Generic;
using UdonSharp;
using UdonSharp.CE.Data;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// Fixture for CEPerfDataRegressionTests. It exists because some defects cannot be reproduced in
    /// .NET at all and have to be run as a compiled Udon program; the tests read the public result
    /// fields off the heap afterwards.
    ///
    /// RunGrowth and RunChurn cover CEDictionary. It used to rebuild its table by calling Add for every
    /// existing entry, from inside Add. U# gives a method one static set of parameter slots instead of a
    /// stack frame, so those inner calls overwrote the outer call's key and value: the entry that
    /// triggered the growth was dropped and the last entry rehashed was stored twice. Plain C# has real
    /// frames and behaves correctly, which is how this survived every editor-side check. Both methods
    /// work in locals and report through plain fields on purpose: a failure there has to mean "the
    /// dictionary lost data", not "the fixture did something Udon cannot serialize".
    ///
    /// RunAudit is the VM half of the checks for the U# collections, compiler lowering and Procgen;
    /// AuditBox and the Audit static classes below are its helpers, and the two Audit enums are read by
    /// CEAuditEditorGameplayRegressionTests. SystemEarly and SystemLate are only ever called as plain
    /// C#, by the CEWorld system-order test.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CEPerfDataRegressionTestBehaviour : UdonSharpBehaviour
    {
        public bool auditRan;
        public int auditFailures;
        private Dictionary<string, int> _loadedDictionary = new Dictionary<string, int> { { "one", 1 } };
        private HashSet<int> _loadedSet = new HashSet<int> { 7 };

        public void RunAudit()
        {
            auditFailures = 0;
            var stack = new Stack<int>();
            for (int i = 1; i <= 8; i++) stack.Push(i);
            if (!stack.TryPop(out int top) || top != 8) auditFailures++;
            if (stack.ToArray()[0] != 7) auditFailures++;
            stack.Clear(); stack.TrimExcess(); stack.Push(9);
            if (stack.Pop() != 9) auditFailures++;

            var list = new List<int>(0); list.Add(1); list.RemoveRange(1, 0);
            var queue = new Queue<int>(0); queue.Enqueue(1); queue.Enqueue(2); queue.Enqueue(3);
            int pairs = 0;
            foreach (int outer in queue) foreach (int inner in queue) pairs++;
            if (pairs != 9) auditFailures++;
            var other = new Queue<int>(); other.Enqueue(4); other.Enqueue(5);
            pairs = 0;
            foreach (int outer in queue) foreach (int inner in other) pairs++;
            if (pairs != 6) auditFailures++;
            var nullable = new Queue<string>(); nullable.Enqueue(null);
            if (!nullable.Contains(null)) auditFailures++;

            if (_loadedDictionary.Count != 1 || _loadedSet.Count != 1) auditFailures++;
            var dictionary = new Dictionary<int, string>(); dictionary.Add(1, "one"); dictionary.Add(2, null);
            if (!dictionary.ContainsValue(null)) auditFailures++;
            KeyValuePair<int, string> first = default;
            int firstKey = -1;
            foreach (var pair in dictionary)
            {
                if (firstKey < 0) { first = pair; firstKey = pair.Key; }
            }
            if (first.Key != firstKey) auditFailures++;
            pairs = 0;
            foreach (var outer in dictionary) foreach (var inner in dictionary) pairs++;
            if (pairs != 4) auditFailures++;

            if (AuditStatics.A + AuditStatics.B != 12) auditFailures++;
            if (AuditStatics.A + AuditStatics.B != 12) auditFailures++;
            if (AuditStaticTarget.Echo(AuditStaticHelper.Identity(6)) != 6) auditFailures++;
            object old = new AuditBox(null);
            object saved = old;
            old = new AuditBox(old);
            if (((AuditBox)old).value != saved) auditFailures++;
            var color = new UnityEngine.Color(0, 0, 0, 0.25f);
            color = new UnityEngine.Color(1, 1, 1) { a = color.a };
            if (color.a != 0.25f) auditFailures++;
            var random = new UdonSharp.CE.Procgen.CERandom(-1);
            random.NextInt();
            var noise = UdonSharp.CE.Procgen.CENoise.Simplex3D(7.1f, -3.4f, 5.2f);
            var rules = new UdonSharp.CE.Procgen.WFCTile[1];
            rules[0] = new UdonSharp.CE.Procgen.WFCTile();
            rules[0].Id = 1;
            rules[0].Weight = 0;
            var solver = new UdonSharp.CE.Procgen.WFCSolver(new UdonSharp.CE.Procgen.WFCConfig { Width = 1, Height = 1, Tiles = rules }, 1);
            solver.Solve();
            auditRan = true;
        }

        private const int KeyCount = 100;

        // Deliberately tiny. Reaching 100 entries from 4 slots takes five rebuilds, and before the fix
        // every one of them cost an entry.
        private const int InitialCapacity = 4;

        // ---- RunGrowth -------------------------------------------------------------------------

        /// <summary>False until RunGrowth reaches its last line; false afterwards means Udon halted.</summary>
        public bool growthRan;
        public int growthCount;
        public int growthMissingKeys;
        public int growthWrongValues;
        public int growthFirstMissingKey = -1;

        // ---- RunChurn --------------------------------------------------------------------------

        /// <summary>False until RunChurn reaches its last line; false afterwards means Udon halted.</summary>
        public bool churnRan;
        public int churnCountAfterRemove;
        public int churnSurvivorsMissing;
        public int churnRemovedStillPresent;
        public int churnCountAfterReAdd;
        public int churnMissingAfterReAdd;

        // ---- CEWorld system probes (driven from .NET, see the system-index test) ---------------

        /// <summary>One letter per system invocation, in the order CEWorld.Tick made them.</summary>
        public string systemLog = "";

        /// <summary>
        /// Adds KeyCount keys through repeated growth, then looks every one of them up again.
        /// </summary>
        public void RunGrowth()
        {
            CEDictionary<int, int> dictionary = new CEDictionary<int, int>(InitialCapacity);

            for (int i = 0; i < KeyCount; i++)
            {
                // Keys are spread out rather than 0..n so that a key never coincides with its own
                // insertion order or with its slot index.
                dictionary.Add(i * 7 + 3, i);
            }

            int missing = 0;
            int wrong = 0;
            int firstMissing = -1;

            for (int i = 0; i < KeyCount; i++)
            {
                int key = i * 7 + 3;

                if (dictionary.TryGetValue(key, out int value))
                {
                    if (value != i)
                    {
                        wrong++;
                    }
                }
                else
                {
                    missing++;

                    if (firstMissing < 0)
                    {
                        firstMissing = key;
                    }
                }
            }

            growthCount = dictionary.Count;
            growthMissingKeys = missing;
            growthWrongValues = wrong;
            growthFirstMissingKey = firstMissing;
            growthRan = true;
        }

        /// <summary>
        /// Fills the dictionary, removes half of it (enough removals to cross the tombstone threshold,
        /// so the table is rebuilt in place part-way through), checks what is left, then puts the
        /// removed half back.
        /// </summary>
        public void RunChurn()
        {
            CEDictionary<int, int> dictionary = new CEDictionary<int, int>(InitialCapacity);

            for (int i = 0; i < KeyCount; i++)
            {
                dictionary.Add(i, i * 2);
            }

            for (int i = 0; i < KeyCount; i += 2)
            {
                dictionary.Remove(i);
            }

            int survivorsMissing = 0;
            int removedStillPresent = 0;

            for (int i = 0; i < KeyCount; i++)
            {
                bool expectPresent = (i % 2) == 1;

                if (dictionary.TryGetValue(i, out int value))
                {
                    if (!expectPresent)
                    {
                        removedStillPresent++;
                    }
                    else if (value != i * 2)
                    {
                        survivorsMissing++;
                    }
                }
                else if (expectPresent)
                {
                    survivorsMissing++;
                }
            }

            churnCountAfterRemove = dictionary.Count;
            churnSurvivorsMissing = survivorsMissing;
            churnRemovedStillPresent = removedStillPresent;

            for (int i = 0; i < KeyCount; i += 2)
            {
                dictionary.Add(i, i * 2);
            }

            int missingAfterReAdd = 0;

            for (int i = 0; i < KeyCount; i++)
            {
                if (!dictionary.ContainsKey(i))
                {
                    missingAfterReAdd++;
                }
            }

            churnCountAfterReAdd = dictionary.Count;
            churnMissingAfterReAdd = missingAfterReAdd;
            churnRan = true;
        }

        public void SystemEarly()
        {
            systemLog += "E";
        }

        public void SystemLate()
        {
            systemLog += "L";
        }
    }
    public class AuditBox
    {
        public object value;
        public AuditBox(object value) { this.value = value; }
    }
    public static class AuditStatics
    {
        public static int A = 5;
        public static int B = A + 2;
    }
    public static class AuditStaticHelper
    {
        public static int Identity(int value) { return value; }
    }
    public static class AuditStaticTarget
    {
        public static int Seed = AuditStaticHelper.Identity(99);
        public static int Echo(int value) { return value; }
    }

    public enum AuditByteEnum : byte { Value = 1 }
    public enum AuditLongEnum : long { Value = 1 }

}
