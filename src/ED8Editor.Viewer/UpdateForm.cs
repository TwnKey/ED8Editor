using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using ED8Editor.Application;

namespace ED8Editor.Viewer;

/// <summary>
/// Offers a newer build, shows what changed in it, and installs it.
///
/// The changelog is the point. An updater that says "a new version is available"
/// asks someone to accept a change they cannot see; one that shows the release notes
/// asks them to accept a change they can read. The notes are the author's own words,
/// shown unaltered.
///
/// A running program cannot overwrite itself, so the new files are unpacked beside
/// the old ones and a small script does the swap once this process has ended. That
/// script is the only part that has to be right when nothing is watching, so it does
/// as little as possible: wait, move, start, delete itself.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly GitHubRelease release;
    private readonly TextBox notes = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9.5f),
        BackColor = Color.FromArgb(24, 24, 28),
        ForeColor = Color.Gainsboro,
    };

    private readonly ProgressBar progress = new()
    {
        Dock = DockStyle.Bottom,
        Height = 18,
        Visible = false,
    };

    private readonly Label status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 24,
        AutoEllipsis = true,
        ForeColor = Color.Gainsboro,
    };

    private readonly Button install;

    public UpdateForm(GitHubRelease release, Version running)
    {
        this.release = release ?? throw new ArgumentNullException(nameof(release));

        Text = $"Update available — {release.Name}";
        Width = 720;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 30, 34);

        notes.Text = release.Notes.Length == 0
            ? "This release came with no notes."
            : release.Notes.Replace("\n", Environment.NewLine);

        install = new Button
        {
            Text = release.DownloadUrl is null ? "No build attached" : "Install and restart",
            AutoSize = true,
            Enabled = release.DownloadUrl is not null,
        };
        var later = new Button
        {
            Text = "Later",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        var page = new Button { Text = "Open the release page", AutoSize = true };
        page.Click += (_, _) => Browse(
            $"https://github.com/{GitHubUpdateCheck.Repository}/releases/tag/{release.Tag}");
        install.Click += async (_, _) => await InstallAsync();

        var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        tools.Controls.AddRange(new Control[] { install, page, later });

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            ForeColor = Color.Gainsboro,
            Padding = new Padding(6, 8, 0, 0),
            Text = $"{running} installed   →   {release.Version} available"
                + (release.DownloadSize == 0
                    ? string.Empty
                    : $"   ({release.DownloadSize / 1024 / 1024.0:0.0} MB)"),
        };

        Controls.Add(notes);
        Controls.Add(heading);
        Controls.Add(tools);
        Controls.Add(progress);
        Controls.Add(status);
        CancelButton = later;
    }

    private async Task InstallAsync()
    {
        if (release.DownloadUrl is not { } url) return;
        install.Enabled = false;
        progress.Visible = true;
        progress.Style = ProgressBarStyle.Marquee;
        status.Text = "Downloading…";
        try
        {
            var staging = Path.Combine(
                Path.GetTempPath(), $"ed8editor-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "update.zip");

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var stream = await client.GetStreamAsync(url))
            using (var file = File.Create(archive))
            {
                await stream.CopyToAsync(file);
            }

            status.Text = "Unpacking…";
            var unpacked = Path.Combine(staging, "files");
            ZipFile.ExtractToDirectory(archive, unpacked);

            // Some archives carry a single folder holding everything; some do not.
            var payload = Directory.GetFiles(unpacked).Length == 0
                && Directory.GetDirectories(unpacked) is { Length: 1 } only
                    ? only[0]
                    : unpacked;

            var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var script = WriteSwapScript(payload, target, Environment.ProcessId);
            status.Text = "Restarting…";
            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            System.Windows.Forms.Application.Exit();
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException
            or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            progress.Visible = false;
            install.Enabled = true;
            status.Text = "The update failed: " + failure.Message;
        }
    }

    /// <summary>
    /// The script that swaps the files in once this process has gone.
    ///
    /// It waits for the process to end rather than for a fixed delay: a delay long
    /// enough to be safe is long enough to look broken, and one short enough to feel
    /// quick copies over files that are still open.
    /// </summary>
    private static string WriteSwapScript(string payload, string target, int processId)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ed8editor-update-{Guid.NewGuid():N}.cmd");
        var executable = Path.Combine(target, "ED8Editor.Viewer.exe");
        File.WriteAllText(path, $"""
            @echo off
            :wait
            tasklist /fi "PID eq {processId}" | find "{processId}" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait
            )
            xcopy "{payload}\*" "{target}\" /e /y /i >nul
            start "" "{executable}"
            del "%~f0"
            """);
        return path;
    }

    private static void Browse(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException
            or System.ComponentModel.Win32Exception)
        {
            // A machine with no browser association is not a reason to fall over.
        }
    }

    /// <summary>
    /// Looks for a newer build and offers it, or says nothing at all.
    ///
    /// Nothing is reported when the check fails: no network, a rate limit, or a
    /// repository that has published nothing yet all mean the same thing to someone
    /// starting the tool, which is that it starts.
    /// </summary>
    public static async Task OfferAsync(IWin32Window owner)
    {
        var running = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var release = await GitHubUpdateCheck.FetchLatestAsync(client);
        if (release is null || !GitHubUpdateCheck.IsNewer(release, running)) return;
        using var form = new UpdateForm(release, running);
        form.ShowDialog(owner);
    }
}
