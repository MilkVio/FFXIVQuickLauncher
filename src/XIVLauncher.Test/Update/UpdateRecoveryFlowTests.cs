using System.Net;
using System.Net.Http;
using System.Windows;
using Velopack;
using XIVLauncher.Update;
using Xunit;

namespace XIVLauncher.Test.Update;

public sealed class UpdateRecoveryFlowTests
{
    [Theory]
    [InlineData(403, MessageBoxResult.Yes, true)]
    [InlineData(429, MessageBoxResult.Yes, true)]
    [InlineData(0, MessageBoxResult.Yes, true)]
    [InlineData(-1, MessageBoxResult.Yes, true)]
    [InlineData(403, MessageBoxResult.No, false)]
    [InlineData(429, MessageBoxResult.Cancel, false)]
    [InlineData(0, MessageBoxResult.None, false)]
    public async Task CheckFailureOffersOneChoiceAndAlwaysClosesStartupWindows(int error, MessageBoxResult choice, bool continues)
    {
        var session = new Session { CheckError = NetworkError(error) };
        var prompts = 0;
        var closed = 0;
        var result = await UpdateStartupFlow.RunAsync(
            () => new UpdateOrchestrator(_ => session).Run(false, null, null),
            builder =>
            {
                prompts++;
                Assert.Equal("继续启动启动器", builder.YesButtonText);
                Assert.Equal("退出", builder.NoButtonText);
                Assert.Equal(MessageBoxResult.Yes, builder.DefaultResult);
                Assert.Equal(MessageBoxResult.No, builder.CancelResult);
                Assert.Contains("无法检查启动器更新", builder.Text);
                Assert.DoesNotContain("最新版", builder.Text);
                if (error > 0) Assert.Contains(error.ToString(), builder.Text);
                return choice;
            }, () => closed++);
        Assert.Equal(continues ? UpdateStartupResult.Continue : UpdateStartupResult.Exit, result);
        Assert.Equal(1, prompts);
        Assert.Equal(1, closed);
        Assert.Equal(0, session.Downloads);
        Assert.Equal(0, session.Applies);
    }

    [Theory]
    [InlineData(MessageBoxResult.Yes, true)]
    [InlineData(MessageBoxResult.No, false)]
    public async Task DownloadFailureCanContinueWithoutApplyingPartialUpdate(MessageBoxResult choice, bool continues)
    {
        var session = new Session { UpdateAvailable = true, DownloadError = NetworkError(429) };
        var prompts = 0;
        var result = await UpdateStartupFlow.RunAsync(
            () => new UpdateOrchestrator(_ => session).Run(false, null, null),
            builder => { prompts++; Assert.Contains("无法下载", builder.Text); return choice; }, () => { });
        Assert.Equal(continues ? UpdateStartupResult.Continue : UpdateStartupResult.Exit, result);
        Assert.Equal(1, prompts);
        Assert.Equal(1, session.Downloads);
        Assert.Equal(0, session.Applies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulCheckContinuesOrAppliesOnceWithoutErrorPrompt(bool updateAvailable)
    {
        var session = new Session { UpdateAvailable = updateAvailable };
        var closed = 0;
        var result = await UpdateStartupFlow.RunAsync(
            () => new UpdateOrchestrator(_ => session).Run(false, null, null),
            _ => throw new InvalidOperationException("Unexpected recovery prompt"), () => closed++);
        Assert.Equal(updateAvailable ? UpdateStartupResult.Restarting : UpdateStartupResult.Continue, result);
        Assert.Equal(updateAvailable ? 1 : 0, session.Downloads);
        Assert.Equal(updateAvailable ? 1 : 0, session.Applies);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task ApplyingFailureCannotContinueAndDoesNotApplyTwice()
    {
        var session = new Session { UpdateAvailable = true, ApplyError = new InvalidOperationException("install failed") };
        var prompts = 0;
        var result = await UpdateStartupFlow.RunAsync(
            () => new UpdateOrchestrator(_ => session).Run(false, null, null),
            builder =>
            {
                prompts++;
                Assert.Equal(MessageBoxButton.OK, builder.Buttons);
                Assert.Equal("退出", builder.OkButtonText);
                Assert.Null(builder.YesButtonText);
                return MessageBoxResult.Yes; // Even an invalid UI response must not continue after applying.
            }, () => { });
        Assert.Equal(UpdateStartupResult.Exit, result);
        Assert.Equal(1, session.Applies);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task OuterSetupErrorUsesSameRecoveryAndCleanup()
    {
        var prompts = 0;
        var closed = 0;
        var result = await UpdateStartupFlow.RunAsync(
            () => throw new InvalidOperationException("setup failed"),
            _ => { prompts++; return MessageBoxResult.Yes; }, () => closed++);
        Assert.Equal(UpdateStartupResult.Continue, result);
        Assert.Equal(1, prompts);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task ContinuingDoesNotDisableTheNextCheck()
    {
        var session = new Session { CheckError = NetworkError(403) };
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(UpdateStartupResult.Continue, await UpdateStartupFlow.RunAsync(
                () => new UpdateOrchestrator(_ => session).Run(false, null, null),
                _ => MessageBoxResult.Yes, () => { }));
        }
        Assert.Equal(2, session.Checks);
    }

    [Fact]
    public async Task PromptFailureDoesNotPromptAgainAndStillCleansUp()
    {
        var prompts = 0;
        var closed = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateStartupFlow.RunAsync(
            () => throw NetworkError(403),
            _ => { prompts++; throw new InvalidOperationException("UI failure"); }, () => closed++));
        Assert.Equal(1, prompts);
        Assert.Equal(1, closed);
    }

    private static Exception NetworkError(int error) => error switch
    {
        0 => new TimeoutException("request timed out"),
        -1 => new HttpRequestException("connection failed"),
        _ => new HttpRequestException("request rejected", null, (HttpStatusCode)error)
    };

    private sealed class Session : ILauncherUpdateSession
    {
        public bool UpdateAvailable { get; init; }
        public Exception? CheckError { get; init; }
        public Exception? DownloadError { get; init; }
        public Exception? ApplyError { get; init; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Applies { get; private set; }

        public Task<UpdateInfo?> CheckAsync()
        {
            Checks++;
            if (CheckError != null) throw CheckError;
            return Task.FromResult(UpdateAvailable ? new UpdateInfo(new VelopackAsset
            {
                Version = SemanticVersion.Parse("2.5.4"), Type = VelopackAssetType.Full, NotesMarkdown = "Test update"
            }, false, null, []) : null);
        }

        public Task DownloadAsync(UpdateInfo update, Action<int> progress)
        {
            Downloads++;
            if (DownloadError != null) throw DownloadError;
            progress(100);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(UpdateInfo update)
        {
            Applies++;
            if (ApplyError != null) throw ApplyError;
        }
    }
}
