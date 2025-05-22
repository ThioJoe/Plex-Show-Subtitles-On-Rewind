# Thio's Rewind Subtitle Displayer For Plex

## ⚠️ WARNING: Plex recently released major new updates for the various Plex player apps which COMPLETELY breaks this tool, and apparently results in broken playback / pauses / media restarts when certain API requests are made to devices. Don't try to use this app with the new app versions. I'll try to see if I can fix it, but it seems like they might have removed support for the "Remote API" this app heavily relied on. ⚠️

## What it Does

If you rewind a show or movie by a short amount (like pressing the back button on a remote), this tool will automatically tell Plex to **turn on subtitles** temporarily on that device. 

Once playback reaches the point where you started the rewind, it will **automatically turn the subtitles back off** (unless you had them on manually already).

## Why?

99% of the time you use the rewind button, it's probably because you didn't catch the dialog

# Additional Features & Modes

* **Play/Pause button Double & Triple Click hotkey actions**: Manually toggle subtitles, or toggle rewind monitoring overall
   - Note: Because of how the Apple TV's Plex app works, these hotkeys can only be triggered if the media is paused first. This is not an issue on the mobile apps.
* **Manually Only Mode**: The app will not automatically enable subtitles on rewind. Instead you can just use the double or triple click hotkeys
* **Subtitles Always On Mode**: Automatically enable subtitles whenever you start watching anything.
* **Remember Subtitles Per TV Show Mode**: It will remember when you manually enable subtitles while watching a particular show, and automatically enable subtitles whenever you start watching any other episodes of that show
* **Always Show Subtitles For Specific Libraries**: In the settings enter a list of Library names, and any media within those libraries will have subtitles enabled upon playing.

# Key Rewind-Detection Features

* **Respects Manual Settings**: If you manually turned subtitles on *before* rewinding, they will stay on.
* **Temporary Subtitle Display**: Enables the first available subtitle track *only* for the duration you rewound. Then seamlessly disables them again.
* **Smart-Cancellation**: Automatic disabling of temporary subtitles again on certain conditions:
  * If you rewind longer than a specified threshold (60 seconds default)
  * If you fast forward at any point
* **Use Preferred Subtitles**: In the `settings.ini` file, you can configure how it will choose which subtitle track to display.

## Frequently Asked Questions:
### **Q:** Do I need to install this on every device where I'm watching?

**A:** No, you only need to have it run on one computer/server/NAS on your local network. It will centrally manage the connection to your Plex server and any playing devices. It doesn't even necessarily need to be on the same computer as your Plex server.

### **Q:** Is there docker support?

**A:** Yes but at the moment you'll need to set it up with some manual steps. There are instructions [here](https://github.com/ThioJoe/Plex-Show-Subtitles-On-Rewind/blob/master/Docker/Docker%20Instructions.md) and also see [this wiki article](https://github.com/ThioJoe/Plex-Show-Subtitles-On-Rewind/wiki/Running-it-On-a-NAS-in-Docker) for specific instructions for setup in Docker on a NAS

### **Q: What platforms are supported?**

**A:** The app is cross-platform with dedicated versions for **Windows**, **MacOS** (both Intel and Apple Silicon), and **Linux** (both x86 and arm64)

# Screenshot
![Console Window Screenshot](https://github.com/user-attachments/assets/c7dc6a08-2f92-4406-85a0-8b34ccd36054)


## Requirements

* You **MUST** enable the "Remote Control" / aka "Advertise As Player" option in each player app for this to work.
    * For example in the iOS app this can be found under Settings > Remote Control > Advertise As Player
    * Note: The Plex Media Server desktop app strangely does not allow this feature. However the "[HTPC](https://support.plex.tv/articles/htpc-getting-started/)" version of the desktop Plex player does.
* This may only work if your Plex server is on your local network

## How to Download

1. Look for the "Releases" link on the right side of the page, or [click here](https://github.com/ThioJoe/Plex-Show-Subtitles-On-Rewind/releases).
2. Under the latest release, look in the `Assets` dropdown (expand if necessary) to see the available versions of the app
   - Refer to the "Which file below do I need?" info in the release notes above the assets

## Setup

1.  **Connect to Plex**:
    * Run the application. It will guide you through a one-time authorization process in your web browser to link with your Plex account. This is required to connect to your server via the API.
2.  **Configure Server Address and Other Settings**:
    * Edit the `settings.ini` file. ( See: [Settings Info Wiki Article](https://github.com/ThioJoe/Plex-Show-Subtitles-On-Rewind/wiki/Settings.ini-File) )
    * Set `Server_URL_And_Port` to your Plex Media Server's local IP address and port (e.g., `http://192.168.1.100:32400`).

## Usage

1.  Run the executable (e.g., `RewindSubtitleDisplayerForPlex.exe` on Windows)
    - If running for the first time: Configure your settings, including server address by editing the automatically created `settings.ini` file, then running the app again.
    - Also if prompted, go through the authorization flow to allow the app to communicate with your server.
3.  Leave it running in the background. It will automatically monitor sessions and go idle to minimize resources when there aren't any active playback sessions.

## Other Benefits

* **API Integration**: This uses the Plex API directly, the enabling/disabling of subtitles is seamless.
* **Monitors All Local Players**: Automatically detects playback sessions from any Plex client on your network connected to your server.
* **Cross-Platform**: It's a simple console app that can be run on Windows, MacOS, and Linux.
* **Run Purely in Background**: On Windows use the `-background` launch argument to run it without any console Window. It will sit idle until Plex notifies it that Media is playing.
 
### Optional Command-Line Arguments: 

- Use the `-help` launch argument to list all other available launch arguments, including:
     - `-background`: Windows Only: The program runs in the background without showing a console. Alternatively, background mode can be enabled in the settings.ini file.
     - `-stop`: Stops all other running instances of the app. Useful if you launched another instance in background mode, so you don't have to end it via task manager.
     - `-settings-template`: Generate a default settings config file template.
     - `-no-force-debug`: For development, doesn't force the program into DebugExtra mode when debugging.
     - `-token-template`: Generate an example token config file.
     - `-allow-duplicate-instance`: New app instance will not close if it detects another is already connected to the same server.
     - `-update-settings-file`: Update your old settings file version, to include missing settings (if any) and update any settings descriptions. A backup will be created.
     - `-test-settings`: Load your settings file and show which values are valid, and which are not and therefore will be set to default.
     - `-no-instance-check`: `This instance will not respond to -stop or other checks by other instances.`
     - `-auth-device-name`: Specify a device name when authorizing in non-interactive mode. Mainly for use when containerized. Include the name after this argument.
   
