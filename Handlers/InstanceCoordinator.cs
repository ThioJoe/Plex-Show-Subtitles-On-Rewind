﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text; // Added for Encoding
using System.Threading;
using System.Threading.Tasks;

// Add using static for your Logger if applicable
// using static RewindSubtitleDisplayerForPlex.Logger;

namespace RewindSubtitleDisplayerForPlex
{
    public static class InstanceCoordinator
    {
        // --- Unique Identifiers (Ensure GUID is set) ---
        private const string AppGuid = "{391AE04C-125D-11F0-8C20-2A0F2161BBC3}"; // Use your actual GUID
        private static readonly string AppNameDashed = "RewindSubtitleDisplayerForPlex"; // Or get dynamically
        // Added version markers to ensure fresh handles/pipes if testing iterations
        private static string AnyoneHereEventName = $"Global\\{AppNameDashed}_{AppGuid}_AnyoneHere";
        private static string ShutdownEventName = $"Global\\{AppNameDashed}_{AppGuid}_Shutdown";
        private const string PipeName = $"RewindSubtitleDisplayer_{AppGuid}_InstanceCheckPipe";

        // --- Event Handles ---
        private static EventWaitHandle? _anyoneHereEvent;
        private static EventWaitHandle? _shutdownEvent;

        // --- Configuration ---
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500); // Timeout for EI connecting to NI pipe
        private static readonly TimeSpan ConnectionAttemptTimeout = TimeSpan.FromMilliseconds(1500); // How long NI waits for ANY connection after signaling AnyoneHere
        private static readonly int ConsecutiveTimeoutThreshold = 2; // How many consecutive timeouts NI allows before assuming done
        private static readonly TimeSpan OverallCheckTimeout = TimeSpan.FromSeconds(10); // Max time NI spends checking

        // --- State for EIs ---
        private static CancellationTokenSource? _eiListenerCts;

        // --- Initialization and Global Handle Management ---
        // In InitializeHandles: Change the EventResetMode for AnyoneHere BACK to ManualReset
        public static bool InitializeHandles()
        {
            try
            {
                // Use ManualReset for AnyoneHere (to signal all) and Shutdown
                _anyoneHereEvent = CreateOrOpenEvent(AnyoneHereEventName, EventResetMode.ManualReset); // <-- CHANGE BACK
                _shutdownEvent = CreateOrOpenEvent(ShutdownEventName, EventResetMode.ManualReset);
                if (_anyoneHereEvent == null || _shutdownEvent == null)
                {
                    throw new InvalidOperationException("Failed to create or open required coordination handles.");
                }
                return true;
            }
            catch (Exception ex)
            {
                LogError($"FATAL: Error creating/opening coordination handles: {ex.Message}. Cannot proceed.");
                return false;
            }
        }

        private static EventWaitHandle CreateOrOpenEvent(string name, EventResetMode mode)
        {
            EventWaitHandle? handle = null;
            bool createdNew = false;
            try
            {
                // Use the 'mode' parameter passed in
                handle = new EventWaitHandle(false, mode, name, out createdNew);
                LogDebug($"Handle '{name}': {(createdNew ? "Created new" : "Opened existing")} (Mode: {mode})"); // Log the mode
                return handle;
            }
            // ... (rest of catch blocks remain the same) ...
            catch (Exception exCreate)
            {
                LogError($"Generic error creating/opening handle '{name}': {exCreate.Message}");
                handle?.Dispose();
                throw;
            }
        }

