using System.Collections.Generic;
using System.Text.Json.Serialization;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AiProviderSettings))]
[JsonSerializable(typeof(List<AiProviderSettings>))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(OpcUaNodeConfig))]
[JsonSerializable(typeof(UsersFile))]
[JsonSerializable(typeof(TasksFile))]
[JsonSerializable(typeof(ActivityLogFile))]
[JsonSerializable(typeof(List<ActivityLog>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext { }
