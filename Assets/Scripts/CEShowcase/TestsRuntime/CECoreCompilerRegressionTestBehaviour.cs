using System.Collections.Generic;
using UdonSharp;
using UnityEngine;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// Fixture for CECoreCompilerRegressionTests: core compiler and collection defects that only exist in the
    /// Udon VM, where a method has one set of parameter slots and a call's result lives in a shared return slot.
    ///
    /// RunCoreAudit records each failed check by name in auditFailures, and the name of the check it is on in
    /// lastCheck, so a halted VM can be traced to the check that halted it. The test also reads _serialQueue and
    /// _serialStack back off the heap through the editor serializers.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CECoreCompilerRegressionTestBehaviour : UdonSharpBehaviour
    {
        public bool auditRan;
        public string auditFailures = "";
        public string lastCheck = "";

        private Queue<int> _serialQueue;
        private Stack<int> _serialStack;
        private Dictionary<string, int> _pendingDictionary = new Dictionary<string, int> { { "one", 1 }, { "two", 2 } };

        public void RunCoreAudit()
        {
            auditFailures = "";

            // A static initializer whose method nothing else calls must still be emitted.
            lastCheck = "static-initializer-dependency";
            if (CoreAuditStaticDependency.Value != 21) auditFailures += "static-initializer-dependency;";

            // A recursive call whose receiver is the caller's own parameter must run on that receiver, not on the
            // argument that overwrote the parameter slot.
            lastCheck = "recursive-receiver";
            CoreAuditNode first = new CoreAuditNode(1);
            CoreAuditNode second = new CoreAuditNode(10);
            CoreAuditNode third = new CoreAuditNode(100);
            first.next = second;
            second.next = third;
            if (first.SumFrom(second) != 111) auditFailures += "recursive-receiver;";

            // Nested enumeration needs an enumerator per loop: one Stack twice, and two different HashSets.
            lastCheck = "nested-stack";
            Stack<int> stack = new Stack<int>();
            stack.Push(1);
            stack.Push(2);
            stack.Push(3);
            int pairs = 0;
            foreach (int outer in stack)
                foreach (int inner in stack)
                    pairs++;
            if (pairs != 9) auditFailures += "nested-stack;";

            lastCheck = "nested-hashset";
            HashSet<int> left = new HashSet<int>();
            left.Add(1);
            left.Add(2);
            left.Add(3);
            HashSet<int> right = new HashSet<int>();
            right.Add(4);
            right.Add(5);
            pairs = 0;
            foreach (int outer in left)
                foreach (int inner in right)
                    pairs++;
            if (pairs != 6) auditFailures += "nested-hashset;";

            // Remove(key, out value) on a dictionary that still holds its serialized initializer.
            lastCheck = "pending-remove";
            if (!_pendingDictionary.Remove("one", out int removed) || removed != 1) auditFailures += "pending-remove;";
            if (_pendingDictionary.Count != 1 || _pendingDictionary.ContainsKey("one") || !_pendingDictionary.ContainsKey("two"))
                auditFailures += "pending-remove-state;";

            // 'as' has to test the runtime type: null when it does not match, the value when it does.
            lastCheck = "as";
            // The operands here are deliberately not UnityEngine.Objects. Udon passes such an argument
            // through its object security filter, whose first use calls DontDestroyOnLoad -- fine in a
            // running world, an exception in an edit-mode test -- so a Transform would halt this fixture
            // for a reason that has nothing to do with 'as'. The extern and the lowering are the same.
            object boxedText = "a string";
            string asString = boxedText as string;
            if (asString == null || asString.Length != 8) auditFailures += "as-match;";
            object boxedNumber = 42;
            string notString = boxedNumber as string;
            if (notString != null) auditFailures += "as-mismatch;";
            object boxedInts = new int[2];
            int[] asInts = boxedInts as int[];
            float[] asFloats = boxedInts as float[];
            if (asInts == null || asInts.Length != 2 || asFloats != null) auditFailures += "as-array;";

            lastCheck = "sizeof";
            int charSize = sizeof(char);
            if (charSize != 2) auditFailures += "sizeof-char;";

            // State the test reads back through QueueSerializer and StackSerializer.
            lastCheck = "serial-state";
            _serialQueue = new Queue<int>();
            _serialQueue.Enqueue(1);
            _serialQueue.Enqueue(2);
            _serialQueue.Enqueue(3);
            _serialQueue.Dequeue();
            _serialQueue.Enqueue(4);
            _serialStack = new Stack<int>();
            _serialStack.Push(1);
            _serialStack.Push(2);
            _serialStack.Push(3);

            lastCheck = "done";
            auditRan = true;
        }
    }

    public static class CoreAuditStaticDependency
    {
        public static int Value = CoreAuditStaticDependencyHelper.Compute(20);
    }

    public static class CoreAuditStaticDependencyHelper
    {
        // More than one statement, so the check above still needs a real call if a method inliner is ever enabled.
        public static int Compute(int value)
        {
            int result = value;
            result += 1;
            return result;
        }
    }

    public class CoreAuditNode
    {
        public int value;
        public CoreAuditNode next;

        public CoreAuditNode(int value)
        {
            this.value = value;
        }

        [RecursiveMethod]
        public int SumFrom(CoreAuditNode other)
        {
            if (other == null)
                return value;

            return value + other.SumFrom(other.next);
        }
    }
}
