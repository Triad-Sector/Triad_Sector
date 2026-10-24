#nullable enable

using System.Threading.Tasks;
using Robust.Shared.Console;
using Robust.Shared.Toolshed;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The Admin menu's Drydock button runs <c>drydockadmin</c> by name and shows only for a command
    /// the client's admin status lists. <c>AdminManager</c> builds that list from the server's classic
    /// console commands and never from Toolshed's, so being announced to the client is not enough: the
    /// command has to be a classic one, or every admin but a host loses the button.
    /// </summary>
    [TestFixture]
    public sealed class DrydockAdminCommandTest
    {
        [Test]
        public async Task TheCommandIsAClassicCommandSoTheAdminMenuButtonShows()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            await pair.RunTicksSync(5);

            var serverConsole = pair.Server.ResolveDependency<IConsoleHost>();
            var toolshed = pair.Server.ResolveDependency<ToolshedManager>();
            var clientConsole = pair.Client.ResolveDependency<IConsoleHost>();

            Assert.Multiple(() =>
            {
                Assert.That(serverConsole.AvailableCommands.ContainsKey("drydockadmin"), Is.True,
                    "drydockadmin is a classic console command, the only kind the admin status list carries.");
                Assert.That(clientConsole.AvailableCommands.ContainsKey("drydockadmin"), Is.True,
                    "The client learned the command from the server, so the button has a target.");

                // Control: drydockrebake is a Toolshed command with the same admin flag. It exists, and
                // the classic registry does not hold it, so the first assertion tells the two kinds apart.
                Assert.That(toolshed.DefaultEnvironment.TryGetCommand("drydockrebake", out _), Is.True);
                Assert.That(serverConsole.AvailableCommands.ContainsKey("drydockrebake"), Is.False);

                // Control: a name nothing registers is absent on both sides.
                Assert.That(serverConsole.AvailableCommands.ContainsKey("drydocknotacommand"), Is.False);
                Assert.That(clientConsole.AvailableCommands.ContainsKey("drydocknotacommand"), Is.False);
            });

            await pair.CleanReturnAsync();
        }
    }
}
