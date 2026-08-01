namespace Spice86.Audio.Tests.Backend;

using FluentAssertions;

using Spice86.Audio.Backend.Audio.CrossPlatform;
using Spice86.Audio.Backend.Audio.CrossPlatform.Sdl;

using Xunit;

public class SdlAudioDeviceTest
{
    [Fact]
    public void Open_ForwardsAllowNegotiateToDriver()
    {
        CaptureDriver driver = new();
        SdlAudioDevice device = new(driver);

        AudioSpec desiredSpec = new()
        {
            SampleRate = 48_000,
            Channels = 2,
            BufferFrames = 512,
            AllowNegotiate = false,
            Callback = static _ => { }
        };

        bool opened = device.Open(desiredSpec);

        opened.Should().BeTrue();
        driver.CapturedSpec.Should().NotBeNull();
        driver.CapturedSpec!.AllowNegotiate.Should().BeFalse();
    }

    private sealed class CaptureDriver : ISdlAudioDriver
    {
        public bool ProvidesOwnCallbackThread => true;

        public AudioSpec? CapturedSpec { get; private set; }

        public bool OpenDevice(SdlAudioDevice device, AudioSpec desiredSpec, out AudioSpec obtainedSpec, out int sampleFrames, out string? error)
        {
            CapturedSpec = desiredSpec;
            obtainedSpec = desiredSpec;
            sampleFrames = desiredSpec.BufferFrames;
            error = null;
            return true;
        }

        public void CloseDevice(SdlAudioDevice device)
        {
        }

        public void WaitDevice(SdlAudioDevice device)
        {
        }

        public IntPtr GetDeviceBuf(SdlAudioDevice device)
        {
            return IntPtr.Zero;
        }

        public void PlayDevice(SdlAudioDevice device)
        {
        }

        public void ThreadInit(SdlAudioDevice device)
        {
        }

        public void ThreadDeinit(SdlAudioDevice device)
        {
        }
    }
}