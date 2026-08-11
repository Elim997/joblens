namespace JobLens.Core.Configuration;

public class BaseResumeConfig
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
}

public class ReziOptions
{
    public List<BaseResumeConfig> BaseResumes { get; set; } = [];

    // Not read by IResumeTailor - Phase 3's write step targets this slot.
    public string ForEditResumeId { get; set; } = "";
}
