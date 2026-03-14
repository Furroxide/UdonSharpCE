
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using UdonSharp.CE.Data;

namespace UdonSharp.Tests
{
    [AddComponentMenu("Udon Sharp/Tests/LambdaInliningTest")]
    public class LambdaInliningTest : UdonSharpBehaviour
    {
        [System.NonSerialized]
        public IntegrationTestSuite tester;

        private int _untilCount;

        public void ExecuteTests()
        {
            ImmediateInvocation();
            CEListForEach();
            CEListForEachWithIndex();
            CEListForEachUntil();
        }

        private void ImmediateInvocation()
        {
            int value = 0;

            (() =>
            {
                value = 5;
            })();

            tester.TestAssertion("Lambda IIFE inlined", value == 5);
        }

        private void CEListForEach()
        {
            CEList<int> list = new CEList<int>(4);
            list.Add(1);
            list.Add(2);
            list.Add(3);

            int sum = 0;
            list.ForEach(x => sum += x);

            tester.TestAssertion("CEList.ForEach lambda", sum == 6);
        }

        private void CEListForEachWithIndex()
        {
            CEList<int> list = new CEList<int>(4);
            list.Add(1);
            list.Add(2);
            list.Add(3);

            int weighted = 0;
            list.ForEachWithIndex((x, i) => weighted += x * (i + 1));

            tester.TestAssertion("CEList.ForEachWithIndex lambda", weighted == 14);
        }

        private void CEListForEachUntil()
        {
            CEList<int> list = new CEList<int>(4);
            list.Add(1);
            list.Add(2);
            list.Add(3);
            list.Add(4);

            _untilCount = 0;
            list.ForEachUntil(x => CountUntil(x));

            tester.TestAssertion("CEList.ForEachUntil lambda", _untilCount == 3);
        }

        private bool CountUntil(int value)
        {
            _untilCount++;
            return value < 3;
        }
    }
}
