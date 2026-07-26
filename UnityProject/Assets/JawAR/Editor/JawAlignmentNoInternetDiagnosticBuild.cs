using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;

namespace BMC.JawAR.Editor
{
    /// <summary>Forces Unity's automatic INTERNET permission off around the isolated diagnostic build.</summary>
    public static class JawAlignmentNoInternetDiagnosticBuild
    {
        public const string QuizOutput = "/home/omar/JawRepair/JawAlignmentDiag_Quiz_NoNetwork_v31.apk";
        public const string GoodOutput = "/home/omar/JawRepair/JawAlignmentDiag_Good_NoNetwork_v18.apk";

        public static void BuildQuiz() => Invoke("Assets/Scenes/JawArUcoAnatomy_SurfaceQuiz_AR.unity",
            QuizOutput, "com.omar.jawsurfacequizalignmentdiag", "Jaw Quiz Alignment Diagnostic",
            "quiz", false, 31);

        public static void BuildGood() => Invoke("Assets/Scenes/JawArUcoAnatomy_SurfacePaint_AR.unity",
            GoodOutput, "com.omar.jawgoodalignmentdiag", "Jaw Good Alignment Diagnostic",
            "good", true, 18);

        private static void Invoke(string scene, string output, string package, string product,
            string label, bool goodProfile, int versionCode)
        {
            bool previous = PlayerSettings.Android.forceInternetPermission;
            try
            {
                PlayerSettings.Android.forceInternetPermission = false;
                var method = typeof(JawAlignmentDiagnosticBuild).GetMethod("Build",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) throw new MissingMethodException("Diagnostic build entry point not found.");
                method.Invoke(null, new object[] { scene, output, package, product, label, goodProfile, versionCode });
            }
            finally
            {
                PlayerSettings.Android.forceInternetPermission = previous;
                AssetDatabase.SaveAssets();
            }
        }
    }
}
