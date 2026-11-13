// Porchlight on the desktop starts here, not in the game: Velopack installs
// this launcher beside the Unity player and keeps both up to date from the
// repository's GitHub releases (a new one, 0.0.<build>, for every push to main).
//
//   1. Velopack's startup: install/uninstall hooks, and an update downloaded
//      last time is applied now (Velopack then starts us again, on it).
//   2. A copy of us goes off in the background to look for a newer release and
//      download it (only the difference, where it can), for next time.
//   3. We wait a few seconds for it: a release it has downloaded by then is
//      applied straight away and the app starts again on it, so opening the
//      app once is enough to be on the newest release (not once to download
//      and again to apply). A slow download carries on in the background, for
//      the next start.
//   4. The game: on Windows, started beside us; on macOS, in our place (exec),
//      so the Dock shows the one app and it's the game.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace Porchlight.Launcher;

static class Program
{
    const string REPO = "https://github.com/joe307bad/porchlight";
    const string FETCH = "--fetch-update";
    /// <summary>The fetch's exit code when it has downloaded a release that's waiting to be applied.</summary>
    const int DOWNLOADED = 3;
    /// <summary>How long a start waits for the fetch before playing anyway (a delta is a few seconds).</summary>
    const int WAIT_MS = 12000;

    [STAThread]
    static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        if (args.Length > 0 && args[0] == FETCH) return Fetch();
        var fetch = StartFetch();
        if (fetch != null && fetch.WaitForExit(WAIT_MS) && fetch.ExitCode == DOWNLOADED) ApplyNow(args);
        return Play(args);
    }

    static UpdateManager Updates()
    {
        // PORCHLIGHT_UPDATES: a folder or address of releases to use instead (for trying an update out).
        string? other = Environment.GetEnvironmentVariable("PORCHLIGHT_UPDATES");
        return string.IsNullOrEmpty(other) ? new UpdateManager(new GithubSource(REPO, null, false)) : new UpdateManager(other);
    }

    /// <summary>The release the fetch just downloaded, applied now: Velopack swaps it in and starts the app again (this process ends here).</summary>
    static void ApplyNow(string[] args)
    {
        try
        {
            var updates = Updates();
            var pending = updates.UpdatePendingRestart;
            if (pending != null) updates.ApplyUpdatesAndRestart(pending, args);
        }
        catch (Exception e)
        {
            // It's still downloaded: VelopackApp applies it at the next start.
            Console.Error.WriteLine("porchlight: couldn't apply the update now: " + e.Message);
        }
    }

    /// <summary>The newest release, downloaded and waiting to be applied (exit code DOWNLOADED); nothing if there's none or we're not installed (run from a build folder).</summary>
    static int Fetch()
    {
        try
        {
            var updates = Updates();
            if (!updates.IsInstalled) return 0;
            var newer = updates.CheckForUpdates();
            if (newer == null) return 0;
            updates.DownloadUpdates(newer);
            Console.WriteLine($"porchlight: {newer.TargetFullRelease.Version} downloaded");
            return DOWNLOADED;
        }
        catch (Exception e)
        {
            // Offline, rate limited, or another copy already at it: try again next start.
            Console.Error.WriteLine("porchlight: no update this time: " + e.Message);
        }
        return 0;
    }

    static Process? StartFetch()
    {
        try
        {
            string? self = Environment.ProcessPath;
            if (self == null) return null;
            return Process.Start(new ProcessStartInfo(self, FETCH) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("porchlight: couldn't look for updates: " + e.Message);
            return null;
        }
    }

    static int Play(string[] args)
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(Path.Combine(dir, "Porchlight.exe")) { UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in args) start.ArgumentList.Add(a);
            Process.Start(start);
            return 0;
        }
        // macOS: the Unity player beside us in Contents/MacOS, becoming this process.
        string game = Path.Combine(dir, "Porchlight");
        var argv = new IntPtr[args.Length + 2];
        argv[0] = Marshal.StringToCoTaskMemUTF8(game);
        for (int i = 0; i < args.Length; i++) argv[i + 1] = Marshal.StringToCoTaskMemUTF8(args[i]);
        execv(game, argv);
        Console.Error.WriteLine($"porchlight: couldn't start {game} (errno {Marshal.GetLastPInvokeError()})");
        return 1;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr[] argv);
}
