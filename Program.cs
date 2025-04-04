﻿using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

#nullable enable

namespace RewindSubtitleDisplayerForPlex
{
    static class Program
    {
        internal static string PLEX_APP_TOKEN = "";
        internal static string PLEX_APP_IDENTIFIER = "";
        public static Settings config = new();

        public static bool debugMode = false;
        public static bool KeepAlive { get; set; } = true; // Used to keep the program running until user decides to exit

        private static ConnectionWatchdog? _connectionWatchdog; // Instance of the watchdog
        private static readonly ManualResetEvent _exitEvent = new ManualResetEvent(false);

        // ===========================================================================================

        static void Main(string[] args)
        {
            #if DEBUG
                debugMode = true;
            #endif

            // Event to signal application exit
            ManualResetEvent _exitEvent = new ManualResetEvent(false);

            // ------------ Apply launch parameters ------------
            bool runBackgroundArg = LaunchArgs.Background.CheckIfMatchesInputArgs(args);
            OS_Handlers.HandleBackgroundArg(runBackgroundArg);

            if (LaunchArgs.Debug.CheckIfMatchesInputArgs(args))
                debugMode = true;

            if (LaunchArgs.Help.CheckIfMatchesInputArgs(args) || LaunchArgs.HelpAlt.CheckIfMatchesInputArgs(args))
            {
                Console.WriteLine(MyStrings.LaunchArgsInfo + "\n\n");
                Console.WriteLine("Press Enter to exit.");
                Console.ReadLine();
                return;
            }
            // The normal launch message (only if not running background)
            else
            {
                WriteGreen(MyStrings.HeadingTitle);
                if (debugMode)
                    WriteWarning("Debug mode enabled.\n");
                Console.WriteLine(MyStrings.LaunchArgsInfo);
                Console.WriteLine("------------------------------------------------------------------------\n");
            }

            // ------------------ Start Main ------------------

            try
            {
                (string, string)? resultTuple = AuthTokenHandler.LoadTokens();
                if (resultTuple == null)
                {
                    Console.WriteLine("\nFailed to load tokens. Exiting.");
                    if (!runBackgroundArg) { Console.ReadLine(); }
                    return;
                }

                PLEX_APP_TOKEN = resultTuple.Value.Item1;
                PLEX_APP_IDENTIFIER = resultTuple.Value.Item2;

                config = SettingsHandler.LoadSettings(); // Assign loaded settings to the static config variable

                Console.WriteLine($"Using Plex server at {config.ServerURL}");
                PlexServer.SetupPlexServer(config.ServerURL, PLEX_APP_TOKEN, PLEX_APP_IDENTIFIER);

                // --- Instantiate and Start Watchdog ---
                _connectionWatchdog = new ConnectionWatchdog(config.ServerURL, PLEX_APP_TOKEN, PLEX_APP_IDENTIFIER);

                // Subscribe MonitorManager to the watchdog's event
                _connectionWatchdog.PlayingNotificationReceived += MonitorManager.HandlePlayingNotificationReceived; // Static handler now


                // Set up Ctrl+C handler. This doesn't run now, it just gets registered.
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    Console.WriteLine("\nCtrl+C detected. Initiating shutdown...");
                    eventArgs.Cancel = true; // Prevent immediate process termination
                    _exitEvent.Set();        // Signal the main thread to exit
                };

                // Start the watchdog - it will handle connection and listener internally
                _connectionWatchdog.Start();

                Console.WriteLine("Application running. Press Ctrl+C to exit.");

                // --- Wait for Exit Signal ---
                _exitEvent.WaitOne(); // Block main thread until Ctrl+C or other exit signal

                WriteWarning("Exit signal received. Shutting down (this might take several seconds)...");

            }
            catch (Exception ex) // Catch errors during initial setup
            {
                WriteErrorSuper($"Fatal error during startup: {ex.Message}\n");
                Console.WriteLine(ex.StackTrace);
                if (!runBackgroundArg)
                {
                    Console.WriteLine("\nPress Enter to exit...");
                    Console.ReadKey();
                }
            }
            finally
            {
                // --- Cleanup ---
                WriteWarning("Performing final cleanup...");
                _connectionWatchdog?.Stop(); // Stop the watchdog first
                _connectionWatchdog?.Dispose(); // Dispose the watchdog
                MonitorManager.RemoveAllMonitors(); // Stop any active rewind monitors
                Console.WriteLine("Application exited.");
                _exitEvent.Dispose();
            }
        }

    }  // ---------------- End class Program ----------------

} // --------------- End namespace PlexShowSubtitlesOnRewind ---------------
