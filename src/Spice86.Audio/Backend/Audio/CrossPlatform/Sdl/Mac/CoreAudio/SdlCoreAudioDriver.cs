namespace Spice86.Audio.Backend.Audio.CrossPlatform.Sdl.Mac.CoreAudio;

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

/// <summary>
/// CoreAudio AudioQueue driver implementing <see cref="ISdlAudioDriver"/>.
/// References:
/// - SDL/src/audio/coreaudio/SDL_coreaudio.m
/// - SDL/src/audio/coreaudio/SDL_coreaudio.h
///
/// This is a faithful port of SDL's macOS CoreAudio playback backend: default
/// device preflight, AudioQueue ownership on a dedicated CFRunLoop thread, and
/// the current_buffer / GetDeviceBuf / PlayDevice buffer-ready contract.
/// </summary>
[SupportedOSPlatform("osx")]
internal sealed class SdlCoreAudioDriver : ISdlAudioDriver
{
    /// <summary>
    /// Byte value used to fill a buffer with silence.
    /// Reference: SDL_audio.c device->silence_value, which is 0x00 for every
    /// signed and floating point format, including the F32 format used here.
    /// </summary>
    private const byte SilenceValue = 0;

    private IntPtr _audioQueue;
    private IntPtr[] _audioBuffers = [];
    private IntPtr _currentBuffer;
    private Thread? _audioQueueThread;
    private volatile bool _shutdown;
    private readonly ManualResetEventSlim _readySemaphore = new(false);
    private string? _threadError;
    private SdlAudioDevice? _device;
    private CoreAudioNativeMethods.AudioQueueOutputCallback? _outputCallbackDelegate;
    private GCHandle _callbackHandle;
    private GCHandle _deviceHandle;
    private IntPtr _defaultRunLoopMode;
    private uint _deviceId;

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
    /// Flow:
    /// 1. Preflight the default macOS output device (PrepareDevice).
    /// 2. Create the ready semaphore and spawn the AudioQueue thread, so queue
    ///    creation happens on a thread that owns its own CFRunLoop.
    /// 3. Wait for the thread, and propagate any thread_error it reported.
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
        _currentBuffer = IntPtr.Zero;

        int channels = desiredSpec.Channels;
        int freq = desiredSpec.SampleRate;
        int bufferFrames = desiredSpec.BufferFrames;

        if (!PrepareDevice(out error))
        {
            return false;
        }

        _defaultRunLoopMode = CoreAudioNativeMethods.GetDefaultRunLoopMode();
        _readySemaphore.Reset();

        // Reference: COREAUDIO_OpenDevice passes `device` as the AudioQueue user data.
        _deviceHandle = GCHandle.Alloc(device);

        _audioQueueThread = new Thread(() => AudioQueueThreadProc(freq, channels, bufferFrames))
        {
            Name = "CoreAudio-AudioQueue",
            IsBackground = true
        };
        _audioQueueThread.Start();

        _readySemaphore.Wait();

        // Reference: COREAUDIO_OpenDevice
        // "SDL_WaitThread(device->hidden->thread, NULL)" on thread_error.
        if (_threadError != null)
        {
            _audioQueueThread.Join();
            _audioQueueThread = null;
            error = _threadError;
            return false;
        }

        sampleFrames = bufferFrames;

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
    /// 1. AudioQueueFlush -> AudioQueueStop -> AudioQueueDispose, before joining
    ///    the thread, "or it might stall for a long time!"
    /// 2. Wait for the AudioQueue thread.
    /// 3. Release the callback and device handles.
    /// </summary>
    public void CloseDevice(SdlAudioDevice device)
    {
        // Reference: COREAUDIO_CloseDevice
        // "if callback fires again, feed silence; don't call into the app."
        // The shutdown flag is already set by SdlAudioDevice.Close()
        _shutdown = true;

        // Reference: COREAUDIO_CloseDevice
        // "dispose of the audio queue before waiting on the thread,
        //  or it might stall for a long time!"
        if (_audioQueue != IntPtr.Zero)
        {
            CoreAudioNativeMethods.AudioQueueFlush(_audioQueue);
            CoreAudioNativeMethods.AudioQueueStop(_audioQueue, 0);
            CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 0);
            _audioQueue = IntPtr.Zero;
        }

