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
                ["JobLens:GroupChatJids:0"] = "120363427094606388@g.us",
                ["JobLens:GroupChatJids:1"] = "111111111111111111@g.us",
                ["JobLens:TargetCategories:0"] = "Software",
                ["JobLens:TargetCategories:1"] = "QA",
                ["JobLens:ScoringTemplates:0:Name"] = "Backend",
                ["JobLens:ScoringTemplates:0:Profile"] = "C# and Postgres profile",
                ["JobLens:ScoringTemplates:1:Name"] = "QA",
                ["JobLens:ScoringTemplates:1:Profile"] = "Selenium automation profile",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<JobLensOptions>(config.GetSection("JobLens"));
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<JobLensOptions>>().Value;

        Assert.Equal("C:/data/messages.db", options.MessagesDbPath);
        Assert.Equal(["120363427094606388@g.us", "111111111111111111@g.us"], options.GroupChatJids);
        Assert.Equal(["Software", "QA"], options.TargetCategories);
        Assert.Collection(options.ScoringTemplates,
            backend =>
            {
                Assert.Equal("Backend", backend.Name);
                Assert.Equal("C# and Postgres profile", backend.Profile);
            },
            qa =>
            {
                Assert.Equal("QA", qa.Name);
                Assert.Equal("Selenium automation profile", qa.Profile);
            });
    }
}
