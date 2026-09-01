using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CoreCLRTest.Build
{
    internal static class CleanBuildProfilesRunner
    {
        private const string BuildProfileSearchFilter = "t:BuildProfile";
        private const string ProfileProductNamePattern = "^\\s*productName:\\s*(?<value>.+?)\\s*$";
        private const string BuildTargetPropertyName = "m_BuildTarget";
        private const string DefaultPlayerName = "Player";
        private const string MissingProfileName = "Missing Profile";
        private const string ReportsDirectoryName = "BuildReports";
        private const string ReportFileNameFormat = "CleanBuildProfiles-{0}.csv";
        private const string ReportTimestampFormat = "yyyyMMdd-HHmmss-fff";
        private const string CsvHeader = "Profile,ProfileAssetPath,Target,Result,Elapsed,OutputPath,TotalSizeBytes,Warnings,Errors,Message";
        private const string AggregateProfileName = "TOTAL";
        private const string AggregateResultName = "Aggregate";
        private const string AssetsDirectoryName = "Assets"; 
        private const string WindowsExtension = ".exe";
        private const string MacOsExtension = ".app";
        private const string LinuxExtension = ".x86_64";
        private const string AdditionalInvalidFileNameCharacters = "<>:\"/\\|?*";
        private const string ElapsedTimeFormat = "{0:00}:{1:00}:{2:00}.{3:000}";
        private const string BuildStartFormat = "Clean build {0}/{1}: '{2}' ({3}) -> {4}";
        private const string BuildResultFormat = "Clean build {0}: '{1}' in {2} -> {3}";
        private const string RunSummaryFormat = "Clean build run finished in {0}. Succeeded: {1}, Failed: {2}, Cancelled: {3}, Skipped: {4}.";
        private const string ReportLogFormat = "Clean build CSV report: {0}";
        private const string ReportErrorFormat = "Clean build CSV report could not be written: {0}";
        private const string AggregateMessageFormat = "Succeeded: {0}; Failed: {1}; Cancelled: {2}; Skipped: {3}";

        internal static IReadOnlyList<CleanBuildProfileDescriptor> DiscoverProfiles()
        {
            var descriptors = new List<CleanBuildProfileDescriptor>();
            var assetGuids = AssetDatabase.FindAssets(BuildProfileSearchFilter, new[] { AssetsDirectoryName });
            foreach (var assetGuid in assetGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                var buildProfile = AssetDatabase.LoadAssetAtPath<BuildProfile>(assetPath);
                var displayName = buildProfile != null ? buildProfile.name : Path.GetFileNameWithoutExtension(assetPath);
                var buildTarget = BuildTarget.NoTarget;
                var validationMessage = string.Empty;
                if (buildProfile == null)
                {
                    validationMessage = "The BuildProfile asset could not be loaded.";
                }
                else if (!TryGetBuildTarget(buildProfile, out buildTarget, out validationMessage))
                {
                }
                else if (!IsRecognizedStandaloneTarget(buildTarget))
                {
                    validationMessage = string.Format(CultureInfo.InvariantCulture, "Build target '{0}' is not supported by this tool.", buildTarget);
                }
                else if (!IsSupportedStandaloneTarget(buildTarget))
                {
                    validationMessage = string.Format(CultureInfo.InvariantCulture, "The platform module for '{0}' is not installed or supported by this editor.", buildTarget);
                }

                descriptors.Add(new CleanBuildProfileDescriptor(buildProfile, assetPath, assetGuid, displayName, buildTarget, validationMessage));
            }

            return descriptors.OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(descriptor => descriptor.AssetPath, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
        }

        internal static bool TryValidateOutputRoot(string relativeOutputRoot, out string errorMessage)
        {
            return TryGetAbsoluteOutputRoot(relativeOutputRoot, out _, out errorMessage);
        }

        internal static CleanBuildRunResult BuildSelectedProfiles(IReadOnlyList<CleanBuildProfileDescriptor> profiles, string relativeOutputRoot, Action<int, int, string> progressCallback)
        {
            if (profiles == null)
            {
                throw new ArgumentNullException(nameof(profiles));
            }

            if (profiles.Count == 0)
            {
                throw new ArgumentException("Select at least one build profile.", nameof(profiles));
            }

            if (!TryGetAbsoluteOutputRoot(relativeOutputRoot, out var absoluteOutputRoot, out var rootErrorMessage))
            {
                throw new ArgumentException(rootErrorMessage, nameof(relativeOutputRoot));
            }

            var results = new List<CleanBuildProfileResult>();
            var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runStopwatch = Stopwatch.StartNew();
            var csvReportPath = string.Empty;
            var csvReportErrorMessage = string.Empty;
            try
            {
                for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
                {
                    var profile = profiles[profileIndex];
                    var profileName = profile != null ? profile.DisplayName : MissingProfileName;
                    try
                    {
                        progressCallback?.Invoke(profileIndex + 1, profiles.Count, profileName);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }

                    var result = default(CleanBuildProfileResult);
                    try
                    {
                        result = BuildProfile(profile, relativeOutputRoot, absoluteOutputRoot, profileIndex + 1, profiles.Count, reservedOutputPaths);
                    }
                    catch (Exception exception)
                    {
                        var profileAssetPath = profile != null ? profile.AssetPath : string.Empty;
                        var buildTarget = profile != null ? profile.BuildTarget : BuildTarget.NoTarget;
                        result = new CleanBuildProfileResult(profileName, profileAssetPath, buildTarget, CleanBuildProfileStatus.Failed, TimeSpan.Zero, string.Empty, 0, 0, 1, exception.ToString());
                        LogProfileResult(result);
                    }

                    results.Add(result);
                    if (result.Status == CleanBuildProfileStatus.Cancelled) break;
                }
            }
            finally
            {
                runStopwatch.Stop();
                try
                {
                    csvReportPath = CreateCsvReport(results, runStopwatch.Elapsed, absoluteOutputRoot);
                }
                catch (Exception exception)
                {
                    csvReportErrorMessage = exception.Message;
                    Debug.LogError(string.Format(CultureInfo.InvariantCulture, ReportErrorFormat, exception));
                }
            }

            var runResult = new CleanBuildRunResult(results, runStopwatch.Elapsed, absoluteOutputRoot, csvReportPath, csvReportErrorMessage);
            LogRunSummary(runResult);
            return runResult;
        }

        private static CleanBuildProfileResult BuildProfile(CleanBuildProfileDescriptor profile, string relativeOutputRoot, string absoluteOutputRoot, int currentIndex, int totalCount, ISet<string> reservedOutputPaths)
        {
            if (profile == null)
            {
                return CreateSkippedResult(MissingProfileName, string.Empty, BuildTarget.NoTarget, string.Empty, "The selected profile descriptor is null.");
            }

            if (profile.BuildProfile == null)
            {
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, profile.BuildTarget, string.Empty, "The BuildProfile asset could not be loaded.");
            }

            if (!TryGetBuildTarget(profile.BuildProfile, out var buildTarget, out var targetErrorMessage))
            {
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, BuildTarget.NoTarget, string.Empty, targetErrorMessage);
            }

            if (!IsRecognizedStandaloneTarget(buildTarget))
            {
                var message = string.Format(CultureInfo.InvariantCulture, "Build target '{0}' is not supported by this tool.", buildTarget);
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, buildTarget, string.Empty, message);
            }

            if (!IsSupportedStandaloneTarget(buildTarget))
            {
                var message = string.Format(CultureInfo.InvariantCulture, "The platform module for '{0}' is not installed or supported by this editor.", buildTarget);
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, buildTarget, string.Empty, message);
            }

            var resolvedProfile = new CleanBuildProfileDescriptor(profile.BuildProfile, profile.AssetPath, profile.AssetGuid, profile.DisplayName, buildTarget, string.Empty);
            if (!TryCreateOutputPath(resolvedProfile, relativeOutputRoot, out var outputPath, out var outputErrorMessage))
            {
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, buildTarget, string.Empty, outputErrorMessage);
            }

            if (!IsPathInsideRoot(outputPath, absoluteOutputRoot))
            {
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, buildTarget, outputPath, "The generated output path is outside the configured output root.");
            }

            if (!reservedOutputPaths.Add(outputPath))
            {
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, buildTarget, outputPath, "Another selected profile resolves to the same output path.");
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, buildTarget, outputPath, "The output directory could not be determined.");
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception exception)
            {
                return CreateSkippedResult(profile.DisplayName, profile.AssetPath, buildTarget, outputPath, string.Format(CultureInfo.InvariantCulture, "The output directory could not be created: {0}", exception.Message));
            }

            Debug.Log(string.Format(CultureInfo.InvariantCulture, BuildStartFormat, currentIndex, totalCount, profile.DisplayName, buildTarget, outputPath));
            var profileStopwatch = Stopwatch.StartNew();
            try
            {
                var buildOptions = new BuildPlayerWithProfileOptions
                {
                    buildProfile = profile.BuildProfile,
                    locationPathName = outputPath,
                    options = BuildOptions.CleanBuildCache
                };
                var report = BuildPipeline.BuildPlayer(buildOptions);
                profileStopwatch.Stop();
                if (report == null)
                {
                    var nullReportMessage = "Unity returned no BuildReport.";
                    var nullReportResult = new CleanBuildProfileResult(profile.DisplayName, profile.AssetPath, buildTarget, CleanBuildProfileStatus.Failed, profileStopwatch.Elapsed, outputPath, 0, 0, 1, nullReportMessage);
                    LogProfileResult(nullReportResult);
                    return nullReportResult;
                }

                var summary = report.summary;
                var elapsed = summary.totalTime > TimeSpan.Zero ? summary.totalTime : profileStopwatch.Elapsed;
                var reportedOutputPath = string.IsNullOrEmpty(summary.outputPath) ? outputPath : summary.outputPath;
                var status = ConvertBuildResult(summary.result);
                var message = GetBuildResultMessage(summary.result);
                var result = new CleanBuildProfileResult(profile.DisplayName, profile.AssetPath, buildTarget, status, elapsed, reportedOutputPath, summary.totalSize, summary.totalWarnings, summary.totalErrors, message);
                LogProfileResult(result);
                return result;
            }
            catch (Exception exception)
            {
                profileStopwatch.Stop();
                var result = new CleanBuildProfileResult(profile.DisplayName, profile.AssetPath, buildTarget, CleanBuildProfileStatus.Failed, profileStopwatch.Elapsed, outputPath, 0, 0, 1, exception.ToString());
                LogProfileResult(result);
                return result;
            }
        }

        private static bool TryGetBuildTarget(BuildProfile buildProfile, out BuildTarget buildTarget, out string errorMessage)
        {
            buildTarget = BuildTarget.NoTarget;
            errorMessage = string.Empty;
            if (buildProfile == null)
            {
                errorMessage = "The BuildProfile asset is null.";
                return false;
            }

            var serializedProfile = new SerializedObject(buildProfile);
            serializedProfile.UpdateIfRequiredOrScript();
            var buildTargetProperty = serializedProfile.FindProperty(BuildTargetPropertyName);
            if (buildTargetProperty == null)
            {
                errorMessage = string.Format(CultureInfo.InvariantCulture, "Serialized property '{0}' was not found.", BuildTargetPropertyName);
                return false;
            }

            var rawBuildTarget = buildTargetProperty.intValue;
            if (rawBuildTarget < int.MinValue || rawBuildTarget > int.MaxValue)
            {
                errorMessage = string.Format(CultureInfo.InvariantCulture, "Serialized build target value '{0}' is outside the supported integer range.", rawBuildTarget);
                return false;
            }

            buildTarget = (BuildTarget)(int)rawBuildTarget;
            if (buildTarget == BuildTarget.NoTarget || !Enum.IsDefined(typeof(BuildTarget), buildTarget))
            {
                errorMessage = string.Format(CultureInfo.InvariantCulture, "Serialized build target value '{0}' is undefined.", rawBuildTarget);
                buildTarget = BuildTarget.NoTarget;
                return false;
            }

            return true;
        }

        private static bool TryCreateOutputPath(CleanBuildProfileDescriptor profile, string relativeOutputRoot, out string outputPath, out string errorMessage)
        {
            outputPath = string.Empty;
            if (profile == null)
            {
                errorMessage = "The profile descriptor is null.";
                return false;
            }

            if (!TryGetAbsoluteOutputRoot(relativeOutputRoot, out var absoluteOutputRoot, out errorMessage))
            {
                return false;
            }

            if (!TryGetPlatformExtension(profile.BuildTarget, out var platformExtension))
            {
                errorMessage = string.Format(CultureInfo.InvariantCulture, "Build target '{0}' does not have a supported output extension.", profile.BuildTarget);
                return false;
            }

            var sanitizedProfileName = SanitizePathSegment(profile.DisplayName, MissingProfileName);
            var sanitizedProductName = SanitizePathSegment(GetProfileProductName(profile), DefaultPlayerName);
            outputPath = Path.GetFullPath(Path.Combine(absoluteOutputRoot, sanitizedProfileName, sanitizedProductName + platformExtension));
            if (!IsPathInsideRoot(outputPath, absoluteOutputRoot))
            {
                outputPath = string.Empty;
                errorMessage = "The generated output path is outside the configured output root.";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        private static string GetProfileProductName(CleanBuildProfileDescriptor profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.AssetPath)) return PlayerSettings.productName;

            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot)) return PlayerSettings.productName;
                var absoluteProfilePath = Path.GetFullPath(Path.Combine(projectRoot, profile.AssetPath));
                var serializedProfile = File.ReadAllText(absoluteProfilePath);
                var productNameMatch = Regex.Match(serializedProfile, ProfileProductNamePattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
                if (!productNameMatch.Success) return PlayerSettings.productName;
                var productName = productNameMatch.Groups["value"].Value.Trim();
                if (productName.Length >= 2 && productName[0] == '"' && productName[productName.Length - 1] == '"')
                {
                    productName = productName.Substring(1, productName.Length - 2).Replace("\\\"", "\"");
                }

                return string.IsNullOrWhiteSpace(productName) ? PlayerSettings.productName : productName;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(string.Format(CultureInfo.InvariantCulture, "Product name for build profile '{0}' could not be read from '{1}': {2}", profile.DisplayName, profile.AssetPath, exception.Message));
                return PlayerSettings.productName;
            }
        }

        private static bool IsSupportedStandaloneTarget(BuildTarget buildTarget)
        {
            return IsRecognizedStandaloneTarget(buildTarget) && BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, buildTarget);
        }

        private static string CreateCsvReport(IReadOnlyList<CleanBuildProfileResult> results, TimeSpan totalElapsed, string absoluteOutputRoot)
        {
            var reportDirectory = Path.Combine(absoluteOutputRoot, ReportsDirectoryName);
            Directory.CreateDirectory(reportDirectory);
            var reportFileName = string.Format(CultureInfo.InvariantCulture, ReportFileNameFormat, DateTime.Now.ToString(ReportTimestampFormat, CultureInfo.InvariantCulture));
            var reportPath = Path.Combine(reportDirectory, reportFileName);
            var reportBuilder = new StringBuilder();
            reportBuilder.AppendLine(CsvHeader);
            foreach (var result in results)
            {
                AppendCsvRow(reportBuilder, result.ProfileName, result.ProfileAssetPath, result.Target.ToString(), result.Status.ToString(), FormatElapsedTime(result.Elapsed), result.OutputPath, result.TotalSizeBytes.ToString(CultureInfo.InvariantCulture), result.WarningCount.ToString(CultureInfo.InvariantCulture), result.ErrorCount.ToString(CultureInfo.InvariantCulture), result.Message);
            }

            var succeededCount = results.Count(result => result.Status == CleanBuildProfileStatus.Succeeded);
            var failedCount = results.Count(result => result.Status == CleanBuildProfileStatus.Failed);
            var cancelledCount = results.Count(result => result.Status == CleanBuildProfileStatus.Cancelled);
            var skippedCount = results.Count(result => result.Status == CleanBuildProfileStatus.Skipped);
            var totalSize = results.Aggregate<CleanBuildProfileResult, ulong>(0, (current, result) => current + result.TotalSizeBytes);
            var totalWarnings = results.Aggregate<CleanBuildProfileResult, int>(0, (current, result) => current + result.WarningCount);
            var totalErrors = results.Aggregate<CleanBuildProfileResult, int>(0, (current, result) => current + result.ErrorCount);
            var aggregateMessage = string.Format(CultureInfo.InvariantCulture, AggregateMessageFormat, succeededCount, failedCount, cancelledCount, skippedCount);
            AppendCsvRow(reportBuilder, AggregateProfileName, string.Empty, string.Empty, AggregateResultName, FormatElapsedTime(totalElapsed), absoluteOutputRoot, totalSize.ToString(CultureInfo.InvariantCulture), totalWarnings.ToString(CultureInfo.InvariantCulture), totalErrors.ToString(CultureInfo.InvariantCulture), aggregateMessage);
            File.WriteAllText(reportPath, reportBuilder.ToString(), new UTF8Encoding(false));
            return reportPath;
        }

        private static string EscapeCsvValue(string value)
        {
            var safeValue = value ?? string.Empty;
            if (safeValue.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            {
                return safeValue;
            }

            return string.Concat("\"", safeValue.Replace("\"", "\"\""), "\"");
        }

        private static void AppendCsvRow(StringBuilder reportBuilder, params string[] values)
        {
            reportBuilder.AppendLine(string.Join(",", values.Select(EscapeCsvValue)));
        }

        private static bool TryGetAbsoluteOutputRoot(string relativeOutputRoot, out string absoluteOutputRoot, out string errorMessage)
        {
            absoluteOutputRoot = string.Empty;
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(relativeOutputRoot))
            {
                errorMessage = "The output root cannot be empty.";
                return false;
            }

            var normalizedRoot = relativeOutputRoot.Trim().Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(normalizedRoot) || Path.IsPathRooted(normalizedRoot))
            {
                errorMessage = "The output root must be relative to the project.";
                return false;
            }

            var rootSegments = normalizedRoot.Split('/');
            foreach (var rootSegment in rootSegments)
            {
                if (string.IsNullOrEmpty(rootSegment) || rootSegment == "." || rootSegment == "..")
                {
                    errorMessage = "The output root cannot contain empty, '.' or '..' path segments.";
                    return false;
                }

                if (ContainsInvalidFileNameCharacter(rootSegment))
                {
                    errorMessage = string.Format(CultureInfo.InvariantCulture, "Output root segment '{0}' contains invalid filename characters.", rootSegment);
                    return false;
                }
            }

            if (string.Equals(rootSegments[0], AssetsDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "The output root must be outside the Assets directory.";
                return false;
            }

            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot))
                {
                    errorMessage = "The Unity project root could not be determined.";
                    return false;
                }

                absoluteOutputRoot = Path.GetFullPath(Path.Combine(projectRoot, Path.Combine(rootSegments)));
                if (!IsPathInsideRoot(absoluteOutputRoot, projectRoot))
                {
                    absoluteOutputRoot = string.Empty;
                    errorMessage = "The output root resolves outside the Unity project.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                absoluteOutputRoot = string.Empty;
                errorMessage = string.Format(CultureInfo.InvariantCulture, "The output root is invalid: {0}", exception.Message);
                return false;
            }

            return true;
        }

        private static bool IsRecognizedStandaloneTarget(BuildTarget buildTarget)
        {
            return buildTarget == BuildTarget.StandaloneWindows64 || buildTarget == BuildTarget.StandaloneOSX || buildTarget == BuildTarget.StandaloneLinux64;
        }

        private static bool TryGetPlatformExtension(BuildTarget buildTarget, out string platformExtension)
        {
            switch (buildTarget)
            {
                case BuildTarget.StandaloneWindows64:
                    platformExtension = WindowsExtension;
                    return true;
                case BuildTarget.StandaloneOSX:
                    platformExtension = MacOsExtension;
                    return true;
                case BuildTarget.StandaloneLinux64:
                    platformExtension = LinuxExtension;
                    return true;
                default:
                    platformExtension = string.Empty;
                    return false;
            }
        }

        private static string SanitizePathSegment(string value, string fallbackValue)
        {
            var sourceValue = string.IsNullOrWhiteSpace(value) ? fallbackValue : value.Trim();
            var sanitizedBuilder = new StringBuilder(sourceValue.Length);
            foreach (var character in sourceValue)
            {
                sanitizedBuilder.Append(ContainsInvalidFileNameCharacter(character) ? '_' : character);
            }

            var sanitizedValue = sanitizedBuilder.ToString().Trim().TrimEnd('.');
            return string.IsNullOrEmpty(sanitizedValue) ? fallbackValue : sanitizedValue;
        }

        private static bool ContainsInvalidFileNameCharacter(string value)
        {
            return value.Any(ContainsInvalidFileNameCharacter);
        }

        private static bool ContainsInvalidFileNameCharacter(char character)
        {
            return character < ' ' || AdditionalInvalidFileNameCharacters.IndexOf(character) >= 0 || Path.GetInvalidFileNameChars().Contains(character);
        }

        private static bool IsPathInsideRoot(string candidatePath, string rootPath)
        {
            var normalizedCandidatePath = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedCandidatePath, normalizedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rootPrefix = normalizedRootPath + Path.DirectorySeparatorChar;
            return normalizedCandidatePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static CleanBuildProfileResult CreateSkippedResult(string profileName, string profileAssetPath, BuildTarget target, string outputPath, string message)
        {
            var result = new CleanBuildProfileResult(profileName, profileAssetPath, target, CleanBuildProfileStatus.Skipped, TimeSpan.Zero, outputPath, 0, 0, 1, message);
            LogProfileResult(result);
            return result;
        }

        private static CleanBuildProfileStatus ConvertBuildResult(BuildResult buildResult)
        {
            switch (buildResult)
            {
                case BuildResult.Succeeded:
                    return CleanBuildProfileStatus.Succeeded;
                case BuildResult.Cancelled:
                    return CleanBuildProfileStatus.Cancelled;
                case BuildResult.Failed:
                case BuildResult.Unknown:
                default:
                    return CleanBuildProfileStatus.Failed;
            }
        }

        private static string GetBuildResultMessage(BuildResult buildResult)
        {
            return buildResult == BuildResult.Unknown ? "Unity reported an unknown build result." : buildResult.ToString();
        }

        private static void LogProfileResult(CleanBuildProfileResult result)
        {
            var message = string.Format(CultureInfo.InvariantCulture, BuildResultFormat, result.Status, result.ProfileName, FormatElapsedTime(result.Elapsed), result.OutputPath);
            if (!string.IsNullOrEmpty(result.Message) && result.Status != CleanBuildProfileStatus.Succeeded)
            {
                message = string.Concat(message, Environment.NewLine, result.Message);
            }

            switch (result.Status)
            {
                case CleanBuildProfileStatus.Succeeded:
                    Debug.Log(message);
                    break;
                case CleanBuildProfileStatus.Cancelled:
                case CleanBuildProfileStatus.Skipped:
                    Debug.LogWarning(message);
                    break;
                default:
                    Debug.LogError(message);
                    break;
            }
        }

        private static void LogRunSummary(CleanBuildRunResult runResult)
        {
            var summaryMessage = string.Format(CultureInfo.InvariantCulture, RunSummaryFormat, FormatElapsedTime(runResult.TotalElapsed), runResult.SucceededCount, runResult.FailedCount, runResult.CancelledCount, runResult.SkippedCount);
            if (runResult.FailedCount > 0 || !string.IsNullOrEmpty(runResult.CsvReportErrorMessage))
            {
                Debug.LogError(summaryMessage);
            }
            else if (runResult.CancelledCount > 0 || runResult.SkippedCount > 0)
            {
                Debug.LogWarning(summaryMessage);
            }
            else
            {
                Debug.Log(summaryMessage);
            }

            if (!string.IsNullOrEmpty(runResult.CsvReportPath))
            {
                Debug.Log(string.Format(CultureInfo.InvariantCulture, ReportLogFormat, runResult.CsvReportPath));
            }
        }

        internal static string FormatElapsedTime(TimeSpan elapsed)
        {
            return string.Format(CultureInfo.InvariantCulture, ElapsedTimeFormat, (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds, elapsed.Milliseconds);
        }
    }

    internal enum CleanBuildProfileStatus
    {
        Succeeded,
        Failed,
        Cancelled,
        Skipped
    }

    internal sealed class CleanBuildProfileDescriptor
    {
        internal CleanBuildProfileDescriptor(BuildProfile buildProfile, string assetPath, string assetGuid, string displayName, BuildTarget buildTarget, string validationMessage)
        {
            BuildProfile = buildProfile;
            AssetPath = assetPath;
            AssetGuid = assetGuid;
            DisplayName = displayName;
            BuildTarget = buildTarget;
            ValidationMessage = validationMessage;
        }

        internal BuildProfile BuildProfile { get; }
        internal string AssetPath { get; }
        internal string AssetGuid { get; }
        internal string DisplayName { get; }
        internal BuildTarget BuildTarget { get; }
        internal string ValidationMessage { get; }
        internal bool IsValid => BuildProfile != null && string.IsNullOrEmpty(ValidationMessage);
    }

    internal sealed class CleanBuildProfileResult
    {
        internal CleanBuildProfileResult(string profileName, string profileAssetPath, BuildTarget target, CleanBuildProfileStatus status, TimeSpan elapsed, string outputPath, ulong totalSizeBytes, int warningCount, int errorCount, string message)
        {
            ProfileName = profileName;
            ProfileAssetPath = profileAssetPath;
            Target = target;
            Status = status;
            Elapsed = elapsed;
            OutputPath = outputPath;
            TotalSizeBytes = totalSizeBytes;
            WarningCount = warningCount;
            ErrorCount = errorCount;
            Message = message;
        }

        internal string ProfileName { get; }
        internal string ProfileAssetPath { get; }
        internal BuildTarget Target { get; }
        internal CleanBuildProfileStatus Status { get; }
        internal TimeSpan Elapsed { get; }
        internal string OutputPath { get; }
        internal ulong TotalSizeBytes { get; }
        internal int WarningCount { get; }
        internal int ErrorCount { get; }
        internal string Message { get; }
    }

    internal sealed class CleanBuildRunResult
    {
        internal CleanBuildRunResult(IReadOnlyList<CleanBuildProfileResult> results, TimeSpan totalElapsed, string absoluteOutputRoot, string csvReportPath, string csvReportErrorMessage)
        {
            Results = new List<CleanBuildProfileResult>(results).AsReadOnly();
            TotalElapsed = totalElapsed;
            AbsoluteOutputRoot = absoluteOutputRoot;
            CsvReportPath = csvReportPath;
            CsvReportErrorMessage = csvReportErrorMessage;
            SucceededCount = Results.Count(result => result.Status == CleanBuildProfileStatus.Succeeded);
            FailedCount = Results.Count(result => result.Status == CleanBuildProfileStatus.Failed);
            CancelledCount = Results.Count(result => result.Status == CleanBuildProfileStatus.Cancelled);
            SkippedCount = Results.Count(result => result.Status == CleanBuildProfileStatus.Skipped);
        }

        internal IReadOnlyList<CleanBuildProfileResult> Results { get; }
        internal TimeSpan TotalElapsed { get; }
        internal string AbsoluteOutputRoot { get; }
        internal string CsvReportPath { get; }
        internal string CsvReportErrorMessage { get; }
        internal int SucceededCount { get; }
        internal int FailedCount { get; }
        internal int CancelledCount { get; }
        internal int SkippedCount { get; }
    }
}
