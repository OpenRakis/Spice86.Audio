namespace Spice86.Audio.Backend.Audio.CrossPlatform.Sdl.Mac.CoreAudio;

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

/// <summary>
/// SDL3-inspired CoreAudio AudioQueue driver implementing <see cref="ISdlAudioDriver"/>.
/// References:
/// - SDL/src/audio/coreaudio/SDL_coreaudio.m
/// - SDL/src/audio/coreaudio/SDL_coreaudio.h
/// 
/// This driver now follows the SDL3 playback control flow for default-device
/// selection, AudioQueue ownership, and queue-buffer lifecycle, while keeping
/// the managed Spice86.Audio float callback contract unchanged.
/// </summary>
[SupportedOSPlatform("osx")]
internal sealed class SdlCoreAudioDriver : ISdlAudioDriver
{
    private IntPtr _audioQueue;
    private IntPtr[] _audioBuffers = [];
    private IntPtr _mixBuffer;
    private int _mixBufferSize;
    private int _mixBufferOffset;
    private Thread? _audioQueueThread;
    private volatile bool _shutdown;
    private readonly ManualResetEventSlim _readySemaphore = new(false);
    private string? _threadError;
    private SdlAudioDevice? _device;
    private readonly object _mixerLock = new();
    private CoreAudioNativeMethods.AudioQueueOutputCallback? _outputCallbackDelegate;
    private GCHandle _callbackHandle;
    private IntPtr _defaultRunLoopMode;
    private uint _deviceId;
    private int _obtainedSampleRate;
    private int _obtainedChannels;
    private int _obtainedBufferFrames;

    /// <summary>
    /// Gets a value indicating whether CoreAudio owns the callback thread.
    /// SDL3 marks CoreAudio as <c>ProvidesOwnCallbackThread</c> because
    /// AudioQueue callbacks are driven by a CFRunLoop rather than SDL's
    /// generic playback thread.
    /// </summary>
    public bool ProvidesOwnCallbackThread => true;

    /// <summary>
    /// Opens the CoreAudio AudioQueue device.
    /// Reference: SDL_coreaudio.m COREAUDIO_OpenDevice.
    /// 
    /// Actual managed flow:
    /// 1. Resolve and preflight the default macOS output device.
    /// 2. Build the managed mix-buffer state for the requested callback format.
    /// 3. Spawn the AudioQueue thread so queue creation happens on its own run loop.
    /// 4. Return the obtained playback spec used by the managed mixer.
    /// </summary>
    public bool OpenDevice(SdlAudioDevice device, AudioSpec desiredSpec, out AudioSpec obtainedSpec, out int sampleFrames, out string? error)
    {
        obtainedSpec = desiredSpec;
        sampleFrames = 0;
        error = null;
        _device = device;
        _shutdown = false;
        _threadError = null;
        _deviceId = 0;
        _obtainedSampleRate = 0;
        _obtainedChannels = 0;
        _obtainedBufferFrames = 0;

        int channels = desiredSpec.Channels;
        int freq = desiredSpec.SampleRate;
        int bufferFrames = desiredSpec.BufferFrames;

        if (!PrepareDevice(out error))
        {
            return false;
        }

        _mixBufferSize = bufferFrames * channels * sizeof(float);
        _mixBufferOffset = _mixBufferSize;

        _mixBuffer = Marshal.AllocHGlobal(_mixBufferSize);
        unsafe
        {
            NativeMemory.Clear(_mixBuffer.ToPointer(), (nuint)_mixBufferSize);
        }

        _defaultRunLoopMode = CoreAudioNativeMethods.GetDefaultRunLoopMode();
        _readySemaphore.Reset();

        _audioQueueThread = new Thread(() => AudioQueueThreadProc(freq, channels, bufferFrames))
        {
            Name = "CoreAudio-AudioQueue",
            IsBackground = true
        };
        _audioQueueThread.Start();

        _readySemaphore.Wait();

        if (_threadError != null)
        {
            error = _threadError;
            return false;
        }

        int obtainedRate = _obtainedSampleRate > 0 ? _obtainedSampleRate : freq;
        int obtainedChannels = _obtainedChannels > 0 ? _obtainedChannels : channels;
        int finalBufferFrames = _obtainedBufferFrames > 0 ? _obtainedBufferFrames : bufferFrames;

        if (!desiredSpec.AllowNegotiate)
        {
            obtainedRate = freq;
            obtainedChannels = channels;
            finalBufferFrames = desiredSpec.BufferFrames;
        }

        obtainedSpec = new AudioSpec
        {
            SampleRate = obtainedRate,
            Channels = obtainedChannels,
            BufferFrames = finalBufferFrames,
            Callback = desiredSpec.Callback,
            PostmixCallback = desiredSpec.PostmixCallback,
            AllowNegotiate = desiredSpec.AllowNegotiate
        };
        sampleFrames = finalBufferFrames;

        return true;
    }

