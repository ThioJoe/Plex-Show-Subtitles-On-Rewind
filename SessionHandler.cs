﻿
namespace PlexShowSubtitlesOnRewind
{
    public static class SessionHandler
    {
        private readonly static List<ActiveSession> _activeSessionList = [];
        private static readonly Lock _lockObject = new Lock();
        private static PlexServer? _plexServer = null;

        // This not only fetches the sessions, but also gets both active and available subtitles
        public static async Task<List<ActiveSession>> ProcessActiveSessions(List<PlexSession> sessionsList, PlexServer plexServer)
        {
            List<ActiveSession> newActiveSessionList = [];

            foreach (PlexSession session in sessionsList)
            {
                // Get active subtitles directly from the session Media
                List<SubtitleStream> activeSubs = GetOnlyActiveSubtitlesForSession(session);

                // Get ALL available subtitles with a separate metadata call
                List<SubtitleStream> availableSubs = await FetchAllAvailableSubtitles_ViaServerQuery_Async(session, plexServer);

                newActiveSessionList.Add(new ActiveSession(
                    session: session,
                    availableSubtitles: availableSubs,
                    activeSubtitles: activeSubs,
                    plexServer: plexServer
                ));
            }

            return newActiveSessionList;
        }

        public static async Task<List<ActiveSession>> ClearAndLoadActiveSessionsAsync(PlexServer plexServer)
        {
            _plexServer = plexServer;
            List<PlexSession>? sessionsList = await _plexServer.GetSessionsAsync(shortTimeout:false);

            // It will only return null for an error
            if (sessionsList == null)
            {
                Console.WriteLine("Error Occurred. See above messages. Will use existing session list if any.");
                return _activeSessionList;
            }

            List <ActiveSession> activeSessions = await ProcessActiveSessions(sessionsList, plexServer);
            lock (_lockObject)
            {
                _activeSessionList.Clear();
                _activeSessionList.AddRange(activeSessions);
            }

            return _activeSessionList;
        }

