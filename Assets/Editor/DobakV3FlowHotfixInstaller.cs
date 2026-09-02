#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Dobak V3 flow hotfix v13.
///
/// This editor-only installer applies narrow, source-validated fixes to the existing v12 project.
/// It never edits scenario CSV, scenes, dialogue, money amounts, choices, or ending conditions.
/// Every target file is validated in memory before any write, backed up under Library, and restored
/// automatically if a write fails.
/// </summary>
[InitializeOnLoad]
public static class DobakV3FlowHotfixInstaller
{
    private const string Version = "v13-flow-20260903";
    private const string MenuRoot = "Tools/Dobak/";
    private const string StatusFileName = "DobakV3Hotfix_v13_STATUS.txt";

    private static bool isRunning;
    private static bool autoApplyScheduled;

    private sealed class TextPatch
    {
        public readonly string Label;
        public readonly string Marker;
        public readonly string OldText;
        public readonly string NewText;

        public TextPatch(string label, string marker, string oldBase64, string newBase64)
        {
            Label = label;
            Marker = marker;
            OldText = Decode(oldBase64);
            NewText = Decode(newBase64);
        }
    }

    private sealed class FilePlan
    {
        public readonly string AssetPath;
        public readonly string ExpectedGitBlobSha;
        public readonly TextPatch[] Patches;

        public FilePlan(string assetPath, string expectedGitBlobSha, TextPatch[] patches)
        {
            AssetPath = assetPath;
            ExpectedGitBlobSha = expectedGitBlobSha;
            Patches = patches;
        }
    }

    private sealed class FileSnapshot
    {
        public FilePlan Plan;
        public string AbsolutePath;
        public byte[] OriginalBytes;
        public bool HadUtf8Bom;
        public string NewLine;
        public string NormalizedText;
        public string PatchedNormalizedText;
        public byte[] PatchedBytes;
        public string OriginalGitBlobSha;
    }

