namespace Spice86.Audio.Backend.Audio.CrossPlatform.Sdl.Mac.CoreAudio;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke bindings for the subset of CoreAudio, AudioToolbox, and CoreFoundation
/// used by the managed macOS audio backend.
/// References:
/// - SDL/src/audio/coreaudio/SDL_coreaudio.m
/// - SDL/src/audio/coreaudio/SDL_coreaudio.h
/// - Apple AudioQueue and AudioObject property APIs
/// 
/// These bindings model the default-device playback flow actually used by
/// Spice86.Audio rather than the full SDL3 device-enumeration surface.
/// </summary>
internal static class CoreAudioNativeMethods
{
    private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>
    /// AudioStreamBasicDescription structure.
    /// Reference: CoreAudioTypes.h
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    /// <summary>
    /// AudioQueueBuffer structure.
    /// Reference: AudioQueue.h
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    /// <summary>
    /// AudioChannelLayout structure.
    /// Reference: CoreAudioTypes.h
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioChannelLayout
    {
        public uint ChannelLayoutTag;
        public uint ChannelBitmap;
        public uint NumberChannelDescriptions;
    }

    /// <summary>
    /// AudioObjectPropertyAddress structure.
    /// Reference: AudioHardwareBase.h
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    /// <summary>
    /// AudioQueue output callback delegate.
    /// Reference: AudioQueue.h AudioQueueOutputCallback.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void AudioQueueOutputCallback(
        IntPtr inUserData,
        IntPtr inAudioQueue,
        IntPtr inBuffer);

    /// <summary>
    /// Creates a playback AudioQueue bound to the calling thread's run loop.
    /// Reference: SDL_coreaudio.m PrepareAudioQueue -> AudioQueueNewOutput.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueNewOutput")]
    internal static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription inFormat,
        AudioQueueOutputCallback inCallbackProc,
        IntPtr inUserData,
        IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode,
        uint inFlags,
        out IntPtr outAQ);

    /// <summary>
    /// Allocates an AudioQueue buffer.
    /// Reference: SDL_coreaudio.m PrepareAudioQueue -> AudioQueueAllocateBuffer.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueAllocateBuffer")]
    internal static extern int AudioQueueAllocateBuffer(
        IntPtr inAQ,
        uint inBufferByteSize,
        out IntPtr outBuffer);

    /// <summary>
    /// Enqueues an AudioQueue buffer for playback.
    /// Reference: SDL_coreaudio.m PlaybackBufferReadyCallback / PrepareAudioQueue.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueEnqueueBuffer")]
    internal static extern int AudioQueueEnqueueBuffer(
        IntPtr inAQ,
        IntPtr inBuffer,
        uint inNumPacketDescs,
        IntPtr inPacketDescs);

    /// <summary>
    /// Starts the AudioQueue.
    /// Reference: SDL_coreaudio.m PrepareAudioQueue -> AudioQueueStart.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueStart")]
    internal static extern int AudioQueueStart(
        IntPtr inAQ,
        IntPtr inStartTime);

    /// <summary>
    /// Stops the AudioQueue.
    /// Reference: SDL_coreaudio.m COREAUDIO_CloseDevice -> AudioQueueStop.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueStop")]
    internal static extern int AudioQueueStop(
        IntPtr inAQ,
        byte inImmediate);

    /// <summary>
    /// Flushes queued playback buffers.
    /// Reference: SDL_coreaudio.m COREAUDIO_CloseDevice -> AudioQueueFlush.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueFlush")]
    internal static extern int AudioQueueFlush(IntPtr inAQ);

    /// <summary>
    /// Disposes the AudioQueue and its buffers.
    /// Reference: SDL_coreaudio.m COREAUDIO_CloseDevice -> AudioQueueDispose.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueDispose")]
    internal static extern int AudioQueueDispose(
        IntPtr inAQ,
        byte inImmediate);

    /// <summary>
    /// Sets an AudioQueue property using unmanaged memory.
    /// Reference: SDL_coreaudio.m PrepareAudioQueue -> AudioQueueSetProperty.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueSetProperty")]
    internal static extern int AudioQueueSetProperty(
        IntPtr inAQ,
        uint inID,
        IntPtr inData,
        uint inDataSize);

    /// <summary>
    /// Sets an AudioQueue property using a pointer-sized value.
    /// Reference: SDL_coreaudio.m AssignDeviceToAudioQueue -> kAudioQueueProperty_CurrentDevice.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueSetProperty")]
    internal static extern int AudioQueueSetProperty(
        IntPtr inAQ,
        uint inID,
        ref IntPtr inData,
        uint inDataSize);

    /// <summary>
    /// Gets an AudioQueue property into unmanaged storage.
    /// Used to read the queue's effective stream description after creation.
    /// </summary>
    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueGetProperty")]
    internal static extern int AudioQueueGetProperty(
        IntPtr inAQ,
        uint inID,
        IntPtr outData,
        ref uint ioDataSize);

    /// <summary>
    /// Reads a UInt32 property value from a CoreAudio object.
    /// Used for default device identifiers and boolean-style device flags.
    /// </summary>
    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    internal static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out uint outData);

    /// <summary>
    /// Reads an Int32 property value from a CoreAudio object.
    /// Used for hog-mode process identifiers.
    /// </summary>
    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    internal static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out int outData);

    /// <summary>
    /// Reads a pointer-sized property value from a CoreAudio object.
    /// Used for CFStringRef device UID values.
    /// </summary>
    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    internal static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out IntPtr outData);

    /// <summary>
    /// Gets the current thread's run loop.
    /// Reference: SDL_coreaudio.m AudioQueueThreadEntry -> CFRunLoopGetCurrent.
    /// </summary>
    [DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopGetCurrent")]
    internal static extern IntPtr CFRunLoopGetCurrent();

    /// <summary>
    /// Runs the current thread's run loop for a bounded interval.
    /// Reference: SDL_coreaudio.m AudioQueueThreadEntry -> CFRunLoopRunInMode.
    /// </summary>
    [DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopRunInMode")]
    internal static extern int CFRunLoopRunInMode(
        IntPtr mode,
        double seconds,
        byte returnAfterSourceHandled);

    /// <summary>
    /// Gets the main run loop.
    /// Present for completeness alongside the other run-loop bindings.
    /// </summary>
    [DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopGetMain")]
    internal static extern IntPtr CFRunLoopGetMain();

    /// <summary>
    /// Releases a CoreFoundation object retained by a property query.
    /// </summary>
    [DllImport(CoreFoundationLib, EntryPoint = "CFRelease")]
    internal static extern void CFRelease(IntPtr cf);

    /// <summary>
    /// Loads the global kCFRunLoopDefaultMode symbol.
    /// Reference: SDL_coreaudio.m AudioQueueThreadEntry uses kCFRunLoopDefaultMode.
    /// </summary>
    internal static IntPtr GetDefaultRunLoopMode()
    {
        IntPtr lib = NativeLibrary.Load(CoreFoundationLib);
        IntPtr symbolAddr = NativeLibrary.GetExport(lib, "kCFRunLoopDefaultMode");
        return Marshal.ReadIntPtr(symbolAddr);
    }

    /// <summary>
    /// Creates a CoreAudio property-address struct with the main element filled in.
    /// </summary>
    internal static AudioObjectPropertyAddress CreatePropertyAddress(uint selector, uint scope)
    {
        return new AudioObjectPropertyAddress
        {
            Selector = selector,
            Scope = scope,
            Element = CoreAudioConstants.AudioObjectPropertyElementMain
        };
    }
}

/// <summary>
/// CoreAudio and AudioToolbox constants used by the managed macOS playback port.
/// References: CoreAudioTypes.h, AudioHardwareBase.h, AudioQueue.h, SDL_coreaudio.m.
/// </summary>
internal static class CoreAudioConstants
{
    internal const int NoErr = 0;
    internal const uint AudioObjectSystemObject = 1;
    internal const uint AudioFormatLinearPcm = 0x6C70636D;
    internal const uint LinearPcmFormatFlagIsFloat = 1 << 0;
    internal const uint LinearPcmFormatFlagIsBigEndian = 1 << 1;
    internal const uint LinearPcmFormatFlagIsSignedInteger = 1 << 2;
    internal const uint LinearPcmFormatFlagIsPacked = 1 << 3;
    internal const uint AudioQueuePropertyChannelLayout = 0x61716368;
    internal static readonly uint AudioQueuePropertyCurrentDevice = MakeFourCc("aqcd");
    internal static readonly uint AudioQueuePropertyStreamDescription = MakeFourCc("aqft");
    internal static readonly uint AudioHardwarePropertyDefaultOutputDevice = MakeFourCc("dOut");
    internal static readonly uint AudioDevicePropertyDeviceUid = MakeFourCc("uid ");
    internal static readonly uint AudioDevicePropertyDeviceIsAlive = MakeFourCc("livn");
    internal static readonly uint AudioDevicePropertyHogMode = MakeFourCc("oink");
    internal static readonly uint AudioObjectPropertyScopeGlobal = MakeFourCc("glob");
    internal static readonly uint AudioDevicePropertyScopeOutput = MakeFourCc("outp");
    internal const uint AudioObjectPropertyElementMain = 0;
    internal const uint AudioChannelLayoutTagMono = (100 << 16) | 1;
    internal const uint AudioChannelLayoutTagStereo = (101 << 16) | 2;
    internal const uint AudioChannelLayoutTagDvd4 = (134 << 16) | 3;
    internal const uint AudioChannelLayoutTagQuadraphonic = (108 << 16) | 4;
    internal const uint AudioChannelLayoutTagDvd6 = (136 << 16) | 5;
    internal const uint AudioChannelLayoutTagDvd12 = (142 << 16) | 6;
    internal const double MinimumAudioBufferTimeMs = 15.0;

    private static uint MakeFourCc(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Length != 4)
        {
            throw new ArgumentException("FourCC values must be exactly four characters long.", nameof(value));
        }

        return ((uint)value[0] << 24) |
               ((uint)value[1] << 16) |
               ((uint)value[2] << 8) |
               (uint)value[3];
    }
}

/// <summary>
/// Pure managed helper for SDL3 CoreAudio buffer-count policy.
/// SDL3 uses three buffers by default and scales up only when the callback
/// period is smaller than the minimum buffer time target.
/// Reference: SDL/src/audio/coreaudio/SDL_coreaudio.m PrepareAudioQueue.
/// </summary>
internal static class CoreAudioBufferPolicy
{
    /// <summary>
    /// Computes the number of AudioQueue buffers SDL3 would allocate for playback.
    /// </summary>
    /// <param name="sampleRate">Playback sample rate in Hz.</param>
    /// <param name="bufferFrames">Frames per callback period.</param>
    /// <returns>The number of queue buffers to allocate.</returns>
    public static int ComputeAudioBufferCount(int sampleRate, int bufferFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferFrames);

        int numAudioBuffers = 3;
        double callbackPeriodMs = ((double)bufferFrames / sampleRate) * 1000.0;
        if (callbackPeriodMs < CoreAudioConstants.MinimumAudioBufferTimeMs)
        {
            numAudioBuffers = (int)(Math.Ceiling(CoreAudioConstants.MinimumAudioBufferTimeMs / callbackPeriodMs) * 2);
        }

        return numAudioBuffers;
    }
}
