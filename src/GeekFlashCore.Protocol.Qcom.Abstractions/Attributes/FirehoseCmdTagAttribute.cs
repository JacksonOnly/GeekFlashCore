namespace GeekFlashCore.Protocol.Qcom.Abstractions;

[AttributeUsage(AttributeTargets.Class)]
public class FirehoseCmdTagAttribute(string tag) : Attribute
{
    public string Tag { get; set; } = tag;
}