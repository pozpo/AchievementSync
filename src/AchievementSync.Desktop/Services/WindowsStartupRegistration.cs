using Microsoft.Win32;
using System.IO;

namespace AchievementSync.Desktop.Services;

public interface IStartupRegistration
{
    void SetEnabled(bool enabled);
}

/// <summary>Manages only the current user's AchievementSync Run-key entry.</summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AchievementSync";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new UnauthorizedAccessException("The current user's startup settings are unavailable.");
        var existing = key.GetValue(ValueName) as string;
        if (enabled)
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
                throw new InvalidOperationException("AchievementSync could not identify its executable path.");
            var command = $"\"{executable}\" --tray";
            if (!string.IsNullOrWhiteSpace(existing) && !IsOurCommand(existing, executable!))
                throw new InvalidOperationException("A different startup entry already uses the AchievementSync name.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else if (!string.IsNullOrWhiteSpace(existing))
        {
            var currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable) || !IsOurCommand(existing, currentExecutable))
                throw new InvalidOperationException("The existing startup entry was left unchanged because it does not point to this AchievementSync executable.");
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static bool IsOurCommand(string command, string executable)
    {
        var prefix = $"\"{executable}\"";
        return command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (command.Length == prefix.Length || char.IsWhiteSpace(command[prefix.Length]));
    }
}

public sealed class AchievementTrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _pauseItem;
    private bool _disposed;

    public AchievementTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip { ShowImageMargin = false };
        var open = new System.Windows.Forms.ToolStripMenuItem("Open AchievementSync");
        _pauseItem = new System.Windows.Forms.ToolStripMenuItem("Pause Monitoring");
        var exit = new System.Windows.Forms.ToolStripMenuItem("Exit");
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _pauseItem.Click += (_, _) => PauseToggleRequested?.Invoke(this, EventArgs.Empty);
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        var executable = Environment.ProcessPath;
        var icon = !string.IsNullOrWhiteSpace(executable)
            ? System.Drawing.Icon.ExtractAssociatedIcon(executable)
            : null;
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "AchievementSync · Monitoring",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += OnDoubleClick;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? PauseToggleRequested;
    public event EventHandler? ExitRequested;

    public void SetPaused(bool paused)
    {
        _pauseItem.Text = paused ? "Resume Monitoring" : "Pause Monitoring";
        _icon.Text = paused ? "AchievementSync · Paused" : "AchievementSync · Monitoring";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.DoubleClick -= OnDoubleClick;
        var menu = _icon.ContextMenuStrip;
        _icon.Visible = false;
        _icon.Dispose();
        menu?.Dispose();
    }

    private void OnDoubleClick(object? sender, EventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
}
