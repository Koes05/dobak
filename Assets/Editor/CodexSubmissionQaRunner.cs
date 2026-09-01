#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class CodexSubmissionQaRunner
{
    private const string MarkerName = ".codex-run-submission-qa";
    private static bool restartQueued;

    static CodexSubmissionQaRunner()
    {
        EditorApplication.update -= TryRun;
        EditorApplication.update += TryRun;
    }

    private static void TryRun()
    {
        string marker = Path.GetFullPath(Path.Combine(Application.dataPath, "..", MarkerName));
        if (!File.Exists(marker) || EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        EditorApplication.update -= TryRun;
        string mode = File.ReadAllText(marker).Trim();
        File.Delete(marker);
        if (string.Equals(mode, "no-help", System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log("[CODEX SUBMISSION QA] Starting no-help route.");
            ScenarioV4FullPlayQa.RunNoHelp();
        }
        else if (string.Equals(mode, "final-remaining", System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log("[CODEX SUBMISSION QA] Starting final routes from SeojunDebt.");
            ScenarioV4FullPlayQa.RunRemainingFromSeojunDebt();
        }
        else if (string.Equals(mode, "remaining", System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log("[CODEX SUBMISSION QA] Starting remaining scenario routes from NoHelp.");
            ScenarioV4FullPlayQa.RunRemainingFromNoHelp();
        }
        else
        {
            Debug.Log("[CODEX SUBMISSION QA] Starting all scenario routes.");
            ScenarioV4FullPlayQa.RunAll();
        }
    }

    [MenuItem("Tools/Codex/Run Submission QA %#q")]
    private static void RunFromShortcut()
    {
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
        {
            restartQueued = true;
            EditorApplication.playModeStateChanged -= StartAfterEditMode;
            EditorApplication.playModeStateChanged += StartAfterEditMode;
            ScenarioV4FullPlayQa.AbortForRestart();
            return;
        }

        Debug.Log("[CODEX SUBMISSION QA] Starting all scenario routes from shortcut.");
        ScenarioV4FullPlayQa.RunAll();
    }

    [MenuItem("Tools/Codex/Run Final Routes %#e")]
    private static void RunFinalRoutesFromShortcut()
    {
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        Debug.Log("[CODEX SUBMISSION QA] Starting final routes from SeojunDebt shortcut.");
        ScenarioV4FullPlayQa.RunRemainingFromSeojunDebt();
    }

    [MenuItem("Tools/Codex/Run No-Help Route %#n")]
    private static void RunNoHelpFromShortcut()
    {
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        Debug.Log("[CODEX SUBMISSION QA] Starting no-help route from shortcut.");
        ScenarioV4FullPlayQa.RunNoHelp();
    }

    private static void StartAfterEditMode(PlayModeStateChange state)
    {
        if (!restartQueued || state != PlayModeStateChange.EnteredEditMode)
            return;

        restartQueued = false;
        EditorApplication.playModeStateChanged -= StartAfterEditMode;
        EditorApplication.delayCall += RunFromShortcut;
    }
}
#endif