    private static readonly TextPatch[] DirectorPatches =
    {
            new TextPatch(
                "checkpoint-transient-reset",
                "DOBak V13-D01",
                "ICAgICAgICBkZWxpdmVyZWRPdXRnb2luZ0xpbmVJZHMuQ2xlYXIoKTsKICAgICAgICBkZWxpdmVyZWRJbmNvbWluZ0xpbmVJZHMuQ2xlYXIoKTsKICAgICAgICBwZW5kaW5nRGF5QWR2YW5jZSA9IGZhbHNlOwogICAgICAgIHJlYWN0aXZlVHJpZ2dlciA9IHN0cmluZy5FbXB0eTsKICAgICAgICBpbW1lZGlhdGVSb3V0ZSA9IHN0cmluZy5FbXB0eTs=",
                "ICAgICAgICBkZWxpdmVyZWRPdXRnb2luZ0xpbmVJZHMuQ2xlYXIoKTsKICAgICAgICBkZWxpdmVyZWRJbmNvbWluZ0xpbmVJZHMuQ2xlYXIoKTsKICAgICAgICBwZW5kaW5nRGF5QWR2YW5jZSA9IGZhbHNlOwogICAgICAgIC8vIERPQmFrIFYxMy1EMDE6IOuQmOqwkOq4sCDrkqQg65+w7YOA7J6EIOyghOyaqSDslYTsuagg7KCE7ZmYIO2UjOuemOq3uOqwgCDrr7jrnpgg67aE6riw66GcIOyDiOyngCDslYrqsowg7ZWc64ukLgogICAgICAgIHBlbmRpbmdMYXRlV2FrZUFmdGVyR2FtYmxpbmcgPSBmYWxzZTsKICAgICAgICBwZW5kaW5nQm9ycm93TW9ybmluZ0FkdmFuY2UgPSBmYWxzZTsKICAgICAgICByZWFjdGl2ZVRyaWdnZXIgPSBzdHJpbmcuRW1wdHk7CiAgICAgICAgaW1tZWRpYXRlUm91dGUgPSBzdHJpbmcuRW1wdHk7"),
            new TextPatch(
                "reset-gambled-late-state",
                "DOBak V13-D02",
                "ICAgICAgICBzdGF0ZVsiZmxhZy5sYXRlX3dha2VfdG9kYXkiXSA9ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImZsYWcuYm9ycm93X2RlZmVycmVkIl0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJib3Jyb3dlZC5tb20iXSA9ICJmYWxzZSI7",
                "ICAgICAgICBzdGF0ZVsiZmxhZy5sYXRlX3dha2VfdG9kYXkiXSA9ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImZsYWcuYm9ycm93X2RlZmVycmVkIl0gPSAiZmFsc2UiOwogICAgICAgIC8vIERPQmFrIFYxMy1EMDI6IOyDiCDqsozsnoTsnYAg7J207KCEIOuwpOyDmCDsg4Htg5zrpbwg7KCI64yAIOydtOyWtOuwm+yngCDslYrripTri6QuCiAgICAgICAgc3RhdGVbImZsYWcuZ2FtYmxlZF9sYXRlIl0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJib3Jyb3dlZC5tb20iXSA9ICJmYWxzZSI7"),
            new TextPatch(
                "preserve-queued-completion-across-route",
                "DOBak V13-D03",
                "ICAgICAgICBzY2VuZVF1ZXVlLkNsZWFyKCk7CiAgICAgICAgcXVldWVDb21wbGV0ZWQgPSBudWxsOwogICAgICAgIEJlZ2luU2NlbmUoc2NlbmUpOw==",
                "ICAgICAgICBzY2VuZVF1ZXVlLkNsZWFyKCk7CiAgICAgICAgLy8gRE9CYWsgVjEzLUQwMzog7KeB7KCRIOudvOyasO2MheuQnCDsnqXrqbTrj4Qg7ZiE7J6sIO2KuOumrOqxsCDssrTsnbjsnZgg7JmE66OMIOy9nOuwseydhCDrs7TsobTtlZzri6QuCiAgICAgICAgLy8g7IOIIOqyjOyehC/rkJjqsJDquLAv6rCV7KCcIOuCoOynnCDsoITtmZjsspjrn7wg7LK07J247J2EIO2PkOq4sO2VtOyVvCDtlZjripQg6rK966Gc64qUIO2YuOy2nOu2gOyXkOyEnCDrqoXsi5zsoIHsnLzroZwg7LSI6riw7ZmU7ZWc64ukLgogICAgICAgIEJlZ2luU2NlbmUoc2NlbmUpOw=="),
            new TextPatch(
                "return-to-tablet-preserves-followup",
                "DOBak V13-D04",
                "ICAgICAgICBpZiAocmV0dXJuVG9UYWJsZXQgJiYgIWhhc1F1ZXVlZFNjZW5lKQogICAgICAgIHsKICAgICAgICAgICAgSGlkZU5vdmVsKCk7CiAgICAgICAgICAgIHNjZW5lUXVldWUuQ2xlYXIoKTsKICAgICAgICAgICAgcXVldWVDb21wbGV0ZWQgPSBudWxsOwogICAgICAgICAgICB3YWl0aW5nRm9yTWVzc2FnZUNob2ljZSA9IGZhbHNlOwogICAgICAgICAgICB3YWl0aW5nTWVzc2FnZVNwZWFrZXIgPSBTcGVha2VyVHlwZS5Vbmtub3duOwogICAgICAgICAgICB3YWl0aW5nTWVzc2FnZVNjZW5lID0gbnVsbDsKICAgICAgICAgICAgd2FpdGluZ01lc3NhZ2VMaW5lSW5kZXggPSAtMTsKICAgICAgICAgICAgYXBwV2luZG93Py5DbG9zZUN1cnJlbnRBcHAoKTsKICAgICAgICAgICAgaWYgKCFUcnlRdWV1ZUV2ZW5pbmdGaWxsKCkpCiAgICAgICAgICAgICAgICBUcnlRdWV1ZUJlZHRpbWVDdWUoKTsKICAgICAgICAgICAgcmV0dXJuOwogICAgICAgIH0=",
                "ICAgICAgICBpZiAocmV0dXJuVG9UYWJsZXQgJiYgIWhhc1F1ZXVlZFNjZW5lKQogICAgICAgIHsKICAgICAgICAgICAgSGlkZU5vdmVsKCk7CiAgICAgICAgICAgIHdhaXRpbmdGb3JNZXNzYWdlQ2hvaWNlID0gZmFsc2U7CiAgICAgICAgICAgIHdhaXRpbmdNZXNzYWdlU3BlYWtlciA9IFNwZWFrZXJUeXBlLlVua25vd247CiAgICAgICAgICAgIHdhaXRpbmdNZXNzYWdlU2NlbmUgPSBudWxsOwogICAgICAgICAgICB3YWl0aW5nTWVzc2FnZUxpbmVJbmRleCA9IC0xOwogICAgICAgICAgICBhcHBXaW5kb3c/LkNsb3NlQ3VycmVudEFwcCgpOwoKICAgICAgICAgICAgLy8gRE9CYWsgVjEzLUQwNDog7YOc67iU66a/IOuzteq3gCDsnqXrqbTsnbQgZGF5X3N0YXJ0IOuSpOydmCDssKjsmqkg7JWI64K0IOqwmeydgCDsmIjslb0g7J6R7JeF7J2EIOyngOyasOyngCDslYrqsowg7ZWc64ukLgogICAgICAgICAgICAvLyDrqLzsoIAg64Ko7J2AIOyZhOujjCDsvZzrsLHsnYQg7IaM7KeE7ZWY6rOgLCDqt7gg7L2c67Cx7J20IOyDiCDsnqXrqbQv66mU7Iuc7KeA66W8IOyLnOyeke2WiOuLpOuptCDsoIDrhYHCt+y3qOy5qCDsnpDrj5kg7KeE7ZaJ7J2EIOunieuKlOuLpC4KICAgICAgICAgICAgU3RhcnRRdWV1ZWRTY2VuZSgpOwogICAgICAgICAgICBpZiAoYWN0aXZlU2NlbmUgIT0gbnVsbCB8fCBzY2VuZVF1ZXVlLkNvdW50ID4gMCB8fCBxdWV1ZUNvbXBsZXRlZCAhPSBudWxsIHx8CiAgICAgICAgICAgICAgICB3YWl0aW5nRm9yTWVzc2FnZUNob2ljZSB8fCBwZW5kaW5nT3V0Z29pbmdMaW5lICE9IG51bGwgfHwgd2FpdGluZ0Zvck1lc3NhZ2VTY2VuZUNsb3NlIHx8CiAgICAgICAgICAgICAgICBzY2VuZVRyYW5zaXRpb25JblByb2dyZXNzKQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICByZXR1cm47CiAgICAgICAgICAgIH0KCiAgICAgICAgICAgIGlmICghVHJ5UXVldWVFdmVuaW5nRmlsbCgpKQogICAgICAgICAgICAgICAgVHJ5UXVldWVCZWR0aW1lQ3VlKCk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9"),
            new TextPatch(
                "separate-borrow-deferral-from-late-wake",
                "DOBak V13-D05",
                "ICAgICAgICBib29sIHdva2VGcm9tR2FtYmxpbmcgPSBwZW5kaW5nTGF0ZVdha2VBZnRlckdhbWJsaW5nICYmICFleHBsaWNpdEJvcnJvd0RlZmVycmFsOw==",
                "ICAgICAgICAvLyBET0JhayBWMTMtRDA1OiDssKjsmqkg7Jew65297J2EIOyVhOy5qOycvOuhnCDrr7jro6wg6rKD6rO8IOyLpOygnCDrj4TrsJUg67Ck7IOY7J2EIOu2hOumrO2VnOuLpC4KICAgICAgICBib29sIHdva2VGcm9tR2FtYmxpbmcgPSBwZW5kaW5nTGF0ZVdha2VBZnRlckdhbWJsaW5nOw=="),
            new TextPatch(
                "conditional-morning-clock-and-flags",
                "DOBak V13-D06",
                "ICAgICAgICBzdGF0ZVsicGVuZGluZy5ib3Jyb3dfbWVudSJdID0gc2hvd0JvcnJvd01lbnUgPyAidHJ1ZSIgOiAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJmbGFnLmxhdGVfd2FrZV90b2RheSJdID0gInRydWUiOwogICAgICAgIHN0YXRlWyJmbGFnLmJvcnJvd19kZWZlcnJlZCJdID0gZXhwbGljaXRCb3Jyb3dEZWZlcnJhbCA/ICJ0cnVlIiA6ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImZsYWcuZ2FtYmxlZF9sYXRlIl0gPSB3b2tlRnJvbUdhbWJsaW5nID8gInRydWUiIDogImZhbHNlIjsKICAgICAgICBzdGF0ZVsiZGF5X2Nhc2hfc3RhcnQiXSA9IGZsb3cuVjNCYW5rQ2FzaC5Ub1N0cmluZyhDdWx0dXJlSW5mby5JbnZhcmlhbnRDdWx0dXJlKTsKICAgICAgICBmbG93LlYzU2V0TG9jYXRpb24oIuynkSIpOwogICAgICAgIGZsb3cuVjNTZXRDbG9jaygiMTA6MDAiKTs=",
                "ICAgICAgICBzdGF0ZVsicGVuZGluZy5ib3Jyb3dfbWVudSJdID0gc2hvd0JvcnJvd01lbnUgPyAidHJ1ZSIgOiAiZmFsc2UiOwogICAgICAgIC8vIERPQmFrIFYxMy1EMDY6IOyLpOygnCDrsKTsg5jrp4wgMTDsi5wg64qm7J6g7Jy866GcIOyymOumrO2VnOuLpC4g7LCo7JqpIOyYiOyVveunjCDsnojsnLzrqbQgN+yLnOyXkCDsoJXsg4Eg6riw7IOB7ZWc64ukLgogICAgICAgIHN0YXRlWyJmbGFnLmxhdGVfd2FrZV90b2RheSJdID0gd29rZUZyb21HYW1ibGluZyA/ICJ0cnVlIiA6ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImZsYWcuYm9ycm93X2RlZmVycmVkIl0gPSB3b2tlRnJvbUdhbWJsaW5nICYmIGV4cGxpY2l0Qm9ycm93RGVmZXJyYWwgPyAidHJ1ZSIgOiAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJmbGFnLmdhbWJsZWRfbGF0ZSJdID0gd29rZUZyb21HYW1ibGluZyA/ICJ0cnVlIiA6ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImRheV9jYXNoX3N0YXJ0Il0gPSBmbG93LlYzQmFua0Nhc2guVG9TdHJpbmcoQ3VsdHVyZUluZm8uSW52YXJpYW50Q3VsdHVyZSk7CiAgICAgICAgZmxvdy5WM1NldExvY2F0aW9uKCLsp5EiKTsKICAgICAgICBmbG93LlYzU2V0Q2xvY2sod29rZUZyb21HYW1ibGluZyA/ICIxMDowMCIgOiAiMDc6MDAiKTs="),
            new TextPatch(
                "clear-special-morning-flags-before-borrow-cue",
                "DOBak V13-D07",
                "ICAgICAgICAgICAgLy8g7Yq57IiYIOq4sOyDgSDsl7Dstpzqs7wg6re4IOuCoOydmCDrtoDqsIAg7J2067Kk7Yq46rCAIOuqqOuRkCDrgZ3rgpwg65Kk7JeQ66eMIOywqOyaqeydhCDsnbTslrQg6rCE64ukLgogICAgICAgICAgICBTZXRTdGF0ZSgiZmxhZy5sYXRlX3dha2VfdG9kYXkiLCAiZmFsc2UiKTsKICAgICAgICAgICAgaWYgKEdldFN0YXRlKCJwZW5kaW5nLmJvcnJvd19tZW51IikgIT0gInRydWUiKQ==",
                "ICAgICAgICAgICAgLy8g7Yq57IiYIOq4sOyDgSDsl7Dstpzqs7wg6re4IOuCoOydmCDrtoDqsIAg7J2067Kk7Yq46rCAIOuqqOuRkCDrgZ3rgpwg65Kk7JeQ66eMIOywqOyaqeydhCDsnbTslrQg6rCE64ukLgogICAgICAgICAgICBTZXRTdGF0ZSgiZmxhZy5sYXRlX3dha2VfdG9kYXkiLCAiZmFsc2UiKTsKICAgICAgICAgICAgLy8gRE9CYWsgVjEzLUQwNzog64u57J28IOyVhOy5qCDsnqXrqbQg7ISg7YOd7J20IOuBneuCnCDrkqQg7J6E7IucIO2UjOuemOq3uOulvCDsoJXrpqztlbQg64uk7J2MIOuCoCDsnqXrqbTsl5Ag64Ko6riw7KeAIOyViuuKlOuLpC4KICAgICAgICAgICAgU2V0U3RhdGUoImZsYWcuZ2FtYmxlZF9sYXRlIiwgImZhbHNlIik7CiAgICAgICAgICAgIFNldFN0YXRlKCJmbGFnLmJvcnJvd19kZWZlcnJlZCIsICJmYWxzZSIpOwogICAgICAgICAgICBpZiAoR2V0U3RhdGUoInBlbmRpbmcuYm9ycm93X21lbnUiKSAhPSAidHJ1ZSIp"),
            new TextPatch(
                "normal-day-clears-gambled-late",
                "DOBak V13-D08",
                "ICAgICAgICBzdGF0ZVsicGVuZGluZy5nYW1ibGVfYXR0ZW50aW9uIl0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJmbGFnLmxhdGVfd2FrZV90b2RheSJdID0gImZhbHNlIjsKICAgICAgICBzdGF0ZVsiZmxhZy5ib3Jyb3dfZGVmZXJyZWQiXSA9ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImRheV9jYXNoX3N0YXJ0Il0gPSBmbG93LlYzQmFua0Nhc2guVG9TdHJpbmcoQ3VsdHVyZUluZm8uSW52YXJpYW50Q3VsdHVyZSk7",
                "ICAgICAgICBzdGF0ZVsicGVuZGluZy5nYW1ibGVfYXR0ZW50aW9uIl0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJmbGFnLmxhdGVfd2FrZV90b2RheSJdID0gImZhbHNlIjsKICAgICAgICBzdGF0ZVsiZmxhZy5ib3Jyb3dfZGVmZXJyZWQiXSA9ICJmYWxzZSI7CiAgICAgICAgLy8gRE9CYWsgVjEzLUQwODog7KCV7IOBIOy3qOy5qOycvOuhnCDrhJjslrTqsIQg64Kg7JeQ64+EIOyghOuCoCDrsKTsg5gg7ZGc7Iud7J2EIOygleumrO2VnOuLpC4KICAgICAgICBzdGF0ZVsiZmxhZy5nYW1ibGVkX2xhdGUiXSA9ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImRheV9jYXNoX3N0YXJ0Il0gPSBmbG93LlYzQmFua0Nhc2guVG9TdHJpbmcoQ3VsdHVyZUluZm8uSW52YXJpYW50Q3VsdHVyZSk7"),
            new TextPatch(
                "count-immediate-job-miss-at-day-finalize",
                "DOBak V13-D09",
                "ICAgICAgICBpZiAod2Vla2VuZERheSAmJiBHZXRTdGF0ZSgic2NoZWR1bGUuam9iIikgPT0gInBlbmRpbmciKQogICAgICAgIHsKICAgICAgICAgICAgU2V0U3RhdGUoInNjaGVkdWxlLmpvYiIsICJtaXNzZWQiKTsKICAgICAgICAgICAgQWRkSW50KCJjb3VudGVyLmpvYl9mYWlsdXJlcyIsIDEpOwogICAgICAgIH0=",
                "ICAgICAgICBpZiAod2Vla2VuZERheSAmJiBHZXRTdGF0ZSgic2NoZWR1bGUuam9iIikgPT0gInBlbmRpbmciKQogICAgICAgICAgICBTZXRTdGF0ZSgic2NoZWR1bGUuam9iIiwgIm1pc3NlZCIpOwogICAgICAgIC8vIERPQmFrIFYxMy1EMDk6IOymieyLnCDqsrDqt7wg7LKY66as66GcIOydtOuvuCBtaXNzZWTqsIAg65CcIOuCoOuPhCDtlZjro6gg7ZmV7KCVIOyLnCDsoJXtmZXtnogg7ZWcIOuyiCDsp5Hqs4TtlZzri6QuCiAgICAgICAgLy8gZGF5X2ZpbmFsaXplZCDqsIDrk5wg642V67aE7JeQIOykkeuztSDtmLjstpzrkJjslrTrj4Qg6rCZ7J2AIOqysOq3vOydhCDrkZAg67KIIOyEuOyngCDslYrripTri6QuCiAgICAgICAgaWYgKHdlZWtlbmREYXkgJiYgR2V0U3RhdGUoInNjaGVkdWxlLmpvYiIpID09ICJtaXNzZWQiKQogICAgICAgICAgICBBZGRJbnQoImNvdW50ZXIuam9iX2ZhaWx1cmVzIiwgMSk7"),
            new TextPatch(
                "use-seven-am-day-boundary",
                "DOBak V13-D10",
                "ICAgICAgICAgICAgICAgICAgICBpZiAoR2V0U3RhdGUoImZsYWcuZ2FtYmxlZF9sYXRlIikgPT0gInRydWUiICYmCiAgICAgICAgICAgICAgICAgICAgICAgIENyb3NzZXNDbG9ja0hvdXIoc3RhcnRIb3VyLCBlbGFwc2VkSG91cnMsIDgpKQ==",
                "ICAgICAgICAgICAgICAgICAgICAvLyBET0JhayBWMTMtRDEwOiDqsozsnoTsnZgg7ZWY66OoIOyLnOyekSDsi5zqsIEoMDc6MDAp7J2EIOyLpOygnCDrsKTsg5gg6rK96rOE66GcIOyCrOyaqe2VnOuLpC4KICAgICAgICAgICAgICAgICAgICBpZiAoR2V0U3RhdGUoImZsYWcuZ2FtYmxlZF9sYXRlIikgPT0gInRydWUiICYmCiAgICAgICAgICAgICAgICAgICAgICAgIENyb3NzZXNDbG9ja0hvdXIoc3RhcnRIb3VyLCBlbGFwc2VkSG91cnMsIDcpKQ==")
    };

