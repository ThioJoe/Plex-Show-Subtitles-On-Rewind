﻿namespace RewindSubtitleDisplayerForPlex
{
    // Monitors a single session for rewinding
    public class RewindMonitor
    {
        private readonly ActiveSession _activeSession;
        //private readonly PlexClient _client;
        private readonly double _activeFrequencySec;
        private readonly double _idleFrequencySec;
        private readonly double _maxRewindAmountSec;
        private readonly string _deviceName;

        private readonly int _fastForwardThreshold = 7; // Minimum amount of seconds to consider a fast forward (in seconds)

        private static int DefaultCooldownCount = Program.config.CoolDownCount;

        private bool _isMonitoring;
        private bool _subtitlesUserEnabled;
        private double _latestWatchedPosition;
        private double _previousPosition; // Use to detect fast forwards
        private int _cooldownCyclesLeft = 0; // Used after rewinding too long, to prevent detecting rewinds again too quickly
        private int _cooldownToUse = 0; // Used to store the current max cooldown so it can be reset
        private bool _temporarilyDisplayingSubtitles;
        private double _smallestResolutionSec; // This might be updated depending on available data during refreshes

        public string PlaybackID => _activeSession.Session.PlaybackID;
        public bool IsMonitoring => _isMonitoring;
        public ActiveSession AttachedSession => _activeSession;

        public string MachineID { get => _activeSession.MachineID; }

        public RewindMonitor(
            ActiveSession session,
            double activeFrequencySec,
            double idleFrequencySec,
            double maxRewindAmountSec,
            int smallestResolution = MonitorManager.DefaultSmallestResolution
            )
        {
            _activeSession = session;
            _activeFrequencySec = activeFrequencySec;
            _idleFrequencySec = idleFrequencySec;
            _maxRewindAmountSec = maxRewindAmountSec;
            _deviceName = _activeSession.DeviceName;
            _idleFrequencySec = idleFrequencySec;
            _isMonitoring = false;
            _subtitlesUserEnabled = false;
            _latestWatchedPosition = 0;
            _previousPosition = 0;
            _temporarilyDisplayingSubtitles = false;
            _smallestResolutionSec = Math.Max(_activeFrequencySec, smallestResolution);

            SetupMonitoringInitialConditions();
        }

        // Constructor that takes another monitor and creates a new one with the same settings to apply to a new session
        public RewindMonitor(RewindMonitor otherMonitor, ActiveSession newSession)
        {
            // Potentially updated values
            _activeSession = newSession;
            _deviceName = newSession.DeviceName;

            // Values that will be re-used
            _latestWatchedPosition = otherMonitor._latestWatchedPosition; // Ensures subtitle stopping point is the same for new session
            _activeFrequencySec = otherMonitor._activeFrequencySec;
            _idleFrequencySec = otherMonitor._idleFrequencySec;
            _maxRewindAmountSec = otherMonitor._maxRewindAmountSec;
            _idleFrequencySec = otherMonitor._idleFrequencySec;
            _isMonitoring = otherMonitor._isMonitoring;
            _subtitlesUserEnabled = otherMonitor._subtitlesUserEnabled;
            _previousPosition = otherMonitor._previousPosition;
            _temporarilyDisplayingSubtitles = otherMonitor._temporarilyDisplayingSubtitles;
            _smallestResolutionSec = otherMonitor._smallestResolutionSec;
        }

        private string GetTimeString(double seconds)
        {
            // ---------------- Local function -------------------
            static string SecondsToTimeString(double seconds)
            {
                TimeSpan time = TimeSpan.FromSeconds(seconds);
                if (time.Hours > 0)
                {
                    return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
                }
                else
                {
                    return $"{time.Minutes:D2}:{time.Seconds:D2}";
                }
            }
            // --------------------------------------------------

            if (Program.config.ConsoleLogLevel >= LogLevel.Debug)
                return seconds.ToString() + $" ({SecondsToTimeString(seconds)})";
            else
                return SecondsToTimeString(seconds);
        }

        private void RewindOccurred()
        {
            LogInfo($"{_deviceName}: Rewind occurred for {_activeSession.MediaTitle} - Will stop subtitles at time: {GetTimeString(_latestWatchedPosition)}", Yellow);
            _activeSession.EnableSubtitles();
            _temporarilyDisplayingSubtitles = true;
        }

        // Disable subtitles but only if they were enabled by the monitor
        private void ReachedOriginalPosition()
        {
            LogInfo($"{_deviceName}: Reached original position {GetTimeString(_latestWatchedPosition)} for {_activeSession.MediaTitle}", Yellow);
            StopSubtitlesIfNotUserEnabled();
        }

        public void StopSubtitlesIfNotUserEnabled()
        {
            if (_temporarilyDisplayingSubtitles)
            {
                _activeSession.DisableSubtitles();
                _temporarilyDisplayingSubtitles = false;
            }
        }

        // Disables subtitles regardless of how they were enabled
        private void ForceStopShowingSubtitles()
        {
            _activeSession.DisableSubtitles();
            _temporarilyDisplayingSubtitles = false;
        }

        private void SetLatestWatchedPosition(double newTime)
        {
            // If we're in a cooldown, that means the user might still be rewinding further,
            // so we don't want to update the latest watched position until the cooldown is over,
            // otherwise when they finish rewinding beyond the max it might result in showing subtitles again
            if (_cooldownCyclesLeft == 0)
                _latestWatchedPosition = newTime;
        }

        private void PrintTimelineDebugMessage(double positionSec, bool isFromNotification, bool temporarySubtitlesWereEnabledForPass, string prepend="")
        {
            string subtitlesStatus = _activeSession.KnownIsShowingSubtitles.HasValue
                ? _activeSession.KnownIsShowingSubtitles.Value.ToString()
                : "Unknown";

            string msgPart1 = $"           > {_deviceName}: Position: {positionSec} " +
                $"| Latest: {_latestWatchedPosition} " +
                $"| Prev: {_previousPosition} " +
                $"|  Actually/Expected Showing Subs: {subtitlesStatus}/{temporarySubtitlesWereEnabledForPass} " +
                //$"| FromNotification: {isFromNotification} " + // Not currently using notifications
                $"| UserEnabledSubs: ";

            msgPart1 = prepend + msgPart1;

            string msgPart2 = _subtitlesUserEnabled.ToString();

            // Print last part about user subs with color if enabled so it's more obvious
            if (_subtitlesUserEnabled)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                WriteColorParts(msgPart1, msgPart2, ConsoleColor.White, ConsoleColor.Red);
            }
            else
            {
                WriteLineSafe(msgPart1 + msgPart2);
                Task.Run(() => MyLogger.LogToFile(msgPart1 + msgPart2));
            }
        }

        // This is a point-in-time function that will stop subtitles based on last known position and collected data
        // It might be called from a polling loop at a regular interval, or can be updated 'out-of-phase' from a plex server notification
        //      Doing so should not interrupt the loop intervals but will allow for more instant reactions to user input
        public void MakeMonitoringPass(bool isFromNotification = false)
        {
            try
            {
                double positionSec = _activeSession.GetPlayPositionSeconds();
                bool temporarySubtitlesWereEnabledForPass = _temporarilyDisplayingSubtitles; // Store the value before it gets updated to print at the end

                // If the position is the same as before, we don't have any new info so don't do anything
                if (positionSec == _previousPosition)
                {
                    LogDebugExtra($"{_deviceName}: Ignoring message without new data.");
                    return;
                }

                double _smallestResolution = Math.Max(_activeFrequencySec, _activeSession.SmallestResolutionExpected);
                    
                // If the user had manually enabled subtitles, check if they disabled them
                if (_subtitlesUserEnabled)
                {
                    SetLatestWatchedPosition(positionSec);
                    // If the active subtitles are empty, the user must have disabled them
                    if (_activeSession.KnownIsShowingSubtitles == false)
                    {
                        _subtitlesUserEnabled = false;
                    }
                }
                // If we know there are subtitles showing but we didn't enable them, then the user must have enabled them.
                // In this case again we don't want to stop them, so this is an else-if to prevent it falling through to the else
                else if (!_temporarilyDisplayingSubtitles && _activeSession.KnownIsShowingSubtitles == true)
                {
                    _subtitlesUserEnabled = true;
                    SetLatestWatchedPosition(positionSec);
                    LogInfo($"{_deviceName}: User appears to have enabled subtitles manually.", Yellow);
                }
                // Only further process & check for rewinds if the user hasn't manually enabled subtitles
                else
                {
                    // These all stop subtitles, so only bother if they are currently showing
                    if (_temporarilyDisplayingSubtitles)
                    {
                        // If the user fast forwards, stop showing subtitles
                        if (positionSec > _previousPosition + Math.Max(_smallestResolution + 2, _fastForwardThreshold)) //Setting minimum to 7 seconds to avoid false positives
                        {
                            LogInfo($"{_deviceName}: Force stopping subtitles for {_activeSession.MediaTitle} - Reason: User fast forwarded", Yellow);

                            SetLatestWatchedPosition(positionSec);
                            StopSubtitlesIfNotUserEnabled();
                        }
                        // If they rewind too far, stop showing subtitles, and reset the latest watched position
                        else if (positionSec < _latestWatchedPosition - _maxRewindAmountSec)
                        {
                            LogInfo($"{_deviceName}: Force stopping subtitles for {_activeSession.MediaTitle} - Reason: User rewound too far. Initiating cooldown.", Yellow);

                            SetLatestWatchedPosition(positionSec);
                            StopSubtitlesIfNotUserEnabled();

                            // Initiate a cooldown, because if the user is rewinding in steps with a remote with brief pauses,
                            //      further rewinds may be interpreted as rewinds to show subtitles again
                            // If in accurate mode, cooldown for 2 cycles (2 seconds), otherwise 1 cycle since that's about 5 seconds.

                            // Note: Add 1 to the actual number of cooldowns you want because we decrement it immediately after at the end of the loop
                            if (_activeSession.AccurateTimeMs != null)
                            {
                                _cooldownCyclesLeft = DefaultCooldownCount;
                                _cooldownToUse = DefaultCooldownCount;
                            }
                            else
                            {
                                _cooldownCyclesLeft = 2;
                                _cooldownToUse = 2;
                            }

                        }
                        // Check if the position has gone back by the rewind amount.
                        // Add smallest resolution to avoid stopping subtitles too early
                        else if (positionSec > _latestWatchedPosition + _smallestResolution)
                        {
                            ReachedOriginalPosition();
                            SetLatestWatchedPosition(positionSec);
                        }
                    }
                    // Special handling during cooldown
                    else if (_cooldownCyclesLeft > 0)
                    {
                        // If they have fast forwarded
                        if (positionSec > _previousPosition + Math.Max(_smallestResolution + 2, _fastForwardThreshold)) //Setting minimum to 7 seconds to avoid false positives
                        {
                            LogInfo($"{_deviceName}: Cancelling cooldown - Reason: User fast forwarded during cooldown", Yellow);
                            SetLatestWatchedPosition(positionSec);
                            _cooldownCyclesLeft = 0; // Reset cooldown
                        }
                        else if (!isFromNotification)
                        {
                            _cooldownCyclesLeft--;

                            // If the user rewinded again while in cooldown, we want to reset the cooldown
                            if (positionSec < _previousPosition - 2)
                            {
                                _cooldownCyclesLeft = _cooldownToUse;
                            }

                            LogDebug($"{_deviceName}: Cooldown cycles left: {_cooldownCyclesLeft}");
                        }
                    }
                    // Check if the position has gone back by 2 seconds or more. Using 2 seconds just for safety to be sure.
                    // But don't count it if the rewind amount goes beyond the max.
                    // Since at this point it isn't displaying subtitles we can technically use either _previousPosition or _latestWatchedPosition to check for rewinds.
                    // Only _previousPosition works with the cooldown but that doesn't matter because we handle that in the other else if
                    else if ((positionSec < _latestWatchedPosition - 2) && !(positionSec < _latestWatchedPosition - _maxRewindAmountSec))
                    {
                        RewindOccurred();
                    }
                    // Otherwise update the latest watched position
                    else
                    {
                        SetLatestWatchedPosition(positionSec);
                    }
                }

                // Print the timeline debug message at the end of the pass so the watch position related data is updated
                // But use the temporary subtitles value from the start of the pass because any changes wouldn't have taken effect yet because the player takes time to do it
                if (Program.config.ConsoleLogLevel >= LogLevel.Debug) PrintTimelineDebugMessage(positionSec, isFromNotification, temporarySubtitlesWereEnabledForPass);

                _previousPosition = positionSec;

            }
            catch (Exception e)
            {
                LogError($"{_deviceName}: Error in monitor iteration: {e.Message}");
                // Add a small delay to avoid tight loop on errors
                //Thread.Sleep(1000); // Moving the delay to more global loop
            }
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            StopSubtitlesIfNotUserEnabled();
        }

        public void RestartMonitoring()
        {
            _isMonitoring = true;
        }

        public void SetupMonitoringInitialConditions()
        {
            if (_isMonitoring)
            {
                LogDebug("Already monitoring this session");
                return;
            }

            try
            {
                if (_activeSession.KnownIsShowingSubtitles == true)
                {
                    _subtitlesUserEnabled = true;
                }

                _latestWatchedPosition = _activeSession.GetPlayPositionSeconds();
                LogDebug($"Before thread start - position: {_latestWatchedPosition} -- Previous: {_previousPosition} -- UserEnabledSubtitles: {_subtitlesUserEnabled}\n");

                _previousPosition = _latestWatchedPosition;
                _isMonitoring = true;

                MakeMonitoringPass(); // Run the monitoring pass directly instead of in a separate thread since they all need to be updated together anyway

                LogDebug($"Finished setting up monitoring for {_deviceName} and ran first pass.");
            }
            catch (Exception e)
            {
                LogError($"Error during monitoring setup: {e.Message}");
                if (Program.config.ConsoleLogLevel >= LogLevel.Debug)
                    WriteLineSafe(e.StackTrace);
            }
        }
    }
}