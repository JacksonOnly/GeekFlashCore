namespace GeekFlashCore.Transport.Abstractions;

public interface IControlTransferTransport
{
    void ControlOut(
        byte requestType,
        byte request,
        ushort value,
        ushort index,
        ReadOnlySpan<byte> data);

    int ControlIn(
        byte requestType,
        byte request,
        ushort value,
        ushort index,
        Span<byte> destination);
}