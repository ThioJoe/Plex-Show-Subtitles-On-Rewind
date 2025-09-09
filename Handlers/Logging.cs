using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RewindSubtitleDisplayerForPlex;
internal static partial class Logging
{
    private static readonly Lock ConsoleWriterLock = new Lock();
    private static LogLevel _logLevel => Program.config.ConsoleLogLevel; // Default log level

    // --- Core Queuing Components ---
    private static readonly BlockingCollection<WriteQueueObject> _logQueue = [];
    private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
    // Update the declaration of _consumerTask to make it nullable.  
    private static Task? _consumerTask;

    private class WriteQueueObject
    {
        public WriteMode WriteMode { get; private set; }
        public LogParams? LogParams { get; private set; } = null;
        public WriteColorParams? WriteColorParams { get; private set; } = null;
        public WriteColorPartsParams? WriteColorPartsParams { get; private set; } = null;
        public WriteWithBackgroundParams? WriteWithBackgroundParams { get; private set; } = null;
        public string? SimpleMessage { get; private set; } = null; // For WriteSafe and WriteLineSafe

        public WriteQueueObject (
            WriteMode writeMode,

            // Log
            string? message = null,
            LogLevel? level = null,
            ConsoleColor? logTypeColor = null,
            ConsoleColor? messageColor = null,

            // WriteColor
            ConsoleColor? foreground = null,
            ConsoleColor? background = null,
            bool? noNewline = null,

            // WriteColorParts
            string? msg1 = null,
            string? msg2 = null,
            ConsoleColor? foreground1 = null,
            ConsoleColor? foreground2 = null
        )
        {
            WriteMode = writeMode;

            if (writeMode == WriteMode.Log && message != null && level != null)
            {
                LogParams = new LogParams(message, (LogLevel)level, logTypeColor, messageColor);
            }
            else if (writeMode == WriteMode.WriteColor && message != null && foreground != null)
            {
                WriteColorParams = new WriteColorParams(message, (ConsoleColor)foreground, background, noNewline ?? false);
            }
            else if (writeMode == WriteMode.WriteColorParts && msg1 != null && msg2 != null)
            {
                WriteColorPartsParams = new WriteColorPartsParams(msg1, msg2, foreground1, foreground2);
            }
            else if (writeMode == WriteMode.WriteWithBackground && message != null && foreground != null)
            {
                WriteWithBackgroundParams = new WriteWithBackgroundParams(message, (ConsoleColor)foreground, background, noNewline ?? false);
            }
            else if ((writeMode == WriteMode.WriteSafe || writeMode == WriteMode.WriteLineSafe) && message != null)
            {
                SimpleMessage = message;
            }
            else
            {
                throw new ArgumentException("Invalid parameters for WriteQueueObject");
            }
        }
    }

    public enum WriteMode
    {
        Log,
        WriteSafe,
        WriteLineSafe,
        WriteColor,
        WriteColorParts,
        WriteWithBackground
    }

    public class LogParams
    {
        public string Message { get; }
        public LogLevel Level { get; }
        public ConsoleColor? LogTypeColor { get; }
        public ConsoleColor? MessageColor { get; }
        public LogParams(string message, LogLevel level, ConsoleColor? logTypeColor, ConsoleColor? messageColor)
        {
            Message = message;
            Level = level;
            LogTypeColor = logTypeColor;
            MessageColor = messageColor;
        }
    }

    public class WriteColorParams
    {
        public string Message { get; }
        public ConsoleColor Foreground { get; }
        public ConsoleColor? Background { get; }
        public bool NoNewline { get; }
        public WriteColorParams(string message, ConsoleColor foreground, ConsoleColor? background, bool noNewline)
        {
            Message = message;
            Foreground = foreground;
            Background = background;
            NoNewline = noNewline;
        }
    }

