# MiruPlay Windows Audio DSP Design

## Goal

Add the Android audio DSP feature set to the Windows client for playback
started by MiruPlay's own `mpv.exe` process. The feature must include PEQ,
minimum-phase and linear-phase modes, output routing, limiter/headroom
controls, versioned persistence, settings editing, and Web API parity.

The Windows system-player fallback remains unchanged and does not receive DSP.

## Scope

The persisted configuration mirrors the Android contract:

- `enabled` and `selectedPresetId`.
- PEQ presets with stable ids and names.
- Preamp, phase mode, FIR quality, output mode, channel rules, and limiter.
- Band types: peaking, low/high shelf, low/high pass, notch, and band pass.
- Channel targets: all, front, center/LFE, surround groups, and concrete
  channels where the input layout is known.
- Output modes: automatic preserve, standard stereo downmix, and HRTF
  binaural.

DSP is disabled by default and a neutral preset is always available. This
release does not add stereo-to-surround upmixing, room correction, loudness
normalization, microphone capture, or a second Windows PCM output stack.

## Architecture

### Configuration model

`src/MiruPlay.Windows/Models/AudioDspModels.cs` owns the versioned C# records
and enums. Its field names and storage values match the Android model. The
model provides normalization and validation for finite numeric values,
frequency/gain/Q limits, duplicate ids, missing selected presets, and band
limits.

`AppSettings` gains an `AudioDsp` property. Existing settings files deserialize
with the neutral default. `AppSettingsStore.Load` normalizes malformed DSP
data to the neutral configuration and records a recoverable warning for the
runtime status; it never rejects an otherwise usable library configuration.

The full settings object remains protected by the existing one-megabyte
settings bound and atomic save path. Secrets are not part of the DSP model.

### Filter graph compiler

`src/MiruPlay.Windows/Services/AudioDspFilterGraphCompiler.cs` is a pure
mpv-facing compiler. It resolves the selected preset, validates it, and
returns a structured graph containing:

- the `--af` value;
- forced PCM output arguments;
- the effective output route;
- sample-rate/layout warnings;
- preview response data.

Minimum-phase PEQ uses the FFmpeg/mpv biquad and shelf/pass/notch filters,
with preamp, per-rule output gain, and the linked limiter applied in a stable
order. Linear-phase mode samples the same validated PEQ magnitude response
used by the Android design and emits `firequalizer` entries. FIR quality maps
to the configured response resolution and group delay. The compiler preserves
one delay across active channels, including flat channels.

Channel-specific rules are represented with a generated FFmpeg channel split,
per-channel filter chains, and channel merge. Standard downmix emits the
explicit ITU matrix used by Android. HRTF emits the FFmpeg headphone route.
Unknown layouts only receive rules that are provably safe for all channels;
otherwise the compiler returns a warning and refuses to claim that DSP is
active.

When DSP is enabled, the launch graph also requests float PCM, disables
exclusive output, and disables encoded passthrough/offload behavior. When DSP
is disabled, no DSP arguments are added and the existing mpv defaults remain.

### mpv integration

`MpvPlayerLauncher.CreateStartInfo` calls the compiler and appends its
arguments only for MiruPlay-owned mpv playback. Headless playback uses the
same graph so behavior is consistent in tests and diagnostics. The system
player fallback path is not passed through the compiler.

`MpvPlaybackSession` gains an audio DSP update operation that sends the
compiled `af` value through the existing JSON IPC lock. The update is applied
at an mpv audio-filter boundary; it does not restart the video process. The
session publishes the effective route and warning/error state.

If mpv rejects a startup or runtime graph, the previous working graph remains
active. A first-playback failure is surfaced as a playback error instead of
silently playing unprocessed audio while reporting DSP as enabled.

### Settings surfaces

The native WPF settings page adds an Audio DSP summary with an enable toggle,
preset selector, phase/output summary, and an editor dialog. The dialog uses
standard WPF controls and edits the complete preset contract: preamp, phase,
FIR quality, output mode, limiter, channel rules, and PEQ rows. Applying a
valid edit first compiles and applies it to the active MiruPlay mpv session,
then persists it. An invalid edit leaves both the active session and stored
settings unchanged.

The Web API adds:

- `GET /api/audio-dsp`: config, capabilities, effective route, and warnings.
- `PUT /api/audio-dsp`: validate, apply atomically, and persist the config.
- `POST /api/audio-dsp/preview`: return sampled magnitude and phase data for
  one unsaved preset.

The existing playback-settings endpoint keeps its nullable backward-compatible
shape and may expose only the DSP enabled/preset projection. Full DSP editing
uses the dedicated endpoint. The existing WebUI gets a dedicated DSP editor
view with preset CRUD, PEQ row editing, channel-rule selection, limiter and
phase controls, response preview, and apply status.

## Data flow

```text
stored AudioDspConfig
  -> normalize and validate
  -> resolve selected preset
  -> compile mpv filter graph and preview response
  -> mpv startup arguments or JSON IPC af update
  -> mpv decoded PCM -> FFmpeg filters -> Windows audio output
```

When DSP is enabled, encoded passthrough is not allowed. When it is disabled,
the existing playback behavior is restored on the next launch or filter
update. The system-player fallback never enters this flow.

## Error handling and compatibility

- Missing or old DSP fields load as disabled neutral configuration.
- Invalid API payloads return structured HTTP 400 field errors and do not
  change stored settings or the active graph.
- Unsupported HRTF or channel layout initialization keeps the previous graph
  and reports the fallback/error reason.
- The default path remains bitstream/passthrough-compatible because DSP is
  off by default.
- Filter strings are generated from validated numeric values only; user text
  is limited to preset names and never interpolated into an executable
  command without escaping.
- DSP state is scoped to the current MiruPlay-owned mpv session and is not
  applied to external applications or Windows' default media player.

## Verification

Add focused tests before implementation for:

1. model normalization, malformed recovery, enum/storage compatibility, and
   validation boundaries;
2. biquad response sampling, linear-phase response generation, FIR quality,
   channel target routing, downmix output count, and limiter headroom;
3. mpv argument generation for disabled/enabled DSP, minimum/linear phase,
   multichannel routing, and no changes to the system-player fallback;
4. runtime IPC update behavior and preservation of the previous graph on an
   mpv rejection;
5. Web API GET/PUT/preview round trips and invalid-payload atomicity;
6. WPF settings apply behavior, including AutomationProperties and focus-safe
   editor controls.

Run the focused tests, the full test suite, and a Release build. Where a
packaged mpv executable is available, validate startup with a deterministic
48 kHz sweep and inspect mpv's effective `af` property. Record whether the
session used MiruPlay mpv or the system-player fallback.