        // --- NI Logic: Check for Duplicate Servers ---
        public static async Task<bool> CheckForDuplicateServersAsync(string myServerUrl, bool allowDuplicates = false)
        {
            if (_anyoneHereEvent == null) { LogError("Coordination handle not initialized."); return false; }

            LogInfo("Checking for other instances monitoring the same server...");
            var respondedPidsThisCheckin = new HashSet<int>();
            var overallStopwatch = Stopwatch.StartNew();
            bool duplicateFound = false;

            // --- Signaling Logic (ManualResetEvent) ---
            try
            {
                // 1. Ensure event is initially reset
                _anyoneHereEvent.Reset();
                LogDebug("Ensured AnyoneHere is Reset.");

                // 2. Signal all waiting EIs
                LogDebug("Signaling AnyoneHere? (Once, ManualReset)");
                _anyoneHereEvent.Set();

                // 3. Wait briefly to allow EIs to wake up
                // Adjust delay if needed, 200-250ms is often sufficient
                int wakeUpDelayMs = 250;
                LogDebug($"Waiting {wakeUpDelayMs}ms for EIs to wake...");
                await Task.Delay(wakeUpDelayMs);

                // 4. Reset the event BEFORE starting pipe listener loop
                // This prevents EIs from re-triggering on the same signal if they loop quickly
                _anyoneHereEvent.Reset();
                LogDebug("Reset AnyoneHere signal after delay.");
            }
            catch (ObjectDisposedException)
            {
                LogError("Error: AnyoneHere event was disposed during signaling phase.");
                return false; // Cannot proceed if handle is bad
            }
            catch (Exception exSignal)
            {
                LogError($"Error during AnyoneHere signaling/reset phase: {exSignal.Message}");
                // Optionally reset again in case of error during delay/reset
                try { _anyoneHereEvent?.Reset(); } catch { }
                // Decide if you can continue or should return false
                // return false; // Safer to abort if signaling failed
            }
            // --- End Signaling Logic ---


            LogDebug($"NI: Listening for connections for up to {OverallCheckTimeout.TotalSeconds} seconds...");
            try
            {
                // Loop, attempting to accept connections until timeout or duplicate found (if blocking duplicates)
                while (overallStopwatch.Elapsed < OverallCheckTimeout)
                {
                    // Exit loop immediately if a duplicate is found and we don't allow them
                    if (!allowDuplicates && duplicateFound)
                    {
                        LogDebug("Duplicate found and not allowed, stopping listening.");
                        break;
                    }

                    using var connectionTimeoutCts = new CancellationTokenSource(ConnectionAttemptTimeout);

                    LogDebug($"NI: Waiting for next connection attempt (timeout: {ConnectionAttemptTimeout.TotalMilliseconds}ms)...");
                    (int clientPid, string? receivedUrl) = await RunPipeServerCycleAsync(PipeName, connectionTimeoutCts.Token);

                    // Condition: WaitForConnectionAsync timed out - means no EIs (that woke up) are left waiting to connect
                    if (clientPid == -1 && connectionTimeoutCts.IsCancellationRequested)
                    {
                        LogInfo($"No connection attempts within timeout ({ConnectionAttemptTimeout.TotalMilliseconds}ms). Assuming check complete.");
                        break; // Exit the main listening loop
                    }

                    // Condition: Something else cancelled the wait (e.g. overall timeout triggered cancellation source externally)
                    if (connectionTimeoutCts.IsCancellationRequested && clientPid == -1)
                    {
                        LogWarning("CheckForDuplicateServersAsync cancelled while waiting for connection.");
                        break; // Exit the main listening loop
                    }


                    // --- Process a successful connection ---
                    if (clientPid != -1 && receivedUrl != null)
                    {
                        LogDebug($"NI received PID {clientPid} and URL '{receivedUrl}'");
                        if (respondedPidsThisCheckin.Add(clientPid)) // True if PID was new for this check cycle
                        {
                            LogInfo($"Received response from new instance PID {clientPid}: {receivedUrl}");
                            if (string.Equals(myServerUrl, receivedUrl, StringComparison.OrdinalIgnoreCase))
                            {
                                LogError($"Duplicate instance (PID {clientPid}) found monitoring the same server: {myServerUrl}");
                                duplicateFound = true;
                                // Loop will break on next iteration if !allowDuplicates
                            }
                        }
                        else
                        {
                            LogDebug($"Ignoring duplicate response from PID {clientPid} in this cycle.");
                        }
                    }
                    else // Handle case where RunPipeServerCycleAsync returned (-1, null) but NOT due to timeout
                    {
                        LogWarning($"Pipe server cycle completed without valid data (PID={clientPid}, URL={receivedUrl}). Client disconnect or internal error?");
                        // Continue listening for other potential clients
                    }
                } // End while loop
            }
            catch (Exception ex)
            {
                LogError($"Error during instance check pipe listening loop: {ex.Message}");
                // Ensure event is reset in case of unexpected exit
                try { _anyoneHereEvent?.Reset(); } catch { }
            }
            finally
            {
                // Event should already be reset from the signaling phase, no reset needed here.
                overallStopwatch.Stop();
                LogDebug($"Instance check loop finished. Duration: {overallStopwatch.Elapsed}");
            }

            if (!duplicateFound) { LogInfo("Duplicate server check complete. No duplicates found."); }
            // Return true if a duplicate was found, false otherwise
            return duplicateFound;
        }


