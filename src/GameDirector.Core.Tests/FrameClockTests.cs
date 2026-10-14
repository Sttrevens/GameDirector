using GameDirector.Core.Dsl;
using Xunit;
namespace GameDirector.Core.Tests;
public class FrameClockTests
{
    [Theory]
    [InlineData(24, 3, .125)] [InlineData(25, 3, .12)] [InlineData(30, 3, .1)] [InlineData(60, 3, .05)]
    [InlineData(60, 36000, 600)]
    public void FrameTimeIsTheAbsoluteQuotient(int fps, int frame, double seconds)
    {
        Assert.Equal(seconds, FrameClock.TimeOf(frame, fps)); Assert.Equal(0, FrameClock.TimeOf(0, fps));
    }
    [Theory]
    [InlineData(24, .125, 3)] [InlineData(25, .125, 4)] [InlineData(30, .125, 4)] [InlineData(60, .125, 8)]
    [InlineData(24, .1, 3)] [InlineData(25, .1, 3)] [InlineData(30, .1, 3)] [InlineData(60, .1, 6)]
    [InlineData(24, 0, 0)] [InlineData(60, .0000000001, 1)]
    public void FractionalTakesRoundUpButNeverLoseTheirOnlyFrame(int fps, double duration, int frames)
    {
        Assert.Equal(frames, FrameClock.FrameCount(duration, fps)); Assert.Equal(frames, CaptureContract.FrameCount(duration, fps));
    }
    [Theory] [InlineData(24)] [InlineData(25)] [InlineData(30)] [InlineData(60)]
    public void AlignmentSnapAndCountShareOneFrameTolerance(int fps)
    {
        foreach (var t in new[] { 1 - .000005 / fps, 1.0, 1 + .000005 / fps }) {
            Assert.True(FrameClock.IsAligned(t, fps)); Assert.Equal(1, FrameClock.Snap(t, fps)); Assert.Equal(fps, FrameClock.FrameCount(t, fps));
        }
        Assert.False(FrameClock.IsAligned(1 + .00002 / fps, fps)); Assert.False(FrameClock.IsAligned(1 - .00002 / fps, fps));
        Assert.Equal(fps + 1, FrameClock.FrameCount(1 + .00002 / fps, fps));
        Assert.Equal(fps, FrameClock.FrameCount(1 - .00002 / fps, fps));
        Assert.Equal(0, FrameClock.Snap(.5 / fps, fps)); Assert.Equal(2.0 / fps, FrameClock.Snap(1.5 / fps, fps)); // nearest, ties-to-even
        Assert.True(FrameClock.IsAligned(0, fps)); Assert.False(FrameClock.IsAligned(.5 / fps, fps));
    }
    [Theory] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidTimesCannotBeSnappedOrCounted(double t)
    {
        Assert.False(FrameClock.IsAligned(t, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.Snap(t, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.FrameCount(t, 24));
    }
    [Fact] public void AlignedInstantsHaveIndicesNotDurationCeilings()
    {
        foreach (var fps in new[] { 24, 25, 30, 60 }) {
            Assert.Equal(0, FrameClock.FrameIndex(0, fps)); Assert.Equal(0, FrameClock.FrameIndex(FrameClock.AlignmentToleranceFrames / (2 * fps), fps));
            Assert.Equal(3, FrameClock.FrameIndex(FrameClock.TimeOf(3, fps), fps));
            Assert.Equal(fps, FrameClock.FrameIndex(1 + FrameClock.AlignmentToleranceFrames / (2 * fps), fps));
            Assert.Throws<ArgumentException>(() => FrameClock.FrameIndex(.5 / fps, fps)); // unaligned instant has no index
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.FrameIndex(-1, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.FrameIndex(1, 0));
        Assert.Throws<OverflowException>(() => FrameClock.FrameIndex(int.MaxValue, 60));
    }
    [Fact] public void InvalidRatesFramesAndCountOverflowAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.TimeOf(-1, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.TimeOf(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.Snap(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.IsAligned(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameClock.FrameCount(1, 0));
        Assert.Throws<OverflowException>(() => FrameClock.FrameCount(int.MaxValue, 60));
    }
}
