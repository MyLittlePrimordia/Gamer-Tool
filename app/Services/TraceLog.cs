using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GamerTool.Services;

public static class TraceLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        Debug.WriteLine(message);
        try
        {
            lock (Gate)
            {
                string folder = ProfileManager.AppDataFolder;
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "debug.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    string old = File.ReadAllText(path);
                    File.WriteAllText(path, old.Substring(old.Length / 2));
                }

                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Write(string tag, Exception ex)
    {
        StringBuilder builder = new();
        builder.Append(tag).Append(" ERROR ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(Environment.NewLine);
        builder.Append(ex.StackTrace);
        Write(builder.ToString());
    }
}
