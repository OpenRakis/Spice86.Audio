namespace Spice86.Audio.Backend.Audio.CrossPlatform.Sdl.Mac.CoreAudio;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke bindings for Apple AudioToolbox (AudioQueue API).
/// Reference: SDL_coreaudio.m uses AudioQueueNewOutput, AudioQueueAllocateBuffer,
/// AudioQueueEnqueueBuffer, AudioQueueStart, AudioQueueStop, AudioQueueFlush,
/// AudioQueueDispose, AudioQueueSetProperty, and CoreFoundation's CFRunLoop.
/// 
/// These are macOS-only APIs from AudioToolbox.framework and CoreFoundation.framework.
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
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void AudioQueueOutputCallback(
        IntPtr inUserData,
        IntPtr inAudioQueue,
        IntPtr inBuffer);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueNewOutput")]
    internal static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription inFormat,
        AudioQueueOutputCallback inCallbackProc,
        IntPtr inUserData,
        IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode,
        uint inFlags,
        out IntPtr outAQ);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueAllocateBuffer")]
    internal static extern int AudioQueueAllocateBuffer(
        IntPtr inAQ,
        uint inBufferByteSize,
        out IntPtr outBuffer);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueEnqueueBuffer")]
    internal static extern int AudioQueueEnqueueBuffer(
        IntPtr inAQ,
        IntPtr inBuffer,
        uint inNumPacketDescs,
        IntPtr inPacketDescs);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueStart")]
    internal static extern int AudioQueueStart(
        IntPtr inAQ,
        IntPtr inStartTime);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueStop")]
    internal static extern int AudioQueueStop(
        IntPtr inAQ,
        byte inImmediate);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueFlush")]
    internal static extern int AudioQueueFlush(IntPtr inAQ);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueDispose")]
    internal static extern int AudioQueueDispose(
        IntPtr inAQ,
        byte inImmediate);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueSetProperty")]
    internal static extern int AudioQueueSetProperty(
        IntPtr inAQ,
        uint inID,
        IntPtr inData,
        uint inDataSize);

    [DllImport(AudioToolboxLib, EntryPoint = "AudioQueueSetProperty")]
    internal static extern int AudioQueueSetProperty(
        IntPtr inAQ,
        uint inID,
        ref IntPtr inData,
        uint inDataSize);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    internal static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out uint outData);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    internal static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out int outData);

    [DllImport(CoreAudioLib, EntryPoint = "AudioObjectGetPropertyData")]
    internal static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out IntPtr outData);

    [DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopGetCurrent")]
    internal static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopRunInMode")]
    internal static extern int CFRunLoopRunInMode(
        IntPtr mode,
        double seconds,
        byte returnAfterSourceHandled);

    [DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopGetMain")]
    internal static extern IntPtr CFRunLoopGetMain();

    [DllImport(CoreFoundationLib, EntryPoint = "CFRelease")]
    internal static extern void CFRelease(IntPtr cf);

    internal static IntPtr GetDefaultRunLoopMode()
    {
        IntPtr lib = NativeLibrary.Load(CoreFoundationLib);
        IntPtr symbolAddr = NativeLibrary.GetExport(lib, "kCFRunLoopDefaultMode");
        return Marshal.ReadIntPtr(symbolAddr);
    }

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
/// CoreAudio / AudioToolbox constants.
/// Reference: CoreAudioTypes.h, AudioQueue.h
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
/// </summary>
internal static class CoreAudioBufferPolicy
{
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