    /// <summary>
    /// Resolves and validates the default macOS playback device.
    /// Reference: SDL_coreaudio.m PrepareDevice.
    /// 
    /// The full SDL3 backend works with discovered device handles; the managed
    /// Spice86.Audio port currently supports default-device playback only, so it
    /// mirrors SDL3's validation logic against the current default output device.
    /// </summary>
    /// <param name="error">Receives a human-readable failure description.</param>
    /// <returns><see langword="true"/> if the default output device is usable.</returns>
    private bool PrepareDevice(out string? error)
    {
        error = null;

        CoreAudioNativeMethods.AudioObjectPropertyAddress address =
            CoreAudioNativeMethods.CreatePropertyAddress(
                CoreAudioConstants.AudioHardwarePropertyDefaultOutputDevice,
                CoreAudioConstants.AudioObjectPropertyScopeGlobal);

        uint size = sizeof(uint);
        int result = CoreAudioNativeMethods.AudioObjectGetPropertyData(
            CoreAudioConstants.AudioObjectSystemObject,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            out uint deviceId);

        if (result != CoreAudioConstants.NoErr || deviceId == 0)
        {
            error = $"CoreAudio: AudioObjectGetPropertyData(kAudioHardwarePropertyDefaultOutputDevice) failed with error {result}";
            return false;
        }

        address.Selector = CoreAudioConstants.AudioDevicePropertyDeviceIsAlive;
        address.Scope = CoreAudioConstants.AudioDevicePropertyScopeOutput;
        size = sizeof(uint);

        result = CoreAudioNativeMethods.AudioObjectGetPropertyData(
            deviceId,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            out uint alive);

        if (result != CoreAudioConstants.NoErr)
        {
            error = $"CoreAudio: AudioObjectGetPropertyData(kAudioDevicePropertyDeviceIsAlive) failed with error {result}";
            return false;
        }

        if (alive == 0)
        {
            error = "CoreAudio: requested default output device exists, but isn't alive.";
            return false;
        }

        address.Selector = CoreAudioConstants.AudioDevicePropertyHogMode;
        size = sizeof(int);

        result = CoreAudioNativeMethods.AudioObjectGetPropertyData(
            deviceId,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            out int hogModePid);

        if (result == CoreAudioConstants.NoErr && hogModePid != -1)
        {
            error = "CoreAudio: default output device is being hogged.";
            return false;
        }

        _deviceId = deviceId;
        return true;
    }

    /// <summary>
    /// Binds the AudioQueue to the previously selected CoreAudio device UID.
    /// Reference: SDL_coreaudio.m AssignDeviceToAudioQueue.
    /// </summary>
    /// <returns><see langword="true"/> if the queue is bound to the selected device.</returns>
    private bool AssignDeviceToAudioQueue()
    {
        if (_deviceId == 0)
        {
            _threadError = "CoreAudio: no output device was selected for AudioQueue binding.";
            return false;
        }

        CoreAudioNativeMethods.AudioObjectPropertyAddress address =
            CoreAudioNativeMethods.CreatePropertyAddress(
                CoreAudioConstants.AudioDevicePropertyDeviceUid,
                CoreAudioConstants.AudioDevicePropertyScopeOutput);

        uint size = (uint)IntPtr.Size;
        int result = CoreAudioNativeMethods.AudioObjectGetPropertyData(
            _deviceId,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            out IntPtr deviceUid);

        if (result != CoreAudioConstants.NoErr || deviceUid == IntPtr.Zero)
        {
            _threadError = $"CoreAudio: AudioObjectGetPropertyData(kAudioDevicePropertyDeviceUID) failed with error {result}";
            return false;
        }

        try
        {
            result = CoreAudioNativeMethods.AudioQueueSetProperty(
                _audioQueue,
                CoreAudioConstants.AudioQueuePropertyCurrentDevice,
                ref deviceUid,
                (uint)IntPtr.Size);

            if (result != CoreAudioConstants.NoErr)
            {
                _threadError = $"CoreAudio: AudioQueueSetProperty(kAudioQueueProperty_CurrentDevice) failed with error {result}";
                return false;
            }

            return true;
        }
        finally
        {
            CoreAudioNativeMethods.CFRelease(deviceUid);
        }
    }

