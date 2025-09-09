using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;

namespace RewindSubtitleDisplayerForPlex
{
    // PlexServer class which contains many methods to interact with the Plex server
    public static class PlexServer
    {
        private static string _serverUrl = string.Empty;
        private static string _token = string.Empty;
        private static string _appClientID = string.Empty;
        private static HttpClient _httpClient = new HttpClient();
        private static HttpClient _httpClientShortTimeout = new HttpClient();

        public static void SetupPlexServer(string url, string token, string appClientID)
        {
            Dictionary<string, string> defaultHeadersDict = new()
            {
                { "X-Plex-Token", token },
                { "X-Plex-Client-Identifier", appClientID }
            };

            _serverUrl = url;
            _token = token;
            _appClientID = appClientID;

            _httpClient = Utils.AddHttpClientHeaders(_httpClient, defaultHeadersDict);
            _httpClientShortTimeout = Utils.AddHttpClientHeaders(_httpClientShortTimeout, defaultHeadersDict);

            int shortTimeout = Program.config.ShortTimeoutLimit;
            _httpClientShortTimeout.Timeout = TimeSpan.FromMilliseconds(shortTimeout); // Will be used between loop iterations which only last a second
        }

        // In certain places we want to ensure we don't overload the server with similar or contradictory requests
        // So this basically ensures they are sent one at a time
        public static HttpResponseMessage SendRequestNoCollision(HttpRequestMessage request)
        {
            // This method is called from a thread, so we need to use the semaphore to protect the HTTP client
            _httpClientSemaphore.Wait();
            try
            {
                return _httpClient.SendAsync(request).Result;
            }
            finally
            {
                _httpClientSemaphore.Release();
            }
        }

        public static async Task<List<PlexResource>> GetResources()
        {
            string responseString;
            HttpRequestMessage request;
            HttpResponseMessage response;
            List<PlexResource> resources = [];

            Dictionary<string, string> moreHeaders = new()
            {
                { "Accept", "application/json" },
            };

            try
            {
                request = new HttpRequestMessage(HttpMethod.Get, $"https://plex.tv/api/v2/resources");
                request = Utils.AddHttpRequestHeaders(request, moreHeaders);

                response = await _httpClient.SendAsync(request);
                responseString = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                LogError($"Error getting resources during HTTP Request: {ex.Message}");
                return resources;
            }

            if (!response.IsSuccessStatusCode)
            {
                string statusCode = ((int)response.StatusCode).ToString();
                string statusName = response.StatusCode.ToString();
                string errorText = await response.Content.ReadAsStringAsync();
                errorText = errorText.Replace("\n", " ");
                LogError($"Error getting resources: {statusCode} ({statusName}), Error: {errorText}");
                return resources;
            }
            else
            {
                // Deserialize the response string to a list of resources
                try
                {
                    // Use the source-generated context
                    List<PlexResource>? returnedResources = JsonSerializer.Deserialize(responseString,PlexResourceJsonContext.Default.ListPlexResource);

                    if (returnedResources != null)
                    {
                        resources = returnedResources;
                    }
                    else
                    {
                        LogError("Failed to deserialize resources.");
                    }
                }
                catch (JsonException jsonEx)
                {
                    LogError($"Error deserializing resources: {jsonEx.Message}");
                }
                catch (Exception ex)
                {
                    LogError($"Unexpected error deserializing resources: {ex.Message}");
                }
            }

            return resources;
        }