    public class WriteColorPartsParams
    {
        public string Message1 { get; }
        public string Message2 { get; }
        public ConsoleColor? Foreground1 { get; }
        public ConsoleColor? Foreground2 { get; }
        public WriteColorPartsParams(string message1, string message2, ConsoleColor? foreground1, ConsoleColor? foreground2)
        {
            Message1 = message1;
            Message2 = message2;
            Foreground1 = foreground1;
            Foreground2 = foreground2;
        }
    }

    public class WriteWithBackgroundParams
    {
        public string Message { get; }
        public ConsoleColor Foreground { get; }
        public ConsoleColor? Background { get; }
        public bool NoNewline { get; }
        public WriteWithBackgroundParams(string message, ConsoleColor foreground, ConsoleColor? background, bool noNewline)
        {
            Message = message;
            Foreground = foreground;
            Background = background;
            NoNewline = noNewline;
        }
    }

    // --- Initialization & Shutdown ---
    public static void Initialize()
    {
        if (_consumerTask != null && !_consumerTask.IsCompleted) return; // Prevent re-initialization

        _consumerTask = Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
    }

    // --- Background Logger (Consumer) ---
    private static void ProcessLogQueue(CancellationToken cancellationToken)
    {
        try
        {
            foreach (WriteQueueObject message in _logQueue.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    if (message.WriteMode == WriteMode.Log && message.LogParams != null)
                    {
                        PrintLogItem(
                        message: message.LogParams.Message,
                        level: message.LogParams.Level,
                        logTypeColor: message.LogParams.LogTypeColor,
                        messageColor: message.LogParams.MessageColor
                        );
                    }
                    else if (message.WriteMode == WriteMode.WriteColor && message.WriteColorParams != null)
                    {
                        PrintWriteColor(
                            message: message.WriteColorParams.Message,
                            foreground: message.WriteColorParams.Foreground,
                            background: message.WriteColorParams.Background,
                            noNewline: message.WriteColorParams.NoNewline
                        );
                    }
                    else if (message.WriteMode == WriteMode.WriteColorParts && message.WriteColorPartsParams != null)
                    {
                        PrintWriteColorParts(
                            msg1: message.WriteColorPartsParams.Message1,
                            msg2: message.WriteColorPartsParams.Message2,
                            foreground1: message.WriteColorPartsParams.Foreground1,
                            foreground2: message.WriteColorPartsParams.Foreground2
                        );
                    }
                    else if (message.WriteMode == WriteMode.WriteWithBackground && message.WriteWithBackgroundParams != null)
                    {
                        PrintWriteWithBackground(
                            message: message.WriteWithBackgroundParams.Message,
                            foreground: message.WriteWithBackgroundParams.Foreground,
                            background: message.WriteWithBackgroundParams.Background,
                            noNewLine: message.WriteWithBackgroundParams.NoNewline
                        );
                    }
                    else if (message.WriteMode == WriteMode.WriteSafe && message.SimpleMessage != null)
                    {
                        PrintWriteSafe(message.SimpleMessage);
                    }
                    else if (message.WriteMode == WriteMode.WriteLineSafe && message.SimpleMessage != null)
                    {
                        PrintWriteLineSafe(message.SimpleMessage);
                    }
                }
                catch (Exception)
                {
                    // Failed to write this message, log to console or ignore
                    LogError($"Failed to write log message: {message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Task was cancelled, exit gracefully
            LogDebug("Log queue processing was cancelled.");
        }
        catch (Exception)
        {
            // Unhandled exception in consumer task
            LogError("An error occurred while processing the log queue.");
        }
    }

    // --- Add to the queue with Log object (Producer) ---
    // Log is private since we should be calling the individual log methods
    private static void Log(string message, LogLevel level, ConsoleColor? logTypeColor, ConsoleColor? messageColor)
    {
        try
        {
            // Non-blocking add is preferred for fire-and-forget
            if (!_logQueue.TryAdd(new WriteQueueObject(WriteMode.Log, message: message, level: level, logTypeColor: logTypeColor, messageColor: messageColor)))
            {
                PrintLogItem("Log queue is full. Message not added.", LogLevel.Error, Red, Red);
            }
        }
        catch (InvalidOperationException)
        {
            PrintLogItem("Log queue is completed. Cannot add more messages.", LogLevel.Debug, null, null);
        }
        catch (Exception ex)
        {
            // Handle other exceptions if needed
            PrintLogItem($"[Func Log] Failed to add log message to queue: {ex.Message}", LogLevel.Error, Red, Red);
        }
    }

    private static string GetPrefix(LogLevel level)
    {
        return level switch
        {
            LogLevel.Warning =>     "[ WARNING ]  ",
            LogLevel.Verbose =>     "[ VERBOSE ]  ",
            LogLevel.Info =>        "[  INFO   ]  ",
            LogLevel.Error =>       "[  ERROR  ]  ",
            LogLevel.Debug =>       "[  DEBUG  ]  ",
            LogLevel.DebugExtra =>  "[++DEBUG++]  ",
            _ => "[UNKNOWN] "
        };
    }

    private static void PrintLogItem(string message, LogLevel level, ConsoleColor? logTypeColor, ConsoleColor? messageColor)
    {
        string prefix = GetPrefix(level);

        // If the logTypeColor provided is not default for the log level, 

        // Move any leading newlines prior to the prefix
        if (message.StartsWith('\n'))
        {
            // Get the number of leading newlines
            int leadingNewlines = message.TakeWhile(c => c == '\n').Count();
            // Remove the leading newlines from the message
            message = message.Substring(leadingNewlines);
            // Add the leading newlines to the prefix
            prefix = new string('\n', leadingNewlines) + prefix;
        }

        // Any trailing newlines, store them and print them after the message
        string trailingNewlines = "";
        if (message.EndsWith('\n'))
        {
            // Get the number of trailing newlines
            int trailingNewlineCount = message.Reverse().TakeWhile(c => c == '\n').Count();
            // Remove the trailing newlines from the message
            message = message.Substring(0, message.Length - trailingNewlineCount);
            // Add the trailing newlines to the string
            trailingNewlines = new string('\n', trailingNewlineCount);
        }

        // Any other newlines, add spaces after so it lines up with the prefix
        message = message.Replace("\n", "\n             ");

        lock (ConsoleWriterLock) // Lock access to the console
        {
            try
            {
                Console.ResetColor();

                // Write the prefix
                if (logTypeColor is ConsoleColor nonNullColor)
                {
                    Console.ForegroundColor = nonNullColor;
                    Console.Write($"{prefix}");
                }
                else
                {
                    Console.Write($"{prefix}");
                }

                // Write the message
                if (messageColor is ConsoleColor nonNullMessageColor)
                {
                    Console.ForegroundColor = nonNullMessageColor;
                    Console.WriteLine(message);
                }
                else
                {
                    // Don't reset the color so it inherits the logTypeColor
                    Console.WriteLine(message);
                }
            }
            finally // Want to be sure to always reset the logTypeColor
            {
                Console.ResetColor();
                Console.Write(trailingNewlines); // Print the trailing newlines after the message
            }
        }

        // Log to file
        string logMessage = $"{prefix}{message}";
        logMessage = logMessage.Trim();
        Task.Run(() => FileLogger.LogToFile(logMessage)); // Fire and forget
    }

    private static void PrintWriteSafe(string message)
    {
        lock (ConsoleWriterLock) // Lock access to the console
        {
            Console.ResetColor(); // Reset ahead of time just in case
            Console.Write(message);
        }
    }

    private static void PrintWriteLineSafe(string? message = null)
    {
        lock (ConsoleWriterLock) // Lock access to the console
        {
            Console.ResetColor(); // Reset ahead of time just in case
            Console.WriteLine(message);
        }
    }

    public static void WriteLineSafe(string? message = "")
    {
        try
        {
            // Non-blocking add is preferred for fire-and-forget
            if (!_logQueue.TryAdd(new WriteQueueObject(WriteMode.WriteLineSafe, message: message)))
            {
                PrintLogItem("Log queue is full. Message not added.", LogLevel.Error, Red, Red);
            }
        }
        catch (Exception ex)
        {
            PrintLogItem($"[Func WriteLineSafe] Failed to add log message to queue: {ex.Message}", LogLevel.Error, Red, Red);
        }

    }

    public static void WriteSafe(string message)
    {
        try
        {
            // Non-blocking add is preferred for fire-and-forget
            if (!_logQueue.TryAdd(new WriteQueueObject(WriteMode.WriteSafe, message: message)))
            {
                PrintLogItem("Log queue is full. Message not added.", LogLevel.Error, Red, Red);
            }
        }
        catch (Exception ex)
        {
            PrintLogItem($"[Func Writesafe] Failed to add log message to queue: {ex.Message}", LogLevel.Error, Red, Red);
        }

    }

    public static void LogInfo(string message, ConsoleColor? color = null)
    {
        if (_logLevel >= LogLevel.Info)
            Log(message, LogLevel.Info, null, color);
    }

    public static void LogDebug(string message, ConsoleColor? messageColor = null)
    {
        if (_logLevel >= LogLevel.Debug)
            Log(message, LogLevel.Debug, ConsoleColor.DarkGray, messageColor);
    }

    public static void LogDebugExtra(string message, ConsoleColor? messageColor = null)
    {
        if (_logLevel >= LogLevel.DebugExtra)
            Log(message, LogLevel.DebugExtra, ConsoleColor.DarkGray, messageColor);
    }

    public static void LogWarning(string message, ConsoleColor? messageColor = null)
    {
        if (_logLevel >= LogLevel.Warning)
            Log(message, LogLevel.Warning, ConsoleColor.Yellow, messageColor);
    }

    public static void LogError(string message, ConsoleColor? messageColor = null)
    {
        if (_logLevel >= LogLevel.Error)
            Log(message, LogLevel.Error, ConsoleColor.Red, messageColor);
    }

    public static void LogVerbose(string message, ConsoleColor? messageColor = null)
    {
        if (_logLevel >= LogLevel.Verbose)
            Log(message, LogLevel.Verbose, null, messageColor);
    }

    // Log success message with green color for both the log type prefix and message
    public static void LogSuccess(string message)
    {
        if (_logLevel >= LogLevel.Info)
            Log(message, LogLevel.Info, logTypeColor: ConsoleColor.Green, null);
    }

    // ------------------------------- COLOR RELATED ---------------------------------

    public static void WriteWithBackground(string message, ConsoleColor foreground, ConsoleColor? background, bool noNewLine = false)
    {
        try
        {
            // Non-blocking add is preferred for fire-and-forget
            if (!_logQueue.TryAdd(new WriteQueueObject(WriteMode.WriteWithBackground, message: message, foreground: foreground, background: background, noNewline: noNewLine)))
            {
                PrintLogItem("Log queue is full. Message not added.", LogLevel.Error, Red, Red);
            }
        }
        catch (Exception ex)
        {
            PrintLogItem($"Failed to add log message to queue: {ex.Message}", LogLevel.Error, Red, Red);
        }
    }

    public static void PrintWriteWithBackground(string message, ConsoleColor foreground, ConsoleColor? background, bool noNewLine = false)
    {
        // If there are any newlines in the message, split it and write each line separately.
        // If there are trailing or leading newlines, write them separately not colored.
        // This is because the background logTypeColor can be messed up by newline

        string[] lines = AnyNewlineRegex().Split(message);

        lock (ConsoleWriterLock) // Lock access to the console
        {
            foreach (string line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                else if (line.Trim('\n').Length > 0)
                {
                    PrintWriteColor(message: line, foreground: foreground, background: background, noNewline: true);
                }
                else
                {
                    Console.WriteLine(); // Write a newline at the end because we've been using noNewline:true
                }
            }

            if (noNewLine == false)
            {
                Console.WriteLine(); // Write a newline at the end because we've been using noNewline:true
            }
        }
    }

    public static void WriteYellow(string message, bool noNewline = false)
    {
        WriteColor(message: message, foreground: ConsoleColor.Yellow, noNewline: noNewline);
    }

    public static void WriteGreen(string message, bool noNewline = false)
    {
        WriteColor(message: message, foreground: ConsoleColor.Green, noNewline: noNewline);
    }

    public static void WriteRed(string message, bool noNewline = false)
    {
        WriteColor(message: message, foreground: ConsoleColor.Red, noNewline: noNewline);
    }

    public static void WriteRedSuper(string message, bool noNewline=false)
    {
        WriteWithBackground(message: message, foreground: ConsoleColor.White, background: ConsoleColor.DarkRed, noNewLine: noNewline);
    }
    public static void WriteGreenSuper(string message, bool noNewLine = false)
    {
        WriteWithBackground(message, ConsoleColor.White, ConsoleColor.DarkGreen, noNewLine: noNewLine);
    }

    private static void PrintWriteColorParts(string msg1, string msg2, ConsoleColor? foreground1 = null, ConsoleColor? foreground2 = null)
    {
        lock (ConsoleWriterLock)
        {
            try
            {
                Console.ResetColor();

                // First Part
                if (foreground1 is ConsoleColor nonNullColor1)
                    Console.ForegroundColor = nonNullColor1;

                Console.Write(msg1);

                // Second Part
                if (foreground2 is ConsoleColor nonNullColor2)
                    Console.ForegroundColor = nonNullColor2;
                else
                    Console.ResetColor();

                Console.WriteLine(msg2);
            }
            finally
            {
                Console.ResetColor();
            }
        }

        // Log to file
        string logMessage = $"{msg1}{msg2}";
        Task.Run(() => FileLogger.LogToFile(logMessage)); // Fire and forget
    }

    private static void PrintWriteColor(string message, ConsoleColor foreground, ConsoleColor? background = null, bool noNewline = false)
    {
        lock (ConsoleWriterLock) // Lock access to the console
        {
            try
            {
                Console.ResetColor(); // Reset ahead of time just in case

                Console.ForegroundColor = foreground;
                if (background != null)
                    Console.BackgroundColor = background.Value;

                if (noNewline)
                    Console.Write(message);
                else
                    Console.WriteLine(message);
            }
            finally
            {
                Console.ResetColor();
            }
        }

        Task.Run(() => FileLogger.LogToFile(message)); // Fire and forget
    }



    public static void WriteColor(string message, ConsoleColor foreground, ConsoleColor? background = null, bool noNewline = false)
    {
        try
        {
            // Non-blocking add is preferred for fire-and-forget
            if (!_logQueue.TryAdd(new WriteQueueObject(WriteMode.WriteColor, message: message, foreground: foreground, background: background, noNewline: noNewline)))
            {
                PrintLogItem("Log queue is full. Message not added.", LogLevel.Error, Red, Red);
            }
        }
        catch (Exception ex)
        {
            PrintLogItem($"Failed to add log message to queue: {ex.Message}", LogLevel.Error, Red, Red);
        }
    }

    public static void WriteColorParts(string msg1, string msg2, ConsoleColor? foreground1 = null, ConsoleColor? foreground2 = null)
    {
        try
        {
            // Non-blocking add is preferred for fire-and-forget
            if (!_logQueue.TryAdd(new WriteQueueObject(WriteMode.WriteColorParts, msg1: msg1, msg2: msg2, foreground1: foreground1, foreground2: foreground2)))
            {
                PrintLogItem("Log queue is full. Message not added.", LogLevel.Error, Red, Red);
            }
        }
        catch (Exception ex)
        {
            // Handle other exceptions if needed
            PrintLogItem($"Failed to add log message to queue: {ex.Message}", LogLevel.Error, Red, Red);
        }
    }

    [GeneratedRegex(@"(\r\n|\r|\n)", RegexOptions.Compiled)]
    private static partial Regex AnyNewlineRegex();
}
