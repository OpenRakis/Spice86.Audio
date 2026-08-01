namespace Spice86.Audio.Backend.Audio.CrossPlatform.Sdl.Mac;

using System;
using System.Runtime.Versioning;

using Spice86.Audio.Backend.Audio.CrossPlatform.Sdl.Mac.CoreAudio;

/// <summary>
/// SDL-style audio backend for macOS using CoreAudio AudioQueue.
/// Mirrors the playback-only path of SDL3's CoreAudio backend in
/// SDL/src/audio/coreaudio/SDL_coreaudio.m as closely as the managed
/// Spice86.Audio abstraction allows.
/// 
/// Actual usage in this repository is narrower than SDL3 itself:
/// playback only, default-device selection only, and a managed float callback
/// contract that is translated into the backend's native queue model.
/// </summary>
[SupportedOSPlatform("osx")]
public sealed class SdlMacBackend : IAudioBackend
{
    private readonly SdlAudioDevice _device;
    private AudioDeviceState _state = AudioDeviceState.Stopped;
    private string? _lastError;

    /// <summary>
    /// Initializes a new instance of the <see cref="SdlMacBackend"/> class.
    /// The backend delegates SDL-style device lifecycle management to
    /// <see cref="SdlAudioDevice"/> and CoreAudio-specific queue control to
    /// <see cref="SdlCoreAudioDriver"/>.
    /// </summary>
    public SdlMacBackend()
    {
        _device = new SdlAudioDevice(new SdlCoreAudioDriver());
    }

    /// <inheritdoc/>
    public AudioSpec ObtainedSpec => _device.ObtainedSpec;

    /// <inheritdoc/>
    public AudioDeviceState State => _state;

    /// <inheritdoc/>
    public string? LastError => _lastError;

    /// <inheritdoc/>
    public bool Open(AudioSpec desiredSpec)
    {
        ArgumentNullException.ThrowIfNull(desiredSpec);
        ArgumentNullException.ThrowIfNull(desiredSpec.Callback);

        if (!_device.Open(desiredSpec))
        {
            _lastError = _device.LastError;
            _state = AudioDeviceState.Error;
            return false;
        }

        _state = AudioDeviceState.Stopped;
        return true;
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (_state == AudioDeviceState.Playing)
        {
            return;
        }

        _device.Start();
        _state = AudioDeviceState.Playing;
    }

    /// <inheritdoc/>
    public void Pause()
    {
        if (_state != AudioDeviceState.Playing)
        {
            return;
        }

        _device.Pause();
        _state = AudioDeviceState.Stopped;
    }

    /// <inheritdoc/>
    public void Close()
    {
        _device.Close();
        _state = AudioDeviceState.Stopped;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
    }
}
