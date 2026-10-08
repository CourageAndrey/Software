# Media Player

A compact WPF audio/video player inspired by Winamp and MPC-HC, using LibVLCSharp and packaged LibVLC. VLC does not need to be separately installed.

## Run

Requires the .NET 10 SDK and Windows. Keep the complete build/publish directory, including the architecture-specific `libvlc` files; copying only the executable will not work.

```powershell
dotnet run --project Software.MediaPlayer
dotnet run --project Software.MediaPlayer -- 'C:\Music\track.flac' 'C:\Video\movie.mkv'
```

Open or drop audio/video files, add files from one folder, or open an HTTP/HTTPS/RTSP stream. The playlist supports double-click/Enter to play, remove/clear, sequential next/previous, shuffle, and repeat off/all/one. M3U/M3U8 playlists can be loaded and saved; loading appends entries and resolves relative paths against the playlist's directory.

Playback includes play/pause, stop, seek, volume/mute, playback speed, fullscreen, and elapsed/total time. Seeking is disabled for non-seekable/live sources. Audio displays the track title and artist when available; video uses the native VLC renderer with a WPF input layer. Previous restarts the current track after its first three seconds, otherwise selects the preceding item.

Keyboard controls: Ctrl+O opens files, Space plays/pauses, F/F11 toggles fullscreen, Escape exits fullscreen, M toggles mute, and left/right arrows seek five seconds. Double-click the media surface to toggle fullscreen.

## Limits

Supported formats/codecs depend on packaged LibVLC. This is not a full Winamp/VLC replacement: no equalizer, media library database, subtitle selector, disc-menu interface, visualization DSP, or metadata editing. Playlist limit: 2,000 items; playlist files: 2 MiB. Missing playlist files cause a diagnostic rather than being silently skipped. Streams require network access and may not support seeking/speed changes. Playlists and their URLs are plain text; avoid saving URLs with credentials/tokens.

## Tests

```powershell
dotnet test Software.UnitTests --filter 'FullyQualifiedName~MediaPlaylistTests|FullyQualifiedName~MediaEngineTests|FullyQualifiedName~MediaPlayerUiTests' -p:BuildInParallel=false
```

Tests use generated WAV files and LibVLC dummy output to verify real play/pause states without speaker output, along with playlist rules and WPF controls/fullscreen. Physical audio volume, network streams, and video rendering across hardware codecs require manual verification.