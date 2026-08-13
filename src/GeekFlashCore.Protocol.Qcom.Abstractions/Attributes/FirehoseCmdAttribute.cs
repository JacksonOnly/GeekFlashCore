namespace GeekFlashCore.Protocol.Qcom.Abstractions;

[AttributeUsage(AttributeTargets.Property)]
public class FirehoseCmdAttributeAttribute(string attributeName) : Attribute
{
    public string AttributeName { get; } = attributeName;
}