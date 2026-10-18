using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace MakeItZBS.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var oscriptExe = Path.Combine(baseDir, "runtime", "bin", "oscript.exe");
                var scriptRel = Path.Combine("src", "onescript", "zbs-run.os");
                var scriptAbs = Path.Combine(baseDir, scriptRel);

                if (!File.Exists(oscriptExe))
                {
                    MessageBox.Show(
                        "Не найден бандленный рантайм:\n" + oscriptExe + "\n\nПапка дистрибутива повреждена или неполна.",
                        "Make it ZBS",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 2;
                }

                if (!File.Exists(scriptAbs))
                {
                    MessageBox.Show(
                        "Не найден стартовый скрипт:\n" + scriptAbs + "\n\nПапка дистрибутива повреждена или неполна.",
                        "Make it ZBS",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 2;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = oscriptExe,
                    // Абсолютный путь (не относительный): папку
                    // дистрибутива пользователь кладёт куда угодно —
                    // в путь с пробелами/кириллицей (Downloads, Рабочий
                    // стол, имя пользователя). Кавычки + абсолютный
                    // путь надёжнее, чем relative + зависимость от CWD.
                    Arguments = "-encoding=utf-8 \"" + scriptAbs + "\"",
                    WorkingDirectory = baseDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                // fire-and-forget: лаунчер сразу выходит, oscript живёт
                // в трее. Process освобождаем (хендл — не убивает child).
                using (Process.Start(psi))
                {
                    // Dispose only the launcher-side handle; the tray process continues independently.
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось запустить Make it ZBS:\n" + ex.Message,
                    "Make it ZBS",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
