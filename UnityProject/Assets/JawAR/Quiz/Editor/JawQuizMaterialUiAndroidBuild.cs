using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BMC.JawAR.Quiz.Editor
{
    /// <summary>
    /// Builds the Material 3-inspired redesign as v33, using the exact same player-settings
    /// pipeline (package id, ARM64, IL2CPP, OpenGLES3 only, AutoRotation orientation, proxy
    /// URL/token handling) as the v32 compact-UI build — only the output path/version differ.
    /// </summary>
    public static class JawQuizMaterialUiAndroidBuild
    {
        public const string OutputPath =
            "/home/omar/JawRepair/JawSurfaceQuiz_BackboardProxy_v33_Material3UI.apk";
        public const string VersionName = "1.5.0-quiz-proxy-material3-ui";
        public const int VersionCode = 33;

        [MenuItem("Tools/Jaw Anatomy Quiz/Material 3/Build Material 3 UI APK")]
        public static void Build()
        {
            if (File.Exists(OutputPath))
                throw new IOException("Safety stop: output APK already exists and will not be overwritten: " + OutputPath);
            var proxyUrl = Environment.GetEnvironmentVariable("QUIZ_PROXY_URL") ??
                           JawQuizAndroidTestBuild.PhoneProxyDefaultUrl;
            var token = Environment.GetEnvironmentVariable("QUIZ_PROXY_TOKEN") ?? string.Empty;
            JawQuizAndroidTestBuild.ValidatePhoneProxyBuildConfiguration(proxyUrl, token);
            var method = typeof(JawQuizAndroidTestBuild).GetMethod("BuildInternal",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException("JawQuizAndroidTestBuild.BuildInternal");
            method.Invoke(null, new object[]
            {
                OutputPath, VersionName, VersionCode, proxyUrl, token,
                JawQuizAndroidTestBuild.PhoneProxyProductName
            });
        }

        public static void BuildAndExit()
        {
            try { Build(); }
            catch (TargetInvocationException exception)
            {
                Debug.LogException(exception.InnerException ?? exception);
                EditorApplication.Exit(1);
                return;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }
    }
}
