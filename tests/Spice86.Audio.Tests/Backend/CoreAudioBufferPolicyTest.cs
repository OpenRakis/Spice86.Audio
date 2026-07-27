namespace Spice86.Audio.Tests.Backend;

using FluentAssertions;

using Spice86.Audio.Backend.Audio.CrossPlatform.Sdl.Mac.CoreAudio;

using Xunit;

public class CoreAudioBufferPolicyTest
{
    [Fact]
    public void ComputeAudioBufferCount_UsesThreeBuffers_WhenPeriodAlreadyExceedsMinimum()
    {
        int bufferCount = CoreAudioBufferPolicy.ComputeAudioBufferCount(48_000, 1024);

        bufferCount.Should().Be(3);
    }

    [Fact]
    public void ComputeAudioBufferCount_ScalesUp_WhenPeriodIsSmall()
    {
        int bufferCount = CoreAudioBufferPolicy.ComputeAudioBufferCount(48_000, 256);

        bufferCount.Should().Be(6);
    }

    [Fact]
    public void ComputeAudioBufferCount_ThrowsForInvalidSampleRate()
    {
        Action act = () => CoreAudioBufferPolicy.ComputeAudioBufferCount(0, 256);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}