    /// <summary>
    /// Closes the CoreAudio device.
    /// Reference: SDL_coreaudio.m COREAUDIO_CloseDevice.
    /// 
    /// Flow:
    /// 1. Set paused flag to feed silence from callback
    /// 2. AudioQueueFlush -> AudioQueueStop -> AudioQueueDispose
    /// 3. Wait for audioqueue_thread to finish
    /// 4. Free mix buffer
    /// </summary>
    public void CloseDevice(SdlAudioDevice device)
    {
        // Reference: COREAUDIO_CloseDevice line 679
        // "if callback fires again, feed silence; don't call into the app."
        // The shutdown flag is already set by SdlAudioDevice.Close()

        // Reference: COREAUDIO_CloseDevice line 681-683
        // "dispose of the audio queue before waiting on the thread, 
        //  or it might stall for a long time!"
        if (_audioQueue != IntPtr.Zero)
        {
            CoreAudioNativeMethods.AudioQueueFlush(_audioQueue);
            CoreAudioNativeMethods.AudioQueueStop(_audioQueue, 0);
            CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 0);
            _audioQueue = IntPtr.Zero;
        }

        // Reference: COREAUDIO_CloseDevice line 685-687
        // "SDL_WaitThread(this->hidden->thread, NULL)"
        _shutdown = true;
        if (_audioQueueThread != null && _audioQueueThread.IsAlive)
        {
            _audioQueueThread.Join(TimeSpan.FromSeconds(5));
        }
        _audioQueueThread = null;

