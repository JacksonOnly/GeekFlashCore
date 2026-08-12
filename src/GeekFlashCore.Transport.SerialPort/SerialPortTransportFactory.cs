using System.IO.Ports;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Transport.SerialPort;

public static class SerialPortTransportFactory
{
    private static ITransport CreateCore(string portName, int baudRate = 115200, int dataBits = 8,
        StopBits stopBits = StopBits.None, Parity parity = Parity.None, int bufferSize = 8192,
        int maximumReadBufferSize = 4 * 1024 * 1024, int readTimeout = 1000, int writeTimeout = 1000)
    {
        return new SerialPortTransport(portName, baudRate, dataBits, stopBits, parity, bufferSize,
            maximumReadBufferSize, readTimeout, writeTimeout);
    }

    public static ITransport Create(string portName, int bufferSize,
        int maximumReadBufferSize, int readTimeout, int writeTimeout)
    {
        return CreateCore(portName: portName, bufferSize: bufferSize,
            maximumReadBufferSize: maximumReadBufferSize, readTimeout: readTimeout, writeTimeout: writeTimeout);
    }

    public static ITransport Create(string portName, int readTimeout = 1000, int writeTimeout = 1000)
    {
        return CreateCore(portName: portName, readTimeout: readTimeout, writeTimeout: writeTimeout);
    }
}