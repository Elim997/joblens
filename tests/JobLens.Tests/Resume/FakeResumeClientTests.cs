using System.Text.Json.Nodes;
using JobLens.Tests.Resume;

namespace JobLens.Tests.Resume;

public class FakeResumeClientTests
{
    [Fact]
    public async Task WriteResumeAsync_EditsExistingItemInPlace_ByReusingItsId()
    {
        var client = new FakeResumeClient();
        client.Seed("r1", new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["experience"] = new JsonObject
                {
                    ["abc123"] = new JsonObject { ["role"] = "Old Role", ["company"] = "Acme" },
                },
            },
        });

        await client.WriteResumeAsync("r1", new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["experience"] = new JsonObject
                {
                    ["abc123"] = new JsonObject { ["role"] = "New Role" },
                },
            },
        });

        var resume = await client.ReadResumeAsync("r1");
        var item = resume!["data"]!["experience"]!["abc123"]!;
        Assert.Equal("New Role", item["role"]!.GetValue<string>());
        Assert.Equal("Acme", item["company"]!.GetValue<string>());
    }

    [Fact]
    public async Task WriteResumeAsync_AddsNewItem_WhenKeyIsNew()
    {
        var client = new FakeResumeClient();
        client.Seed("r1", new JsonObject
        {
            ["data"] = new JsonObject { ["skills"] = new JsonObject() },
        });

        await client.WriteResumeAsync("r1", new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["skills"] = new JsonObject { ["newkey"] = new JsonObject { ["skill"] = "C#" } },
            },
        });

        var resume = await client.ReadResumeAsync("r1");
        Assert.Equal("C#", resume!["data"]!["skills"]!["newkey"]!["skill"]!.GetValue<string>());
    }

    [Fact]
    public async Task WriteResumeAsync_DeletesItem_WhenValueIsNull()
    {
        var client = new FakeResumeClient();
        client.Seed("r1", new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["skills"] = new JsonObject { ["toDelete"] = new JsonObject { ["skill"] = "COBOL" } },
            },
        });

        await client.WriteResumeAsync("r1", new JsonObject
        {
            ["data"] = new JsonObject { ["skills"] = new JsonObject { ["toDelete"] = null } },
        });

        var resume = await client.ReadResumeAsync("r1");
        Assert.False(resume!["data"]!["skills"]!.AsObject().ContainsKey("toDelete"));
    }

    [Fact]
    public async Task ReadResumeAsync_ReturnsNull_WhenResumeNotSeeded()
    {
        var client = new FakeResumeClient();
        Assert.Null(await client.ReadResumeAsync("missing"));
    }
}
