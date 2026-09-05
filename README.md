# GeekFlashCore
A communication protocol and flashing development framework targeting various chip-level protocols. You can use it to develop flashing tools.

## CLI

`GeekFlashCore.CLI` is the .NET 10 command-line host for the implemented protocols. With no transport option it discovers Qualcomm EDL USB devices and waits for a matching hot-plug event on Windows; `--port COMx` and `--usb VID:PID` select a transport explicitly. The protocol is inferred by default and can be forced with `--protocol QualcommEdl`.

```text
geekflash devices
geekflash --port COM73 info
geekflash --loader programmer_firehose.mbn --port COM73 connect
geekflash --port COM73 partitions
geekflash --port COM73 read partition:boot boot.img
geekflash --port COM73 write boot.img partition:boot
geekflash --port COM73 qcom configure
```

When a loader, Digest, VIP table, or vendor authentication response is needed and no corresponding option was supplied, the CLI asks for it through the protocol Provider. `--verbose` enables package-level Debug logs; normal output keeps protocol stages and progress visible without exposing sensitive payloads.
