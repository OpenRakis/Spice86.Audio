# Introduction

A full cross-platform audio library for desktop OSes (Windows, Mac, Linux), entirely in C#. Requires .NET 10.

[![NuGet](https://img.shields.io/nuget/v/Spice86.Audio)](https://www.nuget.org/packages/Spice86.Audio)
[![CI](https://github.com/OpenRakis/Spice86.Audio/actions/workflows/pr.yml/badge.svg)](https://github.com/OpenRakis/Spice86.Audio/actions/workflows/pr.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com/)

---

## Overview

`Spice86.Audio` is the audio subsystem extracted from [Spice86](https://github.com/OpenRakis/Spice86), a DOS program emulator. It provides everything needed to render and output real-time PCM audio on Windows, Linux, and macOS, without any native binary dependencies.

The library is a faithful C# port of the audio pipeline used by [DOSBox Staging](https://github.com/dosbox-staging/dosbox-staging) and [SDL2](https://www.libsdl.org/), adapted for modern .NET. It is used by `Spice86.Core` to support emulated audio devices — Sound Blaster, OPL2, OPL3, Adlib Gold, PC Speaker, and more.

**Key properties:**

- 100% managed C# — no bundled native DLLs
- Callback-driven audio output via WASAPI (Windows), ALSA (Linux), CoreAudio (macOS)
- Thread-safe producer/consumer audio queue (`RWQueue<T>`)
- Rich filter chain: compressor, reverb (MVerb), chorus (TAL), noise gate, crossfeed, IIR filters, Speex resampler
- Graceful no-op fallback when no audio device is available

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Your application                        │
│  (e.g. SoftwareMixer in Spice86.Core)                       │
├─────────────────────────────────────────────────────────────┤
│  AudioPlayerFactory  ──creates──►  AudioPlayer              │
│    (AudioEngine.CrossPlatform          │                    │
│     or AudioEngine.Dummy)              │ WriteData(Span<float>)
├────────────────────────────────────────┼────────────────────┤
│  CrossPlatformAudioPlayer              │                    │
│    RWQueue<float>  ◄── producer ───────┘                    │
│         │                                                   │
│         └── consumer (audio callback) ──► IAudioBackend     │
├─────────────────────────────────────────────────────────────┤
│  IAudioBackend (platform-specific C# ports of SDL2)         │
│    Windows  → WASAPI   (SdlWindowsBackend)                  │
│    Linux    → ALSA     (SdlLinuxBackend)                    │
│    macOS    → CoreAudio (SdlMacBackend)                     │
└─────────────────────────────────────────────────────────────┘
```

Audio data flows as `AudioFrame` (stereo IEEE float32 pairs) through an optional filter chain before being written to the player queue as a flat `Span<float>`.

---

## Installation

```
dotnet add package Spice86.Audio
```

Targets `net10.0`. No native runtime packages required.

---

## API Reference

### Core primitives

| Type | Namespace | Description |
|---|---|---|
| `AudioFrame` | `Spice86.Audio.Common` | Stereo sample pair `(float Left, float Right)`. Supports `+`, `*`, indexer. |
| `AudioFrameBuffer` | `Spice86.Audio.Filters` | Resizable array of `AudioFrame` values with `Add`, `AddRange`, `AsSpan`, `RemoveRange`. |
| `SampleFormat` | `Spice86.Audio.Backend.Audio` | `UnsignedPcm8`, `SignedPcm16`, `IeeeFloat32`. |
| `AudioFormat` | `Spice86.Audio.Backend.Audio` | Record: `SampleRate`, `Channels`, `SampleFormat`. |

### Audio player

| Type | Description |
|---|---|
| `AudioEngine` | Enum: `CrossPlatform` (default) or `Dummy` (silent). |
| `AudioPlayerFactory` | Creates an `AudioPlayer` for the chosen engine. |
| `AudioPlayer` | Abstract base: `WriteData(Span<float>)`, `Start()`, `ClearQueuedData()`, `MuteOutput()`, `UnmuteOutput()`. |
| `CrossPlatformAudioPlayer` | Callback-based player backed by an `IAudioBackend`. Uses `RWQueue<float>` for lock-friendly producer/consumer handoff. |
| `DummyAudioPlayer` | No-op player for headless / test scenarios. |

#### `AudioPlayerFactory.CreatePlayer`

```csharp
AudioPlayer CreatePlayer(
    int sampleRate,       // Hz — e.g. 48000
    int framesPerBuffer,  // device buffer size; 0 → default (1024)
    int prebufferMs,      // extra latency cushion in ms (e.g. 25)
    bool allowNegotiate)  // let the OS driver adjust buffer size
```

Falls back to `DummyAudioPlayer` if the native backend cannot be opened.

### Thread-safe queue

`RWQueue<T>` (namespace `Spice86.Audio.Backend`) is a fixed-capacity circular buffer:

| Member | Description |
|---|---|
| `BulkEnqueue(Span<T>)` | **Blocking** — waits until space is available. |
| `NonblockingBulkEnqueue(Span<T>)` | Returns immediately; may enqueue fewer items. |
| `BulkDequeue(Span<T>, int)` | Drains up to the requested count; fills remaining with silence on underrun. |
| `Clear()` | Empties the queue and unblocks waiting threads. |
| `Stop()` | Signals shutdown; unblocks all waiters. |
| `Size`, `MaxCapacity`, `IsFull`, `IsEmpty` | Introspection. |

### Filters & effects

All filter types live in `Spice86.Audio.Filters`.

#### Compressor

RMS-based feed-forward compressor (port of Thomas Scott Stillwell's "Master Tom Compressor"):

```csharp
var compressor = new Compressor();
compressor.Configure(
    sampleRateHz: 48000,
    zeroDbfsSampleValue: (float)short.MaxValue,
    thresholdDb: -6.0f,
    ratio: 3.0f,
    attackTimeMs: 5.0f,
    releaseTimeMs: 50.0f,
    rmsWindowMs: 5.0f);

AudioFrame output = compressor.Process(inputFrame);
```

#### Noise gate

Mutes the signal below a configurable threshold with configurable attack/release:

```csharp
var gate = new NoiseGate();
gate.Configure(
    sampleRateHz: 48000,
    db0fsSampleValue: (float)short.MaxValue,
    thresholdDb: -60.0f,
    attackTimeMs: 1.0f,
    releaseTimeMs: 20.0f);

AudioFrame output = gate.Process(inputFrame);
```

#### Chorus (TAL-NoiseMaker / DOSBox Staging port)

Dual chorus engine — left and right lines processed independently for a wide stereo effect:

```csharp
var chorus = new ChorusEngine(sampleRate: 48000f);
chorus.SetEnablesChorus(isChorus1Enabled: true, isChorus2Enabled: false);

float left = ..., right = ...;
chorus.Process(ref left, ref right);
```

**Presets:** `ChorusPreset.None`, `Light`, `Normal`, `Strong`.

#### Reverb (MVerb)

Professional algorithmic reverb, modelled after DOSBox Staging's MVerb integration:

```csharp
var reverb = new MVerb();
reverb.SetSampleRate(48000);
// configure wet/dry mix, room size, etc. via MVerb properties
```

**Presets:** `ReverbPreset.None`, `Tiny`, `Small`, `Medium`, `Large`, `Huge`.

#### Crossfeed

Blends a fraction of the left channel into the right and vice-versa for natural headphone imaging:

**Presets:** `CrossfeedPreset.None`, `Light` (20%), `Normal` (40%), `Strong` (60%).

#### IIR filters (Butterworth, Chebyshev I/II, RBJ)

Port of [iir1](https://github.com/berndporr/iir1) by Vinnie Falco and Bernd Porr:

```csharp
using Spice86.Audio.Filters.IirFilters.Filters.Butterworth;

var hp = new HighPass();
hp.Setup(order: 2, sampleRate: 48000f, cutoffHz: 200f);
float filtered = hp.Filter(sample);

var lp = new LowPass();
lp.Setup(order: 2, sampleRate: 48000f, cutoffHz: 4000f);
```

Available families: `Butterworth`, `ChebyshevI`, `ChebyshevII`, `RBJ`. Each provides `LowPass`, `HighPass`, and more.

#### Speex resampler

High-quality resampler ported from libspeex (used internally by `SoundChannel`):

```csharp
using Spice86.Audio.Filters.Speex;

var resampler = new SpeexResamplerCSharp(
    channels: 2,
    inRate: 22050,
    outRate: 48000,
    quality: 5);
```

**Resample methods:** `ResampleMethod.Resample` (Speex), `LerpUpsampleOrResample`, `ZeroOrderHoldAndResample` (vintage DAC).

### Mixer state

`MixerState` controls the audio output state at mixer level:

| Value | Description |
|---|---|
| `NoSound` | Audio device not initialized or disabled. |
| `On` | Audio actively playing and mixing. |
| `Muted` | Device active but producing silence. |

---

## Usage — Spice86 integration example

The following is how `Spice86.Core`'s `SoftwareMixer` uses this library:

```csharp
// 1. Create the player
var factory = new AudioPlayerFactory(AudioEngine.CrossPlatform);
AudioPlayer player = factory.CreatePlayer(
    sampleRate: 48000,
    framesPerBuffer: 1024,
    prebufferMs: 25,
    allowNegotiate: false);

// 2. Start playback
player.Start();

// 3. Push audio (from mixer thread, blocks until queue has space)
Span<float> pcmData = /* interleaved L/R float32 samples */;
player.WriteData(pcmData);

// 4. Mute/unmute on emulator pause
player.MuteOutput();
player.UnmuteOutput();

// 5. Flush and dispose
player.ClearQueuedData();
player.Dispose();
```

A `SoundChannel` in Spice86 maps a single emulated audio device to the mixer. It handles resampling (Speex), per-channel filters (IIR high/low-pass, noise gate, reverb send, chorus send), stereo mapping, and volume envelope. The `SoftwareMixer` then runs a dedicated background thread that drains per-channel `AudioFrameBuffer`s, applies master-level effects (compressor, reverb, chorus, crossfeed), and calls `player.WriteData`.

---

## Platform support

| Platform | Backend | Native API |
|---|---|---|
| Windows | `SdlWindowsBackend` | WASAPI (via COM interop) |
| Linux | `SdlLinuxBackend` | ALSA (via `libasound`) |
| macOS | `SdlMacBackend` | CoreAudio AudioQueue |
| Any | `DummyAudioPlayer` | No output — automatic fallback |

All native interop is implemented directly in C# using `LibraryImport` / P/Invoke. No bundled `.so`, `.dll`, or `.dylib` files are included. System ALSA and system CoreAudio frameworks are used on Linux and macOS respectively; WASAPI is available on all modern Windows versions.

---

## Building and testing

```bash
# Build
dotnet build src/Spice86.Audio.slnx --configuration Release

# Run tests
dotnet test tests/Spice86.Audio.Tests
```

CI runs on all three platforms on every pull request and on every merge to `main`.

---

## License and Credits

The `Spice86.Audio` library itself is licensed under the [Apache License 2.0](LICENSE).

It incorporates C# ports of the following third-party components:

### SDL (Simple DirectMedia Layer)
- **License:** [zlib License](LICENSE.SDL)
- The cross-platform audio backend (WASAPI/ALSA/CoreAudio) is a faithful port of SDL2's audio subsystem. SDL's callback model is preserved exactly: the audio hardware drives timing, and the application fills buffers on demand.
- Full thanks to the SDL team for their outstanding cross-platform multimedia library. This C# port would not exist otherwise.

### DOSBox Staging
- **License:** [GNU GPL v2.0](LICENSE.DOSBOXSTAGING)
- The mixer architecture, `RWQueue<T>`, `AudioFrame`, chorus engine (TAL-NoiseMaker integration), noise gate, MVerb reverb, compressor, and resampling pipeline are all faithful ports of DOSBox Staging's audio subsystem (`src/audio/`, `src/utils/rwqueue.*`).
- Full thanks to the DOSBox Staging team and the original DOSBox team. This C# port would not exist otherwise.

### IIR Filters (iir1)
- **License:** MIT / GPL v3 (see [`src/Spice86.Audio/Filters/IirFilters/LICENSE`](src/Spice86.Audio/Filters/IirFilters/LICENSE))
- Port of [iir1](https://github.com/berndporr/iir1) by Vinnie Falco (original DSPFilters) and Bernd Porr (iir1 C++ library).

### Speex Resampler (libspeex)
- **License:** BSD-style (see libspeex source headers)
- Port of the Speex resampler by Jean-Marc Valin and the Speex project.

### TAL-NoiseMaker (Chorus & DCBlock)
- **License:** [GNU GPL v2.0](https://www.gnu.org/licenses/gpl-2.0.html)
- `ChorusEngine`, `Chorus`, `DCBlock`, and `Lfo` are ported from TAL-NoiseMaker by Patrick Kunz (Togu Audio Line, Inc.).

### Master Tom Compressor
- **License:** BSD-style (see `Compressor.cs` header)
- `Compressor` is a port of Thomas Scott Stillwell's "Master Tom Compressor" JSFX effect.

### Additional contributors
Work from Patrick Kunz, Martin Eastwood, and John Novak has also been ported. See individual source file headers for details.

Please see the respective LICENSE files at the root of this repository for the full license texts.