    private static readonly TextPatch[] GameFlowPatches =
    {
            new TextPatch(
                "show-complete-vs-missed-in-home-checklist",
                "DOBak V13-G01",
                "ICAgIHByaXZhdGUgdm9pZCBVcGRhdGVIb21lQ2hlY2tsaXN0KCkKICAgIHsKICAgICAgICBpZiAoaG9tZUNoZWNrbGlzdExpbmVzLkNvdW50ID09IDApCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgc3RyaW5nIGdvYWxMaW5lID0gJCLrhbjtirjrtoEg7IiY66as67mEICB7VjNCYW5rQ2FzaDpOMH0gLyAyNTAsMDAw7JuQIjsKICAgICAgICBzdHJpbmcgZGVidExpbmUgPSBkZWJ0ID4gMCA/ICQi67mM66awIOuPiCAge2RlYnQ6TjB97JuQIiA6ICIiOwogICAgICAgIGJvb2wga25vd3NQcm9qZWN0ID0gc2NlbmFyaW9WMyA9PSBudWxsIHx8IGN1cnJlbnREYXkgPiAxIHx8IHNjaG9vbERvbmUgfHwKICAgICAgICAgICAgICAgICAgICAgICAgICAgIHN0cmluZy5FcXVhbHMoc2NlbmFyaW9WMy5HZXRTdGF0ZSgiZmxhZy5wcm9qZWN0X2ludHJvZHVjZWQiKSwgInRydWUiLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKTsKICAgICAgICBzdHJpbmcgc3R1ZHlMaW5lID0gIWtub3dzUHJvamVjdAogICAgICAgICAgICA/ICIiCiAgICAgICAgICAgIDogVjNIYXNTdHVkeVRvZGF5CiAgICAgICAgICAgICAgICA/ICQie01hcmsoaG9tZXdvcmtEb25lKX0ge3F1aXpNYW5hZ2VyLkN1cnJlbnRBY3Rpdml0eVRpdGxlfSIKICAgICAgICAgICAgICAgIDogIuyYpOuKmOydgCDrs4Trj4Qg6rO87KCcIOyXhuydjCI7CiAgICAgICAgc3RyaW5nW10gbGluZXMgPSBJc1dlZWtlbmQKICAgICAgICAgICAgPyBuZXdbXQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICAkIntNYXJrKGpvYkRvbmUpfSDsubTtjpgg7JWM67CUICAwODowMH4xNjowMCIsCiAgICAgICAgICAgICAgICBnb2FsTGluZSwKICAgICAgICAgICAgICAgIGRlYnRMaW5lLAogICAgICAgICAgICAgICAgIiIKICAgICAgICAgICAgfQogICAgICAgICAgICA6IG5ld1tdCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgICQie01hcmsoc2Nob29sRG9uZSl9IO2Vmeq1kCDqsIDquLAiLAogICAgICAgICAgICAgICAgc3R1ZHlMaW5lLAogICAgICAgICAgICAgICAgZ29hbExpbmUsCiAgICAgICAgICAgICAgICBkZWJ0TGluZQogICAgICAgICAgICB9OwoKICAgICAgICBmb3IgKGludCBpID0gMDsgaSA8IGhvbWVDaGVja2xpc3RMaW5lcy5Db3VudDsgaSsrKQogICAgICAgICAgICBob21lQ2hlY2tsaXN0TGluZXNbaV0udGV4dCA9IGkgPCBsaW5lcy5MZW5ndGggPyBsaW5lc1tpXSA6ICIiOwogICAgfQ==",
                "ICAgIHByaXZhdGUgdm9pZCBVcGRhdGVIb21lQ2hlY2tsaXN0KCkKICAgIHsKICAgICAgICBpZiAoaG9tZUNoZWNrbGlzdExpbmVzLkNvdW50ID09IDApCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgc3RyaW5nIGdvYWxMaW5lID0gJCLrhbjtirjrtoEg7IiY66as67mEICB7VjNCYW5rQ2FzaDpOMH0gLyAyNTAsMDAw7JuQIjsKICAgICAgICBzdHJpbmcgZGVidExpbmUgPSBkZWJ0ID4gMCA/ICQi67mM66awIOuPiCAge2RlYnQ6TjB97JuQIiA6ICIiOwogICAgICAgIGJvb2wga25vd3NQcm9qZWN0ID0gc2NlbmFyaW9WMyA9PSBudWxsIHx8IGN1cnJlbnREYXkgPiAxIHx8IHNjaG9vbERvbmUgfHwKICAgICAgICAgICAgICAgICAgICAgICAgICAgIHN0cmluZy5FcXVhbHMoc2NlbmFyaW9WMy5HZXRTdGF0ZSgiZmxhZy5wcm9qZWN0X2ludHJvZHVjZWQiKSwgInRydWUiLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKTsKICAgICAgICBzdHJpbmcgc2Nob29sTWFyayA9IFYzU2NoZWR1bGVNYXJrKCJzY2hvb2wiLCBzY2hvb2xEb25lKTsKICAgICAgICBzdHJpbmcgaG9tZXdvcmtNYXJrID0gVjNTY2hlZHVsZU1hcmsoImhvbWV3b3JrIiwgaG9tZXdvcmtEb25lKTsKICAgICAgICBzdHJpbmcgam9iTWFyayA9IFYzU2NoZWR1bGVNYXJrKCJqb2IiLCBqb2JEb25lKTsKICAgICAgICBzdHJpbmcgc3R1ZHlMaW5lID0gIWtub3dzUHJvamVjdAogICAgICAgICAgICA/ICIiCiAgICAgICAgICAgIDogVjNIYXNTdHVkeVRvZGF5CiAgICAgICAgICAgICAgICA/ICQie2hvbWV3b3JrTWFya30ge3F1aXpNYW5hZ2VyLkN1cnJlbnRBY3Rpdml0eVRpdGxlfSIKICAgICAgICAgICAgICAgIDogIuyYpOuKmOydgCDrs4Trj4Qg6rO87KCcIOyXhuydjCI7CiAgICAgICAgc3RyaW5nW10gbGluZXMgPSBJc1dlZWtlbmQKICAgICAgICAgICAgPyBuZXdbXQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICAkIntqb2JNYXJrfSDsubTtjpgg7JWM67CUICAwODowMH4xNjowMCIsCiAgICAgICAgICAgICAgICBnb2FsTGluZSwKICAgICAgICAgICAgICAgIGRlYnRMaW5lLAogICAgICAgICAgICAgICAgIiIKICAgICAgICAgICAgfQogICAgICAgICAgICA6IG5ld1tdCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgICQie3NjaG9vbE1hcmt9IO2Vmeq1kCDqsIDquLAiLAogICAgICAgICAgICAgICAgc3R1ZHlMaW5lLAogICAgICAgICAgICAgICAgZ29hbExpbmUsCiAgICAgICAgICAgICAgICBkZWJ0TGluZQogICAgICAgICAgICB9OwoKICAgICAgICBmb3IgKGludCBpID0gMDsgaSA8IGhvbWVDaGVja2xpc3RMaW5lcy5Db3VudDsgaSsrKQogICAgICAgICAgICBob21lQ2hlY2tsaXN0TGluZXNbaV0udGV4dCA9IGkgPCBsaW5lcy5MZW5ndGggPyBsaW5lc1tpXSA6ICIiOwogICAgfQoKICAgIC8vIERPQmFrIFYxMy1HMDE6IO2WieuPmSDsnqDquIjsmqkgcmVzb2x2ZWQg67aI66as7Ja46rO8IO2ZlOuptOyXkCDrs7Tsl6wg7KSEIOyZhOujjC/rhpPsuagg7IOB7YOc66W8IOu2hOumrO2VnOuLpC4KICAgIHByaXZhdGUgc3RyaW5nIFYzU2NoZWR1bGVNYXJrKHN0cmluZyBzY2hlZHVsZSwgYm9vbCBmYWxsYmFja1Jlc29sdmVkKQogICAgewogICAgICAgIGlmIChzY2VuYXJpb1YzID09IG51bGwpCiAgICAgICAgICAgIHJldHVybiBNYXJrKGZhbGxiYWNrUmVzb2x2ZWQpOwoKICAgICAgICBzdHJpbmcgc3RhdHVzID0gc2NlbmFyaW9WMy5HZXRTdGF0ZSgic2NoZWR1bGUuIiArIHNjaGVkdWxlKTsKICAgICAgICByZXR1cm4gc3RhdHVzIHN3aXRjaAogICAgICAgIHsKICAgICAgICAgICAgImNvbXBsZXRlIiA9PiAiW+yZhOujjF0iLAogICAgICAgICAgICAibWlzc2VkIiA9PiAiW+uGk+y5qF0iLAogICAgICAgICAgICBfID0+ICJbICBdIgogICAgICAgIH07CiAgICB9")
    };

