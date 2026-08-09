using JobLens.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JobLens.Tests.Configuration;

public class ConfigurationTests
{
    [Fact]
    public void JobLensOptions_BindsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JobLens:MessagesDbPath"] = "C:/data/messages.db",
                ["JobLens:GroupChatJid"] = "120363427094606388@g.us",
                ["JobLens:TargetCategories:0"] = "Software",
                ["JobLens:TargetCategories:1"] = "QA",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<JobLensOptions>(config.GetSection("JobLens"));
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<JobLensOptions>>().Value;

        Assert.Equal("C:/data/messages.db", options.MessagesDbPath);
        Assert.Equal("120363427094606388@g.us", options.GroupChatJid);
        Assert.Equal(["Software", "QA"], options.TargetCategories);
    }
}
