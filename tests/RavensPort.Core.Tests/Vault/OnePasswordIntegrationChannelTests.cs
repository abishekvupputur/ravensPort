using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Whether 1Password's integration channel is there, on Linux, where it is an abstract Unix socket
/// and not the Windows pipe the check used to know about.
///
/// The check used <c>File.Exists</c> on the pipe's path, which on Linux is always false — so a
/// machine with 1Password open and integration switched on was told 1Password was not running.
/// These run against captured <c>/proc/net/unix</c> text, so they do not depend on whether the
/// machine running them has 1Password open.
/// </summary>
public class OnePasswordIntegrationChannelTests
{
    private const string Header = "Num       RefCount Protocol Flags    Type St Inode Path";

    [Fact]
    public void TheSocketIsFoundWhenItIsListed()
    {
        string[] table =
        [
            Header,
            "0000000000000000: 00000002 00000000 00010000 0001 01 30955 /run/user/1000/s.sock",
            "0000000000000000: 00000002 00000000 00010000 0001 01 19371 @1PASSWORD_SDK_INTERGATIONS",
        ];

        Assert.True(NativeCliRunner.ListsIntegrationSocket(table));
    }

    [Fact]
    public void ItIsNotFoundWhenOnlyOtherSocketsAre()
    {
        string[] table =
        [
            Header,
            "0000000000000000: 00000002 00000000 00010000 0001 01 9187 /run/user/1000/1Password-BrowserSupport.sock",
            "0000000000000000: 00000002 00000000 00010000 0001 01 55 @/tmp/.X11-unix/X0",
        ];

        Assert.False(NativeCliRunner.ListsIntegrationSocket(table));
    }

    [Fact]
    public void AnEmptyListingFindsNothing()
    {
        Assert.False(NativeCliRunner.ListsIntegrationSocket([]));
    }

    [Theory]
    [InlineData("0000000000000000: 00000002 00000000 00010000 0001 01 1 @1PASSWORD_SDK_INTEGRATIONS")]
    [InlineData("0000000000000000: 00000002 00000000 00010000 0001 01 1 1PASSWORD_SDK_INTERGATIONS")]
    [InlineData("0000000000000000: 00000002 00000000 00010000 0001 01 1 @1PASSWORD_SDK_INTERGATIONS_OLD")]
    [InlineData("0000000000000000: 00000002 00000000 00010000 0001 01 1 /run/@1PASSWORD_SDK_INTERGATIONS")]
    public void ANearMissIsNotTheSocket(string line)
    {
        // Matched as the whole last column, spelled exactly as 1Password spells it — including the
        // misspelling. Anything merely resembling it must not be mistaken for the channel.
        Assert.False(NativeCliRunner.ListsIntegrationSocket([Header, line]));
    }
}