    private static readonly TextPatch[] NotificationPatches =
    {
            new TextPatch(
                "keep-newest-notification-visible-at-cap",
                "DOBak V13-N01",
                "ICAgICAgICBOb3RpZmljYXRpb25JdGVtIGl0ZW0gPSBJbnN0YW50aWF0ZShpdGVtUHJlZmFiLCBjb250ZW50KTsKICAgICAgICAKICAgICAgICBpZiAoY29udGVudC5jaGlsZENvdW50ID49IG1heE5vdGlmaWNhdGlvbkNvdW50KQogICAgICAgIHsKICAgICAgICAgICAgRGVzdHJveShjb250ZW50LkdldENoaWxkKGNvbnRlbnQuY2hpbGRDb3VudCAtIDEpLmdhbWVPYmplY3QpOwogICAgICAgIH0KCiAgICAgICAgLy8g7IOI66GcIOy2lOqwgOuQnCDslYzrprzsnYQg66eoIOychOuhnCDsnbTrj5kKICAgICAgICBpdGVtLnRyYW5zZm9ybS5TZXRBc0ZpcnN0U2libGluZygpOwoKICAgICAgICAvLyDrjbDsnbTthLAg7Jew6rKwIAogICAgICAgIGl0ZW0uU2V0RGF0YShkYXRhLCB0aGlzKTs=",
                "ICAgICAgICBOb3RpZmljYXRpb25JdGVtIGl0ZW0gPSBJbnN0YW50aWF0ZShpdGVtUHJlZmFiLCBjb250ZW50KTsKCiAgICAgICAgLy8g7IOI66GcIOy2lOqwgOuQnCDslYzrprzsnYQg66i87KCAIOunqCDsnITroZwg7J2064+Z7ZWc64ukLgogICAgICAgIGl0ZW0udHJhbnNmb3JtLlNldEFzRmlyc3RTaWJsaW5nKCk7CgogICAgICAgIC8vIERPQmFrIFYxMy1OMDE6IOy1nOuMgCDqsJzsiJjsl5Ag64+E64us7ZaI7J2EIOuVjCDrsKnquIgg66eM65OgIOy1nOyLoCDslYzrprzsnbQg7JWE64uI6528IOqwgOyepSDsmKTrnpjrkJwg7ZWt66qp7J2EIOygnOqxsO2VnOuLpC4KICAgICAgICBpbnQgdmlzaWJsZUxpbWl0ID0gTWF0aGYuTWF4KDEsIG1heE5vdGlmaWNhdGlvbkNvdW50KTsKICAgICAgICBmb3IgKGludCBpbmRleCA9IGNvbnRlbnQuY2hpbGRDb3VudCAtIDE7IGluZGV4ID49IHZpc2libGVMaW1pdDsgaW5kZXgtLSkKICAgICAgICAgICAgRGVzdHJveShjb250ZW50LkdldENoaWxkKGluZGV4KS5nYW1lT2JqZWN0KTsKCiAgICAgICAgLy8g642w7J207YSwIOyXsOqysAogICAgICAgIGl0ZW0uU2V0RGF0YShkYXRhLCB0aGlzKTs=")
    };

