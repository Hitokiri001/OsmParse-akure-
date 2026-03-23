using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Captures all Unity console output (logs, warnings, errors) and saves
/// them to a text file on the C drive for easy access.
/// Attach to any persistent GameObject in your scene.
/// The log file is written continuously so even if Unity crashes
/// you still get the output up to that point.
/// </summary>
public class LogSaver : MonoBehaviour
{
    // -----------------------------------------------------------------------
    // CONFIGURATION
    // -----------------------------------------------------------------------

    [Tooltip("Full path to save the log file.")]
    public string LogFilePath = @"C:\UnityLog\osm_debug.txt";

    [Tooltip("Include stack traces for errors.")]
    public bool IncludeStackTrace = true;

    [Tooltip("Include timestamp on each line.")]
    public bool IncludeTimestamp = true;

    // -----------------------------------------------------------------------

    private StreamWriter _writer;
    private readonly object _lock = new object();

    private void Awake()
    {
        try
        {
            // Create directory if it doesn't exist
            string dir = Path.GetDirectoryName(LogFilePath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Open file for writing — overwrites previous log
            _writer = new StreamWriter(LogFilePath, append: false, encoding: Encoding.UTF8)
            {
                AutoFlush = true  // Write immediately so nothing is lost on crash
            };

            _writer.WriteLine("=== Unity OSM Debug Log ===");
            _writer.WriteLine($"Session started : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine($"Unity version   : {Application.unityVersion}");
            _writer.WriteLine($"Platform        : {Application.platform}");
            _writer.WriteLine(new string('=', 60));
            _writer.WriteLine();

            // Register for log callbacks
            Application.logMessageReceived += OnLogMessage;

            Debug.Log($"[LogSaver] Logging to: {LogFilePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[LogSaver] Failed to open log file: {e.Message}");
        }
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= OnLogMessage;

        if (_writer != null)
        {
            _writer.WriteLine();
            _writer.WriteLine(new string('=', 60));
            _writer.WriteLine($"Session ended : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.Close();
            _writer = null;
        }
    }

    private void OnLogMessage(string message, string stackTrace, LogType type)
    {
        if (_writer == null) return;

        lock (_lock)
        {
            try
            {
                string timestamp = IncludeTimestamp
                    ? $"[{DateTime.Now:HH:mm:ss.fff}] "
                    : "";

                string prefix = type switch
                {
                    LogType.Error     => "[ERROR]   ",
                    LogType.Warning   => "[WARNING] ",
                    LogType.Exception => "[EXCEPT]  ",
                    LogType.Assert    => "[ASSERT]  ",
                    _                 => "[INFO]    "
                };

                _writer.WriteLine($"{timestamp}{prefix}{message}");

                if (IncludeStackTrace &&
                   (type == LogType.Error || type == LogType.Exception) &&
                   !string.IsNullOrEmpty(stackTrace))
                {
                    _writer.WriteLine("  Stack trace:");
                    foreach (string line in stackTrace.Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            _writer.WriteLine($"    {line.Trim()}");
                }

                _writer.WriteLine();
            }
            catch
            {
                // Silently ignore write errors to avoid infinite loops
            }
        }
    }
}
