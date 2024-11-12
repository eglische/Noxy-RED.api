using System.ComponentModel.DataAnnotations;

namespace Voxta.SampleProviderApp;

[Serializable]
public class SampleProviderAppOptions
{
    [Required]
    public required int AutoReplyDelay { get; init; }
}