    private static readonly FilePlan[] Plans =
    {
            new FilePlan(
                "Assets/Tablet/Script/ScenarioV3Director.cs",
                "dab1e9f7ac2c4d96b5228bcf4592f218edf30e48",
                DirectorPatches),
            new FilePlan(
                "Assets/Tablet/Script/GameFlowManager.cs",
                "4ac54c4e174b8aa7f7525bb6548cda33ac108923",
                GameFlowPatches),
            new FilePlan(
                "Assets/Tablet/Script/NotificationManager.cs",
                "ccf324e0ccd673868faa20859c4f4251aaed811b",
                NotificationPatches)
    };

    static DobakV3FlowHotfixInstaller()
    {
        ScheduleAutoApply();
    }

    [MenuItem(MenuRoot + "Apply V13 Flow Hotfix")]
    public static void ApplyFromMenu()
    {
        Apply(showDialog: true);
    }

    [MenuItem(MenuRoot + "Validate V13 Flow Hotfix")]
    public static void ValidateFromMenu()
    {
        try
        {
            string projectRoot = GetProjectRoot();
            ValidateProjectData(projectRoot);
            foreach (FilePlan plan in Plans)
            {
                FileSnapshot snapshot = ReadSnapshot(projectRoot, plan);
                ValidateMarkers(snapshot.NormalizedText, plan);
            }

            string message = "Dobak V3 flow hotfix v13 validation PASS.\n\n" +
                             "시간 경계, 밤샘/차용 분리, 예약 장면 보존, 결근 집계, 일정 표시, 알림 상한 패치가 모두 확인되었습니다.";
            Debug.Log("[DOBak V13] VALIDATION PASS");
            EditorUtility.DisplayDialog("DOBak V13", message, "확인");
        }
        catch (Exception exception)
        {
            Debug.LogError("[DOBak V13] VALIDATION FAIL\n" + exception);
            EditorUtility.DisplayDialog("DOBak V13 검증 실패", exception.Message, "확인");
        }
    }

