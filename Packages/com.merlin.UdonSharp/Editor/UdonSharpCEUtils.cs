using System;
using System.Reflection;
using UnityEngine;

namespace UdonSharp
{
    /// <summary>
    /// Console logging helpers for messages originating from UdonSharpCE rather than upstream UdonSharp.
    /// Mirrors <see cref="UdonSharpUtils"/> logging, but tags messages with [UdonSharpCE] so users can tell
    /// fork-specific diagnostics apart from upstream ones.
    /// </summary>
    public static class UdonSharpCEUtils
    {
        private const string LogPrefix = "[<color=#0c824c>UdonSharpCE</color>]";
        private const string AlertPrefix = "[<color=#FF00FF>UdonSharpCE</color>]";

        public static void Log(object message)
        {
            Debug.Log($"{LogPrefix} {message}");
        }

        public static void Log(object message, UnityEngine.Object context)
        {
            Debug.Log($"{LogPrefix} {message}", context);
        }

        public static void LogWarning(object message)
        {
            Debug.LogWarning($"{AlertPrefix} {message}");
        }

        public static void LogWarning(object message, UnityEngine.Object context)
        {
            Debug.LogWarning($"{AlertPrefix} {message}", context);
        }

        public static void LogError(object message)
        {
            Debug.LogError($"{AlertPrefix} {message}");
        }

        public static void LogError(object message, UnityEngine.Object context)
        {
            Debug.LogError($"{AlertPrefix} {message}", context);
        }

        public static void LogBuildError(string message, string filePath, int line, int character)
        {
            MethodInfo buildErrorLogMethod = typeof(Debug).GetMethod("LogPlayerBuildError", BindingFlags.NonPublic | BindingFlags.Static);

            string errorMessage = $"{AlertPrefix} {filePath}({line},{character}): {message}";

            buildErrorLogMethod.Invoke(null, new object[] {
                        errorMessage,
                        filePath,
                        line,
                        character });
        }

        /// <summary>
        /// True when a compiler diagnostic message carries a UdonSharpCE diagnostic code rather than an
        /// upstream UdonSharp one, so the console entry can be tagged [UdonSharpCE] instead of [UdonSharp].
        /// CE compiler paths emit "USCE####: ..." and the CE analyzers emit "[CE####] ...".
        /// </summary>
        public static bool IsCEDiagnostic(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            if (message.StartsWith("USCE", StringComparison.Ordinal))
                return HasDigitsThen(message, 4, 4, ':');

            if (message.StartsWith("[CE", StringComparison.Ordinal))
                return HasDigitsThen(message, 3, 4, ']');

            return false;
        }

        private static bool HasDigitsThen(string message, int start, int digitCount, char terminator)
        {
            if (message.Length < start + digitCount + 1)
                return false;

            for (int i = start; i < start + digitCount; ++i)
            {
                if (!char.IsDigit(message[i]))
                    return false;
            }

            return message[start + digitCount] == terminator;
        }
    }
}
