using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps the gambling site's internal point arithmetic unchanged while presenting all player-facing
/// amounts as won. This is a display-only compatibility layer for legacy prefabs and messages.
/// </summary>
[DefaultExecutionOrder(20000)]
public sealed class ScenarioV3WonDisplayRuntimeFix : MonoBehaviour
{
    private const string TabletSceneName = "TabletUI";
    private const int WonPerPoint = 10;
    private readonly List<TMP_Text> texts = new List<TMP_Text>();
    private float nextRescanAt;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Register()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        CreateForCurrentScene();
    }

    private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        CreateForCurrentScene();
    }

    private static void CreateForCurrentScene()
    {
        if (!string.Equals(SceneManager.GetActiveScene().name, TabletSceneName, StringComparison.Ordinal))
            return;
        if (FindAnyObjectByType<ScenarioV3WonDisplayRuntimeFix>(FindObjectsInactive.Include) != null)
            return;
        new GameObject("Scenario V3 Won Display Runtime Fix V20.3")
            .AddComponent<ScenarioV3WonDisplayRuntimeFix>();
    }

    private void OnEnable()
    {
        Rescan();
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime >= nextRescanAt)
            Rescan();

        for (int i = texts.Count - 1; i >= 0; i--)
        {
            TMP_Text text = texts[i];
            if (text == null)
            {
                texts.RemoveAt(i);
                continue;
            }

            string current = text.text ?? string.Empty;
            string converted = ConvertVisibleText(current);
            if (!string.Equals(current, converted, StringComparison.Ordinal))
                text.text = converted;
        }
    }

    private void Rescan()
    {
        texts.Clear();
        foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text != null && text.gameObject.scene.IsValid())
                texts.Add(text);
        }
        nextRescanAt = Time.unscaledTime + 0.45f;
    }

    private static string ConvertVisibleText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        string result = value;

        // Legacy cash-out feedback contains both points and the already-converted won amount.
        result = Regex.Replace(result,
            @"[\d,]+\s*P가\s*([\d,]+)\s*원으로\s*정상\s*환전되었다\.",
            match => $"{match.Groups[1].Value}원이 통장으로 자동 환전됐다.");

        result = result
            .Replace("사이트 포인트 환전", "도박 앱 자동 환전")
            .Replace("사이트 포인트 충전", "도박 앱 충전")
            .Replace("포인트 환전", "도박 앱 자동 환전")
            .Replace("포인트 충전", "도박 앱 충전")
            .Replace("사이트 포인트", "도박 앱 잔액")
            .Replace("보유 포인트", "보유 금액")
            .Replace("추가 포인트", "추가 보너스")
            .Replace("신규 가입 포인트", "가입 보너스")
            .Replace("무료 포인트", "가입 보너스")
            .Replace("공짜 포인트", "가입 보너스")
            .Replace("추천 포인트", "추천 보너스")
            .Replace("20,000P", "20만 원")
            .Replace("2만P", "20만 원")
            .Replace("2만 포인트", "20만 원")
            .Replace("이만 포인트", "20만 원")
            .Replace("5,000P", "5만 원")
            .Replace("5천P", "5만 원")
            .Replace("5천 포인트", "5만 원")
            .Replace("오천 포인트", "5만 원");

        result = Regex.Replace(result, @"(?<![A-Za-z0-9가-힣])([\d,]+)\s*P(?![A-Za-z])", match =>
        {
            if (!int.TryParse(match.Groups[1].Value.Replace(",", string.Empty),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int points))
                return match.Value;
            long won = (long)points * WonPerPoint;
            return won.ToString("N0", CultureInfo.InvariantCulture) + "원";
        });

        result = Regex.Replace(result, @"(?<![A-Za-z0-9가-힣])([\d,]+)\s*포인트", match =>
        {
            if (!int.TryParse(match.Groups[1].Value.Replace(",", string.Empty),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int points))
                return match.Value;
            long won = (long)points * WonPerPoint;
            return won.ToString("N0", CultureInfo.InvariantCulture) + "원";
        });

        // Any remaining generic label is presentation language only; internal values are untouched.
        result = result.Replace("포인트", "금액");
        return result;
    }
}