    private static void ScheduleAutoApply()
    {
        if (autoApplyScheduled)
            return;
        autoApplyScheduled = true;
        EditorApplication.delayCall += TryAutoApply;
    }

    private static void TryAutoApply()
    {
        autoApplyScheduled = false;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.isPlayingOrWillChangePlaymode)
        {
            ScheduleAutoApply();
            return;
        }

        Apply(showDialog: false);
    }

    private static void Apply(bool showDialog)
    {
        if (isRunning)
            return;

        isRunning = true;
        try
        {
            string projectRoot = GetProjectRoot();
            ValidateProjectData(projectRoot);

            var snapshots = new List<FileSnapshot>();
            foreach (FilePlan plan in Plans)
            {
                FileSnapshot snapshot = ReadSnapshot(projectRoot, plan);
                bool alreadyPatched = plan.Patches.All(item => snapshot.NormalizedText.Contains(item.Marker));
                bool partlyPatched = !alreadyPatched && plan.Patches.Any(item => snapshot.NormalizedText.Contains(item.Marker));
                if (partlyPatched)
                    throw new InvalidOperationException(plan.AssetPath + "에 V13 마커가 일부만 있습니다. 부분 적용 상태에서는 안전을 위해 아무 파일도 쓰지 않습니다.");

                if (!alreadyPatched && !string.Equals(snapshot.OriginalGitBlobSha, plan.ExpectedGitBlobSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning("[DOBak V13] " + plan.AssetPath +
                                     "의 Git blob SHA가 기준본과 다르지만, 모든 원본 코드 조각이 정확히 일치할 때만 호환 패치를 계속합니다.\n" +
                                     "expected=" + plan.ExpectedGitBlobSha + " actual=" + snapshot.OriginalGitBlobSha);
                }

                snapshot.PatchedNormalizedText = ApplyPatches(snapshot.NormalizedText, plan);
                snapshot.PatchedBytes = Encode(snapshot.PatchedNormalizedText, snapshot.NewLine, snapshot.HadUtf8Bom);
                ValidateMarkers(snapshot.PatchedNormalizedText, plan);
                snapshots.Add(snapshot);
            }

            List<FileSnapshot> changed = snapshots
                .Where(item => !ByteArraysEqual(item.OriginalBytes, item.PatchedBytes))
                .ToList();

            if (changed.Count == 0)
            {
                WriteStatus(projectRoot, "PASS - already applied", snapshots, null);
                Debug.Log("[DOBak V13] PASS - hotfix is already applied and validated.");
                if (showDialog)
                    EditorUtility.DisplayDialog("DOBak V13", "이미 적용되어 있으며 검증도 통과했습니다.", "확인");
                return;
            }

            string backupRoot = CreateBackup(projectRoot, changed);
            var written = new List<FileSnapshot>();
            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (FileSnapshot snapshot in changed)
                    {
                        WriteBytesSafely(snapshot.AbsolutePath, snapshot.PatchedBytes);
                        written.Add(snapshot);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                // 디스크에 실제 기록된 결과까지 다시 읽어 검증한다. 여기서 실패해도 아래 catch가 원본으로 되돌린다.
                foreach (FileSnapshot snapshot in changed)
                {
                    FileSnapshot disk = ReadSnapshot(projectRoot, snapshot.Plan);
                    ValidateMarkers(disk.NormalizedText, snapshot.Plan);
                    AssetDatabase.ImportAsset(snapshot.Plan.AssetPath, ImportAssetOptions.ForceUpdate);
                }
            }
            catch (Exception applyException)
            {
                try
                {
                    RestoreOriginals(written);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "패치 적용 중 오류가 났고 자동 복원에서도 오류가 발생했습니다. Library/DobakV3HotfixBackup의 원본을 확인하세요.",
                        applyException, rollbackException);
                }
                throw;
            }

            WriteStatus(projectRoot, "PASS - applied", snapshots, backupRoot);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            string pass = "[DOBak V13] PASS - " + changed.Count +
                          "개 소스 파일에 흐름 핫픽스를 적용했습니다. 원본 백업: " + backupRoot;
            Debug.Log(pass);
            if (showDialog)
            {
                EditorUtility.DisplayDialog("DOBak V13 적용 완료",
                    "흐름 핫픽스를 적용하고 검증했습니다.\n\n원본 백업:\n" + backupRoot +
                    "\n\nUnity Console에서 [DOBak V13] PASS를 확인하세요.", "확인");
            }
        }
        catch (Exception exception)
        {
            string projectRoot = SafeGetProjectRoot();
            if (!string.IsNullOrEmpty(projectRoot))
                WriteFailureStatus(projectRoot, exception);
            Debug.LogError("[DOBak V13] APPLY FAIL - 안전을 위해 패치를 중단했습니다.\n" + exception);
            if (showDialog)
                EditorUtility.DisplayDialog("DOBak V13 적용 실패",
                    "원본 파일은 그대로 유지되었거나 자동 복원되었습니다.\n\n" + exception.Message, "확인");
        }
        finally
        {
            isRunning = false;
        }
    }

    private static string ApplyPatches(string source, FilePlan plan)
    {
        bool allApplied = plan.Patches.All(item => source.Contains(item.Marker));
        if (allApplied)
            return source;

        string result = source;
        foreach (TextPatch item in plan.Patches)
        {
            int occurrences = CountOccurrences(result, item.OldText);
            if (occurrences != 1)
            {
                throw new InvalidOperationException(plan.AssetPath + " / " + item.Label +
                    ": 예상 원본 코드가 정확히 1개여야 하지만 " + occurrences +
                    "개입니다. 최신 파일을 잘못 덮어쓰지 않도록 전체 적용을 취소했습니다.");
            }
            result = ReplaceOnce(result, item.OldText, item.NewText);
        }
        return result;
    }

    private static void ValidateMarkers(string source, FilePlan plan)
    {
        foreach (TextPatch item in plan.Patches)
        {
            if (!source.Contains(item.Marker))
                throw new InvalidOperationException(plan.AssetPath + "에서 검증 마커가 누락되었습니다: " + item.Marker);
            if (source.Contains(item.OldText))
                throw new InvalidOperationException(plan.AssetPath + "에 교체 전 코드가 남아 있습니다: " + item.Label);
        }
    }

    private static void ValidateProjectData(string projectRoot)
    {
        RequireTextTokens(projectRoot, "Assets/Resources/ScenarioV3.csv", new[]
        {
            "borrow_defer_night", "borrow_morning_cue", "borrow_choice",
            "sys_late_gamble_morning", "sys_borrow_late_morning", "counter.job_failures"
        });
        RequireTextTokens(projectRoot, "Assets/Resources/ScenarioV3Flow.csv", new[]
        {
            "borrow_morning_cue", "d4_job_missed_now", "d5_job_missed_now_first",
            "d5_job_missed_now_repeat", "sys_late_gamble_morning", "sys_borrow_late_morning"
        });
        RequireFile(projectRoot, "Assets/Resources/StudyActivities.csv");
        RequireFile(projectRoot, "Assets/Tablet/TabletUI.unity");
    }

    private static void RequireTextTokens(string projectRoot, string assetPath, IEnumerable<string> tokens)
    {
        string absolutePath = ToAbsolutePath(projectRoot, assetPath);
        if (!File.Exists(absolutePath))
            throw new FileNotFoundException("필수 기획 데이터가 없습니다: " + assetPath, absolutePath);
        string text = File.ReadAllText(absolutePath, Encoding.UTF8);
        foreach (string token in tokens)
        {
            if (!text.Contains(token))
                throw new InvalidOperationException(assetPath + "에 필요한 기존 기획 연결이 없습니다: " + token);
        }
    }

    private static void RequireFile(string projectRoot, string assetPath)
    {
        string absolutePath = ToAbsolutePath(projectRoot, assetPath);
        if (!File.Exists(absolutePath))
            throw new FileNotFoundException("필수 프로젝트 파일이 없습니다: " + assetPath, absolutePath);
    }

    private static FileSnapshot ReadSnapshot(string projectRoot, FilePlan plan)
    {
        string absolutePath = ToAbsolutePath(projectRoot, plan.AssetPath);
        if (!File.Exists(absolutePath))
            throw new FileNotFoundException("패치 대상 파일이 없습니다: " + plan.AssetPath, absolutePath);

        byte[] bytes = File.ReadAllBytes(absolutePath);
        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        int offset = hasBom ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(plan.AssetPath + "가 올바른 UTF-8 소스가 아닙니다.", exception);
        }

        string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        string normalized = NormalizeNewLines(text);
        return new FileSnapshot
        {
            Plan = plan,
            AbsolutePath = absolutePath,
            OriginalBytes = bytes,
            HadUtf8Bom = hasBom,
            NewLine = newLine,
            NormalizedText = normalized,
            OriginalGitBlobSha = ComputeGitBlobSha(bytes)
        };
    }

    private static string CreateBackup(string projectRoot, List<FileSnapshot> changed)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupRoot = Path.Combine(projectRoot, "Library", "DobakV3HotfixBackup", Version + "_" + stamp);
        int suffix = 1;
        while (Directory.Exists(backupRoot))
            backupRoot = Path.Combine(projectRoot, "Library", "DobakV3HotfixBackup", Version + "_" + stamp + "_" + suffix++);

        foreach (FileSnapshot snapshot in changed)
        {
            string target = Path.Combine(backupRoot,
                snapshot.Plan.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllBytes(target, snapshot.OriginalBytes);
        }

        var manifest = new StringBuilder();
        manifest.AppendLine("Dobak V3 flow hotfix " + Version);
        manifest.AppendLine("Created: " + DateTime.Now.ToString("O"));
        manifest.AppendLine("Only code flow/state/presentation fixes; no CSV/scene/design files are modified.");
        manifest.AppendLine();
        foreach (FileSnapshot snapshot in changed)
        {
            manifest.AppendLine(snapshot.Plan.AssetPath);
            manifest.AppendLine("  baseline git blob: " + snapshot.OriginalGitBlobSha);
            manifest.AppendLine("  original sha256: " + Sha256(snapshot.OriginalBytes));
            manifest.AppendLine("  patched sha256:  " + Sha256(snapshot.PatchedBytes));
        }
        Directory.CreateDirectory(backupRoot);
        File.WriteAllText(Path.Combine(backupRoot, "manifest.txt"), manifest.ToString(), new UTF8Encoding(true));
        return backupRoot;
    }

    private static void WriteStatus(string projectRoot, string result, List<FileSnapshot> snapshots, string backupRoot)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "Library"));
        var status = new StringBuilder();
        status.AppendLine("Dobak V3 flow hotfix " + Version);
        status.AppendLine("Result: " + result);
        status.AppendLine("Checked: " + DateTime.Now.ToString("O"));
        status.AppendLine("Backup: " + (backupRoot ?? "not needed"));
        status.AppendLine();
        foreach (FileSnapshot snapshot in snapshots)
        {
            status.AppendLine(snapshot.Plan.AssetPath);
            status.AppendLine("  source git blob before apply: " + snapshot.OriginalGitBlobSha);
            status.AppendLine("  markers: " + string.Join(", ", snapshot.Plan.Patches.Select(item => item.Marker)));
        }
        File.WriteAllText(Path.Combine(projectRoot, "Library", StatusFileName), status.ToString(), new UTF8Encoding(true));
    }

    private static void WriteFailureStatus(string projectRoot, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Library"));
            File.WriteAllText(Path.Combine(projectRoot, "Library", StatusFileName),
                "Dobak V3 flow hotfix " + Version + "\nResult: FAIL\nChecked: " + DateTime.Now.ToString("O") +
                "\n\n" + exception, new UTF8Encoding(true));
        }
        catch
        {
            // Status logging must never hide the original failure.
        }
    }

    private static void RestoreOriginals(List<FileSnapshot> written)
    {
        if (written == null || written.Count == 0)
            return;

        AssetDatabase.StartAssetEditing();
        try
        {
            for (int index = written.Count - 1; index >= 0; index--)
                File.WriteAllBytes(written[index].AbsolutePath, written[index].OriginalBytes);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
    }

    private static void WriteBytesSafely(string absolutePath, byte[] bytes)
    {
        string temporary = absolutePath + ".dobak_v13_tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);
        File.WriteAllBytes(temporary, bytes);
        File.Copy(temporary, absolutePath, true);
        File.Delete(temporary);
    }

    private static byte[] Encode(string normalizedText, string newLine, bool includeBom)
    {
        string text = newLine == "\n" ? normalizedText : normalizedText.Replace("\n", newLine);
        byte[] body = new UTF8Encoding(false).GetBytes(text);
        if (!includeBom)
            return body;

        byte[] result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        Buffer.BlockCopy(body, 0, result, 3, body.Length);
        return result;
    }

    private static string NormalizeNewLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReplaceOnce(string source, string oldValue, string newValue)
    {
        int index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException("ReplaceOnce source was not found.");
        return source.Substring(0, index) + newValue + source.Substring(index + oldValue.Length);
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left == null || right == null || left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
                return false;
        }
        return true;
    }

    private static string ComputeGitBlobSha(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        byte[] input = new byte[header.Length + bytes.Length];
        Buffer.BlockCopy(header, 0, input, 0, header.Length);
        Buffer.BlockCopy(bytes, 0, input, header.Length, bytes.Length);
        using (SHA1 sha = SHA1.Create())
            return ToHex(sha.ComputeHash(input));
    }

    private static string Sha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
            return ToHex(sha.ComputeHash(bytes));
    }

    private static string ToHex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string Decode(string base64)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static string GetProjectRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        if (!Directory.Exists(Path.Combine(root, "Assets")))
            throw new DirectoryNotFoundException("Unity 프로젝트 루트를 찾을 수 없습니다: " + root);
        return root;
    }

    private static string SafeGetProjectRoot()
    {
        try { return GetProjectRoot(); }
        catch { return string.Empty; }
    }

    private static string ToAbsolutePath(string projectRoot, string assetPath)
    {
        return Path.GetFullPath(Path.Combine(projectRoot,
            assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
#endif
