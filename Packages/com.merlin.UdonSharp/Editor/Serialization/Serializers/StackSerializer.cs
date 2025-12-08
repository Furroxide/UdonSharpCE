using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp.Lib.Internal;

namespace UdonSharp.Serialization
{
    internal class StackSerializer<T> : Serializer<T>
    {
        // Cache for reflection lookups to avoid repeated GetMethod calls
        private static readonly ConcurrentDictionary<Type, MethodInfo> _pushMethodCache = new ConcurrentDictionary<Type, MethodInfo>();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _toArrayMethodCache = new ConcurrentDictionary<Type, MethodInfo>();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _clearMethodCache = new ConcurrentDictionary<Type, MethodInfo>();
        
        public StackSerializer(TypeSerializationMetadata typeMetadata) : base(typeMetadata)
        {
        }
        
        private static MethodInfo GetPushMethod(Type stackType)
        {
            return _pushMethodCache.GetOrAdd(stackType, t => t.GetMethod("Push"));
        }
        
        private static MethodInfo GetToArrayMethod(Type stackType)
        {
            return _toArrayMethodCache.GetOrAdd(stackType, t => t.GetMethod("ToArray"));
        }
        
        private static MethodInfo GetClearMethod(Type stackType)
        {
            return _clearMethodCache.GetOrAdd(stackType, t => t.GetMethod("Clear"));
        }

        protected override Serializer MakeSerializer(TypeSerializationMetadata typeMetadata)
        {
            VerifyTypeCheckSanity();
            return (Serializer)Activator.CreateInstance(typeof(StackSerializer<>).MakeGenericType(typeMetadata.cSharpType), typeMetadata);
        }

        protected override bool HandlesTypeSerialization(TypeSerializationMetadata typeMetadata)
        {
            VerifyTypeCheckSanity();
            return UdonSharpUtils.IsStackType(typeMetadata.cSharpType);
        }

        public override Type GetUdonStorageType()
        {
            return typeof(object[]);
        }

        public override void Write(IValueStorage targetObject, in T sourceObject)
        {
            if (sourceObject == null)
            {
                targetObject.Value = null;
                return;
            }
            
            Type[] elementTypes = typeof(T).GetGenericArguments();
            Type uSharpStackType = typeof(Lib.Internal.Collections.Stack<>).MakeGenericType(elementTypes);
            Serializer serializer = CreatePooled(uSharpStackType);
            
            if (UsbSerializationContext.SerializedObjectMap.TryGetValue(sourceObject, out object serializedObject))
            {
                targetObject.Value = serializedObject;
                return;
            }
            
            object newUSharpList = Activator.CreateInstance(uSharpStackType);
            
            MethodInfo addMethod = GetPushMethod(uSharpStackType);
            
            // Use ICollection.Count instead of LINQ Cast+Count to avoid double enumeration
            int count = ((ICollection)sourceObject).Count;
            object[] reverseArray = new object[count];
            int index = reverseArray.Length - 1;
            foreach (object item in (IEnumerable)sourceObject)
            {
                reverseArray[index] = item;
                index--;
            }
            
            foreach (object item in reverseArray)
            {
                // ReSharper disable once PossibleNullReferenceException
                addMethod.Invoke(newUSharpList, new[] { item });
            }
            
            serializer.WriteWeak(targetObject, newUSharpList);
            
            UsbSerializationContext.SerializedObjectMap[sourceObject] = targetObject.Value;
        }

        public override void Read(ref T targetObject, IValueStorage sourceObject)
        {
            if (sourceObject.Value == null)
            {
                targetObject = default;
                return;
            }
            
            if (UsbSerializationContext.SerializedObjectMap.TryGetValue(sourceObject.Value, out object deserializedObject))
            {
                targetObject = (T)deserializedObject;
                return;
            }
            
            if (targetObject == null)
            {
                targetObject = (T)Activator.CreateInstance(typeof(Stack<>).MakeGenericType(typeof(T).GetGenericArguments()));
            }
            else
            {
                MethodInfo clearMethod = GetClearMethod(targetObject.GetType());
                clearMethod.Invoke(targetObject, null);
            }

            Type[] elementTypes = typeof(T).GetGenericArguments();
            Type uSharpStackType = typeof(Lib.Internal.Collections.Stack<>).MakeGenericType(elementTypes);
            Serializer serializer = CreatePooled(uSharpStackType);

            object newUSharpList = Activator.CreateInstance(uSharpStackType);
            serializer.ReadWeak(ref newUSharpList, sourceObject);

            MethodInfo toArrayMethod = GetToArrayMethod(uSharpStackType);
            // ReSharper disable once PossibleNullReferenceException
            Array listArray = (Array)toArrayMethod.Invoke(newUSharpList, null);
            
            MethodInfo addMethod = GetPushMethod(targetObject.GetType());
            
            // Use Array.Length instead of LINQ Cast+Count
            object[] reverseArray = new object[listArray.Length];
            int index = reverseArray.Length - 1;
            foreach (object item in (IEnumerable)listArray)
            {
                reverseArray[index] = item;
                index--;
            }
            
            foreach (object item in reverseArray)
            {
                // ReSharper disable once PossibleNullReferenceException
                addMethod.Invoke(targetObject, new[] { item });
            }
            
            UsbSerializationContext.SerializedObjectMap[sourceObject.Value] = targetObject;
        }
    }
}