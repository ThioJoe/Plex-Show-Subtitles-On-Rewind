﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static RewindSubtitleDisplayerForPlex.SubtitlesHotkeyMonitor;

namespace RewindSubtitleDisplayerForPlex;


internal class SubtitlesHotkeyMonitor
{
    private string PlaybackID;
    private string MachineID;
    private ActiveSession AttachedActiveSession;
    private static List<SubtitlesHotkeyMonitor> _allHotkeyMonitors = [];

    // -------------------------------------
    private int msOfLastPause = 0;
    private int msOfLastPlay = 0;
    private int currentTime = 0;
    private Action lastAction = Action.None;
    CancellationTokenSource doubleClickCancelTokenSource = new CancellationTokenSource();

    // Options
    private readonly int clickTimeThreshold = 250; // 500 ms threshold for double click detection
    public HotkeyAction DoubleClickAction { get; set; } = HotkeyAction.None;
    public HotkeyAction TripleClickAction { get; set; } = HotkeyAction.ToggleSubtitles;

    // Constructor
    public SubtitlesHotkeyMonitor(string playbackID, string machineID, ActiveSession activeSession)
    {
        PlaybackID = playbackID;
        MachineID = machineID;
        AttachedActiveSession = activeSession;
        _allHotkeyMonitors.Add(this);
    }

    // This method is called when the play/pause key is pressed.
    public void OnPlayPauseKeyPress(Action action)
    {
        // Need to monitor for double presses of the play/pause key.
        // For Double Click: Pause -> Play          (Just need to know current time and last pause time)
        // For Triple Click: Pause -> Play -> Pause (Need to know current time, last play time, and last pause time)

        // For the purposes of the hotkey, we'll treat a buffering action the same as playing.
        //  But a playing action immediately following buffering will be ignored

        // Ignore buffering action immediately after playing
        if (action == Action.Play && lastAction == Action.Buffering)
        {
            return;
        }

        // -------------------------------------------------------
        currentTime = Environment.TickCount;
        int pauseTimeDiff = currentTime - msOfLastPause;
        int playTimeDiff = currentTime - msOfLastPlay;

        // If the action is Play or Buffering, update the last play time
        if (action == Action.Play || action == Action.Buffering)
        {
            msOfLastPlay = currentTime;
            lastAction = action;
        }
        else if (action == Action.Pause) // If the action is Pause, update the last pause time
        {
            msOfLastPause = currentTime;
            lastAction = action;
        }

        // Detect double and triple clicks. All else-ifs because a triple click should not be detected as a double click.

        // ----- Triple Click -----
        if ((action == Action.Play || action == Action.Buffering)   // Current click - Play
                && pauseTimeDiff < clickTimeThreshold                   // Last click - Pause
                && playTimeDiff < clickTimeThreshold)                   // Preceding click - Play
        {
            OnTripleClick();
        }
        else if (action == Action.Pause                     // Current click - Pause
            && playTimeDiff < clickTimeThreshold            // Last click - Play
            && pauseTimeDiff < (clickTimeThreshold * 2))    // Preceding click - Pause -- Allow twice as much time since it's a triple click
        {
            OnTripleClick();
        }
        // ----- Double Click -----
        else if ((action == Action.Play || action == Action.Buffering) && pauseTimeDiff < clickTimeThreshold)
        {
            OnPossibleDoubleClick();

        }
        else if (action == Action.Pause && playTimeDiff < clickTimeThreshold)
        {
            OnPossibleDoubleClick();
        }

    } // ----------------- End of OnPlayPauseKeyPress -----------------

