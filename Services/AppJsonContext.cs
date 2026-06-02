using System.Text.Json.Serialization;

namespace TLIGDashboard.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(OpcUaNodeConfig))]
[JsonSerializable(typeof(UsersFile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext { }
