using Suiyi.Core.Packaging;

namespace Suiyi.Core.Tests.Packaging;

public class StubModelProvisionerTests
{
    [Fact]
    public void ListMissing_ReturnsEmpty()
    {
        var provisioner = new StubModelProvisioner();
        Assert.Empty(provisioner.ListMissingTranslationModels(Path.GetTempPath()));
    }

    [Fact]
    public async Task EnsureModels_ReturnsNotImplemented()
    {
        var provisioner = new StubModelProvisioner();
        var result = await provisioner.EnsureModelsAsync(["opus-mt-en-zh"]);

        Assert.False(result.Succeeded);
        Assert.Contains("按需下载尚未实现", result.Message, StringComparison.Ordinal);
        Assert.Equal(ModelProvisionResult.NotImplemented, result);
    }
}