    public void OnPossibleDoubleClick()
    {
        // This method is called when a possible double click is detected.
        // It will wait for the clickTimeThreshold before firing the actual OnDoubleClick function. (The time within a third click would have to be)
        // If a triple click is detected before the threshold time, this method will be cancelled.
        // Start a new thread to wait for the threshold time

        // -------------------------------------------------------
        // Cancel any previous waiting operation
        doubleClickCancelTokenSource.Cancel();
        // Create a new token source for this operation
        doubleClickCancelTokenSource = new CancellationTokenSource();
        CancellationToken token = doubleClickCancelTokenSource.Token;

        // Use Task.Run instead of creating a raw thread
        Task.Run(async () =>
        {
            try
            {
                // Use Task.Delay instead of Thread.Sleep
                await Task.Delay(clickTimeThreshold, token);

                // Check if cancellation was requested
                if (!token.IsCancellationRequested)
                {
                    OnDoubleClick();
                }
            }
            catch (TaskCanceledException)
            {
                // Task was canceled, no action needed
            }
        });
    }

    public void OnDoubleClick()
    {
        Action finalAction = lastAction;
        ResetPrevInfo();
        LogDebugExtra("Double click action triggered.", Yellow);

        HotkeyActionFunctionDirector(DoubleClickAction);
    }
    public void OnTripleClick()
    {
        // Cancel the pending double click operation if it's still waiting
        doubleClickCancelTokenSource.Cancel();

        Action finalAction = lastAction;
        ResetPrevInfo();
        LogDebugExtra("Triple click action triggered.", Yellow);

        // Perform a restorative action based on the last action, since 3 causes the opposite of the original state
        if (finalAction == Action.Play || finalAction == Action.Buffering)
        {
            // Perform a pause action
            _ = PlexServer.SendPauseCommand(machineID: MachineID, sendDirectToDevice: true, activeSession: AttachedActiveSession);
        }
        else if (finalAction == Action.Pause)
        {
            // Perform a play action
            _ = PlexServer.SendPlayCommand(machineID: MachineID, sendDirectToDevice: true, activeSession: AttachedActiveSession);
        }

        // Perform the action based on the hotkey action
        HotkeyActionFunctionDirector(TripleClickAction);
    }

    // -----------------------------------------------------
    private void ResetPrevInfo()
    {
        msOfLastPause = 0;
        msOfLastPlay = 0;
        currentTime = 0;
        lastAction = Action.None;
    }

    public static void ForwardActionToMonitorByID(string? machineID, Action action)
    {
        if (string.IsNullOrEmpty(machineID))
        {
            LogWarning("Machine ID is null or empty. Cannot forward action.");
            return;
        }

        // Find the monitor with the specified playbackID
        SubtitlesHotkeyMonitor? monitor = _allHotkeyMonitors.FirstOrDefault(m => m.MachineID == machineID);
        if (monitor != null)
        {
            // Call the OnPlayPauseKeyPress method on the found monitor
            monitor.OnPlayPauseKeyPress(action);
        }
        else
        {
            LogWarning($"No monitor found for playbackID trying to forward event to proper hotkey monitor: {machineID}");
        }
    }

    private static void ToggleSubtitles(string machineID)
    {
        RewindMonitor? monitor = MonitorManager.GetMonitorForMachineID(machineID);
        if (monitor == null)
        {
            LogWarning($"No monitor found for machineID: {machineID}");
            return;
        }

        // Get the current subtitles state
        if (monitor.SubtitlesAreShowing)
        {
            monitor.StopSubtitlesWithRetry(force: true);
        }
        else
        {
            monitor.StartSubtitlesWithRetry(persist:true);
        }
    }

    private void HotkeyActionFunctionDirector(HotkeyAction hotkeyAction)
    {
        // Perform the action based on the hotkey action
        switch (hotkeyAction)
        {
            case HotkeyAction.ToggleSubtitles:
                ToggleSubtitles(MachineID);
                break;
            case HotkeyAction.None:
                // No action needed
                break;
            default:
                LogWarning($"Unknown hotkey action: {hotkeyAction}");
                break;
        }
    }

    //  ------------------------------------------
    public enum Action
    {
        Play,
        Pause,
        Buffering,
        None
    }

    public enum HotkeyAction // What to do when the hotkey is activated
    {
        None,
        ToggleSubtitles
    }

    public enum HotkeyMode
    {
        DoubleClick,
        TripleClick
    }
}