        // --- Pipe Server Logic for ONE Cycle (Run by NI) ---
        private static async Task<(int pid, string? url)> RunPipeServerCycleAsync(string pipeName, CancellationToken cancellationToken)
        {
            NamedPipeServerStream? pipeServer = null;
            int clientPid = -1;
            string? clientUrl = null;
            try
            {
                pipeServer = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); // Added CurrentUserOnly

                LogDebug($"NI: Pipe server created ('{pipeName}'). Waiting for connection...");
                await pipeServer.WaitForConnectionAsync(cancellationToken);

                // Check immediately after wait completes
                if (cancellationToken.IsCancellationRequested)
                {
                    LogDebug("NI: Cancellation requested immediately after connection wait.");
                    return (-1, null);
                }
                LogDebug("NI: Client connected.");

                // Removed setting pipeServer.ReadTimeout

                // Use using declarations for guaranteed disposal
                using BinaryReader reader = new BinaryReader(pipeServer, Encoding.UTF8, leaveOpen: true);
                using StreamReader sReader = new StreamReader(pipeServer, Encoding.UTF8, leaveOpen: true);

                try
                {
                    clientPid = reader.ReadInt32(); // Read PID first
                                                    // Use ReadLineAsync without token if not available, relies on pipe break/close
                                                    // For .NET versions supporting it, pass the token:
                                                    // clientUrl = await sReader.ReadLineAsync(cancellationToken);
                    clientUrl = await sReader.ReadLineAsync(); // Simpler version

                    LogDebug($"NI: Received PID '{clientPid}', URL '{clientUrl ?? "<null>"}'");
                }
                catch (EndOfStreamException)
                { // Client disconnected before sending everything
                    LogWarning("NI: Pipe closed by client before receiving expected data.");
                    clientPid = -1; clientUrl = null;
                }
                catch (IOException ioEx)
                { // Other pipe errors during read
                    LogWarning($"NI: Pipe IO error during read: {ioEx.Message}");
                    clientPid = -1; clientUrl = null;
                }
                // Let other exceptions propagate for now

                return (clientPid, clientUrl);
            }
            catch (OperationCanceledException)
            {
                LogDebug("NI: Pipe connection wait was canceled.");
                return (-1, null);
            }
            catch (IOException ioEx) when (ioEx.Message.Contains("All pipe instances are busy"))
            {
                LogWarning($"NI: Pipe server IO error (Instances busy?): {ioEx.Message}"); // Should be less likely now
                return (-1, null);
            }
            catch (IOException ioEx)
            {
                LogWarning($"NI: Pipe server IO error: {ioEx.Message}");
                return (-1, null);
            }
            catch (Exception ex)
            {
                LogError($"NI: Pipe server unexpected error: {ex.Message}");
                return (-1, null);
            }
            finally
            {
                try { if (pipeServer?.IsConnected ?? false) pipeServer.Disconnect(); } catch { }
                pipeServer?.Dispose();
                LogDebug("NI: Pipe server cycle finished, stream disposed.");
            }
        }

        // --- EI Logic: Listener Task ---
        public static void StartExistingInstanceListener(string myServerUrl, CancellationToken appShutdownToken)
        {
            if (_anyoneHereEvent == null) return; // Handle not ready

            _eiListenerCts = CancellationTokenSource.CreateLinkedTokenSource(appShutdownToken);
            var token = _eiListenerCts.Token;

            Task.Run(async () =>
            {
                LogInfo("EI: Starting listener for 'AnyoneHere?' signals...");
                var handles = new WaitHandle[] { token.WaitHandle, _anyoneHereEvent };

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        LogDebug("EI: Waiting for AnyoneHere signal or shutdown...");
                        int signaledIndex = WaitHandle.WaitAny(handles); // Wait indefinitely

                        if (token.IsCancellationRequested || signaledIndex == 0) { break; }

                        if (signaledIndex == 1) // anyoneHereEvent was signaled
                        {
                            LogDebug("EI: Received 'AnyoneHere' signal. Attempting response...");

                            NamedPipeClientStream? pipeClient = null;
                            try
                            {
                                pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
                                LogDebug("EI: Attempting to connect to NI pipe...");
                                // Give slightly less time than NI waits for connection attempt to avoid race condition
                                await pipeClient.ConnectAsync((int)(ConnectionAttemptTimeout.TotalMilliseconds * 0.9), token);
                                LogDebug("EI: Connected to NI pipe.");

                                using (var writer = new BinaryWriter(pipeClient, Encoding.UTF8, leaveOpen: true))
                                {
                                    writer.Write(Process.GetCurrentProcess().Id); writer.Flush();
                                }
                                using (var sWriter = new StreamWriter(pipeClient, Encoding.UTF8, leaveOpen: true))
                                {
                                    await sWriter.WriteLineAsync(myServerUrl); await sWriter.FlushAsync();
                                }
                                LogDebug($"EI: Sent PID {Process.GetCurrentProcess().Id} and URL. Closing pipe.");
                            }
                            catch (OperationCanceledException) { LogWarning("EI: Connection attempt cancelled."); }
                            catch (TimeoutException) { LogWarning($"EI: Timeout connecting to NI pipe (busy/finished/NI exited?)."); } // More likely NI finished
                            catch (IOException ioEx) { LogError($"EI: Pipe IO error connecting/sending: {ioEx.Message}"); }
                            catch (Exception ex) { LogError($"EI: Error connecting/sending to NI pipe: {ex.Message}"); }
                            finally
                            {
                                pipeClient?.Dispose();
                                LogDebug("EI: Pipe client disposed.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"EI Listener loop error: {ex.Message}");
                        try { await Task.Delay(1000, token); } catch { /* Ignore cancellation */ }
                    }
                }
                LogInfo("EI Listener task finished.");
            }, token);
        }

        public static void StopExistingInstanceListener() => _eiListenerCts?.Cancel();

        // --- Shutdown Signal Logic (Using Constructor) ---
        public static bool SignalShutdown()
        {
            EventWaitHandle? handle = null;
            bool createdNew;
            try
            {
                handle = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName, out createdNew);
                if (createdNew)
                {
                    LogInfo("No existing instance found to signal shutdown (created new handle)."); return true;
                }
                else
                {
                    LogInfo("Existing instance found. Signaling shutdown..."); handle.Set();
                    LogInfo("Shutdown signal sent successfully."); return true;
                }
            }
            catch (Exception ex) { LogError($"Error during shutdown signaling process for handle '{ShutdownEventName}': {ex.Message}"); return false; }
            finally { handle?.Dispose(); }
        }

        public static WaitHandle GetShutdownWaitHandle() => _shutdownEvent ?? throw new InvalidOperationException("Shutdown handle not initialized.");

        // --- Cleanup ---
        public static void Cleanup()
        {
            LogDebug("Cleaning up coordination handles...");
            _anyoneHereEvent?.Dispose();
            _shutdownEvent?.Dispose();
            _anyoneHereEvent = null;
            _shutdownEvent = null;
            LogDebug("Coordination handles disposed.");
        }

        // --- Logging Placeholders ---
        // Ensure these methods exist and are accessible, e.g., public static in Program or own class
        //private static void LogInfo(string message) => Console.WriteLine($"INFO: {message}");
        //private static void LogDebug(string message) { if (Program.debugMode) Console.WriteLine($"DEBUG: {message}"); }
        //private static void LogWarning(string message) => Console.WriteLine($"WARN: {message}");
        //private static void LogError(string message) => Console.WriteLine($"ERROR: {message}");
    }
}