        // Using XmlSerializer to get sessions
        public static async Task<List<PlexSession>?> GetSessionsAsync(bool shortTimeout = false)
        {
            HttpClient httpClientToUse = shortTimeout ? _httpClientShortTimeout : _httpClient;

            try
            {
                string responseString;
                HttpResponseMessage response = await httpClientToUse.GetAsync($"{_serverUrl}/status/sessions");
                responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string statusCode = ((int)response.StatusCode).ToString();
                    string statusName = response.StatusCode.ToString();
                    string errorText = await response.Content.ReadAsStringAsync();
                    errorText = errorText.Replace("\n", " ");
                    LogError($"Error getting sessions: {statusCode} ({statusName}), Error: {errorText}");
                    return null;
                }

                // Deserialize directly to your model
                SessionMediaContainer? container = XmlResponseParser.ParseMediaContainer(responseString);

                if (container != null)
                {
                    // Store raw XML in case it's useful
                    foreach (PlexSession session in container.Sessions)
                    {
                        session.RawXml = responseString;
                    }

                    //LogDebugExtra($"Found {container.Sessions.Count} active Plex sessions");

                    container.Sessions = SessionHandler.UpdatePlayerPorts(container.Sessions);

                    return container.Sessions;
                }
                else
                {
                    LogError("Something went wrong deserializing the sessions MediaContainer. It returned null.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogError($"Error getting sessions (Will return what it can): {ex.Message}");
                return null;
            }
        }

        public static async Task<ConnectionResult> TestConnectionAsync()
        {
            try
            {
                string testUrl = $"{_serverUrl}/";
                HttpResponseMessage response = await _httpClient.GetAsync(testUrl);

                if (response.IsSuccessStatusCode)
                {
                    LogSuccess("\nConnection successful!");
                    return ConnectionResult.Success;
                }
                else
                {
                    int statusCode = (int)response.StatusCode;
                    string statusName = response.StatusCode.ToString();
                    string errorText = await response.Content.ReadAsStringAsync();
                    errorText = errorText.Replace("\n", " ");

                    // Try to parse the errorText XML to a ConnectionTestResponse object
                    ConnectionTestResponse? testResponse = XmlResponseParser.ParseConnectionTestResponse(errorText);

                    // If the test response is not null, then it probably wasn't a connection error, but something else, and the server should provide more info in the response we can use
                    if (testResponse != null)
                    {
                        // Show more specific error/status if available
                        string statusText = testResponse.Status ?? errorText;
                        string titleText = testResponse.Title ?? string.Empty;

                        if (titleText != string.Empty)
                            titleText = $"{titleText} - ";

                        // If it's maintenance, return that specifically so we know to check more often
                        if (testResponse.Code == "503" && String.Equals(testResponse.Title, "maintenance", StringComparison.CurrentCultureIgnoreCase))
                        {
                            LogDebug($"Connection failed. Status Code: {statusCode} ({statusName}), Reason: {titleText}{statusText}\n"); // Log maintenance error as debug only since it's sort of expected
                            return ConnectionResult.Maintenance;
                        }
                        else
                        {
                            LogWarning($"Connection failed. Status Code: {statusCode} ({statusName}), Reason: {titleText}{statusText}\n");
                            return ConnectionResult.Failure;
                        }
                    }
                    // Other types of errors that probably prevent connection in the first place
                    else
                    {
                        LogError($"Connection failed. Status Code: {statusCode} ({statusName}), Error: {errorText}\n");

                        if (statusCode == 401)
                        {
                            return ConnectionResult.Unauthorized;
                        }
                        else if (statusCode == 403)
                        {
                            return ConnectionResult.Forbidden;
                        }
                        else
                        {
                            return ConnectionResult.Failure;
                        }
                    }
                    
                }
            }
            catch (HttpRequestException hEx)
            {
                //WriteLineSafe($"Error testing connection: {hEx.Message}\n");
                if (hEx.InnerException is SocketException socketEx)
                {
                    if (socketEx.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        LogDebug("Connection refused. Server may be down or unreachable.");
                        return ConnectionResult.Refused;
                    }
                }

                // Fall through to general error handling
                LogError($"Server Connection Error: {hEx.Message}");
                // If inner exception show it
                if (hEx.InnerException != null)
                {
                    LogError($"Inner Exception: {hEx.InnerException.Message}\n");
                }
                return ConnectionResult.Failure;
            }
            catch (Exception ex)
            {
                LogError($"Server Connection Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    LogError($"Inner Exception: {ex.InnerException.Message}\n");
                }
                return ConnectionResult.Failure;
            }
        }

        public enum ConnectionResult
        {
            Success,
            Failure,
            Refused,
            Maintenance,
            Unauthorized,
            Forbidden,
            Cancelled, // Our own cancelled exception
        }

        [XmlRoot("Response")]
        public class ConnectionTestResponse
        {
            [XmlAttribute("code")]
            public string? Code { get; set; } = null;

            [XmlAttribute("title")]
            public string? Title { get; set; } = null;

            [XmlAttribute("status")]
            public string? Status { get; set; } = null;
        }


        // This method is more complex due to the different possible root nodes
        public static async Task<PlexMediaItem> FetchItemAsync(string key)
        {
            try
            {
                // Get the raw XML response string from the server
                string response = await _httpClient.GetStringAsync($"{_serverUrl}{key}");

                // Use the new parser to deserialize the XML into a PlexMediaItem
                // The parser handles the different root nodes (Video, Track, Episode) internally
                PlexMediaItem? mediaItem = XmlResponseParser.ParsePlexMediaItem(response, key);

                // Check if parsing was successful
                if (mediaItem != null)
                {
                    // Calculate subtitle count (restored from original logic)
                    int subtitleCount = mediaItem.GetSubtitleStreams().Count;
                    // Logging line (restored, still commented out as in original)
                    // WriteLineSafe($"Fetched media item: {mediaItem.Title} with {subtitleCount} subtitle streams");

                    return mediaItem;
                }
                else
                {
                    // Parsing failed, log an error and return a default/empty item
                    LogError($"Failed to parse media item response for key: {key}");
                    return new PlexMediaItem(key: key); // Return an empty item with the key
                }
            }
            catch (HttpRequestException httpEx)
            {
                LogError($"HTTP Error fetching media item {key}: {httpEx.Message}");
                return new PlexMediaItem(key: key); // Return default on error
            }
            catch (Exception ex) // Catch other potential exceptions (network issues, etc.)
            {
                LogError($"Error fetching media item {key}: {ex.Message}");
                return new PlexMediaItem(key: key); // Return default on error
            }
        }

        /// <summary>
        /// Select the subtitle stream for the current playback item (only video).
        /// </summary>
        /// <param name="subtitleStreamID">ID of the subtitle stream from the media object</param>
        /// <param name="mediaType">Media type to take action against (default: video)</param>
        /// <returns>Task representing the asynchronous operation</returns>
        public static async Task<CommandResult> SetSubtitleStreamAsync(string machineID, bool sendDirectToDevice, int subtitleStreamID, string mediaType = "video", ActiveSession? activeSession = null)
        {
            // Simply call the SetStreamsAsync method with only the subtitle stream ID parameter
            return await SetStreamsAsync(
                machineID: machineID,
                sendDirectToDevice: sendDirectToDevice,
                subtitleStreamID: subtitleStreamID,
                mediaType: mediaType,
                activeSession: activeSession
                );
        }

        /// <summary>
        /// Select multiple playback streams at once.
        /// </summary>
        /// <param name="audioStreamID">ID of the audio stream from the media object</param>
        /// <param name="subtitleStreamID">ID of the subtitle stream from the media object</param>
        /// <param name="videoStreamID">ID of the video stream from the media object</param>
        /// <param name="mediaType">Media type to take action against (default: video)</param>
        /// <param name="server">The PlexServer instance to send the command through</param>
        /// <returns>Task representing the asynchronous operation</returns>
        public static async Task<CommandResult> SetStreamsAsync(
            string machineID,
            bool sendDirectToDevice,
            string mediaType,
            int? audioStreamID = null,
            int? subtitleStreamID = null,
            int? videoStreamID = null,
            ActiveSession? activeSession = null
            )
        {
            // Create dictionary for additional parameters
            Dictionary<string, string> parameters = [];

            // Add parameters only if they're not null
            if (audioStreamID.HasValue)
                parameters["audioStreamID"] = audioStreamID.Value.ToString();

            if (subtitleStreamID.HasValue)
                parameters["subtitleStreamID"] = subtitleStreamID.Value.ToString();

            if (videoStreamID.HasValue)
                parameters["videoStreamID"] = videoStreamID.Value.ToString();

            if (!string.IsNullOrEmpty(mediaType))
                parameters["type"] = mediaType;

            // Create headers with machine identifier
            Dictionary<string, string> headers = new()
            {
                { "X-Plex-Target-Client-Identifier", machineID },
                {"Accept" , "application/json"},
            };

            if (activeSession != null)
            {
                string deviceName = activeSession.DeviceName;
                if (!string.IsNullOrEmpty(deviceName))
                {
                    headers["X-Plex-Device-Name"] = deviceName;
                }
            }

            // Send the command through the PlexServer
            return await SendCommandAsync(command: "playback/setStreams", headers, sendDirectToDevice: sendDirectToDevice, additionalParams: parameters, activeSession:activeSession);
        }

        public static async Task<CommandResult> SendPauseCommand(
            string machineID,
            bool sendDirectToDevice,
            ActiveSession? activeSession = null
            )
        {
            // Create headers with machine identifier
            Dictionary<string, string> headers = new()
            {
                { "X-Plex-Target-Client-Identifier", machineID },
                {"Accept" , "application/json"},
            };
            return await SendCommandAsync(command: "playback/pause", headers, sendDirectToDevice: sendDirectToDevice, activeSession: activeSession);
        }

        public static async Task<CommandResult> SendPlayCommand(
            string machineID,
            bool sendDirectToDevice,
            ActiveSession? activeSession = null
            )
        {
            // Create headers with machine identifier
            Dictionary<string, string> headers = new()
            {
                { "X-Plex-Target-Client-Identifier", machineID },
                {"Accept" , "application/json"},
            };
            return await SendCommandAsync(command: "playback/play", headers, sendDirectToDevice: sendDirectToDevice, activeSession: activeSession);
        }

        public static async Task<CommandResult> SeekToTime(
            int seekTimeMs,
            string machineID,
            bool sendDirectToDevice,
            ActiveSession? activeSession = null
            )
        {
            // Create dictionary for additional parameters
            Dictionary<string, string> parameters = [];

            parameters["type"] = "video";
            parameters["offset"] = seekTimeMs.ToString();

            // Create headers with machine identifier
            Dictionary<string, string> headers = new()
            {
                { "X-Plex-Target-Client-Identifier", machineID },
                {"Accept" , "application/json"},
            };

            return await SendCommandAsync(command: "playback/seekTo", headers, sendDirectToDevice: sendDirectToDevice, additionalParams: parameters, activeSession: activeSession);
        }

        // Overload
        public static async Task<TimelineMediaContainer?> GetTimelineAsync(string machineID, string deviceName, string url)
        {
            // Create headers with machine identifier
            Dictionary<string, string> headers = new()
            {
                { "X-Plex-Target-Client-Identifier", machineID },
                { "X-Plex-Device-Name", deviceName },
            };

            string newCommandID = GetNextCommandId();

            Dictionary<string, object> parameters = new()
            {
                { "wait", "0" },
                //{"commandID" , newCommandID},
            };

            string paramString = Utils.JoinArgs(parameters);

            // Build the command URL
            //string paramString = "?wait=1";
            string path = $"{url}/player/timeline/poll{paramString}";
            LogDebugExtra($"Fetching Timeline Command: {path}");

            try
            {
                HttpRequestMessage timelineRequest = new HttpRequestMessage(HttpMethod.Get, path);
                timelineRequest = Utils.AddHttpRequestHeaders(timelineRequest, headers);

                HttpResponseMessage timelineResponse = await _httpClientShortTimeout.SendAsync(timelineRequest);

                if (timelineResponse.IsSuccessStatusCode)
                {
                    string responseBody = timelineResponse.Content.ReadAsStringAsync().Result;
                    TimelineMediaContainer? container = XmlResponseParser.ParseTimelineMediaContainer(responseBody);
                    return container;
                }
                else
                {
                    // Handle error
                    LogError($"Error fetching timeline: {timelineResponse.ReasonPhrase}");
                    return null;
                }
            }
            catch (TaskCanceledException ex)
            {
                // Timeouts aren't a critical error so don't emphasize as error
                if (ex.InnerException is TimeoutException)
                {
                    LogDebugExtra($"Timeout fetching timeline from {deviceName} (Player app may have closed or device shut down.)");
                }
                else
                {
                    LogError($"Error fetching device timeline: {ex.Message}");
                }
                return null;
            }
            // Display other exceptions as errors
            catch (Exception ex)
            {
                LogError($"Error fetching device timeline: {ex.Message}");
                return null;
            }
        }

        public static async Task<CommandResult> SendCommandAsync(
            string command, 
            Dictionary<string, string> headers, 
            bool? sendDirectToDevice, 
            Dictionary<string, string>? additionalParams = null, 
            bool needResponse = false, 
            ActiveSession? activeSession = null
            )
        {
            string mainUrlBase;
            string? retryUrlBase;

            //DEBUG:
            //sendDirectToDevice = false; //DEBUG override

            if (sendDirectToDevice == true)
            {
                if (activeSession != null && activeSession.Session != null)
                {
                    mainUrlBase = activeSession.Session.Player.DirectUrlPath;
                    retryUrlBase = _serverUrl; // Fallback to the main URL if sending directly fails
                }
                else
                {
                    // If no active session, we can't send directly
                    LogWarning("No active session found to send command directly.");
                    mainUrlBase = _serverUrl;
                    retryUrlBase = null;
                }
            }
            else
            {
                mainUrlBase = _serverUrl;

                if (activeSession != null && activeSession.Session != null)
                {
                    retryUrlBase = activeSession.Session.Player.DirectUrlPath;
                }
                else
                {
                    // If no active session, we can't send directly
                    retryUrlBase = null;
                }
            }



            // Build query parameters
            Dictionary<string, object> parameters = [];

            // Add additional parameters if provided
            if (additionalParams != null)
            {
                foreach (KeyValuePair<string, string> param in additionalParams)
                {
                    parameters[param.Key] = param.Value;
                }
            }

            // Add command ID
            //parameters["commandID"] = GetNextCommandId();

            // Build the query string using JoinArgs
            string queryString = Utils.JoinArgs(parameters);

            // Create the final command URL
            command = command.Trim('/');
            string mainUrl = $"{mainUrlBase}/player/{command}{queryString}";

            try
            {
                // Send the request
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, mainUrl);
                request = Utils.AddHttpRequestHeaders(request, headers);

                // Send the command and get the response
                HttpResponseMessage response = SendRequestNoCollision(request);

                // The rest of the method remains unchanged
                // Check if the response was successful
                if (!response.IsSuccessStatusCode)
                {
                    string statusCode = ((int)response.StatusCode).ToString();
                    string statusName = response.StatusCode.ToString();
                    string errorText = await response.Content.ReadAsStringAsync();
                    errorText = errorText.Replace("\n", " ");
                    string message = $"({statusCode}) {statusName}; URL: {mainUrl} \nError: {errorText}";

                    CommandResult originalResult = new CommandResult(success: false, responseErrorMessage: message, responseXml: null);
                    LogDebug($"Command failed to {mainUrl}. Status Code: {statusCode} ({statusName}), Error: {errorText}");

                    // Retry by sending the command directly to the device (or the server URL if the function call was to send to device directly)
                    if (retryUrlBase != null)
                    {
                        string retryUrl = $"{retryUrlBase}/player/{command}{queryString}";
                        HttpRequestMessage retryRequest = new HttpRequestMessage(HttpMethod.Get, retryUrl);
                        retryRequest = Utils.AddHttpRequestHeaders(retryRequest, headers);

                        HttpResponseMessage retryResponse = SendRequestNoCollision(retryRequest);

                        if (retryResponse.IsSuccessStatusCode)
                        {
                            // Process the XML response from the device
                            string responseData = await retryResponse.Content.ReadAsStringAsync();
                            LogDebug($"Command fallback successful to {retryUrl} for command {command}. Response: {responseData}");
                            return new CommandResult(success: true, responseErrorMessage: "", responseXml: ProcessXMLResponse(responseData));
                        }
                        else
                        {
                            // Log the retry failure
                            string retryErrorText = await retryResponse.Content.ReadAsStringAsync();
                            retryErrorText = retryErrorText.Replace("\n", " ");
                            LogWarning($"Retry failed. Error: {retryErrorText}");
                            return originalResult;
                        }
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        //throw new UnauthorizedAccessException(message);
                        LogError("Unauthorized Error. Error Message: " + message);
                        return new CommandResult(success: false, responseErrorMessage: message, responseXml: null);
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        //throw new FileNotFoundException(message);
                        LogError("Not Found Error. Error Message: " + message);
                        return new CommandResult(success: false, responseErrorMessage: message, responseXml: null);
                    }
                    else
                    {
                        //throw new InvalidOperationException(message);
                        LogError("Invalid Operation Error. Error Message: " + message);
                        return new CommandResult(success: false, responseErrorMessage: message, responseXml: null);
                    }
                }
                else
                {
                    LogDebugExtra($"Command sent successfully to {mainUrl}. Response: {response.StatusCode}");
                }

                // If the calling function needs the response XML, get and return that too
                if (needResponse)
                {
                    // Process the XML response
                    string responseData = await response.Content.ReadAsStringAsync();
                    return new CommandResult(success: true, responseErrorMessage: "", responseXml: ProcessXMLResponse(responseData));
                }
                else
                {
                    return new CommandResult(success: true, responseErrorMessage: "", responseXml: null);
                }

            }
            catch (Exception ex)
            {
                // Log the error and rethrow
                LogError($"Error sending command: {ex.Message}");
                return new CommandResult(success: false, responseErrorMessage: "[Exception while sending request, no response error to get]", responseXml: null);
            }
        }

