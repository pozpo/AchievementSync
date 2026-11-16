namespace AchievementSync.Core.Parsing;

public interface IAchievementParser
{
    string FormatName { get; }
    bool SupportsFile(string path);
    ParseResult Parse(string path, string appId);
}