        // Reference: COREAUDIO_CloseDevice "SDL_WaitThread(device->hidden->thread, NULL)"
        if (_audioQueueThread != null && _audioQueueThread.IsAlive)
        {
            _audioQueueThread.Join(TimeSpan.FromSeconds(5));
        }
        _audioQueueThread = null;

        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }

        if (_deviceHandle.IsAllocated)
        {
            _deviceHandle.Free();
        }

        _currentBuffer = IntPtr.Zero;
        _deviceId = 0;
        _audioBuffers = [];
    }

    /// <summary>
    /// WaitDevice for CoreAudio is a no-op.
    /// Reference: SDL marks CoreAudio as <c>ProvidesOwnCallbackThread</c>, so the
    /// generic WaitDevice loop is never entered; the buffer-ready callback drives
    /// each iteration instead.
    /// </summary>
    public void WaitDevice(SdlAudioDevice device)
    {
    }

    /// <summary>
    /// Returns the AudioQueue buffer that is currently being filled.
    /// Reference: SDL_coreaudio.m COREAUDIO_GetDeviceBuf.
    /// </summary>
    public IntPtr GetDeviceBuf(SdlAudioDevice device)
    {
        // Reference: COREAUDIO_GetDeviceBuf
        // "should have been called from PlaybackBufferReadyCallback"
        IntPtr currentBuffer = _currentBuffer;
        if (currentBuffer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        unsafe
        {
            CoreAudioNativeMethods.AudioQueueBuffer* bufPtr =
                (CoreAudioNativeMethods.AudioQueueBuffer*)currentBuffer;
            return bufPtr->AudioData;
        }
    }

    /// <summary>
    /// Submits the filled AudioQueue buffer back to CoreAudio.
    /// Reference: SDL_coreaudio.m COREAUDIO_PlayDevice.
    /// </summary>
    public void PlayDevice(SdlAudioDevice device)
    {
        // Reference: COREAUDIO_PlayDevice
        // "should have been called from PlaybackBufferReadyCallback"
        IntPtr currentBuffer = _currentBuffer;
        if (currentBuffer == IntPtr.Zero)
        {
            return;
        }

        unsafe
        {
            CoreAudioNativeMethods.AudioQueueBuffer* bufPtr =
                (CoreAudioNativeMethods.AudioQueueBuffer*)currentBuffer;
            bufPtr->AudioDataByteSize = bufPtr->AudioDataBytesCapacity;
        }

        _currentBuffer = IntPtr.Zero;
        CoreAudioNativeMethods.AudioQueueEnqueueBuffer(_audioQueue, currentBuffer, 0, IntPtr.Zero);
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
        // Reference: AudioQueueThreadEntry "SDL_PlaybackAudioThreadSetup(device)",
        // which raises the thread to SDL_THREAD_PRIORITY_HIGH before preparing the queue.
        Thread.CurrentThread.Priority = ThreadPriority.Highest;

        // Reference: AudioQueueThreadEntry
        // PrepareAudioQueue creates the AudioQueue bound to this thread's CFRunLoop
        if (!PrepareAudioQueue(sampleRate, channels, bufferFrames))
        {
            _threadError ??= "Failed to prepare AudioQueue";
            _readySemaphore.Set();
            return;
        }

        // Reference: AudioQueueThreadEntry
        // "init was successful, alert parent thread and start running..."
        _readySemaphore.Set();

        // Reference: AudioQueueThreadEntry
        // "This would be WaitDevice in the normal SDL audio thread, but we get
        //  *BufferReadyCallback calls here to know when to iterate."
        while (!_shutdown && (_device == null || !_device.ShutdownRequested))
        {
            CFRunLoopRunInModeDefault(0.10, true);
        }

        // Reference: AudioQueueThreadEntry "Drain off any pending playback."
        // const CFTimeInterval secs = (sample_frames / spec.freq) * 2.0
        double secs = ((double)bufferFrames / sampleRate) * 2.0;
        CFRunLoopRunInModeDefault(secs, false);
    }

    private void CFRunLoopRunInModeDefault(double seconds, bool returnAfterSourceHandled)
    {
        CoreAudioNativeMethods.CFRunLoopRunInMode(
            _defaultRunLoopMode,
            seconds,
            (byte)(returnAfterSourceHandled ? 1 : 0));
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
            GCHandle.ToIntPtr(_deviceHandle),
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

        // Reference: PrepareAudioQueue channel layout selection
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
            case 7:
                // Reference: PrepareAudioQueue
                // "kAudioChannelLayoutTag_WAVE_6_1" on macOS 10.15+, which is the
                // minimum supported by this runtime.
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagWave61;
                break;
            case 8:
                // Reference: PrepareAudioQueue "kAudioChannelLayoutTag_WAVE_7_1".
                layout.ChannelLayoutTag = CoreAudioConstants.AudioChannelLayoutTagWave71;
                break;
            default:
                // Reference: PrepareAudioQueue SDL_SetError("Unsupported audio channels")
                _threadError = "Unsupported audio channels";
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
            }
            finally
            {
                Marshal.FreeHGlobal(layoutPtr);
            }

            // Reference: PrepareAudioQueue
            // CHECK_RESULT("AudioQueueSetProperty (kAudioQueueProperty_ChannelLayout)")
            if (result != CoreAudioConstants.NoErr)
            {
                _threadError = $"CoreAudio: AudioQueueSetProperty (kAudioQueueProperty_ChannelLayout) failed with error {result}";
                CoreAudioNativeMethods.AudioQueueDispose(_audioQueue, 1);
                _audioQueue = IntPtr.Zero;
                return false;
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

            // Reference: PrepareAudioQueue
            // SDL_memset(device->hidden->audioBuffer[i]->mAudioData, device->silence_value, ...)
            unsafe
            {
                CoreAudioNativeMethods.AudioQueueBuffer* bufPtr =
                    (CoreAudioNativeMethods.AudioQueueBuffer*)_audioBuffers[i];
                NativeMemory.Fill(
                    bufPtr->AudioData.ToPointer(),
                    bufPtr->AudioDataBytesCapacity,
                    SilenceValue);
                bufPtr->AudioDataByteSize = bufPtr->AudioDataBytesCapacity;
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

    /// <summary>
    /// The AudioQueue output callback.
    /// Reference: SDL_coreaudio.m PlaybackBufferReadyCallback.
    ///
    /// Flow:
    /// 1. Publish the ready buffer as current_buffer.
    /// 2. Run one playback iteration, which calls GetDeviceBuf, fills it, and
    ///    hands it back through PlayDevice.
    /// 3. If the buffer is unexpectedly still pending, we're probably dying:
    ///    requeue it filled with the silence value.
    /// </summary>
    private void OutputCallback(IntPtr inUserData, IntPtr inAudioQueue, IntPtr inBuffer)
    {
        // Reference: PlaybackBufferReadyCallback
        // "SDL_AudioDevice *device = (SDL_AudioDevice *)inUserData"
        SdlAudioDevice? device = null;
        if (inUserData != IntPtr.Zero)
        {
            device = GCHandle.FromIntPtr(inUserData).Target as SdlAudioDevice;
        }

        device ??= _device;
        if (device == null)
        {
            return;
        }

        // Reference: PlaybackBufferReadyCallback
        // "device->hidden->current_buffer = inBuffer"
        _currentBuffer = inBuffer;

        bool okay = PlaybackAudioThreadIterate(device);

        // Reference: PlaybackBufferReadyCallback
        // "buffer is unexpectedly here? We're probably dying, but try to
        //  requeue this buffer with silence."
        if (!okay && _currentBuffer != IntPtr.Zero)
        {
            IntPtr currentBuffer = _currentBuffer;
            _currentBuffer = IntPtr.Zero;

            unsafe
            {
                CoreAudioNativeMethods.AudioQueueBuffer* bufPtr =
                    (CoreAudioNativeMethods.AudioQueueBuffer*)currentBuffer;
                NativeMemory.Fill(
                    bufPtr->AudioData.ToPointer(),
                    bufPtr->AudioDataBytesCapacity,
                    SilenceValue);
            }

            CoreAudioNativeMethods.AudioQueueEnqueueBuffer(inAudioQueue, currentBuffer, 0, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Runs a single playback iteration for the buffer that CoreAudio just released.
    /// Reference: SDL_audio.c SDL_PlaybackAudioThreadIterate, as driven by
    /// PlaybackBufferReadyCallback.
    /// </summary>
    /// <param name="device">The device owning the callback.</param>
    /// <returns><see langword="false"/> when the device is shutting down.</returns>
    private bool PlaybackAudioThreadIterate(SdlAudioDevice device)
    {
        lock (device.MixerLock)
        {
            // Reference: SDL_PlaybackAudioThreadIterate
            // "if (SDL_GetAtomicInt(&device->shutdown)) { return false; }"
            if (_shutdown || device.ShutdownRequested)
            {
                return false;
            }

            IntPtr deviceBuffer = GetDeviceBuf(device);
            if (deviceBuffer == IntPtr.Zero)
            {
                return false;
            }

            int bufferSize;
            unsafe
            {
                CoreAudioNativeMethods.AudioQueueBuffer* bufPtr =
                    (CoreAudioNativeMethods.AudioQueueBuffer*)_currentBuffer;
                bufferSize = (int)bufPtr->AudioDataBytesCapacity;
            }

            device.FillAudioBuffer(deviceBuffer, bufferSize);
            PlayDevice(device);

            return true;
        }
    }
}