        // Free the mix buffer
        if (_mixBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_mixBuffer);
            _mixBuffer = IntPtr.Zero;
        }

        // Free callback handle
        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }

        _deviceId = 0;
        _audioBuffers = [];
    }

    /// <summary>
    /// WaitDevice for CoreAudio is a no-op.
    /// Reference: SDL3 sets <c>ProvidesOwnCallbackThread</c> for CoreAudio, so
    /// SDL's generic WaitDevice/GetDeviceBuf/PlayDevice loop is bypassed.
    /// </summary>
    public void WaitDevice(SdlAudioDevice device)
    {
        // CoreAudio uses ProvidesOwnCallbackThread.
        // The SdlAudioDevice thread is idle; sleep to avoid busy-waiting.
        Thread.Sleep(100);
    }

    /// <summary>
    /// GetDeviceBuf for CoreAudio returns <see cref="IntPtr.Zero"/>.
    /// The AudioQueue callback path fills and re-enqueues buffers directly.
    /// </summary>
    public IntPtr GetDeviceBuf(SdlAudioDevice device)
    {
        return IntPtr.Zero;
    }

    /// <summary>
    /// PlayDevice for CoreAudio is a no-op.
    /// The AudioQueue callback path re-enqueues buffers directly.
    /// </summary>
    public void PlayDevice(SdlAudioDevice device)
    {
    }

    /// <summary>
    /// ThreadInit for CoreAudio is a no-op in the managed SDL thread.
    /// Reference: SDL3 does its priority adjustment in AudioQueueThreadEntry,
    /// which corresponds to <see cref="AudioQueueThreadProc(int, int, int)"/>.
    /// </summary>
    public void ThreadInit(SdlAudioDevice device)
    {
        // CoreAudio manages its own thread. The SdlAudioDevice thread
        // is essentially idle for CoreAudio.
    }

    /// <summary>
    /// ThreadDeinit for CoreAudio is a no-op.
    /// </summary>
    public void ThreadDeinit(SdlAudioDevice device)
    {
    }

    /// <summary>
    /// The AudioQueue thread function.
    /// Reference: SDL_coreaudio.m AudioQueueThreadEntry.
    /// 
    /// Flow:
    /// 1. Call prepare_audioqueue (creates AudioQueue on this thread's CFRunLoop)
    /// 2. Signal ready semaphore
    /// 3. Loop CFRunLoopRunInMode until shutdown
    /// 4. On exit, drain remaining playback
    /// </summary>
    private void AudioQueueThreadProc(int sampleRate, int channels, int bufferFrames)
    {
        // Reference: audioqueue_thread line 1010-1013
        // prepare_audioqueue creates the AudioQueue bound to this thread's CFRunLoop
        if (!PrepareAudioQueue(sampleRate, channels, bufferFrames))
        {
            _threadError = _threadError ?? "Failed to prepare AudioQueue";
            _readySemaphore.Set();
            return;
        }

        // Reference: audioqueue_thread line 1020
        // SDL_SetThreadPriority(SDL_THREAD_PRIORITY_HIGH)
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

        // Reference: audioqueue_thread line 1023
        // "init was successful, alert parent thread and start running..."
        _readySemaphore.Set();

        // Reference: audioqueue_thread line 1025-1059
        // Main run loop
        while (!_shutdown && (_device == null || !_device.ShutdownRequested))
        {
            // Reference: audioqueue_thread line 1026
            // CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.10, 1)
            CoreAudioNativeMethods.CFRunLoopRunInMode(_defaultRunLoopMode, 0.10, 1);
        }

        // Reference: audioqueue_thread line 1061-1064
        // "if (!this->iscapture)" - drain off any pending playback
        if (_device != null)
        {
            double secs = (((double)_mixBufferSize / sizeof(float)) / channels) / sampleRate * 2.0;
            CoreAudioNativeMethods.CFRunLoopRunInMode(_defaultRunLoopMode, secs, 0);
        }
    }

    /// <summary>
    /// Prepares the AudioQueue.
    /// Reference: SDL_coreaudio.m PrepareAudioQueue.
    /// 
    /// Actual managed flow:
    /// 1. Create an output queue for the requested managed float mix format.
    /// 2. Bind the queue to the selected CoreAudio default device.
    /// 3. Apply SDL3-style channel-layout and buffer-count policy.
    /// 4. Prime the queue with silence and start playback.
    /// </summary>
    private bool PrepareAudioQueue(int sampleRate, int channels, int bufferFrames)
    {
        // Reference: prepare_audioqueue line ~896-900
        // Setup AudioStreamBasicDescription
        CoreAudioNativeMethods.AudioStreamBasicDescription strdesc = new()
        {
            SampleRate = sampleRate,
            FormatId = CoreAudioConstants.AudioFormatLinearPcm,
            // Float LE + Packed (matching SDL's AUDIO_F32LSB path)
            FormatFlags = CoreAudioConstants.LinearPcmFormatFlagIsFloat |
                          CoreAudioConstants.LinearPcmFormatFlagIsPacked,
            ChannelsPerFrame = (uint)channels,
            BitsPerChannel = 32, // float = 32 bits
            FramesPerPacket = 1,
            BytesPerFrame = (uint)(channels * sizeof(float)),
            BytesPerPacket = (uint)(channels * sizeof(float)),
        };

        // Reference: prepare_audioqueue line ~908-910
        // AudioQueueNewOutput with CFRunLoopGetCurrent()
        _outputCallbackDelegate = OutputCallback;
        _callbackHandle = GCHandle.Alloc(_outputCallbackDelegate);

        IntPtr currentRunLoop = CoreAudioNativeMethods.CFRunLoopGetCurrent();

        int result = CoreAudioNativeMethods.AudioQueueNewOutput(
            ref strdesc,
            _outputCallbackDelegate,
            IntPtr.Zero,
            currentRunLoop,
            _defaultRunLoopMode,
            0,
            out _audioQueue);

        if (result != CoreAudioConstants.NoErr)
        {
            _threadError = $"CoreAudio: AudioQueueNewOutput failed with error {result}";
            return false;
        }

        if (!AssignDeviceToAudioQueue())
        {
            CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 1);
            _audioQueue = IntPtr.Zero;
            return false;
        }

        if (!UpdateObtainedFormatFromQueue(bufferFrames, channels))
        {
            CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 1);
            _audioQueue = IntPtr.Zero;
            return false;
        }

        // Reference: prepare_audioqueue line ~920-944
        // Set channel layout
        CoreAudioNativeMethods.AudioChannelLayout layout = new();
        switch (channels)
        {
            case 1:
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagMono;
                break;
            case 2:
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagStereo;
                break;
            case 3:
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagDvd4;
                break;
            case 4:
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagQuadraphonic;
                break;
            case 5:
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagDvd6;
                break;
            case 6:
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagDvd12;
                break;
            default:
                _threadError = $"CoreAudio: Unsupported audio channels: {channels}";
                CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 1);
                _audioQueue = IntPtr.Zero;
                return false;
        }

        if (layout.ChannelLayoutTag != 0)
        {
            int layoutSize = Marshal.SizeOf<CoreAudioNativeMethods.AudioChannelLayout>();
            IntPtr layoutPtr = Marshal.AllocHGlobal(layoutSize);
            try
            {
                Marshal.StructureToPtr(layout, layoutPtr, false);
                result = CoreAudioNativeMethods.AudioQueueSetProperty(
                    _audioQueue,
                    CoreAudioConstants.AudioQueuePropertyChannelLayout,
                    layoutPtr,
                    (uint)layoutSize);
                // Ignore errors - not critical (SDL does CHECK_RESULT but continues)
            }
            finally
            {
                Marshal.FreeHGlobal(layoutPtr);
            }
        }

        // Reference: prepare_audioqueue line ~956-970
        // Calculate number of audio buffers
        // "Make sure we can feed the device a minimum amount of time"
        uint bufferSizeBytes = (uint)(bufferFrames * channels * sizeof(float));
        int numAudioBuffers = CoreAudioBufferPolicy.ComputeAudioBufferCount(sampleRate, bufferFrames);

        _audioBuffers = new IntPtr[numAudioBuffers];

        for (int i = 0; i < numAudioBuffers; i++)
        {
            result = CoreAudioNativeMethods.AudioQueueAllocateBuffer(
                _audioQueue,
                bufferSizeBytes,
                out _audioBuffers[i]);

            if (result != CoreAudioConstants.NoErr)
            {
                _threadError = $"CoreAudio: AudioQueueAllocateBuffer failed with error {result}";
                CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 1);
                _audioQueue = IntPtr.Zero;
                return false;
            }

            // Fill with silence and set size
            // Reference: prepare_audioqueue line ~967
            unsafe
            {
                CoreAudioNativeMethods.AudioQueueBuffer* bufPtr =
                    (CoreAudioNativeMethods.AudioQueueBuffer*)_audioBuffers[i];
                NativeMemory.Clear(bufPtr->AudioData.ToPointer(), bufPtr->AudioDataBytesCapacity);
                bufPtr->AudioDataByteSize = bufPtr->AudioDataBytesCapacity;

                if (i == 0)
                {
                    int bytesPerFrame = channels * sizeof(float);
                    int obtainedFrames;
                    if (bytesPerFrame > 0)
                    {
                        obtainedFrames = (int)(bufPtr->AudioDataBytesCapacity / (uint)bytesPerFrame);
                    }
                    else
                    {
                        obtainedFrames = 0;
                    }

                    if (obtainedFrames > 0)
                    {
                        _obtainedBufferFrames = obtainedFrames;
                    }
                }
            }

            // Enqueue the buffer
            result = CoreAudioNativeMethods.AudioQueueEnqueueBuffer(
                _audioQueue, _audioBuffers[i], 0, IntPtr.Zero);

            if (result != CoreAudioConstants.NoErr)
            {
                _threadError = $"CoreAudio: AudioQueueEnqueueBuffer failed with error {result}";
                CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 1);
                _audioQueue = IntPtr.Zero;
                return false;
            }
        }

        // Reference: prepare_audioqueue line ~972
        // Start the AudioQueue
        result = CoreAudioNativeMethods.AudioQueueStart(_audioQueue, IntPtr.Zero);
        if (result != CoreAudioConstants.NoErr)
        {
            _threadError = $"CoreAudio: AudioQueueStart failed with error {result}";
            CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 1);
            _audioQueue = IntPtr.Zero;
            return false;
        }

        return true;
    }

    private bool UpdateObtainedFormatFromQueue(int requestedBufferFrames, int requestedChannels)
    {
        int streamDescSize = Marshal.SizeOf<CoreAudioNativeMethods.AudioStreamBasicDescription>();
        IntPtr streamDescPtr = Marshal.AllocHGlobal(streamDescSize);

        try
        {
            uint size = (uint)streamDescSize;
            int result = CoreAudioNativeMethods.AudioQueueGetProperty(
                _audioQueue,
                CoreAudioConstants.AudioQueuePropertyStreamDescription,
                streamDescPtr,
                ref size);

            if (result != CoreAudioConstants.NoErr)
            {
                _threadError = $"CoreAudio: AudioQueueGetProperty(kAudioQueueProperty_StreamDescription) failed with error {result}";
                return false;
            }

            CoreAudioNativeMethods.AudioStreamBasicDescription queueFormat =
                Marshal.PtrToStructure<CoreAudioNativeMethods.AudioStreamBasicDescription>(streamDescPtr);

            _obtainedSampleRate = (int)Math.Round(queueFormat.SampleRate);
            _obtainedChannels = (int)queueFormat.ChannelsPerFrame;

            if (_obtainedChannels <= 0)
            {
                _obtainedChannels = requestedChannels;
            }

            _obtainedBufferFrames = requestedBufferFrames;

            if (_obtainedSampleRate <= 0)
            {
                _obtainedSampleRate = 0;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(streamDescPtr);
        }
    }

    /// <summary>
    /// The AudioQueue output callback.
    /// Reference: SDL_coreaudio.m PlaybackBufferReadyCallback and the non-stream
    /// callback path it eventually drives through SDL_PlaybackAudioThreadIterate.
    /// 
    /// This is called by CoreAudio when a buffer has been consumed and needs refilling.
    /// The managed port uses a private mix buffer and the public float callback
    /// contract to refill the AudioQueue buffer before immediately re-enqueueing it.
    /// 
    /// Managed callback flow:
    /// 1. Lock mixer_lock
    /// 2. While remaining bytes in buffer:
    ///    a. If bufferOffset >= bufferSize, call user callback to fill mix buffer
    ///    b. Copy from mix buffer to AudioQueue buffer
    /// 3. Enqueue the buffer back
    /// 4. Unlock mixer_lock
    /// </summary>
    private void OutputCallback(IntPtr inUserData, IntPtr inAudioQueue, IntPtr inBuffer)
    {
        // Reference: outputCallback line 463-466
        // Check shutdown before and after lock
        if (_device == null || _device.ShutdownRequested)
        {
            return;
        }

        lock (_mixerLock)
        {
            if (_device.ShutdownRequested)
            {
                return;
            }

            unsafe
            {
                CoreAudioNativeMethods.AudioQueueBuffer* bufPtr =
                    (CoreAudioNativeMethods.AudioQueueBuffer*)inBuffer;

                uint remaining = bufPtr->AudioDataBytesCapacity;
                byte* ptr = (byte*)bufPtr->AudioData;

                // Reference: outputCallback line 501-518 (non-stream path)
                while (remaining > 0)
                {
                    if (_mixBufferOffset >= _mixBufferSize)
                    {
                        // Generate the data via the user callback
                        // Reference: outputCallback line 504-505
                        _device.FillAudioBuffer(_mixBuffer, _mixBufferSize);
                        _mixBufferOffset = 0;
                    }

                    uint len = (uint)(_mixBufferSize - _mixBufferOffset);
                    if (len > remaining)
                    {
                        len = remaining;
                    }

                    // Reference: outputCallback line 512-513
                    Buffer.MemoryCopy(
                        ((byte*)_mixBuffer + _mixBufferOffset),
                        ptr,
                        remaining,
                        len);

                    ptr += len;
                    remaining -= len;
                    _mixBufferOffset += (int)len;
                }

                // Reference: outputCallback line 487-489
                // Enqueue the buffer back and set its size
                bufPtr->AudioDataByteSize = bufPtr->AudioDataBytesCapacity;
                int enqueueResult = CoreAudioNativeMethods.AudioQueueEnqueueBuffer(
                    inAudioQueue, inBuffer, 0, IntPtr.Zero);

                if (enqueueResult != CoreAudioConstants.NoErr)
                {
                    _device.SetDeviceDisconnected();
                    _threadError = $"CoreAudio: AudioQueueEnqueueBuffer failed in callback with error {enqueueResult}";
                    return;
                }
            }
        }
    }
}

