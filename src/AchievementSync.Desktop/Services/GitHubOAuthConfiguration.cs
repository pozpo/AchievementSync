using System.Reflection;

namespace AchievementSync.Desktop.Services;

internal static class GitHubOAuthConfiguration
{
    public static string ClientId => typeof(GitHubOAuthConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "AchievementSync.GitHubOAuthClientId")?.Value
        ?? Environment.GetEnvironmentVariable("ACHIEVEMENTSYNC_GITHUB_CLIENT_ID")
        ?? string.Empty;
}
