using System.Diagnostics;

namespace PKTracker.Data;

/// <summary>
/// Портативный PostgreSQL рядом с трекером (%LOCALAPPDATA%\PKTracker\pgsql + pgdata): поднимаем сами,
/// чтобы и приложение, и MCP-сервер работали без установленной службы и без запуска друг друга.
/// </summary>
public static class LocalPostgres
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PKTracker");
    public static string BinFolder => Path.Combine(Root, "pgsql", "bin");
    public static string DataFolder => Path.Combine(Root, "pgdata");

    public static bool IsInstalled => File.Exists(Path.Combine(BinFolder, "pg_ctl.exe")) && Directory.Exists(DataFolder);

    /// <summary>Запускает кластер, если он установлен локально и ещё не работает. Возвращает сообщение или null.</summary>
    public static string? EnsureRunning(TimeSpan? timeout = null)
    {
        if (!IsInstalled) return null;
        if (IsRunning()) return null;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(BinFolder, "pg_ctl.exe"),
                $"-D \"{DataFolder}\" -l \"{Path.Combine(Root, "postgres.log")}\" -w start")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "не удалось запустить pg_ctl";
            p.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds);
            return IsRunning() ? "запущен локальный PostgreSQL" : "PostgreSQL не ответил после запуска";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "не удалось запустить локальный PostgreSQL: " + ex.Message;
        }
    }

    private static bool IsRunning()
    {
        // pg_ctl status: код 0 — сервер работает, 3 — остановлен. Надёжнее, чем существование postmaster.pid:
        // после падения или перезагрузки файл может остаться.
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(BinFolder, "pg_ctl.exe"), $"-D \"{DataFolder}\" status")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
