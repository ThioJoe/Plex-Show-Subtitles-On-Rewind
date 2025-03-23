﻿using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace PlexShowSubtitlesOnRewind;
public class PlexMediaItem
{
    public string Key { get; set; }
    public string Title { get; set; }
    public string Type { get; set; }
    public List<Media> Media { get; set; } = [];

    public List<SubtitleStream> GetSubtitleStreams()
    {
        List<SubtitleStream> subtitles = [];

        foreach (Media media in Media)
        {
            foreach (MediaPart part in media.Parts)
            {
                subtitles.AddRange(part.Subtitles);
            }
        }

        return subtitles;
    }
}

public class PlexSession
{
    public string Key { get; set; }
    public string SessionId { get; set; }
    public string RatingKey { get; set; }
    public string SessionKey { get; set; }
    public string Title { get; set; }
    public string GrandparentTitle { get; set; }
    public string Type { get; set; } // movie, episode, etc.
    public int ViewOffset { get; set; } // in milliseconds
    public PlexPlayer Player { get; set; }
    public List<Media> Media { get; set; } = [];
    private PlexMediaItem _cachedItem;

    public PlexSession()
    {
        Player = new PlexPlayer();
        Media = new List<Media>();
    }

    public void Reload()
    {
        Console.WriteLine("PLEXSESSION RELOAD NOT YET IMPLEMENTED....");
    }

    public async Task<PlexMediaItem> FetchItemAsync(string key, PlexServer server)
    {
        if (_cachedItem == null)
        {
            _cachedItem = await server.FetchItemAsync(key);
        }
        return _cachedItem;
    }

}

public class PlexPlayer
{
    public string Title { get; set; }
    public string MachineIdentifier { get; set; }
}

public class PlexClient
{
    public string DeviceName { get; set; }
    public string MachineIdentifier { get; set; }
    public string ClientAppName { get; set; }
    public string DeviceClass { get; set; }
    public string Platform { get; set; }
    public HttpClient HttpClient { get; set; }
    public string BaseUrl { get; set; }

    public async Task SetSubtitleStreamAsync(int streamId, string mediaType = "video")
    {
        try
        {
            // Send command to the Plex client
            string command = $"{BaseUrl}/player/playback/setSubtitleStream?id={streamId}&type={mediaType}&machineIdentifier={MachineIdentifier}";
            Console.WriteLine($"Sending command: {command}");

            HttpResponseMessage response = await HttpClient.GetAsync(command);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Successfully set subtitle stream {streamId} on client {DeviceName}");
            }
            else
            {
                Console.WriteLine($"Failed to set subtitle stream {streamId} on client {DeviceName}. Status: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error setting subtitle stream: {ex.Message}");
        }
    }
}

public class Media
{
    public string Id { get; set; }
    public int Duration { get; set; }
    public string VideoCodec { get; set; }
    public string AudioCodec { get; set; }
    public string Container { get; set; }
    public List<MediaPart> Parts { get; set; } = [];
}

public class MediaPart
{
    public string Id { get; set; }
    public string Key { get; set; }
    public int Duration { get; set; }
    public string File { get; set; }
    public List<SubtitleStream> Subtitles { get; set; } = [];
}

public class SubtitleStream
{
    public int Id { get; set; }
    public int Index { get; set; }
    public string ExtendedDisplayTitle { get; set; }
    public string Language { get; set; }
    public bool Selected { get; set; }
    public string Format { get; set; }  
    public string Title { get; set; }   
    public string Location { get; set; } 
    public bool IsExternal { get; set; } 
}

// Class to hold session objects and associated subtitles
public class ActiveSession
{
    private PlexSession _session;
    private List<SubtitleStream> _availableSubtitles;
    private List<SubtitleStream> _activeSubtitles;

    // Properly implemented public properties that use the private fields
    public PlexSession Session
    {
        get => _session;
        private set => _session = value;
    }

    public List<SubtitleStream> AvailableSubtitles
    {
        get => _availableSubtitles;
        private set => _availableSubtitles = value;
    }

    public List<SubtitleStream> ActiveSubtitles
    {
        get => _activeSubtitles;
        private set => _activeSubtitles = value;
    }
    public string DeviceName { get; }
    public string MachineID { get; }
    public string MediaTitle { get; }
    public string SessionID { get; }

    public ActiveSession(
        PlexSession session,
        List<SubtitleStream> availableSubtitles,
        List<SubtitleStream> activeSubtitles
        )
    {
        _session = session;
        _availableSubtitles = availableSubtitles;
        _activeSubtitles = activeSubtitles;

        DeviceName = session.Player.Title;
        MachineID = session.Player.MachineIdentifier;
        MediaTitle = session.GrandparentTitle ?? session.Title;
        SessionID = session.SessionId;
    }

    public double GetPlayPositionSeconds()
    {
        //Session.Reload(); // Otherwise it won't update
        int positionMilliseconds = Session.ViewOffset;
        double positionSec = Math.Round(positionMilliseconds / 1000.0, 2);
        return positionSec;
    }

    public ActiveSession Refresh(PlexSession session, List<SubtitleStream> activeSubtitles)
    {
        Session = session;
        AvailableSubtitles = _availableSubtitles; // Don't bother updating available subtitles
        ActiveSubtitles = activeSubtitles; // Don't bother updating active subtitles
        return this;
    }
}

// XML-specific versions of your model classes
[XmlRoot("Video")]
public class PlexSessionXml
{
    [XmlAttribute("key")]
    public string Key { get; set; }

    [XmlAttribute("ratingKey")]
    public string RatingKey { get; set; }

    [XmlAttribute("sessionKey")]
    public string SessionKey { get; set; }

    [XmlAttribute("title")]
    public string Title { get; set; }

