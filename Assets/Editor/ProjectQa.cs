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
            if (scenario.EventCount < 75 || scenario.Count("initial_invitation") < 1 ||
                scenario.Count("invitation_retempt") < 3 || scenario.Count("sns_intro") < 3 ||
                scenario.Count("sns_gambling_feed") < 3 || scenario.Count("ending_cashout") < 1 ||
                scenario.Count("debt_repay_complete") < 1 || scenario.Count("ending_recovery") < 1 ||
                scenario.Count("ending_abstinence") < 1)
            {
                failures.Add("ScenarioMessages.csv does not contain enough playable event messages.");
            }

            var csvActionTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var replyRequiredEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "initial_invitation", "invitation_detail", "family_dinner_start", "weekend_shift_start",
                "joonho_casual", "seoyeon_homework", "seoyeon_reply_together", "mom_checkin", "short_sleep", "job_late", "miss_job",
                "gamble_round_1", "gamble_round_5", "gamble_round_10", "borrow_mom_request",
                "borrow_friend_request", "help_available", "sns_mom_homework", "sns_job_warning", "sns_late_mom"
            };
            foreach (ScenarioEventDefinition definition in scenario.Events)
            {
                if (replyRequiredEvents.Contains(definition.id))
                {
                    bool hasReply = definition.steps.Exists(step =>
                        !string.IsNullOrWhiteSpace(step.choiceA) && !string.IsNullOrWhiteSpace(step.actionA));
                    if (!hasReply)
                        failures.Add($"Conversational CSV event has no player reply: {definition.id}");
                    replyRequiredEvents.Remove(definition.id);
                }

                foreach (ScenarioMessage step in definition.steps)
                {
                    AddScenarioActionTarget(csvActionTargets, step.actionA);
                    AddScenarioActionTarget(csvActionTargets, step.actionB);
                }
            }
            foreach (string missingReplyEvent in replyRequiredEvents)
                failures.Add($"Required conversational CSV event is missing: {missingReplyEvent}");

            string flowSource = File.ReadAllText("Assets/Tablet/Script/GameFlowManager.cs");
            foreach (string trigger in scenario.Triggers)
            {
                if (!flowSource.Contains($"TriggerScenario(\"{trigger}\"", StringComparison.Ordinal) &&
                    !csvActionTargets.Contains(trigger))
                    failures.Add($"CSV trigger is not connected to the game flow: {trigger}");
            }

            foreach (ScenarioEventDefinition definition in scenario.Events)
            {
                foreach (ScenarioMessage step in definition.steps)
                {
                    ValidateScenarioAction(failures, scenario, definition.id, step.actionA);
                    ValidateScenarioAction(failures, scenario, definition.id, step.actionB);
                }
            }

            TextAsset scenarioAsset = Resources.Load<TextAsset>("ScenarioMessages");
            if (scenarioAsset != null && scenarioAsset.text.Contains("학원"))
                failures.Add("ScenarioMessages.csv still contains an academy event, but the game only uses school, home, and cafe work.");
            if (scenarioAsset != null && (scenarioAsset.text.Contains(",Stranger,") ||
                                          scenarioAsset.text.Contains("사진으로 보내자")))
                failures.Add("ScenarioMessages.csv still contains an unexplained stranger contact or an unsupported photo-sharing promise.");
            if (scenario.Count("loan_spam") > 0 || scenario.Count("debt_followup") > 0 ||
                scenario.Count("gamble_win") > 0 || scenario.Count("gamble_loss") > 0)
                failures.Add("ScenarioMessages.csv still contains high-frequency site or spam-contact events.");

            ValidateFixedScenario(failures, scenario, "seoyeon_homework", "day=3");
            ValidateFixedScenario(failures, scenario, "mom_checkin", "day=4");
            ValidateFixedScenario(failures, scenario, "joonho_casual", "day=5");
            ValidateFixedScenario(failures, scenario, "weekend_shift_start", "day=6");
            ValidateFixedScenario(failures, scenario, "family_dinner_start", "day=7");
            ValidateFixedScenario(failures, scenario, "school_project_start", "day=8");
            ValidateFixedScenario(failures, scenario, "teacher_day10_steady", "day=10");
            ValidateFixedScenario(failures, scenario, "weekend_shift_second", "day=12");

            ScenarioEventDefinition retempt = FindScenario(scenario, "site_retempt");
            if (retempt == null || !retempt.condition.Contains("days_without_gambling>=2", StringComparison.Ordinal) ||
                retempt.chance < 1f || !string.Equals(retempt.once, "game", StringComparison.OrdinalIgnoreCase))
                failures.Add("Site re-engagement must be a one-time event gated by actual gambling inactivity.");

            if (SlotMachineManager.GetSessionWinBoost(4) != 0f ||
                SlotMachineManager.GetSessionWinBoost(5) <= 0f ||
                SlotMachineManager.GetSessionWinBoost(10) <= SlotMachineManager.GetSessionWinBoost(5))
            {
                failures.Add("Slot session win boost is not configured for rounds 5 and 10.");
            }

            if (GameFlowManager.GetSleepHoursUntilSeven(23) != 8 ||
                GameFlowManager.GetSleepHoursUntilSeven(2) != 5 ||
                GameFlowManager.GetSleepHoursUntilSeven(4) != 3)
            {
                failures.Add("Sleep duration must be calculated from the current hour until 7 AM.");
            }

            if (GameFlowManager.GetHoursUntilDayBoundary(23) != 8 ||
                GameFlowManager.GetHoursUntilDayBoundary(2) != 5 ||
                GameFlowManager.GetHoursUntilDayBoundary(7) != 24)
            {
                failures.Add("A game day must advance only when the clock reaches 7 AM.");
            }

            if (CoinManager.ConvertWonToPoints(5000) != 500 ||
                CoinManager.ConvertWonToPoints(10000) != 1000 ||
                CoinManager.ConvertWonToPoints(50000) != 5000 ||
                CoinManager.ConvertWonToPoints(100000) != 10000)
            {
                failures.Add("Casino charge conversion must use 10,000 won = 1,000P.");
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
                ,"Assets/Resources/SNS/sns_icon.png"
                ,"Assets/Resources/TestAssets/Gemini_Generated_Image_39o0xn39o0xn39o0.png"
                ,"Assets/Resources/TestAssets/Gemini_Generated_Image_iob66iiob66iiob6.png"
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

        private static void ValidateScenarioAction(List<string> failures, ScenarioMessageTable scenario,
            string eventId, string action)
        {
            const string prefix = "trigger:";
            if (string.IsNullOrWhiteSpace(action) || !action.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return;

            string target = ReadTriggerTarget(action);
            if (!scenario.HasTrigger(target))
                failures.Add($"CSV event {eventId} points to missing reply trigger: {target}");
        }

        private static void AddScenarioActionTarget(HashSet<string> targets, string action)
        {
            const string prefix = "trigger:";
            if (!string.IsNullOrWhiteSpace(action) && action.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                targets.Add(ReadTriggerTarget(action));
        }

        private static string ReadTriggerTarget(string action)
        {
            const string prefix = "trigger:";
            string target = action.Substring(prefix.Length).Trim();
            int separator = target.IndexOf('|');
            return separator >= 0 ? target.Substring(0, separator).Trim() : target;
        }

        private static void ValidateFixedScenario(List<string> failures, ScenarioMessageTable scenario,
            string eventId, string requiredCondition)
        {
            ScenarioEventDefinition definition = FindScenario(scenario, eventId);
            if (definition == null || !definition.condition.Contains(requiredCondition, StringComparison.Ordinal) ||
                definition.chance < 1f || !string.Equals(definition.once, "game", StringComparison.OrdinalIgnoreCase))
                failures.Add($"Fixed-day CSV event is not deterministic: {eventId}");
        }

        private static ScenarioEventDefinition FindScenario(ScenarioMessageTable scenario, string eventId)
        {
            foreach (ScenarioEventDefinition definition in scenario.Events)
                if (string.Equals(definition.id, eventId, StringComparison.OrdinalIgnoreCase))
                    return definition;
            return null;
        }
    }
}
#endif
