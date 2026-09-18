using RemoteFlow.Protocol.Rdp.Interop;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class RdpKeyboardSequenceTests
{
    [Fact]
    public void TaskManagerRemoteActionUsesMstscActionValue()
    {
        Assert.Equal(6, (int)RdpRemoteSessionAction.TaskManager);
    }

    [Fact]
    public void TaskManagerSequenceUsesCtrlShiftEscapePressAndReleaseOrder()
    {
        var sequence = RdpKeyboardSequence.TaskManager;

        Assert.Equal(
            new[]
            {
                new RdpKeyStroke(0x001D0001, IsKeyUp: false),
                new RdpKeyStroke(0x002A0001, IsKeyUp: false),
                new RdpKeyStroke(0x00010001, IsKeyUp: false),
                new RdpKeyStroke(unchecked((int)0xC0010001), IsKeyUp: true),
                new RdpKeyStroke(unchecked((int)0xC02A0001), IsKeyUp: true),
                new RdpKeyStroke(unchecked((int)0xC01D0001), IsKeyUp: true)
            },
            sequence);
    }
}