        public static async Task<List<ActiveSession>> RefreshExistingActiveSessionsAsync(bool currentlyIdle)
        {
            // Assume _plexServer is already set. Show error if not
            if (_plexServer is not PlexServer plexServer)
            {
                Console.WriteLine("Error: PlexServer instance is null. Cannot refresh sessions. Must load sessions first.");
                return _activeSessionList;
            }
            // -----------------------------------

            List<PlexSession>? fetchedSessionsList = await plexServer.GetSessionsAsync(shortTimeout: !currentlyIdle);

            if (fetchedSessionsList == null || fetchedSessionsList.Count == 0)
            {
                Console.WriteLine("No active sessions found.");
                return _activeSessionList;
            }

            List<Task> tasks = [];
            foreach (PlexSession fetchedSession in fetchedSessionsList)
            {
                tasks.Add(Task.Run(async () =>
                {
                    // We'll need the active subs in any case. Active subtitles are available from data we'll get from the session data anyway
                    List<SubtitleStream> activeSubtitles = GetOnlyActiveSubtitlesForSession(fetchedSession);

                    // Check if the session already exists in the active session list, and update in place if so
                    ActiveSession? existingSession = _activeSessionList.FirstOrDefault(s => s.SessionID == fetchedSession.SessionId);
                    if (existingSession != null)
                    {
                        existingSession.ApplyUpdatedData(fetchedSession, activeSubtitles);
                    }
                    else
                    {
                        // If the session is not found in the existing list, add it as a new session
                        // First need to get available subs by specifically querying the server for data about the media,
                        //      otherwise the session data doesn't include all available subs
                        List<SubtitleStream> availableSubs = await FetchAllAvailableSubtitles_ViaServerQuery_Async(fetchedSession, plexServer);

                        ActiveSession newSession = new ActiveSession(
                            session: fetchedSession,
                            availableSubtitles: availableSubs,
                            activeSubtitles: activeSubtitles,
                            plexServer: plexServer
                        );
                        lock (_lockObject)
                        {
                            _activeSessionList.Add(newSession);
                        }

                        // Create a new monitor for the newly found session. The method will automatically check for duplicates
                        MonitorManager.CreateMonitorForSession(
                            activeSession: newSession,
                            activeFrequency: Program.config.ActiveMonitorFrequency,
                            idleFrequency: Program.config.IdleMonitorFrequency,
                            smallestResolution: newSession.SmallestResolutionExpected);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Check for dead sessions and remove them
            if (_activeSessionList.Count > 0)
            {
                // Find any sessions in active sessions list that are not in the fetched list, by session ID
                List<ActiveSession> deadSessions = _activeSessionList.Where(s => !fetchedSessionsList.Any(fs => fs.SessionId == s.SessionID)).ToList();

                foreach (ActiveSession deadSession in deadSessions)
                {
                    WriteColor($"Removing leftover session from {deadSession.DeviceName}", ConsoleColor.Yellow);
                    _activeSessionList.Remove(deadSession);
                    MonitorManager.RemoveMonitorForSession(deadSession.SessionID);
                }
            }

            return _activeSessionList;
        }

        public static List<ActiveSession> Get()
        {
            lock (_lockObject)
            {
                return _activeSessionList;
            }
        }

        // This specifically queries the server for data about the media item, which includes non-active subtitle tracks, whereas the session data does not include that
        // So we usually only use this when initially loading sessions, since available subs don't change often
        private static async Task<List<SubtitleStream>> FetchAllAvailableSubtitles_ViaServerQuery_Async(PlexSession session, PlexServer plexServer)
        {
            List<SubtitleStream> subtitles = [];
            try
            {
                // Make a separate call to get the full media metadata including all subtitle tracks
                string mediaKey = session.Key; // Like '/library/metadata/20884'
                PlexMediaItem mediaItem = await plexServer.FetchItemAsync(mediaKey);

                // Get all subtitle streams from the media item
                subtitles = mediaItem.GetSubtitleStreams();

                Console.WriteLine($"Found {subtitles.Count} available subtitle tracks for {session.Title}");
                return subtitles;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting available subtitles: {ex.Message}");
                return subtitles;
            }
        }

        private static List<SubtitleStream> GetOnlyActiveSubtitlesForSession(PlexSession session)
        {
            List<SubtitleStream> result = [];

            if (session.Media != null && session.Media.Count > 0)
            {
                foreach (Media media in session.Media)
                {
                    if (media.Parts != null && media.Parts.Count > 0)
                    {
                        foreach (MediaPart part in media.Parts)
                        {
                            // Only add subtitles that are marked as selected
                            result.AddRange(part.Subtitles.Where(s => s.Selected));
                        }
                    }
                }
            }

            return result;
        }

        public static void PrintSubtitles()
        {
            foreach (ActiveSession activeSession in _activeSessionList)
            {
                List<SubtitleStream> activeSubtitles = activeSession.ActiveSubtitles;
                List<SubtitleStream> availableSubtitles = activeSession.AvailableSubtitles;
                string deviceName = activeSession.DeviceName;
                string mediaTitle = activeSession.MediaTitle;

                Console.WriteLine("\n-------------------------------------");
                Console.WriteLine($"Active Subtitles for {mediaTitle} on {deviceName}:");
                if (activeSubtitles.Count == 0)
                {
                    Console.WriteLine("[None]");
                }
                else
                {
                    foreach (SubtitleStream subtitle in activeSubtitles)
                    {
                        Console.WriteLine(subtitle.ExtendedDisplayTitle);
                    }
                }

                Console.WriteLine($"\nAvailable Subtitles for {mediaTitle} on {deviceName}:");
                if (availableSubtitles.Count == 0)
                {
                    Console.WriteLine("[None]");
                }
                else
                {
                    foreach (SubtitleStream subtitle in availableSubtitles)
                    {
                        Console.WriteLine(subtitle.ExtendedDisplayTitle);
                    }
                }
            }
        }
    }
}