# Linux Demo for Ai.Tlbx.RealTimeAudio

This console application demonstrates how to use the Ai.Tlbx.RealTimeAudio library with Linux systems, including Raspberry Pi.

## Requirements

- .NET 10.0 runtime
- Linux operating system with ALSA support
- ALSA development libraries (`libasound2` package on Debian/Ubuntu-based distributions)
- OpenAI API key

## Getting Started

1. Install the ALSA libraries:

```bash
# Debian/Ubuntu/Raspberry Pi OS
sudo apt install libasound2

# Fedora
sudo dnf install alsa-lib

# Arch Linux
sudo pacman -S alsa-lib
```

2. Configure your OpenAI API key:

Edit the `appsettings.json` file and add your OpenAI API key:

```json
{
  "OpenAI": {
    "ApiKey": "your-api-key-here",
    ...
  }
}
```

Alternatively, you can set the `OPENAI_API_KEY` environment variable:

```bash
export OPENAI_API_KEY=your-api-key-here
```

3. Run the application:

```bash
dotnet run
```

## Usage

Once the application is running, use the following commands:

- `s` - Start a conversation
- `p` - Pause/Resume the conversation
- `q` - Quit the application

## Troubleshooting

If you encounter audio device access issues:

1. Make sure your user is part of the `audio` group:
   ```bash
   sudo usermod -a -G audio $USER
   ```
   (Log out and back in for changes to take effect)

2. Check if your audio devices are recognized:
   ```bash
   arecord -l  # List recording devices
   aplay -l    # List playback devices
   ```

3. Test microphone recording:
   ```bash
   arecord -f cd -d 5 test.wav  # Record 5 seconds
   aplay test.wav               # Play it back
   ``` 