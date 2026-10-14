using WorkbenchProcess = GameDirector.Decisions.Tests.WorkbenchHttpTests.WorkbenchProcess;
using GameDirector.Workbench;

namespace GameDirector.Decisions.Tests;

public class UiMessageTests
{
    [Fact]
    public async Task FolderErrorsExposeStableMessageCodesWithoutChangingFailurePolicy()
    {
        await using var server = await WorkbenchProcess.Start();
        var error = await server.Call("folders", new { path = "relative/project" }, 422);
        Assert.Equal("invalid_input", error["code"]!.GetValue<string>());
        Assert.Equal("folder_absolute", error["messageCode"]!.GetValue<string>());
        Assert.Equal("Choose a full folder path.", error["error"]!.GetValue<string>());
        Assert.False(error["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task MediaOptionsExposeCodesAlongsideEnglishFallbackCopy()
    {
        await using var server = await WorkbenchProcess.Start();
        var status = await server.Call("media/install");
        var option = status["options"]!.AsArray().Single(o => o!["id"]!.GetValue<string>() == "user-bundle")!;
        Assert.Equal("install.user-bundle.label", option["labelCode"]!.GetValue<string>());
        Assert.Equal("install.user-bundle.detail", option["detailCode"]!.GetValue<string>());
        Assert.Equal("install.user-bundle.license", option["licenseCode"]!.GetValue<string>());
        Assert.Equal("Install your offline FFmpeg bundle", option["label"]!.GetValue<string>());
    }
}