        private static XmlDocument? ProcessXMLResponse(string responseData)
        {
            // Clean XML string and handle potential parsing issues
            responseData = CleanXmlString(responseData);

            // If there's no data, return null
            if (string.IsNullOrWhiteSpace(responseData))
            {
                return null;
            }

            try
            {
                // Parse the XML
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(responseData);
                return xmlDoc;
            }
            catch (XmlException)
            {
                LogError("Invalid response from server. Command may have been successful anyway.");
                return null;
            }
        }

        // Helper methods that would be needed
        private static string CleanXmlString(string xml)
        {
            // Implement XML cleaning similar to Python's utils.cleanXMLString
            // This is a simple implementation - you might need to enhance it
            if (string.IsNullOrEmpty(xml))
                return string.Empty;

            // Remove invalid XML characters
            string cleanedXml = new string(xml.Where(c =>
                (c >= 0x0020 && c <= 0xD7FF) ||
                (c >= 0xE000 && c <= 0xFFFD) ||
                c == 0x0009 || c == 0x000A || c == 0x000D).ToArray());

            return cleanedXml;
        }

        // Command ID tracking
        private static int _commandId = 0;
        // Replace the lock statement with a SemaphoreSlim to allow asynchronous operations
        private static readonly SemaphoreSlim _httpClientSemaphore = new SemaphoreSlim(1, 1);
        private static string GetNextCommandId()
        {
            return (++_commandId).ToString();
        }

    } // ------------ End PlexServer Class ------------


} // ------------ End Namespace ------------