    [XmlAttribute("grandparentTitle")]
    public string GrandparentTitle { get; set; }

    [XmlAttribute("type")]
    public string Type { get; set; }

    [XmlAttribute("viewOffset")]
    public int ViewOffset { get; set; }

    [XmlElement("Player")]
    public PlexPlayerXml Player { get; set; }

    [XmlElement("Media")]
    public List<MediaXml> Media { get; set; } = new List<MediaXml>();

    [XmlElement("id")]
    public string Id { get; set; }

    // Convert to your existing PlexSession class
    public PlexSession ToPlexSession()
    {
        PlexSession session = new PlexSession
        {
            Key = Key,
            RatingKey = RatingKey,
            SessionKey = SessionKey,
            Title = Title,
            GrandparentTitle = GrandparentTitle,
            Type = Type,
            ViewOffset = ViewOffset,
            SessionId = Id
        };

        if (Player != null)
        {
            session.Player.Title = Player.Title;
            session.Player.MachineIdentifier = Player.MachineIdentifier;
        }

        if (Media != null)
        {
            foreach (var mediaXml in Media)
            {
                session.Media.Add(mediaXml.ToMedia());
            }
        }

        return session;
    }
}

[XmlRoot("Player")]
public class PlexPlayerXml
{
    [XmlAttribute("title")]
    public string Title { get; set; }

    [XmlAttribute("machineIdentifier")]
    public string MachineIdentifier { get; set; }
}

[XmlRoot("Server")]
public class PlexClientXml
{
    [XmlAttribute("name")]
    public string DeviceName { get; set; }

    [XmlAttribute("machineIdentifier")]
    public string MachineIdentifier { get; set; }

    [XmlAttribute("product")]
    public string ClientAppName { get; set; }

    [XmlAttribute("deviceClass")]
    public string DeviceClass { get; set; }

    [XmlAttribute("platform")]
    public string Platform { get; set; }

    // Convert to your existing PlexClient class
    public PlexClient ToPlexClient(HttpClient httpClient, string baseUrl)
    {
        return new PlexClient
        {
            DeviceName = DeviceName,
            MachineIdentifier = MachineIdentifier,
            ClientAppName = ClientAppName,
            DeviceClass = DeviceClass,
            Platform = Platform ?? ClientAppName, // Handle the fallback
            HttpClient = httpClient,
            BaseUrl = baseUrl
        };
    }
}

[XmlRoot("Media")]
public class MediaXml
{
    [XmlAttribute("id")]
    public string Id { get; set; }

    [XmlAttribute("duration")]
    public int Duration { get; set; }

    [XmlAttribute("videoCodec")]
    public string VideoCodec { get; set; }

    [XmlAttribute("audioCodec")]
    public string AudioCodec { get; set; }

    [XmlAttribute("container")]
    public string Container { get; set; }

    [XmlElement("Part")]
    public List<MediaPartXml> Parts { get; set; } = new List<MediaPartXml>();

    // Convert to your existing Media class
    public Media ToMedia()
    {
        var media = new Media
        {
            Id = Id,
            Duration = Duration,
            VideoCodec = VideoCodec,
            AudioCodec = AudioCodec,
            Container = Container
        };

        if (Parts != null)
        {
            foreach (var partXml in Parts)
            {
                media.Parts.Add(partXml.ToMediaPart());
            }
        }

        return media;
    }
}

[XmlRoot("Part")]
public class MediaPartXml
{
    [XmlAttribute("id")]
    public string Id { get; set; }

    [XmlAttribute("key")]
    public string Key { get; set; }

    [XmlAttribute("duration")]
    public int Duration { get; set; }

    [XmlAttribute("file")]
    public string File { get; set; }

    [XmlElement("Stream")]
    public List<SubtitleStreamXml> Subtitles { get; set; } = new List<SubtitleStreamXml>();

    // Convert to your existing MediaPart class
    public MediaPart ToMediaPart()
    {
        var part = new MediaPart
        {
            Id = Id,
            Key = Key,
            Duration = Duration,
            File = File
        };

        if (Subtitles != null)
        {
            foreach (var subtitleXml in Subtitles)
            {
                if (subtitleXml.StreamType == 3) // Only add subtitle streams
                {
                    part.Subtitles.Add(subtitleXml.ToSubtitleStream());
                }
            }
        }

        return part;
    }
}

[XmlRoot("Stream")]
public class SubtitleStreamXml
{
    [XmlAttribute("id")]
    public int Id { get; set; }

    [XmlAttribute("streamType")]
    public int StreamType { get; set; }

    [XmlAttribute("index")]
    public int Index { get; set; }

    [XmlAttribute("extendedDisplayTitle")]
    public string ExtendedDisplayTitle { get; set; }

    [XmlAttribute("language")]
    public string Language { get; set; }

    [XmlAttribute("selected")]
    public string SelectedValue { get; set; }

    [XmlAttribute("format")]
    public string Format { get; set; }

    [XmlAttribute("title")]
    public string Title { get; set; }

    [XmlAttribute("location")]
    public string Location { get; set; }

    // Convert to your existing SubtitleStream class
    public SubtitleStream ToSubtitleStream()
    {
        return new SubtitleStream
        {
            Id = Id,
            Index = Index,
            ExtendedDisplayTitle = ExtendedDisplayTitle,
            Language = Language,
            Selected = SelectedValue == "1",
            Format = Format,
            Title = Title,
            Location = Location,
            IsExternal = Location == "external"
        };
    }
}

[XmlRoot("MediaContainer")]
public class MediaContainerXml
{
    [XmlElement("Video")]
    public List<PlexSessionXml> Sessions { get; set; } = new List<PlexSessionXml>();

    [XmlElement("Server")]
    public List<PlexClientXml> Clients { get; set; } = new List<PlexClientXml>();
}