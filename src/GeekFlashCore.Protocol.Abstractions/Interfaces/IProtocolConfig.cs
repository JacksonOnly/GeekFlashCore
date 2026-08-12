namespace GeekFlashCore.Protocol.Abstractions;

public interface IProtocolConfig
{
    int ConnectTimeoutMs { get; set; }
    int ReadTimeoutMs { get; set; }
    int WriteTimeoutMs { get; set; }
    
    T Get<T>(string key);
    void Set<T>(string key, T value);
}