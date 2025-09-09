using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

#nullable enable
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace RewindSubtitleDisplayerForPlex
{
    static class Program
    {
        internal static string PLEX_APP_TOKEN = "";
        internal static string PLEX_APP_IDENTIFIER = "";
        public static Settings config = new();

        public static bool isBackgroundMode = false; // Used to check if the program is running in background mode (no console window)

        private static bool instancesAreSetup = false; // Used to check if the instances are set up correctly

        private static ConnectionWatchdog? _connectionWatchdog; // Instance of the watchdog

        // ManualResetEvent for Ctrl+C
        private static readonly ManualResetEvent _ctrlCExitEvent = new ManualResetEvent(false);
        // CancellationTokenSource for graceful shutdown propagation
        private static readonly CancellationTokenSource _appShutdownCts_Program = new CancellationTokenSource();
        public static ShutdownProcedure UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;

        public static bool IsContainerized => Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        private static readonly List<Argument> FoundValidArgs = [];

        // ===========================================================================================

        static void Main(string[] args)
        {
            // =======================================================================
            // ============== STARTUP LOGIC & LAUNCH ARGUMENTS HANDLING ==============
            // =======================================================================

            Logging.Initialize();

            foreach (Argument possibleArg in LaunchArgs.GetAllArgs())
            {
                if (possibleArg.Check(args))
                {
                    FoundValidArgs.Add(possibleArg);
                }
            }

            // Very first thing is check for background mode. Trying to print anything to console before allocating console prevents anything from showing.
            // Local scope variable to avoid confusion of runBackgroundMode vs isBackgroundMode
            {
                bool runBackgroundMode = LaunchArgs.Background.Check(args); // Ignore background mode config setting if -stop is used
                isBackgroundMode = OS_Handlers.HandleBackgroundMode(runBackgroundMode); // OS specific handling for background mode
            }

            // Check for invalid launch args. False means there are unknown args.
            if (!LaunchArgs.CheckForUnknownArgsAndValidate(args))
            {
                WriteColor("\n------------ See valid launch args below ------------\n", Yellow);
                WriteLineSafe(LaunchArgs.AllLaunchArgsInfo + "\n\n");
                WaitPressEnterIfNotBackgroundMode(verb: "Continue Anyway", isForExit: false);
            }

            // Load Settings from file early on
            if (LaunchArgs.TestSettings.Check(args))
            {
                WriteLineSafe();
                SettingsHandler.LoadSettings(printResult: SettingsHandler.PrintResultType.ResultingConfig);
                UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                ExitProgramSafe();
            }

            // Load settings file and set default values if not present
            config = SettingsHandler.LoadSettings(); // Load settings early on but after debug mode is set by launch args if necessary

            if (config.LogToFile.Value == true)
            {
                FileLogger.Initialize(); // Will only initialize the logger if logging to file is enabled
            }

            FileLogger.LogToFile("\n\n--------------------------------------------------- NEW INSTANCE ---------------------------------------------------\n");
            
            if (!LaunchArgs.ForceNoDebug.Check(args))
            {
                #if DEBUG
                    config.ConsoleLogLevel.Value = LogLevel.DebugExtra;
                #endif
            }

            // Allow duplicate instances (Those that are set to connect to the same exact server. Mostly for testing.)
            if (LaunchArgs.AllowDuplicateInstance.Check(args))
                config.AllowDuplicateInstance.Value = true;

            // Token Template Generation
            if (LaunchArgs.TokenTemplate.Check(args))
            {
                bool tokenTemplateResult = AuthTokenHandler.CreateTemplateTokenFile(force: true);
                if (tokenTemplateResult)
                    WriteGreen("Token template file generated successfully.");
                else
                    WriteRed("Failed to generate token template file.");

                UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                ExitProgramSafe();
            }

            // Config Template Generation
            if (LaunchArgs.ConfigTemplate.Check(args))
            {
                SettingsHandler.GenerateTemplateSettingsFile();
                UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                ExitProgramSafe();
            }

            // Update Settings File
            if (LaunchArgs.UpdateSettings.Check(args))
            {
                SettingsHandler.UpdateSettingsFile(config);
                UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                ExitProgramSafe();
            }

            // ------------ Instance Coordination ------------
            if (!LaunchArgs.NoInstanceCheck.Check(args) && !IsContainerized)
            {
                instancesAreSetup = InstanceCoordinator.InitializeHandles(); // Setup event wait handles and listeners for instance coordination
                if (!instancesAreSetup)
                    WriteRedSuper("ERROR: Failed to initialize coordination handles. Certain functions like the -stop parameter will not work.");
            }

            // Stop other instances if requested
            if (LaunchArgs.Stop.Check(args))
            {
                InstanceCoordinator.SignalShutdown();
                // Clean up handles for this short-lived instance
                InstanceCoordinator.Cleanup();
                UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                ExitProgramSafe();
            }
            // ---------- End Instance Coordination -----------

            // Display startup message or help message (right now they are basically the same)
            if (LaunchArgs.Help.Check(args))
            {
                WriteLineSafe(LaunchArgs.AdvancedHelpInfo + "\n\n");
                UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                ExitProgramSafe();
            }
            else // The normal launch message (only if not running background)
            {
                WriteGreen(MyStrings.HeadingTitle);
                if (Program.config.ConsoleLogLevel > LogLevel.Info)
                    WriteYellow($"Log Level: {Program.config.ConsoleLogLevel}\n");

                WriteLineSafe(LaunchArgs.StandardLaunchArgsInfo);
                WriteSafe("\n\n   "); // Spacing to separate from the above
                WriteGreenSuper("---- Important Notes: ----");
                WriteRed(MyStrings.StartupImportantNotes + "\n");
                WriteLineSafe("------------------------------------------------------------------------\n");
            }

            // ------------ New Instance Logic: Check for Duplicates ------------
            if (instancesAreSetup)
            {
                bool duplicateFound = InstanceCoordinator.CheckForDuplicateServersAsync(config.ServerURL, config.AllowDuplicateInstance).Result;
                if (duplicateFound)
                {
                    if (config.AllowDuplicateInstance == true)
                    {
                        LogWarning("Duplicate instance found but currently set to allow duplicates. Continuing...");
                    }
                    else
                    {
                        // Error already logged by CheckForDuplicateServersAsync
                        LogError($"Exiting because another instance is already monitoring server: {config.ServerURL}");
                        if (!isBackgroundMode) { Utils.TimedWaitForEnterKey(15, "exit"); }

                        InstanceCoordinator.Cleanup(); // Cleanup handles
                        UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                        ExitProgramSafe();
                    }
                }
            }

            // =====================================================================
            // ======================== Start Main Logic ===========================
            // =====================================================================

            // Load any other settings that are needed
            MonitorManager.LoadRememberedShows_FromFile();

            try
            {
                if (config.SkipAuth.Value == false) {

                    string? deviceNameChosen = null;
                    if (LaunchArgs.AuthDeviceName.Check(args) && LaunchArgs.AuthDeviceName.GetArgValue(args) is string deviceName)
                    {
                        deviceNameChosen = deviceName;
                    }

                    (string, string)? resultTuple = AuthTokenHandler.LoadTokens(deviceNameArg: deviceNameChosen);
                    if (resultTuple == null)
                    {
                        WriteLineSafe("\nFailed to load tokens. Exiting.");
                        UseShutdownProcedure = ShutdownProcedure.PreferWaitUserInput;
                        return; // Will end up in finally block and end up in ExitProgramSafe() anyway
                    }

                    PLEX_APP_TOKEN = resultTuple.Value.Item1;
                    PLEX_APP_IDENTIFIER = resultTuple.Value.Item2;
                }
                else
                {
                    LogWarning("Skipping authentication because of config file setting.");
                    PLEX_APP_TOKEN = "";
                    PLEX_APP_IDENTIFIER = "";
                }

                LogInfo($"Using Plex server at {config.ServerURL}");
                PlexServer.SetupPlexServer(config.ServerURL, PLEX_APP_TOKEN, PLEX_APP_IDENTIFIER);

                // --- Instantiate and Start Watchdog ---
                _connectionWatchdog = new ConnectionWatchdog(config.ServerURL, PLEX_APP_TOKEN, PLEX_APP_IDENTIFIER, _appShutdownCts_Program);

                // Subscribe MonitorManager to the watchdog's event, which will alert us to playing notifications
                _connectionWatchdog.ForwardPlayingNotificationReceived += PlexNotificationListener.HandlePlayingNotificationReceived;


                // Set up Ctrl+C handler. This doesn't run now, it just gets registered.
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    WriteYellow("\n***** Ctrl+C detected. Initiating shutdown... *****\n");
                    eventArgs.Cancel = true; // Prevent immediate process termination
                    _ctrlCExitEvent.Set(); // Signal the legacy Ctrl+C event
                    _appShutdownCts_Program.Cancel(); // Signal cancellation token for newer async waits
                };


                // Start the watchdog - it will handle connection and listener internally
                _connectionWatchdog.Start();

                // --- Start Existing Instance Listener Task (This instance now acts as an Existing Instance too) ---
                WaitHandle[] waitHandles;
                if (instancesAreSetup)
                {
                    InstanceCoordinator.StartExistingInstanceListener(config.ServerURL, _appShutdownCts_Program.Token);
                    WaitHandle otherInstanceShutdownHandle = InstanceCoordinator.GetShutdownWaitHandle();
                    waitHandles = [_ctrlCExitEvent, _appShutdownCts_Program.Token.WaitHandle, otherInstanceShutdownHandle];
                }
                else
                {
                    waitHandles = [_ctrlCExitEvent, _appShutdownCts_Program.Token.WaitHandle];
                }

                WriteLineSafe("\nApplication running. Press Ctrl+C to exit.\n");
                int signaledHandleIndex = WaitHandle.WaitAny(waitHandles);

                // Determine reason for exit
                string exitReason = signaledHandleIndex switch
                {
                    0 => "Ctrl+C detected",
                    1 => "Application cancellation token triggered",
                    2 => "External shutdown signal received", // This is last because it might not always be set
                    _ => "WaitAny returned an unexpected index"
                };
                LogInfo($"Exit signal received ({exitReason}). Shutting down (this might take several seconds)...", ConsoleColor.Yellow);

                // Ensure cancellation is signaled if not already if we make it to this point,
                // since at this point we are shutting down (because we're beyond the wait handle trigger)
                if (!_appShutdownCts_Program.IsCancellationRequested)
                {
                    _appShutdownCts_Program.Cancel();
                }

            }
            catch (Exception ex) // Catch errors during initial setup
            {
                WriteRedSuper($"Fatal error during startup: {ex.Message}\n");
                WriteLineSafe(ex.StackTrace);
                if (!isBackgroundMode)
                {
                    UseShutdownProcedure = ShutdownProcedure.ImmediateGraceful;
                }
            }
            finally
            {
                // --- Cleanup ---
                LogDebug("Performing final cleanup...");
                _connectionWatchdog?.Stop(); // Stop the watchdog first
                _connectionWatchdog?.Dispose(); // Dispose the watchdog
                //MonitorManager.RemoveAllMonitors(); // Not needed if we're just exiting the app, plus should be already handled
                LogInfo("    Application exited.");
                _ctrlCExitEvent.Dispose();
                _appShutdownCts_Program.Dispose(); // Dispose the cancellation token source
                FileLogger.Shutdown(); 

                ExitProgramSafe();
            }
        }

        // Note not all of these are necessarily used yet, but they are here for future use
        public enum ShutdownProcedure
        {
            ImmediateGraceful,  // Never wait for user input, just exit
            ImmediateKill,      // Kill the process immediately
            PreferWaitUserInput // If not in background mode, wait for user input if the console was allocated
                                //      but not if it was attached since the user will be still able to see any messages
            
        }

        private static void ExitProgramSafe()
        {
            try
            {
                // Flush standard output and error streams BEFORE releasing the console
                Console.Out.Flush();
                Console.Error.Flush();
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Error flushing console output: {ex.Message}");
            }

            if (UseShutdownProcedure == ShutdownProcedure.PreferWaitUserInput)
            {
                if (IsContainerized)
                {
                    Utils.ContainerExitStop(); // Wait indefinitely for user to shut down the container or restart it manually
                    Console.WriteLine("\nContainerized mode detected. Waiting indefinitely for manual user restart or container shutdown...");
                }
                else
                {
                    WaitPressEnterIfNotBackgroundMode(verb: "Exit", isForExit: true);
                }
            }
            
            OS_Handlers.FreeConsoleIfNeeded();

            Environment.Exit(0);
        }

    }  // ---------------- End class Program ----------------

} // --------------- End namespace PlexShowSubtitlesOnRewind ---------------
