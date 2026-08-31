#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Dobak.App.Map;
using Dobak.Manager;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TMPro;
using Dobak.App.Casino.SlotMachine;

namespace Dobak.Editor
{
    public static class ProjectQa
    {
        private const string MainScene = "Assets/Tablet/TabletUI.unity";

        public static void Validate()
        {
            var failures = new List<string>();

            ScenarioMessageTable scenario = ScenarioMessageTable.Load();
            if (scenario.Count("initial") < 1 || scenario.Count("daily_weekday") < 5 ||
                scenario.Count("spin_loss") < 4 || scenario.Count("round_10") < 2)
            {
                failures.Add("ScenarioMessages.csv does not contain enough playable event messages.");
            }

            if (SlotMachineManager.GetSessionWinBoost(4) != 0f ||
                SlotMachineManager.GetSessionWinBoost(5) <= 0f ||
                SlotMachineManager.GetSessionWinBoost(10) <= SlotMachineManager.GetSessionWinBoost(5))
            {
                failures.Add("Slot session win boost is not configured for rounds 5 and 10.");
            }

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
                "Assets/Tablet/Img/galaxy_message_screen_clean.png",
                "Assets/Resources/Map/2288553.png",
                "Assets/Resources/Map/Code_Generated_Image (1).png"
            };

            foreach (string assetPath in requiredArt)
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) == null)
                    failures.Add($"Required visual asset is missing: {assetPath}");
            }

            TMP_FontAsset koreanFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/Tablet/Front/NotoSansKR-Regular SDF.asset");
            if (koreanFont == null)
                failures.Add("Noto Sans KR font asset is missing.");
            else if (koreanFont.atlasPopulationMode != AtlasPopulationMode.Dynamic)
                failures.Add("Noto Sans KR must use a dynamic atlas to prevent missing Korean glyphs.");

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
