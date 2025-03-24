﻿#nullable enable

namespace PlexShowSubtitlesOnRewind
{
    public static class MonitorManager
    {
        // Shared values / constants
        public const int DefaultMaxRewindAmount = 60;
        public const int DefaultActiveFrequency = 1;
        public const int DefaultIdleFrequency = 5;
        public const int DefaultSmallestResolution = 5; // iPhone has 5 second resolution apparently

        private static readonly List<SessionRewindMonitor> _allMonitors = [];
        private static int _activeFrequencyMs = DefaultActiveFrequency;
        private static int _idleFrequencyMs = DefaultIdleFrequency;
        private static bool _isRunning = false;
        private static bool _printDebugAll = false;

        public static void CreateAllMonitoringAllSessions(
            List<ActiveSession> activeSessionList,
            int activeFrequency = DefaultActiveFrequency,
            int idleFrequency = DefaultIdleFrequency,
            int maxRewindAmount = DefaultMaxRewindAmount,
            bool printDebugAll = false,
            string? debugDeviceName = null
            )
        {
            _activeFrequencyMs = activeFrequency * 1000; // Convert to milliseconds
            _idleFrequencyMs = idleFrequency * 1000;     // Convert to milliseconds

            foreach (ActiveSession activeSession in activeSessionList)
            {
                // Enable/Disable debugging per session depending on variables. Either for all devices or just a specific one
                bool printDebug = printDebugAll || Utils.CompareStringsWithWildcards(debugDeviceName, activeSession.DeviceName);

                if (printDebugAll)
                    _printDebugAll = true; // Set global debug flag so that future monitors can use it

                CreateMonitorForSession(
                    activeSession: activeSession,
                    activeFrequency: activeFrequency,
                    idleFrequency: idleFrequency,
                    maxRewindAmount: maxRewindAmount,
                    printDebug: printDebug
                );
            }

            StartRefreshLoop();
        }

        public static void CreateMonitorForSession(
            ActiveSession activeSession,
            int activeFrequency = DefaultActiveFrequency,
            int idleFrequency = DefaultIdleFrequency,
            int maxRewindAmount = DefaultMaxRewindAmount,
            bool printDebug = false
            )
        {
            _activeFrequencyMs = activeFrequency * 1000; // Convert to milliseconds
            _idleFrequencyMs = idleFrequency * 1000;     // Convert to milliseconds
            string sessionID = activeSession.Session.SessionId;

            if (_printDebugAll)
            {
                printDebug = true;
            }

            // Check if a monitor already exists for this session, if not create a new one
            if (_allMonitors.Any(m => m.SessionID == sessionID))
            {
                Console.WriteLine($"Monitor for session {sessionID} already exists. Not creating a new one.");
                return;
            }
            else
            {
                SessionRewindMonitor monitor = new SessionRewindMonitor(activeSession, frequency: activeFrequency, maxRewindAmount: maxRewindAmount, printDebug: printDebug);
                _allMonitors.Add(monitor);
                Console.WriteLine($"Found and monitoring new session for {activeSession.DeviceName}");
            }
        }

        public static List<string> GetMonitoredSessions()
        {
            List<string> sessionIDs = [];
            foreach (SessionRewindMonitor monitor in _allMonitors)
            {
                sessionIDs.Add(monitor.SessionID);
            }
            return sessionIDs;
        }

        private static void StartRefreshLoop()
        {
            _isRunning = true;

            while (_isRunning)
            {
                _ = SessionManager.RefreshExistingActiveSessionsAsync(); // Using discard since it's an async method, but we want this loop synchronous
                bool anyMonitorsActive = RefreshMonitors_OneIteration(_allMonitors);

                if (anyMonitorsActive == true)
                    Thread.Sleep(_activeFrequencyMs);
                else
                    Thread.Sleep(_idleFrequencyMs);
            }
        }

        // Will return false if no monitors are active
        private static bool RefreshMonitors_OneIteration(List<SessionRewindMonitor> monitorsToRefresh)
        {
            bool anyMonitorsActive = false;
            foreach (SessionRewindMonitor monitor in monitorsToRefresh)
            {
                if (monitor.IsMonitoring) // This gets checked inside the loop also but is here for clarity. Might remove later
                {
                    anyMonitorsActive = true;
                    monitor.MakeMonitoringPass();
                }
            }
            return anyMonitorsActive;
        }

        public static void StopAllMonitors()
        {
            foreach (SessionRewindMonitor monitor in _allMonitors)
            {
                monitor.StopMonitoring();
            }
        }
    }
}