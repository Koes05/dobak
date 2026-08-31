#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Dobak.App.Map;
using Dobak.Manager;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Dobak.Editor
{
    public static class ProjectQa
    {
        private const string MainScene = "Assets/Tablet/TabletUI.unity";

        public static void Validate()
        {
            var failures = new List<string>();

            if (!File.Exists(MainScene))
                failures.Add($"Main scene is missing: {MainScene}");

            bool buildSceneEnabled = false;
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && scene.path == MainScene)
                    buildSceneEnabled = true;
            }

            if (!buildSceneEnabled)
                failures.Add("TabletUI is not enabled in Build Settings.");

            var loadedScene = EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            if (!loadedScene.IsValid())
                failures.Add("TabletUI could not be opened.");

            ValidateSingle<QuizManager>(failures);
            ValidateSingle<DialogueManager>(failures);
            ValidateSingle<NotificationManager>(failures);
            ValidateSingle<AppWindow>(failures);
            ValidateSingle<MapLocationController>(failures);
            ValidateSingle<CoinManager>(failures);

            QuizManager quiz = UnityEngine.Object.FindAnyObjectByType<QuizManager>();
            if (quiz != null)
            {
                SerializedObject serializedQuiz = new SerializedObject(quiz);
                SerializedProperty quizzes = serializedQuiz.FindProperty("quizzes");
                SerializedProperty dailyCount = serializedQuiz.FindProperty("dailyQuestionCount");
                if (quizzes == null || quizzes.arraySize < 5)
                    failures.Add("Study app needs at least five quiz questions.");
                if (dailyCount == null || dailyCount.intValue != 5)
                    failures.Add("Study app daily question count must be five.");
            }

            string[] requiredArt =
            {
                "Assets/Tablet/Img/UnityQuizUIAssets/Backgrounds/BG_main.png",
                "Assets/Tablet/Img/UnityQuizUIAssets/Buttons/Answer_normal.png",
                "Assets/Tablet/Img/UnityQuizUIAssets/Icons/Icon_book.png",
                "Assets/Tablet/Img/galaxy_message_screen_clean.png"
            };

            foreach (string assetPath in requiredArt)
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) == null)
                    failures.Add($"Required visual asset is missing: {assetPath}");
            }

            if (failures.Count > 0)
            {
                foreach (string failure in failures)
                    Debug.LogError($"[PROJECT QA] {failure}");

                throw new InvalidOperationException($"Project QA failed with {failures.Count} issue(s).");
            }

            Debug.Log("[PROJECT QA] PASS - TabletUI, five-question study flow, core apps, and visual assets are valid.");
        }

        private static void ValidateSingle<T>(List<string> failures) where T : UnityEngine.Object
        {
            T[] found = UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include);
            if (found.Length != 1)
                failures.Add($"Expected exactly one {typeof(T).Name}, found {found.Length}.");
        }
    }
}
#endif
