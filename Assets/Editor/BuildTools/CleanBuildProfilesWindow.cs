using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CoreCLRTest.Build
{
    internal sealed class CleanBuildProfilesWindow : EditorWindow
    {
        private const string MenuPath = "CoreCLR Test/Build/Clean Build Profiles";
        private const string WindowTitle = "Clean Build Profiles";
        private const string DefaultOutputRoot = "Builds";
        private const string PreferencePrefix = "CoreCLRTest.CleanBuildProfiles";
        private const string OutputRootPreferenceSuffix = ".OutputRoot";
        private const string SelectedProfilesPreferenceSuffix = ".SelectedProfiles";
        private const string KnownProfilesPreferenceSuffix = ".KnownProfiles";
        private const string PreferenceValueSeparator = ",";
        private const string NoProfilesMessage = "No BuildProfile assets were found below Assets.";
        private const string EmptySelectionMessage = "Select at least one build profile.";
        private const string EmptyValidSelectionMessage = "Select at least one valid build profile.";
        private const string PlayModeMessage = "Clean builds cannot start while Unity is in or entering Play Mode.";
        private const string CompilationMessage = "Clean builds cannot start while scripts are compiling.";
        private const string BuildButtonLabel = "Clean Build Selected Profiles";
        private const string SelectAllButtonLabel = "Select All";
        private const string SelectNoneButtonLabel = "Select None";
        private const string RefreshButtonLabel = "Refresh";
        private const string RevealReportButtonLabel = "Reveal Latest Report";
        private const string OutputRootLabel = "Output Root";
        private const string ProfilesLabel = "Build Profiles";
        private const string LatestRunLabel = "Latest Run";
        private const string NoLatestRunMessage = "No clean build has been run from this window.";
        private const string ProgressTitle = "Clean Build Profiles";
        private const string ProgressMessageFormat = "Building {0}/{1}: {2}";
        private const string SummaryFormat = "Succeeded: {0}   Failed: {1}   Cancelled: {2}   Skipped: {3}   Total: {4}";
        private const string ReportPathLabel = "CSV Report";
        private const string ReportErrorLabel = "Report Error";
        private const string ProfileTargetFormat = "Target: {0}";
        private const string SelectedCountFormat = "Selected: {0}/{1}";
        private const float MinimumWindowWidth = 540f;
        private const float MinimumWindowHeight = 420f;
        private const float ProfileListMinimumHeight = 160f;
        private const float ProfileListReservedHeight = 260f;
        private const float ProfileRowSpacing = 4f;
        private const float ProfileIndentWidth = 20f;

        private readonly HashSet<string> selectedProfileGuids = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> knownProfileGuids = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<CleanBuildProfileDescriptor> profiles = Array.Empty<CleanBuildProfileDescriptor>();
        private Vector2 profileScrollPosition;
        private string relativeOutputRoot = DefaultOutputRoot;
        private CleanBuildRunResult latestRunResult;
        private string currentProgressMessage = string.Empty;
        private bool isBuilding;

        [MenuItem(MenuPath)]
        private static void OpenWindow()
        {
            var window = GetWindow<CleanBuildProfilesWindow>(WindowTitle);
            window.minSize = new Vector2(MinimumWindowWidth, MinimumWindowHeight);
            window.Show();
            window.Focus();
        }

        private void OnEnable()
        {
            LoadPreferences();
            RefreshProfiles();
        }

        private void OnDisable()
        {
            SavePreferences();
        }

        private void OnGUI()
        {
            DrawOutputRoot();
            EditorGUILayout.Space();
            DrawProfileControls();
            DrawProfileList();
            EditorGUILayout.Space();
            DrawBuildControls();
            EditorGUILayout.Space();
            DrawLatestRun();
        }

        private void DrawOutputRoot()
        {
            EditorGUILayout.LabelField(OutputRootLabel, EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            relativeOutputRoot = EditorGUILayout.TextField(new GUIContent(OutputRootLabel, "Project-relative folder outside Assets."), relativeOutputRoot);
            if (EditorGUI.EndChangeCheck())
            {
                SavePreferences();
            }

            if (!CleanBuildProfilesRunner.TryValidateOutputRoot(relativeOutputRoot, out var validationMessage))
            {
                EditorGUILayout.HelpBox(validationMessage, MessageType.Error);
            }
        }

        private void DrawProfileControls()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(ProfilesLabel, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(string.Format(SelectedCountFormat, GetSelectedProfiles().Count, profiles.Count), GUILayout.Width(110f));
            if (GUILayout.Button(SelectAllButtonLabel, EditorStyles.miniButtonLeft, GUILayout.Width(72f)))
            {
                SelectAllProfiles();
            }

            if (GUILayout.Button(SelectNoneButtonLabel, EditorStyles.miniButtonMid, GUILayout.Width(76f)))
            {
                SelectNoProfiles();
            }

            if (GUILayout.Button(RefreshButtonLabel, EditorStyles.miniButtonRight, GUILayout.Width(68f)))
            {
                RefreshProfiles();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawProfileList()
        {
            if (profiles.Count == 0)
            {
                EditorGUILayout.HelpBox(NoProfilesMessage, MessageType.Info);
                return;
            }

            var listHeight = Mathf.Max(ProfileListMinimumHeight, position.height - ProfileListReservedHeight);
            profileScrollPosition = EditorGUILayout.BeginScrollView(profileScrollPosition, EditorStyles.helpBox, GUILayout.MinHeight(ProfileListMinimumHeight), GUILayout.Height(listHeight));
            foreach (var profile in profiles)
            {
                DrawProfile(profile);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawProfile(CleanBuildProfileDescriptor profile)
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.BeginHorizontal();
            var isSelected = selectedProfileGuids.Contains(profile.AssetGuid);
            var updatedSelection = EditorGUILayout.Toggle(isSelected, GUILayout.Width(16f));
            if (updatedSelection != isSelected)
            {
                SetProfileSelection(profile.AssetGuid, updatedSelection);
            }

            EditorGUILayout.LabelField(profile.DisplayName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(string.Format(ProfileTargetFormat, profile.BuildTarget), EditorStyles.miniLabel, GUILayout.Width(190f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(ProfileIndentWidth);
            EditorGUILayout.SelectableLabel(profile.AssetPath, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
            if (!profile.IsValid)
            {
                EditorGUILayout.HelpBox(profile.ValidationMessage, MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            GUILayout.Space(ProfileRowSpacing);
        }

        private void DrawBuildControls()
        {
            var selectedProfiles = GetSelectedProfiles();
            var hasValidSelection = selectedProfiles.Any(profile => profile.IsValid);
            var hasValidOutputRoot = CleanBuildProfilesRunner.TryValidateOutputRoot(relativeOutputRoot, out _);
            var isEditorBusy = EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(isBuilding || isEditorBusy || !hasValidSelection || !hasValidOutputRoot))
            {
                if (GUILayout.Button(BuildButtonLabel, GUILayout.Height(32f)))
                {
                    BuildSelectedProfiles();
                }
            }

            if (EditorApplication.isCompiling)
            {
                EditorGUILayout.HelpBox(CompilationMessage, MessageType.Info);
            }
            else if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUILayout.HelpBox(PlayModeMessage, MessageType.Info);
            }
            else if (selectedProfiles.Count == 0)
            {
                EditorGUILayout.HelpBox(EmptySelectionMessage, MessageType.Info);
            }
            else if (!hasValidSelection)
            {
                EditorGUILayout.HelpBox(EmptyValidSelectionMessage, MessageType.Warning);
            }

            if (!string.IsNullOrEmpty(currentProgressMessage))
            {
                EditorGUILayout.HelpBox(currentProgressMessage, MessageType.Info);
            }
        }

        private void DrawLatestRun()
        {
            EditorGUILayout.LabelField(LatestRunLabel, EditorStyles.boldLabel);
            if (latestRunResult == null)
            {
                EditorGUILayout.HelpBox(NoLatestRunMessage, MessageType.Info);
                return;
            }

            var summary = string.Format(SummaryFormat, latestRunResult.SucceededCount, latestRunResult.FailedCount, latestRunResult.CancelledCount, latestRunResult.SkippedCount, CleanBuildProfilesRunner.FormatElapsedTime(latestRunResult.TotalElapsed));
            EditorGUILayout.LabelField(summary, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(latestRunResult.CsvReportPath))
            {
                EditorGUILayout.LabelField(ReportPathLabel, EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(latestRunResult.CsvReportPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            if (!string.IsNullOrEmpty(latestRunResult.CsvReportErrorMessage))
            {
                EditorGUILayout.HelpBox(string.Concat(ReportErrorLabel, ": ", latestRunResult.CsvReportErrorMessage), MessageType.Error);
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(latestRunResult.CsvReportPath) || !File.Exists(latestRunResult.CsvReportPath)))
            {
                if (GUILayout.Button(RevealReportButtonLabel))
                {
                    RevealLatestReport();
                }
            }
        }

        private void RefreshProfiles()
        {
            var discoveredProfiles = CleanBuildProfilesRunner.DiscoverProfiles();
            var discoveredProfileGuids = new HashSet<string>(discoveredProfiles.Select(profile => profile.AssetGuid), StringComparer.Ordinal);
            foreach (var profile in discoveredProfiles)
            {
                if (!knownProfileGuids.Contains(profile.AssetGuid))
                {
                    selectedProfileGuids.Add(profile.AssetGuid);
                }
            }

            selectedProfileGuids.IntersectWith(discoveredProfileGuids);
            knownProfileGuids.Clear();
            knownProfileGuids.UnionWith(discoveredProfileGuids);
            profiles = discoveredProfiles;
            SavePreferences();
            Repaint();
        }

        private void BuildSelectedProfiles()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog(WindowTitle, PlayModeMessage, "OK");
                return;
            }

            if (EditorApplication.isCompiling)
            {
                EditorUtility.DisplayDialog(WindowTitle, CompilationMessage, "OK");
                return;
            }

            var selectedProfiles = GetSelectedProfiles();
            if (selectedProfiles.Count == 0)
            {
                EditorUtility.DisplayDialog(WindowTitle, EmptySelectionMessage, "OK");
                return;
            }

            if (!selectedProfiles.Any(profile => profile.IsValid))
            {
                EditorUtility.DisplayDialog(WindowTitle, EmptyValidSelectionMessage, "OK");
                return;
            }

            if (!CleanBuildProfilesRunner.TryValidateOutputRoot(relativeOutputRoot, out var validationMessage))
            {
                EditorUtility.DisplayDialog(WindowTitle, validationMessage, "OK");
                return;
            }

            isBuilding = true;
            currentProgressMessage = string.Empty;
            SavePreferences();
            try
            {
                latestRunResult = CleanBuildProfilesRunner.BuildSelectedProfiles(selectedProfiles, relativeOutputRoot, UpdateBuildProgress);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(WindowTitle, exception.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                currentProgressMessage = string.Empty;
                isBuilding = false;
                Repaint();
            }
        }

        private void UpdateBuildProgress(int currentIndex, int totalCount, string profileName)
        {
            currentProgressMessage = string.Format(ProgressMessageFormat, currentIndex, totalCount, profileName);
            var progress = totalCount > 0 ? (float)(currentIndex - 1) / totalCount : 0f;
            EditorUtility.DisplayProgressBar(ProgressTitle, currentProgressMessage, progress);
            Repaint();
        }

        private void LoadPreferences()
        {
            relativeOutputRoot = EditorPrefs.GetString(GetPreferenceKey(OutputRootPreferenceSuffix), DefaultOutputRoot);
            selectedProfileGuids.Clear();
            selectedProfileGuids.UnionWith(ParseGuidPreference(EditorPrefs.GetString(GetPreferenceKey(SelectedProfilesPreferenceSuffix), string.Empty)));
            knownProfileGuids.Clear();
            knownProfileGuids.UnionWith(ParseGuidPreference(EditorPrefs.GetString(GetPreferenceKey(KnownProfilesPreferenceSuffix), string.Empty)));
        }

        private void SavePreferences()
        {
            EditorPrefs.SetString(GetPreferenceKey(OutputRootPreferenceSuffix), relativeOutputRoot);
            EditorPrefs.SetString(GetPreferenceKey(SelectedProfilesPreferenceSuffix), string.Join(PreferenceValueSeparator, selectedProfileGuids.OrderBy(guid => guid, StringComparer.Ordinal)));
            EditorPrefs.SetString(GetPreferenceKey(KnownProfilesPreferenceSuffix), string.Join(PreferenceValueSeparator, knownProfileGuids.OrderBy(guid => guid, StringComparer.Ordinal)));
        }

        private void RevealLatestReport()
        {
            if (latestRunResult == null || string.IsNullOrEmpty(latestRunResult.CsvReportPath) || !File.Exists(latestRunResult.CsvReportPath)) return;
            EditorUtility.RevealInFinder(latestRunResult.CsvReportPath);
        }

        private void SelectAllProfiles()
        {
            selectedProfileGuids.Clear();
            selectedProfileGuids.UnionWith(profiles.Select(profile => profile.AssetGuid));
            SavePreferences();
        }

        private void SelectNoProfiles()
        {
            selectedProfileGuids.Clear();
            SavePreferences();
        }

        private void SetProfileSelection(string assetGuid, bool isSelected)
        {
            if (isSelected)
            {
                selectedProfileGuids.Add(assetGuid);
            }
            else
            {
                selectedProfileGuids.Remove(assetGuid);
            }

            SavePreferences();
        }

        private List<CleanBuildProfileDescriptor> GetSelectedProfiles()
        {
            return profiles.Where(profile => selectedProfileGuids.Contains(profile.AssetGuid)).ToList();
        }

        private static IEnumerable<string> ParseGuidPreference(string serializedGuids)
        {
            if (string.IsNullOrEmpty(serializedGuids)) return Array.Empty<string>();
            return serializedGuids.Split(new[] { PreferenceValueSeparator }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string GetPreferenceKey(string suffix)
        {
            return string.Concat(PreferencePrefix, ".", PlayerSettings.productGUID, suffix);
        }
    }
}
