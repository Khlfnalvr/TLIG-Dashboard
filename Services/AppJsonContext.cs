using System.Collections.Generic;
using System.Text.Json.Serialization;

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
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext { }
